using System.Collections.Generic;
using System.IO;
using Emby.IsoBluray.Udf;

namespace Emby.IsoBluray.Bluray
{
    /// <summary>
    /// Reads the PMT (MPEG-TS program map table) at the start of a .m2ts clip to learn the track
    /// order as ffprobe will number them. Blu-ray packets: 192 bytes (4 + 188). The PMT of a rich
    /// disc (many languages) spans several packets: it is reassembled.
    /// </summary>
    public static class ProgramMap
    {
        public sealed class ElementaryStream
        {
            public int Pid { get; set; }
            public byte StreamType { get; set; }
        }

        public static List<ElementaryStream> Read(string isoPath, List<Extent> extents, int maxBytes = 8 * 1024 * 1024)
        {
            var empty = new List<ElementaryStream>();
            if (extents.Count == 0) return empty;
            byte[] data = ReadStart(isoPath, extents[0], maxBytes, out int length);

            int pmtPid = -1;
            List<byte> pmt = null;      // PMT section being reassembled
            int pmtTotal = 0;

            for (int p = 0; p + 192 <= length; p += 192)
            {
                int ts = p + 4;
                if (data[ts] != 0x47) continue;
                bool start = (data[ts + 1] & 0x40) != 0;
                int pid = ((data[ts + 1] & 0x1F) << 8) | data[ts + 2];
                int afc = (data[ts + 3] >> 4) & 3;
                if (afc == 0 || afc == 2) continue;
                int payload = ts + 4 + (afc == 3 ? 1 + data[ts + 4] : 0);
                int packetEnd = ts + 188;
                if (payload >= packetEnd) continue;

                if (pid == 0 && pmtPid < 0 && start)
                {
                    int s = payload + 1 + data[payload];
                    if (data[s] != 0x00) continue;
                    int sectionLength = ((data[s + 1] & 0x0F) << 8) | data[s + 2];
                    for (int q = s + 8; q + 4 <= s + 3 + sectionLength - 4 && q + 4 <= packetEnd; q += 4)
                    {
                        int program = (data[q] << 8) | data[q + 1];
                        if (program != 0) { pmtPid = ((data[q + 2] & 0x1F) << 8) | data[q + 3]; break; }
                    }
                }
                else if (pid == pmtPid && pmtPid >= 0)
                {
                    if (start)
                    {
                        int s = payload + 1 + data[payload];
                        if (s >= packetEnd || data[s] != 0x02) continue;
                        pmtTotal = 3 + (((data[s + 1] & 0x0F) << 8) | data[s + 2]);
                        pmt = new List<byte>(pmtTotal);
                        for (int i = s; i < packetEnd && pmt.Count < pmtTotal; i++) pmt.Add(data[i]);
                    }
                    else if (pmt != null)
                    {
                        for (int i = payload; i < packetEnd && pmt.Count < pmtTotal; i++) pmt.Add(data[i]);
                    }
                    if (pmt != null && pmt.Count >= pmtTotal) return ParsePmt(pmt.ToArray());
                }
            }
            return empty;
        }

        private static byte[] ReadStart(string isoPath, Extent first, int maxBytes, out int length)
        {
            length = (int)System.Math.Min(maxBytes, first.Length);
            var data = new byte[length];
            using (var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fs.Seek(first.Offset, SeekOrigin.Begin);
                int read = 0;
                while (read < length)
                {
                    int n = fs.Read(data, read, length - read);
                    if (n <= 0) break;
                    read += n;
                }
                length = read;
            }
            return data;
        }

        private static List<ElementaryStream> ParsePmt(byte[] d)
        {
            var list = new List<ElementaryStream>();
            int end = d.Length - 4; // without the CRC
            int programInfoLength = ((d[10] & 0x0F) << 8) | d[11];
            int q = 12 + programInfoLength;
            while (q + 5 <= end)
            {
                list.Add(new ElementaryStream { StreamType = d[q], Pid = ((d[q + 1] & 0x1F) << 8) | d[q + 2] });
                q += 5 + (((d[q + 3] & 0x0F) << 8) | d[q + 4]);
            }
            return list;
        }
    }
}
