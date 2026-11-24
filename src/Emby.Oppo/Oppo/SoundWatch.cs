using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace Emby.Oppo.Oppo
{
    /// <summary>
    /// The film's sound lost on its way to the receiver (jailbroken player with oppo-dv-mkv's vidinfo, the amplifier in
    /// Home Assistant). Seen on a UDP-205 whose HDMI 1 goes to the TV and HDMI 2 to the receiver (2026-11-24): the
    /// receiver got nothing ("PCM / Analog") while the player sent the film's sound to HDMI 1, not muted, in 2 channels
    /// (the TV's limit); while the sound is fine, HDMI 1's sound is muted. Checked every 10 s or so while a film plays;
    /// seen twice in a row with the amplifier on, it is reported once: a warning in Emby's log, the player's kernel log
    /// saved next to it (to find the cause), the web remote's warning with "Restart the OPPO", and a "sound_lost" event to
    /// the home automation webhook. Cleared when HDMI 1 is muted again or the film ends.
    /// </summary>
    public static class SoundWatch
    {
        public sealed class Alert
        {
            /// <summary>When it was seen (UTC, ISO 8601); also tells one alert from the next.</summary>
            public string At { get; set; }
            public string Title { get; set; }
            /// <summary>What HDMI 1 (the TV) gets: "AC3", 2 (channels).</summary>
            public string Codec { get; set; }
            public int? Channels { get; set; }
            /// <summary>What the amplifier says it decodes ("PCM / Analog"), or null.</summary>
            public string AmpFormat { get; set; }
            /// <summary>The saved kernel log's file name in Emby's log folder, or null.</summary>
            public string LogFile { get; set; }
        }

        /// <summary>Emby's log folder (set at start-up), where the player's log is saved.</summary>
        public static string LogDirectory { get; set; }

        /// <summary>The current alert, or null.</summary>
        public static Alert Current { get; private set; }

        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        private static DateTime _lastCheck = DateTime.MinValue;
        private static int _suspect, _fine;
        private static bool _reported;

        /// <summary>A film ended or another starts: its alert goes, the next film is watched afresh.</summary>
        public static void Reset()
        {
            Current = null;
            _suspect = _fine = 0;
            _reported = false;
        }

        /// <summary>Called while a film plays (not paused); does something at most every 9 s.</summary>
        public static async Task Check(string host, string title, IJsonSerializer json, ILogger logger, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(host) || !Amplifier.Configured || DateTime.UtcNow - _lastCheck < TimeSpan.FromSeconds(9)) return;
            if (!await Gate.WaitAsync(0).ConfigureAwait(false)) return;
            try
            {
                _lastCheck = DateTime.UtcNow;
                var info = await VidInfo.Get(host, json, logger, ct).ConfigureAwait(false);
                if (info == null || !info.playing || info.audio_out == null) return;   // no vidinfo, or no film on the video plane
                var amp = await Amplifier.GetState(json, logger).ConfigureAwait(false);
                if (amp == null || !amp.Available) return;
                var a = info.audio_out;
                bool suspect = amp.On && !a.muted && !string.IsNullOrEmpty(a.codec);
                if (!suspect)
                {
                    _suspect = 0;
                    if (Current != null && ++_fine >= 2)
                    {
                        logger.Info("OPPO: the film's sound no longer goes to HDMI 1 (the TV): back to the amplifier ({0})", title ?? "?");
                        Current = null;
                    }
                    return;
                }
                _fine = 0;
                if (++_suspect < 2 || Current != null || _reported) return;
                _reported = true;
                var alert = new Alert
                {
                    At = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    Title = title,
                    Codec = a.codec,
                    Channels = a.channels,
                    AmpFormat = amp.Format ?? amp.SoundMode,
                };
                logger.Warn("OPPO: sound probably lost: the player sends the film's sound to HDMI 1 (the TV: {0}{1}, not muted) "
                    + "while the amplifier is on (it decodes: {2}). Film: {3}", a.codec,
                    a.channels.HasValue ? " " + a.channels.Value.ToString(CultureInfo.InvariantCulture) + " ch" : "",
                    alert.AmpFormat ?? "?", title ?? "?");
                alert.LogFile = await SaveLog(host, alert, json, logger, ct).ConfigureAwait(false);
                Current = alert;
                await Webhook(json, logger, "sound_lost", title).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                logger.Debug("OPPO: sound check: {0}", ex.Message);
            }
            finally
            {
                Gate.Release();
            }
        }

        // The player's kernel log as vidinfo keeps it (the driver's lines of the film's start included) and its /info,
        // in Emby's log folder: oppo-sound-lost-YYYYMMDD-HHMMSS.txt, the 10 latest kept.
        private static async Task<string> SaveLog(string host, Alert alert, IJsonSerializer json, ILogger logger, CancellationToken ct)
        {
            string dir = LogDirectory;
            if (string.IsNullOrEmpty(dir)) return null;
            try
            {
                string info = await Fetch(host, "/info", ct).ConfigureAwait(false);
                string log = await Fetch(host, "/log?n=4096&all=1", ct).ConfigureAwait(false);
                string name = "oppo-sound-lost-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt";
                var text = new StringBuilder()
                    .Append("# OPPO: the film's sound went to HDMI 1 (the TV) instead of the amplifier\n")
                    .Append("# alert: ").Append(json.SerializeToString(alert)).Append('\n')
                    .Append("# vidinfo /info:\n").Append(info).Append('\n')
                    .Append("# vidinfo /log (kernel log, lines of vidinfo's own queries marked q):\n").Append(log);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, name), text.ToString(), new UTF8Encoding(false));
                foreach (var old in new DirectoryInfo(dir).GetFiles("oppo-sound-lost-*.txt").OrderByDescending(f => f.Name).Skip(10))
                    old.Delete();
                logger.Info("OPPO: the player's log at that moment is in {0}", name);
                return name;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                logger.Info("OPPO: could not save the player's log ({0})", ex.Message);
                return null;
            }
        }

        private static async Task<string> Fetch(string host, string path, CancellationToken ct)
        {
            var request = (HttpWebRequest)WebRequest.Create("http://" + host.Trim() + ":" + VidInfo.Port.ToString(CultureInfo.InvariantCulture) + path);
            request.Timeout = 8000;
            request.ReadWriteTimeout = 8000;
            using (ct.Register(request.Abort))
            using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                return await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        // The home automation webhook (settings), as the "detected" / "stopped" events: {"event": "sound_lost", "title": …}.
        private static async Task Webhook(IJsonSerializer json, ILogger logger, string evt, string title)
        {
            string url = Plugin.Instance.Configuration.WebhookUrl;
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Timeout = 3000;
                var body = Encoding.UTF8.GetBytes(json.SerializeToString(new Dictionary<string, string> { ["event"] = evt, ["title"] = title ?? "" }));
                using (var s = await request.GetRequestStreamAsync().ConfigureAwait(false))
                    await s.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
                using (await request.GetResponseAsync().ConfigureAwait(false)) { }
            }
            catch (Exception ex)
            {
                logger.Info("OPPO: home automation webhook unreachable ({0})", ex.Message);
            }
        }
    }
}
