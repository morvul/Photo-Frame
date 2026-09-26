using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Path есть и в Android.*, и в System.IO: нужен однозначный псевдоним.
using IoPath = System.IO.Path;

namespace PhotoFrame
{
    /// <summary>
    /// Забирает клип Immich в момент, когда его слайд показан.
    /// </summary>
    /// <remarks>
    /// Устроено так же, как и для альбома Google, и по той же причине: клип весит
    /// десятки мегабайт, и тянуть все ради кадров, до которых показ может не дойти,
    /// незачем. В кэше сперва лежит только заставка, клип появляется рядом при показе
    /// и остаётся там — на следующем круге кадр играет сразу.
    ///
    /// Отличие от альбома одно: адрес клипа известен заранее (он выводится из
    /// идентификатора объекта), поэтому запрос ровно один, без разбора чужих страниц.
    /// </remarks>
    internal static class ImmichVideoCache
    {
        private const string SkipListFileName = "immich.videos.skip";

        /// <summary>Ключ доступа передаётся этим заголовком — так описано в API Immich.</summary>
        private const string ApiKeyHeaderName = "x-api-key";

        /// <summary>
        /// Предел на размер одного клипа: место на рамке не бесконечно, а размер
        /// известен из заголовков ещё до начала загрузки.
        /// </summary>
        private const long MaxVideoBytes = 200L * 1024 * 1024;

        /// <summary>Одновременно качаем один клип: показан всё равно один кадр.</summary>
        private static readonly SemaphoreSlim DownloadGate = new(1, 1);

        /// <summary>
        /// На сколько замолкаем после того, как сервер не ответил вовсе.
        /// </summary>
        /// <remarks>
        /// Таймаут на закачку — пять минут, ради перекодирования на слабом сервере.
        /// Но если сервер недоступен вообще (сеть, адрес, сам процесс лёг), ждать
        /// столько же для каждого следующего живого фото незачем: результат тот же,
        /// а слайд-шоу за это время успело бы показать десятки кадров.
        /// </remarks>
        private static readonly TimeSpan UnavailableBackoff = TimeSpan.FromMinutes(2);

        /// <summary>До какого момента не пытаемся качать клипы вовсе. UTC.</summary>
        private static DateTime _unavailableUntilUtc = DateTime.MinValue;

        /// <summary>Первые байты файла как текст — для журнала.</summary>
        private static string DescribeHead(string filePath)
        {
            try
            {
                var head = new byte[48];
                using FileStream file = File.OpenRead(filePath);
                int read = file.Read(head, 0, head.Length);

                var text = new StringBuilder(read);
                for (int index = 0; index < read; index++)
                {
                    byte value = head[index];
                    text.Append(value >= 32 && value < 127 ? (char)value : '.');
                }

                return text.ToString();
            }
            catch (Exception readFailure) when (
                readFailure is IOException or UnauthorizedAccessException)
            {
                return "не прочитать";
            }
        }

        private static string SkipListPath =>
            IoPath.Combine(ImmichPhotoSource.PhotoLibraryDirectory, SkipListFileName);

        /// <summary>Путь к уже скачанному годному клипу либо null.</summary>
        public static string? FindReadyVideo(string posterPath) => ClipStorage.FindReady(posterPath);

        /// <summary>
        /// Снимает паузу после недоступности сервера — сразу, не дожидаясь истечения.
        /// </summary>
        /// <remarks>
        /// Вызывается при сохранении адреса или ключа в настройках: пауза могла быть
        /// набрана по неверному адресу, и после правки ждать её остаток незачем —
        /// следующее живое фото должно пробовать сеть сразу же.
        /// </remarks>
        public static void ResetAvailability() => _unavailableUntilUtc = DateTime.MinValue;

