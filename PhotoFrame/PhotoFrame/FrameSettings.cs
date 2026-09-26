using System;
using Microsoft.Maui.Storage;

namespace PhotoFrame
{
    /// <summary>
    /// Настройки, которые пользователь меняет прямо на рамке.
    /// </summary>
    /// <remarks>
    /// Хранятся в <see cref="Preferences"/>, то есть переживают перезапуск и обновление
    /// приложения. Значения из secrets.props используются только как начальные: рамка
    /// стоит на полке без клавиатуры и монитора, и пересобирать APK ради смены интервала
    /// показа — плохая идея.
    /// </remarks>
    public static class FrameSettings
    {
        private const string UseSharedAlbumKey = "use_shared_album";
        private const string UseLocalFoldersKey = "use_local_folders";
        private const string LocalFolderPathsKey = "local_folder_paths";
        private const string LocalFolderRecursiveKey = "local_folder_recursive";
        private const string ShareUrlKey = "share_url";
        private const string UseImmichKey = "use_immich";
        private const string ImmichServerUrlKey = "immich_server_url";
        private const string ImmichApiKeyKey = "immich_api_key";
        private const string ImmichAlbumIdKey = "immich_album_id";
        private const string ImmichAlbumNameKey = "immich_album_name";
        private const string ImmichPhotoLimitKey = "immich_photo_limit";
        private const string SlideshowIntervalKey = "slideshow_interval_seconds";
        private const string PollIntervalKey = "poll_interval_hours";
        private const string ShuffleKey = "shuffle_photos";
        private const string FillScreenKey = "fill_screen";
        private const string NightModeKey = "night_mode_enabled";
        private const string NightStartHourKey = "night_start_hour";
        private const string NightEndHourKey = "night_end_hour";
        private const string NightClockColorKey = "night_clock_color";
        private const string NightScreenBrightnessKey = "night_screen_brightness";
        private const string DayScreenBrightnessKey = "day_screen_brightness";
        private const string NightClockIntensityKey = "night_clock_intensity";
        private const string HomeAssistantBaseUrlKey = "home_assistant_base_url";
        private const string HomeAssistantTokenKey = "home_assistant_token";
        private const string CameraSnapshotIntervalKey = "camera_snapshot_interval_ms";
        private const string CameraRtspUrlKey = "camera_rtsp_url";
        private const string CameraRtspNameKey = "camera_rtsp_name";
        private const string ShowSensorsKey = "show_sensors";
        private const string SensorEntityIdsKey = "sensor_entity_ids";
        private const string SensorIconsKey = "sensor_icons";
        private const string VideoRepeatKey = "video_repeat";
        private const string VideoMutedKey = "video_muted";
        private const string ShowCaptureInfoKey = "show_capture_info";
        private const string PanelRevealKey = "panel_reveal_seconds";
        private const string AlbumPhotoLimitKey = "album_photo_limit";
        private const string DownloadAlbumVideosKey = "download_album_videos";
        private const string AnimateMotionPhotosKey = "animate_motion_photos";
        private const string ShowClockKey = "show_clock";
        private const string ShowDateKey = "show_date";
        private const string LastSyncKey = "last_sync_utc";
        private const string LaunchOnBootKey = "launch_on_boot";
        private const string LaunchOnBootDelayKey = "launch_on_boot_delay_seconds";
        private const string TrashedAlbumFilesKey = "trashed_album_files";

        /// <summary>
        /// Варианты длительности показа одного кадра, секунды.
        /// </summary>
        /// <remarks>
        /// Верхние значения — для комнаты, где рамка висит как картина: кадр, меняющийся
        /// раз в пару часов, воспринимается именно картиной, а не слайд-шоу.
        /// </remarks>
        public static readonly int[] SlideshowIntervalChoices =
        {
            5, 10, 15, 30, 60, 300, 600, 1800, 3600, 7200,
        };

        /// <summary>Варианты периода проверки альбома, часы.</summary>
        public static readonly int[] PollIntervalChoices = { 1, 3, 6, 12, 24 };

