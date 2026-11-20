using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace Emby.Oppo.Oppo
{
    /// <summary>
    /// Small HTTP server that serves a disc image (ISO) byte for byte, with Range requests, to the OPPO.
    /// Used to play an ISO over DLNA with the oppo-dv-mkv mod ("DLNA Disc Images"), which reads the image
    /// through HTTP ranges. Emby's own stream URL cannot be used: for an ISO, the ISO Blu-ray plugin answers
    /// with the main title as an M2TS stream, not with the image itself.
    /// Each file is reachable only through a random token (http://server:port/disc/{token}/{name}.iso).
    /// Written on a raw socket and answering like Python's http.server (HTTP/1.0, one request per
    /// connection, no Host header required): the player read an image from that server, while it sent
    /// nothing usable to .NET's HttpListener (2026-11-19).
    /// </summary>
    public static class DiscServer
    {
        /// <summary>Piece of a served file: a byte range of a file on disk.</summary>
        public sealed class Segment
        {
            public string Path;
            public long Offset;
            public long Length;
        }

        private sealed class Registration
        {
            public List<Segment> Segments;   // a file (ISO, or clips end to end)…
            public string Folder;            // …or a disc folder (the one that contains BDMV), served over WebDAV
            public long Size;
            public string ContentType;
            public DateTime ExpiresUtc;
            public int Requests;
        }

        private static readonly ConcurrentDictionary<string, Registration> Files = new ConcurrentDictionary<string, Registration>();
        private static readonly object StartLock = new object();
        private static TcpListener _listener;
        private static int _port;
        private static ILogger _logger;
        private static int _connections;

        /// <summary>
        /// Registers a file and returns its URL as seen by the OPPO. Starts the server on the first call
        /// (or restarts it if the port changed).
        /// </summary>
        public static string Register(string path, string serverAddress, int port, TimeSpan lifetime, ILogger logger, string contentType = "video/iso")
        {
            long size = new FileInfo(path).Length;
            var segments = new List<Segment> { new Segment { Path = path, Offset = 0, Length = size } };
            return RegisterSegments(segments, SafeName(path), contentType, serverAddress, port, lifetime, logger);
        }

        /// <summary>
        /// Registers a virtual file made of byte ranges put end to end (e.g. the .m2ts clips of a Blu-ray's main
        /// playlist, read inside the ISO) and returns its URL. <paramref name="fileName"/> must be URL-safe.
        /// </summary>
        public static string RegisterSegments(List<Segment> segments, string fileName, string contentType, string serverAddress, int port, TimeSpan lifetime, ILogger logger)
        {
            EnsureStarted(port, logger);
            Purge();
            string token = Guid.NewGuid().ToString("N");
            long size = 0;
            foreach (var seg in segments) size += seg.Length;
            Files[token] = new Registration { Segments = segments, Size = size, ContentType = contentType, ExpiresUtc = DateTime.UtcNow + lifetime };
            return "http://" + serverAddress + ":" + port.ToString(CultureInfo.InvariantCulture) + "/disc/" + token + "/" + fileName;
        }

        /// <summary>
        /// Registers a Blu-ray / DVD folder (the folder that contains BDMV or VIDEO_TS) and returns the URL of its root,
        /// ending in "/". The oppo-dv-mkv mod lists it over WebDAV (PROPFIND) and builds a disc image from it.
        /// </summary>
        public static string RegisterFolder(string discRoot, string serverAddress, int port, TimeSpan lifetime, ILogger logger)
        {
            EnsureStarted(port, logger);
            Purge();
            string token = Guid.NewGuid().ToString("N");
            Files[token] = new Registration { Folder = System.IO.Path.GetFullPath(discRoot).TrimEnd('/', '\\'), ExpiresUtc = DateTime.UtcNow + lifetime };
            return "http://" + serverAddress + ":" + port.ToString(CultureInfo.InvariantCulture) + "/disc/" + token + "/";
        }

        /// <summary>Number of HTTP requests received for a URL returned by <see cref="Register"/> (0 if unknown).</summary>
        public static int RequestCount(string url)
        {
            var segments = new Uri(url).AbsolutePath.Trim('/').Split('/');
            return segments.Length >= 2 && Files.TryGetValue(segments[1], out var reg) ? reg.Requests : 0;
        }

        /// <summary>Connections accepted since the server started, whatever they asked for.</summary>
        public static int Connections => _connections;

        /// <summary>ASCII file name for the URL; keeps the extension, which the mod uses to recognise a disc image.</summary>
        private static string SafeName(string path)
        {
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            var sb = new StringBuilder();
            foreach (char c in System.IO.Path.GetFileNameWithoutExtension(path).Normalize(NormalizationForm.FormD))
            {
                if (c < 128 && (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')) sb.Append(c);
                else if (c == ' ') sb.Append('_');
            }
            string name = sb.Length > 0 ? sb.ToString() : "disc";
            if (name.Length > 80) name = name.Substring(0, 80);
            return name + (string.IsNullOrEmpty(ext) ? ".iso" : ext);
        }

        private static void Purge()
        {
            var now = DateTime.UtcNow;
            foreach (var kv in Files)
                if (kv.Value.ExpiresUtc < now) Files.TryRemove(kv.Key, out _);
        }

        private static void EnsureStarted(int port, ILogger logger)
        {
            lock (StartLock)
            {
                _logger = logger;
                if (_listener != null && _port == port) return;
                if (_listener != null)
                {
                    try { _listener.Stop(); } catch { }
                    _listener = null;
                }
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                _listener = listener;
                _port = port;
                Task.Run(() => AcceptLoop(listener));
                logger.Info("OPPO: disc image server listening on port {0}", port);
            }
        }

        private static async Task AcceptLoop(TcpListener listener)
        {
            while (true)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { if (!ReferenceEquals(listener, _listener)) return; continue; }
                catch (InvalidOperationException) { return; }
                Interlocked.Increment(ref _connections);
                _ = Task.Run(() => Handle(client));
            }
        }

        private static async Task Handle(TcpClient client)
        {
            string remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    var stream = client.GetStream();
                    var (requestLine, headers) = await ReadRequest(stream).ConfigureAwait(false);
                    if (requestLine == null)
                    {
                        _logger?.Debug("OPPO disc image: connection from {0} closed without a request", remote);
                        return;
                    }
                    headers.TryGetValue("Range", out string range);

                    var parts = requestLine.Split(' ');
                    string method = parts[0].ToUpperInvariant();
                    string target = parts.Length > 1 ? parts[1] : "/";
                    int q = target.IndexOf('?');
                    if (q >= 0) target = target.Substring(0, q);
                    if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase))   // absolute form
                    {
                        int slash = target.IndexOf('/', 7);
                        target = slash >= 0 ? target.Substring(slash) : "/";
                    }
                    var segments = Uri.UnescapeDataString(target).Trim('/').Split('/');

                    if (segments.Length < 2 || !string.Equals(segments[0], "disc", StringComparison.OrdinalIgnoreCase)
                        || !Files.TryGetValue(segments[1].ToLowerInvariant(), out var reg) || reg.ExpiresUtc < DateTime.UtcNow)
                    {
                        _logger?.Debug("OPPO disc image: 404 for \"{0}\" from {1}", requestLine, remote);
                        await WriteHead(stream, 404, "Not Found", null).ConfigureAwait(false);
                        return;
                    }
                    Interlocked.Increment(ref reg.Requests);

                    List<Segment> fileSegments;
                    string contentType;
                    long size;
                    if (reg.Folder != null)
                    {
                        // Disc folder: WebDAV listing, or one of its files.
                        string local = LocalPath(reg.Folder, segments);
                        if (local == null || (!Directory.Exists(local) && !File.Exists(local)))
                        {
                            _logger?.Debug("OPPO disc folder: 404 for \"{0}\" from {1}", requestLine, remote);
                            await WriteHead(stream, 404, "Not Found", null).ConfigureAwait(false);
                            return;
                        }
                        if (method == "OPTIONS")
                        {
                            await WriteHead(stream, 200, "OK", new[] { "DAV: 1", "Allow: OPTIONS, GET, HEAD, PROPFIND" }).ConfigureAwait(false);
                            return;
                        }
                        if (method == "PROPFIND")
                        {
                            headers.TryGetValue("Depth", out string depth);
                            string prefix = "/disc/" + segments[1].ToLowerInvariant();
                            byte[] xml = Encoding.UTF8.GetBytes(Multistatus(reg.Folder, local, prefix, depth));
                            await WriteHead(stream, 207, "Multi-Status", new[] { "Content-Type: application/xml; charset=utf-8", "Content-Length: " + xml.Length.ToString(CultureInfo.InvariantCulture) }, noLength: true).ConfigureAwait(false);
                            await stream.WriteAsync(xml, 0, xml.Length).ConfigureAwait(false);
                            _logger?.Debug("OPPO disc folder: PROPFIND {0} depth {1} → {2} bytes", target, depth ?? "-", xml.Length);
                            return;
                        }
                        if (method != "GET" && method != "HEAD" || Directory.Exists(local))
                        {
                            await WriteHead(stream, 405, "Method Not Allowed", new[] { "Allow: OPTIONS, GET, HEAD, PROPFIND" }).ConfigureAwait(false);
                            return;
                        }
                        size = new FileInfo(local).Length;
                        fileSegments = new List<Segment> { new Segment { Path = local, Offset = 0, Length = size } };
                        contentType = "application/octet-stream";
                    }
                    else
                    {
                        if (method != "GET" && method != "HEAD")
                        {
                            await WriteHead(stream, 501, "Not Implemented", null).ConfigureAwait(false);
                            return;
                        }
                        if (!File.Exists(reg.Segments[0].Path))
                        {
                            await WriteHead(stream, 404, "Not Found", null).ConfigureAwait(false);
                            return;
                        }
                        fileSegments = reg.Segments;
                        contentType = reg.ContentType;
                        size = reg.Size;
                    }

                    if (size == 0)
                    {
                        await WriteHead(stream, 200, "OK", new[] { "Content-Type: " + contentType }).ConfigureAwait(false);
                        return;
                    }
                    long start = 0, end = size - 1;
                    bool partial = false;
                    if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                    {
                        var r = range.Substring(6).Split(',')[0].Trim().Split('-');
                        if (r[0].Length > 0)
                        {
                            start = long.Parse(r[0], CultureInfo.InvariantCulture);
                            if (r.Length > 1 && r[1].Length > 0) end = Math.Min(end, long.Parse(r[1], CultureInfo.InvariantCulture));
                        }
                        else if (r.Length > 1 && r[1].Length > 0)
                        {
                            start = Math.Max(0, size - long.Parse(r[1], CultureInfo.InvariantCulture));   // last N bytes
                        }
                        if (start > end || start >= size)
                        {
                            await WriteHead(stream, 416, "Requested Range Not Satisfiable", new[] { "Content-Range: bytes */" + size.ToString(CultureInfo.InvariantCulture) }).ConfigureAwait(false);
                            return;
                        }
                        partial = true;
                    }

                    long length = end - start + 1;
                    var extra = new List<string> { "Content-Type: " + contentType, "Accept-Ranges: bytes", "Content-Length: " + length.ToString(CultureInfo.InvariantCulture) };
                    if (partial) extra.Add(string.Format(CultureInfo.InvariantCulture, "Content-Range: bytes {0}-{1}/{2}", start, end, size));
                    await WriteHead(stream, partial ? 206 : 200, partial ? "Partial Content" : "OK", extra, noLength: true).ConfigureAwait(false);
                    if (method == "HEAD") return;

                    await CopyRange(fileSegments, start, length, stream).ConfigureAwait(false);
                }
                catch (IOException) { /* the player closed the connection: expected while seeking */ }
                catch (SocketException) { /* same */ }
                catch (Exception ex)
                {
                    _logger?.ErrorException("OPPO: error while serving a disc image to " + remote, ex);
                }
            }
        }

        /// <summary>Local path for /disc/{token}/a/b, kept inside the registered folder (null otherwise).</summary>
        private static string LocalPath(string root, string[] segments)
        {
            var rest = new List<string>();
            for (int i = 2; i < segments.Length; i++)
            {
                string part = segments[i];
                if (part.Length == 0 || part == ".") continue;
                if (part == ".." || part.IndexOf('\\') >= 0) return null;
                rest.Add(part);
            }
            string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, string.Join(System.IO.Path.DirectorySeparatorChar.ToString(), rest)));
            var comparison = System.IO.Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;   // Windows
            return string.Equals(full, root, comparison) || full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, comparison) ? full : null;
        }

        /// <summary>
        /// WebDAV multistatus for a folder or file: the entry itself, then its children (Depth 1) or everything
        /// below it (Depth infinity, the default). The mod reads href, collection and getcontentlength only.
        /// </summary>
        private static string Multistatus(string root, string local, string prefix, string depth)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<D:multistatus xmlns:D=\"DAV:\">\n");
            void Entry(string path)
            {
                bool dir = Directory.Exists(path);
                string rel = path.Length > root.Length ? path.Substring(root.Length + 1) : "";
                var href = new StringBuilder(prefix);
                foreach (var part in rel.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)) href.Append('/').Append(Uri.EscapeDataString(part));
                if (dir) href.Append('/');
                sb.Append("<D:response><D:href>").Append(System.Security.SecurityElement.Escape(href.ToString())).Append("</D:href><D:propstat><D:prop>");
                if (dir) sb.Append("<D:resourcetype><D:collection/></D:resourcetype>");
                else sb.Append("<D:resourcetype/><D:getcontentlength>").Append(new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture)).Append("</D:getcontentlength>");
                sb.Append("<D:getlastmodified>").Append((dir ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path)).ToString("r", CultureInfo.InvariantCulture)).Append("</D:getlastmodified>");
                sb.Append("</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>\n");
            }
            void Children(string dir, bool recursive)
            {
                foreach (var d in Directory.GetDirectories(dir).OrderBy(x => x, StringComparer.Ordinal))
                {
                    if (System.IO.Path.GetFileName(d).StartsWith(".", StringComparison.Ordinal) || System.IO.Path.GetFileName(d).StartsWith("@", StringComparison.Ordinal)) continue;   // .DS_Store, Synology @eaDir
                    Entry(d);
                    if (recursive) Children(d, true);
                }
                foreach (var f in Directory.GetFiles(dir).OrderBy(x => x, StringComparer.Ordinal))
                {
                    if (System.IO.Path.GetFileName(f).StartsWith(".", StringComparison.Ordinal)) continue;
                    Entry(f);
                }
            }
            Entry(local);
            if (Directory.Exists(local) && depth != "0") Children(local, !string.Equals(depth, "1", StringComparison.Ordinal));
            sb.Append("</D:multistatus>\n");
            return sb.ToString();
        }

        /// <summary>Writes <paramref name="length"/> bytes of the virtual file from <paramref name="start"/>.</summary>
        private static async Task CopyRange(List<Segment> segments, long start, long length, Stream output)
        {
            var buffer = new byte[1 << 20];
            long logical = 0, left = length, position = start;
            FileStream file = null;
            string openPath = null;
            try
            {
                foreach (var seg in segments)
                {
                    if (left <= 0) break;
                    long segEnd = logical + seg.Length;
                    if (position < segEnd)
                    {
                        if (openPath != seg.Path)
                        {
                            file?.Dispose();
                            file = new FileStream(seg.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
                            openPath = seg.Path;
                        }
                        file.Seek(seg.Offset + (position - logical), SeekOrigin.Begin);
                        long inSeg = Math.Min(left, segEnd - position);
                        while (inSeg > 0)
                        {
                            int read = await file.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, inSeg)).ConfigureAwait(false);
                            if (read <= 0) return;
                            await output.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                            inSeg -= read; left -= read; position += read;
                        }
                    }
                    logical = segEnd;
                }
            }
            finally
            {
                file?.Dispose();
            }
        }

        /// <summary>
        /// Reads the request line and headers (up to 16 KB, 15 s), then discards the body (PROPFIND sends one):
        /// closing a socket with unread data would reset the connection before the player reads the answer.
        /// Returns a null line if nothing came.
        /// </summary>
        private static async Task<(string, Dictionary<string, string>)> ReadRequest(NetworkStream stream)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var data = new List<byte>();
            var buffer = new byte[4096];
            int headerEnd = -1, separator = 0;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                while (data.Count < 16384 && headerEnd < 0)
                {
                    int n;
                    try { n = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    if (n <= 0) break;
                    for (int i = 0; i < n; i++) data.Add(buffer[i]);
                    (headerEnd, separator) = FindHeaderEnd(data);
                }
                if (data.Count == 0) return (null, headers);
                int end = headerEnd >= 0 ? headerEnd : data.Count;
                var lines = Encoding.ASCII.GetString(data.GetRange(0, end).ToArray()).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                string requestLine = lines[0].Trim();
                for (int i = 1; i < lines.Length; i++)
                {
                    int colon = lines[i].IndexOf(':');
                    if (colon > 0) headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
                }
                if (headerEnd >= 0 && headers.TryGetValue("Content-Length", out var cl) && long.TryParse(cl, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bodyLength))
                {
                    long left = Math.Min(bodyLength, 1 << 20) - (data.Count - headerEnd - separator);
                    while (left > 0)
                    {
                        int n;
                        try { n = await stream.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, left), cts.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                        if (n <= 0) break;
                        left -= n;
                    }
                }
                return (requestLine.Length > 0 ? requestLine : null, headers);
            }
        }

        /// <summary>Position of the blank line ending the headers, and its length (4 for CRLFCRLF, 2 for LFLF); -1 if not there yet.</summary>
        private static (int, int) FindHeaderEnd(List<byte> d)
        {
            for (int i = 0; i + 1 < d.Count; i++)
            {
                if (i + 3 < d.Count && d[i] == '\r' && d[i + 1] == '\n' && d[i + 2] == '\r' && d[i + 3] == '\n') return (i, 4);
                if (d[i] == '\n' && d[i + 1] == '\n') return (i, 2);
            }
            return (-1, 0);
        }

        private static async Task WriteHead(NetworkStream stream, int status, string reason, IEnumerable<string> extra, bool noLength = false)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.0 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(reason).Append("\r\n");
            sb.Append("Server: Emby.Oppo\r\n");
            sb.Append("Date: ").Append(DateTime.UtcNow.ToString("r", CultureInfo.InvariantCulture)).Append("\r\n");
            if (extra != null) foreach (var h in extra) sb.Append(h).Append("\r\n");
            if (!noLength) sb.Append("Content-Length: 0\r\n");
            sb.Append("Connection: close\r\n\r\n");
            var bytes = Encoding.ASCII.GetBytes(sb.ToString());
            await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        }
    }
}
