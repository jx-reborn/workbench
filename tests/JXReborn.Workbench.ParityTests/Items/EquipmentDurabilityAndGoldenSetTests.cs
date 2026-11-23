using JXReborn.TestBase.SimulationHarnesses;
using JXReborn.TestBase.Configuration;
using System;
using System.Collections.Generic;
using JXReborn.GameAssets.Configuration;
using JXReborn.GameAssets.Items;
using JXReborn.Primitives.Characters;
using JXReborn.Primitives.Items;
using JXReborn.ZoneEngine.Items;
using JXReborn.ZoneEngine.PlayerSessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JXReborn.ZoneEngine.Tests.Items;

public sealed class EquipmentDurabilityAndGoldenSetTests
{
    private static PlayerZoneState CreateTestPlayer(
        ElementSeries series = ElementSeries.Water,
        int life = 1000,
        ushort maxLife = 1000,
        int money = 100000)
    {
        var sink = new PlayerOutboundEventRecorder();
        return new PlayerZoneState(
            runtimeNpcId: 10001u,
            accountId: Guid.NewGuid(),
            characterId: Guid.NewGuid(),
            leaseEpoch: 1L,
            roleNameUnicode: "Lệnh Hồ Xung",
            gender: CharacterGender.Male,
            series: series,
            level: 100,
            experience: 0,
            leadExperience: 0,
            mapId: 53,
            currentMpsX: 52000,
            currentMpsY: 101696,
            currentLife: life,
            maxLife: maxLife,
            currentMana: 500,
            maxMana: 500,
            currentStamina: 300,
            maxStamina: 300,
            strength: 500,
            dexterity: 500,
            vitality: 500,
            energy: 500,
            lucky: 10,
            unallocatedAttributePoints: 0,
            unallocatedSkillPoints: 0,
            inventoryMoney: money,
            repositoryMoney: 0,
            currentFactionId: 255,
            firstFactionId: 255,
            factionJoinCount: 0,
            initialTick: 1L,
            outboundSink: sink);
    }

    private static ItemTemplate CreateWeaponTemplate(string code = "test-sword", int price = 1000)
    {
        return new ItemTemplate(
            Id: 501,
            Code: code,
            Name: "Test Sword",
            Genre: ItemGenreKind.Equipment,
            Quality: ItemQualityKind.Normal,
            DetailType: 0, // Weapon
            ParticularType: 1,
            Level: 5,
            ElementSeries: ElementSeries.Metal,
            GridWidthCells: 2,
            GridHeightCells: 3,
            ObjDataId: 1,
            AppearanceResId: 1,
            PriceGold: price,
            MaxStackCount: 1,
            BaseAttributes:
            [
                new ItemBaseAttributeTemplate(EquipmentAbrasionConsts.DurabilityAttributeId, "durability_v", 50, 50),
            ],
            Requirements: []);
    }

    private static ItemTemplate CreateArmorTemplate(string code = "test-armor", int price = 2000)
    {
        return new ItemTemplate(
            Id: 502,
            Code: code,
            Name: "Test Armor",
            Genre: ItemGenreKind.Equipment,
            Quality: ItemQualityKind.Normal,
            DetailType: 2, // Body Armor (Part 1)
            ParticularType: 0,
            Level: 5,
            ElementSeries: ElementSeries.Water,
            GridWidthCells: 2,
            GridHeightCells: 3,
            ObjDataId: 2,
            AppearanceResId: 2,
            PriceGold: price,
            MaxStackCount: 1,
            BaseAttributes:
            [
                new ItemBaseAttributeTemplate(EquipmentAbrasionConsts.DurabilityAttributeId, "durability_v", 80, 80),
            ],
            Requirements: []);
    }

    private static ItemTemplate CreateBootsTemplate(string code = "test-boots")
    {
        return new ItemTemplate(
            Id: 503,
            Code: code,
            Name: "Test Boots",
            Genre: ItemGenreKind.Equipment,
            Quality: ItemQualityKind.Normal,
            DetailType: 8, // Foot / Boots (Part 4)
            ParticularType: 0,
            Level: 5,
            ElementSeries: ElementSeries.Earth,
            GridWidthCells: 2,
            GridHeightCells: 2,
            ObjDataId: 3,
            AppearanceResId: 3,
            PriceGold: 500,
            MaxStackCount: 1,
            BaseAttributes:
            [
                new ItemBaseAttributeTemplate(EquipmentAbrasionConsts.DurabilityAttributeId, "durability_v", 60, 60),
            ],
            Requirements: []);
    }

