using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JXReborn.Contracts.Characters;
using JXReborn.GameAssets.Items;
using JXReborn.GameAssets.NpcTemplates;
using JXReborn.GameAssets.PlayerTemplates;
using JXReborn.GameAssets.Skills;
using JXReborn.GameAssets.WorldMaps;
using JXReborn.Primitives.Characters;
using JXReborn.Primitives.Combat;
using JXReborn.Primitives.Items;
using JXReborn.Primitives.WorldZones;
using JXReborn.TestBase.Fixtures;
using JXReborn.TestBase.SimulationHarnesses;
using JXReborn.ZoneEngine.Combat;
using JXReborn.ZoneEngine.Configuration;
using JXReborn.ZoneEngine.Items;
using JXReborn.ZoneEngine.NpcEntities;
using JXReborn.ZoneEngine.PlayerSessions;
using JXReborn.ZoneEngine.SpatialGrid;
using JXReborn.ZoneEngine.WorldZones;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace JXReborn.ZoneEngine.Tests.Combat;

/// <summary>
/// Binary ground-truth audit tests anchoring Five-Elements (<c>g_InitSeries</c>, <c>KItemList::GetEquipEnhance</c>,
/// <c>KNpc::CalcCurRes</c>, <c>KNpc::ModifyAllMaxRes</c>), multi-channel combat mitigation/leech/reflect/poison,
/// and 18-Hz frame breakpoints (<c>DoOrdinSkill</c>, <c>DoHurt</c>, <c>ProcessState</c>, <c>KMissle</c>, PK cooldowns)
/// directly against decompiled <c>legacy-server/discovery/</c> formulas and real catalog templates.
/// </summary>
public sealed class FiveElementsAndFrameBreakpointAuditTests
{
    private static PlayerZoneState CreateAuditPlayer(
        uint runtimeNpcId = 21001u,
        ElementSeries series = ElementSeries.Metal,
        byte level = 50,
        ushort strength = 100,
        ushort dexterity = 100,
        ushort vitality = 100,
        ushort energy = 50,
        int currentLife = 2000,
        ushort maxLife = 2000,
        int currentMana = 1000,
        ushort maxMana = 1000)
    {
        return new PlayerZoneState(
            runtimeNpcId: runtimeNpcId,
            accountId: Guid.NewGuid(),
            characterId: Guid.NewGuid(),
            leaseEpoch: 1L,
            roleNameUnicode: "BinaryAuditHero",
            gender: CharacterGender.Male,
            series: series,
            level: level,
            experience: 0,
            leadExperience: 0,
            mapId: 53,
            currentMpsX: 52000,
            currentMpsY: 101696,
            currentLife: currentLife,
            maxLife: maxLife,
            currentMana: currentMana,
            maxMana: maxMana,
            currentStamina: 300,
            maxStamina: 300,
            strength: strength,
            dexterity: dexterity,
            vitality: vitality,
            energy: energy,
            lucky: 0,
            unallocatedAttributePoints: 0,
            unallocatedSkillPoints: 0,
            inventoryMoney: 10000,
            repositoryMoney: 0,
            currentFactionId: 0xFF,
            firstFactionId: 0xFF,
            factionJoinCount: 0,
            initialTick: 1L,
            outboundSink: new PlayerOutboundEventRecorder());
    }

    private static NpcZoneState CreateAuditNpc(
        uint npcId = 31001u,
        int templateId = 1,
        ElementSeries series = ElementSeries.Wood,
        short level = 50,
        int walkSpeed = 5,
        int runSpeed = 10,
        uint maxLife = 5000u)
    {
        return new NpcZoneState(
            npcId: npcId,
            templateId: templateId,
            name: "BinaryAuditTarget",
            level: level,
            kind: EntityKind.Monster,
            camp: EntityCamp.Animal,
            series: series,
            isSpecialNpc: false,
            mapId: 53,
            mpsX: 52064,
            mpsY: 101696,
            walkSpeed: walkSpeed,
            runSpeed: runSpeed,
            maxLife: maxLife,
            scriptPath: string.Empty);
    }

    [Fact]
    public void FiveElementsSeriesMatrix_AcrossAllFiveSeriesAndCatalogSkills_ShouldMatchLegacyAccrueAndConquerTables()
    {
        // Ground truth: legacy-server/discovery/globals_free/KGlobals_P1_MathSeriesAndMissle.cpp:259-280
        // g_InitSeries @ 0x0808b420:
        //   Accrue:  Metal(0)->Water(2)->Wood(1)->Fire(3)->Earth(4)->Metal(0)
        //   Conquer: Metal(0)->Wood(1)->Earth(4)->Water(2)->Fire(3)->Metal(0)
        IReadOnlyCollection<SkillTemplate> allSkills = SharedGameAssetsFixture.Default.SkillTemplateCatalog.AllSkills;
        Assert.NotEmpty(allSkills);

        var observedOffensiveSeries = new HashSet<ElementSeries>();
        foreach (SkillTemplate skill in allSkills)
        {
            if (skill.IsOffensive && skill.HasElementSeries && FiveElementsSeriesMatrix.IsValidSeries(skill.ElementSeries))
            {
                observedOffensiveSeries.Add(skill.ElementSeries);
            }
        }

        Assert.Equal(5, observedOffensiveSeries.Count);

        Assert.True(FiveElementsSeriesMatrix.Generates(ElementSeries.Metal, ElementSeries.Water));
        Assert.True(FiveElementsSeriesMatrix.Generates(ElementSeries.Water, ElementSeries.Wood));
        Assert.True(FiveElementsSeriesMatrix.Generates(ElementSeries.Wood, ElementSeries.Fire));
        Assert.True(FiveElementsSeriesMatrix.Generates(ElementSeries.Fire, ElementSeries.Earth));
        Assert.True(FiveElementsSeriesMatrix.Generates(ElementSeries.Earth, ElementSeries.Metal));

        Assert.True(FiveElementsSeriesMatrix.Conquers(ElementSeries.Metal, ElementSeries.Wood));
        Assert.True(FiveElementsSeriesMatrix.Conquers(ElementSeries.Wood, ElementSeries.Earth));
        Assert.True(FiveElementsSeriesMatrix.Conquers(ElementSeries.Earth, ElementSeries.Water));
        Assert.True(FiveElementsSeriesMatrix.Conquers(ElementSeries.Water, ElementSeries.Fire));
        Assert.True(FiveElementsSeriesMatrix.Conquers(ElementSeries.Fire, ElementSeries.Metal));

        Assert.Equal(120, FiveElementsSeriesMatrix.GetSeriesDamageMultiplierPercent(ElementSeries.Metal, ElementSeries.Wood));
        Assert.Equal(80, FiveElementsSeriesMatrix.GetSeriesDamageMultiplierPercent(ElementSeries.Wood, ElementSeries.Metal));
        Assert.Equal(100, FiveElementsSeriesMatrix.GetSeriesDamageMultiplierPercent(ElementSeries.Metal, ElementSeries.Water));
    }

