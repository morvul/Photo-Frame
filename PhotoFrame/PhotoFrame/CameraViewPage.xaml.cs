using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace PhotoFrame
{
    /// <summary>
    /// Живой поток камеры без звука. Камера берётся либо из Home Assistant (HLS/MJPEG/снимки),
    /// либо напрямую как IP-камера по RTSP — минуя перекодировку Home Assistant, которая на
    /// нестабильной камере и рвётся. Можно переключаться между камерами.
    /// </summary>
    /// <remarks>
    /// Чем показывать камеру из Home Assistant, решает сама Home Assistant через атрибут
    /// <c>frontend_stream_type</c>: HLS-плейлист играет ExoPlayer, MJPEG идёт одним долгим
    /// соединением (см. <see cref="HomeAssistantClient.StreamCameraMjpegFramesAsync"/>), а
    /// WebRTC на рамке не поддержан. Если поток не открылся или обрывается на середине
    /// просмотра, страница сама переходит на резерв: снимок камеры, обновляемый по таймеру
    /// с периодом из настроек рамки (см. <see cref="FrameSettings.CameraSnapshotIntervalMilliseconds"/>).
    /// Прямая IP-камера (см. <see cref="FrameSettings.CameraRtspUrl"/>) играет RTSP-поток сама и
    /// при обрыве просто переподключается к нему же.
    /// </remarks>
    public partial class CameraViewPage : ContentPage
    {
        private const int SnapshotCrossfadeMilliseconds = 300;

        /// <summary>Сколько ждать первый кадр живого потока, прежде чем перейти на резерв.</summary>
        private const int MjpegFirstFrameTimeoutMilliseconds = 20000;

        /// <summary>
        /// Сколько ждать первый кадр HLS-потока, прежде чем пробовать другой способ.
        /// Больше, чем у MJPEG, и заметно: Home Assistant только запускает ffmpeg, а на
        /// 32-битной рамке первый кадр дополнительно ждёт JIT-компиляции медиа-классов.
        /// </summary>
        private const int LiveFirstFrameTimeoutMilliseconds = 30000;

        /// <summary>Сколько ждать первый кадр прямого RTSP-потока: рукопожатие RTSP
        /// и первый GOP обычно занимают меньше, чем перекодировка Home Assistant.</summary>
        private const int RtspFirstFrameTimeoutMilliseconds = 20000;

        /// <summary>Сколько попыток поднять живой поток, прежде чем сдаться.</summary>
        /// <remarks>
        /// Сбой обычно мимолётный: камера на секунды уходит в «недоступна», а Home Assistant
        /// в этот момент не успевает отдать ни поток, ни снимок. Поэтому после отказа поток
        /// поднимается заново, а не сразу уступает место резерву.
        /// </remarks>
        private const int MaxLiveAttempts = 2;

        /// <summary>Пауза перед повторной попыткой поднять поток камеры.</summary>
        private const int LiveRetryDelayMilliseconds = 3000;

        /// <summary>
        /// Сколько ждать первый кадр при повторной попытке. Меньше, чем при первой: первый раз
        /// кадр ждёт ещё и JIT-компиляции медиа-классов на 32-битной рамке, а этот путь уже прогрет.
        /// </summary>
        private const int LiveRetryFirstFrameTimeoutMilliseconds = 10000;

        /// <summary>Как часто повторять попытку подключиться к прямой IP-камере после того,
        /// как попытки кончились: она могла выключиться и только что включиться.</summary>
        private const int RtspGiveUpRetryDelayMilliseconds = 15000;

        /// <summary>Откуда камера: из Home Assistant или прямой RTSP-поток IP-камеры.</summary>
        private enum CameraKind
        {
            HomeAssistant,
            Rtsp,
        }

        /// <summary>Одна камера в списке показа.</summary>
        /// <param name="Kind">Источник потока.</param>
        /// <param name="Key">Стабильный ключ камеры: entity_id для Home Assistant, адрес RTSP для IP-камеры.</param>
        /// <param name="DisplayName">Что подписывать над камерой.</param>
        /// <param name="RtspUrl">Адрес RTSP, только для прямого источника.</param>
        private readonly record struct CameraSource(
            CameraKind Kind, string Key, string DisplayName, string? RtspUrl);

        private readonly HomeAssistantClient _client;

        // Интервал берётся из настроек при каждом входе на страницу (см. OnAppearing),
        // это лишь стартовое значение таймера до первого чтения.
        private readonly System.Timers.Timer _snapshotTimer =
            new(FrameSettings.CameraSnapshotIntervalMilliseconds) { AutoReset = true };

        private List<CameraSource> _cameras = new();

        private int _cameraIndex;

        /// <summary>Камера, которую показываем сейчас. По ней понимаем, чей поток оборвался.</summary>
        private CameraSource _currentCamera;

        private string? _snapshotEntityId;

        /// <summary>Идёт ли уже запрос снимка: не даёт следующему тику таймера обогнать его.</summary>
        private bool _isRefreshingSnapshot;

        /// <summary>Какой слой сейчас на виду — на него и не пишем следующий кадр.</summary>
        private bool _snapshotIntoTopLayer;

        /// <summary>
        /// Токен текущей попытки MJPEG-потока Home Assistant. Замена другим экземпляром — это и
        /// есть «останови прошлый поток»: старый цикл видит несовпадение и завершается сам.
        /// </summary>
        private CancellationTokenSource? _mjpegStreamCts;

        /// <summary>Ключ камеры, которую показываем сейчас (entity_id или адрес RTSP).</summary>
        private string? _currentEntityId;

        /// <summary>
        /// Камеры Home Assistant, которым HA не отдаёт живой HLS (облачные, без RTSP-источника).
        /// Для них HLS-попытку пропускаем и идём сразу на MJPEG/снимки — иначе страница зря
        /// висит на «Подключение...» до таймаута, а толку нет.
        /// </summary>
        private readonly HashSet<string> _camerasWithoutHls = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Бренды облачных камер, которым HA не может отдать HLS без локального RTSP.</summary>
        private static readonly HashSet<string> CloudOnlyCameraBrands = new(StringComparer.OrdinalIgnoreCase)
        {
            "Tuya",
        };

        /// <summary>
        /// Сигнал «пришёл первый кадр» для текущей попытки. Каждая попытка живёт со своим
        /// экземпляром: устаревший кадр от прошлой камеры не должен будить новую.
        /// </summary>
        private TaskCompletionSource<bool>? _firstFrameTcs;

        /// <summary>Сколько попыток поднять живой поток уже сделано для текущей камеры.</summary>
        private int _liveAttemptCount;

        /// <summary>Идёт ли уже перезапуск потока: не даёт новым отказам наслоиться друг на друга.</summary>
        private bool _isRestartingLive;

        /// <summary>
        /// Номер текущей попытки поднять поток. Старый сторожевой таймер от предыдущей попытки
        /// не должен перезапускать поток после того, как началась более новая попытка.
        /// </summary>
        private int _liveAttemptGeneration;

        /// <summary>
        /// Идёт ли сейчас живая картинка с камеры (стилл-снимок при переподключении не в счёт).
        /// По нему понимаем, можно ли прятать снимок, когда поток снова поднялся.
        /// </summary>
        private bool _isLiveVideoShowing;

        public CameraViewPage()
        {
            InitializeComponent();

            // Тот же клиент, что и у остальных страниц: он держит долгоживущий
            // HttpClient, и отдельный экземпляр на каждый вход в камеру был бы
            // лишним подключением, которое никто не закрывает.
            _client = IPlatformApplication.Current?.Services.GetService<HomeAssistantClient>()
                ?? new HomeAssistantClient();

            _snapshotTimer.Elapsed += OnSnapshotTimerElapsed;
            LiveVideoPlayer.PlaybackFailed += OnLiveVideoFailed;
            LiveVideoPlayer.FirstFrameRendered += OnLiveVideoFirstFrame;
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();

            // Могли поменять на экране настроек, пока страницы камеры не было на экране.
            _snapshotTimer.Interval = FrameSettings.CameraSnapshotIntervalMilliseconds;

            if (!HomeAssistantClient.IsConfigured
                && string.IsNullOrWhiteSpace(FrameSettings.CameraRtspUrl))
            {
                ShowStatus("Не заданы адрес и токен Home Assistant и адрес IP-камеры в настройках.");
                return;
            }

            ShowStatus("Поиск камер...");

            _ = LoadCamerasAsync();
        }

        private async Task LoadCamerasAsync()
        {
            var cameras = new List<CameraSource>();

            // Прямой RTSP-источник IP-камеры — первым: это самый стабильный путь,
            // и рамка по умолчанию показывает именно его.
            string rtspUrl = FrameSettings.CameraRtspUrl;
            if (!string.IsNullOrWhiteSpace(rtspUrl))
            {
                string rtspName = FrameSettings.CameraRtspName;
                cameras.Add(new CameraSource(
                    CameraKind.Rtsp,
                    rtspUrl,
                    string.IsNullOrWhiteSpace(rtspName) ? rtspUrl : rtspName,
                    rtspUrl));
            }

            // Камеры из Home Assistant, только если он настроен: прямое RTSP камеры
            // рамка умеет и без него.
            if (HomeAssistantClient.IsConfigured)
            {
                try
                {
                    // Список запрашивается заново при каждом входе на страницу: список камер
                    // мог измениться, а рамка не должна показывать камеру, которой уже нет.
                    List<string> cameraEntityIds =
                        await _client.GetCameraEntityIdsAsync().ConfigureAwait(true);

                    foreach (string entityId in cameraEntityIds)
                    {
                        cameras.Add(new CameraSource(
                            CameraKind.HomeAssistant, entityId, entityId, RtspUrl: null));
                    }
                }
                catch (PhotoSourceException lookupFailure)
                {
                    FrameLog.Warn($"Камеры Home Assistant не прочитаны ({lookupFailure.Message})");
                }
            }

            if (cameras.Count == 0)
            {
                ShowStatus("Нет ни одной камеры: ни в Home Assistant, ни прямой IP-камеры.");
                return;
            }

            _cameras = cameras;
            NextCameraButton.IsVisible = _cameras.Count > 1;
            _cameraIndex = 0;

            // Узнаём, что сама Home Assistant считает подходящим способом показа для
            // каждой камеры, и запоминаем: от этого зависит, какой механизм запускать.
            foreach (CameraSource source in _cameras)
            {
                if (source.Kind != CameraKind.HomeAssistant)
                {
                    continue;
                }

                string cameraId = source.Key;
                string? streamType = null;
                try
                {
                    streamType = await _client
                        .GetCameraFrontendStreamTypeAsync(cameraId).ConfigureAwait(true);
                    FrameLog.Info($"{cameraId}: frontend_stream_type = {streamType ?? "(нет)"}");
                }
                catch (PhotoSourceException lookupFailure)
                {
                    FrameLog.Warn($"{cameraId}: frontend_stream_type не прочитан ({lookupFailure.Message})");
                }

                // Облачные камеры (Tuya и подобные) HA не отдаёт живым HLS без RTSP-источника —
                // таких в HLS-попытке нет смысла. Отмечаем их, чтобы идти сразу на MJPEG/снимки.
                try
                {
                    string? brand = await _client.GetCameraBrandAsync(cameraId).ConfigureAwait(true);
                    if (brand is not null && CloudOnlyCameraBrands.Contains(brand))
                    {
                        _camerasWithoutHls.Add(cameraId);
                        FrameLog.Info($"{cameraId}: бренд {brand} — HLS пропускаем, идём на MJPEG/снимки");
                    }
                }
                catch (PhotoSourceException lookupFailure)
                {
                    FrameLog.Warn($"{cameraId}: бренд не прочитан ({lookupFailure.Message})");
                }
            }

            PlayCurrentCamera();
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();

            StopSnapshotPolling();
            StopMjpegStream();
            StopLiveStream();
            _currentEntityId = null;
        }

        private void PlayCurrentCamera()
        {
            CameraSource source = _cameras[_cameraIndex];
            _currentCamera = source;
            _currentEntityId = source.Key;

            // Новая камера — с чистого листа: перезапускаем счётчик попыток и снимаем
            // флаг перезапуска, оставшийся от прежней попытки или прежней камеры.
            _liveAttemptCount = 0;
            _isRestartingLive = false;
            _isLiveVideoShowing = false;

            StopSnapshotPolling();
            StopMjpegStream();
            StopLiveStream();

            // Слои снимка от прошлой камеры или прошлой попытки не нужны — первый
            // пришедший кадр сам их сменит через обычный для своего режима переход.
            SnapshotImage.IsVisible = false;
            SnapshotImage.Opacity = 0;
            SnapshotImageTop.IsVisible = false;
            SnapshotImageTop.Opacity = 0;

            HeaderLabel.Text =
                $"Камера ({_cameraIndex + 1}/{_cameras.Count}) — {source.DisplayName}";

            if (source.Kind == CameraKind.Rtsp)
            {
                // Прямой RTSP-поток: рамка играет его сама. При обрыве просто переподключается.
                ShowStatus("Подключение к камере...");
                _ = RunLiveStreamAsync(RtspFirstFrameTimeoutMilliseconds);
            }
            else if (_camerasWithoutHls.Contains(source.Key))
            {
                // Облачная камера без RTSP: HLS не отдаёт, сразу MJPEG/снимки.
                ShowStatus("Подключение...");
                _ = RunMjpegStreamAsync(source.Key);
            }
            else
            {
                // Живой поток пробуем так: сначала HLS (его отдаёт camera/stream), и только
                // если он так и не отдал кадр после нескольких перезапусков — MJPEG, а в самом
                // крайнем случае снимки. Атрибут frontend_stream_type у камер не всегда
                // заполнен, так что полагаться на него нельзя.
                ShowStatus("Подключение...");
                // Первая попытка ждёт кадр дольше всех: в ней и JIT-компиляция медиа-классов,
                // и первый запуск ffmpeg в Home Assistant. Повторные попытки идут с меньшим
                // ожиданием (см. LiveRetryFirstFrameTimeoutMilliseconds) — путь уже прогрет.
                _ = RunLiveStreamAsync(LiveFirstFrameTimeoutMilliseconds);
            }
        }

        private void StopMjpegStream()
        {
            _mjpegStreamCts?.Cancel();
            _mjpegStreamCts?.Dispose();
            _mjpegStreamCts = null;
        }

        /// <summary>
        /// Живой поток текущей камеры. Для камеры из Home Assistant адрес плейлиста получается
        /// по WebSocket (camera/stream), для прямой IP-камеры — это её адрес RTSP. Отдаёт адрес
        /// ExoPlayer и оставляет играть по кругу. Если поток не поднялся или оборвался, страница
        /// не сдаётся сразу, а перезапускает поток на этой же камере — сбой обычно мимолётный.
        /// </summary>
        private async Task RunLiveStreamAsync(int firstFrameTimeoutMilliseconds)
        {
            string cameraKey = _currentCamera.Key;

            // Началась новая попытка: перезапуск, отложенный прошлым отказом, уже не в работе.
            _isRestartingLive = false;

            // Запоминаем номер этой попытки: отставший сторожевой таймер прошлой попытки
            // должен её не перезапускать, а молча уйти.
            int attemptGeneration = ++_liveAttemptGeneration;

            string streamUrl;
            try
            {
                streamUrl = _currentCamera.Kind == CameraKind.Rtsp
                    ? _currentCamera.RtspUrl ?? string.Empty
                    : await _client.GetCameraStreamUrlAsync(_currentCamera.Key).ConfigureAwait(true);
            }
            catch (PhotoSourceException streamFailure)
            {
                FrameLog.Warn($"{cameraKey}: поток не открылся ({streamFailure.Message})");
                TryRestartLiveStream();
                return;
            }

            // Пока запрашивали адрес, переключили камеру или ушли со страницы.
            if (_currentEntityId != cameraKey)
            {
                return;
            }

            var firstFrame = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _firstFrameTcs = firstFrame;

            // Сначала показываем вид, чтобы у него появился платформенный обработчик,
            // и только потом отдаём адрес: иначе SourcePath был бы записан раньше,
            // чем ExoPlayer успел бы его принять.
            LiveVideoPlayer.IsVisible = true;
            LiveVideoPlayer.SourcePath = streamUrl;
            LiveVideoPlayer.IsLooping = true;
            LiveVideoPlayer.IsMuted = true;
            LiveVideoPlayer.Play();
            StatusLabel.IsVisible = false;

            // Ждём первый кадр. Если его нет — поток живой картинки не даёт — перезапускаем,
            // а не сдаёмся: сбой мог быть кратким.
            Task completed = await Task.WhenAny(
                firstFrame.Task, Task.Delay(firstFrameTimeoutMilliseconds)).ConfigureAwait(true);

            if (completed != firstFrame.Task
                && _currentEntityId == cameraKey
                && attemptGeneration == _liveAttemptGeneration)
            {
                FrameLog.Warn($"{cameraKey}: поток не отдал кадр, перезапускаем");
                TryRestartLiveStream();
            }
        }

        /// <summary>
        /// Перезапускает живой поток либо, когда попытки кончились, уходит на резерв.
        /// </summary>
        /// <remarks>
        /// Вызывается при любом отказе живой попытки: не пришёл адрес, не пришёл первый кадр
        /// либо проигрыватель упал на середине. До определённого числа попыток поток поднимается
        /// заново с короткой паузой (см. <see cref="MaxLiveAttempts"/>); после них — резерв:
        /// для камеры Home Assistant это MJPEG, а тот сам уйдёт на снимки; для прямой IP-камеры
        /// рамка продолжает пробовать её RTSP, только реже.
        /// </remarks>
        private void TryRestartLiveStream()
        {
            string cameraKey = _currentCamera.Key;

            if (_currentEntityId != cameraKey || _isRestartingLive)
            {
                // Другая камера, ушли со страницы или перезапуск уже назначен — не наслаиваемся.
                return;
            }

            _isRestartingLive = true;

            // Освобождаем предыдущий поток и прячем видео, пока подключаемся заново.
            StopLiveStream();

            if (_liveAttemptCount++ < MaxLiveAttempts)
            {
                ShowStatus("Переподключение к камере...");
                // Чтобы экран не гас в чёрное на время переподключения, показываем свежий
                // снимок камеры — он останется, пока новый поток не даст первый кадр. Для
                // прямой IP-камеры снимка в Home Assistant нет, поэтому обходимся надписью.
                if (_currentCamera.Kind == CameraKind.HomeAssistant)
                {
                    _ = ShowStillDuringReconnectAsync(cameraKey);
                }

                _ = RestartLiveAfterDelayAsync();
            }
            else
            {
                _isRestartingLive = false;
                GiveUpLiveStream();
            }
        }

        /// <summary>Что делать, когда попытки поднять живой поток кончились.</summary>
        private void GiveUpLiveStream()
        {
            string cameraKey = _currentCamera.Key;

            if (_currentCamera.Kind == CameraKind.Rtsp)
            {
                // У прямой камеры резерва нет: показываем надпись и продолжаем пробовать её же,
                // но не так часто, чтобы не крутить подключение вхолостую.
                ShowStatus("Камера не отвечает, пробую снова...");
                _ = RtspRetryAfterGiveUpAsync(cameraKey);
            }
            else
            {
                _ = RunMjpegStreamAsync(cameraKey);
            }
        }

        /// <summary>Продолжает пробовать прямой RTSP-поток после того, как попытки кончились.</summary>
        /// <remarks>
        /// IP-камера могла выключиться и только что включиться, а Home Assistant ей в этом не
        /// помощник — рамка сама возвращается к ней по кругу, пока камера не оживёт.
        /// </remarks>
        private async Task RtspRetryAfterGiveUpAsync(string cameraKey)
        {
            await Task.Delay(RtspGiveUpRetryDelayMilliseconds).ConfigureAwait(true);

            // Пока ждали, могли переключить камеру или уйти со страницы — тогда не трогаем.
            if (_currentCamera.Kind == CameraKind.Rtsp
                && _currentCamera.Key == cameraKey
                && _currentEntityId == cameraKey)
            {
                _liveAttemptCount = 0;
                _ = RunLiveStreamAsync(LiveRetryFirstFrameTimeoutMilliseconds);
            }
        }

        /// <summary>
        /// Показывает свежий снимок камеры Home Assistant, пока живой поток переподключается:
        /// без него экран гас в чёрное на всё время переподключения, а камера начинает «мигать».
        /// </summary>
        /// <remarks>
        /// Снимок берётся по тому же лёгкому запросу <c>camera_proxy</c>, которым рамка
        /// пользуется как резервом: он идёт напрямую к камере и не зависит от того, поднялся
        /// перекодированный поток. Если снимок сейчас тоже недоступен, остаёмся на надписи
        /// о переподключении. Живой первый кадр нового потока снимок прячет
        /// (см. <see cref="OnLiveVideoFirstFrame"/>).
        /// </remarks>
        private async Task ShowStillDuringReconnectAsync(string entityId)
        {
            byte[] jpegBytes;
            try
            {
                jpegBytes = await _client.GetCameraSnapshotAsync(entityId).ConfigureAwait(true);
            }
            catch (PhotoSourceException)
            {
                // Снимок тоже не отдаётся — ничего не меняем, страница остаётся на надписи.
                return;
            }

            // Пока снимок ехал, могли переключить камеру или поток уже поднялся.
            if (_currentEntityId != entityId || _isLiveVideoShowing)
            {
                return;
            }

            SnapshotImageTop.Source = null;
            SnapshotImageTop.IsVisible = false;
            SnapshotImageTop.Opacity = 0;
            SnapshotImage.Source = ImageSource.FromStream(() => new MemoryStream(jpegBytes));
            SnapshotImage.IsVisible = true;
            SnapshotImage.Opacity = 1;
            // Снимок показан на нижнем слое — это текущий видимый слой для резервного
            // перехода, если поток так и не поднялся и рамка уйдёт на снимки по таймеру.
            _snapshotIntoTopLayer = false;
            StatusLabel.IsVisible = false;
        }

        /// <summary>
        /// Ждёт паузу и поднимает поток заново. Пауза нужна, чтобы не крутить переподключение
        /// вхолостую: чуть присевший источник за это время успевает отпустить занятый поток.
        /// </summary>
        private async Task RestartLiveAfterDelayAsync()
        {
            string cameraKey = _currentCamera.Key;
            await Task.Delay(LiveRetryDelayMilliseconds).ConfigureAwait(true);

            // Пока ждали, могли переключить камеру или уйти со страницы — тогда не трогаем.
            if (_currentEntityId == cameraKey)
            {
                _ = RunLiveStreamAsync(LiveRetryFirstFrameTimeoutMilliseconds);
            }
        }

        /// <summary>
        /// Останавливает живой поток. Начинается всё с того же рукопожатия, что и у клипов,
        /// — слои угасают и отдают декодер, а страница прячет видео.
        /// </summary>
        private void StopLiveStream()
        {
            // Устаревшая попытка не должна реагировать на первый кадр от прошлой камеры.
            _firstFrameTcs = null;
            // Живая картинка больше не идёт: снимок, показанный на время переподключения,
            // снова пригодится (его спрячет первый кадр нового потока).
            _isLiveVideoShowing = false;
            // Одного SourcePath=null достаточно: он сам гасит и пересоздаёт слои.
            // Лишний Stop() сверху даёт двойной teardown — лишние Init/Release декодера,
            // из-за которых повторное открытие ловит отказ переинициализации (-1010).
            LiveVideoPlayer.SourcePath = null;
            LiveVideoPlayer.IsVisible = false;
        }

        /// <summary>
        /// ExoPlayer не смог ни открыть, ни доиграть поток. Пробуем поднять его заново:
        /// обрыв на середине часто тоже мимолётный. Лишь когда попытки кончились, уходим
        /// на резерв.
        /// </summary>
        private void OnLiveVideoFailed(object? sender, EventArgs e)
        {
            // Уже на MJPEG — не перезапускаем его повторным фолбэком.
            if (_currentEntityId is string cameraKey && _mjpegStreamCts is null)
            {
                FrameLog.Warn($"{cameraKey}: поток оборвался, перезапускаем");
                TryRestartLiveStream();
            }
        }

        /// <summary>
        /// Пришёл первый кадр — снимаем ожидание: поток живой, оставляем играть.
        /// Заодно прячем снимок, показанный на время переподключения: живая картинка
        /// идёт на верхнем слое и накрывает его, а у AspectFit он просвечивал бы в полосах.
        /// </summary>
        private void OnLiveVideoFirstFrame(object? sender, EventArgs e)
        {
            _isLiveVideoShowing = true;

            SnapshotImage.IsVisible = false;
            SnapshotImage.Opacity = 0;
            SnapshotImageTop.IsVisible = false;
            SnapshotImageTop.Opacity = 0;

            _firstFrameTcs?.TrySetResult(true);
        }

        /// <summary>
        /// Держит соединение открытым и показывает кадры по мере прихода. Если поток не
        /// открылся или обрывается на середине, страница сама переходит на резерв.
        /// </summary>
        private async Task RunMjpegStreamAsync(string entityId)
        {
            var cts = new CancellationTokenSource();
            _mjpegStreamCts = cts;

            // Часы на первый кадр: без этого зависший обмен (заголовки пришли, а кадра
            // от источника нет) держал бы страницу на «Подключение...» бесконечно —
            // ни ошибки, ни следа в журнале, только вечная надпись. Дальше, пока кадры
            // идут, ограничение не нужно — снимаем его после первого же кадра.
            cts.CancelAfter(MjpegFirstFrameTimeoutMilliseconds);
            bool receivedFirstFrame = false;

            try
            {
                await foreach (byte[] jpegBytes in _client
                    .StreamCameraMjpegFramesAsync(entityId, cts.Token).ConfigureAwait(true))
                {
                    // Пока кадр ждали, могли переключить камеру, уйти со страницы или
                    // уже перейти на резерв — устаревший кадр показывать не нужно.
                    if (_mjpegStreamCts != cts)
                    {
                        return;
                    }

                    if (!receivedFirstFrame)
                    {
                        receivedFirstFrame = true;
                        cts.CancelAfter(Timeout.InfiniteTimeSpan);
                        FrameLog.Info($"{entityId}: поток камеры открылся");
                    }

                    ShowMjpegFrame(jpegBytes);
                }

                // Цикл закончился сам — источник закрыл соединение, а не мы его остановили.
                if (_mjpegStreamCts == cts)
                {
                    FrameLog.Warn($"{entityId}: поток камеры закрылся, переходим на снимки");
                    StartSnapshotPolling(entityId);
                }
            }
            catch (OperationCanceledException) when (!receivedFirstFrame)
            {
                // Часы на первый кадр вышли, а не мы сами остановили поток.
                if (_mjpegStreamCts == cts)
                {
                    FrameLog.Warn($"{entityId}: поток камеры не ответил вовремя, переходим на снимки");
                    StartSnapshotPolling(entityId);
                }
            }
            catch (OperationCanceledException)
            {
                // Ушли со страницы, переключили камеру или сами остановили поток — не ошибка.
            }
            catch (PhotoSourceException streamFailure)
            {
                if (_mjpegStreamCts == cts)
                {
                    FrameLog.Warn(
                        $"{entityId}: поток камеры не открылся ({streamFailure.Message}), "
                        + "переходим на снимки");
                    StartSnapshotPolling(entityId);
                }
            }
            finally
            {
                if (_mjpegStreamCts == cts)
                {
                    _mjpegStreamCts = null;
                }

                cts.Dispose();
            }
        }

        /// <summary>
        /// Показывает кадр живого потока без перехода: кадры и так сменяют друг друга
        /// часто, а затухание на каждый только смазывало бы картинку.
        /// </summary>
        private void ShowMjpegFrame(byte[] jpegBytes)
        {
            SnapshotImage.Source = ImageSource.FromStream(() => new MemoryStream(jpegBytes));
            SnapshotImage.Opacity = 1;
            SnapshotImage.IsVisible = true;
            SnapshotImageTop.Opacity = 0;
            StatusLabel.IsVisible = false;
        }

        private void StartSnapshotPolling(string entityId)
        {
            StopMjpegStream();
            StopLiveStream();
            ShowStatus("Загрузка снимка...");

            // Слои снимка от предыдущей камеры не нужны: первый кадр новой камеры
            // сам проступит поверх них через обычный переход.
            _isRefreshingSnapshot = false;

            _snapshotEntityId = entityId;
            _snapshotTimer.Start();

            // Не ждать первого тика таймера — первый снимок нужен сразу.
            _ = RefreshSnapshotAsync(entityId);
        }

        private void StopSnapshotPolling()
        {
            _snapshotTimer.Stop();
            _snapshotEntityId = null;
        }

        private async Task RefreshSnapshotAsync(string entityId)
        {
            // Пока снимок ждали, могли переключить камеру или уйти со страницы —
            // устаревший ответ показывать не нужно.
            if (_snapshotEntityId != entityId)
            {
                return;
            }

            // Предыдущий запрос этой же камеры ещё не завершился: ответ Home Assistant
            // иногда занимает больше периода таймера, и без этой проверки более
            // медленный старый снимок мог прийти позже уже показанного нового.
            if (_isRefreshingSnapshot)
            {
                return;
            }

            _isRefreshingSnapshot = true;
            try
            {
                byte[] jpegBytes = await _client.GetCameraSnapshotAsync(entityId).ConfigureAwait(true);

                if (_snapshotEntityId != entityId)
                {
                    return;
                }

                await ShowSnapshotAsync(jpegBytes).ConfigureAwait(true);
            }
            catch (PhotoSourceException snapshotFailure)
            {
                if (_snapshotEntityId == entityId)
                {
                    ShowStatus($"{entityId}: {snapshotFailure.Message}");
                }
            }
            finally
            {
                _isRefreshingSnapshot = false;
            }
        }

        /// <summary>
        /// Выводит новый снимок плавным переходом, как и слайд-шоу на главном экране.
        /// </summary>
        /// <remarks>
        /// Кадр пишется в скрытый слой и проступает поверх видимого: подмена картинки
        /// скачком на резервных, редко обновляемых снимках выглядела бы рябью.
        /// </remarks>
        private async Task ShowSnapshotAsync(byte[] jpegBytes)
        {
            bool intoTopLayer = !_snapshotIntoTopLayer;
            Image targetLayer = intoTopLayer ? SnapshotImageTop : SnapshotImage;
            Image otherLayer = intoTopLayer ? SnapshotImage : SnapshotImageTop;

            targetLayer.Source = ImageSource.FromStream(() => new MemoryStream(jpegBytes));
            targetLayer.Opacity = 0;
            targetLayer.IsVisible = true;
            _snapshotIntoTopLayer = intoTopLayer;

            StatusLabel.IsVisible = false;

            await targetLayer.FadeToAsync(1, SnapshotCrossfadeMilliseconds).ConfigureAwait(true);

            // Прежний слой убираем не сразу, а после перехода: пока оба видны,
            // старый снимок и просвечивает через новый, давая тот самый переход.
            otherLayer.Opacity = 0;
        }

        private void OnSnapshotTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            string? entityId = _snapshotEntityId;
            if (entityId is null)
            {
                return;
            }

            MainThread.BeginInvokeOnMainThread(() => _ = RefreshSnapshotAsync(entityId));
        }

        private void ShowStatus(string text)
        {
            StatusLabel.Text = text;
            StatusLabel.IsVisible = true;
        }

        private void OnNextCameraClicked(object? sender, EventArgs e)
        {
            if (_cameras.Count == 0)
            {
                return;
            }

            _cameraIndex = (_cameraIndex + 1) % _cameras.Count;
            PlayCurrentCamera();
        }

        private async void OnBackClicked(object? sender, EventArgs e)
        {
            await Shell.Current.GoToAsync("..");
        }
    }
}
