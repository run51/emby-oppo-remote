using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.Oppo.Oppo;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Session;

namespace Emby.Oppo
{
    /// <summary>
    /// Controller of the "OPPO 205" session: receives Emby's commands (Play, Pause, Stop…),
    /// translates them for the OPPO, and reports the playback state back to Emby to keep "watched" and resume points.
    /// It sends no user-facing text (log messages only; webhook titles are the item names).
    /// </summary>
    public sealed class OppoSessionController : ISessionController, IDisposable
    {
        private readonly SessionInfo _session;
        private readonly ISessionManager _sessionManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly IJsonSerializer _json;
        private readonly ILogger _logger;
        private readonly string _serverAddress;

        private CancellationTokenSource _playback;
        private BaseItem _item;
        private string _mediaSourceId;
        private string _playSessionId;
        private long _lastPositionTicks;
        private bool _paused;
        private int? _audioIndex;      // Emby index of the requested audio track
        private int? _subtitleIndex;   // Emby index of the requested subtitles (-1 = none)
        private List<BaseItem> _parts = new List<BaseItem>();
        private long[] _partDurations = Array.Empty<long>();
        private int _partIndex;
        private int? _jumpTo;
        private CancellationTokenSource _currentPart;
        private DateTime _requestedAt;
        private string _requestKey;          // to recognise a request sent twice
        private int? _requestedPart;          // requested disc (0 = first), null = from the resume point
        private int _startPart;               // disc playback starts with
        private long _startInner;             // start position within that disc

        public OppoSessionController(SessionInfo session, ISessionManager sessionManager, ILibraryManager libraryManager,
            IItemRepository itemRepository, IJsonSerializer json, ILogger logger, string serverAddress)
        {
            _session = session;
            _sessionManager = sessionManager;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _json = json;
            _logger = logger;
            _serverAddress = serverAddress;
        }

        private static PluginConfiguration Config => Plugin.Instance.Configuration;
        private OppoClient Oppo => new OppoClient(Config.OppoAddress, _json, _logger);

        public bool IsSessionActive => true;
        public bool SupportsMediaControl => true;
        public int Priority => 100;

        public bool SupportsMessage(string name) =>
            string.Equals(name, "Play", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "Playstate", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "GeneralCommand", StringComparison.OrdinalIgnoreCase);

        public Task SendMessage<T>(string name, string messageId, T data, CancellationToken cancellationToken)
        {
            if (string.Equals(name, "Play", StringComparison.OrdinalIgnoreCase) && data is PlayRequest play)
                return StartPlayback(play);
            if (string.Equals(name, "Playstate", StringComparison.OrdinalIgnoreCase) && data is PlaystateRequest state)
                return HandlePlaystate(state);
            if (string.Equals(name, "GeneralCommand", StringComparison.OrdinalIgnoreCase) && data is GeneralCommand command)
                return HandleGeneralCommand(command);
            return Task.CompletedTask;
        }

        // ------------------------------------------------------------------ Playback

