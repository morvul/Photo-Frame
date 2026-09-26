using PhotoFrame;
using Xunit;

namespace PhotoFrame.Core.Tests
{
    /// <summary>
    /// Разбор /manifest.json, которым отличают настоящую Home Assistant от чего угодно
    /// ещё, что может слушать тот же порт в домашней сети.
    /// </summary>
    public class HomeAssistantDiscoveryTests
    {
        [Theory]
        [InlineData("{\"name\":\"Home Assistant\",\"short_name\":\"Home Assistant\"}", true)]
        [InlineData("{\"name\":\"Some Other App\"}", false)]
        [InlineData("<html>Not HA</html>", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsHomeAssistantManifest_RecognizesTheFrontendManifest(string? body, bool expected) =>
            Assert.Equal(expected, HomeAssistantDiscovery.IsHomeAssistantManifest(body));
    }
}
