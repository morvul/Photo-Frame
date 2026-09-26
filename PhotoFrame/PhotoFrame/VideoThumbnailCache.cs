using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Android.Graphics;
using Android.Media;
using Microsoft.Maui.Storage;

// Path и Encoding есть и в Android.*, и в System.*: нужны однозначные псевдонимы.
using IoPath = System.IO.Path;
using TextEncoding = System.Text.Encoding;

namespace PhotoFrame
{
    /// <summary>
    /// Кадр-заставка для видео, чтобы слайд с видео не выглядел чёрным прямоугольником.
    /// </summary>
    /// <remarks>
    /// Кадр берётся из самого файла через MediaMetadataRetriever и сохраняется в кэш
    /// приложения: извлечение занимает сотни миллисекунд, повторять его при каждом
    /// круге слайд-шоу незачем.
    /// </remarks>
    internal static class VideoThumbnailCache
    {
        private const string CacheDirectoryName = "video_posters";

        /// <summary>Момент, из которого берётся кадр. Первый кадр часто чёрный.</summary>
        private const long FramePositionMicroseconds = 1_000_000;

        /// <summary>
        /// Возвращает путь к заставке, создавая её при необходимости.
        /// Null, если кадр извлечь не удалось.
        /// </summary>
        public static string? GetOrCreate(string videoPath)
        {
            try
            {
                string cacheDirectory = IoPath.Combine(FileSystem.CacheDirectory, CacheDirectoryName);
                Directory.CreateDirectory(cacheDirectory);

                string posterPath = IoPath.Combine(cacheDirectory, BuildCacheFileName(videoPath));
                if (File.Exists(posterPath))
                {
                    return posterPath;
                }

                Bitmap? frame = ExtractFrame(videoPath);
                if (frame is null)
                {
                    return null;
                }

                try
                {
                    using var posterStream = File.Create(posterPath);
                    frame.Compress(Bitmap.CompressFormat.Jpeg!, 85, posterStream);
                    return posterPath;
                }
                finally
                {
                    // Recycle, а не только Dispose: на Android 8 пиксели кадра лежат
                    // в native-куче и освобождаются лишь когда до объекта доберётся
                    // сборщик мусора. На рамке с гигабайтом памяти это слишком поздно.
                    frame.Recycle();
                    frame.Dispose();
                }
            }
            catch (Exception thumbnailFailure) when (
                thumbnailFailure is IOException or UnauthorizedAccessException
                    or Java.Lang.Throwable)
            {
                // Без заставки слайд просто покажет чёрный фон с кнопкой воспроизведения.
                System.Diagnostics.Debug.WriteLine(
                    $"Заставка для {videoPath} не создана: {thumbnailFailure.Message}");
                return null;
            }
        }

        private static Bitmap? ExtractFrame(string videoPath)
        {
            var retriever = new MediaMetadataRetriever();

            try
            {
                retriever.SetDataSource(videoPath);

                // Если секунда за пределами длительности, берём самый первый кадр.
                return retriever.GetFrameAtTime(FramePositionMicroseconds)
                       ?? retriever.GetFrameAtTime(0);
            }
            finally
            {
                retriever.Release();
                retriever.Dispose();
            }
        }

        /// <summary>Имя файла заставки выводится из пути и времени изменения видео.</summary>
        private static string BuildCacheFileName(string videoPath)
        {
            string signatureSource = videoPath;

            try
            {
                signatureSource += "|" + File.GetLastWriteTimeUtc(videoPath).Ticks;
            }
            catch (Exception statFailure) when (
                statFailure is IOException or UnauthorizedAccessException)
            {
                // Без времени изменения кэш просто не обновится при перезаписи файла.
            }

            byte[] hash = SHA256.HashData(TextEncoding.UTF8.GetBytes(signatureSource));
            return string.Concat(Convert.ToHexString(hash).AsSpan(0, 20), ".jpg");
        }
    }
}
