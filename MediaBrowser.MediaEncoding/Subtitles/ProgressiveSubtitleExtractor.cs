#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.MediaEncoding.Subtitles
{
    /// <summary>
    /// Streams the events of an embedded ASS/SSA track while ffmpeg reads the source.
    /// </summary>
    /// <remarks>
    /// The events of a Matroska text track are spread over the whole file, so a full extraction of a
    /// remote file has to download all of it before the browser shows a single line (40-60 s for a
    /// 2-3 GB episode at our bandwidth). Reading from the playback position with the ASS muxer told to
    /// write events as they come (ignore_readorder) gives the first lines in about two seconds and
    /// then runs far ahead of playback, so the web player can start with what is there and pick up
    /// the rest as it arrives.
    /// </remarks>
    public sealed class ProgressiveSubtitleExtractor : IDisposable
    {
        private const int MaxSessions = 6;

        // A session is reused for a later start as long as it began before it and has not fallen
        // behind it by more than this (the client asks a few seconds before the playback position).
        private const double ReuseWindowSeconds = 90;

        private static readonly TimeSpan _idleTimeout = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan _completedRetention = TimeSpan.FromMinutes(15);

        private readonly IMediaEncoder _mediaEncoder;
        private readonly ILogger<ProgressiveSubtitleExtractor> _logger;
        private readonly Lock _lock = new();
        private readonly List<Session> _sessions = new();
        private readonly Timer _reaper;
        private bool _disposed;

        public ProgressiveSubtitleExtractor(IMediaEncoder mediaEncoder, ILogger<ProgressiveSubtitleExtractor> logger)
        {
            _mediaEncoder = mediaEncoder;
            _logger = logger;
            _reaper = new Timer(_ => Reap(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        /// <summary>
        /// Returns the events found so far, waiting up to <paramref name="wait"/> for new ones.
        /// </summary>
        /// <param name="mediaSource">The media source holding the track.</param>
        /// <param name="streamIndex">The subtitle stream index (also the ffmpeg stream index).</param>
        /// <param name="startSeconds">Where to start reading when a new session is needed.</param>
        /// <param name="sessionId">The session a client is following, if any.</param>
        /// <param name="since">The number of events the client already has from that session.</param>
        /// <param name="wait">How long to wait for events past <paramref name="since"/>.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The snapshot.</returns>
        public async Task<ProgressiveSubtitleSnapshot> ReadAsync(
            MediaSourceInfo mediaSource,
            int streamIndex,
            double startSeconds,
            string? sessionId,
            int since,
            TimeSpan wait,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(mediaSource);

            var session = GetOrStartSession(mediaSource, streamIndex, Math.Max(0, startSeconds), sessionId);
            since = Math.Max(0, since);

            var deadline = DateTime.UtcNow + wait;
            while (true)
            {
                Task changed;
                lock (session.Sync)
                {
                    session.LastAccess = DateTime.UtcNow;
                    // The header alone is not worth a reply: the first events follow it within a
                    // fraction of a second, and a client starting with none would render nothing.
                    if (session.Events.Count > since || session.Finished)
                    {
                        return session.Snapshot(since);
                    }

                    changed = session.Changed.Task;
                }

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    lock (session.Sync)
                    {
                        return session.Snapshot(since);
                    }
                }

                await Task.WhenAny(changed, Task.Delay(remaining, cancellationToken)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        /// <summary>
        /// Parses the start time of a <c>Dialogue:</c> or <c>Comment:</c> line.
        /// </summary>
        /// <param name="line">The event line.</param>
        /// <param name="seconds">The start, in seconds.</param>
        /// <returns>Whether the line had a start time.</returns>
        internal static bool TryParseEventStart(string line, out double seconds)
        {
            seconds = 0;
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                return false;
            }

            // Dialogue: Layer,Start,End,...
            var fields = line.AsSpan(colon + 1);
            var firstComma = fields.IndexOf(',');
            if (firstComma < 0)
            {
                return false;
            }

            fields = fields[(firstComma + 1)..];
            var secondComma = fields.IndexOf(',');
            if (secondComma < 0)
            {
                return false;
            }

            var start = fields[..secondComma].Trim();
            var parts = start.ToString().Split(':');
            if (parts.Length != 3
                || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
                || !double.TryParse(parts[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var secs))
            {
                return false;
            }

            seconds = (hours * 3600) + (minutes * 60) + secs;
            return true;
        }

        /// <summary>
        /// Builds the ffmpeg arguments for one session.
        /// </summary>
        /// <param name="inputArgument">The quoted input, as built by the media encoder.</param>
        /// <param name="streamIndex">The subtitle stream index.</param>
        /// <param name="startSeconds">The start position.</param>
        /// <returns>The arguments.</returns>
        internal static string BuildArguments(string inputArgument, int streamIndex, double startSeconds)
        {
            // -ss before -i seeks through the container index, so only the part of the file from the
            // start position onwards is read; -copyts keeps the original event times.
            var seek = startSeconds > 0
                ? string.Create(CultureInfo.InvariantCulture, $"-ss {startSeconds:F3} ")
                : string.Empty;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"-nostdin -hide_banner -loglevel error {seek}-i {inputArgument} -map 0:{streamIndex} -an -vn -c:s copy -copyts -flush_packets 1 -f ass -ignore_readorder 1 pipe:1");
        }

        private Session GetOrStartSession(MediaSourceInfo mediaSource, int streamIndex, double startSeconds, string? sessionId)
        {
            lock (_lock)
            {
                Session? session = null;
                if (!string.IsNullOrEmpty(sessionId))
                {
                    session = _sessions.FirstOrDefault(s => string.Equals(s.Id, sessionId, StringComparison.Ordinal));
                }

                session ??= _sessions.FirstOrDefault(s =>
                    string.Equals(s.Path, mediaSource.Path, StringComparison.Ordinal)
                    && s.StreamIndex == streamIndex
                    && !s.Failed
                    && s.StartSeconds <= startSeconds + 1
                    && (s.Finished || startSeconds - s.StartSeconds <= ReuseWindowSeconds || s.CoveredSeconds >= startSeconds));

                if (session is not null)
                {
                    return session;
                }

                while (_sessions.Count >= MaxSessions)
                {
                    var oldest = _sessions.MinBy(s => s.LastAccess)!;
                    _sessions.Remove(oldest);
                    oldest.Stop();
                }

                session = new Session(mediaSource.Path, streamIndex, startSeconds);
                _sessions.Add(session);
                Start(session, mediaSource);
                return session;
            }
        }

        private void Start(Session session, MediaSourceInfo mediaSource)
        {
            var arguments = BuildArguments(_mediaEncoder.GetInputArgument(mediaSource.Path, mediaSource), session.StreamIndex, session.StartSeconds);
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _mediaEncoder.EncoderPath,
                    Arguments = arguments,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    ErrorDialog = false
                },
                EnableRaisingEvents = true
            };

            _logger.LogInformation(
                "Progressive subtitle extraction of track {Index} from {Start:F1}s: {Path} {Arguments}",
                session.StreamIndex,
                session.StartSeconds,
                _mediaEncoder.EncoderPath,
                arguments);

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not start progressive subtitle extraction");
                lock (session.Sync)
                {
                    session.Failed = true;
                    session.Finished = true;
                    session.Signal();
                }

                process.Dispose();
                return;
            }

            session.Process = process;
            _ = DrainErrorAsync(process, session);
            _ = PumpAsync(process, session);
        }

        private async Task PumpAsync(Process process, Session session)
        {
            var started = Stopwatch.StartNew();
            var header = new StringBuilder();
            var inEvents = false;
            try
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    var isEvent = inEvents
                        && (line.StartsWith("Dialogue:", StringComparison.Ordinal) || line.StartsWith("Comment:", StringComparison.Ordinal));
                    lock (session.Sync)
                    {
                        if (isEvent)
                        {
                            session.Header ??= header.ToString();
                            session.Events.Add(line);
                            if (TryParseEventStart(line, out var start) && start > session.CoveredSeconds)
                            {
                                session.CoveredSeconds = start;
                            }

                            session.Signal();
                        }
                        else if (session.Header is null)
                        {
                            header.Append(line).Append('\n');
                            if (line.StartsWith("[Events]", StringComparison.Ordinal))
                            {
                                inEvents = true;
                            }
                        }
                    }
                }

                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.IO.IOException)
            {
                // The session was stopped while reading.
            }

            int exitCode;
            try
            {
                exitCode = process.HasExited ? process.ExitCode : -1;
            }
            catch (InvalidOperationException)
            {
                exitCode = -1;
            }

            lock (session.Sync)
            {
                session.Header ??= header.ToString();
                session.Failed = session.Events.Count == 0 && exitCode != 0 && !session.Stopped;
                session.Finished = true;
                session.Signal();
                _logger.LogInformation(
                    "Progressive subtitle extraction of track {Index} from {Start:F1}s ended after {Time}ms: {Count} events, exit code {ExitCode}{Stopped}",
                    session.StreamIndex,
                    session.StartSeconds,
                    started.ElapsedMilliseconds,
                    session.Events.Count,
                    exitCode,
                    session.Stopped ? " (stopped)" : string.Empty);
            }
        }

        private async Task DrainErrorAsync(Process process, Session session)
        {
            try
            {
                var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(error) && !session.Stopped)
                {
                    _logger.LogWarning("Progressive subtitle extraction of track {Index}: {Error}", session.StreamIndex, error.Trim());
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.IO.IOException)
            {
                // The session was stopped while reading.
            }
        }

        private void Reap()
        {
            List<Session> stale;
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                stale = _sessions
                    .Where(s => now - s.LastAccess > (s.Finished ? _completedRetention : _idleTimeout))
                    .ToList();
                foreach (var session in stale)
                {
                    _sessions.Remove(session);
                }
            }

            foreach (var session in stale)
            {
                session.Stop();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _reaper.Dispose();
            lock (_lock)
            {
                foreach (var session in _sessions)
                {
                    session.Stop();
                }

                _sessions.Clear();
            }
        }

        private sealed class Session
        {
            public Session(string path, int streamIndex, double startSeconds)
            {
                Id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                Path = path;
                StreamIndex = streamIndex;
                StartSeconds = startSeconds;
                LastAccess = DateTime.UtcNow;
            }

            public Lock Sync { get; } = new();

            public string Id { get; }

            public string Path { get; }

            public int StreamIndex { get; }

            public double StartSeconds { get; }

            public Process? Process { get; set; }

            public string? Header { get; set; }

            public List<string> Events { get; } = new();

            public double CoveredSeconds { get; set; }

            public bool Finished { get; set; }

            public bool Failed { get; set; }

            public bool Stopped { get; private set; }

            public DateTime LastAccess { get; set; }

            public TaskCompletionSource Changed { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void Signal()
            {
                var changed = Changed;
                Changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                changed.TrySetResult();
            }

            public ProgressiveSubtitleSnapshot Snapshot(int since)
            {
                var from = Math.Min(since, Events.Count);
                return new ProgressiveSubtitleSnapshot(
                    Id,
                    StartSeconds,
                    since == 0 ? Header : null,
                    Events.GetRange(from, Events.Count - from),
                    Events.Count,
                    Finished && !Failed,
                    Failed,
                    CoveredSeconds);
            }

            public void Stop()
            {
                Stopped = true;
                try
                {
                    if (Process is { HasExited: false })
                    {
                        Process.Kill(true);
                    }
                }
                catch (InvalidOperationException)
                {
                    // Already gone.
                }

                Process?.Dispose();
            }
        }
    }

    /// <summary>
    /// What a progressive subtitle extraction has found so far.
    /// </summary>
    /// <param name="SessionId">The session to keep following.</param>
    /// <param name="StartSeconds">Where the session started reading.</param>
    /// <param name="Header">The script header up to the [Events] format line, on the first read only.</param>
    /// <param name="Events">The event lines past the ones the client already has.</param>
    /// <param name="Next">The value to pass as <c>since</c> next time.</param>
    /// <param name="Complete">Whether the session read to the end of the file.</param>
    /// <param name="Failed">Whether ffmpeg failed before producing anything.</param>
    /// <param name="CoveredSeconds">The latest event start seen so far.</param>
    public sealed record ProgressiveSubtitleSnapshot(
        string SessionId,
        double StartSeconds,
        string? Header,
        IReadOnlyList<string> Events,
        int Next,
        bool Complete,
        bool Failed,
        double CoveredSeconds);
}
