using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.IsoBluray.Bluray;
using Emby.IsoBluray.Server;
using Emby.IsoBluray.Udf;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;

namespace Emby.IsoBluray
{
    /// <summary>What the plugin knows about an ISO after examining it, reused to set the languages.</summary>
    public sealed class IsoAnalysis
    {
        public DiscInfo Disc { get; set; }
        /// <summary>PID of each track, in the order ffprobe numbers them (Emby index).</summary>
        public List<int> PidByProbeIndex { get; set; }
        public DateTime AnalysedUtc { get; set; }
    }

    /// <summary>
    /// "Mounts" a Blu-ray ISO for Emby: finds the main playlist and exposes its clips to ffprobe
    /// through the local HTTP server. No system mount, read-only.
    /// </summary>
    public sealed class IsoBlurayMounter : IMediaMounter
    {
        internal static readonly ConcurrentDictionary<string, IsoAnalysis> Analyses =
            new ConcurrentDictionary<string, IsoAnalysis>(StringComparer.Ordinal);

        private static ClipStreamServer _server;
        private static readonly object ServerLock = new object();
        private readonly ILogger _logger;

        public IsoBlurayMounter(ILogManager logManager)
        {
            _logger = logManager.GetLogger("IsoBluray");
        }

        public string Name => "ISO Blu-ray mounter";

        /// <summary>Cache key independent of the Unicode form of the path (composed or decomposed accents).</summary>
        internal static string Key(string path) => path.Normalize(System.Text.NormalizationForm.FormC);

        internal static ClipStreamServer GetServer(ILogger logger)
        {
            lock (ServerLock) return _server ?? (_server = new ClipStreamServer(logger));
        }

        public bool CanMount(IFileSystem fileSystem, FileSystemMetadata fileInfo, string container)
        {
            if (fileInfo == null || fileInfo.IsDirectory) return false;
            if (!string.Equals(Path.GetExtension(fileInfo.FullName), ".iso", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(container, "dvdiso", StringComparison.OrdinalIgnoreCase)) return false;
            if (!UdfImage.LooksLikeUdf(fileInfo.FullName)) return false;
            try
            {
                using (var image = new UdfImage(fileInfo.FullName))
                    return DiscInfo.FindBdmvRoot(image) != null;
            }
            catch (Exception ex)
            {
                _logger.Debug("ISO Blu-ray: {0} is not a readable Blu-ray ({1})", fileInfo.FullName, ex.Message);
                return false;
            }
        }

        public Task<MediaMount> Mount(IFileSystem fileSystem, FileSystemMetadata fileInfo, string container, CancellationToken cancellationToken)
        {
            string iso = fileInfo.FullName;
            string language = Plugin.PreferredDiscLanguage();

            DiscInfo disc;
            using (var image = new UdfImage(iso))
                disc = DiscInfo.Examine(image, language);

            var firstClip = disc.ClipFiles[0];
            var pmt = ProgramMap.Read(firstClip.Path, firstClip.Extents);
            Analyses[Key(iso)] = new IsoAnalysis
            {
                Disc = disc,
                PidByProbeIndex = ProbeOrder(pmt),
                AnalysedUtc = DateTime.UtcNow
            };

            var server = GetServer(_logger);
            var urls = disc.ClipFiles
                .Select(c => server.Register(iso, c.Extents, c.Size, TimeSpan.FromHours(2)))
                .ToList();

            _logger.Info("ISO Blu-ray: {0} -> playlist {1} ({2}), {3} clip(s)",
                iso, disc.MainPlaylist.Name, disc.MainPlaylist.Duration, urls.Count);

            var mount = new MediaMount
            {
                MountedPathInfo = new FileSystemMetadata
                {
                    FullName = string.Join("|", urls),
                    Exists = true,
                    IsDirectory = false
                },
                MountedProtocol = MediaProtocol.Http,
                MountedContainer = "ts",
                FileSystem = fileSystem
            };
            mount.AddDisposable(new Releaser(() => urls.ForEach(server.Unregister)));
            return Task.FromResult(mount);
        }

        /// <summary>
        /// ffprobe exposes one track per PMT stream, plus an extra AC3 track for the "core" of a
        /// TrueHD track (same PID). This reproduces that order.
        /// </summary>
        internal static List<int> ProbeOrder(List<ProgramMap.ElementaryStream> pmt)
        {
            var order = new List<int>();
            foreach (var es in pmt)
            {
                order.Add(es.Pid);
                if (es.StreamType == 0x83) order.Add(es.Pid);
            }
            return order;
        }

        public void Dispose() { }

        private sealed class Releaser : IDisposable
        {
            private readonly Action _action;
            public Releaser(Action action) { _action = action; }
            public void Dispose() { try { _action(); } catch { } }
        }
    }
}
