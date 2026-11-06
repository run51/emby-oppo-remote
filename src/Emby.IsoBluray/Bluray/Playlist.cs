using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Emby.IsoBluray.Udf;

namespace Emby.IsoBluray.Bluray
{
    public enum BlurayStreamKind { Video, Audio, Subtitle }

    public sealed class BlurayStream
    {
        public BlurayStreamKind Kind { get; set; }
        public int Pid { get; set; }
        public string Codec { get; set; }
        /// <summary>ISO 639-2 language code as written on the disc (eng, fra...), empty if none.</summary>
        public string Language { get; set; }
        /// <summary>Video: height derived from the disc format (480, 576, 720, 1080, 2160), 0 if unknown.</summary>
        public int Height { get; set; }
        /// <summary>Video: frames per second derived from the frame rate code, 0 if unknown.</summary>
        public float FrameRate { get; set; }
        /// <summary>Audio: announced channel count (1, 2 or 6 for "multichannel"), 0 if unknown.</summary>
        public int Channels { get; set; }
        /// <summary>Audio: sample rate in Hz, 0 if unknown.</summary>
        public int SampleRate { get; set; }
    }

    public sealed class Playlist
    {
        public string Name { get; set; }
        public TimeSpan Duration { get; set; }
        public List<string> Clips { get; } = new List<string>();
        public List<BlurayStream> Streams { get; } = new List<BlurayStream>();
        /// <summary>Items played, in order: clip and bounds (45 kHz clip clock).</summary>
        public List<PlayItem> Items { get; } = new List<PlayItem>();
        /// <summary>Start of each chapter, in seconds from the start of the playlist ("entry" marks).</summary>
        public List<double> Chapters { get; } = new List<double>();

        private static readonly Dictionary<byte, string> Codecs = new Dictionary<byte, string>
        {
            [0x01] = "mpeg1video", [0x02] = "mpeg2video", [0x1B] = "h264", [0x20] = "h264", [0x24] = "hevc", [0xEA] = "vc1",
            [0x80] = "pcm_bluray", [0x81] = "ac3", [0x82] = "dts", [0x83] = "truehd", [0x84] = "eac3",
            [0x85] = "dts", [0x86] = "dts", [0xA1] = "eac3", [0xA2] = "dts",
            [0x90] = "pgssub", [0x91] = "igs", [0x92] = "textst",
        };

        /// <summary>
        /// True if the .mpls carries the Blu-ray 3D extension data (ExtensionData entries with ID1 = 2:
        /// stereoscopic stream table and sub-paths of the MVC dependent view). 2D playlists have none.
        /// </summary>
        public static bool HasStereoExtension(byte[] d)
        {
            if (d == null || d.Length < 20 || Encoding.ASCII.GetString(d, 0, 4) != "MPLS") return false;
            int off = BE32(d, 16);
            if (off <= 0 || off + 12 > d.Length || BE32(d, off) == 0) return false;
            int count = d[off + 11];
            for (int i = 0; i < count; i++)
            {
                int q = off + 12 + i * 12;
                if (q + 4 > d.Length) break;
                if (BE16(d, q) == 2) return true;
            }
            return false;
        }

        /// <summary>Parses a .mpls file (public Blu-ray playlist format).</summary>
        public static Playlist Parse(string name, byte[] d)
        {
            if (d.Length < 20 || Encoding.ASCII.GetString(d, 0, 4) != "MPLS") throw new InvalidDataException(name + " is not an MPLS file");
            int pl = BE32(d, 8);
            int itemCount = BE16(d, pl + 6);
            int p = pl + 10;
            double seconds = 0;
            var playlist = new Playlist { Name = name };
            for (int i = 0; i < itemCount; i++)
            {
                int itemLength = BE16(d, p);
                int s = p + 2;
                playlist.Clips.Add(Encoding.ASCII.GetString(d, s, 5));
                bool multiAngle = (BE16(d, s + 9) & 0x10) != 0;
                long inTime = (uint)BE32(d, s + 12), outTime = (uint)BE32(d, s + 16);
                playlist.Items.Add(new PlayItem { Clip = Encoding.ASCII.GetString(d, s, 5), In = inTime, Out = outTime, Start = seconds });
                seconds += (outTime - inTime) / 45000.0;
                int q = s + 32;
                if (multiAngle) q += 2 + (d[q] - 1) * 10;
                if (i == 0) ReadStreamTable(d, q, playlist.Streams);
                p += 2 + itemLength;
            }
            playlist.Duration = TimeSpan.FromSeconds(seconds);
            ReadMarks(d, playlist);
            return playlist;
        }

