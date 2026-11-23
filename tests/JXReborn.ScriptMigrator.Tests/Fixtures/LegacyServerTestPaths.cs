using System;
using System.IO;

namespace JXReborn.ScriptMigrator.Tests.Fixtures;

internal static class LegacyServerTestPaths
{
    public static string FindLegacyBinRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "legacy-server", "bin");
            if (Directory.Exists(Path.Combine(candidate, "server1", "script")))
            {
                return candidate;
            }

            string sibling = Path.Combine(dir.FullName, "..", "legacy-server", "bin");
            if (Directory.Exists(Path.Combine(sibling, "server1", "script")))
            {
                return Path.GetFullPath(sibling);
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate 'legacy-server/bin/server1/script' from test base directory.");
    }
}
