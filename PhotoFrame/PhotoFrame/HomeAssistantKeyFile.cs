using System;
using System.IO;

// Path есть и в Android.*, и в System.IO: нужен однозначный псевдоним.
using IoPath = System.IO.Path;

namespace PhotoFrame
{
    /// <summary>
    /// Читает токен доступа к Home Assistant из файла в общей памяти рамки.
    /// </summary>
    /// <remarks>
    /// Долгоживущий токен Home Assistant — длинная случайная строка, а у рамки нет ни
    /// клавиатуры, ни мыши: набирать её пультом по экранной клавиатуре занятие
    /// безнадёжное. Файл же кладётся на рамку по USB обычным копированием, как и
    /// снимки, — тот же приём, что и у ключа Immich (см. <see cref="ImmichKeyFile"/>).
    ///
    /// Файл только читается и остаётся лежать, где лежал: удалять чужие файлы
    /// приложение не должно, а при переустановке он же и восстановит настройку.
    /// </remarks>
    internal static class HomeAssistantKeyFile
    {
        private const string FrameDirectoryName = "PhotoFrame";

        /// <summary>Имя файла с токеном; лежит рядом с ключом Immich, корзиной и журналами.</summary>
        private const string KeyFileName = "homeassistant.key";

        /// <summary>
        /// Длина, после которой содержимое явно не токен, а случайно переименованный файл.
        /// Долгоживущий токен Home Assistant — это JWT, обычно на одну-две сотни символов.
        /// </summary>
        private const int MaxKeyLength = 2048;

        /// <summary>Путь к файлу — показывается в подсказке на экране настроек.</summary>
        public static string FilePath
        {
            get
            {
                string? sharedStorageRoot =
                    Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;

                return IoPath.Combine(
                    sharedStorageRoot ?? string.Empty, FrameDirectoryName, KeyFileName);
            }
        }

        /// <summary>
        /// Возвращает токен из файла либо пустую строку, если файла нет или он не похож
        /// на токен.
        /// </summary>
        /// <remarks>
        /// Пробелы и перевод строки на конце отбрасываются: их добавит любой редактор,
        /// а Home Assistant с таким токеном ответит отказом.
        /// </remarks>
        public static string TryRead()
        {
            string keyFilePath = FilePath;

            try
            {
                if (!File.Exists(keyFilePath))
                {
                    return string.Empty;
                }

                if (new FileInfo(keyFilePath).Length > MaxKeyLength)
                {
                    FrameLog.Warn($"Файл {keyFilePath} слишком велик для токена доступа.");
                    return string.Empty;
                }

                return File.ReadAllText(keyFilePath).Trim();
            }
            catch (Exception readFailure) when (
                readFailure is IOException or UnauthorizedAccessException)
            {
                FrameLog.Warn($"Токен Home Assistant не прочитан из файла: {readFailure.Message}");
                return string.Empty;
            }
        }
    }
}
