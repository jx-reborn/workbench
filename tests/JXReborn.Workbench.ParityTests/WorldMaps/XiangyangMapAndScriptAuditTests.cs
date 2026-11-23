using System;
using System.Collections.Generic;
using System.Linq;
using JXReborn.GameAssets.Items;
using JXReborn.GameAssets.NpcTemplates;
using JXReborn.GameAssets.Shops;
using JXReborn.GameAssets.WorldMaps;
using JXReborn.Primitives.Characters;
using JXReborn.Primitives.WorldZones;
using JXReborn.TestBase.Fixtures;
using Xunit;

namespace JXReborn.GameAssets.Tests.WorldMaps;

/// <summary>
/// Layer 1 asset, script, and portal audit for Tương Dương Phủ (<c>Map 78</c>) and
/// Mật đạo Nha môn Tương Dương (<c>Map 79</c>) against exported legacy map and script data.
/// </summary>
[Trait("Category", "LegacyData")]
public sealed class XiangyangMapAndScriptAuditTests
{
    private static readonly ushort[] ExpectedConnectedMaps = [78, 79, 81, 90, 101, 237, 324, 523];

    private static readonly int[] ExpectedShopIds = [10, 11, 46, 93, 99, 141, 151];

    private static readonly string[] ExpectedWudangQuestItemCodes =
    [
        "thien-tam-dao-bao-bi-rach",
        "thien-tam",
        "thien-tam-dao-bao-hoan-chinh",
        "duong-quy-g4-d107-p0-r108",
        "luong-ngan-khoang",
        "xich-dong-khoang",
        "hop-hoan",
        "huyen-sam",
        "phong-ky-g4-d136-p0-r137",
        "chu-sa-g4-d137-p0-r138",
        "tu-thiet-khoang",
    ];

    private static readonly uint[] ExpectedMap78TrapIds =
    [
        1693105733u, // Bắc Môn
        2855701602u, // Đông Môn
        1165897818u, // Nam Môn
        2186758855u, // Tây Môn
        1938385781u, // -> Võ Đang (81)
        1397835010u, // -> Phục Ngưu Sơn Đông (90)
        2366506913u, // -> Đạo Hương Thôn (101)
        948300338u,  // -> Tống Kim
        3721490225u, // -> Mật đạo Nha môn (79)
    ];

    private static readonly (int TemplateId, string Name, int Count, short MinLevel, short MaxLevel)[] ExpectedMap78MonsterGroups =
    [
        (11, "Heo rừng", 254, 10, 14),
        (12, "Nhím", 672, 12, 16),
        (33, "Hoán hùng", 204, 14, 18),
        (34, "Linh Miêu", 135, 16, 20),
    ];

    private static readonly (int TemplateId, string Name, int Count)[] ExpectedMap79MonsterGroups =
    [
        (53, "Tương Dương Thủ tướng", 1),
        (58, "Cung binh", 33),
        (60, "Đao binh", 28),
        (62, "Thương binh", 16),
    ];

    private static readonly (int TemplateId, ushort MapId, int MpsX, int MpsY, string DisplayName)[] ExpectedCanonicalAutoExecSpawns =
    [
        (769, 78, 48384, 102592, "Nhiếp Thí Trần"),
        // Note: 950 (Tiếp Đầu Công Thành Chiến) is gated under event-spawns.npcs.json (CFG_DefaultSeasonalScript, enabledByDefault=false) in canonical legacy-server archive.
        (698, 78, 51680, 101920, "Lưu Uẩn Cổ"),
        (701, 78, 56992, 102816, "Nguyễn Minh Viễn"),
        (702, 78, 43488, 112448, "Thịnh Doãn"),
        (667, 79, 53856, 100608, "Người thần bí"),
    ];

