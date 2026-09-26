using Android.Graphics;

// Rect есть и в Android.Graphics, и в Microsoft.Maui.Graphics (ImplicitUsings).
using AndroidRect = Android.Graphics.Rect;

namespace PhotoFrame
{
    /// <summary>
    /// Выбирает угол для наложенных часов, разбирая сам снимок.
    /// </summary>
    /// <remarks>
    /// Часы читаются тем лучше, чем ровнее фон под ними, поэтому для каждого угла
    /// считается дисперсия яркости — чем она меньше, тем «спокойнее» участок. Отдельно
    /// оценивается доля пикселей телесного оттенка: перекрывать цифрами лицо не стоит
    /// даже на ровном фоне, поэтому такие участки получают тяжёлый штраф.
    ///
    /// Снимок декодируется в уменьшенном виде (не более ~160 px по ширине): для оценки
    /// участков этого достаточно, а полноразмерный кадр на рамке декодируется долго
    /// и занимает десятки мегабайт.
    /// </remarks>
    internal static class ClockPlacementAnalyzer
    {
        /// <summary>Ширина, до которой уменьшается снимок перед анализом.</summary>
        private const int AnalysisWidthPixels = 160;

        /// <summary>Какую часть кадра по каждой оси занимает область под часами.</summary>
        private const double RegionWidthFraction = 0.42;
        private const double RegionHeightFraction = 0.30;

        /// <summary>
        /// Во сколько раз телесные пиксели весомее неровности фона. Вес достаточен,
        /// чтобы участок с лицом проигрывал даже самому пёстрому свободному углу.
        /// </summary>
        private const double SkinPenaltyWeight = 20000;

        /// <summary>
        /// Оценивает четыре угла и возвращает индекс лучшего в том же порядке,
        /// в котором углы перечислены на странице слайд-шоу.
        /// </summary>
        /// <returns>
        /// Индекс угла либо null, если снимок не удалось прочитать — тогда часы остаются
        /// на текущем углу.
        /// </returns>
        public static int? ChooseBestCorner(string photoPath)
        {
            Bitmap? photo = DecodeScaled(photoPath);
            if (photo is null)
            {
                return null;
            }

            try
            {
                return ChooseBestCorner(photo);
            }
            finally
            {
                // Recycle освобождает пиксели сразу; одного Dispose мало — на Android 8
                // они живут в native-куче до сборки мусора.
                photo.Recycle();
                photo.Dispose();
            }
        }

        private static int? ChooseBestCorner(Bitmap photo)
        {

            int width = photo.Width;
            int height = photo.Height;
            if (width < 8 || height < 8)
            {
                return null;
            }

            int regionWidth = (int)(width * RegionWidthFraction);
            int regionHeight = (int)(height * RegionHeightFraction);

            // Порядок совпадает с ClockPositions в MainPage: низ-слева, низ-справа,
            // верх-справа, верх-слева.
            var regions = new[]
            {
                new AndroidRect(0, height - regionHeight, regionWidth, height),
                new AndroidRect(width - regionWidth, height - regionHeight, width, height),
                new AndroidRect(width - regionWidth, 0, width, regionHeight),
                new AndroidRect(0, 0, regionWidth, regionHeight),
            };

            int[] pixels = new int[width * height];
            photo.GetPixels(pixels, 0, width, 0, 0, width, height);

            int bestCornerIndex = 0;
            double bestScore = double.MaxValue;

            for (int cornerIndex = 0; cornerIndex < regions.Length; cornerIndex++)
            {
                double score = ScoreRegion(pixels, width, regions[cornerIndex]);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestCornerIndex = cornerIndex;
                }
            }

            return bestCornerIndex;
        }

        /// <summary>
        /// Чем меньше результат, тем участок пригоднее: дисперсия яркости плюс штраф
        /// за телесные оттенки.
        /// </summary>
        private static double ScoreRegion(int[] pixels, int strideWidth, AndroidRect region)
        {
            long luminanceSum = 0;
            long luminanceSquaredSum = 0;
            int pixelCount = 0;
            int skinPixelCount = 0;

            for (int y = region.Top; y < region.Bottom; y++)
            {
                int rowOffset = y * strideWidth;

                for (int x = region.Left; x < region.Right; x++)
                {
                    int argb = pixels[rowOffset + x];
                    int red = (argb >> 16) & 0xFF;
                    int green = (argb >> 8) & 0xFF;
                    int blue = argb & 0xFF;

                    // Rec. 601: достаточно точно для оценки «пестроты» и втрое дешевле,
                    // чем перевод в линейное пространство.
                    int luminance = (red * 299 + green * 587 + blue * 114) / 1000;

                    luminanceSum += luminance;
                    luminanceSquaredSum += (long)luminance * luminance;
                    pixelCount++;

                    if (IsSkinTone(red, green, blue))
                    {
                        skinPixelCount++;
                    }
                }
            }

            if (pixelCount == 0)
            {
                return double.MaxValue;
            }

            double meanLuminance = (double)luminanceSum / pixelCount;
            double variance = ((double)luminanceSquaredSum / pixelCount)
                              - (meanLuminance * meanLuminance);

            double skinRatio = (double)skinPixelCount / pixelCount;
            return variance + skinRatio * SkinPenaltyWeight;
        }

        /// <summary>
        /// Признак телесного оттенка по YCbCr.
        /// </summary>
        /// <remarks>
        /// Диапазоны Cb 77..127 и Cr 133..173 — классический порог для детекции кожи;
        /// он неточен на краях, но здесь и не нужна сегментация лица: достаточно понять,
        /// что участок в основном занят человеком.
        /// </remarks>
        private static bool IsSkinTone(int red, int green, int blue)
        {
            int chromaBlue = (int)(128 - 0.168736 * red - 0.331264 * green + 0.5 * blue);
            int chromaRed = (int)(128 + 0.5 * red - 0.418688 * green - 0.081312 * blue);

            return chromaBlue >= 77 && chromaBlue <= 127
                   && chromaRed >= 133 && chromaRed <= 173;
        }

        /// <summary>Декодирует снимок уменьшенным, чтобы анализ был быстрым и дешёвым.</summary>
        private static Bitmap? DecodeScaled(string photoPath)
        {
            try
            {
                var boundsOptions = new BitmapFactory.Options { InJustDecodeBounds = true };
                BitmapFactory.DecodeFile(photoPath, boundsOptions);

                if (boundsOptions.OutWidth <= 0)
                {
                    return null;
                }

                int sampleSize = 1;
                while (boundsOptions.OutWidth / (sampleSize * 2) >= AnalysisWidthPixels)
                {
                    sampleSize *= 2;
                }

                var decodeOptions = new BitmapFactory.Options { InSampleSize = sampleSize };
                return BitmapFactory.DecodeFile(photoPath, decodeOptions);
            }
            catch (System.Exception decodeFailure)
            {
                // Битый или нечитаемый файл не должен ломать показ: вернём null,
                // и часы просто переедут в следующий угол по кругу.
                System.Diagnostics.Debug.WriteLine(
                    $"Не удалось разобрать {photoPath}: {decodeFailure.Message}");
                return null;
            }
        }
    }
}
