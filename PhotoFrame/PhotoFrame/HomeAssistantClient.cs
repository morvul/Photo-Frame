using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoFrame
{
    /// <summary>
    /// Читает значения датчиков из Home Assistant по локальному REST API.
    /// </summary>
    /// <remarks>
    /// Выбран именно Home Assistant, а не облако Tuya: долгоживущий токен HA не истекает,
    /// тогда как подписка Tuya IoT Core прекращает работу API по окончании пробного
    /// периода. Обращение идёт по внутреннему адресу, поэтому датчики читаются и при
    /// отсутствии интернета.
    /// </remarks>
    public class HomeAssistantClient
    {
        /// <summary>
        /// От какого количества выбранных датчиков выгоднее один запрос всех состояний,
        /// чем по запросу на каждую сущность.
        /// </summary>
        /// <remarks>
        /// Порог грубый: ответ /api/states — это состояние всего дома (десятки килобайт),
        /// а запрос одной сущности стоит ещё и обращения по сети. На пяти датчиках расходы
        /// примерно равны, дальше выигрывает пакетное чтение.
        /// </remarks>
        private const int BulkReadThreshold = 5;

        private const string CameraEntityPrefix = "camera.";

        private const string FahrenheitUnit = "°F";

        private const string CelsiusUnit = "°C";

        /// <summary>Сколько ждать весь обмен по WebSocket, от подключения до ответа камеры.</summary>
        private static readonly TimeSpan WebSocketTimeout = TimeSpan.FromSeconds(15);

        private readonly HttpClient _httpClient;

        /// <summary>
        /// Клиент для живого потока камеры. Общий <see cref="_httpClient"/> живёт с
        /// лимитом в 15 секунд, а MJPEG-поток — это одно бесконечно долгое соединение,
        /// и такой лимит его рвал бы через четверть минуты, даже когда кадры идут.
        /// У этого клиента лимита нет: о времени выхода из потока заботится сама рамка
        /// (см. <c>MjpegFirstFrameTimeoutMilliseconds</c> в <c>CameraViewPage</c>).
        /// </summary>
        private readonly HttpClient _streamingHttpClient;

        public HomeAssistantClient()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            _streamingHttpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        }

        /// <summary>True, если адрес и токен заданы в настройках рамки.</summary>
        public static bool IsConfigured =>
            !string.IsNullOrWhiteSpace(FrameSettings.HomeAssistantBaseUrl)
            && !string.IsNullOrWhiteSpace(FrameSettings.HomeAssistantToken);

        private static string BaseUrl => FrameSettings.HomeAssistantBaseUrl.TrimEnd('/');

        /// <summary>
        /// Возвращает все сущности с числовым значением и единицей измерения —
        /// то есть то, что имеет смысл вывести на экран.
        /// </summary>
        public async Task<List<HomeAssistantSensor>> GetNumericSensorsAsync(
            CancellationToken cancellationToken = default)
        {
            EnsureConfigured();

            using JsonDocument statesDocument =
                await GetJsonAsync("/api/states", cancellationToken).ConfigureAwait(false);

            var sensors = new List<HomeAssistantSensor>();

            foreach (JsonElement entity in statesDocument.RootElement.EnumerateArray())
            {
                HomeAssistantSensor? sensor = TryReadSensor(entity);
                if (sensor is not null)
                {
                    sensors.Add(sensor);
                }
            }

            // Сортировка по «интересности» класса, затем по имени. Простая сортировка по
            // имени класса ставила первыми battery: шесть батареек шторок заслоняли
            // термометры, ради которых список и открывают.
            sensors.Sort((first, second) =>
            {
                int byPriority = GetDeviceClassPriority(first.DeviceClass)
                                 - GetDeviceClassPriority(second.DeviceClass);
                if (byPriority != 0)
                {
                    return byPriority;
                }

                int byClass = string.Compare(
                    first.DeviceClass ?? "￿", second.DeviceClass ?? "￿",
                    StringComparison.OrdinalIgnoreCase);

                return byClass != 0
                    ? byClass
                    : string.Compare(
                        first.DisplayName, second.DisplayName, StringComparison.OrdinalIgnoreCase);
            });

            return sensors;
        }

        /// <summary>
        /// Насколько класс датчика уместен на фоторамке: чем меньше число, тем выше в списке.
        /// </summary>
        /// <remarks>
        /// Заряд батареек в списке нужен последним: он есть почти у каждого устройства
        /// и на рамке никому не интересен.
        /// </remarks>
        private static int GetDeviceClassPriority(string? deviceClass) => deviceClass switch
        {
            "temperature" => 0,
            "humidity" => 1,
            "pressure" or "atmospheric_pressure" => 2,
            "carbon_dioxide" or "pm25" or "pm10" or "aqi" => 3,
            "illuminance" => 4,
            "power" or "current" or "voltage" => 5,
            "energy" => 6,
            "battery" => 9,
            _ => 7,
        };

        /// <summary>
        /// Читает текущие значения выбранных датчиков в порядке, заданном пользователем.
        /// </summary>
        /// <remarks>
        /// Пока датчиков мало, каждая сущность запрашивается отдельно: это дешевле, чем
        /// тянуть состояние всего дома. С длинным списком выгоднее наоборот — один запрос
        /// вместо десятка обращений по сети, см. <see cref="BulkReadThreshold"/>.
        /// </remarks>
        public async Task<List<(string EntityId, string Text)>> ReadSensorValuesAsync(
            IReadOnlyList<string> entityIds,
            CancellationToken cancellationToken = default)
        {
            EnsureConfigured();

            if (entityIds.Count >= BulkReadThreshold)
            {
                return await ReadSensorValuesInBulkAsync(entityIds, cancellationToken)
                    .ConfigureAwait(false);
            }

            var formattedValues = new List<(string EntityId, string Text)>(entityIds.Count);

            foreach (string entityId in entityIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    using JsonDocument stateDocument = await GetJsonAsync(
                        "/api/states/" + Uri.EscapeDataString(entityId), cancellationToken)
                        .ConfigureAwait(false);

                    HomeAssistantSensor? sensor = TryReadSensor(stateDocument.RootElement);
                    if (sensor is not null)
                    {
                        formattedValues.Add((entityId, sensor.ValueWithUnit));
                    }
                }
                catch (PhotoSourceException singleSensorFailure)
                {
                    // Один недоступный датчик не должен убирать с экрана остальные.
                    System.Diagnostics.Debug.WriteLine(
                        $"Датчик {entityId} не прочитан: {singleSensorFailure.Message}");
                }
            }

            return formattedValues;
        }

        /// <summary>
        /// Все сущности камер, по алфавиту. Пустой список, если камер нет.
        /// </summary>
        /// <remarks>
        /// Алфавитный порядок, а не порядок ответа /api/states: последний не гарантирован
        /// и может меняться при перезапуске Home Assistant, а рамке нужен стабильный выбор.
        /// Список, а не первая камера: одна из нескольких может не отдавать поток (сама
        /// камера offline, интеграция не поддерживает stream), и это видно только выбором.
        /// </remarks>
        public async Task<List<string>> GetCameraEntityIdsAsync(
            CancellationToken cancellationToken = default)
        {
            EnsureConfigured();

            using JsonDocument statesDocument =
                await GetJsonAsync("/api/states", cancellationToken).ConfigureAwait(false);

            var cameraEntityIds = new List<string>();

            foreach (JsonElement entity in statesDocument.RootElement.EnumerateArray())
            {
                if (entity.TryGetProperty("entity_id", out JsonElement entityIdElement)
                    && entityIdElement.GetString() is string entityId
                    && entityId.StartsWith(CameraEntityPrefix, StringComparison.Ordinal))
                {
                    cameraEntityIds.Add(entityId);
                }
            }

            cameraEntityIds.Sort(StringComparer.OrdinalIgnoreCase);
            return cameraEntityIds;
        }

        /// <summary>
        /// Способ показа камеры, которым сама Home Assistant считает нужным её отдавать
        /// панели — "hls" или "web_rtc". Null, если сущность не сообщает об этом вовсе.
        /// </summary>
        /// <remarks>
        /// Диагностика, а не часть показа камеры: перед тем как писать полноценный
        /// WebRTC-клиент, дешевле спросить у самой Home Assistant, предлагает ли она
        /// WebRTC для конкретной сущности, чем выяснять это перебором.
        /// </remarks>
        public async Task<string?> GetCameraFrontendStreamTypeAsync(
            string entityId, CancellationToken cancellationToken = default)
        {
            EnsureConfigured();

            using JsonDocument stateDocument = await GetJsonAsync(
                "/api/states/" + Uri.EscapeDataString(entityId), cancellationToken)
                .ConfigureAwait(false);

            return stateDocument.RootElement.TryGetProperty("attributes", out JsonElement attributes)
                ? ReadStringAttribute(attributes, "frontend_stream_type")
                : null;
        }

        /// <summary>
        /// Бренд-вендор камеры ("Tuya", "Ring", ...). Null, если сущность его не сообщает.
        /// </summary>
        /// <remarks>
        /// Нужен, чтобы отличать облачные камеры, которые Home Assistant не может отдать
        /// живым HLS-потоком без локального RTSP-источника, от камер с реальным стримом.
        /// </remarks>
        public async Task<string?> GetCameraBrandAsync(
            string entityId, CancellationToken cancellationToken = default)
        {
            EnsureConfigured();

            using JsonDocument stateDocument = await GetJsonAsync(
                "/api/states/" + Uri.EscapeDataString(entityId), cancellationToken)
                .ConfigureAwait(false);

            return stateDocument.RootElement.TryGetProperty("attributes", out JsonElement attributes)
                ? ReadStringAttribute(attributes, "brand")
                : null;
        }

        /// <summary>
        /// Запрашивает адрес живого потока камеры со звуком.
        /// </summary>
        /// <remarks>
        /// REST-адреса Home Assistant отдают только снимок или MJPEG без звука.
        /// Ссылку на HLS-плейлист со звуком отдаёт лишь команда camera/stream по
        /// WebSocket API. Соединение открывается на одну команду и закрывается сразу
        /// после ответа — держать его на всё время просмотра не нужно, ссылка уже несёт
        /// временный токен доступа в собственном пути.
        /// </remarks>
        public async Task<string> GetCameraStreamUrlAsync(
            string entityId, CancellationToken cancellationToken = default)
        {
            EnsureConfigured();

            using var socket = new ClientWebSocket();

            // Своя граница по времени: без неё зависший обмен по WebSocket держал бы
            // страницу камеры на «Подключение...» бесконечно — ни исключения, ни следа
            // в журнале, только вечный спиннер.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(WebSocketTimeout);
            CancellationToken linkedToken = timeoutCts.Token;

            try
            {
                await socket.ConnectAsync(BuildWebSocketUri(), linkedToken).ConfigureAwait(false);

                // Первое сообщение сервера — приглашение авторизоваться, само оно не нужно.
                using (await ReceiveJsonAsync(socket, linkedToken).ConfigureAwait(false))
                {
                }

                await SendJsonAsync(
                    socket,
                    new { type = "auth", access_token = FrameSettings.HomeAssistantToken },
                    linkedToken).ConfigureAwait(false);

                using JsonDocument authResponse =
                    await ReceiveJsonAsync(socket, linkedToken).ConfigureAwait(false);

                if (ReadStringAttribute(authResponse.RootElement, "type") != "auth_ok")
                {
                    throw new PhotoSourceException("Home Assistant отклонил токен по WebSocket.");
                }

                await SendJsonAsync(
                    socket,
                    new { id = 1, type = "camera/stream", entity_id = entityId },
                    linkedToken).ConfigureAwait(false);

                using JsonDocument streamResponse =
                    await ReceiveJsonAsync(socket, linkedToken).ConfigureAwait(false);

                if (!streamResponse.RootElement.TryGetProperty("success", out JsonElement successElement)
                    || successElement.ValueKind != JsonValueKind.True
                    || !streamResponse.RootElement.TryGetProperty("result", out JsonElement resultElement)
                    || !resultElement.TryGetProperty("url", out JsonElement urlElement)
                    || urlElement.GetString() is not string streamPath)
                {
                    throw new PhotoSourceException("Камера не отдаёт поток через Home Assistant.");
                }

                string streamUrl = streamPath.StartsWith("/", StringComparison.Ordinal)
                    ? BaseUrl + streamPath
                    : streamPath;

                // Разово в журнал: тот же адрес можно проверить curl'ом отдельно от
                // рамки, чтобы понять, кто именно не отвечает — Home Assistant или
                // ExoPlayer на устройстве.
                FrameLog.Info($"Поток камеры {entityId}: {streamUrl}");

                return streamUrl;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                FrameLog.Warn($"Home Assistant не ответил по WebSocket за {WebSocketTimeout}.");
                throw new PhotoSourceException("Камера Home Assistant не ответила по WebSocket вовремя.");
            }
            catch (WebSocketException socketFailure)
            {
                FrameLog.Warn(
                    $"WebSocket Home Assistant недоступен ({socketFailure.WebSocketErrorCode}): "
                    + $"{socketFailure.Message} — {socketFailure.InnerException?.Message}");
                throw new PhotoSourceException("Home Assistant не ответил по WebSocket.", socketFailure);
            }
            finally
            {
                if (socket.State == WebSocketState.Open)
                {
                    try
                    {
                        await socket
                            .CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (WebSocketException)
                    {
                        // Соединение всё равно больше не нужно — не закрылось, не беда.
                    }
                }
            }
        }

        private static Uri BuildWebSocketUri()
        {
            string httpBaseUrl = BaseUrl;

            string scheme = httpBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? "wss://"
                : "ws://";

            int schemeSeparatorIndex = httpBaseUrl.IndexOf("://", StringComparison.Ordinal);
            string hostAndPath = schemeSeparatorIndex >= 0
                ? httpBaseUrl[(schemeSeparatorIndex + 3)..]
                : httpBaseUrl;

            return new Uri(scheme + hostAndPath + "/api/websocket");
        }

        private static Task SendJsonAsync(
            ClientWebSocket socket, object payload, CancellationToken cancellationToken)
        {
            byte[] payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            return socket.SendAsync(
                payloadBytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }

        private static async Task<JsonDocument> ReceiveJsonAsync(
            ClientWebSocket socket, CancellationToken cancellationToken)
        {
            using var messageBuffer = new MemoryStream();
            var receiveBuffer = new byte[8192];

            while (true)
            {
                WebSocketReceiveResult receiveResult = await socket
                    .ReceiveAsync(receiveBuffer, cancellationToken).ConfigureAwait(false);

                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    throw new PhotoSourceException("Home Assistant закрыл WebSocket-соединение.");
                }

                messageBuffer.Write(receiveBuffer, 0, receiveResult.Count);

                if (receiveResult.EndOfMessage)
                {
                    break;
                }
            }

            messageBuffer.Position = 0;
            return JsonDocument.Parse(messageBuffer);
        }

        /// <summary>
        /// Живой поток камеры без звука — кадры MJPEG по одному соединению, а не
        /// снимок на каждый запрос.
        /// </summary>
        /// <remarks>
        /// Используется, когда Home Assistant сообщает <c>frontend_stream_type = "mjpeg"</c>:
        /// кадры идут одним долгим HTTP-ответом в формате multipart/x-mixed-replace, а не
        /// снимком на каждый запрос. Соединение одно на весь просмотр, а не одно на кадр,
        /// как у ручного опроса <see cref="GetCameraSnapshotAsync"/>. Для камер, которые
        /// Home Assistant отдаёт как HLS или WebRTC, этот способ не подходит — служит
        /// ответом на <see cref="GetCameraFrontendStreamTypeAsync"/>.
        /// </remarks>
        public async IAsyncEnumerable<byte[]> StreamCameraMjpegFramesAsync(
            string entityId,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            EnsureConfigured();

            using HttpResponseMessage response = await SendGetAsync(
                _streamingHttpClient,
                "/api/camera_proxy_stream/" + Uri.EscapeDataString(entityId),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            string? boundary = ReadMultipartBoundary(response.Content.Headers.ContentType);
            if (boundary is null)
            {
                throw new PhotoSourceException(
                    "Home Assistant не отдаёт поток камеры в формате multipart.");
            }

            Stream responseStream = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            await using (responseStream.ConfigureAwait(false))
            {
                await foreach (byte[] frame in ReadMjpegFramesAsync(responseStream, boundary, cancellationToken)
                    .ConfigureAwait(false))
                {
                    yield return frame;
                }
            }
        }

        private static string? ReadMultipartBoundary(MediaTypeHeaderValue? contentType)
        {
            if (contentType is null
                || !string.Equals(
                    contentType.MediaType, "multipart/x-mixed-replace", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            foreach (NameValueHeaderValue parameter in contentType.Parameters)
            {
                if (string.Equals(parameter.Name, "boundary", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(parameter.Value))
                {
                    return parameter.Value.Trim('"');
                }
            }

            return null;
        }

        /// <summary>Сколько байт читать за раз из сетевого потока.</summary>
        private const int MjpegReadChunkBytes = 16 * 1024;

        /// <summary>
        /// Предел на размер одной части multipart-ответа. Один кадр JPEG с камеры
        /// не бывает такого размера — превышение значит, что поток пошёл не так
        /// (нет границ вовсе, или граница не совпадает с ожидаемой).
        /// </summary>
        private const int MjpegMaxPartBytes = 4 * 1024 * 1024;

        private static readonly byte[] MultipartHeaderTerminator = { 13, 10, 13, 10 };

        /// <summary>
        /// Разбирает multipart/x-mixed-replace на отдельные кадры JPEG.
        /// </summary>
        /// <remarks>
        /// Кадром считается всё между концом заголовков очередной части и следующей
        /// границей: у частей MJPEG заголовки короткие (Content-Type, иногда
        /// Content-Length), и искать конец кадра по самим байтам JPEG не нужно —
        /// граница уже размечает его точно.
        /// </remarks>
        private static async IAsyncEnumerable<byte[]> ReadMjpegFramesAsync(
            Stream stream,
            string boundary,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            byte[] boundaryMarker = Encoding.ASCII.GetBytes(
                boundary.StartsWith("--", StringComparison.Ordinal) ? boundary : "--" + boundary);

            byte[] buffer = new byte[MjpegReadChunkBytes];
            int length = 0;

            while (true)
            {
                if (length == buffer.Length)
                {
                    if (buffer.Length >= MjpegMaxPartBytes)
                    {
                        throw new PhotoSourceException(
                            "Поток камеры Home Assistant превысил ожидаемый размер кадра.");
                    }

                    Array.Resize(ref buffer, buffer.Length * 2);
                }

                int bytesRead = await stream
                    .ReadAsync(buffer.AsMemory(length, buffer.Length - length), cancellationToken)
                    .ConfigureAwait(false);

                if (bytesRead == 0)
                {
                    yield break;
                }

                length += bytesRead;

                while (TryExtractFrame(buffer, length, boundaryMarker, out byte[]? frame, out int consumedLength))
                {
                    length -= consumedLength;
                    Array.Copy(buffer, consumedLength, buffer, 0, length);

                    if (frame is not null)
                    {
                        yield return frame;
                    }
                }
            }
        }

        /// <summary>
        /// Пытается вырезать один кадр из начала буфера. Возвращает false, если
        /// в буфере пока нет целой части — ждём следующего чтения из сети.
        /// </summary>
        private static bool TryExtractFrame(
            byte[] buffer, int length, byte[] boundaryMarker, out byte[]? frame, out int consumedLength)
        {
            frame = null;
            consumedLength = 0;

            int firstBoundaryIndex = IndexOf(buffer, length, boundaryMarker, 0);
            if (firstBoundaryIndex < 0)
            {
                return false;
            }

            int partStart = firstBoundaryIndex + boundaryMarker.Length;

            int headerEnd = IndexOf(buffer, length, MultipartHeaderTerminator, partStart);
            int nextBoundaryIndex = IndexOf(buffer, length, boundaryMarker, partStart);

            if (headerEnd < 0 || nextBoundaryIndex < 0)
            {
                return false;
            }

            int frameStart = headerEnd + MultipartHeaderTerminator.Length;
            int frameEnd = nextBoundaryIndex;

            // Перед следующей границей стоит CRLF, который в сам кадр не входит.
            while (frameEnd > frameStart && (buffer[frameEnd - 1] == 10 || buffer[frameEnd - 1] == 13))
            {
                frameEnd--;
            }

            if (frameEnd > frameStart)
            {
                frame = new byte[frameEnd - frameStart];
                Array.Copy(buffer, frameStart, frame, 0, frame.Length);
            }

            consumedLength = nextBoundaryIndex;
            return true;
        }

        private static int IndexOf(byte[] haystack, int haystackLength, byte[] needle, int startIndex)
        {
            int lastPossibleStart = haystackLength - needle.Length;
            for (int i = startIndex; i <= lastPossibleStart; i++)
            {
                int j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j])
                {
                    j++;
                }

                if (j == needle.Length)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Читает значения одним запросом всех состояний, сохраняя порядок выбора.
        /// </summary>
        /// <remarks>
        /// Датчики, которых в ответе нет (сущность удалили или переименовали), просто
        /// пропускаются: остальные значения показать всё равно нужно.
        /// </remarks>
        private async Task<List<(string EntityId, string Text)>> ReadSensorValuesInBulkAsync(
            IReadOnlyList<string> entityIds,
            CancellationToken cancellationToken)
        {
            using JsonDocument statesDocument =
                await GetJsonAsync("/api/states", cancellationToken).ConfigureAwait(false);

            var requestedIds = new HashSet<string>(entityIds, StringComparer.OrdinalIgnoreCase);
            var valuesByEntityId = new Dictionary<string, string>(
                entityIds.Count, StringComparer.OrdinalIgnoreCase);

            foreach (JsonElement entity in statesDocument.RootElement.EnumerateArray())
            {
                HomeAssistantSensor? sensor = TryReadSensor(entity);
                if (sensor is not null && requestedIds.Contains(sensor.EntityId))
                {
                    valuesByEntityId[sensor.EntityId] = sensor.ValueWithUnit;
                }
            }

            var formattedValues = new List<(string EntityId, string Text)>(entityIds.Count);

            foreach (string entityId in entityIds)
            {
                if (valuesByEntityId.TryGetValue(entityId, out string? valueWithUnit))
                {
                    formattedValues.Add((entityId, valueWithUnit));
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Датчик {entityId} отсутствует в ответе Home Assistant.");
                }
            }

            return formattedValues;
        }

        /// <summary>
        /// Разбирает одну сущность. Возвращает null, если это не числовой датчик:
        /// у выключателей и текстовых сущностей показывать нечего.
        /// </summary>
        private static HomeAssistantSensor? TryReadSensor(JsonElement entity)
        {
            if (!entity.TryGetProperty("entity_id", out JsonElement entityIdElement)
                || entityIdElement.GetString() is not string entityId)
            {
                return null;
            }

            if (!entity.TryGetProperty("state", out JsonElement stateElement)
                || stateElement.GetString() is not string state)
            {
                return null;
            }

            if (!entity.TryGetProperty("attributes", out JsonElement attributes))
            {
                return null;
            }

            string? unit = ReadStringAttribute(attributes, "unit_of_measurement");
            if (string.IsNullOrEmpty(unit))
            {
                return null;
            }

            // Датчик может быть "unavailable" или "unknown" — такое не показываем.
            if (!double.TryParse(
                    state, NumberStyles.Float, CultureInfo.InvariantCulture, out double numericState))
            {
                return null;
            }

            string displayName = ReadStringAttribute(attributes, "friendly_name") ?? entityId;
            string? deviceClass = ReadStringAttribute(attributes, "device_class");

            // Рамка всегда показывает температуру в Цельсиях: часть интеграций отдаёт
            // Фаренгейты независимо от системы единиц, выбранной в самом Home Assistant,
            // и разбираться в настройках каждого источника ради одной рамки не нужно.
            if (deviceClass == "temperature" && unit == FahrenheitUnit)
            {
                numericState = (numericState - 32) * 5 / 9;
                state = numericState.ToString("0.#", CultureInfo.InvariantCulture);
                unit = CelsiusUnit;
            }

            return new HomeAssistantSensor(entityId, displayName, state, unit, deviceClass);
        }

        private static string? ReadStringAttribute(JsonElement attributes, string name) =>
            attributes.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        /// <summary>
        /// Снимок камеры одним запросом — простой JPEG без звука и без задержки ffmpeg.
        /// </summary>
        /// <remarks>
        /// Резервный способ показать камеру, если поток из <see cref="StreamCameraMjpegFramesAsync"/>
        /// не открылся вовсе: снимок для облачных интеграций (Tuya и подобные) обычно идёт
        /// отдельным лёгким запросом к их облаку и не зависит от того, поднимается ли поток.
        /// </remarks>
        public async Task<byte[]> GetCameraSnapshotAsync(
            string entityId, CancellationToken cancellationToken = default)
        {
            EnsureConfigured();

            using HttpResponseMessage response = await SendGetAsync(
                "/api/camera_proxy/" + Uri.EscapeDataString(entityId), cancellationToken)
                .ConfigureAwait(false);

            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        private Task<HttpResponseMessage> SendGetAsync(
            string relativePath, CancellationToken cancellationToken) =>
            SendGetAsync(_httpClient, relativePath, HttpCompletionOption.ResponseContentRead, cancellationToken);

        /// <remarks>
        /// <paramref name="completionOption"/> — <see cref="HttpCompletionOption.ResponseHeadersRead"/>
        /// нужен для потока камеры: тело там не заканчивается никогда, и ждать его
        /// целиком (обычное поведение GetAsync) означало бы никогда не вернуться из вызова.
        /// </remarks>
        private Task<HttpResponseMessage> SendGetAsync(
            string relativePath, HttpCompletionOption completionOption, CancellationToken cancellationToken) =>
            SendGetAsync(_httpClient, relativePath, completionOption, cancellationToken);

        private async Task<HttpResponseMessage> SendGetAsync(
            HttpClient client, string relativePath, HttpCompletionOption completionOption, CancellationToken cancellationToken)
        {
            // Клиент — общий на всё приложение и живёт дольше настроек: адрес и токен
            // могли поменять на экране настроек уже после его создания, поэтому
            // заголовок ставится заново на каждый запрос, а не один раз в конструкторе.
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", FrameSettings.HomeAssistantToken);

            HttpResponseMessage response;
            try
            {
                response = await client
                    .GetAsync(BaseUrl + relativePath, completionOption, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException networkFailure)
            {
                // Сообщение на экране рамки одно и то же для любой сетевой причины,
                // а настоящая — только здесь, в журнале adb logcat.
                FrameLog.Warn(
                    $"Home Assistant недоступен ({relativePath}): "
                    + $"{networkFailure.Message} — {networkFailure.InnerException?.Message}");
                throw new PhotoSourceException(
                    "Home Assistant недоступен по указанному адресу.", networkFailure);
            }
            catch (TaskCanceledException timeout) when (!cancellationToken.IsCancellationRequested)
            {
                FrameLog.Warn($"Home Assistant не ответил вовремя ({relativePath}): {timeout.Message}");
                throw new PhotoSourceException("Home Assistant не ответил вовремя.", timeout);
            }
            catch (UriFormatException badUrl)
            {
                throw new PhotoSourceException(
                    "Адрес Home Assistant выглядит некорректно.", badUrl);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized
                || response.StatusCode == HttpStatusCode.Forbidden)
            {
                response.Dispose();
                throw new PhotoSourceException(
                    "Home Assistant отклонил токен. Создайте новый долгоживущий токен.");
            }

            if (!response.IsSuccessStatusCode)
            {
                int statusCode = (int)response.StatusCode;
                response.Dispose();
                throw new PhotoSourceException($"Home Assistant вернул ошибку ({statusCode}).");
            }

            return response;
        }

        private async Task<JsonDocument> GetJsonAsync(
            string relativePath, CancellationToken cancellationToken)
        {
            using HttpResponseMessage response =
                await SendGetAsync(relativePath, cancellationToken).ConfigureAwait(false);

            string responseBody = await response.Content
                .ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return JsonDocument.Parse(responseBody);
            }
            catch (JsonException malformedJson)
            {
                throw new PhotoSourceException(
                    "Home Assistant вернул некорректный JSON.", malformedJson);
            }
        }

        private static void EnsureConfigured()
        {
            if (!IsConfigured)
            {
                throw new PhotoSourceException(
                    "Не заданы адрес и токен Home Assistant в настройках рамки.");
            }
        }
    }
}
