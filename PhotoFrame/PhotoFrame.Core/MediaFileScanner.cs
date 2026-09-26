namespace PhotoFrame
{
    /// <summary>
    /// Обход папок в поисках снимков и видео.
    /// </summary>
    /// <remarks>
    /// Обход написан вручную, а не через SearchOption.AllDirectories, ради возможности
    /// не заходить в служебные каталоги: приложения хранят миниатюры рядом с оригиналами,
    /// и без фильтра слайд-шоу наполняется размытыми превью. У Frameo, например, в
    /// frameo_files/cache/galleryThumbnails лежит 151 превью по ~19 КБ против 66
    /// полноразмерных снимков в frameo_files/media.
    /// </remarks>
    public static class MediaFileScanner
    {
        /// <summary>
        /// Куда убираются кадры, снятые с показа кнопкой «Убрать».
        /// </summary>
        /// <remarks>
        /// Имя вынесено сюда, а не в код рамки, именно затем, чтобы обход и корзина
        /// не разошлись: попади корзина внутрь выбранной папки, убранные снимки
        /// вернулись бы в слайд-шоу на следующем обновлении.
        /// </remarks>
        public const string TrashDirectoryName = "Trash";

        /// <summary>Каталоги, в которые не заходим.</summary>
        private static readonly string[] SkippedDirectoryNames =
            { "cache", "caches", "thumbnails", "temp", "tmp", TrashDirectoryName };

        /// <summary>
        /// Перечисляет медиафайлы в папке. Порядок стабильный: внутри каталога — по имени,
        /// подкаталоги — по алфавиту.
        /// </summary>
        public static IEnumerable<string> EnumerateMediaFiles(string rootPath, bool recurse)
        {
            if (!Directory.Exists(rootPath))
            {
                yield break;
            }

            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(rootPath);

            while (pendingDirectories.Count > 0)
            {
                string currentDirectory = pendingDirectories.Pop();

                string[] filePaths;
                try
                {
                    filePaths = Directory.GetFiles(currentDirectory);
                }
                catch (Exception scanFailure) when (
                    scanFailure is IOException or UnauthorizedAccessException)
                {
                    // Недоступный каталог не должен ломать обход остальных.
                    System.Diagnostics.Debug.WriteLine(
                        $"Каталог {currentDirectory} не прочитан: {scanFailure.Message}");
                    continue;
                }

                Array.Sort(filePaths, StringComparer.OrdinalIgnoreCase);

                foreach (string filePath in filePaths)
                {
                    if (MediaFileTypes.IsSupportedMedia(filePath))
                    {
                        yield return filePath;
                    }
                }

                if (!recurse)
                {
                    continue;
                }

                string[] subdirectories;
                try
                {
                    subdirectories = Directory.GetDirectories(currentDirectory);
                }
                catch (Exception scanFailure) when (
                    scanFailure is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                Array.Sort(subdirectories, StringComparer.OrdinalIgnoreCase);

                // Кладём в стек в обратном порядке, чтобы обход шёл по алфавиту.
                for (int index = subdirectories.Length - 1; index >= 0; index--)
                {
                    if (!ShouldSkipDirectory(subdirectories[index]))
                    {
                        pendingDirectories.Push(subdirectories[index]);
                    }
                }
            }
        }

        /// <summary>Каталоги с миниатюрами и кэшем в слайд-шоу не нужны.</summary>
        public static bool ShouldSkipDirectory(string directoryPath)
        {
            string directoryName = Path.GetFileName(directoryPath);

            // Скрытые каталоги вроде .thumbnails создаёт сама система.
            if (directoryName.StartsWith('.'))
            {
                return true;
            }

            if (directoryName.Contains("thumbnail", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (string skippedName in SkippedDirectoryNames)
            {
                if (directoryName.Equals(skippedName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
