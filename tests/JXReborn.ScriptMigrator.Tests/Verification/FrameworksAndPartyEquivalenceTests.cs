using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using JXReborn.LegacyImporter.Sources;
using JXReborn.Packs.TieuDaoTu.Hosting;
using JXReborn.ScriptMigrator.Analyzers;
using JXReborn.ScriptMigrator.Ast;
using JXReborn.ScriptMigrator.Generators;
using JXReborn.ScriptMigrator.Models;
using JXReborn.ScriptMigrator.Tests.Fixtures;
using Xunit;

namespace JXReborn.ScriptMigrator.Tests.Verification;

/// <summary>
/// Ground-truth tests verifying compilation and equivalence for:
/// 1) The final 3 Map Objects (<c>捡拾_kll60_宝箱.lua</c>, <c>西南北区-唐门-告示牌5.lua</c>, <c>捡拾_cyl20_大曼陀罗花.lua</c>);
/// 2) Shared-header isolation (<c>IncludeLib("LEAGUE")</c>, <c>g_dialog.lua</c>, <c>activitydetail.lua</c>);
/// 3) 2D arrays, tuple splatting (<c>RevID2WXY(GetPlayerRev())</c>), <c>SetTempRevPos</c>, <c>DisabledUseTownP</c>;
/// 4) Party <c>PlayerIndex</c> + <c>SetMemberTask</c> callback monomorphization &amp; synchronous <c>Talk</c> in <c>OnDeath</c>;
/// 5) Heterogeneous tuple tables (<c>dafengbao.lua</c> + <c>AddGoldItem(0, 164)</c>), <c>tbAwardTemplet</c>, and <c>CreateNewSayEx</c>.
/// </summary>
public sealed class FrameworksAndPartyEquivalenceTests
{
    private static readonly Lazy<ScriptInventoryManifest> CachedManifest = new(() =>
    {
        var builder = new ScriptInventoryBuilder();
        return builder.Build(LegacyServerTestPaths.FindLegacyBinRoot());
    });

