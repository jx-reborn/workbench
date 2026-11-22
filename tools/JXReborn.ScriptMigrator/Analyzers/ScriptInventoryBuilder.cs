using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JXReborn.Primitives.Encodings;
using JXReborn.ScriptMigrator.Models;
using JXReborn.ScriptMigrator.Rules;

namespace JXReborn.ScriptMigrator.Analyzers;

/// <summary>
/// Scans 100% of <c>server1/script</c> and <c>gateway/s3relay/script</c> in two passes
/// (pass 1: global/included constant extraction; pass 2: full static analysis with cross-file constant resolution)
/// to build the authoritative 100% inventory manifest with zero unaccounted files.
/// </summary>
public sealed class ScriptInventoryBuilder
{
    private const string EmptyTokenAstHash = "e3b0c44298fc1c14";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly Encoding Cp1252;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly string[] GeographicRegionPrefixes =
    [
        "server1/script/ÖÐÔ­ÄÏÇø/",
        "server1/script/ÖÐÔ­±±Çø/",
        "server1/script/Î÷ÄÏ±±Çø/",
        "server1/script/½­ÄÏÇø/",
        "server1/script/Á½ºþÇø/",
        "server1/script/Î÷ÄÏÄÏÇø/",
        "server1/script/Î÷±±±±Çø/",
        "server1/script/Î÷±±ÄÏÇø/",
        "server1/script/¶«±±Çø/",
    ];