        private async Task StartPlayback(PlayRequest request)
        {
            if (request.ItemIds == null || request.ItemIds.Length == 0) return;
            var item = _libraryManager.GetItemById(request.ItemIds[0]);
            if (item == null) return;
            if (!string.IsNullOrEmpty(request.MediaSourceId))
                item = _libraryManager.GetItemById(item.InternalId.ToString(CultureInfo.InvariantCulture), request.MediaSourceId) ?? item;

            // Selected disc: either by number (StartIndex, 0 = first disc), or by starting a disc's own
            // item directly ("… - Disc2"), which is mapped back to its main item.
            int? requestedPart = request.StartIndex;
            if (item.ParentId > 0 && _libraryManager.GetItemById(item.ParentId) is Video owner && owner.SupportsAdditionalParts)
            {
                var ids = owner.GetAdditionalPartIds() ?? Array.Empty<long>();
                int position = Array.IndexOf(ids, item.InternalId);
                if (position >= 0)
                {
                    item = owner;
                    requestedPart = position + 1;
                }
            }

            // The Android app sometimes resends the same request a few seconds later:
            // do not restart a movie that is already starting.
            string key = string.Join("|", item.InternalId, requestedPart, (request.StartPositionTicks ?? 0) / TimeSpan.TicksPerMinute, request.MediaSourceId);
            var current = _item;
            if (current != null && key == _requestKey && DateTime.UtcNow - _requestedAt < TimeSpan.FromSeconds(90))
            {
                _logger.Info("OPPO: duplicate play request for {0}, ignored (playback already in progress)", item.Name);
                return;
            }

            await StopMonitoring(reportStopped: true).ConfigureAwait(false);
            _requestedAt = DateTime.UtcNow;
            _requestKey = key;
            _requestedPart = requestedPart;

            var cts = new CancellationTokenSource();
            _playback = cts;
            _item = item;
            // Emby media source ID ("mediasource_<id>"): Emby derives from it the track list sent to
            // the app, which only shows the audio / subtitle button when it has one.
            _mediaSourceId = string.IsNullOrEmpty(request.MediaSourceId) ? item.GetDefaultMediaSourceId() : request.MediaSourceId;
            _playSessionId = Guid.NewGuid().ToString("N");
            _lastPositionTicks = request.StartPositionTicks ?? 0;
            _paused = false;
            _audioIndex = request.AudioStreamIndex;
            _subtitleIndex = request.SubtitleStreamIndex;
            _logger.Info("OPPO: play request {0}: audio {1}, subtitles {2}, resume at {3}", item.Name,
                request.AudioStreamIndex?.ToString(CultureInfo.InvariantCulture) ?? "unspecified",
                request.SubtitleStreamIndex?.ToString(CultureInfo.InvariantCulture) ?? "unspecified",
                TimeSpan.FromTicks(request.StartPositionTicks ?? 0));

            // Session attached to the user who starts the movie (otherwise Emby records neither resume point nor "watched").
            OppoEntryPoint.Current?.AttachUser(request.ControllingUserId);

            // Reported right away: the app shows the playback page immediately instead of waiting
            // (and resending the request) while the share is mounted and the disc loads.
            await ReportStart(item).ConfigureAwait(false);
            _ = TvPower.TurnOn(_logger);   // TV turned on as soon as requested (if a remote is configured)
            _ = Task.Run(() => RunPlayback(item, request, cts.Token));
        }

        private enum PartEnd { Natural, Jump, Stopped, Failed }

        /// <summary>
        /// Plays an item, possibly in several parts (e.g. a concert on 2 to 4 discs named
        /// "… - Disc1 / Disc2"). Parts are chained: when a disc ends, the next one is started.
        /// ("Next" / "Previous" are used for chapters.)
        /// </summary>
        private async Task RunPlayback(BaseItem item, PlayRequest request, CancellationToken ct)
        {
            var oppo = Oppo;
            string title = item.Name;
            var parts = new List<BaseItem> { item };
            if (item is Video video && video.SupportsAdditionalParts)
            {
                foreach (var id in video.GetAdditionalPartIds() ?? Array.Empty<long>())
                {
                    var part = _libraryManager.GetItemById(id);
                    if (part != null) parts.Add(part);
                }
            }
            _parts = parts;
            _partDurations = new long[parts.Count];
            if (parts.Count > 1) _logger.Info("OPPO: {0} has {1} discs, they will be played in sequence", title, parts.Count);

            // Starting disc: the requested one, otherwise the one containing the resume point (from each
            // disc's duration; not possible if a duration is missing, as on DVDs: start from the first).
            int startPart = 0;
            long startInner = request.StartPositionTicks ?? 0;
            if (parts.Count > 1)
            {
                if (_requestedPart.HasValue)
                {
                    startPart = Math.Max(0, Math.Min(parts.Count - 1, _requestedPart.Value));
                }
                else if (startInner > TimeSpan.TicksPerMinute)
                {
                    long offset = 0;
                    for (int i = 0; i < parts.Count - 1; i++)
                    {
                        long duration = parts[i].RunTimeTicks ?? 0;
                        if (duration <= 0 || startInner < offset + duration) break;
                        offset += duration;
                        startPart = i + 1;
                    }
                    startInner -= offset;
                }
                if (startPart > 0)
                    _logger.Info("OPPO: starting at disc {0}/{1}{2}", startPart + 1, parts.Count,
                        startInner > TimeSpan.TicksPerMinute ? ", resuming at " + TimeSpan.FromTicks(startInner).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : "");
            }
            _startPart = startPart;
            _startInner = startInner;

            bool notified = false;   // "detected" webhook sent (the disc really started)
            bool finished = false;   // last disc played to the end
            try
            {
                int index = startPart;
                while (index >= 0 && index < parts.Count && !ct.IsCancellationRequested)
                {
                    _partIndex = index;
                    _jumpTo = null;
                    using (var partCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        _currentPart = partCts;
                        var end = await PlayPart(oppo, item, parts[index], index, request, () => notified, () => notified = true, partCts.Token, ct).ConfigureAwait(false);
                        _currentPart = null;
                        if (end == PartEnd.Failed || end == PartEnd.Stopped || ct.IsCancellationRequested) break;
                        if (end == PartEnd.Jump && _jumpTo.HasValue) { index = _jumpTo.Value; continue; }
                        index++;
                        if (index >= parts.Count) finished = true;
                        if (index < parts.Count) _logger.Info("OPPO: disc {0}/{1} finished, starting the next disc", index, parts.Count);
                    }
                }
                _logger.Info("OPPO: session ended for {0}", title);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.ErrorException("OPPO: error while playing " + title, ex);
            }
            finally
            {
                if (ReferenceEquals(_item, item))
                {
                    _item = null;
                    _playback = null;
                    await ReportStopped(item).ConfigureAwait(false);
                    if (notified) await NotifyHomeAssistant("stopped", title).ConfigureAwait(false);
                    SleepTimer.PlaybackEnded(finished);
                }
            }
        }

