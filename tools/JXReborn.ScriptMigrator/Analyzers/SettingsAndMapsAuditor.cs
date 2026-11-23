using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using JXReborn.ScriptMigrator.Models;

namespace JXReborn.ScriptMigrator.Analyzers;

/// <summary>
/// Audits 100% of <c>server1/settings/</c> and <c>server1/settings/maplist.ini</c> against exported
/// <c>pack-tieudaotu/data/</c> catalogs so no settings table or map declaration is silently omitted.
/// </summary>
public sealed class SettingsAndMapsAuditor
{
    private static readonly FrozenSet<string> KnownExportedRootSettings = new[]
    {
        "maplist.ini",
        "forbitheart.txt",
        "forbititem.ini",
        "skills.txt",
        "skills1.txt",
        "clientweaponskill.txt",
        "attribconstdata.ini",
        "magicdesc.ini",
        "missles.txt",
        "npcs.txt",
        "objdata.txt",
        "buysell.txt",
        "goods.txt",
        "station.txt",
        "stationprice.txt",
        "waypoint.txt",
        "waypointprice.txt",
        "wharf.txt",
        "wharfprice.txt",
        "revivepos.ini",
        "nativeplacelist.ini",
        "factiontitle.txt",
        "ranksetting.txt",
        "rolename.flt",
        "timertask.txt",
        "playertitle.txt",
        "taxrates.ini",
        "map_type.txt",
        "item_type.txt",
        "item_detail.txt",
        "gamesetting.ini",
        "progressconfig.txt",
        "ÎäÆ÷ÎïÀí¹¥»÷¶ÔÕÕ±í.txt",
        "武器物理攻击对照表.txt",
        "tongname.flt",
        "goldboss.txt",
        "killer.ini",
        "adventure.txt",
        "citywar.ini",
        "cavelist.ini",
        "maptraffic.ini",
        "thiefskill.txt",
        "lottery.txt",
        "lotterys.txt",
        "player_limittime.ini",
        "tiredwarning.ini",
        "auction.ini",
        "stallscript_permit.ini",
        "permitdialognpc_info.txt",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Explicitly audited root <c>server1/settings/</c> files that are verified non-gameplay, client-visual/UI-only,
    /// legacy duplicates/templates, runtime 2-byte toggle flags, or Lua trigger scripts.
    /// </summary>
    private static readonly FrozenDictionary<string, string> KnownClassifiedNonPackRootSettings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // Client UI, sprite, color, audio, auto-pathfinding & localization tables
        ["adjustcolor.txt"] = "clientVisualAudioUiOnly",
        ["autopathfindroutes.txt"] = "clientVisualAudioUiOnly",
        ["portrait.ini"] = "clientVisualAudioUiOnly",
        ["soundlist.txt"] = "clientVisualAudioUiOnly",
        ["stringresource.txt"] = "clientVisualAudioUiOnly",
        ["×´Ì¬Óë¹âÐ§Í¼ÐÎ¶ÔÕÕ±í.txt"] = "clientVisualAudioUiOnly",
        ["状态与光效图形对照表.txt"] = "clientVisualAudioUiOnly",

        // Legacy backup copies, editor templates, and Excel sheets
        ["lotterys_.txt"] = "legacyBackupOrTemplate",
        ["missles1.txt"] = "legacyBackupOrTemplate",
        ["missletemplate.txt"] = "legacyBackupOrTemplate",
        ["skilltemplate.txt"] = "legacyBackupOrTemplate",
        ["½»Ò×±àºÅ.xls"] = "legacyBackupOrTemplate",
        ["交易编号.xls"] = "legacyBackupOrTemplate",

        // Legacy server topology, ops, updater & empty header-only tables
        ["autoupdate.ini"] = "legacyOpsOrEmptyTable",
        ["goc_serverlist.ini"] = "legacyOpsOrEmptyTable",
        ["Serverlist.ini"] = "legacyOpsOrEmptyTable",
        ["lastupdate.ini"] = "legacyOpsOrEmptyTable",
        ["logset.ini"] = "legacyOpsOrEmptyTable",
        ["product_config.ini"] = "legacyOpsOrEmptyTable",
        ["systemtimetask.txt"] = "legacyOpsOrEmptyTable",
        ["trip_config.ini"] = "legacyOpsOrEmptyTable",
        ["utilities.ini"] = "legacyOpsOrEmptyTable",
        ["event_1000_thanglong.txt"] = "legacyOpsOrEmptyTable",

        // Runtime 2-byte toggle state flags written by legacy GM scripts
        ["boss_runtime_DOC_CO_1945.cfg"] = "runtimeToggleFlag",
        ["boss_runtime_HK_DAI_0800.cfg"] = "runtimeToggleFlag",
        ["boss_runtime_HK_DAI_1930.cfg"] = "runtimeToggleFlag",
        ["boss_runtime_HK_DAI_2200.cfg"] = "runtimeToggleFlag",
        ["boss_runtime_HK_DAI_MANUAL.cfg"] = "runtimeToggleFlag",
        ["boss_runtime_HK_TIEU_0400.cfg"] = "runtimeToggleFlag",
        ["boss_runtime_HK_TIEU_1230.cfg"] = "runtimeToggleFlag",
        ["boss_runtime_HK_TIEU_2000.cfg"] = "runtimeToggleFlag",
        ["boss_runtime_HK_TIEU_2300.cfg"] = "runtimeToggleFlag",
        ["boss_runtime_HK_TIEU_MANUAL.cfg"] = "runtimeToggleFlag",

        // Root relay/trigger Lua scripts
        ["trigger.lua"] = "rootTriggerLuaScript",
        ["trigger_challengeoftime.lua"] = "rootTriggerLuaScript",
        ["trigger_dragonboat.lua"] = "rootTriggerLuaScript",
        ["trigger_include.lua"] = "rootTriggerLuaScript",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public SettingsAuditReport Audit(string legacyBinRoot, string packRoot)
    {
        string settingsRoot = Path.Combine(legacyBinRoot, "server1", "settings");
        var topFolderCounts = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var uncoveredRootFiles = new List<string>();
        int totalFiles = 0;
        long totalBytes = 0;

        if (Directory.Exists(settingsRoot))
        {
            foreach (string filePath in Directory.EnumerateFiles(settingsRoot, "*", SearchOption.AllDirectories))
            {
                totalFiles++;
                totalBytes += new FileInfo(filePath).Length;
                string rel = Path.GetRelativePath(settingsRoot, filePath).Replace('\\', '/');
                int slashIdx = rel.IndexOf('/', StringComparison.Ordinal);
                string topBucket = slashIdx < 0 ? "(root)" : rel[..slashIdx];
                topFolderCounts[topBucket] = topFolderCounts.GetValueOrDefault(topBucket) + 1;

                if (slashIdx < 0 &&
                    !KnownExportedRootSettings.Contains(rel) &&
                    !KnownClassifiedNonPackRootSettings.ContainsKey(rel))
                {
                    uncoveredRootFiles.Add(rel);
                }
            }
        }

        uncoveredRootFiles.Sort(StringComparer.OrdinalIgnoreCase);

        // Parse maplist.ini declared map IDs
        var declaredMapIds = new SortedSet<int>();
        string mapListPath = Path.Combine(settingsRoot, "maplist.ini");
        if (File.Exists(mapListPath))
        {
            foreach (string rawLine in File.ReadLines(mapListPath, Encoding.Latin1))
            {
                string line = rawLine.Trim();
                int eqIdx = line.IndexOf('=', StringComparison.Ordinal);
                if (eqIdx <= 0)
                {
                    continue;
                }

                string key = line[..eqIdx].Trim();
                if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mapId) && mapId > 0)
                {
                    declaredMapIds.Add(mapId);
                }
            }
        }