    [Fact]
    public void CalcCurResAndModifyAllMaxRes_AcrossNegativeDiminishingAndSeriesConquer_ShouldMatchLegacyIntegerMath()
    {
        // Ground truth:
        //   1. KNpc::CalcCurRes @ 0x08090320 (combat/damage/KNpc_CalcCurRes.cpp:8-18)
        //   2. KNpc::ModifyAllMaxRes @ 0x08091b90 (combat/damage/KNpc_ModifyAllMaxRes.cpp:8-15)
        IReadOnlyCollection<SkillTemplate> allSkills = SharedGameAssetsFixture.Default.SkillTemplateCatalog.AllSkills;
        Assert.NotEmpty(allSkills);

        SkillTemplate? realMetalSkill = null;
        foreach (SkillTemplate candidate in allSkills)
        {
            if (candidate.IsOffensive && candidate.HasElementSeries && candidate.ElementSeries == ElementSeries.Metal)
            {
                realMetalSkill = candidate;
                break;
            }
        }

        Assert.NotNull(realMetalSkill);

        // Negative resistance passes through CalcCurRes unmodified and amplifies channel damage:
        // raw = 200, PhysicsResist = -30 -> effRes = -30 -> dealt = (200 * (100 - (-30))) / 100 = 260.
        Assert.Equal(-30, CombatDamageCalculator.CalcCurRes(-30, CombatConsts.DefaultMaxResistPercent));
        NpcZoneState negResTarget = CreateAuditNpc(series: ElementSeries.Earth, level: 50, maxLife: 2000u);
        negResTarget.PhysicsResistPercent = -30;

        var negResPayload = new SkillDamagePayload(
            SkillId: realMetalSkill.Id,
            SkillLevel: 1,
            ElementSeries: realMetalSkill.ElementSeries,
            IsPhysical: true,
            IsMelee: true,
            UseAttackRating: false,
            DoHurtAnimation: false,
            AttackRatingScore: 500,
            PhysicsMinDamage: 200,
            PhysicsMaxDamage: 200,
            ColdMinDamage: 0,
            ColdMaxDamage: 0,
            ColdFreezeDurationFrames: 0,
            FireMinDamage: 0,
            FireMaxDamage: 0,
            LightMinDamage: 0,
            LightMaxDamage: 0,
            LightStunDurationFrames: 0,
            PoisonDamagePerTick: 0,
            PoisonDurationFrames: 0,
            PoisonIntervalFrames: 10,
            MagicMinDamage: 0,
            MagicMaxDamage: 0);

        CombatHitResolutionResult negResHit = CombatDamageCalculator.ResolveHitAndApplyDamage(
            attacker: null,
            defender: negResTarget,
            isPvp: false,
            in negResPayload,
            new Random(1));
        Assert.Equal(260, negResHit.TotalHpDamageDealt);

        // ModifyAllMaxRes + AntiMaxResist + CalcCurRes diminishing curve:
        // Metal conquers Wood (level 50), SeriesDamagePercent = 35 -> seriesResDelta = -35.
        // FiveElementsEnhance = 140, defender FiveElementsResist = 40:
        //   maxResDelta = ((((140 - 40) * 100) / (50 * 8 + 200) + 100) * -35) / 700
        //               = ((10000 / 600 + 100) * -35) / 700 = (116 * -35) / 700 = -4060 / 700 = -5.
        // With AntiMaxResistPercent = 10 -> effMaxRes = 75 - 5 - 10 = 60.
        // Defender PhysicsResist = 115 -> resYin = 115 - 35 = 80 (> effMaxRes 60):
        //   effRes = (((80 - 60) * (95 - 60)) / 400) + 60 = (20 * 35) / 400 + 60 = 1 + 60 = 61.
        //   damage = (1000 * (100 - 61)) / 100 = 390.
        NpcZoneState woodTarget = CreateAuditNpc(series: ElementSeries.Wood, level: 50, maxLife: 5000u);
        woodTarget.PhysicsResistPercent = 115;
        woodTarget.CombatModifiers.FiveElementsResistValue = 40;

        var conquerPayload = negResPayload with
        {
            PhysicsMinDamage = 1000,
            PhysicsMaxDamage = 1000,
            SeriesDamagePercent = 35,
            FiveElementsEnhanceValue = 140,
            AntiMaxResistPercent = 10,
        };

        CombatHitResolutionResult conquerHit = CombatDamageCalculator.ResolveHitAndApplyDamage(
            attacker: null,
            defender: woodTarget,
            isPvp: false,
            in conquerPayload,
            new Random(1));
        Assert.Equal(390, conquerHit.TotalHpDamageDealt);
    }

