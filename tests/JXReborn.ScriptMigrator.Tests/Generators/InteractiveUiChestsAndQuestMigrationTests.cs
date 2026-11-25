using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using JXReborn.LegacyImporter.Sources;
using JXReborn.Packs.TieuDaoTu.Hosting;
using JXReborn.Packs.TieuDaoTu.Scripts.Generated;
using JXReborn.ScriptMigrator.Analyzers;
using JXReborn.ScriptMigrator.Ast;
using JXReborn.ScriptMigrator.Generators;
using JXReborn.ScriptMigrator.Models;
using JXReborn.ScriptMigrator.Tests.Fixtures;
using JXReborn.ScriptMigrator.Verification;
using JXReborn.ZoneEngine.Scripting;
using Xunit;

namespace JXReborn.ScriptMigrator.Tests.Generators;

/// <summary>
/// Ground-truth migration tests for Phase 4 gameplay script clusters:
/// 1) Zero-byte traps, missing coachman wrappers, header-defined NPC <c>main</c>, <c>inherit(...)</c> isolation, and unreachable const-branch pruning;
/// 2) Cyclic UI dialog menus, <c>/#func(...)</c>, <c>Prise</c> hole functions with 2D local tables and Lua 4.0 extra-arg dropping, and <c>AskClientForNumber</c>;
/// 3) Monster <c>OnDeath</c> <c>Talk</c> continuations + fire-and-forget teleports, character attribute/skill point awards, <c>GetLeavePos</c>, <c>LeaveTeam</c>/<c>SetCreateTeam</c>/<c>SetDeathScript</c>, and <c>BT_LeaveBattle</c>;
/// 4) Chest rewards, <c>IsMyItem</c>/<c>RemoveItemByIndex</c>/<c>GetItemName</c>/<c>SyncItem</c>, expiration date helpers, and <c>CallBack</c>/<c>pFun</c> lowering.
/// </summary>
public sealed class InteractiveUiChestsAndQuestMigrationTests
{
    private static readonly Lazy<ScriptInventoryManifest> CachedManifest = new(() =>
    {
        var builder = new ScriptInventoryBuilder();
        return builder.Build(LegacyServerTestPaths.FindLegacyBinRoot());
    });

