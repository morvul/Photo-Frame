using Android.Views;
using Android.Widget;
using AndroidX.Media3.Common;
using AndroidX.Media3.DataSource;
using AndroidX.Media3.ExoPlayer;
using AndroidX.Media3.ExoPlayer.Source;
using AndroidX.Media3.UI;
using Microsoft.Maui.Handlers;

namespace PhotoFrame
{
    /// <summary>
    /// Платформенная часть <see cref="VideoPlayerView"/> на основе ExoPlayer.
    /// </summary>
    /// <remarks>
    /// ExoPlayer идёт к декодерам через MediaCodec и сам выбирает дорожку — в клипах
    /// живых фото их две, 720p и 1080p. HEVC рамка декодирует аппаратно
    /// (OMX.rk.video_decoder.hevc, до 1920x1088) — ровно под клипы альбома.
    ///
    /// Проигрывателей два, и они меняются местами: клип готовится на невидимом слое,
    /// а когда у него появился первый кадр, слои перетекают друг в друга. С одним
    /// проигрывателем поверхность переставлялась бы под новый размер кадра, и переход
    /// между вертикальным и горизонтальным клипом был бы виден.
    ///
    /// VP9 недостижим: единственный декодер на него программный и ограничен 720x480,
    /// а клипы альбома — 1080p. Такие кадры страница отмечает и показывает снимком.
    ///
    /// Отыгравший слой не просто останавливается, а пересоздаётся целиком: после Stop
    /// и ClearMediaItems проигрыватель держит декодер за собой и отдаёт ему следующий
    /// клип, пока очередь вендорного декодера не переполнится. Свежий экземпляр на
    /// каждый клип этот путь исключает: декодер честно отдаётся системе.
    /// </remarks>
    public class VideoPlayerViewHandler : ViewHandler<VideoPlayerView, FrameLayout>
    {
        public static readonly IPropertyMapper<VideoPlayerView, VideoPlayerViewHandler> Mapper =
            new PropertyMapper<VideoPlayerView, VideoPlayerViewHandler>(ViewMapper)
            {
                [nameof(VideoPlayerView.SourcePath)] = MapSourcePath,
                [nameof(VideoPlayerView.IsLooping)] = MapIsLooping,
                [nameof(VideoPlayerView.IsMuted)] = MapIsMuted,
            };

        public static readonly CommandMapper<VideoPlayerView, VideoPlayerViewHandler> Commands =
            new(ViewCommandMapper)
            {
                [nameof(VideoPlayerView.Play)] = MapPlay,
                [nameof(VideoPlayerView.Pause)] = MapPause,
                [nameof(VideoPlayerView.Stop)] = MapStop,
            };

        /// <summary>
        /// Значения из androidx.media3.common.Player. В привязке они лежат так, что
        /// добраться до них из C# не удаётся, а числа эти в Media3 фиксированы.
        /// </summary>
        private const int RepeatModeOff = 0;
        private const int RepeatModeOne = 1;
        private const int PlaybackStateEnded = 4;

        /// <summary>
        /// Сколько длится перетекание. Четверть секунды: заметно как смена кадра,
        /// а не как рывок, и не растягивает начало короткого клипа живого фото.
        /// </summary>
        private const long CrossfadeMilliseconds = 250;

        /// <summary>
        /// Таймаут HTTP-запросов для сетевого источника (поток камеры Home Assistant).
        /// </summary>
        /// <remarks>
        /// Восемь секунд, зашитых в ExoPlayer по умолчанию, не хватает на первый запрос
        /// плейлиста: Home Assistant в этот момент только запускает ffmpeg и ждёт первый
        /// кадр от камеры. На локальные клипы не влияет — для файлов HTTP не используется.
        /// </remarks>
        private const int NetworkConnectTimeoutMilliseconds = 20000;

        /// <summary>Сколько ждать данные сетевого источника между пакетами.</summary>
        /// <remarks>
        /// Больше, чем у соединения, нарочно: живой HLS-поток в Home Assistant пересоздаёт
        /// ffmpeg, и пока он перезапускается, сегменты не приходят вовсе. Без такого запаса
        /// поток падал бы и рамка уходила в переподключение, хотя камера жива и через
        /// полминуты снова отдаёт кадры.
        /// </remarks>
        private const int NetworkReadTimeoutMilliseconds = 45000;

