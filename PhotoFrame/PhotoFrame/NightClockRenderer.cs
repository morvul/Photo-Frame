using Android.Graphics;

// Paint и Color есть и в Android.Graphics, и в Microsoft.Maui.Graphics, который
// подключён через ImplicitUsings, поэтому нужны однозначные псевдонимы.
using AndroidPaint = Android.Graphics.Paint;
using AndroidColor = Android.Graphics.Color;
using AndroidRect = Android.Graphics.Rect;

namespace PhotoFrame
{
    /// <summary>
    /// Рисует ночные часы на весь экран, зажигая только каждый второй пиксель.
    /// </summary>
    /// <remarks>
    /// Шахматная маска решает сразу две задачи: суммарная яркость падает примерно вдвое
    /// без изменения подсветки, и набор светящихся пикселей меняется при каждом
    /// обновлении времени, поэтому статичная картинка не «выжигается» в одном месте.
    ///
    /// Пиксельная маска недостижима средствами Microsoft.Maui.Graphics, поэтому здесь
    /// напрямую используется Android.Graphics: у проекта единственный целевой
    /// фреймворк — net10.0-android.
    /// </remarks>
    internal static class NightClockRenderer
    {
        /// <summary>Доля ширины экрана, которую занимает строка времени.</summary>
        private const float TimeWidthFraction = 0.92f;

        /// <summary>Размер даты относительно размера времени.</summary>
        private const float DateSizeFraction = 0.13f;

        /// <summary>
        /// Размер строки датчиков относительно размера времени. Чуть крупнее даты:
        /// показания читают с другого конца комнаты, а дату — заодно с часами.
        /// </summary>
        private const float SensorSizeFraction = 0.16f;

