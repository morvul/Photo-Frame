using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.Storage;

namespace PhotoFrame
{
    /// <summary>
    /// Загружает снимки из публично расшаренного альбома Google Photos.
    /// </summary>
    /// <remarks>
    /// Почему не Photos Library API: на рамке нет браузера, Google Play Services и
    /// аккаунтов, device flow запрещает scope photoslibrary.*, а токены обновления
    /// в проекте со статусом Testing живут 7 дней. Ссылка на расшаренный альбом не
    /// требует авторизации вообще, поэтому ничего не истекает и настраивать в консоли
    /// Google ничего не нужно.
    ///
    /// Плата за это — разбор HTML страницы, а не документированного API: если Google
    /// поменяет вёрстку, извлечение перестанет находить снимки. Именно поэтому пустой
    /// результат трактуется как ошибка с внятным текстом, а не как «альбом пуст».
    ///
    /// Кэш адресуется содержимым ссылки: имя файла — хэш токена снимка. Поэтому при
    /// добавлении пары кадров в альбом качаются только они, а не весь набор заново.
    /// </remarks>
    public class SharedAlbumPhotoSource : IPhotoSource
    {
        private const string PhotoDirectoryName = "photos";

        /// <summary>Список файлов в порядке показа. Пишется последним — это точка фиксации.</summary>
        private const string ManifestFileName = "album.manifest";

        private const string AlbumSignatureKey = "shared_album_signature";

        /// <summary>
        /// Предел на размер одного видео. Клип на минуту весит десятки мегабайт, но
        /// найдётся и такой, что займёт всю память рамки, — а места там гигабайты.
        /// </summary>
        private const long MaxVideoBytes = 200L * 1024 * 1024;

        private readonly HttpClient _httpClient;

        /// <summary>
        /// Адрес альбома после переходов: короткая ссылка photos.app.goo.gl ведёт на
        /// /share/&lt;альбом&gt;?key=…, и только оттуда можно собрать адрес страницы
        /// отдельного кадра, где лежит ссылка на видео.
        /// </summary>
        private Uri? _resolvedAlbumUri;

        public SharedAlbumPhotoSource()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

