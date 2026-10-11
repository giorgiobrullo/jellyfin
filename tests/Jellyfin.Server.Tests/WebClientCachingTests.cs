using Xunit;

namespace Jellyfin.Server.Tests
{
    public class WebClientCachingTests
    {
        [Theory]
        [InlineData("1133.03edf3bb7ee048ee10be.css", true)]
        [InlineData("itemDetails.02f43c131afb593adb5d.chunk.js", true)]
        [InlineData("node_modules.markdown-it.577ee674061f4f85a169.chunk.js", true)]
        [InlineData("itemDetails-index-html.fc20dbd21fff214b6611.chunk.js", false)]
        [InlineData("home-html.0eb29b9d06280bb84dc5.chunk.js", false)]
        [InlineData("main.jellyfin.bundle.js", false)]
        [InlineData("index.html", false)]
        [InlineData("config.json", false)]
        [InlineData("serviceworker.js", false)]
        [InlineData("chunk.03EDF3BB7EE048EE10BE.js", false)]
        public void IsContentHashedWebFile_MatchesContentHashes(string fileName, bool expected)
        {
            Assert.Equal(expected, Startup.IsContentHashedWebFile(fileName));
        }
    }
}