        /// <summary>
        /// Рисует кадр с часами. Вызывать в фоновом потоке: отрисовка кадра 1280x800
        /// на такой рамке занимает заметное время.
        /// </summary>
        /// <remarks>
        /// Возвращается именно <see cref="Bitmap"/>, а не PNG: страница отдаёт его
        /// платформенному ImageView напрямую, без кодирования в поток и обратного
        /// декодирования, на время которого MAUI гасит картинку.
        ///
        /// Владелец кадра — вызывающий код: он же его и освобождает.
        /// </remarks>
        /// <param name="phaseShifted">
        /// Сдвигает шахматную маску на один пиксель — при каждом обновлении времени
        /// светятся уже другие пиксели.
        /// </param>
        /// <param name="colorHex">Цвет часов из <see cref="NightClockPalette"/>.</param>
        /// <param name="sensorText">
        /// Показания датчиков одной строкой. Пусто — строка не рисуется.
        /// </param>
        /// <param name="reusableFrame">
        /// Кадр прошлой минуты того же слоя: если он подходит по размеру, рисуем прямо
        /// в нём. Каждая минута — это 1280x800x4 байта, и на устройстве с гигабайтом
        /// памяти выделять их заново всю ночь ни к чему.
        /// </param>
        /// <param name="showColon">
        /// Рисовать ли двоеточие между часами и минутами. Страница гасит его через
        /// секунду и зажигает снова: иначе по неподвижным часам не понять, идут они
        /// или рамка давно замерла. Цифры при этом не смещаются — каждая часть строки
        /// рисуется на своём месте.
        /// </param>
        /// <param name="intensityPercent">
        /// Насыщенность рисунка в процентах; 0 — в полную силу. Нужна потому, что
        /// подсветка упирается в свой предел: на рамке это 20 из 255, около 8%, и в
        /// темноте часы всё равно слепят. Дальше гасить приходится самой краской.
        /// </param>
        public static Bitmap RenderBitmap(
            int widthPixels,
            int heightPixels,
            string timeText,
            string? dateText,
            string? sensorText,
            bool phaseShifted,
            string colorHex,
            int intensityPercent,
            Bitmap? reusableFrame = null,
            bool showColon = true)
        {
            // Пригодный кадр переиспользуем: фон всё равно заливается чёрным целиком,
            // так что от прошлой минуты ничего не просвечивает.
            Bitmap frame = reusableFrame is { IsRecycled: false }
                           && reusableFrame.Width == widthPixels
                           && reusableFrame.Height == heightPixels
                ? reusableFrame
                : Bitmap.CreateBitmap(widthPixels, heightPixels, Bitmap.Config.Argb8888!)!;
            using var canvas = new Canvas(frame);
            canvas.DrawColor(AndroidColor.Black);

            using Bitmap checkerPattern = CreateCheckerPattern(colorHex);
            using var checkerShader = new BitmapShader(
                checkerPattern, Shader.TileMode.Repeat!, Shader.TileMode.Repeat!);

            if (phaseShifted)
            {
                // Сдвиг на один пиксель по горизонтали инвертирует шахматный порядок.
                using var shaderMatrix = new Matrix();
                shaderMatrix.SetTranslate(1, 0);
                checkerShader.SetLocalMatrix(shaderMatrix);
            }

            using var textPaint = new AndroidPaint(PaintFlags.AntiAlias)
            {
                TextAlign = AndroidPaint.Align.Center,
                Alpha = ToAlpha(intensityPercent),
            };

            textPaint.SetShader(checkerShader);
            textPaint.SetTypeface(Typeface.Create(Typeface.Default, TypefaceStyle.Bold));

            float timeTextSize = FitTextSize(textPaint, timeText, widthPixels * TimeWidthFraction);
            textPaint.TextSize = timeTextSize;

            // Вертикальное центрирование по фактической высоте глифов, а не по строке:
            // у крупных цифр разница заметна.
            var timeBounds = new AndroidRect();
            textPaint.GetTextBounds(timeText, 0, timeText.Length, timeBounds);

            bool hasDate = !string.IsNullOrEmpty(dateText);
            float dateTextSize = timeTextSize * DateSizeFraction;
            float dateBlockHeight = hasDate ? dateTextSize * 1.6f : 0f;

            float sensorTextSize = timeTextSize * SensorSizeFraction;

            // Значков может быть сколько угодно, поэтому строка переносится: одной
            // строкой показания попросту уезжали за края экрана.
            List<string> sensorLines = WrapSensorBadges(
                sensorText, sensorTextSize, widthPixels * TimeWidthFraction);

            bool hasSensors = sensorLines.Count > 0;
            float sensorLineHeight = sensorTextSize * 1.5f;
            float sensorBlockHeight = hasSensors
                ? sensorTextSize * 0.2f + sensorLines.Count * sensorLineHeight
                : 0f;

            // Весь блок центрируется целиком: иначе время съезжало бы вверх на каждую
            // добавленную под ним строку.
            float centreX = widthPixels / 2f;
            float timeBaselineY =
                (heightPixels - dateBlockHeight - sensorBlockHeight) / 2f
                + timeBounds.Height() / 2f;

            DrawTime(canvas, textPaint, timeText, centreX, timeBaselineY, showColon);

            textPaint.SetTypeface(Typeface.Create(Typeface.Default, TypefaceStyle.Normal));

            if (hasDate)
            {
                textPaint.TextSize = dateTextSize;
                canvas.DrawText(dateText!, centreX, timeBaselineY + dateBlockHeight, textPaint);
            }

            if (hasSensors)
            {
                textPaint.TextSize = sensorTextSize;
                float sensorBaselineY =
                    timeBaselineY + dateBlockHeight + sensorTextSize * 0.2f + sensorLineHeight;

                foreach (string sensorLine in sensorLines)
                {
                    canvas.DrawText(sensorLine, centreX, sensorBaselineY, textPaint);
                    sensorBaselineY += sensorLineHeight;
                }
            }

            return frame;
        }

        /// <summary>
        /// Рисует время, при необходимости без двоеточия.
        /// </summary>
        /// <remarks>
        /// Строка рисуется по частям, а не целиком: убрать двоеточие из целой строки
        /// значило бы сдвинуть минуты влево, и часы дёргались бы каждую секунду.
        /// Части ставятся по своим отступам, поэтому пропавшее двоеточие оставляет
        /// на своём месте ровно пустоту.
        /// </remarks>
        private static void DrawTime(
            Canvas canvas,
            AndroidPaint textPaint,
            string timeText,
            float centreX,
            float baselineY,
            bool showColon)
        {
            int separatorIndex = timeText.IndexOf(':');

            if (separatorIndex <= 0 || separatorIndex >= timeText.Length - 1)
            {
                // Двоеточия нет вовсе — рисуем как есть.
                canvas.DrawText(timeText, centreX, baselineY, textPaint);
                return;
            }

            string hoursText = timeText[..separatorIndex];
            string separatorText = timeText[separatorIndex].ToString();
            string minutesText = timeText[(separatorIndex + 1)..];

            float hoursWidth = textPaint.MeasureText(hoursText);
            float separatorWidth = textPaint.MeasureText(separatorText);
            float minutesWidth = textPaint.MeasureText(minutesText);

            AndroidPaint.Align previousAlign = textPaint.TextAlign;
            textPaint.TextAlign = AndroidPaint.Align.Left!;

            float leftX = centreX - (hoursWidth + separatorWidth + minutesWidth) / 2f;

            canvas.DrawText(hoursText, leftX, baselineY, textPaint);

            if (showColon)
            {
                canvas.DrawText(separatorText, leftX + hoursWidth, baselineY, textPaint);
            }

            canvas.DrawText(
                minutesText, leftX + hoursWidth + separatorWidth, baselineY, textPaint);

            textPaint.TextAlign = previousAlign;
        }