    private static ItemTemplateCatalog CreateCatalogFromAssets()
    {
        var options = Options.Create(new GameAssetsOptions
        {
            DataDirectory = TestWorkspacePaths.DataDirectory,
            ArtifactsDirectory = TestWorkspacePaths.EffectiveArtifactsDirectory,
            DataSnapshotFile = TestWorkspacePaths.EffectiveDataSnapshotFile,
            PreferBinarySnapshots = true
        });
        return new ItemTemplateCatalog(options, NullLogger<ItemTemplateCatalog>.Instance);
    }

    #region T019 — User Story 1: Durability Abrasion Math & Rules

    [Fact]
    public void EquipmentAbrasionCalculator_RandRanges_ShouldMatchAbradeRateIni()
    {
        // Legacy reference: settings/item/abraderate.ini
        // [Attack] Weapon=256
        Assert.Equal(256, EquipmentAbrasionCalculator.GetAbradeRandRange(EquipmentAbradeKind.Attack, EquipmentPartKind.Weapon));
        Assert.Equal(0, EquipmentAbrasionCalculator.GetAbradeRandRange(EquipmentAbradeKind.Attack, EquipmentPartKind.Head));
        Assert.Equal(0, EquipmentAbrasionCalculator.GetAbradeRandRange(EquipmentAbradeKind.Attack, EquipmentPartKind.Foot));

        // [Defend] Head=320, Body=320, Belt=320, Cuff=320; Boots (Foot) = 0
        Assert.Equal(320, EquipmentAbrasionCalculator.GetAbradeRandRange(EquipmentAbradeKind.Defend, EquipmentPartKind.Head));
        Assert.Equal(320, EquipmentAbrasionCalculator.GetAbradeRandRange(EquipmentAbradeKind.Defend, EquipmentPartKind.Body));
        Assert.Equal(320, EquipmentAbrasionCalculator.GetAbradeRandRange(EquipmentAbradeKind.Defend, EquipmentPartKind.Belt));
        Assert.Equal(320, EquipmentAbrasionCalculator.GetAbradeRandRange(EquipmentAbradeKind.Defend, EquipmentPartKind.Cuff));
        Assert.Equal(0, EquipmentAbrasionCalculator.GetAbradeRandRange(EquipmentAbradeKind.Defend, EquipmentPartKind.Foot));
        Assert.Equal(0, EquipmentAbrasionCalculator.GetAbradeRandRange(EquipmentAbradeKind.Defend, EquipmentPartKind.Weapon));

        // [Move] Foot=2560, Horse=5120, Mask=0
        Assert.Equal(2560, EquipmentAbrasionCalculator.GetAbradeRandRange(EquipmentAbradeKind.Move, EquipmentPartKind.Foot));
        Assert.Equal(5120, EquipmentAbrasionCalculator.GetAbradeRandRange(EquipmentAbradeKind.Move, EquipmentPartKind.Horse));
        Assert.Equal(0, EquipmentAbrasionCalculator.GetAbradeRandRange(EquipmentAbradeKind.Move, EquipmentPartKind.Mask));
        Assert.Equal(0, EquipmentAbrasionCalculator.GetAbradeRandRange(EquipmentAbradeKind.Move, EquipmentPartKind.Weapon));
    }

