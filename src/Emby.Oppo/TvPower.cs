using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace Emby.Oppo
{
    /// <summary>
    /// Turns the TV on through an Unfolded Circle remote (Remote 2/3 REST API): a single
    /// "media_player.on" command on the TV entity, without starting an activity (an activity also replays
    /// its toggle keys, which may turn other devices off).
    /// </summary>
    internal static class TvPower
    {
        public static async Task<bool> TurnOn(ILogger logger)
        {
            var c = Plugin.Instance.Configuration;
            if (string.IsNullOrWhiteSpace(c.RemoteAddress) || string.IsNullOrWhiteSpace(c.RemoteApiKey) || string.IsNullOrWhiteSpace(c.RemoteTvEntity))
                return false;
            try
            {
                string host = c.RemoteAddress.Trim();
                if (!host.StartsWith("http", StringComparison.OrdinalIgnoreCase)) host = "http://" + host;
                var request = (HttpWebRequest)WebRequest.Create(host.TrimEnd('/') + "/api/entities/" + Uri.EscapeDataString(c.RemoteTvEntity.Trim()) + "/command");
                request.Method = "PUT";
                request.ContentType = "application/json";
                request.Timeout = 8000;
                request.Headers["Authorization"] = "Bearer " + c.RemoteApiKey.Trim();
                var body = Encoding.UTF8.GetBytes("{\"cmd_id\":\"media_player.on\"}");
                using (var s = await request.GetRequestStreamAsync().ConfigureAwait(false))
                    await s.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
                using (await request.GetResponseAsync().ConfigureAwait(false)) { }
                logger.Info("OPPO: TV turned on by the remote ({0})", c.RemoteAddress);
                return true;
            }
            catch (Exception ex)
            {
                logger.Info("OPPO: could not turn the TV on through the remote ({0})", ex.Message);
                return false;
            }
        }
    }
}
