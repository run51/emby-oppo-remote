using System;
using System.Globalization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Emby.IsoBluray
{
    public sealed class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Language (ISO 639-2, e.g. "eng", "fra") preferred when picking the main playlist of a disc
        /// that offers several of the same length (one per region). Empty: derived from Emby's
        /// preferred metadata language.
        /// </summary>
        public string PreferredLanguage { get; set; } = "";
    }

    public sealed class Plugin : BasePlugin<PluginConfiguration>
    {
        private static IServerConfigurationManager _serverConfiguration;

        public static Plugin Instance { get; private set; }

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, IServerConfigurationManager serverConfiguration)
            : base(applicationPaths, xmlSerializer)
        {
            _serverConfiguration = serverConfiguration;
            Instance = this;
        }

        public override string Name => "ISO Blu-ray";

        public override string Description => Text(
            "Analyses Blu-ray ISOs (UDF 2.50): audio/video characteristics, languages and track selection.",
            "Analyse les ISO Blu-ray (UDF 2.50) : caractéristiques audio/vidéo, langues et choix des pistes.");

        public override Guid Id { get; } = new Guid("5d0e1c8a-3f4b-4a8e-9c61-2b7f0a9e4c13");

        /// <summary>True when Emby's server UI language is French.</summary>
        internal static bool IsFrench
        {
            get
            {
                string culture = _serverConfiguration?.Configuration?.UICulture;
                return culture != null && culture.StartsWith("fr", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>Picks the English or French text depending on Emby's server UI language.</summary>
        internal static string Text(string english, string french) => IsFrench ? french : english;

        /// <summary>
        /// Preferred disc language (ISO 639-2): the plugin setting if set, otherwise derived from
        /// Emby's preferred metadata language ("fr" gives "fra"). Empty if none is known.
        /// </summary>
        internal static string PreferredDiscLanguage()
        {
            string configured = Instance?.Configuration?.PreferredLanguage;
            if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
            string metadata = _serverConfiguration?.Configuration?.PreferredMetadataLanguage;
            if (string.IsNullOrWhiteSpace(metadata)) return "";
            try
            {
                string code = new CultureInfo(metadata.Trim()).ThreeLetterISOLanguageName;
                return code == "ivl" ? "" : code;   // "ivl": invariant culture, language unknown
            }
            catch (CultureNotFoundException)
            {
                return "";
            }
        }
    }
}
