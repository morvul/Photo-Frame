namespace PhotoFrame
{
    /// <summary>
    /// Позиция и длительность видео для подписи под полосой прогресса.
    /// </summary>
    public static class ClipTimeFormatter
    {
        /// <summary>
        /// Время клипа в виде m:ss, а для записей длиннее часа — h:mm:ss.
        /// </summary>
        /// <remarks>
        /// Ведущий ноль в минутах не нужен: «0:07» читается лучше, чем «00:07»,
        /// а на длинных записях формат расширяется сам.
        /// </remarks>
        public static string Describe(int milliseconds)
        {
            var span = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));

            return span.TotalHours >= 1
                ? span.ToString(@"h\:mm\:ss")
                : span.ToString(@"m\:ss");
        }
    }
}
