using PhotoFrame;
using Xunit;

namespace PhotoFrame.Tests
{
    /// <summary>
    /// Обход папок проверяется на настоящем дереве во временном каталоге: подделывать
    /// файловую систему тут дороже, чем создать десяток пустых файлов.
    /// </summary>
    public class MediaFileScannerTests : IDisposable
    {
        private readonly string _root;

        public MediaFileScannerTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "photoframe-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Каталог во временной папке; если не удалился, его уберёт система.
            }
        }

        private void CreateFile(params string[] relativeParts)
        {
            string fullPath = Path.Combine(new[] { _root }.Concat(relativeParts).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, "x");
        }

        private string[] Scan(bool recurse) =>
            MediaFileScanner.EnumerateMediaFiles(_root, recurse)
                .Select(path => Path.GetRelativePath(_root, path).Replace('\\', '/'))
                .ToArray();

        [Fact]
        public void SkipsThumbnailCacheDirectories()
        {
            // Ровно тот случай, который наполнял слайд-шоу размытыми превью: у Frameo
            // 151 миниатюра в cache/galleryThumbnails против 66 снимков в media.
            CreateFile("media", "photo.jpg");
            CreateFile("cache", "galleryThumbnails", "thumb.jpeg");
            CreateFile("videoThumbnails", "thumb.jpg");

            Assert.Equal(new[] { "media/photo.jpg" }, Scan(recurse: true));
        }

        [Fact]
        public void SkipsHiddenDirectories()
        {
            CreateFile("Camera", "photo.jpg");
            CreateFile(".thumbnails", "hidden.jpg");

            Assert.Equal(new[] { "Camera/photo.jpg" }, Scan(recurse: true));
        }

        [Fact]
        public void SkipsTrashDirectory()
        {
            // Убранный кнопкой «Убрать» кадр не должен вернуться в показ, даже если
            // корзина оказалась внутри выбранной папки.
            CreateFile("Camera", "photo.jpg");
            CreateFile(MediaFileScanner.TrashDirectoryName, "removed.jpg");
            CreateFile(MediaFileScanner.TrashDirectoryName, "Google Photos", "removed.jpg");

            Assert.Equal(new[] { "Camera/photo.jpg" }, Scan(recurse: true));
        }

        [Fact]
        public void IgnoresUnsupportedExtensions()
        {
            CreateFile("photo.jpg");
            CreateFile("notes.txt");
            CreateFile("archive.zip");
            CreateFile("raw.dng");

            Assert.Equal(new[] { "photo.jpg" }, Scan(recurse: true));
        }

        [Fact]
        public void IncludesVideoAlongsideImages()
        {
            CreateFile("clip.mp4");
            CreateFile("photo.jpg");

            Assert.Equal(new[] { "clip.mp4", "photo.jpg" }, Scan(recurse: true));
        }

        [Fact]
        public void WithoutRecurseReturnsOnlyTopLevel()
        {
            CreateFile("top.jpg");
            CreateFile("nested", "deep.jpg");

            Assert.Equal(new[] { "top.jpg" }, Scan(recurse: false));
        }

        [Fact]
        public void OrderIsStableAlphabetically()
        {
            // Порядок должен быть предсказуемым: на нём держится и порядок показа,
            // и совпадение счётчиков в экране выбора папок.
            CreateFile("b.jpg");
            CreateFile("a.jpg");
            CreateFile("Sub", "d.jpg");
            CreateFile("Sub", "c.jpg");

            Assert.Equal(new[] { "a.jpg", "b.jpg", "Sub/c.jpg", "Sub/d.jpg" }, Scan(recurse: true));
        }

        [Fact]
        public void MissingDirectoryYieldsNothing()
        {
            Assert.Empty(MediaFileScanner.EnumerateMediaFiles(
                Path.Combine(_root, "does-not-exist"), recurse: true));
        }

        [Theory]
        [InlineData("cache", true)]
        [InlineData("Cache", true)]
        [InlineData("thumbnails", true)]
        [InlineData("galleryThumbnails", true)]
        [InlineData(".hidden", true)]
        [InlineData("tmp", true)]
        [InlineData("Trash", true)]
        [InlineData("trash", true)]
        [InlineData("Camera", false)]
        [InlineData("media", false)]
        [InlineData("Cached memories", false)]
        public void ShouldSkipDirectory_MatchesWholeNamesAndThumbnailSubstring(
            string folderName, bool expectedSkip)
        {
            string path = Path.Combine(_root, folderName);

            Assert.Equal(expectedSkip, MediaFileScanner.ShouldSkipDirectory(path));
        }
    }
}
