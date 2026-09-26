using System.Collections.Generic;
using PhotoFrame;
using Xunit;

namespace PhotoFrame.Core.Tests
{
    /// <summary>
    /// Раскладка значков датчиков по строкам.
    /// </summary>
    /// <remarks>
    /// Строки собираются из настоящих значений, какие приходят от Home Assistant:
    /// со значком, пробелом и единицей измерения. Разделитель между значками — три
    /// пробела, и внутренний одиночный пробел не должен приниматься за него.
    /// </remarks>
    public class SensorBadgeLayoutTests
    {
        private const string FourBadges = "🌱 23.9°C   🍳 23.5°C   🛋 22.0°C   🌳 19.2°C";

        [Fact]
        public void SplitIntoLines_PutsThreeBadgesPerLine()
        {
            List<string> lines = SensorBadgeLayout.SplitIntoLines(FourBadges);

            Assert.Equal(2, lines.Count);
            Assert.Equal("🌱 23.9°C   🍳 23.5°C   🛋 22.0°C", lines[0]);
            Assert.Equal("🌳 19.2°C", lines[1]);
        }

        [Fact]
        public void SplitBadges_KeepsTheSpaceInsideABadge()
        {
            List<string> badges = SensorBadgeLayout.SplitBadges(FourBadges);

            Assert.Equal(4, badges.Count);
            Assert.Equal("🌱 23.9°C", badges[0]);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void SplitIntoLines_ReturnsNothingWithoutBadges(string? sensorText) =>
            Assert.Empty(SensorBadgeLayout.SplitIntoLines(sensorText));

        [Fact]
        public void SplitIntoLines_KeepsASingleBadgeOnItsOwnLine() =>
            Assert.Equal("🌱 23.9°C", Assert.Single(SensorBadgeLayout.SplitIntoLines("🌱 23.9°C")));

        [Fact]
        public void SplitIntoLines_FillsEveryLineWhenTheCountDivides()
        {
            List<string> lines = SensorBadgeLayout.SplitIntoLines(
                "a   b   c   d   e   f");

            Assert.Equal(new[] { "a   b   c", "d   e   f" }, lines);
        }

        /// <summary>Ноль в строке означал бы бесконечный цикл — берём хотя бы один.</summary>
        [Fact]
        public void SplitIntoLines_SurvivesAbsurdLineLength() =>
            Assert.Equal(3, SensorBadgeLayout.SplitIntoLines("a   b   c", badgesPerLine: 0).Count);

        [Fact]
        public void WrapWithLineBreaks_JoinsLinesForAPlainLabel() =>
            Assert.Equal(
                "a   b   c\nd",
                SensorBadgeLayout.WrapWithLineBreaks("a   b   c   d"));
    }
}
