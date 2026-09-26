using System;
using System.IO;

// Path есть и в Android.*, и в System.IO: нужен однозначный псевдоним.
using IoPath = System.IO.Path;

namespace PhotoFrame
{
    /// <summary>
    /// Читает ключ доступа к Immich из файла в общей памяти рамки.
    /// </summary>
    /// <remarks>
    /// Ключ Immich — это сорок случайных символов, а у рамки нет ни клавиатуры, ни
    /// мыши: набирать его пультом по экранной клавиатуре занятие безнадёжное. Файл
    /// же кладётся на рамку по USB обычным копированием, как и снимки.
    ///
    /// Файл только читается и остаётся лежать, где лежал: удалять чужие файлы
    /// приложение не должно, а при переустановке он же и восстановит настройку.
    /// Хранится ключ там же, куда попадёт и так, — в памяти самой рамки; смысл
    /// файла не в секретности, а в том, чтобы его не пришлось вводить руками.
    /// </remarks>
    internal static class ImmichKeyFile
    {
        private const string FrameDirectoryName = "PhotoFrame";

        /// <summary>Имя файла с ключом; лежит рядом с корзиной и журналами.</summary>
        private const string KeyFileName = "immich.key";

        /// <summary>
        /// Второе имя того же файла: на части рамок ключ лежит под ним, и файл должен
        /// читаться наравне с текущим — переименовывать чужие файлы приложение не вправе,
        /// а из-за расхождения в имени ключ просто не читался бы.
        /// </summary>
        private const string LegacyKeyFileName = "immich_key.txt";

        /// <summary>
        /// Длина, после которой содержимое явно не ключ, а случайно переименованный файл.
        /// Ключ Immich — около сорока символов.
        /// </summary>
        private const int MaxKeyLength = 512;

        /// <summary>Путь к файлу — показывается в подсказке на экране настроек.</summary>
        public static string FilePath => BuildPath(KeyFileName);

        /// <summary>Путь к файлу со вторым именем — тоже ради подсказки.</summary>
        public static string LegacyFilePath => BuildPath(LegacyKeyFileName);

        /// <summary>
        /// Возвращает ключ из файла либо пустую строку, если файла нет или он не похож
        /// на ключ.
        /// </summary>
        /// <remarks>
        /// Сначала ищется файл с текущим именем, затем — со вторым: на части рамок ключ
        /// положен под ним, и он работает так же.
        /// </remarks>
        public static string TryRead()
        {
            string keyFromCurrentName = ReadKeyFile(FilePath);
            return keyFromCurrentName.Length > 0
                ? keyFromCurrentName
                : ReadKeyFile(LegacyFilePath);
        }

        private static string BuildPath(string fileName)
        {
            string? sharedStorageRoot =
                Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;

            return IoPath.Combine(sharedStorageRoot ?? string.Empty, FrameDirectoryName, fileName);
        }

        /// <summary>
        /// Читает один файл. Пробелы и перевод строки на конце отбрасываются: их добавит
        /// любой редактор, а сервер с таким ключом ответит отказом.
        /// </summary>
        private static string ReadKeyFile(string keyFilePath)
        {
            try
            {
                if (!File.Exists(keyFilePath))
                {
                    return string.Empty;
                }

                if (new FileInfo(keyFilePath).Length > MaxKeyLength)
                {
                    FrameLog.Warn($"Файл {keyFilePath} слишком велик для ключа доступа.");
                    return string.Empty;
                }

                return File.ReadAllText(keyFilePath).Trim();
            }
            catch (Exception readFailure) when (
                readFailure is IOException or UnauthorizedAccessException)
            {
                FrameLog.Warn($"Ключ Immich не прочитан из файла: {readFailure.Message}");
                return string.Empty;
            }
        }
    }
}
