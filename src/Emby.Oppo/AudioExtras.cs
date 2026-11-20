using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.IsoBluray.Bluray;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace Emby.Oppo
{
    /// <summary>Atmos / DTS:X of one audio track (Emby stream index).</summary>
    public sealed class AudioExtra
    {
        public int Index { get; set; }
        public bool Atmos { get; set; }
        public bool DtsX { get; set; }
    }

    /// <summary>
    /// Finds Dolby Atmos (TrueHD) and DTS:X tracks, which Emby's ffprobe (FFmpeg 5.1) does not report.
    /// TrueHD: the "16-channel presentation" bit (bit 7 of the substream_info byte, 17 bytes after the
    /// major sync 0xF8726FBA) is set on Atmos tracks. DTS-HD MA: the DTS:X extension sync 0x02000850 appears
    /// in the lossless (XLL) data. Checked on 2026-11-20: 1917 Atmos, Contact not; Apollo 13 and Backdraft
    /// DTS:X, Batman Begins not.
    /// Blu-ray (ISO or folder): 48 MB of the main playlist's clip, demultiplexed by PID (the playlist gives the
    /// PID of each audio track, in Emby's order). Files: a few seconds of each TrueHD / DTS track copied by ffmpeg.
    /// Results are cached on disk, per file and modification date.
    /// </summary>
    internal sealed class AudioExtras
    {
        private const int Sample = 48 * 1024 * 1024;
        private static readonly byte[] TrueHdSync = { 0xF8, 0x72, 0x6F, 0xBA };
        private static readonly byte[] DtsXSync = { 0x02, 0x00, 0x08, 0x50 };
        private static readonly byte[] DtsXImaxSync = { 0xF1, 0x40, 0x00, 0xD0 };

        private static readonly ConcurrentDictionary<string, AudioExtra[]> Cache = new ConcurrentDictionary<string, AudioExtra[]>();
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        private static bool _loaded;

        private readonly IItemRepository _items;
        private readonly IJsonSerializer _json;
        private readonly ILogger _logger;
        private readonly string _ffmpeg;
        private readonly string _cacheFile;

        public AudioExtras(IItemRepository items, IJsonSerializer json, ILogger logger, string ffmpegPath, string cachePath)
        {
            _items = items;
            _json = json;
            _logger = logger;
            _ffmpeg = ffmpegPath;
            _cacheFile = Path.Combine(cachePath, "oppo-audio-extras.json");
        }

        /// <summary>Atmos / DTS:X of the item's audio tracks (computed once per file, then cached).</summary>
        public async Task<AudioExtra[]> Get(BaseItem item, CancellationToken ct)
        {
            string path = item.Path;
            if (string.IsNullOrEmpty(path)) return Array.Empty<AudioExtra>();
            string key = Key(path);
            if (key == null) return Array.Empty<AudioExtra>();
            Load();
            if (Cache.TryGetValue(key, out var hit)) return hit;

            await Gate.WaitAsync(ct).ConfigureAwait(false);   // one analysis at a time (disk and CPU)
            try
            {
                if (Cache.TryGetValue(key, out hit)) return hit;
                var embedded = _items.GetMediaStreams(new MediaStreamQuery { ItemId = item.InternalId }, ct)
                    .Where(s => !s.IsExternal).OrderBy(s => s.Index).ToList();
                var audio = embedded.Where(s => s.Type == MediaStreamType.Audio).ToList();
                AudioExtra[] result;
                var watch = Stopwatch.StartNew();
                bool disc = path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) || (Directory.Exists(path) && DiscInfo.FindBdmvFolder(path) != null);
                result = disc ? FromDisc(path, embedded) : await FromFile(path, audio, ct).ConfigureAwait(false);
                _logger.Info("OPPO: audio of {0}: {1} ({2} ms)", Path.GetFileName(path.TrimEnd('/', '\\')),
                    string.Join(", ", result.Select(r => r.Index + (r.Atmos ? " Atmos" : r.DtsX ? " DTS:X" : " -"))), watch.ElapsedMilliseconds);
                Cache[key] = result;
                Save();
                return result;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Info("OPPO: audio analysis of {0} failed ({1})", path, ex.Message);
                return Array.Empty<AudioExtra>();
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>Atmos / DTS:X of an item if it was already analysed, otherwise null (no analysis here).</summary>
        public AudioExtra[] Peek(BaseItem item)
        {
            if (string.IsNullOrEmpty(item.Path)) return null;
            Load();
            string key = Key(item.Path);
            return key != null && Cache.TryGetValue(key, out var hit) ? hit : null;
        }

        private static string Key(string path)
        {
            try
            {
                if (File.Exists(path)) { var f = new FileInfo(path); return path + "|" + f.Length + "|" + f.LastWriteTimeUtc.Ticks; }
                if (Directory.Exists(path)) return path + "|dir|" + Directory.GetLastWriteTimeUtc(path).Ticks;
            }
            catch (IOException) { }
            return null;
        }

        // ---------------------------------------------------------------- Blu-ray

        private static AudioExtra[] FromDisc(string path, List<MediaStream> embedded)
        {
            var disc = Chapters.Disc(path);
            var audio = MapToPlaylist(disc, embedded);
            // The largest clip of the main playlist (the movie itself), sampled at a fifth of its length.
            var clip = disc.ClipFiles.OrderByDescending(c => c.Size).First();
            long offset = (long)(clip.Size * 0.2) / 6144 * 6144;
            var payloads = Demux(Chapters.Read(clip, offset, Sample), new HashSet<int>(audio.Where(x => x.Entry != null).Select(x => x.Entry.Pid)));
            var result = new List<AudioExtra>();
            foreach (var (stream, entry, _) in audio)
            {
                var extra = new AudioExtra { Index = stream.Index };
                if (entry != null && payloads.TryGetValue(entry.Pid, out var data)) Detect(data, extra);
                result.Add(extra);
            }
            return result.ToArray();
        }

        /// <summary>
        /// The disc playlist's audio entry of each Emby audio track, with its position in the playlist (which is
        /// the OPPO's audio menu order). The ISO Blu-ray plugin gave each Emby track the language of its PID in the
        /// playlist, so tracks are matched by language and codec, in order. Emby's own order can differ from the
        /// playlist's (Dune: the clip carries an AC-3 track the playlist does not list). The AC-3 core that
        /// ffprobe reports right after a TrueHD track, and tracks without a language, get no entry.
        /// </summary>
        internal static List<(MediaStream Stream, BlurayStream Entry, int Position)> MapToPlaylist(DiscInfo disc, List<MediaStream> embedded)
        {
            var entries = disc.MainPlaylist.Streams.Where(s => s.Kind == BlurayStreamKind.Audio).ToList();
            var used = new bool[entries.Count];
            var result = new List<(MediaStream, BlurayStream, int)>();
            MediaStream previous = null;
            foreach (var stream in embedded)
            {
                if (stream.Type != MediaStreamType.Audio) { previous = stream; continue; }
                bool core = previous != null && previous.Type == MediaStreamType.Audio
                    && string.Equals(previous.Codec, "truehd", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(stream.Codec, "ac3", StringComparison.OrdinalIgnoreCase) && stream.Index == previous.Index + 1;
                previous = stream;
                int found = -1;
                if (!core && !string.IsNullOrEmpty(stream.Language))
                    for (int i = 0; i < entries.Count && found < 0; i++)
                        if (!used[i] && string.Equals(entries[i].Language, stream.Language, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(Family(entries[i].Codec), Family(stream.Codec), StringComparison.Ordinal))
                            found = i;
                if (found >= 0) used[found] = true;
                result.Add((stream, found >= 0 ? entries[found] : null, found));
            }
            return result;
        }

        private static string Family(string codec)
        {
            string c = (codec ?? "").ToLowerInvariant();
            return c.StartsWith("dts", StringComparison.Ordinal) ? "dts" : c;
        }

        /// <summary>PES payload of the given PIDs in an M2TS sample (192-byte packets), up to 4 MB each.</summary>
        private static Dictionary<int, MemoryStream> Demux(byte[] data, HashSet<int> pids)
        {
            var output = new Dictionary<int, MemoryStream>();
            const int packet = 192;
            int start = -1;
            for (int o = 0; o < packet && o + 4 + 4 * packet < data.Length; o++)
            {
                bool ok = true;
                for (int k = 0; k < 5 && ok; k++) ok = data[o + 4 + k * packet] == 0x47;
                if (ok) { start = o; break; }
            }
            if (start < 0) return output;
            for (int off = start; off + packet <= data.Length; off += packet)
            {
                int p = off + 4;
                if (data[p] != 0x47) continue;
                int pid = ((data[p + 1] & 0x1F) << 8) | data[p + 2];
                if (!pids.Contains(pid)) continue;
                int afc = (data[p + 3] >> 4) & 3;
                if (afc == 0 || afc == 2) continue;
                int i = p + 4 + (afc == 3 ? 1 + data[p + 4] : 0);
                int end = p + 188;
                if (i >= end) continue;
                bool unitStart = (data[p + 1] & 0x40) != 0;
                if (unitStart && end - i > 9 && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1) i += 9 + data[i + 8];
                if (i >= end) continue;
                if (!output.TryGetValue(pid, out var ms)) output[pid] = ms = new MemoryStream();
                if (ms.Length < 4 * 1024 * 1024) ms.Write(data, i, end - i);
            }
            return output;
        }

        private static void Detect(MemoryStream payload, AudioExtra extra)
        {
            byte[] d = payload.ToArray();
            int syncs = 0, atmos = 0;
            for (int pos = IndexOf(d, TrueHdSync, 0); pos >= 0 && syncs < 300; pos = IndexOf(d, TrueHdSync, pos + 4))
            {
                syncs++;
                if (pos + 17 < d.Length && (d[pos + 17] & 0x80) != 0) atmos++;
            }
            extra.Atmos = syncs > 0 && atmos * 2 > syncs;
            extra.DtsX = Count(d, DtsXSync) >= 3 || Count(d, DtsXImaxSync) >= 3;
        }

        private static int IndexOf(byte[] d, byte[] pattern, int from)
        {
            for (int i = from; i <= d.Length - pattern.Length; i++)
            {
                if (d[i] != pattern[0]) continue;
                int k = 1;
                while (k < pattern.Length && d[i + k] == pattern[k]) k++;
                if (k == pattern.Length) return i;
            }
            return -1;
        }

        private static int Count(byte[] d, byte[] pattern)
        {
            int n = 0;
            for (int pos = IndexOf(d, pattern, 0); pos >= 0 && n < 50; pos = IndexOf(d, pattern, pos + pattern.Length)) n++;
            return n;
        }

        // ---------------------------------------------------------------- files (MKV…)

        private async Task<AudioExtra[]> FromFile(string path, List<MediaStream> audio, CancellationToken ct)
        {
            var result = new List<AudioExtra>();
            for (int i = 0; i < audio.Count; i++)
            {
                var extra = new AudioExtra { Index = audio[i].Index };
                string codec = (audio[i].Codec ?? "").ToLowerInvariant();
                string format = codec == "truehd" ? "truehd" : codec.StartsWith("dts", StringComparison.Ordinal) ? "dts" : null;
                if (format != null)
                {
                    // A few seconds of this track, copied as is, from ten minutes in (past studio logos).
                    string args = string.Format(CultureInfo.InvariantCulture,
                        "-hide_banner -loglevel error -ss 600 -i \"{0}\" -map 0:a:{1} -c copy -t 4 -f {2} pipe:1", path, i, format);
                    var data = await RunFfmpeg(args, ct).ConfigureAwait(false);
                    if (data.Length > 0) Detect(new MemoryStream(data), extra);
                }
                result.Add(extra);
            }
            return result.ToArray();
        }

        private async Task<byte[]> RunFfmpeg(string args, CancellationToken ct)
        {
            using (var process = new Process { StartInfo = new ProcessStartInfo(_ffmpeg, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } })
            {
                process.Start();
                var errors = process.StandardError.ReadToEndAsync();
                var output = new MemoryStream();
                var copy = process.StandardOutput.BaseStream.CopyToAsync(output, 81920, ct);
                var done = Task.Run(() => process.WaitForExit(60000), ct);
                await Task.WhenAll(copy, done).ConfigureAwait(false);
                if (!process.HasExited) { try { process.Kill(); } catch { } }
                string err = await errors.ConfigureAwait(false);
                if (output.Length == 0 && !string.IsNullOrWhiteSpace(err)) _logger.Debug("OPPO: ffmpeg (audio) failed: {0}", err.Trim());
                return output.ToArray();
            }
        }

        // ---------------------------------------------------------------- cache

        private void Load()
        {
            if (_loaded) return;
            lock (Cache)
            {
                if (_loaded) return;
                try
                {
                    if (File.Exists(_cacheFile))
                        foreach (var kv in _json.DeserializeFromFile<Dictionary<string, AudioExtra[]>>(_cacheFile) ?? new Dictionary<string, AudioExtra[]>())
                            Cache[kv.Key] = kv.Value;
                }
                catch (Exception ex) { _logger.Info("OPPO: audio cache unreadable ({0})", ex.Message); }
                _loaded = true;
            }
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile));
                _json.SerializeToFile(Cache.ToDictionary(kv => kv.Key, kv => kv.Value), _cacheFile);
            }
            catch (Exception ex) { _logger.Info("OPPO: audio cache not saved ({0})", ex.Message); }
        }
    }
}
