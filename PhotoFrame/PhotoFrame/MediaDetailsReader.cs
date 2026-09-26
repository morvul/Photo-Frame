using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Android.Graphics;
using Android.Media;

// Path и Encoding есть и в Android.*, и в System.*: нужны однозначные псевдонимы.
using IoPath = System.IO.Path;

namespace PhotoFrame
{
    /// <summary>
    /// Собирает всё, что известно о файле: свойства файловой системы, размеры,
    /// EXIF для снимков и метаданные контейнера для видео.
    /// </summary>
    /// <remarks>
    /// Снимки из общего альбома Google приходят пересжатыми, и EXIF в них обычно вырезан —
    /// поэтому пустые поля не показываются вовсе, вместо длинного списка прочерков.
    /// </remarks>
    internal static class MediaDetailsReader
    {
        /// <summary>Пара «подпись — значение» для экрана сведений.</summary>
        public readonly record struct DetailRow(string Label, string Value);

        /// <summary>Чем и когда снят кадр — для короткой подписи поверх снимка.</summary>
        public readonly record struct CaptureInfo(string? Device, DateTime? TakenAt);

        /// <summary>
        /// Читает только съёмочные данные: этого достаточно для подписи на экране,
        /// и не нужно собирать весь список свойств на каждом кадре.
        /// </summary>
        public static CaptureInfo ReadCaptureInfo(string mediaPath)
        {
            try
            {
                return MediaFileTypes.IsVideo(mediaPath)
                    ? ReadVideoCaptureInfo(mediaPath)
                    : ReadImageCaptureInfo(mediaPath);
            }
            catch (Exception readFailure) when (
                readFailure is IOException or UnauthorizedAccessException or Java.Lang.Throwable)
            {
                return default;
            }
        }

        private static CaptureInfo ReadImageCaptureInfo(string imagePath)
        {
            using var exif = new ExifInterface(imagePath);

            string? device = CameraNameFormatter.Combine(
                exif.GetAttribute(ExifInterface.TagMake),
                exif.GetAttribute(ExifInterface.TagModel));

            string? takenText = exif.GetAttribute(ExifInterface.TagDatetimeOriginal)
                                ?? exif.GetAttribute(ExifInterface.TagDatetime);

            return new CaptureInfo(device, ParseExifDate(takenText));
        }

        private static CaptureInfo ReadVideoCaptureInfo(string videoPath)
        {
            var retriever = new MediaMetadataRetriever();

            try
            {
                retriever.SetDataSource(videoPath);
                return new CaptureInfo(
                    null, ParseVideoDate(retriever.ExtractMetadata(MetadataKey.Date)));
            }
            finally
            {
                retriever.Release();
                retriever.Dispose();
            }
        }

        /// <summary>EXIF хранит дату как "2024:07:12 18:30:00".</summary>
        private static DateTime? ParseExifDate(string? exifDate) =>
            DateTime.TryParseExact(
                exifDate, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime parsed)
                ? parsed
                : null;

        /// <summary>Контейнеры пишут дату как "20240712T183000.000Z".</summary>
        private static DateTime? ParseVideoDate(string? containerDate)
        {
            string[] formats =
            {
                "yyyyMMdd'T'HHmmss.fff'Z'",
                "yyyyMMdd'T'HHmmss'Z'",
                "yyyyMMdd'T'HHmmss",
            };

            return DateTime.TryParseExact(
                containerDate, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal, out DateTime parsed)
                ? parsed.ToLocalTime()
                : null;
        }

        public static List<DetailRow> Read(string mediaPath)
        {
            var rows = new List<DetailRow>();

            AddFileSystemDetails(rows, mediaPath);

            if (MediaFileTypes.IsVideo(mediaPath))
            {
                AddVideoDetails(rows, mediaPath);
            }
            else
            {
                AddImageDetails(rows, mediaPath);
            }

            return rows;
        }

