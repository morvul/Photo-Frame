using PhotoFrame;
using Xunit;

namespace PhotoFrame.Tests
{
    public class MediaLabelFormatterTests
    {
        [Theory]
        [InlineData("/storage/emulated/0/DCIM/Camera/IMG_1234.jpg", "Camera/IMG_1234.jpg")]
        [InlineData("/storage/emulated/0/DCIM/Archive/SonyE k500/P1010001.JPG", "SonyE k500/P1010001.JPG")]
        public void ShowsTheContainingFolderWithTheFileName(string path, string expected)
        {
            Assert.Equal(expected, MediaLabelFormatter.Describe(path));
        }

        [Fact]
        public void KeepsOnlyTheFileNameAtTheStorageRoot()
        {
            Assert.Equal("photo.jpg", MediaLabelFormatter.Describe("/photo.jpg"));
        }

        [Fact]
        public void HandlesAPlainFileNameWithoutAPath()
        {
            Assert.Equal("photo.jpg", MediaLabelFormatter.Describe("photo.jpg"));
        }

        [Fact]
        public void IgnoresATrailingSeparator()
        {
            // Иначе разделитель на конце съедал имя файла.
            Assert.Equal("Camera/IMG_1.jpg", MediaLabelFormatter.Describe("/DCIM/Camera/IMG_1.jpg/"));
        }

        [Fact]
        public void UnderstandsWindowsSeparators()
        {
            // Пути с рамки — Unix, но тесты идут на Windows, и формат не должен зависеть
            // от разделителя.
            Assert.Equal("Camera/IMG_1.jpg", MediaLabelFormatter.Describe(@"C:\DCIM\Camera\IMG_1.jpg"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void GivesNothingForAnEmptyPath(string? path)
        {
            Assert.Equal(string.Empty, MediaLabelFormatter.Describe(path!));
        }
    }
}
