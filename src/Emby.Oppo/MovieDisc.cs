using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Emby.IsoBluray.Bluray;
using Emby.IsoBluray.Udf;
using Emby.Oppo.Oppo;

namespace Emby.Oppo
{
    /// <summary>
    /// A Blu-ray reduced to its main movie, served as a virtual disc folder (BDMV) over WebDAV to the
    /// oppo-dv-mkv mod, which plays it as a disc: Dolby Vision and lossless audio kept, unlike the
    /// .m2ts file of "Movie without menus". The disc's own navigation (BD-J menus) is replaced by a minimal
    /// HDMV one that plays the main playlist at once:
    /// - index.bdmv: the disc's header, application info and extension data (UHD / HDR information) kept;
    ///   First Playback, Top Menu and a single title all point to HDMV movie object 0;
    /// - MovieObject.bdmv: one object, one command: PlayPL(main playlist);
    /// - the main playlist and the clip information of every clip it references, copied as is;
    /// - the clips (.m2ts), read inside the ISO or the folder.
    /// Formats: index.bdmv / MovieObject.bdmv as parsed by libbluray (index_parse.c, mobj_parse.c).
    /// </summary>
    internal static class MovieDisc
    {
        /// <summary>
        /// Test variant for a Blu-ray folder: every file of the real disc (BDMV, CERTIFICATE…) with only
        /// index.bdmv and MovieObject.bdmv replaced.
        /// </summary>
        public static Dictionary<string, DiscServer.VirtualFile> BuildFull(string path, bool keepNavigation = false)
        {
            var reduced = Build(path);
            string bdmv = DiscInfo.FindBdmvFolder(path) ?? throw new InvalidDataException("folders only");
            string root = Path.GetDirectoryName(bdmv.TrimEnd('/', '\\'));
            var files = new Dictionary<string, DiscServer.VirtualFile>(StringComparer.Ordinal);
            foreach (var top in new[] { Path.GetFileName(bdmv.TrimEnd('/', '\\')), "CERTIFICATE", "AACS" })
            {
                string dir = Path.Combine(root, top);
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    if (Path.GetFileName(file).StartsWith(".", StringComparison.Ordinal) || file.Contains("@eaDir")) continue;
                    string rel = file.Substring(root.Length + 1).Replace('\\', '/');
                    files[rel] = new DiscServer.VirtualFile { Segments = new List<DiscServer.Segment> { new DiscServer.Segment { Path = file, Offset = 0, Length = new FileInfo(file).Length } }, Size = new FileInfo(file).Length };
                }
            }
            if (keepNavigation) return files;   // control test: the real disc, unchanged
            files["BDMV/index.bdmv"] = reduced["BDMV/index.bdmv"];
            files["BDMV/MovieObject.bdmv"] = reduced["BDMV/MovieObject.bdmv"];
            return files;
        }

