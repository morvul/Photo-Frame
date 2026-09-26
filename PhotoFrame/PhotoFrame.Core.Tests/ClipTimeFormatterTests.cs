using PhotoFrame;
using Xunit;

namespace PhotoFrame.Tests
{
    public class ClipTimeFormatterTests
    {
        [Theory]
        [InlineData(0, "0:00")]
        [InlineData(999, "0:00")]
        [InlineData(1000, "0:01")]
        [InlineData(7000, "0:07")]
        [InlineData(17000, "0:17")]
        [InlineData(59999, "0:59")]
        [InlineData(60000, "1:00")]
        [InlineData(605000, "10:05")]
        public void FormatsShortClipsAsMinutesAndSeconds(int milliseconds, string expected)
        {
            Assert.Equal(expected, ClipTimeFormatter.Describe(milliseconds));
        }

        [Theory]
        [InlineData(3600000, "1:00:00")]
        [InlineData(3661000, "1:01:01")]
        [InlineData(7325000, "2:02:05")]
        public void ExpandsToHoursForLongRecordings(int milliseconds, string expected)
        {
            Assert.Equal(expected, ClipTimeFormatter.Describe(milliseconds));
        }

        [Fact]
        public void TreatsNegativeAsZero()
        {
            // VideoView возвращает -1, пока файл не подготовлен.
            Assert.Equal("0:00", ClipTimeFormatter.Describe(-1));
        }
    }
}