        /// <summary>Варианты времени, через которое панель управления сама скрывается.</summary>
        public static readonly int[] PanelRevealChoices = { 3, 5, 10, 20, 30, 60 };

        /// <summary>Варианты задержки запуска после включения рамки, секунды.</summary>
        public static readonly int[] LaunchOnBootDelayChoices = { 0, 10, 20, 30, 60, 120 };

        /// <summary>
        /// Варианты предела на число скачиваемых из альбома кадров. 0 — без предела.
        /// </summary>
        public static readonly int[] AlbumPhotoLimitChoices = { 100, 250, 500, 1000, 2000, 5000, 0 };

        /// <summary>
        /// Брать снимки из общего альбома Google Photos. Может быть включено
        /// одновременно с <see cref="UseLocalFolders"/> — тогда наборы объединяются.
        /// </summary>
        public static bool UseSharedAlbum
        {
            get => Preferences.Default.Get(UseSharedAlbumKey, true);
            set => Preferences.Default.Set(UseSharedAlbumKey, value);
        }

        /// <summary>
        /// Скачивать из альбома и видео, а не только снимки.
        /// </summary>
        /// <remarks>
        /// Видео весят куда больше снимков, поэтому это отдельный выключатель. Когда он
        /// выключен, видео не показываются вовсе: заставка без возможности воспроизвести
        /// только сбивает с толку.
        /// </remarks>
        public static bool DownloadAlbumVideos
        {
            get => Preferences.Default.Get(DownloadAlbumVideosKey, true);
            set => Preferences.Default.Set(DownloadAlbumVideosKey, value);
        }

        /// <summary>
        /// Оживлять «живые фото» — снимки с приложенным клипом на пару секунд.
        /// </summary>
        /// <remarks>
        /// По умолчанию выключено: таких кадров в альбоме бывает много, и без спроса
        /// менять характер показа не стоит. Клип играется один раз, после чего кадр
        /// остаётся обычным снимком до конца своего времени.
        /// </remarks>
        public static bool AnimateMotionPhotos
        {
            get => Preferences.Default.Get(AnimateMotionPhotosKey, false);
            set => Preferences.Default.Set(AnimateMotionPhotosKey, value);
        }

        /// <summary>
        /// Брать снимки с домашнего сервера Immich.
        /// </summary>
        /// <remarks>
        /// По умолчанию выключено: у источника нет разумного значения по умолчанию —
        /// без адреса сервера и ключа доступа он всё равно ничего не покажет.
        /// </remarks>
        public static bool UseImmich
        {
            get => Preferences.Default.Get(UseImmichKey, false);
            set => Preferences.Default.Set(UseImmichKey, value);
        }

        /// <summary>Адрес сервера Immich, например http://192.168.1.10:2283.</summary>
        public static string ImmichServerUrl
        {
            get => ImmichCatalog.NormalizeServerUrl(
                Preferences.Default.Get(ImmichServerUrlKey, string.Empty));

            set => Preferences.Default.Set(
                ImmichServerUrlKey, ImmichCatalog.NormalizeServerUrl(value));
        }

        /// <summary>
        /// Ключ доступа к Immich (Account Settings → API Keys).
        /// </summary>
        /// <remarks>
        /// Хранится только на самом устройстве и в сборку не попадает: в отличие от
        /// ссылки на общий альбом, это полноценный доступ ко всей библиотеке, и
        /// значения по умолчанию из secrets.props у него намеренно нет.
        /// </remarks>
        public static string ImmichApiKey
        {
            get => Preferences.Default.Get(ImmichApiKeyKey, string.Empty);
            set => Preferences.Default.Set(ImmichApiKeyKey, value?.Trim() ?? string.Empty);
        }