        public static Dictionary<string, DiscServer.VirtualFile> Build(string path)
        {
            var disc = Chapters.Disc(path);
            string playlistName = disc.MainPlaylist.Name;                       // "00800.mpls"
            int playlistNumber = int.Parse(Path.GetFileNameWithoutExtension(playlistName), System.Globalization.CultureInfo.InvariantCulture);
            var files = new Dictionary<string, DiscServer.VirtualFile>(StringComparer.Ordinal);

            Func<string, byte[]> read;
            Func<string, List<DiscServer.Segment>> stream;
            UdfImage image = null;
            try
            {
                if (path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                {
                    image = new UdfImage(path);
                    var img = image;
                    string root = DiscInfo.FindBdmvRoot(img) ?? throw new InvalidDataException("no BDMV folder");
                    read = rel => img.ReadAllBytes(img.Resolve(root + "/" + rel) ?? throw new FileNotFoundException(rel));
                    stream = rel =>
                    {
                        var entry = img.Resolve(root + "/" + rel) ?? throw new FileNotFoundException(rel);
                        return img.GetExtents(entry, out _).Select(e => new DiscServer.Segment { Path = path, Offset = e.Offset, Length = e.Length }).ToList();
                    };
                }
                else
                {
                    string bdmv = DiscInfo.FindBdmvFolder(path) ?? throw new InvalidDataException("no BDMV folder");
                    read = rel => File.ReadAllBytes(Path.Combine(bdmv, rel));
                    stream = rel =>
                    {
                        string file = Path.Combine(bdmv, rel);
                        return new List<DiscServer.Segment> { new DiscServer.Segment { Path = file, Offset = 0, Length = new FileInfo(file).Length } };
                    };
                }

                byte[] mpls = read("PLAYLIST/" + playlistName);
                // Every clip the playlist references (play items, angles, sub-paths): "nnnnnM2TS".
                var clips = Regex.Matches(Encoding.ASCII.GetString(mpls), @"(\d{5})M2TS").Cast<Match>()
                    .Select(m => m.Groups[1].Value).Distinct().ToList();

                files["BDMV/index.bdmv"] = Data(Index(read("index.bdmv")));
                files["BDMV/MovieObject.bdmv"] = Data(MovieObject(read("MovieObject.bdmv"), playlistNumber));
                files["BDMV/PLAYLIST/" + playlistName] = Data(mpls);
                foreach (var clip in clips)
                {
                    files["BDMV/CLIPINF/" + clip + ".clpi"] = Data(read("CLIPINF/" + clip + ".clpi"));
                    var segments = stream("STREAM/" + clip + ".m2ts");
                    files["BDMV/STREAM/" + clip + ".m2ts"] = new DiscServer.VirtualFile { Segments = segments, Size = segments.Sum(s => s.Length) };
                }
                // BDMV/BACKUP: the disc format keeps a second copy of the navigation, playlist and clip information
                // files; players may check it.
                foreach (var key in files.Keys.Where(k => !k.StartsWith("BDMV/STREAM/", StringComparison.Ordinal)).ToList())
                    files["BDMV/BACKUP/" + key.Substring(5)] = files[key];
                return files;
            }
            finally
            {
                image?.Dispose();
            }
        }

        private static DiscServer.VirtualFile Data(byte[] data) => new DiscServer.VirtualFile { Data = data, Size = data.Length };

        // index.bdmv playback objects: 4-byte type word (HDMV, access type 0), then playback type and movie object.
        // As on an HDMV disc (Contact): First Playback and Top Menu run an interactive object that jumps to title 1,
        // title 1 is a movie object that plays the playlist. A First Playback object that played the playlist itself
        // was loaded by the OPPO and never started (2026-11-20).
        private static readonly byte[] FirstPlayObject = { 0x40, 0, 0, 0, 0x40, 0, 0, 1, 0, 0, 0, 0 };   // interactive, object 1
        private static readonly byte[] TitleObject = { 0x40, 0, 0, 0, 0x00, 0, 0, 0, 0, 0, 0, 0 };       // movie, object 0

        /// <summary>The disc's index.bdmv with First Playback and Top Menu on object 1 and one title on object 0.</summary>
        internal static byte[] Index(byte[] original)
        {
            if (original.Length < 40 || Encoding.ASCII.GetString(original, 0, 4) != "INDX") throw new InvalidDataException("index.bdmv");
            int indexesStart = BE32(original, 8), extensionStart = BE32(original, 12);
            var indexes = new List<byte>();
            indexes.AddRange(FirstPlayObject);            // First Playback
            indexes.AddRange(FirstPlayObject);            // Top Menu
            indexes.Add(0); indexes.Add(1);               // one title
            indexes.AddRange(TitleObject);                // title 1
            var output = new List<byte>(original.Take(indexesStart));
            output.AddRange(BE32(indexes.Count));
            output.AddRange(indexes);
            while (output.Count % 4 != 0) output.Add(0);
            int newExtension = 0;
            if (extensionStart > 0 && extensionStart < original.Length)
            {
                newExtension = output.Count;
                output.AddRange(original.Skip(extensionStart));   // UHD / HDR information of the disc
            }
            var bytes = output.ToArray();
            Array.Copy(BE32(newExtension), 0, bytes, 12, 4);
            return bytes;
        }

        /// <summary>
        /// MovieObject.bdmv with two objects: 0 = title (resume allowed): PlayPL(playlist); 1 = first play / top
        /// menu: JumpTitle(1).
        /// </summary>
        internal static byte[] MovieObject(byte[] original, int playlist)
        {
            string version = original != null && original.Length >= 8 && Encoding.ASCII.GetString(original, 0, 4) == "MOBJ"
                ? Encoding.ASCII.GetString(original, 4, 4) : "0200";
            var output = new List<byte>(Encoding.ASCII.GetBytes("MOBJ" + version));
            output.AddRange(BE32(0));                     // no extension data
            output.AddRange(new byte[28]);                // reserved
            var data = new List<byte>();
            data.AddRange(BE32(0));                       // reserved
            data.Add(0); data.Add(2);                     // two movie objects
            // Object 0: resume intention on, menu call / title search allowed; PlayPL(playlist)
            data.Add(0x80); data.Add(0);
            data.Add(0); data.Add(1);
            data.AddRange(new byte[] { 0x22, 0x80, 0x00, 0x00 });   // operand count 1, branch / play, immediate: PlayPL
            data.AddRange(BE32(playlist));
            data.AddRange(BE32(0));
            // Object 1: JumpTitle(1)
            data.Add(0); data.Add(0);
            data.Add(0); data.Add(1);
            data.AddRange(new byte[] { 0x21, 0x81, 0x00, 0x00 });   // operand count 1, branch / jump, immediate: JumpTitle
            data.AddRange(BE32(1));
            data.AddRange(BE32(0));
            output.AddRange(BE32(data.Count));
            output.AddRange(data);
            return output.ToArray();
        }

        private static int BE32(byte[] d, int o) => (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];
        private static byte[] BE32(int v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    }
}
