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
            /// <summary>"HDR10", "SDR" or null when the decoder's HDR type is not one of these.</summary>
            public string SourceHdr { get; set; }
            public bool ToDolbyVision { get; set; }
        }

        private static readonly Dictionary<int, double> Rates = new Dictionary<int, double>
            { [1] = 23.976, [2] = 24, [3] = 25, [4] = 29.97, [5] = 30, [6] = 50, [7] = 59.94, [8] = 60 };

        private const string Command = "dmesg | grep -E 'NotiDataInfo|NotiHdrInfo|HDR_Type|DOVI output' | tail -12";

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
        /// Reads the log now, after 3, 8 and 15 s, then every 5 s for 2 minutes, until the decoder's line is there: a disc
        /// may log it only when its main title starts (2012, VC-1: after the first 15 s). One capture at a time, and from
        /// the remote's polling at most every 20 s; a new film stops it.
        /// </summary>
        public static void Capture(string host, ILogger logger, bool fromPolling = false)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.SshRestart || string.IsNullOrWhiteSpace(host)) return;
            if (fromPolling && DateTime.UtcNow - _lastAttempt < TimeSpan.FromSeconds(20)) return;
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
                    var waits = new List<int> { 0, 3, 5, 7 };
                    while (waits.Count < 25) waits.Add(5);
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
                        if (info == null) continue;
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

        internal static Info Parse(string text)
        {
            Match data = null;
            foreach (Match m in Regex.Matches(text ?? "", @"NotiDataInfo, w:(\d+), h:(\d+), fps:(\d+)")) data = m;
            if (data == null) return null;
            var info = new Info
            {
                Width = int.Parse(data.Groups[1].Value, CultureInfo.InvariantCulture),
                Height = int.Parse(data.Groups[2].Value, CultureInfo.InvariantCulture),
                FpsCode = int.Parse(data.Groups[3].Value, CultureInfo.InvariantCulture),
            };
            if (info.Height <= 0) return null;
            // Only this film's lines: from its HDR line (logged just before the data line) to the end.
            int from = text.LastIndexOf("HDR_Type", data.Index, StringComparison.Ordinal);
            text = text.Substring(from >= 0 ? from : data.Index);
            Match bits = null;
            foreach (Match m in Regex.Matches(text, @"ucBitMode:(\d+)")) bits = m;
            if (bits != null) info.Bits = int.Parse(bits.Groups[1].Value, CultureInfo.InvariantCulture);
            Match hdr = null;
            foreach (Match m in Regex.Matches(text, @"HDR_Type\s*:\s*(\d+)")) hdr = m;
            if (hdr != null) info.SourceHdr = hdr.Groups[1].Value == "1" ? "HDR10" : hdr.Groups[1].Value == "0" ? "SDR" : null;
            info.ToDolbyVision = text.IndexOf("-> DOVI output", StringComparison.Ordinal) >= 0;
            return info;
        }

        /// <summary>"2160p 23,976 i/s" (French): the decoded picture's height and frame rate.</summary>
        public static string Picture(Info info, bool fr) =>
            OppoClient.PictureLabel(info.Height, Rates.TryGetValue(info.FpsCode, out double fps) ? fps : 0, false, fr);
    }
}
