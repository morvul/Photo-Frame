using System;
using System.Text.Json;

namespace PhotoFrame
{
    /// <summary>
    /// Проверка, что найденный в сети сервер — действительно Home Assistant, а не что-то
    /// ещё, что случайно слушает тот же порт.
    /// </summary>
    /// <remarks>
    /// Разбор вынесен в Core, чтобы покрыть его тестами: собрать MAUI под Android ради
    /// проверки одной строки JSON невозможно.
    /// </remarks>
    public static class HomeAssistantDiscovery
    {
        /// <summary>Порт, на котором слушает почти любая установка Home Assistant.</summary>
        public const int DefaultPort = 8123;

        /// <summary>
        /// True, если тело ответа /manifest.json похоже на манифест самой Home Assistant.
        /// </summary>
        /// <remarks>
        /// /manifest.json — файл PWA фронтенда, отдаётся без авторизации и не менялся
        /// между версиями: надёжнее, чем считать сервером Home Assistant всё, что просто
        /// ответило на порту 8123.
        /// </remarks>
        public static bool IsHomeAssistantManifest(string? manifestJson)
        {
            if (string.IsNullOrWhiteSpace(manifestJson))
            {
                return false;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(manifestJson);
                return document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("name", out JsonElement nameElement)
                    && nameElement.ValueKind == JsonValueKind.String
                    && string.Equals(
                        nameElement.GetString(), "Home Assistant", StringComparison.Ordinal);
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}
