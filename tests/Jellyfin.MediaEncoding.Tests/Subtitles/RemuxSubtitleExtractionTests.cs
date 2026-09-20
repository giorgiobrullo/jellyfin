using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AutoFixture;
using AutoFixture.AutoMoq;
using MediaBrowser.Controller.IO;
using MediaBrowser.MediaEncoding.Subtitles;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Moq;
using Xunit;

namespace Jellyfin.MediaEncoding.Subtitles.Tests;

public sealed class RemuxSubtitleExtractionTests : IDisposable
{
    private const string MediaSourceId = "0123456789abcdef0123456789abcdef";

    private readonly string _cacheDirectory;
    private readonly SubtitleEncoder _encoder;

    public RemuxSubtitleExtractionTests()
    {
        _cacheDirectory = Path.Combine(Path.GetTempPath(), "jellyfin-remux-subs-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(_cacheDirectory);

        var fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });

        var pathManager = fixture.Freeze<Mock<IPathManager>>();
        pathManager
            .Setup(x => x.GetSubtitlePath(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .Returns((string _, int index, string extension) => Path.Combine(_cacheDirectory, index.ToString(CultureInfo.InvariantCulture) + extension));

        var fileSystem = fixture.Freeze<Mock<IFileSystem>>();
        fileSystem
            .Setup(x => x.GetFileInfo(It.IsAny<string>()))
            .Returns((string path) => new FileSystemMetadata { FullName = path, Exists = File.Exists(path), Length = File.Exists(path) ? new FileInfo(path).Length : 0 });

        _encoder = fixture.Create<SubtitleEncoder>();
    }

    public void Dispose()
    {
        _encoder.Dispose();
        if (Directory.Exists(_cacheDirectory))
        {
            Directory.Delete(_cacheDirectory, true);
        }
    }

    private static MediaSourceInfo CreateSource(params MediaStream[] subtitles)
    {
        var streams = new List<MediaStream>
        {
            new() { Index = 0, Type = MediaStreamType.Video, Codec = "h264" },
            new() { Index = 1, Type = MediaStreamType.Audio, Codec = "aac" }
        };
        streams.AddRange(subtitles);

        return new MediaSourceInfo
        {
            Id = MediaSourceId,
            Path = "https://example.invalid/video.mkv",
            Protocol = MediaProtocol.Http,
            IsRemote = true,
            MediaStreams = streams
        };
    }

    private static MediaStream Subtitle(int index, string codec, bool external = false)
        => new()
        {
            Index = index,
            Type = MediaStreamType.Subtitle,
            Codec = codec,
            IsExternal = external,
            SupportsExternalStream = true,
            Path = external ? "/media/video." + codec : null
        };

    [Fact]
    public void Plan_TakesOnlyInternalCopyableTextTracksNotYetCached()
    {
        File.WriteAllText(Path.Combine(_cacheDirectory, "3.srt"), "already here");
        var source = CreateSource(
            Subtitle(2, "ass"),
            Subtitle(3, "subrip"),           // cached
            Subtitle(4, "pgssub"),           // bitmap
            Subtitle(5, "mov_text"),         // needs a conversion
            Subtitle(6, "srt", external: true),
            Subtitle(7, "subrip"));

        var plan = _encoder.PlanRemuxExtraction(source);

        Assert.NotNull(plan);
        Assert.Equal(new[] { 2, 7 }, plan.Outputs.Select(o => o.StreamIndex));
        Assert.Equal(Path.Combine(_cacheDirectory, "2.ass"), plan.Outputs[0].FinalPath);
        Assert.Equal(Path.Combine(_cacheDirectory, "7.srt"), plan.Outputs[1].FinalPath);
        Assert.All(plan.Outputs, o => Assert.StartsWith(plan.TempDirectory, o.TempPath, StringComparison.Ordinal));

        // ffmpeg numbers the streams inside the container, so the external track (6) does not
        // count and Jellyfin's stream 7 is ffmpeg's 0:6.
        Assert.Contains($"-map 0:2 -an -vn -c:s copy -flush_packets 1 \"{plan.Outputs[0].TempPath}\"", plan.Arguments, StringComparison.Ordinal);
        Assert.Contains($"-map 0:6 -an -vn -c:s copy -flush_packets 1 \"{plan.Outputs[1].TempPath}\"", plan.Arguments, StringComparison.Ordinal);
        Assert.StartsWith(" ", plan.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_IsNullWhenNothingIsMissing()
    {
        File.WriteAllText(Path.Combine(_cacheDirectory, "2.ass"), "cached");

        Assert.Null(_encoder.PlanRemuxExtraction(CreateSource(Subtitle(2, "ass"))));
    }

    [Fact]
    public async Task Plan_IsNotHandedOutTwiceForTheSameSource()
    {
        var source = CreateSource(Subtitle(2, "ass"));

        var first = _encoder.PlanRemuxExtraction(source);
        Assert.NotNull(first);
        Assert.Null(_encoder.PlanRemuxExtraction(source));

        await _encoder.CompleteRemuxExtraction(first, false);

        Assert.NotNull(_encoder.PlanRemuxExtraction(source));
    }

    [Fact]
    public async Task Complete_MovesFinishedFilesIntoTheCache()
    {
        var plan = _encoder.PlanRemuxExtraction(CreateSource(Subtitle(2, "subrip"), Subtitle(3, "subrip")));
        Assert.NotNull(plan);
        await File.WriteAllTextAsync(plan.Outputs[0].TempPath, "1\n00:00:01,000 --> 00:00:02,000\nhello\n", TestContext.Current.CancellationToken);
        // The second track produced nothing: it must not reach the cache as an empty file.
        await File.WriteAllTextAsync(plan.Outputs[1].TempPath, string.Empty, TestContext.Current.CancellationToken);

        await _encoder.CompleteRemuxExtraction(plan, true);

        Assert.True(await plan.Completion.Task);
        Assert.True(File.Exists(plan.Outputs[0].FinalPath));
        Assert.False(File.Exists(plan.Outputs[1].FinalPath));
        Assert.False(Directory.Exists(plan.TempDirectory));
    }

    [Fact]
    public async Task Complete_DiscardsEverythingWhenTheRemuxWasCutShort()
    {
        var plan = _encoder.PlanRemuxExtraction(CreateSource(Subtitle(2, "subrip")));
        Assert.NotNull(plan);
        await File.WriteAllTextAsync(plan.Outputs[0].TempPath, "1\n00:00:01,000 --> 00:00:02,000\nonly the first minute\n", TestContext.Current.CancellationToken);

        await _encoder.CompleteRemuxExtraction(plan, false);

        Assert.False(await plan.Completion.Task);
        Assert.False(File.Exists(plan.Outputs[0].FinalPath));
        Assert.False(Directory.Exists(plan.TempDirectory));
    }

    [Fact]
    public async Task Complete_KeepsAFileSomeoneElseWroteMeanwhile()
    {
        var plan = _encoder.PlanRemuxExtraction(CreateSource(Subtitle(2, "subrip")));
        Assert.NotNull(plan);
        await File.WriteAllTextAsync(plan.Outputs[0].TempPath, "from the remux", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(plan.Outputs[0].FinalPath, "from the dedicated extraction", TestContext.Current.CancellationToken);

        await _encoder.CompleteRemuxExtraction(plan, true);

        Assert.Equal("from the dedicated extraction", await File.ReadAllTextAsync(plan.Outputs[0].FinalPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Plan_CanBeSwitchedOff()
    {
        Environment.SetEnvironmentVariable("JELLYFIN_REMUX_SUBTITLE_EXTRACTION", "false");
        try
        {
            Assert.Null(_encoder.PlanRemuxExtraction(CreateSource(Subtitle(2, "ass"))));
        }
        finally
        {
            Environment.SetEnvironmentVariable("JELLYFIN_REMUX_SUBTITLE_EXTRACTION", null);
        }
    }
}
