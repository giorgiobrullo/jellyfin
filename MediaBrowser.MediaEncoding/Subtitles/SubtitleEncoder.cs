#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AsyncKeyedLock;
using MediaBrowser.Common;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.MediaEncoding.Encoder;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;
using UtfUnknown;

namespace MediaBrowser.MediaEncoding.Subtitles
{
    public sealed class SubtitleEncoder : ISubtitleEncoder, IDisposable
    {
        private readonly ILogger<SubtitleEncoder> _logger;
        private readonly IFileSystem _fileSystem;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly ISubtitleParser _subtitleParser;
        private readonly IPathManager _pathManager;

        /// <summary>
        /// The _semaphoreLocks.
        /// </summary>
        private readonly AsyncKeyedLocker<string> _semaphoreLocks = new(o =>
        {
            o.PoolSize = 20;
            o.PoolInitialFill = 1;
        });

        public SubtitleEncoder(
            ILogger<SubtitleEncoder> logger,
            IFileSystem fileSystem,
            IMediaEncoder mediaEncoder,
            IHttpClientFactory httpClientFactory,
            IMediaSourceManager mediaSourceManager,
            ISubtitleParser subtitleParser,
            IPathManager pathManager)
        {
            _logger = logger;
            _fileSystem = fileSystem;
            _mediaEncoder = mediaEncoder;
            _httpClientFactory = httpClientFactory;
            _mediaSourceManager = mediaSourceManager;
            _subtitleParser = subtitleParser;
            _pathManager = pathManager;
        }

        private MemoryStream ConvertSubtitles(
            Stream stream,
            string inputFormat,
            string outputFormat,
            long startTimeTicks,
            long endTimeTicks,
            bool preserveOriginalTimestamps,
            CancellationToken cancellationToken)
        {
            var ms = new MemoryStream();

            try
            {
                var trackInfo = _subtitleParser.Parse(stream, inputFormat);

                FilterEvents(trackInfo, startTimeTicks, endTimeTicks, preserveOriginalTimestamps);

                var writer = GetWriter(outputFormat);

                writer.Write(trackInfo, ms, cancellationToken);
                ms.Position = 0;
            }
            catch
            {
                ms.Dispose();
                throw;
            }

            return ms;
        }

        private void FilterEvents(SubtitleTrackInfo track, long startPositionTicks, long endTimeTicks, bool preserveTimestamps)
        {
            // Drop subs that are earlier than what we're looking for
            track.TrackEvents = track.TrackEvents
                .SkipWhile(i => (i.StartPositionTicks - startPositionTicks) < 0 || (i.EndPositionTicks - startPositionTicks) < 0)
                .ToArray();

            if (endTimeTicks > 0)
            {
                track.TrackEvents = track.TrackEvents
                    .TakeWhile(i => i.StartPositionTicks <= endTimeTicks)
                    .ToArray();
            }

            if (!preserveTimestamps)
            {
                foreach (var trackEvent in track.TrackEvents)
                {
                    trackEvent.EndPositionTicks -= startPositionTicks;
                    trackEvent.StartPositionTicks -= startPositionTicks;
                }
            }
        }

        async Task<Stream> ISubtitleEncoder.GetSubtitles(BaseItem item, string mediaSourceId, int subtitleStreamIndex, string outputFormat, long startTimeTicks, long endTimeTicks, bool preserveOriginalTimestamps, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(item);

            if (string.IsNullOrWhiteSpace(mediaSourceId))
            {
                throw new ArgumentNullException(nameof(mediaSourceId));
            }

            var mediaSources = await _mediaSourceManager.GetPlaybackMediaSources(item, null, true, false, cancellationToken).ConfigureAwait(false);

            var mediaSource = mediaSources
                .First(i => string.Equals(i.Id, mediaSourceId, StringComparison.OrdinalIgnoreCase));

            var subtitleStream = mediaSource.MediaStreams
               .First(i => i.Type == MediaStreamType.Subtitle && i.Index == subtitleStreamIndex);

            var (stream, inputFormat) = await GetSubtitleStream(mediaSource, subtitleStream, cancellationToken)
                        .ConfigureAwait(false);

            // Return the original if the same format is being requested
            // Character encoding was already handled in GetSubtitleStream
            if (string.Equals(inputFormat, outputFormat, StringComparison.OrdinalIgnoreCase))
            {
                return stream;
            }

            using (stream)
            {
                return ConvertSubtitles(stream, inputFormat, outputFormat, startTimeTicks, endTimeTicks, preserveOriginalTimestamps, cancellationToken);
            }
        }

        private async Task<(Stream Stream, string Format)> GetSubtitleStream(
            MediaSourceInfo mediaSource,
            MediaStream subtitleStream,
            CancellationToken cancellationToken)
        {
            var fileInfo = await GetReadableFile(mediaSource, subtitleStream, cancellationToken).ConfigureAwait(false);

            var stream = await GetSubtitleStream(fileInfo, cancellationToken).ConfigureAwait(false);

            return (stream, fileInfo.Format);
        }

        private async Task<Stream> GetSubtitleStream(SubtitleInfo fileInfo, CancellationToken cancellationToken)
        {
            if (fileInfo.IsExternal)
            {
                var stream = await GetStream(fileInfo.Path, fileInfo.Protocol, cancellationToken).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    var result = await CharsetDetector.DetectFromStreamAsync(stream, cancellationToken).ConfigureAwait(false);
                    var detected = result.Detected;
                    stream.Position = 0;

                    if (detected is not null)
                    {
                        _logger.LogDebug("charset {CharSet} detected for {Path}", detected.EncodingName, fileInfo.Path);

                        using var reader = new StreamReader(stream, detected.Encoding);
                        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

                        return new MemoryStream(Encoding.UTF8.GetBytes(text));
                    }
                }
            }

            return AsyncFile.OpenRead(fileInfo.Path);
        }

