using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.Storage;

namespace PhotoFrame
{
    /// <summary>
    /// Загружает снимки с домашнего сервера Immich.
    /// </summary>
    /// <remarks>
    /// Immich принимает автозагрузку с телефона, в том числе с iPhone; ссылка на общий
    /// альбом Google этого не умеет — кадры там приходится добавлять руками.
    ///
    /// Здесь, в отличие от альбома Google, есть документированный API: список объектов
    /// приходит JSON-ом, ключ доступа передаётся заголовком x-api-key. Ключ создаётся
    /// в самом Immich и хранится только в настройках устройства — в сборку он не
    /// попадает и в репозитории его нет.
    ///
    /// Устройство кэша повторяет альбомное: имя файла выводится из идентификатора
    /// объекта, поэтому добавленные на сервер снимки докачиваются поодиночке, а не
    /// весь набор заново. Порядок показа фиксируется манифестом.
    ///
    /// Видео качаются не при синхронизации: забирается только заставка, а клип —
    /// когда показ доходит до кадра. Сколько кадров не удалось получить, сообщается
    /// в итоге обновления, чтобы это не выглядело пропажей.
    /// </remarks>
    public class ImmichPhotoSource : IPhotoSource
    {
        private const string PhotoDirectoryName = "immich";

        /// <summary>Список файлов в порядке показа. Пишется последним — это точка фиксации.</summary>
        private const string ManifestFileName = "immich.manifest";

        /// <summary>
        /// Живые фото, для которых сервер уже отказал в отвязке клипа. Android-версию
        /// живого фото Immich не отвязывает никогда: клип у неё не отдельный объект,
        /// а зашит в тот же файл, и повторный запрос вернул бы тот же отказ.
        /// </summary>
        private const string DetachUnsupportedFileName = "immich.detach.unsupported";

        /// <summary>
        /// Отпечаток набора. Версия в имени ключа привязана к составу столбцов списка
        /// (альбом, дата съёмки): сохранённый отпечаток другой версии не применяется,
        /// и полный проход, который заполняет эти столбцы, выполняется.
        /// </summary>
        private const string SignatureKey = "immich_signature_v2";

        /// <summary>Ключ доступа передаётся этим заголовком — так описано в API Immich.</summary>
        private const string ApiKeyHeaderName = "x-api-key";

        /// <summary>
        /// Сколько объектов запрашивается за один раз при обходе всей библиотеки.
        /// Тысяча записей — это около мегабайта JSON: и запросов немного, и разбор
        /// не съедает память рамки.
        /// </summary>
        private const int SearchPageSize = 1000;

        /// <summary>
        /// Предел на число страниц поиска. Библиотека на десятки тысяч снимков не должна
        /// заставлять рамку ходить по серверу без конца: до предела кадров, заданного
        /// в настройках, дело дойдёт куда раньше.
        /// </summary>
        private const int MaxSearchPages = 50;

        private readonly HttpClient _httpClient;

        /// <summary>
        /// Из какого альбома пришёл объект. Заполняется при обходе альбомов и уходит
        /// в список рядом с кэшем: сервер в самом объекте альбомов не перечисляет.
        /// </summary>
        private readonly Dictionary<string, string> _albumNameByAssetId = new(StringComparer.Ordinal);

        public ImmichPhotoSource()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        }

        /// <summary>Каталог со снимками и манифестом.</summary>
        public static string PhotoLibraryDirectory =>
            Path.Combine(FileSystem.AppDataDirectory, PhotoDirectoryName);

        /// <inheritdoc />
        public bool IsConfigured => FrameSettings.IsImmichConfigured;

        /// <inheritdoc />
        public string DescribeConfiguration()
        {
            if (!FrameSettings.IsImmichConfigured)
            {
                return "Сервер или ключ не заданы";
            }

            string[] albumNames = FrameSettings.ImmichAlbumNames;

            return albumNames.Length == 0
                ? "Вся библиотека"
                : "Альбомы: " + string.Join(", ", albumNames);
        }

        /// <inheritdoc />
        public List<string> GetPhotoPaths() => GetCachedPhotoPaths();

