using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Emby.Oppo
{
    /// <summary>Maps a folder as seen by Emby to a network share mounted by the OPPO.</summary>
    public sealed class PathMapping
    {
        /// <summary>Path prefix as Emby sees it (e.g. "/volume1/Movies").</summary>
        public string EmbyPath { get; set; }

        /// <summary>"nfs" or "smb".</summary>
        public string Protocol { get; set; } = "nfs";

        /// <summary>Address of the file server as seen by the OPPO (e.g. "192.168.1.10").</summary>
        public string Server { get; set; }

        /// <summary>
        /// NFS: exported path (e.g. "volume1/Movies"). SMB: share name, optionally followed by
        /// a subfolder (e.g. "Movies" or "Movies/HD").
        /// </summary>
        public string Share { get; set; }

        /// <summary>SMB only: user name (empty = guest).</summary>
        public string Username { get; set; }

        /// <summary>SMB only: password. Never written to the log.</summary>
        public string Password { get; set; }
    }

    public sealed class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>IP address of the OPPO (UDP-203/205) or of a compatible player.</summary>
        public string OppoAddress { get; set; } = "";

        /// <summary>
        /// Optional "host[:port]" for the IP control protocol (port 23), e.g. an oppo-multiplexer shared with
        /// another controller: the player accepts a single IP control connection. Empty: the player itself,
        /// port 23. The HTTP interface (port 436) always goes to <see cref="OppoAddress"/>.
        /// </summary>
        public string IpControlAddress { get; set; } = "";

        /// <summary>Name shown in Emby's "Play on…" menu.</summary>
        public string DeviceName { get; set; } = "OPPO";

        /// <summary>Emby path → network share mappings.</summary>
        public PathMapping[] Mappings { get; set; } = Array.Empty<PathMapping>();

        /// <summary>URL of a webhook called at the start and end of each movie (empty = disabled). JSON body: {event, title}.</summary>
        public string WebhookUrl { get; set; } = "";

        /// <summary>
        /// Optional: Unfolded Circle remote (Remote 2/3) that turns the TV on over the network when a movie
        /// starts and on the "Showtime" (Seance route) button (useful when the server or home automation is not on the
        /// same network as the TV). Address (e.g. 192.168.1.30), API key, the TV's "media_player" entity in the remote.
        /// </summary>
        public string RemoteAddress { get; set; } = "";
        public string RemoteApiKey { get; set; } = "";
        public string RemoteTvEntity { get; set; } = "";

        /// <summary>
        /// Optional: Home Assistant (address such as http://homeassistant:8123, long-lived access token) and the
        /// amplifier's media_player entity, for the volume and mute buttons of the remote.
        /// </summary>
        public string HomeAssistantUrl { get; set; } = "";
        public string HomeAssistantToken { get; set; } = "";
        public string AmplifierEntity { get; set; } = "";

        /// <summary>Maximum time for playback to start on the OPPO (mount included).</summary>
        public int StartTimeoutSeconds { get; set; } = 60;

        // ---- Legacy settings (v0.1.x), migrated automatically to Mappings / WebhookUrl ----
        public string NfsServer { get; set; }
        public string NfsShares { get; set; }
        public string HomeAssistantWebhook { get; set; }

        /// <summary>Imports settings from the first versions. Returns true if anything changed.</summary>
        internal bool MigrateLegacy()
        {
            bool changed = false;
            if ((Mappings == null || Mappings.Length == 0) && !string.IsNullOrWhiteSpace(NfsShares) && !string.IsNullOrWhiteSpace(NfsServer))
            {
                Mappings = NfsShares.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim().TrimEnd('/'))
                    .Where(s => s.Length > 1)
                    .Select(s => new PathMapping { EmbyPath = s, Protocol = "nfs", Server = NfsServer, Share = s.TrimStart('/') })
                    .ToArray();
                changed = true;
            }
            if (string.IsNullOrWhiteSpace(WebhookUrl) && !string.IsNullOrWhiteSpace(HomeAssistantWebhook))
            {
                WebhookUrl = HomeAssistantWebhook;
                changed = true;
            }
            if (changed) { NfsShares = null; NfsServer = null; HomeAssistantWebhook = null; }
            return changed;
        }
    }

    public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public static Plugin Instance { get; private set; }

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            // No access to Configuration here: Emby has not yet provided the settings file.
            // Legacy settings are migrated when the service starts (OppoEntryPoint.Run).
        }

        public override string Name => "OPPO";

        public override string Description =>
            "Plays movies on an OPPO UDP-203/205 player from Emby's \"Play on…\" menu (NFS/SMB, playback tracking, tracks, webhook).";

        public override Guid Id { get; } = new Guid("8b1f4e62-6c3d-4f0a-a7e2-91d5c03b7f48");

        public IEnumerable<PluginPageInfo> GetPages() => new[]
        {
            new PluginPageInfo
            {
                Name = "oppo",
                DisplayName = "OPPO",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.oppo.html",
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "album",
                IsMainConfigPage = true
            },
            new PluginPageInfo
            {
                Name = "oppojs",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.oppo.js",
                IsMainConfigPage = false
            }
        };
    }
}
