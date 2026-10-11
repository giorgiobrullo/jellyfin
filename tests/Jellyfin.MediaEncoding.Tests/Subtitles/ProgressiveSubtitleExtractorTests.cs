using MediaBrowser.MediaEncoding.Subtitles;
using Xunit;

namespace Jellyfin.MediaEncoding.Subtitles.Tests
{
    public class ProgressiveSubtitleExtractorTests
    {
        [Theory]
        [InlineData("Dialogue: 0,0:10:00.83,0:10:02.37,Default,,0,0,0,,Hello", 600.83)]
        [InlineData("Dialogue: 10,1:02:03.50,1:02:04.00,Sign,,0,0,0,,{\\pos(10,20)}Text, with commas", 3723.5)]
        [InlineData("Comment: 0,0:00:00.00,0:00:05.00,Default,,0,0,0,,note", 0)]
        public void TryParseEventStart_ReadsTheStartField(string line, double expected)
        {
            Assert.True(ProgressiveSubtitleExtractor.TryParseEventStart(line, out var seconds));
            Assert.Equal(expected, seconds, 3);
        }

        [Theory]
        [InlineData("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text")]
        [InlineData("Dialogue: 0")]
        [InlineData("not an event")]
        public void TryParseEventStart_RejectsLinesWithoutATime(string line)
        {
            Assert.False(ProgressiveSubtitleExtractor.TryParseEventStart(line, out _));
        }

        [Fact]
        public void BuildArguments_SeeksBeforeTheInputAndWritesEventsAsTheyCome()
        {
            var args = ProgressiveSubtitleExtractor.BuildArguments("\"http://host/file.mkv\"", 3, 600.5);

            Assert.Equal(
                "-nostdin -hide_banner -loglevel error -analyzeduration 0 -probesize 1000000 -ss 600.500 -i \"http://host/file.mkv\" -map 0:3 -an -vn -c:s copy -copyts -flush_packets 1 -f ass -ignore_readorder 1 pipe:1",
                args);
        }

        [Theory]
        [InlineData("/stub", MediaBrowser.Model.MediaInfo.MediaProtocol.File, false)]
        [InlineData("", MediaBrowser.Model.MediaInfo.MediaProtocol.Http, false)]
        [InlineData("http://stremthru:8080/stremio/store/x", MediaBrowser.Model.MediaInfo.MediaProtocol.Http, true)]
        public void IsReadable_RejectsPlaceholders(string path, MediaBrowser.Model.MediaInfo.MediaProtocol protocol, bool expected)
        {
            var source = new MediaBrowser.Model.Dto.MediaSourceInfo { Path = path, Protocol = protocol };
            Assert.Equal(expected, ProgressiveSubtitleExtractor.IsReadable(source));
        }

        [Fact]
        public void BuildArguments_DoesNotSeekFromTheStart()
        {
            var args = ProgressiveSubtitleExtractor.BuildArguments("\"file.mkv\"", 2, 0);

            Assert.StartsWith("-nostdin -hide_banner -loglevel error -analyzeduration 0 -probesize 1000000 -i \"file.mkv\" -map 0:2", args, System.StringComparison.Ordinal);
        }
    }
}
