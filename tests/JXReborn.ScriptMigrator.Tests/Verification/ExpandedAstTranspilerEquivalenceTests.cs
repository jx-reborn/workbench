using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JXReborn.LegacyImporter.Sources;
using JXReborn.Packs.TieuDaoTu.Scripts.Generated;
using JXReborn.ScriptMigrator.Analyzers;
using JXReborn.ScriptMigrator.Ast;
using JXReborn.ScriptMigrator.Generators;
using JXReborn.ScriptMigrator.Models;
using JXReborn.ScriptMigrator.Tests.Fixtures;
using JXReborn.ScriptMigrator.Verification;
using Xunit;

namespace JXReborn.ScriptMigrator.Tests.Verification;

public sealed class ExpandedAstTranspilerEquivalenceTests
{
    private static readonly Lazy<ScriptInventoryManifest> CachedManifest = new(() =>
    {
        var builder = new ScriptInventoryBuilder();
        return builder.Build(LegacyServerTestPaths.FindLegacyBinRoot());
    });

    [Fact]
    public void Transpile_RealSetPropStateReputeHeadAndAllBrotherScripts_ShouldLinkValidateAndEmitCleanCSharp()
    {
        string legacyBin = LegacyServerTestPaths.FindLegacyBinRoot();
        string server1Dir = Path.Combine(legacyBin, "server1");
        string packRoot = LegacyServerTestPaths.FindPackTieuDaoTuRoot();

        ILegacyServerSource source = LegacyServerSourceFactory.Create(server1Dir);
        PackItemCatalogIndex itemIndex = PackItemCatalogIndex.Load(packRoot);
        var parser = new Lua4AstParser();
        var linker = new LuaIncludeLinker(source, parser);
        var validator = new LuaScriptSemanticValidator(itemIndex.EventItemCodeByDetailId, itemIndex);
        var emitter = new LuaCSharpCodeEmitter(itemIndex.EventItemCodeByDetailId, itemIndex);

        (string Suffix, GeneratedScriptKind Kind)[] targets =
        [
            ("江南区/龙泉村/obj/捡拾_白雪百合.lua", GeneratedScriptKind.Object),
            ("江南区/五毒教/入门迷宫/npc/路人_五毒弟子2.lua", GeneratedScriptKind.Npc),
            ("江南区/临安/临安/npc/路人_谢大爷.lua", GeneratedScriptKind.Npc),
        ];

        Assert.NotEmpty(targets);
        var compiledList = new List<CompiledLuaScript>();

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
                linked.LinkedProgram!);