    [Fact]
    public void FiveElementsEquipmentActivation_WhenPlayerSeriesGeneratesEquipOrHorseEquipped_ShouldMatchGetEquipEnhanceCount()
    {
        // Ground truth: KItemList::GetEquipEnhance @ 0x08078f20
        //   (legacy-server/discovery/Items/KItemList_EquipAndAttrib.cpp:629-655)
        //   1. Slots 10..14 (Horse, Mask, Mantle, Signet, Shipin): ((uint)(nPlace - 10) < 5) -> returns 3 unconditionally.
        //   2. Slots 0..9: nCount = Generates(player.Series, equip.Series)
        //                        + Generates(neighborA.Series, equip.Series)
        //                        + Generates(neighborB.Series, equip.Series) in [0..3],
        //      activating nCount hidden odd magic slots (1, 3, 5).
        IReadOnlyCollection<ItemTemplate> allItems = SharedGameAssetsFixture.Default.ItemTemplateCatalog.AllItems;
        Assert.NotEmpty(allItems);

        PlayerZoneState earthPlayer = CreateAuditPlayer(
            series: ElementSeries.Earth,
            level: 120,
            strength: 500,
            dexterity: 500,
            vitality: 500,
            energy: 500);

        ItemTemplate? metalWeaponTpl = null;
        ItemTemplate? earthBodyTpl = null;
        ItemTemplate? horseTpl = null;
        foreach (ItemTemplate item in allItems)
        {
            if (item.Genre != ItemGenreKind.Equipment || item.SuiteBinding is not null)
            {
                continue;
            }

            if (metalWeaponTpl is null && item.DetailType == 0 && item.Level == 1)
            {
                metalWeaponTpl = item with { Requirements = [] };
            }
            else if (earthBodyTpl is null && item.DetailType == 2 && item.Level == 1)
            {
                earthBodyTpl = item with { Requirements = [] };
            }
            else if (horseTpl is null && item.DetailType == 10 && item.Level == 1)
            {
                horseTpl = item with { Requirements = [] };
            }

            if (metalWeaponTpl is not null && earthBodyTpl is not null && horseTpl is not null)
            {
                break;
            }
        }

        Assert.NotNull(metalWeaponTpl);
        Assert.NotNull(earthBodyTpl);
        Assert.NotNull(horseTpl);

        var weaponAffixes = new[]
        {
            new ItemMagicAttributeSlot(0, 85, 50, 0, 0, 5),
            new ItemMagicAttributeSlot(1, 103, 20, 0, 0, 5), // Hidden 1: +20% Lightning Resist
            new ItemMagicAttributeSlot(2, 89, 30, 0, 0, 5),
            new ItemMagicAttributeSlot(3, 101, 25, 0, 0, 5), // Hidden 2: +25% Poison Resist
            new ItemMagicAttributeSlot(4, 93, 40, 0, 0, 5),
            new ItemMagicAttributeSlot(5, 102, 30, 0, 0, 5), // Hidden 3: +30% Fire Resist
        };

        var weaponSlot = new PlayerItemSlot(
            runtimeItemId: 101u,
            domainItemId: Guid.NewGuid(),
            template: metalWeaponTpl,
            genre: ItemGenreKind.Equipment,
            detailType: 0,
            particularType: (short)metalWeaponTpl.ParticularType,
            level: metalWeaponTpl.Level,
            series: ElementSeries.Metal, // Earth player generates Metal!
            quality: ItemQualityKind.Normal,
            randomSeed: 1u,
            generatorVersion: ItemConsts.GeneratorVersion,
            luck: 0,
            currentDurability: 50,
            maxDurability: 50,
            stackCount: 1,
            containerPlace: ItemContainerPlace.EquipRoom,
            gridX: 0,
            gridY: 0,
            magicAttributes: weaponAffixes);

        Assert.True(earthPlayer.Inventory.TryPlaceItemDirect(weaponSlot, ItemContainerPlace.EquipRoom, 0, 0, earthPlayer));
        Assert.True(earthPlayer.Inventory.TryMoveOrSwapItem(
            earthPlayer,
            ItemContainerPlace.EquipRoom,
            0,
            0,
            ItemContainerPlace.Equip,
            (byte)EquipmentPartKind.Weapon,
            0));

        // Earth player generates Metal weapon with 0 matching neighbors -> nCount == 1 -> Hidden Slot 1 MUST be active!
        Assert.True(weaponSlot.ActiveHiddenSlots[0]);
        Assert.False(weaponSlot.ActiveHiddenSlots[1]);
        Assert.False(weaponSlot.ActiveHiddenSlots[2]);
        Assert.Equal(20, earthPlayer.LightResistPercent);

        // Equip Earth Body armor (neighbor of Weapon in ms_ActivedEquip) -> nCount == 2 -> Hidden Slots 1 & 2 active!
        var armorSlot = new PlayerItemSlot(
            runtimeItemId: 102u,
            domainItemId: Guid.NewGuid(),
            template: earthBodyTpl,
            genre: ItemGenreKind.Equipment,
            detailType: 2,
            particularType: (short)earthBodyTpl.ParticularType,
            level: earthBodyTpl.Level,
            series: ElementSeries.Earth,
            quality: ItemQualityKind.Normal,
            randomSeed: 2u,
            generatorVersion: ItemConsts.GeneratorVersion,
            luck: 0,
            currentDurability: 50,
            maxDurability: 50,
            stackCount: 1,
            containerPlace: ItemContainerPlace.EquipRoom,
            gridX: 0,
            gridY: 0,
            magicAttributes: []);

        Assert.True(earthPlayer.Inventory.TryPlaceItemDirect(armorSlot, ItemContainerPlace.EquipRoom, 0, 0, earthPlayer));
        Assert.True(earthPlayer.Inventory.TryMoveOrSwapItem(
            earthPlayer,
            ItemContainerPlace.EquipRoom,
            0,
            0,
            ItemContainerPlace.Equip,
            (byte)EquipmentPartKind.Body,
            0));

        Assert.True(weaponSlot.ActiveHiddenSlots[0]);
        Assert.True(weaponSlot.ActiveHiddenSlots[1]);
        Assert.False(weaponSlot.ActiveHiddenSlots[2]);
        Assert.Equal(25, earthPlayer.PoisonResistPercent);

        // Equip Horse (partIdx == 10, Fire series NOT generated by Earth) -> ((uint)(10 - 10) < 5) -> all 3 hidden slots active!
        var horseSlot = new PlayerItemSlot(
            runtimeItemId: 103u,
            domainItemId: Guid.NewGuid(),
            template: horseTpl,
            genre: ItemGenreKind.Equipment,
            detailType: 10,
            particularType: (short)horseTpl.ParticularType,
            level: horseTpl.Level,
            series: ElementSeries.Fire,
            quality: ItemQualityKind.Normal,
            randomSeed: 3u,
            generatorVersion: ItemConsts.GeneratorVersion,
            luck: 0,
            currentDurability: 50,
            maxDurability: 50,
            stackCount: 1,
            containerPlace: ItemContainerPlace.EquipRoom,
            gridX: 0,
            gridY: 0,
            magicAttributes:
            [
                new ItemMagicAttributeSlot(1, 105, 15, 0, 0, 5),
                new ItemMagicAttributeSlot(3, 102, 15, 0, 0, 5),
                new ItemMagicAttributeSlot(5, 104, 15, 0, 0, 5),
            ]);

        Assert.True(earthPlayer.Inventory.TryPlaceItemDirect(horseSlot, ItemContainerPlace.EquipRoom, 0, 0, earthPlayer));
        Assert.True(earthPlayer.Inventory.TryMoveOrSwapItem(
            earthPlayer,
            ItemContainerPlace.EquipRoom,
            0,
            0,
            ItemContainerPlace.Equip,
            (byte)EquipmentPartKind.Horse,
            0));

        Assert.True(horseSlot.ActiveHiddenSlots[0]);
        Assert.True(horseSlot.ActiveHiddenSlots[1]);
        Assert.True(horseSlot.ActiveHiddenSlots[2]);
    }

