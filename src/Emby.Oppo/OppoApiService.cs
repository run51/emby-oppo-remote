using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Oppo.Oppo;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;

namespace Emby.Oppo
{
    [Route("/Oppo/Discover", "GET", Summary = "Wakes the OPPO and lists the NFS/SMB shares it can see")]
    public sealed class DiscoverShares : IReturn<DiscoverResult>
    {
        /// <summary>Address of the OPPO to query (otherwise the one from the settings).</summary>
        public string Address { get; set; }
    }

    public sealed class DiscoveredServer
    {
        public string Protocol { get; set; }
        public string Name { get; set; }
        public string Address { get; set; }
        /// <summary>NFS: visible exports. SMB: empty (the OPPO does not list them without credentials).</summary>
        public string[] Shares { get; set; } = Array.Empty<string>();
        public string Error { get; set; }
    }

    public sealed class DiscoverResult
    {
        public bool Reachable { get; set; }
        public string Message { get; set; }
        public DiscoveredServer[] Servers { get; set; } = Array.Empty<DiscoveredServer>();
    }

    [Route("/Oppo/TestConnection", "GET", Summary = "Checks the OPPO's IP control link (port 23) and says what is wrong")]
    public sealed class TestConnection : IReturn<TestMappingResult>
    {
        /// <summary>Player address (otherwise the one from the settings).</summary>
        public string Address { get; set; }
        /// <summary>"IP control through" address (oppo-multiplexer); empty: the player itself.</summary>
        public string IpControl { get; set; }
    }

    [Route("/Oppo/TestMapping", "POST", Summary = "Mounts a share on the OPPO and lists its root")]
    public sealed class TestMapping : IReturn<TestMappingResult>
    {
        public string Address { get; set; }
        public string Protocol { get; set; }
        public string Server { get; set; }
        public string Share { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
    }

    public sealed class TestMappingResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string[] Entries { get; set; } = Array.Empty<string>();
    }

    /// <summary>API used by the "OPPO" settings page (administrators only).</summary>
    [Authenticated(Roles = "Admin")]
    public sealed class OppoApiService : IService, IRequiresRequest
    {
        private readonly IJsonSerializer _json;
        private readonly ILogger _logger;

        public OppoApiService(IJsonSerializer json, ILogManager logManager)
        {
            _json = json;
            _logger = logManager.GetLogger("Oppo");
        }

        public IRequest Request { get; set; }

        private bool Fr => L10n.IsFrench(Request);

        private OppoClient Client(string address)
        {
            string host = string.IsNullOrWhiteSpace(address) ? Plugin.Instance.Configuration.OppoAddress : address.Trim();
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException(L10n.T(Fr, "OPPO address not set", "Adresse de l'OPPO non renseignée"));
            return new OppoClient(host, _json, _logger);
        }

