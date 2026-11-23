using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using JXReborn.Packs.TieuDaoTu.Hosting;
using JXReborn.ScriptMigrator.Ast;
using JXReborn.ScriptMigrator.Models;

namespace JXReborn.ScriptMigrator.Generators;

/// <summary>
/// Deterministic, strictly fail-closed Lua 4.0 AST -> C# 14 code generator for <c>pack-tieudaotu</c>.
/// Converts verified traps, map objects, items, monster death hooks, and simple NPC dialogs into
/// strongly-typed C# classes with full <c>[LegacySource]</c> SHA-256 provenance, routing all
/// unsupported or unsafe scripts into <c>manual-migration-queue.md</c>.
/// </summary>
public sealed partial class LuaToCSharpGenerator
{
    private static readonly HashSet<string> ReplaceableGenericObjectCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "obj-map-signboard",
        "obj-storage-box",
        "obj-town-portal",
    };

    [GeneratedRegex(@"\[LegacySource\(\s*(@?)""([^""]+)""\s*,\s*(@?)""([^""]+)""\s*\)\]", RegexOptions.Compiled)]
    private static partial Regex LegacySourceAttrRegex();

    public ScriptGenerationResult Generate(
        ScriptInventoryManifest manifest,
        string legacyBinRoot,
        string packRoot,
        string migrationArtifactsDir,
        string kindFilter = "all")
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyBinRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(packRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationArtifactsDir);

        string normalizedFilter = string.IsNullOrWhiteSpace(kindFilter) ? "all" : kindFilter.Trim().ToLowerInvariant();
        HashSet<GeneratedScriptKind> targetKinds = ResolveTargetKinds(normalizedFilter);

        // 1. Load pack references & EventItem DetailType -> itemCode table
        Dictionary<int, string> eventItemCodes = LoadEventItemCodeMap(packRoot);
        (HashSet<string> mapTraps, HashSet<string> mapObjs, HashSet<string> mapNpcs, HashSet<string> activeItems) = LoadPackReferencedPaths(packRoot);
        HashSet<string> handcraftedOwnedPaths = LoadHandcraftedOwnedPaths(packRoot);
        HashSet<string> ambientNpcPaths = LoadAmbientNpcPaths(packRoot);

        var parser = new Lua4AstParser();
        var validator = new LuaScriptSemanticValidator(eventItemCodes);
        var compiled = new List<CompiledLuaScript>();
        var manualQueue = new List<ManualMigrationQueueItem>();

        int evaluated = 0;
        int skippedOwned = 0;

        bool hasExportedNpcLevelScripts = File.Exists(Path.Combine(packRoot, "data", "npcs", "level-scripts.npcs.json"));

        foreach (ScriptInventoryEntry entry in manifest.Entries)
        {
            if (entry.IsExcludedSimBot)
            {
                continue;
            }

            string utf8ScriptPath = ToUtf8ScriptPath(entry.Utf8DisplayPath);
            string normalizedCanonical = LegacyScriptPathNormalizer.NormalizeToUtf8(entry.CanonicalScriptPath);
            string relSlash = entry.RelativePath.Replace('\\', '/');

            // Skill formulas and NPC level scripts are already accounted by data/skills/*.skills.json & data/npcs/level-scripts.npcs.json
            if (relSlash.StartsWith("server1/script/skill/", StringComparison.OrdinalIgnoreCase) ||
                (hasExportedNpcLevelScripts && relSlash.StartsWith("server1/script/npclevelscript/", StringComparison.OrdinalIgnoreCase)))
            {
                skippedOwned++;
                continue;
            }

            GeneratedScriptKind kind = ClassifyKind(entry, utf8ScriptPath, mapTraps, mapObjs, mapNpcs, activeItems);

            // Already owned by handcrafted C# scripts?
            if (handcraftedOwnedPaths.Contains(utf8ScriptPath) || handcraftedOwnedPaths.Contains(normalizedCanonical))
            {
                skippedOwned++;
                continue;
            }

            // Pure static NPC dialogs already served by AmbientDialogCatalog (ONLY for NPC kind;
            // traps/objects/items MUST NOT be skipped because AmbientLoreDialogScript does not dispatch them!)
            if (kind == GeneratedScriptKind.Npc &&
                (ambientNpcPaths.Contains(utf8ScriptPath) || ambientNpcPaths.Contains(normalizedCanonical)))
            {
                skippedOwned++;
                continue;
            }

            // S3Relay or non-targeted scripts go cleanly to manual queue when running `all`
            if (entry.Scope == LegacyScriptScope.S3Relay)
            {
                if (normalizedFilter == "all")
                {
                    manualQueue.Add(new ManualMigrationQueueItem(
                        entry.RelativePath,
                        utf8ScriptPath,
                        entry.Sha256,
                        entry.Bucket,
                        entry.BucketName,
                        GeneratedScriptKind.Other,
                        ["scope:s3relay-scheduled-event"]));
                }

                continue;
            }

            if (!targetKinds.Contains(kind))
            {
                if (normalizedFilter == "all")
                {
                    manualQueue.Add(new ManualMigrationQueueItem(
                        entry.RelativePath,
                        utf8ScriptPath,
                        entry.Sha256,
                        entry.Bucket,
                        entry.BucketName,
                        kind,
                        ["unclassified-or-library-script"]));
                }

                continue;
            }

            evaluated++;

            string fullPath = Path.Combine(legacyBinRoot, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                manualQueue.Add(new ManualMigrationQueueItem(
                    entry.RelativePath,
                    utf8ScriptPath,
                    entry.Sha256,
                    entry.Bucket,
                    entry.BucketName,
                    kind,
                    ["missing-source-file"]));
                continue;
            }

            byte[] rawBytes = File.ReadAllBytes(fullPath);
            LuaParseResult parseResult = parser.Parse(rawBytes);
            if (!parseResult.IsSuccess || parseResult.Program is null)
            {
                manualQueue.Add(new ManualMigrationQueueItem(
                    entry.RelativePath,
                    utf8ScriptPath,
                    entry.Sha256,
                    entry.Bucket,
                    entry.BucketName,
                    kind,
                    [parseResult.FailureReason ?? "parse-failed"]));
                continue;
            }

            (CompiledLuaScript? validatedScript, IReadOnlyList<string> reasons) = validator.Validate(entry, utf8ScriptPath, kind, parseResult.Program);
            if (validatedScript is null || reasons.Count > 0)
            {
                manualQueue.Add(new ManualMigrationQueueItem(
                    entry.RelativePath,
                    utf8ScriptPath,
                    entry.Sha256,
                    entry.Bucket,
                    entry.BucketName,
                    kind,
                    reasons.Count > 0 ? reasons : ["validation-failed"]));
                continue;
            }

            compiled.Add(validatedScript);
        }

        // Deduplicate by lowercase UTF-8 path (keep deterministic first by RelativePath)
        var dedupedCompiled = new List<CompiledLuaScript>(compiled.Count);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CompiledLuaScript s in compiled.OrderBy(static x => x.Entry.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            if (seenPaths.Add(s.Utf8ScriptPath))
            {
                dedupedCompiled.Add(s);
            }
        }

        string generatedDir = Path.Combine(packRoot, "src", "JXReborn.Packs.TieuDaoTu", "Scripts", "Generated");
        var emitter = new LuaCSharpCodeEmitter(eventItemCodes);
        IReadOnlyList<string> writtenFiles = emitter.EmitAll(generatedDir, dedupedCompiled);

        var result = new ScriptGenerationResult
        {
            KindFilter = normalizedFilter,
            TotalCandidatesEvaluated = evaluated,
            GeneratedScriptCount = dedupedCompiled.Count,
            GeneratedTrapCount = dedupedCompiled.Count(static s => s.Kind == GeneratedScriptKind.Trap),
            GeneratedObjectCount = dedupedCompiled.Count(static s => s.Kind == GeneratedScriptKind.Object),
            GeneratedItemCount = dedupedCompiled.Count(static s => s.Kind == GeneratedScriptKind.Item),
            GeneratedMonsterCount = dedupedCompiled.Count(static s => s.Kind == GeneratedScriptKind.Monster),
            GeneratedNpcCount = dedupedCompiled.Count(static s => s.Kind == GeneratedScriptKind.Npc),
            SkippedAlreadyOwnedCount = skippedOwned,
            FailedClosedCount = manualQueue.Count,
            WrittenCSharpFiles = writtenFiles,
            ManualQueue = manualQueue,
        };

        var queueWriter = new ManualMigrationQueueWriter();
        queueWriter.WriteOutputs(result, migrationArtifactsDir);

        return result;
    }

    private static HashSet<GeneratedScriptKind> ResolveTargetKinds(string filter) =>
        filter switch
        {
            "traps" => [GeneratedScriptKind.Trap],
            "objects" => [GeneratedScriptKind.Object],
            "items" => [GeneratedScriptKind.Item],
            "monsters" => [GeneratedScriptKind.Monster],
            "npcs" => [GeneratedScriptKind.Npc],
            _ => [GeneratedScriptKind.Trap, GeneratedScriptKind.Object, GeneratedScriptKind.Item, GeneratedScriptKind.Monster, GeneratedScriptKind.Npc],
        };

    private static GeneratedScriptKind ClassifyKind(
        ScriptInventoryEntry entry,
        string utf8ScriptPath,
        HashSet<string> mapTraps,
        HashSet<string> mapObjs,
        HashSet<string> mapNpcs,
        HashSet<string> activeItems)
    {
        string lowerUtf8 = utf8ScriptPath.Replace('\\', '/').ToLowerInvariant();
        string lowerRel = entry.RelativePath.Replace('\\', '/').ToLowerInvariant();

        if (lowerUtf8.Contains("/trap/", StringComparison.Ordinal) ||
            lowerUtf8.Contains("/连接trap/", StringComparison.Ordinal) ||
            mapTraps.Contains(utf8ScriptPath))
        {
            return GeneratedScriptKind.Trap;
        }

        if (lowerUtf8.Contains("/obj/", StringComparison.Ordinal) ||
            lowerUtf8.Contains("/地图obj/", StringComparison.Ordinal) ||
            mapObjs.Contains(utf8ScriptPath))
        {
            return GeneratedScriptKind.Object;
        }

        if (lowerRel.StartsWith("server1/script/item/", StringComparison.Ordinal) ||
            activeItems.Contains(utf8ScriptPath))
        {
            return GeneratedScriptKind.Item;
        }

        bool hasOnDeath = entry.DefinedFunctions.Contains("OnDeath", StringComparer.Ordinal);
        bool hasMain = entry.DefinedFunctions.Contains("main", StringComparer.Ordinal);

        if (hasOnDeath && !hasMain)
        {
            // Player death callbacks registered via SetDeathScript (e.g. playerdeath.lua)
            // are NOT monster INpcCombatScript handlers; fail closed to manual migration queue.
            string fileName = Path.GetFileName(lowerUtf8);
            if (fileName.Contains("playerdeath", StringComparison.Ordinal))
            {
                return GeneratedScriptKind.Other;
            }

            return GeneratedScriptKind.Monster;
        }

        if (hasMain &&
            (lowerUtf8.Contains("/npc/", StringComparison.Ordinal) ||
             lowerUtf8.Contains("/职能npc/", StringComparison.Ordinal) ||
             lowerUtf8.Contains("/路人npc/", StringComparison.Ordinal) ||
             lowerUtf8.Contains("/对话npc/", StringComparison.Ordinal) ||
             mapNpcs.Contains(utf8ScriptPath)))
        {
            return GeneratedScriptKind.Npc;
        }

        return GeneratedScriptKind.Other;
    }

    private static Dictionary<int, string> LoadEventItemCodeMap(string packRoot)
    {
        var map = new Dictionary<int, string>();
        string itemsPath = Path.Combine(packRoot, "data", "items", "consumables-and-scripts.items.json");
        if (!File.Exists(itemsPath))
        {
            return map;
        }

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(itemsPath));
        if (!doc.RootElement.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
        {
            return map;
        }

        foreach (JsonElement item in items.EnumerateArray())
        {
            int genre = item.TryGetProperty("genre", out JsonElement g) && g.ValueKind == JsonValueKind.Number ? g.GetInt32() : -1;
            if (genre != 4)
            {
                continue;
            }

            int detail = item.TryGetProperty("detailType", out JsonElement d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : -1;
            int part = item.TryGetProperty("particularType", out JsonElement p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
            string? code = item.TryGetProperty("code", out JsonElement c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;

            if (detail >= 0 && part == 0 && !string.IsNullOrWhiteSpace(code))
            {
                map.TryAdd(detail, code);
            }
        }

        return map;
    }

    private static (HashSet<string> Traps, HashSet<string> Objs, HashSet<string> Npcs, HashSet<string> Items) LoadPackReferencedPaths(string packRoot)
    {
        var traps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var objs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var npcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static void AddNormalized(JsonElement el, string propName, HashSet<string> set)
        {
            if (el.TryGetProperty("legacyScriptStatus", out JsonElement statusEl) &&
                statusEl.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(statusEl.GetString()))
            {
                return;
            }

            if (el.TryGetProperty(propName, out JsonElement p) && p.ValueKind == JsonValueKind.String)
            {
                string? raw = p.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    set.Add(LegacyScriptPathNormalizer.NormalizeToUtf8(raw));
                }
            }
        }

        string mapsDir = Path.Combine(packRoot, "data", "maps");
        if (Directory.Exists(mapsDir))
        {
            foreach (string mapFile in Directory.EnumerateFiles(mapsDir, "*.map.json", SearchOption.TopDirectoryOnly))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(mapFile));
                JsonElement root = doc.RootElement;

                if (root.TryGetProperty("portals", out JsonElement portals) && portals.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement portal in portals.EnumerateArray())
                    {
                        AddNormalized(portal, "scriptPath", traps);
                    }
                }

                if (root.TryGetProperty("mapObjects", out JsonElement mapObjects) && mapObjects.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement obj in mapObjects.EnumerateArray())
                    {
                        AddNormalized(obj, "scriptPath", objs);
                    }
                }

                if (root.TryGetProperty("spawnGroups", out JsonElement sgs) && sgs.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement sg in sgs.EnumerateArray())
                    {
                        AddNormalized(sg, "scriptPath", npcs);
                        if (sg.TryGetProperty("spawns", out JsonElement spawns) && spawns.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement sp in spawns.EnumerateArray())
                            {
                                AddNormalized(sp, "scriptPath", npcs);
                                AddNormalized(sp, "scriptPathOverride", npcs);
                            }
                        }
                    }
                }
            }
        }

        foreach (string npcFile in new[] { "global-spawns.npcs.json", "event-spawns.npcs.json" })
        {
            string full = Path.Combine(packRoot, "data", "npcs", npcFile);
            if (!File.Exists(full))
            {
                continue;
            }

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(full));
            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                bool isWorldPortal = string.Equals(prop.Name, "worldPortals", StringComparison.OrdinalIgnoreCase);
                foreach (JsonElement g in prop.Value.EnumerateArray())
                {
                    AddNormalized(g, "scriptPath", isWorldPortal ? traps : npcs);
                }
            }
        }

        string itemsPath = Path.Combine(packRoot, "data", "items", "consumables-and-scripts.items.json");
        if (File.Exists(itemsPath))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(itemsPath));
            if (doc.RootElement.TryGetProperty("items", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement it in arr.EnumerateArray())
                {
                    if (it.TryGetProperty("scriptUsage", out JsonElement su) &&
                        su.ValueKind == JsonValueKind.Object &&
                        su.TryGetProperty("scriptPath", out JsonElement sp) &&
                        sp.ValueKind == JsonValueKind.String)
                    {
                        string? raw = sp.GetString();
                        if (!string.IsNullOrWhiteSpace(raw) && !raw.Contains("noscript.lua", StringComparison.OrdinalIgnoreCase))
                        {
                            AddNormalized(su, "scriptPath", items);
                        }
                    }
                }
            }
        }

        return (traps, objs, npcs, items);
    }

    private static HashSet<string> LoadHandcraftedOwnedPaths(string packRoot)
    {
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Scan non-generated C# script files for [LegacySource] attributes
        string scriptsDir = Path.Combine(packRoot, "src", "JXReborn.Packs.TieuDaoTu", "Scripts");
        string generatedSubDir = Path.Combine(scriptsDir, "Generated") + Path.DirectorySeparatorChar;
        if (Directory.Exists(scriptsDir))
        {
            foreach (string csFile in Directory.EnumerateFiles(scriptsDir, "*.cs", SearchOption.AllDirectories))
            {
                if (csFile.StartsWith(generatedSubDir, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string src = File.ReadAllText(csFile);
                foreach (Match match in LegacySourceAttrRegex().Matches(src))
                {
                    bool isVerbatim = match.Groups[1].Value.Length > 0;
                    string rawPath = isVerbatim
                        ? match.Groups[2].Value.Replace("\"\"", "\"", StringComparison.Ordinal)
                        : match.Groups[2].Value.Replace("\\\\", "\\", StringComparison.Ordinal);
                    owned.Add(LegacyScriptPathNormalizer.NormalizeToUtf8(rawPath));
                }
            }
        }

        // 2. Include exact paths owned by handcrafted scripts in TieuDaoTuExactScriptPathManifest
        //    (excluding the 3 generic partial object codes that are replaced by exact generated scripts).
        foreach (KeyValuePair<string, string> kv in TieuDaoTuExactScriptPathManifest.DialogNpcPaths)
        {
            owned.Add(LegacyScriptPathNormalizer.NormalizeToUtf8(kv.Key));
        }

        foreach (KeyValuePair<string, string> kv in TieuDaoTuExactScriptPathManifest.MonsterPaths)
        {
            owned.Add(LegacyScriptPathNormalizer.NormalizeToUtf8(kv.Key));
        }

        foreach (KeyValuePair<string, string> kv in TieuDaoTuExactScriptPathManifest.TrapPaths)
        {
            owned.Add(LegacyScriptPathNormalizer.NormalizeToUtf8(kv.Key));
        }

        foreach (KeyValuePair<string, string> kv in TieuDaoTuExactScriptPathManifest.MapObjectPaths)
        {
            if (!ReplaceableGenericObjectCodes.Contains(kv.Value))
            {
                owned.Add(LegacyScriptPathNormalizer.NormalizeToUtf8(kv.Key));
            }
        }

        return owned;
    }

    private static HashSet<string> LoadAmbientNpcPaths(string packRoot)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string ambientPath = Path.Combine(packRoot, "data", "dialogs", "ambient-npcs.dialogs.json");
        if (!File.Exists(ambientPath))
        {
            return paths;
        }

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(ambientPath));
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("entries", out JsonElement entries) ||
            entries.ValueKind != JsonValueKind.Array)
        {
            return paths;
        }

        foreach (JsonElement entry in entries.EnumerateArray())
        {
            if (entry.TryGetProperty("scriptPaths", out JsonElement spArr) && spArr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement sp in spArr.EnumerateArray())
                {
                    if (sp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(sp.GetString()))
                    {
                        paths.Add(LegacyScriptPathNormalizer.NormalizeToUtf8(sp.GetString()));
                    }
                }
            }
        }

        return paths;
    }

    private static string ToUtf8ScriptPath(string utf8DisplayPath)
    {
        string normalized = utf8DisplayPath.Trim().Replace('/', '\\').TrimStart('\\');
        if (normalized.StartsWith("server1\\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["server1\\".Length..];
        }
        else if (normalized.StartsWith("gateway\\s3relay\\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["gateway\\s3relay\\".Length..];
        }

        return "\\" + normalized;
    }
}
