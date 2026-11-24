using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
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

    [Route("/Oppo/TestSsh", "POST", Summary = "Connects to a jailbroken OPPO over SSH (root) and reads its uptime and the jb_overlay's Telnet setting")]
    public sealed class TestSsh : IReturn<TestMappingResult>
    {
        public string Address { get; set; }
        /// <summary>Root password; empty: the saved one.</summary>
        public string Password { get; set; }
    }

    [Route("/Oppo/TestDlna", "POST", Summary = "Checks DLNA playback: the OPPO's renderer, the plugin's film server and, over SSH, that the player reaches it")]
    public sealed class TestDlna : IReturn<TestMappingResult>
    {
        public string Address { get; set; }
        /// <summary>Film server port; 0: the saved one.</summary>
        public int Port { get; set; }
        /// <summary>Also check from the player over SSH (jailbroken player).</summary>
        public bool Ssh { get; set; }
        /// <summary>Root password; empty: the saved one.</summary>
        public string Password { get; set; }
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

        public async Task<object> Post(TestSsh request)
        {
            var config = Plugin.Instance.Configuration;
            string host = string.IsNullOrWhiteSpace(request.Address) ? config.OppoAddress : request.Address.Trim();
            if (string.IsNullOrWhiteSpace(host))
                return new TestMappingResult { Message = L10n.T(Fr, "Enter the OPPO's IP address first.", "Indiquez d'abord l'adresse IP de l'OPPO.") };
            string password = string.IsNullOrEmpty(request.Password) ? config.SshPassword : request.Password;
            var (result, output) = await OppoSsh.Run(host.Trim(), password,
                "cat /proc/uptime; grep '^USE_TELNET' /mnt/ubi_boot/jb_overlay/config.ini 2>/dev/null", TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            _logger.Info("OPPO: SSH test {0}: {1}", host.Trim(), result);
            if (result != OppoSsh.Outcome.Ok)
                return new TestMappingResult { Message = "✘ " + PlayerRestart.Explain(result, output, Fr) };
            var lines = output.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            double.TryParse(lines.Length > 0 ? lines[0].Split(' ')[0] : "", NumberStyles.Float, CultureInfo.InvariantCulture, out double uptime);
            var since = TimeSpan.FromSeconds(uptime);
            string message = L10n.T(Fr, "✔ SSH works: the OPPO can be restarted from the remote (running for ", "✔ SSH fonctionne : l'OPPO peut être redémarré depuis la télécommande (en marche depuis ")
                + ((int)since.TotalHours > 0 ? (int)since.TotalHours + " h " : "") + since.Minutes + " min).";
            if (output.IndexOf("USE_TELNET=1", StringComparison.Ordinal) >= 0)
                message += L10n.T(Fr, " ⚠ The jb_overlay's Telnet is on (USE_TELNET=1): it takes port 23 from the OPPO's IP control. Set it to 0 (see the README).",
                    " ⚠ Le Telnet du jb_overlay est activé (USE_TELNET=1) : il prend le port 23 au contrôle IP de l'OPPO. Passez-le à 0 (voir le README).");
            return new TestMappingResult { Success = true, Message = message };
        }

        /// <summary>
        /// "Test DLNA" of the settings page (0.24.1): the OPPO's DLNA renderer answers (port 49152); the plugin's film
        /// server serves a small test file; on a jailbroken player, the OPPO itself fetches that file over SSH (the usual
        /// failure: port 8099 closed by Docker's bridge network or Windows' firewall) and the oppo-dv-mkv mod runs.
        /// </summary>
        public async Task<object> Post(TestDlna request)
        {
            var config = Plugin.Instance.Configuration;
            string player = (string.IsNullOrWhiteSpace(request.Address) ? config.OppoAddress : request.Address ?? "").Trim();
            if (player.Length == 0)
                return new TestMappingResult { Message = L10n.T(Fr, "Enter the OPPO's IP address first.", "Indiquez d'abord l'adresse IP de l'OPPO.") };
            int port = request.Port > 1024 && request.Port < 65536 ? request.Port : config.DiscServerPort;
            var lines = new List<string>();
            bool ok = true;

            // 1. The renderer.
            try
            {
                var r = (HttpWebRequest)WebRequest.Create("http://" + player + ":49152/dmr_oppo.xml");
                r.Timeout = 5000; r.ReadWriteTimeout = 5000;
                using (var response = (HttpWebResponse)await r.GetResponseAsync().ConfigureAwait(false))
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(await reader.ReadToEndAsync().ConfigureAwait(false), "<friendlyName>([^<]+)</friendlyName>");
                    lines.Add(L10n.T(Fr, "✔ The OPPO's DLNA renderer answers", "✔ Le lecteur DLNA de l'OPPO répond") + (m.Success ? " (" + m.Groups[1].Value + ")." : "."));
                }
            }
            catch (Exception)
            {
                ok = false;
                lines.Add(L10n.T(Fr, "✘ The OPPO's DLNA renderer does not answer (port 49152): turn the player on, then test again.",
                    "✘ Le lecteur DLNA de l'OPPO ne répond pas (port 49152) : allumez l'OPPO, puis testez à nouveau."));
            }

            // 2. The film server, with a small test file.
            string server = OppoEntryPoint.Current?.ServerAddress ?? "";
            string url = null;
            if (server.Length == 0)
            {
                ok = false;
                lines.Add(L10n.T(Fr, "✘ This server's network address is unknown (Emby's network settings).", "✘ L'adresse réseau de ce serveur est inconnue (réglages réseau d'Emby)."));
            }
            else
            {
                const string marker = "OPPO-DLNA-TEST";
                try
                {
                    url = DiscServer.RegisterTree(new Dictionary<string, DiscServer.VirtualFile>
                    {
                        ["test.txt"] = new DiscServer.VirtualFile { Data = Encoding.ASCII.GetBytes(marker), Size = marker.Length },
                    }, server, port, TimeSpan.FromMinutes(2), _logger) + "test.txt";
                    var r = (HttpWebRequest)WebRequest.Create(url);
                    r.Timeout = 5000; r.ReadWriteTimeout = 5000;
                    using (var response = (HttpWebResponse)await r.GetResponseAsync().ConfigureAwait(false))
                    using (var reader = new StreamReader(response.GetResponseStream()))
                        if ((await reader.ReadToEndAsync().ConfigureAwait(false)).Contains(marker))
                            lines.Add(L10n.T(Fr, "✔ The plugin's film server answers on ", "✔ Le serveur de films du plugin répond sur ") + server + ":" + port.ToString(CultureInfo.InvariantCulture) + ".");
                        else throw new InvalidDataException("unexpected answer");
                }
                catch (Exception ex)
                {
                    ok = false; url = null;
                    lines.Add(L10n.T(Fr, "✘ The plugin's film server does not answer on port " + port.ToString(CultureInfo.InvariantCulture) + " (" + ex.Message + "): choose another free port under Advanced and restart Emby.",
                        "✘ Le serveur de films du plugin ne répond pas sur le port " + port.ToString(CultureInfo.InvariantCulture) + " (" + ex.Message + ") : choisissez un autre port libre sous Avancé et redémarrez Emby."));
                }
            }

            // 3. From the player itself, over SSH (jailbroken players).
            if (request.Ssh && url != null)
            {
                string password = string.IsNullOrEmpty(request.Password) ? config.SshPassword : request.Password;
                var (result, output) = await OppoSsh.Run(player, password,
                    "ps | grep -c '[d]lnaiso'; wget -T 6 -q -O - '" + url + "' 2>&1 | head -c 200", TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                if (result != OppoSsh.Outcome.Ok)
                    lines.Add("? " + L10n.T(Fr, "Not checked from the player: ", "Non vérifié depuis l'OPPO : ") + PlayerRestart.Explain(result, output, Fr));
                else
                {
                    var outLines = output.Split(new[] { '\n' }, 2);
                    bool mod = int.TryParse(outLines[0].Trim(), out int n) && n > 0;
                    lines.Add(mod ? L10n.T(Fr, "✔ The oppo-dv-mkv mod is running on the player.", "✔ Le mod oppo-dv-mkv tourne sur l'OPPO.")
                                  : L10n.T(Fr, "✘ The oppo-dv-mkv mod is not running on the player (Setup → Extras → DLNA Disc Images).",
                                               "✘ Le mod oppo-dv-mkv ne tourne pas sur l'OPPO (Setup → Extras → DLNA Disc Images)."));
                    bool reached = outLines.Length > 1 && outLines[1].Contains("OPPO-DLNA-TEST");
                    lines.Add(reached ? L10n.T(Fr, "✔ The OPPO reaches the film server.", "✔ L'OPPO joint le serveur de films.")
                        : L10n.T(Fr, "✘ The OPPO cannot reach " + server + ":" + port.ToString(CultureInfo.InvariantCulture) + ": with Docker, use host networking or publish this port; on Windows, allow it in the firewall.",
                                     "✘ L'OPPO ne joint pas " + server + ":" + port.ToString(CultureInfo.InvariantCulture) + " : avec Docker, utilisez le réseau hôte ou publiez ce port ; sous Windows, autorisez-le dans le pare-feu."));
                    ok &= mod && reached;
                }
            }
            else if (url != null)
                lines.Add("? " + L10n.T(Fr, "Whether the OPPO itself reaches this port is only checked on a jailbroken player (SSH, under Advanced); otherwise, start a film.",
                    "Que l'OPPO joigne bien ce port n'est vérifié que sur un OPPO déverrouillé (SSH, sous Avancé) ; sinon, lancez un film."));
            _logger.Info("OPPO: DLNA test {0}: {1}", player, ok ? "OK" : "problem");
            return new TestMappingResult { Success = ok, Message = string.Join("\n", lines) };
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