    [Fact]
    public void TryAbradeSlot_DeterministicRandomRoll_ShouldAbradeOrExempt()
    {
        var swordTpl = CreateWeaponTemplate();
        var swordSlot = new PlayerItemSlot(
            runtimeItemId: 1,
            domainItemId: Guid.NewGuid(),
            template: swordTpl,
            genre: swordTpl.Genre,
            detailType: (short)swordTpl.DetailType,
            particularType: (short)swordTpl.ParticularType,
            level: swordTpl.Level,
            series: swordTpl.ElementSeries,
            quality: swordTpl.Quality,
            randomSeed: 12345u,
            generatorVersion: 4,
            luck: 0,
            currentDurability: 10,
            maxDurability: 50,
            stackCount: 1,
            containerPlace: ItemContainerPlace.Equip,
            gridX: (byte)EquipmentPartKind.Weapon,
            gridY: 0);

        // Find a seed where random.Next(256) == 0
        int seedThatRollsZero = -1;
        for (int s = 0; s < 10000; s++)
        {
            if (new Random(s).Next(EquipmentAbrasionConsts.WeaponAttackRange) == 0)
            {
                seedThatRollsZero = s;
                break;
            }
        }
        Assert.True(seedThatRollsZero >= 0);

        // Test roll that hits 0 -> durability decreases from 10 to 9
        bool abraded = EquipmentAbrasionCalculator.TryAbradeSlot(
            swordSlot,
            EquipmentAbradeKind.Attack,
            EquipmentPartKind.Weapon,
            new Random(seedThatRollsZero),
            out bool becameBroken);

        Assert.True(abraded);
        Assert.False(becameBroken);
        Assert.Equal((ushort)9, swordSlot.CurrentDurability);

        // Find a seed where random.Next(256) != 0
        int seedThatRollsNonZero = -1;
        for (int s = 0; s < 10000; s++)
        {
            if (new Random(s).Next(EquipmentAbrasionConsts.WeaponAttackRange) != 0)
            {
                seedThatRollsNonZero = s;
                break;
            }
        }
        Assert.True(seedThatRollsNonZero >= 0);

        // Test roll that does not hit 0 -> durability unchanged
        abraded = EquipmentAbrasionCalculator.TryAbradeSlot(
            swordSlot,
            EquipmentAbradeKind.Attack,
            EquipmentPartKind.Weapon,
            new Random(seedThatRollsNonZero),
            out becameBroken);

        Assert.False(abraded);
        Assert.False(becameBroken);
        Assert.Equal((ushort)9, swordSlot.CurrentDurability);
    }

    [Fact]
    public void TryAbradeSlot_IndestructibleAndZeroDurability_ShouldNeverAbrade()
    {
        // 1. MaxDurability == 0
        var ringTpl = new ItemTemplate(
            Id: 504,
            Code: "test-ring",
            Name: "Test Ring",
            Genre: ItemGenreKind.Equipment,
            Quality: ItemQualityKind.Normal,
            DetailType: 3, // Ring
            ParticularType: 0,
            Level: 5,
            ElementSeries: ElementSeries.Water,
            GridWidthCells: 1,
            GridHeightCells: 1,
            ObjDataId: 1,
            AppearanceResId: 1,
            PriceGold: 1000,
            MaxStackCount: 1,
            BaseAttributes: [],
            Requirements: []);

        var ringSlot = new PlayerItemSlot(
            runtimeItemId: 2,
            domainItemId: Guid.NewGuid(),
            template: ringTpl,
            genre: ringTpl.Genre,
            detailType: (short)ringTpl.DetailType,
            particularType: (short)ringTpl.ParticularType,
            level: ringTpl.Level,
            series: ringTpl.ElementSeries,
            quality: ringTpl.Quality,
            randomSeed: 12345u,
            generatorVersion: 4,
            luck: 0,
            currentDurability: 0,
            maxDurability: 0,
            stackCount: 1,
            containerPlace: ItemContainerPlace.Equip,
            gridX: (byte)EquipmentPartKind.Ring1,
            gridY: 0);

        Assert.True(ringSlot.IsIndestructible);
        bool abraded = EquipmentAbrasionCalculator.TryAbradeSlot(
            ringSlot,
            EquipmentAbradeKind.Defend,
            EquipmentPartKind.Head,
            new Random(0),
            out _);
        Assert.False(abraded);

        // 2. Attribute 43 (indestructible_b)
        var swordTpl = CreateWeaponTemplate();
        var affixes = new[]
        {
            new ItemMagicAttributeSlot(0, EquipmentAbrasionConsts.IndestructibleAttributeId, 1, 0, 0, 10)
        };
        var indestructibleSword = new PlayerItemSlot(
            runtimeItemId: 3,
            domainItemId: Guid.NewGuid(),
            template: swordTpl,
            genre: swordTpl.Genre,
            detailType: (short)swordTpl.DetailType,
            particularType: (short)swordTpl.ParticularType,
            level: swordTpl.Level,
            series: swordTpl.ElementSeries,
            quality: swordTpl.Quality,
            randomSeed: 12345u,
            generatorVersion: 4,
            luck: 0,
            currentDurability: 50,
            maxDurability: 50,
            stackCount: 1,
            containerPlace: ItemContainerPlace.Equip,
            gridX: (byte)EquipmentPartKind.Weapon,
            gridY: 0,
            magicAttributes: affixes);

        Assert.True(indestructibleSword.IsIndestructible);
        abraded = EquipmentAbrasionCalculator.TryAbradeSlot(
            indestructibleSword,
            EquipmentAbradeKind.Attack,
            EquipmentPartKind.Weapon,
            new Random(0),
            out _);
        Assert.False(abraded);
    }