        /// <summary>PlayListMark section: type 1 marks ("entry mark") = chapters.</summary>
        private static void ReadMarks(byte[] d, Playlist playlist)
        {
            try
            {
                int m = BE32(d, 12);
                if (m <= 0 || m + 6 > d.Length) return;
                int count = BE16(d, m + 4);
                for (int i = 0; i < count; i++)
                {
                    int e = m + 6 + i * 14;
                    if (e + 14 > d.Length) break;
                    if (d[e + 1] != 1) continue;
                    int item = BE16(d, e + 2);
                    long time = (uint)BE32(d, e + 4);
                    if (item >= playlist.Items.Count) continue;
                    var pi = playlist.Items[item];
                    double at = pi.Start + Math.Max(0, time - pi.In) / 45000.0;
                    if (playlist.Chapters.Count == 0 || at - playlist.Chapters[playlist.Chapters.Count - 1] > 1) playlist.Chapters.Add(at);
                }
            }
            catch (Exception) { /* unreadable marks: no chapters */ }
        }

        private static void ReadStreamTable(byte[] d, int q, List<BlurayStream> output)
        {
            int video = d[q + 4], audio = d[q + 5], pg = d[q + 6];
            int r = q + 16;
            r = ReadEntries(d, r, video, BlurayStreamKind.Video, output);
            r = ReadEntries(d, r, audio, BlurayStreamKind.Audio, output);
            ReadEntries(d, r, pg, BlurayStreamKind.Subtitle, output);
        }

        private static int ReadEntries(byte[] d, int p, int count, BlurayStreamKind kind, List<BlurayStream> output)
        {
            for (int i = 0; i < count; i++)
            {
                int entryLength = d[p];
                byte entryType = d[p + 1];
                int pid = entryType == 1 ? BE16(d, p + 2) : entryType == 2 ? BE16(d, p + 4) : BE16(d, p + 3);
                p += 1 + entryLength;
                int attrLength = d[p];
                byte coding = d[p + 1];
                string lang = "";
                if (kind == BlurayStreamKind.Audio || coding == 0x92) lang = Ascii(d, p + 3, 3);
                else if (kind == BlurayStreamKind.Subtitle) lang = Ascii(d, p + 2, 3);
                var stream = new BlurayStream
                {
                    Kind = kind,
                    Pid = pid,
                    Codec = Codecs.TryGetValue(coding, out var c) ? c : "0x" + coding.ToString("x2"),
                    Language = lang.Trim('\0', ' ')
                };
                if (attrLength >= 2 && kind == BlurayStreamKind.Video)
                {
                    stream.Height = VideoHeights[(d[p + 2] >> 4) & 0x0F];
                    stream.FrameRate = FrameRates[d[p + 2] & 0x0F];
                }
                else if (attrLength >= 2 && kind == BlurayStreamKind.Audio)
                {
                    int presentation = (d[p + 2] >> 4) & 0x0F;
                    stream.Channels = presentation == 1 ? 1 : presentation == 3 ? 2 : presentation == 6 || presentation == 12 ? 6 : 0;
                    int rate = d[p + 2] & 0x0F;
                    stream.SampleRate = rate == 1 ? 48000 : rate == 4 ? 96000 : rate == 5 ? 192000 : rate == 12 || rate == 14 ? 48000 : 0;
                }
                output.Add(stream);
                p += 1 + attrLength;
            }
            return p;
        }

