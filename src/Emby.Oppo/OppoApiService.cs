using System;
using System.Collections.Generic;
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
