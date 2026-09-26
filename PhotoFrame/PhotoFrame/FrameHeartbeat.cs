using System;
using System.Globalization;
using System.IO;
using System.Timers;

// Path и Environment есть и в Android.*, и в System.*: нужны однозначные псевдонимы.
using IoPath = System.IO.Path;

namespace PhotoFrame
{
    /// <summary>Что рамка делала в момент записи.</summary>
    /// <param name="IsNightMode">Показаны ночные часы, а не снимок.</param>
    /// <param name="PhotoNumber">Номер показанного кадра, начиная с единицы; 0 — нет кадра.</param>
    /// <param name="PhotoCount">Всего кадров в показе.</param>
    /// <param name="NightFramesRendered">Сколько кадров часов отрисовано за сеанс.</param>
    /// <param name="PlayingClip">Имя проигрываемого клипа либо null.</param>
    public readonly record struct HeartbeatState(
        bool IsNightMode,
        int PhotoNumber,
        int PhotoCount,
        int NightFramesRendered,
        string? PlayingClip);

    /// <summary>
    /// Раз в минуту записывает строку о состоянии рамки в файл.
    /// </summary>
    /// <remarks>
    /// Нужен потому, что рамка зависает по ночам, а системный журнал перезапуск
    /// не переживает: `logcat` хранит только текущую загрузку, /proc/last_kmsg и pstore
    /// на этой прошивке пусты, а persist.logd.logpersistd не создаёт своего каталога.
    /// После очередного зависания это единственное, по чему можно судить, что
    /// происходило перед ним.
    ///
    /// Пишется из фонового таймера, а не из UI-потока, и это само по себе полезно:
    /// если строки идут, а экран замер, значит встал именно UI-поток, а не устройство.
    ///
    /// Файл лежит в общей памяти, поэтому читается по USB без всякого adb.
    /// </remarks>
    internal static class FrameHeartbeat
    {
        private const string LogDirectoryName = "Logs";
        private const string LogFileName = "frame.log";
        private const string PreviousLogFileName = "frame.log.1";

        /// <summary>
        /// Предел файла перед перекладыванием. Строка занимает около 130 байт, так что
        /// 400 КБ — это примерно двое суток записей.
        /// </summary>
        private const long MaxLogBytes = 400 * 1024;

        /// <summary>Сколько дней хранить старые файлы журнала.</summary>
        private const int KeepLogDays = 7;

        private static readonly object WriteLock = new();
        private static System.Timers.Timer? _timer;
        private static Func<HeartbeatState>? _readState;

        /// <summary>Куда пишется журнал; определяется один раз при первой записи.</summary>
        private static string? _logDirectory;

        /// <summary>
        /// Общая память, если она доступна, иначе внешний каталог приложения.
        /// </summary>
        /// <remarks>
        /// В общей памяти файл виден с компьютера по USB и не требует adb — так удобнее.
        /// Но разрешение на запись выдаётся не сразу: на свежей рамке его ещё никто
        /// не спрашивал, и журнал молча не писался. Свой каталог приложения доступен
        /// всегда, и лучше писать туда, чем никуда.
        /// </remarks>
        private static string ResolveLogDirectory()
        {
            if (_logDirectory is not null)
            {
                return _logDirectory;
            }

            string? sharedStorageRoot =
                Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;

            if (!string.IsNullOrEmpty(sharedStorageRoot))
            {
                string sharedLogs = IoPath.Combine(sharedStorageRoot, "PhotoFrame", LogDirectoryName);

                try
                {
                    Directory.CreateDirectory(sharedLogs);
                    _logDirectory = sharedLogs;
                    return sharedLogs;
                }
                catch (Exception sharedFailure) when (
                    sharedFailure is IOException or UnauthorizedAccessException)
                {
                    FrameLog.Warn(
                        $"Журнал пишется в каталог приложения: {sharedFailure.Message}");
                }
            }

            string? appExternal = Android.App.Application.Context
                .GetExternalFilesDir(null)?.AbsolutePath;

            string fallback = IoPath.Combine(
                appExternal ?? Microsoft.Maui.Storage.FileSystem.AppDataDirectory, LogDirectoryName);

            Directory.CreateDirectory(fallback);
            _logDirectory = fallback;
            return fallback;
        }

        /// <summary>Начинает вести журнал. Вызывается один раз при запуске страницы.</summary>
        public static void Start(Func<HeartbeatState> readState)
        {
            if (_timer is not null)
            {
                return;
            }

            _readState = readState;

            RemoveExpiredLogs();
            Write("рамка запущена");

            _timer = new System.Timers.Timer(TimeSpan.FromMinutes(1).TotalMilliseconds)
            {
                AutoReset = true,
            };

            _timer.Elapsed += (_, _) => Write(note: null);
            _timer.Start();
        }

