using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace PhotoFrame
{
    /// <summary>Альбом на сервере Immich.</summary>
    /// <param name="Id">Идентификатор альбома, он же часть адреса запроса.</param>
    /// <param name="Name">Название, как его видит пользователь.</param>
    /// <param name="AssetCount">Сколько в альбоме объектов по данным сервера.</param>
    public sealed record ImmichAlbum(string Id, string Name, int AssetCount);

    /// <summary>Снимок или видео на сервере Immich.</summary>
    /// <param name="Id">Идентификатор объекта: по нему строятся адреса загрузки.</param>
    /// <param name="FileName">Исходное имя файла — оно и показывается подписью кадра.</param>
    /// <param name="IsVideo">True для видео: у него качается заставка, клип — по требованию.</param>
    /// <param name="DurationMilliseconds">Длительность клипа; 0 у снимка.</param>
    /// <param name="LivePhotoVideoId">
    /// Идентификатор клипа живого фото (снято на iPhone) либо пусто. Сам клип отдельным
    /// объектом в выдаче не появляется, поэтому оживить кадр можно только по этой ссылке.
    /// </param>
    /// <param name="CameraName">Чем снято, из exifInfo; пусто, если сервер не знает.</param>
    /// <param name="TakenAt">Когда снято, из exifInfo.</param>
    public readonly record struct ImmichAsset(
        string Id,
        string FileName,
        bool IsVideo,
        int DurationMilliseconds = 0,
        string LivePhotoVideoId = "",
        string CameraName = "",
        DateTime? TakenAt = null)
    {
        /// <summary>True, если к снимку приложен клип на пару секунд.</summary>
        public bool IsMotionPhoto => !IsVideo && LivePhotoVideoId.Length > 0;
    }

    /// <summary>
    /// Адреса и разбор ответов Immich.
    /// </summary>
    /// <remarks>
    /// В отличие от Google Photos, здесь есть документированный HTTP API, поэтому
    /// разбирается JSON, а не вёрстка страницы. Ключ доступа создаётся в самом Immich
    /// (Account Settings → API Keys) и передаётся заголовком x-api-key.
    ///
    /// Ответы читаются через JsonDocument, а не через десериализацию в типы: полей у
    /// объекта Immich несколько десятков, нужны из них три, а версии сервера эти поля
    /// добавляют и переименовывают. Пропущенное поле здесь означает пропущенный объект,
    /// а не исключение на весь ответ.
    ///
    /// Разбор вынесен из проекта приложения в Core, чтобы покрыть его тестами: собрать
    /// MAUI под Android ради проверки одной строки JSON невозможно.
    /// </remarks>
    public static class ImmichCatalog
    {
        /// <summary>
        /// Приводит введённый пользователем адрес сервера к виду «схема://хост:порт».
        /// </summary>
        /// <remarks>
        /// Адрес набирают на самой рамке, пультом по экранной клавиатуре, поэтому здесь
        /// прощаются частые огрехи: пропущенная схема (в домашней сети сервер почти
        /// всегда по http), косая черта на конце и скопированный из документации
        /// суффикс /api — его добавляет уже сама сборка адресов.
        /// </remarks>
        public static string NormalizeServerUrl(string? serverUrl)
        {
            string trimmedUrl = (serverUrl ?? string.Empty).Trim();
            if (trimmedUrl.Length == 0)
            {
                return string.Empty;
            }

            if (!trimmedUrl.Contains("://", StringComparison.Ordinal))
            {
                trimmedUrl = "http://" + trimmedUrl;
            }

            trimmedUrl = trimmedUrl.TrimEnd('/');

            if (trimmedUrl.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            {
                trimmedUrl = trimmedUrl[..^"/api".Length];
            }

            return trimmedUrl;
        }

        /// <summary>Список альбомов пользователя.</summary>
        public static string BuildAlbumsUrl(string serverUrl) =>
            NormalizeServerUrl(serverUrl) + "/api/albums";

        /// <summary>Удаление объектов — тот же адрес, что и у кнопки удаления в Immich.</summary>
        public static string BuildDeleteAssetsUrl(string serverUrl) =>
            NormalizeServerUrl(serverUrl) + "/api/assets";

        /// <summary>
        /// Тело запроса на удаление.
        /// </summary>
        /// <remarks>
        /// force=false — объект уходит в корзину сервера и хранится там положенный срок,
        /// а не пропадает безвозвратно: кнопку на рамке нажимают мимоходом.
        /// </remarks>
        public static string BuildDeleteAssetsBody(string assetId) =>
            $"{{\"ids\":[\"{assetId}\"],\"force\":false}}";

        /// <summary>Правка отдельного объекта — тем же адресом, что и updateAsset в Immich.</summary>
        public static string BuildUpdateAssetUrl(string serverUrl, string assetId) =>
            NormalizeServerUrl(serverUrl) + "/api/assets/" + Uri.EscapeDataString(assetId);

        /// <summary>
        /// Тело запроса, отвязывающего клип живого фото от снимка.
        /// </summary>
        /// <remarks>
        /// Отдельного адреса «отвязать» в Immich нет: снимок остаётся собой, а поле
        /// livePhotoVideoId просто обнуляется через тот же updateAsset, что правит любое
        /// другое поле объекта. Сам клип от этого не удаляется — он становится обычным
        /// (скрытым) видео в библиотеке, и убрать его — отдельный запрос на удаление.
        /// </remarks>
        public static string BuildDetachLivePhotoBody() => "{\"livePhotoVideoId\":null}";

        /// <summary>Постраничный поиск по библиотеке или по одному альбому.</summary>
        public static string BuildSearchUrl(string serverUrl) =>
            NormalizeServerUrl(serverUrl) + "/api/search/metadata";

        /// <summary>Кто владелец ключа: этим запросом проверяется связь.</summary>
        public static string BuildIdentityUrl(string serverUrl) =>
            NormalizeServerUrl(serverUrl) + "/api/users/me";

        /// <summary>Порт, на котором Immich слушает по умолчанию.</summary>
        public const int DefaultPort = 2283;

        /// <summary>
        /// Лёгкий запрос без авторизации — годится, чтобы отличить настоящий Immich от
        /// чего угодно ещё, что слушает тот же порт, при поиске сервера в сети.
        /// </summary>
        public static string BuildPingUrl(string serverUrl) =>
            NormalizeServerUrl(serverUrl) + "/api/server/ping";

        /// <summary>Тот же запрос по старому пути — для серверов без /api/server/ping.</summary>
        public static string BuildLegacyPingUrl(string serverUrl) =>
            NormalizeServerUrl(serverUrl) + "/api/server-info/ping";

        /// <summary>True, если тело ответа похоже на {"res":"pong"} — здоровый Immich.</summary>
        public static bool IsPingResponse(string? pingJson)
        {
            using JsonDocument? document = TryParse(pingJson ?? string.Empty);
            return document is not null
                && string.Equals(
                    ReadString(document.RootElement, "res"), "pong", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Адрес готового изображения объекта.
        /// </summary>
        /// <remarks>
        /// Берётся preview, а не original: сервер уже сделал из снимка JPEG около
        /// 1440 пикселей по длинной стороне, и для экрана 1280x800 этого с запасом.
        /// Оригинал в разы тяжелее и на рамке всё равно ужимается при показе.
        /// </remarks>
        public static string BuildPreviewUrl(string serverUrl, string assetId) =>
            NormalizeServerUrl(serverUrl) + "/api/assets/" + Uri.EscapeDataString(assetId)
            + "/thumbnail?size=preview";

        /// <summary>
        /// Адрес видеофайла.
        /// </summary>
        /// <remarks>
        /// playback, а не original: сервер отдаёт перекодированный H.264, который рамка
        /// разбирает аппаратно, тогда как оригинал с телефона бывает HEVC 10 бит или
        /// вовсе HDR — такой клип на этом железе не открывается.
        /// </remarks>
        public static string BuildVideoUrl(string serverUrl, string assetId) =>
            NormalizeServerUrl(serverUrl) + "/api/assets/" + Uri.EscapeDataString(assetId)
            + "/video/playback";

        /// <summary>
        /// Тело запроса к постраничному поиску.
        /// </summary>
        /// <remarks>
        /// Содержимое альбома берётся тем же запросом с фильтром albumIds, а не через
        /// /api/albums/{id}: оттуда приходят одни сведения об альбоме, без списка
        /// снимков — даже с явным withoutAssets=false. Через поиск ответ ещё и
        /// постраничный, так что альбом на десять тысяч кадров не придёт одним куском.
        ///
        /// Тип объектов намеренно не ограничивается: видео нужно посчитать, чтобы
        /// сказать, сколько их пропущено, а отфильтрованных сервером не увидеть.
        /// </remarks>
        /// <param name="albumIds">
        /// Пусто — искать по всей библиотеке. Передаётся не больше одного альбома за
        /// запрос: с несколькими идентификаторами сервер выдаёт пустой список, поэтому
        /// обход альбомов по очереди и сшивание выдачи делает вызывающий код.
        /// </param>
        public static string BuildSearchRequestBody(
            int pageNumber, int pageSize, IReadOnlyList<string>? albumIds = null)
        {
            string albumFilter = string.Empty;

            if (albumIds is { Count: > 0 })
            {
                var quotedIds = new List<string>(albumIds.Count);
                foreach (string albumId in albumIds)
                {
                    quotedIds.Add($"\"{albumId}\"");
                }

                albumFilter = $",\"albumIds\":[{string.Join(',', quotedIds)}]";
            }

            // withExif — иначе сервер не присылает съёмочные поля вовсе, а других
            // источников для них нет: рамка показывает превью, из которого EXIF вырезан.
            return "{\"page\":" + pageNumber
                + ",\"size\":" + pageSize
                + ",\"withDeleted\":false,\"withExif\":true"
                + albumFilter + "}";
        }

        /// <summary>Разбирает ответ /api/albums.</summary>
        public static List<ImmichAlbum> ParseAlbums(string albumsJson)
        {
            var albums = new List<ImmichAlbum>();

            using JsonDocument? document = TryParse(albumsJson);
            if (document is null || document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return albums;
            }

            foreach (JsonElement albumElement in document.RootElement.EnumerateArray())
            {
                string? albumId = ReadString(albumElement, "id");
                if (albumId is null)
                {
                    continue;
                }

                albums.Add(new ImmichAlbum(
                    albumId,
                    ReadString(albumElement, "albumName") ?? albumId,
                    ReadInt(albumElement, "assetCount")));
            }

            return albums;
        }

        /// <summary>
        /// Разбирает ответ /api/search/metadata.
        /// </summary>
        /// <param name="nextPageNumber">
        /// Номер следующей страницы либо 0, если эта страница последняя. Поле nextPage
        /// сервер отдаёт строкой, и в разных версиях оно бывает и числом, и null.
        /// </param>
        public static List<ImmichAsset> ParseSearchAssets(string searchJson, out int nextPageNumber)
        {
            nextPageNumber = 0;

            using JsonDocument? document = TryParse(searchJson);
            if (document is null
                || !document.RootElement.TryGetProperty("assets", out JsonElement assetsElement))
            {
                return new List<ImmichAsset>();
            }

            if (assetsElement.TryGetProperty("nextPage", out JsonElement nextPageElement))
            {
                nextPageNumber = nextPageElement.ValueKind switch
                {
                    JsonValueKind.Number => nextPageElement.TryGetInt32(out int page) ? page : 0,
                    JsonValueKind.String =>
                        int.TryParse(nextPageElement.GetString(), out int page) ? page : 0,
                    _ => 0,
                };
            }

            return assetsElement.TryGetProperty("items", out JsonElement itemsElement)
                ? ReadAssetArray(itemsElement)
                : new List<ImmichAsset>();
        }

        private static List<ImmichAsset> ReadAssetArray(JsonElement arrayElement)
        {
            var assets = new List<ImmichAsset>();

            if (arrayElement.ValueKind != JsonValueKind.Array)
            {
                return assets;
            }

            foreach (JsonElement assetElement in arrayElement.EnumerateArray())
            {
                string? assetId = ReadString(assetElement, "id");
                if (assetId is null)
                {
                    continue;
                }

                // Удалённое в корзину сервера показывать незачем: пользователь уже сказал,
                // что этот снимок ему не нужен.
                if (ReadBool(assetElement, "isTrashed"))
                {
                    continue;
                }

                bool isVideo = string.Equals(
                    ReadString(assetElement, "type"), "VIDEO", StringComparison.OrdinalIgnoreCase);

                (string cameraName, DateTime? takenAt) = ReadExifInfo(assetElement);

                assets.Add(new ImmichAsset(
                    assetId,
                    ReadString(assetElement, "originalFileName") ?? assetId,
                    isVideo,
                    ParseDurationMilliseconds(ReadString(assetElement, "duration")),
                    ReadString(assetElement, "livePhotoVideoId") ?? string.Empty,
                    cameraName,
                    takenAt));
            }

            return assets;
        }

        /// <summary>
        /// Разбирает длительность клипа, записанную сервером как «00:00:12.345000».
        /// </summary>
        /// <remarks>
        /// Не TimeSpan.Parse: у снимков поле бывает нулевым («0:00:00.00000») или
        /// отсутствует вовсе, и падать из-за подписи под кадром незачем.
        /// </remarks>
        public static int ParseDurationMilliseconds(string? durationText)
        {
            if (string.IsNullOrWhiteSpace(durationText))
            {
                return 0;
            }

            return TimeSpan.TryParse(durationText, CultureInfo.InvariantCulture, out TimeSpan duration)
                   && duration > TimeSpan.Zero
                ? (int)duration.TotalMilliseconds
                : 0;
        }

        /// <summary>
        /// Чем и когда снят кадр — из вложенного объекта exifInfo.
        /// </summary>
        /// <remarks>
        /// Нужно потому, что рамка показывает не оригинал, а превью с сервера, и EXIF
        /// в нём уже вырезан: без этих полей подпись «чем и когда снято» у снимков
        /// Immich пуста.
        /// </remarks>
        private static (string CameraName, DateTime? TakenAt) ReadExifInfo(JsonElement assetElement)
        {
            if (assetElement.ValueKind != JsonValueKind.Object
                || !assetElement.TryGetProperty("exifInfo", out JsonElement exifElement)
                || exifElement.ValueKind != JsonValueKind.Object)
            {
                return (string.Empty, null);
            }

            string cameraName = CameraNameFormatter.Combine(
                ReadString(exifElement, "make"), ReadString(exifElement, "model")) ?? string.Empty;

            DateTime? takenAt = null;
            string? takenText = ReadString(exifElement, "dateTimeOriginal");

            if (takenText is not null
                && DateTime.TryParse(
                    takenText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out DateTime parsedDate))
            {
                // Сервер отдаёт время в UTC, а подпись читают на стене — местное.
                takenAt = parsedDate.ToLocalTime();
            }

            return (cameraName, takenAt);
        }

        /// <summary>
        /// Ответ мог оказаться не JSON — например, страницей входа обратного прокси.
        /// Такой случай разбирается вызывающим кодом как «объектов не нашлось».
        /// </summary>
        private static JsonDocument? TryParse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? ReadString(JsonElement element, string propertyName) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

        private static int ReadInt(JsonElement element, string propertyName) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out int value)
                ? value
                : 0;

        private static bool ReadBool(JsonElement element, string propertyName) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.True;
    }
}
