using System;
using System.Collections.Generic;

namespace Jellyfin.Api.Models.SubtitleDtos;

/// <summary>
/// The events a progressive subtitle extraction has found so far.
/// </summary>
public class ProgressiveSubtitleDto
{
    /// <summary>
    /// Gets or sets the extraction to keep following.
    /// </summary>
    public string Session { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets where the extraction started reading.
    /// </summary>
    public long StartPositionTicks { get; set; }

    /// <summary>
    /// Gets or sets the script header up to the [Events] format line; only on the first read.
    /// </summary>
    public string? Header { get; set; }

    /// <summary>
    /// Gets or sets the event lines past the ones the client already has.
    /// </summary>
    public IReadOnlyList<string> Events { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the value to pass as <c>since</c> on the next read.
    /// </summary>
    public int Next { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the extraction read to the end of the file.
    /// </summary>
    public bool Complete { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the extraction failed before producing anything.
    /// </summary>
    public bool Failed { get; set; }

    /// <summary>
    /// Gets or sets the latest event start seen so far.
    /// </summary>
    public long CoveredTicks { get; set; }
}