    [Fact]
    public void PlayerItemSlot_IsIndestructible_ShouldTreatCanonicalAttribute43AsIndestructibleAndAttribute38AsDestructible()
    {
        ItemTemplate swordTpl = CreateWeaponTemplate();

        var canonicalIndestructible = new PlayerItemSlot(
            runtimeItemId: 101,
            domainItemId: Guid.NewGuid(),
            template: swordTpl,
            genre: swordTpl.Genre,
            detailType: (short)swordTpl.DetailType,
            particularType: (short)swordTpl.ParticularType,
            level: swordTpl.Level,
            series: swordTpl.ElementSeries,
            quality: swordTpl.Quality,
            randomSeed: 12345u,
            generatorVersion: 4,
            luck: 0,
            currentDurability: 50,
            maxDurability: 50,
            stackCount: 1,
            containerPlace: ItemContainerPlace.Equip,
            gridX: (byte)EquipmentPartKind.Weapon,
            gridY: 0,
            magicAttributes: [new ItemMagicAttributeSlot(0, 43, 1, 0, 0, 1)]);

        Assert.True(canonicalIndestructible.IsIndestructible);
        Assert.False(EquipmentAbrasionCalculator.TryAbradeSlot(
            canonicalIndestructible,
            EquipmentAbradeKind.Attack,
            EquipmentPartKind.Weapon,
            new Random(0),
            out _));

        var requireSexAffixSlot = new PlayerItemSlot(
            runtimeItemId: 102,
            domainItemId: Guid.NewGuid(),
            template: swordTpl,
            genre: swordTpl.Genre,
            detailType: (short)swordTpl.DetailType,
            particularType: (short)swordTpl.ParticularType,
            level: swordTpl.Level,
            series: swordTpl.ElementSeries,
            quality: swordTpl.Quality,
            randomSeed: 12345u,
            generatorVersion: 4,
            luck: 0,
            currentDurability: 50,
            maxDurability: 50,
            stackCount: 1,
            containerPlace: ItemContainerPlace.Equip,
            gridX: (byte)EquipmentPartKind.Weapon,
            gridY: 0,
            magicAttributes: [new ItemMagicAttributeSlot(0, 38, 0, 0, 0, 1)]);

        Assert.False(requireSexAffixSlot.IsIndestructible);
    }

    #endregion

    #region T024 — User Story 2: Broken Equipment Stat Suspension & Repair Restoration