    static ScriptInventoryBuilder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp1252 = Encoding.GetEncoding(1252);
    }

    public ScriptInventoryManifest Build(string legacyBinRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyBinRoot);

        var rawFiles = new List<(string FilePath, string RelPath, LegacyScriptScope Scope, bool IsExcluded, byte[] Bytes)>(8000);
        CollectScope(legacyBinRoot, Path.Combine(legacyBinRoot, "server1", "script"), LegacyScriptScope.Server1, rawFiles);
        CollectScope(legacyBinRoot, Path.Combine(legacyBinRoot, "gateway", "s3relay", "script"), LegacyScriptScope.S3Relay, rawFiles);

        // Pass 1: build global exported constant table indexed by normalized include path
        var constantsByNormalizedScriptPath = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        var globalFallbackConstants = new Dictionary<string, int>(StringComparer.Ordinal);
        var globalConstantCollisions = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in rawFiles)
        {
            if (item.IsExcluded)
            {
                continue;
            }

            Dictionary<string, int> fileConsts = LuaStaticAnalyzer.ExtractExportedIntConstants(item.Bytes);
            if (fileConsts.Count == 0)
            {
                continue;
            }

            string normKey = ToIncludeLookupKey(item.RelPath, item.Scope);
            constantsByNormalizedScriptPath[normKey] = fileConsts;

            foreach (KeyValuePair<string, int> kv in fileConsts)
            {
                if (globalConstantCollisions.Contains(kv.Key))
                {
                    continue;
                }

                if (globalFallbackConstants.TryGetValue(kv.Key, out int existing) && existing != kv.Value)
                {
                    globalFallbackConstants.Remove(kv.Key);
                    globalConstantCollisions.Add(kv.Key);
                }
                else
                {
                    globalFallbackConstants[kv.Key] = kv.Value;
                }
            }
        }

        // Pass 2: analyze every file with cross-file constant resolution
        var entries = new List<ScriptInventoryEntry>(rawFiles.Count);
        foreach (var item in rawFiles)
        {
            string sha256 = Convert.ToHexStringLower(SHA256.HashData(item.Bytes));
            LuaAnalysisResult prelim = LuaStaticAnalyzer.Analyze(item.Bytes, globalFallbackConstants);

            // If file has Includes with specific constant definitions, re-analyze with merged included constants
            LuaAnalysisResult analysis = prelim;
            if (!item.IsExcluded && prelim.IncludedFiles.Count > 0)
            {
                Dictionary<string, int>? mergedExternal = null;
                foreach (string inc in prelim.IncludedFiles)
                {
                    string incKey = inc.TrimStart('/');
                    if (constantsByNormalizedScriptPath.TryGetValue(incKey, out Dictionary<string, int>? incConsts))
                    {
                        mergedExternal ??= new Dictionary<string, int>(globalFallbackConstants, StringComparer.Ordinal);
                        foreach (KeyValuePair<string, int> kv in incConsts)
                        {
                            mergedExternal[kv.Key] = kv.Value;
                        }
                    }
                }

                if (mergedExternal is not null)
                {
                    analysis = LuaStaticAnalyzer.Analyze(item.Bytes, mergedExternal);
                }
            }

            string canonicalScriptPath = item.Scope == LegacyScriptScope.Server1
                ? "\\" + item.RelPath["server1/".Length..].Replace('/', '\\')
                : "\\" + item.RelPath["gateway/s3relay/".Length..].Replace('/', '\\');

            string utf8DisplayPath = DecodePathToUtf8(item.RelPath);
            (int bucket, string bucketName) = ClassifyBucket(item.RelPath, item.Scope, item.IsExcluded);

            entries.Add(new ScriptInventoryEntry
            {
                RelativePath = item.RelPath,
                CanonicalScriptPath = canonicalScriptPath,
                Utf8DisplayPath = utf8DisplayPath,
                Scope = item.Scope,
                Bucket = bucket,
                BucketName = bucketName,
                IsExcludedSimBot = item.IsExcluded,
                ByteSize = item.Bytes.Length,
                LineCount = analysis.LineCount,
                Sha256 = sha256,
                NormalizedAstHash = analysis.NormalizedAstHash,
                IsPureStaticDialog = analysis.IsPureStaticDialog,
                CalledApis = analysis.CalledApis,
                ReferencedTaskIds = analysis.ReferencedTaskIds,
                ReferencedTempTaskIds = analysis.ReferencedTempTaskIds,
                ReferencedGlobalValueIds = analysis.ReferencedGlobalValueIds,
                IncludedFiles = analysis.IncludedFiles,
                DefinedFunctions = analysis.DefinedFunctions,
            });
        }

        entries.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));

        var bucketCounts = entries
            .GroupBy(e => $"{e.Bucket}: {e.BucketName}")
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var apiCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ScriptInventoryEntry entry in entries.Where(e => !e.IsExcludedSimBot))
        {
            foreach (string api in entry.CalledApis)
            {
                apiCounts[api] = apiCounts.GetValueOrDefault(api) + 1;
            }
        }

        var topApis = apiCounts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(80)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        int server1Total = entries.Count(e => e.Scope == LegacyScriptScope.Server1);
        int server1Excluded = entries.Count(e => e.Scope == LegacyScriptScope.Server1 && e.IsExcludedSimBot);
        int s3RelayTotal = entries.Count(e => e.Scope == LegacyScriptScope.S3Relay);
        int s3RelayExcluded = entries.Count(e => e.Scope == LegacyScriptScope.S3Relay && e.IsExcludedSimBot);

        return new ScriptInventoryManifest
        {
            GeneratedAtUtc = DateTime.UtcNow.ToString("O"),
            LegacyRootPath = Path.GetFullPath(legacyBinRoot),
            TotalFiles = entries.Count,
            Server1TotalFiles = server1Total,
            Server1ActiveFiles = server1Total - server1Excluded,
            Server1ExcludedFiles = server1Excluded,
            S3RelayTotalFiles = s3RelayTotal,
            S3RelayActiveFiles = s3RelayTotal - s3RelayExcluded,
            S3RelayExcludedFiles = s3RelayExcluded,
            PureStaticDialogCandidates = entries.Count(e => !e.IsExcludedSimBot && e.IsPureStaticDialog),
            BucketCounts = bucketCounts,
            TopApiCallFileCounts = topApis,
            Entries = entries,
        };
    }

    public void WriteOutputs(ScriptInventoryManifest manifest, string migrationArtifactsDir)
    {
        Directory.CreateDirectory(migrationArtifactsDir);

        string jsonPath = Path.Combine(migrationArtifactsDir, "script-inventory-manifest.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(manifest, JsonOptions), new UTF8Encoding(false));

        string mdPath = Path.Combine(migrationArtifactsDir, "script-inventory-summary.md");
        var sb = new StringBuilder();
        sb.AppendLine("# Script Inventory Summary (100% Ground Truth from `legacy-server/bin`)");
        sb.AppendLine();
        sb.AppendLine($"- **Generated (UTC)**: `{manifest.GeneratedAtUtc}`");
        sb.AppendLine($"- **Total Lua Files**: **{manifest.TotalFiles:N0}** (`server1`: `{manifest.Server1TotalFiles:N0}` total / `{manifest.Server1ActiveFiles:N0}` active / `{manifest.Server1ExcludedFiles:N0}` Sim/Bot excluded; `s3relay`: `{manifest.S3RelayTotalFiles:N0}` total / `{manifest.S3RelayActiveFiles:N0}` active / `{manifest.S3RelayExcludedFiles:N0}` Sim/Bot excluded)");
        sb.AppendLine($"- **Pure Static Dialog Candidates (strict: single `main`, zero branching/callbacks/task state)**: **{manifest.PureStaticDialogCandidates:N0}**");
        sb.AppendLine();
        sb.AppendLine("## Bucket Breakdown");
        sb.AppendLine("| Bucket | File Count |");
        sb.AppendLine("| :--- | ---: |");
        foreach (KeyValuePair<string, int> kv in manifest.BucketCounts)
        {
            sb.AppendLine($"| {kv.Key} | {kv.Value:N0} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Top Identical AST Clusters (Include-Preserving Safe Parametric Candidates)");
        sb.AppendLine("| Normalized AST Hash | File Count | Sample Path |");
        sb.AppendLine("| :--- | ---: | :--- |");
        var astClusters = manifest.Entries
            .Where(e => !e.IsExcludedSimBot && !string.Equals(e.NormalizedAstHash, EmptyTokenAstHash, StringComparison.Ordinal))
            .GroupBy(e => e.NormalizedAstHash)
            .Where(g => g.Count() >= 5)
            .OrderByDescending(g => g.Count())
            .Take(30);
        foreach (IGrouping<string, ScriptInventoryEntry> cluster in astClusters)
        {
            sb.AppendLine($"| `{cluster.Key}` | {cluster.Count():N0} | `{cluster.First().Utf8DisplayPath}` |");
        }

        sb.AppendLine();
        sb.AppendLine("## Top 50 Called Lua C-APIs (Active Scripts)");
        sb.AppendLine("| API Name | Files Calling |");
        sb.AppendLine("| :--- | ---: |");
        foreach (KeyValuePair<string, int> kv in manifest.TopApiCallFileCounts.Take(50))
        {
            sb.AppendLine($"| `{kv.Key}` | {kv.Value:N0} |");
        }

        File.WriteAllText(mdPath, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>
    /// Decodes NTFS CP1252 / double-mojibake legacy path segments back to clean UTF-8 GBK Chinese/ASCII names,
    /// mirroring <c>recover_ntfs_mojibake_segment</c> in <c>pack_legacy_profiles.py</c>.
    /// </summary>
    public static string DecodePathToUtf8(string relPath)
    {
        string[] segments = relPath.Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            segments[i] = RecoverNtfsMojibakeSegment(segments[i]);
        }

        return string.Join('/', segments);
    }

    private static string RecoverNtfsMojibakeSegment(string segment)
    {
        bool hasHigh = false;
        for (int i = 0; i < segment.Length; i++)
        {
            if (segment[i] >= 0x80)
            {
                hasHigh = true;
                break;
            }
        }

        if (!hasHigh)
        {
            return segment;
        }

        // Convert Unicode NTFS filename back to raw CP1252 bytes (handles U+2020 `†` -> 0x86, etc.)
        byte[] rawBytes = Cp1252.GetBytes(segment);

        // Unwrap UTF-8-as-CP1252 double mojibake if present (e.g. `Ã‚Â·ÃˆÃ‹` -> Latin1 bytes -> GBK)
        try
        {
            string unwrapped = StrictUtf8.GetString(rawBytes);
            bool allLatin1 = true;
            for (int i = 0; i < unwrapped.Length; i++)
            {
                if (unwrapped[i] > 0xFF)
                {
                    allLatin1 = false;
                    break;
                }
            }

            if (allLatin1)
            {
                rawBytes = Encoding.Latin1.GetBytes(unwrapped);
            }
        }
        catch (DecoderFallbackException)
        {
            // Already single-layer GBK bytes
        }

        return GbkEncoding.DecodeNullTerminated(rawBytes);
    }

    private static void CollectScope(
        string legacyBinRoot,
        string scopeDir,
        LegacyScriptScope scope,
        List<(string FilePath, string RelPath, LegacyScriptScope Scope, bool IsExcluded, byte[] Bytes)> target)
    {
        if (!Directory.Exists(scopeDir))
        {
            return;
        }

        foreach (string filePath in Directory.EnumerateFiles(scopeDir, "*.lua", SearchOption.AllDirectories))
        {
            string relPath = LegacyExclusionRules.NormalizeRelativePath(Path.GetRelativePath(legacyBinRoot, filePath));
            bool isExcluded = LegacyExclusionRules.IsExcludedSimBotScript(relPath);
            byte[] rawBytes = File.ReadAllBytes(filePath);
            target.Add((filePath, relPath, scope, isExcluded, rawBytes));
        }
    }

    private static string ToIncludeLookupKey(string relPath, LegacyScriptScope scope) =>
        scope == LegacyScriptScope.Server1
            ? relPath["server1/".Length..]
            : relPath["gateway/s3relay/".Length..];

    private static (int Bucket, string Name) ClassifyBucket(string relPath, LegacyScriptScope scope, bool isExcluded)
    {
        if (isExcluded)
        {
            return (0, "Excluded Sim/Bot Overlays");
        }

        if (scope == LegacyScriptScope.S3Relay)
        {
            return (6, "S3Relay Scheduled World Scripts");
        }

        if (relPath.StartsWith("server1/script/skill/", StringComparison.OrdinalIgnoreCase) ||
            relPath.StartsWith("server1/script/npclevelscript/", StringComparison.OrdinalIgnoreCase) ||
            relPath.Contains("/trap/", StringComparison.OrdinalIgnoreCase))
        {
            return (1, "Skills, NpcLevelScripts & Map Traps");
        }

        foreach (string regionPrefix in GeographicRegionPrefixes)
        {
            if (relPath.StartsWith(regionPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return (2, "9 Geographic Regions (NPCs, Objects, Region Helpers)");
            }
        }

        if (relPath.StartsWith("server1/script/task/", StringComparison.OrdinalIgnoreCase) ||
            relPath.StartsWith("server1/script/shitu/", StringComparison.OrdinalIgnoreCase) ||
            relPath.StartsWith("server1/script/partner/", StringComparison.OrdinalIgnoreCase) ||
            relPath.StartsWith("server1/script/global/¸÷ÅÉ½ÓÒýµÜ×Ó/", StringComparison.OrdinalIgnoreCase))
        {
            return (4, "Storyline Quests, Sects & Progression");
        }

        if (relPath.StartsWith("server1/script/missions/", StringComparison.OrdinalIgnoreCase) ||
            relPath.StartsWith("server1/script/battles/", StringComparison.OrdinalIgnoreCase) ||
            relPath.StartsWith("server1/script/tong/", StringComparison.OrdinalIgnoreCase) ||
            relPath.StartsWith("server1/script/nationalwar/", StringComparison.OrdinalIgnoreCase) ||
            relPath.StartsWith("server1/script/activitysys/", StringComparison.OrdinalIgnoreCase) ||
            relPath.StartsWith("server1/script/event/", StringComparison.OrdinalIgnoreCase) ||
            relPath.StartsWith("server1/script/vng_event/", StringComparison.OrdinalIgnoreCase) ||
            relPath.StartsWith("server1/script/sukien_nam/", StringComparison.OrdinalIgnoreCase) ||
            relPath.StartsWith("server1/script/vng_feature/", StringComparison.OrdinalIgnoreCase) ||
            relPath.StartsWith("server1/script/eventthanglong1000/", StringComparison.OrdinalIgnoreCase) ||
            relPath.StartsWith("server1/script/bonusvlmc/", StringComparison.OrdinalIgnoreCase) ||
            relPath.StartsWith("server1/script/baoruongthanbi/", StringComparison.OrdinalIgnoreCase))
        {
            return (5, "Missions, Battlegrounds, Guild Wars & Events");
        }

        return (3, "Items, Global Core, Libraries & Utilities");
    }
}
