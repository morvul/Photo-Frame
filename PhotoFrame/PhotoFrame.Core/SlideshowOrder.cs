namespace PhotoFrame
{
    /// <summary>
    /// Восстановление порядка показа и места в нём после перезапуска.
    /// </summary>
    /// <remarks>
    /// Без этого после включения рамки показ идёт заново: при случайном порядке —
    /// с новой перетасовки, поэтому кадр, висевший на экране до выключения, может
    /// не повториться целый круг.
    /// </remarks>
    public static class SlideshowOrder
    {
        /// <summary>
        /// Раскладывает найденные кадры в сохранённом порядке и возвращает место,
        /// с которого продолжать показ.
        /// </summary>
        /// <param name="currentPaths">
        /// Найденные сейчас кадры. При успехе переупорядочиваются по месту.
        /// </param>
        /// <param name="savedOrder">Порядок, сохранённый в прошлый раз.</param>
        /// <param name="lastShownPath">Кадр, показанный последним.</param>
        /// <returns>
        /// Номер кадра, с которого продолжать, либо -1, если сохранённый порядок
        /// использовать нельзя — тогда вызывающий код задаёт порядок заново.
        /// </returns>
        /// <remarks>
        /// Порядок принимается только когда набор кадров совпал целиком. Иначе снимки
        /// успели добавить или убрать, и продолжать прежнюю последовательность
        /// бессмысленно: её всё равно нужно пересобирать.
        /// </remarks>
        public static int TryApplySavedOrder(
            List<string> currentPaths,
            IReadOnlyList<string>? savedOrder,
            string? lastShownPath)
        {
            ArgumentNullException.ThrowIfNull(currentPaths);

            if (savedOrder is null || savedOrder.Count != currentPaths.Count)
            {
                return -1;
            }

            // Каждый сохранённый путь должен найтись среди текущих, и ровно один раз:
            // Remove заодно отсекает повторы в сохранённом списке.
            var unmatchedPaths = new HashSet<string>(currentPaths, StringComparer.OrdinalIgnoreCase);
            foreach (string savedPath in savedOrder)
            {
                if (!unmatchedPaths.Remove(savedPath))
                {
                    return -1;
                }
            }

            if (unmatchedPaths.Count > 0)
            {
                return -1;
            }

            currentPaths.Clear();
            currentPaths.AddRange(savedOrder);

            if (string.IsNullOrEmpty(lastShownPath))
            {
                return 0;
            }

            int lastShownIndex = currentPaths.FindIndex(
                path => path.Equals(lastShownPath, StringComparison.OrdinalIgnoreCase));

            // Порядок восстановлен, а вот кадра в нём нет — начинаем с начала списка.
            return lastShownIndex < 0 ? 0 : lastShownIndex;
        }
    }
}
