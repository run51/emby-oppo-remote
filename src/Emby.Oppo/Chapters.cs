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
using Emby.IsoBluray.Udf;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace Emby.Oppo
{
    public sealed class ChapterEntry
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public double Seconds { get; set; }
    }

    /// <summary>
    /// Chapters of a movie: for a Blu-ray (ISO or folder), the real marks of the main playlist
    /// (Emby only makes one up every 5 minutes); for a file (MKV…), Emby's own chapters.
    /// Thumbnails: one frame extracted with Emby's ffmpeg at the start of each chapter, kept in a cache.
    /// </summary>
    internal sealed class Chapters
    {
        private static readonly ConcurrentDictionary<string, (DateTime Stamp, DiscInfo Disc)> Discs =
            new ConcurrentDictionary<string, (DateTime, DiscInfo)>(StringComparer.Ordinal);
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(2, 2);

        private readonly IItemRepository _items;
        private readonly ILogger _logger;
        private readonly string _ffmpeg;
        private readonly string _cacheDir;

        public Chapters(IItemRepository items, ILogger logger, string ffmpeg, string cachePath)
        {
            _items = items;
            _logger = logger;
            _ffmpeg = ffmpeg;
            _cacheDir = Path.Combine(cachePath, "oppo-chapitres-2");   // folder name kept so existing caches stay valid
        }

        private static bool IsDisc(string path) =>
            path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) || DiscInfo.FindBdmvFolder(path) != null;

        private static DiscInfo Disc(string path)
        {
            DateTime stamp = Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path);
            if (Discs.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Disc;
            DiscInfo disc;
            if (path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                using (var image = new UdfImage(path)) disc = DiscInfo.Examine(image, L10n.DiscLanguage());   // language hint for the main-playlist choice; only the chapter marks are used here
            else
                disc = DiscInfo.ExamineFolder(DiscInfo.FindBdmvFolder(path), L10n.DiscLanguage());
            Discs[path] = (stamp, disc);
            return disc;
        }

        /// <summary>Chapter list; generated names ("Chapter N") are in French if <paramref name="fr"/>.</summary>
        public List<ChapterEntry> List(BaseItem item, bool fr)
        {
            string prefix = L10n.T(fr, "Chapter ", "Chapitre ");
            var result = new List<ChapterEntry>();
            if (string.IsNullOrEmpty(item.Path)) return result;
            if (IsDisc(item.Path))
            {
                try
                {
                    var marks = Disc(item.Path).MainPlaylist.Chapters;
                    for (int i = 0; i < marks.Count; i++)
                        result.Add(new ChapterEntry { Index = i, Name = prefix + (i + 1), Seconds = Math.Round(marks[i], 1) });
                }
                catch (Exception ex) { _logger.Debug("OPPO: could not read the disc chapters ({0})", ex.Message); }
                return result;
            }
            var chapters = _items.GetChapters(item) ?? new List<ChapterInfo>();
            for (int i = 0; i < chapters.Count; i++)
                result.Add(new ChapterEntry { Index = i, Name = string.IsNullOrWhiteSpace(chapters[i].Name) ? prefix + (i + 1) : chapters[i].Name, Seconds = Math.Round(chapters[i].StartPositionTicks / 1e7, 1) });
            return result;
        }

        /// <summary>Path of the thumbnail (created if needed), or null if not possible.</summary>
        public async Task<string> Image(BaseItem item, int index, CancellationToken ct)
        {
            var list = List(item, false);   // only the positions are used
            if (index < 0 || index >= list.Count) return null;
            Directory.CreateDirectory(_cacheDir);
            string file = Path.Combine(_cacheDir, item.InternalId.ToString(CultureInfo.InvariantCulture) + "-" + index.ToString(CultureInfo.InvariantCulture) + ".jpg");
            if (File.Exists(file) && new FileInfo(file).Length > 0) return file;

            // A few seconds after the chapter start, to avoid black frames from fades.
            double at = list[index].Seconds + (index + 1 < list.Count ? Math.Min(8, (list[index + 1].Seconds - list[index].Seconds) / 3) : 8);
            var video = _items.GetMediaStreams(new MediaStreamQuery { ItemId = item.InternalId }, ct).FirstOrDefault(s => s.Type == MediaStreamType.Video && !s.IsExternal);
            bool hdr = video != null && (string.Equals(video.ColorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase)
                                         || string.Equals(video.ColorTransfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase));

            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (File.Exists(file) && new FileInfo(file).Length > 0) return file;
                foreach (bool tonemap in hdr ? new[] { true, false } : new[] { false })
                {
                    if (await Extract(item.Path, at, file, tonemap, ct).ConfigureAwait(false)) return file;
                }
                return null;
            }
            finally { Gate.Release(); }
        }

        private const string Scale = "scale=480:-2";
        private const string ToneMap = "scale=480:-2,zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p";

        private async Task<bool> Extract(string path, double at, string output, bool tonemap, CancellationToken ct)
        {
            string vf = tonemap ? ToneMap : Scale;
            if (!IsDisc(path))
            {
                string args = string.Format(CultureInfo.InvariantCulture,
                    "-hide_banner -loglevel error -skip_frame nokey -ss {0:0.###} -i \"{1}\" -map 0:v:0 -frames:v 1 -vf \"{2}\" -q:v 4 -y \"{3}\"", at, path, vf, output);
                return await Run(args, null, ct).ConfigureAwait(false);
            }

            // Blu-ray: find the play item and the approximate position in its .m2ts, then feed
            // ~24 MB of the stream to ffmpeg, which decodes the first complete frame.
            var disc = Disc(path);
            var items = disc.MainPlaylist.Items;
            int k = Math.Max(0, items.FindLastIndex(x => x.Start <= at));
            var pi = items[k];
            var clip = disc.ClipFiles[Math.Min(k, disc.ClipFiles.Count - 1)];
            double fraction = pi.Seconds > 0 ? Math.Max(0, Math.Min(0.97, (at - pi.Start) / pi.Seconds)) : 0;
            long offset = (long)(clip.Size * fraction) / 6144 * 6144;
            string discArgs = string.Format(CultureInfo.InvariantCulture,
                "-hide_banner -loglevel error -skip_frame nokey -f mpegts -i pipe:0 -map 0:v:0 -frames:v 1 -vf \"{0}\" -q:v 4 -y \"{1}\"", vf, output);
            // 24 MB is usually enough; a very high-bitrate UHD sometimes needs more to find a complete keyframe.
            foreach (int megabytes in new[] { 24, 72 })
            {
                byte[] data = Read(clip, offset, megabytes * 1024 * 1024);
                if (data.Length == 0) return false;
                if (await Run(discArgs, data, ct).ConfigureAwait(false) && File.Exists(output) && new FileInfo(output).Length > 0) return true;
            }
            return false;
        }

        /// <summary>Reads a range of a clip, through its extents (inside the ISO) or directly (folder).</summary>
        private static byte[] Read(ClipFile clip, long offset, int count)
        {
            var buffer = new byte[(int)Math.Min(count, Math.Max(0, clip.Size - offset))];
            int done = 0;
            using (var fs = new FileStream(clip.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                long logical = 0;
                foreach (var e in clip.Extents)
                {
                    if (done >= buffer.Length) break;
                    long start = Math.Max(offset + done, logical), end = logical + e.Length;
                    if (start < end)
                    {
                        fs.Seek(e.Offset + (start - logical), SeekOrigin.Begin);
                        int want = (int)Math.Min(buffer.Length - done, end - start);
                        int got = 0;
                        while (got < want)
                        {
                            int n = fs.Read(buffer, done + got, want - got);
                            if (n <= 0) break;
                            got += n;
                        }
                        done += got;
                    }
                    logical = end;
                }
            }
            if (done < buffer.Length) Array.Resize(ref buffer, done);
            return buffer;
        }

        private async Task<bool> Run(string args, byte[] input, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(_ffmpeg, args)
            {
                UseShellExecute = false,
                RedirectStandardInput = input != null,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using (var process = Process.Start(psi))
            {
                if (process == null) return false;
                var stderr = process.StandardError.ReadToEndAsync();
                var stdout = process.StandardOutput.ReadToEndAsync();
                if (input != null)
                {
                    try
                    {
                        await process.StandardInput.BaseStream.WriteAsync(input, 0, input.Length, ct).ConfigureAwait(false);
                    }
                    catch (IOException) { /* ffmpeg stopped reading before the end: expected */ }
                    try { process.StandardInput.Close(); } catch (IOException) { }
                }
                var exited = Task.Run(() => process.WaitForExit(30000));
                if (!await exited.ConfigureAwait(false))
                {
                    try { process.Kill(); } catch (Exception) { }
                    _logger.Info("OPPO: chapter thumbnail took too long to produce, giving up ({0})", _ffmpeg);
                    return false;
                }
                string error = await stderr.ConfigureAwait(false);
                await stdout.ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    _logger.Debug("OPPO: ffmpeg (thumbnail) failed: {0}", error.Length > 300 ? error.Substring(0, 300) : error);
                    return false;
                }
                return true;
            }
        }
    }

    /// <summary>Sleep timer (at a given time, or at the end of the movie): turn everything off.</summary>
    internal static class SleepTimer
    {
        private static readonly object Lock = new object();
        private static Timer _timer;
        private static Func<Task> _action;

        public static DateTime? At { get; private set; }
        public static bool EndOfFilm { get; private set; }

        public static void Set(int minutes, Func<Task> action)
        {
            lock (Lock)
            {
                CancelLocked();
                if (minutes == 0) return;
                _action = action;
                if (minutes < 0) { EndOfFilm = true; return; }
                At = DateTime.UtcNow.AddMinutes(minutes);
                _timer = new Timer(_ => Fire(), null, TimeSpan.FromMinutes(minutes), Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>Called when playback ends; "end of movie" only applies to playback that ran to the end.</summary>
        public static void PlaybackEnded(bool finished)
        {
            if (finished && EndOfFilm) Fire();
        }

        private static void Fire()
        {
            Func<Task> action;
            lock (Lock)
            {
                action = _action;
                CancelLocked();
            }
            if (action != null) Task.Run(action);
        }

        private static void CancelLocked()
        {
            _timer?.Dispose();
            _timer = null;
            _action = null;
            At = null;
            EndOfFilm = false;
        }
    }
}
