using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.Storage;

// Path есть и в Android.*, и в System.IO: нужен однозначный псевдоним.
using IoPath = System.IO.Path;

namespace PhotoFrame
{
    /// <summary>
    /// Забирает видео общего альбома в момент, когда его слайд показан.
    /// </summary>
    /// <remarks>
    /// Видео не качаются при синхронизации нарочно: минутный клип весит десятки
    /// мегабайт, и тянуть их все ради кадров, до которых показ может не дойти, незачем.
    /// Поэтому в кэше сперва лежит только заставка, а сам файл появляется рядом
    /// (то же имя, расширение .mp4), когда слайд-шоу доходит до кадра. Раз скачанный
    /// клип остаётся в кэше и на следующем круге играет сразу.
    ///
    /// Ссылки на видео нет в списке альбома — она есть только на странице отдельного
    /// кадра, поэтому здесь два запроса: страница кадра и сам файл.
    /// </remarks>
    internal static class AlbumVideoCache
    {
        private const string IndexFileName = "album.videos";

        /// <summary>Вид записи в списке: обычное видео либо живое фото.</summary>
        private const string VideoKind = "video";
        private const string MotionKind = "motion";

        /// <summary>
        /// Метка формата списка, первой строкой файла.
        /// </summary>
        /// <remarks>
        /// Список без метки не принимается: он может знать только о видео, без живых
        /// фото, поэтому такой файл пересобирается при следующей синхронизации, не
        /// дожидаясь изменений в самом альбоме.
        /// </remarks>
        private const string IndexVersionLine = "#v2";
        private const string SkipListFileName = "album.videos.skip";
        private const string AlbumUrlKey = "album_resolved_url";

        /// <summary>
        /// Предел на размер одного клипа. Место на рамке не бесконечно, а размер известен
        /// из заголовков до начала загрузки.
        /// </summary>
        private const long MaxVideoBytes = 200L * 1024 * 1024;

        /// <summary>Одновременно качаем один клип: показан всё равно один кадр.</summary>
        private static readonly SemaphoreSlim DownloadGate = new(1, 1);

        private static string IndexPath =>
            IoPath.Combine(SharedAlbumPhotoSource.PhotoLibraryDirectory, IndexFileName);

        private static string SkipListPath =>
            IoPath.Combine(SharedAlbumPhotoSource.PhotoLibraryDirectory, SkipListFileName);

        /// <summary>
        /// Куда складываются скачанные клипы.
        /// </summary>
        /// <remarks>
        /// Общая память, а не личная папка приложения: проигрыватель живёт в отдельном
        /// процессе mediaserver и файл из /data/user/0/… открыть не может — подготовка
        /// падает с «error (1, 246)». Не годится и Android/data/&lt;пакет&gt;: этот каталог
        /// тоже закрыт от чужих процессов. Видео из папок на устройстве играют именно
        /// потому, что лежат в общей памяти.
        ///
        /// Имя каталога — Cache: обход папок пропускает такие по имени, поэтому свои же
        /// клипы не попадут в слайд-шоу как локальные файлы.
        /// </remarks>
        private static string VideoDirectory
        {
            get
            {
                string? sharedStorageRoot =
                    Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;

                return string.IsNullOrEmpty(sharedStorageRoot)
                    ? IoPath.Combine(SharedAlbumPhotoSource.PhotoLibraryDirectory, "album_videos")
                    : IoPath.Combine(sharedStorageRoot, "PhotoFrame", "Cache");
            }
        }

        /// <summary>
        /// True, если список видео уже составлен.
        /// </summary>
        /// <remarks>
        /// Проверяется перед тем, как пропустить синхронизацию по совпавшему отпечатку
        /// альбома: список видео — единственный источник сведений о клипах, и без него
        /// кадры так и остались бы заставками.
        /// </remarks>
        public static bool HasIndex
        {
            get
            {
                string[] lines = ReadIndex(includeHeader: true);
                return lines.Length > 0 && lines[0].Trim().Equals(
                    IndexVersionLine, StringComparison.Ordinal);
            }
        }

        /// <summary>Запоминает адрес альбома после переходов — из него собирается адрес кадра.</summary>
        public static void RememberAlbumUrl(string resolvedAlbumUrl) =>
            Preferences.Default.Set(AlbumUrlKey, resolvedAlbumUrl);

