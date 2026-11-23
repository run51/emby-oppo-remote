using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace Emby.Oppo.Oppo
{
    /// <summary>
    /// What the OPPO's video decoder reports in its kernel log when a film starts: picture size, frame rate, bit depth
    /// and the source's HDR, e.g. "[VDEC]: [H265-0]: u4H265NotiDataInfo, w:3840, h:2160, fps:1", "ucBitMode:10",
    /// "HDR_Type : 1", "dolby HDR10/SDR source -> DOVI output". Read over SSH, so only on a jailbroken player with SSH set
    /// up (the "restart over SSH" setting). For a disc the player gives this nowhere else (its HTTP getmovieplayinfo
    /// only knows files, and its UVO announcement does not reach an idle oppo-multiplexer client). The lines are pushed
    /// out of the log within about 30 s by HDCP messages, so the log is read right after the start and the values are
    /// kept for the film (24/11).
    /// </summary>
    internal static class DecoderInfo
    {
        internal sealed class Info
        {
            public int Width { get; set; }
            public int Height { get; set; }
            /// <summary>MPEG frame_rate_code: 1 = 23.976, 2 = 24, 3 = 25, 4 = 29.97, 5 = 30, 6 = 50, 7 = 59.94, 8 = 60.</summary>
            public int FpsCode { get; set; }
            public int Bits { get; set; }
            /// <summary>"Dolby Vision" (two layers), "HDR10", "SDR", or null when the decoder does not say.</summary>
            public string SourceHdr { get; set; }
            public bool ToDolbyVision { get; set; }
            /// <summary>The start line closing the film's group is there (both layers of a dual-layer disc are in).</summary>
            public bool Complete { get; set; }
        }

        private static readonly Dictionary<int, double> Rates = new Dictionary<int, double>
            { [1] = 23.976, [2] = 24, [3] = 25, [4] = 29.97, [5] = 30, [6] = 50, [7] = 59.94, [8] = 60 };

        private const string Command = "dmesg | grep -E 'NotiDataInfo|NotiHdrInfo|HDR_Type|DOVI output|Size Change start' | tail -24";

        private static Info _current;
        private static readonly object Gate = new object();
        private static int _capturingGeneration = -1;   // the film whose log is being watched, -1: none
        private static DateTime _lastAttempt = DateTime.MinValue;

        public static Info Current => _current;

        /// <summary>A new film: forget the previous one's values.</summary>
        public static void Reset()
        {
            _current = null;
            Interlocked.Increment(ref _generation);
        }

        private static int _generation;

        /// <summary>
        /// At a film's start: reads the log now, after 3, 8 and 15 s, then every 5 s for 2 minutes, until the decoder's line
        /// is there (a disc may log it only when its main title starts: 2012, VC-1, after the first 15 s). From the remote's
        /// polling: one read, at most once a minute. One capture at a time; a new film stops it.
        /// </summary>
        public static void Capture(string host, ILogger logger, bool fromPolling = false)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.SshRestart || string.IsNullOrWhiteSpace(host)) return;
            // From the remote's polling (the app opened after the start, Emby restarted…): one read a minute at most, the
            // lines may be long gone.
            if (fromPolling && DateTime.UtcNow - _lastAttempt < TimeSpan.FromSeconds(60)) return;
            int generation = _generation;
            lock (Gate)
            {
                if (_capturingGeneration == generation) return;   // already watching this film's start
                _capturingGeneration = generation;
            }
            _lastAttempt = DateTime.UtcNow;
            Task.Run(async () =>
            {
                try
                {
                    var started = DateTime.UtcNow;
                    var waits = new List<int> { 0 };
                    if (!fromPolling)
                    {
                        waits.AddRange(new[] { 3, 5, 7 });
                        while (waits.Count < 25) waits.Add(5);
                    }
                    foreach (int wait in waits)
                    {
                        if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait)).ConfigureAwait(false);
                        if (generation != _generation || _current != null) return;
                        var (result, output) = await OppoSsh.Run(host, config.SshPassword, Command, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                        if (result != OppoSsh.Outcome.Ok)
                        {
                            logger.Debug("OPPO: decoder log not read over SSH ({0})", result);
                            return;
                        }
                        var info = Parse(output);
                        // Without its start line the group may still lack the enhancement layer: wait for it 30 s.
                        if (info == null || (!info.Complete && DateTime.UtcNow - started < TimeSpan.FromSeconds(30))) continue;
                        _current = info;
                        string found = string.Format(CultureInfo.InvariantCulture, "{0}x{1}, frame rate code {2}, {3} bits, source {4}{5}", info.Width,
                            info.Height, info.FpsCode, info.Bits, info.SourceHdr ?? "?", info.ToDolbyVision ? " -> Dolby Vision" : "");
                        logger.Info("OPPO: decoder reports {0} ({1})", found, host);
                        return;
                    }
                    logger.Debug("OPPO: no decoder line in the player's log ({0})", host);
                }
                catch (Exception ex)
                {
                    logger.Debug("OPPO: decoder log: {0}", ex.Message);
                }
                finally
                {
                    lock (Gate) { if (_capturingGeneration == generation) _capturingGeneration = -1; }
                }
            });
        }

        /// <summary>
        /// The last film's lines: those since the previous "Nfy Size Change start" (logged once per start, after the
        /// decoders' data lines) up to the last one. A Dolby Vision dual-layer disc has two decoders: "[H265-0]" for the
        /// base layer (3840 x 2160) and "[H265-1]" for the enhancement layer (1920 x 1080, Alerte !, 24/11): the picture
        /// is the larger one, and two layers mean a Dolby Vision source.
        /// </summary>
        internal static Info Parse(string text)
        {
            var lines = (text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int last = -1, previous = -1;
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (lines[i].IndexOf("Size Change start", StringComparison.Ordinal) < 0) continue;
                if (last < 0) last = i; else { previous = i; break; }
            }
            // No start line yet: the data lines at the end of the log.
            int from = previous + 1, to = last >= 0 ? last : lines.Length - 1;
            Info info = null;
            var decoders = new HashSet<string>(StringComparer.Ordinal);
            for (int i = from; i <= to; i++)
            {
                var m = Regex.Match(lines[i], @"\[(\w+(?:-\d+)?)\]:? ?\w*NotiDataInfo, w:(\d+), h:(\d+), fps:(\d+)");
                if (!m.Success) continue;
                decoders.Add(m.Groups[1].Value);
                int h = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                if (info != null && h <= info.Height) continue;
                info = new Info
                {
                    Width = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                    Height = h,
                    FpsCode = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture),
                };
            }
            if (info == null || info.Height <= 0) return null;
            for (int i = from; i < lines.Length; i++)
            {
                var bits = Regex.Match(lines[i], @"ucBitMode:(\d+)");
                if (bits.Success && i <= to) info.Bits = Math.Max(info.Bits, int.Parse(bits.Groups[1].Value, CultureInfo.InvariantCulture));
                var hdr = Regex.Match(lines[i], @"HDR_Type\s*:\s*(\d+)");
                if (hdr.Success && i <= to) info.SourceHdr = hdr.Groups[1].Value == "1" ? "HDR10" : hdr.Groups[1].Value == "0" ? "SDR" : null;
                if (lines[i].IndexOf("-> DOVI output", StringComparison.Ordinal) >= 0) info.ToDolbyVision = true;
            }
            if (decoders.Count > 1) { info.SourceHdr = "Dolby Vision"; info.ToDolbyVision = false; }
            info.Complete = last >= 0;
            return info;
        }

        /// <summary>"2160p 23,976 i/s" (French): the decoded picture's height and frame rate.</summary>
        public static string Picture(Info info, bool fr) =>
            OppoClient.PictureLabel(info.Height, Rates.TryGetValue(info.FpsCode, out double fps) ? fps : 0, false, fr);
    }
}
