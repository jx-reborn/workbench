using System;
using System.IO;
using System.Linq;
using JXReborn.ScriptMigrator.Analyzers;
using JXReborn.ScriptMigrator.Ast;
using JXReborn.ScriptMigrator.Models;
using JXReborn.ScriptMigrator.Tests.Fixtures;
using Xunit;

namespace JXReborn.ScriptMigrator.Tests.Ast;

public sealed class Lua4AstParserTests
{
    private static readonly Lazy<ScriptInventoryManifest> CachedManifest = new(() =>
    {
        var builder = new ScriptInventoryBuilder();
        return builder.Build(LegacyServerTestPaths.FindLegacyBinRoot());
    });

    private static byte[] ReadLegacyLuaByUtf8Suffix(string utf8Suffix)
    {
        string legacyBin = LegacyServerTestPaths.FindLegacyBinRoot();
        ScriptInventoryManifest manifest = CachedManifest.Value;
        ScriptInventoryEntry entry = manifest.Entries.First(e =>
            e.Utf8DisplayPath.Replace('\\', '/').EndsWith(utf8Suffix, StringComparison.OrdinalIgnoreCase));
        string diskPath = Path.Combine(legacyBin, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.ReadAllBytes(diskPath);
    }

    [Fact]
    public void Parse_RealXiangyangNorthGateTrap_ShouldProduceExactIfElseSetPosAndProtectAst()
    {
        byte[] bytes = ReadLegacyLuaByUtf8Suffix("中原南区/襄阳/襄阳/trap/襄阳北门.lua");
        var parser = new Lua4AstParser();
        LuaParseResult result = parser.Parse(bytes);

        Assert.True(result.IsSuccess, $"Expected parse success, got: {result.FailureReason}");
        Assert.NotNull(result.Program);
        LuaFunctionDecl mainFn = Assert.Single(result.Program.Functions);
        Assert.Equal("main", mainFn.Name);
        Assert.Contains(mainFn.Body, static s => s is LuaIfStmt);
        Assert.Contains(mainFn.Body, static s => s is LuaCallStmt { Call.CalleeName: "AddStation" });
        Assert.Contains(mainFn.Body, static s => s is LuaCallStmt { Call.CalleeName: "SetProtectTime" });
        Assert.Contains(mainFn.Body, static s => s is LuaCallStmt { Call.CalleeName: "AddSkillState" });
    }

    [Fact]
    public void Parse_RealXiangyangSignboardAndLadderAndBox_ShouldDecodeExactVietnameseAndNumbers()
    {
        var parser = new Lua4AstParser();

        byte[] signBytes = ReadLegacyLuaByUtf8Suffix("中原南区/襄阳/襄阳/obj/襄阳-路标1.lua");
        LuaParseResult signResult = parser.Parse(signBytes);
        Assert.True(signResult.IsSuccess, signResult.FailureReason);
        var signCall = Assert.IsType<LuaCallStmt>(Assert.Single(Assert.Single(signResult.Program!.Functions).Body));
        Assert.Equal("Say", signCall.Call.CalleeName);
        var promptExpr = Assert.IsType<LuaStringExpr>(signCall.Call.Arguments[0]);
        Assert.Equal("Tương Dương <---->Chiến trường Tống Kim", promptExpr.DecodedUtf8);

        byte[] ladderBytes = ReadLegacyLuaByUtf8Suffix("中原南区/襄阳/襄阳/obj/襄阳-告示牌1.lua");
        LuaParseResult ladderResult = parser.Parse(ladderBytes);
        Assert.True(ladderResult.IsSuccess, ladderResult.FailureReason);
        var ladderCall = Assert.IsType<LuaCallStmt>(Assert.Single(Assert.Single(ladderResult.Program!.Functions).Body));
        Assert.Equal("ShowLadder", ladderCall.Call.CalleeName);
        Assert.Equal(4, ladderCall.Call.Arguments.Count);
        Assert.Equal(3.0, Assert.IsType<LuaNumberExpr>(ladderCall.Call.Arguments[0]).Value);
        Assert.Equal(1.0, Assert.IsType<LuaNumberExpr>(ladderCall.Call.Arguments[1]).Value);
        Assert.Equal(2.0, Assert.IsType<LuaNumberExpr>(ladderCall.Call.Arguments[2]).Value);
        Assert.Equal(3.0, Assert.IsType<LuaNumberExpr>(ladderCall.Call.Arguments[3]).Value);

        byte[] boxBytes = ReadLegacyLuaByUtf8Suffix("江南区/五毒教/五毒教/obj/五毒教储物箱1.lua");
        LuaParseResult boxResult = parser.Parse(boxBytes);
        Assert.True(boxResult.IsSuccess, boxResult.FailureReason);
        LuaFunctionDecl boxMain = Assert.Single(boxResult.Program!.Functions);
        Assert.Equal(2, boxMain.Body.Count);
        Assert.IsType<LuaCallStmt>(boxMain.Body[0]);
        Assert.IsType<LuaIfStmt>(boxMain.Body[1]);
    }

    [Fact]
    public void Parse_RealComplexScriptWithLoopsOrMethodCalls_ShouldFailClosedWithExplicitReason()
    {
        var parser = new Lua4AstParser();
        byte[] bytes = ReadLegacyLuaByUtf8Suffix("global/seasonnpc.lua");
        LuaParseResult result = parser.Parse(bytes);

        Assert.False(result.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
    }

    [Fact]
    public void Parse_RealWuduWeaponMerchant_ShouldParseMultipleFunctionsAndOrCondition()
    {
        var parser = new Lua4AstParser();
        byte[] bytes = ReadLegacyLuaByUtf8Suffix("江南区/五毒教/五毒教/npc/五毒卖武器.lua");
        LuaParseResult result = parser.Parse(bytes);

        Assert.True(result.IsSuccess, result.FailureReason);
        Assert.NotNull(result.Program);
        Assert.Equal(3, result.Program.Functions.Count);
        Assert.Contains(result.Program.Functions, static f => f.Name == "main");
        Assert.Contains(result.Program.Functions, static f => f.Name == "yes");
        Assert.Contains(result.Program.Functions, static f => f.Name == "no");
    }

    [Fact]
    public void Parse_RealNewTaskHeadIncludeString_ShouldPreserveBackslashTAndNotDoubleUnescapeTab()
    {
        var parser = new Lua4AstParser();
        byte[] bytes = ReadLegacyLuaByUtf8Suffix("task/newtask/newtask_head.lua");
        LuaParseResult result = parser.Parse(bytes);

        Assert.True(result.IsSuccess, $"Expected parse success, got: {result.FailureReason}");
        Assert.NotNull(result.Program);
        Assert.NotEmpty(result.Program.TopLevelStatements);

        var includeCall = Assert.IsType<LuaCallStmt>(result.Program.TopLevelStatements[0]);
        Assert.Equal("Include", includeCall.Call.CalleeName);
        var pathArg = Assert.IsType<LuaStringExpr>(Assert.Single(includeCall.Call.Arguments));
        Assert.Equal(@"\script\task\task_award_extend.lua", pathArg.DecodedUtf8);
    }

    [Fact]
    public void Parse_RealCheckMapIdBuyItemAndPilidanScripts_ShouldParseForLoopsMultiAssignUpvaluesAndUnmaskOopReason()
    {
        var parser = new Lua4AstParser();

        byte[] checkMapBytes = ReadLegacyLuaByUtf8Suffix("item/checkmapid.lua");
        LuaParseResult checkMapResult = parser.Parse(checkMapBytes);
        Assert.True(checkMapResult.IsSuccess, $"checkmapid.lua parse failed: {checkMapResult.FailureReason}");
        LuaFunctionDecl checkMain = Assert.Single(checkMapResult.Program!.Functions);
        Assert.Contains(checkMain.Body, static s => s is LuaMultiAssignStmt { TargetNames: ["W", "X", "Y"] });
        Assert.Contains(checkMain.Body, static s => s is LuaNumericForStmt { IteratorName: "i" });

        byte[] buyItemBytes = ReadLegacyLuaByUtf8Suffix("item/dynamic_shop/buy_item.lua");
        LuaParseResult buyItemResult = parser.Parse(buyItemBytes);
        Assert.True(buyItemResult.IsSuccess, $"buy_item.lua parse failed: {buyItemResult.FailureReason}");
        LuaFunctionDecl buyFn = Assert.Single(buyItemResult.Program!.Functions, static f => f.Name == "BuyItem");
        var localStmt = Assert.IsType<LuaLocalDeclStmt>(buyFn.Body[0]);
        var indexExpr = Assert.IsType<LuaIndexExpr>(localStmt.Initializer);
        var upval = Assert.IsType<LuaUpvalueExpr>(indexExpr.Target);
        Assert.Equal("tbProcessRequest", upval.Name);

        byte[] pilidanBytes = ReadLegacyLuaByUtf8Suffix("activitysys/config/39/pilidan.lua");
        LuaParseResult pilidanResult = parser.Parse(pilidanBytes);
        Assert.True(pilidanResult.IsSuccess, $"pilidan.lua parse failed: {pilidanResult.FailureReason}");
        LuaFunctionDecl pilidanMain = Assert.Single(pilidanResult.Program!.Functions, static f => f.Name == "main");
        Assert.Contains(
            pilidanMain.Body,
            static s => s is LuaIfStmt
            {
                Condition: LuaBinaryExpr
                {
                    Left: LuaMemberCallExpr { Receiver: LuaIdentifierExpr { Name: "PlayerFunLib" }, IsColonCall: true }
                }
            } or LuaIfStmt
            {
                Condition: LuaUnaryExpr
                {
                    Operand: LuaMemberCallExpr { Receiver: LuaIdentifierExpr { Name: "PlayerFunLib" }, IsColonCall: true }
                }
            } or LuaMemberCallStmt { Call: { Receiver: LuaIdentifierExpr { Name: "PlayerFunLib" }, IsColonCall: true } });
    }

    [Fact]
    public void ParseLibrary_RealIncludeGuardedHeadersAndOopLibraries_ShouldExtractFunctionsAndPoisonOopTables()
    {
        var parser = new Lua4AstParser();

        string[] guardedHeaders =
        [
            "lib/string.lua",
            "lib/common.lua",
            "lib/basic.lua",
            "tong/tong_award_head.lua",
        ];
        Assert.NotEmpty(guardedHeaders);

        foreach (string suffix in guardedHeaders)
        {
            LuaParseResult res = parser.ParseLibrary(ReadLegacyLuaByUtf8Suffix(suffix));
            Assert.True(res.IsSuccess, $"ParseLibrary failed on {suffix}: {res.FailureReason}");
            Assert.NotNull(res.Program);
            Assert.NotEmpty(res.Program.Functions);
        }

        LuaParseResult huangRes = parser.ParseLibrary(ReadLegacyLuaByUtf8Suffix("event/great_night/huangzhizhang/event.lua"));
        Assert.True(huangRes.IsSuccess, $"ParseLibrary failed on huangzhizhang/event.lua: {huangRes.FailureReason}");
        Assert.NotNull(huangRes.Program);
        Assert.NotEmpty(huangRes.Program.Functions);

        LuaParseResult awardTpl = parser.ParseLibrary(ReadLegacyLuaByUtf8Suffix("lib/awardtemplet.lua"));
        Assert.True(awardTpl.IsSuccess, $"ParseLibrary failed on awardtemplet.lua: {awardTpl.FailureReason}");
        Assert.NotNull(awardTpl.Program);
        Assert.Contains(
            awardTpl.Program.TopLevelStatements,
            static s => s is LuaMemberAssignStmt
            {
                Target: LuaIdentifierExpr { Name: "tbAwardTemplet" },
                FieldName: "TYPE"
            });
    }
}
