namespace PhotoFrame
{
    /// <summary>
    /// Подпись кадра для наложения: папка и имя файла.
    /// </summary>
    /// <remarks>
    /// Имя папки добавляется к имени файла: в папках вроде DCIM снимки называются
    /// IMG_1234.jpg, и по одному имени не понять, откуда кадр, — а имя папки как раз
    /// и говорит, из какой поездки или какого архива он взялся.
    /// </remarks>
    public static class MediaLabelFormatter
    {
        /// <summary>
        /// Возвращает «папка/файл», либо только имя файла, если папки в пути нет.
        /// </summary>
        public static string Describe(string mediaPath)
        {
            if (string.IsNullOrWhiteSpace(mediaPath))
            {
                return string.Empty;
            }

            // Лишние разделители на конце иначе съедают имя файла.
            string trimmedPath = mediaPath.TrimEnd('/', '\\');
            string fileName = GetLastSegment(trimmedPath);

            string directoryPath = trimmedPath[..^fileName.Length].TrimEnd('/', '\\');
            string folderName = GetLastSegment(directoryPath);

            return folderName.Length == 0 ? fileName : folderName + "/" + fileName;
        }

        private static string GetLastSegment(string path)
        {
            int separatorIndex = path.LastIndexOfAny(new[] { '/', '\\' });
            return separatorIndex < 0 ? path : path[(separatorIndex + 1)..];
        }
    }
}
