using System;
using System.IO;
using System.Security.Cryptography;
using RiMCP.Bridge;

namespace RiMCP.Read
{
    internal static class BuildIdentity
    {
        private static readonly Lazy<string> modHash = new Lazy<string>(AssemblyHash);

        public static object Describe()
        {
            return Dto.Obj(
                Dto.Field("modSha256", modHash.Value),
                Dto.Field("enabledGroups", BridgeRuntime.Access.EnabledGroups()));
        }

        private static string AssemblyHash()
        {
            string path = typeof(BuildIdentity).Assembly.Location;
            if (!File.Exists(path))
            {
                // Some runtimes load assemblies from memory.
                return null;
            }
            try
            {
                using (FileStream assembly = File.OpenRead(path))
                using (SHA256 sha = SHA256.Create())
                {
                    return BitConverter.ToString(sha.ComputeHash(assembly)).Replace("-", "").ToLowerInvariant();
                }
            }
            catch (IOException ex)
            {
                Verse.Log.Warning("[RimMCP] Could not read build identity: " + ex.Message);
                return null;
            }
            catch (UnauthorizedAccessException ex)
            {
                Verse.Log.Warning("[RimMCP] Could not read build identity: " + ex.Message);
                return null;
            }
        }
    }
}