        internal async Task<SubtitleInfo> GetReadableFile(
            MediaSourceInfo mediaSource,
            MediaStream subtitleStream,
            CancellationToken cancellationToken)
        {
            if (!subtitleStream.IsExternal || subtitleStream.Path.EndsWith(".mks", StringComparison.OrdinalIgnoreCase))
            {
                // Extract the requested track first for fast availability,
                // then lazily extract remaining tracks in the background.
                await ExtractSingleSubtitle(mediaSource, subtitleStream, cancellationToken).ConfigureAwait(false);
                _ = Task.Run(() => ExtractRemainingSubtitlesAsync(mediaSource, subtitleStream.Index), CancellationToken.None);

                var outputFileExtension = GetExtractableSubtitleFileExtension(subtitleStream);
                var outputFormat = GetExtractableSubtitleFormat(subtitleStream);
                var outputPath = GetSubtitleCachePath(mediaSource, subtitleStream.Index, "." + outputFileExtension);

                return new SubtitleInfo()
                {
                    Path = outputPath,
                    Protocol = MediaProtocol.File,
                    Format = outputFormat,
                    IsExternal = false
                };
            }

            var currentFormat = (Path.GetExtension(subtitleStream.Path) ?? subtitleStream.Codec)
                .TrimStart('.');

            // Handle PGS subtitles as raw streams for the client to render
            if (MediaStream.IsPgsFormat(currentFormat))
            {
                return new SubtitleInfo()
                {
                    Path = subtitleStream.Path,
                    Protocol = _mediaSourceManager.GetPathProtocol(subtitleStream.Path),
                    Format = "pgssub",
                    IsExternal = true
                };
            }

            // Fallback to ffmpeg conversion
            if (!_subtitleParser.SupportsFileExtension(currentFormat))
            {
                // Convert
                var outputPath = GetSubtitleCachePath(mediaSource, subtitleStream.Index, ".srt");

                await ConvertTextSubtitleToSrt(subtitleStream, mediaSource, outputPath, cancellationToken).ConfigureAwait(false);

                return new SubtitleInfo()
                {
                    Path = outputPath,
                    Protocol = MediaProtocol.File,
                    Format = "srt",
                    IsExternal = true
                };
            }

            // It's possible that the subtitleStream and mediaSource don't share the same protocol (e.g. .STRM file with local subs)
            return new SubtitleInfo()
            {
                Path = subtitleStream.Path,
                Protocol = _mediaSourceManager.GetPathProtocol(subtitleStream.Path),
                Format = currentFormat,
                IsExternal = true
            };
        }

        private bool TryGetWriter(string format, [NotNullWhen(true)] out ISubtitleWriter? value)
        {
            ArgumentException.ThrowIfNullOrEmpty(format);

            if (string.Equals(format, SubtitleFormat.ASS, StringComparison.OrdinalIgnoreCase))
            {
                value = new AssWriter();
                return true;
            }

            if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            {
                value = new JsonWriter();
                return true;
            }

            if (string.Equals(format, SubtitleFormat.SRT, StringComparison.OrdinalIgnoreCase) || string.Equals(format, SubtitleFormat.SUBRIP, StringComparison.OrdinalIgnoreCase))
            {
                value = new SrtWriter();
                return true;
            }

            if (string.Equals(format, SubtitleFormat.SSA, StringComparison.OrdinalIgnoreCase))
            {
                value = new SsaWriter();
                return true;
            }

            if (string.Equals(format, SubtitleFormat.VTT, StringComparison.OrdinalIgnoreCase) || string.Equals(format, SubtitleFormat.WEBVTT, StringComparison.OrdinalIgnoreCase))
            {
                value = new VttWriter();
                return true;
            }

            if (string.Equals(format, SubtitleFormat.TTML, StringComparison.OrdinalIgnoreCase))
            {
                value = new TtmlWriter();
                return true;
            }

            value = null;
            return false;
        }

        private ISubtitleWriter GetWriter(string format)
        {
            if (TryGetWriter(format, out var writer))
            {
                return writer;
            }

            throw new ArgumentException("Unsupported format: " + format);
        }

