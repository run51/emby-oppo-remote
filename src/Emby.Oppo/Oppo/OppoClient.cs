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
            return Success(r);
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
            if (!mounted) return (false, L10n.T(fr, "Mount refused by the OPPO", "Montage refusé par l'OPPO"), new List<string>());
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
            var deadline = DateTime.UtcNow + timeout;
            bool notified = false;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var r = await Call("getmainfirmwareversion", null, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                    if (Success(r)) return true;
                }
                catch (Exception ex) when (!(ex is OperationCanceledException)) { }
                if (!notified) _logger.Info("OPPO: HTTP interface (port {0}) not answering, sending the control-app login notice (UDP {1})", 436, 7624);
                notified = true;
                NotifyLogin();
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
            return false;
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
            }
            return status;
        }

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
        private (string Host, int Port) IpControlEndpoint()
        {
            string address = (Plugin.Instance?.Configuration?.IpControlAddress ?? "").Trim();
            if (address.Length == 0) return (_host, 23);
            int colon = address.LastIndexOf(':');
            if (colon > 0 && address.IndexOf(':') == colon
                && int.TryParse(address.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int port)
                && port > 0 && port < 65536)
                return (address.Substring(0, colon), port);
            return (address, 23);
        }

        /// <summary>Sends an OPPO IP protocol command (e.g. "PAU") and returns the response.</summary>
        public async Task<string> Command(string code, CancellationToken ct, string argument = null)
        {
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
                        var deadline = DateTime.UtcNow.AddSeconds(3);
                        while (DateTime.UtcNow < deadline)
                        {
                            var read = stream.ReadAsync(buffer, 0, buffer.Length, ct);
                            var left = deadline - DateTime.UtcNow;
                            if (left <= TimeSpan.Zero || await Task.WhenAny(read, Task.Delay(left, ct)).ConfigureAwait(false) != read || read.Result == 0) break;
                            received.Append(Encoding.ASCII.GetString(buffer, 0, read.Result));
                            CaptureEvents(received.ToString());
                            var line = received.ToString().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(l => l.Trim())
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
                Events[m.Groups[1].Value.ToUpperInvariant()] = m.Groups[2].Value.Trim().Split(' ')[0];
        }

        /// <summary>
        /// Player state from the IP protocol ("#QPL"): PLAY, PAUSE, STOP, DISC MENU, LOADING,
        /// HOME MENU, MEDIA CENTER, SCREEN SAVER… Empty string if the OPPO does not respond.
        /// </summary>
        public async Task<string> QueryPlayback(CancellationToken ct)
        {
            try
            {
                string answer = await Command("QPL", ct).ConfigureAwait(false);
                const string prefix = "@QPL OK ";
                return answer.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? answer.Substring(prefix.Length).Trim().ToUpperInvariant() : "";
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Debug("OPPO #QPL failed: {0}", ex.Message);
                return "";
            }
        }

        public Task Pause(CancellationToken ct) => Command("PAU", ct);
        public Task Resume(CancellationToken ct) => Command("PLA", ct);
        public Task Stop(CancellationToken ct) => Command("STP", ct);
        public Task PowerOn(CancellationToken ct) => Command("PON", ct);

        /// <summary>Seeks to a time in the title (IP protocol command "SRH T hh:mm:ss").</summary>
        public Task<string> Seek(TimeSpan position, CancellationToken ct) =>
            Command("SRH", ct, "T " + ((int)position.TotalHours).ToString("00", CultureInfo.InvariantCulture) + position.ToString("\\:mm\\:ss", CultureInfo.InvariantCulture));
    }
}
