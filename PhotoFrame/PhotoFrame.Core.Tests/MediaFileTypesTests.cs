using PhotoFrame;
using Xunit;

namespace PhotoFrame.Tests
{
    public class MediaFileTypesTests
    {
        [Theory]
        [InlineData("photo.jpg")]
        [InlineData("photo.JPG")]
        [InlineData("photo.jpeg")]
        [InlineData("photo.png")]
        [InlineData("photo.webp")]
        [InlineData("photo.bmp")]
        public void RecognisesImages(string fileName)
        {
            Assert.True(MediaFileTypes.IsImage(fileName));
            Assert.False(MediaFileTypes.IsVideo(fileName));
            Assert.True(MediaFileTypes.IsSupportedMedia(fileName));
        }

        [Theory]
        [InlineData("clip.mp4")]
        [InlineData("clip.MP4")]
        [InlineData("clip.m4v")]
        [InlineData("clip.3gp")]
        [InlineData("clip.webm")]
        [InlineData("clip.mkv")]
        public void RecognisesVideo(string fileName)
        {
            Assert.True(MediaFileTypes.IsVideo(fileName));
            Assert.False(MediaFileTypes.IsImage(fileName));
            Assert.True(MediaFileTypes.IsSupportedMedia(fileName));
        }

        [Theory]
        [InlineData("notes.txt")]
        [InlineData("raw.dng")]
        [InlineData("image.heic")]
        [InlineData("noextension")]
        [InlineData("trailing.")]
        public void RejectsEverythingElse(string fileName)
        {
            // HEIC и DNG сознательно не поддерживаются: показать чёрный кадр хуже,
            // чем не показать его вовсе.
            Assert.False(MediaFileTypes.IsSupportedMedia(fileName));
        }

        [Fact]
        public void WorksOnFullPaths()
        {
            Assert.True(MediaFileTypes.IsImage("/storage/emulated/0/DCIM/DSCF0613.JPG"));
            Assert.True(MediaFileTypes.IsVideo("/storage/emulated/0/DCIM/VID_20180430.mp4"));
        }

        [Fact]
        public void DescribeSupportedExtensions_ListsBothKinds()
        {
            string description = MediaFileTypes.DescribeSupportedExtensions();

            Assert.Contains(".jpg", description);
            Assert.Contains(".mp4", description);
        }
    }
}
