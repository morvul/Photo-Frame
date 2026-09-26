using System;
using System.IO;
using Android.Graphics;
using Android.Media;

// Bitmap и Matrix есть и в Android.Graphics, и в Microsoft.Maui.Graphics, который
// подключён через ImplicitUsings, поэтому нужны однозначные псевдонимы.
using AndroidBitmap = Android.Graphics.Bitmap;
using AndroidMatrix = Android.Graphics.Matrix;

namespace PhotoFrame
{
    /// <summary>
    /// Читает снимок с диска в кадр под размер экрана.
    /// </summary>
    /// <remarks>
    /// Своё чтение, а не ImageSource.FromFile, по двум причинам.
    ///
    /// Первая — мигание. MAUI загружает Source асинхронно и на это время гасит
    /// картинку: между кадрами слайд-шоу мелькал чёрный экран. Готовый Bitmap
    /// отдаётся платформенному ImageView сразу, без промежуточного пустого кадра, —
    /// тот же приём, что и у ночных часов.
    ///
    /// Вторая — память. Снимок с зеркальной камеры на 22 МБ разворачивается в кадр
    /// 6000x4000, то есть под сотню мегабайт в родной памяти, — и это на устройстве,
    /// где всего гигабайт. Экран рамки 1280x800, разницы не видно, а места нужно
    /// в двадцать раз меньше. Уменьшение делает сам декодер (inSampleSize), поэтому
    /// полноразмерный кадр в памяти не появляется вовсе.
    /// </remarks>
    internal static class PhotoFrameDecoder
    {
        /// <summary>
        /// Читает снимок, уменьшая его под экран. Null — файл не изображение либо
        /// не читается.
        /// </summary>
        /// <remarks>
        /// Вызывать в фоновом потоке: чтение и поворот кадра на этой рамке занимают
        /// заметное время.
        /// </remarks>
        public static AndroidBitmap? Decode(string photoPath, int screenWidth, int screenHeight)
        {
            try
            {
                if (!File.Exists(photoPath))
                {
                    return null;
                }

                // Сначала только размеры: сам кадр в память не читается.
                var boundsOptions = new BitmapFactory.Options { InJustDecodeBounds = true };
                BitmapFactory.DecodeFile(photoPath, boundsOptions);

                if (boundsOptions.OutWidth <= 0 || boundsOptions.OutHeight <= 0)
                {
                    return null;
                }

                var decodeOptions = new BitmapFactory.Options
                {
                    InSampleSize = ChooseSampleSize(
                        boundsOptions.OutWidth, boundsOptions.OutHeight, screenWidth, screenHeight),

                    // Argb8888, а не Rgb565: на градиентах вроде неба половинная
                    // точность даёт видимые полосы, а кадр под экран и так невелик.
                    InPreferredConfig = AndroidBitmap.Config.Argb8888,
                };

                AndroidBitmap? frame = BitmapFactory.DecodeFile(photoPath, decodeOptions);
                return frame is null ? null : ApplyExifRotation(frame, photoPath);
            }
            catch (Exception decodeFailure) when (
                decodeFailure is IOException or UnauthorizedAccessException
                    or OutOfMemoryException or Java.Lang.Throwable)
            {
                FrameLog.Warn($"Снимок не прочитан: {decodeFailure.Message}");
                return null;
            }
        }

        /// <summary>
        /// Во сколько раз уменьшать при чтении.
        /// </summary>
        /// <remarks>
        /// Декодер понимает только степени двойки, и берётся ближайшая снизу: кадр
        /// остаётся не меньше экрана, поэтому вписывать его не приходится растягиванием.
        /// </remarks>
        public static int ChooseSampleSize(
            int sourceWidth, int sourceHeight, int screenWidth, int screenHeight)
        {
            int sampleSize = 1;

            while (sourceWidth / (sampleSize * 2) >= screenWidth
                   && sourceHeight / (sampleSize * 2) >= screenHeight)
            {
                sampleSize *= 2;
            }

            return sampleSize;
        }

        /// <summary>
        /// Поворачивает кадр так, как он был снят.
        /// </summary>
        /// <remarks>
        /// BitmapFactory на EXIF не смотрит вовсе, поэтому снимки с телефона, снятые
        /// вертикально, ложились бы на бок. MAUI это делал за нас — теперь делаем сами.
        /// </remarks>
        private static AndroidBitmap ApplyExifRotation(AndroidBitmap frame, string photoPath)
        {
            int degrees;

            try
            {
                using var exif = new ExifInterface(photoPath);

                degrees = exif.GetAttributeInt(ExifInterface.TagOrientation, 1) switch
                {
                    3 => 180,
                    6 => 90,
                    8 => 270,
                    _ => 0,
                };
            }
            catch (Exception exifFailure) when (
                exifFailure is IOException or Java.Lang.Throwable)
            {
                // Без EXIF считаем, что кадр уже стоит правильно.
                return frame;
            }

            if (degrees == 0)
            {
                return frame;
            }

            try
            {
                using var rotation = new AndroidMatrix();
                rotation.PostRotate(degrees);

                AndroidBitmap? rotated = AndroidBitmap.CreateBitmap(
                    frame, 0, 0, frame.Width, frame.Height, rotation, filter: true);

                if (rotated is null || ReferenceEquals(rotated, frame))
                {
                    return frame;
                }

                // Исходный кадр больше не нужен: держать оба незачем.
                frame.Recycle();
                frame.Dispose();
                return rotated;
            }
            catch (Exception rotateFailure) when (
                rotateFailure is OutOfMemoryException or Java.Lang.Throwable)
            {
                FrameLog.Warn($"Кадр не повёрнут: {rotateFailure.Message}");
                return frame;
            }
        }
    }
}
