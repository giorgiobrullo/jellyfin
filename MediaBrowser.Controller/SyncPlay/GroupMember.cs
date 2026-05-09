#nullable disable

using System;
using MediaBrowser.Controller.Session;

namespace MediaBrowser.Controller.SyncPlay
{
    /// <summary>
    /// Class GroupMember.
    /// </summary>
    public class GroupMember
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="GroupMember"/> class.
        /// </summary>
        /// <param name="session">The session.</param>
        public GroupMember(SessionInfo session)
        {
            SessionId = session.Id;
            UserId = session.UserId;
            UserName = session.UserName;
        }

        /// <summary>
        /// Gets the identifier of the session.
        /// </summary>
        /// <value>The session identifier.</value>
        public string SessionId { get; }

        /// <summary>
        /// Gets the identifier of the user.
        /// </summary>
        /// <value>The user identifier.</value>
        public Guid UserId { get; }

        /// <summary>
        /// Gets the username.
        /// </summary>
        /// <value>The username.</value>
        public string UserName { get; }

        /// <summary>
        /// Gets or sets the ping, in milliseconds.
        /// </summary>
        /// <value>The ping.</value>
        public long Ping { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this member is buffering.
        /// </summary>
        /// <value><c>true</c> if member is buffering; <c>false</c> otherwise.</value>
        public bool IsBuffering { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this member is following group playback.
        /// </summary>
        /// <value><c>true</c> to ignore member on group wait; <c>false</c> if they're following group playback.</value>
        public bool IgnoreGroupWait { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this member has acknowledged being in sync on the current playlist item.
        /// </summary>
        /// <remarks>
        /// Set to <c>true</c> when the session reports a Ready event whose PlaylistItemId matches
        /// the group's current item and whose position passes the offset tolerance check. Reset to
        /// <c>false</c> on session join and whenever the group's current item changes. Used to
        /// reject NextItem/PreviousItem requests from sessions that have not yet demonstrated
        /// loading the current media — for example, a client whose WebSocket reconnected near
        /// end-of-file and forwarded mpv's EOF event as a queue-advance request before its player
        /// actually loaded the new item.
        /// </remarks>
        public bool HasAcknowledgedCurrentItem { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the server has an outstanding Seek correction in flight to this member.
        /// </summary>
        /// <remarks>
        /// Set to <c>true</c> when the server emits a Seek command to this session and clears
        /// when the session reports a Ready whose position is within tolerance (i.e. the seek
        /// has landed). While in flight, additional position-mismatched Ready events from this
        /// session do NOT trigger another Seek — the previous one is presumed to still be
        /// applying. This is the standard inflight-command pattern and prevents the seek-storm
        /// behaviour where mpv reports intermediate positions during a seek and the server
        /// amplifies each report into a fresh corrective Seek, causing A/V desync.
        /// </remarks>
        public bool SeekInflight { get; set; }

        /// <summary>
        /// Gets or sets the UTC time at which the most recent corrective Seek command was issued to this member.
        /// </summary>
        /// <remarks>
        /// Combined with <see cref="SeekInflight"/> to enforce a safety timeout: if a previously
        /// issued Seek has not been confirmed within a few seconds it is presumed lost (network
        /// drop, client crash) and the inflight flag is treated as cleared so a fresh Seek can
        /// be emitted.
        /// </remarks>
        public DateTime SeekIssuedAt { get; set; }
    }
}
