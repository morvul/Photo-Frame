using PhotoFrame;
using Xunit;

namespace PhotoFrame.Tests
{
    public class NightClockPaletteTests
    {
        [Fact]
        public void KeepsAColorFromTheList()
        {
            Assert.Equal("#00E5FF", NightClockPalette.ResolveHex("#00E5FF"));
        }

        [Fact]
        public void IgnoresLetterCase()
        {
            // Значение могло попасть в настройки из другой версии списка.
            Assert.Equal("#00E5FF", NightClockPalette.ResolveHex("#00e5ff"));
        }

        [Fact]
        public void TrimsStoredValue()
        {
            Assert.Equal("#FFFFFF", NightClockPalette.ResolveHex("  #FFFFFF  "));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("#123456")]
        [InlineData("красный")]
        [InlineData("FFFFFF")]
        public void FallsBackToDefaultForAnythingElse(string? storedHex)
        {
            // На рамке некому разбираться с исключением при отрисовке часов.
            Assert.Equal(NightClockPalette.DefaultHex, NightClockPalette.ResolveHex(storedHex));
        }

        [Fact]
        public void DefaultIsPresentInTheList()
        {
            Assert.Contains(NightClockPalette.Choices, option =>
                option.Hex == NightClockPalette.DefaultHex);
        }

        [Fact]
        public void IndexPointsAtTheStoredColor()
        {
            int index = NightClockPalette.IndexOfHex("#76FF03");

            Assert.Equal("#76FF03", NightClockPalette.Choices[index].Hex);
        }

        [Fact]
        public void UnknownColorIndexPointsAtTheDefault()
        {
            int index = NightClockPalette.IndexOfHex("#010203");

            Assert.Equal(NightClockPalette.DefaultHex, NightClockPalette.Choices[index].Hex);
        }

        [Fact]
        public void LabelsMatchTheChoiceOrder()
        {
            // Picker хранит номер строки, поэтому подписи и цвета обязаны идти в лад.
            string[] labels = NightClockPalette.BuildChoiceLabels();

            Assert.Equal(NightClockPalette.Choices.Length, labels.Length);
            for (int index = 0; index < labels.Length; index++)
            {
                Assert.Equal(NightClockPalette.Choices[index].Name, labels[index]);
            }
        }

        [Fact]
        public void EveryChoiceIsASixDigitHexColor()
        {
            // Android.Graphics.Color.ParseColor принимает именно такой вид.
            foreach (NightClockPalette.Option option in NightClockPalette.Choices)
            {
                Assert.Matches("^#[0-9A-F]{6}$", option.Hex);
            }
        }

        [Fact]
        public void ColorsAreDistinct()
        {
            var seenHexValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (NightClockPalette.Option option in NightClockPalette.Choices)
            {
                Assert.True(seenHexValues.Add(option.Hex), $"Цвет {option.Hex} повторяется");
            }
        }
    }
}
