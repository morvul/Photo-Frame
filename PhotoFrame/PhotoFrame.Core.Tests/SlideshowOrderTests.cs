using PhotoFrame;
using Xunit;

namespace PhotoFrame.Tests
{
    public class SlideshowOrderTests
    {
        [Fact]
        public void RestoresOrderAndPositionOfTheLastShownFrame()
        {
            // Найденные кадры приходят в порядке источника, а показывались в другом.
            var currentPaths = new List<string> { "/a.jpg", "/b.jpg", "/c.jpg" };
            var savedOrder = new[] { "/c.jpg", "/a.jpg", "/b.jpg" };

            int index = SlideshowOrder.TryApplySavedOrder(currentPaths, savedOrder, "/a.jpg");

            Assert.Equal(savedOrder, currentPaths);
            Assert.Equal(1, index);
        }

        [Fact]
        public void StartsFromTheBeginningWhenTheLastFrameIsUnknown()
        {
            var currentPaths = new List<string> { "/a.jpg", "/b.jpg" };

            int index = SlideshowOrder.TryApplySavedOrder(
                currentPaths, new[] { "/b.jpg", "/a.jpg" }, lastShownPath: null);

            Assert.Equal(new[] { "/b.jpg", "/a.jpg" }, currentPaths);
            Assert.Equal(0, index);
        }

        [Fact]
        public void StartsFromTheBeginningWhenTheLastFrameIsGone()
        {
            // Кадр могли убрать в корзину с другого запуска.
            var currentPaths = new List<string> { "/a.jpg", "/b.jpg" };

            int index = SlideshowOrder.TryApplySavedOrder(
                currentPaths, new[] { "/b.jpg", "/a.jpg" }, "/removed.jpg");

            Assert.Equal(0, index);
        }

        [Fact]
        public void RejectsOrderWhenAFrameWasAdded()
        {
            var currentPaths = new List<string> { "/a.jpg", "/b.jpg", "/new.jpg" };
            var untouched = new List<string>(currentPaths);

            int index = SlideshowOrder.TryApplySavedOrder(
                currentPaths, new[] { "/b.jpg", "/a.jpg" }, "/a.jpg");

            Assert.Equal(-1, index);
            Assert.Equal(untouched, currentPaths);
        }

        [Fact]
        public void RejectsOrderWhenAFrameWasRemoved()
        {
            var currentPaths = new List<string> { "/a.jpg" };

            int index = SlideshowOrder.TryApplySavedOrder(
                currentPaths, new[] { "/a.jpg", "/b.jpg" }, "/a.jpg");

            Assert.Equal(-1, index);
        }

        [Fact]
        public void RejectsOrderListingAnUnknownFrame()
        {
            // Совпадение по количеству ещё не значит совпадение по составу.
            var currentPaths = new List<string> { "/a.jpg", "/b.jpg" };

            int index = SlideshowOrder.TryApplySavedOrder(
                currentPaths, new[] { "/a.jpg", "/elsewhere.jpg" }, "/a.jpg");

            Assert.Equal(-1, index);
        }

        [Fact]
        public void RejectsOrderWithRepeatedFrames()
        {
            var currentPaths = new List<string> { "/a.jpg", "/b.jpg" };

            int index = SlideshowOrder.TryApplySavedOrder(
                currentPaths, new[] { "/a.jpg", "/a.jpg" }, "/a.jpg");

            Assert.Equal(-1, index);
        }

        [Fact]
        public void RejectsMissingOrder()
        {
            var currentPaths = new List<string> { "/a.jpg" };

            Assert.Equal(-1, SlideshowOrder.TryApplySavedOrder(currentPaths, null, "/a.jpg"));
        }

        [Fact]
        public void MatchesPathsIgnoringLetterCase()
        {
            // Пути приходят и из манифеста, и из обхода папок; регистр может разойтись.
            var currentPaths = new List<string> { "/DCIM/A.JPG", "/DCIM/b.jpg" };

            int index = SlideshowOrder.TryApplySavedOrder(
                currentPaths, new[] { "/dcim/b.jpg", "/dcim/a.jpg" }, "/DCIM/A.JPG");

            Assert.Equal(1, index);
        }

        [Fact]
        public void EmptyOrderForAnEmptyLibraryIsAccepted()
        {
            var currentPaths = new List<string>();

            Assert.Equal(0, SlideshowOrder.TryApplySavedOrder(
                currentPaths, Array.Empty<string>(), lastShownPath: null));
        }
    }
}
