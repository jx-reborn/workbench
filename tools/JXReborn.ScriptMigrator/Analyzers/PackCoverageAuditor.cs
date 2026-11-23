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
/// to measure exact, deduplicated file-level coverage and enforce strict scope parity gates.
/// </summary>
public sealed partial class PackCoverageAuditor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    [GeneratedRegex(@"ScriptCode\s*=>\s*(@?)""([^""]+)""", RegexOptions.Compiled)]
    private static partial Regex ScriptCodePropertyRegex();

    [GeneratedRegex(@"\[LegacySource\(\s*(@?)""([^""]+)""\s*,\s*(@?)""([^""]+)""\s*\)\]", RegexOptions.Compiled)]
    private static partial Regex LegacySourceAttributeRegex();

    public CoverageAuditReport Audit(ScriptInventoryManifest manifest, string packRoot, string? scope = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(packRoot);

        string normalizedScope = string.IsNullOrWhiteSpace(scope) ? "all" : scope.Trim().ToLowerInvariant();

        // Map both raw canonical and UTF-8 display script paths -> active ScriptInventoryEntry
        var activeEntries = manifest.Entries.Where(static e => !e.IsExcludedSimBot).ToList();
        var entryByNormalizedPath = new Dictionary<string, ScriptInventoryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (ScriptInventoryEntry entry in activeEntries)
        {
            string rawCanonical = NormalizeSlashPath(entry.CanonicalScriptPath);
            entryByNormalizedPath.TryAdd(rawCanonical, entry);

            string utf8Script = ToScriptPathFromUtf8Display(entry.Utf8DisplayPath);
            if (!string.IsNullOrWhiteSpace(utf8Script))
            {
                entryByNormalizedPath.TryAdd(utf8Script, entry);
            }
        }

        var resolver = new TieuDaoTuScriptCodeResolver();

        // 1. Collect C# registered ScriptCodes & [LegacySource] attributes (handling both regular and @"..." verbatim strings)
        var registeredCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var legacySourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string packSrcDir = Path.Combine(packRoot, "src");
        if (Directory.Exists(packSrcDir))
        {
            foreach (string csFile in Directory.EnumerateFiles(packSrcDir, "*.cs", SearchOption.AllDirectories))
            {
                string src = File.ReadAllText(csFile);
                foreach (Match match in ScriptCodePropertyRegex().Matches(src))
                {
                    bool isVerbatim = match.Groups[1].Value.Length > 0;
                    string code = UnescapeCSharpString(match.Groups[2].Value, isVerbatim);
                    registeredCodes.Add(code);
                    if (code.StartsWith('\\') || code.StartsWith('/'))
                    {
                        registeredCodes.Add(NormalizeSlashPath(code));
                    }
                }

                foreach (Match match in LegacySourceAttributeRegex().Matches(src))
                {
                    bool isVerbatim = match.Groups[1].Value.Length > 0;
                    string rawPath = UnescapeCSharpString(match.Groups[2].Value, isVerbatim);
                    string norm = NormalizeSlashPath(rawPath);
                    legacySourcePaths.Add(norm);
                    string utf8Norm = LegacyScriptPathNormalizer.NormalizeToUtf8(norm);
                    if (!string.IsNullOrWhiteSpace(utf8Norm))
                    {
                        legacySourcePaths.Add(utf8Norm);
                    }
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
                                string norm = NormalizeSlashPath(p.GetString()!);
                                ambientDialogPaths.Add(norm);
                                ambientDialogPaths.Add(LegacyScriptPathNormalizer.NormalizeToUtf8(norm));
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
        var deadLegacyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                        CollectScriptPath(sg, "scriptPath", npcPaths, deadLegacyPaths);
                        if (sg.TryGetProperty("spawns", out JsonElement spawns) && spawns.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement sp in spawns.EnumerateArray())
                            {
                                CollectScriptPath(sp, "scriptPathOverride", npcPaths, deadLegacyPaths);
                                CollectScriptPath(sp, "scriptPath", npcPaths, deadLegacyPaths);
                            }
                        }
                    }
                }

                if (root.TryGetProperty("mapObjects", out JsonElement mapObjects) && mapObjects.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement obj in mapObjects.EnumerateArray())
                    {
                        CollectScriptPath(obj, "scriptPath", objPaths, deadLegacyPaths);
                    }
                }

                if (root.TryGetProperty("portals", out JsonElement portals) && portals.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement portal in portals.EnumerateArray())
                    {
                        CollectScriptPath(portal, "scriptPath", portalPaths, deadLegacyPaths);
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

                bool isPortalProp = string.Equals(prop.Name, "worldPortals", StringComparison.OrdinalIgnoreCase);
                foreach (JsonElement g in prop.Value.EnumerateArray())
                {
                    CollectScriptPath(g, "scriptPath", isPortalProp ? portalPaths : npcPaths, deadLegacyPaths);
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
                            CollectScriptPath(usage, "scriptPath", activeItemPaths, deadLegacyPaths);
                        }
                    }
                }
            }
        }

        // 5. Evaluate resolution via current TieuDaoTuScriptCodeResolver & exact deduplicated file accounting
        var accountedRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAccountPath(string scriptPath)
        {
            if (entryByNormalizedPath.TryGetValue(scriptPath, out ScriptInventoryEntry? hit))
            {
                accountedRelativePaths.Add(hit.RelativePath);
                return;
            }

            string utf8 = LegacyScriptPathNormalizer.NormalizeToUtf8(scriptPath);
            if (entryByNormalizedPath.TryGetValue(utf8, out hit))
            {
                accountedRelativePaths.Add(hit.RelativePath);
            }
        }

        bool ExistsInLegacy(string scriptPath)
        {
            if (entryByNormalizedPath.ContainsKey(scriptPath))
            {
                return true;
            }

            string utf8 = LegacyScriptPathNormalizer.NormalizeToUtf8(scriptPath);
            return entryByNormalizedPath.ContainsKey(utf8);
        }

        // Account all [LegacySource] attributed C# scripts directly against real manifest entries
        int attributedExistingCount = 0;
        var seenAttributedRelative = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string srcPath in legacySourcePaths)
        {
            if (entryByNormalizedPath.TryGetValue(srcPath, out ScriptInventoryEntry? hit) &&
                seenAttributedRelative.Add(hit.RelativePath))
            {
                attributedExistingCount++;
                accountedRelativePaths.Add(hit.RelativePath);
            }
        }

        // Account all NPC ambient-dialog JSON entries (excluding non-NPC trap/obj/item paths that require their own handlers)
        foreach (string ambPath in ambientDialogPaths)
        {
            if (entryByNormalizedPath.TryGetValue(ambPath, out ScriptInventoryEntry? hit) &&
                !IsTrapOrObjectOrItemEntry(hit))
            {
                accountedRelativePaths.Add(hit.RelativePath);
            }
        }

        var unresolvedNpc = new List<string>();
        int resolvedNpc = 0;
        foreach (string sp in npcPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (!ExistsInLegacy(sp))
            {
                brokenPaths.Add(sp);
            }

            string utf8Sp = LegacyScriptPathNormalizer.NormalizeToUtf8(sp);
            string? code = resolver.ResolveNpcScriptCode(EntityKind.DialogNpc, sp, null)
                ?? resolver.ResolveNpcScriptCode(EntityKind.Monster, sp, null);
            bool isResolved = ambientDialogPaths.Contains(sp) ||
                              ambientDialogPaths.Contains(utf8Sp) ||
                              legacySourcePaths.Contains(sp) ||
                              legacySourcePaths.Contains(utf8Sp) ||
                              (!string.IsNullOrWhiteSpace(code) && registeredCodes.Contains(code));
            if (isResolved)
            {
                resolvedNpc++;
                TryAccountPath(sp);
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
            if (!ExistsInLegacy(sp))
            {
                brokenPaths.Add(sp);
            }

            string utf8Sp = LegacyScriptPathNormalizer.NormalizeToUtf8(sp);
            string? code = resolver.ResolveMapObjectScriptCode(0, sp);
            bool isResolved = legacySourcePaths.Contains(sp) ||
                              legacySourcePaths.Contains(utf8Sp) ||
                              (!string.IsNullOrWhiteSpace(code) && registeredCodes.Contains(code));
            if (isResolved)
            {
                resolvedObj++;
                TryAccountPath(sp);
            }
            else
            {
                unresolvedObj.Add(sp);
            }
        }

        var unresolvedPortals = new List<string>();
        int resolvedPortals = 0;
        foreach (string sp in portalPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (!ExistsInLegacy(sp))
            {
                brokenPaths.Add(sp);
            }

            string utf8Sp = LegacyScriptPathNormalizer.NormalizeToUtf8(sp);
            string? code = resolver.ResolveTrapScriptCode(0, 0, sp);
            bool isResolved = legacySourcePaths.Contains(sp) ||
                              legacySourcePaths.Contains(utf8Sp) ||
                              (!string.IsNullOrWhiteSpace(code) && registeredCodes.Contains(code));
            if (isResolved)
            {
                resolvedPortals++;
                TryAccountPath(sp);
            }
            else
            {
                unresolvedPortals.Add(sp);
            }
        }

        var unresolvedItems = new List<string>();
        int resolvedItems = 0;
        foreach (string sp in activeItemPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (!ExistsInLegacy(sp))
            {
                brokenPaths.Add(sp);
            }

            string utf8Sp = LegacyScriptPathNormalizer.NormalizeToUtf8(sp);
            if (registeredCodes.Contains(sp) ||
                registeredCodes.Contains(utf8Sp) ||
                legacySourcePaths.Contains(sp) ||
                legacySourcePaths.Contains(utf8Sp))
            {
                resolvedItems++;
                TryAccountPath(sp);
            }
            else
            {
                unresolvedItems.Add(sp);
            }
        }

        // 6. Account for skill formula scripts already exported into data/skills/*.skills.json
        int accountedSkillFormulas = 0;
        string skillsDir = Path.Combine(packRoot, "data", "skills");
        if (Directory.Exists(skillsDir) &&
            Directory.EnumerateFiles(skillsDir, "*.skills.json", SearchOption.TopDirectoryOnly).Any())
        {
            foreach (ScriptInventoryEntry entry in activeEntries)
            {
                if (entry.RelativePath.Replace('\\', '/').StartsWith("server1/script/skill/", StringComparison.OrdinalIgnoreCase))
                {
                    accountedSkillFormulas++;
                    accountedRelativePaths.Add(entry.RelativePath);
                }
            }
        }

        // 7. Account for NPC level scripts verified by sourceSha256 in data/npcs/level-scripts.npcs.json
        int accountedNpcLevelScripts = 0;
        string npcLevelScriptsFile = Path.Combine(packRoot, "data", "npcs", "level-scripts.npcs.json");
        if (File.Exists(npcLevelScriptsFile))
        {
            var entryByRelPath = activeEntries.ToDictionary(
                static e => e.RelativePath.Replace('\\', '/'),
                StringComparer.OrdinalIgnoreCase);

            using JsonDocument lvlDoc = JsonDocument.Parse(File.ReadAllText(npcLevelScriptsFile));
            if (lvlDoc.RootElement.TryGetProperty("npcLevelScripts", out JsonElement lvlArr) &&
                lvlArr.ValueKind == JsonValueKind.Array)
            {
                var seenLvlRel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (JsonElement item in lvlArr.EnumerateArray())
                {
                    string? legacyRel = item.TryGetProperty("legacyRelativePath", out JsonElement rp) && rp.ValueKind == JsonValueKind.String
                        ? rp.GetString()
                        : null;
                    string? sha = item.TryGetProperty("sourceSha256", out JsonElement sh) && sh.ValueKind == JsonValueKind.String
                        ? sh.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(legacyRel) || string.IsNullOrWhiteSpace(sha))
                    {
                        continue;
                    }

                    string fullRel = "server1/" + legacyRel.Replace('\\', '/').TrimStart('/');
                    if (entryByRelPath.TryGetValue(fullRel, out ScriptInventoryEntry? hit) &&
                        string.Equals(hit.Sha256, sha, StringComparison.OrdinalIgnoreCase) &&
                        seenLvlRel.Add(hit.RelativePath))
                    {
                        accountedNpcLevelScripts++;
                        accountedRelativePaths.Add(hit.RelativePath);
                    }
                }
            }
        }

        int totalActiveLegacy = activeEntries.Count;
        int accountedTotal = accountedRelativePaths.Count;
        int unaccounted = Math.Max(0, totalActiveLegacy - accountedTotal);

        List<ScriptInventoryEntry> scopedEntries = activeEntries
            .Where(e => MatchesScope(e, normalizedScope))
            .ToList();
        int scopedAccounted = scopedEntries.Count(e => accountedRelativePaths.Contains(e.RelativePath));
        int scopedUnaccounted = Math.Max(0, scopedEntries.Count - scopedAccounted);

        return new CoverageAuditReport
        {
            GeneratedAtUtc = DateTime.UtcNow.ToString("O"),
            ScopeFilter = normalizedScope,
            TotalLegacyActiveScripts = totalActiveLegacy,
            AccountedLuaFiles = accountedTotal,
            RegisteredCSharpScriptCodes = registeredCodes.Count,
            LegacySourceAttributedScripts = attributedExistingCount,
            AccountedSkillFormulaScripts = accountedSkillFormulas,
            AccountedNpcLevelScripts = accountedNpcLevelScripts,
            AmbientDialogEntries = ambientDialogCount,
            UniqueMapNpcScriptPaths = npcPaths.Count,
            ResolvedMapNpcScriptPaths = resolvedNpc,
            UniqueMapObjectScriptPaths = objPaths.Count,
            ResolvedMapObjectScriptPaths = resolvedObj,
            UniqueMapPortalScriptPaths = portalPaths.Count,
            ResolvedMapPortalScriptPaths = resolvedPortals,
            UniqueActiveItemScriptPaths = activeItemPaths.Count,
            ResolvedActiveItemScriptPaths = resolvedItems,
            BrokenScriptPaths = brokenPaths.Count,
            DeadLegacyCorpusScriptPaths = deadLegacyPaths.Count,
            UnaccountedLuaFiles = unaccounted,
            ScopedTotalLegacyActiveScripts = scopedEntries.Count,
            ScopedAccountedLuaFiles = scopedAccounted,
            ScopedUnaccountedLuaFiles = scopedUnaccounted,
            UnresolvedNpcPathsSample = unresolvedNpc.Take(50).ToArray(),
            UnresolvedObjectPathsSample = unresolvedObj.Take(50).ToArray(),
            UnresolvedPortalPathsSample = unresolvedPortals.Take(50).ToArray(),
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
        sb.AppendLine($"- **Scope Filter**: `{report.ScopeFilter}` (scoped accounted: `{report.ScopedAccountedLuaFiles:N0}` / `{report.ScopedTotalLegacyActiveScripts:N0}`, scoped unaccounted: `{report.ScopedUnaccountedLuaFiles:N0}`)");
        sb.AppendLine($"- **Total Active Legacy Scripts (`server1` + `s3relay`)**: **{report.TotalLegacyActiveScripts:N0}**");
        sb.AppendLine($"- **Exact Deduplicated Accounted Active Lua Files**: **{report.AccountedLuaFiles:N0}**");
        sb.AppendLine($"- **Registered C# `ScriptCode`s**: **{report.RegisteredCSharpScriptCodes:N0}** (`[LegacySource]` attributed files: `{report.LegacySourceAttributedScripts:N0}`)");
        sb.AppendLine($"- **Ambient Dialog JSON Entries**: **{report.AmbientDialogEntries:N0}**");
        sb.AppendLine($"- **Skill Formula Scripts Accounted by `data/skills/*.skills.json`**: **{report.AccountedSkillFormulaScripts:N0}**");
        sb.AppendLine($"- **NPC Level Scripts Accounted by `data/npcs/level-scripts.npcs.json`**: **{report.AccountedNpcLevelScripts:N0}**");
        sb.AppendLine($"- **Map/Global NPC Unique Script Paths**: `{report.ResolvedMapNpcScriptPaths:N0}` / `{report.UniqueMapNpcScriptPaths:N0}` resolved");
        sb.AppendLine($"- **Map Object (`0x72`) Unique Script Paths**: `{report.ResolvedMapObjectScriptPaths:N0}` / `{report.UniqueMapObjectScriptPaths:N0}` resolved");
        sb.AppendLine($"- **Map Portal/Trap Unique Script Paths**: `{report.ResolvedMapPortalScriptPaths:N0}` / `{report.UniqueMapPortalScriptPaths:N0}` resolved");
        sb.AppendLine($"- **Active Item Unique Script Paths (excl. `noscript.lua` & dead legacy refs)**: `{report.ResolvedActiveItemScriptPaths:N0}` / `{report.UniqueActiveItemScriptPaths:N0}` resolved");
        sb.AppendLine($"- **Broken `scriptPath` Links (exporter/encoding errors)**: **{report.BrokenScriptPaths:N0}**");
        sb.AppendLine($"- **Dead Legacy Corpus `scriptPath` References (`missingInLegacyCorpus` / `excludedSimBot`)**: **{report.DeadLegacyCorpusScriptPaths:N0}**");
        sb.AppendLine($"- **Exact Remaining Unaccounted Active Lua Files**: **{report.UnaccountedLuaFiles:N0}**");
        sb.AppendLine();

        AppendSample(sb, "Sample Unresolved NPC Script Paths (first 50)", report.UnresolvedNpcPathsSample);
        AppendSample(sb, "Sample Unresolved Map Object Script Paths (first 50)", report.UnresolvedObjectPathsSample);
        if (report.UnresolvedPortalPathsSample.Count > 0)
        {
            AppendSample(sb, "Sample Unresolved Map Portal/Trap Script Paths (first 50)", report.UnresolvedPortalPathsSample);
        }

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

    private static bool MatchesScope(ScriptInventoryEntry entry, string scope)
    {
        if (scope == "all")
        {
            return true;
        }

        string up = entry.Utf8DisplayPath.Replace('\\', '/').ToLowerInvariant();
        string rp = entry.RelativePath.Replace('\\', '/').ToLowerInvariant();

        return scope switch
        {
            "traps" => up.Contains("/trap/", StringComparison.Ordinal) || up.Contains("/连接trap/", StringComparison.Ordinal),
            "objects" => up.Contains("/obj/", StringComparison.Ordinal) || up.Contains("/地图obj/", StringComparison.Ordinal),
            "items" => rp.StartsWith("server1/script/item/", StringComparison.Ordinal),
            "monsters" => entry.DefinedFunctions.Contains("OnDeath") && !entry.DefinedFunctions.Contains("main"),
            "npcs" => up.Contains("/npc/", StringComparison.Ordinal) || up.Contains("/职能npc/", StringComparison.Ordinal) || up.Contains("/路人npc/", StringComparison.Ordinal),
            "bucket1" => entry.Bucket == 1,
            "bucket2" => entry.Bucket == 2,
            "bucket3" => entry.Bucket == 3,
            "bucket4" => entry.Bucket == 4,
            "bucket5" => entry.Bucket == 5,
            "bucket6" => entry.Bucket == 6,
            _ => true,
        };
    }

    private static bool IsTrapOrObjectOrItemEntry(ScriptInventoryEntry entry)
    {
        string up = entry.Utf8DisplayPath.Replace('\\', '/').ToLowerInvariant();
        string rp = entry.RelativePath.Replace('\\', '/').ToLowerInvariant();
        return up.Contains("/trap/", StringComparison.Ordinal)
            || up.Contains("/连接trap/", StringComparison.Ordinal)
            || up.Contains("/obj/", StringComparison.Ordinal)
            || up.Contains("/地图obj/", StringComparison.Ordinal)
            || rp.StartsWith("server1/script/item/", StringComparison.Ordinal);
    }

    private static string UnescapeCSharpString(string rawValue, bool isVerbatim)
    {
        if (isVerbatim)
        {
            return rawValue.Replace("\"\"", "\"", StringComparison.Ordinal);
        }

        return rawValue
            .Replace("\\\\", "\\", StringComparison.Ordinal)
            .Replace("\\\"", "\"", StringComparison.Ordinal);
    }

    private static string ToScriptPathFromUtf8Display(string utf8DisplayPath)
    {
        if (string.IsNullOrWhiteSpace(utf8DisplayPath))
        {
            return string.Empty;
        }

        string p = utf8DisplayPath.Trim().Replace('/', '\\');
        if (p.StartsWith("server1\\", StringComparison.OrdinalIgnoreCase))
        {
            p = p["server1\\".Length..];
        }
        else if (p.StartsWith("gateway\\s3relay\\", StringComparison.OrdinalIgnoreCase))
        {
            p = p["gateway\\s3relay\\".Length..];
        }

        return NormalizeSlashPath(p);
    }

    private static void CollectScriptPath(
        JsonElement obj,
        string propName,
        HashSet<string> target,
        HashSet<string> deadLegacyPaths)
    {
        if (obj.TryGetProperty(propName, out JsonElement prop) && prop.ValueKind == JsonValueKind.String)
        {
            string? val = prop.GetString();
            if (!string.IsNullOrWhiteSpace(val))
            {
                string norm = NormalizeSlashPath(val);
                if (obj.TryGetProperty("legacyScriptStatus", out JsonElement statusProp) &&
                    statusProp.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(statusProp.GetString()))
                {
                    deadLegacyPaths.Add(norm);
                    return;
                }

                target.Add(norm);
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