        /// <summary>
        /// Проверяет связь с сервером и возвращает список альбомов.
        /// </summary>
        /// <remarks>
        /// Вызывается кнопкой на экране настроек: идентификатор альбома — UUID,
        /// и вручную его не набрать.
        /// </remarks>
        public async Task<List<ImmichAlbum>> GetAlbumsAsync(
            string serverUrl, string apiKey, CancellationToken cancellationToken = default)
        {
            string albumsJson = await GetStringAsync(
                ImmichCatalog.BuildAlbumsUrl(serverUrl), apiKey, cancellationToken)
                .ConfigureAwait(false);

            return ImmichCatalog.ParseAlbums(albumsJson);
        }

        /// <summary>
        /// Проверяет связь с сервером и ключ доступа, ничего больше не запрашивая.
        /// </summary>
        /// <remarks>
        /// /api/users/me — самый лёгкий запрос, отвечающий и на «сервер не отвечает»,
        /// и на «ключ не тот», без похода за списком альбомов, который может быть и
        /// длинным. Вызывается при сохранении настроек: адрес, который сервер не
        /// примет, лучше заметить сразу, а не на следующей синхронизации.
        /// </remarks>
        /// <returns>Текст отказа либо null, если сервер ответил.</returns>
        public async Task<string?> TryCheckConnectionAsync(
            string serverUrl, string apiKey, CancellationToken cancellationToken = default)
        {
            try
            {
                await GetStringAsync(ImmichCatalog.BuildIdentityUrl(serverUrl), apiKey, cancellationToken)
                    .ConfigureAwait(false);
                return null;
            }
            catch (PhotoSourceException connectionFailure)
            {
                return connectionFailure.Message;
            }
        }

        /// <summary>
        /// Убирает объект в корзину сервера — ту же, что и кнопка удаления в самом Immich.
        /// </summary>
        /// <remarks>
        /// force не ставится: объект уходит в корзину и хранится там положенные сервером
        /// дни, а не пропадает безвозвратно. Кнопка на рамке нажимается мимоходом, с
        /// дивана, и права на окончательное удаление у неё быть не должно.
        ///
        /// Отказ не считается ошибкой показа: кадр уже убран из слайд-шоу и внесён
        /// в список убранных, поэтому обратно он не вернётся в любом случае. Ключ
        /// доступа мог быть создан без права на удаление — об этом и сообщаем.
        /// </remarks>
        /// <returns>Текст отказа либо null, если объект убран.</returns>
        public Task<string?> TryTrashOnServerAsync(
            string assetId, CancellationToken cancellationToken = default) =>
            TrashAssetAsync(assetId, cancellationToken);

        /// <summary>Общая часть отправки объекта в корзину сервера.</summary>
        private async Task<string?> TrashAssetAsync(string assetId, CancellationToken cancellationToken)
        {
            if (!FrameSettings.IsImmichConfigured || assetId.Length == 0)
            {
                return "Immich не настроен.";
            }

            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Delete, ImmichCatalog.BuildDeleteAssetsUrl(FrameSettings.ImmichServerUrl))
                {
                    Content = new StringContent(
                        ImmichCatalog.BuildDeleteAssetsBody(assetId),
                        Encoding.UTF8,
                        "application/json"),
                };

                await SendAsync(request, FrameSettings.ImmichApiKey, cancellationToken)
                    .ConfigureAwait(false);

