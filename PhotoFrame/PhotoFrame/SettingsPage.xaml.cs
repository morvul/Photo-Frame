using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace PhotoFrame
{
    /// <summary>
    /// Экран настроек рамки: ссылка на альбом, темп слайд-шоу, периодичность проверки.
    /// </summary>
    public partial class SettingsPage : ContentPage
    {
        /// <summary>
        /// Ставится в true, когда пользователь попросил обновиться сразу после сохранения.
        /// MainPage читает флаг в OnAppearing — так экраны не держат ссылок друг на друга.
        /// </summary>
        public static bool SyncRequestedOnReturn { get; set; }

        /// <summary>
        /// Альбомы, полученные с сервера Immich. Пусто, пока список не запрашивали:
        /// лезть в сеть при каждом открытии настроек ни к чему, обычно правят не это.
        /// </summary>
        private List<ImmichAlbum> _immichAlbums = new();

        /// <summary>
        /// Отмеченные альбомы. Пустое множество означает «вся библиотека» — отдельного
        /// пункта для этого не нужно: снять все отметки и есть «показывать всё».
        /// </summary>
        private readonly HashSet<string> _selectedImmichAlbumIds = new(StringComparer.Ordinal);

        /// <summary>Названия отмеченных альбомов — переживают закрытие настроек без сети.</summary>
        private readonly Dictionary<string, string> _immichAlbumNames = new(StringComparer.Ordinal);

        /// <summary>
        /// Слепок формы сразу после загрузки — с ним сравнивается текущий при уходе
        /// со страницы, чтобы решить, есть ли что терять.
        /// </summary>
        private string? _loadedSnapshot;

        public SettingsPage()
        {
            InitializeComponent();

            SlideshowIntervalPicker.ItemsSource = BuildSecondsChoices();
            PollIntervalPicker.ItemsSource = BuildHoursChoices();

            PanelRevealPicker.ItemsSource = BuildPanelRevealChoices();
            AlbumLimitPicker.ItemsSource = BuildAlbumLimitChoices();
            ImmichLimitPicker.ItemsSource = BuildAlbumLimitChoices();

            string[] hourChoices = BuildHourOfDayChoices();
            NightStartPicker.ItemsSource = hourChoices;
            NightEndPicker.ItemsSource = hourChoices;

            NightColorPicker.ItemsSource = NightClockPalette.BuildChoiceLabels();
            LaunchDelayPicker.ItemsSource = BuildLaunchDelayChoices();

            CameraSnapshotIntervalPicker.ItemsSource = BuildCameraSnapshotIntervalChoices();

            // Путь не меняется на ходу выполнения, поэтому виден сразу, а не только
            // пока поле пустое: набирать ключ или токен пультом по экранной клавиатуре
            // всё равно не вариант.
            ImmichKeyFileHintLabel.Text =
                $"Либо положите файл на рамку: {ImmichKeyFile.FilePath} "
                + $"(или прежнее имя {ImmichKeyFile.LegacyFilePath})";
            HomeAssistantKeyFileHintLabel.Text =
                $"Либо положите файл на рамку: {HomeAssistantKeyFile.FilePath}";
            CameraRtspFileHintLabel.Text =
                $"Либо положите адрес файлом: {CameraRtspFile.UrlFilePath} "
                + $"(название — {CameraRtspFile.NameFilePath})";
        }

        /// <summary>0 в списке означает «без предела».</summary>
        private static string[] BuildAlbumLimitChoices()
        {
            var labels = new string[FrameSettings.AlbumPhotoLimitChoices.Length];
            for (int index = 0; index < labels.Length; index++)
            {
                int limit = FrameSettings.AlbumPhotoLimitChoices[index];
                labels[index] = limit == 0 ? "без предела" : limit + " фото";
            }

            return labels;
        }

        private static string[] BuildPanelRevealChoices()
        {
            var labels = new string[FrameSettings.PanelRevealChoices.Length];
            for (int index = 0; index < labels.Length; index++)
            {
                labels[index] = FrameSettings.PanelRevealChoices[index] + " сек";
            }

            return labels;
        }

        /// <summary>0 в списке означает «сразу».</summary>
        private static string[] BuildLaunchDelayChoices()
        {
            var labels = new string[FrameSettings.LaunchOnBootDelayChoices.Length];
            for (int index = 0; index < labels.Length; index++)
            {
                int delaySeconds = FrameSettings.LaunchOnBootDelayChoices[index];
                labels[index] = delaySeconds == 0 ? "сразу" : delaySeconds + " сек";
            }

            return labels;
        }

        /// <summary>Часы суток: индекс в списке равен самому часу.</summary>
        private static string[] BuildHourOfDayChoices()
        {
            var hourLabels = new string[24];
            for (int hour = 0; hour < hourLabels.Length; hour++)
            {
                hourLabels[hour] = $"{hour:D2}:00";
            }

            return hourLabels;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            // На свежей установке разрешение ещё не выдано, и без него чтение
            // immich.key/homeassistant.key проваливается молча — поля просто
            // остаются пустыми без единой пометки, почему. Здесь же самое место
            // спросить: до LoadCurrentSettings ничего из общей памяти не читается.
            await EnsureStoragePermissionAsync().ConfigureAwait(true);

            LoadCurrentSettings();
        }

        /// <summary>
        /// Разрешение на чтение общей памяти. См. <see cref="FolderPickerPage"/> — тот же
        /// приём, здесь нужен ради ключевых файлов, а не выбора папок.
        /// </summary>
        private static async Task<bool> EnsureStoragePermissionAsync()
        {
            PermissionStatus permissionStatus =
                await Permissions.CheckStatusAsync<Permissions.StorageRead>().ConfigureAwait(true);

            if (permissionStatus != PermissionStatus.Granted)
            {
                permissionStatus =
                    await Permissions.RequestAsync<Permissions.StorageRead>().ConfigureAwait(true);
            }

            return permissionStatus == PermissionStatus.Granted;
        }

        private static string[] BuildSecondsChoices()
        {
            var choiceLabels = new string[FrameSettings.SlideshowIntervalChoices.Length];
            for (int choiceIndex = 0; choiceIndex < choiceLabels.Length; choiceIndex++)
            {
                choiceLabels[choiceIndex] =
                    DescribeInterval(FrameSettings.SlideshowIntervalChoices[choiceIndex]);
            }

            return choiceLabels;
        }

        /// <summary>
        /// Длительность показа словами. Часы отдельно от минут: «120 мин» рядом
        /// с «60 мин» читается хуже, чем «2 часа» рядом с «1 час».
        /// </summary>
        private static string DescribeInterval(int seconds)
        {
            if (seconds < 60)
            {
                return $"{seconds} сек";
            }

            if (seconds < 3600)
            {
                return $"{seconds / 60} мин";
            }

            int hours = seconds / 3600;
            return hours == 1 ? "1 час" : $"{hours} часа";
        }

        private static string[] BuildHoursChoices()
        {
            var choiceLabels = new string[FrameSettings.PollIntervalChoices.Length];
            for (int choiceIndex = 0; choiceIndex < choiceLabels.Length; choiceIndex++)
            {
                choiceLabels[choiceIndex] = $"каждые {FrameSettings.PollIntervalChoices[choiceIndex]} ч";
            }

            return choiceLabels;
        }

        private static string[] BuildCameraSnapshotIntervalChoices()
        {
            int[] choicesMs = FrameSettings.CameraSnapshotIntervalChoicesMs;
            var choiceLabels = new string[choicesMs.Length];
            for (int choiceIndex = 0; choiceIndex < choiceLabels.Length; choiceIndex++)
            {
                choiceLabels[choiceIndex] = DescribeSnapshotInterval(choicesMs[choiceIndex]);
            }

            return choiceLabels;
        }

        /// <summary>Меньше секунды — в миллисекундах, иначе в секундах с точностью до десятых.</summary>
        private static string DescribeSnapshotInterval(int milliseconds)
        {
            if (milliseconds < 1000)
            {
                return $"{milliseconds} мс";
            }

            double seconds = milliseconds / 1000.0;
            return seconds % 1 == 0
                ? $"{(int)seconds} с"
                : $"{seconds.ToString("0.#", CultureInfo.InvariantCulture)} с";
        }

        private void LoadCurrentSettings()
        {
            UseSharedAlbumSwitch.IsToggled = FrameSettings.UseSharedAlbum;
            UseImmichSwitch.IsToggled = FrameSettings.UseImmich;
            UseLocalFoldersSwitch.IsToggled = FrameSettings.UseLocalFolders;
            UpdateSourcePanels();

            ShareUrlEntry.Text = FrameSettings.SharedAlbumUrl;

            ImmichUrlEntry.Text = FrameSettings.ImmichServerUrl;
            ShowImmichApiKey();

            // Выбор запоминается настройками, а список альбомов — нет: до запроса
            // к серверу известны только те, что уже отмечены.
            _selectedImmichAlbumIds.Clear();
            _immichAlbumNames.Clear();

            string[] savedAlbumIds = FrameSettings.ImmichAlbumIds;
            string[] savedAlbumNames = FrameSettings.ImmichAlbumNames;

            for (int albumIndex = 0; albumIndex < savedAlbumIds.Length; albumIndex++)
            {
                _selectedImmichAlbumIds.Add(savedAlbumIds[albumIndex]);
                _immichAlbumNames[savedAlbumIds[albumIndex]] = albumIndex < savedAlbumNames.Length
                    ? savedAlbumNames[albumIndex]
                    : savedAlbumIds[albumIndex];
            }

            ImmichLimitPicker.SelectedIndex = Array.IndexOf(
                FrameSettings.AlbumPhotoLimitChoices, FrameSettings.ImmichPhotoLimit);
            if (ImmichLimitPicker.SelectedIndex < 0)
            {
                ImmichLimitPicker.SelectedIndex =
                    Array.IndexOf(FrameSettings.AlbumPhotoLimitChoices, AppSettings.DefaultAlbumPhotoLimit);
            }

            ShowImmichAlbumChoices();

            AlbumLimitPicker.SelectedIndex = Array.IndexOf(
                FrameSettings.AlbumPhotoLimitChoices, FrameSettings.AlbumPhotoLimit);
            if (AlbumLimitPicker.SelectedIndex < 0)
            {
                AlbumLimitPicker.SelectedIndex =
                    Array.IndexOf(FrameSettings.AlbumPhotoLimitChoices, AppSettings.DefaultAlbumPhotoLimit);
            }
            AlbumVideosSwitch.IsToggled = FrameSettings.DownloadAlbumVideos;
            MotionPhotosSwitch.IsToggled = FrameSettings.AnimateMotionPhotos;
            RecursiveSwitch.IsToggled = FrameSettings.LocalFolderRecursive;
            ShowSelectedFolders();
            ShuffleSwitch.IsToggled = FrameSettings.ShufflePhotos;
            FillScreenSwitch.IsToggled = FrameSettings.FillScreen;
            ShowClockSwitch.IsToggled = FrameSettings.ShowClock;
            ShowDateSwitch.IsToggled = FrameSettings.ShowDate;
            ShowCaptureInfoSwitch.IsToggled = FrameSettings.ShowCaptureInfo;

            PanelRevealPicker.SelectedIndex = Array.IndexOf(
                FrameSettings.PanelRevealChoices, FrameSettings.PanelRevealSeconds);
            if (PanelRevealPicker.SelectedIndex < 0)
            {
                PanelRevealPicker.SelectedIndex = 2;
            }

            HomeAssistantUrlEntry.Text = FrameSettings.HomeAssistantBaseUrl;
            ShowHomeAssistantToken();

            CameraRtspUrlEntry.Text = FrameSettings.CameraRtspUrl;
            CameraRtspNameEntry.Text = FrameSettings.CameraRtspName;

            CameraSnapshotIntervalPicker.SelectedIndex = Array.IndexOf(
                FrameSettings.CameraSnapshotIntervalChoicesMs,
                FrameSettings.CameraSnapshotIntervalMilliseconds);
            if (CameraSnapshotIntervalPicker.SelectedIndex < 0)
            {
                CameraSnapshotIntervalPicker.SelectedIndex =
                    Array.IndexOf(FrameSettings.CameraSnapshotIntervalChoicesMs, 1200);
            }

            ShowSensorsSwitch.IsToggled = FrameSettings.ShowSensors;
            SensorPanel.IsVisible = FrameSettings.ShowSensors;
            ShowSelectedSensors();

            NightModeSwitch.IsToggled = FrameSettings.NightModeEnabled;
            NightStartPicker.SelectedIndex = FrameSettings.NightStartHour;
            NightEndPicker.SelectedIndex = FrameSettings.NightEndHour;

            NightColorPicker.SelectedIndex =
                NightClockPalette.IndexOfHex(FrameSettings.NightClockColorHex);

            DayBrightnessSlider.Value = FrameSettings.DayScreenBrightnessPercent;
            ShowDayBrightness();
            NightBrightnessSlider.Value = FrameSettings.NightScreenBrightnessPercent;
            NightIntensitySlider.Value = FrameSettings.NightClockIntensityPercent;
            ShowNightBrightness();
            ShowNightIntensity();

            SlideshowIntervalPicker.SelectedIndex = Array.IndexOf(
                FrameSettings.SlideshowIntervalChoices, FrameSettings.SlideshowIntervalSeconds);
            PollIntervalPicker.SelectedIndex = Array.IndexOf(
                FrameSettings.PollIntervalChoices, FrameSettings.AlbumPollIntervalHours);

            // Если сохранённое значение не из списка (например, после смены набора вариантов),
            // не оставляем Picker пустым.
            if (SlideshowIntervalPicker.SelectedIndex < 0)
            {
                SlideshowIntervalPicker.SelectedIndex = 1;
            }

            if (PollIntervalPicker.SelectedIndex < 0)
            {
                PollIntervalPicker.SelectedIndex = 2;
            }

            LaunchOnBootSwitch.IsToggled = FrameSettings.LaunchOnBoot;
            LaunchDelayPanel.IsVisible = FrameSettings.LaunchOnBoot;

            LaunchDelayPicker.SelectedIndex = Array.IndexOf(
                FrameSettings.LaunchOnBootDelayChoices, FrameSettings.LaunchOnBootDelaySeconds);

            if (LaunchDelayPicker.SelectedIndex < 0)
            {
                LaunchDelayPicker.SelectedIndex =
                    Array.IndexOf(FrameSettings.LaunchOnBootDelayChoices, 10);
            }

            ShowDiagnostics();

            // Слепок — самым последним: он должен увидеть форму такой же, какой её
            // увидит человек, а не промежуточное состояние по ходу заполнения.
            _loadedSnapshot = BuildFormSnapshot();
        }

        /// <summary>
        /// Сворачивает видимые поля формы в одну строку для сравнения «было/стало».
        /// </summary>
        /// <remarks>
        /// Список папок и датчиков сюда не входит: их правят на отдельных страницах,
        /// и те сохраняют выбор сами, минуя кнопки «Сохранить» этой страницы.
        /// </remarks>
        private string BuildFormSnapshot()
        {
            var orderedAlbumIds = new List<string>(_selectedImmichAlbumIds);
            orderedAlbumIds.Sort(StringComparer.Ordinal);

            return string.Join('|', new[]
            {
                UseSharedAlbumSwitch.IsToggled.ToString(),
                UseImmichSwitch.IsToggled.ToString(),
                UseLocalFoldersSwitch.IsToggled.ToString(),
                ShareUrlEntry.Text ?? string.Empty,
                ImmichUrlEntry.Text ?? string.Empty,
                ImmichApiKeyEntry.Text ?? string.Empty,
                string.Join(',', orderedAlbumIds),
                ImmichLimitPicker.SelectedIndex.ToString(CultureInfo.InvariantCulture),
                AlbumVideosSwitch.IsToggled.ToString(),
                MotionPhotosSwitch.IsToggled.ToString(),
                RecursiveSwitch.IsToggled.ToString(),
                AlbumLimitPicker.SelectedIndex.ToString(CultureInfo.InvariantCulture),
                ShuffleSwitch.IsToggled.ToString(),
                FillScreenSwitch.IsToggled.ToString(),
                HomeAssistantUrlEntry.Text ?? string.Empty,
                HomeAssistantTokenEntry.Text ?? string.Empty,
                CameraSnapshotIntervalPicker.SelectedIndex.ToString(CultureInfo.InvariantCulture),
                ShowSensorsSwitch.IsToggled.ToString(),
                ShowClockSwitch.IsToggled.ToString(),
                ShowDateSwitch.IsToggled.ToString(),
                ShowCaptureInfoSwitch.IsToggled.ToString(),
                PanelRevealPicker.SelectedIndex.ToString(CultureInfo.InvariantCulture),
                NightModeSwitch.IsToggled.ToString(),
                NightStartPicker.SelectedIndex.ToString(CultureInfo.InvariantCulture),
                NightEndPicker.SelectedIndex.ToString(CultureInfo.InvariantCulture),
                NightColorPicker.SelectedIndex.ToString(CultureInfo.InvariantCulture),
                DayBrightnessSlider.Value.ToString(CultureInfo.InvariantCulture),
                NightBrightnessSlider.Value.ToString(CultureInfo.InvariantCulture),
                NightIntensitySlider.Value.ToString(CultureInfo.InvariantCulture),
                LaunchOnBootSwitch.IsToggled.ToString(),
                LaunchDelayPicker.SelectedIndex.ToString(CultureInfo.InvariantCulture),
                SlideshowIntervalPicker.SelectedIndex.ToString(CultureInfo.InvariantCulture),
                PollIntervalPicker.SelectedIndex.ToString(CultureInfo.InvariantCulture),
            });
        }

        private void OnDayBrightnessChanged(object? sender, ValueChangedEventArgs e) =>
            ShowDayBrightness();

        /// <summary>Ноль на дневной шкале означает «не трогать подсветку».</summary>
        private void ShowDayBrightness()
        {
            int percent = (int)Math.Round(DayBrightnessSlider.Value);
            DayBrightnessValueLabel.Text = percent == 0 ? "как в системе" : $"{percent} %";
        }

        private void OnNightBrightnessChanged(object? sender, ValueChangedEventArgs e) =>
            ShowNightBrightness();

        private void OnNightIntensityChanged(object? sender, ValueChangedEventArgs e) =>
            ShowNightIntensity();

        /// <summary>Ноль на шкале яркости означает «не трогать подсветку».</summary>
        private void ShowNightBrightness()
        {
            int percent = (int)Math.Round(NightBrightnessSlider.Value);
            NightBrightnessValueLabel.Text = percent == 0 ? "как в системе" : $"{percent} %";
        }

        private void ShowNightIntensity() =>
            NightIntensityValueLabel.Text = $"{(int)Math.Round(NightIntensitySlider.Value)} %";

        private void OnLaunchOnBootToggled(object? sender, ToggledEventArgs e) =>
            LaunchDelayPanel.IsVisible = LaunchOnBootSwitch.IsToggled;

        /// <summary>
        /// Тех.режим для обслуживания рамки: выключает автозапуск и напоминает шаги, как
        /// подтвердить разрешение отладки / настроить adb. Приложение остаётся home-экраном,
        /// поэтому после перезагрузки рамка всё равно покажет слайд-шоу (не кирпич) —
        /// отключаем только лишний автозапуск по BOOT_COMPLETED.
        /// </summary>
        private void OnTechModeClicked(object? sender, EventArgs e)
        {
            FrameSettings.LaunchOnBoot = false;
            LaunchOnBootSwitch.IsToggled = false;
            LaunchDelayPanel.IsVisible = false;

            DisplayAlert(
                "Тех.режим",
                "Автозапуск выключен (LaunchOnBoot = false). Приложение остаётся home-экраном, "
                + "поэтому после перезагрузки рамка всё равно покажет слайд-шоу.\n\n"
                + "Подключите рамку по USB и подтвердите «Разрешить отладку по USB» "
                + "(или доверьте ключ с уже авторизованной машины).\n\n"
                + "Для возврата включите «Открывать слайд-шоу при включении рамки».",
                "OK");
        }

        /// <summary>
        /// Раскрывает и сворачивает раздел настроек.
        /// </summary>
        /// <remarks>
        /// Раздел передаётся параметром жеста, поэтому обработчик один на все заголовки
        /// и знать о них ничего не должен. Значок раскрытия — первый символ подписи,
        /// так что менять его можно, не трогая остальной текст.
        ///
        /// Заголовок собран из Border с Label, а не из Button: у кнопки MAUI не
        /// настраивается выравнивание текста. Под подписью раздела идёт вторая строка —
        /// что внутри; поэтому заголовок ищется как первый Label, даже когда он лежит
        /// в контейнере.
        /// </remarks>
        private static void OnSectionHeaderTapped(object? sender, TappedEventArgs e)
        {
            if (sender is not Border header
                || FindTitleLabel(header.Content) is not Label title
                || e.Parameter is not VisualElement section)
            {
                return;
            }

            bool expanding = !section.IsVisible;
            section.IsVisible = expanding;
            title.Text = (expanding ? '▾' : '▸') + title.Text[1..];
        }

        /// <summary>Подпись раздела — сам Label либо первый Label внутри контейнера заголовка.</summary>
        private static Label? FindTitleLabel(object? headerContent) => headerContent switch
        {
            Label label => label,
            Layout layout => layout.Children.OfType<Label>().FirstOrDefault(),
            _ => null,
        };

        /// <summary>Настройки источника показываются только когда сам источник включён.</summary>
        private void UpdateSourcePanels()
        {
            SharedAlbumPanel.IsVisible = UseSharedAlbumSwitch.IsToggled;
            ImmichPanel.IsVisible = UseImmichSwitch.IsToggled;
            LocalFolderPanel.IsVisible = UseLocalFoldersSwitch.IsToggled;
        }

        private void OnSourceToggled(object? sender, ToggledEventArgs e) => UpdateSourcePanels();

        private void ShowSelectedFolders()
        {
            string[] folderPaths = FrameSettings.LocalFolderPaths;

            SelectedFoldersLabel.Text = folderPaths.Length == 0
                ? "Папки не выбраны"
                : $"Выбрано папок: {folderPaths.Length}";

            SelectedFoldersDetailLabel.Text = folderPaths.Length == 0
                ? "Нажмите «Выбрать папки…»"
                : string.Join("\n", folderPaths);
        }

        /// <summary>
        /// Сохраняем перед уходом на выбор папок: иначе введённая ссылка на альбом
        /// и переключатели потерялись бы при возврате.
        /// </summary>
        private async void OnPickFoldersClicked(object? sender, EventArgs e)
        {
            ApplySettings();
            await Shell.Current.GoToAsync(nameof(FolderPickerPage));
        }

        private void OnShowSensorsToggled(object? sender, ToggledEventArgs e) =>
            SensorPanel.IsVisible = ShowSensorsSwitch.IsToggled;

        private void ShowSelectedSensors()
        {
            string[] entityIds = FrameSettings.SensorEntityIds;

            SelectedSensorsLabel.Text = entityIds.Length == 0
                ? "Датчики не выбраны"
                : $"Выбрано датчиков: {entityIds.Length}";

            SelectedSensorsDetailLabel.Text = entityIds.Length == 0
                ? "Нажмите «Выбрать датчики…»"
                : string.Join(", ", entityIds);
        }

        private async void OnPickSensorsClicked(object? sender, EventArgs e)
        {
            ApplySettings();
            await Shell.Current.GoToAsync(nameof(SensorPickerPage));
        }

        /// <summary>Ищет Home Assistant в домашней сети, чтобы не набирать адрес пультом.</summary>
        private async void OnFindHomeAssistantServerClicked(object? sender, EventArgs e)
        {
            HomeAssistantFindButton.IsEnabled = false;
            HomeAssistantStatusLabel.Text = "Поиск в сети…";

            try
            {
                var finder = new LanServerFinder();
                List<string> foundServers = await finder.FindHomeAssistantServersAsync().ConfigureAwait(true);

                // Пока искали адрес, могли положить и файл с токеном — самое время его подхватить.
                ShowHomeAssistantToken();
                ApplyFoundServer(foundServers, HomeAssistantUrlEntry, HomeAssistantStatusLabel, "Home Assistant");
            }
            finally
            {
                HomeAssistantFindButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Проверяет введённый адрес и токен запросом списка датчиков — заодно и
        /// проверка связи, отдельной ей быть незачем.
        /// </summary>
        private async void OnTestHomeAssistantClicked(object? sender, EventArgs e)
        {
            if ((HomeAssistantUrlEntry.Text ?? string.Empty).Trim().Length == 0
                || (HomeAssistantTokenEntry.Text ?? string.Empty).Trim().Length == 0)
            {
                HomeAssistantStatusLabel.Text = "Сначала заполните адрес и токен";
                return;
            }

            // Клиент читает адрес и токен из настроек рамки, а не из текста на экране,
            // поэтому без сохранения проверялось бы прошлое значение.
            ApplySettings();

            HomeAssistantStatusLabel.Text = "Проверка связи...";

            HomeAssistantClient client =
                IPlatformApplication.Current?.Services.GetService<HomeAssistantClient>()
                ?? new HomeAssistantClient();

            try
            {
                List<HomeAssistantSensor> sensors =
                    await client.GetNumericSensorsAsync().ConfigureAwait(true);
                HomeAssistantStatusLabel.Text = $"Связь есть. Числовых датчиков: {sensors.Count}.";
            }
            catch (PhotoSourceException connectionFailure)
            {
                HomeAssistantStatusLabel.Text = connectionFailure.Message;
            }
        }

        private void ShowDiagnostics()
        {
            int cachedPhotoCount = SharedAlbumPhotoSource.GetCachedPhotoPaths().Count
                                   + new LocalFolderPhotoSource().GetPhotoPaths().Count;
            PhotoCountLabel.Text = $"Снимков готово к показу: {cachedPhotoCount}";

            DateTime? lastSyncUtc = FrameSettings.LastSyncUtc;
            LastSyncLabel.Text = lastSyncUtc is null
                ? "Последнее обновление: ещё не было"
                : $"Последнее обновление: {lastSyncUtc.Value.ToLocalTime():dd.MM.yyyy HH:mm}";

            StorageLabel.Text = DescribeFreeSpace();
            CacheSizeLabel.Text = DescribePhotoCacheSize();
            ShowTrashSize();
            VersionLabel.Text = $"Версия {AppInfo.Current.VersionString} ({AppInfo.Current.BuildString})";
        }

        /// <summary>
        /// Свободное место в разделе, где приложение хранит скачанные снимки.
        /// </summary>
        /// <remarks>
        /// Через DriveInfo это посчитать нельзя: Path.GetPathRoot на Unix возвращает "/",
        /// а корень на Android — крошечный rootfs и к /data отношения не имеет. StatFs
        /// считает именно тот раздел, в котором лежит переданный путь.
        /// </remarks>
        private static string DescribeFreeSpace()
        {
            try
            {
                var storageStats = new Android.OS.StatFs(FileSystem.AppDataDirectory);

                double freeGigabytes = storageStats.AvailableBytes / 1024d / 1024d / 1024d;
                double totalGigabytes = storageStats.TotalBytes / 1024d / 1024d / 1024d;

                return string.Format(
                    CultureInfo.CurrentCulture,
                    "Свободно в памяти рамки: {0:F1} из {1:F1} ГБ",
                    freeGigabytes,
                    totalGigabytes);
            }
            catch (Exception storageQueryFailure) when (
                storageQueryFailure is Java.Lang.Throwable or IOException
                    or UnauthorizedAccessException)
            {
                // Справочная строка не должна ронять экран настроек.
                return "Свободное место: неизвестно";
            }
        }

        /// <summary>
        /// Сколько файлов лежит в корзине рамки и сколько они занимают.
        /// </summary>
        /// <remarks>
        /// Кадры Immich сюда не попадают: у них хозяин — сервер, и убранный снимок уходит
        /// в корзину самого Immich. Здесь оказываются файлы из папок на устройстве
        /// и кадры общего альбома Google.
        /// </remarks>
        private void ShowTrashSize()
        {
            (int fileCount, long totalBytes) = MeasureTrash();

            EmptyTrashButton.IsVisible = fileCount > 0;

            TrashLabel.Text = fileCount == 0
                ? "Корзина рамки: пуста"
                : string.Format(
                    CultureInfo.CurrentCulture,
                    "Корзина рамки: файлов {0}, {1:F0} МБ",
                    fileCount,
                    totalBytes / 1024d / 1024d);
        }

        private static (int FileCount, long TotalBytes) MeasureTrash()
        {
            string trashDirectory = MediaTrash.RootDirectory;
            if (!Directory.Exists(trashDirectory))
            {
                return (0, 0);
            }

            int fileCount = 0;
            long totalBytes = 0;

            try
            {
                // Вместе с вложенными: у альбома и Immich там свои подкаталоги.
                foreach (string filePath in Directory.EnumerateFiles(
                    trashDirectory, "*", SearchOption.AllDirectories))
                {
                    fileCount++;
                    totalBytes += new FileInfo(filePath).Length;
                }
            }
            catch (Exception sizeQueryFailure) when (
                sizeQueryFailure is IOException or UnauthorizedAccessException)
            {
                // Посчитали сколько успели: цифра приблизительная, но лучше, чем ничего.
            }

            return (fileCount, totalBytes);
        }

        /// <summary>
        /// Очищает корзину рамки — насовсем, поэтому со спросом.
        /// </summary>
        private async void OnEmptyTrashClicked(object? sender, EventArgs e)
        {
            (int fileCount, _) = MeasureTrash();
            if (fileCount == 0)
            {
                return;
            }

            bool confirmed = await DisplayAlert(
                "Очистить корзину?",
                $"Файлов: {fileCount}. Они будут удалены с рамки насовсем — "
                + "снимки из папок на устройстве восстановить будет неоткуда.",
                "Удалить",
                "Отмена");

            if (!confirmed)
            {
                return;
            }

            int deletedCount = EmptyTrash();
            ShowTrashSize();

            TrashLabel.Text = deletedCount == fileCount
                ? $"Корзина рамки: удалено {deletedCount}"
                : $"Корзина рамки: удалено {deletedCount} из {fileCount}";
        }

        private static int EmptyTrash()
        {
            string trashDirectory = MediaTrash.RootDirectory;
            int deletedCount = 0;

            try
            {
                foreach (string filePath in Directory.EnumerateFiles(
                    trashDirectory, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.Delete(filePath);
                        deletedCount++;
                    }
                    catch (Exception deleteFailure) when (
                        deleteFailure is IOException or UnauthorizedAccessException)
                    {
                        // Один упрямый файл не должен срывать очистку остальных.
                    }
                }
            }
            catch (Exception walkFailure) when (
                walkFailure is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                // Ниже вернём то, что успели удалить.
            }

            return deletedCount;
        }

        /// <summary>
        /// Сколько занимает кэш скачанных снимков. Снимки из локальных папок не копируются
        /// и в этот размер не входят.
        /// </summary>
        private static string DescribePhotoCacheSize()
        {
            string cacheDirectory = SharedAlbumPhotoSource.PhotoLibraryDirectory;
            if (!Directory.Exists(cacheDirectory))
            {
                return "Кэш скачанных снимков: пуст";
            }

            long totalBytes = 0;
            try
            {
                foreach (string filePath in Directory.EnumerateFiles(cacheDirectory))
                {
                    totalBytes += new FileInfo(filePath).Length;
                }
            }
            catch (Exception sizeQueryFailure) when (
                sizeQueryFailure is IOException or UnauthorizedAccessException)
            {
                return "Кэш скачанных снимков: размер неизвестен";
            }

            return string.Format(
                CultureInfo.CurrentCulture,
                "Кэш скачанных снимков: {0:F0} МБ",
                totalBytes / 1024d / 1024d);
        }

        /// <summary>
        /// Подставляет ключ доступа: сохранённый, а если его нет — из файла на рамке.
        /// </summary>
        /// <remarks>
        /// Набирать сорок случайных символов пультом по экранной клавиатуре нереально,
        /// поэтому ключ можно просто скопировать на рамку файлом по USB.
        ///
        /// Файл имеет преимущество над сохранённым ключом: иначе положить рядом новый
        /// файл было бы бесполезно — рамка продолжала бы ходить со старым ключом, а
        /// смена ключа как раз и есть та задача, ради которой файл нужен. Ввести ключ
        /// руками это не мешает: тогда файла попросту нет.
        ///
        /// Подставленный ключ ещё нужно сохранить — как и всё остальное на этом экране.
        /// </remarks>
        private void ShowImmichApiKey()
        {
            string savedApiKey = FrameSettings.ImmichApiKey;
            string apiKeyFromFile = ImmichKeyFile.TryRead();

            if (apiKeyFromFile.Length == 0)
            {
                ImmichApiKeyEntry.Text = savedApiKey;
                return;
            }

            ImmichApiKeyEntry.Text = apiKeyFromFile;

            ImmichStatusLabel.Text = apiKeyFromFile == savedApiKey
                ? "Ключ взят из файла на рамке"
                : "В файле на рамке другой ключ — подставлен, нажмите «Сохранить»";
        }

        /// <summary>
        /// Подставляет токен доступа Home Assistant: сохранённый, а если его нет —
        /// из файла на рамке. См. <see cref="ShowImmichApiKey"/> — тот же приём.
        /// </summary>
        private void ShowHomeAssistantToken()
        {
            string savedToken = FrameSettings.HomeAssistantToken;
            string tokenFromFile = HomeAssistantKeyFile.TryRead();

            if (tokenFromFile.Length == 0)
            {
                HomeAssistantTokenEntry.Text = savedToken;
                HomeAssistantStatusLabel.Text = string.Empty;
                return;
            }

            HomeAssistantTokenEntry.Text = tokenFromFile;

            HomeAssistantStatusLabel.Text = tokenFromFile == savedToken
                ? "Токен взят из файла homeassistant.key"
                : "В файле homeassistant.key другой токен — подставлен, нажмите «Сохранить»";
        }

        /// <summary>
        /// Перерисовывает список альбомов Immich с отметками.
        /// </summary>
        /// <remarks>
        /// Пока альбомы не запрошены, в списке всё равно видны уже отмеченные: иначе
        /// открытие настроек без сети выглядело бы так, будто выбор потерян.
        /// </remarks>
        private void ShowImmichAlbumChoices()
        {
            ImmichAlbumList.Clear();

            List<(string Id, string Label)> rows = BuildImmichAlbumRows();

            foreach ((string albumId, string albumLabel) in rows)
            {
                ImmichAlbumList.Add(BuildImmichAlbumRow(albumId, albumLabel));
            }

            ShowImmichSelectionSummary();
        }

        private List<(string Id, string Label)> BuildImmichAlbumRows()
        {
            var rows = new List<(string Id, string Label)>();

            if (_immichAlbums.Count > 0)
            {
                foreach (ImmichAlbum album in _immichAlbums)
                {
                    rows.Add((album.Id, $"{album.Name} ({album.AssetCount})"));
                }

                return rows;
            }

            foreach (string albumId in _selectedImmichAlbumIds)
            {
                rows.Add((albumId, _immichAlbumNames.GetValueOrDefault(albumId, albumId)));
            }

            return rows;
        }

        /// <summary>Строка альбома: галочка у отмеченного, плюс у остальных.</summary>
        private View BuildImmichAlbumRow(string albumId, string albumLabel)
        {
            bool isSelected = _selectedImmichAlbumIds.Contains(albumId);

            var row = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(34) },
                    new ColumnDefinition { Width = GridLength.Star },
                },
                Padding = new Thickness(0, 6),
            };

            row.Add(new Label
            {
                Text = isSelected ? "\u2713" : "+",
                TextColor = isSelected ? Color.FromArgb("#4FC3F7") : Color.FromArgb("#555555"),
                FontSize = 18,
                VerticalOptions = LayoutOptions.Center,
            });

            row.Add(
                new Label
                {
                    Text = albumLabel,
                    TextColor = isSelected ? Colors.White : Colors.LightGray,
                    FontSize = 16,
                    LineBreakMode = LineBreakMode.TailTruncation,
                    VerticalOptions = LayoutOptions.Center,
                },
                column: 1);

            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => ToggleImmichAlbum(albumId);
            row.GestureRecognizers.Add(tap);

            return row;
        }

        private void ToggleImmichAlbum(string albumId)
        {
            if (!_selectedImmichAlbumIds.Remove(albumId))
            {
                _selectedImmichAlbumIds.Add(albumId);
            }

            ShowImmichAlbumChoices();
        }

        private void ShowImmichSelectionSummary() =>
            ImmichStatusLabel.Text = _selectedImmichAlbumIds.Count == 0
                ? "Ничего не отмечено — покажем всю библиотеку"
                : $"Отмечено альбомов: {_selectedImmichAlbumIds.Count}";

        /// <summary>Ищет Immich в домашней сети, чтобы не набирать адрес пультом.</summary>
        private async void OnFindImmichServerClicked(object? sender, EventArgs e)
        {
            ImmichFindButton.IsEnabled = false;
            ImmichStatusLabel.Text = "Поиск в сети…";

            try
            {
                var finder = new LanServerFinder();
                List<string> foundServers = await finder.FindImmichServersAsync().ConfigureAwait(true);

                // Пока искали адрес, могли положить и файл с ключом — самое время его подхватить.
                ShowImmichApiKey();
                ApplyFoundServer(foundServers, ImmichUrlEntry, ImmichStatusLabel, "Immich");
            }
            finally
            {
                ImmichFindButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Подставляет найденный адрес в поле. Ноль или несколько найденных — решение
        /// оставляем человеку: подставлять наугад неверный адрес хуже, чем спросить.
        /// </summary>
        private static void ApplyFoundServer(
            List<string> foundServers, Entry urlEntry, Label statusLabel, string serviceName)
        {
            switch (foundServers.Count)
            {
                case 0:
                    statusLabel.Text = $"{serviceName} не найден в этой сети — введите адрес вручную";
                    break;

                case 1:
                    urlEntry.Text = foundServers[0];
                    statusLabel.Text = $"Найден: {foundServers[0]}";
                    break;

                default:
                    statusLabel.Text =
                        $"Найдено несколько: {string.Join(", ", foundServers)} — введите нужный адрес вручную";
                    break;
            }
        }

        /// <summary>
        /// Спрашивает у сервера список альбомов — заодно это и проверка связи с ключом.
        /// </summary>
        private async void OnLoadImmichAlbumsClicked(object? sender, EventArgs e)
        {
            string serverUrl = ImmichUrlEntry.Text ?? string.Empty;
            string apiKey = (ImmichApiKeyEntry.Text ?? string.Empty).Trim();

            if (serverUrl.Trim().Length == 0 || apiKey.Length == 0)
            {
                ImmichStatusLabel.Text = "Сначала заполните адрес сервера и ключ доступа";
                return;
            }

            ImmichAlbumsButton.IsEnabled = false;
            ImmichStatusLabel.Text = "Соединение…";

            try
            {
                ImmichPhotoSource immichSource =
                    IPlatformApplication.Current?.Services.GetService<ImmichPhotoSource>()
                    ?? new ImmichPhotoSource();

                _immichAlbums = await immichSource
                    .GetAlbumsAsync(serverUrl, apiKey)
                    .ConfigureAwait(true);

                foreach (ImmichAlbum album in _immichAlbums)
                {
                    _immichAlbumNames[album.Id] = album.Name;
                }

                // Адрес мог быть введён без схемы: показываем то, что реально пойдёт в запрос.
                ImmichUrlEntry.Text = ImmichCatalog.NormalizeServerUrl(serverUrl);

                ShowImmichAlbumChoices();

                if (_immichAlbums.Count == 0)
                {
                    ImmichStatusLabel.Text = "Связь есть, но альбомов нет — покажем всю библиотеку";
                }
            }
            catch (PhotoSourceException immichFailure)
            {
                ImmichStatusLabel.Text = immichFailure.Message;
            }
            finally
            {
                ImmichAlbumsButton.IsEnabled = true;
            }
        }

        /// <summary>Переносит отметки на альбомах в настройки.</summary>
        private void ApplyImmichAlbumChoice()
        {
            var albumIds = new List<string>(_selectedImmichAlbumIds.Count);
            var albumNames = new List<string>(_selectedImmichAlbumIds.Count);

            // Порядок берём из списка сервера, если он получен: так подпись в настройках
            // совпадает с тем, что человек только что видел на экране.
            foreach (ImmichAlbum album in _immichAlbums)
            {
                if (_selectedImmichAlbumIds.Contains(album.Id))
                {
                    albumIds.Add(album.Id);
                    albumNames.Add(album.Name);
                }
            }

            foreach (string albumId in _selectedImmichAlbumIds)
            {
                if (!albumIds.Contains(albumId))
                {
                    albumIds.Add(albumId);
                    albumNames.Add(_immichAlbumNames.GetValueOrDefault(albumId, albumId));
                }
            }

            FrameSettings.ImmichAlbumIds = albumIds.ToArray();
            FrameSettings.ImmichAlbumNames = albumNames.ToArray();
        }

        private void ApplySettings()
        {
            FrameSettings.UseSharedAlbum = UseSharedAlbumSwitch.IsToggled;
            FrameSettings.UseImmich = UseImmichSwitch.IsToggled;
            FrameSettings.UseLocalFolders = UseLocalFoldersSwitch.IsToggled;

            FrameSettings.ImmichServerUrl = ImmichUrlEntry.Text ?? string.Empty;
            FrameSettings.ImmichApiKey = ImmichApiKeyEntry.Text ?? string.Empty;
            ApplyImmichAlbumChoice();

            if (ImmichLimitPicker.SelectedIndex >= 0)
            {
                FrameSettings.ImmichPhotoLimit =
                    FrameSettings.AlbumPhotoLimitChoices[ImmichLimitPicker.SelectedIndex];
            }

            FrameSettings.DownloadAlbumVideos = AlbumVideosSwitch.IsToggled;
            FrameSettings.AnimateMotionPhotos = MotionPhotosSwitch.IsToggled;

            // Список папок редактируется только на FolderPickerPage, здесь он не трогается.
            FrameSettings.LocalFolderRecursive = RecursiveSwitch.IsToggled;
            FrameSettings.SharedAlbumUrl = ShareUrlEntry.Text ?? string.Empty;

            if (AlbumLimitPicker.SelectedIndex >= 0)
            {
                FrameSettings.AlbumPhotoLimit =
                    FrameSettings.AlbumPhotoLimitChoices[AlbumLimitPicker.SelectedIndex];
            }
            FrameSettings.ShufflePhotos = ShuffleSwitch.IsToggled;
            FrameSettings.FillScreen = FillScreenSwitch.IsToggled;

            FrameSettings.HomeAssistantBaseUrl = HomeAssistantUrlEntry.Text ?? string.Empty;
            FrameSettings.HomeAssistantToken = HomeAssistantTokenEntry.Text ?? string.Empty;

            FrameSettings.CameraRtspUrl = CameraRtspUrlEntry.Text ?? string.Empty;
            FrameSettings.CameraRtspName = CameraRtspNameEntry.Text ?? string.Empty;

            if (CameraSnapshotIntervalPicker.SelectedIndex >= 0)
            {
                FrameSettings.CameraSnapshotIntervalMilliseconds =
                    FrameSettings.CameraSnapshotIntervalChoicesMs[CameraSnapshotIntervalPicker.SelectedIndex];
            }

            FrameSettings.ShowSensors = ShowSensorsSwitch.IsToggled;

            // Список датчиков правится только на SensorPickerPage.
            FrameSettings.ShowClock = ShowClockSwitch.IsToggled;
            FrameSettings.ShowDate = ShowDateSwitch.IsToggled;
            FrameSettings.ShowCaptureInfo = ShowCaptureInfoSwitch.IsToggled;

            if (PanelRevealPicker.SelectedIndex >= 0)
            {
                FrameSettings.PanelRevealSeconds =
                    FrameSettings.PanelRevealChoices[PanelRevealPicker.SelectedIndex];
            }
            FrameSettings.NightModeEnabled = NightModeSwitch.IsToggled;

            // Индекс в списке часов совпадает с самим часом.
            if (NightStartPicker.SelectedIndex >= 0)
            {
                FrameSettings.NightStartHour = NightStartPicker.SelectedIndex;
            }

            if (NightEndPicker.SelectedIndex >= 0)
            {
                FrameSettings.NightEndHour = NightEndPicker.SelectedIndex;
            }

            if (NightColorPicker.SelectedIndex >= 0)
            {
                FrameSettings.NightClockColorHex =
                    NightClockPalette.Choices[NightColorPicker.SelectedIndex].Hex;
            }

            FrameSettings.DayScreenBrightnessPercent = (int)Math.Round(DayBrightnessSlider.Value);
            FrameSettings.NightScreenBrightnessPercent = (int)Math.Round(NightBrightnessSlider.Value);
            FrameSettings.NightClockIntensityPercent = (int)Math.Round(NightIntensitySlider.Value);

            FrameSettings.LaunchOnBoot = LaunchOnBootSwitch.IsToggled;

            if (LaunchDelayPicker.SelectedIndex >= 0)
            {
                FrameSettings.LaunchOnBootDelaySeconds =
                    FrameSettings.LaunchOnBootDelayChoices[LaunchDelayPicker.SelectedIndex];
            }

            if (SlideshowIntervalPicker.SelectedIndex >= 0)
            {
                FrameSettings.SlideshowIntervalSeconds =
                    FrameSettings.SlideshowIntervalChoices[SlideshowIntervalPicker.SelectedIndex];
            }

            if (PollIntervalPicker.SelectedIndex >= 0)
            {
                FrameSettings.AlbumPollIntervalHours =
                    FrameSettings.PollIntervalChoices[PollIntervalPicker.SelectedIndex];
            }
        }

        private async void OnSaveClicked(object? sender, EventArgs e)
        {
            if (!await ConfirmImmichConnectionAsync().ConfigureAwait(true))
            {
                return;
            }

            ApplySettings();
            SyncRequestedOnReturn = true;
            await Shell.Current.GoToAsync("..");
        }

        private async void OnSaveOnlyClicked(object? sender, EventArgs e)
        {
            if (!await ConfirmImmichConnectionAsync().ConfigureAwait(true))
            {
                return;
            }

            ApplySettings();
            await Shell.Current.GoToAsync("..");
        }

        /// <summary>
        /// Проверяет связь с Immich перед сохранением, если источник включён и заполнен.
        /// </summary>
        /// <remarks>
        /// Тем же запросом, что и кнопка «Список альбомов», только без явного нажатия:
        /// адрес, введённый с опечаткой, иначе обнаружился бы только на следующей
        /// синхронизации, когда экран настроек уже закрыт и опечатку не с чем сравнить.
        /// Отказ не блокирует сохранение — спрашивает и оставляет решение человеку:
        /// сервер мог быть просто выключен на минуту.
        /// </remarks>
        /// <returns>False — остаться на странице; сохранение не продолжается.</returns>
        private async Task<bool> ConfirmImmichConnectionAsync()
        {
            if (!UseImmichSwitch.IsToggled)
            {
                return true;
            }

            string serverUrl = ImmichUrlEntry.Text ?? string.Empty;
            string apiKey = (ImmichApiKeyEntry.Text ?? string.Empty).Trim();

            if (serverUrl.Trim().Length == 0 || apiKey.Length == 0)
            {
                // Пустое поле — отдельная и уже видимая на экране проблема, не эта.
                return true;
            }

            ImmichStatusLabel.Text = "Проверяем связь с Immich…";

            ImmichPhotoSource immichSource =
                IPlatformApplication.Current?.Services.GetService<ImmichPhotoSource>()
                ?? new ImmichPhotoSource();

            string? failureMessage = await immichSource
                .TryCheckConnectionAsync(serverUrl, apiKey)
                .ConfigureAwait(true);

            if (failureMessage is null)
            {
                ImmichStatusLabel.Text = "Связь с Immich есть";

                // Проверка только что подтвердила связь напрямую — пауза от прежних
                // неудач (если она набралась) больше не про действительность.
                ImmichVideoCache.ResetAvailability();
                return true;
            }

            ImmichStatusLabel.Text = failureMessage;

            bool saveAnyway = await DisplayAlert(
                "Immich не отвечает",
                failureMessage,
                "Сохранить всё равно",
                "Остаться");

            if (saveAnyway)
            {
                // Только что убедились сами: сервер недоступен. Первому живому фото
                // незачем узнавать то же самое ещё раз тем же долгим таймаутом.
                ImmichVideoCache.MarkServerUnavailable();
            }

            return saveAnyway;
        }

        private void OnBackClicked(object? sender, EventArgs e) => _ = GoBackWithConfirmationAsync();

        /// <summary>
        /// Аппаратная «назад» на Android идёт мимо кнопки на экране, поэтому спрашивать
        /// о несохранённых правках нужно и здесь — тем же способом.
        /// </summary>
        protected override bool OnBackButtonPressed()
        {
            _ = GoBackWithConfirmationAsync();
            return true;
        }

        /// <summary>Спрашивает о потере правок, если форма отличается от загруженной.</summary>
        private async Task GoBackWithConfirmationAsync()
        {
            if (_loadedSnapshot is not null && BuildFormSnapshot() != _loadedSnapshot)
            {
                bool discard = await DisplayAlert(
                    "Несохранённые изменения",
                    "Уйти без сохранения правок?",
                    "Уйти",
                    "Остаться");

                if (!discard)
                {
                    return;
                }
            }

            await Shell.Current.GoToAsync("..");
        }
    }
}