        // Blu-ray format tables (STN stream attributes).
        private static readonly int[] VideoHeights = { 0, 480, 576, 480, 1080, 720, 1080, 576, 2160, 0, 0, 0, 0, 0, 0, 0 };
        private static readonly float[] FrameRates = { 0, 23.976f, 24, 25, 29.97f, 0, 50, 59.94f, 0, 0, 0, 0, 0, 0, 0, 0 };

        private static string Ascii(byte[] d, int o, int n) => Encoding.ASCII.GetString(d, o, n);
        private static int BE16(byte[] d, int o) => (d[o] << 8) | d[o + 1];
        private static int BE32(byte[] d, int o) => (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];
    }

    /// <summary>Playlist item: clip and bounds (45 kHz clock), start within the playlist (s).</summary>
    public sealed class PlayItem
    {
        public string Clip { get; set; }
        public long In { get; set; }
        public long Out { get; set; }
        public double Start { get; set; }
        public double Seconds => (Out - In) / 45000.0;
    }

    /// <summary>A .m2ts clip: file that contains it (ISO or .m2ts), extents and size.</summary>
    public sealed class ClipFile
    {
        public string Clip { get; set; }
        /// <summary>ISO (clip inside the image) or .m2ts file of a Blu-ray folder.</summary>
        public string Path { get; set; }
        public List<Extent> Extents { get; set; }
        public long Size { get; set; }
    }

    /// <summary>Result of examining a disc (ISO or folder): chosen playlist and its clips.</summary>
    public sealed class DiscInfo
    {
        public Playlist MainPlaylist { get; set; }
        public List<ClipFile> ClipFiles { get; } = new List<ClipFile>();

        /// <summary>
        /// Path of the BDMV folder inside an ISO: at the root, or in a first-level subfolder (some
        /// images are made from a "Title/BDMV" folder). Null if missing.
        /// </summary>
        public static string FindBdmvRoot(UdfImage image)
        {
            if (image.Resolve("/BDMV/PLAYLIST") != null) return "/BDMV";
            foreach (var e in image.ListDirectory(image.Root()))
            {
                if (!e.IsDirectory) continue;
                string candidate = "/" + e.Name + "/BDMV";
                if (image.Resolve(candidate + "/PLAYLIST") != null) return candidate;
            }
            return null;
        }

        /// <summary>
        /// Blu-ray 3D: the disc has a non-empty BDMV/STREAM/SSIF folder (interleaved left/right views, MVC),
        /// or playlists with the 3D extension data. 2D discs have neither.
        /// </summary>
        public static bool IsStereo3D(UdfImage image)
        {
            string root = FindBdmvRoot(image);
            if (root == null) return false;
            var ssif = image.Resolve(root + "/STREAM/SSIF");
            if (ssif != null && image.ListDirectory(ssif).Any(e => !e.IsDirectory && e.Name.EndsWith(".ssif", StringComparison.OrdinalIgnoreCase)))
                return true;
            // Some rips drop the SSIF folder but keep the 3D playlists.
            var playlists = image.Resolve(root + "/PLAYLIST");
            if (playlists == null) return false;
            foreach (var e in image.ListDirectory(playlists))
            {
                if (e.IsDirectory || !e.Name.EndsWith(".mpls", StringComparison.OrdinalIgnoreCase)) continue;
                try { if (Playlist.HasStereoExtension(image.ReadAllBytes(e))) return true; }
                catch (Exception) { /* unreadable playlist: ignored */ }
            }
            return false;
        }

        /// <summary>Same as <see cref="IsStereo3D(UdfImage)"/> for a BDMV folder on disk.</summary>
        public static bool IsStereo3DFolder(string bdmvFolder)
        {
            string ssif = System.IO.Path.Combine(bdmvFolder, "STREAM", "SSIF");
            if (Directory.Exists(ssif) && Directory.EnumerateFiles(ssif, "*.ssif").Any()) return true;
            string playlists = System.IO.Path.Combine(bdmvFolder, "PLAYLIST");
            if (!Directory.Exists(playlists)) return false;
            foreach (var file in Directory.EnumerateFiles(playlists, "*.mpls"))
            {
                try { if (Playlist.HasStereoExtension(File.ReadAllBytes(file))) return true; }
                catch (Exception) { }
            }
            return false;
        }

