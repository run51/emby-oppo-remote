using System;
using System.Threading;
using System.Threading.Tasks;
using Emby.Oppo.Oppo;
using MediaBrowser.Model.Logging;

namespace Emby.Oppo
{
    /// <summary>
    /// Restarts a jailbroken player over SSH ("reboot -f", see <see cref="OppoSsh"/>): what unplugging it does,
    /// without touching the cable. The player comes back on after about 25 s; if it was in standby before, it is
    /// put back in standby.
    /// </summary>
    internal static class PlayerRestart
    {
        private static int _running;

        public static async Task<(bool Ok, string Message)> Start(OppoClient oppo, string host, ILogger logger, bool fr)
        {
            var config = Plugin.Instance.Configuration;
            if (!config.SshRestart)
                return (false, L10n.T(fr, "Restarting over SSH is off (OPPO settings page).", "Le redémarrage par SSH est désactivé (page de réglages OPPO)."));
            if (Interlocked.Exchange(ref _running, 1) == 1)
                return (false, L10n.T(fr, "The OPPO is already restarting.", "L'OPPO est déjà en train de redémarrer."));
            bool handedOver = false;
            try
            {
                bool wasOff = false;
                try
                {
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                        wasOff = (await oppo.Command("QPW", cts.Token).ConfigureAwait(false)).IndexOf("OFF", StringComparison.OrdinalIgnoreCase) >= 0;
                }
                catch (Exception ex) when (!(ex is OperationCanceledException)) { /* power state unknown: left as it comes back */ }
                catch (OperationCanceledException) { }

                var (result, output) = await OppoSsh.Run(host, config.SshPassword, OppoSsh.RestartCommand, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                if (result != OppoSsh.Outcome.Ok)
                {
                    logger.Info("OPPO: restart over SSH failed ({0}): {1} {2}", host, result, output);
                    return (false, Explain(result, output, fr));
                }
                logger.Info("OPPO: restarting the player over SSH ({0}){1}", host, wasOff ? "; it will be put back in standby" : "");
                handedOver = true;
                _ = Task.Run(() => Follow(oppo, wasOff, logger));
                return (true, L10n.T(fr, "The OPPO is restarting (about 30 s).", "L'OPPO redémarre (environ 30 s)."));
            }
            finally
            {
                if (!handedOver) Interlocked.Exchange(ref _running, 0);
            }
        }

        /// <summary>What an SSH outcome means for the user.</summary>
        public static string Explain(OppoSsh.Outcome result, string output, bool fr)
        {
            switch (result)
            {
                case OppoSsh.Outcome.BadPassword:
                    return L10n.T(fr, "SSH password refused: check the root password (jb_overlay's ROOT_PWD, \"oppo\" by default).",
                        "Mot de passe SSH refusé : vérifiez le mot de passe root (ROOT_PWD du jb_overlay, « oppo » par défaut).");
                case OppoSsh.Outcome.NoSsh:
                    return L10n.T(fr, "The OPPO has no SSH server: this needs a jailbroken player with the jb_overlay (USE_SSH=1).",
                        "L'OPPO n'a pas de serveur SSH : il faut un lecteur jailbreaké avec le jb_overlay (USE_SSH=1).");
                case OppoSsh.Outcome.Unreachable:
                    return L10n.T(fr, "The OPPO cannot be reached over SSH (" + output + ").", "L'OPPO est injoignable en SSH (" + output + ").");
                default:
                    return L10n.T(fr, "SSH error: " + output, "Erreur SSH : " + output);
            }
        }

        private static async Task Follow(OppoClient oppo, bool wasOff, ILogger logger)
        {
            var started = DateTime.UtcNow;
            try
            {
                // The player goes down about 8 s after the command and answers again after about 25 s.
                await Task.Delay(TimeSpan.FromSeconds(18)).ConfigureAwait(false);
                while (DateTime.UtcNow - started < TimeSpan.FromSeconds(150))
                {
                    string power = "";
                    try
                    {
                        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                            power = await oppo.Command("QPW", cts.Token).ConfigureAwait(false);
                    }
                    catch (Exception) { /* still restarting */ }
                    if (power.StartsWith("@QPW OK", StringComparison.OrdinalIgnoreCase))
                    {
                        logger.Info("OPPO: player back {0} s after the restart ({1})", (int)(DateTime.UtcNow - started).TotalSeconds, power);
                        if (wasOff && power.IndexOf("ON", 7, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            await Task.Delay(3000).ConfigureAwait(false);
                            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                                logger.Info("OPPO: put back in standby after the restart ({0})", await oppo.Command("POF", cts.Token).ConfigureAwait(false));
                        }
                        else if (power.IndexOf(" ON", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            // Back on: open its HTTP interface (position, chapters, tracks), closed after a restart.
                            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40)))
                                logger.Info("OPPO: HTTP interface after the restart: {0}", await oppo.WaitForHttp(TimeSpan.FromSeconds(30), cts.Token).ConfigureAwait(false) ? "open" : "still closed");
                        }
                        return;
                    }
                    await Task.Delay(3000).ConfigureAwait(false);
                }
                logger.Info("OPPO: no answer {0} s after the restart (unplug the player if it does not come back)", 150);
            }
            catch (Exception ex)
            {
                logger.ErrorException("OPPO: following the restart failed", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }
    }
}