        private static void AddFileSystemDetails(List<DetailRow> rows, string mediaPath)
        {
            rows.Add(new DetailRow("Файл", IoPath.GetFileName(mediaPath)));
            rows.Add(new DetailRow("Папка", IoPath.GetDirectoryName(mediaPath) ?? "—"));

            try
            {
                var fileInfo = new FileInfo(mediaPath);
                if (!fileInfo.Exists)
                {
                    rows.Add(new DetailRow("Состояние", "файл не найден"));
                    return;
                }

                rows.Add(new DetailRow("Размер", DescribeBytes(fileInfo.Length)));
                rows.Add(new DetailRow(
                    "Изменён",
                    fileInfo.LastWriteTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture)));
            }
            catch (Exception fileFailure) when (
                fileFailure is IOException or UnauthorizedAccessException)
            {
                rows.Add(new DetailRow("Состояние", "нет доступа к файлу"));
            }
        }

        private static void AddImageDetails(List<DetailRow> rows, string imagePath)
        {
            try
            {
                var boundsOptions = new BitmapFactory.Options { InJustDecodeBounds = true };
                BitmapFactory.DecodeFile(imagePath, boundsOptions);

                if (boundsOptions.OutWidth > 0 && boundsOptions.OutHeight > 0)
                {
                    rows.Add(new DetailRow(
                        "Разрешение", $"{boundsOptions.OutWidth} × {boundsOptions.OutHeight}"));

                    double megapixels = boundsOptions.OutWidth * (double)boundsOptions.OutHeight
                                        / 1_000_000d;
                    rows.Add(new DetailRow(
                        "Мегапикселей",
                        megapixels.ToString("F1", CultureInfo.CurrentCulture)));
                }

                if (!string.IsNullOrEmpty(boundsOptions.OutMimeType))
                {
                    rows.Add(new DetailRow("Тип", boundsOptions.OutMimeType));
                }
            }
            catch (Exception decodeFailure) when (decodeFailure is Java.Lang.Throwable or IOException)
            {
                rows.Add(new DetailRow("Разрешение", "не определено"));
            }

            AddExifDetails(rows, imagePath);
        }

        private static void AddExifDetails(List<DetailRow> rows, string imagePath)
        {
            ExifInterface exif;
            try
            {
                exif = new ExifInterface(imagePath);
            }
            catch (Exception exifFailure) when (
                exifFailure is IOException or Java.Lang.Throwable)
            {
                return;
            }

            // Порядок такой, каким его ожидают увидеть: сначала «когда и чем», потом параметры.
            AddIfPresent(rows, "Снято", exif.GetAttribute(ExifInterface.TagDatetimeOriginal)
                                        ?? exif.GetAttribute(ExifInterface.TagDatetime));
            AddIfPresent(rows, "Производитель", exif.GetAttribute(ExifInterface.TagMake));
            AddIfPresent(rows, "Модель", exif.GetAttribute(ExifInterface.TagModel));
            // Часть тегов не выведена в биндинге Android.Media.ExifInterface,
            // поэтому берём их по имени из спецификации EXIF.
            AddIfPresent(rows, "Объектив", exif.GetAttribute("LensModel"));

            string? exposure = exif.GetAttribute(ExifInterface.TagExposureTime);
            AddIfPresent(rows, "Выдержка", DescribeExposure(exposure));
            AddIfPresent(rows, "Диафрагма", FormatWithPrefix(
                exif.GetAttribute(ExifInterface.TagFNumber), "f/"));
            AddIfPresent(rows, "ISO",
                exif.GetAttribute(ExifInterface.TagIsoSpeedRatings)
                ?? exif.GetAttribute("PhotographicSensitivity"));
            AddIfPresent(rows, "Фокусное расстояние", FormatWithSuffix(
                exif.GetAttribute(ExifInterface.TagFocalLength), " мм"));
            AddIfPresent(rows, "Вспышка", DescribeFlash(exif.GetAttribute(ExifInterface.TagFlash)));
            AddIfPresent(rows, "Ориентация", exif.GetAttribute(ExifInterface.TagOrientation));
            AddIfPresent(rows, "Программа", exif.GetAttribute(ExifInterface.TagSoftware));

            var coordinates = new float[2];
            if (exif.GetLatLong(coordinates))
            {
                rows.Add(new DetailRow(
                    "Координаты",
                    string.Format(
                        CultureInfo.CurrentCulture, "{0:F5}, {1:F5}", coordinates[0], coordinates[1])));
            }

            AddIfPresent(rows, "Высота", exif.GetAttribute(ExifInterface.TagGpsAltitude));

            exif.Dispose();
        }

