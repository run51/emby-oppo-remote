using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Emby.IsoBluray.Udf;
using MediaBrowser.Model.Logging;

namespace Emby.IsoBluray.Server
{
    /// <summary>
    /// Small local HTTP server (127.0.0.1 only) that serves a .m2ts clip read directly from the
    /// ISO, with Range request support. Emby refuses to probe a ".iso" path, so ffprobe is given
    /// an http://127.0.0.1:port/clip/{token}.m2ts URL instead.
    /// </summary>
    public sealed class ClipStreamServer : IDisposable
    {
        private sealed class Registration
        {
            public string IsoPath;
            public List<Extent> Extents;
            public long Size;
            public DateTime ExpiresUtc;
        }

        private readonly ConcurrentDictionary<string, Registration> _clips = new ConcurrentDictionary<string, Registration>();
        private readonly ILogger _logger;
        private readonly object _startLock = new object();
        private HttpListener _listener;
        private CancellationTokenSource _cts;

        public ClipStreamServer(ILogger logger) { _logger = logger; }

        public int Port { get; private set; }

        private void EnsureStarted()
        {
            if (_listener != null) return;
            lock (_startLock)
            {
                if (_listener != null) return;
                Port = FreePort();
                var listener = new HttpListener();
                listener.Prefixes.Add("http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture) + "/");
                listener.Start();
                _cts = new CancellationTokenSource();
                _listener = listener;
                Task.Run(() => AcceptLoop(_cts.Token));
                _logger.Info("ISO Blu-ray: clip server started on 127.0.0.1:{0}", Port);
            }
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        /// <summary>Registers a clip and returns its local URL. The token expires after <paramref name="lifetime"/>.</summary>
        public string Register(string isoPath, List<Extent> extents, long size, TimeSpan lifetime)
        {
            EnsureStarted();
            Purge();
            string token = Guid.NewGuid().ToString("N");
            _clips[token] = new Registration { IsoPath = isoPath, Extents = extents, Size = size, ExpiresUtc = DateTime.UtcNow + lifetime };
            return "http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture) + "/clip/" + token + ".m2ts";
        }

        public void Unregister(string url)
        {
            int i = url.LastIndexOf('/');
            if (i < 0) return;
            string token = Path.GetFileNameWithoutExtension(url.Substring(i + 1));
            _clips.TryRemove(token, out _);
        }

        private void Purge()
        {
            var now = DateTime.UtcNow;
            foreach (var kv in _clips)
                if (kv.Value.ExpiresUtc < now) _clips.TryRemove(kv.Key, out _);
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch (Exception) when (ct.IsCancellationRequested) { return; }
                catch (HttpListenerException) { return; }
                catch (ObjectDisposedException) { return; }
                _ = Task.Run(() => Handle(ctx, ct));
            }
        }

        private async Task Handle(HttpListenerContext ctx, CancellationToken ct)
        {
            var response = ctx.Response;
            try
            {
                string name = Path.GetFileNameWithoutExtension(ctx.Request.Url.AbsolutePath);
                if (!_clips.TryGetValue(name ?? "", out var reg))
                {
                    response.StatusCode = 404;
                    return;
                }

                long start = 0, end = reg.Size - 1;
                string range = ctx.Request.Headers["Range"];
                bool partial = false;
                if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = range.Substring(6).Split('-');
                    if (parts[0].Length > 0) start = long.Parse(parts[0], CultureInfo.InvariantCulture);
                    if (parts.Length > 1 && parts[1].Length > 0) end = Math.Min(end, long.Parse(parts[1], CultureInfo.InvariantCulture));
                    if (start > end || start >= reg.Size)
                    {
                        response.StatusCode = 416;
                        response.Headers["Content-Range"] = "bytes */" + reg.Size.ToString(CultureInfo.InvariantCulture);
                        return;
                    }
                    partial = true;
                }

                response.StatusCode = partial ? 206 : 200;
                response.ContentType = "video/MP2T";
                response.Headers["Accept-Ranges"] = "bytes";
                if (partial)
                    response.Headers["Content-Range"] = string.Format(CultureInfo.InvariantCulture, "bytes {0}-{1}/{2}", start, end, reg.Size);
                response.ContentLength64 = end - start + 1;
                if (ctx.Request.HttpMethod == "HEAD") return;

                await CopyRange(reg, start, end - start + 1, response.OutputStream, ct).ConfigureAwait(false);
            }
            catch (HttpListenerException) { /* ffprobe closed the connection: expected */ }
            catch (IOException) { /* same */ }
            catch (Exception ex)
            {
                _logger.ErrorException("ISO Blu-ray: error while serving a clip", ex);
                try { response.StatusCode = 500; } catch { }
            }
            finally
            {
                try { response.Close(); } catch { }
            }
        }

        /// <summary>Copies [offset, offset+count) of the clip by walking its extents in the ISO.</summary>
        private static async Task CopyRange(Registration reg, long offset, long count, Stream output, CancellationToken ct)
        {
            var buffer = new byte[1024 * 1024];
            using (var iso = new FileStream(reg.IsoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true))
            {
                long position = 0;
                foreach (var extent in reg.Extents)
                {
                    if (count <= 0) break;
                    long extentEnd = position + extent.Length;
                    if (offset < extentEnd)
                    {
                        long inExtent = offset - position;
                        long toCopy = Math.Min(extent.Length - inExtent, count);
                        iso.Seek(extent.Offset + inExtent, SeekOrigin.Begin);
                        while (toCopy > 0)
                        {
                            int n = await iso.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, toCopy), ct).ConfigureAwait(false);
                            if (n <= 0) return;
                            await output.WriteAsync(buffer, 0, n, ct).ConfigureAwait(false);
                            toCopy -= n;
                            count -= n;
                            offset += n;
                        }
                    }
                    position = extentEnd;
                }
            }
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Close(); } catch { }
        }
    }
}
