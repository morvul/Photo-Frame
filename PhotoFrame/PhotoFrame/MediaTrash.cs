using System;
using System.IO;
using System.Threading.Tasks;
using Android.Media;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

// Path есть и в Android.*, и в System.IO: нужен однозначный псевдоним.
using IoPath = System.IO.Path;

namespace PhotoFrame
{
    /// <summary>
    /// Переносит снятые с показа кадры в корзину.
    /// </summary>
    /// <remarks>
    /// Файл не удаляется, а переезжает: на рамке нет ни подтверждения по второму разу,
    /// ни возможности достать снимок из «недавно удалённых», а нажать кнопку случайно
    /// вполне реально. Корзина лежит в общей памяти устройства, поэтому её видно и
    /// с компьютера по USB, и удаление приложения снимки не уносит.
    ///
    /// Кадры из общего альбома Google складываются в отдельный подкаталог: они
    /// адресуются хэшем ссылки, и по такому имени невозможно понять, откуда файл.
    /// </remarks>
    internal static class MediaTrash
    {
        /// <summary>Каталог рамки в общей памяти, внутри которого лежит корзина.</summary>
        private const string FrameDirectoryName = "PhotoFrame";

        /// <summary>Подкаталог для кадров из общего альбома Google Photos.</summary>
        private const string AlbumSubdirectoryName = "Google Photos";

        /// <summary>Подкаталог для кадров, скачанных с сервера Immich.</summary>
        private const string ImmichSubdirectoryName = "Immich";

        /// <summary>Корень корзины.</summary>
        /// <remarks>
        /// Общая память недоступна в редких случаях (нет карты, не смонтировано) —
        /// тогда корзина уходит в данные приложения: снимок всё равно лучше сохранить.
        /// </remarks>
        public static string RootDirectory
        {
            get
            {
                string? sharedStorageRoot =
                    Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;

                string baseDirectory = string.IsNullOrEmpty(sharedStorageRoot)
                    ? FileSystem.AppDataDirectory
                    : IoPath.Combine(sharedStorageRoot, FrameDirectoryName);

                return IoPath.Combine(baseDirectory, MediaFileScanner.TrashDirectoryName);
            }
        }

        /// <summary>True, если файл — скачанный кадр общего альбома, а не снимок из папки.</summary>
        public static bool IsAlbumPhoto(string mediaPath) =>
            IsInDirectory(mediaPath, SharedAlbumPhotoSource.PhotoLibraryDirectory);

        /// <summary>True, если файл скачан с сервера Immich.</summary>
        public static bool IsImmichPhoto(string mediaPath) =>
            IsInDirectory(mediaPath, ImmichPhotoSource.PhotoLibraryDirectory);

        /// <summary>
        /// True для кадра из любого сетевого источника.
        /// </summary>
        /// <remarks>
        /// Такой кадр мало убрать в корзину: файл лежит в кэше, а запись о нём осталась
        /// на сервере, и следующая синхронизация скачает снимок заново. Поэтому имя
        /// файла ещё и запоминается в списке убранных.
        /// </remarks>
        public static bool IsDownloadedPhoto(string mediaPath) =>
            IsAlbumPhoto(mediaPath) || IsImmichPhoto(mediaPath);

