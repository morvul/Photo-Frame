using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace PhotoFrame
{
    /// <summary>
    /// Список числовых датчиков Home Assistant с их текущими значениями.
    /// </summary>
    /// <remarks>
    /// Идентификаторы сущностей нигде не вводятся руками: список приходит из самого
    /// Home Assistant, поэтому неважно, сколько там датчиков и как они называются.
    /// </remarks>
    public partial class SensorPickerPage : ContentPage
    {
        private readonly HomeAssistantClient _homeAssistantClient;
        private readonly ObservableCollection<HomeAssistantSensor> _sensors = new();

        /// <summary>Порядок выбора сохраняется: он же задаёт порядок на экране рамки.</summary>
        private readonly List<string> _selectedEntityIds = new();

        public SensorPickerPage()
        {
            InitializeComponent();

            _homeAssistantClient =
                IPlatformApplication.Current?.Services.GetService<HomeAssistantClient>()
                ?? new HomeAssistantClient();

            SensorList.ItemsSource = _sensors;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            _selectedEntityIds.Clear();
            _selectedEntityIds.AddRange(FrameSettings.SensorEntityIds);
            UpdateSelectionSummary();

            if (!HomeAssistantClient.IsConfigured)
            {
                StatusLabel.Text =
                    "Не заданы адрес и токен Home Assistant. Укажите их в настройках рамки " +
                    "(раздел «Датчики»).";
                return;
            }

            StatusLabel.Text = "Запрос списка датчиков...";

            try
            {
                List<HomeAssistantSensor> sensors =
                    await _homeAssistantClient.GetNumericSensorsAsync().ConfigureAwait(true);

                _sensors.Clear();
                foreach (HomeAssistantSensor sensor in sensors)
                {
                    sensor.Icon = FrameSettings.GetSensorIcon(sensor.EntityId);
                    sensor.IsSelected = _selectedEntityIds.Contains(sensor.EntityId);
                    _sensors.Add(sensor);
                }

                ShowSelectionOrder();

                StatusLabel.Text = sensors.Count == 0
                    ? "Home Assistant не вернул ни одного датчика с числовым значением."
                    : $"Найдено датчиков: {sensors.Count}. " +
                      "Отметьте любое количество; порядок меняется стрелками, "
                      + "по три значения в строке.";
            }
            catch (PhotoSourceException requestFailure)
            {
                StatusLabel.Text = requestFailure.Message;
            }
            catch (Exception unexpectedFailure)
            {
                StatusLabel.Text = "Непредвиденная ошибка запроса к Home Assistant.";
                System.Diagnostics.Debug.WriteLine(unexpectedFailure);
            }
        }

        private void OnToggleSensorClicked(object? sender, EventArgs e)
        {
            if (sender is not Button { CommandParameter: HomeAssistantSensor sensor })
            {
                return;
            }

            if (_selectedEntityIds.Remove(sensor.EntityId))
            {
                sensor.IsSelected = false;
            }
            else
            {
                // Количество не ограничено: сколько значений уместно на экране, решает
                // сам пользователь, а строка датчиков переносится по три в строке.
                _selectedEntityIds.Add(sensor.EntityId);
                sensor.IsSelected = true;
            }

            ShowSelectionOrder();
            UpdateSelectionSummary();
        }

        /// <summary>Двигает датчик выше в порядке показа.</summary>
        private void OnMoveSensorUpClicked(object? sender, EventArgs e) => MoveSensor(sender, -1);

        /// <summary>Двигает датчик ниже в порядке показа.</summary>
        private void OnMoveSensorDownClicked(object? sender, EventArgs e) => MoveSensor(sender, +1);

        /// <summary>
        /// Переставляет выбранный датчик в порядке показа.
        /// </summary>
        /// <remarks>
        /// Двигается запись в списке выбранных, а не строка в перечне датчиков: перечень
        /// приходит от Home Assistant и отсортирован по названию, а порядок показа —
        /// дело пользователя. На краях список не заворачивается: дошёл до первого места
        /// и стоит, иначе кнопка «выше» неожиданно уводила бы датчик в самый низ.
        /// </remarks>
        private void MoveSensor(object? sender, int offset)
        {
            if (sender is not Button { CommandParameter: HomeAssistantSensor sensor })
            {
                return;
            }

            int position = _selectedEntityIds.IndexOf(sensor.EntityId);
            int target = position + offset;

            if (position < 0 || target < 0 || target >= _selectedEntityIds.Count)
            {
                return;
            }

            _selectedEntityIds.RemoveAt(position);
            _selectedEntityIds.Insert(target, sensor.EntityId);

            ShowSelectionOrder();
            UpdateSelectionSummary();
        }

        /// <summary>Расставляет номера по текущему порядку выбранных.</summary>
        private void ShowSelectionOrder()
        {
            foreach (HomeAssistantSensor sensor in _sensors)
            {
                sensor.OrderNumber = _selectedEntityIds.IndexOf(sensor.EntityId) + 1;
            }
        }

        /// <summary>
        /// Значок выбирается системным списком: свой попап ради тринадцати вариантов
        /// на экране рамки не нужен.
        /// </summary>
        private async void OnPickIconClicked(object? sender, EventArgs e)
        {
            if (sender is not Button { CommandParameter: HomeAssistantSensor sensor })
            {
                return;
            }

            string[] labels = SensorIconChoices.BuildActionSheetLabels();
            string? chosenLabel = await DisplayActionSheetAsync(
                sensor.DisplayName, "Отмена", null, labels);

            string? chosenIcon = SensorIconChoices.FindIconByLabel(chosenLabel);
            if (chosenIcon is null)
            {
                return;
            }

            sensor.Icon = chosenIcon;
            FrameSettings.SetSensorIcon(sensor.EntityId, chosenIcon);
            UpdateSelectionSummary();
        }

        private void UpdateSelectionSummary()
        {
            SelectionSummaryLabel.Text = $"Выбрано датчиков: {_selectedEntityIds.Count}";

            if (_selectedEntityIds.Count == 0)
            {
                SelectionDetailLabel.Text = "Отметьте датчики кнопкой + справа";
                return;
            }

            var preview = new List<string>(_selectedEntityIds.Count);
            foreach (string entityId in _selectedEntityIds)
            {
                HomeAssistantSensor? sensor =
                    _sensors.FirstOrDefault(candidate => candidate.EntityId == entityId);

                if (sensor is null)
                {
                    preview.Add(entityId);
                    continue;
                }

                preview.Add(string.IsNullOrEmpty(sensor.Icon)
                    ? sensor.ValueWithUnit
                    : sensor.Icon + " " + sensor.ValueWithUnit);
            }

            SelectionDetailLabel.Text = string.Join(" · ", preview);
        }

        private async void OnDoneClicked(object? sender, EventArgs e)
        {
            FrameSettings.SensorEntityIds = _selectedEntityIds.ToArray();
            await Shell.Current.GoToAsync("..");
        }

        private async void OnCancelClicked(object? sender, EventArgs e)
        {
            await Shell.Current.GoToAsync("..");
        }
    }
}
