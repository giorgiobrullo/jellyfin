using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AutoFixture;
using AutoFixture.AutoMoq;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Session;
using MediaBrowser.MediaEncoding.Subtitles;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.MediaEncoding.Subtitles.Tests;

public sealed class TextTrackPassTests : IDisposable
{
    private const string MediaSourceId = "0123456789abcdef0123456789abcdef";

    private readonly string _cacheDirectory;
    private readonly Mock<ISessionManager> _sessionManager;
    private readonly SubtitleEncoder _encoder;

    public TextTrackPassTests()
    {
        _cacheDirectory = Path.Combine(Path.GetTempPath(), "jellyfin-text-pass-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(_cacheDirectory);

        var fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });

        var pathManager = fixture.Freeze<Mock<IPathManager>>();
        pathManager
            .Setup(x => x.GetSubtitlePath(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .Returns((string _, int index, string extension) => Path.Combine(_cacheDirectory, index.ToString(CultureInfo.InvariantCulture) + extension));

        fixture.Freeze<Mock<IFileSystem>>();
        _sessionManager = fixture.Freeze<Mock<ISessionManager>>();
        fixture.Inject(new Lazy<ISessionManager>(() => _sessionManager.Object));

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
            RunTimeTicks = TimeSpan.FromMinutes(24).Ticks,
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

    private SessionInfo Session(string? mediaSourceId, bool playing = true)
        => new(_sessionManager.Object, NullLogger.Instance)
        {
            NowPlayingItem = playing ? new BaseItemDto { Id = Guid.NewGuid() } : null,
            PlayState = new PlayerStateInfo { MediaSourceId = mediaSourceId }
        };

    [Fact]
    public void Tracks_AreTheEmbeddedTextTracksNotYetCached()
    {
        File.WriteAllText(Path.Combine(_cacheDirectory, "3.srt"), "already here");
        var source = CreateSource(
            Subtitle(2, "ass"),
            Subtitle(3, "subrip"),           // cached
            Subtitle(4, "pgssub"),           // bitmap, left to the background pass
            Subtitle(5, "mov_text"),         // converted to srt
            Subtitle(6, "ssa"),              // the chunk merge only knows ASS and SRT
            Subtitle(7, "srt", external: true),
            Subtitle(8, "subrip"));

        var tracks = _encoder.GetTextTracksForPass(source);

        Assert.Equal(new[] { 2, 5, 8 }, tracks.Select(t => t.StreamIndex));
        Assert.Equal(new[] { "copy", "srt", "copy" }, tracks.Select(t => t.Codec));
        Assert.Equal(new[] { "ass", "srt", "srt" }, tracks.Select(t => t.Format));
        Assert.Equal(Path.Combine(_cacheDirectory, "2.ass"), tracks[0].OutputPath);

        // ffmpeg numbers the streams inside the container, so the external track (7) does not
        // count and Jellyfin's stream 8 is ffmpeg's 0:7.
        Assert.Equal(new[] { 2, 5, 7 }, tracks.Select(t => t.FfmpegIndex));
    }

    [Fact]
    public void Tracks_AreEmptyWhenEverythingIsCached()
    {
        File.WriteAllText(Path.Combine(_cacheDirectory, "2.ass"), "cached");

        Assert.Empty(_encoder.GetTextTracksForPass(CreateSource(Subtitle(2, "ass"))));
    }

    [Fact]
    public void ChunkArguments_SeekOnceAndWriteOneOutputPerTrack()
    {
        var args = SubtitleEncoder.BuildTextTrackChunkArguments(
            "\"https://example.invalid/video.mkv\"",
            335.024,
            730.048,
            new[] { (2, "copy", "/tmp/chunk_1_2.ass"), (6, "srt", "/tmp/chunk_1_7.srt") });

        Assert.StartsWith("-ss 335.024 -to 730.048 -i \"https://example.invalid/video.mkv\" -copyts", args, StringComparison.Ordinal);
        Assert.Contains(" -map 0:2 -an -vn -c:s copy -flush_packets 1 \"/tmp/chunk_1_2.ass\"", args, StringComparison.Ordinal);
        Assert.EndsWith(" -map 0:6 -an -vn -c:s srt -flush_packets 1 \"/tmp/chunk_1_7.srt\"", args, StringComparison.Ordinal);
        Assert.Single(args.Split("-ss ", StringSplitOptions.None).Skip(1));
    }

    [Fact]
    public void ChunkArguments_UseInvariantNumbers()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("it-IT");
            var args = SubtitleEncoder.BuildTextTrackChunkArguments("in", 0, 375.5, new[] { (2, "copy", "out.ass") });
            Assert.StartsWith("-ss 0.000 -to 375.500 ", args, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void ChunkRanges_OverlapTheirNeighbours()
    {
        var ranges = SubtitleEncoder.GetChunkRanges(1400);

        Assert.Equal((0d, 370d), ranges[0]);
        Assert.Equal((330d, 720d), ranges[1]);
        Assert.Equal((1030d, 1400d), ranges[3]);
    }

    [Fact]
    public void Chunks_AgreeWhenTheyHoldTheSameOverlap()
    {
        var ranges = SubtitleEncoder.GetChunkRanges(1400);
        var paths = new[]
        {
            WriteAssChunk(0, 10, 200, 340, 355),
            WriteAssChunk(1, 332, 340, 355, 500, 690, 705),
            WriteAssChunk(2, 690, 705, 900, 1040, 1055),
            WriteAssChunk(3, 1040, 1055, 1300),
        };

        Assert.Equal(-1, SubtitleEncoder.FindDisagreeingChunk(paths, ranges, "ass"));
    }

    [Fact]
    public void Chunks_DisagreeWhenOneCameBackEmpty()
    {
        // A read that failed at once: the header, no events, exit code 0.
        var ranges = SubtitleEncoder.GetChunkRanges(1400);
        var paths = new[]
        {
            WriteAssChunk(0),
            WriteAssChunk(1, 332, 340, 355, 500, 690, 705),
            WriteAssChunk(2, 690, 705, 900, 1040, 1055),
            WriteAssChunk(3, 1040, 1055, 1300),
        };

        Assert.Equal(0, SubtitleEncoder.FindDisagreeingChunk(paths, ranges, "ass"));
    }

    [Fact]
    public void Chunks_DisagreeWhenOneStoppedEarly()
    {
        var ranges = SubtitleEncoder.GetChunkRanges(1400);
        var paths = new[]
        {
            WriteAssChunk(0, 10, 200, 340, 355),
            WriteAssChunk(1, 332, 340, 355, 500),
            WriteAssChunk(2, 690, 705, 900, 1040, 1055),
            WriteAssChunk(3, 1040, 1055, 1300),
        };

        Assert.Equal(1, SubtitleEncoder.FindDisagreeingChunk(paths, ranges, "ass"));
    }

    [Fact]
    public void Chunks_AgreeAtTheEdgesOfTheOverlap()
    {
        // The seek of the later chunk lands before its start and the earlier one stops at its
        // end: events right at the edges of the overlap are in one chunk only.
        var ranges = SubtitleEncoder.GetChunkRanges(1400);
        var paths = new[]
        {
            WriteAssChunk(0, 10, 345, 368),
            WriteAssChunk(1, 328, 331, 345, 600),
            WriteAssChunk(2, 900),
            WriteAssChunk(3, 1300),
        };

        Assert.Equal(-1, SubtitleEncoder.FindDisagreeingChunk(paths, ranges, "ass"));
    }

    [Fact]
    public void SrtChunks_DisagreeWhenOneIsMissing()
    {
        var ranges = SubtitleEncoder.GetChunkRanges(1400);
        var paths = new[]
        {
            WriteSrtChunk(0, 10, 340),
            Path.Combine(_cacheDirectory, "chunk_1.srt"),
            WriteSrtChunk(2, 900),
            WriteSrtChunk(3, 1300),
        };

        Assert.Equal(0, SubtitleEncoder.FindDisagreeingChunk(paths, ranges, "srt"));
    }

    [Fact]
    public void SrtChunks_AgreeWhatEverTheirNumbering()
    {
        var ranges = SubtitleEncoder.GetChunkRanges(1400);
        var paths = new[]
        {
            WriteSrtChunk(0, 10, 200, 340),
            WriteSrtChunk(1, 340, 500),
            WriteSrtChunk(2, 900),
            WriteSrtChunk(3, 1300),
        };

        Assert.Equal(-1, SubtitleEncoder.FindDisagreeingChunk(paths, ranges, "srt"));
    }

    private string WriteAssChunk(int chunk, params int[] eventStarts)
    {
        var path = Path.Combine(_cacheDirectory, string.Create(CultureInfo.InvariantCulture, $"chunk_{chunk}.ass"));
        var lines = new List<string>
        {
            "[Script Info]",
            "ScriptType: v4.00+",
            string.Empty,
            "[Events]",
            "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text",
        };
        lines.AddRange(eventStarts.Select(s => string.Create(CultureInfo.InvariantCulture, $"Dialogue: 0,{AssTime(s)},{AssTime(s + 2)},Default,,0,0,0,,Line at {s}")));
        File.WriteAllLines(path, lines);
        return path;
    }

    private string WriteSrtChunk(int chunk, params int[] eventStarts)
    {
        var path = Path.Combine(_cacheDirectory, string.Create(CultureInfo.InvariantCulture, $"chunk_{chunk}.srt"));
        File.WriteAllText(path, string.Concat(eventStarts.Select((s, i) => string.Create(
            CultureInfo.InvariantCulture,
            $"{i + 1}\n{SrtTime(s)} --> {SrtTime(s + 2)}\nLine at {s}\n\n"))));
        return path;
    }

    private static string AssTime(int seconds)
        => TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss\.ff", CultureInfo.InvariantCulture);

    private static string SrtTime(int seconds)
        => TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);

    [Fact]
    public void Watched_WhenAnySessionPlaysTheSource()
    {
        // A SyncPlay group: one member moved on, another still plays the source.
        _sessionManager.Setup(x => x.Sessions).Returns(new[]
        {
            Session("another source"),
            Session(MediaSourceId.ToUpperInvariant()),
        });

        Assert.True(_encoder.IsSourceBeingWatched(MediaSourceId));
    }

    [Fact]
    public void NotWatched_WhenNobodyPlaysIt()
    {
        _sessionManager.Setup(x => x.Sessions).Returns(new[]
        {
            Session("another source"),
            Session(MediaSourceId, playing: false),   // stopped, the play state is stale
            Session(null),
        });

        Assert.False(_encoder.IsSourceBeingWatched(MediaSourceId));
    }
}
