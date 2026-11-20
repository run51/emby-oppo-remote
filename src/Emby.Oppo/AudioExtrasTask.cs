using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;

namespace Emby.Oppo
{
    /// <summary>
    /// Looks for Dolby Atmos / DTS:X in every title that has a TrueHD or DTS track, so that the library's audio
    /// filter knows them all (a title page analyses its own title on demand). Already analysed files are skipped.
    /// </summary>
    public sealed class AudioExtrasTask : IScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _items;
        private readonly IJsonSerializer _json;
        private readonly ILogger _logger;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly IApplicationPaths _paths;

        public AudioExtrasTask(ILibraryManager libraryManager, IItemRepository items, IJsonSerializer json, ILogManager logManager,
            IMediaEncoder mediaEncoder, IApplicationPaths paths)
        {
            _libraryManager = libraryManager;
            _items = items;
            _json = json;
            _logger = logManager.GetLogger("Oppo");
            _mediaEncoder = mediaEncoder;
            _paths = paths;
        }

        public string Name => "OPPO: Dolby Atmos / DTS:X";
        public string Key => "OppoAudioExtras";
        public string Description => "Finds the Dolby Atmos and DTS:X tracks of the library (read from the audio itself), for the badges and the audio filter of the web remote.";
        public string Category => "OPPO";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
        {
            new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerDaily, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks }
        };

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
#pragma warning disable CS0618
            var extras = new AudioExtras(_items, _json, _logger, _mediaEncoder.EncoderPath, _paths.CachePath);
#pragma warning restore CS0618
            var videos = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { "Movie", "MusicVideo" }, Recursive = true })
                .OfType<Video>().Where(v => !string.IsNullOrEmpty(v.Path)).ToList();
            int done = 0, analysed = 0, atmos = 0, dtsx = 0;
            foreach (var video in videos)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var audio = _items.GetMediaStreams(new MediaStreamQuery { ItemId = video.InternalId }, cancellationToken)
                    .Where(s => s.Type == MediaStreamType.Audio).ToList();
                if (audio.Any(s => string.Equals(s.Codec, "truehd", StringComparison.OrdinalIgnoreCase)
                                   || (s.Codec ?? "").StartsWith("dts", StringComparison.OrdinalIgnoreCase)))
                {
                    var result = await extras.Get(video, cancellationToken).ConfigureAwait(false);
                    analysed++;
                    if (result.Any(r => r.Atmos)) atmos++;
                    if (result.Any(r => r.DtsX)) dtsx++;
                }
                progress.Report(100.0 * ++done / Math.Max(1, videos.Count));
            }
            _logger.Info("OPPO: Atmos / DTS:X analysis finished: {0} title(s) with TrueHD or DTS, {1} with Atmos, {2} with DTS:X", analysed, atmos, dtsx);
        }
    }
}
