using PhotoFrame;
using Xunit;

namespace PhotoFrame.Tests
{
    /// <summary>
    /// Подпись под снимком: реальные пары Make/Model зафиксированы тестами.
    /// </summary>
    public class CameraNameFormatterTests
    {
        [Theory]
        // Реальные пары Make/Model.
        [InlineData("OLYMPUS CORPORATION", "X-3,C-60Z", "OLYMPUS X-3,C-60Z")]
        [InlineData("NIKON CORPORATION", "NIKON D60", "NIKON D60")]
        [InlineData("FUJIFILM", "X-T5", "FUJIFILM X-T5")]
        [InlineData("SONY", "ILCE-5100", "SONY ILCE-5100")]
        [InlineData("Sony Ericsson", "K750i", "Sony Ericsson K750i")]
        // Производитель уже внутри модели — не повторяем.
        [InlineData("Apple", "Apple iPhone 12", "Apple iPhone 12")]
        [InlineData("Canon", "Canon EOS 5D", "Canon EOS 5D")]
        public void Combine_ProducesReadableName(string make, string model, string expected)
        {
            Assert.Equal(expected, CameraNameFormatter.Combine(make, model));
        }

        [Fact]
        public void Combine_UsesMakeWhenModelMissing()
        {
            Assert.Equal("FUJIFILM", CameraNameFormatter.Combine("FUJIFILM", null));
            Assert.Equal("FUJIFILM", CameraNameFormatter.Combine("FUJIFILM", "   "));
        }

        [Fact]
        public void Combine_UsesModelWhenMakeMissing()
        {
            Assert.Equal("X-T5", CameraNameFormatter.Combine(null, "X-T5"));
        }

        [Fact]
        public void Combine_ReturnsNullWhenNothingKnown()
        {
            // Подпись «неизвестно» поверх фотографии никому не нужна: строка не выводится.
            Assert.Null(CameraNameFormatter.Combine(null, null));
            Assert.Null(CameraNameFormatter.Combine("  ", ""));
        }

        [Theory]
        [InlineData("NIKON CORPORATION", "NIKON")]
        [InlineData("OLYMPUS CORP.", "OLYMPUS")]
        [InlineData("CANON INC.", "CANON")]
        [InlineData("SEIKO EPSON CORP", "SEIKO EPSON")]
        public void StripCorporateSuffix_RemovesLegalNoise(string input, string expected)
        {
            Assert.Equal(expected, CameraNameFormatter.StripCorporateSuffix(input));
        }

        [Fact]
        public void StripCorporateSuffix_KeepsOriginalWhenNothingWouldRemain()
        {
            // Лучше показать странное название, чем пустую строку.
            Assert.Equal("CORPORATION", CameraNameFormatter.StripCorporateSuffix("CORPORATION"));
        }

        [Fact]
        public void StripCorporateSuffix_MatchesWholeWordsOnly()
        {
            // "Incredible" начинается на "Inc", но обрезать его нельзя.
            Assert.Equal("Incredible Optics", CameraNameFormatter.StripCorporateSuffix("Incredible Optics"));
        }

        [Fact]
        public void RemoveRepeatedWords_IsCaseInsensitiveAndKeepsFirstSpelling()
        {
            Assert.Equal("Nikon D80", CameraNameFormatter.RemoveRepeatedWords("Nikon NIKON D80"));
        }

        [Fact]
        public void RemoveRepeatedWords_LeavesDistinctWordsAlone()
        {
            Assert.Equal("Sony Ericsson K750i", CameraNameFormatter.RemoveRepeatedWords("Sony Ericsson K750i"));
        }
    }
}