        /// <summary>
        /// Записывает, какие заставки на самом деле видео и какому кадру принадлежат.
        /// </summary>
        public static void WriteIndex(List<AlbumItem> albumItems, Func<AlbumItem, string> posterFileName)
        {
            var lines = new List<string> { IndexVersionLine };
            int videoCount = 0;
            int motionCount = 0;

            foreach (AlbumItem albumItem in albumItems)
            {
                if (string.IsNullOrEmpty(albumItem.ItemId))
                {
                    continue;
                }

                // Живые фото попадают в список наравне с видео: оживлять их или нет,
                // решается при показе — так настройку можно переключить без новой
                // синхронизации.
                if (albumItem.IsVideo)
                {
                    videoCount++;
                    lines.Add(string.Join(
                        '\t',
                        posterFileName(albumItem),
                        albumItem.ItemId,
                        albumItem.VideoDurationMilliseconds,
                        VideoKind));
                }
                else if (albumItem.IsMotionPhoto)
                {
                    motionCount++;
                    lines.Add(string.Join(
                        '\t',
                        posterFileName(albumItem),
                        albumItem.ItemId,
                        albumItem.MotionDurationMilliseconds,
                        MotionKind));
                }
            }

            FrameLog.Info($"Видео в альбоме: {videoCount}, живых фото: {motionCount}");

            string indexPath = IndexPath;
            string temporaryPath = indexPath + ".tmp";

            try
            {
                File.WriteAllLines(temporaryPath, lines);
                File.Move(temporaryPath, indexPath, overwrite: true);
            }
            catch (Exception writeFailure) when (
                writeFailure is IOException or UnauthorizedAccessException)
            {
                // Без списка видео покажутся заставками.
                System.Diagnostics.Debug.WriteLine(
                    $"Список видео альбома не сохранён: {writeFailure.Message}");
            }
        }

        /// <summary>Что стоит за заставкой: чей клип, какой длины и какого вида.</summary>
        public sealed record AlbumClip(string ItemId, int DurationMilliseconds, bool IsMotionPhoto);