        /// <summary>
        /// Идентификаторы выбранных альбомов; пусто — вся библиотека.
        /// </summary>
        /// <remarks>
        /// Список, а не один альбом: сервер принимает несколько идентификаторов одним
        /// запросом, а на рамке обычно и хотят видеть сразу несколько — «Отпуск» вместе
        /// с «Детьми», но без сканов документов. Хранится строками через перевод строки,
        /// как и список папок.
        /// </remarks>
        public static string[] ImmichAlbumIds
        {
            get => SplitLines(Preferences.Default.Get(ImmichAlbumIdKey, string.Empty));
            set => Preferences.Default.Set(
                ImmichAlbumIdKey, value is null ? string.Empty : string.Join('\n', value));
        }

        /// <summary>
        /// Названия выбранных альбомов, по одному на строку и в том же порядке, что
        /// и идентификаторы, — только для показа в настройках: запрашивать ради подписи
        /// весь список альбомов было бы расточительно.
        /// </summary>
        public static string[] ImmichAlbumNames
        {
            get => SplitLines(Preferences.Default.Get(ImmichAlbumNameKey, string.Empty));
            set => Preferences.Default.Set(
                ImmichAlbumNameKey, value is null ? string.Empty : string.Join('\n', value));
        }

        private static string[] SplitLines(string storedValue) =>
            storedValue.Split(
                '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        /// <summary>
        /// Предел на число кадров, скачиваемых из Immich; 0 — без предела.
        /// </summary>
        /// <remarks>
        /// Свой, а не общий с альбомом Google: у своего сервера снимков обычно на порядок
        /// больше, чем в расшаренном альбоме, и один предел на двоих означал бы, что
        /// удобное для одного источника калечит другой.
        /// </remarks>
        public static int ImmichPhotoLimit
        {
            get => Preferences.Default.Get(ImmichPhotoLimitKey, AppSettings.DefaultAlbumPhotoLimit);
            set => Preferences.Default.Set(ImmichPhotoLimitKey, value);
        }

        /// <summary>True, если Immich можно опрашивать: есть и адрес, и ключ.</summary>
        public static bool IsImmichConfigured =>
            ImmichServerUrl.Length > 0 && ImmichApiKey.Length > 0;

        /// <summary>Брать снимки из папок на устройстве.</summary>
        public static bool UseLocalFolders
        {
            get => Preferences.Default.Get(UseLocalFoldersKey, false);
            set => Preferences.Default.Set(UseLocalFoldersKey, value);
        }

        /// <summary>
        /// Папки со снимками, по одной на строку. Порядок задаёт порядок показа.
        /// </summary>
        public static string[] LocalFolderPaths
        {
            get
            {
                string storedPaths = Preferences.Default.Get(LocalFolderPathsKey, string.Empty);
                return storedPaths.Split(
                    '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }

            set => Preferences.Default.Set(
                LocalFolderPathsKey, value is null ? string.Empty : string.Join('\n', value));
        }

        /// <summary>Заходить ли во вложенные папки.</summary>
        public static bool LocalFolderRecursive
        {
            get => Preferences.Default.Get(LocalFolderRecursiveKey, true);
            set => Preferences.Default.Set(LocalFolderRecursiveKey, value);
        }

        /// <summary>
        /// Ссылка на публично расшаренный альбом Google Photos.
        /// По умолчанию берётся из secrets.props, дальше живёт в настройках устройства.
        /// </summary>
        public static string SharedAlbumUrl
        {
            get => Preferences.Default.Get(ShareUrlKey, LocalConfig.SharedAlbumUrl);
            set => Preferences.Default.Set(ShareUrlKey, value?.Trim() ?? string.Empty);
        }

        /// <summary>Сколько секунд показывается один снимок.</summary>
        public static int SlideshowIntervalSeconds
        {
            get => Preferences.Default.Get(SlideshowIntervalKey, 10);
            set => Preferences.Default.Set(SlideshowIntervalKey, value);
        }

        /// <summary>Как часто рамка сама проверяет альбом на изменения.</summary>
        public static int AlbumPollIntervalHours
        {
            get => Preferences.Default.Get(PollIntervalKey, 6);
            set => Preferences.Default.Set(PollIntervalKey, value);
        }

        /// <summary>Показывать снимки в случайном порядке.</summary>
        public static bool ShufflePhotos
        {
            get => Preferences.Default.Get(ShuffleKey, false);
            set => Preferences.Default.Set(ShuffleKey, value);
        }

        /// <summary>
        /// True — кадр растягивается на весь экран с обрезкой (AspectFill),
        /// False — вписывается целиком (AspectFit). По умолчанию не обрезаем:
        /// в альбоме встречаются вертикальные снимки.
        /// </summary>
        public static bool FillScreen
        {
            get => Preferences.Default.Get(FillScreenKey, false);
            set => Preferences.Default.Set(FillScreenKey, value);
        }

        /// <summary>
        /// Ночью показывать вместо слайд-шоу крупные приглушённые часы.
        /// </summary>
        /// <remarks>
        /// Рамка обычно стоит в комнате, где спят: яркие сменяющиеся снимки ночью мешают,
        /// а часы на чёрном фоне остаются полезными.
        /// </remarks>
        public static bool NightModeEnabled
        {
            get => Preferences.Default.Get(NightModeKey, true);
            set => Preferences.Default.Set(NightModeKey, value);
        }

        /// <summary>
        /// Яркость подсветки днём, проценты; 0 — не трогать.
        /// </summary>
        /// <remarks>
        /// Отдельно от ночной: рамка на солнце и рамка в сумерках требуют разного, а
        /// системную яркость на этом устройстве не выставить — своей кнопки нет, и
        /// автоматики тоже (датчик освещённости в прошивке заявлен, но не работает).
        /// </remarks>
        public static int DayScreenBrightnessPercent
        {
            get => Preferences.Default.Get(DayScreenBrightnessKey, 0);
            set => Preferences.Default.Set(DayScreenBrightnessKey, Math.Clamp(value, 0, 100));
        }

        /// <summary>Час начала ночного режима.</summary>
        public static int NightStartHour
        {
            get => Preferences.Default.Get(NightStartHourKey, 22);
            set => Preferences.Default.Set(NightStartHourKey, value);
        }

        /// <summary>Час окончания ночного режима.</summary>
        public static int NightEndHour
        {
            get => Preferences.Default.Get(NightEndHourKey, 7);
            set => Preferences.Default.Set(NightEndHourKey, value);
        }

        /// <summary>
        /// Цвет ночных часов. Значение всегда из <see cref="NightClockPalette"/>.
        /// </summary>
        public static string NightClockColorHex
        {
            get => NightClockPalette.ResolveHex(
                Preferences.Default.Get(NightClockColorKey, NightClockPalette.DefaultHex));

            set => Preferences.Default.Set(
                NightClockColorKey, NightClockPalette.ResolveHex(value));
        }

        /// <summary>
        /// Насыщенность самих цифр ночью, проценты.
        /// </summary>
        /// <remarks>
        /// Отдельно от яркости экрана: подсветка упирается в предел устройства (на рамке
        /// это 20 из 255, около 8%), и дальше гасить можно только краской. Разделение
        /// позволяет свести одно с другим — приглушить подсветку, а цифры сделать совсем
        /// бледными, либо наоборот.
        /// </remarks>
        public static int NightClockIntensityPercent
        {
            get => Preferences.Default.Get(NightClockIntensityKey, 50);
            set => Preferences.Default.Set(NightClockIntensityKey, Math.Clamp(value, 1, 100));
        }

        /// <summary>
        /// Яркость экрана, пока показаны ночные часы. Проценты; 0 — не вмешиваться.
        /// </summary>
        /// <remarks>
        /// Приглушается именно подсветка, а не рисунок: прозрачностью нужного вида не
        /// добиться, пока сама подсветка светит по-дневному.
        /// У рамки настоящая управляемая подсветка (`/sys/class/backlight`, 256 шагов),
        /// а яркость окна её перекрывает и не требует ни прав, ни правки системных
        /// настроек.
        /// </remarks>
        public static int NightScreenBrightnessPercent
        {
            get => Preferences.Default.Get(NightScreenBrightnessKey, 10);
            set => Preferences.Default.Set(NightScreenBrightnessKey, value);
        }

        /// <summary>
        /// True, если указанное время попадает в ночной интервал.
        /// </summary>
        /// <remarks>
        /// Интервал обычно переходит через полночь (22:00–07:00), поэтому сравнение
        /// «начало &lt;= час &lt; конец» здесь не работает.
        /// </remarks>
        public static bool IsNightHour(int hour)
        {
            return NightSchedule.IsNightHour(hour, NightStartHour, NightEndHour);
        }

        /// <summary>Адрес Home Assistant, например http://homeassistant.local:8123.</summary>
        /// <remarks>
        /// Внутренний адрес, а не внешний: рамке незачем интернет, чтобы прочитать датчик.
        /// Settings → System → Network → Home Assistant URL (Internal).
        /// </remarks>
        public static string HomeAssistantBaseUrl
        {
            get => ImmichCatalog.NormalizeServerUrl(
                Preferences.Default.Get(HomeAssistantBaseUrlKey, string.Empty));

            set => Preferences.Default.Set(
                HomeAssistantBaseUrlKey, ImmichCatalog.NormalizeServerUrl(value));
        }

        /// <summary>
        /// Долгоживущий токен Home Assistant (профиль → Security → Long-lived access tokens).
        /// </summary>
        /// <remarks>
        /// Хранится только на устройстве, как и ключ Immich: токен даёт полный доступ
        /// к дому, а не к одному альбому, и значения по умолчанию из secrets.props
        /// у него намеренно нет — только то, что введено на этом экране.
        /// </remarks>
        public static string HomeAssistantToken
        {
            get => Preferences.Default.Get(HomeAssistantTokenKey, string.Empty);
            set => Preferences.Default.Set(HomeAssistantTokenKey, value?.Trim() ?? string.Empty);
        }

        /// <summary>Варианты периода опроса снимка камеры, миллисекунды.</summary>
        public static readonly int[] CameraSnapshotIntervalChoicesMs =
            { 500, 800, 1200, 2000, 3000, 5000, 10000 };

        /// <summary>
        /// Как часто CameraViewPage запрашивает новый снимок у Home Assistant.
        /// </summary>
        /// <remarks>
        /// Некоторым источникам камеры каждый запрос снимка обходится не бесплатно —
        /// например, если сущность на деле оборачивает съёмку телефоном, каждый опрос
        /// может означать настоящий щелчок затвора и его звук, — и тогда снизить
        /// частоту проще, чем лезть в код.
        /// </remarks>
        public static int CameraSnapshotIntervalMilliseconds
        {
            get => Preferences.Default.Get(CameraSnapshotIntervalKey, 1200);
            set => Preferences.Default.Set(CameraSnapshotIntervalKey, value);
        }

        /// <summary>
        /// Адрес прямого RTSP-потока IP-камеры, например
        /// <c>rtsp://user:password@192.168.1.50:554/stream1</c>. Пусто — прямого источника нет.
        /// </summary>
        /// <remarks>
        /// Прямой источник минует перекодировку Home Assistant: на нестабильной камере
        /// она рвётся, а RTSP-поток рамка играет сама, без потери картины.
        /// </remarks>
        public static string CameraRtspUrl
        {
            get
            {
                // Файл в общей памяти — удобнее, чем набирать длинный адрес пультом, и
                // приоритетнее сохранённого значения (как ключ Home Assistant из файла).
                string fromFile = CameraRtspFile.TryReadUrl();
                if (!string.IsNullOrWhiteSpace(fromFile))
                {
                    return fromFile;
                }

                return Preferences.Default.Get(CameraRtspUrlKey, string.Empty);
            }
            set => Preferences.Default.Set(CameraRtspUrlKey, value?.Trim() ?? string.Empty);
        }

        /// <summary>Подпись над прямой камерой, если адрес задан. Пусто — показываем сам адрес.</summary>
        public static string CameraRtspName
        {
            get
            {
                string fromFile = CameraRtspFile.TryReadName();
                if (!string.IsNullOrWhiteSpace(fromFile))
                {
                    return fromFile;
                }

                return Preferences.Default.Get(CameraRtspNameKey, string.Empty);
            }
            set => Preferences.Default.Set(CameraRtspNameKey, value?.Trim() ?? string.Empty);
        }

        /// <summary>Показывать значения датчиков Home Assistant поверх снимка.</summary>
        public static bool ShowSensors
        {
            get => Preferences.Default.Get(ShowSensorsKey, false);
            set => Preferences.Default.Set(ShowSensorsKey, value);
        }

        /// <summary>
        /// Выбранные датчики в порядке показа. Количество не ограничено: строка с
        /// показаниями переносится по словам.
        /// </summary>
        public static string[] SensorEntityIds
        {
            get
            {
                string storedIds = Preferences.Default.Get(SensorEntityIdsKey, string.Empty);
                return storedIds.Split(
                    '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }

            set => Preferences.Default.Set(
                SensorEntityIdsKey, value is null ? string.Empty : string.Join('\n', value));
        }

        /// <summary>
        /// Значок, выбранный для датчика. Пустая строка — без значка.
        /// </summary>
        /// <remarks>
        /// Хранится как строки "entity_id\tзначок": выбранные датчики могут все оказаться
        /// термометрами, и без значка непонятно, где какая температура.
        /// </remarks>
        public static string GetSensorIcon(string entityId)
        {
            foreach (string line in ReadSensorIconLines())
            {
                int separatorIndex = line.IndexOf('\t');
                if (separatorIndex > 0
                    && line.AsSpan(0, separatorIndex).SequenceEqual(entityId))
                {
                    return line[(separatorIndex + 1)..];
                }
            }

            return string.Empty;
        }

        public static void SetSensorIcon(string entityId, string icon)
        {
            var kept = new List<string>();
            foreach (string line in ReadSensorIconLines())
            {
                int separatorIndex = line.IndexOf('\t');
                if (separatorIndex > 0
                    && !line.AsSpan(0, separatorIndex).SequenceEqual(entityId))
                {
                    kept.Add(line);
                }
            }

            if (!string.IsNullOrEmpty(icon))
            {
                kept.Add(entityId + '\t' + icon);
            }

            Preferences.Default.Set(SensorIconsKey, string.Join('\n', kept));
        }

        private static string[] ReadSensorIconLines() =>
            Preferences.Default.Get(SensorIconsKey, string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        /// <summary>Повторять видео по кругу вместо перехода к следующему кадру.</summary>
        public static bool VideoRepeat
        {
            get => Preferences.Default.Get(VideoRepeatKey, false);
            set => Preferences.Default.Set(VideoRepeatKey, value);
        }

        /// <summary>
        /// Воспроизводить видео без звука.
        /// </summary>
        /// <remarks>
        /// По умолчанию включено: рамка стоит в комнате, и внезапный звук из фотографии
        /// пугает сильнее, чем радует.
        /// </remarks>
        public static bool VideoMuted
        {
            get => Preferences.Default.Get(VideoMutedKey, true);
            set => Preferences.Default.Set(VideoMutedKey, value);
        }

        /// <summary>
        /// Показывать, чем и когда снят кадр.
        /// </summary>
        /// <remarks>
        /// Строка появляется только если данные есть: Google при пересжатии вырезает EXIF,
        /// и у снимков из общего альбома подписи обычно не будет.
        /// </remarks>
        public static bool ShowCaptureInfo
        {
            get => Preferences.Default.Get(ShowCaptureInfoKey, true);
            set => Preferences.Default.Set(ShowCaptureInfoKey, value);
        }

        /// <summary>
        /// Сколько секунд панель управления остаётся на экране после касания.
        /// </summary>
        public static int PanelRevealSeconds
        {
            get => Preferences.Default.Get(PanelRevealKey, 10);
            set => Preferences.Default.Set(PanelRevealKey, value);
        }

        /// <summary>
        /// Сколько кадров максимум скачивать из общего альбома. 0 — без предела.
        /// </summary>
        /// <remarks>
        /// Ограничение касается только альбома: каждый его кадр — это HTTP-запрос и место
        /// на диске. Локальные папки не ограничены, там файлы не копируются.
        /// </remarks>
        public static int AlbumPhotoLimit
        {
            get => Preferences.Default.Get(AlbumPhotoLimitKey, AppSettings.DefaultAlbumPhotoLimit);
            set => Preferences.Default.Set(AlbumPhotoLimitKey, value);
        }

        /// <summary>Показывать часы поверх снимка.</summary>
        public static bool ShowClock
        {
            get => Preferences.Default.Get(ShowClockKey, true);
            set => Preferences.Default.Set(ShowClockKey, value);
        }

        /// <summary>Показывать дату под часами.</summary>
        public static bool ShowDate
        {
            get => Preferences.Default.Get(ShowDateKey, true);
            set => Preferences.Default.Set(ShowDateKey, value);
        }

        /// <summary>
        /// Открывать слайд-шоу после включения рамки.
        /// </summary>
        /// <remarks>
        /// По умолчанию включено: рамка включается кнопкой питания или после отключения
        /// света, и запускать приложение руками с пульта лаунчера — не то, чего ждут
        /// от фоторамки.
        /// </remarks>
        public static bool LaunchOnBoot
        {
            get => Preferences.Default.Get(LaunchOnBootKey, true);
            set => Preferences.Default.Set(LaunchOnBootKey, value);
        }

        /// <summary>
        /// Через сколько секунд после загрузки открывать слайд-шоу.
        /// </summary>
        /// <remarks>
        /// Сразу после загрузки система ещё поднимает свой лаунчер и перекрывает им
        /// чужие активности, поэтому запуск лучше отложить.
        /// </remarks>
        public static int LaunchOnBootDelaySeconds
        {
            get => Preferences.Default.Get(LaunchOnBootDelayKey, 10);
            set => Preferences.Default.Set(LaunchOnBootDelayKey, value);
        }

        /// <summary>Когда последний раз успешно синхронизировались. UTC.</summary>
        public static DateTime? LastSyncUtc
        {
            get
            {
                long storedTicks = Preferences.Default.Get(LastSyncKey, 0L);
                return storedTicks == 0 ? null : new DateTime(storedTicks, DateTimeKind.Utc);
            }
            set => Preferences.Default.Set(LastSyncKey, value?.Ticks ?? 0L);
        }

        /// <summary>
        /// Имена файлов скачанных кадров, убранных в корзину.
        /// </summary>
        /// <remarks>
        /// Список общий для альбома Google и для Immich: имена там и там выводятся
        /// хэшированием, совпасть не могут, а смысл один — не качать убранное заново.
        ///
        /// Без этого списка кнопка «Убрать» для альбома выглядела бы сломанной: файла в
        /// кэше нет, ссылка в альбоме осталась, и очередная синхронизация скачивала бы
        /// снимок заново. Имя файла — хэш ссылки, поэтому список остаётся верным и после
        /// пересоздания кэша.
        /// </remarks>
        public static string[] TrashedAlbumFileNames =>
            Preferences.Default.Get(TrashedAlbumFilesKey, string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        /// <summary>Запоминает, что кадр альбома убран, и качать его больше не нужно.</summary>
        public static void AddTrashedAlbumFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return;
            }

            var storedNames = new List<string>(TrashedAlbumFileNames);
            foreach (string storedName in storedNames)
            {
                if (storedName.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            storedNames.Add(fileName);
            Preferences.Default.Set(TrashedAlbumFilesKey, string.Join('\n', storedNames));
        }

        /// <summary>True, если ссылка на альбом задана.</summary>
        public static bool IsAlbumConfigured => !string.IsNullOrWhiteSpace(SharedAlbumUrl);
    }
}