    [Fact]
    public void Transpile_FinalThreeMapObjectsTrapsMonstersItemsAndNpcs_ShouldLinkValidateAndEmitCleanCSharp()
    {
        string legacyBin = LegacyServerTestPaths.FindLegacyBinRoot();
        string server1Dir = Path.Combine(legacyBin, "server1");
        string packRoot = LegacyServerTestPaths.FindPackTieuDaoTuRoot();

        ILegacyServerSource source = LegacyServerSourceFactory.Create(server1Dir);
        PackItemCatalogIndex itemIndex = PackItemCatalogIndex.Load(packRoot);

        // Ground-truth gold-equip row 164 == An Bang Băng Tinh Thạch Hạng Liên (`dafengbao.lua` AddGoldItem(0, 164))
        Assert.True(itemIndex.TryResolveGoldEquipByRowId(164, out PackItemEntry gold164));
        Assert.Equal("an-bang-bang-tinh-thach-hang-lien-hk164", gold164.Code);

        var parser = new Lua4AstParser();
        var linker = new LuaIncludeLinker(source, parser);
        var validator = new LuaScriptSemanticValidator(itemIndex.EventItemCodeByDetailId, itemIndex);
        var emitter = new LuaCSharpCodeEmitter(itemIndex.EventItemCodeByDetailId, itemIndex);

        (string Suffix, GeneratedScriptKind Kind)[] targets =
        [
            // Final 3 Map Objects in the entire 952-map world
            ("西北北区/黄河源头/留仙洞六层/obj/捡拾_kll60_宝箱.lua", GeneratedScriptKind.Object),
            ("西南北区/唐门/唐门/obj/西南北区-唐门-告示牌5.lua", GeneratedScriptKind.Object),
            ("西南南区/翠烟门/翠烟门/obj/捡拾_cyl20_大曼陀罗花.lua", GeneratedScriptKind.Object),

            // Traps: 2D array (`CSP_ClearTrapTab`), `RevID2WXY(GetPlayerRev())` tuple splatting, `SetTempRevPos`, `DisabledUseTownP`
            ("global/特殊用地/梦境/trap/战斗切换点1.lua", GeneratedScriptKind.Trap),
            ("东北区/长白山/长白山北麓/trap/长白山北麓to长白山脚1.lua", GeneratedScriptKind.Trap),
            ("tong/map/bianjing/spacenorth_trap.lua", GeneratedScriptKind.Trap),
            ("东北区/漠北草原/trap/trap_tolinyuguan.lua", GeneratedScriptKind.Trap),

            // Monsters: Party `PlayerIndex` + `SetMemberTask(..., fnCallback)` + sync `Talk` + integer & string-name `GetItemCount`
            ("task/newtask/master/zhongli/enemy_shaolinkongji.lua", GeneratedScriptKind.Monster),
            ("task/newtask/branch/zhengpai/enemy_cangbaoke.lua", GeneratedScriptKind.Monster),
            ("两湖区/天王帮/天王岛山洞/npc/twenemy01/twenemy01.lua", GeneratedScriptKind.Monster),

            // Items: Heterogeneous tuple rows + `AddGoldItem`, `tbAwardTemplet:Give` + keyed table, and `IncludeLib("LEAGUE")` + `GiveAwardByList`
            ("event/menglan_2006/dafengbao.lua", GeneratedScriptKind.Item),
            ("event/jiefangri2011/item/huanhundanlibao.lua", GeneratedScriptKind.Item),
            ("global/nobitaxd/item/lebaovlhx.lua", GeneratedScriptKind.Item),

            // NPCs: `CreateNewSayEx` + tuple table, `DailogClass` + `g_dialog.lua` / `g_task.lua` header isolation, `SetFaction`
            ("global/npc/chuanfu.lua", GeneratedScriptKind.Npc),
            ("missions/yuegedao/yuegedao/chuanfu.lua", GeneratedScriptKind.Npc),
            ("两湖区/南岳镇/npc/南岳镇_峨嵋弟子对话.lua", GeneratedScriptKind.Npc),
        ];

        Assert.NotEmpty(targets);
        var compiledList = new List<CompiledLuaScript>();
        var includedHeaderShaByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach ((string suffix, GeneratedScriptKind kind) in targets)
        {
            ScriptInventoryEntry entry = CachedManifest.Value.Entries.First(e =>
                e.Utf8DisplayPath.Replace('\\', '/').EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            string fullPath = Path.Combine(legacyBin, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            LuaParseResult parsed = parser.Parse(File.ReadAllBytes(fullPath));
            Assert.True(parsed.IsSuccess, $"Parse failed for {suffix}: {parsed.FailureReason}");

            string expectedEntry = kind == GeneratedScriptKind.Monster ? "OnDeath" : "main";
            LuaLinkResult linked = linker.LinkAndTreeShake(entry.CanonicalScriptPath, parsed.Program!, expectedEntry);
            Assert.True(linked.IsSuccess, $"Link failed for {suffix}: {string.Join(", ", linked.BlockingReasons)}");

            string utf8ScriptPath = "\\" + entry.Utf8DisplayPath.Replace('/', '\\')["server1\\".Length..];
            (CompiledLuaScript? compiled, IReadOnlyList<string> reasons) = validator.Validate(
                entry,
                utf8ScriptPath,
                kind,
                linked.LinkedProgram!,
                linked.IncludedVirtualKeys);

            Assert.True(reasons.Count == 0, $"Validation failed for {suffix}: {string.Join(", ", reasons)}");
            Assert.NotNull(compiled);
            compiledList.Add(compiled);

            foreach (string vKey in linked.IncludedVirtualKeys)
            {
                if (source.TryReadServerFile(vKey, out byte[]? hdrBytes))
                {
                    string utf8Hdr = LegacyScriptPathNormalizer.NormalizeToUtf8(vKey);
                    includedHeaderShaByPath[utf8Hdr] = Convert.ToHexStringLower(SHA256.HashData(hdrBytes));
                }
            }
        }

        Assert.NotEmpty(includedHeaderShaByPath);

        string tempDir = Path.Combine(Path.GetTempPath(), "jxreborn-fw-party-" + Guid.NewGuid().ToString("N"));
        try
        {
            IReadOnlyList<string> written = emitter.EmitAll(tempDir, compiledList, includedHeaderShaByPath);
            Assert.NotEmpty(written);
            Assert.True(File.Exists(Path.Combine(tempDir, "GeneratedGeneralObjectScript.cs")));
            Assert.True(File.Exists(Path.Combine(tempDir, "GeneratedMonsterDeathScript.cs")));
            Assert.True(File.Exists(Path.Combine(tempDir, "GeneratedGeneralItemScript.cs")));
            Assert.True(File.Exists(Path.Combine(tempDir, "GeneratedNpcDialogScript.cs")));

            string headerProvenanceFile = Path.Combine(tempDir, "GeneratedIncludedHeaderProvenance.cs");
            Assert.True(File.Exists(headerProvenanceFile));
            string provenanceSource = File.ReadAllText(headerProvenanceFile);
            Assert.Contains("Sha256ByHeaderPath", provenanceSource, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("activitysys/npcdailog.lua")]
    [InlineData("global/粽嫂.lua")]
    public void Validate_ScriptsCallingDynamicActivityOrTaskHooks_ShouldFailClosed(string suffix)
    {
        string legacyBin = LegacyServerTestPaths.FindLegacyBinRoot();
        string server1Dir = Path.Combine(legacyBin, "server1");
        string packRoot = LegacyServerTestPaths.FindPackTieuDaoTuRoot();

        ILegacyServerSource source = LegacyServerSourceFactory.Create(server1Dir);
        PackItemCatalogIndex itemIndex = PackItemCatalogIndex.Load(packRoot);
        var parser = new Lua4AstParser();
        var linker = new LuaIncludeLinker(source, parser);
        var validator = new LuaScriptSemanticValidator(itemIndex.EventItemCodeByDetailId, itemIndex);

        ScriptInventoryEntry entry = CachedManifest.Value.Entries.First(e =>
            e.Utf8DisplayPath.Replace('\\', '/').EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        string fullPath = Path.Combine(legacyBin, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        LuaParseResult parsed = parser.Parse(File.ReadAllBytes(fullPath));
        Assert.True(parsed.IsSuccess, $"Expected {suffix} to parse cleanly, got: {parsed.FailureReason}");

        LuaLinkResult linked = linker.LinkAndTreeShake(entry.CanonicalScriptPath, parsed.Program!, "main");
        Assert.True(linked.IsSuccess, $"Expected {suffix} headers to isolate cleanly, got: {string.Join(", ", linked.BlockingReasons)}");

        string utf8ScriptPath = "\\" + entry.Utf8DisplayPath.Replace('/', '\\')["server1\\".Length..];
        (CompiledLuaScript? compiled, IReadOnlyList<string> reasons) = validator.Validate(
            entry,
            utf8ScriptPath,
            GeneratedScriptKind.Npc,
            linked.LinkedProgram!,
            linked.IncludedVirtualKeys);

        Assert.Null(compiled);
        Assert.NotEmpty(reasons);
        Assert.Contains(
            reasons,
            r => r.Contains("G_ACTIVITY", StringComparison.Ordinal) ||
                 r.Contains("G_TASK", StringComparison.Ordinal) ||
                 r.Contains("EventSys", StringComparison.Ordinal));
    }
}
