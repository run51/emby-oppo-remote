using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Emby.Oppo.Oppo;
using System.Globalization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;

namespace Emby.Oppo
{
    // ------------------------------------------------------------------ Page and files (public)

    [Route("/Oppo/Remote", "GET", Summary = "OPPO web remote")]
    [Unauthenticated]
    public sealed class RemotePage { }

    [Route("/Oppo/Remote/app.webmanifest", "GET")]  // not ".json": Emby would treat it as a response format
    [Unauthenticated]
    public sealed class RemoteManifest { }

    [Route("/Oppo/Remote/icon192.png", "GET")]
    [Unauthenticated]
    public sealed class RemoteIcon192 { }

    [Route("/Oppo/Remote/sw.js", "GET")]
    [Unauthenticated]
    public sealed class RemoteServiceWorker { }

    [Route("/Oppo/Remote/oppo.apk", "GET", Summary = "Android app for the remote")]
    [Unauthenticated]
    public sealed class RemoteApk { }

    [Route("/Oppo/Remote/icon512.png", "GET")]
    [Unauthenticated]
    public sealed class RemoteIcon512 { }

    // ------------------------------------------------------------------ API (signed-in Emby user)

    [Route("/Oppo/Remote/State", "GET", Summary = "OPPO state for the remote")]
    public sealed class RemoteState : IReturn<RemoteStateResult>
    {
        /// <summary>Include the audio and subtitle track lists (slower).</summary>
        public bool Tracks { get; set; }
    }

    [Route("/Oppo/Remote/Key", "POST", Summary = "Sends a remote-control key to the OPPO")]
    public sealed class RemoteKey : IReturn<RemoteActionResult>
    {
        public string Code { get; set; }
    }

    [Route("/Oppo/Remote/Audio", "POST", Summary = "Selects an audio track in the OPPO menu")]
    public sealed class RemoteAudio : IReturn<RemoteActionResult>
    {
        public int Index { get; set; }
    }

    [Route("/Oppo/Remote/Subtitle", "POST", Summary = "Selects subtitles in the OPPO menu (0 = none)")]
    public sealed class RemoteSubtitle : IReturn<RemoteActionResult>
    {
        public int Index { get; set; }
    }

    [Route("/Oppo/Remote/Format", "GET", Summary = "Format of a movie: UHD Blu-ray, Blu-ray, DVD or file type")]
    public sealed class RemoteFormat : IReturn<FormatInfo>
    {
        /// <summary>Emby ID of the movie (or of one of its discs).</summary>
        public string Id { get; set; }
        /// <summary>Selected version ("mediasource_…"), optional.</summary>
        public string MediaSourceId { get; set; }
    }

    [Route("/Oppo/Remote/Badges", "GET", Summary = "Resolution and HDR of a list of movies (poster badges)")]
    public sealed class RemoteBadges : IReturn<RemoteBadge[]>
    {
        /// <summary>Comma-separated Emby IDs.</summary>
        public string Ids { get; set; }
    }

    public sealed class RemoteBadge
    {
        public string Id { get; set; }
        /// <summary>4K, 1080p… (empty if unknown).</summary>
        public string Resolution { get; set; }
        /// <summary>DV, HDR10+, HDR10, HLG or empty.</summary>
        public string Hdr { get; set; }
    }

    [Route("/Oppo/Remote/Seance", "POST", Summary = "Start or end of a movie session: OPPO + home-automation webhook (TV, amplifier)")]
    public sealed class RemoteSeance : IReturn<RemoteActionResult>
    {
        public bool On { get; set; }
    }

    [Route("/Oppo/Remote/Chapters", "GET", Summary = "Chapters of a movie (real Blu-ray marks)")]
    public sealed class RemoteChapters : IReturn<ChapterEntry[]>
    {
        public string Id { get; set; }
        public string MediaSourceId { get; set; }
    }

