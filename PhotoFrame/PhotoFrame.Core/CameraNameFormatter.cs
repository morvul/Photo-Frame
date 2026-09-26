namespace PhotoFrame
{
    /// <summary>
    /// Приводит название камеры из EXIF к виду, пригодному для подписи под снимком.
    /// </summary>
    public static class CameraNameFormatter
    {
        /// <summary>
        /// Юридические придатки в названии производителя на экране только мешают.
        /// </summary>
        /// <remarks>
        /// Список намеренно короткий и состоит из отдельных слов: вычищать всё подряд
        /// рискованно, в названиях моделей встречаются похожие сокращения.
        /// </remarks>
        private static readonly string[] CorporateNoiseWords =
            { "CORPORATION", "CORP.", "CORP", "INC.", "INC", "CO.,LTD.", "CO.", "LTD.", "LTD" };

        /// <summary>
        /// Собирает подпись из полей Make и Model.
        /// </summary>
        /// <remarks>
        /// Камеры заполняют эти поля по-разному: "NIKON CORPORATION" + "NIKON D60" должно
        /// дать "NIKON D60", а не повторять производителя дважды.
        /// </remarks>
        public static string? Combine(string? make, string? model)
        {
            string? cleanedMake = StripCorporateSuffix(make?.Trim());
            model = model?.Trim();

            if (string.IsNullOrEmpty(model))
            {
                return string.IsNullOrEmpty(cleanedMake) ? null : cleanedMake;
            }

            if (string.IsNullOrEmpty(cleanedMake)
                || model.StartsWith(cleanedMake, StringComparison.OrdinalIgnoreCase))
            {
                return RemoveRepeatedWords(model);
            }

            return RemoveRepeatedWords(cleanedMake + " " + model);
        }

        /// <summary>Убирает CORPORATION, INC и подобное из названия производителя.</summary>
        public static string? StripCorporateSuffix(string? manufacturer)
        {
            if (string.IsNullOrEmpty(manufacturer))
            {
                return manufacturer;
            }

            string[] words = manufacturer.Split(
                ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var kept = new List<string>(words.Length);
            foreach (string word in words)
            {
                bool isNoise = false;
                foreach (string noiseWord in CorporateNoiseWords)
                {
                    if (string.Equals(word, noiseWord, StringComparison.OrdinalIgnoreCase))
                    {
                        isNoise = true;
                        break;
                    }
                }

                if (!isNoise)
                {
                    kept.Add(word);
                }
            }

            // Если от названия ничего не осталось, лучше вернуть исходное, чем пустоту.
            return kept.Count == 0 ? manufacturer : string.Join(' ', kept);
        }

        /// <summary>Убирает повторяющиеся слова, сохраняя первое вхождение.</summary>
        public static string RemoveRepeatedWords(string text)
        {
            string[] words = text.Split(
                ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var kept = new List<string>(words.Length);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string word in words)
            {
                if (seen.Add(word))
                {
                    kept.Add(word);
                }
            }

            return string.Join(' ', kept);
        }
    }
}
