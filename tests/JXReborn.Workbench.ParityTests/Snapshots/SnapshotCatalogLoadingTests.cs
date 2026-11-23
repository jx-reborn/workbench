using JXReborn.GameAssets.Configuration;
using JXReborn.GameAssets.Items;
using JXReborn.GameAssets.LootTables;
using JXReborn.GameAssets.PlayerTemplates;
using JXReborn.GameAssets.Shops;
using JXReborn.GameAssets.Skills;
using JXReborn.GameAssets.Snapshots;
using JXReborn.GameAssets.WorldMaps;
using JXReborn.Primitives.Characters;
using JXReborn.Primitives.Items;
using JXReborn.TestBase.Configuration;
using MemoryPack;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JXReborn.GameAssets.Tests.Snapshots;

[Trait("Category", "LegacyData")]
public sealed class SnapshotCatalogLoadingTests
{
    [Fact]
    public void SnapshotLoader_Tier2MemoryPack_ShouldLoadAndMatchCanonicalJson()
    {
        var opts = new GameAssetsOptions
        {
            DataDirectory = TestWorkspacePaths.DataDirectory,
            ArtifactsDirectory = TestWorkspacePaths.EffectiveArtifactsDirectory,
            DataSnapshotFile = TestWorkspacePaths.EffectiveDataSnapshotFile,
            PreferBinarySnapshots = true
        };

        bool loaded = GameAssetsSnapshotLoader.TryLoadBundle(opts, out GameAssetsSnapshotBundle? bundle, out string sourceTier);
        Assert.True(loaded);
        Assert.NotNull(bundle);
        Assert.StartsWith("Tier2:MemoryPack", sourceTier);
        Assert.NotEmpty(bundle.SourceJsonSha256Hex);
        Assert.True(bundle.Skills.Count > 1000);
        Assert.True(bundle.Missiles.Count > 500);
        Assert.True(bundle.Items.Count > 20000);
        Assert.True(bundle.MagicAffixes.Count > 7000);
        Assert.True(bundle.LootTables.Count >= 150);
        Assert.Equal(2540, bundle.NpcTemplates.Count);
        Assert.Equal(10, bundle.CharacterTemplates.Count);
        Assert.NotEmpty(bundle.DefaultStarterItems);
        Assert.NotNull(bundle.Shops);
        Assert.Equal(179, bundle.Shops.Count);
        Assert.NotNull(bundle.Factions);
        Assert.Equal(11, bundle.Factions.Count);
        Assert.NotNull(bundle.WeaponSkillMappings);
        Assert.Equal(32, bundle.WeaponSkillMappings.Count);
        Assert.NotNull(bundle.AttributeConstRules);

        byte[] roundTripBytes = MemoryPackSerializer.Serialize(bundle);
        GameAssetsSnapshotBundle? roundTrip = MemoryPackSerializer.Deserialize<GameAssetsSnapshotBundle>(roundTripBytes);
        Assert.NotNull(roundTrip);
        Assert.Equal(bundle.SourceJsonSha256Hex, roundTrip.SourceJsonSha256Hex);
        Assert.Equal(bundle.Skills.Count, roundTrip.Skills.Count);
        Assert.Equal(bundle.Items.Count, roundTrip.Items.Count);
        Assert.Equal(179, roundTrip.Shops?.Count);
        Assert.Equal(11, roundTrip.Factions?.Count);
        Assert.Equal(32, roundTrip.WeaponSkillMappings?.Count);
        Assert.NotNull(roundTrip.AttributeConstRules?.HideRule);
    }

    [Fact]
    public void SkillParameterCurveTemplate_AllCurveModes_ShouldEvaluateDeterministically()
    {
        var linear = new SkillParameterCurveTemplate("linear", BaseValue: 10, PerLevelValue: 5);
        Assert.Equal(10, linear.Evaluate(1));
        Assert.Equal(30, linear.Evaluate(5));

        var piecewise = new SkillParameterCurveTemplate(
            "piecewise",
            BaseValue: 0,
            Points:
            [
                new SkillCurveControlPoint(1, 10),
                new SkillCurveControlPoint(10, 100),
                new SkillCurveControlPoint(20, 300)
            ]);
        Assert.Equal(10, piecewise.Evaluate(1));
        Assert.Equal(50, piecewise.Evaluate(5));
        Assert.Equal(100, piecewise.Evaluate(10));
        Assert.Equal(200, piecewise.Evaluate(15));
        Assert.Equal(300, piecewise.Evaluate(20));

        var tableCurve = new SkillParameterCurveTemplate(
            "table",
            BaseValue: 0,
            ValuesByLevel: [10, 20, 35, 55, 80]);
        Assert.Equal(10, tableCurve.Evaluate(1));
        Assert.Equal(80, tableCurve.Evaluate(5));
        Assert.Equal(80, tableCurve.Evaluate(30));

        int[] cells = new int[ObstacleRleCodec.RegionCellCount];
        cells[0] = 1;
        cells[1] = 1;
        cells[255] = 3;
        cells[511] = 2;
        string? encoded = ObstacleRleCodec.EncodeCellsToBase64(cells);
        Assert.NotNull(encoded);
        int[] decoded = ObstacleRleCodec.DecodeBase64ToCells(encoded);
        Assert.Equal(cells, decoded);
    }

