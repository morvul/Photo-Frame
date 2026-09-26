using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace PhotoFrame
{
    /// <summary>
    /// Показывает снимки из одной или нескольких папок на устройстве.
    /// </summary>
    /// <remarks>
    /// Файлы намеренно НЕ копируются в кэш приложения: на рамке всего несколько гигабайт,
    /// и дублировать содержимое папки незачем. Вместо этого ведётся манифест с путями
    /// в порядке показа — тот же приём, что и для альбома Google, поэтому страница
    /// слайд-шоу об источнике ничего не знает.
    /// </remarks>
    public class LocalFolderPhotoSource : IPhotoSource
    {
        private const string ManifestFileName = "local.manifest";

        /// <inheritdoc />
        public bool IsConfigured => FrameSettings.LocalFolderPaths.Length > 0;

        private static string ManifestPath =>
            Path.Combine(FileSystem.AppDataDirectory, ManifestFileName);

        /// <inheritdoc />
        public string DescribeConfiguration()
        {
            string[] folderPaths = FrameSettings.LocalFolderPaths;
            if (folderPaths.Length == 0)
            {
                return "Папки не выбраны";
            }

            int existingFolderCount = 0;
            foreach (string folderPath in folderPaths)
            {
                if (Directory.Exists(folderPath))
                {
                    existingFolderCount++;
                }
            }

            return existingFolderCount == folderPaths.Length
                ? $"Папок: {folderPaths.Length}"
                : $"Папок: {folderPaths.Length}, доступно {existingFolderCount}";
        }

        /// <inheritdoc />
        public async Task<AlbumSyncResult> RefreshAsync(
            bool forceRefresh,
            IProgress<(int Completed, int Total)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsConfigured)
            {
                throw new PhotoSourceException(
                    "Не выбрана ни одна папка. Откройте настройки (⚙) и укажите папки со снимками.");
            }

            await EnsureStoragePermissionAsync().ConfigureAwait(false);

            // Никакого предела: файлы не копируются, и обрезка списка означала бы,
            // что дальняя часть папки не покажется никогда.
            List<string> foundPhotoPaths = ScanConfiguredFolders(cancellationToken);

            if (foundPhotoPaths.Count == 0)
            {
                throw new PhotoSourceException(
                    "В указанных папках нет изображений и видео. Проверьте пути и права доступа " +
                    $"(поддерживаются {MediaFileTypes.DescribeSupportedExtensions()}).");
            }

            // Сравниваем с прошлым проходом, чтобы показать осмысленные «новых N / удалено M».
            var previousPaths = new HashSet<string>(ReadManifest(), StringComparer.OrdinalIgnoreCase);
            int addedCount = 0;
            foreach (string photoPath in foundPhotoPaths)
            {
                if (!previousPaths.Contains(photoPath))
                {
                    addedCount++;
                }
            }

            var currentPaths = new HashSet<string>(foundPhotoPaths, StringComparer.OrdinalIgnoreCase);
            int removedCount = 0;
            foreach (string previousPath in previousPaths)
            {
                if (!currentPaths.Contains(previousPath))
                {
                    removedCount++;
                }
            }

            WriteManifest(foundPhotoPaths);

            return new AlbumSyncResult(
                TotalPhotoCount: foundPhotoPaths.Count,
                DownloadedCount: addedCount,
                ReusedCount: foundPhotoPaths.Count - addedCount,
                RemovedCount: removedCount,
                AvailableCount: foundPhotoPaths.Count);
        }

        /// <summary>
        /// Обходит настроенные папки. Порядок стабильный: сначала в порядке настроек,
        /// внутри папки — по имени файла.
        /// </summary>
        private static List<string> ScanConfiguredFolders(CancellationToken cancellationToken)
        {
            var foundPhotoPaths = new List<string>();
            var alreadyAdded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string folderPath in FrameSettings.LocalFolderPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (string filePath in
                         MediaFileScanner.EnumerateMediaFiles(folderPath, FrameSettings.LocalFolderRecursive))
                {
                    // Один и тот же файл может попасть из вложенных выборов папок.
                    if (alreadyAdded.Add(filePath))
                    {
                        foundPhotoPaths.Add(filePath);
                    }
                }
            }

            return foundPhotoPaths;
        }

        /// <summary>
        /// На Android 8.1 чтение чужих папок требует разрешения, выданного в рантайме,
        /// а не только объявленного в манифесте.
        /// </summary>
        private static async Task EnsureStoragePermissionAsync()
        {
            PermissionStatus permissionStatus =
                await Permissions.CheckStatusAsync<Permissions.StorageRead>().ConfigureAwait(false);

            if (permissionStatus != PermissionStatus.Granted)
            {
                permissionStatus =
                    await Permissions.RequestAsync<Permissions.StorageRead>().ConfigureAwait(false);
            }

            if (permissionStatus != PermissionStatus.Granted)
            {
                throw new PhotoSourceException(
                    "Нет разрешения на чтение файлов. Выдайте приложению доступ к памяти " +
                    "в настройках Android.");
            }
        }

        /// <inheritdoc />
        public List<string> GetPhotoPaths()
        {
            var photoPaths = new List<string>();

            foreach (string photoPath in ReadManifest())
            {
                // Файл могли удалить между обновлениями — молча пропускаем.
                if (File.Exists(photoPath))
                {
                    photoPaths.Add(photoPath);
                }
            }

            return photoPaths;
        }

        private static IEnumerable<string> ReadManifest()
        {
            if (!File.Exists(ManifestPath))
            {
                return Array.Empty<string>();
            }

            try
            {
                return File.ReadAllLines(ManifestPath);
            }
            catch (Exception manifestReadFailure) when (
                manifestReadFailure is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Манифест папок не прочитан: {manifestReadFailure.Message}");
                return Array.Empty<string>();
            }
        }

        private static void WriteManifest(List<string> photoPaths)
        {
            string temporaryPath = ManifestPath + ".tmp";

            try
            {
                File.WriteAllLines(temporaryPath, photoPaths);
                File.Move(temporaryPath, ManifestPath, overwrite: true);
            }
            catch (Exception manifestFailure) when (
                manifestFailure is IOException or UnauthorizedAccessException)
            {
                throw new PhotoSourceException(
                    "Не удалось сохранить список снимков на диске рамки.", manifestFailure);
            }
        }
    }
}