    [Route("/Oppo/Remote/ChapterImage", "GET", Summary = "Chapter thumbnail (JPEG)")]
    [Unauthenticated]   // like Emby's posters: an <img> tag cannot send a token
    public sealed class RemoteChapterImage
    {
        public string Id { get; set; }
        public string MediaSourceId { get; set; }
        public int Index { get; set; }
    }

    [Route("/Oppo/Remote/Sleep", "POST", Summary = "Sleep timer: minutes (0 = cancel, -1 = at the end of the movie)")]
    public sealed class RemoteSleep : IReturn<RemoteActionResult>
    {
        public int Minutes { get; set; }
    }

    [Route("/Oppo/Remote/Seek", "POST", Summary = "Seeks to a time in the current title")]
    public sealed class RemoteSeek : IReturn<RemoteActionResult>
    {
        public double Seconds { get; set; }
    }

    public sealed class RemoteTrack
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public bool Selected { get; set; }
    }

    public sealed class RemoteStateResult
    {
        /// <summary>Emby session of the OPPO (to start a movie on it), empty if the OPPO is not configured.</summary>
        public string SessionId { get; set; }
        public string DeviceName { get; set; }
        public bool Reachable { get; set; }
        /// <summary>#QPL state: PLAY, PAUSE, STOP, DISC MENU, HOME MENU…</summary>
        public string State { get; set; }
        public string Title { get; set; }
        public string ItemId { get; set; }
        public string ImageTag { get; set; }
        public double Position { get; set; }
        public double Duration { get; set; }
        public string Chapter { get; set; }
        public RemoteTrack[] Audio { get; set; }
        public RemoteTrack[] Subtitles { get; set; }
        /// <summary>What the OPPO actually outputs: picture (HDR + resolution), decoded audio, subtitles.</summary>
        public string OutputVideo { get; set; }
        public string OutputAudio { get; set; }
        public string OutputSubtitle { get; set; }
        /// <summary>Scheduled power-off time (UTC, ISO 8601), or null.</summary>
        public string SleepAt { get; set; }
        public bool SleepEndOfFilm { get; set; }
    }

    public sealed class RemoteActionResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Web remote: a page (public, no data) and a small API restricted to signed-in Emby users,
    /// which drives the OPPO directly (port 23 and port 436). Texts returned to the client follow the
    /// request's Accept-Language (English by default, French).
    /// </summary>
    public sealed class OppoRemoteService : IService, IRequiresRequest
    {
        /// <summary>Allowed keys: only those of the remote control (OPPO IP protocol).</summary>
        private static readonly HashSet<string> AllowedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PON", "POF", "POW",
            "PLA", "PAU", "STP", "PRE", "NXT", "REV", "FWD",
            "NUP", "NDN", "NLT", "NRT", "SEL", "RET",
            "HOM", "OSD", "TTL", "MNU", "OPT", "PUP", "PDN",
            "RED", "GRN", "BLU", "YLW",
            "AUD", "SUB", "ANG", "ZOM", "RPT"
        };

        private static readonly HashSet<string> PlayingStates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PLAY", "PAUSE", "FFWD", "FREV", "SFWD", "SREV", "STEP"
        };

        private readonly IJsonSerializer _json;
        private readonly ILogger _logger;
        private readonly IHttpResultFactory _resultFactory;
        private readonly ISessionManager _sessionManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;

        private readonly IMediaEncoder _mediaEncoder;
        private readonly IApplicationPaths _paths;

        public OppoRemoteService(IJsonSerializer json, ILogManager logManager, IHttpResultFactory resultFactory,
            ISessionManager sessionManager, ILibraryManager libraryManager, IItemRepository itemRepository,
            IMediaEncoder mediaEncoder, IApplicationPaths paths)
        {
            _mediaEncoder = mediaEncoder;
            _paths = paths;
            _itemRepository = itemRepository;
            _json = json;
            _logger = logManager.GetLogger("Oppo");
            _resultFactory = resultFactory;
            _sessionManager = sessionManager;
            _libraryManager = libraryManager;
        }

        public IRequest Request { get; set; }

        /// <summary>True if the client asked for French (Accept-Language).</summary>
        private bool Fr => L10n.IsFrench(Request);

        private OppoClient Oppo
        {
            get
            {
                string host = Plugin.Instance.Configuration.OppoAddress;
                if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException(L10n.T(Fr, "OPPO address not set (OPPO page of the dashboard).", "Adresse de l'OPPO non renseignée (page OPPO du tableau de bord)."));
                return new OppoClient(host.Trim(), _json, _logger);
            }
        }

        // ---------------------------------------------------------------- Files

        private static readonly MethodInfo StreamResult = typeof(IHttpResultFactory).GetMethod("GetResult",
            new[] { typeof(IRequest), typeof(Stream), typeof(string), typeof(IDictionary<string, string>) })
            ?? throw new MissingMethodException("IHttpResultFactory.GetResult(IRequest, Stream, string, IDictionary)");

        private object Embedded(string name, string contentType) => Embedded(name, contentType, null);

        private object Embedded(string name, string contentType, Dictionary<string, string> extraHeaders)
        {
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Emby.Oppo.Remote." + name);
            if (stream == null) throw new FileNotFoundException(name);
            // Called through reflection: the other GetResult overloads use ReadOnlySpan/ReadOnlyMemory,
            // which netstandard2.0 lacks without the System.Memory package.
            return StreamResult.Invoke(_resultFactory, new object[] { Request, stream, contentType, Headers(extraHeaders) });
        }

        private static Dictionary<string, string> Headers(Dictionary<string, string> extra)
        {
            var headers = new Dictionary<string, string> { ["Cache-Control"] = "no-cache" };
            if (extra != null) foreach (var kv in extra) headers[kv.Key] = kv.Value;
            return headers;
        }

        // The service worker is served under …/Oppo/Remote/ but must cover the page …/Oppo/Remote.
        public object Get(RemoteServiceWorker request) => Embedded("sw.js", "application/javascript; charset=utf-8",
            new Dictionary<string, string> { ["Service-Worker-Allowed"] = "/" });

        public object Get(RemoteApk request) => Embedded("oppo.apk", "application/vnd.android.package-archive",
            new Dictionary<string, string> { ["Content-Disposition"] = "attachment; filename=\"OPPO.apk\"" });

        public object Get(RemotePage request) => Embedded("remote.html", "text/html; charset=utf-8");

        public object Get(RemoteManifest request) => Embedded("manifest.json", "application/manifest+json");

        public object Get(RemoteIcon192 request) => Embedded("icon192.png", "image/png");

        public object Get(RemoteIcon512 request) => Embedded("icon512.png", "image/png");

        // ---------------------------------------------------------------- API

        public async Task<object> Get(RemoteState request)
        {
            var result = new RemoteStateResult();
            var oppo = Oppo;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                var ct = cts.Token;
                result.State = await oppo.QueryPlayback(ct).ConfigureAwait(false);
                result.Reachable = !string.IsNullOrEmpty(result.State);

                // Emby's "OPPO" session: used to start a movie on it, and gives the current title.
                var session = _sessionManager.Sessions.FirstOrDefault(s =>
                    string.Equals(s.Client, "OPPO", StringComparison.Ordinal) && s.SessionControllers.OfType<OppoSessionController>().Any());
                result.SessionId = session?.Id;
                result.DeviceName = Plugin.Instance.Configuration.DeviceName;
                result.SleepAt = SleepTimer.At?.ToString("o", CultureInfo.InvariantCulture);
                result.SleepEndOfFilm = SleepTimer.EndOfFilm;
                var now = session?.NowPlayingItem;
                if (now != null)
                {
                    result.Title = now.Name;
                    result.ItemId = now.Id;
                    if (now.ImageTags != null && now.ImageTags.TryGetValue(ImageType.Primary, out var tag)) result.ImageTag = tag;
                }

                if (result.Reachable && PlayingStates.Contains(result.State))
                {
                    var status = await oppo.GetStatus(ct).ConfigureAwait(false);
                    result.Position = status.Position.TotalSeconds;
                    result.Duration = status.Duration.TotalSeconds;
                    result.Chapter = await oppo.QueryChapter(ct).ConfigureAwait(false);
                    if (request.Tracks)
                    {
                        string hdr = Value(await oppo.Command("QHS", ct).ConfigureAwait(false), "QHS");
                        OppoClient.Events.TryGetValue("UVO", out var uvo);
                        bool fr = Fr;
                        result.OutputVideo = string.Join(" · ", new[] { HdrLabel(hdr), VideoOutLabel(uvo, fr) }.Where(x => !string.IsNullOrEmpty(x)));
                        result.OutputAudio = TrackLabel(Value(await oppo.Command("QAT", ct).ConfigureAwait(false), "QAT"), true, fr);
                        result.OutputSubtitle = TrackLabel(Value(await oppo.Command("QST", ct).ConfigureAwait(false), "QST"), false, fr);
                        result.Audio = (await oppo.GetTrackMenu(true, ct).ConfigureAwait(false))
                            .Select(t => new RemoteTrack { Index = t.index, Name = t.name, Selected = t.selected }).ToArray();
                        result.Subtitles = (await oppo.GetTrackMenu(false, ct).ConfigureAwait(false))
                            .Select(t => new RemoteTrack { Index = t.index, Name = t.name, Selected = t.selected }).ToArray();
                    }
                }
            }
            return result;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, (DateTime At, RemoteBadge Badge)> BadgeCache =
            new System.Collections.Concurrent.ConcurrentDictionary<long, (DateTime, RemoteBadge)>();

        // Badge values (4K, 1080p, DV, HDR10…) are language-neutral, so the cache is shared by all languages.
        public object Get(RemoteBadges request)
        {
            var result = new List<RemoteBadge>();
            foreach (var raw in (request.Ids ?? "").Split(',').Take(200))
            {
                if (!long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)) continue;
                if (BadgeCache.TryGetValue(id, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(30)) { result.Add(hit.Badge); continue; }
                var badge = new RemoteBadge { Id = raw.Trim() };
                var video = _itemRepository.GetMediaStreams(new MediaStreamQuery { ItemId = id }, CancellationToken.None)
                                           .FirstOrDefault(s => s.Type == MediaStreamType.Video && !s.IsExternal);
                if (video != null)
                {
                    int w = video.Width ?? 0, h = video.Height ?? 0;
                    badge.Resolution = w >= 3800 || h >= 2100 ? "4K" : w >= 1900 || h >= 1000 ? "1080p" : w >= 1200 || h >= 700 ? "720p" : h > 0 ? "SD" : null;
                    switch (video.ExtendedVideoType)
                    {
                        case ExtendedVideoTypes.DolbyVision: badge.Hdr = "DV"; break;
                        case ExtendedVideoTypes.Hdr10Plus: badge.Hdr = "HDR10+"; break;
                        case ExtendedVideoTypes.Hdr10: badge.Hdr = "HDR10"; break;
                        case ExtendedVideoTypes.HyperLogGamma: badge.Hdr = "HLG"; break;
                    }
                }
                BadgeCache[id] = (DateTime.UtcNow, badge);
                result.Add(badge);
            }
            return result.ToArray();
        }

        /// <summary>
        /// Movie session: powers the OPPO on and notifies home automation ("seance_on": TV on the player's
        /// input, amplifier on), or the reverse ("seance_off", then the OPPO is powered off).
        /// </summary>
        public async Task<object> Post(RemoteSeance request)
        {
            var oppo = Oppo;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                if (request.On) _ = TvPower.TurnOn(_logger);
                if (request.On) { try { await oppo.PowerOn(cts.Token).ConfigureAwait(false); } catch (Exception ex) { _logger.Info("OPPO: could not power on ({0})", ex.Message); } }
                bool notified = await Webhook(request.On ? "seance_on" : "seance_off").ConfigureAwait(false);
                if (!request.On) { try { await oppo.Command("POF", cts.Token).ConfigureAwait(false); } catch (Exception ex) { _logger.Info("OPPO: could not power off ({0})", ex.Message); } }
                _logger.Info("OPPO: movie session {0} (home automation {1})", request.On ? "start" : "end", notified ? "notified" : "not configured or unreachable");
                return new RemoteActionResult
                {
                    Ok = true,
                    Message = notified ? null : L10n.T(Fr,
                        "Home automation not notified (webhook address missing or unreachable): only the OPPO was controlled.",
                        "Domotique non prévenue (adresse du webhook absente ou injoignable) : seul l'OPPO a été piloté.")
                };
            }
        }

        private async Task<bool> Webhook(string evt)
        {
            string url = Plugin.Instance.Configuration.WebhookUrl;
            if (string.IsNullOrWhiteSpace(url)) return false;
            try
            {
                var request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Timeout = 5000;
                var body = System.Text.Encoding.UTF8.GetBytes(_json.SerializeToString(new Dictionary<string, string> { ["event"] = evt, ["title"] = "" }));
                using (var s = await request.GetRequestStreamAsync().ConfigureAwait(false))
                    await s.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
                using (await request.GetResponseAsync().ConfigureAwait(false)) { }
                return true;
            }
            catch (Exception ex)
            {
                _logger.Info("OPPO: home-automation webhook unreachable ({0})", ex.Message);
                return false;
            }
        }

        private BaseItem Resolve(string idText, string mediaSourceId)
        {
            if (!long.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)) return null;
            var item = _libraryManager.GetItemById(id);
            if (item != null && !string.IsNullOrEmpty(mediaSourceId))
                item = _libraryManager.GetItemById(item.InternalId.ToString(CultureInfo.InvariantCulture), mediaSourceId) ?? item;
            return item;
        }

        private Chapters ChapterSource => new Chapters(_itemRepository, _logger, _mediaEncoder.EncoderPath, _paths.CachePath);

        public object Get(RemoteChapters request)
        {
            var item = Resolve(request.Id, request.MediaSourceId);
            return item == null ? Array.Empty<ChapterEntry>() : ChapterSource.List(item, Fr).ToArray();
        }

        public async Task<object> Get(RemoteChapterImage request)
        {
            var item = Resolve(request.Id, request.MediaSourceId);
            if (item == null) throw new FileNotFoundException("Unknown movie");
            string file;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                file = await ChapterSource.Image(item, request.Index, cts.Token).ConfigureAwait(false);
            if (file == null) throw new FileNotFoundException("Thumbnail unavailable");
            var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            return StreamResult.Invoke(_resultFactory, new object[] { Request, stream, "image/jpeg", new Dictionary<string, string> { ["Cache-Control"] = "public, max-age=604800" } });
        }

        public object Post(RemoteSleep request)
        {
            SleepTimer.Set(request.Minutes, SeanceOff);
            _logger.Info("OPPO: sleep timer {0}", request.Minutes == 0 ? "cancelled" : request.Minutes < 0 ? "at the end of the movie" : "in " + request.Minutes + " min");
            return new RemoteActionResult { Ok = true };
        }

        /// <summary>End of the movie session: home automation (TV, amplifier), then the OPPO is powered off.</summary>
        private async Task SeanceOff()
        {
            await Webhook("seance_off").ConfigureAwait(false);
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                    await Oppo.Command("POF", cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) { _logger.Info("OPPO: could not power off ({0})", ex.Message); }
            _logger.Info("OPPO: sleep timer fired ({0})", DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture));
        }

        public object Get(RemoteFormat request)
        {
            if (!long.TryParse(request.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)) return new FormatInfo();
            var item = _libraryManager.GetItemById(id);
            if (item == null) return new FormatInfo();
            if (!string.IsNullOrEmpty(request.MediaSourceId))
                item = _libraryManager.GetItemById(item.InternalId.ToString(CultureInfo.InvariantCulture), request.MediaSourceId) ?? item;
            return DiscFormat.Detect(item.Path, Fr) ?? new FormatInfo();
        }

        // ---------------------------------------------------------------- OPPO output information

        private static string Value(string answer, string code)
        {
            string prefix = "@" + code + " OK ";
            return answer != null && answer.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? answer.Substring(prefix.Length).Trim() : null;
        }

        private static string HdrLabel(string v)
        {
            switch ((v ?? "").ToUpperInvariant())
            {
                case "": return null;
                case "DOV": case "DV": case "DOLBY VISION": return "Dolby Vision";
                case "HDR": case "HDR10": return "HDR10";
                case "HDR10+": case "HDR10P": case "HDR10PLUS": return "HDR10+";
                case "HLG": return "HLG";
                case "SDR": return "SDR";
                default: return v;
            }
        }

        /// <summary>"_UHD24_" → "4K 24 fps" ("4K 24 i/s" in French), "_1080P60_" → "1080p 60 fps".</summary>
        private static string VideoOutLabel(string v, bool fr)
        {
            if (string.IsNullOrEmpty(v)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(v.Trim('_').ToUpperInvariant(), @"^(UHD|4K|\d{3,4}[PI]?)(\d{2,3})?$");
            if (!m.Success) return v.Trim('_');
            string res = m.Groups[1].Value == "UHD" || m.Groups[1].Value == "4K" ? "4K" : m.Groups[1].Value.ToLowerInvariant();
            return m.Groups[2].Success ? res + " " + m.Groups[2].Value + L10n.T(fr, " fps", " i/s") : res;
        }

        private static readonly Dictionary<string, string> Codecs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DD"] = "Dolby Digital", ["DDP"] = "Dolby Digital Plus", ["DD+"] = "Dolby Digital Plus", ["EAC3"] = "Dolby Digital Plus",
            ["TRUEHD"] = "Dolby TrueHD", ["TRUE-HD"] = "Dolby TrueHD", ["ATMOS"] = "Dolby Atmos", ["DTS"] = "DTS", ["DTS-HD"] = "DTS-HD",
            ["DTSHD"] = "DTS-HD", ["DTS-HDMA"] = "DTS-HD MA", ["DTSHDMA"] = "DTS-HD MA", ["DTS-HRA"] = "DTS-HD HRA", ["DTSX"] = "DTS:X",
            ["DTS-X"] = "DTS:X", ["DTS:X"] = "DTS:X", ["LPCM"] = "PCM", ["PCM"] = "PCM", ["MPEG"] = "MPEG", ["AAC"] = "AAC", ["MP3"] = "MP3",
            ["FLAC"] = "FLAC"
        };

        // Keys are the language names the OPPO reports (device tokens, matched as is); values are the
        // French names. In English the OPPO's own name is shown.
        private static readonly Dictionary<string, string> FrenchLanguages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["French"] = "Français", ["English"] = "Anglais", ["German"] = "Allemand", ["Spanish"] = "Espagnol", ["Italian"] = "Italien",
            ["Japanese"] = "Japonais", ["Dutch"] = "Néerlandais", ["Portuguese"] = "Portugais", ["Russian"] = "Russe", ["Chinese"] = "Chinois",
            ["Korean"] = "Coréen", ["Polish"] = "Polonais", ["Swedish"] = "Suédois", ["Danish"] = "Danois", ["Norwegian"] = "Norvégien",
            ["Finnish"] = "Finnois", ["Czech"] = "Tchèque", ["Hungarian"] = "Hongrois", ["Turkish"] = "Turc", ["Greek"] = "Grec",
            ["Thai"] = "Thaï", ["Hebrew"] = "Hébreu", ["Arabic"] = "Arabe", ["Other"] = "Autre", ["Unknown"] = "Inconnue"
        };

        /// <summary>
        /// "DD 3/3 French" → "Dolby Digital · French · track 3/3" (French: "Dolby Digital · Français · piste 3/3");
        /// "Subtitle off" → "None" ("Aucun").
        /// </summary>
        private static string TrackLabel(string v, bool audio, bool fr)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            if (v.IndexOf("off", StringComparison.OrdinalIgnoreCase) >= 0 && !audio) return L10n.T(fr, "None", "Aucun");
            var m = System.Text.RegularExpressions.Regex.Match(v, @"^(.*?)\s*(\d+/\d+)\s*(.*)$");
            if (!m.Success) return v;
            string kind = m.Groups[1].Value.Trim(), track = m.Groups[2].Value, lang = m.Groups[3].Value.Trim();
            var parts = new List<string>();
            if (audio && kind.Length > 0)
                parts.Add(string.Join(" ", kind.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(t => Codecs.TryGetValue(t, out var c) ? c : t)));
            if (lang.Length > 0) parts.Add(fr && FrenchLanguages.TryGetValue(lang, out var l) ? l : lang);
            parts.Add(L10n.T(fr, "track ", "piste ") + track);
            return string.Join(" · ", parts);
        }

        public async Task<object> Post(RemoteKey request)
        {
            string code = (request.Code ?? "").Trim().ToUpperInvariant();
            if (!AllowedKeys.Contains(code)) return new RemoteActionResult { Message = L10n.T(Fr, "Unknown key: ", "Touche inconnue : ") + code };
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                string answer = await Oppo.Command(code, cts.Token).ConfigureAwait(false);
                bool ok = answer.IndexOf(" OK", StringComparison.OrdinalIgnoreCase) >= 0;
                _logger.Debug("OPPO: web remote #{0} → {1}", code, answer);
                return new RemoteActionResult { Ok = ok, Message = ok ? null : (string.IsNullOrEmpty(answer) ? L10n.T(Fr, "The OPPO is not responding", "L'OPPO ne répond pas") : answer) };
            }
        }

        public async Task<object> Post(RemoteAudio request)
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                bool ok = await Oppo.SelectAudio(request.Index, cts.Token).ConfigureAwait(false);
                _logger.Info("OPPO: web remote, audio track #{0}: {1}", request.Index, ok ? "OK" : "refused");
                return new RemoteActionResult { Ok = ok, Message = ok ? null : L10n.T(Fr, "The OPPO refused the track", "L'OPPO a refusé la piste") };
            }
        }

        public async Task<object> Post(RemoteSubtitle request)
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                bool ok = await Oppo.SelectSubtitle(request.Index, cts.Token).ConfigureAwait(false);
                _logger.Info("OPPO: web remote, subtitles #{0}: {1}", request.Index, ok ? "OK" : "refused");
                return new RemoteActionResult { Ok = ok, Message = ok ? null : L10n.T(Fr, "The OPPO refused the subtitles", "L'OPPO a refusé les sous-titres") };
            }
        }

        public async Task<object> Post(RemoteSeek request)
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                var target = TimeSpan.FromSeconds(Math.Max(0, request.Seconds));
                string answer = await Oppo.Seek(target, cts.Token).ConfigureAwait(false);
                bool ok = answer.IndexOf(" OK", StringComparison.OrdinalIgnoreCase) >= 0;
                _logger.Info("OPPO: web remote, seek to {0} ({1})", target, answer);
                return new RemoteActionResult { Ok = ok, Message = ok ? null : (string.IsNullOrEmpty(answer) ? L10n.T(Fr, "The OPPO is not responding", "L'OPPO ne répond pas") : answer) };
            }
        }
    }
}