    [Fact]
    public void BrokenEquipment_ShouldRetainInSlot_SuspendStats_AndClampVitals()
    {
        var player = CreateTestPlayer(series: ElementSeries.Earth, life: 1000, maxLife: 1000);

        // Create armor granting +200 max life (attribute 85 lifemax_v)
        var armorTpl = CreateArmorTemplate();
        var affixes = new[]
        {
            new ItemMagicAttributeSlot(0, 85, 200, 0, 0, 10)
        };
        var armorSlot = new PlayerItemSlot(
            runtimeItemId: 10,
            domainItemId: Guid.NewGuid(),
            template: armorTpl,
            genre: armorTpl.Genre,
            detailType: 2, // Body Armor
            particularType: 0,
            level: armorTpl.Level,
            series: ElementSeries.Metal,
            quality: ItemQualityKind.Normal,
            randomSeed: 12345u,
            generatorVersion: 4,
            luck: 0,
            currentDurability: 1,
            maxDurability: 80,
            stackCount: 1,
            containerPlace: ItemContainerPlace.Equip,
            gridX: (byte)EquipmentPartKind.Body,
            gridY: 0,
            magicAttributes: affixes);

        Assert.True(player.Inventory.TryPlaceItemDirect(armorSlot, ItemContainerPlace.Equip, (byte)EquipmentPartKind.Body, 0, player));
        player.Inventory.RecalculateEquipmentAndFiveElementsActivation(player);

        Assert.Equal(200, player.BonusLifeMax);
        Assert.Equal(1200, player.EffectiveMaxLife);
        player.CurrentLife = 1200; // Top off health to max

        // Now break the armor (durability 1 -> 0)
        armorSlot.SetDurability(0);
        Assert.True(armorSlot.IsBroken);
        Assert.Equal((ushort)0, armorSlot.CurrentDurability);

        // Recalculate stats with broken item in slot
        player.Inventory.RecalculateEquipmentAndFiveElementsActivation(player);

        // Broken item stats must be 100% suspended
        Assert.Equal(0, player.BonusLifeMax);
        Assert.Equal(1000, player.EffectiveMaxLife);

        // Player's CurrentLife must be clamped to newly lowered EffectiveMaxLife (1000)
        Assert.Equal(1000, player.CurrentLife);

        // Broken item remains in slot
        Assert.Same(armorSlot, player.Inventory.GetEquippedItem(EquipmentPartKind.Body));

        // Attempting to re-equip a broken item directly into slot must fail
        Assert.False(PlayerInventoryGrid.CanEquipItem(player, armorSlot, EquipmentPartKind.Body));
    }

    [Fact]
    public void EquipmentRepairCalculator_NormalAndGoldenPricing_ShouldMatchFormulas()
    {
        // 1. Normal Item Repair Pricing
        // Price = (TemplatePrice * 50 / 100) * (missingDur / MaxDur) * (16 + SumMagic) / 16
        var swordTpl = CreateWeaponTemplate(price: 1000);
        var swordSlot = new PlayerItemSlot(
            runtimeItemId: 20,
            domainItemId: Guid.NewGuid(),
            template: swordTpl,
            genre: swordTpl.Genre,
            detailType: 0,
            particularType: 1,
            level: 5,
            series: ElementSeries.Metal,
            quality: ItemQualityKind.Normal,
            randomSeed: 12345u,
            generatorVersion: 4,
            luck: 0,
            currentDurability: 25, // Missing 25 out of 50 (50%)
            maxDurability: 50,
            stackCount: 1,
            containerPlace: ItemContainerPlace.EquipRoom,
            gridX: 0,
            gridY: 0);

        var (canRepair, priceGold, newCur, newMax) = EquipmentRepairCalculator.ComputeRepairQuote(swordSlot);
        Assert.True(canRepair);
        // Step 1: 1000 * 50 / 100 = 500
        // Step 2: 500 * 25 / 50 = 250
        // Step 3: (250 * 16) / 16 = 250
        Assert.Equal(250, priceGold);
        Assert.Equal((ushort)50, newCur);
        Assert.Equal((ushort)50, newMax);

        // 2. Golden Cheap vs Costly Repair Pricing
        // FixDur = Max - Cur = 90 - 70 = 20
        // Cheap: Price = 10 * Level^2 * FixDur = 10 * 100 * 20 = 20,000
        // LostDur = max(1, FixDur / 10) = 20 / 10 = 2 -> NewMax = 90 - 2 = 88
        // Costly: Price = 30 * Level^2 * FixDur = 60,000, NewMax = 90
        var goldenArmorTpl = CreateArmorTemplate();
        var goldenArmorSlot = new PlayerItemSlot(
            runtimeItemId: 21,
            domainItemId: Guid.NewGuid(),
            template: goldenArmorTpl,
            genre: ItemGenreKind.Equipment,
            detailType: 2,
            particularType: 0,
            level: 10,
            series: ElementSeries.Water,
            quality: ItemQualityKind.Golden,
            randomSeed: 12345u,
            generatorVersion: 4,
            luck: 0,
            currentDurability: 70,
            maxDurability: 90,
            stackCount: 1,
            containerPlace: ItemContainerPlace.Equip,
            gridX: (byte)EquipmentPartKind.Body,
            gridY: 0,
            goldRowId: 2);

        // Cheap Golden Repair
        var (cheapCan, cheapPrice, cheapCur, cheapMax) = EquipmentRepairCalculator.ComputeRepairQuote(goldenArmorSlot, isCheapGoldenRepair: true);
        Assert.True(cheapCan);
        Assert.Equal(20000, cheapPrice);
        Assert.Equal((ushort)88, cheapCur);
        Assert.Equal((ushort)88, cheapMax);

        // Costly Golden Repair
        var (costlyCan, costlyPrice, costlyCur, costlyMax) = EquipmentRepairCalculator.ComputeRepairQuote(goldenArmorSlot, isCheapGoldenRepair: false);
        Assert.True(costlyCan);
        Assert.Equal(60000, costlyPrice);
        Assert.Equal((ushort)90, costlyCur);
        Assert.Equal((ushort)90, costlyMax);

        // Repairing resets IsBroken
        goldenArmorSlot.SetDurability(0);
        Assert.True(goldenArmorSlot.IsBroken);
        goldenArmorSlot.Repair(costlyCur, costlyMax);
        Assert.False(goldenArmorSlot.IsBroken);
        Assert.Equal((ushort)90, goldenArmorSlot.CurrentDurability);
        Assert.Equal((ushort)90, goldenArmorSlot.MaxDurability);
    }

