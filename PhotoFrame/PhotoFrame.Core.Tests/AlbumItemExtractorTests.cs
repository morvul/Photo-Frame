using PhotoFrame;
using Xunit;

namespace PhotoFrame.Tests
{
    /// <summary>
    /// Фрагменты в тестах повторяют вёрстку настоящей страницы альбома — на ней
    /// и подобраны признаки видео.
    /// </summary>
    public class AlbumItemExtractorTests
    {
        private const string PhotoEntry =
            """["AF1QipPhotoOne",["https://lh3.googleusercontent.com/pw/AaBbCcPhotoOne",2268,4032,null,null,null,null,null,[null,null,1],[1380882],2,[[1,1]]],1733946172385,"AF1QipX",10800000,1786557443876,["AF1QipY"],[[2],[31,0,1]],2,{"15":21833,"101428965":[0,"vdjljqm5"],"525000002":[["AF1QipZ"]]}]""";

        private const string VideoEntry =
            """["AF1QipVideoOne",["https://lh3.googleusercontent.com/pw/AaBbCcVideoOne",1920,1080,null,null,null,null,null,[null,null,1],[2961973]],1738494070000,"AF1QipX",10800000,1786557443876,["AF1QipY"],[[2],[31,0,1]],2,{"15":21833,"76647426":[91360,null,1920,1080,null,4,134,null,[[1,null,1920,1080]],0,null,null,null,["https://lh3.googleusercontent.com/pw/AaBbCcPoster"]],"101428965":[0,"vdjpujvv"],"525000002":[["AF1QipZ"]]}]""";

        private const string MotionPhotoEntry =
            """["AF1QipMotionOne",["https://lh3.googleusercontent.com/pw/AaBbCcMotionOne",4032,2268,null,null,null,null,null,[null,null,1],[5254432],2,[[1,1]]],1749645800674,"AF1QipX",10800000,1786557443876,["AF1QipY"],[[2],[31,0,1]],2,{"15":21833,"101428965":[0,"vdk4iubh"],"146008172":[null,2197],"525000002":[["AF1QipZ"]]}]""";

        private static string Page(params string[] entries) =>
            "AF_initDataCallback({key: 'ds:1', data:[[[" + string.Join(",", entries) + "]]]});";

        [Fact]
        public void ReadsIdAndPosterOfEveryItem()
        {
            List<AlbumItem> items = AlbumItemExtractor.Extract(Page(PhotoEntry, VideoEntry));

            Assert.Equal(2, items.Count);
            Assert.Equal("AF1QipPhotoOne", items[0].ItemId);
            Assert.Equal("https://lh3.googleusercontent.com/pw/AaBbCcPhotoOne", items[0].PosterUrl);
        }

        [Fact]
        public void MarksAnItemWithTheVideoFieldAsVideo()
        {
            List<AlbumItem> items = AlbumItemExtractor.Extract(Page(PhotoEntry, VideoEntry));

            Assert.False(items[0].IsVideo);
            Assert.True(items[1].IsVideo);
            Assert.Equal(91360, items[1].VideoDurationMilliseconds);
        }

        [Fact]
        public void TreatsAMotionPhotoAsAPhotoAndKeepsItsClipLength()
        {
            // «Живое фото» — снимок с секундным клипом: видео это не делает, но
            // длительность нужна, чтобы кадр можно было оживить.
            List<AlbumItem> items = AlbumItemExtractor.Extract(Page(MotionPhotoEntry));

            Assert.Single(items);
            Assert.False(items[0].IsVideo);
            Assert.Equal(0, items[0].VideoDurationMilliseconds);
            Assert.True(items[0].IsMotionPhoto);
            Assert.Equal(2197, items[0].MotionDurationMilliseconds);
        }

        [Fact]
        public void APlainPhotoIsNeitherVideoNorMotion()
        {
            List<AlbumItem> items = AlbumItemExtractor.Extract(Page(PhotoEntry));

            Assert.False(items[0].IsVideo);
            Assert.False(items[0].IsMotionPhoto);
        }

        [Fact]
        public void AVideoIsNotCountedAsMotion()
        {
            // У видео поля живого фото нет, но проверка защищает от путаницы полей.
            List<AlbumItem> items = AlbumItemExtractor.Extract(Page(VideoEntry));

            Assert.True(items[0].IsVideo);
            Assert.False(items[0].IsMotionPhoto);
        }

        [Fact]
        public void KeepsTheOrderOfThePage()
        {
            List<AlbumItem> items = AlbumItemExtractor.Extract(Page(VideoEntry, PhotoEntry, MotionPhotoEntry));

            Assert.Equal(
                new[] { "AF1QipVideoOne", "AF1QipPhotoOne", "AF1QipMotionOne" },
                items.Select(item => item.ItemId));
        }

        [Fact]
        public void SkipsRepeatedItems()
        {
            // Один и тот же кадр встречается в данных страницы не раз.
            List<AlbumItem> items = AlbumItemExtractor.Extract(Page(PhotoEntry, PhotoEntry));

            Assert.Single(items);
        }

        [Fact]
        public void DoesNotLeakTheVideoFieldToTheNextItem()
        {
            // Признак ищется в границах записи: иначе видео «заражало» бы соседний снимок.
            List<AlbumItem> items = AlbumItemExtractor.Extract(Page(VideoEntry, PhotoEntry));

            Assert.True(items[0].IsVideo);
            Assert.False(items[1].IsVideo);
        }

        [Fact]
        public void IgnoresImagesOutsideItemEntries()
        {
            // Аватары участников и обложка живут на тех же хостах, но записями не являются.
            string page = """<img src="https://lh3.googleusercontent.com/a/AvatarToken">"""
                          + Page(PhotoEntry);

            List<AlbumItem> items = AlbumItemExtractor.Extract(page);

            Assert.Single(items);
            Assert.Equal("AF1QipPhotoOne", items[0].ItemId);
        }

        [Theory]
        [InlineData("")]
        [InlineData("<html><body>Nothing here</body></html>")]
        public void GivesNothingWhenThereAreNoItems(string page)
        {
            Assert.Empty(AlbumItemExtractor.Extract(page));
        }
    }
}
