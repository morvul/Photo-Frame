using PhotoFrame;
using Xunit;

namespace PhotoFrame.Tests
{
    /// <summary>
    /// Разбор страницы расшаренного альбома — самая хрупкая часть проекта: это вёрстка,
    /// а не документированный API. Фрагменты ниже повторяют то, что реально встречается
    /// на странице: ссылки на снимки, аватары авторов, дубль обложки и размеры кадра.
    /// </summary>
    public class AlbumPhotoUrlExtractorTests
    {
        private const string PhotoUrlOne =
            "https://lh3.googleusercontent.com/pw/AP1GczOSrJq5gE5jPSWQBK-YK7klJ4HmCNpDI3PaIRYx";

        private const string PhotoUrlTwo =
            "https://lh3.googleusercontent.com/pw/AP1GczOmUPWJWopXSO7QZ5ui3pGTmkJSx8M1WnEya_86";

        [Fact]
        public void Extract_FindsPhotoUrls()
        {
            string html = $"[[\"{PhotoUrlOne}\",4032,2268],[\"{PhotoUrlTwo}\",2268,4032]]";

            List<string> urls = AlbumPhotoUrlExtractor.Extract(html);

            Assert.Equal(new[] { PhotoUrlOne, PhotoUrlTwo }, urls);
        }

        [Fact]
        public void Extract_SkipsProfilePictures()
        {
            // Аватары авторов лежат на /a/, а не /pw/, и в слайд-шоу попадать не должны.
            string html =
                "https://lh3.googleusercontent.com/a/ACg8ocJloaOY7bWWY7TOwUQpLYsU7hTHF2kw=s20-p-no "
                + PhotoUrlOne;

            List<string> urls = AlbumPhotoUrlExtractor.Extract(html);

            Assert.Equal(new[] { PhotoUrlOne }, urls);
        }

        [Fact]
        public void Extract_RemovesDuplicatesKeepingFirstOrder()
        {
            // Обложка альбома дублирует один из снимков: без устранения повторов
            // один кадр показывался бы дважды.
            string html = $"{PhotoUrlTwo} {PhotoUrlOne} {PhotoUrlTwo}";

            List<string> urls = AlbumPhotoUrlExtractor.Extract(html);

            Assert.Equal(new[] { PhotoUrlTwo, PhotoUrlOne }, urls);
        }

        [Theory]
        [InlineData("lh3")]
        [InlineData("lh4")]
        [InlineData("lh5")]
        [InlineData("lh6")]
        public void Extract_AcceptsAnyContentHost(string host)
        {
            // Google отдаёт кадры с разных хостов lh3..lh6.
            string url = $"https://{host}.googleusercontent.com/pw/AP1GczTokenValue_-123";

            Assert.Equal(new[] { url }, AlbumPhotoUrlExtractor.Extract(url));
        }

        [Fact]
        public void Extract_ReturnsEmptyWhenMarkupHasNoPhotos()
        {
            // Пустой результат — сигнал, что вёрстка поменялась. Вызывающий код обязан
            // трактовать его как ошибку, а не как «альбом пуст».
            Assert.Empty(AlbumPhotoUrlExtractor.Extract("<html><body>no photos here</body></html>"));
        }

        [Fact]
        public void Extract_StopsTokenAtQuoteAndDoesNotSwallowMarkup()
        {
            string html = $"[\"{PhotoUrlOne}\",\"other\"]";

            List<string> urls = AlbumPhotoUrlExtractor.Extract(html);

            Assert.Equal(new[] { PhotoUrlOne }, urls);
        }

        [Fact]
        public void WithSize_AppendsFrameResolution()
        {
            Assert.Equal(
                PhotoUrlOne + "=w1280-h800",
                AlbumPhotoUrlExtractor.WithSize(PhotoUrlOne, 1280, 800));
        }
    }
}