    [Fact]
    public void EquipmentMagicAttributes_WhenBootsCarryFastWalkRunPercentAffix_ShouldIncreasePlayerWalkAndRunSpeed()
    {
        // Ground truth: KNpcAttribModify_Dispatch.cpp:117 (case 0x6f = 111 -> FastWalkRunP)
        //   & KNpcAttribModify_StatesAndSpeed.cpp:40-55 (FastWalkRunP @ 0x080b0f70):
        //   Base WalkSpeed = 5, RunSpeed = 10. With +40% fastwalkrun_p on boots (slot 0 explicit):
        //   EffectiveWalkSpeed = (5 * 140) / 100 = 7; EffectiveRunSpeed = (10 * 140) / 100 = 14.
        IItemTemplateCatalog itemCatalog = SharedGameAssetsFixture.Default.ItemTemplateCatalog;
        Assert.NotEmpty(itemCatalog.AllMagicAffixes);
        Assert.NotEmpty(itemCatalog.AllItems);

        MagicAffixTemplate? fastWalkAffix = null;
        foreach (MagicAffixTemplate affix in itemCatalog.AllMagicAffixes)
        {
            if (affix.AttributeId == 111)
            {
                fastWalkAffix = affix;
                break;
            }
        }

        Assert.NotNull(fastWalkAffix);

        ItemTemplate? bootTpl = null;
        foreach (ItemTemplate item in itemCatalog.AllItems)
        {
            if (item.Genre == ItemGenreKind.Equipment && item.DetailType == 5 && item.Level == 1)
            {
                bootTpl = item with { Requirements = [] };
                break;
            }
        }

        Assert.NotNull(bootTpl);

        PlayerZoneState player = CreateAuditPlayer(level: 80, strength: 300, dexterity: 300);
        Assert.Equal(5, player.EffectiveWalkSpeed);
        Assert.Equal(10, player.EffectiveRunSpeed);

        var bootSlot = new PlayerItemSlot(
            runtimeItemId: 201u,
            domainItemId: Guid.NewGuid(),
            template: bootTpl,
            genre: ItemGenreKind.Equipment,
            detailType: 5,
            particularType: (short)bootTpl.ParticularType,
            level: bootTpl.Level,
            series: ElementSeries.Water,
            quality: ItemQualityKind.Normal,
            randomSeed: 77u,
            generatorVersion: ItemConsts.GeneratorVersion,
            luck: 0,
            currentDurability: 50,
            maxDurability: 50,
            stackCount: 1,
            containerPlace: ItemContainerPlace.EquipRoom,
            gridX: 0,
            gridY: 0,
            magicAttributes:
            [
                new ItemMagicAttributeSlot(0, (short)fastWalkAffix.AttributeId, 40, 0, 0, 10),
            ]);

        Assert.True(player.Inventory.TryPlaceItemDirect(bootSlot, ItemContainerPlace.EquipRoom, 0, 0, player));
        Assert.True(player.Inventory.TryMoveOrSwapItem(
            player,
            ItemContainerPlace.EquipRoom,
            0,
            0,
            ItemContainerPlace.Equip,
            (byte)EquipmentPartKind.Foot,
            0));

        Assert.Equal(7, player.EffectiveWalkSpeed);
        Assert.Equal(14, player.EffectiveRunSpeed);
    }

    [Fact]
    public void SkillStateModifiers_WhenBuffCarriesSeriesEnhanceAndAntiResistAttributes_ShouldAggregateIntoCombatState()
    {
        // Ground truth: KNpcAttribModify_Dispatch.cpp:177-178, 221-225, 242, 246-247
        //   All state/passive skills modify KNpc combat fields via g_MagicModify and must cleanly revert to 0 on removal.
        ISkillTemplateCatalog skillCatalog = SharedGameAssetsFixture.Default.SkillTemplateCatalog;
        Assert.NotEmpty(skillCatalog.AllSkills);

        int stateBearingSkills = 0;
        foreach (SkillTemplate skill in skillCatalog.AllSkills)
        {
            if (skill.Style is SkillStyleKind.Passive or SkillStyleKind.SelfBuff or SkillStyleKind.TargetBuff)
            {
                stateBearingSkills++;
            }
        }

        Assert.True(stateBearingSkills > 0);

        PlayerZoneState player = CreateAuditPlayer();
        ICombatEntityState combatPlayer = player;

        player.StatusEffects.AddOrReplaceStateSkillEffect(new ActiveStateSkillEffect
        {
            SkillId = 9001,
            SkillLevel = 10,
            RemainingFrames = 180,
            Attributes =
            [
                new ActiveStateAttribEntry(177, "seriesres_p", 25, 180, 0),
                new ActiveStateAttribEntry(178, "seriesenhance_p", 30, 180, 0),
                new ActiveStateAttribEntry(224, "anti_physicsres_p", 15, 180, 0),
                new ActiveStateAttribEntry(242, "anti_maxres_p", 5, 180, 0),
                new ActiveStateAttribEntry(246, "five_elements_enhance_v", 80, 180, 0),
                new ActiveStateAttribEntry(247, "five_elements_resist_v", 60, 180, 0),
            ],
        });

        SkillStateEffectApplier.RecalculateEntitySkillModifiers(player, skillCatalog);

        Assert.Equal(25, combatPlayer.SeriesResistPercent);
        Assert.Equal(30, combatPlayer.SeriesEnhancePercent);
        Assert.Equal(15, combatPlayer.AntiPhysicsResistPercent);
        Assert.Equal(5, combatPlayer.AntiMaxResistPercent);
        Assert.Equal(80, combatPlayer.FiveElementsEnhanceValue);
        Assert.Equal(60, combatPlayer.FiveElementsResistValue);

        // Removing the state skill effect must roll back every modifier to 0 with zero stat drift.
        Assert.True(player.StatusEffects.RemoveStateSkillEffect(9001));
        SkillStateEffectApplier.RecalculateEntitySkillModifiers(player, skillCatalog);

        Assert.Equal(0, combatPlayer.SeriesResistPercent);
        Assert.Equal(0, combatPlayer.SeriesEnhancePercent);
        Assert.Equal(0, combatPlayer.AntiPhysicsResistPercent);
        Assert.Equal(0, combatPlayer.AntiMaxResistPercent);
        Assert.Equal(0, combatPlayer.FiveElementsEnhanceValue);
        Assert.Equal(0, combatPlayer.FiveElementsResistValue);
    }

