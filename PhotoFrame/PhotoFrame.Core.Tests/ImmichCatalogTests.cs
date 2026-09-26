using System;
using System.Collections.Generic;
using PhotoFrame;
using Xunit;

namespace PhotoFrame.Core.Tests
{
    /// <summary>
    /// Разбор ответов Immich и сборка адресов.
    /// </summary>
    /// <remarks>
    /// Образцы JSON здесь урезаны до используемых полей: у настоящего ответа их
    /// несколько десятков, и полная копия только скрывала бы, что именно проверяется.
    /// Лишние поля добавлены там, где важно, что разбор их игнорирует.
    /// </remarks>
    public class ImmichCatalogTests
    {
        [Theory]
        [InlineData("192.168.1.10:2283", "http://192.168.1.10:2283")]
        [InlineData("http://immich.home/", "http://immich.home")]
        [InlineData("https://immich.example.com/api", "https://immich.example.com")]
        [InlineData("  immich.home  ", "http://immich.home")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void NormalizeServerUrl_ForgivesCommonTypos(string? enteredUrl, string expectedUrl) =>
            Assert.Equal(expectedUrl, ImmichCatalog.NormalizeServerUrl(enteredUrl));

        [Fact]
        public void BuildPreviewUrl_AsksServerForScreenSizedJpeg() =>
            Assert.Equal(
                "http://immich.home/api/assets/abc/thumbnail?size=preview",
                ImmichCatalog.BuildPreviewUrl("immich.home/", "abc"));

        [Fact]
        public void BuildPingUrl_UsesServerRoute() =>
            Assert.Equal(
                "http://immich.home/api/server/ping", ImmichCatalog.BuildPingUrl("immich.home"));

        [Fact]
        public void BuildLegacyPingUrl_UsesOlderRoute() =>
            Assert.Equal(
                "http://immich.home/api/server-info/ping",
                ImmichCatalog.BuildLegacyPingUrl("immich.home"));

        [Theory]
        [InlineData("{\"res\":\"pong\"}", true)]
        [InlineData("{\"res\":\"PONG\"}", true)]
        [InlineData("{\"res\":\"ping\"}", false)]
        [InlineData("<html>Not Immich</html>", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsPingResponse_RecognizesImmichsPong(string? body, bool expected) =>
            Assert.Equal(expected, ImmichCatalog.IsPingResponse(body));

        [Fact]
        public void ParseAlbums_ReadsNameAndCount()
        {
            const string albumsJson = """
                [
                  {"id":"1a","albumName":"Отпуск","assetCount":42,"shared":false},
                  {"id":"2b","albumName":"Дом","assetCount":7}
                ]
                """;

            List<ImmichAlbum> albums = ImmichCatalog.ParseAlbums(albumsJson);

            Assert.Equal(2, albums.Count);
            Assert.Equal(new ImmichAlbum("1a", "Отпуск", 42), albums[0]);
            Assert.Equal(new ImmichAlbum("2b", "Дом", 7), albums[1]);
        }

        [Fact]
        public void ParseSearchAssets_KeepsServerOrderAndMarksVideos()
        {
            const string searchJson = """
                {"assets":{"items":[
                  {"id":"one","type":"IMAGE","originalFileName":"IMG_0001.HEIC"},
                  {"id":"two","type":"VIDEO","originalFileName":"IMG_0002.MOV"}
                ]}}
                """;

            List<ImmichAsset> assets = ImmichCatalog.ParseSearchAssets(searchJson, out _);

            Assert.Equal(2, assets.Count);
            Assert.Equal(new ImmichAsset("one", "IMG_0001.HEIC", IsVideo: false), assets[0]);
            Assert.True(assets[1].IsVideo);
        }

        [Fact]
        public void ParseSearchAssets_SkipsWhatServerAlreadyTrashed()
        {
            const string searchJson = """
                {"assets":{"items":[
                  {"id":"kept","type":"IMAGE","isTrashed":false},
                  {"id":"gone","type":"IMAGE","isTrashed":true}
                ]}}
                """;

            ImmichAsset onlyAsset = Assert.Single(ImmichCatalog.ParseSearchAssets(searchJson, out _));
            Assert.Equal("kept", onlyAsset.Id);
        }

        [Fact]
        public void ParseSearchAssets_SkipsEntriesWithoutIdentifier()
        {
            const string searchJson =
                """{"assets":{"items":[{"type":"IMAGE"},{"id":"real","type":"IMAGE"}]}}""";

            Assert.Equal("real", Assert.Single(ImmichCatalog.ParseSearchAssets(searchJson, out _)).Id);
        }

        [Theory]
        [InlineData("00:00:12.345000", 12345)]
        [InlineData("0:00:03.50000", 3500)]
        [InlineData("00:01:30.000000", 90000)]

        // У снимка поле нулевое, пустое либо отсутствует — длительности нет.
        [InlineData("0:00:00.00000", 0)]
        [InlineData("", 0)]
        [InlineData(null, 0)]
        [InlineData("непонятно", 0)]
        public void ParseDurationMilliseconds_ReadsServerFormat(string? durationText, int expected) =>
            Assert.Equal(expected, ImmichCatalog.ParseDurationMilliseconds(durationText));

        [Fact]
        public void ParseSearchAssets_ReadsClipLengthAndLivePhotoLink()
        {
            const string searchJson = """
                {"assets":{"items":[
                  {"id":"clip","type":"VIDEO","duration":"00:00:07.500000"},
                  {"id":"live","type":"IMAGE","livePhotoVideoId":"live-clip"},
                  {"id":"still","type":"IMAGE"}
                ]}}
                """;

            List<ImmichAsset> assets = ImmichCatalog.ParseSearchAssets(searchJson, out _);

            Assert.Equal(7500, assets[0].DurationMilliseconds);
            Assert.True(assets[1].IsMotionPhoto);
            Assert.Equal("live-clip", assets[1].LivePhotoVideoId);
            Assert.False(assets[2].IsMotionPhoto);
        }

        /// <summary>
        /// Рамка показывает превью с сервера, а EXIF из него вырезан: съёмочные поля
        /// берутся только отсюда.
        /// </summary>
        [Fact]
        public void ParseSearchAssets_ReadsCameraAndDateFromExifInfo()
        {
            const string searchJson = """
                {"assets":{"items":[{"id":"one","type":"IMAGE","exifInfo":{
                  "make":"NIKON CORPORATION","model":"NIKON D60",
                  "dateTimeOriginal":"2024-07-14T09:30:00.000Z"}}]}}
                """;

            ImmichAsset asset = Assert.Single(ImmichCatalog.ParseSearchAssets(searchJson, out _));

            Assert.Equal("NIKON D60", asset.CameraName);
            Assert.Equal(new DateTime(2024, 7, 14, 9, 30, 0, DateTimeKind.Utc), asset.TakenAt!.Value.ToUniversalTime());
        }

        [Fact]
        public void ParseSearchAssets_SurvivesAssetWithoutExifInfo()
        {
            const string searchJson = """{"assets":{"items":[{"id":"one","type":"IMAGE"}]}}""";

            ImmichAsset asset = Assert.Single(ImmichCatalog.ParseSearchAssets(searchJson, out _));

            Assert.Equal(string.Empty, asset.CameraName);
            Assert.Null(asset.TakenAt);
        }

        [Fact]
        public void BuildVideoUrl_AsksForTheTranscodedCopy() =>
            Assert.Equal(
                "http://immich.home/api/assets/abc/video/playback",
                ImmichCatalog.BuildVideoUrl("immich.home", "abc"));

        [Fact]
        public void ParseSearchAssets_ReportsNextPageGivenAsString()
        {
            const string searchJson = """
                {"assets":{"total":2,"count":1,"items":[{"id":"one","type":"IMAGE"}],"nextPage":"2"}}
                """;

            List<ImmichAsset> assets = ImmichCatalog.ParseSearchAssets(searchJson, out int nextPage);

            Assert.Equal("one", Assert.Single(assets).Id);
            Assert.Equal(2, nextPage);
        }

        [Fact]
        public void ParseSearchAssets_TreatsNullNextPageAsLastPage()
        {
            const string searchJson = """{"assets":{"items":[],"nextPage":null}}""";

            Assert.Empty(ImmichCatalog.ParseSearchAssets(searchJson, out int nextPage));
            Assert.Equal(0, nextPage);
        }

        [Theory]
        [InlineData("")]
        [InlineData("<html>Sign in</html>")]
        [InlineData("{\"error\":\"unauthorized\"}")]
        public void ParseSearchAssets_ReturnsNothingForAnswerThatIsNotAListing(string body)
        {
            Assert.Empty(ImmichCatalog.ParseSearchAssets(body, out int nextPage));
            Assert.Equal(0, nextPage);
            Assert.Empty(ImmichCatalog.ParseAlbums(body));
        }

        /// <summary>
        /// Ответ /api/albums/{id} содержит сведения об альбоме и не содержит списка
        /// снимков — потому содержимое и берётся поиском.
        /// </summary>
        [Fact]
        public void ParseSearchAssets_ReturnsNothingForAlbumInfoWithoutAssets()
        {
            const string albumInfoJson =
                """{"albumName":"Camera","id":"0de6829f","albumUsers":[],"assetCount":128}""";

            Assert.Empty(ImmichCatalog.ParseSearchAssets(albumInfoJson, out _));
        }

        [Fact]
        public void BuildSearchRequestBody_WithoutAlbumSearchesWholeLibrary() =>
            Assert.Equal(
                "{\"page\":3,\"size\":1000,\"withDeleted\":false,\"withExif\":true}",
                ImmichCatalog.BuildSearchRequestBody(3, 1000));

        [Fact]
        public void BuildSearchRequestBody_WithAlbumFiltersByIt() =>
            Assert.Equal(
                "{\"page\":1,\"size\":1000,\"withDeleted\":false,\"withExif\":true,\"albumIds\":[\"0de6829f\"]}",
                ImmichCatalog.BuildSearchRequestBody(1, 1000, new[] { "0de6829f" }));

        /// <summary>
        /// Несколько альбомов уходят одним запросом: обходить их по очереди значило бы
        /// ещё и вычищать снимки, попавшие сразу в два.
        /// </summary>
        [Fact]
        public void BuildSearchRequestBody_WithSeveralAlbumsListsThemAll() =>
            Assert.Equal(
                "{\"page\":2,\"size\":500,\"withDeleted\":false,\"withExif\":true,\"albumIds\":[\"one\",\"two\"]}",
                ImmichCatalog.BuildSearchRequestBody(2, 500, new[] { "one", "two" }));

        [Fact]
        public void BuildSearchRequestBody_WithEmptyAlbumListSearchesWholeLibrary() =>
            Assert.Equal(
                "{\"page\":1,\"size\":1000,\"withDeleted\":false,\"withExif\":true}",
                ImmichCatalog.BuildSearchRequestBody(1, 1000, Array.Empty<string>()));
    }
}
