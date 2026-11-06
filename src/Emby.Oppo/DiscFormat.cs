using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using Emby.IsoBluray.Udf;

namespace Emby.Oppo
{
    /// <summary>Physical format of a movie: UHD Blu-ray, Blu-ray, DVD, or file type (MKV…).</summary>
    public sealed class FormatInfo
    {
        /// <summary>uhd, bluray, dvd, file, iso (unrecognised image) or folder.</summary>
        public string Kind { get; set; }
        public string Label { get; set; }
    }

    /// <summary>
    /// Detects a movie's format from its files: BDMV or VIDEO_TS folder (on disk or inside the ISO
    /// image, read with the ISO Blu-ray plugin's UDF reader). A Blu-ray is "UHD" if its index.bdmv
    /// file is version 3 ("INDX0300"), a format specific to UHD Blu-ray discs.
    /// The cache holds language-neutral results; the only translated labels ("Folder", "File") are
    /// localised on output.
    /// </summary>
    internal static class DiscFormat
    {
        private static readonly ConcurrentDictionary<string, (DateTime Stamp, FormatInfo Info)> Cache =
            new ConcurrentDictionary<string, (DateTime, FormatInfo)>(StringComparer.Ordinal);

        /// <summary>Format of <paramref name="path"/>, with its label in French if <paramref name="fr"/>.</summary>
        public static FormatInfo Detect(string path, bool fr)
        {
            var info = Detect(path);
            return info == null ? null : new FormatInfo { Kind = info.Kind, Label = Localise(info, fr) };
        }

        private static string Localise(FormatInfo info, bool fr)
        {
            if (info.Label == FolderLabel) return L10n.T(fr, "Folder", "Dossier");
            if (info.Label == FileLabel) return L10n.T(fr, "File", "Fichier");
            return info.Label;
        }

        // Placeholders stored in the cache for the two labels that depend on the language.
        private const string FolderLabel = "\u0001folder";
        private const string FileLabel = "\u0001file";

        private static FormatInfo Detect(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            DateTime stamp;
            try { stamp = Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path); }
            catch (Exception) { return null; }
            if (Cache.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Info;
            var info = DetectUncached(path);
            Cache[path] = (stamp, info);
            return info;
        }

        private static FormatInfo DetectUncached(string path)
        {
            if (Directory.Exists(path))
            {
                string bdmv = string.Equals(Path.GetFileName(path.TrimEnd('/')), "BDMV", StringComparison.OrdinalIgnoreCase)
                    ? path : Path.Combine(path, "BDMV");
                if (Directory.Exists(bdmv))
                {
                    string index = Path.Combine(bdmv, "index.bdmv");
                    return BluRay(File.Exists(index) ? ReadHeader(index) : null);
                }
                if (Directory.Exists(Path.Combine(path, "VIDEO_TS"))
                    || string.Equals(Path.GetFileName(path.TrimEnd('/')), "VIDEO_TS", StringComparison.OrdinalIgnoreCase))
                    return new FormatInfo { Kind = "dvd", Label = "DVD" };
                return new FormatInfo { Kind = "folder", Label = FolderLabel };
            }

            string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            if (ext == "iso")
            {
                try
                {
                    using (var image = new UdfImage(path))
                    {
                        string root = image.Resolve("/BDMV") != null ? "/BDMV" : null;
                        if (root == null)
                        {
                            foreach (var e in image.ListDirectory(image.Root()).Where(e => e.IsDirectory))
                                if (image.Resolve("/" + e.Name + "/BDMV") != null) { root = "/" + e.Name + "/BDMV"; break; }
                        }
                        if (root != null)
                        {
                            var index = image.Resolve(root + "/index.bdmv");
                            return BluRay(index != null ? image.ReadAllBytes(index) : null);
                        }
                        if (image.Resolve("/VIDEO_TS") != null) return new FormatInfo { Kind = "dvd", Label = "DVD" };
                    }
                }
                catch (Exception)
                {
                    // Image unreadable by the UDF reader: fall back to "ISO".
                }
                return new FormatInfo { Kind = "iso", Label = "ISO" };
            }

            string label;
            switch (ext)
            {
                case "m4v": label = "MP4"; break;
                case "mts": label = "M2TS"; break;
                case "": label = FileLabel; break;
                default: label = ext.ToUpperInvariant(); break;
            }
            return new FormatInfo { Kind = "file", Label = label };
        }

        private static FormatInfo BluRay(byte[] indexHeader)
        {
            bool uhd = indexHeader != null && indexHeader.Length >= 8 && Encoding.ASCII.GetString(indexHeader, 0, 8) == "INDX0300";
            return uhd ? new FormatInfo { Kind = "uhd", Label = "UHD Blu-ray" } : new FormatInfo { Kind = "bluray", Label = "Blu-ray" };
        }

        private static byte[] ReadHeader(string file)
        {
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var buffer = new byte[8];
                    int read = fs.Read(buffer, 0, buffer.Length);
                    return read == buffer.Length ? buffer : null;
                }
            }
            catch (Exception) { return null; }
        }
    }
}