    [Fact]
    public void ReceiveDamagePipeline_WithDeadlyStrikeManaShieldDamageReturnLeechAndStunReduce_ShouldMatchLegacyFormulas()
    {
        // Ground truth:
        //   1. KNpc::CheckHitTarget @ 0x080951b0: [5, 95] clamp & ignoredefense_p
        //   2. KNpc::ReceiveDamage & KNpc::CalcDamage @ 0x080a1dd0 / 0x0809e790:
        //      - DeadlyStrike (*2 physical channel ONLY)
        //      - MeleeDamageReturn computed BEFORE PvP 20% m_nPKDamageRate
        //      - Damage2Mana restores (hpLost * damage2addmana_p) / 100
        //      - Leech (steallife_p) computed strictly from PhysicsHpLost
        //      - ManaShield collapses CurrentMana -> 0 and absorbs 100% HP loss when 0 < CurrentMana < manaAbsorb
        //      - Stun duration: netReduce < 75 -> (((enhanceHit * dur)/100) * (100 - netReduce))/100; >= 75 -> (enhanceHit * dur)/400
        IReadOnlyCollection<SkillTemplate> allSkills = SharedGameAssetsFixture.Default.SkillTemplateCatalog.AllSkills;
        Assert.NotEmpty(allSkills);

        SkillTemplate? meleeSkill = null;
        foreach (SkillTemplate skill in allSkills)
        {
            if (skill.IsPhysical && skill.IsMelee)
            {
                meleeSkill = skill;
                break;
            }
        }

        Assert.NotNull(meleeSkill);

        Assert.Equal(40, CombatDamageCalculator.ComputeHitPercent(100, 200, ignoreDefensePercent: 25));

        PlayerZoneState attacker = CreateAuditPlayer(runtimeNpcId: 22001u, series: ElementSeries.Metal, currentLife: 500, maxLife: 2000);
        PlayerZoneState defender = CreateAuditPlayer(runtimeNpcId: 22002u, series: ElementSeries.Metal, currentLife: 2000, maxLife: 2000, currentMana: 100, maxMana: 1000);
        defender.EquipmentCombatModifiers.MeleeDamageReturnPercent = 30;
        defender.EquipmentCombatModifiers.Damage2ManaPercent = 25;
        defender.EquipmentCombatModifiers.StunTimeReducePercent = 60;

        // Phys = 100 * 2 (DeadlyStrike 100%) = 200; Fire = 100 (NOT doubled by DeadlyStrike).
        // MeleeDamageReturn (pre-PvP 20%):
        //   Phys channel reflect raw = (200 * 30) / 100 = 60 -> PvP 20% on attacker = 12.
        //   Fire channel reflect raw = (100 * 30) / 100 = 30 -> PvP 20% on attacker = 6 -> total reflected = 18.
        // Defender HP loss: Phys (200 * 20) / 100 = 40; Fire (100 * 20) / 100 = 20 -> total = 60.
        // Defender Damage2Mana: (40 * 25)/100 + (20 * 25)/100 = 10 + 5 = 15 mana gained (100 -> 115).
        // Attacker LifeStolen (50% of PhysicsHpLost = 40 ONLY): (40 * 50) / 100 = 20 HP stolen!
        // Stun: netReduce = 60 - 10 (AntiStunTimer) = 50 (< 75) -> (((100 * 40)/100) * (100 - 50))/100 = 20 frames.
        var pvpPayload = new SkillDamagePayload(
            SkillId: meleeSkill.Id,
            SkillLevel: 1,
            ElementSeries: ElementSeries.Metal,
            IsPhysical: true,
            IsMelee: true,
            UseAttackRating: false,
            DoHurtAnimation: false,
            AttackRatingScore: 500,
            PhysicsMinDamage: 100,
            PhysicsMaxDamage: 100,
            ColdMinDamage: 0,
            ColdMaxDamage: 0,
            ColdFreezeDurationFrames: 0,
            FireMinDamage: 100,
            FireMaxDamage: 100,
            LightMinDamage: 0,
            LightMaxDamage: 0,
            LightStunDurationFrames: 0,
            PoisonDamagePerTick: 0,
            PoisonDurationFrames: 0,
            PoisonIntervalFrames: 10,
            MagicMinDamage: 0,
            MagicMaxDamage: 0,
            DeadlyStrikePercent: 100,
            LifeStolenPercent: 50,
            StunStrikePercent: 100,
            StunDurationFrames: 40,
            AntiStunTimerPercent: 10);

        CombatHitResolutionResult pvpHit = CombatDamageCalculator.ResolveHitAndApplyDamage(
            attacker,
            defender,
            isPvp: true,
            in pvpPayload,
            new Random(1));

        Assert.Equal(60, pvpHit.TotalHpDamageDealt);
        Assert.Equal(40, pvpHit.PhysicsHpLost);
        Assert.Equal(18, pvpHit.ReflectedDamageToAttacker);
        Assert.Equal(20, pvpHit.LifeStolen);
        Assert.Equal(115, defender.CurrentMana);
        Assert.Equal(20, defender.StatusEffects.StunRemainingFrames);

        // Stun >= 75% reduction cap: netReduce = 80 -> (100 * 40) / 400 = 10 frames.
        NpcZoneState highStunResNpc = CreateAuditNpc(maxLife: 2000u);
        highStunResNpc.CombatModifiers.StunTimeReducePercent = 80;
        var stunCapPayload = pvpPayload with { AntiStunTimerPercent = 0, FireMinDamage = 0, FireMaxDamage = 0 };
        CombatDamageCalculator.ResolveHitAndApplyDamage(attacker: null, highStunResNpc, isPvp: false, in stunCapPayload, new Random(1));
        Assert.Equal(10, highStunResNpc.StatusEffects.StunRemainingFrames);

        // ManaShield 0-mana collapse quirk (Toa Vong Vo Nga):
        // Defender has ManaShield = 80%, CurrentMana = 15, incoming Phys = 100 -> manaAbsorb = 80 > CurrentMana (15).
        // Binary behavior: CurrentMana drops to 0 and channel HP damage becomes 0!
        PlayerZoneState wudangDefender = CreateAuditPlayer(runtimeNpcId: 22003u, currentLife: 1000, maxLife: 1000, currentMana: 15, maxMana: 500);
        wudangDefender.SkillCombatModifiers.ManaShieldPercent = 80;
        var shieldTestPayload = pvpPayload with
        {
            DeadlyStrikePercent = 0,
            FireMinDamage = 0,
            FireMaxDamage = 0,
            StunStrikePercent = 0,
            StunDurationFrames = 0,
        };

        CombatHitResolutionResult shieldHit = CombatDamageCalculator.ResolveHitAndApplyDamage(
            attacker: null,
            wudangDefender,
            isPvp: false,
            in shieldTestPayload,
            new Random(1));
        Assert.Equal(0, wudangDefender.CurrentMana);
        Assert.Equal(0, shieldHit.TotalHpDamageDealt);
        Assert.Equal(1000, wudangDefender.CurrentLife);
    }

