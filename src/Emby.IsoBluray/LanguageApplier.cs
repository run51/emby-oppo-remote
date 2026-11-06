using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Emby.IsoBluray.Bluray;
using Emby.IsoBluray.Udf;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace Emby.IsoBluray
{
    /// <summary>
    /// After Emby has analysed an ISO, sets the languages read from the Blu-ray playlist on the
    /// tracks detected by ffprobe (the .m2ts stream itself carries no language).
    /// </summary>
    public sealed class LanguageApplier : IServerEntryPoint
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly ILogger _logger;

        public LanguageApplier(ILibraryManager libraryManager, IItemRepository itemRepository, ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _logger = logManager.GetLogger("IsoBluray");
            Current = this;
        }

        /// <summary>Active instance (used by the folder backfill scheduled task).</summary>
        internal static LanguageApplier Current { get; private set; }

        public void Run()
        {
            _libraryManager.ItemUpdated += OnItemChanged;
            _libraryManager.ItemAdded += OnItemChanged;
        }

        private void OnItemChanged(object sender, ItemChangeEventArgs e)
        {
            try
            {
                if (!(e.Item is Video video) || video.InternalId <= 0) return;
                string path = video.Path;
                if (string.IsNullOrEmpty(path)) return;
                if (path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                {
                    if (IsoBlurayMounter.Analyses.TryRemove(IsoBlurayMounter.Key(path), out var analysis)) Apply(video, analysis);
                }
                else
                {
                    FixFolder(video, onlyIfNeeded: true);
                    // Absurd runtime of a Blu-ray folder (see FixRuntime): fix it from the playlist.
                    if (SuspiciousRuntime(video) && DiscInfo.FindBdmvFolder(path) != null) FixRuntimeFromDisc(video);
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("ISO Blu-ray: could not set track languages", ex);
            }
        }

        /// <summary>
        /// Movie stored as a Blu-ray folder: Emby analyses it with its own module, which does not set
        /// languages and leaves the Dolby Vision layer and the AC3 core of TrueHD tracks as separate
        /// tracks. Re-reads the folder's main playlist and fixes that. Returns true if the movie is a
        /// Blu-ray folder that was processed.
        /// </summary>
        internal bool FixFolder(Video video, bool onlyIfNeeded)
        {
            string bdmv = DiscInfo.FindBdmvFolder(video.Path);
            if (bdmv == null) return false;

            if (onlyIfNeeded)
            {
                var current = _itemRepository.GetMediaStreams(new MediaStreamQuery { ItemId = video.InternalId }, CancellationToken.None)
                                             .Where(s => !s.IsExternal).OrderBy(s => s.Index).ToList();
                bool missingLanguage = current.Any(s => (s.Type == MediaStreamType.Audio || s.Type == MediaStreamType.Subtitle) && string.IsNullOrEmpty(s.Language));
                bool extraVideo = current.Count(s => s.Type == MediaStreamType.Video) > 1;
                bool core = current.Zip(current.Skip(1), (a, b) => a.Type == MediaStreamType.Audio && b.Type == MediaStreamType.Audio
                                                                   && string.Equals(a.Codec, "truehd", StringComparison.OrdinalIgnoreCase)
                                                                   && string.Equals(b.Codec, "ac3", StringComparison.OrdinalIgnoreCase)).Any(x => x);
                if (!missingLanguage && !extraVideo && !core) return true;
            }

            string language = Plugin.PreferredDiscLanguage();
            var disc = DiscInfo.ExamineFolder(bdmv, language);
            var first = disc.ClipFiles[0];
            var analysis = new IsoAnalysis
            {
                Disc = disc,
                PidByProbeIndex = IsoBlurayMounter.ProbeOrder(ProgramMap.Read(first.Path, first.Extents)),
                AnalysedUtc = DateTime.UtcNow
            };
            Apply(video, analysis);
            return true;
        }

        /// <summary>Runtime missing or implausible for a movie or a concert (more than 6 h).</summary>
        internal static bool SuspiciousRuntime(Video video) =>
            !video.RunTimeTicks.HasValue || video.RunTimeTicks.Value <= 0 || video.RunTimeTicks.Value > TimeSpan.FromHours(6).Ticks;

        /// <summary>Re-reads the main playlist of a Blu-ray ISO or folder and fixes the runtime if needed.</summary>
        internal bool FixRuntimeFromDisc(Video video)
        {
            string language = Plugin.PreferredDiscLanguage();
            DiscInfo disc;
            if (video.Path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using (var image = new UdfImage(video.Path))
                    {
                        if (DiscInfo.FindBdmvRoot(image) == null) return false;   // DVD or other format: nothing to fix
                        disc = DiscInfo.Examine(image, language);
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    _logger.Debug("ISO Blu-ray: {0} unreadable for the runtime ({1})", Path.GetFileName(video.Path), ex.Message);
                    return false;
                }
            }
            else
            {
                string bdmv = DiscInfo.FindBdmvFolder(video.Path);
                if (bdmv == null) return false;
                disc = DiscInfo.ExamineFolder(bdmv, language);
            }
            return FixRuntime(video, disc.MainPlaylist);
        }

        /// <summary>
        /// ffprobe measures the runtime from the stream timestamps. When the playlist chains several
        /// .m2ts files whose clock restarts at zero, the computation overflows (about 16 or 25 h
        /// instead of 2 h). The runtime written in the playlist wins, if it is trustworthy: no repeated
        /// item (looping playlist) and less than 6 h. A runtime within 10 % of it is left untouched.
        /// </summary>
        private bool FixRuntime(Video video, Playlist playlist)
        {
            if (playlist == null) return false;
            long expected = playlist.Duration.Ticks;
            if (expected < TimeSpan.FromMinutes(1).Ticks || expected > TimeSpan.FromHours(6).Ticks) return false;
            if (playlist.Clips.Distinct(StringComparer.OrdinalIgnoreCase).Count() != playlist.Clips.Count) return false;
            long current = video.RunTimeTicks ?? 0;
            if (current > 0 && Math.Abs(current - expected) <= expected / 10) return false;

            video.RunTimeTicks = expected;
            video.UpdateToRepository(ItemUpdateType.MetadataImport);
            _logger.Info("ISO Blu-ray: {0}, runtime fixed {1} -> {2} (playlist {3})",
                Path.GetFileName(video.Path.TrimEnd('/')), TimeSpan.FromTicks(current), playlist.Duration, playlist.Name);
            return true;
        }

        private void Apply(Video video, IsoAnalysis analysis)
        {
            FixRuntime(video, analysis.Disc.MainPlaylist);
            var streams = _itemRepository.GetMediaStreams(new MediaStreamQuery { ItemId = video.InternalId }, CancellationToken.None);
            var embedded = streams.Where(s => !s.IsExternal).OrderBy(s => s.Index).ToList();
            if (embedded.Count == 0)
            {
                // ffprobe failed (some streams crash Emby's ffprobe): describe the tracks from the
                // disc playlist.
                FillFromPlaylist(video, streams, analysis);
                return;
            }
            var order = analysis.PidByProbeIndex;
            if (order == null || order.Count != embedded.Count)
            {
                _logger.Info("ISO Blu-ray: {0}, {1} ffprobe tracks for {2} in the PMT, languages not set",
                    Path.GetFileName(video.Path), embedded.Count, order?.Count ?? 0);
                return;
            }

            var languageByPid = analysis.Disc.MainPlaylist.Streams
                .Where(s => !string.IsNullOrEmpty(s.Language))
                .GroupBy(s => s.Pid)
                .ToDictionary(g => g.Key, g => g.First().Language);
            var inPlaylist = new HashSet<int>(analysis.Disc.MainPlaylist.Streams.Select(s => s.Pid));

            int applied = 0;
            for (int i = 0; i < embedded.Count; i++)
            {
                var stream = embedded[i];
                if (stream.Type == MediaStreamType.Video) continue;
                if (languageByPid.TryGetValue(order[i], out var lang) && !string.Equals(stream.Language, lang, StringComparison.OrdinalIgnoreCase))
                {
                    stream.Language = lang;
                    applied++;
                }
                if (!inPlaylist.Contains(order[i]) && string.IsNullOrEmpty(stream.Title))
                {
                    stream.Title = Plugin.Text("Not in main playlist", "Hors playlist principale");
                }
            }

            bool dolbyVision = MergeDolbyVisionLayer(streams, embedded, order);
            int cores = RemoveTrueHdCores(streams, embedded, order);

            if (applied > 0 || dolbyVision || cores > 0)
            {
                _itemRepository.SaveMediaStreams(video.InternalId, streams, CancellationToken.None);
                _logger.Info("ISO Blu-ray: {0}, {1} language(s) set from {2}{3}{4}",
                    Path.GetFileName(video.Path), applied, analysis.Disc.MainPlaylist.Name,
                    dolbyVision ? ", Dolby Vision layer merged" : "",
                    cores > 0 ? ", " + cores + " TrueHD AC3 core(s) hidden" : "");
            }
        }

        private void FillFromPlaylist(Video video, List<MediaStream> streams, IsoAnalysis analysis)
        {
            var playlist = analysis.Disc.MainPlaylist;
            int index = streams.Count == 0 ? 0 : streams.Max(s => s.Index) + 1;
            bool firstVideo = true;
            foreach (var s in playlist.Streams)
            {
                var ms = new MediaStream
                {
                    Index = index++,
                    Codec = s.Codec,
                    Language = string.IsNullOrEmpty(s.Language) ? null : s.Language,
                    Type = s.Kind == BlurayStreamKind.Video ? MediaStreamType.Video
                         : s.Kind == BlurayStreamKind.Audio ? MediaStreamType.Audio
                         : MediaStreamType.Subtitle,
                    Comment = Plugin.Text("Described from the Blu-ray playlist (ffprobe unavailable)", "Décrit d'après la playlist Blu-ray (ffprobe indisponible)")
                };
                if (s.Kind == BlurayStreamKind.Video)
                {
                    if (!firstVideo) continue; // Dolby Vision layer or secondary video
                    firstVideo = false;
                    if (s.Height > 0)
                    {
                        ms.Height = s.Height;
                        ms.Width = s.Height == 2160 ? 3840 : s.Height == 1080 ? 1920 : s.Height == 720 ? 1280 : 720;
                    }
                    if (s.FrameRate > 0) { ms.RealFrameRate = s.FrameRate; ms.AverageFrameRate = s.FrameRate; }
                }
                else if (s.Kind == BlurayStreamKind.Audio)
                {
                    if (s.Channels > 0)
                    {
                        ms.Channels = s.Channels;
                        ms.ChannelLayout = s.Channels == 1 ? "mono" : s.Channels == 2 ? "stereo" : "5.1";
                    }
                    if (s.SampleRate > 0) ms.SampleRate = s.SampleRate;
                }
                streams.Add(ms);
            }
            _itemRepository.SaveMediaStreams(video.InternalId, streams, CancellationToken.None);

            var main = streams.FirstOrDefault(x => x.Type == MediaStreamType.Video);
            if (video.Height <= 0 && main?.Height != null)
            {
                video.Height = main.Height.Value;
                video.Width = main.Width ?? 0;
                video.UpdateToRepository(ItemUpdateType.MetadataImport);
            }
            _logger.Info("ISO Blu-ray: {0}, no ffprobe result, {1} track(s) described from {2}",
                Path.GetFileName(video.Path), streams.Count(x => !x.IsExternal), playlist.Name);
        }

        /// <summary>
        /// ffprobe exposes the AC3 "core" of a Blu-ray TrueHD track as a separate track with the same
        /// PID. It is not a disc track (the player does not count it): remove it so that Emby's list
        /// matches the Blu-ray audio tracks.
        /// </summary>
        private static int RemoveTrueHdCores(List<MediaStream> all, List<MediaStream> embedded, List<int> order)
        {
            int removed = 0;
            for (int i = 1; i < embedded.Count; i++)
            {
                var previous = embedded[i - 1];
                var current = embedded[i];
                if (order[i] == order[i - 1]
                    && previous.Type == MediaStreamType.Audio && current.Type == MediaStreamType.Audio
                    && string.Equals(previous.Codec, "truehd", StringComparison.OrdinalIgnoreCase))
                {
                    all.Remove(current);
                    removed++;
                }
            }
            return removed;
        }

        /// <summary>
        /// On a Dolby Vision UHD Blu-ray (profile 7), the enhancement layer is a second 1080p HEVC
        /// stream (PID 0x1015). It is not a track to offer: remove it from the list and mark the main
        /// video as "Dolby Vision, profile 7.6". The indexes of the other tracks do not change (ffmpeg
        /// uses them).
        /// </summary>
        private static bool MergeDolbyVisionLayer(List<MediaStream> all, List<MediaStream> embedded, List<int> order)
        {
            const int DolbyVisionEnhancementPid = 0x1015;
            var videos = embedded.Select((s, i) => (Stream: s, Pid: order[i]))
                                 .Where(x => x.Stream.Type == MediaStreamType.Video)
                                 .ToList();
            if (videos.Count < 2) return false;

            var main = videos[0].Stream;
            var layers = videos.Skip(1)
                .Where(x => x.Pid == DolbyVisionEnhancementPid
                            || (string.Equals(x.Stream.Codec, "hevc", StringComparison.OrdinalIgnoreCase)
                                && string.Equals(main.Codec, "hevc", StringComparison.OrdinalIgnoreCase)
                                && (x.Stream.Height ?? 0) * 2 == (main.Height ?? 0)))
                .Select(x => x.Stream)
                .ToList();
            if (layers.Count == 0) return false;

            foreach (var layer in layers) all.Remove(layer);
            main.ExtendedVideoType = ExtendedVideoTypes.DolbyVision;
            main.ExtendedVideoSubType = ExtendedVideoSubTypes.DoviProfile76;
            return true;
        }

        public void Dispose()
        {
            _libraryManager.ItemUpdated -= OnItemChanged;
            _libraryManager.ItemAdded -= OnItemChanged;
        }
    }
}
