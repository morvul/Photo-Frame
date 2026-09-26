using System;
using System.Text;
using PhotoFrame;
using Xunit;

namespace PhotoFrame.Core.Tests
{
    /// <summary>
    /// Распознавание видеоконтейнера по началу файла.
    /// </summary>
    public class VideoContainerHeaderTests
    {
        /// <summary>Четыре байта размера атома, а дальше его имя — как в настоящем файле.</summary>
        private static byte[] HeaderWithBox(string boxType)
        {
            var header = new byte[VideoContainerHeader.RequiredBytes];

            // Размер атома: значение неважно, важно, что имя стоит с пятого байта.
            header[3] = 20;
            Encoding.ASCII.GetBytes(boxType).CopyTo(header, 4);

            return header;
        }

        [Theory]
        [InlineData("ftyp")]  // обычный mp4
        [InlineData("moov")]
        [InlineData("mdat")]
        [InlineData("free")]
        public void LooksLikeContainer_AcceptsKnownBoxes(string boxType) =>
            Assert.True(VideoContainerHeader.LooksLikeContainer(HeaderWithBox(boxType)));

        /// <summary>
        /// Клипы живых фото из Immich приходят как video/quicktime и начинаются
        /// с атома «wide» вместо «ftyp».
        /// </summary>
        [Fact]
        public void LooksLikeContainer_AcceptsAppleWideBox() =>
            Assert.True(VideoContainerHeader.LooksLikeContainer(HeaderWithBox("wide")));

        [Fact]
        public void LooksLikeContainer_RejectsErrorPageServedWithCode200() =>
            Assert.False(VideoContainerHeader.LooksLikeContainer(
                Encoding.ASCII.GetBytes("<!DOCTYPE html><html><head>")));

        [Fact]
        public void LooksLikeContainer_RejectsJsonError() =>
            Assert.False(VideoContainerHeader.LooksLikeContainer(
                Encoding.ASCII.GetBytes("{\"message\":\"Not found\"}")));

        [Fact]
        public void LooksLikeContainer_RejectsUnknownBox() =>
            Assert.False(VideoContainerHeader.LooksLikeContainer(HeaderWithBox("zzzz")));

        [Fact]
        public void LooksLikeContainer_RejectsTruncatedFile() =>
            Assert.False(VideoContainerHeader.LooksLikeContainer(new byte[] { 0, 0, 0 }));

        [Fact]
        public void LooksLikeContainer_RejectsBinaryGarbage() =>
            Assert.False(VideoContainerHeader.LooksLikeContainer(
                new byte[] { 0, 0, 0, 20, 0x01, 0x02, 0xFF, 0x00 }));
    }
}
