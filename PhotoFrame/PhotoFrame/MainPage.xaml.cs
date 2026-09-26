using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Microsoft.Maui;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Graphics;

namespace PhotoFrame
{
    public partial class MainPage : ContentPage
    {
        /// <summary>
        /// Толщина обводки в пикселях. Время крупное, ему нужен чуть заметнее контур,
        /// чем мелкой дате: одинаковая толщина делает дату «жирной».
        /// </summary>
        private const double TimeOutlineWidth = 1.5;
        private const double DateOutlineWidth = 1;

        /// <summary>
        /// Обводка не чисто чёрная: полупрозрачная читается так же, но не выглядит
        /// нарисованной поверх снимка.
        /// </summary>
        private const double OutlineOpacity = 0.55;

        /// <summary>
        /// Неразрывный пробел между значком датчика и его значением.
        /// </summary>
        /// <remarks>
        /// Перенос по словам рвёт строку по любому пробелу, и значение уходит на другую
        /// строку в отрыве от своего значка. Значки между собой разделены обычными
        /// пробелами, поэтому переносится строка именно по ним. Константа, а не сам
        /// символ в коде: невидимый пробел в исходнике не отличить от обычного.
        /// </remarks>
        private const char NonBreakingSpace = '\u00A0';

        /// <summary>
        /// Предельная ширина строки датчиков в единицах устройства.
        /// </summary>
        /// <remarks>
        /// Примерно половина ширины экрана рамки (1280): дальше показания начинают
        /// перечёркивать середину кадра, а перенос по словам оставляет их у своего угла.
        /// </remarks>
        private const double SensorLineMaximumWidth = 620;

        /// <summary>
        /// Смещения копий текста, из которых складывается обводка. Восемь направлений
        /// дают ровный контур; меньше — и на диагоналях появляются просветы.
        /// </summary>
        private static readonly (double X, double Y)[] OutlineOffsets =
        {
            (-1, -1), (0, -1), (1, -1),
            (-1, 0), (1, 0),
            (-1, 1), (0, 1), (1, 1),
        };

        /// <summary>
        /// Углы, по которым «гуляют» часы. Центр не используется: там кадр интереснее всего,
        /// а сверху ещё и панель управления.
        /// </summary>
        private static readonly (LayoutOptions Horizontal, LayoutOptions Vertical, Thickness Margin)[]
            ClockPositions =
            {
                (LayoutOptions.Start, LayoutOptions.End, new Thickness(48, 0, 0, 40)),
                (LayoutOptions.End, LayoutOptions.End, new Thickness(0, 0, 48, 40)),
                (LayoutOptions.End, LayoutOptions.Start, new Thickness(0, 132, 48, 0)),
                (LayoutOptions.Start, LayoutOptions.Start, new Thickness(48, 132, 0, 0)),
            };

        private readonly CompositePhotoSource _photoSource;
        private readonly System.Timers.Timer _slideshowTimer;
        private readonly System.Timers.Timer _albumPollTimer;
        private readonly System.Timers.Timer _panelHideTimer;
        private readonly System.Timers.Timer _clockTimer;

        /// <summary>Гасит и зажигает двоеточие ночных часов.</summary>
        private readonly System.Timers.Timer _nightBlinkTimer;

        /// <summary>Опрос позиции воспроизведения: у VideoView нет события о прогрессе.</summary>
        private readonly System.Timers.Timer _videoProgressTimer;

        /// <summary>Гасит всплывающее сообщение.</summary>
        private readonly System.Timers.Timer _toastHideTimer;

        /// <summary>Копии текста времени: восемь для обводки плюс одна основная.</summary>
        private readonly List<Label> _clockTimeLabels;
        private readonly List<Label> _clockDateLabels;
        private readonly List<Label> _sensorLabels;
        private readonly List<Label> _captureInfoLabels;
        private readonly List<Label> _photoCounterLabels;

        private readonly HomeAssistantClient _homeAssistantClient;

        /// <summary>
        /// Идёт ли уже запрос датчиков: не даёт следующей минуте запустить второй
        /// поверх первого, если Home Assistant не ответил за минуту.
        /// </summary>
        private bool _isRefreshingSensors;

        /// <summary>Минута, для которой датчики уже перечитаны.</summary>
        private string? _lastSensorMinute;

        /// <summary>
        /// Есть ли в Home Assistant хоть одна камера и отвечает ли он вообще.
        /// </summary>
        /// <remarks>
        /// Кнопку камеры незачем показывать, если адрес настроен, но сам Home Assistant
        /// не отвечает или камер в нём нет, — нажатие вело бы только на экран с ошибкой.
        /// Значение — снимок последней проверки, а не живой запрос при каждом нажатии:
        /// показ панели должен быть мгновенным.
        /// </remarks>
        private bool _camerasAvailable;

        /// <summary>Идёт ли уже проверка камер: не даёт запросам накладываться друг на друга.</summary>
        private bool _isRefreshingCameraAvailability;

        /// <summary>
        /// Показания датчиков одной строкой. Ночью попадают в сам кадр часов, поэтому
        /// нужны отдельно от меток наложения.
        /// </summary>
        private string? _sensorLineText;

        /// <summary>Не даём проверке по таймеру наложиться на нажатие кнопки.</summary>
        private readonly SemaphoreSlim _syncGate = new(1, 1);

        private List<string> _localPhotoPaths = new();

        /// <summary>Индекс показанного сейчас кадра. -1 — ещё ничего не показано.</summary>
        private int _currentPhotoIndex = -1;

        /// <summary>
        /// Сохранённый порядок показа уже пробовали восстановить.
        /// </summary>
        /// <remarks>
        /// Попытка ровно одна, при первой загрузке: дальше набор меняют обновления
        /// источников, и продолжать прежнюю последовательность уже незачем.
        /// </remarks>
        private bool _hasTriedRestoringOrder;

        /// <summary>
        /// Режим порядка, по которому построена текущая очередь. null — порядок ещё
        /// не задавался.
        /// </summary>
        /// <remarks>
        /// Нужен, чтобы заметить переключение «случайного порядка» на экране настроек:
        /// набор кадров при этом не меняется, и без этой памяти возврат с настроек
        /// не перестраивал бы очередь — переключатель не делал бы ничего.
        /// </remarks>
        private bool? _appliedShuffleMode;

        private int _clockPositionIndex;

        /// <summary>Ночной режим сейчас активен. null — состояние ещё не определялось.</summary>
        private bool? _isNightModeActive;

        /// <summary>Минута, для которой уже нарисован ночной кадр.</summary>
        private string? _lastRenderedNightMinute;

        /// <summary>Текущая фаза шахматной маски ночных часов.</summary>
        private bool _nightMaskPhaseShifted;

        /// <summary>Кадры, лежащие в двух слоях ночных часов.</summary>
        private Android.Graphics.Bitmap? _frontNightFrame;
        private Android.Graphics.Bitmap? _backNightFrame;

        /// <summary>Показанный кадр лежит в верхнем слое.</summary>
        private bool _nightFrameInFrontLayer;

        /// <summary>Сколько кадров ночных часов отрисовано за время работы.</summary>
        private int _nightFramesRendered;

        /// <summary>Растёт на каждом кадре: отбрасывает анализ устаревшего снимка.</summary>
        private int _photoGeneration;

        /// <summary>Текущий кадр — видеофайл.</summary>
        private bool _isCurrentSlideVideo;

        /// <summary>Видео сейчас воспроизводится, слайд-шоу приостановлено.</summary>
        private bool _isVideoPlaying;

        /// <summary>У текущего кадра есть данные о съёмке.</summary>
        private bool _hasCaptureInfo;

        /// <summary>Кадр, о котором спрашивает выдвинутое подтверждение.</summary>
        private string? _pendingRemovalPath;

        /// <summary>Живое фото, для которого спрашивает подтверждение отвязка клипа.</summary>
        private string? _pendingDetachPosterPath;

        /// <summary>Заставка кадра альбома, чей клип сейчас играет.</summary>
        private string? _currentAlbumVideoPoster;

        /// <summary>
        /// Играет клип живого фото, а не видео.
        /// </summary>
        /// <remarks>
        /// Отличается от воспроизведения видео тем, что слайд-шоу не остановлено, кнопок
        /// управления нет и по окончании кадр не листается.
        /// </remarks>
        private bool _isMotionPlayback;

        /// <summary>
        /// Заставка живого фото текущего кадра либо null. Ею включается кнопка повтора
        /// и по ней же качается клип.
        /// </summary>
        private string? _currentMotionPoster;

        /// <summary>
        /// Пауза перед запуском клипа живого фото.
        /// </summary>
        /// <remarks>
        /// Полторы секунды нужны не для красоты: перелистывая кадры двойными касаниями,
        /// можно пройти десяток слайдов за пару секунд, и без задержки на каждый из них
        /// заводился бы и тут же сносился декодер. Именно такой поток вендорный декодер
        /// этой рамки и не выдерживает. При обычном показе кадр держится секунд десять,
        /// и задержки не видно.
        /// </remarks>
        private const int MotionStartDelayMilliseconds = 1500;

        /// <summary>
        /// Насколько живые фото затихают после неудачного клипа.
        /// </summary>
        /// <remarks>
        /// Отказ декодера редко бывает единичным: если он споткнулся, следующий клип
        /// обычно спотыкается тоже. Минута тишины даёт ему прийти в себя, а показу —
        /// продолжаться снимками.
        /// </remarks>
        private static readonly TimeSpan MotionFailureBackoff = TimeSpan.FromMinutes(1);

        /// <summary>До этого момента живые фото не оживляем; null — можно.</summary>
        private DateTime? _motionBackoffUntil;

        /// <summary>
        /// Тот же кадр часов, но без двоеточия.
        /// </summary>
        /// <remarks>
        /// Хранится готовым, чтобы мигание ничего не перерисовывало: подмена уже
        /// нарисованного кадра в ImageView мгновенна, а отрисовка кадра 1280x800 на
        /// этой рамке заметна — раз в минуту её позволить можно, раз в секунду нет.
        /// </remarks>
        private Android.Graphics.Bitmap? _nightFrameWithoutColon;

        /// <summary>Кадр текущей минуты с двоеточием — к нему возвращаемся после мигания.</summary>
        private Android.Graphics.Bitmap? _nightFrameWithColon;

        /// <summary>Слой, в котором лежит кадр текущей минуты.</summary>
        private Image? _nightBlinkLayer;

        /// <summary>Сейчас двоеточие погашено.</summary>
        private bool _nightColonHidden;

        /// <summary>Показания датчиков, попавшие в нарисованный кадр часов.</summary>
        private string? _renderedSensorText;

        /// <summary>Обновление ночных часов уже идёт.</summary>
        private bool _nightRefreshRunning;

        /// <summary>Пока рисовали, понадобилось ещё одно обновление.</summary>
        private bool _nightRefreshQueued;

        /// <summary>Сейчас на виду верхний слой слайд-шоу.</summary>
        private bool _slideInTopLayer;

        /// <summary>Кадры, лежащие в слоях: их надо освобождать при замене.</summary>
        private Android.Graphics.Bitmap? _bottomSlideFrame;

        private Android.Graphics.Bitmap? _topSlideFrame;

        /// <summary>
        /// Сколько длится переход между кадрами.
        /// </summary>
        /// <remarks>
        /// Четверть секунды: смена читается как смена, но не как рывок. Столько же
        /// длится перетекание у видео, чтобы кадр и клип вели себя одинаково.
        /// </remarks>
        private const uint SlideCrossfadeMilliseconds = 250;

        /// <summary>
        /// Живое фото крутится по кругу, пока не выключат.
        /// </summary>
        /// <remarks>
        /// На это время слайд-шоу останавливается: смысл повтора в том, чтобы смотреть
        /// именно этот кадр, а не проводить его мимо по расписанию.
        /// </remarks>
        private bool _isMotionLooping;

        /// <summary>
        /// Файл, который проигрывается на текущем слайде.
        /// </summary>
        /// <remarks>
        /// Для видео из папки это сам слайд, а для кадра альбома — догруженный клип,
        /// лежащий рядом с заставкой.
        /// </remarks>
        private string? _currentVideoPath;