    #endregion

    #region T029 — User Story 3: Golden Equipment Generation & Fixed Affix Activation

    [Fact]
    [Trait("Category", "LegacyData")]
    public void GenerateGoldEquipment_FixedSeed_ShouldProduceDeterministicAffixesWithinBounds()
    {
        var catalog = CreateCatalogFromAssets();
        var generator = new ItemGenerator(catalog);

        // RowId 2: Mộng Long Kim Ti Chính Hồng Cà Sa
        int goldRowId = 2;
        uint fixedSeed = 0x98765432u;

        GeneratedItemData item1 = generator.GenerateGoldEquipment(goldRowId, fixedSeed);
        GeneratedItemData item2 = generator.GenerateGoldEquipment(goldRowId, fixedSeed);

        // 1. Repeatability: identical seed must produce identical rolls
        Assert.Equal(item1.CurrentDurability, item2.CurrentDurability);
        Assert.Equal(item1.MaxDurability, item2.MaxDurability);
        Assert.Equal(item1.GoldRowId, item2.GoldRowId);
        Assert.Equal(ItemQualityKind.Golden, item1.Quality);
        Assert.Equal((ushort)2, item1.GoldRowId);

        for (int i = 0; i < ItemConsts.MaxMagicAffixSlots; i++)
        {
            Assert.Equal(item1.MagicAttributes[i].AttributeId, item2.MagicAttributes[i].AttributeId);
            Assert.Equal(item1.MagicAttributes[i].Value0, item2.MagicAttributes[i].Value0);
            Assert.Equal(item1.MagicAttributes[i].Value1, item2.MagicAttributes[i].Value1);
            Assert.Equal(item1.MagicAttributes[i].Value2, item2.MagicAttributes[i].Value2);
        }

        // 2. Fixed affixes: all 6 slots must be within template bounds
        Assert.NotNull(item1.Template.FixedMagicAffixes);
        Assert.Equal(6, item1.Template.FixedMagicAffixes.Count);

        for (int i = 0; i < 6; i++)
        {
            GoldenFixedAffixBindingTemplate binding = item1.Template.FixedMagicAffixes[i];
            ItemMagicAttributeSlot slot = item1.MagicAttributes[binding.SlotIndex];

            Assert.Equal(binding.AttributeId, slot.AttributeId);
            Assert.InRange(slot.Value0, binding.Param0Min, binding.Param0Max);
        }

        // 3. Routing from code
        GeneratedItemData routedItem = generator.GenerateFromItemCode(item1.Template.Code, randomSeedOverride: fixedSeed);
        Assert.Equal(ItemQualityKind.Golden, routedItem.Quality);
        Assert.Equal(item1.GoldRowId, routedItem.GoldRowId);
        Assert.Equal(item1.MagicAttributes[0].Value0, routedItem.MagicAttributes[0].Value0);
    }

