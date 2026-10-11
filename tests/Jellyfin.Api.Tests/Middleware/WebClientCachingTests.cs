using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Api.Middleware.Tests
{
    public static class WebClientCachingTests
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
        [InlineData("chunk.03EDF3BB7EE048EE10BE.js", false)]
        public static void IsContentHashedWebFile_MatchesContentHashes(string fileName, bool expected)
        {
            Assert.Equal(expected, WebClientCaching.IsContentHashedWebFile(fileName));
        }

        [Theory]
        [InlineData("/web/1133.03edf3bb7ee048ee10be.css", 200, null, "public, max-age=31536000, immutable")]
        [InlineData("/web/1133.03edf3bb7ee048ee10be.css", 304, null, "public, max-age=31536000, immutable")]
        [InlineData("/web/1133.03edf3bb7ee048ee10be.css", 404, null, "")]
        [InlineData("/web/1133.03edf3bb7ee048ee10be.css", 200, "no-cache", "no-cache")]
        [InlineData("/web/index.html", 200, null, "")]
        [InlineData("/Items/03edf3bb7ee048ee10be03edf3bb7ee0/Images/x.03edf3bb7ee048ee10be.jpg", 200, null, "")]
        public static void Apply_SetsImmutableOnlyForHashedWebFiles(string path, int status, string? existing, string expected)
        {
            var context = new DefaultHttpContext();
            context.Request.Path = path;
            context.Response.StatusCode = status;
            if (existing is not null)
            {
                context.Response.Headers.CacheControl = existing;
            }

            WebClientCaching.Apply(context);

            Assert.Equal(expected, context.Response.Headers.CacheControl.ToString());
        }
    }
}
