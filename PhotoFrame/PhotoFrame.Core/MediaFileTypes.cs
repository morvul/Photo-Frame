using System;
using System.IO;

namespace PhotoFrame
{
    /// <summary>
    /// Какие файлы рамка умеет показывать.
    /// </summary>
    /// <remarks>
    /// Тип определяется по расширению, а не по содержимому: разбирать заголовки сотен
    /// файлов при каждом обходе папок дороже, чем польза от точности, а источники всё
    /// равно наши собственные.
    /// </remarks>
    public static class MediaFileTypes
    {
        /// <summary>Форматы изображений, которые Android 8.1 рисует без доп. кодеков.</summary>
        private static readonly string[] ImageExtensions =
            { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };

        /// <summary>
        /// Форматы видео. Ограничены тем, что гарантированно умеет MediaPlayer на API 27:
        /// экзотические контейнеры лучше не показывать вовсе, чем показывать чёрный экран.
        /// </summary>
        private static readonly string[] VideoExtensions =
            { ".mp4", ".m4v", ".3gp", ".webm", ".mkv" };

        public static bool IsImage(string filePath) => HasExtension(filePath, ImageExtensions);

        public static bool IsVideo(string filePath) => HasExtension(filePath, VideoExtensions);

        /// <summary>True для любого файла, который рамка может показать.</summary>
        public static bool IsSupportedMedia(string filePath) =>
            IsImage(filePath) || IsVideo(filePath);

        /// <summary>Перечень расширений для сообщений пользователю.</summary>
        public static string DescribeSupportedExtensions() =>
            string.Join(", ", ImageExtensions) + ", " + string.Join(", ", VideoExtensions);

        private static bool HasExtension(string filePath, string[] extensions)
        {
            string extension = Path.GetExtension(filePath);

            foreach (string candidate in extensions)
            {
                if (string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
