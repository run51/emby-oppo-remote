using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace Emby.Oppo
{
    /// <summary>A trailer for the remote's film page: a YouTube address, its title and its language (VF, VOSTFR).</summary>
    public sealed class TrailerEntry
    {
        public string Url { get; set; }
        public string Name { get; set; }
        public string Lang { get; set; }
    }

    /// <summary>
    /// French trailers of a title, asked from TheMovieDb with the user's own API key (settings page) and the title's
    /// TheMovieDb id that Emby already has. Kept 24 h per title (a failure 10 min). Without a key or an id: none.
    /// </summary>
    internal static class FrenchTrailers
    {
        private static readonly ConcurrentDictionary<string, (DateTime Until, TrailerEntry[] List)> Cache =
            new ConcurrentDictionary<string, (DateTime, TrailerEntry[])>();
        private static string _lastError;

        // TheMovieDb's answer (lower-case names, as in its JSON)
        public sealed class Videos { public Video[] results { get; set; } }
        public sealed class Video
        {
            public string key { get; set; }
            public string name { get; set; }
            public string site { get; set; }
            public string type { get; set; }
            public string iso_639_1 { get; set; }
            public bool official { get; set; }
        }

        public static async Task<TrailerEntry[]> Get(BaseItem item, IJsonSerializer json, ILogger logger)
        {
            string key = (Plugin.Instance.Configuration.TmdbApiKey ?? "").Trim();
            if (key.Length == 0 || item == null) return Array.Empty<TrailerEntry>();
            string kind = item is Movie ? "movie" : item is Series ? "tv" : item is Episode ep && ep.Series != null ? "tv" : null;
            var source = item is Episode e2 ? (BaseItem)e2.Series : item;
            string id = source?.ProviderIds != null && source.ProviderIds.TryGetValue("Tmdb", out var v) ? v : null;
            if (kind == null || string.IsNullOrWhiteSpace(id)) return Array.Empty<TrailerEntry>();
            string cacheKey = kind + "/" + id;
            if (Cache.TryGetValue(cacheKey, out var known) && known.Until > DateTime.UtcNow) return known.List;
            TrailerEntry[] list;
            try
            {
                bool bearer = key.StartsWith("eyJ", StringComparison.Ordinal);   // a v4 read access token, else a v3 key
                string url = "https://api.themoviedb.org/3/" + kind + "/" + Uri.EscapeDataString(id) + "/videos?language=fr-FR"
                             + (bearer ? "" : "&api_key=" + Uri.EscapeDataString(key));
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Timeout = 8000;
                request.Accept = "application/json";
                if (bearer) request.Headers["Authorization"] = "Bearer " + key;
                string text;
                using (var response = await request.GetResponseAsync().ConfigureAwait(false))
                using (var reader = new StreamReader(response.GetResponseStream()))
                    text = await reader.ReadToEndAsync().ConfigureAwait(false);
                var videos = json.DeserializeFromString<Videos>(text)?.results ?? Array.Empty<Video>();
                list = videos
                    .Where(x => x != null && string.Equals(x.site, "YouTube", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(x.key)
                                && (x.iso_639_1 == null || x.iso_639_1 == "fr")
                                && (x.type == "Trailer" || x.type == "Teaser"))
                    .OrderBy(x => x.type == "Trailer" ? 0 : 1).ThenBy(x => x.official ? 0 : 1)
                    .Select(x => new TrailerEntry
                    {
                        Url = "https://www.youtube.com/watch?v=" + x.key,
                        Name = x.name,
                        Lang = (x.name ?? "").IndexOf("vost", StringComparison.OrdinalIgnoreCase) >= 0 ? "VOSTFR" : "VF"
                    })
                    .ToArray();
                Cache[cacheKey] = (DateTime.UtcNow.AddHours(24), list);
                _lastError = null;
            }
            catch (Exception ex)
            {
                // A bad key gives 401 every time: said once in the log, not for every film page.
                string message = ex is WebException we && we.Response is HttpWebResponse r ? "HTTP " + (int)r.StatusCode : ex.Message;
                if (message != _lastError) logger.Info("OPPO: TheMovieDb did not give the French trailers ({0})", message);
                _lastError = message;
                list = Array.Empty<TrailerEntry>();
                Cache[cacheKey] = (DateTime.UtcNow.AddMinutes(10), list);
            }
            return list;
        }
    }
}
