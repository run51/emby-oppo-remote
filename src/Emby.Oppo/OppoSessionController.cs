using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.IsoBluray.Bluray;
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
        private bool _mainMovie;             // play the main movie as a file, without the disc menus
        private bool _rendererDeaf;          // the DLNA renderer took a link but never fetched it: skip DLNA for this part

        // "Movie without menus" asked by the web remote for the next play of an item (valid 60 s).
        private static long _pendingMainMovie;
        private static DateTime _pendingMainMovieUntil;

        /// <summary>Chapter marks (s) of the main movie playing as a file (no disc navigation), else null.</summary>
        internal static double[] MainMovieChapters { get; private set; }

        /// <summary>The next play request for this item plays its main movie directly, without the disc menus.</summary>
        internal static void RequestMainMovie(long itemId)
        {
            _pendingMainMovie = itemId;
            _pendingMainMovieUntil = DateTime.UtcNow.AddSeconds(60);
        }
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
            bool mainMovie = DateTime.UtcNow < _pendingMainMovieUntil && _pendingMainMovie == item.InternalId;
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
            mainMovie = mainMovie || (DateTime.UtcNow < _pendingMainMovieUntil && _pendingMainMovie == item.InternalId);
            _pendingMainMovie = 0;

            // The Android app sometimes resends the same request a few seconds later:
            // do not restart a movie that is already starting.
            string key = string.Join("|", item.InternalId, requestedPart, (request.StartPositionTicks ?? 0) / TimeSpan.TicksPerMinute, request.MediaSourceId, mainMovie);
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
            _mainMovie = mainMovie;

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
            _logger.Info("OPPO: play request {0}: audio {1}, subtitles {2}, resume at {3}{4}", item.Name,
                request.AudioStreamIndex?.ToString(CultureInfo.InvariantCulture) ?? "unspecified",
                request.SubtitleStreamIndex?.ToString(CultureInfo.InvariantCulture) ?? "unspecified",
                TimeSpan.FromTicks(request.StartPositionTicks ?? 0), mainMovie ? ", main movie without menus" : "");

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
                MainMovieChapters = null;
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
                // Main movie as a file (no menus), then the disc image over DLNA (oppo-dv-mkv mod), then the
                // network share: each one is the fallback of the previous one.
                MainMovieChapters = null;
                _rendererDeaf = false;
                bool started = _mainMovie && IsBluray(part.Path)
                    && await StartMainMovie(oppo, title, label, part, partCt).ConfigureAwait(false);
                if (!started)
                    started = Config.DlnaDiscImages && !_rendererDeaf
                        && (IsDiscImage(part.Path) || IsBdmvFolder(part.Path) || IsDvdFolder(part.Path) || DlnaFileMime(part.Path) != null)
                        && await StartDlna(oppo, title, label, part, partCt).ConfigureAwait(false);
                if (!started && !await StartFromShare(oppo, title, label, part, partCt).ConfigureAwait(false))
                    return PartEnd.Failed;

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
                // What the decoder reports is in the player's log for about 30 s only: read it now.
                DecoderInfo.Reset();
                DecoderInfo.Capture(oppo.Host, _logger);
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
                    // Main movie played as a file: tracks and resume point only once the file really plays (known
                    // duration, 2 s in); sent earlier, the player accepted them, then applied its own defaults (2026-11-20).
                    bool mainTitle = MainMovieChapters != null
                        ? status.Duration.Ticks > 0 && status.Position >= TimeSpan.FromSeconds(2)
                        : status.Duration.Ticks >= minimumMovieTicks;
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

        private static bool IsDiscImage(string path) =>
            !string.IsNullOrEmpty(path) && File.Exists(path) && string.Equals(Path.GetExtension(path), ".iso", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Sends the disc image to the player's DLNA renderer (oppo-dv-mkv mod, "DLNA Disc Images") and waits up
        /// to 45 s for the disc to start. Returns false (after stopping the renderer) so the caller falls back to
        /// the network share.
        /// </summary>
        private async Task<bool> StartDlna(OppoClient oppo, string title, string label, BaseItem part, CancellationToken ct)
        {
            string url, mime = "video/iso";
            long size = 0;
            try
            {
                string bdmv = IsDiscImage(part.Path) ? null : DiscInfo.FindBdmvFolder(part.Path);
                string videoTs = bdmv == null && !IsDiscImage(part.Path) ? FindVideoTs(part.Path) : null;
                if (bdmv != null)
                {
                    // Blu-ray folder: served over WebDAV, the mod lists it and builds the disc image itself.
                    // The link names a file inside BDMV, which is how the mod recognises a disc folder.
                    url = DiscServer.RegisterFolder(Directory.GetParent(bdmv.TrimEnd('/', '\\')).FullName, _serverAddress, Config.DiscServerPort, TimeSpan.FromHours(12), _logger)
                        + System.IO.Path.GetFileName(bdmv.TrimEnd('/', '\\')) + "/index.bdmv";
                    mime = FolderMime;
                }
                else if (videoTs != null)
                {
                    // DVD folder: the same, with a link to VIDEO_TS/VIDEO_TS.IFO (oppo-dv-mkv README; VIDEO_TS
                    // confirmed by its author, 2026-11-20). The names are sent as they are on disk.
                    url = DiscServer.RegisterFolder(Directory.GetParent(videoTs).FullName, _serverAddress, Config.DiscServerPort, TimeSpan.FromHours(12), _logger)
                        + Uri.EscapeDataString(System.IO.Path.GetFileName(videoTs)) + "/" + Uri.EscapeDataString(System.IO.Path.GetFileName(VideoTsIfo(videoTs)));
                    mime = FolderMime;
                }
                else
                {
                    // Disc image, or a video file (MKV…: the mod keeps Dolby Vision, 2026-11-20).
                    size = new FileInfo(part.Path).Length;
                    mime = IsDiscImage(part.Path) ? "video/iso" : DlnaFileMime(part.Path);
                    url = DiscServer.Register(part.Path, _serverAddress, Config.DiscServerPort, TimeSpan.FromHours(12), _logger, mime);
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.ErrorException("OPPO: disc image server unavailable (port " + Config.DiscServerPort + "), using the network share", ex);
                return false;
            }

            _logger.Info("OPPO: playing {0}{1} over DLNA ({2})", title, label, part.Path);
            await MakeReady(oppo, ct, needHttp: false).ConfigureAwait(false);
            // A wedged renderer stays in TRANSITIONING (even after Stop) and never fetches anything until the
            // player is unplugged (2026-11-19). Do not wait for it: use the share at once.
            if (!await RendererUsable(oppo, ct).ConfigureAwait(false)) return false;
            if (!await oppo.PlayDlna(url, title, size, ct, mime).ConfigureAwait(false))
            {
                _logger.Info("OPPO: DLNA refused, using the network share ({0})", title);
                return false;
            }

            // The mod reads the image within a second or two: if the player has not asked for it after 6 s,
            // DLNA is not going to work (mod off, port blocked…). Otherwise allow 45 s for the disc to start.
            var started = DateTime.UtcNow;
            var deadline = started.AddSeconds(45);
            int connectionsBefore = DiscServer.Connections;
            string state = "";
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                state = await oppo.QueryPlayback(ct).ConfigureAwait(false);
                if (IsSessionState(state) && state != "LOADING")
                {
                    _logger.Info("OPPO: started over DLNA in {0} s ({1} requests to the server)", (int)(DateTime.UtcNow - started).TotalSeconds, DiscServer.RequestCount(url));
                    return true;
                }
                if (DateTime.UtcNow - started > TimeSpan.FromSeconds(6) && DiscServer.RequestCount(url) == 0)
                {
                    _logger.Info("OPPO: the player did not request the movie within {0} s ({1} connections to the server). If this persists, unplug the player for 30 s to reset its DLNA renderer.", 6, DiscServer.Connections - connectionsBefore);
                    break;
                }
            }
            _logger.Info("OPPO: no playback after the DLNA request (state \"{0}\", {1} requests to the server), using the network share ({2})", state, DiscServer.RequestCount(url), title);
            // No renderer Stop: sent while the mod plays a disc image, it left the disc playing, deaf to IP control,
            // and the renderer wedged until the player was unplugged (2026-11-20). The share path stops by IP control.
            return false;
        }

        /// <summary>
        /// The renderer must answer. Its state is not a reliable sign of trouble: it reports TRANSITIONING during
        /// and after every disc image played by the mod, and still takes a new link then (2026-11-20). A wedged
        /// renderer is caught later, when it does not request the movie.
        /// </summary>
        private async Task<bool> RendererUsable(OppoClient oppo, CancellationToken ct)
        {
            if (await oppo.DlnaTransportState(ct).ConfigureAwait(false) != null) return true;
            _logger.Info("OPPO: DLNA renderer not answering ({0}), not used", Config.OppoAddress);
            return false;
        }

        /// <summary>DLNA type announced for a disc folder link (…/BDMV/index.bdmv). Not documented by the mod: set by testing.</summary>
        internal static string FolderMime = "video/mpeg";

        /// <summary>DLNA type of a video file the player can be sent directly, or null (then the share is used).</summary>
        private static string DlnaFileMime(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            switch (System.IO.Path.GetExtension(path).ToLowerInvariant())
            {
                case ".mkv": return "video/x-matroska";
                case ".mp4": case ".m4v": return "video/mp4";
                case ".m2ts": case ".mts": return "video/tts";   // timestamped TS: the player then knows the duration
                case ".ts": return "video/mpeg";
                default: return null;
            }
        }

        private static bool IsDvdFolder(string path) => FindVideoTs(path) != null;

        /// <summary>
        /// The VIDEO_TS folder of a DVD folder: the item's own folder (as Emby stores DVD folders) or VIDEO_TS
        /// itself, any case, with a VIDEO_TS.IFO inside. Null if there is none.
        /// </summary>
        private static string FindVideoTs(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return null;
            try
            {
                string folder = path.TrimEnd('/', '\\');
                if (string.Equals(System.IO.Path.GetFileName(folder), "VIDEO_TS", StringComparison.OrdinalIgnoreCase))
                    return VideoTsIfo(folder) != null ? folder : null;
                return Directory.EnumerateDirectories(folder)
                    .FirstOrDefault(d => string.Equals(System.IO.Path.GetFileName(d), "VIDEO_TS", StringComparison.OrdinalIgnoreCase) && VideoTsIfo(d) != null);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static string VideoTsIfo(string videoTs) =>
            Directory.EnumerateFiles(videoTs).FirstOrDefault(f => string.Equals(System.IO.Path.GetFileName(f), "VIDEO_TS.IFO", StringComparison.OrdinalIgnoreCase));

        private static bool IsBdmvFolder(string path) =>
            !string.IsNullOrEmpty(path) && Directory.Exists(path) && DiscInfo.FindBdmvFolder(path) != null;

        private static bool IsBluray(string path) =>
            IsDiscImage(path) || (!string.IsNullOrEmpty(path) && Directory.Exists(path) && DiscInfo.FindBdmvFolder(path) != null);

        /// <summary>
        /// "Movie without menus" with the oppo-dv-mkv mod: the Blu-ray reduced to its main movie (a virtual disc
        /// whose navigation plays the main playlist at once, see <see cref="MovieDisc"/>) is served over WebDAV and
        /// played by the OPPO as a disc: Dolby Vision, real chapters and named tracks kept (checked on 1917, UHD
        /// Dolby Vision, 2026-11-20). Returns false so the caller plays the disc normally.
        /// </summary>
        private async Task<bool> StartMovieDisc(OppoClient oppo, string title, string label, BaseItem part, CancellationToken ct)
        {
            string url;
            try
            {
                var tree = MovieDisc.Build(part.Path);
                url = DiscServer.RegisterTree(tree, _serverAddress, Config.DiscServerPort, TimeSpan.FromHours(12), _logger) + "BDMV/index.bdmv";
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Info("OPPO: {0} cannot be reduced to its main movie ({1}), playing the disc", title, ex.Message);
                return false;
            }
            _logger.Info("OPPO: playing {0}{1} reduced to its main movie, as a disc over DLNA", title, label);
            await MakeReady(oppo, ct, needHttp: false).ConfigureAwait(false);
            if (!await RendererUsable(oppo, ct).ConfigureAwait(false)) return false;
            if (!await oppo.PlayDlna(url, title, 0, ct, FolderMime).ConfigureAwait(false)) return false;
            var started = DateTime.UtcNow;
            string state = "";
            while (DateTime.UtcNow - started < TimeSpan.FromSeconds(45))
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                state = await oppo.QueryPlayback(ct).ConfigureAwait(false);
                if (state == "PLAY" || state == "PAUSE")
                {
                    _logger.Info("OPPO: main movie disc started in {0} s ({1} requests to the server)", (int)(DateTime.UtcNow - started).TotalSeconds, DiscServer.RequestCount(url));
                    return true;
                }
                if (DateTime.UtcNow - started > TimeSpan.FromSeconds(6) && DiscServer.RequestCount(url) == 0) break;
            }
            _rendererDeaf = DiscServer.RequestCount(url) == 0;
            _logger.Info("OPPO: the main movie disc did not start (state \"{0}\", {1} requests), playing the disc{2}", state, DiscServer.RequestCount(url),
                _rendererDeaf ? " from the share: the player's DLNA renderer took the link but fetched nothing (unplug the player for 30 s if this persists)" : "");
            return false;
        }

        /// <summary>
        /// Plays the main movie of a Blu-ray without the disc menus. With the mod: the reduced disc above. Without
        /// it: the .m2ts clips of the main playlist, read inside the ISO (or the folder) and served end to end as
        /// one file, sent to the DLNA renderer; the player then has no chapters (they are jumped to by time) and
        /// outputs no Dolby Vision (HDR10 base layer only: the web remote does not offer this mode for Dolby
        /// Vision discs then). Returns false so the caller plays the disc normally.
        /// </summary>
        private async Task<bool> StartMainMovie(OppoClient oppo, string title, string label, BaseItem part, CancellationToken ct)
        {
            if (Config.DlnaDiscImages) return await StartMovieDisc(oppo, title, label, part, ct).ConfigureAwait(false);
            DiscInfo disc;
            string url;
            long size = 0;
            try
            {
                disc = Chapters.Disc(part.Path);
                var segments = new List<DiscServer.Segment>();
                foreach (var clip in disc.ClipFiles)
                    foreach (var e in clip.Extents)
                    {
                        segments.Add(new DiscServer.Segment { Path = clip.Path, Offset = e.Offset, Length = e.Length });
                        size += e.Length;
                    }
                if (segments.Count == 0) throw new InvalidDataException("no clip");
                url = DiscServer.RegisterSegments(segments, "movie_" + part.InternalId.ToString(CultureInfo.InvariantCulture) + ".m2ts",
                    "video/vnd.dlna.mpeg-tts", _serverAddress, Config.DiscServerPort, TimeSpan.FromHours(12), _logger);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Info("OPPO: main movie of {0} unavailable ({1}), playing the disc", title, ex.Message);
                return false;
            }

            _logger.Info("OPPO: playing the main movie of {0}{1} without menus (playlist {2}, {3} clip(s))", title, label, disc.MainPlaylist.Name, disc.ClipFiles.Count);
            await MakeReady(oppo, ct, needHttp: false).ConfigureAwait(false);
            if (!await RendererUsable(oppo, ct).ConfigureAwait(false)) return false;
            // "video/tts" (MPEG-2 TS with timestamps, i.e. .m2ts): the player then knows the duration;
            // with "video/vnd.dlna.mpeg-tts" it refused the stream (2026-11-19).
            if (!await oppo.PlayDlna(url, title, size, ct, "video/tts").ConfigureAwait(false)) return false;

            var started = DateTime.UtcNow;
            string state = "";
            while (DateTime.UtcNow - started < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                state = await oppo.QueryPlayback(ct).ConfigureAwait(false);
                if (state == "PLAY" || state == "PAUSE")
                {
                    MainMovieChapters = disc.MainPlaylist.Chapters.ToArray();
                    _logger.Info("OPPO: main movie started in {0} s ({1} chapters)", (int)(DateTime.UtcNow - started).TotalSeconds, MainMovieChapters.Length);
                    return true;
                }
                if (DateTime.UtcNow - started > TimeSpan.FromSeconds(6) && DiscServer.RequestCount(url) == 0) break;
            }
            _rendererDeaf = DiscServer.RequestCount(url) == 0;
            _logger.Info("OPPO: the main movie did not start (state \"{0}\", {1} requests), playing the disc{2}", state, DiscServer.RequestCount(url),
                _rendererDeaf ? " from the share: the player's DLNA renderer took the link but fetched nothing (unplug the player for 30 s if this persists)" : "");
            return false;   // no renderer Stop (see StartDlna)
        }

        /// <summary>
        /// Next / previous chapter while the main movie plays as a file: the player has no chapters then,
        /// so jump by time to the disc's chapter marks. Returns false if not in that mode.
        /// </summary>
        internal static async Task<bool> JumpChapter(OppoClient oppo, bool next, CancellationToken ct)
        {
            var marks = MainMovieChapters;
            if (marks == null || marks.Length == 0) return false;
            double pos = (await oppo.GetStatus(ct).ConfigureAwait(false)).Position.TotalSeconds;
            double? target;
            if (next)
            {
                target = marks.Where(m => m > pos + 1).Select(m => (double?)m).FirstOrDefault();
            }
            else
            {
                int k = marks.Count(m => m <= pos) - 1;                        // current chapter
                double current = k >= 0 ? marks[k] : 0;
                target = pos - current > 5 || k <= 0 ? current : marks[k - 1];   // restart it, or go to the previous one
            }
            if (target.HasValue) await oppo.Seek(TimeSpan.FromSeconds(target.Value), ct).ConfigureAwait(false);
            return true;
        }

        /// <summary>"current / total" chapter of the main movie playing as a file, from its position.</summary>
        internal static string MainMovieChapter(double positionSeconds)
        {
            var marks = MainMovieChapters;
            if (marks == null || marks.Length == 0) return null;
            int k = Math.Max(1, marks.Count(m => m <= positionSeconds + 0.5));
            return k.ToString(CultureInfo.InvariantCulture) + " / " + marks.Length.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Mounts the network share (NFS or SMB) and starts the file or Blu-ray folder.</summary>
        private async Task<bool> StartFromShare(OppoClient oppo, string title, string label, BaseItem part, CancellationToken ct)
        {
            var partCt = ct;
            var target = MapPath(part.Path);
            if (target == null)
            {
                _logger.Error("OPPO: no share configured for {0} (see the OPPO page of the dashboard)", part.Path);
                return false;
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
                    return false;
                }

                string mounted = (smb ? "/mnt/cifs1/" : "/mnt/nfs1/") + target.Value.Relative;
                bool isFolder = Directory.Exists(part.Path);
                bool accepted = isFolder
                    ? await oppo.PlayBlurayFolder(mounted, partCt).ConfigureAwait(false)
                    : await oppo.PlayFile(mounted, map.Server, partCt).ConfigureAwait(false);
                if (!accepted) _logger.Info("OPPO: play command not confirmed, waiting for the player state ({0})", title);
            return true;
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
                int? audio = _audioIndex.HasValue ? await SelectAudio(oppo, item, _audioIndex.Value, ct).ConfigureAwait(false) : null;
                int? subtitle = _subtitleIndex.HasValue ? await SelectSubtitle(oppo, item, _subtitleIndex.Value, ct).ConfigureAwait(false) : null;
                // The player may accept a track and then switch back to its default (while a file is still
                // starting): check a moment later and send it again.
                for (int attempt = 0; attempt < 2 && (audio.HasValue || subtitle.HasValue); attempt++)
                {
                    await Task.Delay(2500, ct).ConfigureAwait(false);
                    bool redo = false;
                    if (audio.HasValue)
                    {
                        var current = (await oppo.GetTrackMenu(true, ct).ConfigureAwait(false)).FirstOrDefault(t => t.selected);
                        if (current != null && current.index != audio.Value) { redo = true; await oppo.SelectAudio(audio.Value, ct).ConfigureAwait(false); }
                    }
                    if (subtitle.HasValue)
                    {
                        var current = (await oppo.GetTrackMenu(false, ct).ConfigureAwait(false)).FirstOrDefault(t => t.selected);
                        if (current != null && current.index != subtitle.Value) { redo = true; await oppo.SelectSubtitle(subtitle.Value, ct).ConfigureAwait(false); }
                    }
                    if (!redo) break;
                    _logger.Info("OPPO: the player changed the tracks back, selecting them again (attempt {0})", attempt + 1);
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Info("OPPO: could not select tracks ({0})", ex.Message);
            }
        }

        private List<MediaBrowser.Model.Entities.MediaStream> Streams(BaseItem item, MediaStreamType type, CancellationToken ct) =>
            _itemRepository.GetMediaStreams(new MediaStreamQuery { ItemId = item.InternalId }, ct)
                           .Where(s => !s.IsExternal && s.Type == type).OrderBy(s => s.Index).ToList();

        /// <summary>Emby index → position in the OPPO audio list (same order as the disc). Returns the OPPO index sent, or null.</summary>
        private async Task<int?> SelectAudio(OppoClient oppo, BaseItem item, int embyIndex, CancellationToken ct)
        {
            int pos = Streams(item, MediaStreamType.Audio, ct).Select(s => s.Index).ToList().IndexOf(embyIndex);
            if (pos < 0) return null;
            // A disc played as a disc: its audio menu follows the playlist, whose order can differ from Emby's
            // (Dune: an AC-3 track of the clip that the playlist does not list). Use the playlist position.
            if (MainMovieChapters == null && IsBluray(item.Path))
            {
                try
                {
                    var embedded = _itemRepository.GetMediaStreams(new MediaStreamQuery { ItemId = item.InternalId }, ct)
                        .Where(s => !s.IsExternal).OrderBy(s => s.Index).ToList();
                    var match = AudioExtras.MapToPlaylist(Chapters.Disc(item.Path), embedded).FirstOrDefault(x => x.Stream.Index == embyIndex);
                    if (match.Entry != null && match.Position != pos)
                    {
                        _logger.Info("OPPO: Emby audio track {0} is track {1} of the disc playlist (Emby order: {2})", embyIndex, match.Position + 1, pos + 1);
                        pos = match.Position;
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    _logger.Debug("OPPO: playlist audio order unavailable: {0}", ex.Message);
                }
            }
            // The OPPO numbers its audio tracks from 1.
            bool ok = await oppo.SelectAudio(pos + 1, ct).ConfigureAwait(false);
            _logger.Info("OPPO: Emby audio track {0} → OPPO #{1}: {2}", embyIndex, pos + 1, ok ? "OK" : "refused");
            return pos + 1;
        }

        private async Task<int?> SelectSubtitle(OppoClient oppo, BaseItem item, int embyIndex, CancellationToken ct)
        {
            int pos = embyIndex < 0 ? -1 : Streams(item, MediaStreamType.Subtitle, ct).Select(s => s.Index).ToList().IndexOf(embyIndex);
            if (embyIndex >= 0 && pos < 0) return null;
            // OPPO subtitle menu: 0 = "Off", then 1, 2…
            int oppoIndex = pos < 0 ? 0 : pos + 1;
            bool ok = await oppo.SelectSubtitle(oppoIndex, ct).ConfigureAwait(false);
            _logger.Info("OPPO: Emby subtitles {0} → OPPO #{1}: {2}", embyIndex, oppoIndex, ok ? "OK" : "refused");
            return oppoIndex;
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
                    if (!await JumpChapter(oppo, true, ct).ConfigureAwait(false))
                        await oppo.Command("NXT", ct).ConfigureAwait(false);   // next chapter
                    break;
                case PlaystateCommand.PreviousTrack:
                    if (!await JumpChapter(oppo, false, ct).ConfigureAwait(false))
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
        private async Task MakeReady(OppoClient oppo, CancellationToken ct, bool force = false, bool needHttp = true)
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
            else if (needHttp && !await oppo.WaitForHttp(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false))
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
                .OrderByDescending(m => m.EmbyPath.Trim().TrimEnd('/', '\\').Length);
            // Windows paths (D:\Movies\Dune, \\nas\Movies) are compared with forward slashes and without case; the
            // relative part is sent to the OPPO with forward slashes. Linux paths are left as they are.
            foreach (var m in mappings)
            {
                string raw = m.EmbyPath.Trim();
                bool windows = (raw.Length >= 2 && raw[1] == ':') || raw.StartsWith("\\\\", StringComparison.Ordinal);
                string prefix = (windows ? raw.Replace('\\', '/') : raw).TrimEnd('/');
                string candidate = windows ? path.Replace('\\', '/') : path;
                if (candidate.StartsWith(prefix + "/", windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    return (m, candidate.Substring(prefix.Length + 1));
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