        /// <summary>BDMV folder of a movie stored as a folder (or the folder itself if it is named BDMV).</summary>
        public static string FindBdmvFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
            if (string.Equals(System.IO.Path.GetFileName(folder.TrimEnd('/')), "BDMV", StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(System.IO.Path.Combine(folder, "PLAYLIST")))
                return folder;
            string bdmv = System.IO.Path.Combine(folder, "BDMV");
            return Directory.Exists(System.IO.Path.Combine(bdmv, "PLAYLIST")) ? bdmv : null;
        }

        /// <summary>Examines a Blu-ray ISO. <paramref name="preferredLanguage"/> (ISO 639-2, may be empty) only breaks ties between equivalent playlists, see Build.</summary>
        public static DiscInfo Examine(UdfImage image, string preferredLanguage)
        {
            string root = FindBdmvRoot(image) ?? throw new InvalidDataException("No BDMV folder");
            var playlistDir = image.Resolve(root + "/PLAYLIST") ?? throw new InvalidDataException("No BDMV/PLAYLIST folder");
            var streamDir = image.Resolve(root + "/STREAM") ?? throw new InvalidDataException("No BDMV/STREAM folder");

            var playlists = new List<Playlist>();
            foreach (var e in image.ListDirectory(playlistDir))
            {
                if (e.IsDirectory || !e.Name.EndsWith(".mpls", StringComparison.OrdinalIgnoreCase)) continue;
                try { playlists.Add(Playlist.Parse(e.Name, image.ReadAllBytes(e))); }
                catch (Exception) { /* exotic playlist or copy-protection trap: ignored */ }
            }

            var clips = new Dictionary<string, ClipFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in image.ListDirectory(streamDir))
            {
                if (f.IsDirectory || !f.Name.EndsWith(".m2ts", StringComparison.OrdinalIgnoreCase)) continue;
                var extents = image.GetExtents(f, out long size);
                string clip = f.Name.Substring(0, f.Name.Length - 5);
                clips[clip] = new ClipFile { Clip = clip, Path = image.Path, Extents = extents, Size = size };
            }
            return Build(playlists, clips, preferredLanguage);
        }

