using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.IsoBluray.Bluray;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.IsoBluray
{
    /// <summary>
    /// "Blu-ray: tracks and runtimes" scheduled task: goes over every movie already in the library
    /// that is stored as a Blu-ray folder to set track languages, hide the AC3 core of TrueHD tracks
    /// and merge the Dolby Vision layer; also fixes absurd runtimes of ISOs and folders. No default
    /// trigger: run it from Scheduled Tasks.
    /// </summary>
    public sealed class FolderBackfillTask : IScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;

        public FolderBackfillTask(ILibraryManager libraryManager, ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _logger = logManager.GetLogger("IsoBluray");
        }

        public string Name => Plugin.Text("Blu-ray: tracks and runtimes", "Blu-ray : pistes et durées");
        public string Key => "IsoBlurayFolderBackfill";
        public string Description => Plugin.Text(
            "Blu-ray folders: sets track languages and removes duplicate tracks (AC3 core of TrueHD, Dolby Vision layer). ISOs and folders: fixes absurd runtimes using the disc playlist.",
            "Dossiers Blu-ray : pose les langues des pistes et retire les pistes en double (cœur AC3 des TrueHD, couche Dolby Vision). ISO et dossiers : corrige les durées aberrantes d'après la playlist du disque.");
        public string Category => "ISO Blu-ray";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var applier = LanguageApplier.Current;
            if (applier == null) return Task.CompletedTask;

            var videos = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { "Movie", "MusicVideo" }, Recursive = true })
                                        .OfType<Video>()
                                        .Where(v => !string.IsNullOrEmpty(v.Path))
                                        .ToList();
            int done = 0, folders = 0, isos = 0, runtimes = 0, errors = 0;
            foreach (var video in videos)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    bool iso = video.Path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase);
                    if (iso) isos++;
                    else if (applier.FixFolder(video, onlyIfNeeded: true)) folders++;
                    if ((iso || DiscInfo.FindBdmvFolder(video.Path) != null) && applier.FixRuntimeFromDisc(video)) runtimes++;
                }
                catch (Exception ex)
                {
                    errors++;
                    _logger.Info("ISO Blu-ray: {0} not processed ({1})", video.Path, ex.Message);
                }
                progress.Report(100.0 * ++done / Math.Max(1, videos.Count));
            }
            _logger.Info("ISO Blu-ray: backfill finished, {0} Blu-ray folder(s) and {1} ISO(s) seen, {2} runtime(s) fixed, {3} error(s)",
                folders, isos, runtimes, errors);
            return Task.CompletedTask;
        }
    }
}