        public MainPage()
        {
            InitializeComponent();

            // Shell создаёт страницу через DataTemplate, минуя контейнер, поэтому сервис
            // достаём из провайдера вручную — иначе на каждый показ страницы появляется
            // новый экземпляр со своим HttpClient.
            _photoSource = IPlatformApplication.Current?.Services.GetService<CompositePhotoSource>()
                           ?? new CompositePhotoSource(
                               new SharedAlbumPhotoSource(),
                               new ImmichPhotoSource(),
                               new LocalFolderPhotoSource());

            _clockTimeLabels = BuildOutlinedText(
                ClockTimeHost, fontSize: 68, isBold: true, Colors.White, TimeOutlineWidth);
            _clockDateLabels = BuildOutlinedText(
                ClockDateHost, fontSize: 22, isBold: false, Color.FromArgb("#F0F0F0"), DateOutlineWidth);
            _sensorLabels = BuildOutlinedText(
                SensorHost, fontSize: 24, isBold: true, Color.FromArgb("#BFEFFF"), DateOutlineWidth);
            _captureInfoLabels = BuildOutlinedText(
                CaptureInfoHost, fontSize: 17, isBold: false, Color.FromArgb("#D6D6D6"),
                DateOutlineWidth);
            _photoCounterLabels = BuildOutlinedText(
                PhotoCounterHost, fontSize: 17, isBold: false, Color.FromArgb("#D6D6D6"),
                DateOutlineWidth);

            // Датчиков можно выбрать сколько угодно, поэтому строка с показаниями
            // переносится по словам и не уезжает за край экрана.
            foreach (Label sensorLabel in _sensorLabels)
            {
                sensorLabel.LineBreakMode = LineBreakMode.WordWrap;
                sensorLabel.MaximumWidthRequest = SensorLineMaximumWidth;
            }

            _homeAssistantClient =
                IPlatformApplication.Current?.Services.GetService<HomeAssistantClient>()
                ?? new HomeAssistantClient();

            _slideshowTimer = new System.Timers.Timer { AutoReset = true };
            _slideshowTimer.Elapsed += OnSlideshowTimerElapsed;

            _albumPollTimer = new System.Timers.Timer { AutoReset = true };
            _albumPollTimer.Elapsed += OnAlbumPollTimerElapsed;

            // Интервал задаётся в ApplySettings: он настраивается пользователем.
            _panelHideTimer = new System.Timers.Timer { AutoReset = false };
            _panelHideTimer.Elapsed += OnPanelHideTimerElapsed;

            // Минуты меняются раз в 60 секунд, но опрос раз в 10 секунд гарантирует,
            // что показанное время не отстанет заметно после выхода из сна.
            _clockTimer = new System.Timers.Timer(TimeSpan.FromSeconds(10).TotalMilliseconds)
            {
                AutoReset = true,
            };
            _clockTimer.Elapsed += OnClockTimerElapsed;

            // Своя частота у мигания: часы перерисовываются раз в минуту, а двоеточие
            // должно гаснуть заметно чаще — иначе непонятно, идут часы или замерли.
            _nightBlinkTimer = new System.Timers.Timer(TimeSpan.FromSeconds(1).TotalMilliseconds)
            {
                AutoReset = true,
            };

            _nightBlinkTimer.Elapsed += OnNightBlinkTimerElapsed;

            // Полсекунды достаточно: полоса длиной 520 px на минутном клипе сдвигается
            // примерно на 4 px за такт, дробить мельче незачем.
            _videoProgressTimer = new System.Timers.Timer(500) { AutoReset = true };
            _videoProgressTimer.Elapsed += OnVideoProgressTimerElapsed;

            // Пяти секунд хватает, чтобы прочитать строку вроде «Обновлено: 468 фото».
            _toastHideTimer = new System.Timers.Timer(TimeSpan.FromSeconds(5).TotalMilliseconds)
            {
                AutoReset = false,
            };
            _toastHideTimer.Elapsed += OnToastHideTimerElapsed;

            VideoPlayer.PlaybackFinished += OnVideoPlaybackFinished;
            VideoPlayer.PlaybackFailed += OnVideoPlaybackFailed;

            // Разовая запись в журнал: какие форматы устройство вообще умеет
            // декодировать. Нужна, чтобы решать про HEVC и VP9 по данным.
            CodecProbe.LogVideoDecoders();

            // Раз в минуту — строка о состоянии рамки в файл. Системный журнал
            // перезапуск не переживает, а зависает рамка по ночам.
            FrameHeartbeat.Start(ReadHeartbeatState);
        }

        /// <summary>Состояние рамки для строки журнала.</summary>
        private HeartbeatState ReadHeartbeatState() => new(
            IsNightMode: _isNightModeActive == true,
            PhotoNumber: _currentPhotoIndex + 1,
            PhotoCount: _localPhotoPaths.Count,
            NightFramesRendered: _nightFramesRendered,
            PlayingClip: _currentVideoPath is null
                ? null
                : Path.GetFileName(_currentVideoPath));

        /// <summary>
        /// Собирает текст с обводкой: восемь чёрных копий со смещением и одна основная сверху.
        /// В MAUI у Label нет обводки текста, а одной тени не хватает на светлых снимках.
        /// </summary>
        private static List<Label> BuildOutlinedText(
            Grid host, double fontSize, bool isBold, Color fillColor, double outlineWidth)
        {
            var labels = new List<Label>(OutlineOffsets.Length + 1);

            foreach ((double offsetX, double offsetY) in OutlineOffsets)
            {
                var outlineLabel = new Label
                {
                    FontSize = fontSize,
                    FontAttributes = isBold ? FontAttributes.Bold : FontAttributes.None,
                    TextColor = Colors.Black,
                    Opacity = OutlineOpacity,
                    TranslationX = offsetX * outlineWidth,
                    TranslationY = offsetY * outlineWidth,
                    InputTransparent = true,
                };

                host.Add(outlineLabel);
                labels.Add(outlineLabel);
            }

            var fillLabel = new Label
            {
                FontSize = fontSize,
                FontAttributes = isBold ? FontAttributes.Bold : FontAttributes.None,
                TextColor = fillColor,
                InputTransparent = true,
            };

            host.Add(fillLabel);
            labels.Add(fillLabel);
            return labels;
        }

        /// <summary>
        /// Показывает всплывающее сообщение и гасит его через несколько секунд.
        /// </summary>
        /// <remarks>
        /// Сообщение живёт отдельно от панели управления: результат обновления и ошибки
        /// должны быть видны без касания экрана, но и оставаться на снимке навсегда им
        /// незачем. Ночью сообщения не показываются — весь смысл ночного режима в том,
        /// чтобы экран не светил.
        /// </remarks>
        /// <param name="evenAtNight">
        /// Показать сообщение и ночью. Обычно ночью они молчат — весь смысл ночного
        /// режима в тёмном экране, — но ответ на только что сделанный жест исключение:
        /// иначе непонятно, услышали его или нет.
        /// </param>
        private void ShowToast(string text, bool evenAtNight = false)
        {
            if (_isNightModeActive == true && !evenAtNight)
            {
                return;
            }

            ToastLabel.Text = text;
            ToastPanel.Opacity = 1;
            ToastPanel.IsVisible = true;

            // Каждое новое сообщение продлевает показ: во время загрузки они идут чередой.
            _toastHideTimer.Stop();
            _toastHideTimer.Start();
        }