        /// <summary>Mounts, starts and tracks one disc. Returns why it ended.</summary>
        private async Task<PartEnd> PlayPart(OppoClient oppo, BaseItem item, BaseItem part, int index, PlayRequest request,
            Func<bool> wasStarted, Action markStarted, CancellationToken partCt, CancellationToken sessionCt)
        {
            string title = item.Name;
            string label = _parts.Count > 1 ? " (disc " + (index + 1) + "/" + _parts.Count + ")" : "";   // log only
            try
            {
                var target = MapPath(part.Path);
                if (target == null)
                {
                    _logger.Error("OPPO: no share configured for {0} (see the OPPO page of the dashboard)", part.Path);
                    return PartEnd.Failed;
                }

                var map = target.Value.Mapping;
                bool smb = string.Equals(map.Protocol, "smb", StringComparison.OrdinalIgnoreCase);
                _logger.Info("OPPO: playing {0}{1} ({2} {3}:/{4}, file {5})", title, label, smb ? "SMB" : "NFS", map.Server, map.Share, target.Value.Relative);
                await MakeReady(oppo, partCt).ConfigureAwait(false);
                await oppo.SignIn(_serverAddress, partCt).ConfigureAwait(false);
                var mountTimeout = TimeSpan.FromSeconds(Config.StartTimeoutSeconds);
                Func<Task<bool>> mount = () => smb
                    ? oppo.MountSmb(map.Server, map.Share, map.Username, map.Password, mountTimeout, partCt)
                    : oppo.MountNfs(map.Server, map.Share, mountTimeout, partCt);
                bool mountedOk = await mount().ConfigureAwait(false);
                if (!mountedOk)
                {
                    // Typically "bd_is_playing": a disc is still running. Close it and try again.
                    _logger.Info("OPPO: mount refused, stopping current playback and retrying ({0})", map.Server);
                    await MakeReady(oppo, partCt, force: true).ConfigureAwait(false);
                    mountedOk = await mount().ConfigureAwait(false);
                }
                if (!mountedOk)
                {
                    _logger.Error("OPPO: mount failed {0} {1}:/{2}", smb ? "SMB" : "NFS", map.Server, map.Share);
                    return PartEnd.Failed;
                }

                string mounted = (smb ? "/mnt/cifs1/" : "/mnt/nfs1/") + target.Value.Relative;
                bool isFolder = Directory.Exists(part.Path);
                bool accepted = isFolder
                    ? await oppo.PlayBlurayFolder(mounted, partCt).ConfigureAwait(false)
                    : await oppo.PlayFile(mounted, map.Server, partCt).ConfigureAwait(false);
                if (!accepted) _logger.Info("OPPO: play command not confirmed, waiting for the player state ({0})", title);

                // Wait for playback: a disc first goes through screens (warnings, menu…) during
                // which the OPPO reports no video. Rely on the IP state (#QPL).
                var deadline = DateTime.UtcNow.AddSeconds(Config.StartTimeoutSeconds);
                string state;
                do
                {
                    await Task.Delay(1000, partCt).ConfigureAwait(false);
                    state = await oppo.QueryPlayback(partCt).ConfigureAwait(false);
                } while (!IsSessionState(state) && DateTime.UtcNow < deadline);

                if (!IsSessionState(state))
                {
                    _logger.Error("OPPO: playback of {0}{1} did not start in time (state \"{2}\")", title, label, state);
                    return PartEnd.Failed;
                }

                _logger.Info("OPPO: {0}{1} started (state \"{2}\")", title, label, state);
                if (!wasStarted())
                {
                    markStarted();
                    await NotifyHomeAssistant("detected", title).ConfigureAwait(false);
                }

                // Polled every 5 s. Tracks and resume point are applied when the movie itself starts
                // (a long title playing), not during menus or trailers.
                // Capped at 1 h: a wrong duration in Emby (e.g. 25 h) must not prevent recognising the movie.
                long minimumMovieTicks = Math.Max(TimeSpan.FromMinutes(20).Ticks, Math.Min((part.RunTimeTicks ?? 0) / 2, TimeSpan.FromHours(1).Ticks));
                bool movieStarted = false, tracksApplied = false;
                long lastPos = 0, lastDur = 0;
                int outside = 0, tick = 0;
                string lastState = state;
                while (true)
                {
                    // Every 5 s; every 2 s while confirming a stop.
                    await Task.Delay(outside > 0 ? 2000 : 5000, partCt).ConfigureAwait(false);
                    state = await oppo.QueryPlayback(partCt).ConfigureAwait(false);
                    if (state != lastState)
                    {
                        _logger.Info("OPPO: state \"{0}\" → \"{1}\"", lastState, state);
                        lastState = state;
                    }

                    // End of a disc that returns to its menu after the movie (DVDs in particular).
                    bool playedToEnd = lastDur > 0 && lastPos >= lastDur * 95 / 100;
                    if (!IsSessionState(state) || (playedToEnd && (state == "DISC MENU" || state == "STOP")))
                    {
                        // Back to home or stopped: confirmed over ~5 s. The next disc is only started if
                        // this one was played to the end; otherwise it is a deliberate stop.
                        if (++outside < 3) continue;
                        bool finished = lastDur > 0 && lastPos >= lastDur * 90 / 100;
                        if (!finished && index + 1 < _parts.Count)
                            _logger.Info("OPPO: disc {0}/{1} stopped before the end, not chaining", index + 1, _parts.Count);
                        return finished ? PartEnd.Natural : PartEnd.Stopped;
                    }
                    outside = 0;
                    _paused = state == "PAUSE";

                    var status = await oppo.GetStatus(partCt).ConfigureAwait(false);
                    bool mainTitle = status.Duration.Ticks >= minimumMovieTicks;
                    if (mainTitle && state == "PLAY" && !tracksApplied)
                    {
                        tracksApplied = true;
                        _logger.Info("OPPO: main title playing{0} ({1}), applying tracks", label, status.Duration);
                        await ApplyTracks(oppo, part, partCt).ConfigureAwait(false);
                        if (index == _startPart && !movieStarted && _startInner > TimeSpan.TicksPerMinute)
                            await oppo.Seek(TimeSpan.FromTicks(_startInner), partCt).ConfigureAwait(false);
                        movieStarted = true;
                    }
                    if (!mainTitle || state == "DISC MENU") tracksApplied = false;
                    if (mainTitle)
                    {
                        lastPos = status.Position.Ticks;
                        lastDur = status.Duration.Ticks;
                        _partDurations[index] = lastDur;
                        // Overall position = duration of the previous discs + position within this disc.
                        _lastPositionTicks = PartOffset(index) + lastPos;
                    }
                    if (++tick % 2 == 0) await ReportProgress(item, status).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!sessionCt.IsCancellationRequested)
            {
                return _jumpTo.HasValue ? PartEnd.Jump : PartEnd.Stopped;
            }
        }

        /// <summary>
        /// Converts Emby indexes into positions in the OPPO menus. For an ISO analysed by the
        /// ISO Blu-ray plugin, Emby's track order follows the disc's.
        /// </summary>
        private async Task ApplyTracks(OppoClient oppo, BaseItem item, CancellationToken ct)
        {
            try
            {
                _logger.Info("OPPO: OPPO menus: audio {0} | subtitles {1}",
                    await oppo.DescribeMenu("getaudiomenulist", ct).ConfigureAwait(false),
                    await oppo.DescribeMenu("getsubtitlemenulist", ct).ConfigureAwait(false));
                if (_audioIndex.HasValue) await SelectAudio(oppo, item, _audioIndex.Value, ct).ConfigureAwait(false);
                if (_subtitleIndex.HasValue) await SelectSubtitle(oppo, item, _subtitleIndex.Value, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Info("OPPO: could not select tracks ({0})", ex.Message);
            }
        }

        private List<MediaBrowser.Model.Entities.MediaStream> Streams(BaseItem item, MediaStreamType type, CancellationToken ct) =>
            _itemRepository.GetMediaStreams(new MediaStreamQuery { ItemId = item.InternalId }, ct)
                           .Where(s => !s.IsExternal && s.Type == type).OrderBy(s => s.Index).ToList();

        /// <summary>Emby index → position in the OPPO audio list (same order as the disc).</summary>
        private async Task SelectAudio(OppoClient oppo, BaseItem item, int embyIndex, CancellationToken ct)
        {
            int pos = Streams(item, MediaStreamType.Audio, ct).Select(s => s.Index).ToList().IndexOf(embyIndex);
            if (pos < 0) return;
            // The OPPO numbers its audio tracks from 1.
            bool ok = await oppo.SelectAudio(pos + 1, ct).ConfigureAwait(false);
            _logger.Info("OPPO: Emby audio track {0} → OPPO #{1}: {2}", embyIndex, pos + 1, ok ? "OK" : "refused");
        }

        private async Task SelectSubtitle(OppoClient oppo, BaseItem item, int embyIndex, CancellationToken ct)
        {
            int pos = embyIndex < 0 ? -1 : Streams(item, MediaStreamType.Subtitle, ct).Select(s => s.Index).ToList().IndexOf(embyIndex);
            if (embyIndex >= 0 && pos < 0) return;
            // OPPO subtitle menu: 0 = "Off", then 1, 2…
            int oppoIndex = pos < 0 ? 0 : pos + 1;
            bool ok = await oppo.SelectSubtitle(oppoIndex, ct).ConfigureAwait(false);
            _logger.Info("OPPO: Emby subtitles {0} → OPPO #{1}: {2}", embyIndex, oppoIndex, ok ? "OK" : "refused");
        }

        /// <summary>
        /// Remote-control keys sent by the Emby app (directional pad, OK, back, info,
        /// menus) → OPPO IP protocol codes.
        /// </summary>
        internal static readonly Dictionary<string, string> RemoteKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MoveUp"] = "NUP",
            ["MoveDown"] = "NDN",
            ["MoveLeft"] = "NLT",
            ["MoveRight"] = "NRT",
            ["Select"] = "SEL",
            ["Back"] = "RET",
            ["GoHome"] = "HOM",
            ["ToggleOsd"] = "OSD",          // Info key
            ["ToggleStats"] = "OSD",
            ["ToggleContextMenu"] = "MNU",  // disc pop-up menu
            ["Guide"] = "TTL",              // disc main menu (Top Menu)
            ["PageUp"] = "PUP",
            ["PageDown"] = "PDN",
        };

        /// <summary>Track changes and remote-control keys from the Emby app.</summary>
        private async Task HandleGeneralCommand(GeneralCommand command)
        {
            var oppo = Oppo;
            if (RemoteKeys.TryGetValue(command.Name ?? "", out var code))
            {
                string answer = await oppo.Command(code, CancellationToken.None).ConfigureAwait(false);
                _logger.Info("OPPO: key {0} → #{1} ({2})", command.Name, code, answer);
                return;
            }

            var item = _item;
            var parts = _parts;
            int partIndex = _partIndex;
            if (item != null && parts.Count > 1 && partIndex > 0 && partIndex < parts.Count) item = parts[partIndex];
            if (item == null || command.Arguments == null || !command.Arguments.TryGetValue("Index", out var raw)
                || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                return;
            if (string.Equals(command.Name, "SetAudioStreamIndex", StringComparison.OrdinalIgnoreCase))
            {
                _audioIndex = index;
                await SelectAudio(oppo, item, index, CancellationToken.None).ConfigureAwait(false);
            }
            else if (string.Equals(command.Name, "SetSubtitleStreamIndex", StringComparison.OrdinalIgnoreCase))
            {
                _subtitleIndex = index;
                await SelectSubtitle(oppo, item, index, CancellationToken.None).ConfigureAwait(false);
            }
        }

        private async Task HandlePlaystate(PlaystateRequest request)
        {
            var oppo = Oppo;
            var ct = CancellationToken.None;
            switch (request.Command)
            {
                case PlaystateCommand.Stop:
                    await oppo.Stop(ct).ConfigureAwait(false);
                    await StopMonitoring(reportStopped: true).ConfigureAwait(false);
                    break;
                case PlaystateCommand.Pause:
                    await oppo.Pause(ct).ConfigureAwait(false);
                    _paused = true;
                    break;
                case PlaystateCommand.Unpause:
                    await oppo.Resume(ct).ConfigureAwait(false);
                    _paused = false;
                    break;
                case PlaystateCommand.PlayPause:
                    if (_paused) await oppo.Resume(ct).ConfigureAwait(false); else await oppo.Pause(ct).ConfigureAwait(false);
                    _paused = !_paused;
                    break;
                case PlaystateCommand.NextTrack:
                    await oppo.Command("NXT", ct).ConfigureAwait(false);   // next chapter
                    break;
                case PlaystateCommand.PreviousTrack:
                    await oppo.Command("PRE", ct).ConfigureAwait(false);   // previous chapter
                    break;
                case PlaystateCommand.FastForward:
                    await oppo.Command("FWD", ct).ConfigureAwait(false);   // each press speeds up
                    break;
                case PlaystateCommand.Rewind:
                    await oppo.Command("REV", ct).ConfigureAwait(false);
                    break;
                case PlaystateCommand.Seek:
                    if (request.SeekPositionTicks.HasValue)
                    {
                        // Overall position (all discs) → position within the current disc.
                        long inPart = Math.Max(0, request.SeekPositionTicks.Value - PartOffset(_partIndex));
                        await oppo.Seek(TimeSpan.FromTicks(inPart), ct).ConfigureAwait(false);
                        _lastPositionTicks = request.SeekPositionTicks.Value;
                    }
                    break;
                case PlaystateCommand.SeekRelative:
                    // The app's "skip back / forward N seconds" buttons.
                    if (request.SeekPositionTicks.HasValue)
                    {
                        var status = await oppo.GetStatus(ct).ConfigureAwait(false);
                        var target = status.Position + TimeSpan.FromTicks(request.SeekPositionTicks.Value);
                        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
                        if (status.Duration > TimeSpan.Zero && target > status.Duration) target = status.Duration - TimeSpan.FromSeconds(5);
                        string answer = await oppo.Seek(target, ct).ConfigureAwait(false);
                        _logger.Info("OPPO: skip {0} s → {1} ({2})", TimeSpan.FromTicks(request.SeekPositionTicks.Value).TotalSeconds, target, answer);
                    }
                    break;
            }
        }

        /// <summary>Duration of all discs (if each one is known), otherwise the item's.</summary>
        private long? TotalRunTime(BaseItem item)
        {
            long total = 0;
            for (int i = 0; i < _parts.Count; i++)
            {
                long duration = _partDurations.Length > i && _partDurations[i] > 0 ? _partDurations[i] : (_parts[i].RunTimeTicks ?? 0);
                if (duration <= 0) return item.RunTimeTicks;
                total += duration;
            }
            return total;
        }

        /// <summary>Total duration of the discs before disc <paramref name="index"/>.</summary>
        private long PartOffset(int index)
        {
            long offset = 0;
            for (int i = 0; i < index && i < _parts.Count; i++)
                offset += _partDurations.Length > i && _partDurations[i] > 0 ? _partDurations[i] : (_parts[i].RunTimeTicks ?? 0);
            return offset;
        }

        /// <summary>
        /// Prepares the OPPO before a mount: leaves the screen saver and closes a disc that is still
        /// open (otherwise the OPPO refuses the mount with "bd_is_playing").
        /// </summary>
        private async Task MakeReady(OppoClient oppo, CancellationToken ct, bool force = false)
        {
            string state = await oppo.QueryPlayback(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(state))
            {
                // In standby: power it on (requires "network standby" enabled on the OPPO).
                _logger.Info("OPPO: player in standby or unreachable, powering on ({0})", Config.OppoAddress);
                if (!await oppo.WakeAndWait(TimeSpan.FromSeconds(Math.Max(60, Config.StartTimeoutSeconds)), ct).ConfigureAwait(false))
                    _logger.Info("OPPO: no response after the power-on request ({0})", Config.OppoAddress);
                // The command port responds a little after the HTTP interface.
                for (int i = 0; i < 15 && string.IsNullOrEmpty(state); i++)
                {
                    await Task.Delay(2000, ct).ConfigureAwait(false);
                    state = await oppo.QueryPlayback(ct).ConfigureAwait(false);
                }
                if (!string.IsNullOrEmpty(state)) _logger.Info("OPPO: player powered on (state \"{0}\")", state);
            }
            else if (!await oppo.WaitForHttp(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false))
            {
                // On, but restarted: its HTTP interface stays closed until a control app announces itself
                // (2026-11-11 after the player was restarted: "Connection refused (192.168.2.195:436)").
                _logger.Info("OPPO: HTTP interface (port {0}) still closed", 436);
            }
            if (state == "SCREEN SAVER")
            {
                await oppo.Command("RET", ct).ConfigureAwait(false);
                await Task.Delay(1500, ct).ConfigureAwait(false);
                state = await oppo.QueryPlayback(ct).ConfigureAwait(false);
            }
            bool idle = state == "" || state == "HOME MENU" || state == "MEDIA CENTER" || state == "SCREEN SAVER" || state == "NO DISC" || state == "SETUP";
            if (idle && !force) return;

            _logger.Info("OPPO: player busy (state \"{0}\"), closing the current disc", state);
            await oppo.Stop(ct).ConfigureAwait(false);
            for (int i = 0; i < 4; i++)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                state = await oppo.QueryPlayback(ct).ConfigureAwait(false);
                if (state == "HOME MENU" || state == "MEDIA CENTER") return;
            }
            await oppo.Command("HOM", ct).ConfigureAwait(false);
            for (int i = 0; i < 8; i++)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                state = await oppo.QueryPlayback(ct).ConfigureAwait(false);
                if (state == "HOME MENU" || state == "MEDIA CENTER") return;
            }
            _logger.Info("OPPO: the player stayed in state \"{0}\"", state);
        }