            Assert.Empty(reasons);
            Assert.NotNull(compiled);
            compiledList.Add(compiled);
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "jxreborn-ast-emit-" + Guid.NewGuid().ToString("N"));
        try
        {
            IReadOnlyList<string> written = emitter.EmitAll(tempDir, compiledList);
            Assert.NotEmpty(written);

            string objCode = File.ReadAllText(Path.Combine(tempDir, "GeneratedGeneralObjectScript.cs"));
            Assert.Contains(".SetPropState(", objCode, StringComparison.Ordinal);

            string npcCode = File.ReadAllText(Path.Combine(tempDir, "GeneratedNpcDialogScript.cs"));
            Assert.Contains("Math.Floor(", npcCode, StringComparison.Ordinal);
            Assert.Contains("FormatLegacyDate(", npcCode, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void LuaOracleHarness_RealWuduReturnReputeAndLongQuanPropState_ShouldRecordExactFloatFloorAndPropState()
    {
        string legacyBin = LegacyServerTestPaths.FindLegacyBinRoot();
        string server1Dir = Path.Combine(legacyBin, "server1");
        string packRoot = LegacyServerTestPaths.FindPackTieuDaoTuRoot();

        ILegacyServerSource source = LegacyServerSourceFactory.Create(server1Dir);
        PackItemCatalogIndex itemIndex = PackItemCatalogIndex.Load(packRoot);
        var oracle = new LuaOracleHarness(source, itemIndex.EventItemCodeByDetailId, itemIndex);

        ScriptInventoryEntry propEntry = CachedManifest.Value.Entries.First(static e =>
            e.Utf8DisplayPath.Replace('\\', '/').EndsWith("江南区/龙泉村/obj/捡拾_白雪百合.lua", StringComparison.OrdinalIgnoreCase));
        ScriptEffectTrace propTrace = oracle.Execute(
            propEntry.CanonicalScriptPath,
            GeneratedScriptKind.Object,
            new ScriptStateVector("prop-harvest"));
        Assert.Equal((byte)1, propTrace.ObjectPropState);
        Assert.Contains("GRANT_ITEM(bach-tuyet-bach-hop,1)", propTrace.Events);

        // Wudu disciple quest completion at Level 35: ReturnRepute(25, 29, 5) = floor(25 * (1 - 6 * 5 / 100)) = floor(17.5) = 17!
        ScriptInventoryEntry wuduEntry = CachedManifest.Value.Entries.First(static e =>
            e.Utf8DisplayPath.Replace('\\', '/').EndsWith("江南区/五毒教/入门迷宫/npc/路人_五毒弟子2.lua", StringComparison.OrdinalIgnoreCase));
        var wuduVec = new ScriptStateVector(
            "wudu-lv35-complete",
            Level: 35,
            Repute: 100,
            Tasks: new Dictionary<ushort, int> { [37] = 10 << 8 },
            OwnedItemCodes: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                itemIndex.EventItemCodeByDetailId[223],
                itemIndex.EventItemCodeByDetailId[224],
                itemIndex.EventItemCodeByDetailId[225],
                itemIndex.EventItemCodeByDetailId[226],
                itemIndex.EventItemCodeByDetailId[227],
            });
        ScriptEffectTrace wuduTrace = oracle.Execute(wuduEntry.CanonicalScriptPath, GeneratedScriptKind.Npc, wuduVec);
        Assert.Equal(117, wuduTrace.FinalRepute);
    }

    [Fact]
    public async Task GeneratedPackScripts_RealPropStateReputeFloorAndDateNpcScripts_ShouldMatchLuaOracleExactlyAsync()
    {
        string legacyBin = LegacyServerTestPaths.FindLegacyBinRoot();
        string server1Dir = Path.Combine(legacyBin, "server1");
        string packRoot = LegacyServerTestPaths.FindPackTieuDaoTuRoot();

        ILegacyServerSource source = LegacyServerSourceFactory.Create(server1Dir);
        PackItemCatalogIndex itemIndex = PackItemCatalogIndex.Load(packRoot);
        var oracle = new LuaOracleHarness(source, itemIndex.EventItemCodeByDetailId, itemIndex);

        var genObj = new GeneratedGeneralObjectScript();
        var genNpc = new GeneratedNpcDialogScript();

        // 1. Long Tuyền Thôn harvestable prop (`SetPropState` + `AddEventItem(190)`)
        ScriptInventoryEntry propEntry = CachedManifest.Value.Entries.First(static e =>
            e.Utf8DisplayPath.Replace('\\', '/').EndsWith("江南区/龙泉村/obj/捡拾_白雪百合.lua", StringComparison.OrdinalIgnoreCase));
        string propUtf8Path = "\\" + propEntry.Utf8DisplayPath.Replace('/', '\\')["server1\\".Length..];
        var propVec = new ScriptStateVector("prop-harvest");
        ScriptEffectTrace luaProp = oracle.Execute(propEntry.CanonicalScriptPath, GeneratedScriptKind.Object, propVec);
        var csPropCtx = new RecordingScriptContext(propUtf8Path, propVec);
        await genObj.OnInteractObjectAsync(csPropCtx, CancellationToken.None);
        await csPropCtx.DrainInteractiveContinuationsAsync(CancellationToken.None);
        Assert.Equal(luaProp.ToCanonicalString(), csPropCtx.Trace.ToCanonicalString());
        Assert.Equal((byte)1, csPropCtx.Trace.ObjectPropState);

        // 2. Wudu Disciple 2 at Level 35 (`repute_head.lua` float division `floor(17.5) == 17`)
        ScriptInventoryEntry wuduEntry = CachedManifest.Value.Entries.First(static e =>
            e.Utf8DisplayPath.Replace('\\', '/').EndsWith("江南区/五毒教/入门迷宫/npc/路人_五毒弟子2.lua", StringComparison.OrdinalIgnoreCase));
        string wuduUtf8Path = "\\" + wuduEntry.Utf8DisplayPath.Replace('/', '\\')["server1\\".Length..];
        var wuduVec = new ScriptStateVector(
            "wudu-lv35-complete",
            Level: 35,
            Repute: 100,
            Tasks: new Dictionary<ushort, int> { [37] = 10 << 8 },
            OwnedItemCodes: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                itemIndex.EventItemCodeByDetailId[223],
                itemIndex.EventItemCodeByDetailId[224],
                itemIndex.EventItemCodeByDetailId[225],
                itemIndex.EventItemCodeByDetailId[226],
                itemIndex.EventItemCodeByDetailId[227],
            });
        ScriptEffectTrace luaWudu = oracle.Execute(wuduEntry.CanonicalScriptPath, GeneratedScriptKind.Npc, wuduVec);
        var csWuduCtx = new RecordingScriptContext(wuduUtf8Path, wuduVec);
        await genNpc.OnDialogAsync(csWuduCtx, CancellationToken.None);
        await csWuduCtx.DrainInteractiveContinuationsAsync(CancellationToken.None);
        Assert.Equal(luaWudu.ToCanonicalString(), csWuduCtx.Trace.ToCanonicalString());
        Assert.Equal(117, csWuduCtx.Trace.FinalRepute);

        // 3. Lin'an Tạ Đại Gia (`springfestival08/allbrother/head.lua` + `GetLocalDate` + `tinsert` + nil fallthrough)
        ScriptInventoryEntry xieEntry = CachedManifest.Value.Entries.First(static e =>
            e.Utf8DisplayPath.Replace('\\', '/').EndsWith("江南区/临安/临安/npc/路人_谢大爷.lua", StringComparison.OrdinalIgnoreCase));
        string xieUtf8Path = "\\" + xieEntry.Utf8DisplayPath.Replace('/', '\\')["server1\\".Length..];
        DateTime[] clockProbes =
        [
            new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2008, 2, 10, 12, 0, 0, DateTimeKind.Utc),
        ];
        Assert.NotEmpty(clockProbes);

        foreach (DateTime simClock in clockProbes)
        {
            var xieVec = new ScriptStateVector($"xie-{simClock:yyyyMMdd}", SimulatedLocalTime: simClock, DialogChoices: [0, 0]);
            ScriptEffectTrace luaXie = oracle.Execute(xieEntry.CanonicalScriptPath, GeneratedScriptKind.Npc, xieVec);
            using IDisposable clockScope = GeneratedLuaRuntime.UseDeterministicClock(simClock);
            var csXieCtx = new RecordingScriptContext(xieUtf8Path, xieVec);
            await genNpc.OnDialogAsync(csXieCtx, CancellationToken.None);
            await csXieCtx.DrainInteractiveContinuationsAsync(CancellationToken.None);
            Assert.Equal(luaXie.ToCanonicalString(), csXieCtx.Trace.ToCanonicalString());
        }
    }

    [Fact]
    public void Transpile_RealThamCuuNumericStringIdDialogScript_ShouldValidateAndEmitSayByStringIdTalkByStringIdsAndStoreBoxOps()
    {
        string legacyBin = LegacyServerTestPaths.FindLegacyBinRoot();
        string server1Dir = Path.Combine(legacyBin, "server1");
        string packRoot = LegacyServerTestPaths.FindPackTieuDaoTuRoot();

        ILegacyServerSource source = LegacyServerSourceFactory.Create(server1Dir);
        PackItemCatalogIndex itemIndex = PackItemCatalogIndex.Load(packRoot);
        var parser = new Lua4AstParser();
        var linker = new LuaIncludeLinker(source, parser);
        var validator = new LuaScriptSemanticValidator(itemIndex.EventItemCodeByDetailId, itemIndex);
        var emitter = new LuaCSharpCodeEmitter(itemIndex.EventItemCodeByDetailId, itemIndex);

        ScriptInventoryEntry entry = CachedManifest.Value.Entries.First(static e =>
            e.Utf8DisplayPath.Replace('\\', '/').EndsWith("两湖区/巴陵县/npc/巴陵县_路人10商人对话.lua", StringComparison.OrdinalIgnoreCase));
        string fullPath = Path.Combine(legacyBin, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        LuaParseResult parsed = parser.Parse(File.ReadAllBytes(fullPath));
        Assert.True(parsed.IsSuccess, $"Parse failed: {parsed.FailureReason}");

        LuaLinkResult linked = linker.LinkAndTreeShake(entry.CanonicalScriptPath, parsed.Program!, "main");
        Assert.True(linked.IsSuccess, $"Link failed: {string.Join(", ", linked.BlockingReasons)}");

        string utf8ScriptPath = "\\" + entry.Utf8DisplayPath.Replace('/', '\\')["server1\\".Length..];
        (CompiledLuaScript? compiled, IReadOnlyList<string> reasons) = validator.Validate(
            entry,
            utf8ScriptPath,
            GeneratedScriptKind.Npc,
            linked.LinkedProgram!);

        Assert.Empty(reasons);
        Assert.NotNull(compiled);

        string tempDir = Path.Combine(Path.GetTempPath(), "jxreborn-ast-thamcuu-" + Guid.NewGuid().ToString("N"));
        try
        {
            IReadOnlyList<string> written = emitter.EmitAll(tempDir, [compiled]);
            Assert.NotEmpty(written);

            string npcCode = File.ReadAllText(Path.Combine(tempDir, "GeneratedNpcDialogScript.cs"));
            Assert.Contains("context.SayByStringId(11256,", npcCode, StringComparison.Ordinal);
            Assert.Contains("context.TalkByStringIds([11260],", npcCode, StringComparison.Ordinal);
            Assert.Contains("context.TalkByStringIds([11261],", npcCode, StringComparison.Ordinal);
            Assert.Contains("context.IsExpandableBoxUnlocked(", npcCode, StringComparison.Ordinal);
            Assert.Contains("context.UnlockExpandableBox(", npcCode, StringComparison.Ordinal);
            Assert.Contains("context.AddNote(", npcCode, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
