using JXReborn.GameAssets.Configuration;
using JXReborn.GameAssets.Items;
using JXReborn.GameAssets.NpcTemplates;
using JXReborn.GameAssets.PlayerTemplates;
using JXReborn.GameAssets.WorldMaps;
using JXReborn.Primitives.Characters;
using JXReborn.Primitives.Items;
using JXReborn.TestBase.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JXReborn.GameAssets.Tests.Catalogs;

/// <summary>
/// Phase 3 asset-parity regression tests verifying Vietnamese <c>replacename_npc.txt</c> NPC overrides
/// (e.g. <c>"Dã Tẩu"</c> in Ba Lăng Huyện), 1-based <c>NpcS.txt</c> <c>TemplateId</c> parity, and
/// all 4 canonical <c>NewPlayerIni00.ini</c> starter items (<c>Toàn Đầu Chùy</c> durability = 42).
/// </summary>
[Trait("BugRegression", "Phase3")]
[Trait("Category", "LegacyData")]
public sealed class StarterVillageAndEquipmentCatalogTests
{
    [Fact]
    public void BaLangHuyen_Map53_ShouldResolveDaTauVietnameseOverride_AndOneBasedTemplateId()
    {
        var options = Options.Create(new GameAssetsOptions
        {
            DataDirectory = TestWorkspacePaths.DataDirectory,
            ArtifactsDirectory = TestWorkspacePaths.EffectiveArtifactsDirectory,
            DataSnapshotFile = TestWorkspacePaths.EffectiveDataSnapshotFile,
        });

        var npcCatalog = new NpcTemplateCatalog(options, NullLogger<NpcTemplateCatalog>.Instance);
        var mapCatalog = new MapCatalog(options, NullLogger<MapCatalog>.Instance, npcCatalog);

        IReadOnlyList<StaticNpcSpawnTemplate> spawns53 = mapCatalog.GetStaticNpcSpawnsForMap(53);
        Assert.NotEmpty(spawns53);

        // "野叟" in Map 53 Region_S.dat MUST translate via replacename_npc.txt to "Dã Tẩu"
        StaticNpcSpawnTemplate? daTau = spawns53.FirstOrDefault(s =>
            string.Equals(s.DisplayName, "Dã Tẩu", StringComparison.Ordinal));
        Assert.NotNull(daTau);
        Assert.Equal(EntityKind.DialogNpc, daTau.Kind);
        Assert.True(npcCatalog.TryGetTemplate(daTau.TemplateId, out NpcTemplate? daTauTpl) && daTauTpl is not null);
        Assert.Equal(EntityKind.DialogNpc, daTauTpl.Kind);

        // "礼官" -> "Lễ Quan"
        Assert.Contains(spawns53, s => string.Equals(s.DisplayName, "Lễ Quan", StringComparison.Ordinal));
    }

    [Fact]
    public void PlayerTemplateCatalog_ShouldContainCanonicalStarterItem_ToanDauChuyWithDurability42AndNoFabricatedImmediacyItems()
    {
        var options = Options.Create(new GameAssetsOptions
        {
            DataDirectory = TestWorkspacePaths.DataDirectory,
            ArtifactsDirectory = TestWorkspacePaths.EffectiveArtifactsDirectory,
            DataSnapshotFile = TestWorkspacePaths.EffectiveDataSnapshotFile,
        });

        var playerCatalog = new PlayerTemplateCatalog(options, NullLogger<PlayerTemplateCatalog>.Instance);
        var itemCatalog = new ItemTemplateCatalog(options, NullLogger<ItemTemplateCatalog>.Instance);

        Assert.Single(playerCatalog.DefaultStarterItems);

        // No fabricated Immediacy belt items (matches NewPlayerIni00.ini COUNT=1)
        Assert.DoesNotContain(playerCatalog.DefaultStarterItems, static i => i.ContainerPlace == ItemContainerPlace.Immediacy);

        // 1x Toàn Đầu Chùy in EquipRoom (0, 0) with MaxDurability = 42 from MeleeWeapon.txt DurMax
        StarterItemTemplate? mace = playerCatalog.DefaultStarterItems.FirstOrDefault(i =>
            i.ItemCode == "toan-dau-chuy");
        Assert.NotNull(mace);
        Assert.Equal(ItemGenreKind.Equipment, mace.Genre);
        Assert.Equal((short)0, mace.DetailType);
        Assert.Equal((short)4, mace.ParticularType);
        Assert.Equal(ItemContainerPlace.EquipRoom, mace.ContainerPlace);
        Assert.Equal((short)42, mace.MaxDurability);

        Assert.True(itemCatalog.TryGetItem(ItemGenreKind.Equipment, 0, 4, 1, out ItemTemplate? maceTpl) && maceTpl is not null);
        Assert.Contains("Chùy", maceTpl.Name, StringComparison.OrdinalIgnoreCase);
    }
}
