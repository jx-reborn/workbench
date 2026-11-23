using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JXReborn.LegacyImporter.Sources;
using JXReborn.Packs.TieuDaoTu.Hosting;
using JXReborn.Packs.TieuDaoTu.Scripts.Dialogs;
using JXReborn.Packs.TieuDaoTu.Scripts.Generated;
using JXReborn.Scripts.Dialogs;
using JXReborn.ScriptMigrator.Ast;
using JXReborn.ScriptMigrator.Generators;
using JXReborn.ScriptMigrator.Models;
using JXReborn.ZoneEngine.Scripting;

namespace JXReborn.ScriptMigrator.Verification;

/// <summary>
/// Differential behavioral verifier comparing legacy Lua 4.0 VM execution (<see cref="LuaOracleHarness"/>)
/// against C# script execution (<see cref="RecordingScriptContext"/>) across AST-derived boundary state vectors.
/// </summary>
public sealed class DifferentialScriptEquivalenceVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly MethodInfo? ClockScopeMethod =
        typeof(GeneratedLuaRuntime).GetMethod("UseDeterministicClock", BindingFlags.Public | BindingFlags.Static, [typeof(DateTime)]);

    public async Task<EquivalenceAuditReport> VerifyPackAsync(
        ScriptInventoryManifest manifest,
        string legacyBinRoot,
        string packRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyBinRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(packRoot);

        ILegacyServerSource source = LegacyServerSourceFactory.Create(legacyBinRoot);
        PackItemCatalogIndex itemIndex = PackItemCatalogIndex.Load(packRoot);
        var luaOracle = new LuaOracleHarness(source, itemIndex.EventItemCodeByDetailId, itemIndex);
        var parser = new Lua4AstParser();
        var linker = new LuaIncludeLinker(source, parser);

        var entryByUtf8Path = new Dictionary<string, ScriptInventoryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (ScriptInventoryEntry entry in manifest.Entries.Where(static e => !e.IsExcludedSimBot))
        {
            string utf8Path = ToUtf8ScriptPath(entry.Utf8DisplayPath);
            entryByUtf8Path.TryAdd(utf8Path, entry);
            entryByUtf8Path.TryAdd(entry.CanonicalScriptPath, entry);
        }

        int verifiedGenerated = 0;
        int verifiedAmbient = 0;
        int totalVectors = 0;
        int matchedVectors = 0;
        var mismatches = new List<EquivalenceMismatchRecord>();

        // 1. Verify 100% of generated C# scripts ([LegacySource] on types in JXReborn.Packs.TieuDaoTu.Scripts.Generated)
        Assembly packAssembly = typeof(TieuDaoTuScriptCodeResolver).Assembly;
        foreach (Type type in packAssembly.GetTypes().OrderBy(static t => t.FullName, StringComparer.Ordinal))
        {
            if (type.IsAbstract ||
                type.IsInterface ||
                !string.Equals(type.Namespace, "JXReborn.Packs.TieuDaoTu.Scripts.Generated", StringComparison.Ordinal))
            {
                continue;
            }

            LegacySourceAttribute[] attrs = (LegacySourceAttribute[])type.GetCustomAttributes(typeof(LegacySourceAttribute), inherit: false);
            if (attrs.Length == 0)
            {
                continue;
            }

            object? instance = Activator.CreateInstance(type);
            if (instance is null)
            {
                continue;
            }

            GeneratedScriptKind kind = ResolveGeneratedKind(instance);
            if (kind == GeneratedScriptKind.Other)
            {
                continue;
            }

            foreach (LegacySourceAttribute attr in attrs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string utf8Path = LegacyScriptPathNormalizer.NormalizeToUtf8(attr.SourceScriptPath);
                if (!entryByUtf8Path.TryGetValue(utf8Path, out ScriptInventoryEntry? entry))
                {
                    continue;
                }

                string fullLuaPath = Path.Combine(legacyBinRoot, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(fullLuaPath))
                {
                    continue;
                }

                byte[] rawBytes = await File.ReadAllBytesAsync(fullLuaPath, cancellationToken);
                LuaParseResult parsed = parser.Parse(rawBytes);
                LuaProgram? programForProbing = parsed.Program;
                if (programForProbing is not null)
                {
                    string expectedEntry = kind == GeneratedScriptKind.Monster ? "OnDeath" : "main";
                    LuaLinkResult linked = linker.LinkAndTreeShake(entry.CanonicalScriptPath, programForProbing, expectedEntry);
                    if (linked.IsSuccess && linked.LinkedProgram is not null)
                    {
                        programForProbing = linked.LinkedProgram;
                    }
                }

                IReadOnlyList<ScriptStateVector> vectors = AstBoundaryStateProber.BuildVectors(
                    programForProbing,
                    itemIndex.EventItemCodeByDetailId,
                    itemIndex);

                verifiedGenerated++;
                bool scriptClean = true;

                foreach (ScriptStateVector vec in vectors)
                {
                    totalVectors++;
                    ScriptEffectTrace luaTrace = luaOracle.Execute(entry.CanonicalScriptPath, kind, vec);
                    ScriptEffectTrace csTrace = await ExecuteCSharpInstanceAsync(instance, kind, utf8Path, vec, cancellationToken);

                    string expectedStr = luaTrace.ToCanonicalString();
                    string actualStr = csTrace.ToCanonicalString();
                    if (string.Equals(expectedStr, actualStr, StringComparison.Ordinal))
                    {
                        matchedVectors++;
                    }
                    else if (scriptClean)
                    {
                        scriptClean = false;
                        mismatches.Add(new EquivalenceMismatchRecord(
                            utf8Path,
                            kind.ToString(),
                            vec.Label,
                            expectedStr,
                            actualStr,
                            $"Divergence on vector '{vec.Label}'"));
                    }
                }
            }
        }

        // 2. Verify 100% of SHA-256-anchored pure static dialogs in ambient-npcs.dialogs.json via AmbientLoreDialogScript
        string ambientJsonPath = Path.Combine(packRoot, "data", "dialogs", "ambient-npcs.dialogs.json");
        if (File.Exists(ambientJsonPath))
        {
            AmbientDialogCatalog.ConfigureDataDirectory(packRoot);
            var ambientScript = new AmbientLoreDialogScript();
            var baselineVec = new ScriptStateVector("ambient-static");

            using JsonDocument doc = JsonDocument.Parse(await File.ReadAllBytesAsync(ambientJsonPath, cancellationToken));
            if (doc.RootElement.TryGetProperty("entries", out JsonElement entriesEl) &&
                entriesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in entriesEl.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!item.TryGetProperty("sourceSha256", out JsonElement shaProp) ||
                        shaProp.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(shaProp.GetString()))
                    {
                        continue;
                    }

                    if (!item.TryGetProperty("scriptPaths", out JsonElement pathsArr) ||
                        pathsArr.ValueKind != JsonValueKind.Array ||
                        pathsArr.GetArrayLength() == 0)
                    {
                        continue;
                    }

                    string? firstPath = pathsArr[0].GetString();
                    if (string.IsNullOrWhiteSpace(firstPath))
                    {
                        continue;
                    }

                    string utf8Path = LegacyScriptPathNormalizer.NormalizeToUtf8(firstPath);
                    if (!entryByUtf8Path.TryGetValue(utf8Path, out ScriptInventoryEntry? entry))
                    {
                        continue;
                    }

                    verifiedAmbient++;
                    int lineCount = item.TryGetProperty("lines", out JsonElement linesEl) && linesEl.ValueKind == JsonValueKind.Array
                        ? linesEl.GetArrayLength()
                        : 1;
                    string mode = item.TryGetProperty("mode", out JsonElement modeEl) && modeEl.ValueKind == JsonValueKind.String
                        ? modeEl.GetString() ?? "say"
                        : "say";

                    int[] seeds = lineCount > 1 && string.Equals(mode, "say", StringComparison.OrdinalIgnoreCase)
                        ? [42, 7, 99, 1337]
                        : [42];

                    bool entryClean = true;
                    foreach (int seed in seeds)
                    {
                        var ambientVec = seeds.Length == 1
                            ? baselineVec
                            : new ScriptStateVector($"ambient-seed-{seed}") { RandomSeed = seed };
                        totalVectors++;

                        ScriptEffectTrace luaTrace = luaOracle.Execute(entry.CanonicalScriptPath, GeneratedScriptKind.Npc, ambientVec);
                        ScriptEffectTrace csTrace = await ExecuteCSharpInstanceAsync(ambientScript, GeneratedScriptKind.Npc, utf8Path, ambientVec, cancellationToken);

                        string expectedStr = luaTrace.ToCanonicalString();
                        string actualStr = csTrace.ToCanonicalString();
                        if (string.Equals(expectedStr, actualStr, StringComparison.Ordinal))
                        {
                            matchedVectors++;
                        }
                        else if (entryClean)
                        {
                            entryClean = false;
                            mismatches.Add(new EquivalenceMismatchRecord(
                                utf8Path,
                                "AmbientDialog",
                                ambientVec.Label,
                                expectedStr,
                                actualStr,
                                "Ambient dialog output diverged from Lua 4.0 Talk/Say"));
                        }
                    }
                }
            }
        }

        return new EquivalenceAuditReport
        {
            GeneratedAtUtc = DateTime.UtcNow.ToString("O"),
            VerifiedGeneratedScripts = verifiedGenerated,
            VerifiedAmbientDialogs = verifiedAmbient,
            TotalStateVectorsExecuted = totalVectors,
            MatchedVectors = matchedVectors,
            MismatchedScripts = mismatches.Count,
            Mismatches = mismatches,
        };
    }

    public async Task<EquivalenceMismatchRecord?> VerifySingleDialogScriptAsync(
        string legacyLuaFilePath,
        string utf8ScriptPath,
        INpcDialogScript csharpScript,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyLuaFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(utf8ScriptPath);
        ArgumentNullException.ThrowIfNull(csharpScript);

        string server1Dir = FindServer1RootFromFile(legacyLuaFilePath);
        ILegacyServerSource source = LegacyServerSourceFactory.Create(server1Dir);
        var emptyEventItems = new Dictionary<int, string>();
        var luaOracle = new LuaOracleHarness(source, emptyEventItems);
        var parser = new Lua4AstParser();
        var linker = new LuaIncludeLinker(source, parser);

        byte[] rawBytes = await File.ReadAllBytesAsync(legacyLuaFilePath, cancellationToken);
        LuaParseResult parsed = parser.Parse(rawBytes);
        string relKey = Path.GetRelativePath(server1Dir, legacyLuaFilePath);
        LuaProgram? programForProbing = parsed.Program;
        if (programForProbing is not null)
        {
            LuaLinkResult linked = linker.LinkAndTreeShake(relKey, programForProbing, "main");
            if (linked.IsSuccess && linked.LinkedProgram is not null)
            {
                programForProbing = linked.LinkedProgram;
            }
        }

        IReadOnlyList<ScriptStateVector> vectors = AstBoundaryStateProber.BuildVectors(programForProbing, emptyEventItems);
        foreach (ScriptStateVector vec in vectors)
        {
            ScriptEffectTrace luaTrace = luaOracle.Execute(relKey, GeneratedScriptKind.Npc, vec);
            ScriptEffectTrace csTrace = await ExecuteCSharpInstanceAsync(csharpScript, GeneratedScriptKind.Npc, utf8ScriptPath, vec, cancellationToken);

            string expectedStr = luaTrace.ToCanonicalString();
            string actualStr = csTrace.ToCanonicalString();
            if (!string.Equals(expectedStr, actualStr, StringComparison.Ordinal))
            {
                return new EquivalenceMismatchRecord(
                    utf8ScriptPath,
                    "Npc",
                    vec.Label,
                    expectedStr,
                    actualStr,
                    $"Divergence on vector '{vec.Label}'");
            }
        }

        return null;
    }

    public void WriteOutputs(EquivalenceAuditReport report, string migrationArtifactsDir)
    {
        Directory.CreateDirectory(migrationArtifactsDir);
        File.WriteAllText(
            Path.Combine(migrationArtifactsDir, "equivalence-report.json"),
            JsonSerializer.Serialize(report, JsonOptions),
            new UTF8Encoding(false));

        var sb = new StringBuilder();
        sb.AppendLine("# Differential Script Equivalence Report (`Lua4Interpreter` vs C# Runtime)");
        sb.AppendLine();
        sb.AppendLine($"- **Generated (UTC)**: `{report.GeneratedAtUtc}`");
        sb.AppendLine($"- **Verified Auto-Generated C# Scripts**: **{report.VerifiedGeneratedScripts:N0}**");
        sb.AppendLine($"- **Verified SHA-256 Ambient Dialog Scripts**: **{report.VerifiedAmbientDialogs:N0}**");
        sb.AppendLine($"- **Total Boundary State Vectors Executed**: **{report.TotalStateVectorsExecuted:N0}**");
        sb.AppendLine($"- **Exact Vector Matches**: **{report.MatchedVectors:N0}** / `{report.TotalStateVectorsExecuted:N0}`");
        sb.AppendLine($"- **Behavioral Mismatches**: **{report.MismatchedScripts:N0}**");
        sb.AppendLine();

        if (report.Mismatches.Count > 0)
        {
            sb.AppendLine("## Mismatch Details");
            foreach (EquivalenceMismatchRecord m in report.Mismatches.Take(50))
            {
                sb.AppendLine($"### `{m.Utf8ScriptPath}` (`{m.ScriptKind}`, vector `{m.StateVectorSummary}`)");
                sb.AppendLine($"- **Expected (Lua 4.0)**: `{m.ExpectedLuaTrace}`");
                sb.AppendLine($"- **Actual (C#)**: `{m.ActualCSharpTrace}`");
                sb.AppendLine();
            }
        }

        File.WriteAllText(
            Path.Combine(migrationArtifactsDir, "equivalence-report.md"),
            sb.ToString(),
            new UTF8Encoding(false));
    }

    private static async Task<ScriptEffectTrace> ExecuteCSharpInstanceAsync(
        object instance,
        GeneratedScriptKind kind,
        string utf8ScriptPath,
        ScriptStateVector vector,
        CancellationToken cancellationToken)
    {
        var rng = new Random(vector.RandomSeed);
        DateTime simTime = vector.SimulatedLocalTime ?? new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
        using IDisposable scope = GeneratedLuaRuntime.UseDeterministicRandom((min, max) =>
            max <= min ? min : rng.Next(min, max + 1));
        using IDisposable? clockScope = ClockScopeMethod?.Invoke(null, [simTime]) as IDisposable;

        var ctx = new RecordingScriptContext(utf8ScriptPath, vector);
        switch (kind)
        {
            case GeneratedScriptKind.Trap when instance is ITrapScript trap:
                await trap.OnStepTrapAsync(ctx, cancellationToken);
                await ctx.DrainInteractiveContinuationsAsync(cancellationToken);
                break;

            case GeneratedScriptKind.Object when instance is IObjInteractionScript obj:
                await obj.OnInteractObjectAsync(ctx, cancellationToken);
                await ctx.DrainInteractiveContinuationsAsync(cancellationToken);
                break;

            case GeneratedScriptKind.Item when instance is IItemUsageScript item:
                ItemUsageResult itemRes = await item.OnUseItemAsync(ctx, cancellationToken);
                ctx.Trace.ItemResult = itemRes == ItemUsageResult.KeepItem ? "KeepItem" : "ConsumeOne";
                await ctx.DrainInteractiveContinuationsAsync(cancellationToken);
                break;

            case GeneratedScriptKind.Monster when instance is INpcCombatScript combat:
                combat.OnNpcDeath(ctx);
                break;

            case GeneratedScriptKind.Npc when instance is INpcDialogScript npc:
                await npc.OnDialogAsync(ctx, cancellationToken);
                await ctx.DrainInteractiveContinuationsAsync(cancellationToken);
                break;
        }

        return ctx.Trace;
    }

    private static GeneratedScriptKind ResolveGeneratedKind(object instance) =>
        instance switch
        {
            ITrapScript => GeneratedScriptKind.Trap,
            IObjInteractionScript => GeneratedScriptKind.Object,
            IItemUsageScript => GeneratedScriptKind.Item,
            INpcCombatScript => GeneratedScriptKind.Monster,
            INpcDialogScript => GeneratedScriptKind.Npc,
            _ => GeneratedScriptKind.Other,
        };

    private static string FindServer1RootFromFile(string fullLuaPath)
    {
        DirectoryInfo? dir = new FileInfo(fullLuaPath).Directory;
        while (dir is not null)
        {
            if (string.Equals(dir.Name, "server1", StringComparison.OrdinalIgnoreCase))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate enclosing 'server1' directory for '{fullLuaPath}'.");
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