    #endregion

    #region T032 — User Story 4: Classic & Extended Equipment Set Bonuses

    [Fact]
    [Trait("Category", "LegacyData")]
    public void ClassicGoldenSuite_AutoDerivedThreshold_ShouldActivateAllHiddenSlotsUnconditionally()
    {
        var catalog = CreateCatalogFromAssets();
        var generator = new ItemGenerator(catalog);

        // SuiteId 1: Mộng Long set (pieces: rows 1, 2, 3, 4)
        int requiredPieces = catalog.GetClassicSuiteRequiredPieces(1);
        Assert.True(requiredPieces > 0, "Suite 1 required pieces should be auto-derived from golden items.");

        var player = CreateTestPlayer(series: ElementSeries.Water);
        player.ItemCatalog = catalog;

        // Equip pieces of Suite 1
        // Row 1: Helm (Head - Part 0)
        GeneratedItemData piece1 = generator.GenerateGoldEquipment(1, 1001u);
        var slot1 = PlayerItemSlot.FromGeneratedItem(1, piece1, ItemContainerPlace.Equip, (byte)EquipmentPartKind.Head, 0);
        Assert.True(player.Inventory.TryPlaceItemDirect(slot1, ItemContainerPlace.Equip, (byte)EquipmentPartKind.Head, 0, player));

        // Row 2: Armor (Body - Part 1)
        GeneratedItemData piece2 = generator.GenerateGoldEquipment(2, 1002u);
        var slot2 = PlayerItemSlot.FromGeneratedItem(2, piece2, ItemContainerPlace.Equip, (byte)EquipmentPartKind.Body, 0);
        Assert.True(player.Inventory.TryPlaceItemDirect(slot2, ItemContainerPlace.Equip, (byte)EquipmentPartKind.Body, 0, player));

        // Partial set (2 pieces < required): hidden slots should NOT be unconditionally unlocked
        player.Inventory.RecalculateEquipmentAndFiveElementsActivation(player, catalog);
        Assert.False(slot1.ActiveHiddenSlots[0] && slot1.ActiveHiddenSlots[1] && slot1.ActiveHiddenSlots[2],
            "Partial classic suite must not unconditionally unlock all 3 hidden slots.");

        // Equip remaining pieces to complete the set
        // Row 3: Belt (Belt - Part 2)
        GeneratedItemData piece3 = generator.GenerateGoldEquipment(3, 1003u);
        var slot3 = PlayerItemSlot.FromGeneratedItem(3, piece3, ItemContainerPlace.Equip, (byte)EquipmentPartKind.Belt, 0);
        Assert.True(player.Inventory.TryPlaceItemDirect(slot3, ItemContainerPlace.Equip, (byte)EquipmentPartKind.Belt, 0, player));

        // Row 4: Pendant (Pendant - Part 9)
        GeneratedItemData piece4 = generator.GenerateGoldEquipment(4, 1004u);
        var slot4 = PlayerItemSlot.FromGeneratedItem(4, piece4, ItemContainerPlace.Equip, (byte)EquipmentPartKind.Pendant, 0);
        Assert.True(player.Inventory.TryPlaceItemDirect(slot4, ItemContainerPlace.Equip, (byte)EquipmentPartKind.Pendant, 0, player));

        // Row 5: Boots (Foot - Part 4)
        GeneratedItemData piece5 = generator.GenerateGoldEquipment(5, 1005u);
        var slot5 = PlayerItemSlot.FromGeneratedItem(5, piece5, ItemContainerPlace.Equip, (byte)EquipmentPartKind.Foot, 0);
        Assert.True(player.Inventory.TryPlaceItemDirect(slot5, ItemContainerPlace.Equip, (byte)EquipmentPartKind.Foot, 0, player));

        // Full set reached (5/5 pieces): all 3 hidden slots on all pieces must unlock unconditionally!
        player.Inventory.RecalculateEquipmentAndFiveElementsActivation(player, catalog);
        Assert.True(slot1.ActiveHiddenSlots[0], "Slot 1 hidden 0 must be active");
        Assert.True(slot1.ActiveHiddenSlots[1], "Slot 1 hidden 1 must be active");
        Assert.True(slot1.ActiveHiddenSlots[2], "Slot 1 hidden 2 must be active");

        Assert.True(slot2.ActiveHiddenSlots[0], "Slot 2 hidden 0 must be active");
        Assert.True(slot2.ActiveHiddenSlots[1], "Slot 2 hidden 1 must be active");
        Assert.True(slot2.ActiveHiddenSlots[2], "Slot 2 hidden 2 must be active");

        // Break 1 piece (slot 1) -> set count drops below required -> unconditional unlock revoked
        slot1.SetDurability(0);
        Assert.True(slot1.IsBroken);
        player.Inventory.RecalculateEquipmentAndFiveElementsActivation(player, catalog);

        // Broken piece slot 1 has no active stats/slots
        Assert.False(slot1.ActiveHiddenSlots[0]);

        // Repair piece 1 -> full set reactivates
        slot1.Repair(slot1.MaxDurability);
        Assert.False(slot1.IsBroken);
        player.Inventory.RecalculateEquipmentAndFiveElementsActivation(player, catalog);

        Assert.True(slot1.ActiveHiddenSlots[0]);
        Assert.True(slot1.ActiveHiddenSlots[1]);
        Assert.True(slot1.ActiveHiddenSlots[2]);
    }