            // Без узнаваемого User-Agent Google может отдать урезанную страницу без ссылок.
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
                "Chrome/120.0.0.0 Safari/537.36");
        }

        /// <summary>Каталог со снимками и манифестом.</summary>
        public static string PhotoLibraryDirectory =>
            Path.Combine(FileSystem.AppDataDirectory, PhotoDirectoryName);

        /// <inheritdoc />
        public bool IsConfigured => FrameSettings.IsAlbumConfigured;

        /// <inheritdoc />
        public string DescribeConfiguration() =>
            FrameSettings.IsAlbumConfigured ? "Ссылка на альбом задана" : "Ссылка на альбом не задана";

        /// <inheritdoc />
        public List<string> GetPhotoPaths() => GetCachedPhotoPaths();

        /// <summary>
        /// Читает альбом и, если набор снимков изменился, догружает недостающие кадры.
        /// </summary>
        public async Task<AlbumSyncResult> RefreshAsync(
            bool forceRefresh,
            IProgress<(int Completed, int Total)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            List<AlbumItem> albumItems = await GetAlbumItemsAsync(cancellationToken).ConfigureAwait(false);

            // Убранное в корзину отбрасывается раньше всего: и качать заново не нужно,
            // и в «сколько кадров в альбоме» такие снимки попадать не должны — иначе
            // разница списывалась бы на предел загрузки.
            RemoveTrashedItems(albumItems);

            if (albumItems.Count == 0)
            {
                throw new PhotoSourceException("Все кадры альбома убраны в корзину.");
            }

            // Лимит применяется здесь, а не при разборе страницы: так известно и сколько
            // кадров в альбоме на самом деле, и об отброшенных можно сообщить.
            int availableCount = albumItems.Count;
            int photoLimit = FrameSettings.AlbumPhotoLimit;

            if (photoLimit > 0 && albumItems.Count > photoLimit)
            {
                albumItems.RemoveRange(photoLimit, albumItems.Count - photoLimit);
            }

            string albumSignature = ComputeAlbumSignature(BuildPosterUrlList(albumItems));

            // Самый частый случай: альбом не менялся, диск можно вообще не трогать.
            // Но пока список видео не составлен, полный проход нужен, даже если
            // отпечаток совпал.
            if (!forceRefresh && AlbumVideoCache.HasIndex && IsAlreadyDownloaded(albumSignature))
            {
                int cachedCount = GetCachedPhotoPaths().Count;
                return new AlbumSyncResult(
                    cachedCount, DownloadedCount: 0, cachedCount, RemovedCount: 0, availableCount);
            }

            AlbumSyncResult result =
                await SyncPhotosAsync(albumItems, progress, cancellationToken).ConfigureAwait(false);

            return result with { AvailableCount = availableCount };
        }

        /// <summary>Ссылки на изображения кадров — по ним считается отпечаток альбома.</summary>
        private static List<string> BuildPosterUrlList(List<AlbumItem> albumItems)
        {
            var posterUrls = new List<string>(albumItems.Count);
            foreach (AlbumItem albumItem in albumItems)
            {
                posterUrls.Add(albumItem.PosterUrl);
            }

            return posterUrls;
        }

        /// <summary>
        /// Забирает страницу расшаренного альбома и вытаскивает из неё ссылки на снимки
        /// в том порядке, в котором их перечисляет Google.
        /// </summary>
        public async Task<List<AlbumItem>> GetAlbumItemsAsync(CancellationToken cancellationToken = default)
        {
            if (!FrameSettings.IsAlbumConfigured)
            {
                throw new PhotoSourceException(
                    "Не задана ссылка на альбом. Откройте настройки (⚙) и вставьте ссылку " +
                    "на общий альбом Google Photos.");
            }

            string albumPageHtml = await FetchAlbumPageAsync(
                FrameSettings.SharedAlbumUrl, cancellationToken).ConfigureAwait(false);

            List<AlbumItem> albumItems = AlbumItemExtractor.Extract(albumPageHtml);

            if (albumItems.Count == 0)
            {
                // Запасной разбор — по одним ссылкам на изображения. Вёрстка страницы
                // недокументированная: если Google переставит поля записей, кадры всё
                // равно покажутся, просто без видео.
                foreach (string photoUrl in AlbumPhotoUrlExtractor.Extract(albumPageHtml))
                {
                    albumItems.Add(new AlbumItem(
                        ItemId: string.Empty, photoUrl, VideoDurationMilliseconds: 0));
                }
            }

            if (albumItems.Count == 0)
            {
                throw new PhotoSourceException(
                    "В странице альбома не найдено ни одной ссылки на снимок. " +
                    "Проверьте, что ссылка на альбом ещё действует и открыт публичный доступ.");
            }

            return albumItems;
        }

        private async Task<string> FetchAlbumPageAsync(string shareUrl, CancellationToken cancellationToken)
        {
            HttpResponseMessage response;
            try
            {
                // Короткая ссылка photos.app.goo.gl отдаёт редирект на photos.google.com/share/...
                // HttpClient переходит по нему сам.
                response = await _httpClient.GetAsync(shareUrl, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException networkFailure)
            {
                throw new PhotoSourceException("Нет связи с Google Photos.", networkFailure);
            }
            catch (TaskCanceledException timeout) when (!cancellationToken.IsCancellationRequested)
            {
                throw new PhotoSourceException("Google Photos не ответил вовремя.", timeout);
            }
            catch (UriFormatException badUrl)
            {
                throw new PhotoSourceException("Ссылка на альбом выглядит некорректно.", badUrl);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new PhotoSourceException(
                        $"Страница альбома недоступна ({(int)response.StatusCode}). " +
                        "Возможно, доступ по ссылке отключён.");
                }

                // Запоминаем адрес после переходов: из него собираются адреса страниц
                // отдельных кадров, а в них — ссылки на видео.
                _resolvedAlbumUri = response.RequestMessage?.RequestUri;
                if (_resolvedAlbumUri is not null)
                {
                    AlbumVideoCache.RememberAlbumUrl(_resolvedAlbumUri.ToString());
                }

                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Отпечаток набора снимков. Позволяет при регулярной проверке вообще не трогать
        /// диск, если альбом не менялся.
        /// </summary>
        public static string ComputeAlbumSignature(List<string> photoUrls)
        {
            ArgumentNullException.ThrowIfNull(photoUrls);

            byte[] joinedUrlsHash = SHA256.HashData(
                Encoding.UTF8.GetBytes(string.Join('\n', photoUrls)));

            return Convert.ToHexString(joinedUrlsHash);
        }

        /// <summary>True, если набор совпадает с уже скачанным и файлы на месте.</summary>
        public static bool IsAlreadyDownloaded(string albumSignature)
        {
            string storedSignature = Preferences.Default.Get(AlbumSignatureKey, string.Empty);
            return storedSignature == albumSignature && GetCachedPhotoPaths().Count > 0;
        }

        /// <summary>
        /// Приводит локальный кэш в соответствие с альбомом: скачивает только те снимки,
        /// которых ещё нет, и удаляет те, что из альбома убрали.
        /// </summary>
        /// <param name="downloadProgress">
        /// Сообщает, сколько из подлежащих скачиванию снимков уже готово.
        /// </param>
        public async Task<AlbumSyncResult> SyncPhotosAsync(
            List<AlbumItem> albumItems,
            IProgress<(int Completed, int Total)>? downloadProgress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(albumItems);

            string photoDirectory = PhotoLibraryDirectory;
            Directory.CreateDirectory(photoDirectory);
            RemoveLeftoverTempFiles(photoDirectory);

            // Имя файла выводится из ссылки, поэтому один и тот же кадр всегда
            // отображается в один и тот же файл — на этом и держится инкрементальность.
            var desiredFileNames = new List<string>(albumItems.Count);
            var missingItems = new List<(AlbumItem Item, string FileName)>();

            foreach (AlbumItem albumItem in albumItems)
            {
                if (albumItem.IsVideo && !FrameSettings.DownloadAlbumVideos)
                {
                    // Видео выключены — кадр не показывается вовсе: заставка без
                    // возможности воспроизвести только сбивает с толку.
                    continue;
                }

                // И у видео качается только кадр-заставка: сам файл забирается, когда
                // слайд-шоу до него дойдёт, — иначе первая же синхронизация тянула бы
                // десятки мегабайт ради кадров, которые могут и не показаться.
                string fileName = BuildPosterFileName(albumItem);
                desiredFileNames.Add(fileName);

                if (!File.Exists(Path.Combine(photoDirectory, fileName)))
                {
                    missingItems.Add((albumItem, fileName));
                }
            }

            int downloadedCount = 0;
            for (int missingIndex = 0; missingIndex < missingItems.Count; missingIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                (AlbumItem item, string fileName) = missingItems[missingIndex];

                if (await TryDownloadPhotoAsync(
                        photoDirectory, (item.PosterUrl, fileName), cancellationToken)
                    .ConfigureAwait(false))
                {
                    downloadedCount++;
                }

                downloadProgress?.Report((missingIndex + 1, missingItems.Count));
            }

            // В манифест попадают только файлы, реально лежащие на диске: неудачная
            // загрузка отдельного снимка не должна ломать слайд-шоу.
            var availableFileNames = new List<string>(desiredFileNames.Count);
            foreach (string fileName in desiredFileNames)
            {
                if (File.Exists(Path.Combine(photoDirectory, fileName)))
                {
                    availableFileNames.Add(fileName);
                }
            }

            if (availableFileNames.Count == 0)
            {
                throw new PhotoSourceException(
                    "Не удалось получить ни одного снимка — прежний набор фотографий сохранён.");
            }

            int removedCount = RemoveFilesOutsideAlbum(photoDirectory, availableFileNames);
            WriteManifest(photoDirectory, availableFileNames);
            AlbumVideoCache.WriteIndex(albumItems, BuildPosterFileName);
            Preferences.Default.Set(
                AlbumSignatureKey, ComputeAlbumSignature(BuildPosterUrlList(albumItems)));

            return new AlbumSyncResult(
                TotalPhotoCount: availableFileNames.Count,
                DownloadedCount: downloadedCount,
                ReusedCount: availableFileNames.Count - downloadedCount,
                RemovedCount: removedCount);
        }

        private async Task<bool> TryDownloadPhotoAsync(
            string photoDirectory,
            (string Url, string FileName) photo,
            CancellationToken cancellationToken)
        {
            string finalPath = Path.Combine(photoDirectory, photo.FileName);
            string tempPath = finalPath + ".tmp";

            try
            {
                // Google сам масштабирует кадр под экран рамки — качать 4032x2268 незачем.
                string sizedUrl =
                    $"{photo.Url}=w{AppSettings.FrameWidthPixels}-h{AppSettings.FrameHeightPixels}";

                byte[] imageBytes = await _httpClient
                    .GetByteArrayAsync(sizedUrl, cancellationToken)
                    .ConfigureAwait(false);

                if (imageBytes.Length == 0)
                {
                    return false;
                }

                // Сначала во временный файл, потом переименование: прерванная загрузка
                // не оставит битый кадр под именем, которое кэш считает готовым.
                await File.WriteAllBytesAsync(tempPath, imageBytes, cancellationToken).ConfigureAwait(false);
                File.Move(tempPath, finalPath, overwrite: true);
                return true;
            }
            catch (OperationCanceledException)
            {
                TryDeleteFile(tempPath);
                throw;
            }
            catch (Exception singlePhotoFailure) when (
                singlePhotoFailure is HttpRequestException or IOException or UnauthorizedAccessException)
            {
                // Один нечитаемый снимок не должен срывать всю синхронизацию, но и молчать о нём не надо.
                System.Diagnostics.Debug.WriteLine(
                    $"Снимок {photo.FileName} не скачан: {singlePhotoFailure.Message}");
                TryDeleteFile(tempPath);
                return false;
            }
        }

        /// <summary>
        /// Убирает из набора кадры, отправленные в корзину, и возвращает их количество.
        /// </summary>
        /// <remarks>
        /// Сопоставление идёт по имени файла в кэше, то есть по хэшу ссылки: сама ссылка
        /// в списке убранных не хранится — она длинная, а имя файла и так однозначно.
        /// </remarks>
        private static int RemoveTrashedItems(List<AlbumItem> albumItems)
        {
            string[] trashedFileNames = FrameSettings.TrashedAlbumFileNames;
            if (trashedFileNames.Length == 0)
            {
                return 0;
            }

            var trashedSet = new HashSet<string>(trashedFileNames, StringComparer.OrdinalIgnoreCase);
            return albumItems.RemoveAll(item => trashedSet.Contains(BuildCacheFileName(item)));
        }

        /// <summary>
        /// Имя файла кадра-заставки в кэше — короткий хэш ссылки на изображение.
        /// </summary>
        /// <remarks>
        /// И у видео в кэше лежит заставка: сам клип забирается по требованию и получает
        /// то же имя с расширением .mp4 — см. <see cref="AlbumVideoCache"/>.
        /// </remarks>
        private static string BuildPosterFileName(AlbumItem albumItem)
        {
            byte[] urlHash = SHA256.HashData(Encoding.UTF8.GetBytes(albumItem.PosterUrl));
            return string.Concat(Convert.ToHexString(urlHash).AsSpan(0, 20), ".jpg");
        }

        /// <summary>Имя файла в кэше для ссылки на снимок.</summary>
        private static string BuildCacheFileName(string photoUrl) =>
            BuildPosterFileName(new AlbumItem(string.Empty, photoUrl, 0));

        private static string BuildCacheFileName(AlbumItem albumItem) =>
            BuildPosterFileName(albumItem);

        /// <summary>Удаляет кадры, которых больше нет в альбоме.</summary>
        private static int RemoveFilesOutsideAlbum(string photoDirectory, List<string> keepFileNames)
        {
            var keepSet = new HashSet<string>(keepFileNames, StringComparer.OrdinalIgnoreCase);
            int removedCount = 0;

            foreach (string existingPath in Directory.GetFiles(photoDirectory))
            {
                // Манифест и временные файлы к кадрам не относятся.
                if (!MediaFileTypes.IsSupportedMedia(existingPath))
                {
                    continue;
                }

                // Клипы хранятся во внешнем каталоге, откуда их открывает проигрыватель:
                // файл с таким именем в каталоге снимков не воспроизвести, поэтому
                // он удаляется.
                if (!keepSet.Contains(Path.GetFileName(existingPath)) && TryDeleteFile(existingPath))
                {
                    removedCount++;
                }
            }

            return removedCount;
        }

        private static void RemoveLeftoverTempFiles(string photoDirectory)
        {
            foreach (string tempPath in Directory.GetFiles(photoDirectory, "*.tmp"))
            {
                TryDeleteFile(tempPath);
            }
        }

        /// <summary>
        /// Пишет манифест через временный файл: пока он не заменён, слайд-шоу
        /// продолжает работать по прежнему списку.
        /// </summary>
        private static void WriteManifest(string photoDirectory, List<string> orderedFileNames)
        {
            string manifestPath = Path.Combine(photoDirectory, ManifestFileName);
            string tempManifestPath = manifestPath + ".tmp";

            try
            {
                File.WriteAllLines(tempManifestPath, orderedFileNames);
                File.Move(tempManifestPath, manifestPath, overwrite: true);
            }
            catch (Exception manifestFailure) when (
                manifestFailure is IOException or UnauthorizedAccessException)
            {
                TryDeleteFile(tempManifestPath);
                throw new PhotoSourceException(
                    "Не удалось сохранить список снимков на диске рамки.", manifestFailure);
            }
        }

        private static bool TryDeleteFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return false;
            }

            try
            {
                File.Delete(filePath);
                return true;
            }
            catch (Exception deleteFailure) when (
                deleteFailure is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Не удалось удалить {filePath}: {deleteFailure.Message}");
                return false;
            }
        }

        /// <summary>
        /// Возвращает пути к снимкам в порядке альбома, взятом из манифеста.
        /// </summary>
        public static List<string> GetCachedPhotoPaths()
        {
            string photoDirectory = PhotoLibraryDirectory;
            string manifestPath = Path.Combine(photoDirectory, ManifestFileName);
            var photoPaths = new List<string>();

            if (!File.Exists(manifestPath))
            {
                return photoPaths;
            }

            try
            {
                foreach (string fileName in File.ReadAllLines(manifestPath))
                {
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        continue;
                    }

                    string photoPath = Path.Combine(photoDirectory, fileName);
                    if (File.Exists(photoPath))
                    {
                        photoPaths.Add(photoPath);
                    }
                }
            }
            catch (Exception manifestReadFailure) when (
                manifestReadFailure is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Манифест не прочитан: {manifestReadFailure.Message}");
            }

            return photoPaths;
        }
    }
}
