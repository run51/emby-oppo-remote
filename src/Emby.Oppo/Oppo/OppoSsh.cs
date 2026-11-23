using System;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Emby.Oppo.Oppo
{
    /// <summary>
    /// SSH to a jailbroken player (jb_overlay: dropbear on port 22, user root). Used to restart it: the OPPO's own
    /// "reboot" (busybox or /sbin/reboot) does nothing, "reboot -f" after "sync" restarts it in about 25 s
    /// (checked on a UDP-205, 2026-11-21).
    /// </summary>
    internal static class OppoSsh
    {
        public enum Outcome { Ok, BadPassword, NoSsh, Unreachable, Failed }

        /// <summary>Runs a command as root and returns its output. Never throws.</summary>
        public static Task<(Outcome Result, string Output)> Run(string host, string password, string command, TimeSpan timeout)
        {
            EmbeddedAssemblies.Register();   // before any SSH.NET type is touched (RunCore is compiled on its first call)
            return Task.Run(() => Safe(host, password, command, timeout));
        }

        private static (Outcome, string) Safe(string host, string password, string command, TimeSpan timeout)
        {
            try { return RunCore(host, password, command, timeout); }
            catch (SshAuthenticationException ex) { return (Outcome.BadPassword, ex.Message); }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused) { return (Outcome.NoSsh, ex.Message); }
            catch (SocketException ex) { return (Outcome.Unreachable, ex.Message); }
            catch (SshOperationTimeoutException ex) { return (Outcome.Unreachable, ex.Message); }
            catch (Exception ex) { return (Outcome.Failed, ex.GetType().Name + ": " + ex.Message); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static (Outcome, string) RunCore(string host, string password, string command, TimeSpan timeout)
        {
            using (var client = new SshClient(host, 22, "root", password ?? ""))
            {
                client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(10);
                client.Connect();
                using (var cmd = client.CreateCommand(command))
                {
                    cmd.CommandTimeout = timeout;
                    string output = cmd.Execute() + cmd.Error;
                    client.Disconnect();
                    return (cmd.ExitStatus == 0 ? Outcome.Ok : Outcome.Failed, output.Trim());
                }
            }
        }

        /// <summary>
        /// The restart command: it returns at once and the player restarts a second later, so that the SSH session
        /// ends normally first.
        /// </summary>
        public const string RestartCommand = "(sleep 1; sync; sync; reboot -f) </dev/null >/dev/null 2>&1 &";
    }
}