        public async Task<object> Get(DiscoverShares request)
        {
            var result = new DiscoverResult();
            var oppo = Client(request.Address);
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120)))
            {
                if (!await oppo.WakeAndWait(TimeSpan.FromSeconds(60), cts.Token).ConfigureAwait(false))
                {
                    result.Message = L10n.T(Fr,
                        "The OPPO is not responding (address, power, or the \"quick start / network standby\" option is disabled).",
                        "L'OPPO ne répond pas (adresse, alimentation, ou option « démarrage rapide/réseau en veille » désactivée).");
                    return result;
                }
                result.Reachable = true;
                var servers = new List<DiscoveredServer>();
                foreach (var s in await oppo.ListServers(cts.Token).ConfigureAwait(false))
                {
                    var d = new DiscoveredServer { Protocol = s.Protocol, Name = s.Name, Address = s.Address };
                    if (s.Protocol == "nfs")
                    {
                        try { d.Shares = (await oppo.ListNfsExports(s.Address, cts.Token).ConfigureAwait(false)).ToArray(); }
                        catch (Exception ex) when (!(ex is OperationCanceledException)) { d.Error = ex.Message; }
                    }
                    servers.Add(d);
                }
                result.Servers = servers.ToArray();
                result.Message = servers.Count == 0
                    ? L10n.T(Fr, "The OPPO found no NFS or SMB server.", "L'OPPO n'a trouvé aucun serveur NFS ou SMB.")
                    : servers.Count + L10n.T(Fr, " server(s) found by the OPPO.", " serveur(s) trouvé(s) par l'OPPO.");
                _logger.Info("OPPO: discovery: {0} server(s) found", servers.Count);
                return result;
            }
        }

        public async Task<object> Get(TestConnection request)
        {
            string player = string.IsNullOrWhiteSpace(request.Address) ? Plugin.Instance.Configuration.OppoAddress : request.Address.Trim();
            if (string.IsNullOrWhiteSpace(player))
                return new TestMappingResult { Message = L10n.T(Fr, "Enter the OPPO's IP address first.", "Indiquez d'abord l'adresse IP de l'OPPO.") };
            var (host, port) = OppoClient.IpControlEndpoint(request.IpControl, player.Trim());
            bool multiplexer = !string.IsNullOrWhiteSpace(request.IpControl);
            string target = host + ":" + port.ToString(CultureInfo.InvariantCulture) + (multiplexer ? " (oppo-multiplexer)" : "");
            OppoClient.ProbeResult result;
            string answer, error;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
            {
                (result, answer, error) = await OppoClient.Probe(host, port, cts.Token).ConfigureAwait(false);
                // The player takes one connection at a time: the plugin's own query may have been using it.
                if (result == OppoClient.ProbeResult.Refused || result == OppoClient.ProbeResult.Closed)
                {
                    await Task.Delay(1500, cts.Token).ConfigureAwait(false);
                    (result, answer, error) = await OppoClient.Probe(host, port, cts.Token).ConfigureAwait(false);
                }
            }
            string message;
            switch (result)
            {
                case OppoClient.ProbeResult.Ok:
                    string power = answer;
                    int ok = answer.IndexOf("OK ", StringComparison.OrdinalIgnoreCase);
                    if (ok >= 0) power = answer.Substring(ok + 3).Trim().ToUpperInvariant();
                    message = power == "ON" ? L10n.T(Fr, "✔ The OPPO answers: it is on.", "✔ L'OPPO répond : il est allumé.")
                        : power == "OFF" ? L10n.T(Fr, "✔ The OPPO answers: it is in standby (Emby can turn it on).", "✔ L'OPPO répond : il est en veille (Emby peut l'allumer).")
                        : L10n.T(Fr, "✔ The OPPO answers (" + answer + ").", "✔ L'OPPO répond (" + answer + ").");
                    break;
                case OppoClient.ProbeResult.Refused:
                    message = multiplexer
                        ? L10n.T(Fr, "✘ " + target + " refuses the connection: the oppo-multiplexer is not running at this address.",
                            "✘ " + target + " refuse la connexion : l'oppo-multiplexer ne tourne pas à cette adresse.")
                        : L10n.T(Fr, "✘ " + target + " refuses the connection. On the OPPO: Setup > Network Setup > IP Control: On. Also check that no other device or app (home automation, universal remote…) is connected to it: the player accepts only one IP control connection at a time.",
                            "✘ " + target + " refuse la connexion. Sur l'OPPO : Setup > Network Setup > IP Control : On. Vérifiez aussi qu'aucun autre appareil ou application (domotique, télécommande universelle…) n'y est connecté : le lecteur n'accepte qu'une connexion de contrôle IP à la fois.");
                    break;
                case OppoClient.ProbeResult.Timeout:
                    message = L10n.T(Fr, "✘ No answer from " + target + " within 5 s. Check the address, that the player is on, or in network standby when off (Setup > Device Setup > Standby Mode: Network Standby), and that this server can reach your local network (Docker: default bridge or host network).",
                        "✘ Pas de réponse de " + target + " en 5 s. Vérifiez l'adresse, que le lecteur est allumé, ou en veille réseau quand il est éteint (Setup > Device Setup > Standby Mode : Network Standby), et que ce serveur atteint votre réseau local (Docker : réseau bridge par défaut ou host).");
                    break;
                case OppoClient.ProbeResult.Closed:
                    message = L10n.T(Fr, "✘ " + target + " accepted the connection, then closed it: another device or app is probably connected to the OPPO already (one IP control connection at a time).",
                        "✘ " + target + " a accepté la connexion puis l'a fermée : un autre appareil ou une autre application est sans doute déjà connecté à l'OPPO (une seule connexion de contrôle IP à la fois).");
                    break;
                case OppoClient.ProbeResult.Silent when multiplexer:
                    message = L10n.T(Fr, "✘ The oppo-multiplexer (" + target + ") accepts the connection but the player does not answer: the player is off (and not in network standby), or the multiplexer cannot reach it.",
                        "✘ L'oppo-multiplexer (" + target + ") accepte la connexion mais le lecteur ne répond pas : il est éteint (et pas en veille réseau), ou le multiplexeur ne l'atteint pas.");
                    break;
                case OppoClient.ProbeResult.Silent:
                    message = L10n.T(Fr, "✘ " + target + " accepts the connection but does not answer. Unplug the player for 30 s (its IP control sometimes hangs), and check that no other device is connected to it.",
                        "✘ " + target + " accepte la connexion mais ne répond pas. Débranchez le lecteur 30 s (son contrôle IP se bloque parfois), et vérifiez qu'aucun autre appareil n'y est connecté.");
                    break;
                case OppoClient.ProbeResult.Telnet:
                    message = L10n.T(Fr, "✘ " + target + " is a Telnet server (the jailbreak's), not the OPPO's IP control, which cannot use port 23 then. With the jb_overlay, set USE_TELNET=0 in /mnt/ubi_boot/jb_overlay/config.ini (over SSH, see the README) and unplug the player for 30 s. Also check Setup > Network Setup > IP Control: On.",
                        "✘ " + target + " est un serveur Telnet (celui du jailbreak), pas le contrôle IP de l'OPPO, qui ne peut alors pas utiliser le port 23. Avec le jb_overlay, mettez USE_TELNET=0 dans /mnt/ubi_boot/jb_overlay/config.ini (en SSH, voir le README) et débranchez le lecteur 30 s. Vérifiez aussi Setup > Network Setup > IP Control : On.");
                    break;
                case OppoClient.ProbeResult.Unexpected:
                    message = L10n.T(Fr, "✘ Unexpected answer from " + target + ": " + answer, "✘ Réponse inattendue de " + target + " : " + answer);
                    break;
                case OppoClient.ProbeResult.UnknownHost:
                    message = L10n.T(Fr, "✘ Unknown address: " + host + ".", "✘ Adresse inconnue : " + host + ".");
                    break;
                default:
                    message = L10n.T(Fr, "✘ " + host + " cannot be reached from this server (" + error + "). Check the address, that the player is on or in network standby, and the server's network (Docker).",
                        "✘ " + host + " est injoignable depuis ce serveur (" + error + "). Vérifiez l'adresse, que le lecteur est allumé ou en veille réseau, et le réseau du serveur (Docker).");
                    break;
            }
            _logger.Info("OPPO: connection test {0}: {1}{2}", target, result, string.IsNullOrEmpty(answer) ? "" : " (" + answer + ")");
            return new TestMappingResult { Success = result == OppoClient.ProbeResult.Ok, Message = message };
        }

        public async Task<object> Post(TestMapping request)
        {
            var oppo = Client(request.Address);
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
            {
                try
                {
                    if (!await oppo.WakeAndWait(TimeSpan.FromSeconds(60), cts.Token).ConfigureAwait(false))
                        return new TestMappingResult { Message = L10n.T(Fr, "The OPPO is not responding.", "L'OPPO ne répond pas.") };
                    await oppo.SignIn(Plugin.Instance.Configuration.OppoAddress, cts.Token).ConfigureAwait(false);
                    var r = await oppo.TestMapping(request.Protocol, request.Server, (request.Share ?? "").TrimStart('/'),
                        request.Username, request.Password, Fr, cts.Token).ConfigureAwait(false);
                    return new TestMappingResult { Success = r.Ok, Message = r.Message, Entries = r.Entries.ToArray() };
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    return new TestMappingResult { Message = L10n.T(Fr, "Error: ", "Erreur : ") + ex.Message };
                }
            }
        }
    }
}