    [Fact]
    public void CoachmanWrappers_ManifestEntries_ShouldIncludeFengxiangChengduAndJiangjin()
    {
        var manifestPaths = TieuDaoTuExactScriptPathManifest.DialogNpcPaths.Keys;
        Assert.NotEmpty(manifestPaths);

        string[] expectedCoachmen =
        [
            "\\script\\西北南区\\凤翔\\职能npc\\西北南区-凤翔府-驿站车夫对话.lua",
            "\\script\\西南北区\\成都\\成都\\职能npc\\西南北区-成都府--驿站车夫对话.lua",
            "\\script\\西南北区\\江津村\\职能npc\\西南北区--江津村-驿站车夫对话.lua",
        ];

        Assert.NotEmpty(expectedCoachmen);
        foreach (string expectedPath in expectedCoachmen)
        {
            Assert.Contains(expectedPath, manifestPaths, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Transpile_PhaseFourClusters_ShouldLinkValidateAndEmitCleanCSharp()
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

        (string Suffix, GeneratedScriptKind Kind, string EntryFn)[] targets =
        [
            // Cluster A: 0-byte Cuiyan traps & GetTripMode/BT_LeaveBattle trap
            ("西南南区/翠烟门/翠烟门/trap/翠烟门to门楼.lua", GeneratedScriptKind.Trap, "main"),
            ("西南南区/翠烟门/门楼/trap/门楼to翠烟门.lua", GeneratedScriptKind.Trap, "main"),
            ("global/特殊用地/宋金报名点/trap/jin_trap.lua", GeneratedScriptKind.Trap, "main"),

            // Cluster B: Cyclic UI menus, Prise + 2D local string table, AskClientForNumber, const dead-code pruning
            ("中原北区/少林派/少林派/npc/少林帮助npc.lua", GeneratedScriptKind.Npc, "main"),
            ("两湖区/天王帮/天王帮/npc/天王帮帮助npc.lua", GeneratedScriptKind.Npc, "main"),
            ("中原北区/少林派/罗汉堂/npc/路人_玄难.lua", GeneratedScriptKind.Npc, "main"),
            ("global/nobitaxd/npc/baotinminhchau.lua", GeneratedScriptKind.Npc, "main"),
            ("西北南区/凤翔/路人npc/passerby_aoyunzong.lua", GeneratedScriptKind.Npc, "main"),

            // Cluster C: Monster OnDeath Talk/Teleport & attribute points
            ("江南区/临安/莫空月居所/npc/战斗_莫空月.lua", GeneratedScriptKind.Monster, "OnDeath"),
            ("江南区/五毒教/战斗金蛇寨/npc/战斗_云不邪.lua", GeneratedScriptKind.Monster, "OnDeath"),

            // Cluster D & E: Chests, item metadata/expiration, CallBack & pFun lowering
            ("activitysys/config/1002/bronzechest.lua", GeneratedScriptKind.Item, "main"),
            ("event/great_night/yubi_baoxiang.lua", GeneratedScriptKind.Item, "main"),
            ("item/hongbaoclb.lua", GeneratedScriptKind.Item, "main"),
            ("event/bingo_machine/bingo_machine_box.lua", GeneratedScriptKind.Item, "main"),
        ];

        Assert.NotEmpty(targets);
        var compiledList = new List<CompiledLuaScript>();
        var failures = new List<string>();
        foreach ((string suffix, GeneratedScriptKind kind, string entryFn) in targets)
        {
            ScriptInventoryEntry? entry = CachedManifest.Value.Entries.FirstOrDefault(e =>
                e.Utf8DisplayPath.Replace('\\', '/').EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                failures.Add($"[MISSING_ENTRY] {suffix}");
                continue;
            }

            string fullPath = Path.Combine(legacyBin, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            LuaParseResult parsed = parser.Parse(File.ReadAllBytes(fullPath));
            if (!parsed.IsSuccess)
            {
                failures.Add($"[PARSE] {suffix}: {parsed.FailureReason}");
                continue;
            }

            LuaProgram entryProgram = LuaToCSharpGenerator.EnsureZeroByteTrapEntrypoint(kind, parsed.Program!);
            LuaLinkResult linked = linker.LinkAndTreeShake(entry.CanonicalScriptPath, entryProgram, entryFn);
            if (!linked.IsSuccess)
            {
                failures.Add($"[LINK] {suffix}: {string.Join("; ", linked.BlockingReasons)}");
                continue;
            }

            string utf8ScriptPath = "\\" + entry.Utf8DisplayPath.Replace('/', '\\')["server1\\".Length..];
            (CompiledLuaScript? compiled, IReadOnlyList<string> reasons) = validator.Validate(
                entry,
                utf8ScriptPath,
                kind,
                linked.LinkedProgram!,
                linked.IncludedVirtualKeys);

            if (compiled is null)
            {
                failures.Add($"[VALIDATE] {suffix}: {string.Join("; ", reasons)}");
                continue;
            }

            compiledList.Add(compiled);
        }

        Assert.True(failures.Count == 0, "Phase 4 migration failures:\n" + string.Join("\n", failures));
        string tempOutDir = Path.Combine(Path.GetTempPath(), "jxr-phase4-emit-" + Guid.NewGuid().ToString("N"));
        try
        {
            IReadOnlyList<string> emittedFiles = emitter.EmitAll(tempOutDir, compiledList);
            Assert.NotEmpty(emittedFiles);
        }
        finally
        {
            if (Directory.Exists(tempOutDir))
            {
                Directory.Delete(tempOutDir, recursive: true);
            }
        }
    }

    [Fact]
    public void ParseAndClassify_KTabExAndHeaderMainNpc_ShouldSucceed()
    {
        string legacyBin = LegacyServerTestPaths.FindLegacyBinRoot();
        string server1Dir = Path.Combine(legacyBin, "server1");
        ILegacyServerSource source = LegacyServerSourceFactory.Create(server1Dir);
        var parser = new Lua4AstParser();

        string ktabexPath = Path.Combine(server1Dir, "script", "class", "ktabex.lua");
        Assert.True(File.Exists(ktabexPath));
        LuaParseResult ktabexParsed = parser.Parse(File.ReadAllBytes(ktabexPath));
        Assert.True(ktabexParsed.IsSuccess, $"Expected class/ktabex.lua to parse cleanly, got: {ktabexParsed.FailureReason}");

        string guardPath = Path.Combine(server1Dir, "script", "中原北区", "汴京", "汴京", "npc", "汴京_城门卫兵1对话.lua");
        // Disk path may be mojibake-encoded; resolve through ScriptInventoryManifest
        ScriptInventoryEntry guardEntry = CachedManifest.Value.Entries.First(e =>
            e.Utf8DisplayPath.Replace('\\', '/').EndsWith("中原北区/汴京/汴京/npc/汴京_城门卫兵1对话.lua", StringComparison.OrdinalIgnoreCase));
        string fullGuardPath = Path.Combine(legacyBin, guardEntry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        LuaParseResult guardParsed = parser.Parse(File.ReadAllBytes(fullGuardPath));
        Assert.True(guardParsed.IsSuccess, guardParsed.FailureReason);

        var linker = new LuaIncludeLinker(source, parser);
        LuaLinkResult guardLinked = linker.LinkAndTreeShake(guardEntry.CanonicalScriptPath, guardParsed.Program!, "main");
        Assert.True(guardLinked.IsSuccess, string.Join("; ", guardLinked.BlockingReasons));
        Assert.Contains(guardLinked.LinkedProgram!.Functions, f => string.Equals(f.Name, "main", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Execute_PhaseFourClusters_ShouldMatchLuaOracleAndDecodeCleanUtf8()
    {
        string legacyBin = LegacyServerTestPaths.FindLegacyBinRoot();
        string server1Dir = Path.Combine(legacyBin, "server1");
        string packRoot = LegacyServerTestPaths.FindPackTieuDaoTuRoot();

        ILegacyServerSource source = LegacyServerSourceFactory.Create(server1Dir);
        PackItemCatalogIndex itemIndex = PackItemCatalogIndex.Load(packRoot);
        var luaOracle = new LuaOracleHarness(source, itemIndex.EventItemCodeByDetailId, itemIndex);

        var instanceByUtf8Path = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        Assembly packAssembly = typeof(TieuDaoTuScriptCodeResolver).Assembly;
        foreach (Type type in packAssembly.GetTypes())
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

            object? inst = Activator.CreateInstance(type);
            if (inst is null)
            {
                continue;
            }

            foreach (LegacySourceAttribute attr in attrs)
            {
                instanceByUtf8Path.TryAdd(LegacyScriptPathNormalizer.NormalizeToUtf8(attr.SourceScriptPath), inst);
            }
        }

        Assert.NotEmpty(instanceByUtf8Path);

        MethodInfo? clockScopeMethod = typeof(GeneratedLuaRuntime).GetMethod(
            "UseDeterministicClock",
            BindingFlags.Public | BindingFlags.Static,
            [typeof(DateTime)]);

        Assert.True(itemIndex.TryResolveByGdpl(6, 1, 2744, -1, out PackItemEntry ruYiKeyItem));

        (string Suffix, GeneratedScriptKind Kind, ScriptStateVector Vector, Action<ScriptEffectTrace> ExtraCheck)[] cases =
        [
            (
                "global/nobitaxd/npc/baotinminhchau.lua",
                GeneratedScriptKind.Npc,
                new ScriptStateVector("btmc-buy", InventoryMoney: 5_000_000, DialogChoices: [0], NumberInputs: [2]),
                trace => Assert.Contains("ASK_NUM(", string.Join('\n', trace.Events), StringComparison.Ordinal)
            ),
            (
                "item/hongbaoclb.lua",
                GeneratedScriptKind.Item,
                new ScriptStateVector("hongbao-expired", ItemExpirationUnixSeconds: 1700000000u),
                trace =>
                {
                    Assert.Equal("ConsumeOne", trace.ItemResult);
                    Assert.Contains("Vật phẩm quá hạn sử dụng", string.Join('\n', trace.Events), StringComparison.Ordinal);
                }
            ),
            (
                "item/hongbaoclb.lua",
                GeneratedScriptKind.Item,
                new ScriptStateVector("hongbao-valid", FreeBagCells: 5, ItemExpirationUnixSeconds: 1893456000u),
                trace =>
                {
                    Assert.Equal("ConsumeOne", trace.ItemResult);
                    Assert.True(trace.ExpGranted >= 50_000_000L, $"Expected >= 50M exp, got {trace.ExpGranted}");
                }
            ),
            (
                "event/great_night/yubi_baoxiang.lua",
                GeneratedScriptKind.Item,
                new ScriptStateVector(
                    "yubi-fresh",
                    FreeBagCells: 5,
                    OwnedItemCounts: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [ruYiKeyItem.Code] = 2 }),
                trace =>
                {
                    Assert.Contains($"CONSUME_ITEM({ruYiKeyItem.Code},1)", trace.Events);
                    Assert.True(
                        trace.ExpGranted > 0 || trace.Events.Any(static e => e.StartsWith("GRANT_", StringComparison.Ordinal)),
                        "Expected yubi_baoxiang to grant exp or item when Chìa Khóa Như Ý is owned.");
                }
            ),
            (
                "event/great_night/yubi_baoxiang.lua",
                GeneratedScriptKind.Item,
                new ScriptStateVector(
                    "yubi-capped",
                    FreeBagCells: 5,
                    OwnedItemCounts: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [ruYiKeyItem.Code] = 2 },
                    Tasks: new Dictionary<ushort, int> { [2744] = 20261123, [2706] = 50 << 22 }),
                trace =>
                {
                    Assert.Contains($"CONSUME_ITEM({ruYiKeyItem.Code},1)", trace.Events);
                    Assert.Equal("ConsumeOne", trace.ItemResult);
                }
            ),
            (
                "activitysys/config/1002/bronzechest.lua",
                GeneratedScriptKind.Item,
                new ScriptStateVector("bronzechest-opt0", DialogChoices: [0]),
                trace =>
                {
                    string joined = string.Join('\n', trace.Events);
                    Assert.Contains("Đồ Phổ Tử Mãng Khí Giới", joined, StringComparison.Ordinal);
                    Assert.Contains("GRANT_ITEM(", joined, StringComparison.Ordinal);
                }
            ),
            (
                "event/bingo_machine/bingo_machine_box.lua",
                GeneratedScriptKind.Item,
                new ScriptStateVector("bingo-3053", FreeBagCells: 10, ParticularType: 3053),
                trace => Assert.Contains("NEWS(", string.Join('\n', trace.Events), StringComparison.Ordinal)
            ),
            (
                "event/bingo_machine/bingo_machine_box.lua",
                GeneratedScriptKind.Item,
                new ScriptStateVector("bingo-3054", FreeBagCells: 10, ParticularType: 3054),
                trace => Assert.Contains("NEWS(", string.Join('\n', trace.Events), StringComparison.Ordinal)
            ),
            (
                "江南区/临安/莫空月居所/npc/战斗_莫空月.lua",
                GeneratedScriptKind.Monster,
                new ScriptStateVector("boss-mokongyue-death", Tasks: new Dictionary<ushort, int> { [43] = 100 }),
                trace =>
                {
                    string joined = string.Join('\n', trace.Events);
                    Assert.Contains("TALK(", joined, StringComparison.Ordinal);
                    Assert.Contains("ADD_ATTR_PTS(2)", joined, StringComparison.Ordinal);
                    Assert.Contains("ADD_BASE_STR(1)", joined, StringComparison.Ordinal);
                    Assert.Contains("ADD_BASE_DEX(1)", joined, StringComparison.Ordinal);
                    Assert.Equal(255, trace.GetTaskValue(43));
                }
            ),
            (
                "中原北区/少林派/少林派/npc/少林帮助npc.lua",
                GeneratedScriptKind.Npc,
                new ScriptStateVector("shaolin-helper-cycle", DialogChoices: [0, 0]),
                trace => Assert.Contains("SAY(", string.Join('\n', trace.Events), StringComparison.Ordinal)
            ),
        ];

        Assert.NotEmpty(cases);
        CancellationToken ct = TestContext.Current.CancellationToken;
        foreach ((string suffix, GeneratedScriptKind kind, ScriptStateVector vec, Action<ScriptEffectTrace> extraCheck) in cases)
        {
            ScriptInventoryEntry entry = CachedManifest.Value.Entries.First(e =>
                e.Utf8DisplayPath.Replace('\\', '/').EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            string utf8ScriptPath = "\\" + entry.Utf8DisplayPath.Replace('/', '\\')["server1\\".Length..];

            Assert.True(
                instanceByUtf8Path.TryGetValue(utf8ScriptPath, out object? instance),
                $"Generated pack missing Phase 4 script '{utf8ScriptPath}'.");

            ScriptEffectTrace luaTrace = luaOracle.Execute(entry.CanonicalScriptPath, kind, vec);

            var rng = new Random(vec.RandomSeed);
            DateTime simTime = vec.SimulatedLocalTime ?? new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
            using IDisposable randScope = GeneratedLuaRuntime.UseDeterministicRandom((min, max) =>
                max <= min ? min : rng.Next(min, max + 1));
            using IDisposable? clockScope = clockScopeMethod?.Invoke(null, [simTime]) as IDisposable;

            var ctx = new RecordingScriptContext(utf8ScriptPath, vec, itemIndex);
            switch (kind)
            {
                case GeneratedScriptKind.Item:
                    ItemUsageResult res = await ((IItemUsageScript)instance).OnUseItemAsync(ctx, ct);
                    ctx.Trace.ItemResult = res == ItemUsageResult.KeepItem ? "KeepItem" : "ConsumeOne";
                    await ctx.DrainInteractiveContinuationsAsync(ct);
                    break;
                case GeneratedScriptKind.Monster:
                    ((INpcCombatScript)instance).OnNpcDeath(ctx);
                    await ctx.DrainInteractiveContinuationsAsync(ct);
                    break;
                case GeneratedScriptKind.Npc:
                    await ((INpcDialogScript)instance).OnDialogAsync(ctx, ct);
                    await ctx.DrainInteractiveContinuationsAsync(ct);
                    break;
            }

            Assert.Equal(luaTrace.ToCanonicalString(), ctx.Trace.ToCanonicalString());
            Assert.DoesNotContain("\uFFFD", ctx.Trace.ToCanonicalString(), StringComparison.Ordinal);
            extraCheck(ctx.Trace);
        }
    }
}
