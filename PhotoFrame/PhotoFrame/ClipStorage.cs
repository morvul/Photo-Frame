using System;
using System.IO;

// Path есть и в Android.*, и в System.IO: нужен однозначный псевдоним.
using IoPath = System.IO.Path;

namespace PhotoFrame
{
    /// <summary>
    /// Где лежат скачанные клипы и годен ли файл к показу.
    /// </summary>
    /// <remarks>
    /// Общее для всех сетевых источников: и альбом Google, и Immich складывают клипы
    /// в один каталог и проверяют их одинаково, поэтому правило про каталог живёт
    /// в одном месте.
    /// </remarks>
    internal static class ClipStorage
    {
        /// <summary>
        /// Куда складываются скачанные клипы.
        /// </summary>
        /// <remarks>
        /// Общая память, а не личная папка приложения: проигрыватель живёт в отдельном
        /// процессе mediaserver и файл из /data/user/0/… открыть не может — подготовка
        /// падает. Не годится и Android/data/&lt;пакет&gt;: этот каталог тоже закрыт
        /// от чужих процессов.
        ///
        /// Имя каталога — Cache: обход папок пропускает такие по имени, поэтому свои же
        /// клипы не попадут в слайд-шоу как локальные файлы.
        /// </remarks>
        public static string Directory
        {
            get
            {
                string? sharedStorageRoot =
                    Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;

                return string.IsNullOrEmpty(sharedStorageRoot)
                    ? IoPath.Combine(FallbackRoot, "clips")
                    : IoPath.Combine(sharedStorageRoot, "PhotoFrame", "Cache");
            }
        }

        /// <summary>Если общей памяти нет вовсе — хоть куда-то, лишь бы показать.</summary>
        private static string FallbackRoot => Microsoft.Maui.Storage.FileSystem.AppDataDirectory;

        /// <summary>Путь к клипу той же заставки: то же имя, расширение .mp4.</summary>
        public static string BuildClipPath(string posterPath) =>
            IoPath.Combine(
                Directory,
                IoPath.GetFileNameWithoutExtension(posterPath) + ".mp4");

        /// <summary>
        /// Проверяет, что файл похож на видео: ошибку от сервера, отданную с кодом 200,
        /// иначе не отличить от клипа. Какие начала считаются годными и почему —
        /// в <see cref="VideoContainerHeader"/>.
        /// </summary>
        public static bool IsPlayableVideoFile(string videoPath)
        {
            try
            {
                var header = new byte[VideoContainerHeader.RequiredBytes];

                using FileStream file = File.OpenRead(videoPath);
                if (file.Read(header, 0, header.Length) < header.Length)
                {
                    return false;
                }

                return VideoContainerHeader.LooksLikeContainer(header);
            }
            catch (Exception readFailure) when (
                readFailure is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>Готовый к показу клип либо null; негодный файл убирается.</summary>
        public static string? FindReady(string posterPath)
        {
            string clipPath = BuildClipPath(posterPath);

            if (!File.Exists(clipPath))
            {
                return null;
            }

            if (IsPlayableVideoFile(clipPath))
            {
                return clipPath;
            }

            // Обрезанный или подменённый страницей ошибки файл проигрыватель всё равно
            // не откроет: убираем, чтобы скачать заново.
            FrameLog.Warn($"Кэшированный клип негоден, убираем: {clipPath}");
            TryDelete(clipPath);
            return null;
        }

        public static void TryDelete(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch (Exception deleteFailure) when (
                deleteFailure is IOException or UnauthorizedAccessException)
            {
                FrameLog.Warn($"Не удалось удалить {filePath}: {deleteFailure.Message}");
            }
        }
    }
}