        /// <summary>
        /// Помечает клип непроигрываемым: больше не качаем и не пробуем.
        /// </summary>
        /// <remarks>
        /// Google хранит часть видео в VP9, а на рамке этот кодек только программный —
        /// такие клипы проигрыватель не открывает. Отметка избавляет от повторной
        /// загрузки десятков мегабайт на каждом круге показа, а сам кадр остаётся
        /// в слайд-шоу заставкой.
        /// </remarks>
        public static void MarkUnplayable(string posterPath)
        {
            string posterFileName = IoPath.GetFileName(posterPath);
            TryDelete(BuildVideoPath(posterPath));

            var skipped = new List<string>(ReadSkipList());
            if (skipped.Contains(posterFileName, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            skipped.Add(posterFileName);
            FrameLog.Warn($"Клип отмечен непроигрываемым: {posterFileName}");

            try
            {
                File.WriteAllLines(SkipListPath, skipped);
            }
            catch (Exception writeFailure) when (
                writeFailure is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Список непроигрываемых не сохранён: {writeFailure.Message}");
            }
        }

        private static bool IsUnplayable(string posterPath)
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
        /// Клип за этой заставкой либо null: обычный снимок, незнакомый кадр или клип,
        /// уже отмеченный непроигрываемым.
        /// </summary>
        public static AlbumClip? FindClip(string posterPath)
        {
            if (IsUnplayable(posterPath))
            {
                return null;
            }

            string posterFileName = IoPath.GetFileName(posterPath);

            foreach (string line in ReadIndex())
            {
                string[] parts = line.Split('\t');

                if (parts.Length < 3
                    || !parts[0].Equals(posterFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int.TryParse(parts[2], out int durationMilliseconds);

                // Вид записан четвёртым полем: без него запись означает обычное видео.
                bool isMotionPhoto = parts.Length >= 4
                                     && parts[3].Trim().Equals(MotionKind, StringComparison.Ordinal);

                return new AlbumClip(parts[1], durationMilliseconds, isMotionPhoto);
            }

            return null;
        }

        /// <summary>Путь к уже скачанному годному клипу либо null.</summary>
        public static string? FindReadyVideo(string posterPath)
        {
            string videoPath = BuildVideoPath(posterPath);

            if (!File.Exists(videoPath))
            {
                return null;
            }

            if (IsPlayableVideoFile(videoPath))
            {
                return videoPath;
            }

            // Обрезанный или подменённый страницей ошибки файл проигрыватель всё равно
            // не откроет: убираем, чтобы скачать заново.
            FrameLog.Warn($"Кэшированный клип негоден, убираем: {videoPath}");
            TryDelete(videoPath);
            return null;
        }

        /// <summary>
        /// Проверяет, что файл действительно mp4: у контейнера на четвёртом байте стоит
        /// «ftyp». Ошибку от сервера, отданную с кодом 200, иначе не отличить от клипа.
        /// </summary>
        private static bool IsPlayableVideoFile(string videoPath)
        {
            try
            {
                var header = new byte[12];

                using FileStream file = File.OpenRead(videoPath);
                if (file.Read(header, 0, header.Length) < header.Length)
                {
                    return false;
                }

                return header[4] == (byte)'f' && header[5] == (byte)'t'
                    && header[6] == (byte)'y' && header[7] == (byte)'p';
            }
            catch (Exception readFailure) when (
                readFailure is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// Возвращает путь к клипу, при необходимости скачав его. Null — не удалось.
        /// </summary>
        public static async Task<string?> TryGetVideoAsync(
            string posterPath,
            IProgress<double>? downloadProgress = null,
            CancellationToken cancellationToken = default)
        {
            string? readyPath = FindReadyVideo(posterPath);
            if (readyPath is not null)
            {
                return readyPath;
            }

            string? itemId = FindItemId(posterPath);
            string? itemPageUrl = BuildItemPageUrl(itemId);
            if (itemPageUrl is null)
            {
                return null;
            }

            await DownloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                // Пока ждали очереди, клип мог уже скачаться.
                return FindReadyVideo(posterPath)
                       ?? await DownloadAsync(
                               posterPath, itemPageUrl, downloadProgress, cancellationToken)
                           .ConfigureAwait(false);
            }
            finally
            {
                DownloadGate.Release();
            }
        }

        private static async Task<string?> DownloadAsync(
            string posterPath,
            string itemPageUrl,
            IProgress<double>? downloadProgress,
            CancellationToken cancellationToken)
        {
            string videoPath = BuildVideoPath(posterPath);
            string temporaryPath = videoPath + ".tmp";
            Directory.CreateDirectory(VideoDirectory);

            // Свой клиент: у страницы альбома свой, живущий в источнике снимков, и делить
            // его между слоями незачем.
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
                "Chrome/120.0.0.0 Safari/537.36");

            try
            {
                string itemPageHtml = await httpClient
                    .GetStringAsync(itemPageUrl, cancellationToken).ConfigureAwait(false);

                string? videoUrl = AlbumVideoUrlExtractor.Extract(itemPageHtml);
                if (videoUrl is null)
                {
                    FrameLog.Warn("На странице кадра нет ссылки на видео.");
                    return null;
                }

                using HttpResponseMessage response = await httpClient
                    .GetAsync(videoUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    FrameLog.Warn($"Видео не отдано: {(int)response.StatusCode}.");
                    return null;
                }

                if (response.Content.Headers.ContentLength > MaxVideoBytes)
                {
                    FrameLog.Warn(
                        $"Видео пропущено: {response.Content.Headers.ContentLength} байт.");
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

                File.Move(temporaryPath, videoPath, overwrite: true);
                FrameLog.Info($"Видео скачано: {new FileInfo(videoPath).Length} байт.");
                return videoPath;
            }
            catch (Exception downloadFailure) when (
                downloadFailure is HttpRequestException or IOException
                    or UnauthorizedAccessException or TaskCanceledException)
            {
                FrameLog.Warn($"Видео не скачано: {downloadFailure.Message}");
                TryDelete(temporaryPath);
                return null;
            }
        }

        /// <summary>Клип называется так же, как заставка, но лежит во внешнем каталоге.</summary>
        private static string BuildVideoPath(string posterPath)
        {
            string videoFileName = IoPath.ChangeExtension(IoPath.GetFileName(posterPath), ".mp4");
            return IoPath.Combine(VideoDirectory, videoFileName);
        }

        private static string? FindItemId(string posterPath)
        {
            string posterFileName = IoPath.GetFileName(posterPath);

            foreach (string line in ReadIndex())
            {
                string[] parts = line.Split('\t');
                if (parts.Length >= 2
                    && parts[0].Equals(posterFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return parts[1];
                }
            }

            return null;
        }

        private static string[] ReadIndex(bool includeHeader = false)
        {
            try
            {
                if (!File.Exists(IndexPath))
                {
                    return Array.Empty<string>();
                }

                string[] lines = File.ReadAllLines(IndexPath);
                if (includeHeader)
                {
                    return lines;
                }

                // Метка формата — не запись о кадре, и разбирать её не нужно.
                var entries = new List<string>(lines.Length);
                foreach (string line in lines)
                {
                    if (!line.StartsWith('#'))
                    {
                        entries.Add(line);
                    }
                }

                return entries.ToArray();
            }
            catch (Exception readFailure) when (
                readFailure is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Список видео альбома не прочитан: {readFailure.Message}");
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Адрес страницы кадра: из /share/&lt;альбом&gt;?key=… получается
        /// /share/&lt;альбом&gt;/photo/&lt;кадр&gt;?key=…
        /// </summary>
        private static string? BuildItemPageUrl(string? itemId)
        {
            string albumUrl = Preferences.Default.Get(AlbumUrlKey, string.Empty);

            if (string.IsNullOrEmpty(itemId) || string.IsNullOrEmpty(albumUrl)
                || !Uri.TryCreate(albumUrl, UriKind.Absolute, out Uri? albumUri))
            {
                return null;
            }

            string albumPath = albumUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
            return $"{albumPath}/photo/{itemId}{albumUri.Query}";
        }

        private static void TryDelete(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch (Exception deleteFailure) when (
                deleteFailure is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Не удалось убрать {filePath}: {deleteFailure.Message}");
            }
        }
    }
}
