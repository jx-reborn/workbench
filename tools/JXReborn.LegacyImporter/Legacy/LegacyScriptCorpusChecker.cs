using System;
using System.Collections.Frozen;
using JXReborn.LegacyImporter.Sources;
using JXReborn.Packs.TieuDaoTu.Hosting;
using JXReborn.Primitives.Encodings;

namespace JXReborn.LegacyImporter.Legacy;

/// <summary>
/// Classifies legacy <c>\script\...</c> references against <see cref="ILegacyServerSource"/> and Sim/Bot
/// exclusion rules so dead references inherited from Kingsoft/VNG tables are explicitly marked rather than
/// conflated with exporter/encoding regressions.
/// </summary>
public static class LegacyScriptCorpusChecker
{
    private static readonly FrozenSet<string> ExcludedExactServerRelPaths = new[]
    {
        "script/battles/marshal/simtk.lua",
        "script/battles/marshal/npcdeath_simcity.lua",
        "script/missions/sevencity/bosswar.lua",
        "script/missions/sevencity/simsevencity.lua",
        "script/missions/sevencity/sim_guilds.lua",
        "script/missions/sevencity/sim_siege_names.lua",
        "script/item/tuicankhon_tudong.lua",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] ExcludedServerPrefixes =
    [
        "script/global/nobitaxd/vdk/simcity/",
        "script/script/global/nobitaxd/vdk/simcity/",
        "script/missions/leaguematch/sims/",
        "script/global/autoingame/",
        "script/item/autoingame/",
        "script/item/vsd/",
        "script/global/vanhung1/vsd/",
    ];

    public static bool IsExcludedSimBotScript(string? scriptPathOrRelKey)
    {
        if (string.IsNullOrWhiteSpace(scriptPathOrRelKey))
        {
            return false;
        }

        string norm = scriptPathOrRelKey.Trim().Replace('\\', '/').TrimStart('/');
        if (norm.StartsWith("server1/", StringComparison.OrdinalIgnoreCase))
        {
            norm = norm["server1/".Length..];
        }

        if (ExcludedExactServerRelPaths.Contains(norm))
        {
            return true;
        }

        foreach (string prefix in ExcludedServerPrefixes)
        {
            if (norm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns <c>null</c> when <paramref name="scriptPath"/> resolves to an active file in <paramref name="source"/>,
    /// <c>"excludedSimBot"</c> when excluded by Sim/Bot rules, or <c>"missingInLegacyCorpus"</c> when absent from legacy ground truth.
    /// </summary>
    public static string? ClassifyScriptStatus(ILegacyServerSource source, string? scriptPath)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(scriptPath) ||
            scriptPath.Contains("noscript.lua", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string utf8Normalized = LegacyScriptPathNormalizer.NormalizeToUtf8(scriptPath);
        if (IsExcludedSimBotScript(utf8Normalized) || IsExcludedSimBotScript(scriptPath))
        {
            return "excludedSimBot";
        }

        // Absolute Windows workstation paths left in legacy Region_S.dat (e.g. "\e:\剑3\...") are never valid server scripts
        string trimmed = utf8Normalized.TrimStart('\\', '/');
        if (trimmed.Length >= 2 && trimmed[1] == ':')
        {
            return "missingInLegacyCorpus";
        }

        if (source.TryResolveServerFile(utf8Normalized, out _) ||
            source.TryResolveServerFile(scriptPath, out _) ||
            source.TryResolveServerFile(GbkEncoding.Instance.GetBytes(utf8Normalized), out _))
        {
            return null;
        }

        return "missingInLegacyCorpus";
    }
}
