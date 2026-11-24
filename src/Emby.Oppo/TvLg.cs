using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace Emby.Oppo
{
    /// <summary>One external input of the TV (HDMI_1…), as the TV lists it.</summary>
    public sealed class TvInput
    {
        public string Id { get; set; }
        /// <summary>The name given on the TV (e.g. "OPPO"), or the input's own name.</summary>
        public string Label { get; set; }
        /// <summary>A device is plugged in (and seen by the TV).</summary>
        public bool Connected { get; set; }
    }

    /// <summary>
    /// LG webOS TVs over their local network API (SSAP, a websocket on port 3001 with TLS, or 3000 on older models):
    /// pairing (the TV asks once to allow the connection and gives a client key), the list of external inputs and the
    /// switch to one of them. The registration manifest is the one used by the open-source webOS clients (aiowebostv,
    /// Apache 2.0). Wake-on-LAN to turn the TV on, with the MAC address read at pairing.
    /// </summary>
    internal sealed class TvLg : IDisposable
    {
        private const string Signature =
            "eyJhbGdvcml0aG0iOiJSU0EtU0hBMjU2Iiwia2V5SWQiOiJ0ZXN0LXNpZ25pbm" +
            "ctY2VydCIsInNpZ25hdHVyZVZlcnNpb24iOjF9.hrVRgjCwXVvE2OOSpDZ58hR" +
            "+59aFNwYDyjQgKk3auukd7pcegmE2CzPCa0bJ0ZsRAcKkCTJrWo5iDzNhMBWRy" +
            "aMOv5zWSrthlf7G128qvIlpMT0YNY+n/FaOHE73uLrS/g7swl3/qH/BGFG2Hu4" +
            "RlL48eb3lLKqTt2xKHdCs6Cd4RMfJPYnzgvI4BNrFUKsjkcu+WD4OO2A27Pq1n" +
            "50cMchmcaXadJhGrOqH5YmHdOCj5NSHzJYrsW0HPlpuAx/ECMeIZYDh6RMqaFM" +
            "2DXzdKX9NmmyqzJ3o/0lkk/N97gfVRLW5hA29yeAwaCViZNCP8iC9aO0q9fQoj" +
            "oa7NQnAtw==";

        private static readonly string[] Permissions =
        {
            "LAUNCH", "LAUNCH_WEBAPP", "APP_TO_APP", "CLOSE", "TEST_OPEN", "TEST_PROTECTED", "CONTROL_AUDIO",
            "CONTROL_DISPLAY", "CONTROL_INPUT_JOYSTICK", "CONTROL_INPUT_MEDIA_RECORDING", "CONTROL_INPUT_MEDIA_PLAYBACK",
            "CONTROL_INPUT_TV", "CONTROL_POWER", "CONTROL_TV_SCREEN", "READ_APP_STATUS", "READ_CURRENT_CHANNEL",
            "READ_INPUT_DEVICE_LIST", "READ_NETWORK_STATE", "READ_RUNNING_APPS", "READ_TV_CHANNEL_LIST",
            "WRITE_NOTIFICATION_TOAST", "READ_POWER_STATE", "READ_COUNTRY_INFO", "CONTROL_INPUT_TEXT",
            "CONTROL_MOUSE_AND_KEYBOARD", "READ_INSTALLED_APPS", "READ_SETTINGS"
        };

        private static readonly string[] SignedPermissions =
        {
            "TEST_SECURE", "CONTROL_INPUT_TEXT", "CONTROL_MOUSE_AND_KEYBOARD", "READ_INSTALLED_APPS", "READ_LGE_SDX",
            "READ_NOTIFICATIONS", "SEARCH", "WRITE_SETTINGS", "WRITE_NOTIFICATION_ALERT", "CONTROL_POWER",
            "READ_CURRENT_CHANNEL", "READ_RUNNING_APPS", "READ_UPDATE_INFO", "UPDATE_FROM_REMOTE_APP",
            "READ_LGE_TV_INPUT_EVENTS", "READ_TV_CURRENT_TIME"
        };

        /// <summary>The scheme that connected last (newer TVs only accept wss; older ones only ws).</summary>
        private static volatile bool _preferWs;
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        private readonly string _host;
        private readonly IJsonSerializer _json;
        private readonly ILogger _logger;
        private ClientWebSocket _ws;
        private int _nextId;

        public TvLg(string host, IJsonSerializer json, ILogger logger)
        {
            _host = (host ?? "").Trim();
            _json = json;
            _logger = logger;
        }

        public void Dispose()
        {
            try { _ws?.Abort(); } catch { }
            _ws?.Dispose();
        }

        private sealed class Message
        {
            public string type { get; set; }
            public string id { get; set; }
            public string error { get; set; }
        }

        private sealed class InputsMessage
        {
            public InputsPayload payload { get; set; }
        }

        private sealed class InputsPayload
        {
            public bool returnValue { get; set; }
            public List<Device> devices { get; set; }
        }

        private sealed class Device
        {
            public string id { get; set; }
            public string label { get; set; }
            public bool connected { get; set; }
        }

        private sealed class InfoMessage
        {
            public InfoPayload payload { get; set; }
        }

        private sealed class InfoPayload
        {
            public NetInfo wiredInfo { get; set; }
            public NetInfo wifiInfo { get; set; }
        }

        private sealed class NetInfo
        {
            public string macAddress { get; set; }
            public string state { get; set; }
        }

        /// <summary>Opens the websocket (wss on 3001, else ws on 3000).</summary>
        private async Task Open(CancellationToken ct)
        {
            if (_host.Length == 0) throw new ArgumentException("TV address not set");
            var order = _preferWs ? new[] { false, true } : new[] { true, false };
            Exception last = null;
            foreach (bool secure in order)
            {
                var ws = new ClientWebSocket();
                if (secure) AcceptAnyCertificate(ws.Options);
                try
                {
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        timeout.CancelAfter(TimeSpan.FromSeconds(5));
                        await ws.ConnectAsync(new Uri(secure ? "wss://" + _host + ":3001" : "ws://" + _host + ":3000"), timeout.Token).ConfigureAwait(false);
                    }
                    _ws = ws;
                    _preferWs = !secure;
                    return;
                }
                catch (Exception ex)
                {
                    ws.Dispose();
                    last = ex;
                    if (ct.IsCancellationRequested) throw;
                }
            }
            throw new IOException("the TV does not answer on ports 3001/3000 (" + (last?.Message ?? "") + ")", last);
        }

        /// <summary>The TV's certificate is self-signed. The option exists on .NET Core 2.1+ (Emby's runtime), not in netstandard2.0.</summary>
        private static void AcceptAnyCertificate(ClientWebSocketOptions options)
        {
            var property = options.GetType().GetProperty("RemoteCertificateValidationCallback");
            if (property != null)
                property.SetValue(options, (System.Net.Security.RemoteCertificateValidationCallback)((s, c, ch, e) => true));
        }

        private async Task Send(string text, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }

        private async Task<string> Receive(TimeSpan timeout, CancellationToken ct)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(timeout);
                var buffer = new byte[16384];
                using (var ms = new MemoryStream())
                {
                    while (true)
                    {
                        var r = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token).ConfigureAwait(false);
                        if (r.MessageType == WebSocketMessageType.Close) throw new IOException("the TV closed the connection");
                        ms.Write(buffer, 0, r.Count);
                        if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.ToArray());
                    }
                }
            }
        }

        private Message Parse(string text)
        {
            try { return _json.DeserializeFromString<Message>(text) ?? new Message(); }
            catch { return new Message(); }
        }

        private static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char ch in s ?? "")
            {
                if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
                else if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4"));
                else sb.Append(ch);
            }
            return sb.Append('"').ToString();
        }

        private string Registration(string clientKey, bool signed)
        {
            string List(IEnumerable<string> items) => "[" + string.Join(",", items.Select(Quote)) + "]";
            var sb = new StringBuilder();
            sb.Append("{\"type\":\"register\",\"id\":\"register_0\",\"payload\":{\"forcePairing\":false,\"pairingType\":\"PROMPT\",");
            if (!string.IsNullOrEmpty(clientKey)) sb.Append("\"client-key\":").Append(Quote(clientKey)).Append(',');
            sb.Append("\"manifest\":{\"appVersion\":\"1.1\",\"manifestVersion\":1,");
            if (signed)
            {
                sb.Append("\"permissions\":").Append(List(Permissions)).Append(',');
                sb.Append("\"signatures\":[{\"signature\":").Append(Quote(Signature)).Append(",\"signatureVersion\":1}],");
                sb.Append("\"signed\":{\"appId\":\"com.lge.test\",\"created\":\"20140509\",");
                sb.Append("\"localizedAppNames\":{\"\":\"LG Remote App\"},\"localizedVendorNames\":{\"\":\"LG Electronics\"},");
                sb.Append("\"permissions\":").Append(List(SignedPermissions)).Append(',');
                sb.Append("\"serial\":\"2f930e2d2cfe083771f68e4fe7bb07\",\"vendorId\":\"com.lge\"}");
            }
            else
            {
                sb.Append("\"permissions\":").Append(List(Permissions.Concat(SignedPermissions).Distinct().OrderBy(p => p, StringComparer.Ordinal)));
            }
            sb.Append("}}}");
            return sb.ToString();
        }

        private static readonly Regex ClientKey = new Regex("\"client-key\"\\s*:\\s*\"([^\"]+)\"");

        /// <summary>
        /// Connects and registers. With no key (or a key the TV no longer knows), the TV shows its "allow this device"
        /// prompt; the user has <paramref name="promptWait"/> to accept it with the TV remote. Returns the client key.
        /// </summary>
        public async Task<string> Connect(string clientKey, TimeSpan promptWait, CancellationToken ct)
        {
            await Open(ct).ConfigureAwait(false);
            // Newer firmware wants the system info asked before the registration; older ones answer or ignore it.
            try
            {
                await Send("{\"id\":\"get_sys_info\",\"type\":\"request\",\"uri\":\"ssap://system/getSystemInfo\",\"payload\":{}}", ct).ConfigureAwait(false);
                await Receive(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }

            foreach (bool signed in new[] { true, false })
            {
                await Send(Registration(clientKey, signed), ct).ConfigureAwait(false);
                var deadline = DateTime.UtcNow + promptWait;
                while (true)
                {
                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) throw new TimeoutException("the connection was not allowed on the TV in time");
                    string text = await Receive(left, ct).ConfigureAwait(false);
                    var m = Parse(text);
                    if (m.type == "registered")
                    {
                        var key = ClientKey.Match(text);
                        return key.Success ? key.Groups[1].Value : clientKey;
                    }
                    if (m.type == "error")
                    {
                        if (signed && (m.error ?? "").IndexOf("denied", StringComparison.OrdinalIgnoreCase) < 0) break;   // retried unsigned
                        throw new UnauthorizedAccessException(string.IsNullOrEmpty(m.error) ? "the TV refused the connection" : m.error);
                    }
                    // "response" with pairingType PROMPT: the TV shows its prompt; the next message is the answer.
                }
            }
            throw new UnauthorizedAccessException("the TV refused the registration");
        }

        private async Task<string> Request(string uri, string payload, CancellationToken ct)
        {
            string id = "req_" + Interlocked.Increment(ref _nextId);
            await Send("{\"id\":\"" + id + "\",\"type\":\"request\",\"uri\":\"ssap://" + uri + "\",\"payload\":" + (payload ?? "{}") + "}", ct).ConfigureAwait(false);
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (true)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) throw new TimeoutException("the TV did not answer " + uri);
                string text = await Receive(left, ct).ConfigureAwait(false);
                var m = Parse(text);
                if (m.id != id) continue;
                if (m.type == "error") throw new InvalidOperationException(string.IsNullOrEmpty(m.error) ? "the TV refused " + uri : m.error);
                return text;
            }
        }

        public async Task<List<TvInput>> Inputs(CancellationToken ct)
        {
            string text = await Request("tv/getExternalInputList", null, ct).ConfigureAwait(false);
            var devices = _json.DeserializeFromString<InputsMessage>(text)?.payload?.devices ?? new List<Device>();
            return devices.Where(d => !string.IsNullOrEmpty(d.id))
                .Select(d => new TvInput { Id = d.id, Label = string.IsNullOrWhiteSpace(d.label) ? d.id : d.label.Trim(), Connected = d.connected })
                .ToList();
        }

        public Task SwitchInput(string inputId, CancellationToken ct) =>
            Request("tv/switchInput", "{\"inputId\":" + Quote(inputId) + "}", ct);

        /// <summary>The MAC address of the TV's active network interface (for Wake-on-LAN), or null.</summary>
        public async Task<string> MacAddress(CancellationToken ct)
        {
            try
            {
                string text = await Request("com.webos.service.connectionmanager/getinfo", null, ct).ConfigureAwait(false);
                var p = _json.DeserializeFromString<InfoMessage>(text)?.payload;
                var nic = p?.wiredInfo?.state == "connected" ? p.wiredInfo : p?.wifiInfo?.state == "connected" ? p.wifiInfo : p?.wiredInfo ?? p?.wifiInfo;
                return string.IsNullOrWhiteSpace(nic?.macAddress) ? null : nic.macAddress.Trim();
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                return null;
            }
        }

        /// <summary>Wake-on-LAN magic packet (broadcast, port 9). The TV needs its "Turn on via Wi-Fi/LAN" setting.</summary>
        public static void Wake(string mac)
        {
            var hex = Regex.Replace(mac ?? "", "[^0-9A-Fa-f]", "");
            if (hex.Length != 12) return;
            var bytes = Enumerable.Range(0, 6).Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16)).ToArray();
            var packet = new byte[102];
            for (int i = 0; i < 6; i++) packet[i] = 0xFF;
            for (int i = 1; i <= 16; i++) Buffer.BlockCopy(bytes, 0, packet, i * 6, 6);
            using (var udp = new UdpClient())
            {
                udp.EnableBroadcast = true;
                udp.Send(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));
            }
        }

        /// <summary>
        /// When a film starts (option "switch the TV to the OPPO's input"): switches the configured input, retrying for up
        /// to a minute while the TV starts up (turned on by Wake-on-LAN, the Unfolded Circle remote or HDMI-CEC).
        /// </summary>
        public static async Task SwitchOnPlay(IJsonSerializer json, ILogger logger)
        {
            var c = Plugin.Instance.Configuration;
            if (!c.TvSwitchOnPlay || string.IsNullOrWhiteSpace(c.TvAddress) || string.IsNullOrWhiteSpace(c.TvClientKey) || string.IsNullOrWhiteSpace(c.TvInputId))
                return;
            if (!await Gate.WaitAsync(0).ConfigureAwait(false)) return;   // already switching
            try
            {
                var until = DateTime.UtcNow.AddSeconds(75);
                bool woken = false;
                string lastError = null;
                while (DateTime.UtcNow < until)
                {
                    try
                    {
                        using (var tv = new TvLg(c.TvAddress, json, logger))
                        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                        {
                            await tv.Connect(c.TvClientKey, TimeSpan.FromSeconds(10), cts.Token).ConfigureAwait(false);
                            await tv.SwitchInput(c.TvInputId, cts.Token).ConfigureAwait(false);
                            logger.Info("OPPO: TV switched to {0} ({1})", c.TvInputLabel ?? c.TvInputId, c.TvAddress);
                            return;
                        }
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        logger.Info("OPPO: the TV refused the connection ({0}): pair it again in the plugin settings", ex.Message);
                        return;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex.Message;
                        if (!woken && !string.IsNullOrWhiteSpace(c.TvMacAddress))
                        {
                            woken = true;
                            try { Wake(c.TvMacAddress); } catch { }
                        }
                    }
                    await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
                }
                logger.Info("OPPO: could not switch the TV's input ({0})", lastError ?? "no answer");
            }
            finally
            {
                Gate.Release();
            }
        }
    }
}
