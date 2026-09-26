namespace PhotoFrame
{
    /// <summary>
    /// Расписание ночного режима.
    /// </summary>
    public static class NightSchedule
    {
        /// <summary>
        /// True, если час попадает в ночной интервал.
        /// </summary>
        /// <remarks>
        /// Интервал обычно переходит через полночь (22:00–07:00), поэтому наивное
        /// «начало &lt;= час &lt; конец» здесь не работает и логика вынесена под тесты.
        /// Совпадение начала и конца трактуется как «ночного режима нет»: иначе
        /// не отличить ноль часов от всех двадцати четырёх.
        /// </remarks>
        public static bool IsNightHour(int hour, int startHour, int endHour)
        {
            if (startHour == endHour)
            {
                return false;
            }

            return startHour < endHour
                ? hour >= startHour && hour < endHour
                : hour >= startHour || hour < endHour;
        }
    }
}