        /// <summary>
        /// Буфер воспроизведения для живого потока камеры.
        /// </summary>
        /// <remarks>
        /// Значения по умолчанию ExoPlayer (2,5 с и 5 с) делают живой поток рваным: едва
        /// набрав буфер, проигрыватель стартует, тут же спотыкается на короткой паузе и
        /// снова ждёт. У живого HLS буфер и так ограничен окном плейлиста, поэтому большие
        /// min/max не съедают память, а вот «стартовать после более плотного буфера» и
        /// «после сбоя добираться дольше» заметно снижают число подёргиваний и переподключений.
        /// </remarks>
        private const int LiveMinBufferMilliseconds = 50000;
        private const int LiveMaxBufferMilliseconds = 60000;
        private const int LiveBufferForPlaybackMilliseconds = 2000;
        private const int LiveBufferForPlaybackAfterRebufferMilliseconds = 10000;

        private readonly Layer[] _layers = new Layer[2];

        /// <summary>Контейнер нужен, чтобы пересоздавать слой на том же виде.</summary>
        private FrameLayout? _container;

        /// <summary>Слой, который сейчас на виду.</summary>
        private int _frontIndex;

        public VideoPlayerViewHandler() : base(Mapper, Commands)
        {
        }

        private Layer Front => _layers[_frontIndex];

        private Layer Back => _layers[1 - _frontIndex];

        protected override FrameLayout CreatePlatformView()
        {
            var container = (FrameLayout)LayoutInflater.From(Context)!
                .Inflate(Resource.Layout.video_player, null)!;

            _container = container;

            _layers[0] = CreateLayer(container.FindViewById<PlayerView>(Resource.Id.player_back)!, 0);
            _layers[1] = CreateLayer(container.FindViewById<PlayerView>(Resource.Id.player_front)!, 1);

            return container;
        }

        /// <summary>
        /// Отдаёт декодер и заводит на том же виде новый проигрыватель.
        /// </summary>
        /// <remarks>
        /// Вызывается, когда слой отыграл и скрылся: к следующему клипу он должен быть
        /// чистым. Release освобождает MediaCodec немедленно, а не когда до объекта
        /// доберётся сборщик, — на этом и держится весь смысл.
        /// </remarks>
        private void RecycleLayer(int layerIndex)
        {
            Layer? layer = _layers[layerIndex];
            if (layer is null || _container is null)
            {
                return;
            }

            try
            {
                layer.View.Player = null;
                layer.Player.RemoveListener(layer.Listener);
                layer.Player.Release();
                layer.Listener.Dispose();
            }
            catch (Java.Lang.Throwable releaseFailure)
            {
                // Даже если освободить не удалось, новый экземпляр всё равно нужен:
                // играть на сломанном смысла нет.
                FrameLog.Warn($"Проигрыватель не освобождён: {releaseFailure.Message}");
            }

            _layers[layerIndex] = CreateLayer(layer.View, layerIndex);
        }

        private Layer CreateLayer(PlayerView view, int index)
        {
            // Свой сетевой источник с увеличенным таймаутом: HLS-плейлист камеры
            // Home Assistant готовится первым запросом (ffmpeg ещё только запускается),
            // и восьми секунд ExoPlayer по умолчанию не хватает. Локальные клипы идут
            // по file:// и этот источник не затрагивают.
            var httpDataSourceFactory = new DefaultHttpDataSource.Factory()
                .SetConnectTimeoutMs(NetworkConnectTimeoutMilliseconds)
                .SetReadTimeoutMs(NetworkReadTimeoutMilliseconds);

            var dataSourceFactory = new DefaultDataSource.Factory(Context, httpDataSourceFactory);

            var mediaSourceFactory = new DefaultMediaSourceFactory(Context)!
                .SetDataSourceFactory(dataSourceFactory);

            // Свой буфер для живого потока: у живой HLS-картинки рамка должна держать
            // плотный буфер и не подёргиваться на коротких паузах источника. Локальные
            // клипы идут по file:// и на это не влияют.
            var loadControl = new DefaultLoadControl.Builder()
                .SetBufferDurationsMs(
                    LiveMinBufferMilliseconds,
                    LiveMaxBufferMilliseconds,
                    LiveBufferForPlaybackMilliseconds,
                    LiveBufferForPlaybackAfterRebufferMilliseconds)
                .Build();

            IExoPlayer player = new ExoPlayerBuilder(Context)
                .SetMediaSourceFactory(mediaSourceFactory)
                .SetLoadControl(loadControl)
                .Build()!;
            var listener = new PlaybackListener(this, index);

            player.AddListener(listener);
            view.Player = player;

            // Кадр вписывается целиком либо обрезается по краям — так же, как снимок:
            // клип живого фото должен совпадать с кадром, из которого он вырастает.
            view.ResizeMode = FrameSettings.FillScreen
                ? AspectRatioFrameLayout.ResizeModeZoom
                : AspectRatioFrameLayout.ResizeModeFit;

            return new Layer(view, player, listener);
        }