    [Fact]
    public void Map78And79DescriptorsMonstersShopsAndQuestItems_WhenLoadedFromCatalog_ShouldMatchCanonicalOracleData()
    {
        SharedGameAssetsFixture assets = SharedGameAssetsFixture.Default;

        foreach (ushort mapId in ExpectedConnectedMaps)
        {
            Assert.True(
                assets.MapCatalog.TryGetMapDescriptor(mapId, out MapDescriptor? desc) && desc is not null,
                $"Expected connected map {mapId} to be loaded in MapCatalog.");
        }

        Assert.True(assets.MapCatalog.TryGetMapDescriptor(78, out MapDescriptor? map78) && map78 is not null);
        Assert.Contains("Tương Dương", map78!.MapNameUnicode, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(78, map78.RegionBeginX);
        Assert.Equal(88, map78.RegionBeginY);
        Assert.Equal(40, map78.RegionWidth);
        Assert.Equal(26, map78.RegionHeight);

        Assert.True(assets.MapCatalog.TryGetMapDescriptor(79, out MapDescriptor? map79) && map79 is not null);
        Assert.Equal(98, map79!.RegionBeginX);
        Assert.Equal(97, map79.RegionBeginY);
        Assert.Equal(13, map79.RegionWidth);
        Assert.Equal(6, map79.RegionHeight);

        var map78Monsters = map78.RegionNpcSpawns.Where(static n => n.Kind == EntityKind.Monster).ToList();
        var map78DialogNpcs = map78.RegionNpcSpawns.Where(static n => n.Kind == EntityKind.DialogNpc).ToList();
        Assert.Equal(1265, map78Monsters.Count);
        Assert.Equal(71, map78DialogNpcs.Count);

        foreach ((int tplId, string _, int count, short minLvl, short maxLvl) in ExpectedMap78MonsterGroups)
        {
            var group = map78Monsters.Where(m => m.TemplateId == tplId).ToList();
            Assert.Equal(count, group.Count);
            Assert.Equal(minLvl, group.Min(static m => m.Level));
            Assert.Equal(maxLvl, group.Max(static m => m.Level));
        }

        var map79Monsters = map79.RegionNpcSpawns.Where(static n => n.Kind == EntityKind.Monster).ToList();
        Assert.Equal(78, map79Monsters.Count);
        foreach ((int tplId, string _, int count) in ExpectedMap79MonsterGroups)
        {
            Assert.Equal(count, map79Monsters.Count(m => m.TemplateId == tplId));
        }

        StaticNpcSpawnTemplate boss53 = map79Monsters.Single(static m => m.TemplateId == 53);
        Assert.Equal((short)69, boss53.Level);
        Assert.Equal(56043, boss53.SpawnMpsX);
        Assert.Equal(103366, boss53.SpawnMpsY);

        Dictionary<int, int> expectedShopCounts = new()
        {
            [10] = 16,
            [11] = 44,
            [46] = 10,
            [93] = 14,
            [99] = 22,
            [141] = 5,
            [151] = 58,
        };

        foreach (int shopId in ExpectedShopIds)
        {
            Assert.True(assets.ShopCatalog.TryGetShop(shopId, out ShopTemplate? shop) && shop is not null,
                $"Expected shop {shopId} to exist in ShopCatalog.");
            Assert.Equal(expectedShopCounts[shopId], shop!.Goods.Count);
        }

        foreach (string itemCode in ExpectedWudangQuestItemCodes)
        {
            Assert.True(
                assets.ItemTemplateCatalog.TryGetItemByCode(itemCode, out ItemTemplate? item) && item is not null,
                $"Expected Wudang quest item '{itemCode}' to exist in ItemTemplateCatalog.");
        }
    }

    [Fact]
    public void Map78SafeCityRules_WhenLoadedFromSnapshot_ShouldForbidPkModeChange()
    {
        SharedGameAssetsFixture assets = SharedGameAssetsFixture.Default;
        Assert.True(assets.MapCatalog.TryGetMapDescriptor(78, out MapDescriptor? map78));
        Assert.NotNull(map78);
        Assert.False(map78.AllowPkChange);
    }

    [Fact]
    public void Map78And79PortalsAndStaticObjects_WhenInspectedFromCatalog_ShouldContainAllNineTrapsReturnPortalAndTwentyFiveObjects()
    {
        SharedGameAssetsFixture assets = SharedGameAssetsFixture.Default;

        foreach (uint trapId in ExpectedMap78TrapIds)
        {
            Assert.True(
                assets.TrapScriptCatalog.TryGetTrapBinding(trapId, out PortalTrapBindingTemplate? binding) && binding is not null,
                $"Expected Map 78 trapId {trapId} in TrapScriptCatalog.");
        }

        Assert.True(assets.TrapScriptCatalog.TryGetTrapBinding(1693105733u, out PortalTrapBindingTemplate? northGate));
        Assert.Equal(PortalTrapActionKind.ToggleFightStateAndSetPos, northGate!.ActionKind);
        Assert.Equal(54848, northGate.TargetMpsX);
        Assert.Equal(102912, northGate.TargetMpsY);
        Assert.Equal(54752, northGate.AlternateMpsX);
        Assert.Equal(103040, northGate.AlternateMpsY);

        Assert.True(assets.TrapScriptCatalog.TryGetTrapBinding(1397835010u, out PortalTrapBindingTemplate? phucNguu));
        Assert.Equal(PortalTrapActionKind.ChangeMapPortal, phucNguu!.ActionKind);
        Assert.Equal((ushort)90, phucNguu.TargetMapId);
        Assert.Equal(52768, phucNguu.TargetMpsX);
        Assert.Equal(114144, phucNguu.TargetMpsY);

        Assert.True(assets.TrapScriptCatalog.TryGetTrapBinding(2889451457u, out PortalTrapBindingTemplate? returnPortal));
        Assert.Equal((ushort)78, returnPortal!.TargetMapId);
        Assert.Equal(52288, returnPortal.TargetMpsX);
        Assert.Equal(102272, returnPortal.TargetMpsY);
        Assert.Equal((byte)0, returnPortal.TargetFightMode);

        IReadOnlyList<StaticMapObjectSpawnTemplate> mapObjects = assets.MapCatalog.GetStaticMapObjectsForMap(78);
        Assert.Equal(25, mapObjects.Count);
        Assert.Equal(5, mapObjects.Count(static o => o.ObjTemplateId == 1));
        Assert.Equal(3, mapObjects.Count(static o => o.ObjTemplateId == 285));
        Assert.Equal(3, mapObjects.Count(static o => o.ObjTemplateId == 283));
        Assert.Equal(3, mapObjects.Count(static o => o.ObjTemplateId == 284));
        Assert.Equal(7, mapObjects.Count(static o => o.ObjTemplateId is 294 or 296 or 297));
        Assert.Equal(4, mapObjects.Count(static o => o.ObjTemplateId is 3 or 4));
    }

    [Fact]
    public void SongJinSignupMaps323And325_WhenLoadedFromMapCatalog_ShouldExistAlongsideMap324()
    {
        SharedGameAssetsFixture assets = SharedGameAssetsFixture.Default;

        Assert.True(assets.MapCatalog.TryGetMapDescriptor(323, out _), "Expected SongJin Low signup map 323 in MapCatalog.");
        Assert.True(assets.MapCatalog.TryGetMapDescriptor(324, out _), "Expected SongJin Mid signup map 324 in MapCatalog.");
        Assert.True(assets.MapCatalog.TryGetMapDescriptor(325, out _), "Expected SongJin High signup map 325 in MapCatalog.");
    }

    [Fact]
    public void CanonicalXiangyangNpcsObjectsAndAutoExecBosses_WhenResolvedFromCatalog_ShouldSpawnAndInferNonNullScripts()
    {
        SharedGameAssetsFixture assets = SharedGameAssetsFixture.Default;

        Assert.True(assets.MapCatalog.TryGetMapDescriptor(78, out MapDescriptor? map78) && map78 is not null);
        Assert.True(assets.MapCatalog.TryGetMapDescriptor(79, out MapDescriptor? map79) && map79 is not null);

        var map78DialogNpcs = map78!.RegionNpcSpawns.Where(static n => n.Kind == EntityKind.DialogNpc).ToList();
        Assert.NotEmpty(map78DialogNpcs);
        var missingNpcBindings = new List<string>();
        foreach (StaticNpcSpawnTemplate npc in map78DialogNpcs)
        {
            string? code = !string.IsNullOrWhiteSpace(npc.ScriptCode)
                ? npc.ScriptCode
                : StaticNpcSpawnTemplate.InferDefaultScriptCode(npc.Kind, npc.ScriptPath, npc.DisplayName);
            if (string.IsNullOrWhiteSpace(code))
            {
                missingNpcBindings.Add($"Map78 DialogNpc {npc.TemplateId} ({npc.DisplayName}) '{npc.ScriptPath}'");
            }
        }

        IReadOnlyList<StaticNpcSpawnTemplate> map78All = assets.MapCatalog.GetStaticNpcSpawnsForMap(78);
        IReadOnlyList<StaticNpcSpawnTemplate> map79All = assets.MapCatalog.GetStaticNpcSpawnsForMap(79);

        foreach ((int tplId, ushort expectedMap, int mpsX, int mpsY, string name) in ExpectedCanonicalAutoExecSpawns)
        {
            IReadOnlyList<StaticNpcSpawnTemplate> pool = expectedMap == 78 ? map78All : map79All;
            StaticNpcSpawnTemplate? match = pool.FirstOrDefault(n =>
                n.TemplateId == tplId &&
                Math.Abs(n.SpawnMpsX - mpsX) <= 64 &&
                Math.Abs(n.SpawnMpsY - mpsY) <= 64);
            if (match is null)
            {
                missingNpcBindings.Add($"Map{expectedMap} AutoExec spawn missing: {tplId} ({name}) near ({mpsX},{mpsY})");
                continue;
            }

            string? code = !string.IsNullOrWhiteSpace(match.ScriptCode)
                ? match.ScriptCode
                : StaticNpcSpawnTemplate.InferDefaultScriptCode(match.Kind, match.ScriptPath, match.DisplayName);
            if (string.IsNullOrWhiteSpace(code))
            {
                missingNpcBindings.Add($"Map{expectedMap} AutoExec {tplId} ({name}) has null ScriptCode");
            }
        }

        StaticNpcSpawnTemplate wudangBoss53 = map79!.RegionNpcSpawns.Single(static m => m.TemplateId == 53);
        string? boss53Code = !string.IsNullOrWhiteSpace(wudangBoss53.ScriptCode)
            ? wudangBoss53.ScriptCode
            : StaticNpcSpawnTemplate.InferDefaultScriptCode(wudangBoss53.Kind, wudangBoss53.ScriptPath, wudangBoss53.DisplayName);
        if (string.IsNullOrWhiteSpace(boss53Code))
        {
            missingNpcBindings.Add($"Map79 Boss 53 ({wudangBoss53.DisplayName}) '{wudangBoss53.ScriptPath}'");
        }

        IReadOnlyList<StaticMapObjectSpawnTemplate> map78Objects = assets.MapCatalog.GetStaticMapObjectsForMap(78);
        Assert.NotEmpty(map78Objects);
        foreach (StaticMapObjectSpawnTemplate obj in map78Objects)
        {
            string? objCode = StaticMapObjectSpawnTemplate.InferDefaultScriptCode(obj.ObjTemplateId, obj.ScriptPath);
            if (string.IsNullOrWhiteSpace(objCode))
            {
                missingNpcBindings.Add($"Map78 MapObject {obj.ObjTemplateId} ({obj.ObjCode}) '{obj.ScriptPath}'");
            }
        }

        Assert.True(
            missingNpcBindings.Count == 0,
            $"Unbound canonical Xiangyang entities ({missingNpcBindings.Count}):{Environment.NewLine}{string.Join(Environment.NewLine, missingNpcBindings)}");
    }
}
