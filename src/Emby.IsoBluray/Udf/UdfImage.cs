using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Emby.IsoBluray.Udf
{
    /// <summary>Contiguous part of a file in the image (offset in bytes from the start of the ISO).</summary>
    public readonly struct Extent
    {
        public Extent(long offset, long length) { Offset = offset; Length = length; }
        public long Offset { get; }
        public long Length { get; }
    }

    public sealed class UdfEntry
    {
        public string Name { get; set; }
        public bool IsDirectory { get; set; }
        internal uint Lbn { get; set; }
        internal ushort PartitionRef { get; set; }
    }

    /// <summary>
    /// Minimal read-only UDF reader, enough for Blu-ray discs (UDF 2.50 with a metadata
    /// partition). C# port of an earlier Python prototype (udf_ls.py).
    /// </summary>
    public sealed class UdfImage : IDisposable
    {
        private const int Sector = 2048;
        private readonly FileStream _stream;
        private uint _partitionStart;
        private List<(uint Sector, uint Length)> _metadataExtents;
        private (uint Lbn, ushort Ref) _fileSetDescriptor;

        public UdfImage(string path)
        {
            Path = path;
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
            ReadVolumeDescriptors();
        }

        public string Path { get; }

        /// <summary>Tells, without throwing, whether the file looks like a UDF image.</summary>
        public static bool LooksLikeUdf(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (fs.Length < 257L * Sector) return false;
                    var buf = new byte[2];
                    foreach (long sector in AnchorSectors(fs.Length))
                    {
                        fs.Seek(sector * Sector, SeekOrigin.Begin);
                        if (fs.Read(buf, 0, 2) == 2 && BitConverter.ToUInt16(buf, 0) == 2) return true;
                    }
                    return false;
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>Possible UDF anchor locations: sector 256, then N-256 and N-1 (end of image).</summary>
        private static IEnumerable<long> AnchorSectors(long fileLength)
        {
            long n = fileLength / Sector;
            yield return 256;
            if (n > 512) yield return n - 256;
            if (n > 257) yield return n - 1;
        }

        private byte[] Read(long sector, int length = Sector)
        {
            var buf = new byte[length];
            _stream.Seek(sector * Sector, SeekOrigin.Begin);
            int read = 0;
            while (read < length)
            {
                int n = _stream.Read(buf, read, length - read);
                if (n == 0) break;
                read += n;
            }
            return buf;
        }

        private static ushort Tag(byte[] b, int offset = 0) => BitConverter.ToUInt16(b, offset);
        private static uint U32(byte[] b, int o) => BitConverter.ToUInt32(b, o);
        private static ushort U16(byte[] b, int o) => BitConverter.ToUInt16(b, o);

        private void ReadVolumeDescriptors()
        {
            byte[] anchor = null;
            foreach (long sector in AnchorSectors(_stream.Length))
            {
                var candidate = Read(sector);
                if (Tag(candidate) == 2) { anchor = candidate; break; }
            }
            if (anchor == null) throw new InvalidDataException("No UDF anchor (start or end of image)");
            uint vdsLength = U32(anchor, 16), vdsLocation = U32(anchor, 20);
            byte[] lvd = null;
            for (uint i = 0; i < vdsLength / Sector; i++)
            {
                var d = Read(vdsLocation + i);
                var t = Tag(d);
                if (t == 5) _partitionStart = U32(d, 188);
                else if (t == 6) lvd = d;
                else if (t == 8) break;
            }
            if (lvd == null) throw new InvalidDataException("Logical Volume Descriptor missing");
            _fileSetDescriptor = (U32(lvd, 252), U16(lvd, 256));
            uint mapCount = U32(lvd, 268);
            int p = 440;
            for (int m = 0; m < mapCount && p < lvd.Length; m++)
            {
                byte type = lvd[p], len = lvd[p + 1];
                // Type 2 map: type(1) length(1) reserved(2) then entity identifier = flags(1) + text(23)
                if (type == 2 && Encoding.ASCII.GetString(lvd, p + 5, 23).StartsWith("*UDF Metadata Partition", StringComparison.Ordinal))
                {
                    uint metadataFileLbn = U32(lvd, p + 40);
                    var fe = Read(_partitionStart + metadataFileLbn);
                    _metadataExtents = new List<(uint, uint)>();
                    foreach (var ad in AllocationDescriptors(fe))
                        _metadataExtents.Add((_partitionStart + ad.Lbn, ad.Length));
                }
                p += len == 0 ? 1 : len;
            }
        }

        private long LbnToSector(uint lbn, ushort partitionRef)
        {
            if (partitionRef == 0 || _metadataExtents == null) return _partitionStart + lbn;
            long offset = lbn;
            foreach (var e in _metadataExtents)
            {
                long sectors = (e.Length + Sector - 1) / Sector;
                if (offset < sectors) return e.Sector + offset;
                offset -= sectors;
            }
            throw new InvalidDataException("Block outside the metadata partition");
        }

        private struct AllocationDescriptor
        {
            public uint Length;
            public uint Lbn;
            public int PartitionRef;   // -1: same partition as the entry
            public byte[] Inline;      // data embedded in the entry
        }

        private static IEnumerable<AllocationDescriptor> AllocationDescriptors(byte[] fe)
        {
            ushort tag = Tag(fe);
            int eaLength, adLength, start;
            if (tag == 261) { eaLength = (int)U32(fe, 168); adLength = (int)U32(fe, 172); start = 176; }
            else if (tag == 266) { eaLength = (int)U32(fe, 208); adLength = (int)U32(fe, 212); start = 216; }
            else throw new InvalidDataException("Unexpected file entry (tag " + tag + ")");

            int flags = U16(fe, 34) & 7;
            int p = start + eaLength, end = p + adLength;
            var list = new List<AllocationDescriptor>();
            if (flags == 0)
            {
                for (; p + 8 <= end; p += 8)
                {
                    uint len = U32(fe, p) & 0x3FFFFFFF;
                    if (len != 0) list.Add(new AllocationDescriptor { Length = len, Lbn = U32(fe, p + 4), PartitionRef = -1 });
                }
            }
            else if (flags == 1)
            {
                for (; p + 16 <= end; p += 16)
                {
                    uint len = U32(fe, p) & 0x3FFFFFFF;
                    if (len != 0) list.Add(new AllocationDescriptor { Length = len, Lbn = U32(fe, p + 4), PartitionRef = U16(fe, p + 8) });
                }
            }
            else if (flags == 3)
            {
                var inline = new byte[adLength];
                Array.Copy(fe, p, inline, 0, adLength);
                list.Add(new AllocationDescriptor { Length = (uint)adLength, Inline = inline });
            }
            else throw new NotSupportedException("Allocation descriptor type " + flags);
            return list;
        }

        private byte[] ReadEntry(uint lbn, ushort partitionRef, out ulong size)
        {
            var fe = Read(LbnToSector(lbn, partitionRef));
            size = BitConverter.ToUInt64(fe, 56);
            return fe;
        }

        /// <summary>Reads a small file entirely (playlist, clip info, directory contents).</summary>
        public byte[] ReadAllBytes(UdfEntry entry) => ReadFile(entry.Lbn, entry.PartitionRef, entry.PartitionRef);

        private byte[] ReadFile(uint lbn, ushort partitionRef, ushort dataPartitionRef)
        {
            var fe = ReadEntry(lbn, partitionRef, out ulong size);
            if (size > 64 * 1024 * 1024) throw new InvalidOperationException("File too large to be read into memory");
            using (var ms = new MemoryStream())
            {
                foreach (var ad in AllocationDescriptors(fe))
                {
                    if (ad.Inline != null) { ms.Write(ad.Inline, 0, ad.Inline.Length); continue; }
                    ushort pref = ad.PartitionRef < 0 ? dataPartitionRef : (ushort)ad.PartitionRef;
                    var data = Read(LbnToSector(ad.Lbn, pref), (int)ad.Length);
                    ms.Write(data, 0, data.Length);
                }
                var all = ms.ToArray();
                if ((ulong)all.Length > size) Array.Resize(ref all, (int)size);
                return all;
            }
        }

        /// <summary>Extents of a large file (.m2ts stream): the data is in the physical partition.</summary>
        public List<Extent> GetExtents(UdfEntry entry, out long size)
        {
            var fe = ReadEntry(entry.Lbn, entry.PartitionRef, out ulong s);
            size = (long)s;
            var result = new List<Extent>();
            long remaining = size;
            foreach (var ad in AllocationDescriptors(fe))
            {
                if (ad.Inline != null) throw new NotSupportedException("Stream embedded in its entry");
                ushort pref = ad.PartitionRef < 0 ? (ushort)0 : (ushort)ad.PartitionRef;
                long len = Math.Min(ad.Length, remaining);
                result.Add(new Extent(LbnToSector(ad.Lbn, pref) * Sector, len));
                remaining -= len;
                if (remaining <= 0) break;
            }
            return result;
        }

        public List<UdfEntry> ListDirectory(UdfEntry dir)
        {
            var data = ReadFile(dir.Lbn, dir.PartitionRef, dir.PartitionRef);
            var entries = new List<UdfEntry>();
            int p = 0;
            while (p + 38 <= data.Length && Tag(data, p) == 257)
            {
                byte characteristics = data[p + 18];
                byte nameLength = data[p + 19];
                uint icbLbn = U32(data, p + 24);
                ushort icbRef = U16(data, p + 28);
                ushort implUseLength = U16(data, p + 36);
                int nameOffset = p + 38 + implUseLength;
                if ((characteristics & 0x08) == 0 && nameLength > 0)
                {
                    entries.Add(new UdfEntry
                    {
                        Name = DecodeName(data, nameOffset, nameLength),
                        IsDirectory = (characteristics & 0x02) != 0,
                        Lbn = icbLbn,
                        PartitionRef = icbRef
                    });
                }
                p += (38 + implUseLength + nameLength + 3) & ~3;
            }
            return entries;
        }

        private static string DecodeName(byte[] b, int offset, int length)
        {
            byte compression = b[offset];
            if (compression == 16) return Encoding.BigEndianUnicode.GetString(b, offset + 1, length - 1);
            var chars = new char[length - 1];
            for (int i = 1; i < length; i++) chars[i - 1] = (char)b[offset + i]; // Latin-1: byte = code point
            return new string(chars);
        }

        public UdfEntry Root()
        {
            var fsd = Read(LbnToSector(_fileSetDescriptor.Lbn, _fileSetDescriptor.Ref));
            if (Tag(fsd) != 256) throw new InvalidDataException("File Set Descriptor missing");
            return new UdfEntry { Name = "/", IsDirectory = true, Lbn = U32(fsd, 404), PartitionRef = U16(fsd, 408) };
        }

        /// <summary>Resolves a path such as /BDMV/PLAYLIST (case-insensitive); null if missing.</summary>
        public UdfEntry Resolve(string path)
        {
            var current = Root();
            foreach (var part in path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                UdfEntry next = null;
                foreach (var e in ListDirectory(current))
                {
                    if (string.Equals(e.Name, part, StringComparison.OrdinalIgnoreCase)) { next = e; break; }
                }
                if (next == null) return null;
                current = next;
            }
            return current;
        }

        public void Dispose() => _stream.Dispose();
    }
}
