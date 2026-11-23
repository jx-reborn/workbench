using System.Collections.Generic;
using System.IO;
using System.Text;
using JXReborn.ScriptMigrator.Analyzers;
using JXReborn.ScriptMigrator.Models;
using JXReborn.ScriptMigrator.Tests.Fixtures;
using Xunit;

namespace JXReborn.ScriptMigrator.Tests.Analyzers;

public sealed class LuaStaticAnalyzerTests
{
    [Fact]
    public void Analyze_RealBattleMainWithBattleHeadInclude_ShouldResolveGlobalValueIds30And31()
    {
        string legacyBin = LegacyServerTestPaths.FindLegacyBinRoot();
        string battleHeadPath = Path.Combine(legacyBin, "server1", "script", "battles", "battlehead.lua");
        string battleMainPath = Path.Combine(legacyBin, "server1", "script", "battles", "battlemain.lua");
        Assert.True(File.Exists(battleHeadPath));
        Assert.True(File.Exists(battleMainPath));

        Dictionary<string, int> exportedFromHead = LuaStaticAnalyzer.ExtractExportedIntConstants(File.ReadAllBytes(battleHeadPath));
        LuaAnalysisResult result = LuaStaticAnalyzer.Analyze(File.ReadAllBytes(battleMainPath), exportedFromHead);

        Assert.Contains(30, result.ReferencedGlobalValueIds); // GLB_BATTLESTATE
        Assert.Contains(31, result.ReferencedGlobalValueIds); // GLB_FORBIDBATTLE
        Assert.Contains("/script/battles/battlehead.lua", result.IncludedFiles);
        Assert.False(result.IsPureStaticDialog);
    }

    [Fact]
    public void Analyze_RealEquipEnchaseHelpMenu_ShouldRejectFromPureStaticDialog()
    {
        string legacyBin = LegacyServerTestPaths.FindLegacyBinRoot();
        string helpPath = Path.Combine(legacyBin, "server1", "script", "global", "equipenchase_help.lua");
        Assert.True(File.Exists(helpPath));

        LuaAnalysisResult result = LuaStaticAnalyzer.Analyze(File.ReadAllBytes(helpPath));

        Assert.False(result.IsPureStaticDialog);
        Assert.True(result.DefinedFunctions.Count > 1);
    }

    [Theory]
    [InlineData("seed_crystal1.lua")]
    [InlineData("seed_gold1.lua")]
    [InlineData("seed_lucky1.lua")]
    [InlineData("seed_rose1.lua")]
    public void Analyze_RealSeedItemScriptsWithMsg2Player_ShouldNotBeClassifiedAsPureStaticNpcDialog(string itemFileName)
    {
        // Ground truth: server1/script/item/seed_*.lua are item-usage scripts that emit Msg2Player;
        // classifying them as PureStaticDialog caused StaticDialogExporter to skip 4/525 candidates.
        string legacyBin = LegacyServerTestPaths.FindLegacyBinRoot();
        string itemPath = Path.Combine(legacyBin, "server1", "script", "item", itemFileName);
        Assert.True(File.Exists(itemPath));

        LuaAnalysisResult result = LuaStaticAnalyzer.Analyze(File.ReadAllBytes(itemPath));

        Assert.Contains("Msg2Player", result.CalledApis);
        Assert.False(result.IsPureStaticDialog);
    }

    [Fact]
    public void Analyze_ForPureStaticDialogsWithSameStructure_ShouldProduceMatchingAstHash()
    {
        const string npcA = """
            function main()
                Talk(1, "", "Chao mung den Ba Lang Huyen")
            end
            """;
        const string npcB = """
            function main()
                Talk(1, "", "Chao mung den Tuong Duong Phu")
            end
            """;

        LuaAnalysisResult resA = LuaStaticAnalyzer.Analyze(Encoding.Latin1.GetBytes(npcA));
        LuaAnalysisResult resB = LuaStaticAnalyzer.Analyze(Encoding.Latin1.GetBytes(npcB));

        Assert.True(resA.IsPureStaticDialog);
        Assert.True(resB.IsPureStaticDialog);
        Assert.Equal(resA.NormalizedAstHash, resB.NormalizedAstHash);
    }

    [Fact]
    public void Analyze_ForInteractiveSayMenuAndTalkCallback_ShouldRejectPureStaticDialog()
    {
        const string interactiveSay = """
            function main()
                Say("Chon", 2, "Muc 1/opt1", "Dong/no")
            end
            function opt1() end
            function no() end
            """;
        const string interactiveTalk = """
            function main()
                Talk(1, "next_step", "Xin chao")
            end
            """;

        Assert.False(LuaStaticAnalyzer.Analyze(Encoding.Latin1.GetBytes(interactiveSay)).IsPureStaticDialog);
        Assert.False(LuaStaticAnalyzer.Analyze(Encoding.Latin1.GetBytes(interactiveTalk)).IsPureStaticDialog);
    }

    [Fact]
    public void Analyze_WithWrapperApisAndExternalIncludeConstants_ShouldResolveIdsWithoutTaskZero()
    {
        const string lua = """
            function main()
                local n_taskid = 0
                local a = nt_getTask(TSK_EXTERNAL)
                local b = GetByte(GetTask(1088), 2)
                local c = GetGlbValue(GLB_BATTLE_STATE)
                PlayerFunLib:AddTaskDaily(3012, 1)
            end
            """;
        var external = new Dictionary<string, int>
        {
            ["TSK_EXTERNAL"] = 2045,
            ["GLB_BATTLE_STATE"] = 42,
        };

        LuaAnalysisResult res = LuaStaticAnalyzer.Analyze(Encoding.Latin1.GetBytes(lua), external);

        Assert.DoesNotContain(0, res.ReferencedTaskIds);
        Assert.Contains(2045, res.ReferencedTaskIds);
        Assert.Contains(1088, res.ReferencedTaskIds);
        Assert.Contains(3012, res.ReferencedTaskIds);
        Assert.Contains(42, res.ReferencedGlobalValueIds);
    }

    [Fact]
    public void Analyze_WhenIncludeTargetsDiffer_ShouldDistinguishNormalizedAstHash()
    {
        const string fileA = """
            Include("\\script\\global\\a.lua")
            function main() Say("Hi", 0) end
            """;
        const string fileB = """
            Include("\\script\\global\\b.lua")
            function main() Say("Hi", 0) end
            """;

        LuaAnalysisResult resA = LuaStaticAnalyzer.Analyze(Encoding.Latin1.GetBytes(fileA));
        LuaAnalysisResult resB = LuaStaticAnalyzer.Analyze(Encoding.Latin1.GetBytes(fileB));

        Assert.NotEqual(resA.NormalizedAstHash, resB.NormalizedAstHash);
    }
}
