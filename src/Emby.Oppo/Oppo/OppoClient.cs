using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace Emby.Oppo.Oppo
{
    /// <summary>Playback status returned by the OPPO.</summary>
    public sealed class OppoStatus
    {
        public bool Reachable { get; set; }
        public bool IsPlaying { get; set; }
        public bool IsPaused { get; set; }
        public TimeSpan Position { get; set; }
        public TimeSpan Duration { get; set; }
    }

    /// <summary>
    /// Talks to the OPPO:
    /// - port 436: HTTP interface used by the control app (JSON parameters in the URL);
    /// - port 23:  IP control protocol documented by OPPO ("#XXX\r" → "@OK …").
    /// </summary>
    public sealed class OppoClient
    {
        private readonly string _host;

        /// <summary>The player's address (for SSH).</summary>
        public string Host => _host;
        private readonly ILogger _logger;
        private readonly IJsonSerializer _json;
        // Shared by all instances: only one connection at a time on port 23
        // (playback tracking, the web remote and Emby commands each create their own client).
        private static readonly SemaphoreSlim _ipLock = new SemaphoreSlim(1, 1);

        public OppoClient(string host, IJsonSerializer json, ILogger logger)
        {
            _host = host;
            _json = json;
            _logger = logger;
        }

        // ---------------------------------------------------------------- HTTP 436

        private sealed class MoviePlayInfo
        {
#pragma warning disable IDE1006 // JSON names of the OPPO's HTTP interface
            public bool success { get; set; }
            public MoviePlay playinfo { get; set; }
#pragma warning restore IDE1006
        }

        private sealed class MoviePlay
        {
#pragma warning disable IDE1006
            public string frame_rate { get; set; }
            public string width { get; set; }
            public string height { get; set; }
#pragma warning restore IDE1006
        }

        /// <summary>
        /// Picture of the video file being played, from the HTTP interface (getmovieplayinfo: width, height, frame_rate,
        /// e.g. 3840 x 2160 at "23.976"): "2160p 23,976 i/s" in French. Null when the player gives none (disc menus,
        /// discs). HTTP only: it does not hold the IP-control link.
        /// </summary>
        public async Task<string> GetVideoFormat(bool fr, CancellationToken ct)
        {
            try
            {
                string url = "http://" + _host + ":436/getmovieplayinfo";
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Timeout = 3000;
                request.ReadWriteTimeout = 3000;
                MoviePlayInfo info;
                using (ct.Register(request.Abort))
                using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    info = _json.DeserializeFromString<MoviePlayInfo>(await reader.ReadToEndAsync().ConfigureAwait(false));
                var p = info?.playinfo;
                if (p == null || !int.TryParse(p.height, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) || h <= 0) return null;
                double.TryParse(p.frame_rate, NumberStyles.Float, CultureInfo.InvariantCulture, out double fps);
                return PictureLabel(h, fps, false, fr);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Debug("OPPO getmovieplayinfo: {0}", ex.Message);
                return null;
            }
        }

        /// <summary>"2160p 23,976 i/s" (French) / "1080i 29.97 fps": the picture's height and frame rate.</summary>
        internal static string PictureLabel(int height, double fps, bool interlaced, bool fr)
        {
            string res = height + (interlaced ? "i" : "p");
            if (fps > 0)
            {
                string n = fps.ToString(Math.Abs(fps - Math.Round(fps)) < 0.001 ? "0" : "0.###", CultureInfo.InvariantCulture);
                res += " " + (fr ? n.Replace('.', ',') + " i/s" : n + " fps");
            }
            return res;
        }

        private async Task<Dictionary<string, object>> Call(string endpoint, object args, TimeSpan timeout, CancellationToken ct, bool logRequest = true)
        {
            string url = "http://" + _host + ":436/" + endpoint;
            if (args != null) url += "?" + Uri.EscapeDataString(_json.SerializeToString(args));
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = (int)timeout.TotalMilliseconds;
            request.ReadWriteTimeout = request.Timeout;
            using (ct.Register(request.Abort))
            using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                string body = await reader.ReadToEndAsync().ConfigureAwait(false);
                // The response to an SMB mount may echo the credentials: it is not logged.
                if (logRequest) _logger.Debug("OPPO {0} → {1}", endpoint, body.Length > 300 ? body.Substring(0, 300) : body);
                else _logger.Debug("OPPO {0} → ({1} bytes, content hidden)", endpoint, body.Length);
                return _json.DeserializeFromString<Dictionary<string, object>>(body) ?? new Dictionary<string, object>();
            }
        }

        private static bool Success(Dictionary<string, object> r) =>
            r.TryGetValue("success", out var v) && string.Equals(Convert.ToString(v, CultureInfo.InvariantCulture), "true", StringComparison.OrdinalIgnoreCase);

        private static long Number(Dictionary<string, object> r, string key) =>
            r.TryGetValue(key, out var v) && long.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : 0;

        private static bool Flag(Dictionary<string, object> r, string key) =>
            r.TryGetValue(key, out var v) && string.Equals(Convert.ToString(v, CultureInfo.InvariantCulture), "true", StringComparison.OrdinalIgnoreCase);

        /// <summary>Signs in to the OPPO (required before the other HTTP commands).</summary>
        public async Task<bool> SignIn(string clientAddress, CancellationToken ct)
        {
            var r = await Call("signin", new Dictionary<string, object> { ["appIconType"] = 1, ["appIpAddress"] = clientAddress }, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            return Success(r);
        }

        /// <summary>Mounts an NFS export; the OPPO then exposes it under /mnt/nfs1.</summary>
        public async Task<bool> MountNfs(string server, string folder, TimeSpan timeout, CancellationToken ct)
        {
            await Call("loginNfsServer", new Dictionary<string, object> { ["serverName"] = server }, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            var r = await Call("mountNfsSharedFolder", new Dictionary<string, object> { ["server"] = server, ["folder"] = folder }, timeout, ct).ConfigureAwait(false);
            LastMountAnswer = Reason(r);
            return Success(r);
        }

        /// <summary>Why the last NFS / SMB mount was refused, as the OPPO put it (empty if it gave no reason).</summary>
        public string LastMountAnswer { get; private set; } = "";

        /// <summary>
        /// The reason fields of an OPPO answer ("retInfo", "msg"…), never the whole answer: an SMB mount answer
        /// may echo the credentials.
        /// </summary>
        private static string Reason(Dictionary<string, object> r)
        {
            var parts = r.Where(kv => kv.Key.IndexOf("info", StringComparison.OrdinalIgnoreCase) >= 0
                                      || kv.Key.IndexOf("msg", StringComparison.OrdinalIgnoreCase) >= 0
                                      || kv.Key.IndexOf("err", StringComparison.OrdinalIgnoreCase) >= 0
                                      || kv.Key.IndexOf("reason", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(kv => Convert.ToString(kv.Value, CultureInfo.InvariantCulture)?.Trim())
                .Where(v => !string.IsNullOrEmpty(v) && !string.Equals(v, "null", StringComparison.OrdinalIgnoreCase))
                .Distinct();
            return string.Join(" / ", parts);
        }

        /// <summary>File server seen by the OPPO on the network.</summary>
        public sealed class NetworkServer
        {
            public string Protocol { get; set; }   // nfs or smb
            public string Name { get; set; }
            public string Address { get; set; }
        }

        /// <summary>NFS and SMB servers the OPPO detects on the network ("getdevicelist").</summary>
        public async Task<List<NetworkServer>> ListServers(CancellationToken ct)
        {
            // Emby's JSON decoder cannot read a list of objects inside a generic dictionary,
            // so dedicated classes are used.
            string body = Encoding.UTF8.GetString(await Raw("getdevicelist", null, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false));
            _logger.Debug("OPPO getdevicelist → {0}", body.Length > 300 ? body.Substring(0, 300) : body);
            var response = _json.DeserializeFromString<DeviceListResponse>(body);
            return (response?.devicelist ?? new List<DeviceEntry>())
                .Where(d => d.sub_type == "nfs" || d.sub_type == "cifs")
                .Select(d => new NetworkServer { Protocol = d.sub_type == "nfs" ? "nfs" : "smb", Name = d.name, Address = d.path })
                .ToList();
        }

        public sealed class DeviceListResponse
        {
            public bool success { get; set; }
            public List<DeviceEntry> devicelist { get; set; }
        }

        public sealed class DeviceEntry
        {
            public string sub_type { get; set; }
            public string name { get; set; }
            public string path { get; set; }
        }

        /// <summary>HTTP call returning the raw response (some OPPO lists are binary).</summary>
        private async Task<byte[]> Raw(string endpoint, object args, TimeSpan timeout, CancellationToken ct)
        {
            string url = "http://" + _host + ":436/" + endpoint;
            if (args != null) url += "?" + Uri.EscapeDataString(_json.SerializeToString(args));
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = (int)timeout.TotalMilliseconds;
            request.ReadWriteTimeout = request.Timeout;
            using (ct.Register(request.Abort))
            using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
            using (var ms = new MemoryStream())
            {
                await response.GetResponseStream().CopyToAsync(ms).ConfigureAwait(false);
                return ms.ToArray();
            }
        }

        /// <summary>Exports of an NFS server as the OPPO sees them ("volume1/Movies"…).</summary>
        public async Task<List<string>> ListNfsExports(string server, CancellationToken ct)
        {
            await Call("loginNfsServer", new Dictionary<string, object> { ["serverName"] = server }, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            return ParseExportList(await Raw("getNfsShareFolderlist", null, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false));
        }

        /// <summary>
        /// Binary lists from the OPPO (NFS exports, folder contents): optional "FLAG" header,
        /// then for each entry: length (uint32 LE), UTF-8 name, 8 bytes of attributes.
        /// If the format is unexpected, falls back to runs of printable characters.
        /// </summary>
        internal static List<string> ParseExportList(byte[] data)
        {
            var result = new List<string>();
            int p = data.Length >= 4 && data[0] == 'F' && data[1] == 'L' && data[2] == 'A' && data[3] == 'G' ? 4 : 0;
            while (p + 4 <= data.Length)
            {
                int len = BitConverter.ToInt32(data, p);
                if (len <= 0 || len > 1024 || p + 4 + len > data.Length) break;
                result.Add(Encoding.UTF8.GetString(data, p + 4, len));
                p += 4 + len + 8;
            }
            if (result.Count > 0) return result;

            var sb = new StringBuilder();
            foreach (byte b in data)
            {
                if (b >= 0x20) { sb.Append((char)b); continue; }
                if (sb.Length >= 2) result.Add(sb.ToString().Trim());
                sb.Clear();
            }
            if (sb.Length >= 2) result.Add(sb.ToString().Trim());
            return result.Where(x => x.Length > 0).Distinct().ToList();
        }

        /// <summary>Mounts the share then lists its root, to check a mapping. Messages are in French if <paramref name="fr"/>.</summary>
        public async Task<(bool Ok, string Message, List<string> Entries)> TestMapping(string protocol, string server, string share,
            string user, string password, bool fr, CancellationToken ct)
        {
            bool smb = string.Equals(protocol, "smb", StringComparison.OrdinalIgnoreCase);
            bool mounted = smb
                ? await MountSmb(server, share, user, password, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false)
                : await MountNfs(server, share, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            if (!mounted)
            {
                string answer = string.IsNullOrEmpty(LastMountAnswer) ? L10n.T(fr, "no reason given", "sans raison donnée") : "\"" + LastMountAnswer + "\"";
                _logger.Info("OPPO: {0} mount of {1} / {2} refused by the player: {3}", smb ? "SMB" : "NFS", server, share, answer);
                return (false, L10n.T(fr, "Mount refused by the OPPO (" + answer + "). ", "Montage refusé par l'OPPO (" + answer + "). ")
                    + (smb
                        ? L10n.T(fr, "Check the server, the share name (as shared, not a drive path) and the username / password: Windows refuses guests. "
                                     + "The OPPO only speaks SMB 1, which Windows 10/11 disables: with an Emby server on Windows, prefer \"Play over DLNA\" (oppo-dv-mkv mod).",
                                 "Vérifiez le serveur, le nom du partage (tel que partagé, pas un chemin de disque) et l'utilisateur / mot de passe : Windows refuse les invités. "
                                     + "L'OPPO ne parle que SMB 1, que Windows 10/11 désactive : avec un serveur Emby sous Windows, préférez « Lire par DLNA » (mod oppo-dv-mkv).")
                        : L10n.T(fr, "Check the export path and that the NFS server lets the OPPO's address in (Windows has no NFS server).",
                                 "Vérifiez le chemin de l'export et que le serveur NFS autorise l'adresse de l'OPPO (Windows n'a pas de serveur NFS).")),
                    new List<string>());
            }
            var raw = await Raw("getfilelist", new Dictionary<string, object>
            {
                ["path"] = smb ? "/mnt/cifs1" : "/mnt/nfs1",
                ["fileType"] = 1,
                ["mediaType"] = 3,
                ["flag"] = 1
            }, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            var names = ParseExportList(raw).Where(n => !n.StartsWith("#", StringComparison.Ordinal) && !n.StartsWith("@", StringComparison.Ordinal)).ToList();
            string message = names.Count > 0
                ? L10n.T(fr, "Share mounted, " + names.Count + " item(s) visible", "Partage monté, " + names.Count + " élément(s) visibles")
                : L10n.T(fr, "Share mounted but empty", "Partage monté mais vide");
            return (true, message, names.Take(10).ToList());
        }

        /// <summary>
        /// Powers the OPPO on (IP command) and waits for its HTTP interface to respond. While it does not,
        /// the control-app login notice is sent every 2 s: after a power cut the player keeps port 436
        /// closed until it receives that notice, even when it is on.
        /// </summary>
        public async Task<bool> WakeAndWait(TimeSpan timeout, CancellationToken ct)
        {
            try { await PowerOn(ct).ConfigureAwait(false); } catch (Exception ex) when (!(ex is OperationCanceledException)) { _logger.Debug("OPPO #PON: {0}", ex.Message); }
            return await WaitForHttp(timeout, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Waits for the HTTP interface (port 436) to respond, sending the control-app login notice every 2 s
        /// while it does not. Also needed when the player is on: after a power cut or a restart it answers
        /// IP control but keeps port 436 closed until a control app announces itself.
        /// </summary>
        public async Task<bool> WaitForHttp(TimeSpan timeout, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + timeout;
            bool notified = false;
            while (true)
            {
                try
                {
                    var r = await Call("getmainfirmwareversion", null, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                    if (Success(r)) return true;
                }
                catch (Exception ex) when (!(ex is OperationCanceledException)) { }
                if (DateTime.UtcNow >= deadline) return false;
                if (!notified) _logger.Info("OPPO: HTTP interface (port {0}) not answering, sending the control-app login notice (UDP {1})", 436, 7624);
                notified = true;
                NotifyLogin();
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Announces a control app to the OPPO, as the official app does when it starts: the datagram
        /// "NOTIFY OREMOTE LOGIN" on UDP port 7624. It makes the player open its HTTP interface (port 436).
        /// </summary>
        private void NotifyLogin()
        {
            try
            {
                using (var udp = new UdpClient())
                {
                    byte[] message = Encoding.ASCII.GetBytes("NOTIFY OREMOTE LOGIN");
                    udp.Send(message, message.Length, _host, 7624);
                }
            }
            catch (Exception ex) { _logger.Debug("OPPO login notice: {0}", ex.Message); }
        }

        /// <summary>Mounts an SMB/CIFS share; the OPPO then exposes it under /mnt/cifs1.</summary>
        public async Task<bool> MountSmb(string server, string share, string user, string password, TimeSpan timeout, CancellationToken ct)
        {
            bool withId = !string.IsNullOrEmpty(user);
            if (!withId) await Call("loginSambaWithOutID", new Dictionary<string, object> { ["serverName"] = server }, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            var r = await Call("mountSharedFolder", new Dictionary<string, object>
            {
                ["server"] = server,
                ["bWithID"] = withId ? 1 : 0,
                ["folder"] = share,
                ["userName"] = user ?? "",
                ["password"] = password ?? "",
                ["bRememberID"] = 1
            }, timeout, ct, logRequest: false).ConfigureAwait(false);
            LastMountAnswer = Reason(r);
            return Success(r);
        }

        /// <summary>Plays a file (ISO, MKV, M2TS…) mounted under /mnt/nfs1 or /mnt/cifs1.</summary>
        public async Task<bool> PlayFile(string mountedPath, string server, CancellationToken ct)
        {
            var r = await Call("playnormalfile", new Dictionary<string, object>
            {
                ["path"] = mountedPath,
                ["index"] = 0,
                ["type"] = 1,
                ["appDeviceType"] = 2,
                ["extraNetPath"] = server,
                ["playMode"] = 0
            }, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            return Success(r);
        }

        /// <summary>Plays a Blu-ray folder (containing BDMV) mounted under /mnt/nfs1.</summary>
        public async Task<bool> PlayBlurayFolder(string mountedFolder, CancellationToken ct)
        {
            var r = await Call("checkfolderhasBDMV", new Dictionary<string, object> { ["folderpath"] = mountedFolder }, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            return Success(r);
        }

        public async Task<OppoStatus> GetStatus(CancellationToken ct)
        {
            var status = new OppoStatus();
            try
            {
                var g = await Call("getglobalinfo", null, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                status.Reachable = Success(g);
                status.IsPlaying = Flag(g, "is_video_playing") || Flag(g, "is_bdmv_playing") || Flag(g, "is_disc_playing");
                if (status.IsPlaying)
                {
                    var t = await Call("getplayingtime", null, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                    if (Success(t))
                    {
                        status.Position = TimeSpan.FromSeconds(Number(t, "cur_time"));
                        status.Duration = TimeSpan.FromSeconds(Number(t, "total_time"));
                    }
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Debug("OPPO unreachable: {0}", ex.Message);
                // Port 436 refused: after a restart or a power cut the player keeps its HTTP interface closed until
                // a control app announces itself, even when it is on and playing. Without it the web remote lost the
                // position, the chapters and the output line after an SSH restart (2026-11-23).
                if (ex is WebException web && web.Status == WebExceptionStatus.ConnectFailure && DateTime.UtcNow - _lastNotice > TimeSpan.FromSeconds(5))
                {
                    if (_lastNotice == DateTime.MinValue || DateTime.UtcNow - _lastNotice > TimeSpan.FromMinutes(5))
                        _logger.Info("OPPO: HTTP interface (port {0}) closed, sending the control-app login notice (UDP {1})", 436, 7624);
                    _lastNotice = DateTime.UtcNow;
                    NotifyLogin();
                }
            }
            return status;
        }

        private static DateTime _lastNotice = DateTime.MinValue;

        /// <summary>Entry of an OPPO track menu. Audio: index 1…n. Subtitles: 0 = "Off", then 1…n.</summary>
        public sealed class TrackEntry
        {
            public string name { get; set; }
            public int index { get; set; }
            public bool selected { get; set; }
        }

        public sealed class TrackMenuResponse
        {
            public bool success { get; set; }
            public List<TrackEntry> audio_list { get; set; }
            public List<TrackEntry> subtitle_list { get; set; }
        }

        /// <summary>Audio tracks (<paramref name="audio"/>) or subtitles offered by the OPPO for the current title.</summary>
        public async Task<List<TrackEntry>> GetTrackMenu(bool audio, CancellationToken ct)
        {
            try
            {
                string body = Encoding.UTF8.GetString(await Raw(audio ? "getaudiomenulist" : "getsubtitlemenulist", null, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false));
                var r = _json.DeserializeFromString<TrackMenuResponse>(body);
                return (audio ? r?.audio_list : r?.subtitle_list) ?? new List<TrackEntry>();
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Debug("OPPO: track menu unavailable ({0})", ex.Message);
                return new List<TrackEntry>();
            }
        }

        /// <summary>Current chapter and chapter count ("#QCH"), or null if the OPPO does not report it.</summary>
        public async Task<string> QueryChapter(CancellationToken ct)
        {
            try
            {
                string answer = await Command("QCH", ct).ConfigureAwait(false);
                var m = System.Text.RegularExpressions.Regex.Match(answer, @"^@QCH OK\s+(\d+)\s*/\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!m.Success) return null;
                int current = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), total = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                return total > 0 ? current + " / " + total : null;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException)) { return null; }
        }

        /// <summary>Number of audio tracks as the OPPO lists them (disc order).</summary>
        public async Task<int> CountAudioTracks(CancellationToken ct)
        {
            var r = await Call("getaudiomenulist", null, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            return r.TryGetValue("audio_list", out var list) && list is System.Collections.IEnumerable e ? Count(e) : 0;
        }

        private static int Count(System.Collections.IEnumerable e) { int n = 0; foreach (var _ in e) n++; return n; }

        /// <summary>Raw response of a track menu (for the log), truncated.</summary>
        public async Task<string> DescribeMenu(string endpoint, CancellationToken ct)
        {
            try
            {
                var r = await Call(endpoint, null, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                string text = _json.SerializeToString(r);
                return text.Length > 600 ? text.Substring(0, 600) + "…" : text;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException)) { return "unavailable (" + ex.Message + ")"; }
        }

        /// <param name="oppoIndex">Index in the OPPO's list: 1 = first audio track…</param>
        public async Task<bool> SelectAudio(int oppoIndex, CancellationToken ct)
        {
            var r = await Call("setaudiomenulist", new Dictionary<string, object> { ["cur_index"] = oppoIndex }, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            return Success(r);
        }

        /// <param name="oppoIndex">Index in the OPPO's list: 0 = "Off", 1 = first subtitles…</param>
        public async Task<bool> SelectSubtitle(int oppoIndex, CancellationToken ct)
        {
            var r = await Call("setsubttmenulist", new Dictionary<string, object> { ["cur_index"] = oppoIndex }, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            return Success(r);
        }

        // ---------------------------------------------------------------- IP 23

        /// <summary>
        /// Where the IP control protocol goes: the player itself (port 23), or the address set in the
        /// configuration, e.g. an oppo-multiplexer shared with other controllers.
        /// </summary>
        private (string Host, int Port) IpControlEndpoint() => IpControlEndpoint(Plugin.Instance?.Configuration?.IpControlAddress, _host);

        /// <summary>"host" or "host:port" of the IP control link (an oppo-multiplexer), or the player itself on port 23.</summary>
        internal static (string Host, int Port) IpControlEndpoint(string ipControlAddress, string playerHost)
        {
            string address = (ipControlAddress ?? "").Trim();
            if (address.Length == 0) return (playerHost, 23);
            int colon = address.LastIndexOf(':');
            if (colon > 0 && address.IndexOf(':') == colon
                && int.TryParse(address.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int port)
                && port > 0 && port < 65536)
                return (address.Substring(0, colon), port);
            return (address, 23);
        }

        public enum ProbeResult { Ok, Refused, Timeout, Closed, Silent, Unexpected, UnknownHost, NoRoute, Telnet }

        /// <summary>
        /// Diagnoses the IP control link for the settings page: connects to host:port, sends #QPW and says what
        /// happened (refused, no answer, connection closed, answer…). Uses its own connection. #QPW because a player
        /// in network standby answers it ("@OK OFF") but not #QPL (2026-11-20).
        /// </summary>
        public static async Task<(ProbeResult Result, string Answer, string Error)> Probe(string host, int port, CancellationToken ct)
        {
            using (var tcp = new TcpClient())
            {
                try
                {
                    var connect = tcp.ConnectAsync(host, port);
                    if (await Task.WhenAny(connect, Task.Delay(5000, ct)).ConfigureAwait(false) != connect) return (ProbeResult.Timeout, null, null);
                    await connect.ConfigureAwait(false);
                }
                catch (SocketException ex)
                {
                    switch (ex.SocketErrorCode)
                    {
                        case SocketError.ConnectionRefused: return (ProbeResult.Refused, null, ex.Message);
                        case SocketError.TimedOut: return (ProbeResult.Timeout, null, ex.Message);
                        case SocketError.HostNotFound: case SocketError.NoData: case SocketError.TryAgain: return (ProbeResult.UnknownHost, null, ex.Message);
                        default: return (ProbeResult.NoRoute, null, ex.Message);
                    }
                }
                var stream = tcp.GetStream();
                var payload = Encoding.ASCII.GetBytes("#QPW\r");
                try { await stream.WriteAsync(payload, 0, payload.Length, ct).ConfigureAwait(false); }
                catch (IOException ex) { return (ProbeResult.Closed, null, ex.Message); }
                var buffer = new byte[512];
                var received = new StringBuilder();
                // 12 s: through an oppo-multiplexer, an unanswered query of the plugin (#QPL while the player is in
                // standby) holds the link for 10 s before this one is answered.
                var deadline = DateTime.UtcNow.AddSeconds(12);
                while (DateTime.UtcNow < deadline)
                {
                    var read = stream.ReadAsync(buffer, 0, buffer.Length, ct);
                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero || await Task.WhenAny(read, Task.Delay(left, ct)).ConfigureAwait(false) != read) break;
                    int count;
                    try { count = read.Result; }
                    catch (AggregateException ex) { return (ProbeResult.Closed, received.ToString().Trim(), ex.InnerException?.Message); }
                    if (count == 0) break;
                    // A Telnet server opens with IAC (0xFF) option negotiation: the jailbreak's Telnet, offered on port
                    // 23 when the player's IP Control is off (AVForums, 2026-11-21).
                    if (received.Length == 0 && buffer[0] == TelnetIac) return (ProbeResult.Telnet, null, null);
                    received.Append(Encoding.ASCII.GetString(buffer, 0, count));
                    var line = received.ToString().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())
                        .FirstOrDefault(l => l.StartsWith("@QPW", StringComparison.OrdinalIgnoreCase) || l.StartsWith("@OK", StringComparison.OrdinalIgnoreCase)
                                             || l.StartsWith("@ER", StringComparison.OrdinalIgnoreCase));
                    if (line != null) return (ProbeResult.Ok, line, null);
                }
                if (received.Length > 0) return (ProbeResult.Unexpected, received.ToString().Trim(), null);
                return tcp.Client.Poll(0, SelectMode.SelectRead) && tcp.Client.Available == 0 ? (ProbeResult.Closed, null, null) : (ProbeResult.Silent, null, null);
            }
        }

        /// <summary>Sends an OPPO IP protocol command (e.g. "PAU") and returns the response.</summary>
        private const byte TelnetIac = 0xFF;
        private bool _telnet;   // the last IP-control answer was a Telnet greeting (IP Control off on the player)

        public async Task<string> Command(string code, CancellationToken ct, string argument = null)
        {
            _telnet = false;
            await _ipLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using (var tcp = new TcpClient())
                {
                    var (ipHost, ipPort) = IpControlEndpoint();
                    var connect = tcp.ConnectAsync(ipHost, ipPort);
                    if (await Task.WhenAny(connect, Task.Delay(5000, ct)).ConfigureAwait(false) != connect)
                        throw new TimeoutException("OPPO: IP control " + ipHost + ":" + ipPort + " unreachable");
                    using (var stream = tcp.GetStream())
                    {
                        var payload = Encoding.ASCII.GetBytes("#" + code + (argument == null ? "" : " " + argument) + "\r");
                        await stream.WriteAsync(payload, 0, payload.Length, ct).ConfigureAwait(false);
                        // The OPPO may slip unsolicited messages ("@UTC …") in before its reply:
                        // keep the "@<code> …" line.
                        var buffer = new byte[512];
                        var received = new StringBuilder();
                        string expected = "@" + code;
                        // #PON: up to 12 s, as another client's unanswered query can hold an oppo-multiplexer 10 s.
                        var deadline = DateTime.UtcNow.AddSeconds(code == "PON" ? 12 : 3);
                        while (DateTime.UtcNow < deadline)
                        {
                            var read = stream.ReadAsync(buffer, 0, buffer.Length, ct);
                            var left = deadline - DateTime.UtcNow;
                            if (left <= TimeSpan.Zero || await Task.WhenAny(read, Task.Delay(left, ct)).ConfigureAwait(false) != read || read.Result == 0) break;
                            if (received.Length == 0) _telnet = buffer[0] == TelnetIac;
                            received.Append(Encoding.ASCII.GetString(buffer, 0, read.Result));
                            CaptureEvents(received.ToString());
                            // Verbose modes 2/3 answer "@QPL OK PLAY"; modes 0/1 answer "@OK PLAY". Another
                            // IP-control client (e.g. the Unfolded Circle Oppo driver) may switch the player's
                            // mode, so accept both and return the long form, which every caller parses.
                            var line = received.ToString().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(l => l.Trim())
                                .Select(l => l.StartsWith("@OK", StringComparison.OrdinalIgnoreCase) || l.StartsWith("@ER", StringComparison.OrdinalIgnoreCase)
                                    ? expected + " " + l.Substring(1) : l)
                                .FirstOrDefault(l => l.StartsWith(expected, StringComparison.OrdinalIgnoreCase));
                            if (line != null)
                            {
                                _logger.Debug("OPPO #{0} → {1}", code, line);
                                return line;
                            }
                        }
                        string answer = received.ToString().Trim();
                        _logger.Debug("OPPO #{0} → {1}", code, answer);
                        return answer;
                    }
                }
            }
            finally
            {
                _ipLock.Release();
            }
        }

        /// <summary>
        /// Latest unsolicited messages from the OPPO (verbose mode): actual video output ("UVO", e.g.
        /// "_UHD24_"), aspect ratio ("UAR"), 3D ("U3D"). Key = code without "@".
        /// </summary>
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Events =
            new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly System.Text.RegularExpressions.Regex EventPattern =
            new System.Text.RegularExpressions.Regex(@"@(UVO|UAR|U3D)\s+([^@\r\n]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static void CaptureEvents(string text)
        {
            foreach (System.Text.RegularExpressions.Match m in EventPattern.Matches(text))
            {
                string code = m.Groups[1].Value.ToUpperInvariant(), value = m.Groups[2].Value.Trim();
                // UVO carries two resolutions, the source's then the output's ("_480I60 1080P60", as read by openHAB's
                // OPPO binding): both kept. The others keep their first word.
                Events[code] = code == "UVO" ? value : value.Split(' ')[0];
            }
        }


        /// <summary>
        /// Player state from the IP protocol ("#QPL"): PLAY, PAUSE, STOP, DISC MENU, LOADING,
        /// HOME MENU, MEDIA CENTER, SCREEN SAVER… Empty string if the OPPO does not respond.
        /// </summary>
        public async Task<string> QueryPlayback(CancellationToken ct)
        {
            try
            {
                // In network standby the OPPO answers #QPW ("@OK OFF") but not #QPL, and through an oppo-multiplexer
                // each unanswered #QPL holds the link for 10 s. Polled every few seconds by the web remote, the link
                // was never free and a #PON sent meanwhile was lost: "the OPPO does not start" (2026-11-23). So once
                // #QPL goes unanswered, ask #QPW first until the player is on again.
                if (_maybeOff)
                {
                    string power = await Command("QPW", ct).ConfigureAwait(false);
                    if (power.IndexOf("OFF", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        IpControlAnswers("the player is in standby (#QPW OFF)");
                        return "";
                    }
                    if (power.IndexOf(" ON", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        IpControlAnswers(power.Length == 0 ? "no answer" : "answer \"" + power + "\"");
                        return "";
                    }
                }
                string answer = await Command("QPL", ct).ConfigureAwait(false);
                const string prefix = "@QPL OK ";
                _maybeOff = !answer.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                if (!_maybeOff)
                {
                    IpControlAnswers(null);
                    return answer.Substring(prefix.Length).Trim().ToUpperInvariant();
                }
                IpControlAnswers(_telnet
                    ? "port 23 is a Telnet server (the jailbreak's), not the OPPO's IP control: with the jb_overlay, set USE_TELNET=0 in /mnt/ubi_boot/jb_overlay/config.ini and unplug the player (see the README); check IP Control: On"
                    : answer.Length == 0 ? "no answer" : "answer \"" + answer + "\"");
                return "";
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Debug("OPPO #QPL failed: {0}", ex.Message);
                IpControlAnswers(ex.Message);
                return "";
            }
        }

        private static string _ipFailure;
        // #QPL went unanswered (player off, starting or unreachable): the next state query asks #QPW first.
        private static volatile bool _maybeOff = true;

        /// <summary>
        /// Writes to Emby's log, once per change, why IP control does not answer (it used to be in the debug log
        /// only, so a user stuck on "OPPO unreachable" could not tell why), and when it answers again.
        /// </summary>
        private void IpControlAnswers(string failure)
        {
            string previous = _ipFailure;
            _ipFailure = failure;
            if (failure == null)
            {
                if (previous != null) _logger.Info("OPPO: IP control answering again ({0})", _host);
            }
            else if (failure != previous)
            {
                var (host, port) = IpControlEndpoint();
                _logger.Info("OPPO: no playback state from IP control {0}:{1}: {2}. Normal while the player is off; otherwise see \"Test the connection\" on the OPPO settings page", host, port, failure);
            }
        }

        // ---------------------------------------------------------------- DLNA renderer (UPnP AVTransport)

        /// <summary>
        /// Sends a URL to the player's DLNA renderer and starts it (SetAVTransportURI, then Play). With the
        /// oppo-dv-mkv mod, a disc image (video/iso, ".iso") is played as a disc, menus included.
        /// Returns false if the renderer refused or could not be reached.
        /// </summary>
        public async Task<bool> PlayDlna(string url, string title, long size, CancellationToken ct, string mime = "video/iso")
        {
            try
            {
                // No Stop first: the player is stopped by IP control before (a renderer Stop while the mod plays a
                // disc image wedged the renderer, 2026-11-20).
                string control = await AvTransportControlUrl(ct).ConfigureAwait(false);
                string didl = "<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\">"
                    + "<item id=\"1\" parentID=\"0\" restricted=\"1\"><dc:title>" + Xml(title) + "</dc:title><upnp:class>object.item.videoItem.movie</upnp:class>"
                    + "<res protocolInfo=\"http-get:*:" + mime + ":*\"" + (size > 0 ? " size=\"" + size.ToString(CultureInfo.InvariantCulture) + "\"" : "") + ">" + Xml(url) + "</res></item></DIDL-Lite>";
                if (!await Soap(control, "SetAVTransportURI", "<CurrentURI>" + Xml(url) + "</CurrentURI><CurrentURIMetaData>" + Xml(didl) + "</CurrentURIMetaData>", ct).ConfigureAwait(false))
                    return false;
                return await Soap(control, "Play", "<Speed>1</Speed>", ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Info("OPPO: DLNA renderer unreachable ({0})", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// State of the DLNA renderer (GetTransportInfo): STOPPED, NO_MEDIA_PRESENT, PLAYING, TRANSITIONING…,
        /// or null if it does not answer.
        /// </summary>
        public async Task<string> DlnaTransportState(CancellationToken ct)
        {
            try
            {
                string control = await AvTransportControlUrl(ct).ConfigureAwait(false);
                string body = "<?xml version=\"1.0\" encoding=\"utf-8\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>"
                    + "<u:GetTransportInfo xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\"><InstanceID>0</InstanceID></u:GetTransportInfo></s:Body></s:Envelope>";
                string answer = await HttpText(control, body, "\"urn:schemas-upnp-org:service:AVTransport:1#GetTransportInfo\"", TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                var m = System.Text.RegularExpressions.Regex.Match(answer, @"<CurrentTransportState>([^<]*)</CurrentTransportState>");
                return m.Success ? m.Groups[1].Value.Trim() : null;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Debug("OPPO DLNA GetTransportInfo failed: {0}", ex.Message);
                return null;
            }
        }

        private string _avTransportControl;

        /// <summary>AVTransport control URL, read from the renderer's description (port 49152).</summary>
        private async Task<string> AvTransportControlUrl(CancellationToken ct)
        {
            if (_avTransportControl != null) return _avTransportControl;
            string baseUrl = "http://" + _host + ":49152";
            string description = await HttpText(baseUrl + "/dmr_oppo.xml", null, null, TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
            var m = System.Text.RegularExpressions.Regex.Match(description,
                @"urn:schemas-upnp-org:service:AVTransport:1</serviceType>.*?<controlURL>([^<]+)</controlURL>",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            string path = m.Success ? m.Groups[1].Value.Trim() : "/AVTransport/control";
            _avTransportControl = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : baseUrl + (path.StartsWith("/") ? "" : "/") + path;
            return _avTransportControl;
        }

        private async Task<bool> Soap(string control, string action, string args, CancellationToken ct, bool logErrors = true)
        {
            string body = "<?xml version=\"1.0\" encoding=\"utf-8\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>"
                + "<u:" + action + " xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\"><InstanceID>0</InstanceID>" + args + "</u:" + action + "></s:Body></s:Envelope>";
            try
            {
                await HttpText(control, body, "\"urn:schemas-upnp-org:service:AVTransport:1#" + action + "\"", TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                _logger.Debug("OPPO DLNA {0} → OK", action);
                return true;
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse r)
            {
                string detail;
                using (var reader = new StreamReader(r.GetResponseStream(), Encoding.UTF8)) detail = reader.ReadToEnd();
                if (logErrors) _logger.Info("OPPO DLNA {0} → HTTP {1} {2}", action, (int)r.StatusCode, detail.Length > 300 ? detail.Substring(0, 300) : detail);
                return false;
            }
        }

        private static async Task<string> HttpText(string url, string soapBody, string soapAction, TimeSpan timeout, CancellationToken ct)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = (int)timeout.TotalMilliseconds;
            request.ReadWriteTimeout = request.Timeout;
            if (soapBody != null)
            {
                request.Method = "POST";
                request.ContentType = "text/xml; charset=\"utf-8\"";
                request.Headers["SOAPACTION"] = soapAction;
                byte[] data = Encoding.UTF8.GetBytes(soapBody);
                request.ContentLength = data.Length;
                using (ct.Register(request.Abort))
                using (var stream = await request.GetRequestStreamAsync().ConfigureAwait(false))
                    await stream.WriteAsync(data, 0, data.Length, ct).ConfigureAwait(false);
            }
            using (ct.Register(request.Abort))
            using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                return await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        private static string Xml(string s) => System.Security.SecurityElement.Escape(s ?? "");

        public Task Pause(CancellationToken ct) => Command("PAU", ct);
        public Task Resume(CancellationToken ct) => Command("PLA", ct);
        public Task Stop(CancellationToken ct) => Command("STP", ct);
        public Task PowerOn(CancellationToken ct) => Command("PON", ct);

        /// <summary>Seeks to a time in the title (IP protocol command "SRH T hh:mm:ss").</summary>
        public Task<string> Seek(TimeSpan position, CancellationToken ct) =>
            Command("SRH", ct, "T " + ((int)position.TotalHours).ToString("00", CultureInfo.InvariantCulture) + position.ToString("\\:mm\\:ss", CultureInfo.InvariantCulture));
    }
}
