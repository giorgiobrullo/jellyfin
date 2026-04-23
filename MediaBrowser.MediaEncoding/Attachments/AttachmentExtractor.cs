using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AsyncKeyedLock;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.MediaEncoding.Encoder;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.MediaEncoding.Attachments
{
    /// <inheritdoc cref="IAttachmentExtractor"/>
    public sealed class AttachmentExtractor : IAttachmentExtractor, IDisposable
    {
        private readonly ILogger<AttachmentExtractor> _logger;
        private readonly IFileSystem _fileSystem;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly IPathManager _pathManager;

        private readonly AsyncKeyedLocker<string> _semaphoreLocks = new(o =>
        {
            o.PoolSize = 20;
            o.PoolInitialFill = 1;
        });

        /// <summary>
        /// Initializes a new instance of the <see cref="AttachmentExtractor"/> class.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger{AttachmentExtractor}"/>.</param>
        /// <param name="fileSystem">The <see cref="IFileSystem"/>.</param>
        /// <param name="mediaEncoder">The <see cref="IMediaEncoder"/>.</param>
        /// <param name="mediaSourceManager">The <see cref="IMediaSourceManager"/>.</param>
        /// <param name="pathManager">The <see cref="IPathManager"/>.</param>
        public AttachmentExtractor(
            ILogger<AttachmentExtractor> logger,
            IFileSystem fileSystem,
            IMediaEncoder mediaEncoder,
            IMediaSourceManager mediaSourceManager,
            IPathManager pathManager)
        {
            _logger = logger;
            _fileSystem = fileSystem;
            _mediaEncoder = mediaEncoder;
            _mediaSourceManager = mediaSourceManager;
            _pathManager = pathManager;
        }

        /// <inheritdoc />
        public async Task<(MediaAttachment Attachment, Stream Stream)> GetAttachment(BaseItem item, string mediaSourceId, int attachmentStreamIndex, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(item);

            if (string.IsNullOrWhiteSpace(mediaSourceId))
            {
                throw new ArgumentNullException(nameof(mediaSourceId));
            }

            var mediaSources = await _mediaSourceManager.GetPlaybackMediaSources(item, null, true, false, cancellationToken).ConfigureAwait(false);
            var mediaSource = mediaSources
                .FirstOrDefault(i => string.Equals(i.Id, mediaSourceId, StringComparison.OrdinalIgnoreCase));
            if (mediaSource is null)
            {
                throw new ResourceNotFoundException($"MediaSource {mediaSourceId} not found");
            }

            var mediaAttachment = mediaSource.MediaAttachments
                .FirstOrDefault(i => i.Index == attachmentStreamIndex);
            if (mediaAttachment is null)
            {
                throw new ResourceNotFoundException($"MediaSource {mediaSourceId} has no attachment with stream index {attachmentStreamIndex}");
            }

            if (string.Equals(mediaAttachment.Codec, "mjpeg", StringComparison.OrdinalIgnoreCase))
            {
                throw new ResourceNotFoundException($"Attachment with stream index {attachmentStreamIndex} can't be extracted for MediaSource {mediaSourceId}");
            }

            var attachmentStream = await GetAttachmentStream(mediaSource, mediaAttachment, cancellationToken)
                    .ConfigureAwait(false);

            return (mediaAttachment, attachmentStream);
        }

        /// <inheritdoc />
        public async Task ExtractAllAttachments(
            string inputFile,
            MediaSourceInfo mediaSource,
            CancellationToken cancellationToken)
        {
            var shouldExtractOneByOne = mediaSource.MediaAttachments.Any(a => !string.IsNullOrEmpty(a.FileName)
                                                                              && (a.FileName.Contains('/', StringComparison.OrdinalIgnoreCase) || a.FileName.Contains('\\', StringComparison.OrdinalIgnoreCase)));
            if (shouldExtractOneByOne && !inputFile.EndsWith(".mks", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var attachment in mediaSource.MediaAttachments)
                {
                    if (!string.Equals(attachment.Codec, "mjpeg", StringComparison.OrdinalIgnoreCase))
                    {
                        await ExtractAttachment(inputFile, mediaSource, attachment, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                await ExtractAllAttachmentsInternal(
                    inputFile,
                    mediaSource,
                    false,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task ExtractAllAttachmentsInternal(
            string inputFile,
            MediaSourceInfo mediaSource,
            bool isExternal,
            CancellationToken cancellationToken)
        {
            var inputPath = _mediaEncoder.GetInputArgument(inputFile, mediaSource);

            ArgumentException.ThrowIfNullOrEmpty(inputPath);

            var outputFolder = _pathManager.GetAttachmentFolderPath(mediaSource.Id);
            using (await _semaphoreLocks.LockAsync(outputFolder, cancellationToken).ConfigureAwait(false))
            {
                var directory = Directory.CreateDirectory(outputFolder);
                var fileNames = directory.GetFiles("*", SearchOption.TopDirectoryOnly).Select(f => f.Name).ToHashSet();
                var missingFiles = mediaSource.MediaAttachments.Where(a => a.FileName is not null && !fileNames.Contains(a.FileName) && !string.Equals(a.Codec, "mjpeg", StringComparison.OrdinalIgnoreCase));
                if (!missingFiles.Any())
                {
                    // Skip extraction if all files already exist
                    return;
                }

                var processArgs = string.Format(
                    CultureInfo.InvariantCulture,
                    "-dump_attachment:t \"\" -y {0} -i {1} -t 0 -f null null",
                    inputPath.EndsWith(".concat\"", StringComparison.OrdinalIgnoreCase) ? "-f concat -safe 0" : string.Empty,
                    inputPath);

                int exitCode;

                using (var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            Arguments = processArgs,
                            FileName = _mediaEncoder.EncoderPath,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WindowStyle = ProcessWindowStyle.Hidden,
                            WorkingDirectory = outputFolder,
                            ErrorDialog = false
                        },
                        EnableRaisingEvents = true
                    })
                {
                    _logger.LogInformation("{File} {Arguments}", process.StartInfo.FileName, process.StartInfo.Arguments);

                    process.Start();

                    try
                    {
                        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                        exitCode = process.ExitCode;
                    }
                    catch (OperationCanceledException)
                    {
                        process.Kill(true);
                        exitCode = -1;
                    }
                }

                var failed = false;

                if (exitCode != 0)
                {
                    if (isExternal && exitCode == 1)
                    {
                        // ffmpeg returns exitCode 1 because there is no video or audio stream
                        // this can be ignored
                    }
                    else
                    {
                        failed = true;

                        _logger.LogWarning("Deleting extracted attachments {Path} due to failure: {ExitCode}", outputFolder, exitCode);
                        try
                        {
                            Directory.Delete(outputFolder);
                        }
                        catch (IOException ex)
                        {
                            _logger.LogError(ex, "Error deleting extracted attachments {Path}", outputFolder);
                        }
                    }
                }
                else if (!Directory.Exists(outputFolder))
                {
                    failed = true;
                }

                if (failed)
                {
                    _logger.LogError("ffmpeg attachment extraction failed for {InputPath} to {OutputPath}", inputPath, outputFolder);

                    throw new InvalidOperationException(
                        string.Format(CultureInfo.InvariantCulture, "ffmpeg attachment extraction failed for {0} to {1}", inputPath, outputFolder));
                }

                _logger.LogInformation("ffmpeg attachment extraction completed for {InputPath} to {OutputPath}", inputPath, outputFolder);
            }
        }

        private async Task<Stream> GetAttachmentStream(
            MediaSourceInfo mediaSource,
            MediaAttachment mediaAttachment,
            CancellationToken cancellationToken)
        {
            var attachmentPath = await ExtractAttachment(mediaSource.Path, mediaSource, mediaAttachment, cancellationToken)
                .ConfigureAwait(false);
            return AsyncFile.OpenRead(attachmentPath);
        }

        private async Task<string> ExtractAttachment(
            string inputFile,
            MediaSourceInfo mediaSource,
            MediaAttachment mediaAttachment,
            CancellationToken cancellationToken)
        {
            var attachmentPath = _pathManager.GetAttachmentPath(mediaSource.Id, mediaAttachment.FileName ?? mediaAttachment.Index.ToString(CultureInfo.InvariantCulture));

            // Fast path: already on disk from a previous bulk/single extraction.
            if (File.Exists(attachmentPath))
            {
                return attachmentPath;
            }

            // Miss. ExtractAllAttachments dumps every attachment in a single
            // ffmpeg pass. That's cheap for local files, but critical for
            // HTTP/streaming sources where a per-attachment extraction would
            // re-open the remote stream and download through it once per font
            // (10+ fonts × tens of seconds each → minutes of subtitle stalling).
            await ExtractAllAttachments(inputFile, mediaSource, cancellationToken).ConfigureAwait(false);

            // ExtractAllAttachments takes its own folder lock and writes every
            // attachment in the source. If the requested file still isn't on
            // disk, fall back to the per-attachment extract under our own
            // lock — the bulk pass may have skipped an attachment we need.
            using (await _semaphoreLocks.LockAsync(_pathManager.GetAttachmentFolderPath(mediaSource.Id), cancellationToken).ConfigureAwait(false))
            {
                if (!File.Exists(attachmentPath))
                {
                    await ExtractAttachmentInternal(
                        _mediaEncoder.GetInputArgument(inputFile, mediaSource),
                        mediaAttachment.Index,
                        attachmentPath,
                        cancellationToken).ConfigureAwait(false);
                }

                return attachmentPath;
            }
        }

        private async Task ExtractAttachmentInternal(
            string inputPath,
            int attachmentStreamIndex,
            string outputPath,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrEmpty(inputPath);

            ArgumentException.ThrowIfNullOrEmpty(outputPath);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? throw new ArgumentException("Path can't be a root directory.", nameof(outputPath)));

            var processArgs = string.Format(
                CultureInfo.InvariantCulture,
                "-dump_attachment:{1} \"{2}\" -i {0} -t 0 -f null null",
                inputPath,
                attachmentStreamIndex,
                EncodingUtils.NormalizePath(outputPath));

            int exitCode;

            using (var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        Arguments = processArgs,
                        FileName = _mediaEncoder.EncoderPath,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        ErrorDialog = false
                    },
                    EnableRaisingEvents = true
                })
            {
                _logger.LogInformation("{File} {Arguments}", process.StartInfo.FileName, process.StartInfo.Arguments);

                process.Start();

                try
                {
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                    exitCode = process.ExitCode;
                }
                catch (OperationCanceledException)
                {
                    process.Kill(true);
                    exitCode = -1;
                }
            }

            var failed = false;

            if (exitCode != 0)
            {
                failed = true;

                _logger.LogWarning("Deleting extracted attachment {Path} due to failure: {ExitCode}", outputPath, exitCode);
                try
                {
                    if (File.Exists(outputPath))
                    {
                        _fileSystem.DeleteFile(outputPath);
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogError(ex, "Error deleting extracted attachment {Path}", outputPath);
                }
            }
            else if (!File.Exists(outputPath))
            {
                failed = true;
            }

            if (failed)
            {
                _logger.LogError("ffmpeg attachment extraction failed for {InputPath} to {OutputPath}", inputPath, outputPath);

                throw new InvalidOperationException(
                    string.Format(CultureInfo.InvariantCulture, "ffmpeg attachment extraction failed for {0} to {1}", inputPath, outputPath));
            }

            _logger.LogInformation("ffmpeg attachment extraction completed for {InputPath} to {OutputPath}", inputPath, outputPath);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _semaphoreLocks.Dispose();
        }
    }
}