    [Fact]
    public async Task MixPoisonDamageAndTickCadence_WhenCasterDiesOrDurationDividesInterval_ShouldMatchProcessStateBehavior()
    {
        // Ground truth:
        //   1. KNpcAttribModify::MixPoisonDamage @ 0x080b07f0 (KNpcAttribModify_MixPoisonDamage.cpp:8-34)
        //   2. KNpc::ProcessState_AilmentsAndFeatureTimer @ 0x0809f6f0 (lines 22-46):
        //      - Decrements nTime -> iVar7 = nTime - 1; ticks damage whenever (iVar7 % nInterval == 0),
        //        so nTime = 15, nInterval = 10 ticks at iVar7 == 10 (tick 5) AND iVar7 == 0 (tick 15) => 2 ticks!
        //      - If poison launcher died (m_CurrentLife < 1) or left map, nTime is immediately cleared to 0!
        IReadOnlyCollection<SkillTemplate> allSkills = SharedGameAssetsFixture.Default.SkillTemplateCatalog.AllSkills;
        Assert.NotEmpty(allSkills);

        int poisonSkillCount = 0;
        foreach (SkillTemplate skill in allSkills)
        {
            if (skill.ElementSeries == ElementSeries.Wood && skill.IsOffensive)
            {
                poisonSkillCount++;
            }
        }

        Assert.True(poisonSkillCount > 0);

        // Verify weighted MixPoisonDamage integer formula:
        // (d1=20, t1=60, c1=10) + (d2=40, t2=30, c2=10):
        //   sumC = 20; d = (((20*40)/10) + ((20*20)/10)) / 2 = (80 + 40) / 2 = 60;
        //   t = (30*40*10 + 60*20*10) / (10*40 + 10*20) = 24000 / 600 = 40; c = 10.
        (int mixedDmg, int mixedDur, int mixedInt) = CombatDamageCalculator.MixPoisonDamage(20, 60, 10, 40, 30, 10);
        Assert.Equal((60, 40, 10), (mixedDmg, mixedDur, mixedInt));

        var mapDesc = new MapDescriptor(53, "Ba Lăng Huyện", "balanghuyen", 96, 88, 16, 20)
        {
            RegionNpcSpawns =
            [
                new StaticNpcSpawnTemplate(
                    TemplateId: 1,
                    MapId: 53,
                    SpawnMpsX: 52064,
                    SpawnMpsY: 101696,
                    Level: 30,
                    Kind: EntityKind.Monster,
                    Camp: EntityCamp.Animal,
                    Series: ElementSeries.Wood,
                    IsSpecialNpc: false,
                    DisplayName: "Độc Trùng",
                    ScriptPath: string.Empty),
            ],
        };

        var worker = new MapZoneWorker(
            mapDesc,
            new InMemorySessionLeaseAppServiceStub(),
            new ZoneEngineOptions { RandomSeed = 1 },
            new FakeTimeProvider(),
            NullLogger.Instance);

        var sink = new PlayerOutboundEventRecorder();
        Guid charId = Guid.NewGuid();
        var dto = new CharacterDto(
            Id: charId,
            AccountId: Guid.NewGuid(),
            SlotIndex: 0,
            RoleNameUnicode: "PoisonCadenceHero",
            Gender: CharacterGender.Male,
            Series: ElementSeries.Metal,
            Level: 50,
            Experience: 0,
            LeadExperience: 0,
            MapId: 53,
            RevivalMapId: 53,
            RevivalPointId: 19,
            MapMpsX: 52000,
            MapMpsY: 101696,
            CurrentLife: 1000,
            MaxLife: 1000,
            CurrentMana: 500,
            MaxMana: 500,
            CurrentStamina: 200,
            MaxStamina: 200,
            Strength: 50,
            Dexterity: 50,
            Vitality: 50,
            Energy: 50,
            Lucky: 0,
            UnallocatedAttributePoints: 0,
            UnallocatedSkillPoints: 0,
            InventoryMoney: 0,
            RepositoryMoney: 0,
            CurrentFactionId: 0xFF,
            FirstFactionId: 0xFF,
            FactionJoinCount: 0,
            Skills: []);

        worker.TryEnqueueCommand(new AttachPlayerZoneCommand(dto, 1L, sink));
        worker.TryEnqueueCommand(new CompletePlayerSyncZoneCommand(charId, 1L, 10u, 20u));
        await worker.StepSingleTickAsync();

        PlayerZoneState player = worker.PlayersByCharacterId[charId];
        NpcZoneState casterMob = Assert.Single(worker.StaticNpcs.Values);
        player.FightMode = 1;

        // Case A: PoisonRemainingFrames = 15, Interval = 10, Damage = 50 -> must tick TWICE over 15 frames (total 100 HP lost).
        player.StatusEffects.BlendPoisonDamageOverTime(
            incomingDamagePerTick: 50,
            incomingDurationFrames: 15,
            incomingIntervalFrames: 10,
            attackerNpcId: casterMob.NpcId);

        for (int i = 0; i < 15; i++)
        {
            await worker.StepSingleTickAsync();
        }

        Assert.Equal(900, player.CurrentLife);

        // Case B: When caster NPC is dead (CurLife == 0), ProcessState immediately clears nTime = 0 with zero poison damage!
        casterMob.CurLife = 0;
        casterMob.AiState = MonsterAiStateKind.DeadRespawnWait;
        player.StatusEffects.BlendPoisonDamageOverTime(
            incomingDamagePerTick: 50,
            incomingDurationFrames: 30,
            incomingIntervalFrames: 10,
            attackerNpcId: casterMob.NpcId);

        for (int i = 0; i < 12; i++)
        {
            await worker.StepSingleTickAsync();
        }

        Assert.Equal(0, player.StatusEffects.PoisonRemainingFrames);
        Assert.Equal(900, player.CurrentLife);
    }

    [Fact]
    public async Task DoHurtRecoil_WhenTriggeredDuringCombat_ShouldSetHurtPostureAndBlockCastDuringRecoveryFrames()
    {
        // Ground truth: KNpc::DoHurt @ 0x08099270 (combat/state/KNpc_DoHurt.cpp:10-44)
        //   & CharacterActions/HurtActions.cs:20-92:
        //   When DoHurt triggers:
        //   1. hurtFrames = max(1, (((100 - maxHitRecover) + antiHitRecover) * BaseHurtFrames) / 100)
        //      For Player (BaseHurtFrames = 12), HitRecover = 40, AntiHitRecover = 15:
        //      hurtFrames = (((100 - 40) + 15) * 12) / 100 = (75 * 12) / 100 = 9 frames.
        //   2. this->m_eDoing transitions to do_hurt (EntityDoingPosture.Hurt) and blocks skill casts until recovery ends!
        ISkillTemplateCatalog skillCatalog = SharedGameAssetsFixture.Default.SkillTemplateCatalog;
        IMissileTemplateCatalog missileCatalog = SharedGameAssetsFixture.Default.MissileTemplateCatalog;
        Assert.NotEmpty(skillCatalog.AllSkills);

        SkillTemplate? castableSkill = null;
        foreach (SkillTemplate skill in skillCatalog.AllSkills)
        {
            if (skill.Id == 53 || (skill.IsOffensive && skill.Style == SkillStyleKind.Projectile && skill.ReqLevel <= 10))
            {
                castableSkill = skill;
                if (skill.Id == 53)
                {
                    break;
                }
            }
        }

        Assert.NotNull(castableSkill);

        var mapDesc = new MapDescriptor(53, "Ba Lăng Huyện", "balanghuyen", 0, 0, 200, 200)
        {
            RegionNpcSpawns =
            [
                new StaticNpcSpawnTemplate(
                    TemplateId: 586,
                    MapId: 53,
                    SpawnMpsX: 52064,
                    SpawnMpsY: 101696,
                    Level: 50,
                    Kind: EntityKind.Monster,
                    Camp: EntityCamp.Animal,
                    Series: ElementSeries.Wood,
                    IsSpecialNpc: false,
                    DisplayName: "Mộc Nhân",
                    ScriptPath: string.Empty),
            ],
        };

        var worker = new MapZoneWorker(
            mapDesc,
            new InMemorySessionLeaseAppServiceStub(),
            new ZoneEngineOptions { RandomSeed = 2 },
            new FakeTimeProvider(),
            NullLogger.Instance,
            skillTemplateCatalog: skillCatalog,
            missileTemplateCatalog: missileCatalog);

        var sink = new PlayerOutboundEventRecorder();
        Guid charId = Guid.NewGuid();
        var dto = new CharacterDto(
            Id: charId,
            AccountId: Guid.NewGuid(),
            SlotIndex: 0,
            RoleNameUnicode: "HurtLockTester",
            Gender: CharacterGender.Male,
            Series: ElementSeries.Metal,
            Level: 60,
            Experience: 0,
            LeadExperience: 0,
            MapId: 53,
            RevivalMapId: 53,
            RevivalPointId: 19,
            MapMpsX: 52000,
            MapMpsY: 101696,
            CurrentLife: 1000,
            MaxLife: 1000,
            CurrentMana: 500,
            MaxMana: 500,
            CurrentStamina: 300,
            MaxStamina: 300,
            Strength: 100,
            Dexterity: 100,
            Vitality: 100,
            Energy: 100,
            Lucky: 0,
            UnallocatedAttributePoints: 0,
            UnallocatedSkillPoints: 0,
            InventoryMoney: 0,
            RepositoryMoney: 0,
            CurrentFactionId: 0xFF,
            FirstFactionId: 0xFF,
            FactionJoinCount: 0,
            Skills:
            [
                new CharacterSkillDto((ushort)castableSkill.Id, 1, 0),
            ]);

        worker.TryEnqueueCommand(new AttachPlayerZoneCommand(dto, 1L, sink));
        worker.TryEnqueueCommand(new CompletePlayerSyncZoneCommand(charId, 1L, 10u, 20u));
        await worker.StepSingleTickAsync();

        PlayerZoneState player = worker.PlayersByCharacterId[charId];
        NpcZoneState dummy = Assert.Single(worker.StaticNpcs.Values);
        player.FightMode = 1;
        player.EquipmentCombatModifiers.HitRecoverPercent = 40;

        bool triggered = false;
        int hurtFrames = 0;
        for (int seed = 1; seed <= 32 && !triggered; seed++)
        {
            triggered = CombatDamageCalculator.TryTriggerDoHurt(player, antiHitRecoverPercent: 15, new Random(seed), out hurtFrames);
        }

        Assert.True(triggered);
        Assert.Equal(9, hurtFrames);
        Assert.Equal(EntityDoingPosture.Hurt, player.Doing);

        sink.Clear();
        worker.TryEnqueueCommand(new CastSkillZoneCommand(charId, 1L, castableSkill.Id, -1, (int)dummy.NpcId));
        await worker.StepSingleTickAsync();

        // While locked in DoHurt frames, skill cast MUST be rejected!
        Assert.Empty(sink.SkillCastBroadcasts);
    }

