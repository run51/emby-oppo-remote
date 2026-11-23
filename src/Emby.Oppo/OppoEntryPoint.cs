using System;
using System.Linq;
using System.Threading;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Session;

namespace Emby.Oppo
{
    /// <summary>
    /// Registers the OPPO as an Emby playback device and keeps the session alive
    /// (Emby forgets idle sessions, so it is refreshed every minute).
    /// </summary>
    public sealed class OppoEntryPoint : IServerEntryPoint
    {
        private const string AppName = "OPPO";
        private readonly ISessionManager _sessionManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly IServerApplicationHost _appHost;
        private readonly IJsonSerializer _json;
        private readonly ILogger _logger;
        private Timer _timer;

        /// <summary>Active instance (the controller attaches the current playback's user to it).</summary>
        internal static OppoEntryPoint Current { get; private set; }

        private readonly IUserManager _userManager;
        private User _user;   // last user who started a movie: kept on the session (resume point, "watched")

        private readonly MediaBrowser.Controller.MediaEncoding.IMediaEncoder _mediaEncoder;
        private readonly MediaBrowser.Common.Configuration.IApplicationPaths _paths;

        public OppoEntryPoint(ISessionManager sessionManager, ILibraryManager libraryManager, IItemRepository itemRepository,
            IServerApplicationHost appHost, IJsonSerializer json, ILogManager logManager, IUserManager userManager,
            IServerConfigurationManager serverConfig, MediaBrowser.Controller.MediaEncoding.IMediaEncoder mediaEncoder,
            MediaBrowser.Common.Configuration.IApplicationPaths paths)
        {
            _mediaEncoder = mediaEncoder;
            _paths = paths;
            Current = this;
            L10n.ServerConfig = serverConfig;
            _userManager = userManager;
            _sessionManager = sessionManager;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _appHost = appHost;
            _json = json;
            _logger = logManager.GetLogger("Oppo");
        }

        public void Run()
        {
            _libraryManager.ItemUpdated += OnItemUpdated;
            try
            {
                var plugin = Plugin.Instance;
                if (plugin != null && plugin.Configuration.MigrateLegacy()) plugin.SaveConfiguration();
            }
            catch (Exception ex)
            {
                _logger.ErrorException("OPPO: could not migrate legacy settings", ex);
            }
            _timer = new Timer(_ => Register(), null, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1));
        }

        /// <summary>
        /// Attaches the user who starts a movie to the "OPPO" session: Emby only records the playback
        /// position and "watched" state for the session's user.
        /// </summary>
        internal void AttachUser(string controllingUserId)
        {
            // Called through reflection: IUserManager.GetUserById also has ReadOnlySpan overloads, missing from netstandard2.0.
            User user = null;
            if (Guid.TryParse(controllingUserId, out var guid))
                user = typeof(IUserManager).GetMethod("GetUserById", new[] { typeof(Guid) })?.Invoke(_userManager, new object[] { guid }) as User;
            else if (long.TryParse(controllingUserId, out var internalId))
                user = typeof(IUserManager).GetMethod("GetUserById", new[] { typeof(long) })?.Invoke(_userManager, new object[] { internalId }) as User;
            if (user == null) return;
            if (_user == null || _user.InternalId != user.InternalId)
                _logger.Info("OPPO: playing for user {0} (resume point and watched state recorded for them)", user.Name);
            _user = user;
            Register();
        }

        private void Register()
        {
            try
            {
                var config = Plugin.Instance.Configuration;
                if (string.IsNullOrWhiteSpace(config.OppoAddress)) return; // not configured yet
                string deviceId = "emby-oppo-" + config.OppoAddress;
                var session = _sessionManager.LogSessionActivity(AppName, typeof(Plugin).Assembly.GetName().Version.ToString(3), deviceId, config.DeviceName, config.OppoAddress, _user);

                if (!session.SessionControllers.OfType<OppoSessionController>().Any())
                {
                    session.AddController(new OppoSessionController(session, _sessionManager, _libraryManager, _itemRepository,
                        _json, _logger, LocalAddress()));
                    _sessionManager.ReportCapabilities(session, new ClientCapabilities
                    {
                        PlayableMediaTypes = new[] { "Video" },
                        SupportedCommands = new[] { "SetAudioStreamIndex", "SetSubtitleStreamIndex" }.Concat(OppoSessionController.RemoteKeys.Keys).ToArray(),
                        SupportsMediaControl = true
                    });
                    _logger.Info("OPPO: device \"{0}\" ({1}) available in \"Play on…\"", config.DeviceName, config.OppoAddress);
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("OPPO: could not register the device", ex);
            }
        }

        /// <summary>IP address of the Emby server, sent to the OPPO when signing in.</summary>
        private string LocalAddress()
        {
            try
            {
                var host = new Uri(_appHost.GetLocalApiUrl(CancellationToken.None).GetAwaiter().GetResult()).Host;
                return host;
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// A rescan probes a file again and resets Emby's track information: Atmos / DTS:X are written back once
        /// the other plugins (ISO Blu-ray sets the languages) are done with the item.
        /// </summary>
        private void OnItemUpdated(object sender, ItemChangeEventArgs e)
        {
            if (!(e.Item is Video video) || video.InternalId <= 0 || string.IsNullOrEmpty(video.Path)) return;
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
#pragma warning disable CS0618
                    var extras = new AudioExtras(_itemRepository, _json, _logger, _mediaEncoder.EncoderPath, _paths.CachePath);
#pragma warning restore CS0618
                    var known = extras.Peek(video);
                    if (known != null) extras.ApplyToEmby(video, known, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.Debug("OPPO: Atmos / DTS:X not restored for {0}: {1}", video.Name, ex.Message);
                }
            });
        }

        public void Dispose()
        {
            _libraryManager.ItemUpdated -= OnItemUpdated;
            _timer?.Dispose();
        }
    }
}
