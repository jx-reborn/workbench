using System.Text;
using JXReborn.ScriptMigrator.Analyzers;
using JXReborn.ScriptMigrator.Models;
using Xunit;

namespace JXReborn.ScriptMigrator.Tests;

public sealed class LuaStaticAnalyzerTests
{
    [Fact]
    public void Analyze_ExtractsTaskTempGlobalIdsAndIncludes()
    {
        const string lua = """
            Include("\\script\\global\\station.lua")
            TSK_MAIN = 1024
            function main()
                local v = GetTask(TSK_MAIN)
                local b = GetByte(GetTask(1025), 2)
                SetTaskTemp(77, 1)
                if GetGlbValue(12) == 0 then
                    Say("Hello", 0)
                end
            end
            """;

        LuaAnalysisResult result = LuaStaticAnalyzer.Analyze(Encoding.Latin1.GetBytes(lua));

        Assert.Contains(1024, result.ReferencedTaskIds);
        Assert.Contains(1025, result.ReferencedTaskIds);
        Assert.Contains(77, result.ReferencedTempTaskIds);
        Assert.Contains(12, result.ReferencedGlobalValueIds);
        Assert.Contains("/script/global/station.lua", result.IncludedFiles);
        Assert.Contains("main", result.DefinedFunctions);
        Assert.False(result.IsPureStaticDialog);
    }

    [Fact]
    public void Analyze_IdentifiesPureStaticDialogAndProducesMatchingAstHashForSameStructure()
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
    public void Analyze_RejectsInteractiveSayMenuAndTalkCallbackFromPureStaticDialog()
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
    public void Analyze_ResolvesWrapperApisAndExternalIncludeConstantsWithoutPollutingTaskZero()
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
        var external = new System.Collections.Generic.Dictionary<string, int>
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
    public void Analyze_DistinguishesNormalizedAstHashWhenIncludeTargetsDiffer()
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