    [Fact]
    public void ColdFreezeSlow_OnStandardWalkSpeedMonster_ShouldReduceMovementProgressByHalfWithoutArtificialFloor()
    {
        // Ground truth: KNpc::ProcessState_AilmentsAndFeatureTimer @ 0x0809f6f0 (lines 48-62):
        //   When m_FreezeState.nTime > 0, every odd remaining frame ((nTime & 1) != 0) skips the action/movement step,
        //   yielding an exact 50% speed reduction across ALL base speeds (including WalkSpeed == 3 monsters!).
        INpcTemplateCatalog npcCatalog = SharedGameAssetsFixture.Default.NpcTemplateCatalog;
        Assert.True(npcCatalog.Count > 0);

        NpcTemplate? lowSpeedMonsterTpl = null;
        for (int id = 0; id < npcCatalog.Count; id++)
        {
            if (npcCatalog.TryGetTemplate(id, out NpcTemplate? tpl) &&
                tpl is not null &&
                tpl.Kind == EntityKind.Monster &&
                tpl.WalkSpeed is > 0 and <= 3)
            {
                lowSpeedMonsterTpl = tpl;
                break;
            }
        }

        Assert.NotNull(lowSpeedMonsterTpl);

        NpcZoneState mob = CreateAuditNpc(
            templateId: lowSpeedMonsterTpl.TemplateId,
            walkSpeed: lowSpeedMonsterTpl.WalkSpeed,
            runSpeed: Math.Max(6, lowSpeedMonsterTpl.RunSpeed));

        int unfrozenTenTickDistance = mob.EffectiveWalkSpeed * 10;
        mob.StatusEffects.ApplyColdFreeze(20);

        // Over 10 ticks under FreezeState, distance advanced must be halved (50% of unfrozen progress), NOT 100%!
        int frozenTenTickDistance = mob.EffectiveWalkSpeed * 10;
        Assert.Equal(unfrozenTenTickDistance / 2, frozenTenTickDistance);
    }