        protected override void DisconnectHandler(FrameLayout platformView)
        {
            foreach (Layer layer in _layers)
            {
                if (layer is null)
                {
                    continue;
                }

                layer.View.Player = null;
                layer.Player.RemoveListener(layer.Listener);
                layer.Player.Release();
                layer.Listener.Dispose();
            }

            base.DisconnectHandler(platformView);
        }

        /// <summary>
        /// Текущая позиция и длительность в миллисекундах.
        /// </summary>
        /// <remarks>
        /// У проигрывателя нет события о продвижении, поэтому значения опрашиваются.
        /// Пока файл не подготовлен, длительность неизвестна — отдаём нули.
        /// </remarks>
        internal (int PositionMilliseconds, int DurationMilliseconds) QueryProgress()
        {
            try
            {
                IExoPlayer? player = Front?.Player;
                if (player is null)
                {
                    return (0, 0);
                }

                long duration = player.Duration;
                return duration <= 0 ? (0, 0) : ((int)player.CurrentPosition, (int)duration);
            }
            catch (Java.Lang.Throwable)
            {
                return (0, 0);
            }
        }

        /// <summary>
        /// Меняет слои местами: новый проявляется, прежний угасает.
        /// </summary>
        /// <remarks>
        /// Прежний слой останавливается только после перетекания — иначе он погас бы
        /// раньше, чем новый проступил, и между кадрами мелькала бы чернота.
        /// </remarks>
        private void SwapLayers(int newFrontIndex)
        {
            if (newFrontIndex == _frontIndex)
            {
                return;
            }

            Layer incoming = _layers[newFrontIndex];
            Layer outgoing = _layers[_frontIndex];

            _frontIndex = newFrontIndex;

            incoming.View.Animate()!.Alpha(1f)!.SetDuration(CrossfadeMilliseconds)!.Start();
            outgoing.View.Animate()!
                .Alpha(0f)!
                .SetDuration(CrossfadeMilliseconds)!
                .WithEndAction(new Java.Lang.Runnable(() =>
                {
                    outgoing.Player.Stop();
                    outgoing.Player.ClearMediaItems();

                    // Слой ушёл с виду — отдаём его декодер. Индекс, а не ссылка:
                    // за четверть секунды перетекания слои могли поменяться снова.
                    RecycleLayer(1 - _frontIndex);
                }))!
                .Start();
        }

        /// <summary>Плавно убирает видео, открывая снимок под ним.</summary>
        private void FadeOutEverything()
        {
            for (int layerIndex = 0; layerIndex < _layers.Length; layerIndex++)
            {
                Layer captured = _layers[layerIndex];
                int capturedIndex = layerIndex;

                captured.View.Animate()!
                    .Alpha(0f)!
                    .SetDuration(CrossfadeMilliseconds)!
                    .WithEndAction(new Java.Lang.Runnable(() =>
                    {
                        captured.Player.Stop();
                        captured.Player.ClearMediaItems();
                        RecycleLayer(capturedIndex);
                    }))!
                    .Start();
            }
        }

        private void OnFirstFrame(int layerIndex)
        {
            SwapLayers(layerIndex);
            VirtualView?.RaiseFirstFrameRendered();
        }

        private void OnEnded(int layerIndex)
        {
            // Доиграл именно тот слой, что на виду, — иначе это отголосок прошлого клипа.
            if (layerIndex == _frontIndex)
            {
                VirtualView?.RaisePlaybackFinished();
            }
        }

