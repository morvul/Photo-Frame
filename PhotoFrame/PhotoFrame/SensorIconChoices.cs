namespace PhotoFrame
{
    /// <summary>
    /// Набор значков, которые можно поставить датчику.
    /// </summary>
    /// <remarks>
    /// Взяты эмодзи, а не иконочный шрифт: тащить в APK ещё один шрифт ради десятка
    /// картинок незачем, а перечисленные символы входят в Unicode 6-7 и рисуются
    /// системным шрифтом Android 8.1.
    /// </remarks>
    public static class SensorIconChoices
    {
        /// <summary>Значок и его название для списка выбора.</summary>
        public static readonly (string Icon, string Title)[] All =
        {
            ("", "Без значка"),
            ("🏠", "Дом"),
            ("🛋", "Зал"),
            ("🍳", "Кухня"),
            ("🛏", "Спальня"),
            ("🚿", "Ванная"),
            ("🌱", "Теплица"),
            ("🌳", "Улица"),
            ("☀", "Солнце"),
            ("🌡", "Термометр"),
            ("💧", "Влажность"),
            ("⚡", "Электричество"),
            ("🔋", "Заряд"),
        };

        /// <summary>Подписи для DisplayActionSheet: значок и название в одной строке.</summary>
        public static string[] BuildActionSheetLabels()
        {
            var labels = new string[All.Length];
            for (int index = 0; index < All.Length; index++)
            {
                (string icon, string title) = All[index];
                labels[index] = string.IsNullOrEmpty(icon) ? title : icon + "  " + title;
            }

            return labels;
        }

        /// <summary>Обратное преобразование подписи из списка в сам значок.</summary>
        public static string? FindIconByLabel(string? label)
        {
            if (string.IsNullOrEmpty(label))
            {
                return null;
            }

            string[] labels = BuildActionSheetLabels();
            for (int index = 0; index < labels.Length; index++)
            {
                if (labels[index] == label)
                {
                    return All[index].Icon;
                }
            }

            return null;
        }
    }
}
