using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PhotoFrame
{
    /// <summary>
    /// Числовой датчик Home Assistant, пригодный для показа поверх снимка.
    /// </summary>
    public sealed class HomeAssistantSensor : INotifyPropertyChanged
    {
        private bool _isSelected;
        private string _icon = string.Empty;
        private int _orderNumber;

        public HomeAssistantSensor(
            string entityId, string displayName, string value, string unit, string? deviceClass)
        {
            EntityId = entityId;
            DisplayName = displayName;
            Value = value;
            Unit = unit;
            DeviceClass = deviceClass;
        }

        public string EntityId { get; }

        public string DisplayName { get; }

        public string Value { get; }

        public string Unit { get; }

        /// <summary>temperature, humidity, pressure и тому подобное. Может отсутствовать.</summary>
        public string? DeviceClass { get; }

        /// <summary>Как значение выглядит на экране рамки.</summary>
        public string ValueWithUnit => FormatValueWithUnit(Value, Unit);

        /// <summary>Подпись в списке выбора: класс датчика и идентификатор.</summary>
        public string DetailText => string.IsNullOrEmpty(DeviceClass)
            ? EntityId
            : $"{DeviceClass} · {EntityId}";

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(SelectionGlyph));
                RaisePropertyChanged(nameof(SelectionColor));
                RaisePropertyChanged(nameof(IconButtonText));
            }
        }

        /// <summary>Выбранный значок; пустая строка, если значка нет.</summary>
        public string Icon
        {
            get => _icon;
            set
            {
                if (_icon == value)
                {
                    return;
                }

                _icon = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(IconButtonText));
            }
        }

        /// <summary>
        /// Что показывать на кнопке значка. Для невыбранного датчика кнопка бессмысленна,
        /// поэтому пустая.
        /// </summary>
        public string IconButtonText => !IsSelected
            ? string.Empty
            : (string.IsNullOrEmpty(Icon) ? "…" : Icon);

        /// <summary>
        /// Какой по счёту среди выбранных; 0 — не выбран.
        /// </summary>
        /// <remarks>
        /// Порядок задаёт, как значения встанут на экране рамки, и без номера его
        /// не видно: отметки одинаковые, а «третий слева» — это как раз то, что
        /// человек и хочет поправить.
        /// </remarks>
        public int OrderNumber
        {
            get => _orderNumber;
            set
            {
                if (_orderNumber == value)
                {
                    return;
                }

                _orderNumber = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(OrderText));
            }
        }

        /// <summary>Номер для показа: у невыбранного датчика пусто.</summary>
        public string OrderText => OrderNumber > 0 ? OrderNumber.ToString() : string.Empty;

        public string SelectionGlyph => IsSelected ? "✓" : "+";

        public string SelectionColor => IsSelected ? "#4FC3F7" : "#555555";

        /// <summary>
        /// Градусы и проценты принято писать без пробела, остальные единицы — с пробелом.
        /// </summary>
        public static string FormatValueWithUnit(string value, string unit)
        {
            if (string.IsNullOrEmpty(unit))
            {
                return value;
            }

            bool tightUnit = unit.StartsWith('°') || unit == "%";
            return tightUnit ? value + unit : value + " " + unit;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void RaisePropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
