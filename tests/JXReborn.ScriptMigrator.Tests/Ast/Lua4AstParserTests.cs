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
}
