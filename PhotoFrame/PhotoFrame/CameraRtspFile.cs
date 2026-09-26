using System;
using System.IO;

// Path есть и в Android.*, и в System.IO: нужен однозначный псевдоним.
using IoPath = System.IO.Path;

namespace PhotoFrame
{
    /// <summary>
    /// Читает адрес и название прямой IP-камеры (RTSP) из файлов в общей памяти рамки.
    /// </summary>
    /// <remarks>
    /// Прямой RTSP-поток вводить на экранной клавиатуре рамки неудобно (длинный адрес с
    /// двоеточиями и @), поэтому адрес кладётся на рамку файлом по USB — тот же приём, что у
    /// ключа Home Assistant (см. <see cref="HomeAssistantKeyFile"/>) и Immich. Файл только
    /// читается и остаётся лежать: при переустановке он же и восстановит настройку.
    /// </remarks>
    internal static class CameraRtspFile
    {
        private const string FrameDirectoryName = "PhotoFrame";

        /// <summary>Файл с адресом RTSP-потока (без перевода строки на конце).</summary>
        private const string UrlFileName = "camera_rtsp.txt";

        /// <summary>Файл с названием камеры (необязательный).</summary>
        private const string NameFileName = "camera_rtsp_name.txt";

        /// <summary>Длина, после которой содержимое явно не адрес RTSP.</summary>
        private const int MaxLength = 1024;

        private static string FrameDirectoryPath
        {
            get
            {
                string? sharedStorageRoot =
                    Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;

                return IoPath.Combine(sharedStorageRoot ?? string.Empty, FrameDirectoryName);
            }
        }

        /// <summary>Путь к файлу с адресом — показывается в подсказке на экране настроек.</summary>
        public static string UrlFilePath => IoPath.Combine(FrameDirectoryPath, UrlFileName);

        /// <summary>Путь к файлу с названием.</summary>
        public static string NameFilePath => IoPath.Combine(FrameDirectoryPath, NameFileName);

        /// <summary>
        /// Возвращает адрес RTSP-потока из файла либо пустую строку, если файла нет.
        /// </summary>
        public static string TryReadUrl()
        {
            string value = ReadFile(UrlFilePath, "адрес IP-камеры");

            // Адрес мог быть вставлен с хвостовым «;» или пробелами — отбрасываем лишнее.
            return value.Trim().TrimEnd(';').Trim();
        }

        /// <summary>Возвращает название камеры из файла либо пустую строку.</summary>
        public static string TryReadName() => ReadFile(NameFilePath, "название IP-камеры").Trim();

        private static string ReadFile(string filePath, string what)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return string.Empty;
                }

                if (new FileInfo(filePath).Length > MaxLength)
                {
                    FrameLog.Warn($"Файл {filePath} слишком велик для {what}.");
                    return string.Empty;
                }

                return File.ReadAllText(filePath);
            }
            catch (Exception readFailure) when (
                readFailure is IOException or UnauthorizedAccessException)
            {
                FrameLog.Warn($"{what} не прочитан из файла: {readFailure.Message}");
                return string.Empty;
            }
        }
    }
}
