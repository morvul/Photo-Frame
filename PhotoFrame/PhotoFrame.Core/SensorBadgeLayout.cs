using System;
using System.Collections.Generic;

namespace PhotoFrame
{
    /// <summary>
    /// Раскладывает значки датчиков по строкам.
    /// </summary>
    /// <remarks>
    /// Датчиков может быть сколько угодно, а места в строке — нет: одной строкой
    /// показания уезжали за края экрана. Раскладка по три в строке читается лучше
    /// переноса по ширине: строки выходят одинаковой длины, и глаз находит нужное
    /// значение на том же месте, а не там, куда его отодвинул сосед.
    ///
    /// Живёт в Core и не знает ни про Canvas, ни про Label: и ночные часы (кадр
    /// с шахматной маской), и дневная надпись поверх снимка раскладывают значки
    /// одинаково, а проверять это удобно тестами.
    /// </remarks>
    public static class SensorBadgeLayout
    {
        /// <summary>Сколько значков помещается в строку.</summary>
        public const int BadgesPerLine = 3;

        /// <summary>
        /// Чем разделены значки в строке, собранной страницей: три пробела. Внутри
        /// значка пробел один («🌱 24.5°C»), поэтому разделитель шире — иначе
        /// показания сливались бы друг с другом.
        /// </summary>
        public const string BadgeSeparator = "   ";

        /// <summary>
        /// Разбирает строку значков на отдельные значки.
        /// </summary>
        public static List<string> SplitBadges(string? sensorText)
        {
            var badges = new List<string>();

            if (string.IsNullOrWhiteSpace(sensorText))
            {
                return badges;
            }

            foreach (string badge in sensorText.Split(
                BadgeSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = badge.Trim();
                if (trimmed.Length > 0)
                {
                    badges.Add(trimmed);
                }
            }

            return badges;
        }

        /// <summary>
        /// Раскладывает значки по строкам, не больше <paramref name="badgesPerLine"/>
        /// в каждой.
        /// </summary>
        public static List<string> SplitIntoLines(string? sensorText, int badgesPerLine = BadgesPerLine)
        {
            var lines = new List<string>();
            List<string> badges = SplitBadges(sensorText);

            if (badges.Count == 0)
            {
                return lines;
            }

            int perLine = Math.Max(1, badgesPerLine);

            for (int start = 0; start < badges.Count; start += perLine)
            {
                lines.Add(string.Join(
                    BadgeSeparator, badges.GetRange(start, Math.Min(perLine, badges.Count - start))));
            }

            return lines;
        }

        /// <summary>
        /// То же, но одной строкой с переводами строк — для обычной подписи, которая
        /// умеет показывать многострочный текст сама.
        /// </summary>
        public static string WrapWithLineBreaks(string? sensorText, int badgesPerLine = BadgesPerLine) =>
            string.Join('\n', SplitIntoLines(sensorText, badgesPerLine));
    }
}
