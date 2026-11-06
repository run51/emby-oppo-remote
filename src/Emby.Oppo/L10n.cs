using System;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Services;

namespace Emby.Oppo
{
    /// <summary>
    /// Minimal localisation: English by default, French on request. Strings are written inline at the
    /// call site as <c>L10n.T(fr, "English", "Français")</c>.
    /// </summary>
    internal static class L10n
    {
        /// <summary>Set by <see cref="OppoEntryPoint"/>; used when there is no HTTP request to look at.</summary>
        internal static IServerConfigurationManager ServerConfig { get; set; }

        /// <summary>
        /// Server-wide default for contexts without a request (session controller, background work):
        /// French if Emby's UI culture (ServerConfiguration.UICulture) starts with "fr"; if that setting
        /// is empty, the preferred metadata language (ServerConfiguration.PreferredMetadataLanguage) decides.
        /// </summary>
        public static bool ServerIsFrench
        {
            get
            {
                try
                {
                    var c = ServerConfig?.Configuration;
                    if (c == null) return false;
                    return !string.IsNullOrWhiteSpace(c.UICulture) ? StartsWithFr(c.UICulture) : StartsWithFr(c.PreferredMetadataLanguage);
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// True if the first language tag of the request's Accept-Language header starts with "fr"
        /// (the web remote sends "en" or "fr"). Without a header, falls back to <see cref="ServerIsFrench"/>.
        /// </summary>
        public static bool IsFrench(IRequest request)
        {
            string header = null;
            try
            {
                var headers = request?.Headers;
                if (headers != null)
                {
                    header = headers.Get("Accept-Language");
                    if (header == null)
                        foreach (var key in headers.Keys)
                            if (string.Equals(key, "Accept-Language", StringComparison.OrdinalIgnoreCase)) { header = headers.Get(key); break; }
                }
            }
            catch (Exception)
            {
                header = null;
            }
            if (string.IsNullOrWhiteSpace(header)) return ServerIsFrench;
            string first = header.Split(',')[0].Split(';')[0].Trim();
            return StartsWithFr(first);
        }

        /// <summary>Picks the French or the English text.</summary>
        public static string T(bool fr, string en, string frText) => fr ? frText : en;

        private static bool StartsWithFr(string tag) =>
            !string.IsNullOrEmpty(tag) && tag.Trim().StartsWith("fr", StringComparison.OrdinalIgnoreCase);
    
        /// <summary>
        /// Three-letter code of Emby's preferred metadata language ("fr" gives "fra"), used as a tie-break hint
        /// when picking a Blu-ray main playlist. Empty when unknown.
        /// </summary>
        internal static string DiscLanguage()
        {
            try
            {
                string lang = ServerConfig?.Configuration?.PreferredMetadataLanguage;
                if (string.IsNullOrWhiteSpace(lang)) return "";
                string code = new System.Globalization.CultureInfo(lang.Trim()).ThreeLetterISOLanguageName;
                return code == "ivl" ? "" : code;
            }
            catch (Exception) { return ""; }
        }
}
}