        private static bool IsInDirectory(string mediaPath, string directoryPath)
        {
            string? directory = IoPath.GetDirectoryName(mediaPath);

            return directory is not null
                   && directory.Equals(
                       directoryPath.TrimEnd(IoPath.DirectorySeparatorChar),
                       StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Переносит файл в корзину и возвращает его новый путь.
        /// </summary>
        /// <exception cref="PhotoSourceException">
        /// Файл остался на месте: показывать снимок дальше правильнее, чем делать вид,
        /// что он убран.
        /// </exception>
        public static async Task<string> MoveToTrashAsync(string mediaPath)
        {
            if (!File.Exists(mediaPath))
            {
                throw new PhotoSourceException("Файла уже нет на месте.");
            }

            string targetDirectory = RootDirectory;

            if (IsAlbumPhoto(mediaPath))
            {
                targetDirectory = IoPath.Combine(RootDirectory, AlbumSubdirectoryName);
            }
            else if (IsImmichPhoto(mediaPath))
            {
                targetDirectory = IoPath.Combine(RootDirectory, ImmichSubdirectoryName);
            }

            await EnsureStorageWritePermissionAsync().ConfigureAwait(true);

            try
            {
                Directory.CreateDirectory(targetDirectory);

                string targetPath = BuildFreeTargetPath(targetDirectory, mediaPath);
                MoveFile(mediaPath, targetPath);
                NotifyMediaScanner(mediaPath, targetPath);
                return targetPath;
            }
            catch (Exception moveFailure) when (
                moveFailure is IOException or UnauthorizedAccessException
                    or NotSupportedException)
            {
                throw new PhotoSourceException(
                    $"Не удалось убрать файл в корзину: {moveFailure.Message}", moveFailure);
            }
        }

        /// <summary>
        /// Сообщает системе об исчезнувшем и появившемся файле.
        /// </summary>
        /// <remarks>
        /// MTP отдаёт компьютеру не файловую систему, а индекс MediaStore, и обычное
        /// переименование его не обновляет: файл лежит в корзине, но по USB его не видно,
        /// а по старому пути остаётся запись, из-за которой галереи показывают снимок,
        /// которого там уже нет. Разбор старого пути как раз убирает запись: сканер
        /// удаляет из индекса то, чего на диске не нашлось.
        /// </remarks>
        /// <param name="vanishedPath">
        /// Пусто, когда файл только появился и убирать из индекса нечего: так вызывает
        /// загрузчик клипов, складывающий их в общую память мимо сканера.
        /// </param>
        public static void NotifyMediaScanner(string? vanishedPath, string createdPath)
        {
            try
            {
                string[] changedPaths = vanishedPath is null
                    ? new[] { createdPath }
                    : new[] { vanishedPath, createdPath };

                MediaScannerConnection.ScanFile(
                    Android.App.Application.Context,
                    changedPaths,
                    null,
                    null);
            }
            catch (Exception scanFailure) when (scanFailure is Java.Lang.Throwable)
            {
                // Файл уже в корзине — это главное. Без индекса он лишь не появится
                // по USB до следующего обхода сканера.
                System.Diagnostics.Debug.WriteLine(
                    $"Сканер не оповещён о {createdPath}: {scanFailure.Message}");
            }
        }

        /// <summary>
        /// Переносит файл, переписывая его при переезде между разделами.
        /// </summary>
        /// <remarks>
        /// Кэш приложения (/data/user/0) и общая память (/storage/emulated/0) — разные
        /// точки монтирования, и переименование между ними падает с EXDEV. Для снимка
        /// из папки на устройстве переезд остаётся дешёвым переименованием.
        /// </remarks>
        private static void MoveFile(string sourcePath, string targetPath)
        {
            try
            {
                File.Move(sourcePath, targetPath);
            }
            catch (IOException)
            {
                File.Copy(sourcePath, targetPath, overwrite: false);

                try
                {
                    File.Delete(sourcePath);
                }
                catch (Exception deleteFailure) when (
                    deleteFailure is IOException or UnauthorizedAccessException)
                {
                    // Копия в корзине есть, а оригинал остался: убираем копию, иначе
                    // снимок показывался бы дальше и при этом лежал бы в корзине.
                    File.Delete(targetPath);
                    throw;
                }
            }
        }

        /// <summary>
        /// Подбирает свободное имя: два файла из разных папок легко называются одинаково.
        /// </summary>
        private static string BuildFreeTargetPath(string targetDirectory, string sourcePath)
        {
            string fileName = IoPath.GetFileName(sourcePath);
            string candidatePath = IoPath.Combine(targetDirectory, fileName);

            if (!File.Exists(candidatePath))
            {
                return candidatePath;
            }

            string nameWithoutExtension = IoPath.GetFileNameWithoutExtension(fileName);
            string extension = IoPath.GetExtension(fileName);

            for (int copyNumber = 2; copyNumber < int.MaxValue; copyNumber++)
            {
                candidatePath = IoPath.Combine(
                    targetDirectory, $"{nameWithoutExtension} ({copyNumber}){extension}");

                if (!File.Exists(candidatePath))
                {
                    return candidatePath;
                }
            }

            throw new PhotoSourceException("В корзине уже есть файл с таким именем.");
        }

        /// <summary>
        /// На Android 8.1 запись в общую память требует разрешения, выданного в рантайме.
        /// </summary>
        private static async Task EnsureStorageWritePermissionAsync()
        {
            PermissionStatus permissionStatus =
                await Permissions.CheckStatusAsync<Permissions.StorageWrite>().ConfigureAwait(true);

            if (permissionStatus != PermissionStatus.Granted)
            {
                permissionStatus =
                    await Permissions.RequestAsync<Permissions.StorageWrite>().ConfigureAwait(true);
            }

            if (permissionStatus != PermissionStatus.Granted)
            {
                throw new PhotoSourceException(
                    "Нет разрешения на запись в память устройства — файл остался на месте.");
            }
        }
    }
}