                FrameLog.Info($"Объект Immich убран в корзину сервера: {assetId}");
                return null;
            }
            catch (PhotoSourceException trashFailure)
            {
                FrameLog.Warn($"Immich не убрал объект в корзину: {trashFailure.Message}");
                return trashFailure.Message;
            }
        }

        /// <summary>
        /// Отвязывает клип живого фото от снимка и убирает сам клип в корзину сервера.
        /// Снимок остаётся на месте и в библиотеке — только без секунды движения.
        /// </summary>
        /// <remarks>
        /// Отдельного «отвязать» в Immich нет: правится поле livePhotoVideoId у самого
        /// снимка через updateAsset, а бывший клип после этого — обычный (хоть и
        /// скрытый) видеообъект в библиотеке, который надо убирать отдельным запросом,
        /// иначе он останется висеть в хранилище сервера ничьим.
        ///
        /// Неудача второго шага не считается отказом всей операции: снимок уже отвязан,
        /// то есть то, о чём просили, сделано, а осиротевший клип на сервере — не то,
        /// что видно на рамке, и не то, что мешает показу.
        /// </remarks>
        /// <returns>Текст отказа либо null, если клип отвязан.</returns>
        public async Task<string?> TryDetachLivePhotoAsync(
            string assetId, string clipAssetId, CancellationToken cancellationToken = default)
        {
            if (!FrameSettings.IsImmichConfigured || assetId.Length == 0)
            {
                return "Immich не настроен.";
            }

            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Put, ImmichCatalog.BuildUpdateAssetUrl(FrameSettings.ImmichServerUrl, assetId))
                {
                    Content = new StringContent(
                        ImmichCatalog.BuildDetachLivePhotoBody(),
                        Encoding.UTF8,
                        "application/json"),
                };

                await SendAsync(request, FrameSettings.ImmichApiKey, cancellationToken)
                    .ConfigureAwait(false);

                FrameLog.Info($"Живое фото Immich отвязано от клипа: {assetId}");
            }
            catch (PhotoSourceException detachFailure)
            {
                FrameLog.Warn($"Immich не отвязал живое фото: {detachFailure.Message}");
                return detachFailure.Message;
            }

            if (clipAssetId.Length > 0)
            {
                string? trashFailure = await TrashAssetAsync(clipAssetId, cancellationToken)
                    .ConfigureAwait(false);

                if (trashFailure is not null)
                {
                    FrameLog.Warn($"Осиротевший клип Immich не убран в корзину: {trashFailure}");
                }
            }

            return null;
        }

        /// <summary>
        /// Достаёт из неудачного ответа то, что можно показать человеку.
        /// </summary>
        /// <remarks>
        /// Immich отвечает объектом вида {"message":"...","error":"...","statusCode":400}.
        /// Разбирать его целиком незачем: нужна одна строка, а тело обрезается — в него
        /// при иных ошибках попадает целая страница.
        /// </remarks>
        private static async Task<string> ReadFailureReasonAsync(
            HttpResponseMessage response, CancellationToken cancellationToken)
        {
            try
            {
                string body = await response.Content
                    .ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);

                body = body.Trim();
                if (body.Length == 0)
                {
                    return string.Empty;
                }

                return ": " + (body.Length > 200 ? body[..200] : body);
            }
            catch (Exception readFailure) when (
                readFailure is HttpRequestException or IOException or OperationCanceledException)
            {
                return string.Empty;
            }
        }

        /// <inheritdoc />
        public async Task<AlbumSyncResult> RefreshAsync(
            bool forceRefresh,
            IProgress<(int Completed, int Total)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!FrameSettings.IsImmichConfigured)
            {
                throw new PhotoSourceException(
                    "Immich не настроен: откройте настройки (⚙) и укажите адрес сервера и ключ доступа.");
            }

            string serverUrl = FrameSettings.ImmichServerUrl;
            string apiKey = FrameSettings.ImmichApiKey;

            _albumNameByAssetId.Clear();

            List<ImmichAsset> assets = await GetAssetsAsync(
                serverUrl, apiKey, FrameSettings.ImmichAlbumIds, cancellationToken).ConfigureAwait(false);

            // Видео остаются в наборе: качается их заставка, а сам клип забирается,
            // когда слайд-шоу до кадра дойдёт, — см. ImmichVideoCache. Выключенная
            // настройка убирает их совсем: заставка без возможности воспроизвести
            // только сбивает с толку.
            if (!FrameSettings.DownloadAlbumVideos)
            {
                assets.RemoveAll(asset => asset.IsVideo);
            }

            int trashedCount = RemoveTrashedAssets(assets);

            if (assets.Count == 0)
            {
                throw new PhotoSourceException(trashedCount > 0
                    ? "Все снимки Immich убраны в корзину."
                    : "На сервере Immich не нашлось ни одного снимка.");
            }

            // Тот же предел, что и для альбома Google, и по той же причине: каждый кадр —
            // это запрос и место на диске рамки.
            int availableCount = assets.Count;
            int photoLimit = FrameSettings.ImmichPhotoLimit;

            if (photoLimit > 0 && assets.Count > photoLimit)
            {
                assets.RemoveRange(photoLimit, assets.Count - photoLimit);
            }

            string signature = ComputeSignature(assets);

            // Самый частый случай: на сервере ничего не изменилось, диск можно не трогать.
            if (!forceRefresh && ImmichSidecar.HasIndex && IsAlreadyDownloaded(signature))
            {
                int cachedCount = GetCachedPhotoPaths().Count;
                return new AlbumSyncResult(
                    cachedCount, DownloadedCount: 0, cachedCount, RemovedCount: 0, availableCount);
            }

            AlbumSyncResult result = await SyncPhotosAsync(
                serverUrl, apiKey, assets, progress, cancellationToken).ConfigureAwait(false);

            Preferences.Default.Set(SignatureKey, signature);

            return result with { AvailableCount = availableCount };
        }

        /// <summary>
        /// Объекты библиотеки либо одного альбома, страница за страницей.
        /// </summary>
        /// <param name="albumIds">Пусто — вся библиотека.</param>
        private async Task<List<ImmichAsset>> GetAssetsAsync(
            string serverUrl, string apiKey, string[] albumIds, CancellationToken cancellationToken)
        {
            if (albumIds.Length == 0)
            {
                return await SearchAsync(serverUrl, apiKey, albumId: null, cancellationToken)
                    .ConfigureAwait(false);
            }

            // По одному альбому за запрос, а не все идентификаторы разом: сервер
            // понимает список как «снимок во всех этих альбомах сразу», а нужен
            // «в любом из них». Отсюда склейка своими руками и отсев повторов:
            // снимок может лежать в двух отмеченных альбомах.
            var assets = new List<ImmichAsset>();
            var seenAssetIds = new HashSet<string>(StringComparer.Ordinal);

            string[] albumNames = FrameSettings.ImmichAlbumNames;

            for (int albumIndex = 0; albumIndex < albumIds.Length; albumIndex++)
            {
                string albumLabel = albumIndex < albumNames.Length
                    ? albumNames[albumIndex]
                    : albumIds[albumIndex];

                foreach (ImmichAsset asset in await SearchAsync(
                    serverUrl, apiKey, albumIds[albumIndex], cancellationToken).ConfigureAwait(false))
                {
                    if (seenAssetIds.Add(asset.Id))
                    {
                        assets.Add(asset);

                        // Кадр подписывается альбомом, а он известен только здесь:
                        // в самом объекте сервер альбомов не перечисляет. Первый
                        // отмеченный альбом и выигрывает — снимок может лежать в двух.
                        _albumNameByAssetId[asset.Id] = albumLabel;
                    }
                }
            }

            if (assets.Count == 0)
            {
                throw new PhotoSourceException(
                    $"Выбранные альбомы ({string.Join(", ", FrameSettings.ImmichAlbumNames)}) пусты "
                    + "либо больше не существуют. Выберите их заново в настройках (⚙).");
            }

            return assets;
        }

        /// <summary>Постраничный поиск: по одному альбому либо по всей библиотеке.</summary>
        private async Task<List<ImmichAsset>> SearchAsync(
            string serverUrl, string apiKey, string? albumId, CancellationToken cancellationToken)
        {
            var assets = new List<ImmichAsset>();
            string searchUrl = ImmichCatalog.BuildSearchUrl(serverUrl);
            string[]? albumFilter = albumId is null ? null : new[] { albumId };
            int pageNumber = 1;

            for (int pageIndex = 0; pageIndex < MaxSearchPages && pageNumber > 0; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var request = new HttpRequestMessage(HttpMethod.Post, searchUrl)
                {
                    Content = new StringContent(
                        ImmichCatalog.BuildSearchRequestBody(pageNumber, SearchPageSize, albumFilter),
                        Encoding.UTF8,
                        "application/json"),
                };

                string pageJson = await SendAsync(request, apiKey, cancellationToken).ConfigureAwait(false);
                assets.AddRange(ImmichCatalog.ParseSearchAssets(pageJson, out pageNumber));
            }

            FrameLog.Info($"Immich: альбом {albumId ?? "вся библиотека"} — объектов {assets.Count}");
            return assets;
        }

        private Task<string> GetStringAsync(
            string requestUrl, string apiKey, CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            return SendAsync(request, apiKey, cancellationToken);
        }

        /// <summary>
        /// Отправляет запрос с ключом доступа и разбирает частые отказы в понятный текст.
        /// </summary>
        private async Task<string> SendAsync(
            HttpRequestMessage request, string apiKey, CancellationToken cancellationToken)
        {
            using (request)
            {
                request.Headers.Add(ApiKeyHeaderName, apiKey);

                HttpResponseMessage response;
                try
                {
                    response = await _httpClient
                        .SendAsync(request, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (HttpRequestException networkFailure)
                {
                    throw new PhotoSourceException(
                        $"Нет связи с сервером Immich ({FrameSettings.ImmichServerUrl}).",
                        networkFailure);
                }
                catch (TaskCanceledException timeout) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new PhotoSourceException("Сервер Immich не ответил вовремя.", timeout);
                }
                catch (UriFormatException badUrl)
                {
                    throw new PhotoSourceException(
                        "Адрес сервера Immich выглядит некорректно.", badUrl);
                }
                catch (InvalidOperationException badUrl)
                {
                    throw new PhotoSourceException(
                        "Адрес сервера Immich выглядит некорректно.", badUrl);
                }

                using (response)
                {
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        throw new PhotoSourceException(
                            "Immich не принял ключ доступа. Создайте новый в Account Settings → API Keys.");
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        // Тело ответа с собой: в нём сервер и объясняет, что не так.
                        // Одного кода мало — «ошибка 400» не отличает опечатку в запросе
                        // от отсутствия прав на чужой снимок из общего альбома.
                        string reason = await ReadFailureReasonAsync(response, cancellationToken)
                            .ConfigureAwait(false);

                        throw new PhotoSourceException(
                            $"Сервер Immich ответил ошибкой ({(int)response.StatusCode}){reason}.");
                    }

                    return await response.Content
                        .ReadAsStringAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Приводит кэш в соответствие с сервером: качает недостающее, убирает лишнее.
        /// </summary>
        private async Task<AlbumSyncResult> SyncPhotosAsync(
            string serverUrl,
            string apiKey,
            List<ImmichAsset> assets,
            IProgress<(int Completed, int Total)>? downloadProgress,
            CancellationToken cancellationToken)
        {
            string photoDirectory = PhotoLibraryDirectory;
            Directory.CreateDirectory(photoDirectory);
            RemoveLeftoverTempFiles(photoDirectory);

            var desiredFileNames = new List<string>(assets.Count);
            var missingAssets = new List<(ImmichAsset Asset, string FileName)>();

            foreach (ImmichAsset asset in assets)
            {
                string fileName = BuildCacheFileName(asset.Id);
                desiredFileNames.Add(fileName);

                if (!File.Exists(Path.Combine(photoDirectory, fileName)))
                {
                    missingAssets.Add((asset, fileName));
                }
            }

            int downloadedCount = 0;
            for (int missingIndex = 0; missingIndex < missingAssets.Count; missingIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                (ImmichAsset asset, string fileName) = missingAssets[missingIndex];

                if (await TryDownloadPhotoAsync(
                        serverUrl, apiKey, photoDirectory, asset.Id, fileName, cancellationToken)
                    .ConfigureAwait(false))
                {
                    downloadedCount++;
                }

                downloadProgress?.Report((missingIndex + 1, missingAssets.Count));
            }

            // В манифест попадает только то, что действительно лежит на диске: один
            // неудачно скачанный снимок не должен ломать показ.
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
                    "Не удалось получить из Immich ни одного снимка — прежний набор сохранён.");
            }

            // Сервер отдаёт превью не всегда: у только что залитого снимка оно может быть
            // ещё не построено. Молчать об этом нельзя — иначе разница в числах спишется
            // на предел загрузки, который тут ни при чём.
            int failedCount = desiredFileNames.Count - availableFileNames.Count;
            if (failedCount > 0)
            {
                FrameLog.Warn($"Immich: не удалось скачать кадров: {failedCount}");
            }

            int removedCount = RemoveFilesOutsideLibrary(photoDirectory, availableFileNames);
            WriteManifest(photoDirectory, availableFileNames);

            // Список пишется после файлов: подписи и клипы нужны только тем кадрам,
            // которые уже лежат в кэше.
            ImmichSidecar.WriteIndex(
                assets,
                asset => BuildCacheFileName(asset.Id),
                asset => _albumNameByAssetId.GetValueOrDefault(asset.Id, string.Empty));

            return new AlbumSyncResult(
                TotalPhotoCount: availableFileNames.Count,
                DownloadedCount: downloadedCount,
                ReusedCount: availableFileNames.Count - downloadedCount,
                RemovedCount: removedCount,
                AvailableCount: 0,
                Warning: failedCount == 0
                    ? null
                    : $"Immich: не отдал {failedCount} кадр(ов) — возможно, превью ещё не готово.");
        }

        private async Task<bool> TryDownloadPhotoAsync(
            string serverUrl,
            string apiKey,
            string photoDirectory,
            string assetId,
            string fileName,
            CancellationToken cancellationToken)
        {
            string finalPath = Path.Combine(photoDirectory, fileName);
            string tempPath = finalPath + ".tmp";

            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get, ImmichCatalog.BuildPreviewUrl(serverUrl, assetId));
                request.Headers.Add(ApiKeyHeaderName, apiKey);

                using HttpResponseMessage response = await _httpClient
                    .SendAsync(request, cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    return false;
                }

                byte[] imageBytes = await response.Content
                    .ReadAsByteArrayAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (imageBytes.Length == 0)
                {
                    return false;
                }

                // Сначала во временный файл, потом переименование: оборванная загрузка
                // не оставит битый кадр под именем, которое кэш считает готовым.
                await File.WriteAllBytesAsync(tempPath, imageBytes, cancellationToken)
                    .ConfigureAwait(false);
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
                // Один недоступный снимок не должен срывать всю синхронизацию.
                FrameLog.Warn($"Снимок Immich {assetId} не скачан: {singlePhotoFailure.Message}");
                TryDeleteFile(tempPath);
                return false;
            }
        }

        /// <summary>
        /// Убирает из набора снимки, отправленные в корзину рамки, и возвращает их число.
        /// </summary>
        /// <remarks>
        /// Обычно список пуст: убранный кадр уходит в корзину самого Immich, а такие
        /// объекты сервер в выдачу не включает — и восстановленный в Immich снимок
        /// возвращается на рамку сам. Список нужен лишь на случай, когда сервер удалить
        /// отказался (ключ без права на удаление): иначе кадр приезжал бы снова
        /// после каждой синхронизации.
        /// </remarks>
        private static int RemoveTrashedAssets(List<ImmichAsset> assets)
        {
            string[] trashedFileNames = FrameSettings.TrashedAlbumFileNames;
            if (trashedFileNames.Length == 0)
            {
                return 0;
            }

            var trashedSet = new HashSet<string>(trashedFileNames, StringComparer.OrdinalIgnoreCase);
            return assets.RemoveAll(asset => trashedSet.Contains(BuildCacheFileName(asset.Id)));
        }

        /// <summary>
        /// Имя файла в кэше.
        /// </summary>
        /// <remarks>
        /// Идентификатор объекта — UUID, то есть уже готовое имя файла, но полагаться
        /// на это нельзя: сервер вправе выдавать любые строки, а в имени файла нельзя
        /// косую черту. Хэш и короче, и безопасен.
        /// </remarks>
        private static string BuildCacheFileName(string assetId)
        {
            byte[] idHash = SHA256.HashData(Encoding.UTF8.GetBytes(assetId));
            return string.Concat(Convert.ToHexString(idHash).AsSpan(0, 20), ".jpg");
        }

        /// <summary>
        /// Убирает кадр из кэша рамки вместе с его клипом.
        /// </summary>
        /// <remarks>
        /// Именно удаление, а не перенос в корзину рамки: в кэше лежит превью, а сам
        /// снимок остался на сервере — и в его корзине. Хранить на рамке ещё и копию
        /// превью незачем, а главное, вернуть кадр это не поможет: восстанавливают его
        /// в Immich, после чего он приезжает сюда сам при очередной синхронизации.
        /// </remarks>
        public static void RemoveFromCache(string posterPath)
        {
            TryDeleteFile(posterPath);

            // Клип живого фото или видео лежит в общем каталоге под тем же именем.
            ClipStorage.TryDelete(ClipStorage.BuildClipPath(posterPath));
        }

        private static string DetachUnsupportedListPath =>
            Path.Combine(PhotoLibraryDirectory, DetachUnsupportedFileName);

        /// <summary>True, если сервер уже отказал отвязывать клип этого живого фото.</summary>
        public static bool IsDetachUnsupported(string posterPath)
        {
            string posterFileName = Path.GetFileName(posterPath);

            foreach (string line in ReadDetachUnsupportedList())
            {
                if (line.Trim().Equals(posterFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Запоминает отказ сервера — кнопка на этом кадре больше не появится.</summary>
        public static void MarkDetachUnsupported(string posterPath)
        {
            string posterFileName = Path.GetFileName(posterPath);
            var marked = new List<string>(ReadDetachUnsupportedList());

            if (marked.Contains(posterFileName, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            marked.Add(posterFileName);

            try
            {
                Directory.CreateDirectory(PhotoLibraryDirectory);
                File.WriteAllLines(DetachUnsupportedListPath, marked);
            }
            catch (Exception writeFailure) when (
                writeFailure is IOException or UnauthorizedAccessException)
            {
                FrameLog.Warn($"Список неотвязываемых живых фото не сохранён: {writeFailure.Message}");
            }
        }

        private static string[] ReadDetachUnsupportedList()
        {
            try
            {
                return File.Exists(DetachUnsupportedListPath)
                    ? File.ReadAllLines(DetachUnsupportedListPath)
                    : Array.Empty<string>();
            }
            catch (Exception readFailure) when (
                readFailure is IOException or UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>True, если набор совпадает с уже скачанным и файлы на месте.</summary>
        private static bool IsAlreadyDownloaded(string signature) =>
            Preferences.Default.Get(SignatureKey, string.Empty) == signature
            && GetCachedPhotoPaths().Count > 0;

        /// <summary>Отпечаток набора: по нему видно, менялось ли что-то на сервере.</summary>
        private static string ComputeSignature(List<ImmichAsset> assets)
        {
            var assetIds = new List<string>(assets.Count);
            foreach (ImmichAsset asset in assets)
            {
                assetIds.Add(asset.Id);
            }

            byte[] joinedIdsHash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', assetIds)));
            return Convert.ToHexString(joinedIdsHash);
        }

        /// <summary>Удаляет кадры, которых на сервере больше нет.</summary>
        private static int RemoveFilesOutsideLibrary(string photoDirectory, List<string> keepFileNames)
        {
            var keepSet = new HashSet<string>(keepFileNames, StringComparer.OrdinalIgnoreCase);
            int removedCount = 0;

            foreach (string existingPath in Directory.GetFiles(photoDirectory))
            {
                if (!MediaFileTypes.IsSupportedMedia(existingPath))
                {
                    continue;
                }

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
                    "Не удалось сохранить список снимков Immich на диске рамки.", manifestFailure);
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
                FrameLog.Warn($"Не удалось удалить {filePath}: {deleteFailure.Message}");
                return false;
            }
        }

        /// <summary>Пути к снимкам в порядке, взятом из манифеста.</summary>
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
                FrameLog.Warn($"Манифест Immich не прочитан: {manifestReadFailure.Message}");
            }

            return photoPaths;
        }
    }
}
