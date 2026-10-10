namespace MediaBrowser.MediaEncoding.Subtitles;

/// <summary>
/// One text subtitle track written by a text track pass.
/// </summary>
/// <param name="StreamIndex">The Jellyfin index of the subtitle stream.</param>
/// <param name="FfmpegIndex">The index ffmpeg maps it by.</param>
/// <param name="Codec">The ffmpeg output codec: <c>copy</c>, or <c>srt</c> for a track that is converted.</param>
/// <param name="Format">The format of the written file and its chunks: <c>ass</c> or <c>srt</c>.</param>
/// <param name="OutputPath">Its place in the subtitle cache.</param>
internal sealed record TextTrackOutput(int StreamIndex, int FfmpegIndex, string Codec, string Format, string OutputPath);
