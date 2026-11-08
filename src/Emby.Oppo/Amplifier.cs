using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace Emby.Oppo
{
    /// <summary>Amplifier state shown by the remote (volume and mute).</summary>
    public sealed class AmpState
    {
        /// <summary>False if Home Assistant could not be reached or the entity is unknown.</summary>
        public bool Available { get; set; }
        public bool On { get; set; }
        /// <summary>Volume from 0 to 1 (Home Assistant's volume_level), or null.</summary>
        public double? Volume { get; set; }
        /// <summary>Volume in dB when the integration reports it (volume_db attribute, e.g. Denon), or null.</summary>
        public double? VolumeDb { get; set; }
        public bool Muted { get; set; }
        public string Name { get; set; }
    }

    /// <summary>
    /// Amplifier volume and mute through the Home Assistant REST API: reads the state of a media_player
    /// entity and calls media_player.volume_up / volume_down / volume_mute / volume_set. Optional: only
    /// active when the Home Assistant address, a long-lived access token and the entity are set.
    /// </summary>
    internal static class Amplifier
    {
        private static readonly object Gate = new object();
        private static DateTime _cachedAt = DateTime.MinValue;
        private static AmpState _cached;

        public static bool Configured
        {
            get
            {
                var c = Plugin.Instance.Configuration;
                return !string.IsNullOrWhiteSpace(c.HomeAssistantUrl) && !string.IsNullOrWhiteSpace(c.HomeAssistantToken)
                    && !string.IsNullOrWhiteSpace(c.AmplifierEntity);
            }
        }

        /// <summary>Current state, cached for 2 s (the remote polls often); a failure is cached for 20 s.</summary>
        public static async Task<AmpState> GetState(IJsonSerializer json, ILogger logger, bool fresh = false)
        {
            if (!Configured) return null;
            lock (Gate)
            {
                var age = DateTime.UtcNow - _cachedAt;
                if (!fresh && _cached != null && age < TimeSpan.FromSeconds(_cached.Available ? 2 : 20)) return _cached;
            }
            var state = new AmpState();
            try
            {
                var c = Plugin.Instance.Configuration;
                using (var response = await Send("GET", "/api/states/" + Uri.EscapeDataString(c.AmplifierEntity.Trim()), null).ConfigureAwait(false))
                using (var stream = response.GetResponseStream())
                {
                    var doc = json.DeserializeFromStream<HaState>(stream);
                    string s = doc?.state;
                    state.Available = !string.IsNullOrEmpty(s) && s != "unavailable" && s != "unknown";
                    state.On = state.Available && s != "off" && s != "standby";
                    var a = doc?.attributes;
                    if (a != null)
                    {
                        state.Volume = a.volume_level;
                        state.VolumeDb = a.volume_db;
                        state.Muted = a.is_volume_muted ?? false;
                        state.Name = a.friendly_name;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Info("OPPO: amplifier state unavailable from Home Assistant ({0})", ex.Message);
                state.Available = false;
            }
            lock (Gate) { _cached = state; _cachedAt = DateTime.UtcNow; }
            return state;
        }

        /// <summary>Volume up / down (steps times), mute toggle or absolute level (0–1). Returns false if Home Assistant refused or is unreachable.</summary>
        public static async Task<bool> Act(string action, double? level, int steps, IJsonSerializer json, ILogger logger)
        {
            if (!Configured) return false;
            var c = Plugin.Instance.Configuration;
            string entity = c.AmplifierEntity.Trim();
            string service;
            var data = new Dictionary<string, object> { ["entity_id"] = entity };
            switch ((action ?? "").ToLowerInvariant())
            {
                case "up": service = "volume_up"; break;
                case "down": service = "volume_down"; break;
                case "mute":
                    var current = await GetState(json, logger, true).ConfigureAwait(false);
                    service = "volume_mute";
                    data["is_volume_muted"] = !(current?.Muted ?? false);
                    break;
                case "set":
                    if (level == null) return false;
                    service = "volume_set";
                    data["volume_level"] = Math.Max(0, Math.Min(1, level.Value));
                    break;
                default: return false;
            }
            try
            {
                int count = service == "volume_up" || service == "volume_down" ? steps : 1;
                for (int i = 0; i < count; i++)
                    using (await Send("POST", "/api/services/media_player/" + service, json.SerializeToString(data)).ConfigureAwait(false)) { }
                lock (Gate) _cachedAt = DateTime.MinValue;
                return true;
            }
            catch (Exception ex)
            {
                logger.Info("OPPO: amplifier command {0} failed ({1})", service, ex.Message);
                return false;
            }
        }

        private static async Task<WebResponse> Send(string method, string path, string body)
        {
            var c = Plugin.Instance.Configuration;
            string host = c.HomeAssistantUrl.Trim().TrimEnd('/');
            if (!host.StartsWith("http", StringComparison.OrdinalIgnoreCase)) host = "http://" + host;
            var request = (HttpWebRequest)WebRequest.Create(host + path);
            request.Method = method;
            request.Timeout = 4000;
            request.Headers["Authorization"] = "Bearer " + c.HomeAssistantToken.Trim();
            if (body != null)
            {
                request.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(body);
                using (var s = await request.GetRequestStreamAsync().ConfigureAwait(false))
                    await s.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            }
            return await request.GetResponseAsync().ConfigureAwait(false);
        }

#pragma warning disable IDE1006 // JSON names of the Home Assistant REST API
        private sealed class HaState
        {
            public string state { get; set; }
            public HaAttributes attributes { get; set; }
        }

        private sealed class HaAttributes
        {
            public double? volume_level { get; set; }
            public double? volume_db { get; set; }
            public bool? is_volume_muted { get; set; }
            public string friendly_name { get; set; }
        }
#pragma warning restore IDE1006
    }
}