        /// <summary>
        /// Помечает клип непроигрываемым: больше не качаем и не пробуем.
        /// </summary>
        /// <remarks>
        /// Даже перекодированный сервером клип рамка может не открыть — например, если
        /// перекодирование ещё не выполнено и отдан оригинал в HEVC 10 бит. Отметка
        /// избавляет от повторной загрузки десятков мегабайт на каждом круге показа,
        /// а сам кадр остаётся в слайд-шоу заставкой.
        /// </remarks>
        public static void MarkUnplayable(string posterPath)
        {
            string posterFileName = IoPath.GetFileName(posterPath);
            ClipStorage.TryDelete(ClipStorage.BuildClipPath(posterPath));

            var skipped = new List<string>(ReadSkipList());
            if (skipped.Contains(posterFileName, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            skipped.Add(posterFileName);
            FrameLog.Warn($"Клип Immich отмечен непроигрываемым: {posterFileName}");

            try
            {
                File.WriteAllLines(SkipListPath, skipped);
            }
            catch (Exception writeFailure) when (
                writeFailure is IOException or UnauthorizedAccessException)
            {
                FrameLog.Warn($"Список непроигрываемых не сохранён: {writeFailure.Message}");
            }
        }

        /// <summary>True, если этот кадр уже пытались проиграть и не смогли.</summary>
        public static bool IsUnplayable(string posterPath)
        {
            string posterFileName = IoPath.GetFileName(posterPath);

            foreach (string line in ReadSkipList())
            {
                if (line.Trim().Equals(posterFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string[] ReadSkipList()
        {
            try
            {
                return File.Exists(SkipListPath)
                    ? File.ReadAllLines(SkipListPath)
                    : Array.Empty<string>();
            }
            catch (Exception readFailure) when (
                readFailure is IOException or UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Возвращает путь к клипу, при необходимости скачав его. Null — не удалось.
        /// </summary>
        public static async Task<string?> TryGetVideoAsync(
            string posterPath,
            string clipAssetId,
            IProgress<double>? downloadProgress = null,
            CancellationToken cancellationToken = default)
        {
            string? readyPath = FindReadyVideo(posterPath);
            if (readyPath is not null)
            {
                return readyPath;
            }

            if (clipAssetId.Length == 0 || !FrameSettings.IsImmichConfigured)
            {
                return null;
            }

            if (DateTime.UtcNow < _unavailableUntilUtc)
            {
                return null;
            }

            await DownloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                // Пока ждали очереди, клип мог уже скачаться.
                return FindReadyVideo(posterPath)
                       ?? await DownloadAsync(
                               posterPath, clipAssetId, downloadProgress, cancellationToken)
                           .ConfigureAwait(false);
            }
            finally
            {
                DownloadGate.Release();
            }
        }

        private static async Task<string?> DownloadAsync(
            string posterPath,
            string clipAssetId,
            IProgress<double>? downloadProgress,
            CancellationToken cancellationToken)
        {
            string clipPath = ClipStorage.BuildClipPath(posterPath);
            string temporaryPath = clipPath + ".tmp";
            Directory.CreateDirectory(ClipStorage.Directory);

            // Свой клиент: у источника снимков свой, и делить его между слоями незачем.
            // Пять минут — на перекодирование клипа сервером на слабой домашней машине.
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    ImmichCatalog.BuildVideoUrl(FrameSettings.ImmichServerUrl, clipAssetId));

                request.Headers.Add(ApiKeyHeaderName, FrameSettings.ImmichApiKey);

                using HttpResponseMessage response = await httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    string body = await response.Content
                        .ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                    FrameLog.Warn(
                        $"Клип Immich не отдан: {(int)response.StatusCode} "
                        + (body.Length > 200 ? body[..200] : body));
                    return null;
                }

                if (response.Content.Headers.ContentLength > MaxVideoBytes)
                {
                    FrameLog.Warn(
                        $"Клип Immich пропущен: {response.Content.Headers.ContentLength} байт.");
                    return null;
                }

                // Через временный файл: прерванная загрузка не оставит обрезанный клип
                // под именем, которое кэш считает готовым. Копируем сами, а не через
                // CopyToAsync: только так виден ход загрузки.
                long expectedBytes = response.Content.Headers.ContentLength ?? 0;

                await using (Stream source = await response.Content
                    .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (FileStream temporaryFile = File.Create(temporaryPath))
                {
                    var buffer = new byte[81920];
                    long copiedBytes = 0;
                    int readBytes;

                    while ((readBytes = await source
                        .ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await temporaryFile.WriteAsync(
                            buffer.AsMemory(0, readBytes), cancellationToken).ConfigureAwait(false);

                        copiedBytes += readBytes;

                        if (expectedBytes > 0)
                        {
                            downloadProgress?.Report((double)copiedBytes / expectedBytes);
                        }
                    }
                }

                File.Move(temporaryPath, clipPath, overwrite: true);

                if (!ClipStorage.IsPlayableVideoFile(clipPath))
                {
                    // Что именно пришло вместо клипа — сказать необходимо: с кодом 200
                    // сервер отдаёт и страницу ошибки, и пустой файл, и контейнер,
                    // которого мы не ждали. Начало файла и тип содержимого различают
                    // эти случаи, а гадать по одной строке «не похож на mp4» нельзя.
                    FrameLog.Warn(
                        $"Скачанный клип Immich не похож на mp4: тип "
                        + $"{response.Content.Headers.ContentType}, "
                        + $"байт {new FileInfo(clipPath).Length}, начало «{DescribeHead(clipPath)}»");

                    ClipStorage.TryDelete(clipPath);
                    return null;
                }

                // Файл появился в общей памяти мимо системного сканера: без этого
                // проигрыватель находит его не всегда — тот же приём, что и в корзине.
                MediaTrash.NotifyMediaScanner(null, clipPath);
                return clipPath;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ClipStorage.TryDelete(temporaryPath);
                throw;
            }
            catch (TaskCanceledException timeoutFailure)
            {
                // Не настоящая отмена — это HttpClient.Timeout истёк сам, потому что
                // сервер не ответил вовсе. Отличаем от отмены по токену тем же приёмом,
                // что и в ImmichPhotoSource: реальную отмену пробрасываем, а таймаут —
                // такой же повод замолчать на время, как и обрыв соединения ниже.
                MarkServerUnavailable();
                FrameLog.Warn($"Клип Immich не скачан: сервер не ответил вовремя ({timeoutFailure.Message}).");
                ClipStorage.TryDelete(temporaryPath);
                return null;
            }
            catch (Exception downloadFailure) when (
                downloadFailure is HttpRequestException or IOException or UnauthorizedAccessException)
            {
                if (downloadFailure is HttpRequestException)
                {
                    // Сети до сервера нет вовсе — соседние живые фото за то же время
                    // получат тот же отказ, и пробовать их сейчас незачем.
                    MarkServerUnavailable();
                }

                FrameLog.Warn($"Клип Immich не скачан: {downloadFailure.Message}");
                ClipStorage.TryDelete(temporaryPath);
                return null;
            }
        }

        /// <summary>
        /// Замолкает на <see cref="UnavailableBackoff"/> — сервер недоступен.
        /// </summary>
        /// <remarks>
        /// Публичный, помимо вызовов изнутри: экран настроек знает о недоступности
        /// раньше — по итогу собственной проверки связи при сохранении, — и в этом
        /// случае незачем ждать, пока то же самое обнаружит первое живое фото.
        /// </remarks>
        public static void MarkServerUnavailable()
        {
            DateTime resumeAtUtc = DateTime.UtcNow + UnavailableBackoff;
            if (resumeAtUtc <= _unavailableUntilUtc)
            {
                return;
            }

            _unavailableUntilUtc = resumeAtUtc;
            FrameLog.Warn(
                $"Immich недоступен: клипы не запрашиваются {UnavailableBackoff.TotalMinutes:0} мин.");
        }
    }
}