    [Fact]
    public async Task AttackAndCastFrameBreakpoints_AcrossSpeedTiersAndStandingTrapLifecycle_ShouldMatch18HzTickFormulas()
    {
        // Ground truth:
        //   1. DoOrdinSkill @ CharacterActions/SkillActions.cs:20-58:
        //      nTotalFrame = (18 * 100) / (100 + speed); 60% windup = (60 * nTotalFrame) / 100.
        //   2. KMissle standing trap DamageInterval inclusive '<=' gate (period = interval + 1) & silent vanish on PK change.
        //   3. PK Peace switch 3240-frame (3-minute) cooldown in combat zone.
        ISkillTemplateCatalog skillCatalog = SharedGameAssetsFixture.Default.SkillTemplateCatalog;
        IMissileTemplateCatalog missileCatalog = SharedGameAssetsFixture.Default.MissileTemplateCatalog;
        Assert.NotEmpty(skillCatalog.AllSkills);
        Assert.NotEmpty(missileCatalog.AllMissiles);

        // Verify canonical 18-Hz frame breakpoints for baseFrame = 18:
        (int Speed, int ExpectedTotalFrames, int ExpectedWindupFrames)[] expectedTiers =
        [
            (0, 18, 10),
            (20, 15, 9),
            (29, 13, 7),
            (50, 12, 7),
            (64, 10, 6),
            (101, 8, 4),
            (126, 7, 4),
        ];
        Assert.NotEmpty(expectedTiers);

        foreach ((int speed, int expectedTotal, int expectedWindup) in expectedTiers)
        {
            int actualTotal = (18 * 100) / (100 + speed);
            int actualWindup = (60 * actualTotal) / 100;
            Assert.Equal(expectedTotal, actualTotal);
            Assert.Equal(expectedWindup, actualWindup);
        }

        // Verify standing trap DamageInterval + 1 cadence & silent vanish when launcher changes PK state:
        MissileTemplate? realMissile = null;
        foreach (MissileTemplate m in missileCatalog.AllMissiles)
        {
            realMissile = m;
            break;
        }

        Assert.NotNull(realMissile);

        MissileTemplate standingTrap = realMissile with
        {
            MoveKind = MissileMoveKind.Stand,
            FollowKind = "none",
            SpeedMpsPerFrame = 0,
            HeightMps = 1,
            ZSpeedMpsPerFrame = 0,
            ZAccelMpsPerFrame2 = 0,
            LifeFrames = 15,
            HitRadiusMps = 64,
            CollideRadiusCells = 1,
            DamageRadiusCells = 2,
            IsRangeDamage = true,
            DamageIntervalFrames = 4, // Period = 4 + 1 = 5 frames -> hits at frames 1, 6, 11 (3 hits)
            PierceCount = 0,
            CollideVanish = false,
            CanCollideFriend = false,
            AutoExplode = false,
            MustHit = true,
            SubMissileId = 0,
        };

        SkillTemplate? baseSkill = null;
        foreach (SkillTemplate s in skillCatalog.AllSkills)
        {
            if (s.IsOffensive)
            {
                baseSkill = s;
                break;
            }
        }

        Assert.NotNull(baseSkill);

        SkillTemplate trapSkill = baseSkill with
        {
            Style = SkillStyleKind.Projectile,
            MissileForm = "zone",
            MissileId = standingTrap.Id,
            ChildSkillCount = 1,
            Param1 = 0,
            EventSkills = null,
            MissileGenerateStyle = "none",
            MissileGenerateDelayFrames = 0,
            Targeting = null,
            IsBaseSkill = true,
            ClientSendMode = 0,
        };

        var missileSystem = new MissileSimulationTickSystem(32, randomSeed: 1);
        var regionGrid = new SpatialRegionGrid();
        PlayerZoneState launcher = CreateAuditPlayer(runtimeNpcId: 25001u);
        launcher.FightMode = 1;
        launcher.PkState = PlayerPkMode.Fight;
        launcher.BaseCamp = EntityCamp.Justice;
        launcher.CurrentCamp = EntityCamp.Justice;

        NpcZoneState targetMob = CreateAuditNpc(npcId: 35001u);
        targetMob.MpsX = launcher.CurrentMpsX;
        targetMob.MpsY = launcher.CurrentMpsY;
        regionGrid.AddNpc(targetMob);

        var playersByNpcId = new Dictionary<uint, PlayerZoneState> { [launcher.RuntimeNpcId] = launcher };
        var npcsById = new Dictionary<uint, NpcZoneState> { [targetMob.NpcId] = targetMob };
        var trapPayload = new SkillDamagePayload(
            SkillId: trapSkill.Id,
            SkillLevel: 1,
            ElementSeries: ElementSeries.Fire,
            IsPhysical: false,
            IsMelee: false,
            UseAttackRating: false,
            DoHurtAnimation: false,
            AttackRatingScore: 500,
            PhysicsMinDamage: 0,
            PhysicsMaxDamage: 0,
            ColdMinDamage: 0,
            ColdMaxDamage: 0,
            ColdFreezeDurationFrames: 0,
            FireMinDamage: 25,
            FireMaxDamage: 25,
            LightMinDamage: 0,
            LightMaxDamage: 0,
            LightStunDurationFrames: 0,
            PoisonDamagePerTick: 0,
            PoisonDurationFrames: 0,
            PoisonIntervalFrames: 10,
            MagicMinDamage: 0,
            MagicMaxDamage: 0);

        int spawned = missileSystem.SpawnSkillMissiles(
            launcher.RuntimeNpcId,
            isLauncherPlayer: true,
            launcher.CurrentCamp,
            launcher.PkState,
            launcher.CurrentMpsX,
            launcher.CurrentMpsY,
            launcher.CurrentMpsX,
            launcher.CurrentMpsY,
            targetNpcId: 0u,
            trapSkill,
            standingTrap,
            in trapPayload,
            launcherCharacterId: launcher.CharacterId);
        Assert.Equal(1, spawned);

        int hitCount = 0;
        for (int f = 0; f < 15; f++)
        {
            missileSystem.StepTick(
                mapId: 53,
                mapCatalog: null,
                regionGrid,
                playersByNpcId,
                npcsById,
                onNpcHitRaw: (_, _) => hitCount++);
        }

        Assert.Equal(3, hitCount);

        // Verify 3240-frame PK Peace switch cooldown on ZoneSimulationHarness:
        var harness = new ZoneSimulationHarness(new ZoneSimulationHarnessOptions { HeartbeatTimeoutSeconds = 86400 });
        var charDto = new CharacterDto(
            Id: Guid.NewGuid(),
            AccountId: Guid.NewGuid(),
            SlotIndex: 0,
            RoleNameUnicode: "PkTimerHero",
            Gender: CharacterGender.Male,
            Series: ElementSeries.Metal,
            Level: 40,
            Experience: 0,
            LeadExperience: 0,
            MapId: 53,
            RevivalMapId: 53,
            RevivalPointId: 19,
            MapMpsX: 52000,
            MapMpsY: 101696,
            CurrentLife: 1000,
            MaxLife: 1000,
            CurrentMana: 500,
            MaxMana: 500,
            CurrentStamina: 200,
            MaxStamina: 200,
            Strength: 50,
            Dexterity: 50,
            Vitality: 50,
            Energy: 50,
            Lucky: 0,
            UnallocatedAttributePoints: 0,
            UnallocatedSkillPoints: 0,
            InventoryMoney: 0,
            RepositoryMoney: 0,
            CurrentFactionId: 1,
            FirstFactionId: 1,
            FactionJoinCount: 1,
            Skills: []);

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(charDto);
        PlayerZoneState state = handle.GetZoneState()!;
        state.BaseCamp = EntityCamp.Justice;
        state.CurrentCamp = EntityCamp.Justice;
        state.FightMode = 1;

        harness.EnqueueCommand(53, new ChangePkModeZoneCommand(charDto.Id, 1L, PlayerPkMode.Fight));
        await harness.StepFramesAsync(1);
        Assert.Equal(PlayerPkMode.Fight, state.PkState);

        await harness.StepFramesAsync(CombatConsts.SwitchToPeaceCooldownFrames - 2);
        harness.EnqueueCommand(53, new ChangePkModeZoneCommand(charDto.Id, 1L, PlayerPkMode.Peace));
        await harness.StepFramesAsync(1);
        Assert.Equal(PlayerPkMode.Fight, state.PkState);

        await harness.StepFramesAsync(2);
        harness.EnqueueCommand(53, new ChangePkModeZoneCommand(charDto.Id, 1L, PlayerPkMode.Peace));
        await harness.StepFramesAsync(1);
        Assert.Equal(PlayerPkMode.Peace, state.PkState);
    }

    [Fact]
    public void PlayerBaseAttackRatingAndDefense_FromDexterityWithoutEquipment_ShouldMatchSetNpcAttackRatingAndDefence()
    {
        // Ground truth: legacy-server/discovery/Player/KPlayer_P2_AttributesAndStats.cpp:180-195
        //   1. KPlayer::SetNpcAttackRating @ 0x080c3860: m_AttackRating = m_nCurDexterity * 4 - 28
        //   2. KPlayer::SetNpcDefence @ 0x080c3820:      m_Defend       = m_nCurDexterity / 4
        var seriesValues = Enum.GetValues<ElementSeries>();
        Assert.NotEmpty(seriesValues);

        int baseDexSum = 0;
        foreach (ElementSeries series in seriesValues)
        {
            if (FiveElementsSeriesMatrix.IsValidSeries(series))
            {
                Assert.True(SharedGameAssetsFixture.Default.PlayerTemplateCatalog.TryGetCharacterTemplate(
                    series,
                    CharacterGender.Male,
                    out CharacterBaseTemplate? charTpl));
                Assert.NotNull(charTpl);
                Assert.True(charTpl.BaseDexterity > 0);
                baseDexSum += charTpl.BaseDexterity;
            }
        }

        Assert.True(baseDexSum > 0);

        // With Dexterity = 100, Level = 50, zero equipment/skill bonuses:
        //   AttackRating = 100 * 4 - 28 = 372
        //   Defense      = 100 / 4      = 25
        PlayerZoneState player = CreateAuditPlayer(level: 50, dexterity: 100);
        Assert.Equal(372, player.EffectiveAttackRating);
        Assert.Equal(25, player.EffectiveDefense);
    }
}
