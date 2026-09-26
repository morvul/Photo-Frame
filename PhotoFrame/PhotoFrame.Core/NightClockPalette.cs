namespace PhotoFrame
{
    /// <summary>
    /// Цвета, которыми можно зажечь ночные часы.
    /// </summary>
    /// <remarks>
    /// Список закрытый, а не свободный ввод цвета: на рамке нет ни клавиатуры, ни
    /// пипетки, а подобранные оттенки заведомо читаются на чёрном. Хранится всё равно
    /// сам код цвета, а не номер в списке, — иначе правка списка перекрашивала бы часы.
    /// </remarks>
    public static class NightClockPalette
    {
        /// <summary>Вариант цвета: подпись для экрана настроек и код цвета.</summary>
        public sealed record Option(string Name, string Hex);

        /// <summary>
        /// Цвет по умолчанию. Тёплый белый: чистый белый ночью выглядит резким,
        /// а цветные оттенки — уже дело вкуса.
        /// </summary>
        public const string DefaultHex = "#FFD9A0";

        public static readonly Option[] Choices =
        {
            new("Тёплый белый", DefaultHex),
            new("Белый", "#FFFFFF"),
            new("Янтарный", "#FFB000"),
            new("Аквамарин", "#7FFFD4"),
            new("Голубой", "#00E5FF"),
            new("Зелёный", "#76FF03"),
            new("Розовый", "#FF80AB"),
        };

        /// <summary>
        /// Приводит сохранённое значение к цвету из списка.
        /// </summary>
        /// <remarks>
        /// Значение приходит из настроек устройства и могло остаться от прежней версии
        /// приложения, поэтому неизвестный код заменяется цветом по умолчанию: на рамке
        /// некому разбираться с исключением при отрисовке часов.
        /// </remarks>
        public static string ResolveHex(string? storedHex)
        {
            if (string.IsNullOrWhiteSpace(storedHex))
            {
                return DefaultHex;
            }

            string trimmedHex = storedHex.Trim();

            foreach (Option option in Choices)
            {
                if (option.Hex.Equals(trimmedHex, StringComparison.OrdinalIgnoreCase))
                {
                    return option.Hex;
                }
            }

            return DefaultHex;
        }

        /// <summary>Номер цвета в списке; для неизвестного — номер цвета по умолчанию.</summary>
        public static int IndexOfHex(string? storedHex)
        {
            string resolvedHex = ResolveHex(storedHex);

            for (int index = 0; index < Choices.Length; index++)
            {
                if (Choices[index].Hex.Equals(resolvedHex, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            return 0;
        }

        /// <summary>Подписи вариантов в порядке списка — для Picker на экране настроек.</summary>
        public static string[] BuildChoiceLabels()
        {
            var labels = new string[Choices.Length];
            for (int index = 0; index < labels.Length; index++)
            {
                labels[index] = Choices[index].Name;
            }

            return labels;
        }
    }
}
