using PhotoFrame;
using Xunit;

namespace PhotoFrame.Tests
{
    /// <summary>
    /// Ночной интервал почти всегда переходит через полночь, и наивное сравнение
    /// «начало &lt;= час &lt; конец» на нём ломается. Здесь зафиксированы обе формы.
    /// </summary>
    public class NightScheduleTests
    {
        [Theory]
        // Интервал через полночь — значение по умолчанию 22:00–07:00.
        [InlineData(22, true)]
        [InlineData(23, true)]
        [InlineData(0, true)]
        [InlineData(3, true)]
        [InlineData(6, true)]
        [InlineData(7, false)]
        [InlineData(12, false)]
        [InlineData(21, false)]
        public void DefaultEveningToMorningWindow(int hour, bool expected)
        {
            Assert.Equal(expected, NightSchedule.IsNightHour(hour, startHour: 22, endHour: 7));
        }

        [Theory]
        // Интервал внутри одних суток, например для затемнения в обед.
        [InlineData(9, false)]
        [InlineData(10, true)]
        [InlineData(11, true)]
        [InlineData(12, false)]
        public void SameDayWindow(int hour, bool expected)
        {
            Assert.Equal(expected, NightSchedule.IsNightHour(hour, startHour: 10, endHour: 12));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(12)]
        [InlineData(23)]
        public void EqualBoundsMeansNoNightMode(int hour)
        {
            // Иначе не отличить ноль часов от всех двадцати четырёх.
            Assert.False(NightSchedule.IsNightHour(hour, startHour: 22, endHour: 22));
        }

        [Fact]
        public void StartHourIsInclusiveAndEndHourIsExclusive()
        {
            Assert.True(NightSchedule.IsNightHour(22, 22, 7));
            Assert.False(NightSchedule.IsNightHour(7, 22, 7));
        }
    }
}