        private static void AddVideoDetails(List<DetailRow> rows, string videoPath)
        {
            var retriever = new MediaMetadataRetriever();

            try
            {
                retriever.SetDataSource(videoPath);

                string? durationText = retriever.ExtractMetadata(MetadataKey.Duration);
                if (long.TryParse(durationText, out long durationMilliseconds))
                {
                    rows.Add(new DetailRow(
                        "Длительность",
                        TimeSpan.FromMilliseconds(durationMilliseconds).ToString(@"h\:mm\:ss")));
                }

                string? width = retriever.ExtractMetadata(MetadataKey.VideoWidth);
                string? height = retriever.ExtractMetadata(MetadataKey.VideoHeight);
                if (!string.IsNullOrEmpty(width) && !string.IsNullOrEmpty(height))
                {
                    rows.Add(new DetailRow("Разрешение", $"{width} × {height}"));
                }

                string? bitrateText = retriever.ExtractMetadata(MetadataKey.Bitrate);
                if (long.TryParse(bitrateText, out long bitsPerSecond))
                {
                    rows.Add(new DetailRow(
                        "Битрейт",
                        (bitsPerSecond / 1_000_000d).ToString("F1", CultureInfo.CurrentCulture)
                        + " Мбит/с"));
                }

                AddIfPresent(rows, "Поворот", retriever.ExtractMetadata(MetadataKey.VideoRotation));
                AddIfPresent(rows, "Тип", retriever.ExtractMetadata(MetadataKey.Mimetype));
                AddIfPresent(rows, "Снято", retriever.ExtractMetadata(MetadataKey.Date));
                AddIfPresent(rows, "Частота кадров",
                    retriever.ExtractMetadata(MetadataKey.CaptureFramerate));
                AddIfPresent(rows, "Есть звук", DescribeYesNo(
                    retriever.ExtractMetadata(MetadataKey.HasAudio)));
            }
            catch (Exception metadataFailure) when (
                metadataFailure is Java.Lang.Throwable or IOException)
            {
                rows.Add(new DetailRow("Метаданные", "не прочитаны"));
            }
            finally
            {
                retriever.Release();
                retriever.Dispose();
            }
        }

        private static void AddIfPresent(List<DetailRow> rows, string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                rows.Add(new DetailRow(label, value.Trim()));
            }
        }

        /// <summary>Выдержку принято писать как 1/125, а не 0,008.</summary>
        private static string? DescribeExposure(string? exposureSeconds)
        {
            if (!double.TryParse(
                    exposureSeconds, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out double seconds) || seconds <= 0)
            {
                return exposureSeconds;
            }

            return seconds >= 1
                ? seconds.ToString("F1", CultureInfo.CurrentCulture) + " с"
                : "1/" + Math.Round(1 / seconds).ToString(CultureInfo.CurrentCulture) + " с";
        }

        private static string? DescribeFlash(string? flashValue) =>
            int.TryParse(flashValue, out int flags)
                ? ((flags & 1) == 1 ? "сработала" : "не сработала")
                : flashValue;

        private static string? DescribeYesNo(string? rawValue) => rawValue switch
        {
            null or "" => null,
            "yes" or "1" or "true" => "да",
            _ => "нет",
        };

        private static string? FormatWithPrefix(string? value, string prefix) =>
            string.IsNullOrWhiteSpace(value) ? null : prefix + value;

        private static string? FormatWithSuffix(string? value, string suffix) =>
            string.IsNullOrWhiteSpace(value) ? null : value + suffix;

        private static string DescribeBytes(long byteCount)
        {
            if (byteCount >= 1024L * 1024 * 1024)
            {
                return (byteCount / 1024d / 1024 / 1024).ToString("F2", CultureInfo.CurrentCulture)
                       + " ГБ";
            }

            if (byteCount >= 1024 * 1024)
            {
                return (byteCount / 1024d / 1024).ToString("F1", CultureInfo.CurrentCulture) + " МБ";
            }

            return (byteCount / 1024d).ToString("F0", CultureInfo.CurrentCulture) + " КБ";
        }
    }
}