        /// <summary>
        /// Раскладывает значки датчиков по строкам: по три, а если строка не влезает
        /// в ширину — то и меньше.
        /// </summary>
        /// <remarks>
        /// По три, а не «сколько поместится»: строки выходят одинаковой длины, и глаз
        /// находит нужное значение на том же месте. Ширина всё равно проверяется —
        /// значки бывают длинными («🌡 -12.3°C»), и тогда в строке остаётся два.
        ///
        /// Перенос идёт по значкам, а не по словам: «🌱 24.5°C» — единое целое, и рвать
        /// его между значком и значением нельзя. Значок шире всей строки (такого быть
        /// не должно, но всё же) остаётся один в строке и просто выйдет за края.
        /// </remarks>
        private static List<string> WrapSensorBadges(string? sensorText, float textSize, float maxWidth)
        {
            var lines = new List<string>();
            if (string.IsNullOrWhiteSpace(sensorText))
            {
                return lines;
            }

            using var measurePaint = new AndroidPaint(PaintFlags.AntiAlias) { TextSize = textSize };

            foreach (string line in SensorBadgeLayout.SplitIntoLines(sensorText))
            {
                if (measurePaint.MeasureText(line) <= maxWidth)
                {
                    lines.Add(line);
                    continue;
                }

                // Три значка в ширину не уместились — досыпаем по одному, пока влезает.
                string currentLine = string.Empty;

                foreach (string badge in SensorBadgeLayout.SplitBadges(line))
                {
                    string candidate = currentLine.Length == 0
                        ? badge
                        : currentLine + SensorBadgeLayout.BadgeSeparator + badge;

                    if (currentLine.Length > 0 && measurePaint.MeasureText(candidate) > maxWidth)
                    {
                        lines.Add(currentLine);
                        currentLine = badge;
                        continue;
                    }

                    currentLine = candidate;
                }

                if (currentLine.Length > 0)
                {
                    lines.Add(currentLine);
                }
            }

            return lines;
        }

        /// <summary>
        /// Проценты насыщенности в прозрачность краски.
        /// </summary>
        /// <remarks>
        /// Нижнего предела нет: этой настройкой и гасят часы до едва различимых, когда
        /// подсветка уже упёрлась в свой. Единица шкалы — примерно три из 255, и в
        /// темноте этого хватает, чтобы разглядеть время, не освещая комнату.
        /// </remarks>
        private static int ToAlpha(int intensityPercent)
        {
            if (intensityPercent <= 0)
            {
                return 255;
            }

            return System.Math.Clamp(intensityPercent * 255 / 100, 2, 255);
        }

        /// <summary>Подбирает размер шрифта так, чтобы строка заняла нужную ширину.</summary>
        private static float FitTextSize(AndroidPaint paint, string text, float targetWidth)
        {
            const float probeSize = 100f;
            paint.TextSize = probeSize;

            float measuredWidth = paint.MeasureText(text);
            return measuredWidth <= 0
                ? probeSize
                : probeSize * (targetWidth / measuredWidth);
        }

        /// <summary>
        /// Плитка 2x2, в которой светятся только пиксели по диагонали.
        /// </summary>
        private static Bitmap CreateCheckerPattern(string colorHex)
        {
            Bitmap pattern = Bitmap.CreateBitmap(2, 2, Bitmap.Config.Argb8888!)!;
            AndroidColor litColor = ParseColor(colorHex);

            pattern.SetPixel(0, 0, litColor);
            pattern.SetPixel(1, 1, litColor);
            pattern.SetPixel(1, 0, AndroidColor.Transparent);
            pattern.SetPixel(0, 1, AndroidColor.Transparent);

            return pattern;
        }

        /// <summary>
        /// Разбирает код цвета. Значение приходит из настроек, поэтому непонятное
        /// заменяется цветом по умолчанию, а не роняет отрисовку.
        /// </summary>
        private static AndroidColor ParseColor(string colorHex)
        {
            try
            {
                return AndroidColor.ParseColor(NightClockPalette.ResolveHex(colorHex));
            }
            catch (Java.Lang.IllegalArgumentException)
            {
                return AndroidColor.ParseColor(NightClockPalette.DefaultHex);
            }
        }
    }
}