        private void OnToastHideTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                // Плавное угасание: резкое исчезновение надписи поверх снимка заметнее,
                // чем само сообщение.
                await ToastPanel.FadeToAsync(0, 400).ConfigureAwait(true);
                ToastPanel.IsVisible = false;
            });
        }

        private static void SetOutlinedText(List<Label> labels, string text)
        {
            foreach (Label label in labels)
            {
                label.Text = text;
            }
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();

            // Вернулись — значит, переход завершился и кнопки снова рабочие.
            _isLeavingToAnotherPage = false;

            // Настройки могли измениться на экране настроек, поэтому перечитываем их
            // каждый раз при возврате, а не только в конструкторе.
            ApplySettings();

            // Сначала показываем то, что уже лежит на диске: рамка не должна стоять
            // чёрной, пока идёт сетевой запрос.
            _ = LoadPhotosFromCacheAsync();

            _albumPollTimer.Start();

            // Проверка при запуске — иначе после перезагрузки рамки новые снимки
            // ждали бы до следующего срабатывания таймера. Плюс явный запрос
            // с экрана настроек.
            bool syncRequestedFromSettings = SettingsPage.SyncRequestedOnReturn;
            SettingsPage.SyncRequestedOnReturn = false;
            _ = SynchronizeAlbumAsync(forceDownload: syncRequestedFromSettings);
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();

            // Без остановки таймеры продолжают тикать и держать ссылку на страницу.
            _slideshowTimer.Stop();
            _albumPollTimer.Stop();
            _panelHideTimer.Stop();
            _clockTimer.Stop();

            _videoProgressTimer.Stop();

            // Уходя со страницы, освобождаем проигрыватель: иначе звук продолжится
            // на экране настроек.
            StopVideoPlayback();

            // И возвращаем обычную яркость: окно одно на все страницы, и приглушённый
            // экран сделал бы настройки нечитаемыми.
            ApplyScreenBrightness(nightMode: false);
        }

        /// <summary>
        /// Обновляет строку с показаниями датчиков.
        /// </summary>
        /// <remarks>
        /// При недоступном Home Assistant строка убирается целиком: устаревшее значение
        /// температуры хуже, чем отсутствие значения, а текст ошибки поверх фотографии
        /// не нужен — он есть на экране настроек.
        /// </remarks>
        private async Task RefreshSensorsAsync()
        {
            string[] entityIds = FrameSettings.SensorEntityIds;

            if (!FrameSettings.ShowSensors || entityIds.Length == 0
                || !HomeAssistantClient.IsConfigured)
            {
                await MainThread.InvokeOnMainThreadAsync(() => SensorHost.IsVisible = false)
                    .ConfigureAwait(true);
                return;
            }

            // Предыдущий запрос ещё не завершился: Home Assistant не всегда укладывается
            // в минуту между тиками часов, а второй запрос поверх первого мог бы записать
            // показания не в том порядке — более старые уже после более новых.
            if (_isRefreshingSensors)
            {
                return;
            }

            _isRefreshingSensors = true;
            try
            {
                List<(string EntityId, string Text)> values = await _homeAssistantClient
                    .ReadSensorValuesAsync(entityIds).ConfigureAwait(true);

                // Значок перед значением: все выбранные датчики могут быть термометрами,
                // и без него непонятно, где какая температура.
                // Внутри значка стоит неразрывный пробел: перенос по словам иначе
                // отрывает значение от своего значка и уводит его на другую строку.
                var parts = new List<string>(values.Count);
                foreach ((string entityId, string text) in values)
                {
                    string icon = FrameSettings.GetSensorIcon(entityId);
                    parts.Add(string.IsNullOrEmpty(icon) ? text : icon + NonBreakingSpace + text);
                }

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (parts.Count == 0)
                    {
                        SensorHost.IsVisible = false;
                        return;
                    }

                    _sensorLineText = string.Join(SensorBadgeLayout.BadgeSeparator, parts);

                    // Поверх снимка — те же три значка в строке, что и в ночных часах:
                    // одной строкой показания уезжают за край экрана.
                    SetOutlinedText(
                        _sensorLabels, SensorBadgeLayout.WrapWithLineBreaks(_sensorLineText));
                    SensorHost.IsVisible = _isNightModeActive != true;

                    // Ночью показания входят в сам кадр часов. Обычно они приходят
                    // до отрисовки — её для этого и придерживают, — и тогда
                    // перерисовывать нечего. Но если ответ опоздал, значения в кадре
                    // остаются прошлыми до следующей минуты.
                    if (_isNightModeActive == true && _sensorLineText != _renderedSensorText)
                    {
                        _lastRenderedNightMinute = null;
                        UpdateClock();
                    }
                }).ConfigureAwait(true);
            }
            catch (PhotoSourceException sensorFailure)
            {
                System.Diagnostics.Debug.WriteLine($"Датчики не прочитаны: {sensorFailure.Message}");
                await MainThread.InvokeOnMainThreadAsync(() => SensorHost.IsVisible = false)
                    .ConfigureAwait(true);
            }
            finally
            {
                _isRefreshingSensors = false;
            }
        }

        /// <summary>
        /// Перепроверяет, отвечает ли Home Assistant и есть ли в нём хоть одна камера.
        /// </summary>
        /// <remarks>
        /// Результат идёт в <see cref="_camerasAvailable"/> и виден только в
        /// <see cref="UpdateTapRevealedOverlays"/> при следующем показе панели —
        /// сама проверка с сетью никогда не блокирует нажатие.
        /// </remarks>
        private async Task RefreshCameraAvailabilityAsync()
        {
            // Прямая IP-камера (RTSP) видна и без Home Assistant.
            bool hasDirectRtsp = !string.IsNullOrWhiteSpace(FrameSettings.CameraRtspUrl);

            if (!HomeAssistantClient.IsConfigured)
            {
                _camerasAvailable = hasDirectRtsp;
                return;
            }

            if (_isRefreshingCameraAvailability)
            {
                return;
            }

            _isRefreshingCameraAvailability = true;
            try
            {
                List<string> cameraEntityIds = await _homeAssistantClient
                    .GetCameraEntityIdsAsync().ConfigureAwait(true);
                _camerasAvailable = cameraEntityIds.Count > 0 || hasDirectRtsp;
            }
            catch (PhotoSourceException cameraLookupFailure)
            {
                _camerasAvailable = hasDirectRtsp;
                System.Diagnostics.Debug.WriteLine(
                    $"Камеры Home Assistant не проверены: {cameraLookupFailure.Message}");
            }
            finally
            {
                _isRefreshingCameraAvailability = false;
                UpdateTapRevealedOverlays();
            }
        }

        /// <summary>
        /// Блокирует кнопку «назад» на экране слайд-шоу.
        /// </summary>
        /// <remarks>
        /// MainPage — корень навигации, поэтому системное «назад» закрывает приложение,
        /// и рамка остаётся на лаунчере до ручного запуска. Для устройства, которое
        /// должно просто стоять и показывать фотографии, это недопустимо.
        /// </remarks>
        protected override bool OnBackButtonPressed() => true;

        private void ApplySettings()
        {
            _slideshowTimer.Interval =
                TimeSpan.FromSeconds(FrameSettings.SlideshowIntervalSeconds).TotalMilliseconds;
            _albumPollTimer.Interval =
                TimeSpan.FromHours(FrameSettings.AlbumPollIntervalHours).TotalMilliseconds;
            _panelHideTimer.Interval =
                TimeSpan.FromSeconds(FrameSettings.PanelRevealSeconds).TotalMilliseconds;

            SlideshowImage.Aspect = FrameSettings.FillScreen ? Aspect.AspectFill : Aspect.AspectFit;
            SlideshowImageTop.Aspect = SlideshowImage.Aspect;

            // Панель всегда скрыта при возврате на экран: поверх фотографии не должно
            // быть ничего лишнего, а показывается она касанием.
            ControlPanel.IsVisible = false;
            UpdateTapRevealedOverlays();
            _panelHideTimer.Stop();
            ResetRemoveConfirm();
            ResetDetachConfirm();

            // Адрес или токен могли поменяться на экране настроек — перепроверяем
            // камеры сразу, а не только через минуту по тику часов.
            _ = RefreshCameraAvailabilityAsync();

            ClockOverlay.IsVisible = FrameSettings.ShowClock;
            ClockDateHost.IsVisible = FrameSettings.ShowDate;

            ApplyNightClockAppearance();

            // Расписание могли изменить в настройках — пересчитываем режим с нуля.
            _isNightModeActive = null;

            // Таймер работает всегда, даже если часы поверх снимка отключены:
            // по нему же переключается ночной режим.
            UpdateClock();
            _clockTimer.Start();

            // Датчики перечитываются вместе со сменой минуты на часах — см. UpdateClock.
            _lastSensorMinute = null;
        }

        private void OnClockTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(UpdateClock);
        }

        private void UpdateClock()
        {
            DateTime localNow = DateTime.Now;
            string formattedTime = localNow.ToString("HH:mm", CultureInfo.CurrentCulture);

            // "Воскресенье, 9 августа" — первая буква заглавная, иначе выглядит небрежно.
            string rawDate = localNow.ToString("dddd, d MMMM", CultureInfo.CurrentCulture);
            string formattedDate =
                char.ToUpper(rawDate[0], CultureInfo.CurrentCulture) + rawDate[1..];

            SetOutlinedText(_clockTimeLabels, formattedTime);

            if (FrameSettings.ShowDate)
            {
                SetOutlinedText(_clockDateLabels, formattedDate);
            }

            NightTimeLabel.Text = formattedTime;
            NightDateLabel.Text = formattedDate;

            // Значения датчиков обновляются вместе с показанным временем — раз в минуту,
            // а не по отдельному расписанию: иначе показания выглядят устаревшими.
            bool minuteChanged = _lastSensorMinute != formattedTime;
            if (minuteChanged)
            {
                _lastSensorMinute = formattedTime;
            }

            ApplyNightMode(localNow);

            if (_isNightModeActive == true)
            {
                _ = RefreshNightClockWithSensorsAsync(formattedTime, formattedDate, minuteChanged);
            }
            else if (minuteChanged)
            {
                _ = RefreshSensorsAsync();
            }

            // Кнопка камеры — не про датчики и не про день/ночь, но раз в минуту
            // достаточно, чтобы Home Assistant, ушедший в офлайн, не показывал
            // рабочую на вид кнопку.
            if (minuteChanged)
            {
                _ = RefreshCameraAvailabilityAsync();
            }
        }

        /// <summary>
        /// Включает и выключает ночной режим по расписанию. Вызывается тем же таймером,
        /// что обновляет часы, поэтому переключение происходит в течение 10 секунд
        /// после наступления нужного часа.
        /// </summary>
        private void ApplyNightMode(DateTime localNow)
        {
            bool shouldBeNight = FrameSettings.NightModeEnabled
                                 && FrameSettings.IsNightHour(localNow.Hour);

            if (_isNightModeActive == shouldBeNight)
            {
                return;
            }

            // Состояние могло быть неизвестно: ApplySettings сбрасывает его при каждом
            // возврате на страницу, и это не то же самое, что наступление утра.
            bool wasNight = _isNightModeActive == true;

            _isNightModeActive = shouldBeNight;
            NightOverlay.IsVisible = shouldBeNight;
            ApplyScreenBrightness(shouldBeNight);

            // Кадр часов рисуется заново при каждом входе в ночной режим.
            _lastRenderedNightMinute = null;

            if (shouldBeNight)
            {
                // Смена кадров ночью не нужна, и таймер незачем держать работающим.
                _slideshowTimer.Stop();
                ClockOverlay.IsVisible = false;

                // Ночью показания датчиков, подпись кадра и отметку видео не выводим.
                SensorHost.IsVisible = false;
                AlbumVideoBadge.IsVisible = false;

                // И тем более не проигрываем видео.
                StopVideoPlayback();
                VideoControls.IsVisible = false;
                return;
            }

            // Кадр 1280x800 незачем держать в памяти днём.
            ReleaseNightClockFrame();
            ClockOverlay.IsVisible = FrameSettings.ShowClock;

            if (_localPhotoPaths.Count == 0)
            {
                return;
            }

            // Утром показываем следующий кадр сразу, не дожидаясь интервала. А вот при
            // возврате со страницы настроек или сведений о файле кадр менять нельзя:
            // иначе слайд «сам» перещёлкивается при закрытии экрана.
            if (wasNight)
            {
                ShowNextPhoto();
            }

            _slideshowTimer.Start();
        }

        /// <summary>
        /// Красит резервные метки ночных часов так же, как рисованный кадр.
        /// </summary>
        /// <remarks>
        /// Метки видны, только если отрисовка кадра не удалась, но выглядеть при этом
        /// они должны так же: иначе сбой заодно менял бы цвет и яркость часов.
        /// </remarks>
        private void ApplyNightClockAppearance()
        {
            var clockColor = Color.FromArgb(FrameSettings.NightClockColorHex);

            NightTimeLabel.TextColor = clockColor;
            NightDateLabel.TextColor = clockColor;

            // Резервные метки гаснут вместе с рисунком, только вдвое сильнее: шахматной
            // маски, которая делит яркость кадра пополам, у них нет.
            double intensity = Math.Clamp(FrameSettings.NightClockIntensityPercent / 100d, 0.01, 1.0);
            NightFallbackClock.Opacity = intensity / 2;
        }

        /// <summary>
        /// Приглушает подсветку экрана на время ночных часов.
        /// </summary>
        /// <remarks>
        /// Яркость задаётся окну, а не системе: системная требует особого разрешения
        /// WRITE_SETTINGS, спорит с автоматическим режимом и осталась бы изменённой
        /// после удаления приложения. Значение окна перекрывает и автоматику, и действует
        /// только пока окно на экране.
        ///
        /// -1 (BrightnessOverrideNone) возвращает экран к системной яркости.
        /// </remarks>
        private static void ApplyScreenBrightness(bool nightMode)
        {
            int brightnessPercent = nightMode
                ? FrameSettings.NightScreenBrightnessPercent
                : FrameSettings.DayScreenBrightnessPercent;

            // 0 — пользователь не захотел, чтобы рамка трогала подсветку.
            float brightness = brightnessPercent > 0
                ? Math.Clamp(brightnessPercent, 1, 100) / 100f
                : -1f;

            try
            {
                Android.Views.Window? window = Platform.CurrentActivity?.Window;
                if (window?.Attributes is not Android.Views.WindowManagerLayoutParams attributes)
                {
                    return;
                }

                attributes.ScreenBrightness = brightness;
                window.Attributes = attributes;
            }
            catch (Java.Lang.Throwable brightnessFailure)
            {
                // Рамка просто останется на системной яркости.
                System.Diagnostics.Debug.WriteLine(
                    $"Яркость экрана не изменена: {brightnessFailure.Message}");
            }
        }

        /// <summary>
        /// Шаг подстройки взмахом. Десять шагов на всю шкалу: меньше — и до нужного
        /// значения нужно махать без конца, больше — и промахиваешься мимо него.
        /// </summary>
        private const int SwipeAdjustStep = 10;

        /// <summary>Взмах вверх — ярче (ночью — заметнее цифры).</summary>
        private void OnSwipeUp(object? sender, SwipedEventArgs e) => AdjustBySwipe(SwipeAdjustStep);

        /// <summary>Взмах вниз — темнее.</summary>
        private void OnSwipeDown(object? sender, SwipedEventArgs e) => AdjustBySwipe(-SwipeAdjustStep);

        /// <summary>
        /// Подстраивает яркость взмахом по экрану.
        /// </summary>
        /// <remarks>
        /// Днём это подсветка, ночью — насыщенность цифр: подсветка ночью упирается
        /// в свой предел, и дальше гасить можно только краской. То есть взмах всегда
        /// делает ровно то, чего от него ждут, — «ярче» и «темнее» для того, что
        /// сейчас на экране.
        ///
        /// Настройки при этом сохраняются: подобранное с дивана значение должно
        /// пережить и ночь, и перезапуск.
        /// </remarks>
        private void AdjustBySwipe(int stepPercent)
        {
            if (_isNightModeActive == true)
            {
                // У насыщенности нет нуля: ноль означал бы невидимые часы.
                int intensity = Math.Clamp(
                    FrameSettings.NightClockIntensityPercent + stepPercent, 1, 100);

                FrameSettings.NightClockIntensityPercent = intensity;

                // Перерисовываем сейчас же, а не ждём очередного тика часов: тик идёт
                // раз в десять секунд, и взмах выглядит не сработавшим.
                _lastRenderedNightMinute = null;
                _ = RedrawNightClockNowAsync();

                // Ночью сообщения обычно молчат, но это — ответ на только что сделанный
                // взмах: без него непонятно, изменилось ли что-нибудь и насколько.
                ShowToast($"Насыщенность цифр: {intensity} %", evenAtNight: true);
                return;
            }

            int brightness = FrameSettings.DayScreenBrightnessPercent;

            // 0 значит «как в системе», и от него шагать некуда: начинаем с середины,
            // чтобы первый же взмах дал видимый результат в нужную сторону.
            if (brightness == 0)
            {
                brightness = 50 + stepPercent;
            }
            else
            {
                brightness += stepPercent;
            }

            brightness = Math.Clamp(brightness, 1, 100);
            FrameSettings.DayScreenBrightnessPercent = brightness;
            ApplyScreenBrightness(nightMode: false);
            ShowToast($"Яркость экрана: {brightness} %");
        }

        /// <summary>
        /// Перерисовывает ночные часы, когда изменилась минута, и сдвигает шахматную маску.
        /// </summary>
        private async Task RefreshNightClockAsync(string formattedTime, string formattedDate)
        {
            // Отрисовываем только при смене минуты: кодирование PNG на весь экран
            // недёшево, а каждые 10 секунд картинка одна и та же.
            if (_lastRenderedNightMinute == formattedTime)
            {
                return;
            }

            // Два обновления сразу накладываться не должны: второе заняло бы слой,
            // который первое затем отцепляет, и на один кадр оба слоя остались бы
            // пустыми, то есть экран почернел бы. Опоздавшее обновление ждёт очереди.
            if (_nightRefreshRunning)
            {
                _nightRefreshQueued = true;
                return;
            }

            _nightRefreshRunning = true;
            _lastRenderedNightMinute = formattedTime;

            // Инверсия маски на каждом обновлении: светятся уже другие пиксели.
            _nightMaskPhaseShifted = !_nightMaskPhaseShifted;
            bool phaseShifted = _nightMaskPhaseShifted;

            DisplayInfo displayInfo = DeviceDisplay.Current.MainDisplayInfo;
            int widthPixels = (int)displayInfo.Width;
            int heightPixels = (int)displayInfo.Height;

            if (widthPixels <= 0 || heightPixels <= 0)
            {
                return;
            }

            // Мигание останавливаем ещё до отрисовки, а не после подмены кадра:
            // ShowNightClockFrameAsync переставляет _nightBlinkLayer на новый слой ещё
            // до его показа (см. там), и работающий таймер успел бы подсунуть на свежий,
            // ещё проявляющийся слой кадр прошлой минуты, пока _nightFrameWithColon и
            // _nightFrameWithoutColon его не обновили.
            _nightBlinkTimer.Stop();

            try
            {
                string colorHex = FrameSettings.NightClockColorHex;

                // Датчики показываются и ночью, но только если их вообще просили показывать.
                string? sensorText = FrameSettings.ShowSensors ? _sensorLineText : null;

                // Что нарисовано, то и запоминаем: по этому опоздавший ответ датчиков
                // и понимает, нужна ли ещё одна отрисовка.
                _renderedSensorText = _sensorLineText;

                int intensityPercent = FrameSettings.NightClockIntensityPercent;

                // Рисуем в кадр незанятого слоя: он уже отцеплен от своего ImageView,
                // и переиспользовать его безопасно — новый буфер на каждый кадр
                // не выделяется.
                Android.Graphics.Bitmap? reusableFrame =
                    _nightFrameInFrontLayer ? _backNightFrame : _frontNightFrame;

                Android.Graphics.Bitmap renderedFrame = await Task.Run(
                    () => NightClockRenderer.RenderBitmap(
                        widthPixels, heightPixels, formattedTime, formattedDate, sensorText,
                        phaseShifted, colorHex, intensityPercent, reusableFrame))
                    .ConfigureAwait(true);

                _nightFramesRendered++;

                await ShowNightClockFrameAsync(renderedFrame).ConfigureAwait(true);

                // Второй кадр этой же минуты, без двоеточия: им и мигаем. Ссылка на прежний
                // кадр убирается здесь же: рисуем-то мы поверх него, а первым делом заливаем
                // его чёрным.
                Android.Graphics.Bitmap? colonOffCanvas = _nightFrameWithoutColon;
                _nightFrameWithoutColon = null;
                _nightFrameWithColon = renderedFrame;
                _nightColonHidden = false;

                Android.Graphics.Bitmap frameWithoutColon = await Task.Run(
                    () => NightClockRenderer.RenderBitmap(
                        widthPixels, heightPixels, formattedTime, formattedDate, sensorText,
                        phaseShifted, colorHex, intensityPercent, colonOffCanvas,
                        showColon: false))
                    .ConfigureAwait(true);

                _nightFrameWithoutColon = frameWithoutColon;
                _nightBlinkTimer.Start();
            }
            catch (Exception renderFailure)
            {
                // Не удалось — под изображением остаются обычные метки с часами.
                // FrameLog, а не Debug.WriteLine: в Release тот вырезается, и причина
                // повторной отрисовки каждые десять секунд остаётся невидимой.
                FrameLog.Warn($"Ночные часы не отрисованы: {renderFailure}");
                NightFallbackClock.IsVisible = true;
                _lastRenderedNightMinute = null;
            }
            finally
            {
                _nightRefreshRunning = false;
            }

            if (!_nightRefreshQueued)
            {
                return;
            }

            // Пока рисовали, что-то успело измениться: пришли показания датчиков либо
            // взмахом поменяли насыщенность. Обновляемся ещё раз, теперь по порядку.
            _nightRefreshQueued = false;
            _lastRenderedNightMinute = null;

            if (_isNightModeActive == true)
            {
                await RedrawNightClockNowAsync().ConfigureAwait(true);
            }
        }

        /// <summary>
        /// Показывает готовый кадр ночных часов, подменяя картинку без пропадания.
        /// </summary>
        /// <remarks>
        /// Кадр отдаётся платформенному ImageView напрямую: свойство Source в MAUI
        /// загружается асинхронно и на это время гасит картинку, из-за чего на смене
        /// минуты экран заметно мигает чёрным.
        ///
        /// Слои чередуются. Новый кадр всегда попадает в свободный слой, и лишь после
        /// этого гасится тот, что показывал прошлую минуту: пока новый кадр не на месте,
        /// на экране остаётся прежний. С одним слоем между очисткой ImageView и
        /// отрисовкой нового содержимого был бы чёрный кадр.
        /// </remarks>
        private async Task ShowNightClockFrameAsync(Android.Graphics.Bitmap renderedFrame)
        {
            bool intoFrontLayer = !_nightFrameInFrontLayer;

            Image targetLayer = intoFrontLayer ? NightClockFrontImage : NightClockBackImage;
            Image previousLayer = intoFrontLayer ? NightClockBackImage : NightClockFrontImage;

            if (!TrySetLayerFrame(targetLayer, renderedFrame))
            {
                // Обработчик ещё не создан — кадр покажется на следующей минуте,
                // а пока видны резервные метки.
                if (!ReferenceEquals(renderedFrame, _frontNightFrame)
                    && !ReferenceEquals(renderedFrame, _backNightFrame))
                {
                    ReleaseFrame(renderedFrame);
                }

                // Кадр показать не удалось — метки снова нужны.
                NightFallbackClock.IsVisible = true;
                _lastRenderedNightMinute = null;
                return;
            }

            if (intoFrontLayer)
            {
                ReleaseFrame(_frontNightFrame, renderedFrame);
                _frontNightFrame = renderedFrame;

                // Рисуем поверх кадра противоположного слоя, поэтому оба поля могут
                // указывать на один Bitmap: освобождённый дважды, он роняет приложение
                // ObjectDisposedException.
                if (ReferenceEquals(_backNightFrame, renderedFrame))
                {
                    _backNightFrame = null;
                }
            }
            else
            {
                ReleaseFrame(_backNightFrame, renderedFrame);
                _backNightFrame = renderedFrame;

                if (ReferenceEquals(_frontNightFrame, renderedFrame))
                {
                    _frontNightFrame = null;
                }
            }

            _nightFrameInFrontLayer = intoFrontLayer;

            // Мигать двоеточием будем в этом слое: он сейчас на виду.
            _nightBlinkLayer = targetLayer;

            // Резервные метки убираем: кадр с маской уже на экране. Под кадрами любой
            // их зазор — хоть на один кадр отрисовки — показал бы другие часы: мелкие,
            // сплошные и без датчиков.
            NightFallbackClock.IsVisible = false;

            // Перетекание вместо подмены: прозрачность меняется только у верхнего слоя,
            // нижний всё время под ним. Пока новый кадр добирается до экрана, уходящий
            // остаётся нарисованным, поэтому блика между минутами нет.
            await NightClockFrontImage.FadeToAsync(
                intoFrontLayer ? 1 : 0, SlideCrossfadeMilliseconds).ConfigureAwait(true);

            // Кадр прежнего слоя нужен через минуту как холст, поэтому слой только
            // отцепляем: рисовать в прицепленный кадр нельзя, он мелькнёт на экране.
            DetachLayer(previousLayer);
        }

        private static bool TrySetLayerFrame(Image layer, Android.Graphics.Bitmap frame)
        {
            if (layer.Handler?.PlatformView is not Android.Widget.ImageView imageView)
            {
                return false;
            }

            imageView.SetImageBitmap(frame);
            return true;
        }

        /// <summary>Отцепляет кадр от слоя, не трогая сам кадр.</summary>
        private static void DetachLayer(Image layer)
        {
            if (layer.Handler?.PlatformView is Android.Widget.ImageView imageView)
            {
                imageView.SetImageBitmap(null);
            }
        }

        /// <summary>
        /// Освобождает кадр, если он больше не нужен.
        /// </summary>
        /// <remarks>
        /// Recycle, а не только Dispose: пиксели живут в native-куче, и без него они
        /// ждут сборщика мусора — на устройстве с гигабайтом памяти это заметная утечка.
        /// </remarks>
        private static void ReleaseFrame(
            Android.Graphics.Bitmap? frame, Android.Graphics.Bitmap? keepIfSame = null)
        {
            if (frame is null || ReferenceEquals(frame, keepIfSame))
            {
                return;
            }

            try
            {
                // IsRecycled тоже обращается к объекту Java, поэтому и он под защитой.
                if (!frame.IsRecycled)
                {
                    frame.Recycle();
                }

                frame.Dispose();
            }
            catch (Exception releaseFailure) when (
                releaseFailure is ObjectDisposedException or Java.Lang.Throwable)
            {
                // Кадр уже освобождён — это не повод падать: показ ночных часов
                // важнее аккуратности учёта, а память вернёт сборщик.
                FrameLog.Warn($"Кадр часов уже освобождён: {releaseFailure.Message}");
            }
        }

        /// <summary>
        /// Сколько ждать показания датчиков, прежде чем рисовать часы без них.
        /// </summary>
        /// <remarks>
        /// Home Assistant стоит в той же сети и отвечает быстро, но если он выключен,
        /// часы не должны из-за этого стоять на прошлой минуте.
        /// </remarks>
        private const int SensorWaitBeforeDrawMilliseconds = 2000;

        /// <summary>
        /// Обновляет ночные часы, сперва дождавшись показаний датчиков.
        /// </summary>
        /// <remarks>
        /// Показания входят в сам кадр часов, поэтому порядок важен: без ожидания кадр
        /// рисуется сразу, а пришедшие следом датчики заставляют рисовать его заново —
        /// за минуту выходит два перетекания вместо одного.
        /// </remarks>
        private async Task RefreshNightClockWithSensorsAsync(
            string formattedTime, string formattedDate, bool refreshSensors)
        {
            if (refreshSensors && FrameSettings.ShowSensors)
            {
                await Task.WhenAny(
                        RefreshSensorsAsync(),
                        Task.Delay(SensorWaitBeforeDrawMilliseconds))
                    .ConfigureAwait(true);
            }

            await RefreshNightClockAsync(formattedTime, formattedDate).ConfigureAwait(true);
        }

        /// <summary>
        /// Перерисовывает ночные часы немедленно, тем же временем, что и сейчас.
        /// </summary>
        private Task RedrawNightClockNowAsync()
        {
            DateTime localNow = DateTime.Now;

            string rawDate = localNow.ToString("dddd, d MMMM", CultureInfo.CurrentCulture);
            string formattedDate =
                char.ToUpper(rawDate[0], CultureInfo.CurrentCulture) + rawDate[1..];

            return RefreshNightClockAsync(
                localNow.ToString("HH:mm", CultureInfo.CurrentCulture), formattedDate);
        }

        /// <summary>
        /// Гасит и зажигает двоеточие, подменяя готовый кадр.
        /// </summary>
        /// <remarks>
        /// Ничего не рисуется: оба кадра минуты уже готовы, и подмена картинки
        /// в ImageView обходится даром. Работает только ночью и только когда кадры
        /// на месте — иначе таймер просто ничего не делает.
        /// </remarks>
        private void OnNightBlinkTimerElapsed(object? sender, ElapsedEventArgs e) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_isNightModeActive != true || _nightBlinkLayer is null)
                {
                    return;
                }

                Android.Graphics.Bitmap? nextFrame = _nightColonHidden
                    ? _nightFrameWithColon
                    : _nightFrameWithoutColon;

                if (nextFrame is null || nextFrame.IsRecycled)
                {
                    return;
                }

                if (TrySetLayerFrame(_nightBlinkLayer, nextFrame))
                {
                    _nightColonHidden = !_nightColonHidden;
                }
            });

        /// <summary>Убирает ночные часы с экрана и освобождает оба кадра.</summary>
        private void ReleaseNightClockFrame()
        {
            NightClockFrontImage.Opacity = 0;
            NightFallbackClock.IsVisible = true;
            _nightBlinkTimer.Stop();
            _nightBlinkLayer = null;
            _nightColonHidden = false;

            ReleaseFrame(_nightFrameWithoutColon);
            _nightFrameWithoutColon = null;

            // Кадр с двоеточием — это один из кадров слоёв, и освободят его ниже.
            _nightFrameWithColon = null;

            DetachLayer(NightClockFrontImage);
            DetachLayer(NightClockBackImage);

            ReleaseFrame(_frontNightFrame);

            // keepIfSame — та же страховка от одного кадра в двух полях: рисуем мы
            // поверх кадра свободного слоя, и они могут совпасть.
            ReleaseFrame(_backNightFrame, keepIfSame: _frontNightFrame);

            _frontNightFrame = null;
            _backNightFrame = null;
            _nightFrameInFrontLayer = false;
        }

        /// <summary>Переставляет часы в следующий угол — по одному шагу на кадр.</summary>
        private void MoveClockToNextPosition()
        {
            ApplyClockPosition((_clockPositionIndex + 1) % ClockPositions.Length);
        }

        private void ApplyClockPosition(int positionIndex)
        {
            _clockPositionIndex = positionIndex;
            (LayoutOptions horizontal, LayoutOptions vertical, Thickness margin) =
                ClockPositions[_clockPositionIndex];

            ClockOverlay.HorizontalOptions = horizontal;
            ClockOverlay.VerticalOptions = vertical;
            ClockOverlay.Margin = margin;

            ApplyClockContentAlignment(horizontal.Alignment == LayoutAlignment.End);
        }

        /// <summary>
        /// Разворачивает содержимое блока часов к тому краю, у которого он стоит.
        /// </summary>
        /// <remarks>
        /// Ширину блока задаёт самая длинная строка — время, поэтому в правых углах дата
        /// и показания датчиков прижимаются к её левому краю, то есть висят с отступом
        /// от края экрана. Небольшие отступы строк тоже зеркалятся, иначе в правых углах
        /// они отодвигают текст не от края, а от времени.
        /// </remarks>
        private void ApplyClockContentAlignment(bool alignToRight)
        {
            TextAlignment textAlignment = alignToRight ? TextAlignment.End : TextAlignment.Start;
            LayoutOptions hostAlignment = alignToRight ? LayoutOptions.End : LayoutOptions.Start;

            foreach (List<Label> labels in
                     new[] { _clockTimeLabels, _clockDateLabels, _sensorLabels })
            {
                foreach (Label label in labels)
                {
                    label.HorizontalTextAlignment = textAlignment;
                }
            }

            ClockTimeHost.HorizontalOptions = hostAlignment;
            ClockDateHost.HorizontalOptions = hostAlignment;
            SensorHost.HorizontalOptions = hostAlignment;

            ClockDateHost.Margin = alignToRight
                ? new Thickness(0, -8, 4, 0)
                : new Thickness(4, -8, 0, 0);

            SensorHost.Margin = alignToRight
                ? new Thickness(0, 4, 4, 0)
                : new Thickness(4, 4, 0, 0);
        }

        /// <summary>
        /// Подбирает для часов самый ровный угол снимка, избегая лиц.
        /// </summary>
        /// <remarks>
        /// Анализ идёт в фоне и применяется, только если кадр за это время не сменился:
        /// иначе часы прыгнули бы по данным уже неактуальной фотографии.
        /// </remarks>
        private async Task PlaceClockOverPhotoAsync(string photoPath, int photoGeneration)
        {
            int? bestCorner = await Task.Run(
                () => ClockPlacementAnalyzer.ChooseBestCorner(photoPath)).ConfigureAwait(true);

            if (bestCorner is null || photoGeneration != _photoGeneration)
            {
                return;
            }

            ApplyClockPosition(bestCorner.Value);
        }

        /// <summary>
        /// Касание переключает панель управления: первое показывает, второе убирает.
        /// Если не трогать экран, панель уходит сама через настроенное время.
        /// </summary>
        private void OnScreenTapped(object? sender, TappedEventArgs e)
        {
            _panelHideTimer.Stop();

            if (ControlPanel.IsVisible)
            {
                ControlPanel.IsVisible = false;
                UpdateTapRevealedOverlays();

                // Панель ушла вместе с вопросом — считаем это отказом и возвращаем показ.
                // "|", а не "||": оба вопроса не могут висеть разом, но сбросить нужно
                // оба поля, а не только тот, что проверился первым.
                if (ResetRemoveConfirm() | ResetDetachConfirm())
                {
                    _slideshowTimer.Start();
                }

                return;
            }

            ControlPanel.IsVisible = true;
            UpdateTapRevealedOverlays();
            _panelHideTimer.Start();
        }

        private void OnPanelHideTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                ControlPanel.IsVisible = false;
                UpdateTapRevealedOverlays();
            });
        }

        /// <summary>
        /// Двойное касание переключает кадр. Сторона определяется по точке касания,
        /// а не двумя отдельными зонами: одна зона надёжнее в hit-testing и не спорит
        /// с распознавателем одиночного касания.
        /// </summary>
        private void OnDoubleTapped(object? sender, TappedEventArgs e)
        {
            Point? tapPosition = e.GetPosition(TouchOverlay);

            // Если платформа не сообщила координату, трактуем как «вперёд».
            bool isLeftHalf = tapPosition is { } position
                              && TouchOverlay.Width > 0
                              && position.X < TouchOverlay.Width / 2;

            StepPhoto(isLeftHalf ? -1 : +1);
        }

        /// <summary>
        /// Ручное переключение кадра. Таймер перезапускается, иначе следующий кадр
        /// сменился бы сразу после двойного касания.
        /// </summary>
        private void StepPhoto(int direction)
        {
            if (_localPhotoPaths.Count == 0)
            {
                return;
            }

            ShowPhotoAt(_currentPhotoIndex + direction);

            _slideshowTimer.Stop();
            _slideshowTimer.Start();
        }

        /// <summary>
        /// Открывает настройки — по одному экрану на нажатие.
        /// </summary>
        /// <remarks>
        /// Флаг нужен потому, что переход не мгновенен: на этой рамке экран настроек
        /// открывается заметно, и за это время по кнопке успевают нажать ещё раз-другой.
        /// Без флага каждое нажатие добавляет свой экран в стек, и «Назад» приходится
        /// нажимать столько же раз. Снимается флаг в OnAppearing, когда страница снова
        /// на виду.
        /// </remarks>
        private bool _isLeavingToAnotherPage;

        private async void OnSettingsClicked(object? sender, EventArgs e)
        {
            if (_isLeavingToAnotherPage)
            {
                return;
            }

            _isLeavingToAnotherPage = true;
            await Shell.Current.GoToAsync(nameof(SettingsPage));
        }

        private async void OnCameraViewClicked(object? sender, EventArgs e)
        {
            if (_isLeavingToAnotherPage)
            {
                return;
            }

            _isLeavingToAnotherPage = true;
            await Shell.Current.GoToAsync(nameof(CameraViewPage));
        }



        private async void OnFileInfoClicked(object? sender, EventArgs e)
        {
            if (_localPhotoPaths.Count == 0 || _isLeavingToAnotherPage)
            {
                return;
            }

            _isLeavingToAnotherPage = true;

            string mediaPath = _localPhotoPaths[_currentPhotoIndex];

            // У кадра альбома слайд — это заставка, а сведения нужны о самом клипе:
            // его размер, разрешение и длительность. Пока клип не скачан, показываем
            // то, что есть, — заставку.
            FileInfoPage.MediaPath = _currentVideoPath
                                     ?? SlideClips.FindReady(mediaPath)
                                     ?? mediaPath;

            await Shell.Current.GoToAsync(nameof(FileInfoPage));
        }

        /// <summary>
        /// Убирает показанный кадр из слайд-шоу, перенося файл в корзину.
        /// </summary>
        /// <remarks>
        /// Кадр запоминается до вопроса, а таймеры на это время останавливаются: иначе
        /// слайд-шоу успело бы шагнуть дальше и в корзину уехал бы не тот файл.
        /// </remarks>
        private async void OnRemoveClicked(object? sender, EventArgs e)
        {
            if (_localPhotoPaths.Count == 0 || _pendingRemovalPath is not null)
            {
                return;
            }

            // Пока висит вопрос, кадр не должен смениться, иначе в корзину уехал бы
            // не тот файл. Панель тоже не убираем сама собой.
            _slideshowTimer.Stop();
            _panelHideTimer.Stop();

            _pendingRemovalPath = _localPhotoPaths[_currentPhotoIndex];

            RemoveConfirmPanel.IsVisible = true;
            await AnimateRemoveConfirmAsync(shown: true).ConfigureAwait(true);
        }

        private async void OnCancelRemoveClicked(object? sender, EventArgs e)
        {
            await HideRemoveConfirmAsync().ConfigureAwait(true);

            _panelHideTimer.Start();
            _slideshowTimer.Start();
        }

        private async void OnConfirmRemoveClicked(object? sender, EventArgs e)
        {
            string? mediaPath = _pendingRemovalPath;
            await HideRemoveConfirmAsync().ConfigureAwait(true);

            if (mediaPath is null)
            {
                return;
            }

            FrameLog.Info(
                $"Убираем кадр: повтор {_isMotionLooping}, видео {_isCurrentSlideVideo}, "
                + $"кадров {_localPhotoPaths.Count}");

            // Проигрыватель держит файл открытым, и переименование под ним не пройдёт.
            StopVideoPlayback();

            // У Immich хозяин снимка — сервер, и убирать кадр надо там; на рамке лежит
            // лишь превью. Ссылку на общий альбом Google так не тронешь: там правит
            // только владелец альбома, поэтому для него всё остаётся по-прежнему.
            ImmichSlideInfo? immichSlide = ImmichSidecar.Find(mediaPath);
            if (immichSlide is not null)
            {
                string immichOutcome = await RemoveImmichPhotoAsync(
                    mediaPath, immichSlide.AssetId).ConfigureAwait(true);

                RemoveCurrentPhotoFromShow(immichOutcome);
                return;
            }

            try
            {
                await MediaTrash.MoveToTrashAsync(mediaPath).ConfigureAwait(true);
            }
            catch (PhotoSourceException trashFailure)
            {
                ShowToast(trashFailure.Message);
                _panelHideTimer.Start();
                _slideshowTimer.Start();
                return;
            }

            // Ссылка в альбоме осталась, поэтому кадр надо ещё и внести в список убранных,
            // иначе следующая синхронизация скачает его заново.
            bool fromSharedAlbum = MediaTrash.IsAlbumPhoto(mediaPath);
            if (fromSharedAlbum)
            {
                FrameSettings.AddTrashedAlbumFileName(Path.GetFileName(mediaPath));
            }

            // Сам альбом Google тронуть нельзя — правит там только его владелец, — а вот
            // снимок из папки на устройстве действительно переехал. Разница видимая,
            // и сообщение должно её называть.
            RemoveCurrentPhotoFromShow(fromSharedAlbum
                ? "Убрано из показа. В самом альбоме Google снимок остался"
                : "Файл перенесён в корзину рамки");
        }

        /// <summary>
        /// Убирает кадр Immich: в корзину сервера, а из кэша рамки — совсем.
        /// </summary>
        /// <remarks>
        /// Копию в корзине рамки не держим и в список убранных кадр не вносим: снимок
        /// лежит в корзине Immich, откуда его и восстанавливают, а восстановленный
        /// объект сервер снова отдаёт в выдаче — и кадр возвращается на рамку сам собой,
        /// при очередной синхронизации. Список убранных этому только мешал бы.
        ///
        /// Список нужен лишь когда сервер удалить отказался: тогда кадр остаётся на
        /// сервере, и без записи он приезжал бы обратно после каждой проверки.
        /// </remarks>
        /// <returns>Что сказать пользователю: сообщение показывает вызывающий код.</returns>
        private async Task<string> RemoveImmichPhotoAsync(string mediaPath, string assetId)
        {
            ImmichPhotoSource immichSource =
                IPlatformApplication.Current?.Services.GetService<ImmichPhotoSource>()
                ?? new ImmichPhotoSource();

            string? failureMessage = await immichSource
                .TryTrashOnServerAsync(assetId)
                .ConfigureAwait(true);

            if (failureMessage is null)
            {
                ImmichPhotoSource.RemoveFromCache(mediaPath);
                return "Убрано в корзину Immich — вернётся, если восстановить там";
            }

            // Сервер отказал: ведём себя как с кадром альбома — копия в корзине рамки
            // и запись в списке убранных, чтобы кадр не приехал заново.
            try
            {
                await MediaTrash.MoveToTrashAsync(mediaPath).ConfigureAwait(true);
            }
            catch (PhotoSourceException trashFailure)
            {
                FrameLog.Warn($"Кадр Immich не убран в корзину рамки: {trashFailure.Message}");
            }

            FrameSettings.AddTrashedAlbumFileName(Path.GetFileName(mediaPath));

            // Чаще всего отказ означает ровно одно: снимок чужой. В библиотеку он
            // попал из общего альбома, читать его можно, а удалять нет, и сервер
            // отвечает «Not found or no asset.delete access». Так и скажем: код 400
            // на экране рамки не объясняет ничего, а поведение при этом верное —
            // с показа кадр убран и обратно не приедет.
            bool notOurs = failureMessage.Contains(
                "asset.delete access", StringComparison.Ordinal);

            return notOurs
                ? "Убрано с рамки. Снимок чужой (общий альбом) — на сервере остался"
                : $"С рамки убрано, но в Immich осталось: {failureMessage}";
        }

        /// <summary>
        /// Выдвигает подтверждение вправо от кнопки и убирает его обратно.
        /// </summary>
        /// <remarks>
        /// Смещение отрицательное в скрытом виде, поэтому панель выезжает из-под самой
        /// кнопки, а не появляется рядом с ней.
        /// </remarks>
        private Task AnimateRemoveConfirmAsync(bool shown)
        {
            return Task.WhenAll(
                RemoveConfirmPanel.TranslateToAsync(shown ? 0 : -44, 0, 180),
                RemoveConfirmPanel.FadeToAsync(shown ? 1 : 0, 180));
        }

        private async Task HideRemoveConfirmAsync()
        {
            _pendingRemovalPath = null;

            await AnimateRemoveConfirmAsync(shown: false).ConfigureAwait(true);
            RemoveConfirmPanel.IsVisible = false;
        }

        /// <summary>
        /// Убирает подтверждение без анимации. Возвращает true, если вопрос действительно
        /// висел, — тогда вызывающий код возобновляет показ.
        /// </summary>
        /// <remarks>
        /// Нужно, когда панель управления исчезает целиком: анимировать то, что уже
        /// скрыто, незачем, а состояние сбросить обязательно.
        /// </remarks>
        private bool ResetRemoveConfirm()
        {
            if (_pendingRemovalPath is null)
            {
                return false;
            }

            _pendingRemovalPath = null;

            RemoveConfirmPanel.IsVisible = false;
            RemoveConfirmPanel.Opacity = 0;
            RemoveConfirmPanel.TranslationX = -44;
            return true;
        }

        /// <summary>
        /// Выбрасывает показанный кадр из списка и переходит к следующему.
        /// </summary>
        /// <remarks>
        /// Манифесты источников не переписываются: и альбом, и папки при чтении пропускают
        /// файлы, которых нет на диске, поэтому убранный кадр не вернётся и после перезапуска.
        /// </remarks>
        /// <param name="outcome">
        /// Что случилось со снимком. Своё сообщение на каждый источник: у Immich кадр
        /// уходит в корзину сервера, у альбома Google остаётся на месте, а файл из папки
        /// действительно переезжает.
        /// </param>
        private void RemoveCurrentPhotoFromShow(string outcome)
        {
            int removedIndex = _currentPhotoIndex;
            _localPhotoPaths.RemoveAt(removedIndex);

            // Порядок изменился — сохранённый список без этого стал бы неприменим целиком.
            SlideshowStateStore.SaveOrder(_localPhotoPaths);

            ShowToast($"{outcome}. Осталось {_localPhotoPaths.Count} фото");
            FrameLog.Info($"Кадр убран, осталось {_localPhotoPaths.Count}; показ продолжается");

            if (_localPhotoPaths.Count == 0)
            {
                ClearSlideLayers();
                _hasCaptureInfo = false;
                _isCurrentSlideVideo = false;
                _currentPhotoIndex = -1;
                UpdateTapRevealedOverlays();
                return;
            }

            _panelHideTimer.Start();

            // Прежний индекс теперь указывает на следующий кадр; ShowPhotoAt приведёт
            // его к границам списка сам.
            ShowPhotoAt(removedIndex);
            _slideshowTimer.Start();
        }

        private async void OnSyncClicked(object? sender, EventArgs e)
        {
            await SynchronizeAlbumAsync(forceDownload: true);
        }

        /// <summary>
        /// Сколько выждать, прежде чем начинать просроченную проверку источников.
        /// </summary>
        /// <remarks>
        /// Пока рамка спит, таймер проверки не идёт, и при пробуждении он срабатывает
        /// сразу: опрос сервера, разбор тысяч записей, перезапись списков и загрузка
        /// клипов сваливаются в ту самую минуту, когда человек подошёл к рамке. Минута
        /// отсрочки отдаёт первые кадры показу, а работу делает следом.
        /// </remarks>
        private static readonly TimeSpan PollDelayAfterWake = TimeSpan.FromMinutes(1);

        private void OnAlbumPollTimerElapsed(object? sender, ElapsedEventArgs e) =>
            _ = PollSourcesAsync();

        private async Task PollSourcesAsync()
        {
            // Просрочена ли проверка, видно по времени прошлой: у таймера этого
            // не спросить, а спящая рамка его попросту не двигает. Полтора интервала —
            // чтобы обычное срабатывание по расписанию отсрочку не задевало.
            // LastSyncUtc пуст до самой первой проверки — тогда и просрочки нет.
            bool overdue = FrameSettings.LastSyncUtc is { } lastSync
                           && DateTime.UtcNow - lastSync
                              > FrameSettings.AlbumPollIntervalHours * TimeSpan.FromHours(1.5);

            if (overdue)
            {
                FrameHeartbeat.Write("проверка просрочена, начнём через минуту");
                await Task.Delay(PollDelayAfterWake).ConfigureAwait(true);
            }

            await SynchronizeAlbumAsync(forceDownload: false).ConfigureAwait(true);
        }

        /// <summary>
        /// Опрашивает включённые источники и, если набор снимков изменился, догружает новые.
        /// </summary>
        /// <param name="forceDownload">
        /// Обновлять даже если набор совпадает — для явного запроса пользователя.
        /// </param>
        private async Task SynchronizeAlbumAsync(bool forceDownload)
        {
            // Проверка по таймеру не должна прерывать уже идущую синхронизацию.
            if (!await _syncGate.WaitAsync(0).ConfigureAwait(true))
            {
                return;
            }

            try
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    SyncButton.IsEnabled = false;
                    ShowToast("Проверка источников...");
                });

                // В журнал состояния, а не только в logcat: тот не переживает
                // перезагрузку, а разбираться в утреннем подтормаживании приходится
                // уже после неё.
                FrameHeartbeat.Write("проверка источников начата");

                // Прогресс приходит только от альбома: локальные папки ничего не качают,
                // и писать "Загрузка" про обход файлов было бы неправдой.
                // Просроченная проверка после пробуждения стартует с фонового потока
                // таймера — без своего SynchronizationContext обратный вызов Progress
                // придёт туда же, а ShowToast трогает view не из UI-потока.
                var downloadProgress = new Progress<(int Completed, int Total)>(progress =>
                    MainThread.BeginInvokeOnMainThread(() =>
                        ShowToast($"Загрузка из альбома: {progress.Completed} из {progress.Total}...")));

                AlbumSyncResult syncResult = await _photoSource
                    .RefreshAsync(forceDownload, downloadProgress)
                    .ConfigureAwait(true);

                FrameSettings.LastSyncUtc = DateTime.UtcNow;

                FrameHeartbeat.Write(
                    $"проверка завершена: кадров {syncResult.TotalPhotoCount}, "
                    + $"новых {syncResult.DownloadedCount}, убрано {syncResult.RemovedCount}");

                // Список перечитывается вне потока отрисовки, и лишь готовый набор
                // применяется в нём: иначе тысячи проверок файлов подряд заметны глазом.
                List<string> loadedPhotoPaths = await Task.Run(_photoSource.GetPhotoPaths)
                    .ConfigureAwait(true);

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    ApplyLoadedPhotos(loadedPhotoPaths);
                    ShowToast(DescribeSyncResult(syncResult));
                });
            }
            catch (PhotoSourceException syncFailure)
            {
                // Причина показывается на экране: у рамки нет консоли, и это
                // единственный канал диагностики для пользователя.
                await MainThread.InvokeOnMainThreadAsync(() => ShowToast(syncFailure.Message));
            }
            catch (Exception unexpectedFailure)
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                    ShowToast("Непредвиденная ошибка обновления."));
                System.Diagnostics.Debug.WriteLine(unexpectedFailure);
            }
            finally
            {
                await MainThread.InvokeOnMainThreadAsync(() => SyncButton.IsEnabled = true);
                _syncGate.Release();
            }
        }

        /// <summary>
        /// Текст статуса. Отдельно показывает, сколько кадров скачано, — так видно,
        /// что повторная синхронизация не перекачивает всё заново.
        /// </summary>
        private static string DescribeSyncResult(AlbumSyncResult syncResult)
        {
            string statusText;
            if (syncResult.DownloadedCount == 0 && syncResult.RemovedCount == 0)
            {
                // Ни времени, ни даты: сообщение и так живёт пять секунд и появляется
                // сразу после проверки, так что «когда» очевидно из самого его появления.
                statusText = $"Без изменений: {syncResult.TotalPhotoCount} фото";
            }
            else
            {
                // "убрано", а не "удалено": для папок на устройстве снимки лишь перестают
                // показываться, файлы остаются на месте. Счётчик общий для обоих
                // источников, поэтому формулировка должна быть верна и там, и там.
                string changeSummary = syncResult.RemovedCount == 0
                    ? $"новых {syncResult.DownloadedCount}"
                    : $"новых {syncResult.DownloadedCount}, убрано {syncResult.RemovedCount}";

                statusText = $"Обновлено: {syncResult.TotalPhotoCount} фото ({changeSummary})";
            }

            // Кадров меньше, чем есть в источнике, — и это надо объяснить, иначе кажется,
            // что показывается всё. Причины две, и путать их нельзя: предел задан
            // человеком, а неудачная загрузка — нет.
            if (syncResult.AvailableCount > syncResult.TotalPhotoCount)
            {
                // Пределы у источников свои, и виноват тот, чей превышен. Берём меньший
                // из включённых: именно он обрежет набор первым.
                int photoLimit = SmallestActivePhotoLimit();

                statusText += photoLimit > 0 && syncResult.AvailableCount > photoLimit
                    ? $" из {syncResult.AvailableCount} в источнике (предел загрузки)"
                    : $" из {syncResult.AvailableCount} в источнике";
            }

            // Один источник мог отказать, а показывать всё равно есть что — не скрываем это.
            return syncResult.Warning is null
                ? statusText
                : $"{statusText}. {syncResult.Warning}";
        }

        /// <summary>
        /// Наименьший из пределов включённых сетевых источников; 0 — предела нет.
        /// </summary>
        private static int SmallestActivePhotoLimit()
        {
            int smallestLimit = 0;

            if (FrameSettings.UseSharedAlbum && FrameSettings.AlbumPhotoLimit > 0)
            {
                smallestLimit = FrameSettings.AlbumPhotoLimit;
            }

            if (FrameSettings.UseImmich && FrameSettings.ImmichPhotoLimit > 0)
            {
                smallestLimit = smallestLimit == 0
                    ? FrameSettings.ImmichPhotoLimit
                    : Math.Min(smallestLimit, FrameSettings.ImmichPhotoLimit);
            }

            return smallestLimit;
        }

        /// <summary>
        /// Перечитывает набор кадров, не занимая поток отрисовки.
        /// </summary>
        /// <remarks>
        /// Список собирается с диска: два манифеста и проверка существования каждого
        /// файла — при тысячах кадров это тысячи обращений к памяти устройства, и в потоке
        /// отрисовки такая работа заметна подтормаживанием — особенно после сна, когда
        /// просроченная проверка источников сваливает всю работу в одну минуту.
        /// </remarks>
        private async Task LoadPhotosFromCacheAsync()
        {
            List<string> loadedPhotoPaths = await Task.Run(_photoSource.GetPhotoPaths)
                .ConfigureAwait(true);

            ApplyLoadedPhotos(loadedPhotoPaths);
        }

        /// <summary>
        /// Перечитывает список кадров, задавая порядок показа.
        /// </summary>
        /// <remarks>
        /// Порядок при случайном показе создаётся ровно один раз — в момент обновления
        /// набора. Пока набор тот же, список и текущее место сохраняются: иначе возврат
        /// с экрана настроек или из сведений о файле пересоздавал бы порядок, и шаг назад
        /// приводил бы к кадрам, которых в этом показе ещё не было. Исключение — смена
        /// самого режима порядка: тогда очередь пересобирается намеренно.
        /// </remarks>
        private void ApplyLoadedPhotos(List<string> loadedPhotoPaths)
        {

            // Сравнение строит множество из трёх тысяч путей, поэтому считается один раз.
            bool sameSet = HasSamePhotoSet(loadedPhotoPaths);

            // Переключатель случайного порядка — единственная настройка, которая обязана
            // менять уже идущий показ: набор кадров при этом остаётся прежним.
            bool shuffleModeChanged = _appliedShuffleMode is bool appliedShuffleMode
                && appliedShuffleMode != FrameSettings.ShufflePhotos;

            if (!shuffleModeChanged && sameSet && _currentPhotoIndex >= 0)
            {
                // Набор тот же — показ продолжается с того же кадра. Таймер при уходе
                // со страницы останавливается, поэтому его нужно завести снова.
                if (_isNightModeActive != true && !_isVideoPlaying)
                {
                    _slideshowTimer.Stop();
                    _slideshowTimer.Start();
                }

                return;
            }

            int restoredIndex = -1;

            if (shuffleModeChanged)
            {
                // Режим сменили на экране настроек: очередь пересобирается из тех же
                // кадров заново. Выключили случайный порядок — кадры остаются в порядке
                // источника; включили — перемешиваются. Порядок, сохранённый на диске,
                // тут не помощник: он записан для прежнего режима.
                _localPhotoPaths = loadedPhotoPaths;
                _currentPhotoIndex = -1;
                _hasTriedRestoringOrder = true;

                if (FrameSettings.ShufflePhotos)
                {
                    ShufflePhotoOrder(_localPhotoPaths);
                }

                // Порядок задан заново и заведомо отличается от лежащего на диске.
                SlideshowStateStore.SaveOrder(_localPhotoPaths);
            }
            else if (!sameSet)
            {
                if (_currentPhotoIndex >= 0 && _localPhotoPaths.Count > 0)
                {
                    // Показ уже идёт: набор всего лишь обновился (кадры добавились или
                    // пропали), а не загрузился впервые. Полная пересортировка тут же
                    // отправила бы уже показанные кадры обратно вперёд по очереди — и
                    // они замелькали бы снова. Поэтому уже показанная часть списка
                    // остаётся как есть, а новые кадры лишь дополняют ещё не показанный
                    // хвост.
                    MergeLoadedPhotos(loadedPhotoPaths);
                }
                else
                {
                    _localPhotoPaths = loadedPhotoPaths;
                    _currentPhotoIndex = -1;

                    // При первой загрузке пробуем продолжить с того же кадра и в том же
                    // порядке, что были до выключения рамки.
                    if (!_hasTriedRestoringOrder)
                    {
                        _hasTriedRestoringOrder = true;
                        restoredIndex = SlideshowStateStore.TryRestoreOrder(_localPhotoPaths);
                    }

                    if (restoredIndex < 0)
                    {
                        if (FrameSettings.ShufflePhotos)
                        {
                            ShufflePhotoOrder(_localPhotoPaths);
                        }

                        // Четыре тысячи строк на диск — только если порядок и правда стал
                        // другим, а не при любом изменении набора.
                        SlideshowStateStore.SaveOrderIfChanged(_localPhotoPaths);
                    }
                }
            }

            _appliedShuffleMode = FrameSettings.ShufflePhotos;

            // Ночью слайд-шоу не крутится: новые снимки подхватятся утром.
            if (_localPhotoPaths.Count == 0 || _isNightModeActive == true)
            {
                _slideshowTimer.Stop();
                return;
            }

            if (restoredIndex >= 0)
            {
                ShowPhotoAt(restoredIndex);
            }
            else
            {
                ShowNextPhoto();
            }

            _slideshowTimer.Start();
        }

        /// <summary>
        /// True, если набор кадров совпадает с показываемым — без учёта порядка.
        /// </summary>
        private bool HasSamePhotoSet(List<string> loadedPhotoPaths)
        {
            if (loadedPhotoPaths.Count != _localPhotoPaths.Count)
            {
                return false;
            }

            var shownPaths = new HashSet<string>(_localPhotoPaths, StringComparer.OrdinalIgnoreCase);
            foreach (string loadedPath in loadedPhotoPaths)
            {
                if (!shownPaths.Contains(loadedPath))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Встраивает обновлённый набор кадров в уже идущий показ, не трогая
        /// показанную часть очереди.
        /// </summary>
        /// <remarks>
        /// Очередь режется на «уже показано» (индексы 0.._currentPhotoIndex, включая
        /// текущий кадр) и «ещё не показано» (всё, что дальше). Пропавшие из источника
        /// кадры просто выпадают из обеих частей, а новые добавляются в конец ещё не
        /// показанной части — так они попадут в показ не раньше, чем дойдёт очередь, и
        /// не столкнут уже виденные кадры на повтор.
        /// </remarks>
        private void MergeLoadedPhotos(List<string> loadedPhotoPaths)
        {
            var oldPaths = new HashSet<string>(_localPhotoPaths, StringComparer.OrdinalIgnoreCase);
            var loadedSet = new HashSet<string>(loadedPhotoPaths, StringComparer.OrdinalIgnoreCase);

            List<string> shownPart = _localPhotoPaths
                .Take(_currentPhotoIndex + 1)
                .Where(path => loadedSet.Contains(path))
                .ToList();

            List<string> remainingPart = _localPhotoPaths
                .Skip(_currentPhotoIndex + 1)
                .Where(path => loadedSet.Contains(path))
                .ToList();

            List<string> newPhotos = loadedPhotoPaths
                .Where(path => !oldPaths.Contains(path))
                .ToList();

            if (FrameSettings.ShufflePhotos)
            {
                ShufflePhotoOrder(newPhotos);
            }

            remainingPart.AddRange(newPhotos);

            _localPhotoPaths = shownPart.Concat(remainingPart).ToList();
            _currentPhotoIndex = shownPart.Count - 1;

            SlideshowStateStore.SaveOrderIfChanged(_localPhotoPaths);
        }

        /// <summary>Перемешивание Фишера — Йетса по месту.</summary>
        private static void ShufflePhotoOrder(List<string> photoPaths)
        {
            for (int currentIndex = photoPaths.Count - 1; currentIndex > 0; currentIndex--)
            {
                int swapIndex = Random.Shared.Next(currentIndex + 1);
                (photoPaths[currentIndex], photoPaths[swapIndex]) =
                    (photoPaths[swapIndex], photoPaths[currentIndex]);
            }
        }

        private void OnSlideshowTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            // Таймер срабатывает в фоновом потоке, а Source трогать можно только из UI-потока.
            MainThread.BeginInvokeOnMainThread(ShowNextPhoto);
        }

        private void ShowNextPhoto()
        {
            if (_localPhotoPaths.Count == 0)
            {
                return;
            }

            // Порядок не пересоздаётся на новом круге: иначе шаг назад после последнего
            // кадра уводил бы в уже другую случайную последовательность. Новый порядок
            // появляется вместе с новым набором снимков — см. ApplyLoadedPhotos.
            ShowPhotoAt(_currentPhotoIndex + 1);
        }

        private void ShowPhotoAt(int photoIndex)
        {
            int photoCount = _localPhotoPaths.Count;
            if (photoCount == 0)
            {
                return;
            }

            // Уходя с кадра, всегда гасим проигрыватель: иначе видео продолжало бы
            // играть под уже следующей фотографией.
            StopVideoPlayback();

            // Повтор живого фото живёт ровно на своём кадре и на это время останавливает
            // показ. Снимать его надо здесь, для любого следующего слайда: иначе уход
            // с кадра видео (или его удаление) оставляет показ стоять.
            StopMotionLoop(resumeSlideshow: _isMotionLooping);

            // Оператор % в C# сохраняет знак, поэтому для шага назад нужна нормализация.
            _currentPhotoIndex = ((photoIndex % photoCount) + photoCount) % photoCount;

            string mediaPath = _localPhotoPaths[_currentPhotoIndex];
            int photoGeneration = ++_photoGeneration;

            // Чтобы после включения рамки показ продолжился с этого же кадра.
            SlideshowStateStore.SaveLastShown(mediaPath);

            // Источник или имя файла слева от счётчика: сам счётчик остаётся прижатым
            // к углу. Кадр альбома назван хэшем ссылки, и такое имя не говорит ничего —
            // куда полезнее знать, что снимок пришёл из Google Photos.
            // Имя файла кадра Immich — хэш, и подписывать им кадр бессмысленно; зато
            // исходное имя сохранено при синхронизации, и оно как раз читаемое.
            ImmichSlideInfo? immichSlide = ImmichSidecar.Find(mediaPath);

            string frameLabel = MediaTrash.IsAlbumPhoto(mediaPath)
                ? "Google Photos"
                : immichSlide is not null
                    // Альбом полезнее слова «Immich»: источник и так один на все эти
                    // кадры, а альбом говорит, откуда снимок. Библиотеку целиком
                    // подписывать нечем — тогда остаётся имя источника.
                    ? (immichSlide.AlbumName.Length > 0 ? immichSlide.AlbumName : "Immich")
                        + "/" + immichSlide.FileName
                    : MediaTrash.IsImmichPhoto(mediaPath)
                        // Список ещё не составлен — до ближайшей синхронизации у кадра
                        // есть только имя-хэш, и показывать его незачем.
                        ? "Immich"
                        : MediaLabelFormatter.Describe(mediaPath);

            SetOutlinedText(
                _photoCounterLabels, $"{frameLabel}  ·  {_currentPhotoIndex + 1}/{photoCount}");

            // Сразу переставляем часы по кругу: если разбор снимка не удастся или
            // затянется, надпись всё равно не останется на прежнем месте.
            MoveClockToNextPosition();

            _currentMotionPoster = null;
            _isCurrentSlideVideo = MediaFileTypes.IsVideo(mediaPath);
            _currentVideoPath = _isCurrentSlideVideo ? mediaPath : null;
            _currentAlbumVideoPoster = null;
            UpdateTapRevealedOverlays();

            // Подпись читается для любого кадра, и для видео тоже: дата съёмки есть
            // и в контейнере.
            _ = ShowCaptureInfoAsync(mediaPath, photoGeneration);

            if (!_isCurrentSlideVideo)
            {
                _ = ShowSlideFrameAsync(mediaPath, photoGeneration);

                if (FrameSettings.ShowClock)
                {
                    _ = PlaceClockOverPhotoAsync(mediaPath, photoGeneration);
                }

                // За сетевым кадром может стоять клип: заставка уже на экране, а сам
                // файл забирается только теперь — см. SlideClips.
                SlideClip? slideClip = FrameSettings.DownloadAlbumVideos
                    ? SlideClips.Find(mediaPath)
                    : null;

                AlbumVideoBadge.IsVisible = false;

                // Кнопка повтора живёт ровно один кадр: на следующем снимке повторять
                // уже нечего.
                _currentMotionPoster = slideClip is { IsMotionPhoto: true } ? mediaPath : null;
                UpdateTapRevealedOverlays();

                if (slideClip is { IsMotionPhoto: false })
                {
                    ShowAlbumVideoBadge(slideClip.DurationMilliseconds, isReady: false);
                    _ = PlayAlbumVideoAsync(mediaPath, photoGeneration);
                }
                else if (slideClip is { IsMotionPhoto: true } && FrameSettings.AnimateMotionPhotos)
                {
                    // Живое фото оживает молча: ни отметки, ни полосы загрузки — кадр
                    // должен выглядеть снимком, который просто на секунду ожил.
                    _ = AnimateMotionPhotoAsync(mediaPath, photoGeneration);
                }

                return;
            }

            // У видео нет готовой картинки, поэтому показываем кадр из него самого:
            // он виден, пока проигрыватель готовится, и остаётся фоном при ошибке.
            ClearSlideLayers();
            _ = ShowVideoPosterAsync(mediaPath, photoGeneration);

            // Видео начинается само, как только слайд-шоу до него дошло, и по окончании
            // рамка переходит к следующему кадру. Ночью — не начинается.
            if (_isNightModeActive != true)
            {
                StartVideoPlayback();
            }
        }

        /// <summary>
        /// Показывает снимок, проявляя его поверх предыдущего.
        /// </summary>
        /// <remarks>
        /// Кадр читается в фоне и уже готовым отдаётся свободному слою, после чего слои
        /// перетекают друг в друга. Пока чтение идёт, на экране остаётся прежний снимок.
        /// </remarks>
        private async Task ShowSlideFrameAsync(string photoPath, int photoGeneration)
        {
            Android.Graphics.Bitmap? frame = await Task.Run(() => PhotoFrameDecoder.Decode(
                    photoPath, AppSettings.FrameWidthPixels, AppSettings.FrameHeightPixels))
                .ConfigureAwait(true);

            if (frame is null)
            {
                // Нечитаемый файл: на экране остаётся прежний кадр, а показ идёт дальше.
                FrameLog.Warn($"Кадр не показан: {photoPath}");
                return;
            }

            // Слайд успел смениться, пока читали, — этот кадр уже не нужен.
            if (photoGeneration != _photoGeneration)
            {
                ReleaseFrame(frame);
                return;
            }

            bool intoTopLayer = !_slideInTopLayer;
            Image targetLayer = intoTopLayer ? SlideshowImageTop : SlideshowImage;

            if (!TrySetLayerFrame(targetLayer, frame))
            {
                // Обработчик ещё не создан — покажем следующий кадр, этот освобождаем.
                ReleaseFrame(frame);
                return;
            }

            if (intoTopLayer)
            {
                ReleaseFrame(_topSlideFrame, frame);
                _topSlideFrame = frame;
            }
            else
            {
                ReleaseFrame(_bottomSlideFrame, frame);
                _bottomSlideFrame = frame;
            }

            _slideInTopLayer = intoTopLayer;

            // Прозрачность меняется только у верхнего слоя: нижний всегда виден под ним.
            await SlideshowImageTop.FadeToAsync(
                intoTopLayer ? 1 : 0, SlideCrossfadeMilliseconds).ConfigureAwait(true);

            // Кадр из ушедшего слоя надо убрать, а не просто перекрыть: вписанный
            // снимок занимает не весь экран, и вокруг него просвечивает предыдущий —
            // после вертикального кадра по краям остаются куски горизонтального.
            if (photoGeneration != _photoGeneration || _slideInTopLayer != intoTopLayer)
            {
                // За время перехода успел появиться следующий кадр — он и разберётся
                // со слоями сам.
                return;
            }

            if (intoTopLayer)
            {
                DetachLayer(SlideshowImage);
                ReleaseFrame(_bottomSlideFrame, keepIfSame: frame);
                _bottomSlideFrame = null;
            }
            else
            {
                DetachLayer(SlideshowImageTop);
                ReleaseFrame(_topSlideFrame, keepIfSame: frame);
                _topSlideFrame = null;
            }
        }

        /// <summary>Убирает снимок с экрана вместе с его кадрами.</summary>
        private void ClearSlideLayers()
        {
            SlideshowImageTop.Opacity = 0;

            DetachLayer(SlideshowImage);
            DetachLayer(SlideshowImageTop);

            ReleaseFrame(_bottomSlideFrame);
            ReleaseFrame(_topSlideFrame, keepIfSame: _bottomSlideFrame);

            _bottomSlideFrame = null;
            _topSlideFrame = null;
            _slideInTopLayer = false;
        }

        /// <summary>
        /// Подписывает кадр тем, чем и когда он снят.
        /// </summary>
        /// <remarks>
        /// Чтение EXIF — обращение к диску, поэтому идёт в фоне; результат применяется
        /// только если кадр за это время не сменился. Если данных нет, строка убирается:
        /// подпись «неизвестно» поверх фотографии никому не нужна.
        /// </remarks>
        private async Task ShowCaptureInfoAsync(string mediaPath, int photoGeneration)
        {
            if (!FrameSettings.ShowCaptureInfo)
            {
                _hasCaptureInfo = false;
                UpdateTapRevealedOverlays();
                return;
            }

            // У кадра Immich EXIF читать неоткуда: рамка показывает превью с сервера,
            // а из него съёмочные поля вырезаны. Зато сервер отдал их при синхронизации.
            ImmichSlideInfo? immichSlide = ImmichSidecar.Find(mediaPath);

            MediaDetailsReader.CaptureInfo captureInfo = immichSlide is null
                ? await Task
                    .Run(() => MediaDetailsReader.ReadCaptureInfo(mediaPath))
                    .ConfigureAwait(true)
                : new MediaDetailsReader.CaptureInfo(
                    immichSlide.CameraName.Length == 0 ? null : immichSlide.CameraName,
                    immichSlide.TakenAt);

            if (photoGeneration != _photoGeneration)
            {
                return;
            }

            var parts = new List<string>(2);

            if (!string.IsNullOrEmpty(captureInfo.Device))
            {
                parts.Add(captureInfo.Device);
            }

            if (captureInfo.TakenAt is { } takenAt)
            {
                // Со временем, а не только с датой: в один день снимков бывает много,
                // и час съёмки как раз и отличает утреннюю прогулку от вечерней.
                parts.Add(takenAt.ToString("d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture));
            }

            _hasCaptureInfo = parts.Count > 0;

            if (_hasCaptureInfo)
            {
                SetOutlinedText(_captureInfoLabels, string.Join("  ·  ", parts));
            }

            UpdateTapRevealedOverlays();
        }

        /// <summary>
        /// Догружает видео кадра альбома и, если слайд ещё на экране, включает его.
        /// </summary>
        /// <remarks>
        /// Пока клип качается, на экране остаётся заставка — слайд-шоу идёт своим ходом.
        /// Если кадр за это время сменился, найденный файл просто остаётся в кэше
        /// и сыграет на следующем круге, уже без ожидания.
        /// </remarks>
        private async Task PlayAlbumVideoAsync(string posterPath, int photoGeneration)
        {
            // Progress создан в UI-потоке, поэтому его обратные вызовы приходят туда же.
            var downloadProgress = new Progress<double>(fraction =>
            {
                if (photoGeneration == _photoGeneration)
                {
                    AlbumVideoProgressBar.Progress = Math.Clamp(fraction, 0, 1);
                }
            });

            string? videoPath = await SlideClips
                .TryGetAsync(posterPath, downloadProgress).ConfigureAwait(true);

            if (photoGeneration != _photoGeneration)
            {
                return;
            }

            if (videoPath is null)
            {
                // Заставка остаётся на экране, но молчать не стоит: иначе кадр выглядит
                // обычным снимком, который почему-то помечен как видео.
                AlbumVideoBadge.IsVisible = false;
                ShowToast("Видео не загрузилось");
                return;
            }

            if (_isNightModeActive == true)
            {
                return;
            }

            AlbumVideoBadge.IsVisible = false;

            _isCurrentSlideVideo = true;
            _currentVideoPath = videoPath;
            _currentAlbumVideoPoster = posterPath;
            UpdateTapRevealedOverlays();

            StartVideoPlayback();
        }

        /// <summary>
        /// Клип не открылся: отмечаем его и идём дальше.
        /// </summary>
        /// <remarks>
        /// Часть видео Google хранит в VP9, а на рамке этот кодек только программный —
        /// такие файлы проигрыватель не открывает вовсе и показывает нулевую длину.
        /// Отметка не даёт качать их снова на каждом круге.
        /// </remarks>
        private void OnVideoPlaybackFailed(object? sender, EventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                string? posterPath = _currentAlbumVideoPoster;
                bool wasMotionPlayback = _isMotionPlayback;

                StopVideoPlayback();
                AlbumVideoBadge.IsVisible = false;

                if (posterPath is not null)
                {
                    // Пауза для живых фото ставится на любой отказ: спотыкается сам
                    // декодер, а не конкретный клип, и следующий кадр обычно повторит
                    // судьбу этого.
                    _motionBackoffUntil = DateTime.Now + MotionFailureBackoff;

                    SlideClips.MarkUnplayable(posterPath);

                    // О живом фото не сообщаем: оно и так остаётся снимком, а сообщение
                    // на каждый такой кадр было бы навязчивым.
                    if (!wasMotionPlayback)
                    {
                        ShowToast("Это видео рамка не проигрывает — оставили кадром");
                    }
                }

                _isCurrentSlideVideo = false;
                _currentVideoPath = null;
                _currentAlbumVideoPoster = null;
                UpdateTapRevealedOverlays();

                // Показ продолжается: на экране остаётся заставка, а дальше обычный ход.
                if (_localPhotoPaths.Count > 0 && _isNightModeActive != true)
                {
                    _slideshowTimer.Stop();
                    _slideshowTimer.Start();
                }
            });
        }

        /// <summary>
        /// Оживляет живое фото: проигрывает клип один раз и оставляет кадр снимком.
        /// </summary>
        /// <remarks>
        /// Слайд-шоу при этом не останавливается: кадр держится своё обычное время, а
        /// секунда движения — лишь его начало. Так ведут себя живые фото на телефоне,
        /// и повторять их по кругу незачем.
        /// </remarks>
        private async Task AnimateMotionPhotoAsync(
            string posterPath, int photoGeneration, bool loop = false)
        {
            // После отказа декодера живые фото молчат: кадр остаётся снимком, а не
            // тянет за собой вереницу таких же отказов. Нажатие 🔁 — просьба явная,
            // и её выполняем, даже если пауза ещё не кончилась.
            if (!loop && _motionBackoffUntil is { } quietUntil && DateTime.Now < quietUntil)
            {
                return;
            }

            // Пауза перед клипом: пролистывание кадров не должно заводить декодер
            // на каждый пройденный слайд.
            await Task.Delay(MotionStartDelayMilliseconds).ConfigureAwait(true);

            if (photoGeneration != _photoGeneration)
            {
                return;
            }

            // Полоска внизу кадра, а не отметка по центру: живое фото должно выглядеть
            // снимком, но ждать молча тоже нельзя — клип едет с сервера.
            var downloadProgress = new Progress<double>(fraction =>
            {
                if (photoGeneration == _photoGeneration)
                {
                    MotionLoadingBar.Progress = Math.Clamp(fraction, 0, 1);
                    MotionLoadingBar.IsVisible = _isNightModeActive != true;
                }
            });

            MotionLoadingBar.Progress = 0;
            MotionLoadingBar.IsVisible = SlideClips.FindReady(posterPath) is null
                                         && _isNightModeActive != true;

            string? clipPath = await SlideClips
                .TryGetAsync(posterPath, downloadProgress).ConfigureAwait(true);

            if (photoGeneration != _photoGeneration)
            {
                return;
            }

            MotionLoadingBar.IsVisible = false;

            if (clipPath is null || _isNightModeActive == true)
            {
                if (clipPath is null && loop)
                {
                    // Молча не оставляем: кнопку нажали и вправе знать, почему ничего
                    // не произошло.
                    StopMotionLoop(resumeSlideshow: true);
                    ShowToast("Клип живого фото не загрузился");
                }

                return;
            }

            _isMotionPlayback = true;
            _currentAlbumVideoPoster = posterPath;

            VideoPlayer.IsLooping = loop;
            VideoPlayer.IsMuted = true;
            VideoPlayer.SourcePath = clipPath;
            VideoPlayer.Play();
        }

        /// <summary>
        /// Включает и выключает бесконечный повтор живого фото.
        /// </summary>
        private void OnMotionRepeatClicked(object? sender, EventArgs e)
        {
            string? posterPath = _currentMotionPoster;
            if (posterPath is null)
            {
                return;
            }

            _panelHideTimer.Stop();
            _panelHideTimer.Start();

            if (_isMotionLooping)
            {
                StopMotionLoop(resumeSlideshow: true);
                return;
            }

            // Пока крутим — слайд не меняется: иначе кадр уехал бы через свои десять
            // секунд, и повтор оказался бы бессмысленным.
            _isMotionLooping = true;
            _slideshowTimer.Stop();
            UpdateTapRevealedOverlays();

            _ = AnimateMotionPhotoAsync(posterPath, _photoGeneration, loop: true);
        }

        /// <summary>
        /// Спрашивает подтверждение перед отвязкой клипа: действие небыстро обратимо
        /// (клип уходит в корзину сервера), и промах по кнопке не должен стоить его молча.
        /// </summary>
        private async void OnDetachLiveClicked(object? sender, EventArgs e)
        {
            string? posterPath = _currentMotionPoster;
            if (posterPath is null || _pendingDetachPosterPath is not null)
            {
                return;
            }

            ImmichSlideInfo? slide = ImmichSidecar.Find(posterPath);
            if (slide is null || slide.ClipAssetId.Length == 0)
            {
                return;
            }

            // Пока висит вопрос, кадр не должен смениться — иначе отвязался бы не тот.
            _slideshowTimer.Stop();
            _panelHideTimer.Stop();

            if (_isMotionLooping)
            {
                StopMotionLoop(resumeSlideshow: false);
            }

            _pendingDetachPosterPath = posterPath;

            DetachConfirmPanel.IsVisible = true;
            await AnimateDetachConfirmAsync(shown: true).ConfigureAwait(true);
        }

        private async void OnCancelDetachClicked(object? sender, EventArgs e)
        {
            await HideDetachConfirmAsync().ConfigureAwait(true);

            _panelHideTimer.Start();
            _slideshowTimer.Start();
        }

        /// <summary>
        /// Отвязывает клип живого фото от снимка на сервере Immich: сам снимок остаётся
        /// в показе, а секунда движения больше не приезжает и не проигрывается.
        /// </summary>
        private async void OnConfirmDetachClicked(object? sender, EventArgs e)
        {
            string? posterPath = _pendingDetachPosterPath;
            await HideDetachConfirmAsync().ConfigureAwait(true);

            if (posterPath is null)
            {
                _panelHideTimer.Start();
                _slideshowTimer.Start();
                return;
            }

            ImmichSlideInfo? slide = ImmichSidecar.Find(posterPath);
            if (slide is null || slide.ClipAssetId.Length == 0)
            {
                _panelHideTimer.Start();
                _slideshowTimer.Start();
                return;
            }

            DetachLiveButton.IsEnabled = false;

            ImmichPhotoSource immichSource =
                IPlatformApplication.Current?.Services.GetService<ImmichPhotoSource>()
                ?? new ImmichPhotoSource();

            string? failureMessage = await immichSource
                .TryDetachLivePhotoAsync(slide.AssetId, slide.ClipAssetId)
                .ConfigureAwait(true);

            DetachLiveButton.IsEnabled = true;
            _panelHideTimer.Start();
            _slideshowTimer.Start();

            if (failureMessage is not null)
            {
                // Android-живое фото Immich не отвязывает в принципе: клип у него не
                // отдельный объект, а зашит в тот же файл, что и снимок, и сервер честно
                // отказывает любому ключу. Запоминаем — кнопка на этом кадре больше
                // не появится, а вопрос не будет звучать одинаково на каждом таком кадре.
                if (failureMessage.Contains("android motion photos", StringComparison.OrdinalIgnoreCase))
                {
                    ImmichPhotoSource.MarkDetachUnsupported(posterPath);
                    UpdateTapRevealedOverlays();
                    ShowToast("Это Android-живое фото: клип зашит в файл, Immich не даёт его отделить");
                    return;
                }

                ShowToast($"Не отвязано: {failureMessage}");
                return;
            }

            // Сервер уже не знает об этом клипе. Локальный файл и отметку убираем тем же
            // способом, каким кэш прячет неиграющиеся клипы, — не дожидаясь ближайшей
            // полной синхронизации, которая и так перепишет список без ссылки на клип.
            ImmichVideoCache.MarkUnplayable(posterPath);

            _currentMotionPoster = null;
            UpdateTapRevealedOverlays();
            ShowToast("Живое фото отключено — снимок остался");
        }

        private Task AnimateDetachConfirmAsync(bool shown)
        {
            return Task.WhenAll(
                DetachConfirmPanel.TranslateToAsync(shown ? 0 : -44, 0, 180),
                DetachConfirmPanel.FadeToAsync(shown ? 1 : 0, 180));
        }

        private async Task HideDetachConfirmAsync()
        {
            _pendingDetachPosterPath = null;

            await AnimateDetachConfirmAsync(shown: false).ConfigureAwait(true);
            DetachConfirmPanel.IsVisible = false;
        }

        /// <summary>
        /// Убирает подтверждение без анимации. Возвращает true, если вопрос действительно
        /// висел, — тогда вызывающий код возобновляет показ.
        /// </summary>
        private bool ResetDetachConfirm()
        {
            if (_pendingDetachPosterPath is null)
            {
                return false;
            }

            _pendingDetachPosterPath = null;

            DetachConfirmPanel.IsVisible = false;
            DetachConfirmPanel.Opacity = 0;
            DetachConfirmPanel.TranslationX = -44;
            return true;
        }

        /// <summary>
        /// Снимает повтор: проигрыватель гаснет, на экране остаётся снимок.
        /// </summary>
        /// <param name="resumeSlideshow">
        /// Вернуть ли обычный ход показа. При смене кадра — нет: там своё расписание.
        /// </param>
        private void StopMotionLoop(bool resumeSlideshow)
        {
            MotionLoadingBar.IsVisible = false;

            if (!_isMotionLooping)
            {
                UpdateTapRevealedOverlays();
                return;
            }

            _isMotionLooping = false;
            StopVideoPlayback();

            if (resumeSlideshow && _isNightModeActive != true)
            {
                _slideshowTimer.Stop();
                _slideshowTimer.Start();
            }

            UpdateTapRevealedOverlays();
        }

        /// <summary>Отметка «за этим кадром видео» с его длительностью.</summary>
        private void ShowAlbumVideoBadge(int durationMilliseconds, bool isReady)
        {
            // Длительность известна не всегда: в выдаче Immich её у части объектов
            // просто нет. Показывать «0:00» в таком случае хуже, чем не показывать
            // ничего, — отметка нужна, чтобы кадр не выглядел обычным снимком.
            string clipTime = durationMilliseconds > 0
                ? "  " + ClipTimeFormatter.Describe(durationMilliseconds)
                : string.Empty;

            AlbumVideoBadgeLabel.Text = isReady
                ? $"▶{clipTime}"
                : $"▶{clipTime}  ·  загрузка…";

            AlbumVideoProgressBar.Progress = 0;
            AlbumVideoProgressBar.IsVisible = !isReady;
            AlbumVideoBadge.IsVisible = _isNightModeActive != true;
        }

        private async Task ShowVideoPosterAsync(string videoPath, int photoGeneration)
        {
            string? posterPath = await Task.Run(() => VideoThumbnailCache.GetOrCreate(videoPath))
                .ConfigureAwait(true);

            if (photoGeneration != _photoGeneration || posterPath is null)
            {
                return;
            }

            await ShowSlideFrameAsync(posterPath, photoGeneration).ConfigureAwait(true);

            if (FrameSettings.ShowClock)
            {
                await PlaceClockOverPhotoAsync(posterPath, photoGeneration).ConfigureAwait(true);
            }
        }

        /// <summary>
        /// Кнопки видео и подпись кадра живут по тем же правилам, что и панель
        /// управления: поверх снимка не должно быть ничего лишнего, пока экран не тронули.
        /// </summary>
        private void UpdateTapRevealedOverlays()
        {
            bool panelVisible = ControlPanel.IsVisible;
            bool isDayTime = _isNightModeActive != true;

            VideoControls.IsVisible = _isCurrentSlideVideo && isDayTime && panelVisible;
            CaptureInfoHost.IsVisible = _hasCaptureInfo && isDayTime && panelVisible;

            PhotoCounterHost.IsVisible =
                _localPhotoPaths.Count > 0 && _currentPhotoIndex >= 0 && isDayTime && panelVisible;

            // Живой поток камеры не про текущий кадр, поэтому доступен и ночью — только
            // от того, есть ли камера: в Home Assistant или прямая IP-камера
            // (см. RefreshCameraAvailabilityAsync): без этого кнопка вела бы на экран
            // с одной лишь ошибкой.
            CameraButtonHost.IsVisible = _camerasAvailable && panelVisible;

            // Ночью показывать нечего: обновлять, смотреть сведения и убирать кадр —
            // всё это про снимок, которого на экране нет. Настройки остаются.
            PanelLeftButtons.IsVisible = isDayTime;

            // Повторять нечего, если за кадром нет клипа живого фото.
            MotionRepeatButton.IsVisible = _currentMotionPoster is not null && isDayTime;
            MotionRepeatButton.Opacity = _isMotionLooping ? 1.0 : 0.45;

            // Отвязать можно только живое фото Immich — у альбома Google сервер чужой,
            // и удалённо там ничего не поправить. Android-версию, для которой сервер уже
            // отказал, кнопкой больше не предлагаем — второй раз тот же отказ не звучит.
            DetachLiveButton.IsVisible = _currentMotionPoster is { } motionPoster
                && isDayTime
                && MediaTrash.IsImmichPhoto(motionPoster)
                && !ImmichPhotoSource.IsDetachUnsupported(motionPoster);

            PlayPauseButton.Text = _isVideoPlaying ? "⏸" : "▶";
            RepeatButton.Opacity = FrameSettings.VideoRepeat ? 1.0 : 0.45;
            MuteButton.Text = FrameSettings.VideoMuted ? "🔇" : "🔊";

            int volumePercent = SystemVolume.Percent;
            VolumeLabel.Text = FrameSettings.VideoMuted
                ? "тихо"
                : volumePercent < 0 ? "—" : $"{volumePercent}%";
        }

        private void OnPlayPauseClicked(object? sender, EventArgs e)
        {
            if (!_isCurrentSlideVideo || _localPhotoPaths.Count == 0)
            {
                return;
            }

            if (_isVideoPlaying)
            {
                VideoPlayer.Pause();
                _isVideoPlaying = false;

                // Полосу оставляем на месте: пауза — это не сброс позиции.
                _videoProgressTimer.Stop();

                // На паузе слайд-шоу тоже стоит: пользователь ещё смотрит этот кадр.
                UpdateTapRevealedOverlays();
                return;
            }

            StartVideoPlayback();
        }

        /// <summary>
        /// Запускает видео текущего кадра и придерживает слайд-шоу.
        /// </summary>
        private void StartVideoPlayback()
        {
            if (!_isCurrentSlideVideo || _localPhotoPaths.Count == 0 || _currentVideoPath is null)
            {
                return;
            }

            // Пока играет видео, кадры не сменяются.
            _slideshowTimer.Stop();

            VideoPlayer.IsLooping = FrameSettings.VideoRepeat;
            VideoPlayer.IsMuted = FrameSettings.VideoMuted;

            // Для кадра альбома путь ведёт к догруженному клипу, а не к самому слайду:
            // слайд — это заставка.
            VideoPlayer.SourcePath = _currentVideoPath;
            VideoPlayer.Play();

            _isVideoPlaying = true;
            _videoProgressTimer.Start();
            UpdateTapRevealedOverlays();
        }

        private void OnVideoProgressTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(UpdateVideoProgress);
        }

        /// <summary>
        /// Обновляет полосу позиции. Пока файл не подготовлен, длительность приходит
        /// отрицательной — в этом случае полосу не трогаем.
        /// </summary>
        private void UpdateVideoProgress()
        {
            (int positionMilliseconds, int durationMilliseconds) = VideoPlayer.QueryProgress();

            if (durationMilliseconds <= 0)
            {
                return;
            }

            VideoProgressBar.Progress =
                Math.Clamp(positionMilliseconds / (double)durationMilliseconds, 0, 1);

            VideoPositionLabel.Text = ClipTimeFormatter.Describe(positionMilliseconds);
            VideoDurationLabel.Text = ClipTimeFormatter.Describe(durationMilliseconds);
        }


        private void OnRepeatClicked(object? sender, EventArgs e)
        {
            FrameSettings.VideoRepeat = !FrameSettings.VideoRepeat;
            VideoPlayer.IsLooping = FrameSettings.VideoRepeat;
            UpdateTapRevealedOverlays();
        }

        private void OnMuteClicked(object? sender, EventArgs e)
        {
            FrameSettings.VideoMuted = !FrameSettings.VideoMuted;
            VideoPlayer.IsMuted = FrameSettings.VideoMuted;
            UpdateTapRevealedOverlays();
        }

        private void OnVolumeUpClicked(object? sender, EventArgs e) => ChangeVolume(louder: true);

        private void OnVolumeDownClicked(object? sender, EventArgs e) => ChangeVolume(louder: false);

        /// <summary>
        /// Двигает громкость устройства на один шаг.
        /// </summary>
        /// <remarks>
        /// Заодно снимает беззвучный режим: иначе кнопки громкости молча ничего не меняли
        /// бы, и это выглядело бы поломкой.
        /// </remarks>
        private void ChangeVolume(bool louder)
        {
            if (louder)
            {
                SystemVolume.Raise();
            }
            else
            {
                SystemVolume.Lower();
            }

            if (FrameSettings.VideoMuted)
            {
                FrameSettings.VideoMuted = false;
                VideoPlayer.IsMuted = false;
            }

            UpdateTapRevealedOverlays();
        }

        /// <summary>
        /// Видео доиграло. При включённом повторе сюда не приходим — проигрыватель
        /// начинает заново сам.
        /// </summary>
        private void OnVideoPlaybackFinished(object? sender, EventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                // Живое фото отыграло свою секунду: гасим проигрыватель и оставляем
                // кадр на экране — время слайда не кончилось. В режиме повтора клип
                // крутит сам проигрыватель, и сюда мы не попадаем вовсе.
                if (_isMotionPlayback)
                {
                    if (_isMotionLooping)
                    {
                        return;
                    }

                    StopVideoPlayback();
                    return;
                }

                StopVideoPlayback();

                // Возвращаем обычный ход слайд-шоу и сразу переходим к следующему кадру.
                if (_localPhotoPaths.Count > 0 && _isNightModeActive != true)
                {
                    ShowNextPhoto();
                    _slideshowTimer.Start();
                }
            });
        }

        private void StopVideoPlayback()
        {
            if (!_isVideoPlaying && VideoPlayer.SourcePath is null && !_isMotionPlayback)
            {
                return;
            }

            // Stop сам гасит видео перетеканием, открывая снимок под ним: иначе кадр
            // исчезает разом и на переходе к снимку бьёт чёрная вспышка.
            VideoPlayer.Stop();
            VideoPlayer.SourcePath = null;
            _isVideoPlaying = false;

            _isMotionPlayback = false;

            _videoProgressTimer.Stop();
            VideoProgressBar.Progress = 0;
            VideoPositionLabel.Text = ClipTimeFormatter.Describe(0);
            VideoDurationLabel.Text = ClipTimeFormatter.Describe(0);
        }
    }
}
