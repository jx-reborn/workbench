using System;
using System.Collections.Frozen;

namespace JXReborn.ScriptMigrator.Rules;

/// <summary>
/// Canonical Sim/Bot exclusion rules mirroring <c>qa/scripts/pack_legacy_profiles.py</c>.
/// Only explicitly enumerated Sim/Bot overlay scripts are excluded; 100% of gameplay scripts are retained.
/// </summary>
public static class LegacyExclusionRules
{
    private static readonly FrozenSet<string> ExcludedExactRelativePaths = new[]
    {
        "server1/script/battles/marshal/simtk.lua",
        "server1/script/battles/marshal/npcdeath_simcity.lua",
        "server1/script/missions/sevencity/bosswar.lua",
        "server1/script/missions/sevencity/simsevencity.lua",
        "server1/script/missions/sevencity/sim_guilds.lua",
        "server1/script/missions/sevencity/sim_siege_names.lua",
        "server1/script/item/tuicankhon_tudong.lua",
        "gateway/s3relay/script/simcity.lua",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] ExcludedPrefixes =
    [
        "server1/script/global/nobitaxd/vdk/simcity/",
        "server1/script/script/global/nobitaxd/vdk/simcity/",
        "server1/script/missions/leaguematch/sims/",
        "server1/script/global/autoingame/",
        "server1/script/item/autoingame/",
        "server1/script/item/vsd/",
        "server1/script/global/vanhung1/vsd/",
    ];

    public static string NormalizeRelativePath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        string normalized = relativePath.Replace('\\', '/').TrimStart('/');
        return normalized;
    }

    public static bool IsExcludedSimBotScript(string relativePathFromLegacyBin)
    {
        string normalized = NormalizeRelativePath(relativePathFromLegacyBin);
        if (ExcludedExactRelativePaths.Contains(normalized))
        {
            return true;
        }

        foreach (string prefix in ExcludedPrefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