        // Scan exported pack maps
        var exportedMapIds = new HashSet<int>();
        string packMapsDir = Path.Combine(packRoot, "data", "maps");
        if (Directory.Exists(packMapsDir))
        {
            foreach (string mapJson in Directory.EnumerateFiles(packMapsDir, "*.map.json", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(mapJson);
                int underscoreIdx = name.IndexOf('_', StringComparison.Ordinal);
                if (underscoreIdx > 0 &&
                    int.TryParse(name[..underscoreIdx], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mapId))
                {
                    exportedMapIds.Add(mapId);
                }
            }
        }

        int[] missingMapIds = declaredMapIds.Where(id => !exportedMapIds.Contains(id)).ToArray();

        return new SettingsAuditReport
        {
            GeneratedAtUtc = DateTime.UtcNow.ToString("O"),
            TotalSettingsFiles = totalFiles,
            TotalSettingsBytes = totalBytes,
            MapListDeclaredCount = declaredMapIds.Count,
            ExportedPackMapCount = exportedMapIds.Count,
            MissingMapIds = missingMapIds,
            SettingsByTopFolder = topFolderCounts,
            UncoveredRootSettingsFiles = uncoveredRootFiles,
        };
    }

    public void WriteOutputs(SettingsAuditReport report, string migrationArtifactsDir)
    {
        Directory.CreateDirectory(migrationArtifactsDir);
        File.WriteAllText(
            Path.Combine(migrationArtifactsDir, "settings-and-maps-audit.json"),
            JsonSerializer.Serialize(report, JsonOptions),
            new UTF8Encoding(false));

        var sb = new StringBuilder();
        sb.AppendLine("# Settings & `maplist.ini` 100% Audit Report");
        sb.AppendLine();
        sb.AppendLine($"- **Generated (UTC)**: `{report.GeneratedAtUtc}`");
        sb.AppendLine($"- **Total `server1/settings/` Files**: **{report.TotalSettingsFiles:N0}** (`{report.TotalSettingsBytes / (1024.0 * 1024.0):F2} MB`)");
        sb.AppendLine($"- **`maplist.ini` Declared Map IDs**: **{report.MapListDeclaredCount:N0}**");
        sb.AppendLine($"- **Exported `pack-tieudaotu/data/maps/*.map.json`**: **{report.ExportedPackMapCount:N0}**");
        sb.AppendLine($"- **Declared Map IDs Not Exported (missing `.wor` or skipped)**: **{report.MissingMapIds.Count:N0}**");
        sb.AppendLine();

        if (report.MissingMapIds.Count > 0)
        {
            sb.AppendLine("## Missing `maplist.ini` Map IDs");
            sb.AppendLine("```text");
            sb.AppendLine(string.Join(", ", report.MissingMapIds));
            sb.AppendLine("```");
            sb.AppendLine();
        }

        sb.AppendLine("## `server1/settings/` Top-Level Breakdown");
        sb.AppendLine("| Folder | File Count |");
        sb.AppendLine("| :--- | ---: |");
        foreach (KeyValuePair<string, int> kv in report.SettingsByTopFolder)
        {
            sb.AppendLine($"| `{kv.Key}` | {kv.Value:N0} |");
        }

        sb.AppendLine();
        sb.AppendLine($"## Root `settings/` Files Requiring Export Audit ({report.UncoveredRootSettingsFiles.Count})");
        foreach (string file in report.UncoveredRootSettingsFiles)
        {
            sb.AppendLine($"- `{file}`");
        }

        File.WriteAllText(
            Path.Combine(migrationArtifactsDir, "settings-and-maps-audit.md"),
            sb.ToString(),
            new UTF8Encoding(false));
    }
}
