using System.Collections.Generic;
using System.Threading.Tasks;

namespace MediaBrowser.MediaEncoding.Subtitles;

/// <summary>
/// Subtitle outputs riding along on a remux ffmpeg process.
/// </summary>
/// <remarks>
/// Pulling a text subtitle track out of a container means reading the whole container: the
/// packets are interleaved with the video. For a remote source that is a second full download
/// on top of the one the remux is already doing, and both then share the same pipe. A remux
/// that starts at the beginning reads every packet anyway, so the subtitle tracks are added
/// to it as extra outputs and nothing is downloaded twice.
/// </remarks>
internal sealed class RemuxSubtitlePlan
{
    /// <summary>
    /// Gets the id of the media source the outputs belong to.
    /// </summary>
    public required string MediaSourceId { get; init; }

    /// <summary>
    /// Gets the ffmpeg arguments to append after the remux's own output.
    /// </summary>
    public required string Arguments { get; init; }

    /// <summary>
    /// Gets the directory holding the in-progress files, removed once the plan completes.
    /// </summary>
    public required string TempDirectory { get; init; }

    /// <summary>
    /// Gets the subtitle outputs.
    /// </summary>
    public required IReadOnlyList<RemuxSubtitleOutput> Outputs { get; init; }

    /// <summary>
    /// Gets the source completed when the files are in the subtitle cache (true) or the
    /// remux did not get to write them in full (false).
    /// </summary>
    public required TaskCompletionSource<bool> Completion { get; init; }
}

/// <summary>
/// One subtitle track written by the remux.
/// </summary>
/// <param name="StreamIndex">The Jellyfin index of the subtitle stream.</param>
/// <param name="TempPath">Where ffmpeg writes it.</param>
/// <param name="FinalPath">Its place in the subtitle cache.</param>
internal sealed record RemuxSubtitleOutput(int StreamIndex, string TempPath, string FinalPath);
