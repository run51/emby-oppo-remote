using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace Emby.Oppo.Oppo
{
    /// <summary>
    /// What the player decodes and sends out, from oppo-dv-mkv's vidinfo (jailbroken player: http://PLAYER:8436/info).
    /// It reads the video driver's own status and keeps the decoder's log lines from the boot on, so the picture is known
    /// from the first seconds of a film, for discs and files alike, without SSH. A player without it answers nothing:
    /// asked again only every few minutes then.
    /// </summary>
    internal static class VidInfo
    {
        public const int Port = 8436;

#pragma warning disable IDE1006 // names as in vidinfo's JSON
        public sealed class Info
        {
            public bool playing { get; set; }
            public Source source { get; set; }
            public Output output { get; set; }
            public Audio audio_out { get; set; }
        }

        public sealed class Source
        {
            public string codec { get; set; }
            public int width { get; set; }
            public int height { get; set; }
            public double? fps { get; set; }
            public int? bits { get; set; }
            /// <summary>"Dolby Vision", "HDR10", "SDR" or null.</summary>
            public string hdr { get; set; }
            public int layers { get; set; }
            /// <summary>The player turns the source (HDR10 or SDR) into Dolby Vision.</summary>
            public bool to_dolby_vision { get; set; }
            public long? bitrate { get; set; }
        }

        public sealed class Output
        {
            public bool? connected { get; set; }
            public string resolution { get; set; }
            public double? refresh { get; set; }
            public string colorspace { get; set; }
            public int? bits { get; set; }
            /// <summary>"Dolby Vision" (TV-led), "Dolby Vision (player-led)", "HDR10+", "HDR", "SDR".</summary>
            public string hdr { get; set; }
            public string sink { get; set; }
        }

        public sealed class Audio
        {
            public string codec { get; set; }
            public int? channels { get; set; }
            public int? sample_rate { get; set; }
            public bool muted { get; set; }
        }
#pragma warning restore IDE1006

        private sealed class Entry { public Info Info; public DateTime At; public DateTime DownUntil; public DateTime LastOk; }
        private static readonly ConcurrentDictionary<string, Entry> Cache = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>vidinfo answered on this player in the last 10 minutes (the SSH reading of the log is then not needed).</summary>
        public static bool Available(string host) =>
            !string.IsNullOrEmpty(host) && Cache.TryGetValue(host, out var e) && DateTime.UtcNow - e.LastOk < TimeSpan.FromMinutes(10);

        /// <summary>The player's answer, at most 3 s old; null without vidinfo (or while it does not answer).</summary>
        public static async Task<Info> Get(string host, IJsonSerializer json, ILogger logger, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(host)) return null;
            host = host.Trim();
            var e = Cache.GetOrAdd(host, _ => new Entry());
            var now = DateTime.UtcNow;
            if (e.Info != null && now - e.At < TimeSpan.FromSeconds(3)) return e.Info;
            if (now < e.DownUntil) return null;
            try
            {
                var request = (HttpWebRequest)WebRequest.Create("http://" + host + ":" + Port.ToString(CultureInfo.InvariantCulture) + "/info");
                request.Timeout = 6000;
                request.ReadWriteTimeout = 6000;
                Info info;
                using (ct.Register(request.Abort))
                using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    info = json.DeserializeFromString<Info>(await reader.ReadToEndAsync().ConfigureAwait(false));
                if (info == null) return null;
                if (e.LastOk == DateTime.MinValue) logger.Info("OPPO: oppo-dv-mkv's vidinfo answers ({0}): the picture comes from it", host);
                e.Info = info; e.At = e.LastOk = DateTime.UtcNow;
                return info;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                // Not there (no oppo-dv-mkv, an older one, vidinfo = off): asked again in 5 minutes; slow: in 30 s.
                e.DownUntil = DateTime.UtcNow + (OppoClient.Refused(ex) ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30));
                e.Info = null;
                logger.Debug("OPPO vidinfo ({0}): {1}", host, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// The OPPO line's picture: "2160p 23,976 i/s · 10 bits · HDR10 → Dolby Vision" (French), from what the player
        /// decodes and sends out. Null when it gives no picture size.
        /// </summary>
        public static string Picture(Info info, bool fr)
        {
            var s = info?.source;
            if (info == null || !info.playing || s == null || s.height <= 0) return null;
            string text = OppoClient.PictureLabel(s.height, s.fps ?? 0, false, fr);
            if (s.bits >= 10) text += " · " + s.bits.Value.ToString(CultureInfo.InvariantCulture) + L10n.T(fr, "-bit", " bits");
            string outHdr = info.output?.hdr;
            bool outDv = outHdr != null && outHdr.StartsWith("Dolby Vision", StringComparison.Ordinal);
            string hdr = s.hdr == "Dolby Vision" ? "Dolby Vision"
                : (s.to_dolby_vision || (outDv && s.hdr != null)) ? (s.hdr ?? "SDR") + " → Dolby Vision"
                : s.hdr;
            if (!string.IsNullOrEmpty(hdr)) text += " · " + hdr;
            return text;
        }
    }
}
