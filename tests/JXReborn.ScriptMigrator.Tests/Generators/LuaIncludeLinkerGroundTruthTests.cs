using System;
using System.IO;
using System.Linq;
using JXReborn.LegacyImporter.Sources;
using JXReborn.ScriptMigrator.Ast;
using JXReborn.ScriptMigrator.Generators;
using JXReborn.ScriptMigrator.Tests.Fixtures;
using Xunit;

namespace JXReborn.ScriptMigrator.Tests.Generators;

public sealed class LuaIncludeLinkerGroundTruthTests
{
    [Fact]
    public void PackItemCatalogIndex_WhenLoadedFromPackTieuDaoTu_ShouldIndexAll24573ItemsIncludingPart1EventItems()
    {
        string packRoot = LegacyServerTestPaths.FindPackTieuDaoTuRoot();
        PackItemCatalogIndex index = PackItemCatalogIndex.Load(packRoot);

        Assert.True(index.TotalItemCount >= 24_500, $"Expected >= 24,500 items, got {index.TotalItemCount}.");
        Assert.NotEmpty(index.EventItemCodeByDetailId);

        // Event items with ParticularType == 1 (previously missed when filtering part == 0) and ParticularType == 0
        Assert.True(index.TryResolveEventItem(116, out string linhChiCode));
        Assert.Equal("linh-chi", linhChiCode);
        Assert.True(index.TryResolveEventItem(118, out string khoangCode));
        Assert.Equal("luong-ngan-khoang", khoangCode);
        Assert.True(index.TryResolveEventItem(232, out string bachHopCode));
        Assert.Equal("bach-tuyet-bach-hop", bachHopCode);

        // General GDPL magicscript & GDP common item lookup
        Assert.True(index.TryResolveByGdpl(6, 1, 71, 1, out PackItemEntry tienThaoLo));
        Assert.False(string.IsNullOrWhiteSpace(tienThaoLo.Code));
        Assert.True(index.TryResolveByGdp(6, 1, 71, out PackItemEntry byGdp));
        Assert.Equal(tienThaoLo.Code, byGdp.Code);
    }

    [Fact]
    public void LinkAndTreeShake_RealLinAnXieDaYeWithTransitiveAllBrotherHeaders_ShouldPruneUnusedKeyedTablesAndKeepReachableHelpers()
    {
        string legacyBin = LegacyServerTestPaths.FindLegacyBinRoot();
        string server1Dir = Path.Combine(legacyBin, "server1");
        ILegacyServerSource source = LegacyServerSourceFactory.Create(server1Dir);
        var parser = new Lua4AstParser();
        var linker = new LuaIncludeLinker(source, parser);

        // Real LinAn NPC that includes findnpctask.lua -> taskhead.lua (29 KB with huge keyed tables!) -> head.lua + itemlist.lua
        string npcFile = Directory.EnumerateFiles(
            Path.Combine(server1Dir, "script"),
            "*.lua",
            SearchOption.AllDirectories)
            .First(p => p.Contains("findnpctask", StringComparison.OrdinalIgnoreCase) == false &&
                        File.ReadAllText(p, System.Text.Encoding.Latin1).Contains("allbrother_0801_FindNpcTaskDialog(192)", StringComparison.Ordinal));

        byte[] raw = File.ReadAllBytes(npcFile);
        LuaParseResult parsed = parser.Parse(raw);
        Assert.True(parsed.IsSuccess, parsed.FailureReason);

        string relPath = Path.GetRelativePath(server1Dir, npcFile);
        LuaLinkResult linked = linker.LinkAndTreeShake(relPath, parsed.Program!, "main");

        Assert.True(linked.IsSuccess, $"Expected link success, got reasons: {string.Join(", ", linked.BlockingReasons)}");
        Assert.NotNull(linked.LinkedProgram);
        Assert.NotEmpty(linked.IncludedVirtualKeys);

        string[] fnNames = linked.LinkedProgram.Functions.Select(static f => f.Name).ToArray();
        Assert.Contains("main", fnNames);
        Assert.Contains("allbrother_0801_CheckIsDialog", fnNames);
        Assert.Contains("allbrother_0801_FindNpcTaskDialog", fnNames);
        Assert.Contains("allbrother_0801_CompleteTask", fnNames);
        Assert.Contains("allbrother_0801_IsAct", fnNames);

        // Unused keyed tables (allbrother_0801_tbTaskList / allbrother_0801_tbItemList) MUST be tree-shaken out
        string[] topConstNames = linked.LinkedProgram.TopLevelStatements
            .OfType<LuaAssignStmt>()
            .Select(static s => s.TargetName)
            .ToArray();
        Assert.Contains("TSK_allbrother_0801_TaskState", topConstNames);
        Assert.DoesNotContain("allbrother_0801_tbTaskList", topConstNames);
        Assert.DoesNotContain("allbrother_0801_tbItemList", topConstNames);
    }
}
