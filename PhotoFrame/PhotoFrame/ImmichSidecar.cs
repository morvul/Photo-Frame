using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

// Path есть и в Android.*, и в System.IO: нужен однозначный псевдоним.
using IoPath = System.IO.Path;

namespace PhotoFrame
{
    /// <summary>
    /// Что известно про кадр Immich сверх самого файла в кэше.
    /// </summary>
    /// <param name="AssetId">Идентификатор объекта на сервере.</param>
    /// <param name="FileName">Исходное имя файла — оно показывается подписью кадра.</param>
    /// <param name="ClipAssetId">
    /// Чей клип играть: у видео это сам объект, у живого фото — приложенный к нему.
    /// Пусто у обычного снимка.
    /// </param>
    /// <param name="DurationMilliseconds">Длительность клипа.</param>
    /// <param name="IsMotionPhoto">Живое фото: клип играется один раз и молча.</param>
    /// <param name="CameraName">Чем снято.</param>
    /// <param name="TakenAt">Когда снято.</param>
    /// <param name="AlbumName">
    /// Из какого альбома пришёл кадр; пусто, когда показывается вся библиотека и
    /// альбом неизвестен.
    /// </param>
    internal sealed record ImmichSlideInfo(
        string AssetId,
        string FileName,
        string ClipAssetId,
        int DurationMilliseconds,
        bool IsMotionPhoto,
        string CameraName,
        DateTime? TakenAt,
        string AlbumName);

    /// <summary>
    /// Хранит рядом с кэшем Immich то, чего в самих файлах нет.
    /// </summary>
    /// <remarks>
    /// Нужен по двум причинам. Во-первых, рамка показывает не оригинал, а превью с
    /// сервера: имя файла в кэше — хэш, EXIF из превью вырезан, и без этого списка
    /// кадр подписывался бы просто «Immich», а сведения о съёмке оставались бы пусты.
    /// Во-вторых, клип забирается только когда слайд показан, и к этому моменту нужно
    /// знать, у какого объекта его просить.
    ///
    /// Формат — та же таблица с табуляцией, что и у списка видео альбома: файл читается
    /// глазами, дописывается построчно и переживает обновление приложения. Список
    /// переписывается целиком при каждой синхронизации, поэтому устаревших строк
    /// в нём не остаётся.
    /// </remarks>
    internal static class ImmichSidecar
    {
        private const string IndexFileName = "immich.index";

        /// <summary>Разделитель полей: в именах файлов табуляции не бывает.</summary>
        private const char FieldSeparator = '\t';

        private const string VideoKind = "video";
        private const string MotionKind = "motion";
        private const string PhotoKind = "photo";

        /// <summary>
        /// Разобранный список, чтобы не читать файл на каждый кадр.
        /// </summary>
        /// <remarks>
        /// Слайд меняется раз в несколько секунд, а строк здесь бывают тысячи: чтение
        /// и разбор на каждом кадре — лишняя работа на медленном диске рамки. Кэш
        /// сбрасывается записью нового списка, то есть при синхронизации.
        /// </remarks>
        private static Dictionary<string, ImmichSlideInfo>? _byFileName;

        private static readonly object CacheLock = new();

        private static string IndexPath =>
            IoPath.Combine(ImmichPhotoSource.PhotoLibraryDirectory, IndexFileName);

        /// <summary>Переписывает список по итогам синхронизации.</summary>
        /// <param name="cacheFileName">Как объект назван в кэше рамки.</param>
        /// <param name="albumName">
        /// Из какого альбома пришёл объект. Известно потому, что альбомы запрашиваются
        /// по одному; при показе всей библиотеки — пусто.
        /// </param>
        public static void WriteIndex(
            List<ImmichAsset> assets,
            Func<ImmichAsset, string> cacheFileName,
            Func<ImmichAsset, string> albumName)
        {
            var lines = new List<string>(assets.Count);
            int videoCount = 0;
            int motionCount = 0;

            foreach (ImmichAsset asset in assets)
            {
                string kind = PhotoKind;
                string clipAssetId = string.Empty;

                if (asset.IsVideo)
                {
                    kind = VideoKind;
                    clipAssetId = asset.Id;
                    videoCount++;
                }
                else if (asset.IsMotionPhoto)
                {
                    kind = MotionKind;
                    clipAssetId = asset.LivePhotoVideoId;
                    motionCount++;
                }

                lines.Add(string.Join(
                    FieldSeparator,
                    cacheFileName(asset),
                    asset.Id,
                    kind,
                    clipAssetId,
                    asset.DurationMilliseconds.ToString(CultureInfo.InvariantCulture),
                    asset.TakenAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
                    asset.CameraName,
                    albumName(asset),

                    // Имя файла последним: в нём единственном возможны любые символы,
                    // и при разборе остаток строки берётся целиком.
                    asset.FileName));
            }

            FrameLog.Info($"Immich: видео {videoCount}, живых фото {motionCount}, всего {lines.Count}");

            string indexPath = IndexPath;
            string temporaryPath = indexPath + ".tmp";

            try
            {
                Directory.CreateDirectory(ImmichPhotoSource.PhotoLibraryDirectory);
                File.WriteAllLines(temporaryPath, lines);
                File.Move(temporaryPath, indexPath, overwrite: true);

                lock (CacheLock)
                {
                    _byFileName = null;
                }
            }
            catch (Exception writeFailure) when (
                writeFailure is IOException or UnauthorizedAccessException)
            {
                // Без списка кадры покажутся, но без подписей и без видео.
                FrameLog.Warn($"Список Immich не сохранён: {writeFailure.Message}");
            }
        }

