using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;

namespace Emby.Oppo
{
    [Route("/Oppo/Update", "GET", Summary = "Installed versions and the latest release on GitHub")]
    public sealed class CheckUpdate : IReturn<UpdateInfo>
    {
    }

    [Route("/Oppo/Update", "POST", Summary = "Downloads the latest release's plugins into the plugins folder (restart Emby afterwards)")]
    public sealed class InstallUpdate : IReturn<UpdateInfo>
    {
    }

    public sealed class UpdateInfo
    {
        public string Current { get; set; }
        public string IsoBlurayCurrent { get; set; }
        public string Latest { get; set; }
        public bool Available { get; set; }
        public string ReleaseUrl { get; set; }
        public string Notes { get; set; }
        public bool Installed { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Updates both plugins from the GitHub releases (Emby has no third-party catalog): the latest release's
    /// Emby.Oppo.dll and Emby.IsoBluray.dll are downloaded, checked (assembly name and version) and copied over the
    /// installed ones; the previous files are kept in the cache folder. Emby must then be restarted.
    /// Administrators only.
    /// </summary>
    [Authenticated(Roles = "Admin")]
    public sealed class UpdateService : IService, IRequiresRequest
    {
        private const string Repository = "run51/emby-oppo-remote";
        private static readonly string[] Assets = { "Emby.Oppo.dll", "Emby.IsoBluray.dll" };
        private static (DateTime At, GithubRelease Release) _cache;

        private readonly IJsonSerializer _json;
        private readonly ILogger _logger;
        private readonly IApplicationPaths _paths;

        public UpdateService(IJsonSerializer json, ILogManager logManager, IApplicationPaths paths)
        {
            _json = json;
            _logger = logManager.GetLogger("Oppo");
            _paths = paths;
        }

        public IRequest Request { get; set; }

        private bool Fr => L10n.IsFrench(Request);

        public sealed class GithubRelease
        {
            public string tag_name { get; set; }
            public string html_url { get; set; }
            public string body { get; set; }
            public bool prerelease { get; set; }
            public List<GithubAsset> assets { get; set; }
        }

        public sealed class GithubAsset
        {
            public string name { get; set; }
            public string browser_download_url { get; set; }
        }

        public async Task<object> Get(CheckUpdate request)
        {
            var info = Installed();
            try
            {
                var release = await Latest(false).ConfigureAwait(false);
                Fill(info, release);
            }
            catch (Exception ex)
            {
                info.Message = L10n.T(Fr, "GitHub could not be reached: ", "GitHub injoignable : ") + ex.Message;
            }
            return info;
        }

        public async Task<object> Post(InstallUpdate request)
        {
            var info = Installed();
            var release = await Latest(true).ConfigureAwait(false);
            Fill(info, release);
            if (!info.Available)
            {
                info.Message = L10n.T(Fr, "Already up to date.", "Déjà à jour.");
                return info;
            }
            var latest = ParseVersion(release.tag_name);
            string pluginsDir = _paths.PluginsPath;
            string backupDir = Path.Combine(_paths.CachePath, "oppo-update", info.Current);
            Directory.CreateDirectory(backupDir);

            // Download and check everything first, then replace: never leave one plugin updated and not the other.
            var downloaded = new List<(string Temp, string Target)>();
            try
            {
                foreach (var name in Assets)
                {
                    var asset = release.assets?.FirstOrDefault(a => string.Equals(a.name, name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException(name + L10n.T(Fr, " is missing from the release", " absent de la version"));
                    string temp = Path.Combine(backupDir, name + ".download");
                    using (var client = new WebClient())
                    {
                        client.Headers[HttpRequestHeader.UserAgent] = "Emby.Oppo-updater";
                        await client.DownloadFileTaskAsync(asset.browser_download_url, temp).ConfigureAwait(false);
                    }
                    var assembly = AssemblyName.GetAssemblyName(temp);
                    if (!string.Equals(assembly.Name, Path.GetFileNameWithoutExtension(name), StringComparison.Ordinal))
                        throw new InvalidDataException(name + L10n.T(Fr, ": unexpected assembly ", " : assemblage inattendu ") + assembly.Name);
                    if (name == "Emby.Oppo.dll" && latest != null && assembly.Version.ToString(3) != latest.ToString(3))
                        throw new InvalidDataException(name + L10n.T(Fr, ": version ", " : version ") + assembly.Version.ToString(3) + " ≠ " + latest.ToString(3));
                    downloaded.Add((temp, Path.Combine(pluginsDir, name)));
                }
                foreach (var (temp, target) in downloaded)
                {
                    if (File.Exists(target)) File.Copy(target, Path.Combine(backupDir, Path.GetFileName(target)), true);
                    File.Copy(temp, target, true);
                    File.Delete(temp);
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("OPPO: update to " + release.tag_name + " failed", ex);
                foreach (var (temp, _) in downloaded) { try { File.Delete(temp); } catch { } }
                info.Message = L10n.T(Fr, "Update failed: ", "Échec de la mise à jour : ") + ex.Message
                    + (ex is UnauthorizedAccessException || ex is IOException
                        ? L10n.T(Fr, " (the plugins folder cannot be written: copy the files by hand)", " (dossier des plugins non modifiable : copiez les fichiers à la main)") : "");
                return info;
            }
            _logger.Info("OPPO: plugins updated from {0} to {1} (previous files in {2}); restart Emby", info.Current, release.tag_name, backupDir);
            info.Installed = true;
            info.Message = L10n.T(Fr, "Version " + release.tag_name.TrimStart('v') + " installed. Restart Emby to use it.",
                "Version " + release.tag_name.TrimStart('v') + " installée. Redémarrez Emby pour l'utiliser.");
            return info;
        }

        private static UpdateInfo Installed()
        {
            var iso = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Emby.IsoBluray");
            return new UpdateInfo
            {
                Current = typeof(Plugin).Assembly.GetName().Version.ToString(3),
                IsoBlurayCurrent = iso?.GetName().Version.ToString(3)
            };
        }

        private static void Fill(UpdateInfo info, GithubRelease release)
        {
            var latest = ParseVersion(release?.tag_name);
            var current = ParseVersion(info.Current);
            info.Latest = latest?.ToString(3);
            info.ReleaseUrl = release?.html_url;
            info.Notes = release?.body;
            info.Available = latest != null && current != null && latest > current;
        }

        private async Task<GithubRelease> Latest(bool fresh)
        {
            if (!fresh && _cache.Release != null && DateTime.UtcNow - _cache.At < TimeSpan.FromMinutes(30)) return _cache.Release;
            var request = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + Repository + "/releases/latest");
            request.UserAgent = "Emby.Oppo-updater";
            request.Accept = "application/vnd.github+json";
            request.Timeout = 15000;
            using (var response = await request.GetResponseAsync().ConfigureAwait(false))
            using (var stream = response.GetResponseStream())
            {
                var release = _json.DeserializeFromStream<GithubRelease>(stream);
                _cache = (DateTime.UtcNow, release);
                return release;
            }
        }

        private static Version ParseVersion(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string v = text.Trim().TrimStart('v', 'V');
            int dash = v.IndexOf('-');
            if (dash >= 0) v = v.Substring(0, dash);
            return Version.TryParse(v, out var parsed) ? new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build)) : null;
        }
    }
}