        private async Task StopMonitoring(bool reportStopped)
        {
            var cts = _playback;
            _playback = null;
            if (cts == null) return;
            var item = _item;
            _item = null;
            cts.Cancel();
            if (reportStopped && item != null)
            {
                await ReportStopped(item).ConfigureAwait(false);
                await NotifyHomeAssistant("stopped", item.Name).ConfigureAwait(false);
            }
        }

        // ------------------------------------------------------------------ Emby

        private Task ReportStart(BaseItem item) => _sessionManager.OnPlaybackStart(new PlaybackStartInfo
        {
            ItemId = item.InternalId.ToString(CultureInfo.InvariantCulture),
            MediaSourceId = _mediaSourceId,
            PlaySessionId = _playSessionId,
            SessionId = _session.Id,
            PositionTicks = _lastPositionTicks,
            RunTimeTicks = item.RunTimeTicks,
            PlayMethod = PlayMethod.DirectPlay,
            CanSeek = true,
            // The app only shows "Previous / Next" if the play queue has an item before and after.
            // So position 2/3 is reported: these buttons are used for chapters.
            PlaylistIndex = 1,
            PlaylistLength = 3
        });

        private Task ReportProgress(BaseItem item, OppoStatus status) => _sessionManager.OnPlaybackProgress(new PlaybackProgressInfo
        {
            ItemId = item.InternalId.ToString(CultureInfo.InvariantCulture),
            MediaSourceId = _mediaSourceId,
            PlaySessionId = _playSessionId,
            SessionId = _session.Id,
            PositionTicks = _lastPositionTicks,
            RunTimeTicks = _parts.Count > 1 ? TotalRunTime(item) : (status.Duration > TimeSpan.Zero ? status.Duration.Ticks : item.RunTimeTicks),
            IsPaused = _paused,
            AudioStreamIndex = _audioIndex,
            SubtitleStreamIndex = _subtitleIndex,
            PlayMethod = PlayMethod.DirectPlay,
            CanSeek = true,
            // The app only shows "Previous / Next" if the play queue has an item before and after.
            // So position 2/3 is reported: these buttons are used for chapters.
            PlaylistIndex = 1,
            PlaylistLength = 3
        });