        /// <summary>Examines a Blu-ray folder (.../BDMV/PLAYLIST and .../BDMV/STREAM on disk).</summary>
        public static DiscInfo ExamineFolder(string bdmvFolder, string preferredLanguage)
        {
            var playlists = new List<Playlist>();
            foreach (var file in Directory.EnumerateFiles(System.IO.Path.Combine(bdmvFolder, "PLAYLIST"), "*.mpls"))
            {
                try { playlists.Add(Playlist.Parse(System.IO.Path.GetFileName(file), File.ReadAllBytes(file))); }
                catch (Exception) { }
            }

            var clips = new Dictionary<string, ClipFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(System.IO.Path.Combine(bdmvFolder, "STREAM"), "*.m2ts"))
            {
                long size = new FileInfo(file).Length;
                string clip = System.IO.Path.GetFileNameWithoutExtension(file);
                clips[clip] = new ClipFile { Clip = clip, Path = file, Extents = new List<Extent> { new Extent(0, size) }, Size = size };
            }
            return Build(playlists, clips, preferredLanguage);
        }

        /// <summary>
        /// True if <paramref name="whole"/> = <paramref name="film"/> interleaved with exactly the items
        /// of another playlist of the disc (in order).
        /// </summary>
        private static bool IsFilmPlusOther(Playlist whole, Playlist film, List<Playlist> all)
        {
            int j = 0;
            foreach (var clip in whole.Clips)
                if (j < film.Clips.Count && string.Equals(clip, film.Clips[j], StringComparison.OrdinalIgnoreCase)) j++;
            if (j != film.Clips.Count) return false;
            var inFilm = new HashSet<string>(film.Clips, StringComparer.OrdinalIgnoreCase);
            var rest = whole.Clips.Where(c => !inFilm.Contains(c)).ToList();
            return rest.Count > 0 && all.Any(p => !ReferenceEquals(p, whole) && !ReferenceEquals(p, film)
                && p.Clips.Count == rest.Count && p.Clips.Zip(rest, (a, b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase)).All(x => x));
        }

        private static bool StartsWith(List<string> all, List<string> part) =>
            part.Count <= all.Count && part.Select((c, i) => string.Equals(c, all[i], StringComparison.OrdinalIgnoreCase)).All(x => x);

        private static bool EndsWith(List<string> all, List<string> part) =>
            part.Count <= all.Count && part.Select((c, i) => string.Equals(c, all[all.Count - part.Count + i], StringComparison.OrdinalIgnoreCase)).All(x => x);

        /// <summary>
        /// The main movie = the playlist that reads the most distinct data (copy protections create
        /// decoy playlists that repeat a small clip). At nearly equal size: the longest (full movie
        /// rather than a chapter), then the one with an audio track in the preferred language
        /// (regional versions), then the one with the most tracks.
        /// </summary>
        private static DiscInfo Build(List<Playlist> playlists, Dictionary<string, ClipFile> clips, string preferredLanguage)
        {
            if (playlists.Count == 0) throw new InvalidDataException("No readable playlist");
            long DataSize(Playlist x) => x.Clips.Distinct(StringComparer.OrdinalIgnoreCase)
                                                 .Sum(c => clips.TryGetValue(c, out var cf) ? cf.Size : 0);

            var scored = playlists.Select(x => (Playlist: x, Size: DataSize(x))).ToList();
            long biggest = scored.Max(x => x.Size);
            var best = scored
                .Where(x => x.Size >= biggest * 0.98)
                .OrderByDescending(x => (long)Math.Round(x.Playlist.Duration.TotalSeconds / 10))
                .ThenByDescending(x => !string.IsNullOrEmpty(preferredLanguage) && x.Playlist.Streams.Any(s => s.Kind == BlurayStreamKind.Audio && s.Language == preferredLanguage))
                .ThenByDescending(x => x.Playlist.Streams.Count)
                .ThenBy(x => x.Playlist.Clips.Count)
                .ThenBy(x => x.Playlist.Name, StringComparer.Ordinal)
                .First().Playlist;

            // "Play all": the movie followed (or preceded) by the extras, or interleaved with extras
            // ("enhanced view" mode), in a single playlist. If another playlist reads exactly the start
            // (or the end) of the chosen one, or if the chosen one is exactly that playlist + another,
            // and it is at least 60 % of it, it is the movie alone: prefer it (otherwise the runtime
            // includes the extras). Out of 510 discs tested, this only changes Deja Vu and Blade Runner 2049.
            long bestSize = DataSize(best);
            var filmOnly = scored
                .Where(x => !ReferenceEquals(x.Playlist, best)
                            && x.Playlist.Clips.Count > 0 && x.Playlist.Clips.Count < best.Clips.Count
                            && x.Size >= bestSize * 0.6
                            && x.Playlist.Duration >= TimeSpan.FromMinutes(45)
                            && (StartsWith(best.Clips, x.Playlist.Clips) || EndsWith(best.Clips, x.Playlist.Clips)
                                || IsFilmPlusOther(best, x.Playlist, playlists)))
                .OrderByDescending(x => x.Size)
                .ThenByDescending(x => x.Playlist.Duration)
                .Select(x => x.Playlist)
                .FirstOrDefault();
            if (filmOnly != null) best = filmOnly;

            var info = new DiscInfo { MainPlaylist = best };
            foreach (var clip in best.Clips)
            {
                if (!clips.TryGetValue(clip, out var cf)) throw new FileNotFoundException("Missing clip: " + clip);
                info.ClipFiles.Add(cf);
            }
            return info;
        }
    }
}