        /// <summary>Записывает строку состояния; note — повод, если он есть.</summary>
        public static void Write(string? note)
        {
            try
            {
                HeartbeatState state = _readState?.Invoke() ?? default;
                string line = BuildLine(state, note);

                lock (WriteLock)
                {
                    string logDirectory = ResolveLogDirectory();
                    RotateIfLarge(logDirectory);

                    // Дописываем построчно и сразу: зависшая рамка дописать уже не успеет.
                    File.AppendAllText(IoPath.Combine(logDirectory, LogFileName), line + "\n");
                }
            }
            catch (Exception writeFailure) when (
                writeFailure is IOException or UnauthorizedAccessException
                    or Java.Lang.Throwable)
            {
                // Журнал — вспомогательная вещь: из-за него рамка падать не должна.
                FrameLog.Warn($"Строка журнала не записана: {writeFailure.Message}");
            }
        }

        private static string BuildLine(HeartbeatState state, string? note)
        {
            long nativeBytes = Android.OS.Debug.NativeHeapAllocatedSize;
            long managedBytes = GC.GetTotalMemory(false);

            (long availableBytes, bool isLowMemory) = ReadDeviceMemory();
            long processCpuMilliseconds = Android.OS.Process.ElapsedCpuTime;

            string mode = state.IsNightMode ? "night" : "day";
            string slide = state.PhotoCount > 0
                ? $"{state.PhotoNumber}/{state.PhotoCount}"
                : "-";

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss}  {1,-5} {2,-9} managed {3,5:F1}MB  native {4,6:F1}MB  " +
                "free {5,5:F0}MB{6}  cpu {7,7:F1}s  nightframes {8,-5} clip {9}{10}",
                DateTime.Now,
                mode,
                slide,
                managedBytes / 1024d / 1024d,
                nativeBytes / 1024d / 1024d,
                availableBytes / 1024d / 1024d,
                isLowMemory ? " LOW" : "    ",
                processCpuMilliseconds / 1000d,
                state.NightFramesRendered,
                state.PlayingClip ?? "-",
                note is null ? string.Empty : "  << " + note);
        }

        private static (long AvailableBytes, bool IsLowMemory) ReadDeviceMemory()
        {
            try
            {
                var memoryInfo = new Android.App.ActivityManager.MemoryInfo();

                if (Android.App.Application.Context.GetSystemService(
                        Android.Content.Context.ActivityService) is Android.App.ActivityManager manager)
                {
                    manager.GetMemoryInfo(memoryInfo);
                    return (memoryInfo.AvailMem, memoryInfo.LowMemory);
                }
            }
            catch (Java.Lang.Throwable)
            {
                // Ниже вернём нули: строка всё равно полезна остальными полями.
            }

            return (0, false);
        }

        /// <summary>Переполненный файл становится предыдущим, а новый начинается пустым.</summary>
        private static void RotateIfLarge(string logDirectory)
        {
            string logPath = IoPath.Combine(logDirectory, LogFileName);

            if (!File.Exists(logPath) || new FileInfo(logPath).Length < MaxLogBytes)
            {
                return;
            }

            string previousPath = IoPath.Combine(logDirectory, PreviousLogFileName);
            File.Move(logPath, previousPath, overwrite: true);
        }

        /// <summary>
        /// Убирает залежавшиеся файлы журнала.
        /// </summary>
        /// <remarks>
        /// Рамка работает месяцами и никто за ней не следит, поэтому чистка своя:
        /// два файла по 400 КБ — это потолок, и всё, что старше недели, уходит.
        /// </remarks>
        private static void RemoveExpiredLogs()
        {
            try
            {
                string logDirectory = ResolveLogDirectory();
                DateTime expiredBefore = DateTime.UtcNow.AddDays(-KeepLogDays);

                foreach (string filePath in Directory.GetFiles(logDirectory, "frame.log*"))
                {
                    if (File.GetLastWriteTimeUtc(filePath) < expiredBefore)
                    {
                        File.Delete(filePath);
                    }
                }
            }
            catch (Exception cleanupFailure) when (
                cleanupFailure is IOException or UnauthorizedAccessException)
            {
                FrameLog.Warn($"Старые журналы не убраны: {cleanupFailure.Message}");
            }
        }
    }
}