        private async Task ReportStopped(BaseItem item)
        {
            try
            {
                await _sessionManager.OnPlaybackStopped(new PlaybackStopInfo
                {
                    ItemId = item.InternalId.ToString(CultureInfo.InvariantCulture),
                    MediaSourceId = _mediaSourceId,
                    PlaySessionId = _playSessionId,
                    SessionId = _session.Id,
                    PositionTicks = _lastPositionTicks
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("OPPO: could not report the stop to Emby", ex);
            }
        }

        // ------------------------------------------------------------------ Helpers

        /// <summary>
        /// #QPL states in which the playback session is alive (movie, disc menus, loading, pause…).
        /// Home states (HOME MENU, MEDIA CENTER, SCREEN SAVER, NO DISC, SETUP) and no response
        /// mean playback has ended.
        /// </summary>
        private static bool IsSessionState(string state)
        {
            if (string.IsNullOrEmpty(state)) return false;
            switch (state)
            {
                case "HOME MENU":
                case "MEDIA CENTER":
                case "SCREEN SAVER":
                case "NO DISC":
                case "SETUP":
                case "STOP":
                    return false;
                default:
                    return true; // PLAY, PAUSE, DISC MENU, LOADING, STEP, FFWD, FREV, SFWD, SREV…
            }
        }

        /// <summary>Emby path → (share to mount, relative path within that share). The longest prefix wins.</summary>
        internal static (PathMapping Mapping, string Relative)? MapPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var mappings = (Config.Mappings ?? Array.Empty<PathMapping>())
                .Where(m => !string.IsNullOrWhiteSpace(m.EmbyPath) && !string.IsNullOrWhiteSpace(m.Server) && !string.IsNullOrWhiteSpace(m.Share))
                .OrderByDescending(m => m.EmbyPath.TrimEnd('/').Length);
            foreach (var m in mappings)
            {
                string prefix = m.EmbyPath.TrimEnd('/');
                if (path.StartsWith(prefix + "/", StringComparison.Ordinal))
                    return (m, path.Substring(prefix.Length + 1));
            }
            return null;
        }

        private async Task NotifyHomeAssistant(string evt, string title)
        {
            string url = Config.WebhookUrl;
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Timeout = 3000;
                var body = Encoding.UTF8.GetBytes(_json.SerializeToString(new Dictionary<string, string> { ["event"] = evt, ["title"] = title ?? "" }));
                using (var s = await request.GetRequestStreamAsync().ConfigureAwait(false))
                    await s.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
                using (await request.GetResponseAsync().ConfigureAwait(false)) { }
            }
            catch (Exception ex)
            {
                _logger.Info("OPPO: Home Assistant webhook unreachable ({0})", ex.Message);
            }
        }

        public void Dispose() => _playback?.Cancel();
    }
}
