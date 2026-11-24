using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using JXReborn.ScriptMigrator.Analyzers;
using JXReborn.ScriptMigrator.Generators;
using JXReborn.ScriptMigrator.Models;
using JXReborn.ScriptMigrator.Tests.Fixtures;
using Xunit;

namespace JXReborn.ScriptMigrator.Tests.Generators;

public sealed class LuaToCSharpGeneratorTests
{
    private static readonly Lazy<ScriptInventoryManifest> CachedManifest = new(() =>
    {
        var builder = new ScriptInventoryBuilder();
        return builder.Build(LegacyServerTestPaths.FindLegacyBinRoot());
    });

    [Fact]
    public void Generate_AgainstRealLegacyCorpusToTempPack_ShouldGenerateTrapsObjectsItemsMonstersAndNpcsAndFailClosedUnsafeScripts()
    {
        string legacyBin = LegacyServerTestPaths.FindLegacyBinRoot();
        string realPackRoot = LegacyServerTestPaths.FindPackTieuDaoTuRoot();
        string tempRoot = Path.Combine(Path.GetTempPath(), "jxreborn-gen-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Create isolated temp pack copy containing data/items/consumables-and-scripts.items.json,
            // data/dialogs/ambient-npcs.dialogs.json, and non-generated src/ metadata references.
            string tempPack = Path.Combine(tempRoot, "pack");
            string tempArtifacts = Path.Combine(tempRoot, "artifacts");
            CopyPackInputs(realPackRoot, tempPack);

            var generator = new LuaToCSharpGenerator();
            ScriptGenerationResult result = generator.Generate(
                CachedManifest.Value,
                legacyBin,
                tempPack,
                tempArtifacts,
                kindFilter: "all");

            Assert.True(result.GeneratedTrapCount >= 600, $"Expected >= 600 generated traps, got {result.GeneratedTrapCount}.");
            Assert.True(result.GeneratedObjectCount >= 250, $"Expected >= 250 generated objects, got {result.GeneratedObjectCount}.");
            Assert.True(result.GeneratedItemCount >= 25, $"Expected >= 25 generated items, got {result.GeneratedItemCount}.");
            Assert.True(result.GeneratedMonsterCount >= 100, $"Expected >= 100 generated monster OnDeath scripts, got {result.GeneratedMonsterCount}.");
            Assert.True(result.GeneratedNpcCount >= 80, $"Expected >= 80 generated simple NPC scripts, got {result.GeneratedNpcCount}.");
            Assert.NotEmpty(result.WrittenCSharpFiles);
            Assert.NotEmpty(result.ManualQueue);

            // Fail-closed ground truth: scripts with undefined variables or includes MUST appear in ManualQueue, never generated!
            Assert.Contains(result.ManualQueue, static q =>
                q.Utf8ScriptPath.EndsWith(@"\路人_丐帮弟子1.lua", StringComparison.OrdinalIgnoreCase) &&
                q.BlockingReasons.Any(static r => r.Contains("undefined-var:UTask", StringComparison.Ordinal)));

            Assert.Contains(result.ManualQueue, static q =>
                q.Utf8ScriptPath.EndsWith(@"\global\seasonnpc.lua", StringComparison.OrdinalIgnoreCase) ||
                q.Utf8ScriptPath.EndsWith(@"\missions\battle\npc\chefu.lua", StringComparison.OrdinalIgnoreCase));

            // Player-death hooks (SetDeathScript) must NOT be misclassified as monster INpcCombatScript
            Assert.Contains(result.ManualQueue, static q =>
                q.Utf8ScriptPath.EndsWith(@"\missions\clearskill\playerdeath.lua", StringComparison.OrdinalIgnoreCase));

            // Verify manual-migration-queue.md and .json were written with real content
            string queueMd = Path.Combine(tempArtifacts, "manual-migration-queue.md");
            string queueJson = Path.Combine(tempArtifacts, "manual-migration-queue.json");
            Assert.True(File.Exists(queueMd));
            Assert.True(File.Exists(queueJson));
            Assert.Contains("undefined-var:UTask", File.ReadAllText(queueMd));

            // Verify every written C# file contains valid [LegacySource] (except GeneratedLuaRuntime.cs / GeneratedIncludedHeaderProvenance.cs),
            // ZERO duplicate [LegacySource] paths exist across generated files, and ZERO generated [LegacySource] collides with handcrafted scripts.
            var legacySourceRegex = new Regex(@"\[LegacySource\(\s*@?""([^""]+)""", RegexOptions.Compiled);
            var handcraftedSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var seenGeneratedSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string scriptsRoot = Path.Combine(tempPack, "src", "JXReborn.Packs.TieuDaoTu", "Scripts");
            string generatedDirPrefix = Path.Combine(scriptsRoot, "Generated") + Path.DirectorySeparatorChar;
            string[] allScriptFiles = Directory.GetFiles(scriptsRoot, "*.cs", SearchOption.AllDirectories);
            Assert.NotEmpty(allScriptFiles);

            foreach (string csFile in allScriptFiles)
            {
                bool isGeneratedFile = csFile.StartsWith(generatedDirPrefix, StringComparison.OrdinalIgnoreCase);
                string code = File.ReadAllText(csFile);
                foreach (Match match in legacySourceRegex.Matches(code))
                {
                    string pathKey = match.Groups[1].Value.Replace("\\\\", "\\", StringComparison.Ordinal);
                    if (!isGeneratedFile)
                    {
                        handcraftedSources.TryAdd(pathKey, Path.GetFileName(csFile));
                    }
                    else
                    {
                        Assert.False(
                            handcraftedSources.TryGetValue(pathKey, out string? handcraftedOwner),
                            $"Generated script '{Path.GetFileName(csFile)}' collided with handcrafted owner '{handcraftedOwner}' on '{pathKey}'.");
                        Assert.False(
                            seenGeneratedSources.TryGetValue(pathKey, out string? existingGenOwner),
                            $"Duplicate generated [LegacySource] path '{pathKey}' in both '{existingGenOwner}' and '{Path.GetFileName(csFile)}'.");
                        seenGeneratedSources[pathKey] = Path.GetFileName(csFile);
                    }
                }
            }

            foreach ((string genPath, string genFile) in seenGeneratedSources)
            {
                Assert.False(
                    handcraftedSources.TryGetValue(genPath, out string? hcOwner),
                    $"Generated script '{genFile}' collided with handcrafted owner '{hcOwner}' on '{genPath}'.");
            }

            foreach (string csFile in result.WrittenCSharpFiles)
            {
                Assert.True(File.Exists(csFile));
                string code = File.ReadAllText(csFile);
                string fileName = Path.GetFileName(csFile);
                if (fileName.Equals("GeneratedIncludedHeaderProvenance.cs", StringComparison.Ordinal))
                {
                    Assert.Contains("Sha256ByHeaderPath", code, StringComparison.Ordinal);
                }
                else if (!fileName.Equals("GeneratedLuaRuntime.cs", StringComparison.Ordinal))
                {
                    Assert.Contains("[LegacySource(@\"", code, StringComparison.Ordinal);
                    Assert.Contains("public sealed class ", code, StringComparison.Ordinal);
                    Assert.DoesNotContain("playerdeath.lua", code, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static void CopyPackInputs(string realPackRoot, string tempPackRoot)
    {
        // Copy only files needed for generator ownership/catalog checks (fast, < 50ms)
        CopySingleFile(realPackRoot, tempPackRoot, Path.Combine("data", "items", "consumables-and-scripts.items.json"));
        CopySingleFile(realPackRoot, tempPackRoot, Path.Combine("data", "dialogs", "ambient-npcs.dialogs.json"));
        CopyDirectoryFiles(Path.Combine(realPackRoot, "data", "npcs"), Path.Combine(tempPackRoot, "data", "npcs"), "*.json");
        CopyDirectoryFiles(Path.Combine(realPackRoot, "data", "maps"), Path.Combine(tempPackRoot, "data", "maps"), "*.map.json");
        CopyDirectoryTree(
            Path.Combine(realPackRoot, "src", "JXReborn.Packs.TieuDaoTu"),
            Path.Combine(tempPackRoot, "src", "JXReborn.Packs.TieuDaoTu"),
            "*.cs");
    }

    private static void CopySingleFile(string srcRoot, string dstRoot, string relPath)
    {
        string src = Path.Combine(srcRoot, relPath);
        if (!File.Exists(src))
        {
            return;
        }

        string dst = Path.Combine(dstRoot, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(src, dst, overwrite: true);
    }

    private static void CopyDirectoryFiles(string srcDir, string dstDir, string pattern)
    {
        if (!Directory.Exists(srcDir))
        {
            return;
        }

        Directory.CreateDirectory(dstDir);
        foreach (string file in Directory.EnumerateFiles(srcDir, pattern, SearchOption.TopDirectoryOnly))
        {
            File.Copy(file, Path.Combine(dstDir, Path.GetFileName(file)), overwrite: true);
        }
    }

    private static void CopyDirectoryTree(string srcDir, string dstDir, string pattern)
    {
        if (!Directory.Exists(srcDir))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(srcDir, pattern, SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(srcDir, file);
            if (rel.StartsWith("bin", StringComparison.OrdinalIgnoreCase) ||
                rel.StartsWith("obj", StringComparison.OrdinalIgnoreCase) ||
                rel.Contains("Scripts" + Path.DirectorySeparatorChar + "Generated", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string dst = Path.Combine(dstDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(file, dst, overwrite: true);
        }
    }
}