        /// <summary>
        /// True, если список уже составлен.
        /// </summary>
        /// <remarks>
        /// Проверяется перед тем, как пропустить синхронизацию по совпавшему отпечатку:
        /// кэш, набранный прежними версиями, о подписях и клипах ничего не знает, и без
        /// полного прохода кадры так и остались бы безымянными.
        /// </remarks>
        /// <remarks>
        /// Список прежней версии, без столбца альбома, считается отсутствующим: иначе
        /// кадры остались бы подписаны без альбома до первого изменения на сервере.
        /// </remarks>
        public static bool HasIndex
        {
            get
            {
                try
                {
                    if (!File.Exists(IndexPath))
                    {
                        return false;
                    }

                    using var reader = new StreamReader(IndexPath);
                    string? firstLine = reader.ReadLine();

                    // Пустой файл — это тоже «списка нет»: писать его заново дешевле,
                    // чем разбираться, почему кадры без подписей.
                    return firstLine is not null
                           && firstLine.Split(FieldSeparator, 9).Length >= 9;
                }
                catch (Exception readFailure) when (
                    readFailure is IOException or UnauthorizedAccessException)
                {
                    return false;
                }
            }
        }

        /// <summary>Сведения о кадре либо null, если он не из Immich.</summary>
        public static ImmichSlideInfo? Find(string mediaPath)
        {
            if (!MediaTrash.IsImmichPhoto(mediaPath))
            {
                return null;
            }

            return ReadIndex().GetValueOrDefault(IoPath.GetFileName(mediaPath));
        }

        /// <summary>
        /// То же, но и для скачанного клипа: он лежит в общем каталоге клипов и назван
        /// по своей заставке, поэтому ищется по имени без расширения.
        /// </summary>
        public static ImmichSlideInfo? FindByAnyPath(string mediaPath)
        {
            ImmichSlideInfo? bySlide = Find(mediaPath);
            if (bySlide is not null)
            {
                return bySlide;
            }

            string nameWithoutExtension = IoPath.GetFileNameWithoutExtension(mediaPath);

            foreach ((string fileName, ImmichSlideInfo slide) in ReadIndex())
            {
                if (IoPath.GetFileNameWithoutExtension(fileName)
                    .Equals(nameWithoutExtension, StringComparison.OrdinalIgnoreCase))
                {
                    return slide;
                }
            }

            return null;
        }

        private static Dictionary<string, ImmichSlideInfo> ReadIndex()
        {
            lock (CacheLock)
            {
                if (_byFileName is not null)
                {
                    return _byFileName;
                }

                var parsed = new Dictionary<string, ImmichSlideInfo>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    if (File.Exists(IndexPath))
                    {
                        foreach (string line in File.ReadAllLines(IndexPath))
                        {
                            // Девять полей, последнее — имя файла целиком. Строка из
                            // восьми осталась от прежней версии, где не было альбома:
                            // такую читаем как есть, а альбом подставится при следующей
                            // синхронизации.
                            string[] parts = line.Split(FieldSeparator, 9);
                            if (parts.Length < 8)
                            {
                                continue;
                            }

                            bool hasAlbumColumn = parts.Length >= 9;

                            int.TryParse(
                                parts[4],
                                NumberStyles.Integer,
                                CultureInfo.InvariantCulture,
                                out int durationMilliseconds);

                            DateTime? takenAt = DateTime.TryParse(
                                parts[5],
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.RoundtripKind,
                                out DateTime parsedDate)
                                ? parsedDate
                                : null;

                            parsed[parts[0]] = new ImmichSlideInfo(
                                AssetId: parts[1],
                                FileName: hasAlbumColumn ? parts[8] : parts[7],
                                ClipAssetId: parts[3],
                                DurationMilliseconds: durationMilliseconds,
                                IsMotionPhoto: parts[2].Equals(MotionKind, StringComparison.Ordinal),
                                CameraName: parts[6],
                                TakenAt: takenAt,
                                AlbumName: hasAlbumColumn ? parts[7] : string.Empty);
                        }
                    }
                }
                catch (Exception readFailure) when (
                    readFailure is IOException or UnauthorizedAccessException)
                {
                    FrameLog.Warn($"Список Immich не прочитан: {readFailure.Message}");
                }

                _byFileName = parsed;
                return parsed;
            }
        }
    }
}
