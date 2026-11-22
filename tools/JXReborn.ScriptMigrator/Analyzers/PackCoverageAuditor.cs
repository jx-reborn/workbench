using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JXReborn.Packs.TieuDaoTu.Hosting;
using JXReborn.Primitives.Characters;
using JXReborn.ScriptMigrator.Models;

namespace JXReborn.ScriptMigrator.Analyzers;

/// <summary>
/// Audits <c>pack-tieudaotu</c> JSON catalogs and C# scripts against the 100% legacy script manifest
/// to measure exact coverage and enforce zero dangling/unaccounted paths.
/// </summary>
public sealed partial class PackCoverageAuditor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    [GeneratedRegex(@"ScriptCode\s*=>\s*""([^""]+)""", RegexOptions.Compiled)]
    private static partial Regex ScriptCodePropertyRegex();

    public CoverageAuditReport Audit(ScriptInventoryManifest manifest, string packRoot)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(packRoot);

        var knownLegacyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ScriptInventoryEntry entry in manifest.Entries)
        {
            knownLegacyPaths.Add(NormalizeSlashPath(entry.CanonicalScriptPath));
            knownLegacyPaths.Add(NormalizeSlashPath("\\" + entry.Utf8DisplayPath.Replace("server1/", string.Empty, StringComparison.OrdinalIgnoreCase)));
        }

        var resolver = new TieuDaoTuScriptCodeResolver();

        // 1. Collect C# registered ScriptCodes
        var registeredCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string packSrcDir = Path.Combine(packRoot, "src");
        if (Directory.Exists(packSrcDir))
        {
            foreach (string csFile in Directory.EnumerateFiles(packSrcDir, "*.cs", SearchOption.AllDirectories))
            {
                string src = File.ReadAllText(csFile);
                foreach (Match match in ScriptCodePropertyRegex().Matches(src))
                {
                    registeredCodes.Add(match.Groups[1].Value);
                }
            }
        }

        // 2. Ambient dialogs count & resolved scriptPaths
        int ambientDialogCount = 0;
        var ambientDialogPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string ambientPath = Path.Combine(packRoot, "data", "dialogs", "ambient-npcs.dialogs.json");
        if (File.Exists(ambientPath))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(ambientPath));
            JsonElement entriesProp = default;
            bool hasArray = doc.RootElement.ValueKind == JsonValueKind.Array && ((entriesProp = doc.RootElement).ValueKind == JsonValueKind.Array);
            if (!hasArray && doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                hasArray = (doc.RootElement.TryGetProperty("entries", out entriesProp) && entriesProp.ValueKind == JsonValueKind.Array)
                    || (doc.RootElement.TryGetProperty("dialogs", out entriesProp) && entriesProp.ValueKind == JsonValueKind.Array);
            }

            if (hasArray)
            {
                ambientDialogCount = entriesProp.GetArrayLength();
                foreach (JsonElement entry in entriesProp.EnumerateArray())
                {
                    if (entry.TryGetProperty("scriptPaths", out JsonElement pathsProp) && pathsProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement p in pathsProp.EnumerateArray())
                        {
                            if (p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString()))
                            {
                                ambientDialogPaths.Add(NormalizeSlashPath(p.GetString()!));
                            }
                        }
                    }
                }
            }
        }

        // 3. Scan maps/*.map.json & npcs/*.npcs.json
        var npcPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var objPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var portalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var brokenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string mapsDir = Path.Combine(packRoot, "data", "maps");
        if (Directory.Exists(mapsDir))
        {
            foreach (string mapFile in Directory.EnumerateFiles(mapsDir, "*.map.json", SearchOption.TopDirectoryOnly))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(mapFile));
                JsonElement root = doc.RootElement;

                if (root.TryGetProperty("spawnGroups", out JsonElement spawnGroups) && spawnGroups.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement sg in spawnGroups.EnumerateArray())
                    {
                        CollectScriptPath(sg, "scriptPath", npcPaths);
                        if (sg.TryGetProperty("spawns", out JsonElement spawns) && spawns.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement sp in spawns.EnumerateArray())
                            {
                                CollectScriptPath(sp, "scriptPathOverride", npcPaths);
                                CollectScriptPath(sp, "scriptPath", npcPaths);
                            }
                        }
                    }
                }

                if (root.TryGetProperty("mapObjects", out JsonElement mapObjects) && mapObjects.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement obj in mapObjects.EnumerateArray())
                    {
                        CollectScriptPath(obj, "scriptPath", objPaths);
                    }
                }

                if (root.TryGetProperty("portals", out JsonElement portals) && portals.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement portal in portals.EnumerateArray())
                    {
                        CollectScriptPath(portal, "scriptPath", portalPaths);
                    }
                }
            }
        }

        // Global & event spawns
        foreach (string npcFile in new[] { "global-spawns.npcs.json", "event-spawns.npcs.json" })
        {
            string fullPath = Path.Combine(packRoot, "data", "npcs", npcFile);
            if (!File.Exists(fullPath))
            {
                continue;
            }

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(fullPath));
            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement g in prop.Value.EnumerateArray())
                {
                    CollectScriptPath(g, "scriptPath", npcPaths);
                }
            }
        }

        // 4. Scan consumables-and-scripts.items.json
        var activeItemPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string itemsPath = Path.Combine(packRoot, "data", "items", "consumables-and-scripts.items.json");
        if (File.Exists(itemsPath))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(itemsPath));
            if (doc.RootElement.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("scriptUsage", out JsonElement usage) &&
                        usage.ValueKind == JsonValueKind.Object &&
                        usage.TryGetProperty("scriptPath", out JsonElement spProp) &&
                        spProp.ValueKind == JsonValueKind.String)
                    {
                        string? sp = spProp.GetString();
                        if (!string.IsNullOrWhiteSpace(sp) &&
                            !sp.Contains("noscript.lua", StringComparison.OrdinalIgnoreCase))
                        {
                            activeItemPaths.Add(NormalizeSlashPath(sp));
                        }
                    }
                }
            }
        }

        // 5. Evaluate resolution via current TieuDaoTuScriptCodeResolver
        var unresolvedNpc = new List<string>();
        int resolvedNpc = 0;
        foreach (string sp in npcPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (!knownLegacyPaths.Contains(sp))
            {
                brokenPaths.Add(sp);
            }

            string? code = resolver.ResolveNpcScriptCode(EntityKind.DialogNpc, sp, null)
                ?? resolver.ResolveNpcScriptCode(EntityKind.Monster, sp, null);
            if (ambientDialogPaths.Contains(sp) || (!string.IsNullOrWhiteSpace(code) && registeredCodes.Contains(code)))
            {
                resolvedNpc++;
            }
            else
            {
                unresolvedNpc.Add(sp);
            }
        }

        var unresolvedObj = new List<string>();
        int resolvedObj = 0;
        foreach (string sp in objPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (!knownLegacyPaths.Contains(sp))
            {
                brokenPaths.Add(sp);
            }

            string? code = resolver.ResolveMapObjectScriptCode(0, sp);
            if (!string.IsNullOrWhiteSpace(code) && registeredCodes.Contains(code))
            {
                resolvedObj++;
            }
            else
            {
                unresolvedObj.Add(sp);
            }
        }

        var unresolvedItems = new List<string>();
        int resolvedItems = 0;
        foreach (string sp in activeItemPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (!knownLegacyPaths.Contains(sp))
            {
                brokenPaths.Add(sp);
            }

            // Item resolution currently matches ScriptCode directly; track unresolved
            unresolvedItems.Add(sp);
        }

        int totalActiveLegacy = manifest.Server1ActiveFiles + manifest.S3RelayActiveFiles;
        int accountedEstimate = resolvedNpc + resolvedObj + resolvedItems + ambientDialogCount + portalPaths.Count;
        int unaccounted = Math.Max(0, totalActiveLegacy - accountedEstimate);

        return new CoverageAuditReport
        {
            GeneratedAtUtc = DateTime.UtcNow.ToString("O"),
            TotalLegacyActiveScripts = totalActiveLegacy,
            RegisteredCSharpScriptCodes = registeredCodes.Count,
            AmbientDialogEntries = ambientDialogCount,
            UniqueMapNpcScriptPaths = npcPaths.Count,
            ResolvedMapNpcScriptPaths = resolvedNpc,
            UniqueMapObjectScriptPaths = objPaths.Count,
            ResolvedMapObjectScriptPaths = resolvedObj,
            UniqueMapPortalScriptPaths = portalPaths.Count,
            UniqueActiveItemScriptPaths = activeItemPaths.Count,
            ResolvedActiveItemScriptPaths = resolvedItems,
            BrokenScriptPaths = brokenPaths.Count,
            UnaccountedLuaFiles = unaccounted,
            UnresolvedNpcPathsSample = unresolvedNpc.Take(50).ToArray(),
            UnresolvedObjectPathsSample = unresolvedObj.Take(50).ToArray(),
            UnresolvedItemPathsSample = unresolvedItems.Take(50).ToArray(),
            BrokenPathsSample = brokenPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(50).ToArray(),
        };
    }

    public void WriteOutputs(CoverageAuditReport report, string migrationArtifactsDir)
    {
        Directory.CreateDirectory(migrationArtifactsDir);
        File.WriteAllText(
            Path.Combine(migrationArtifactsDir, "coverage-report.json"),
            JsonSerializer.Serialize(report, JsonOptions),
            new UTF8Encoding(false));

        var sb = new StringBuilder();
        sb.AppendLine("# `pack-tieudaotu` Script Coverage & Parity Gate Report");
        sb.AppendLine();
        sb.AppendLine($"- **Generated (UTC)**: `{report.GeneratedAtUtc}`");
        sb.AppendLine($"- **Total Active Legacy Scripts (`server1` + `s3relay`)**: **{report.TotalLegacyActiveScripts:N0}**");
        sb.AppendLine($"- **Registered C# `ScriptCode`s**: **{report.RegisteredCSharpScriptCodes:N0}**");
        sb.AppendLine($"- **Ambient Dialog JSON Entries**: **{report.AmbientDialogEntries:N0}**");
        sb.AppendLine($"- **Map/Global NPC Unique Script Paths**: `{report.ResolvedMapNpcScriptPaths:N0}` / `{report.UniqueMapNpcScriptPaths:N0}` resolved");
        sb.AppendLine($"- **Map Object (`0x72`) Unique Script Paths**: `{report.ResolvedMapObjectScriptPaths:N0}` / `{report.UniqueMapObjectScriptPaths:N0}` resolved");
        sb.AppendLine($"- **Map Portal Unique Script Paths**: `{report.UniqueMapPortalScriptPaths:N0}`");
        sb.AppendLine($"- **Active Item Unique Script Paths (excl. `noscript.lua`)**: `{report.ResolvedActiveItemScriptPaths:N0}` / `{report.UniqueActiveItemScriptPaths:N0}` resolved");
        sb.AppendLine($"- **Broken `scriptPath` Links (missing in legacy corpus)**: **{report.BrokenScriptPaths:N0}**");
        sb.AppendLine($"- **Estimated Remaining Unaccounted Active Lua Files**: **{report.UnaccountedLuaFiles:N0}**");
        sb.AppendLine();

        AppendSample(sb, "Sample Unresolved NPC Script Paths (first 50)", report.UnresolvedNpcPathsSample);
        AppendSample(sb, "Sample Unresolved Map Object Script Paths (first 50)", report.UnresolvedObjectPathsSample);
        AppendSample(sb, "Sample Unresolved Active Item Script Paths (first 50)", report.UnresolvedItemPathsSample);
        if (report.BrokenPathsSample.Count > 0)
        {
            AppendSample(sb, "Sample Broken Script Paths (first 50)", report.BrokenPathsSample);
        }

        File.WriteAllText(
            Path.Combine(migrationArtifactsDir, "coverage-report.md"),
            sb.ToString(),
            new UTF8Encoding(false));
    }

    private static void CollectScriptPath(JsonElement obj, string propName, HashSet<string> target)
    {
        if (obj.TryGetProperty(propName, out JsonElement prop) && prop.ValueKind == JsonValueKind.String)
        {
            string? val = prop.GetString();
            if (!string.IsNullOrWhiteSpace(val))
            {
                target.Add(NormalizeSlashPath(val));
            }
        }
    }

    private static string NormalizeSlashPath(string path)
    {
        string p = path.Trim().Replace('/', '\\');
        if (!p.StartsWith('\\'))
        {
            p = "\\" + p;
        }

        return p;
    }

    private static void AppendSample(StringBuilder sb, string title, IReadOnlyList<string> items)
    {
        sb.AppendLine($"## {title}");
        foreach (string item in items)
        {
            sb.AppendLine($"- `{item}`");
        }

        sb.AppendLine();
    }
}