        /// <summary>
        /// Converts the text subtitle to SRT.
        /// </summary>
        /// <param name="subtitleStream">The subtitle stream.</param>
        /// <param name="mediaSource">The input mediaSource.</param>
        /// <param name="outputPath">The output path.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        private async Task ConvertTextSubtitleToSrt(MediaStream subtitleStream, MediaSourceInfo mediaSource, string outputPath, CancellationToken cancellationToken)
        {
            using (await _semaphoreLocks.LockAsync(outputPath, cancellationToken).ConfigureAwait(false))
            {
                if (!File.Exists(outputPath) || _fileSystem.GetFileInfo(outputPath).Length == 0)
                {
                    await ConvertTextSubtitleToSrtInternal(subtitleStream, mediaSource, outputPath, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Converts the text subtitle to SRT internal.
        /// </summary>
        /// <param name="subtitleStream">The subtitle stream.</param>
        /// <param name="mediaSource">The input mediaSource.</param>
        /// <param name="outputPath">The output path.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        /// <exception cref="ArgumentNullException">
        /// The <c>inputPath</c> or <c>outputPath</c> is <c>null</c>.
        /// </exception>
        private async Task ConvertTextSubtitleToSrtInternal(MediaStream subtitleStream, MediaSourceInfo mediaSource, string outputPath, CancellationToken cancellationToken)
        {
            var inputPath = subtitleStream.Path;
            ArgumentException.ThrowIfNullOrEmpty(inputPath);

            ArgumentException.ThrowIfNullOrEmpty(outputPath);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? throw new ArgumentException($"Provided path ({outputPath}) is not valid.", nameof(outputPath)));

            var encodingParam = await GetSubtitleFileCharacterSet(subtitleStream, subtitleStream.Language, mediaSource, cancellationToken).ConfigureAwait(false);

            // FFmpeg automatically convert character encoding when it is UTF-16
            // If we specify character encoding, it rejects with "do not specify a character encoding" and "Unable to recode subtitle event"
            if ((inputPath.EndsWith(".smi", StringComparison.Ordinal) || inputPath.EndsWith(".sami", StringComparison.Ordinal)) &&
                (encodingParam.Equals("UTF-16BE", StringComparison.OrdinalIgnoreCase) ||
                 encodingParam.Equals("UTF-16LE", StringComparison.OrdinalIgnoreCase)))
            {
                encodingParam = string.Empty;
            }
            else if (!string.IsNullOrEmpty(encodingParam))
            {
                encodingParam = " -sub_charenc " + encodingParam;
            }

            int exitCode;

            using (var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    FileName = _mediaEncoder.EncoderPath,
                    Arguments = string.Format(CultureInfo.InvariantCulture, "{0} -i \"{1}\" -c:s srt \"{2}\"", encodingParam, EncodingUtils.NormalizePath(inputPath), EncodingUtils.NormalizePath(outputPath)),
                    WindowStyle = ProcessWindowStyle.Hidden,
                    ErrorDialog = false
                },
                EnableRaisingEvents = true
            })
            {
                _logger.LogInformation("{0} {1}", process.StartInfo.FileName, process.StartInfo.Arguments);

                try
                {
                    process.Start();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error starting ffmpeg");

                    throw;
                }

                try
                {
                    await process.WaitForExitAsync(TimeSpan.FromMinutes(30)).ConfigureAwait(false);
                    exitCode = process.ExitCode;
                }
                catch (OperationCanceledException)
                {
                    process.Kill(true);
                    exitCode = -1;
                }
            }

            var failed = false;

            if (exitCode == -1)
            {
                failed = true;

                if (File.Exists(outputPath))
                {
                    try
                    {
                        _logger.LogInformation("Deleting converted subtitle due to failure: {Path}", outputPath);
                        _fileSystem.DeleteFile(outputPath);
                    }
                    catch (IOException ex)
                    {
                        _logger.LogError(ex, "Error deleting converted subtitle {Path}", outputPath);
                    }
                }
            }
            else if (!File.Exists(outputPath) || _fileSystem.GetFileInfo(outputPath).Length == 0)
            {
                failed = true;

                try
                {
                    _logger.LogWarning("Deleting converted subtitle due to failure: {Path}", outputPath);
                    _fileSystem.DeleteFile(outputPath);
                }
                catch (FileNotFoundException)
                {
                }
                catch (IOException ex)
                {
                    _logger.LogError(ex, "Error deleting converted subtitle {Path}", outputPath);
                }
            }

            if (failed)
            {
                _logger.LogError("ffmpeg subtitle conversion failed for {Path}", inputPath);

                throw new FfmpegException(
                    string.Format(CultureInfo.InvariantCulture, "ffmpeg subtitle conversion failed for {0}", inputPath));
            }

            await SetAssFont(outputPath, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("ffmpeg subtitle conversion succeeded for {Path}", inputPath);
        }

        private string GetExtractableSubtitleFormat(MediaStream subtitleStream)
        {
            if (string.Equals(subtitleStream.Codec, "ass", StringComparison.OrdinalIgnoreCase)
                || string.Equals(subtitleStream.Codec, "ssa", StringComparison.OrdinalIgnoreCase)
                || string.Equals(subtitleStream.Codec, "pgssub", StringComparison.OrdinalIgnoreCase))
            {
                return subtitleStream.Codec;
            }
            else
            {
                return "srt";
            }
        }

        private string GetExtractableSubtitleFileExtension(MediaStream subtitleStream)
        {
            // Using .pgssub as file extension is not allowed by ffmpeg. The file extension for pgs subtitles is .sup.
            if (string.Equals(subtitleStream.Codec, "pgssub", StringComparison.OrdinalIgnoreCase))
            {
                return "sup";
            }
            else
            {
                return GetExtractableSubtitleFormat(subtitleStream);
            }
        }

        private bool IsCodecCopyable(string codec)
        {
            return string.Equals(codec, "ass", StringComparison.OrdinalIgnoreCase)
                || string.Equals(codec, "ssa", StringComparison.OrdinalIgnoreCase)
                || string.Equals(codec, "srt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(codec, "subrip", StringComparison.OrdinalIgnoreCase)
                || string.Equals(codec, "pgssub", StringComparison.OrdinalIgnoreCase);
        }

        /// <inheritdoc />
        public async Task ExtractAllExtractableSubtitles(MediaSourceInfo mediaSource, CancellationToken cancellationToken)
        {
            var locks = new List<IDisposable>();
            var extractableStreams = new List<MediaStream>();

            try
            {
                var subtitleStreams = mediaSource.MediaStreams
                    .Where(stream => stream is { IsExtractableSubtitleStream: true, SupportsExternalStream: true });

                foreach (var subtitleStream in subtitleStreams)
                {
                    if (subtitleStream.IsExternal && !subtitleStream.Path.EndsWith(".mks", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var outputPath = GetSubtitleCachePath(mediaSource, subtitleStream.Index, "." + GetExtractableSubtitleFileExtension(subtitleStream));

                    var releaser = await _semaphoreLocks.LockAsync(outputPath, cancellationToken).ConfigureAwait(false);

                    if (File.Exists(outputPath) && _fileSystem.GetFileInfo(outputPath).Length > 0)
                    {
                        releaser.Dispose();
                        continue;
                    }

                    locks.Add(releaser);
                    extractableStreams.Add(subtitleStream);
                }

                if (extractableStreams.Count > 0)
                {
                    await ExtractAllExtractableSubtitlesInternal(mediaSource, extractableStreams, cancellationToken).ConfigureAwait(false);
                    await ExtractAllExtractableSubtitlesMKS(mediaSource, extractableStreams, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to get streams for File:{File}", mediaSource.Path);
            }
            finally
            {
                locks.ForEach(x => x.Dispose());
            }
        }

        /// <summary>
        /// Extracts a single subtitle track from the media source.
        /// Used to prioritize the selected subtitle for fast availability.
        /// </summary>
        private async Task ExtractSingleSubtitle(
            MediaSourceInfo mediaSource,
            MediaStream subtitleStream,
            CancellationToken cancellationToken)
        {
            if (!subtitleStream.IsExtractableSubtitleStream || !subtitleStream.SupportsExternalStream)
            {
                return;
            }

            // MKS subtitles are handled by ExtractAllExtractableSubtitlesMKS
            if (subtitleStream.IsExternal && !subtitleStream.Path.EndsWith(".mks", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var outputPath = GetSubtitleCachePath(mediaSource, subtitleStream.Index, "." + GetExtractableSubtitleFileExtension(subtitleStream));

            var releaser = await _semaphoreLocks.LockAsync(outputPath, cancellationToken).ConfigureAwait(false);
            try
            {
                if (File.Exists(outputPath))
                {
                    return;
                }

                // Handle MKS files separately
                if (!string.IsNullOrEmpty(subtitleStream.Path) && subtitleStream.Path.EndsWith(".mks", StringComparison.OrdinalIgnoreCase))
                {
                    await ExtractAllExtractableSubtitlesMKS(mediaSource, new List<MediaStream> { subtitleStream }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                _logger.LogDebug("Extracting selected subtitle track {Index} ({Language}/{Codec}) for {File}", subtitleStream.Index, subtitleStream.Language, subtitleStream.Codec, mediaSource.Path);
                var extractionStart = System.Diagnostics.Stopwatch.StartNew();

                var inputPath = _mediaEncoder.GetInputArgument(mediaSource.Path, mediaSource);
                var outputCodec = IsCodecCopyable(subtitleStream.Codec) ? "copy" : "srt";
                var streamIndex = EncodingHelper.FindIndex(mediaSource.MediaStreams, subtitleStream);

                if (streamIndex == -1)
                {
                    _logger.LogError("Cannot find subtitle stream index for {InputPath} ({Index}), skipping", inputPath, subtitleStream.Index);
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? throw new FileNotFoundException($"Calculated path ({outputPath}) is not valid."));

                // For long-running HTTP sources, ffmpeg has to demux the entire container to
                // be sure it's seen every subtitle packet. On a 10GB+ remote mkv that's ~50s
                // of linear read. We can break that into N chunks extracted in parallel with
                // -ss/-to, then merge. Each chunk pulls only its slice via HTTP range requests.
                //
                // Whether this actually speeds things up depends on the upstream pipe:
                // if the proxy/CDN serves concurrent requests at independent bandwidth, this
                // is a big win; if it rate-limits or shares bandwidth per-client, parallel
                // chunks just divvy up the same pipe and you save nothing. Toggle via env
                // var JELLYFIN_PARALLEL_SUBTITLE_EXTRACTION ('0' / 'false' to disable).
                var durationSeconds = mediaSource.RunTimeTicks.HasValue
                    ? mediaSource.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond
                    : 0;
                // supportedFormat must match the actual subtitle-file extension so the
                // ffmpeg output muxer (selected from the filename) matches the input's
                // codec on `-c:s copy`. Previously this was hardcoded to "ass" when
                // outputCodec=="copy", which produced ASS-extension output for SRT
                // sources and ffmpeg failed immediately (wrong container).
                var fileExt = GetExtractableSubtitleFileExtension(subtitleStream);
                var supportedFormat = string.Equals(outputCodec, "copy", StringComparison.OrdinalIgnoreCase)
                    ? (fileExt?.ToLowerInvariant() ?? string.Empty)
                    : outputCodec;
                var parallelEnvVar = Environment.GetEnvironmentVariable("JELLYFIN_PARALLEL_SUBTITLE_EXTRACTION");
                var parallelEnabled = !string.Equals(parallelEnvVar, "0", StringComparison.Ordinal)
                    && !string.Equals(parallelEnvVar, "false", StringComparison.OrdinalIgnoreCase);
                var willUseParallel = parallelEnabled
                    && durationSeconds >= 600
                    && (supportedFormat == "ass" || supportedFormat == "srt");
                _logger.LogInformation(
                    "Single subtitle extraction decision for track {Index}: durationSeconds={Duration}, format={Format}, parallelEnabled={Enabled}, willUseParallel={UseParallel}",
                    subtitleStream.Index,
                    durationSeconds,
                    supportedFormat,
                    parallelEnabled,
                    willUseParallel);
                if (willUseParallel)
                {
                    try
                    {
                        await ExtractSingleSubtitleParallel(
                            inputPath,
                            streamIndex,
                            outputCodec,
                            outputPath,
                            durationSeconds,
                            supportedFormat,
                            cancellationToken).ConfigureAwait(false);
                        extractionStart.Stop();
                        _logger.LogInformation("Extracted selected subtitle track {Index} ({Language}/{Codec}) in {Time}ms (parallel) for {File}", subtitleStream.Index, subtitleStream.Language, subtitleStream.Codec, extractionStart.ElapsedMilliseconds, mediaSource.Path);
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Parallel subtitle extraction failed; falling back to single-pass for track {Index}", subtitleStream.Index);
                        try
                        {
                            _fileSystem.DeleteFile(outputPath);
                        }
                        catch (FileNotFoundException)
                        {
                        }
                    }
                }

                var args = string.Format(
                    CultureInfo.InvariantCulture,
                    "-i {0} -map 0:{1} -an -vn -c:s {2} -flush_packets 1 \"{3}\"",
                    inputPath,
                    streamIndex,
                    outputCodec,
                    outputPath);

                await ExtractSubtitlesForFile(inputPath, args, new List<string> { outputPath }, cancellationToken).ConfigureAwait(false);
                extractionStart.Stop();
                _logger.LogInformation("Extracted selected subtitle track {Index} ({Language}/{Codec}) in {Time}ms for {File}", subtitleStream.Index, subtitleStream.Language, subtitleStream.Codec, extractionStart.ElapsedMilliseconds, mediaSource.Path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract single subtitle track {Index} for {File}", subtitleStream.Index, mediaSource.Path);
            }
            finally
            {
                releaser.Dispose();
            }
        }

        /// <summary>
        /// Splits the source into N time ranges and extracts each in parallel via -ss/-to,
        /// then merges the ASS/SRT outputs. Designed for slow HTTP sources where a single
        /// linear demux dominates wall-clock time.
        /// </summary>
        private async Task ExtractSingleSubtitleParallel(
            string inputPath,
            int streamIndex,
            string outputCodec,
            string outputPath,
            double durationSeconds,
            string format,
            CancellationToken cancellationToken)
        {
            const int numChunks = 4;
            const double overlapSeconds = 20;
            var chunkSize = durationSeconds / numChunks;

            var outputDir = Path.GetDirectoryName(outputPath) ?? throw new ArgumentException("Invalid output path", nameof(outputPath));
            var baseName = Path.GetFileNameWithoutExtension(outputPath);
            var tempDir = Path.Combine(outputDir, $"_chunks_{baseName}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            var chunkPaths = new List<string>();
            var tasks = new List<Task>();

            try
            {
                for (var i = 0; i < numChunks; i++)
                {
                    var rawStart = i * chunkSize;
                    var rawEnd = (i == numChunks - 1) ? durationSeconds : (i + 1) * chunkSize;
                    var start = Math.Max(0, rawStart - (i == 0 ? 0 : overlapSeconds));
                    var end = Math.Min(durationSeconds, rawEnd + overlapSeconds);
                    var chunkPath = Path.Combine(tempDir, $"chunk_{i}.{format}");
                    chunkPaths.Add(chunkPath);

                    // -ss before -i: input seek (fast for HTTP, uses container index)
                    // -copyts: keep absolute timestamps so events line up across chunks
                    var args = string.Format(
                        CultureInfo.InvariantCulture,
                        "-ss {0} -to {1} -i {2} -map 0:{3} -an -vn -copyts -c:s {4} -flush_packets 1 \"{5}\"",
                        start.ToString("F3", CultureInfo.InvariantCulture),
                        end.ToString("F3", CultureInfo.InvariantCulture),
                        inputPath,
                        streamIndex,
                        outputCodec,
                        chunkPath);

                    tasks.Add(ExtractSubtitlesForFile(inputPath, args, new List<string> { chunkPath }, cancellationToken));
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);

                // Verify at least one chunk produced output; fall through to throw if all empty.
                var anyProduced = chunkPaths.Any(p => File.Exists(p) && new FileInfo(p).Length > 0);
                if (!anyProduced)
                {
                    throw new InvalidOperationException("All subtitle chunks produced empty output");
                }

                if (string.Equals(format, "ass", StringComparison.OrdinalIgnoreCase))
                {
                    MergeAssChunks(chunkPaths, outputPath);
                }
                else
                {
                    MergeSrtChunks(chunkPaths, outputPath);
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch (IOException ex)
                {
                    _logger.LogDebug(ex, "Failed to clean up subtitle chunk tempdir {Dir}", tempDir);
                }
            }
        }

        /// <summary>
        /// Merges N ASS chunk files into a single output file. Takes the header from the
        /// first non-empty chunk and deduplicates Dialogue/Comment lines from [Events].
        /// </summary>
        private static void MergeAssChunks(IReadOnlyList<string> chunkPaths, string outputPath)
        {
            string? header = null;
            string? eventsFormatLine = null;
            var seenEvents = new HashSet<string>(StringComparer.Ordinal);
            var orderedEvents = new List<(double StartSeconds, string Line)>();

            foreach (var chunkPath in chunkPaths)
            {
                if (!File.Exists(chunkPath) || new FileInfo(chunkPath).Length == 0)
                {
                    continue;
                }

                var lines = File.ReadAllLines(chunkPath);
                var eventsIdx = -1;
                for (var i = 0; i < lines.Length; i++)
                {
                    if (lines[i].StartsWith("[Events]", StringComparison.OrdinalIgnoreCase))
                    {
                        eventsIdx = i;
                        break;
                    }
                }

                if (eventsIdx == -1)
                {
                    continue;
                }

                if (header is null)
                {
                    // Header = everything up to and including [Events] + Format: line from this chunk.
                    var sb = new StringBuilder();
                    for (var i = 0; i <= eventsIdx; i++)
                    {
                        sb.AppendLine(lines[i]);
                    }

                    // The line after [Events] should be "Format: ..."
                    if (eventsIdx + 1 < lines.Length && lines[eventsIdx + 1].StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
                    {
                        eventsFormatLine = lines[eventsIdx + 1];
                        sb.AppendLine(eventsFormatLine);
                    }

                    header = sb.ToString();
                }

                for (var i = eventsIdx + 1; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)
                        && !line.StartsWith("Comment:", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!seenEvents.Add(line))
                    {
                        continue;
                    }

                    orderedEvents.Add((ParseAssStartSeconds(line), line));
                }
            }

            if (header is null)
            {
                throw new InvalidOperationException("No chunks had [Events] section");
            }

            orderedEvents.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));

            using var writer = new StreamWriter(outputPath, append: false);
            writer.Write(header);
            foreach (var (_, line) in orderedEvents)
            {
                writer.WriteLine(line);
            }
        }

        /// <summary>
        /// Parses the start time of an ASS Dialogue/Comment line as seconds. Format after the
        /// comma splits is "H:MM:SS.CS" at index 1.
        /// </summary>
        private static double ParseAssStartSeconds(string dialogueLine)
        {
            var colon = dialogueLine.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0 || colon + 1 >= dialogueLine.Length)
            {
                return 0;
            }

            var parts = dialogueLine.Substring(colon + 1).Split(',');
            if (parts.Length < 2)
            {
                return 0;
            }

            var timeStr = parts[1].Trim();
            var timeParts = timeStr.Split(':');
            if (timeParts.Length != 3)
            {
                return 0;
            }

            if (int.TryParse(timeParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
                && int.TryParse(timeParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)
                && double.TryParse(timeParts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
            {
                return (h * 3600) + (m * 60) + s;
            }

            return 0;
        }

        /// <summary>
        /// Merges N SRT chunk files into a single output, deduplicating identical entries and
        /// renumbering sequentially. Entries are sorted by start time.
        /// </summary>
        private static void MergeSrtChunks(IReadOnlyList<string> chunkPaths, string outputPath)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var entries = new List<(double StartSeconds, string TimeLine, string Text)>();

            foreach (var chunkPath in chunkPaths)
            {
                if (!File.Exists(chunkPath) || new FileInfo(chunkPath).Length == 0)
                {
                    continue;
                }

                var text = File.ReadAllText(chunkPath).Replace("\r\n", "\n", StringComparison.Ordinal);
                foreach (var block in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
                {
                    var blockLines = block.Split('\n');
                    if (blockLines.Length < 2)
                    {
                        continue;
                    }

                    // Skip leading number line if present (we renumber)
                    var timeLineIdx = blockLines[0].Contains("-->", StringComparison.Ordinal) ? 0 : 1;
                    if (timeLineIdx >= blockLines.Length)
                    {
                        continue;
                    }

                    var timeLine = blockLines[timeLineIdx];
                    if (!timeLine.Contains("-->", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var bodyLines = blockLines.Skip(timeLineIdx + 1).ToArray();
                    var body = string.Join('\n', bodyLines).TrimEnd();
                    var dedupKey = timeLine + "|" + body;
                    if (!seen.Add(dedupKey))
                    {
                        continue;
                    }

                    entries.Add((ParseSrtStartSeconds(timeLine), timeLine, body));
                }
            }

            entries.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));

            using var writer = new StreamWriter(outputPath, append: false);
            for (var i = 0; i < entries.Count; i++)
            {
                writer.WriteLine((i + 1).ToString(CultureInfo.InvariantCulture));
                writer.WriteLine(entries[i].TimeLine);
                writer.WriteLine(entries[i].Text);
                writer.WriteLine();
            }
        }

        private static double ParseSrtStartSeconds(string timeLine)
        {
            // Format: "00:00:12,345 --> 00:00:15,678"
            var arrow = timeLine.IndexOf("-->", StringComparison.Ordinal);
            var startStr = (arrow < 0 ? timeLine : timeLine.Substring(0, arrow)).Trim().Replace(',', '.');
            var parts = startStr.Split(':');
            if (parts.Length != 3)
            {
                return 0;
            }

            if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
            {
                return (h * 3600) + (m * 60) + s;
            }

            return 0;
        }

        /// <summary>
        /// Extracts remaining subtitle tracks in the background after the priority track.
        /// Text subtitles are extracted first, then PGS (bitmap) subtitles.
        /// </summary>
        private async Task ExtractRemainingSubtitlesAsync(MediaSourceInfo mediaSource, int alreadyExtractedIndex)
        {
            try
            {
                var subtitleStreams = mediaSource.MediaStreams
                    .Where(stream => stream is { IsExtractableSubtitleStream: true, SupportsExternalStream: true }
                        && stream.Index != alreadyExtractedIndex)
                    .ToList();

                if (subtitleStreams.Count == 0)
                {
                    return;
                }

                // Extract text subs first (small, fast), then PGS (large, slow)
                var textStreams = subtitleStreams.Where(s => s.IsTextSubtitleStream).ToList();
                var pgsStreams = subtitleStreams.Where(s => s.IsPgsSubtitleStream).ToList();

                if (textStreams.Count > 0)
                {
                    _logger.LogInformation("Background extracting {Count} text subtitle tracks for {File}", textStreams.Count, mediaSource.Path);
                    await ExtractAllExtractableSubtitlesForStreams(mediaSource, textStreams, CancellationToken.None).ConfigureAwait(false);
                }

                if (pgsStreams.Count > 0)
                {
                    _logger.LogInformation("Background extracting {Count} PGS subtitle tracks for {File}", pgsStreams.Count, mediaSource.Path);
                    await ExtractAllExtractableSubtitlesForStreams(mediaSource, pgsStreams, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background subtitle extraction failed for {File}", mediaSource.Path);
            }
        }

        /// <summary>
        /// Extracts a specific set of subtitle streams, acquiring locks and skipping already-extracted tracks.
        /// </summary>
        private async Task ExtractAllExtractableSubtitlesForStreams(
            MediaSourceInfo mediaSource,
            List<MediaStream> subtitleStreams,
            CancellationToken cancellationToken)
        {
            var locks = new List<IDisposable>();
            var extractableStreams = new List<MediaStream>();

            try
            {
                foreach (var subtitleStream in subtitleStreams)
                {
                    if (subtitleStream.IsExternal && !subtitleStream.Path.EndsWith(".mks", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var outputPath = GetSubtitleCachePath(mediaSource, subtitleStream.Index, "." + GetExtractableSubtitleFileExtension(subtitleStream));

                    var releaser = await _semaphoreLocks.LockAsync(outputPath, cancellationToken).ConfigureAwait(false);

                    if (File.Exists(outputPath))
                    {
                        releaser.Dispose();
                        continue;
                    }

                    locks.Add(releaser);
                    extractableStreams.Add(subtitleStream);
                }

                if (extractableStreams.Count > 0)
                {
                    await ExtractAllExtractableSubtitlesInternal(mediaSource, extractableStreams, cancellationToken).ConfigureAwait(false);
                    await ExtractAllExtractableSubtitlesMKS(mediaSource, extractableStreams, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to extract subtitle streams for File:{File}", mediaSource.Path);
            }
            finally
            {
                locks.ForEach(x => x.Dispose());
            }
        }

        private async Task ExtractAllExtractableSubtitlesMKS(
           MediaSourceInfo mediaSource,
           List<MediaStream> subtitleStreams,
           CancellationToken cancellationToken)
        {
            var mksFiles = new List<string>();

            foreach (var subtitleStream in subtitleStreams)
            {
                if (string.IsNullOrEmpty(subtitleStream.Path) || !subtitleStream.Path.EndsWith(".mks", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!mksFiles.Contains(subtitleStream.Path))
                {
                    mksFiles.Add(subtitleStream.Path);
                }
            }

            if (mksFiles.Count == 0)
            {
                return;
            }

            foreach (string mksFile in mksFiles)
            {
                var inputPath = _mediaEncoder.GetInputArgument(mksFile, mediaSource);
                var outputPaths = new List<string>();
                var args = string.Format(
                    CultureInfo.InvariantCulture,
                    "-i {0}",
                    inputPath);

                foreach (var subtitleStream in subtitleStreams)
                {
                    if (!subtitleStream.Path.Equals(mksFile, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var outputPath = GetSubtitleCachePath(mediaSource, subtitleStream.Index, "." + GetExtractableSubtitleFileExtension(subtitleStream));
                    var outputCodec = IsCodecCopyable(subtitleStream.Codec) ? "copy" : "srt";
                    var streamIndex = EncodingHelper.FindIndex(mediaSource.MediaStreams, subtitleStream);

                    if (streamIndex == -1)
                    {
                        _logger.LogError("Cannot find subtitle stream index for {InputPath} ({Index}), skipping this stream", inputPath, subtitleStream.Index);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? throw new FileNotFoundException($"Calculated path ({outputPath}) is not valid."));

                    outputPaths.Add(outputPath);
                    args += string.Format(
                        CultureInfo.InvariantCulture,
                        " -map 0:{0} -an -vn -c:s {1} -flush_packets 1 \"{2}\"",
                        streamIndex,
                        outputCodec,
                        outputPath);
                }

                await ExtractSubtitlesForFile(inputPath, args, outputPaths, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task ExtractAllExtractableSubtitlesInternal(
            MediaSourceInfo mediaSource,
            List<MediaStream> subtitleStreams,
            CancellationToken cancellationToken)
        {
            var inputPath = _mediaEncoder.GetInputArgument(mediaSource.Path, mediaSource);
            var outputPaths = new List<string>();
            var args = string.Format(
                CultureInfo.InvariantCulture,
                "-i {0}",
                inputPath);

            foreach (var subtitleStream in subtitleStreams)
            {
                if (!string.IsNullOrEmpty(subtitleStream.Path) && subtitleStream.Path.EndsWith(".mks", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Subtitle {Index} for file {InputPath} is part in an MKS file. Skipping", inputPath, subtitleStream.Index);
                    continue;
                }

                var outputPath = GetSubtitleCachePath(mediaSource, subtitleStream.Index, "." + GetExtractableSubtitleFileExtension(subtitleStream));
                var outputCodec = IsCodecCopyable(subtitleStream.Codec) ? "copy" : "srt";
                var streamIndex = EncodingHelper.FindIndex(mediaSource.MediaStreams, subtitleStream);

                if (streamIndex == -1)
                {
                    _logger.LogError("Cannot find subtitle stream index for {InputPath} ({Index}), skipping this stream", inputPath, subtitleStream.Index);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? throw new FileNotFoundException($"Calculated path ({outputPath}) is not valid."));

                outputPaths.Add(outputPath);
                args += string.Format(
                    CultureInfo.InvariantCulture,
                    " -map 0:{0} -an -vn -c:s {1} -flush_packets 1 \"{2}\"",
                    streamIndex,
                    outputCodec,
                    outputPath);
            }

            if (outputPaths.Count == 0)
            {
                return;
            }

            await ExtractSubtitlesForFile(inputPath, args, outputPaths, cancellationToken).ConfigureAwait(false);
        }

        private async Task ExtractSubtitlesForFile(
            string inputPath,
            string args,
            List<string> outputPaths,
            CancellationToken cancellationToken)
        {
            int exitCode;

            using (var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    FileName = _mediaEncoder.EncoderPath,
                    Arguments = args,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    ErrorDialog = false
                },
                EnableRaisingEvents = true
            })
            {
                _logger.LogInformation("{File} {Arguments}", process.StartInfo.FileName, process.StartInfo.Arguments);

                try
                {
                    process.Start();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error starting ffmpeg");

                    throw;
                }

                try
                {
                    await process.WaitForExitAsync(TimeSpan.FromMinutes(30)).ConfigureAwait(false);
                    exitCode = process.ExitCode;
                }
                catch (OperationCanceledException)
                {
                    process.Kill(true);
                    exitCode = -1;
                }
            }

            var failed = false;

            if (exitCode == -1)
            {
                failed = true;

                foreach (var outputPath in outputPaths)
                {
                    try
                    {
                        _logger.LogWarning("Deleting extracted subtitle due to failure: {Path}", outputPath);
                        _fileSystem.DeleteFile(outputPath);
                    }
                    catch (FileNotFoundException)
                    {
                    }
                    catch (IOException ex)
                    {
                        _logger.LogError(ex, "Error deleting extracted subtitle {Path}", outputPath);
                    }
                }
            }
            else
            {
                foreach (var outputPath in outputPaths)
                {
                    if (!File.Exists(outputPath) || _fileSystem.GetFileInfo(outputPath).Length == 0)
                    {
                        _logger.LogError("ffmpeg subtitle extraction failed for {InputPath} to {OutputPath}", inputPath, outputPath);
                        failed = true;

                        try
                        {
                            _logger.LogWarning("Deleting extracted subtitle due to failure: {Path}", outputPath);
                            _fileSystem.DeleteFile(outputPath);
                        }
                        catch (FileNotFoundException)
                        {
                        }
                        catch (IOException ex)
                        {
                            _logger.LogError(ex, "Error deleting extracted subtitle {Path}", outputPath);
                        }

                        continue;
                    }

                    if (outputPath.EndsWith("ass", StringComparison.OrdinalIgnoreCase))
                    {
                        await SetAssFont(outputPath, cancellationToken).ConfigureAwait(false);
                    }

                    _logger.LogInformation("ffmpeg subtitle extraction completed for {InputPath} to {OutputPath}", inputPath, outputPath);
                }
            }

            if (failed)
            {
                throw new FfmpegException(
                    string.Format(CultureInfo.InvariantCulture, "ffmpeg subtitle extraction failed for {0}", inputPath));
            }
        }

        /// <summary>
        /// Extracts the text subtitle.
        /// </summary>
        /// <param name="mediaSource">The mediaSource.</param>
        /// <param name="subtitleStream">The subtitle stream.</param>
        /// <param name="outputCodec">The output codec.</param>
        /// <param name="outputPath">The output path.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        /// <exception cref="ArgumentException">Must use inputPath list overload.</exception>
        private async Task ExtractTextSubtitle(
            MediaSourceInfo mediaSource,
            MediaStream subtitleStream,
            string outputCodec,
            string outputPath,
            CancellationToken cancellationToken)
        {
            using (await _semaphoreLocks.LockAsync(outputPath, cancellationToken).ConfigureAwait(false))
            {
                if (!File.Exists(outputPath) || _fileSystem.GetFileInfo(outputPath).Length == 0)
                {
                    var subtitleStreamIndex = EncodingHelper.FindIndex(mediaSource.MediaStreams, subtitleStream);

                    var args = _mediaEncoder.GetInputArgument(mediaSource.Path, mediaSource);

                    if (subtitleStream.IsExternal)
                    {
                        args = _mediaEncoder.GetExternalSubtitleInputArgument(subtitleStream.Path);
                    }

                    await ExtractTextSubtitleInternal(
                        args,
                        subtitleStreamIndex,
                        outputCodec,
                        outputPath,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task ExtractTextSubtitleInternal(
            string inputPath,
            int subtitleStreamIndex,
            string outputCodec,
            string outputPath,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrEmpty(inputPath);

            ArgumentException.ThrowIfNullOrEmpty(outputPath);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? throw new ArgumentException($"Provided path ({outputPath}) is not valid.", nameof(outputPath)));

            var processArgs = string.Format(
                CultureInfo.InvariantCulture,
                "-i {0} -copyts -map 0:{1} -an -vn -c:s {2} \"{3}\"",
                inputPath,
                subtitleStreamIndex,
                outputCodec,
                outputPath);

            int exitCode;

            using (var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    FileName = _mediaEncoder.EncoderPath,
                    Arguments = processArgs,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    ErrorDialog = false
                },
                EnableRaisingEvents = true
            })
            {
                _logger.LogInformation("{File} {Arguments}", process.StartInfo.FileName, process.StartInfo.Arguments);

                try
                {
                    process.Start();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error starting ffmpeg");

                    throw;
                }

                try
                {
                    await process.WaitForExitAsync(TimeSpan.FromMinutes(30)).ConfigureAwait(false);
                    exitCode = process.ExitCode;
                }
                catch (OperationCanceledException)
                {
                    process.Kill(true);
                    exitCode = -1;
                }
            }

            var failed = false;

            if (exitCode == -1)
            {
                failed = true;

                try
                {
                    _logger.LogWarning("Deleting extracted subtitle due to failure: {Path}", outputPath);
                    _fileSystem.DeleteFile(outputPath);
                }
                catch (FileNotFoundException)
                {
                }
                catch (IOException ex)
                {
                    _logger.LogError(ex, "Error deleting extracted subtitle {Path}", outputPath);
                }
            }
            else if (!File.Exists(outputPath) || _fileSystem.GetFileInfo(outputPath).Length == 0)
            {
                failed = true;

                try
                {
                    _logger.LogWarning("Deleting extracted subtitle due to failure: {Path}", outputPath);
                    _fileSystem.DeleteFile(outputPath);
                }
                catch (FileNotFoundException)
                {
                }
                catch (IOException ex)
                {
                    _logger.LogError(ex, "Error deleting extracted subtitle {Path}", outputPath);
                }
            }

            if (failed)
            {
                _logger.LogError("ffmpeg subtitle extraction failed for {InputPath} to {OutputPath}", inputPath, outputPath);

                throw new FfmpegException(
                    string.Format(CultureInfo.InvariantCulture, "ffmpeg subtitle extraction failed for {0} to {1}", inputPath, outputPath));
            }

            _logger.LogInformation("ffmpeg subtitle extraction completed for {InputPath} to {OutputPath}", inputPath, outputPath);

            if (string.Equals(outputCodec, "ass", StringComparison.OrdinalIgnoreCase))
            {
                await SetAssFont(outputPath, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Sets the ass font.
        /// </summary>
        /// <param name="file">The file.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests. The default value is <c>System.Threading.CancellationToken.None</c>.</param>
        /// <returns>Task.</returns>
        private async Task SetAssFont(string file, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Setting ass font within {File}", file);

            string text;
            Encoding encoding;

            using (var fileStream = AsyncFile.OpenRead(file))
            using (var reader = new StreamReader(fileStream, true))
            {
                encoding = reader.CurrentEncoding;

                text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }

            var newText = text.Replace(",Arial,", ",Arial Unicode MS,", StringComparison.Ordinal);

            if (!string.Equals(text, newText, StringComparison.Ordinal))
            {
                var fileStream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, IODefaults.FileStreamBufferSize, FileOptions.Asynchronous);
                await using (fileStream.ConfigureAwait(false))
                {
                    var writer = new StreamWriter(fileStream, encoding);
                    await using (writer.ConfigureAwait(false))
                    {
                        await writer.WriteAsync(newText.AsMemory(), cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        private string GetSubtitleCachePath(MediaSourceInfo mediaSource, int subtitleStreamIndex, string outputSubtitleExtension)
        {
            return _pathManager.GetSubtitlePath(mediaSource.Id, subtitleStreamIndex, outputSubtitleExtension);
        }

        /// <inheritdoc />
        public async Task<string> GetSubtitleFileCharacterSet(MediaStream subtitleStream, string language, MediaSourceInfo mediaSource, CancellationToken cancellationToken)
        {
            var subtitleCodec = subtitleStream.Codec;
            var path = subtitleStream.Path;

            if (path.EndsWith(".mks", StringComparison.OrdinalIgnoreCase))
            {
                path = GetSubtitleCachePath(mediaSource, subtitleStream.Index, "." + subtitleCodec);
                await ExtractTextSubtitle(mediaSource, subtitleStream, subtitleCodec, path, cancellationToken)
                    .ConfigureAwait(false);
            }

            var stream = await GetStream(path, mediaSource.Protocol, cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var result = await CharsetDetector.DetectFromStreamAsync(stream, cancellationToken).ConfigureAwait(false);
                var charset = result.Detected?.EncodingName ?? string.Empty;

                // UTF16 is automatically converted to UTF8 by FFmpeg, do not specify a character encoding
                if ((path.EndsWith(".ass", StringComparison.Ordinal) || path.EndsWith(".ssa", StringComparison.Ordinal) || path.EndsWith(".srt", StringComparison.Ordinal))
                    && (string.Equals(charset, "utf-16le", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(charset, "utf-16be", StringComparison.OrdinalIgnoreCase)))
                {
                    charset = string.Empty;
                }

                _logger.LogDebug("charset {0} detected for {Path}", charset, path);

                return charset;
            }
        }

        private async Task<Stream> GetStream(string path, MediaProtocol protocol, CancellationToken cancellationToken)
        {
            switch (protocol)
            {
                case MediaProtocol.Http:
                    {
                        using var response = await _httpClientFactory.CreateClient(NamedClient.Default)
                            .GetAsync(new Uri(path), cancellationToken)
                            .ConfigureAwait(false);
                        return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    }

                case MediaProtocol.File:
                    return AsyncFile.OpenRead(path);
                default:
                    throw new ArgumentOutOfRangeException(nameof(protocol));
            }
        }

        public async Task<string> GetSubtitleFilePath(MediaStream subtitleStream, MediaSourceInfo mediaSource, CancellationToken cancellationToken)
        {
            var info = await GetReadableFile(mediaSource, subtitleStream, cancellationToken)
                .ConfigureAwait(false);
            return info.Path;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _semaphoreLocks.Dispose();
        }

#pragma warning disable CA1034 // Nested types should not be visible
        // Only public for the unit tests
        public readonly record struct SubtitleInfo
        {
            public string Path { get; init; }

            public MediaProtocol Protocol { get; init; }

            public string Format { get; init; }

            public bool IsExternal { get; init; }
        }
    }
}
