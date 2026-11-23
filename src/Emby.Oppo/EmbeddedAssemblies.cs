using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace Emby.Oppo
{
    /// <summary>
    /// Loads the libraries embedded in Emby.Oppo.dll (SSH.NET) when they are first needed, so that the plugin is
    /// still installed and updated as two files. AppDomain.AssemblyResolve is the last resort of every load context,
    /// including the one Emby gives plugins.
    /// </summary>
    internal static class EmbeddedAssemblies
    {
        private static readonly string[] Names = { "Renci.SshNet", "SshNet.Security.Cryptography" };
        private static int _registered;
        private static readonly object Gate = new object();

        public static void Register()
        {
            if (Interlocked.Exchange(ref _registered, 1) == 1) return;
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            if (!Names.Contains(name, StringComparer.Ordinal)) return null;
            lock (Gate)
            {
                var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
                if (loaded != null) return loaded;
                using (var stream = typeof(EmbeddedAssemblies).Assembly.GetManifestResourceStream("Emby.Oppo.Lib." + name + ".dll"))
                {
                    if (stream == null) return null;
                    using (var memory = new MemoryStream())
                    {
                        stream.CopyTo(memory);
                        return Assembly.Load(memory.ToArray());
                    }
                }
            }
        }
    }
}