    [Fact]
    public void RuntimeCatalogs_LoadedSnapshots_ShouldContainExpectedVietnameseNamesShopsAndItems()
    {
        IOptions<GameAssetsOptions> options = Options.Create(new GameAssetsOptions
        {
            DataDirectory = TestWorkspacePaths.DataDirectory,
            ArtifactsDirectory = TestWorkspacePaths.EffectiveArtifactsDirectory,
            DataSnapshotFile = TestWorkspacePaths.EffectiveDataSnapshotFile,
            PreferBinarySnapshots = true
        });

        var skillCatalog = new SkillTemplateCatalog(options, NullLogger<SkillTemplateCatalog>.Instance);
        var missileCatalog = new MissileTemplateCatalog(options);
        var itemCatalog = new ItemTemplateCatalog(options, NullLogger<ItemTemplateCatalog>.Instance);
        var lootCatalog = new LootTableCatalog(options, NullLogger<LootTableCatalog>.Instance);
        var playerCatalog = new PlayerTemplateCatalog(options, NullLogger<PlayerTemplateCatalog>.Instance);
        var shopCatalog = new ShopCatalog(options, NullLogger<ShopCatalog>.Instance);

        Assert.True(skillCatalog.TryGetSkill(53, out SkillTemplate? unarmed) && unarmed is not null);
        Assert.Contains("kích vật lý", unarmed.Name, StringComparison.OrdinalIgnoreCase);

        Assert.True(skillCatalog.TryGetSkill(102, out SkillTemplate? windSkill) && windSkill is not null);
        Assert.Equal("Phong Quyển Tàn Tuyết", windSkill.Name);
        Assert.True(missileCatalog.TryGetMissile(windSkill.MissileId, out MissileTemplate? windMissile) && windMissile is not null);
        Assert.Equal("Phong Quyển Tàn Tuyết", windMissile.Name);

        Assert.True(itemCatalog.TryGetItemByCode("kim-sang-duoc", out ItemTemplate? healPotion) && healPotion is not null);
        Assert.Equal(ItemGenreKind.Medicine, healPotion.Genre);
        Assert.Contains("Kim Sáng", healPotion.Name, StringComparison.OrdinalIgnoreCase);

        Assert.True(itemCatalog.TryGetItemByRowId(ItemQualityKind.Golden, 1, out ItemTemplate? goldEquip) && goldEquip is not null);
        Assert.Equal(ItemQualityKind.Golden, goldEquip.Quality);
        Assert.NotEmpty(goldEquip.Code);

        Assert.True(itemCatalog.TryGetItemByRowId(ItemQualityKind.Platina, 1, out ItemTemplate? platinaEquip) && platinaEquip is not null);
        Assert.Equal(ItemQualityKind.Platina, platinaEquip.Quality);

        Assert.True(itemCatalog.TryGetItemByCode("tho-dia-phu", out ItemTemplate? portalScroll) && portalScroll is not null);
        Assert.Equal(ItemGenreKind.TownPortal, portalScroll.Genre);

        Assert.True(lootCatalog.TryGetLootTable("default-monster-loot", out LootTableTemplate? lootTable) && lootTable is not null);
        Assert.NotEmpty(lootTable.WeightedPools);

        Assert.Equal(179, shopCatalog.Count);
        Assert.True(shopCatalog.TryGetShop(1, out ShopTemplate? shop1) && shop1 is not null);
        Assert.NotEmpty(shop1.Goods);

        Assert.Single(playerCatalog.DefaultStarterItems);
        Assert.Equal("toan-dau-chuy", playerCatalog.DefaultStarterItems[0].ItemCode);

        // Verify all 5 ElementSeries growth rules against server1/settings/npc/player/level_add.txt
        PlayerSeriesGrowthRuleTemplate metalGrowth = playerCatalog.GetSeriesGrowthRule(ElementSeries.Metal);
        Assert.Equal(4, metalGrowth.LifePerLevel);
        Assert.Equal(9, metalGrowth.StaminaPerLevel);
        Assert.Equal(1, metalGrowth.ManaPerLevel);
        Assert.Equal(8, metalGrowth.LifePerVitality);
        Assert.Equal(0, metalGrowth.StaminaPerVitality);
        Assert.Equal(1, metalGrowth.ManaPerEnergy);

        PlayerSeriesGrowthRuleTemplate woodGrowth = playerCatalog.GetSeriesGrowthRule(ElementSeries.Wood);
        Assert.Equal(3, woodGrowth.LifePerLevel);
        Assert.Equal(5, woodGrowth.LifePerVitality);
        Assert.Equal(0, woodGrowth.StaminaPerVitality);
        Assert.Equal(3, woodGrowth.ManaPerEnergy);

        PlayerSeriesGrowthRuleTemplate waterGrowth = playerCatalog.GetSeriesGrowthRule(ElementSeries.Water);
        Assert.Equal(3, waterGrowth.LifePerLevel);
        Assert.Equal(6, waterGrowth.LifePerVitality);
        Assert.Equal(0, waterGrowth.StaminaPerVitality);
        Assert.Equal(3, waterGrowth.ManaPerEnergy);

        PlayerSeriesGrowthRuleTemplate fireGrowth = playerCatalog.GetSeriesGrowthRule(ElementSeries.Fire);
        Assert.Equal(3, fireGrowth.LifePerLevel);
        Assert.Equal(7, fireGrowth.LifePerVitality);
        Assert.Equal(0, fireGrowth.StaminaPerVitality);
        Assert.Equal(2, fireGrowth.ManaPerEnergy);

        PlayerSeriesGrowthRuleTemplate earthGrowth = playerCatalog.GetSeriesGrowthRule(ElementSeries.Earth);
        Assert.Equal(1, earthGrowth.LifePerLevel);
        Assert.Equal(3, earthGrowth.LifePerVitality);
        Assert.Equal(0, earthGrowth.StaminaPerVitality);
        Assert.Equal(4, earthGrowth.ManaPerEnergy);

        Assert.True(playerCatalog.GetRequiredExpForNextLevel(1) > 0);
    }
}