    [Fact]
    [Trait("Category", "LegacyData")]
    public void DuplicateRings_ShouldCountAsSinglePiece_TowardSetThreshold()
    {
        var catalog = CreateCatalogFromAssets();
        var generator = new ItemGenerator(catalog);

        var player = CreateTestPlayer(series: ElementSeries.Water);
        player.ItemCatalog = catalog;

        // Suite 29 ("Vô Danh Chỉ Hoàn" Row 141 + "Vô Danh Giới Chỉ" Row 142) is a 2-piece ring set (level >= 60, no gender lock).
        // Equipping two identical Row 141 rings must count as only 1 distinct piece and NOT activate the 2-piece full-suite bonus.
        Assert.Equal(2, catalog.GetClassicSuiteRequiredPieces(29));
        GeneratedItemData ring1Gen = generator.GenerateGoldEquipment(141, 2001u);
        var dupRing1 = PlayerItemSlot.FromGeneratedItem(101, ring1Gen, ItemContainerPlace.Equip, (byte)EquipmentPartKind.Ring1, 0);
        var dupRing2 = PlayerItemSlot.FromGeneratedItem(102, ring1Gen, ItemContainerPlace.Equip, (byte)EquipmentPartKind.Ring2, 0);

        Assert.True(player.Inventory.TryPlaceItemDirect(dupRing1, ItemContainerPlace.Equip, (byte)EquipmentPartKind.Ring1, 0, player));
        Assert.True(player.Inventory.TryPlaceItemDirect(dupRing2, ItemContainerPlace.Equip, (byte)EquipmentPartKind.Ring2, 0, player));

        player.Inventory.RecalculateEquipmentAndFiveElementsActivation(player, catalog);
        Assert.False(dupRing1.ActiveHiddenSlots[0], "Two duplicate Row 141 rings must not unlock Suite 29 hidden allskill_v bonus.");

        // Swapping Ring2 to distinct Row 142 (same Suite 29) brings distinct suite piece count to 2 -> unlocks all Suite 29 hidden slots!
        Assert.True(player.Inventory.TryRemoveItem(102u, out _, player));
        GeneratedItemData ring142Gen = generator.GenerateGoldEquipment(142, 2002u);
        var ring2Slot = PlayerItemSlot.FromGeneratedItem(103, ring142Gen, ItemContainerPlace.Equip, (byte)EquipmentPartKind.Ring2, 0);
        Assert.True(player.Inventory.TryPlaceItemDirect(ring2Slot, ItemContainerPlace.Equip, (byte)EquipmentPartKind.Ring2, 0, player));

        player.Inventory.RecalculateEquipmentAndFiveElementsActivation(player, catalog);
        Assert.True(dupRing1.ActiveHiddenSlots[0] && dupRing1.ActiveHiddenSlots[1] && dupRing1.ActiveHiddenSlots[2],
            "Distinct Suite 29 pair (Row 141 + Row 142) must unlock Suite 29 hidden slots.");
    }

    #endregion
}