        private void OnFailed(int layerIndex, string reason)
        {
            FrameLog.Warn($"Видео не воспроизведено: {reason}");
            VirtualView?.RaisePlaybackFailed();
        }

        private static void MapSourcePath(VideoPlayerViewHandler handler, VideoPlayerView view)
        {
            if (handler._layers[0] is null)
            {
                return;
            }

            if (string.IsNullOrEmpty(view.SourcePath))
            {
                handler.FadeOutEverything();
                return;
            }

            // Готовим на невидимом слое: на виду пока прежний кадр или сам снимок.
            Layer target = handler.Back;

            target.Player.SetMediaItem(MediaItem.FromUri(ResolveMediaUri(view.SourcePath!)));
            target.Player.RepeatMode = view.IsLooping ? RepeatModeOne : RepeatModeOff;
            target.Player.Volume = view.IsMuted ? 0f : 1f;
            target.Player.Prepare();
        }

        /// <summary>
        /// Локальный клип оборачивается в file://, а сетевой адрес отдаётся как есть:
        /// ExoPlayer сам открывает HTTP(S) без лишних заголовков (ссылка на HLS камеры
        /// Home Assistant уже несёт токен доступа в собственном пути), а rtsp:// уводит
        /// на прямой RTSP-источник IP-камеры. Любая схема с «://» — сетевая, иначе файл.
        /// </summary>
        private static Android.Net.Uri ResolveMediaUri(string path)
        {
            if (path.Contains("://", StringComparison.Ordinal))
            {
                return Android.Net.Uri.Parse(path)!;
            }

            using var videoFile = new Java.IO.File(path);
            return Android.Net.Uri.FromFile(videoFile)!;
        }

        private static void MapIsLooping(VideoPlayerViewHandler handler, VideoPlayerView view)
        {
            foreach (Layer layer in handler._layers)
            {
                if (layer is not null)
                {
                    layer.Player.RepeatMode = view.IsLooping ? RepeatModeOne : RepeatModeOff;
                }
            }
        }

        private static void MapIsMuted(VideoPlayerViewHandler handler, VideoPlayerView view)
        {
            foreach (Layer layer in handler._layers)
            {
                if (layer is not null)
                {
                    layer.Player.Volume = view.IsMuted ? 0f : 1f;
                }
            }
        }

        /// <summary>
        /// Играть просят оба слоя: тот, что готовится, должен начать рисовать, иначе
        /// первого кадра не дождаться, а тот, что на виду, мог стоять на паузе.
        /// </summary>
        private static void MapPlay(
            VideoPlayerViewHandler handler, VideoPlayerView view, object? args)
        {
            handler.Back?.Player.Play();
            handler.Front?.Player.Play();
        }

        private static void MapPause(
            VideoPlayerViewHandler handler, VideoPlayerView view, object? args) =>
            handler.Front?.Player.Pause();

        private static void MapStop(
            VideoPlayerViewHandler handler, VideoPlayerView view, object? args) =>
            handler.FadeOutEverything();

        /// <summary>Проигрыватель со своим видом и подпиской.</summary>
        private sealed record Layer(PlayerView View, IExoPlayer Player, PlaybackListener Listener);

        /// <summary>
        /// Слушатель одного слоя: сообщает о первом кадре, окончании и отказе.
        /// </summary>
        private sealed class PlaybackListener : Java.Lang.Object, IPlayerListener
        {
            private readonly VideoPlayerViewHandler _handler;
            private readonly int _layerIndex;

            public PlaybackListener(VideoPlayerViewHandler handler, int layerIndex)
            {
                _handler = handler;
                _layerIndex = layerIndex;
            }

            public void OnRenderedFirstFrame() => _handler.OnFirstFrame(_layerIndex);

            public void OnPlaybackStateChanged(int playbackState)
            {
                if (playbackState == PlaybackStateEnded)
                {
                    _handler.OnEnded(_layerIndex);
                }
            }

            public void OnPlayerError(PlaybackException? error) =>
                _handler.OnFailed(_layerIndex, $"{error?.ErrorCodeName}: {error?.Message}");
        }
    }
}
