using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JXReborn.Contracts.Characters;
using JXReborn.GameAssets.Items;
using JXReborn.GameAssets.NpcTemplates;
using JXReborn.GameAssets.Skills;
using JXReborn.GameAssets.WorldMaps;
using JXReborn.Primitives.Characters;
using JXReborn.Primitives.Combat;
using JXReborn.Primitives.Items;
using JXReborn.Primitives.WorldZones;
using JXReborn.TestBase.Fixtures;
using JXReborn.TestBase.SimulationHarnesses;
using JXReborn.ZoneEngine.Combat;
using JXReborn.ZoneEngine.Items;
using JXReborn.ZoneEngine.NpcEntities;
using JXReborn.ZoneEngine.PlayerSessions;
using JXReborn.ZoneEngine.WorldZones;
using Xunit;

namespace JXReborn.ZoneEngine.Tests.Combat;

public sealed class CombatFormulaSimulationTests
{
    private static MapDescriptor CreateThreeMonsterMap() =>
        new(
            MapId: 53,
            MapNameUnicode: "Ba Lăng Huyện",
            RawSubPath: "balanghuyen",
            RegionBeginX: 0,
            RegionBeginY: 0,
            RegionWidth: 200,
            RegionHeight: 200)
        {
            RegionNpcSpawns =
            [
                new StaticNpcSpawnTemplate(
                    TemplateId: 1,
                    MapId: 53,
                    SpawnMpsX: 52096,
                    SpawnMpsY: 101696,
                    Level: 10,
                    Kind: EntityKind.Monster,
                    Camp: EntityCamp.Animal,
                    Series: ElementSeries.Wood,
                    IsSpecialNpc: false,
                    DisplayName: "Quái Gỗ 1",
                    ScriptPath: string.Empty),
                new StaticNpcSpawnTemplate(
                    TemplateId: 1,
                    MapId: 53,
                    SpawnMpsX: 52160,
                    SpawnMpsY: 101696,
                    Level: 10,
                    Kind: EntityKind.Monster,
                    Camp: EntityCamp.Animal,
                    Series: ElementSeries.Fire,
                    IsSpecialNpc: false,
                    DisplayName: "Quái Hỏa 2",
                    ScriptPath: string.Empty),
                new StaticNpcSpawnTemplate(
                    TemplateId: 1,
                    MapId: 53,
                    SpawnMpsX: 52224,
                    SpawnMpsY: 101696,
                    Level: 10,
                    Kind: EntityKind.Monster,
                    Camp: EntityCamp.Animal,
                    Series: ElementSeries.Metal,
                    IsSpecialNpc: false,
                    DisplayName: "Quái Kim 3",
                    ScriptPath: string.Empty),
            ],
        };

    private static ZoneSimulationHarness CreateHarness(
        int seed,
        MapDescriptor map,
        ISkillTemplateCatalog? skillCatalog = null)
    {
        SharedGameAssetsFixture assets = SharedGameAssetsFixture.Default;
        var services = new ZoneAssetServices
        {
            MapCatalog = assets.MapCatalog,
            NpcTemplateCatalog = assets.NpcTemplateCatalog,
            TrapScriptCatalog = assets.TrapScriptCatalog,
            SkillTemplateCatalog = skillCatalog ?? assets.SkillTemplateCatalog,
            MissileTemplateCatalog = assets.MissileTemplateCatalog,
            PlayerTemplateCatalog = assets.PlayerTemplateCatalog,
            ItemTemplateCatalog = assets.ItemTemplateCatalog,
            LootTableCatalog = assets.LootTableCatalog,
            ShopCatalog = assets.ShopCatalog,
            StallTaxCatalog = assets.StallTaxCatalog,
            ItemGenerator = assets.ItemGenerator,
        };

        var harness = new ZoneSimulationHarness(new ZoneSimulationHarnessOptions
        {
            RandomSeed = seed,
            Assets = services,
            InitialMaps = { map },
        });
        Assert.NotEmpty(harness.Workers);
        foreach (MapZoneWorker w in harness.Workers)
        {
            // Isolate damage-formula math from the 18-frame player wind-up (tested in SkillCastWindupTimingTests)
            w.ExecutionContext.Options.PlayerBaseAttackFrame = 0;
            w.ExecutionContext.Options.PlayerBaseCastFrame = 0;
        }

        return harness;
    }

    private static PlayerZoneState RequireState(SimulatedPlayerHandle handle)
    {
        PlayerZoneState? state = handle.GetZoneState();
        Assert.NotNull(state);
        return state;
    }

    private static CharacterDto CreateTestPlayerDto(
        string name,
        ElementSeries series,
        int mpsX,
        int mpsY,
        int life = 5000,
        int mana = 2000,
        ushort strength = 50,
        ushort dexterity = 50,
        ushort vitality = 50,
        ushort energy = 50,
        IReadOnlyList<CharacterSkillDto>? skills = null) =>
        new(
            Id: Guid.NewGuid(),
            AccountId: Guid.NewGuid(),
            SlotIndex: 0,
            RoleNameUnicode: name,
            Gender: CharacterGender.Male,
            Series: series,
            Level: 50,
            Experience: 0,
            LeadExperience: 0,
            MapId: 53,
            RevivalMapId: 53,
            RevivalPointId: 19,
            MapMpsX: mpsX,
            MapMpsY: mpsY,
            CurrentLife: life,
            MaxLife: (ushort)Math.Min(ushort.MaxValue, life),
            CurrentMana: mana,
            MaxMana: (ushort)Math.Min(ushort.MaxValue, mana),
            CurrentStamina: 500,
            MaxStamina: 500,
            Strength: strength,
            Dexterity: dexterity,
            Vitality: vitality,
            Energy: energy,
            Lucky: 0,
            UnallocatedAttributePoints: 0,
            UnallocatedSkillPoints: 0,
            InventoryMoney: 10000,
            RepositoryMoney: 0,
            CurrentFactionId: 255,
            FirstFactionId: 255,
            FactionJoinCount: 0,
            Skills: skills ?? [new CharacterSkillDto(53, 1, 0)]);

    private static void ConfigurePassiveMonsters(MapZoneWorker worker)
    {
        Assert.True(worker.StaticNpcs.Count >= 3);
        foreach (NpcZoneState npc in worker.StaticNpcs.Values)
        {
            npc.AiMode = 0;
            npc.Defense = 0;
            npc.CurLife = 5000;
            npc.PhysicsMinDamage = 0;
            npc.PhysicsMaxDamage = 0;
            npc.NextAttackReadyTick = long.MaxValue;
            npc.PhysicsResistPercent = 0;
            npc.ColdResistPercent = 0;
            npc.FireResistPercent = 0;
            npc.LightResistPercent = 0;
            npc.PoisonResistPercent = 0;
        }
    }

    private static PlayerItemSlot CreateAndEquipArmor(
        PlayerZoneState player,
        uint runtimeItemId,
        params ItemMagicAttributeSlot[] affixes)
    {
        var armorTpl = new ItemTemplate(
            Id: 9000 + (int)runtimeItemId,
            Code: $"test-armor-{runtimeItemId}",
            Name: $"Test Armor {runtimeItemId}",
            Genre: ItemGenreKind.Equipment,
            Quality: ItemQualityKind.Normal,
            DetailType: 2, // Body
            ParticularType: 1,
            Level: 1,
            ElementSeries: player.Series,
            GridWidthCells: 2,
            GridHeightCells: 3,
            ObjDataId: 1,
            AppearanceResId: 1,
            PriceGold: 100,
            MaxStackCount: 1,
            BaseAttributes: [],
            Requirements: []);

        var slot = new PlayerItemSlot(
            runtimeItemId: runtimeItemId,
            domainItemId: Guid.NewGuid(),
            template: armorTpl,
            genre: ItemGenreKind.Equipment,
            detailType: 2,
            particularType: 1,
            level: 1,
            series: player.Series,
            quality: ItemQualityKind.Normal,
            randomSeed: 1u,
            generatorVersion: ItemConsts.GeneratorVersion,
            luck: 0,
            currentDurability: 100,
            maxDurability: 100,
            stackCount: 1,
            containerPlace: ItemContainerPlace.EquipRoom,
            gridX: 0,
            gridY: 0,
            magicAttributes: affixes);

        Assert.True(player.Inventory.TryPlaceItemDirect(slot, ItemContainerPlace.EquipRoom, 0, 0, player));
        Assert.True(player.Inventory.TryMoveOrSwapItem(
            player,
            ItemContainerPlace.EquipRoom,
            0,
            0,
            ItemContainerPlace.Equip,
            (byte)EquipmentPartKind.Body,
            0));
        return slot;
    }

    private static PlayerItemSlot CreateAndEquipWeapon(
        PlayerZoneState player,
        uint runtimeItemId,
        short detailType,
        short particularType,
        int minDamage,
        int maxDamage,
        params ItemMagicAttributeSlot[] affixes)
    {
        var weaponTpl = new ItemTemplate(
            Id: 9100 + (int)runtimeItemId,
            Code: $"test-weapon-{runtimeItemId}",
            Name: $"Test Weapon {runtimeItemId}",
            Genre: ItemGenreKind.Equipment,
            Quality: ItemQualityKind.Normal,
            DetailType: detailType,
            ParticularType: particularType,
            Level: 1,
            ElementSeries: player.Series,
            GridWidthCells: 1,
            GridHeightCells: 2,
            ObjDataId: 1,
            AppearanceResId: 1,
            PriceGold: 100,
            MaxStackCount: 1,
            BaseAttributes:
            [
                new ItemBaseAttributeTemplate(1, "damage_v", minDamage, maxDamage),
            ],
            Requirements: []);

        var slot = new PlayerItemSlot(
            runtimeItemId: runtimeItemId,
            domainItemId: Guid.NewGuid(),
            template: weaponTpl,
            genre: ItemGenreKind.Equipment,
            detailType: detailType,
            particularType: particularType,
            level: 1,
            series: player.Series,
            quality: ItemQualityKind.Normal,
            randomSeed: 2u,
            generatorVersion: ItemConsts.GeneratorVersion,
            luck: 0,
            currentDurability: 100,
            maxDurability: 100,
            stackCount: 1,
            containerPlace: ItemContainerPlace.EquipRoom,
            gridX: 2,
            gridY: 0,
            magicAttributes: affixes);

        Assert.True(player.Inventory.TryPlaceItemDirect(slot, ItemContainerPlace.EquipRoom, 2, 0, player));
        Assert.True(player.Inventory.TryMoveOrSwapItem(
            player,
            ItemContainerPlace.EquipRoom,
            2,
            0,
            ItemContainerPlace.Equip,
            (byte)EquipmentPartKind.Weapon,
            0));
        return slot;
    }

    private static SkillTemplate CreateSyntheticSkill(
        int id,
        string code,
        ElementSeries series,
        bool isPhysical,
        bool isMelee,
        bool useAttackRating,
        bool doHurtAnimation,
        int missileId,
        params SkillEffectScalingTemplate[] effects) =>
        new(
            Id: id,
            Code: code,
            Name: code,
            Style: isMelee ? SkillStyleKind.Melee : SkillStyleKind.Projectile,
            ElementSeries: series,
            IsOffensive: true,
            IsPhysical: isPhysical,
            IsMelee: isMelee,
            IsAura: false,
            IsWeaponSkill: false,
            IsExpSkill: false,
            UseAttackRating: useAttackRating,
            DoHurtAnimation: doHurtAnimation,
            PeaceCanUse: false,
            ReqLevel: 1,
            MaxLevel: 20,
            EquipLimit: -2,
            HorseLimit: 0,
            CastRangeMps: 512,
            WaitFrames: 2,
            MissileId: missileId,
            MissileForm: "line",
            ChildSkillId: 0,
            ChildSkillLevel: 0,
            ChildSkillCount: 1,
            CostScaling: new SkillCostScalingTemplate("mana", 0, 0),
            EffectScaling: effects);

    [Fact]
    public async Task DamageReturnAndPkOrder_ShouldApplyReturnBeforePk20Percent_WithMeleeRangeAndPoisonReturn()
    {
        SkillTemplate meleeSkill = CreateSyntheticSkill(
            id: 5001,
            code: "test-melee-1000",
            series: ElementSeries.Metal,
            isPhysical: false,
            isMelee: true,
            useAttackRating: false,
            doHurtAnimation: true,
            missileId: 0,
            new SkillEffectScalingTemplate(
                59,
                "physicsdamage_v",
                "damage",
                new SkillParameterCurveTemplate("Constant", 1000),
                null,
                new SkillParameterCurveTemplate("Constant", 1000)));

        SkillTemplate rangedSkill = CreateSyntheticSkill(
            id: 5002,
            code: "test-ranged-1000",
            series: ElementSeries.Metal,
            isPhysical: false,
            isMelee: false,
            useAttackRating: false,
            doHurtAnimation: true,
            missileId: 3,
            new SkillEffectScalingTemplate(
                59,
                "physicsdamage_v",
                "damage",
                new SkillParameterCurveTemplate("Constant", 1000),
                null,
                new SkillParameterCurveTemplate("Constant", 1000)));

        SkillTemplate poisonSkill = CreateSyntheticSkill(
            id: 5003,
            code: "test-poison-1000",
            series: ElementSeries.Wood,
            isPhysical: false,
            isMelee: true,
            useAttackRating: false,
            doHurtAnimation: false,
            missileId: 0,
            new SkillEffectScalingTemplate(
                63,
                "poisondamage_v",
                "damage",
                new SkillParameterCurveTemplate("Constant", 500),
                new SkillParameterCurveTemplate("Constant", 20),
                new SkillParameterCurveTemplate("Constant", 5)));

        var catalog = new OverlaySkillTemplateCatalog(
            SharedGameAssetsFixture.Default.SkillTemplateCatalog,
            meleeSkill,
            rangedSkill,
            poisonSkill);

        MapDescriptor map = CreateThreeMonsterMap();
        ZoneSimulationHarness harness = CreateHarness(seed: 101, map, catalog);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle attacker = await harness.AttachSimulatedPlayerAsync(CreateTestPlayerDto(
            "Attacker",
            ElementSeries.Metal,
            52000,
            101696,
            life: 5000,
            skills:
            [
                new CharacterSkillDto(5001, 1, 0),
                new CharacterSkillDto(5002, 1, 0),
                new CharacterSkillDto(5003, 1, 0),
            ]));
        SimulatedPlayerHandle defender = await harness.AttachSimulatedPlayerAsync(CreateTestPlayerDto(
            "Defender",
            ElementSeries.Metal,
            52032,
            101696,
            life: 5000));

        PlayerZoneState attackerState = RequireState(attacker);
        PlayerZoneState defenderState = RequireState(defender);
        attackerState.FightMode = 1;
        attackerState.BaseCamp = EntityCamp.Justice;
        attackerState.CurrentCamp = EntityCamp.Justice;
        attackerState.PkState = PlayerPkMode.Slaughter;
        defenderState.FightMode = 1;
        defenderState.BaseCamp = EntityCamp.Evil;
        defenderState.CurrentCamp = EntityCamp.Evil;
        defenderState.PkState = PlayerPkMode.Fight;

        // Attacker has 50% returnres_p (205); Defender has 100% melee (118), 100% range (120), 100% poison return (194)
        CreateAndEquipArmor(attackerState, 1u, new ItemMagicAttributeSlot(0, 205, 50, 0, 0, 0));
        CreateAndEquipArmor(
            defenderState,
            2u,
            new ItemMagicAttributeSlot(0, 118, 100, 0, 0, 0),
            new ItemMagicAttributeSlot(2, 120, 100, 0, 0, 0),
            new ItemMagicAttributeSlot(4, 194, 100, 0, 0, 0));

        attacker.CastSkill(5001, -1, (int)defenderState.RuntimeNpcId);
        await harness.StepFramesAsync(4);

        // 0x5A skill cast emitted
        Assert.Contains(attacker.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 5001u);

        // Defender takes PK 20% of 1000 = 200.
        // Attacker takes return computed BEFORE PK 20%: 1000 * 100% = 1000 -> returnres 50% = 500 -> PK 20% = 100.
        Assert.Equal(4800, defenderState.CurrentLife);
        Assert.Equal(4900, attackerState.CurrentLife);

        // Ranged missile return check
        attacker.CastSkill(5002, defenderState.CurrentMpsX, defenderState.CurrentMpsY);
        await harness.StepFramesAsync(6);
        Assert.Contains(attacker.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 5002u);
        Assert.Equal(4600, defenderState.CurrentLife);
        Assert.Equal(4800, attackerState.CurrentLife);

        // Poison tick return check (WaitFrames=2, Cycle=5)
        int attackerLifeBeforePoison = attackerState.CurrentLife;
        attacker.CastSkill(5003, -1, (int)defenderState.RuntimeNpcId);
        await harness.StepFramesAsync(10);
        Assert.True(attackerState.CurrentLife < attackerLifeBeforePoison);
    }

    [Fact]
    public async Task LeechStealLifeAndMana_ShouldScaleStrictlyByActualPhysicalHpLost_AndSkipNonPhysicalSkills()
    {
        SkillTemplate physicalHybridSkill = CreateSyntheticSkill(
            id: 5010,
            code: "test-phys-hybrid-leech",
            series: ElementSeries.Metal,
            isPhysical: true,
            isMelee: true,
            useAttackRating: false,
            doHurtAnimation: false,
            missileId: 0,
            new SkillEffectScalingTemplate(59, "physicsdamage_v", "damage", new SkillParameterCurveTemplate("Constant", 500), null, new SkillParameterCurveTemplate("Constant", 500)),
            new SkillEffectScalingTemplate(61, "firedamage_v", "damage", new SkillParameterCurveTemplate("Constant", 500), null, new SkillParameterCurveTemplate("Constant", 500)),
            new SkillEffectScalingTemplate(66, "steallife_p", "damage", new SkillParameterCurveTemplate("Constant", 20)),
            new SkillEffectScalingTemplate(67, "stealmana_p", "damage", new SkillParameterCurveTemplate("Constant", 10)));

        SkillTemplate magicHybridSkill = CreateSyntheticSkill(
            id: 5011,
            code: "test-magic-hybrid-noleech",
            series: ElementSeries.Metal,
            isPhysical: false,
            isMelee: true,
            useAttackRating: false,
            doHurtAnimation: false,
            missileId: 0,
            new SkillEffectScalingTemplate(59, "physicsdamage_v", "damage", new SkillParameterCurveTemplate("Constant", 500), null, new SkillParameterCurveTemplate("Constant", 500)),
            new SkillEffectScalingTemplate(61, "firedamage_v", "damage", new SkillParameterCurveTemplate("Constant", 500), null, new SkillParameterCurveTemplate("Constant", 500)),
            new SkillEffectScalingTemplate(66, "steallife_p", "damage", new SkillParameterCurveTemplate("Constant", 50)),
            new SkillEffectScalingTemplate(67, "stealmana_p", "damage", new SkillParameterCurveTemplate("Constant", 50)));

        var catalog = new OverlaySkillTemplateCatalog(
            SharedGameAssetsFixture.Default.SkillTemplateCatalog,
            physicalHybridSkill,
            magicHybridSkill);

        MapDescriptor map = CreateThreeMonsterMap();
        ZoneSimulationHarness harness = CreateHarness(seed: 102, map, catalog);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle player = await harness.AttachSimulatedPlayerAsync(CreateTestPlayerDto(
            "Leecher",
            ElementSeries.Metal,
            52080,
            101696,
            life: 2000,
            mana: 1000,
            energy: 0,
            skills:
            [
                new CharacterSkillDto(5010, 1, 0),
                new CharacterSkillDto(5011, 1, 0),
            ]));

        PlayerZoneState playerState = RequireState(player);
        playerState.FightMode = 1;
        playerState.MaxLife = 2000;
        playerState.CurrentLife = 1000;
        playerState.MaxMana = 1000;
        playerState.CurrentMana = 500;

        // 1. Physical skill on Monster 1 (HP 5000): 500 phys + 500 fire -> physLost = 500 -> +100 life, +50 mana
        NpcZoneState m1 = worker.StaticNpcs[1u];
        player.CastSkill(5010, -1, (int)m1.NpcId);
        await harness.StepFramesAsync(4);
        Assert.Contains(player.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 5010u);
        Assert.Equal(4000u, m1.CurLife);
        Assert.Equal(1100, playerState.CurrentLife);
        Assert.Equal(550, playerState.CurrentMana);

        // 2. Non-physical skill (IsPhysical == false) on Monster 2 -> leech MUST NOT trigger
        NpcZoneState m2 = worker.StaticNpcs[2u];
        player.CastSkill(5011, -1, (int)m2.NpcId);
        await harness.StepFramesAsync(4);
        Assert.Equal(4000u, m2.CurLife);
        Assert.Equal(1100, playerState.CurrentLife);
        Assert.Equal(550, playerState.CurrentMana);

        // 3. Physical skill on Monster 3 with only 120 HP -> physLost = 120 -> +24 life, +12 mana
        NpcZoneState m3 = worker.StaticNpcs[3u];
        m3.CurLife = 120;
        player.CastSkill(5010, -1, (int)m3.NpcId);
        await harness.StepFramesAsync(4);
        Assert.Equal(0u, m3.CurLife);
        Assert.Equal(1124, playerState.CurrentLife);
        Assert.Equal(562, playerState.CurrentMana);
    }

    [Fact]
    public async Task ManaShield_ShouldAbsorbDamageWithMana_AndCollapseToFullHpDamageWhenManaInsufficient()
    {
        SkillTemplate flat100Skill = CreateSyntheticSkill(
            id: 5020,
            code: "test-flat-100",
            series: ElementSeries.Metal,
            isPhysical: false,
            isMelee: true,
            useAttackRating: false,
            doHurtAnimation: true,
            missileId: 0,
            new SkillEffectScalingTemplate(59, "physicsdamage_v", "damage", new SkillParameterCurveTemplate("Constant", 500), null, new SkillParameterCurveTemplate("Constant", 500)));

        var catalog = new OverlaySkillTemplateCatalog(
            SharedGameAssetsFixture.Default.SkillTemplateCatalog,
            flat100Skill);

        MapDescriptor map = CreateThreeMonsterMap();
        ZoneSimulationHarness harness = CreateHarness(seed: 103, map, catalog);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle attacker = await harness.AttachSimulatedPlayerAsync(CreateTestPlayerDto(
            "Attacker",
            ElementSeries.Metal,
            52000,
            101696,
            skills: [new CharacterSkillDto(5020, 1, 0)]));
        SimulatedPlayerHandle defender = await harness.AttachSimulatedPlayerAsync(CreateTestPlayerDto(
            "Shielded",
            ElementSeries.Metal,
            52032,
            101696,
            life: 2000,
            mana: 200));

        PlayerZoneState attackerState = RequireState(attacker);
        PlayerZoneState defenderState = RequireState(defender);
        attackerState.FightMode = 1;
        attackerState.BaseCamp = EntityCamp.Justice;
        attackerState.CurrentCamp = EntityCamp.Justice;
        attackerState.PkState = PlayerPkMode.Slaughter;
        defenderState.FightMode = 1;
        defenderState.BaseCamp = EntityCamp.Evil;
        defenderState.CurrentCamp = EntityCamp.Evil;

        // 50% ManaShield (149) -> 500 raw * 20% PK = 100 post-PK dmg -> manaDmg = 50
        CreateAndEquipArmor(defenderState, 1u, new ItemMagicAttributeSlot(0, 149, 50, 0, 0, 0));
        defenderState.CurrentMana = 60;
        defenderState.CurrentLife = 1000;

        // Hit 1: CurrentMana (60) >= manaDmg (50) -> Mana=10, Life=950
        attacker.CastSkill(5020, -1, (int)defenderState.RuntimeNpcId);
        await harness.StepFramesAsync(4);
        Assert.Contains(attacker.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 5020u);
        Assert.Equal(10, defenderState.CurrentMana);
        Assert.Equal(950, defenderState.CurrentLife);

        // Hit 2: CurrentMana (10) < manaDmg (50) -> Mana collapses to 0 and defender takes FULL 100 dmg to HP (950 -> 850)!
        attacker.CastSkill(5020, -1, (int)defenderState.RuntimeNpcId);
        await harness.StepFramesAsync(4);
        Assert.Equal(0, defenderState.CurrentMana);
        Assert.Equal(850, defenderState.CurrentLife);
    }

    [Fact]
    public async Task SkillDamageAssembly_ShouldApplyAddSkillDamageWeaponEnhanceSplitDexScalingMagicBonusEnergyAndMixPoison()
    {
        // Target skill 5030 receives synergy from 5031 (addskilldamage1)
        SkillTemplate synergySourceSkill = new(
            Id: 5031,
            Code: "test-synergy-source",
            Name: "Synergy Source",
            Style: SkillStyleKind.Melee,
            ElementSeries: ElementSeries.Metal,
            IsOffensive: false,
            IsPhysical: true,
            IsMelee: true,
            IsAura: false,
            IsWeaponSkill: false,
            IsExpSkill: false,
            UseAttackRating: false,
            DoHurtAnimation: false,
            PeaceCanUse: true,
            ReqLevel: 1,
            MaxLevel: 20,
            EquipLimit: -2,
            HorseLimit: 0,
            CastRangeMps: 96,
            WaitFrames: 2,
            MissileId: 0,
            MissileForm: "line",
            ChildSkillId: 0,
            ChildSkillLevel: 0,
            ChildSkillCount: 1,
            CostScaling: new SkillCostScalingTemplate("mana", 0, 0),
            EffectScaling:
            [
                new SkillEffectScalingTemplate(
                    260,
                    "addskilldamage1",
                    "special",
                    new SkillParameterCurveTemplate("Constant", 5030),
                    null,
                    new SkillParameterCurveTemplate("Constant", 50)),
            ]);

        SkillTemplate physEnhanceSkill = CreateSyntheticSkill(
            id: 5030,
            code: "test-phys-enhance",
            series: ElementSeries.Metal,
            isPhysical: true,
            isMelee: true,
            useAttackRating: false,
            doHurtAnimation: false,
            missileId: 0,
            new SkillEffectScalingTemplate(65, "physicsenhance_p", "damage", new SkillParameterCurveTemplate("Constant", 100)));

        SkillTemplate coldMagicSkill = CreateSyntheticSkill(
            id: 5032,
            code: "test-cold-magic",
            series: ElementSeries.Water,
            isPhysical: false,
            isMelee: true,
            useAttackRating: false,
            doHurtAnimation: false,
            missileId: 0,
            new SkillEffectScalingTemplate(
                60,
                "colddamage_v",
                "damage",
                new SkillParameterCurveTemplate("Constant", 100),
                new SkillParameterCurveTemplate("Constant", 20),
                new SkillParameterCurveTemplate("Constant", 100)));

        SkillTemplate poisonMagicSkill = CreateSyntheticSkill(
            id: 5033,
            code: "test-poison-magic",
            series: ElementSeries.Wood,
            isPhysical: false,
            isMelee: true,
            useAttackRating: false,
            doHurtAnimation: false,
            missileId: 0,
            new SkillEffectScalingTemplate(
                63,
                "poisondamage_v",
                "damage",
                new SkillParameterCurveTemplate("Constant", 20),
                new SkillParameterCurveTemplate("Constant", 60),
                new SkillParameterCurveTemplate("Constant", 10)));

        var catalog = new OverlaySkillTemplateCatalog(
            SharedGameAssetsFixture.Default.SkillTemplateCatalog,
            synergySourceSkill,
            physEnhanceSkill,
            coldMagicSkill,
            poisonMagicSkill);

        MapDescriptor map = CreateThreeMonsterMap();
        ZoneSimulationHarness harness = CreateHarness(seed: 104, map, catalog);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle player = await harness.AttachSimulatedPlayerAsync(CreateTestPlayerDto(
            "FormulaHero",
            ElementSeries.Metal,
            52080,
            101696,
            strength: 10,
            dexterity: 100,
            energy: 80,
            skills:
            [
                new CharacterSkillDto(5030, 1, 0),
                new CharacterSkillDto(5031, 1, 0),
                new CharacterSkillDto(5032, 1, 0),
                new CharacterSkillDto(5033, 1, 0),
            ]));

        PlayerZoneState state = RequireState(player);
        state.FightMode = 1;

        // Ranged weapon (DetailType = 1 => WeaponType = 1 => Dex/5 = 20 instead of Str/5 = 2!)
        // Weapon damage = 100..100 => Base = 100 + 20 = 120.
        // Weapon has poisonenhance_p (164) = 20% in explicit slot 0.
        // Armor has skill_enhance (243) = 50% (total addDmgP = 50% from 5031 + 50% from 243 = 100%),
        // addphysicsdamage_p (126) = 50% (WeaponEnhanceP multiplies Base=120, NOT D1*AddDmgP!),
        // addcoldmagic_v (169) = 40.
        CreateAndEquipWeapon(
            state,
            1u,
            detailType: 1,
            particularType: 0,
            minDamage: 100,
            maxDamage: 100,
            new ItemMagicAttributeSlot(0, 164, 20, 0, 0, 0));
        CreateAndEquipArmor(
            state,
            2u,
            new ItemMagicAttributeSlot(0, 243, 50, 0, 0, 0),
            new ItemMagicAttributeSlot(2, 126, 50, 0, 6, 0),
            new ItemMagicAttributeSlot(4, 169, 40, 0, 0, 0));

        // 1. Verify Physics payload split & Dex scaling:
        // Base = 120; D1 = 120 * (100+100)/100 = 240; D2 = 240 + 240*100/100 = 480; Final = 480 + 120*50/100 = 540
        SkillDamagePayload physPayload = CombatDamageCalculator.BuildPlayerSkillDamagePayload(state, physEnhanceSkill, 1);
        Assert.Equal(540, physPayload.PhysicsMinDamage);
        Assert.Equal(540, physPayload.PhysicsMaxDamage);

        // Also cast 5030 in simulation against Monster 1 (Wood) and assert 540 dmg + 0x5A
        NpcZoneState m1 = worker.StaticNpcs[1u];
        player.CastSkill(5030, -1, (int)m1.NpcId);
        await harness.StepFramesAsync(4);
        Assert.Contains(player.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 5030u);
        Assert.Equal(5000u - 540u, m1.CurLife);

        // 2. Cold magic skill: base=100, addDmgP=50% (skill_enhance 243) -> 150, + addcoldmagic_v(40) + CurEnergy(80) = 270
        SkillDamagePayload coldPayload = CombatDamageCalculator.BuildPlayerSkillDamagePayload(state, coldMagicSkill, 1);
        Assert.Equal(270, coldPayload.ColdMinDamage);
        Assert.Equal(270, coldPayload.ColdMaxDamage);

        // 3. Poison magic skill: base(20, 60, 10) -> addDmgP=50% -> dmg=30, poisonEnhance=20% -> cycle=8
        // Mixed via MixPoisonDamage with magicPoison(dmg=CurEnergy=80, dur=60, cycle=10):
        // sumC = 18; d = ((18*80)/10 + (18*30)/8)/2 = (144 + 67)/2 = 105; t = (60*80*8 + 60*30*10)/(8*80 + 10*30) = 60; c = 18/2 = 9
        SkillDamagePayload poisonPayload = CombatDamageCalculator.BuildPlayerSkillDamagePayload(state, poisonMagicSkill, 1);
        Assert.Equal(105, poisonPayload.PoisonDamagePerTick);
        Assert.Equal(60, poisonPayload.PoisonDurationFrames);
        Assert.Equal(9, poisonPayload.PoisonIntervalFrames);
    }

    [Fact]
    public async Task CombatModifiers_ShouldEnforceBlockRateEnhanceHitDeadlyFatallyIgnoreDefSeriesAndCalcCurRes()
    {
        SkillTemplate comboSkill = CreateSyntheticSkill(
            id: 5040,
            code: "test-combo-modifiers",
            series: ElementSeries.Metal,
            isPhysical: true,
            isMelee: true,
            useAttackRating: true,
            doHurtAnimation: false,
            missileId: 0,
            new SkillEffectScalingTemplate(59, "physicsdamage_v", "damage", new SkillParameterCurveTemplate("Constant", 100), null, new SkillParameterCurveTemplate("Constant", 100)),
            new SkillEffectScalingTemplate(61, "firedamage_v", "damage", new SkillParameterCurveTemplate("Constant", 100), null, new SkillParameterCurveTemplate("Constant", 100)),
            new SkillEffectScalingTemplate(70, "deadlystrike_p", "damage", new SkillParameterCurveTemplate("Constant", 100)),
            new SkillEffectScalingTemplate(58, "ignoredefense_p", "damage", new SkillParameterCurveTemplate("Constant", 100)),
            new SkillEffectScalingTemplate(75, "seriesdamage_p", "damage", new SkillParameterCurveTemplate("Constant", 20)));

        SkillTemplate fatalAndStunSkill = CreateSyntheticSkill(
            id: 5041,
            code: "test-fatal-stun",
            series: ElementSeries.Metal,
            isPhysical: true,
            isMelee: true,
            useAttackRating: false,
            doHurtAnimation: false,
            missileId: 0,
            new SkillEffectScalingTemplate(59, "physicsdamage_v", "damage", new SkillParameterCurveTemplate("Constant", 100), null, new SkillParameterCurveTemplate("Constant", 100)),
            new SkillEffectScalingTemplate(71, "fatallystrike_p", "damage", new SkillParameterCurveTemplate("Constant", 100)),
            new SkillEffectScalingTemplate(72, "stun_p", "damage", new SkillParameterCurveTemplate("Constant", 100), new SkillParameterCurveTemplate("Constant", 10)));

        var catalog = new OverlaySkillTemplateCatalog(
            SharedGameAssetsFixture.Default.SkillTemplateCatalog,
            comboSkill,
            fatalAndStunSkill);

        MapDescriptor map = CreateThreeMonsterMap();
        ZoneSimulationHarness harness = CreateHarness(seed: 105, map, catalog);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle attacker = await harness.AttachSimulatedPlayerAsync(CreateTestPlayerDto(
            "ModifierHero",
            ElementSeries.Metal,
            52080,
            101696,
            skills:
            [
                new CharacterSkillDto(1, 1, 0), // Neutral skill (elementSeries = -1 in JSON)
                new CharacterSkillDto(5040, 1, 0),
                new CharacterSkillDto(5041, 1, 0),
            ]));
        SimulatedPlayerHandle blocker = await harness.AttachSimulatedPlayerAsync(CreateTestPlayerDto(
            "Blocker",
            ElementSeries.Wood,
            52112,
            101696,
            life: 2000));

        PlayerZoneState attackerState = RequireState(attacker);
        PlayerZoneState blockerState = RequireState(blocker);
        attackerState.FightMode = 1;
        attackerState.BaseCamp = EntityCamp.Justice;
        attackerState.CurrentCamp = EntityCamp.Justice;
        attackerState.PkState = PlayerPkMode.Slaughter;
        blockerState.FightMode = 1;
        blockerState.BaseCamp = EntityCamp.Evil;
        blockerState.CurrentCamp = EntityCamp.Evil;

        // A. BlockRate (226 = 100%): hit negated + 0x87 (SkillId 963, RemainTimeFrames 1) + 0x7A (StateSpecialId 159)
        CreateAndEquipArmor(blockerState, 1u, new ItemMagicAttributeSlot(0, 226, 100, 0, 0, 0));
        attacker.CastSkill(5040, -1, (int)blockerState.RuntimeNpcId);
        await harness.StepFramesAsync(4);
        Assert.Equal(2000, blockerState.CurrentLife);
        Assert.Contains(blocker.Recorder.SyncStateEffects, s => s.SkillId == 963 && s.Level == 1 && s.RemainTimeFrames == 1);
        Assert.Contains(blocker.Recorder.NpcStateInfoBroadcasts, s => s.NpcId == blockerState.RuntimeNpcId && s.StateSpecialIds[0] == 159);

        // B. DeadlyStrike (phys-only 2x = 200) + Fire (100) + IgnoreDefense (100% vs Defense 999999)
        // jx_linux_y (0x0809e790 + 0x080938a0): unconditional Math.Max(resYin, rawResYan) floors 0-Yan Wood monster at 0% (300 dmg, NOT 360).
        // When defender has 40% resist (40 - 20 = 20%), Metal seriesdamage_p=20 reduces resist to 20% (80% of 300 = 240 dmg).
        NpcZoneState m1 = worker.StaticNpcs[1u]; // Wood
        m1.Defense = 999999; // IgnoreDefense 100% makes effective defense 0 -> 95% hit
        m1.CurLife = 5000;
        attackerState.BonusAttackRating = 10000;
        attacker.CastSkill(5040, -1, (int)m1.NpcId);
        await harness.StepFramesAsync(4);
        Assert.Equal(5000u - 300u, m1.CurLife);

        m1.CurLife = 5000;
        m1.PhysicsResistPercent = 40;
        m1.FireResistPercent = 40;
        attacker.CastSkill(5040, -1, (int)m1.NpcId);
        await harness.StepFramesAsync(4);
        Assert.Equal(5000u - 240u, m1.CurLife);

        // With rawResYan = -25, seriesdamage_p = 20 reduces 0-resYin to -20% (200*1.2 + 100*1.2 = 360 dmg)
        m1.CurLife = 5000;
        m1.PhysicsResistPercent = 0;
        m1.FireResistPercent = 0;
        m1.CombatModifiers.PhysicsResistYanPercent = -25;
        m1.CombatModifiers.FireResistYanPercent = -25;
        attacker.CastSkill(5040, -1, (int)m1.NpcId);
        await harness.StepFramesAsync(4);
        Assert.Equal(5000u - 360u, m1.CurLife);

        // C. CalcCurRes diminishing returns + 95% cap + negative resist amplification (when Yan < 0) via live simulation on m3 (Metal)
        SkillTemplate flat1000PhysSkill = CreateSyntheticSkill(
            id: 5042,
            code: "test-calc-cur-res",
            series: ElementSeries.Metal,
            isPhysical: false,
            isMelee: true,
            useAttackRating: false,
            doHurtAnimation: false,
            missileId: 0,
            new SkillEffectScalingTemplate(59, "physicsdamage_v", "damage", new SkillParameterCurveTemplate("Constant", 1000), null, new SkillParameterCurveTemplate("Constant", 1000)));
        var resCatalog = new OverlaySkillTemplateCatalog(
            SharedGameAssetsFixture.Default.SkillTemplateCatalog,
            flat1000PhysSkill);
        MapDescriptor resMap = CreateThreeMonsterMap();
        ZoneSimulationHarness resHarness = CreateHarness(seed: 109, resMap, resCatalog);
        MapZoneWorker resWorker = resHarness.GetZone(53);
        ConfigurePassiveMonsters(resWorker);
        SimulatedPlayerHandle resAttacker = await resHarness.AttachSimulatedPlayerAsync(CreateTestPlayerDto(
            "ResTester",
            ElementSeries.Metal,
            52160,
            101696,
            skills: [new CharacterSkillDto(5042, 1, 0), new CharacterSkillDto(5041, 1, 0)]));
        RequireState(resAttacker).FightMode = 1;

        NpcZoneState rm1 = resWorker.StaticNpcs[1u];
        rm1.PhysicsResistPercent = 155; // CalcCurRes(155, 75) = 75 + (80 * 20) / 400 = 79% -> 1000 * 21 / 100 = 210
        rm1.CurLife = 5000;
        resAttacker.CastSkill(5042, -1, (int)rm1.NpcId);
        await resHarness.StepFramesAsync(4);
        Assert.Equal(5000u - 210u, rm1.CurLife);

        NpcZoneState rm2 = resWorker.StaticNpcs[2u];
        rm2.PhysicsResistPercent = 2000; // Hard cap 95% -> 1000 * 5 / 100 = 50
        rm2.CurLife = 5000;
        resAttacker.CastSkill(5042, -1, (int)rm2.NpcId);
        await resHarness.StepFramesAsync(4);
        Assert.Equal(5000u - 50u, rm2.CurLife);

        NpcZoneState rm3 = resWorker.StaticNpcs[3u];
        rm3.PhysicsResistPercent = -25;
        rm3.CombatModifiers.PhysicsResistYanPercent = -25; // Negative resist amplification when Yan < 0 -> 1000 * 125 / 100 = 1250
        rm3.CurLife = 5000;
        resAttacker.CastSkill(5042, -1, (int)rm3.NpcId);
        await resHarness.StepFramesAsync(4);
        Assert.Equal(5000u - 1250u, rm3.CurLife);

        // D. EnhanceHitRate (227 = 100%) + FatallyStrike (25% current HP) + Stun (2x duration = 20 frames)
        CreateAndEquipArmor(attackerState, 2u, new ItemMagicAttributeSlot(0, 227, 100, 0, 0, 0));
        NpcZoneState m2 = worker.StaticNpcs[2u]; // Fire, 0 res
        m2.CurLife = 1000;
        // EnhanceHitRate doubles 100 phys -> 200 -> m2 life becomes 800; FatallyStrike deals 25% of 800 = 200 -> m2 life becomes 600; Stun = 2 * 10 = 20 frames (minus elapsed frames)
        attacker.CastSkill(5041, -1, (int)m2.NpcId);
        await harness.StepFramesAsync(3);
        Assert.Equal(600u, m2.CurLife);
        Assert.True(m2.StatusEffects.StunRemainingFrames > 10);
    }

    [Fact]
    public async Task DoHurt_ShouldComputeExactHurtFramesAndBroadcast0x56_ForPlayersAndNpcs()
    {
        SkillTemplate hurtSkill = CreateSyntheticSkill(
            id: 5050,
            code: "test-dohurt-100",
            series: ElementSeries.Metal,
            isPhysical: false,
            isMelee: true,
            useAttackRating: false,
            doHurtAnimation: true,
            missileId: 0,
            new SkillEffectScalingTemplate(59, "physicsdamage_v", "damage", new SkillParameterCurveTemplate("Constant", 100), null, new SkillParameterCurveTemplate("Constant", 100)),
            new SkillEffectScalingTemplate(271, "skill_dohurt", "damage", new SkillParameterCurveTemplate("Constant", 100)));

        var catalog = new OverlaySkillTemplateCatalog(
            SharedGameAssetsFixture.Default.SkillTemplateCatalog,
            hurtSkill);

        MapDescriptor map = CreateThreeMonsterMap();
        ZoneSimulationHarness harness = CreateHarness(seed: 106, map, catalog);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle attacker = await harness.AttachSimulatedPlayerAsync(CreateTestPlayerDto(
            "Hurter",
            ElementSeries.Metal,
            52000,
            101696,
            skills: [new CharacterSkillDto(5050, 1, 0)]));
        SimulatedPlayerHandle target = await harness.AttachSimulatedPlayerAsync(CreateTestPlayerDto(
            "TargetPlayer",
            ElementSeries.Metal,
            52032,
            101696,
            life: 2000));

        PlayerZoneState attackerState = RequireState(attacker);
        PlayerZoneState targetState = RequireState(target);
        attackerState.FightMode = 1;
        attackerState.BaseCamp = EntityCamp.Justice;
        attackerState.CurrentCamp = EntityCamp.Justice;
        attackerState.PkState = PlayerPkMode.Slaughter;
        targetState.FightMode = 1;
        targetState.BaseCamp = EntityCamp.Evil;
        targetState.CurrentCamp = EntityCamp.Evil;

        // Target has fasthitrecover_v (113) = 25 => HurtFrames = max(1, ((100 - 25 + 0) * 12) / 100) = 9
        CreateAndEquipArmor(targetState, 1u, new ItemMagicAttributeSlot(0, 113, 25, 0, 0, 0));

        // Cast repeatedly until the 50% coin-flip triggers DoHurt (deterministic seed 106)
        for (int i = 0; i < 8 && target.Recorder.HurtBroadcasts.Count == 0; i++)
        {
            targetState.StatusEffects.HurtRecoveryRemainingFrames = 0;
            targetState.Doing = EntityDoingPosture.Stand;
            attacker.CastSkill(5050, -1, (int)targetState.RuntimeNpcId);
            await harness.StepFramesAsync(3);
        }

        Assert.Contains(target.Recorder.HurtBroadcasts, h => h.NpcId == targetState.RuntimeNpcId && h.HurtFrames == 9);

        // NPC target DoHurt (0x56): BaseHurtFrames = 10 when HitRecoverFrames == 0 and HitRecoverPercent == 0 => HurtFrames = 10
        NpcZoneState npcTarget = worker.StaticNpcs[1u];
        npcTarget.CurLife = 5000;
        npcTarget.HitRecoverFrames = 0;
        attacker.Recorder.Clear();
        for (int i = 0; i < 8 && attacker.Recorder.HurtBroadcasts.Count == 0; i++)
        {
            npcTarget.StatusEffects.HurtRecoveryRemainingFrames = 0;
            npcTarget.Doing = EntityDoingPosture.Stand;
            attacker.CastSkill(5050, -1, (int)npcTarget.NpcId);
            await harness.StepFramesAsync(3);
        }

        Assert.Contains(attacker.Recorder.HurtBroadcasts, h => h.NpcId == npcTarget.NpcId && h.HurtFrames == 10);

        // NPC target with template HitRecoverFrames = 50 and HitRecoverPercent = 20 => HurtFrames = 40
        npcTarget.HitRecoverFrames = 50;
        npcTarget.CombatModifiers.HitRecoverPercent = 20;
        attacker.Recorder.Clear();
        for (int i = 0; i < 8 && attacker.Recorder.HurtBroadcasts.Count == 0; i++)
        {
            npcTarget.StatusEffects.HurtRecoveryRemainingFrames = 0;
            npcTarget.Doing = EntityDoingPosture.Stand;
            attacker.CastSkill(5050, -1, (int)npcTarget.NpcId);
            await harness.StepFramesAsync(3);
        }

        Assert.Contains(attacker.Recorder.HurtBroadcasts, h => h.NpcId == npcTarget.NpcId && h.HurtFrames == 40);
    }

    [Fact]
    [Trait("Category", "LegacyData")]
    public async Task AutoCastSkills_ShouldTriggerAutoReplyAutoAttackAutoRescueAndAutoDeath_With0x85ExtraFlag()
    {
        SkillTemplate hitSkill = CreateSyntheticSkill(
            id: 5060,
            code: "test-autocast-hit",
            series: ElementSeries.Metal,
            isPhysical: false,
            isMelee: true,
            useAttackRating: false,
            doHurtAnimation: false,
            missileId: 0,
            new SkillEffectScalingTemplate(59, "physicsdamage_v", "damage", new SkillParameterCurveTemplate("Constant", 5000), null, new SkillParameterCurveTemplate("Constant", 5000)));

        SkillTemplate procSkill = CreateSyntheticSkill(
            id: 5061,
            code: "test-autocast-proc",
            series: ElementSeries.Metal,
            isPhysical: false,
            isMelee: true,
            useAttackRating: false,
            doHurtAnimation: false,
            missileId: 0,
            new SkillEffectScalingTemplate(59, "physicsdamage_v", "damage", new SkillParameterCurveTemplate("Constant", 50), null, new SkillParameterCurveTemplate("Constant", 50)));

        SkillTemplate excludedSkill = CreateSyntheticSkill(
            id: 389,
            code: "test-excluded-389",
            series: ElementSeries.Metal,
            isPhysical: false,
            isMelee: true,
            useAttackRating: false,
            doHurtAnimation: false,
            missileId: 0,
            new SkillEffectScalingTemplate(59, "physicsdamage_v", "damage", new SkillParameterCurveTemplate("Constant", 50), null, new SkillParameterCurveTemplate("Constant", 50)));

        var catalog = new OverlaySkillTemplateCatalog(
            SharedGameAssetsFixture.Default.SkillTemplateCatalog,
            hitSkill,
            procSkill,
            excludedSkill);

        MapDescriptor map = CreateThreeMonsterMap();
        ZoneSimulationHarness harness = CreateHarness(seed: 107, map, catalog);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle attacker = await harness.AttachSimulatedPlayerAsync(CreateTestPlayerDto(
            "AutoAttacker",
            ElementSeries.Metal,
            52000,
            101696,
            life: 2000,
            skills:
            [
                new CharacterSkillDto(5060, 1, 0),
                new CharacterSkillDto(389, 1, 0),
                new CharacterSkillDto(5061, 1, 0),
            ]));
        SimulatedPlayerHandle defender = await harness.AttachSimulatedPlayerAsync(CreateTestPlayerDto(
            "AutoDefender",
            ElementSeries.Metal,
            52032,
            101696,
            life: 1200));

        PlayerZoneState attackerState = RequireState(attacker);
        PlayerZoneState defenderState = RequireState(defender);
        attackerState.FightMode = 1;
        attackerState.BaseCamp = EntityCamp.Justice;
        attackerState.CurrentCamp = EntityCamp.Justice;
        attackerState.PkState = PlayerPkMode.Slaughter;
        defenderState.FightMode = 1;
        defenderState.BaseCamp = EntityCamp.Evil;
        defenderState.CurrentCamp = EntityCamp.Evil;

        int packedProc = (5061 << 8) | 1;
        // Attacker: AutoAttack (196) 100%
        CreateAndEquipArmor(attackerState, 1u, new ItemMagicAttributeSlot(0, 196, packedProc, 0, 100, 0));
        // Defender: AutoReply (195) 100%, AutoRescue (197, p1=1 -> bSyncCD=1 => 0x85 ExtraFlag=1) 100%, AutoDeath (198) 100%
        CreateAndEquipArmor(
            defenderState,
            2u,
            new ItemMagicAttributeSlot(0, 195, packedProc, 0, 100, 0),
            new ItemMagicAttributeSlot(2, 197, packedProc, 1, 100, 0),
            new ItemMagicAttributeSlot(4, 198, packedProc, 0, 100, 0));

        // 1. Cast AutoAttackExcludedSkillIds[0] = 389 -> AutoAttack MUST NOT fire, AutoReply fires
        attacker.CastSkill(389, -1, (int)defenderState.RuntimeNpcId);
        await harness.StepFramesAsync(4);
        Assert.DoesNotContain(attacker.Recorder.SkillCastBroadcasts, s => s.CastDirectly && s.CasterNpcId == attackerState.RuntimeNpcId && s.SkillId == 5061u);
        Assert.Contains(attacker.Recorder.SkillCastBroadcasts, s => s.CastDirectly && s.CasterNpcId == defenderState.RuntimeNpcId && s.SkillId == 5061u && s.ExtraFlag == 0);

        attacker.Recorder.Clear();
        defender.Recorder.Clear();

        // 2. Cast 5060 (5000 raw * 20% PK = 1000 dmg): defender HP 1200 -> 200 (< 1200/4=300) => triggers AutoRescue (0x85 ExtraFlag = 1) + AutoAttack (0x85 ExtraFlag = 0)
        attacker.CastSkill(5060, -1, (int)defenderState.RuntimeNpcId);
        await harness.StepFramesAsync(4);
        Assert.Contains(defender.Recorder.SkillCastBroadcasts, s => s.CastDirectly && s.CasterNpcId == defenderState.RuntimeNpcId && s.SkillId == 5061u && s.ExtraFlag == 1);
        Assert.Contains(attacker.Recorder.SkillCastBroadcasts, s => s.CastDirectly && s.CasterNpcId == attackerState.RuntimeNpcId && s.SkillId == 5061u && s.ExtraFlag == 0);

        defender.Recorder.Clear();
        attackerState.CurrentLife = 2000;
        // 3. Fatal hit kills defender -> triggers AutoDeath (0x85) exactly once (no infinite recursion!)
        attacker.CastSkill(5060, -1, (int)defenderState.RuntimeNpcId);
        await harness.StepFramesAsync(4);
        Assert.Equal(0, defenderState.CurrentLife);
        Assert.Equal(1990, attackerState.CurrentLife);
        Assert.Equal(1, defender.Recorder.SkillCastBroadcasts.Count(s => s.CastDirectly && s.CasterNpcId == defenderState.RuntimeNpcId && s.SkillId == 5061u));
    }

    [Fact]
    [Trait("Category", "LegacyData")]
    public async Task M1ForwardedFixes_ShouldCorrectIgnoreDefenseAllResAndMask233Collision()
    {
        MapDescriptor map = CreateThreeMonsterMap();
        ZoneSimulationHarness harness = CreateHarness(seed: 108, map);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle player = await harness.AttachSimulatedPlayerAsync(CreateTestPlayerDto(
            "M1FixHero",
            ElementSeries.Metal,
            52080,
            101696,
            skills: [new CharacterSkillDto(1, 1, 0)]));

        PlayerZoneState state = RequireState(player);
        int baseDefense = state.EffectiveDefense;
        int basePhysRes = state.PhysicsResistPercent;
        int baseColdRes = state.ColdResistPercent;
        int baseFireRes = state.FireResistPercent;
        int baseLightRes = state.LightResistPercent;
        int basePoisonRes = state.PoisonResistPercent;

        // M1-F1: case 58 (ignoredefense_p) must NOT add BonusDefense; case 114 (allres_p) must add to all 5 resists
        CreateAndEquipArmor(
            state,
            1u,
            new ItemMagicAttributeSlot(0, 58, 50, 0, 0, 0),
            new ItemMagicAttributeSlot(2, 114, 20, 0, 0, 0));

        Assert.Equal(baseDefense, state.EffectiveDefense);
        Assert.Equal(basePhysRes + 20, state.PhysicsResistPercent);
        Assert.Equal(baseColdRes + 20, state.ColdResistPercent);
        Assert.Equal(baseFireRes + 20, state.FireResistPercent);
        Assert.Equal(baseLightRes + 20, state.LightResistPercent);
        Assert.Equal(basePoisonRes + 20, state.PoisonResistPercent);

        // M1-F2: Golden mask Row 4367 (detailType=11, particularType=578, lifemax_yan_v=484) must resolve disguise NPC template 1311, NOT 484
        Assert.True(
            SharedGameAssetsFixture.Default.ItemTemplateCatalog.TryGetItemByRowId(ItemQualityKind.Golden, 4367, out ItemTemplate? goldenMaskTpl) &&
            goldenMaskTpl is not null);
        Assert.Equal(11, goldenMaskTpl.DetailType);
        Assert.Equal(578, goldenMaskTpl.ParticularType);
        var maskSlot = new PlayerItemSlot(
            runtimeItemId: 99u,
            domainItemId: Guid.NewGuid(),
            template: goldenMaskTpl,
            genre: goldenMaskTpl.Genre,
            detailType: (short)goldenMaskTpl.DetailType,
            particularType: (short)goldenMaskTpl.ParticularType,
            level: goldenMaskTpl.Level,
            series: ElementSeries.Metal,
            quality: ItemQualityKind.Golden,
            randomSeed: 1u,
            generatorVersion: ItemConsts.GeneratorVersion,
            luck: 0,
            currentDurability: 100,
            maxDurability: 100,
            stackCount: 1,
            containerPlace: ItemContainerPlace.Equip,
            gridX: 0,
            gridY: 0);

        Assert.Equal(1311, GoldenPlatinaEquipResCatalog.ResolveMaskDisguiseNpcTemplateId(maskSlot));

        // M1-F3: Neutral skill 1 casts against Wood monster (m1) and Fire monster (m2) with equal damage (no Metal conquest bias) and emits 0x5A
        state.FightMode = 1;
        state.BonusAttackRating = 10000;
        CreateAndEquipWeapon(state, 2u, detailType: 0, particularType: 0, minDamage: 100, maxDamage: 100);
        NpcZoneState m1 = worker.StaticNpcs[1u]; // Wood
        NpcZoneState m2 = worker.StaticNpcs[2u]; // Fire
        uint m1Initial = m1.CurLife;
        uint m2Initial = m2.CurLife;
        player.CastSkill(1, -1, (int)m1.NpcId);
        await harness.StepFramesAsync(6);
        player.CastSkill(1, -1, (int)m2.NpcId);
        await harness.StepFramesAsync(6);
        uint m1Damage = m1Initial - m1.CurLife;
        uint m2Damage = m2Initial - m2.CurLife;
        Assert.Equal(110u, m1Damage);
        Assert.Equal(110u, m2Damage);
        Assert.Contains(player.Recorder.SkillCastBroadcasts, s => !s.CastDirectly && s.SkillId == 1u);

        // Weapon magic affixes 28 (weapondamagemin_v) & 29 (weapondamagemax_v) added before 40 (weapondamageenhance_p)
        CreateAndEquipWeapon(
            state,
            3u,
            detailType: 0,
            particularType: 0,
            minDamage: 100,
            maxDamage: 100,
            new ItemMagicAttributeSlot(0, 28, 20, 0, 0, 0),
            new ItemMagicAttributeSlot(2, 29, 40, 0, 0, 0),
            new ItemMagicAttributeSlot(4, 40, 50, 0, 0, 0));
        Assert.Equal(180, state.WeaponMinDamage);
        Assert.Equal(210, state.WeaponMaxDamage);
    }

    [Fact]
    public async Task AutoDeath_ShouldFireExactlyOnceWithoutInfiniteRecursion_ForBothPlayerAndNpc()
    {
        SkillTemplate autoDeathProc = CreateSyntheticSkill(
            id: 5070,
            code: "test-autodeath-10",
            series: ElementSeries.Metal,
            isPhysical: false,
            isMelee: true,
            useAttackRating: false,
            doHurtAnimation: false,
            missileId: 0,
            new SkillEffectScalingTemplate(59, "physicsdamage_v", "damage", new SkillParameterCurveTemplate("Constant", 10), null, new SkillParameterCurveTemplate("Constant", 10)));

        SkillTemplate playerKillSkill = CreateSyntheticSkill(
            id: 5071,
            code: "test-kill-5000",
            series: ElementSeries.Metal,
            isPhysical: false,
            isMelee: true,
            useAttackRating: false,
            doHurtAnimation: false,
            missileId: 0,
            new SkillEffectScalingTemplate(59, "physicsdamage_v", "damage", new SkillParameterCurveTemplate("Constant", 5000), null, new SkillParameterCurveTemplate("Constant", 5000)));

        var catalog = new OverlaySkillTemplateCatalog(
            SharedGameAssetsFixture.Default.SkillTemplateCatalog,
            autoDeathProc,
            playerKillSkill);

        // Part A: 500 HP player with 10 dmg AutoDeath (198) killed by 5000 HP monster -> fires once (m1.CurLife == 4990)
        MapDescriptor mapA = CreateThreeMonsterMap();
        ZoneSimulationHarness harnessA = CreateHarness(seed: 201, mapA, catalog);
        MapZoneWorker workerA = harnessA.GetZone(53);
        ConfigurePassiveMonsters(workerA);

        SimulatedPlayerHandle victimPlayer = await harnessA.AttachSimulatedPlayerAsync(CreateTestPlayerDto(
            "VictimPlayer",
            ElementSeries.Metal,
            52096,
            101696,
            life: 500,
            skills: [new CharacterSkillDto(5070, 1, 0)]));
        PlayerZoneState victimState = RequireState(victimPlayer);
        victimState.FightMode = 1;
        victimState.SpawnProtectUntilTick = 0;
        CreateAndEquipArmor(victimState, 1u, new ItemMagicAttributeSlot(0, 198, (5070 << 8) | 1, 0, 100, 0));

        NpcZoneState killerMonster = workerA.StaticNpcs[1u];
        killerMonster.CurLife = 5000;
        killerMonster.AiMode = 3;
        killerMonster.AiState = MonsterAiStateKind.Attack;
        killerMonster.AggroTargetNpcId = victimState.RuntimeNpcId;
        killerMonster.PhysicsMinDamage = 1000;
        killerMonster.PhysicsMaxDamage = 1000;
        killerMonster.AttackRating = 100000;
        killerMonster.NextAiActionTick = 0;
        killerMonster.NextAttackReadyTick = 0;
        killerMonster.AttackRadiusMps = 200;
        killerMonster.VisionRadiusMps = 500;

        await harnessA.StepFramesAsync(1);
        Assert.Equal(0, victimState.CurrentLife);
        Assert.Equal(4990u, killerMonster.CurLife);
        Assert.Equal(1, victimPlayer.Recorder.SkillCastBroadcasts.Count(s => s.CastDirectly && s.CasterNpcId == victimState.RuntimeNpcId && s.SkillId == 5070u));

        // Additional ticks must never re-trigger AutoDeath
        await harnessA.StepFramesAsync(4);
        Assert.Equal(1, victimPlayer.Recorder.SkillCastBroadcasts.Count(s => s.CastDirectly && s.CasterNpcId == victimState.RuntimeNpcId && s.SkillId == 5070u));

        // Part B: 500 HP NPC with 10 dmg AutoDeath killed by 5000 HP player -> fires once (player.CurrentLife == 4990)
        MapDescriptor mapB = CreateThreeMonsterMap();
        ZoneSimulationHarness harnessB = CreateHarness(seed: 202, mapB, catalog);
        MapZoneWorker workerB = harnessB.GetZone(53);
        ConfigurePassiveMonsters(workerB);

        SimulatedPlayerHandle killerPlayer = await harnessB.AttachSimulatedPlayerAsync(CreateTestPlayerDto(
            "KillerPlayer",
            ElementSeries.Metal,
            52160,
            101696,
            life: 5000,
            skills: [new CharacterSkillDto(5071, 1, 0)]));
        PlayerZoneState killerState = RequireState(killerPlayer);
        killerState.FightMode = 1;

        NpcZoneState victimNpc = workerB.StaticNpcs[2u];
        victimNpc.CurLife = 500;
        victimNpc.CombatModifiers.AutoDeathSkills.AddEntry(AutoCastSkillEntry.FromMagicAttribute((5070 << 8) | 1, 0, 100, allowSyncCooldown: false));

        killerPlayer.CastSkill(5071, -1, (int)victimNpc.NpcId);
        await harnessB.StepFramesAsync(4);
        Assert.Equal(0u, victimNpc.CurLife);
        Assert.Equal(4990, killerState.CurrentLife);
        Assert.Equal(1, killerPlayer.Recorder.SkillCastBroadcasts.Count(s => s.CastDirectly && s.CasterNpcId == victimNpc.NpcId && s.SkillId == 5070u));
    }

    [Fact]
    public async Task Leech_WithEqualPhysicsAndColdRange_ShouldNotOverwritePhysHpLost()
    {
        SkillTemplate equalRangeSkill = CreateSyntheticSkill(
            id: 5075,
            code: "test-equal-phys-cold",
            series: ElementSeries.Metal,
            isPhysical: true,
            isMelee: true,
            useAttackRating: false,
            doHurtAnimation: false,
            missileId: 0,
            new SkillEffectScalingTemplate(59, "physicsdamage_v", "damage", new SkillParameterCurveTemplate("Constant", 500), null, new SkillParameterCurveTemplate("Constant", 500)),
            new SkillEffectScalingTemplate(60, "colddamage_v", "damage", new SkillParameterCurveTemplate("Constant", 500), null, new SkillParameterCurveTemplate("Constant", 500)),
            new SkillEffectScalingTemplate(66, "steallife_p", "damage", new SkillParameterCurveTemplate("Constant", 20)));

        var catalog = new OverlaySkillTemplateCatalog(SharedGameAssetsFixture.Default.SkillTemplateCatalog, equalRangeSkill);
        MapDescriptor map = CreateThreeMonsterMap();
        ZoneSimulationHarness harness = CreateHarness(seed: 203, map, catalog);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle attacker = await harness.AttachSimulatedPlayerAsync(CreateTestPlayerDto(
            "EqualChannelAttacker",
            ElementSeries.Metal,
            52080,
            101696,
            life: 2000,
            skills: [new CharacterSkillDto(5075, 1, 0)]));
        PlayerZoneState attackerState = RequireState(attacker);
        attackerState.FightMode = 1;
        attackerState.MaxLife = 2000;
        attackerState.CurrentLife = 1000;

        NpcZoneState m3 = worker.StaticNpcs[3u]; // Metal (no series shift from Metal skill)
        m3.CurLife = 5000;
        m3.PhysicsResistPercent = 95;
        m3.CombatModifiers.PhysicsMaxResistBonusPercent = 20; // effMaxRes = 95 -> effRes = 95% => 500 * 5 / 100 = 25 physHpLost!
        m3.ColdResistPercent = 0;

        SkillDamagePayload payload = CombatDamageCalculator.BuildPlayerSkillDamagePayload(attackerState, equalRangeSkill, 1);
        CombatHitResolutionResult directResult = CombatDamageCalculator.ResolveHitAndApplyDamage(
            attackerState,
            m3,
            isPvp: false,
            in payload,
            new Random(203));

        Assert.Equal(25, directResult.PhysicsHpLost);
        Assert.Equal(5, directResult.LifeStolen);
        Assert.Equal(1005, attackerState.CurrentLife);
        Assert.Equal(5000u - 525u, m3.CurLife);

        // Also verify in 3-monster live zone cast
        attacker.CastSkill(5075, -1, (int)m3.NpcId);
        await harness.StepFramesAsync(4);
        Assert.Equal(1010, attackerState.CurrentLife);
    }

    [Fact]
    public async Task InitialPoisonHit_ShouldRouteReturnViaMeleeNotPoison_AndReflectPoisonOnlyOnDoTTicks()
    {
        SkillTemplate meleePoisonSkill = CreateSyntheticSkill(
            id: 5076,
            code: "test-melee-poison-return",
            series: ElementSeries.Wood,
            isPhysical: false,
            isMelee: true,
            useAttackRating: false,
            doHurtAnimation: false,
            missileId: 0,
            new SkillEffectScalingTemplate(
                63,
                "poisondamage_v",
                "damage",
                new SkillParameterCurveTemplate("Constant", 500),
                new SkillParameterCurveTemplate("Constant", 20),
                new SkillParameterCurveTemplate("Constant", 5)));

        var catalog = new OverlaySkillTemplateCatalog(SharedGameAssetsFixture.Default.SkillTemplateCatalog, meleePoisonSkill);
        MapDescriptor map = CreateThreeMonsterMap();
        ZoneSimulationHarness harness = CreateHarness(seed: 204, map, catalog);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle attackerA = await harness.AttachSimulatedPlayerAsync(CreateTestPlayerDto(
            "PoisonAttackerA",
            ElementSeries.Wood,
            52000,
            101696,
            life: 5000,
            energy: 0,
            skills: [new CharacterSkillDto(5076, 1, 0)]));
        SimulatedPlayerHandle defenderMeleeReturnOnly = await harness.AttachSimulatedPlayerAsync(CreateTestPlayerDto(
            "DefMeleeReturnOnly",
            ElementSeries.Wood,
            52032,
            101696,
            life: 5000));
        SimulatedPlayerHandle defenderPoisonReturnOnly = await harness.AttachSimulatedPlayerAsync(CreateTestPlayerDto(
            "DefPoisonReturnOnly",
            ElementSeries.Wood,
            52032,
            101728,
            life: 5000));

        PlayerZoneState atkState = RequireState(attackerA);
        PlayerZoneState defMeleeState = RequireState(defenderMeleeReturnOnly);
        PlayerZoneState defPoisonState = RequireState(defenderPoisonReturnOnly);
        atkState.FightMode = 1;
        atkState.BaseCamp = EntityCamp.Justice;
        atkState.CurrentCamp = EntityCamp.Justice;
        atkState.PkState = PlayerPkMode.Slaughter;

        defMeleeState.FightMode = 1;
        defMeleeState.BaseCamp = EntityCamp.Evil;
        defMeleeState.CurrentCamp = EntityCamp.Evil;
        CreateAndEquipArmor(defMeleeState, 1u, new ItemMagicAttributeSlot(0, 118, 100, 0, 0, 0)); // MeleeDamageReturn = 100, PoisonDamageReturn = 0

        defPoisonState.FightMode = 1;
        defPoisonState.BaseCamp = EntityCamp.Evil;
        defPoisonState.CurrentCamp = EntityCamp.Evil;
        CreateAndEquipArmor(defPoisonState, 2u, new ItemMagicAttributeSlot(0, 194, 100, 0, 0, 0)); // PoisonDamageReturn = 100, MeleeDamageReturn = 0

        // 1. Initial hit against MeleeReturnOnly reflects 100 (bStateHurt == false -> 500 * 100% * 20% PK = 100)
        attackerA.CastSkill(5076, -1, (int)defMeleeState.RuntimeNpcId);
        await harness.StepFramesAsync(3); // before periodic 5-frame DoT tick fires
        Assert.Equal(4900, atkState.CurrentLife);

        // 2. Initial hit against PoisonReturnOnly reflects 0 on hit, then reflects 100 on periodic DoT tick (bStateHurt == true)
        atkState.CurrentLife = 5000;
        attackerA.CastSkill(5076, -1, (int)defPoisonState.RuntimeNpcId);
        await harness.StepFramesAsync(3); // initial hit only (waitFrames=2; tick=2 hits, periodic tick starts +5 frames later)
        Assert.Equal(5000, atkState.CurrentLife);

        await harness.StepFramesAsync(6); // periodic DoT tick fires -> bStateHurt == true -> reflects 100
        Assert.Equal(4900, atkState.CurrentLife);
    }

    [Fact]
    [Trait("Category", "LegacyData")]
    public async Task ColdAndPoisonPayload_ShouldRespectZeroColdDurationAndZeroPoisonDamage()
    {
        SkillTemplate zeroPoisonSkill = CreateSyntheticSkill(
            id: 5077,
            code: "test-zero-poison",
            series: ElementSeries.Wood,
            isPhysical: false,
            isMelee: true,
            useAttackRating: false,
            doHurtAnimation: false,
            missileId: 0,
            new SkillEffectScalingTemplate(
                63,
                "poisondamage_v",
                "damage",
                new SkillParameterCurveTemplate("Constant", 0),
                new SkillParameterCurveTemplate("Constant", 60),
                new SkillParameterCurveTemplate("Constant", 10)));

        var catalog = new OverlaySkillTemplateCatalog(SharedGameAssetsFixture.Default.SkillTemplateCatalog, zeroPoisonSkill);
        MapDescriptor map = CreateThreeMonsterMap();
        ZoneSimulationHarness harness = CreateHarness(seed: 205, map, catalog);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle player = await harness.AttachSimulatedPlayerAsync(CreateTestPlayerDto(
            "ColdPoisonZeroHero",
            ElementSeries.Water,
            52080,
            101696,
            energy: 80,
            skills:
            [
                new CharacterSkillDto(88, 1, 0),
                new CharacterSkillDto(5077, 1, 0),
            ]));
        PlayerZoneState state = RequireState(player);
        state.FightMode = 1;

        // 1. Real Skill 88 (colddamage_v with p1 == 0): FreezeRemainingFrames == 0 when ColdEnhancePercent == 0
        Assert.True(catalog.TryGetSkill(88, out SkillTemplate? skill88));
        Assert.NotNull(skill88);
        SkillDamagePayload coldPayloadNoEnh = CombatDamageCalculator.BuildPlayerSkillDamagePayload(state, skill88, 1);
        Assert.Equal(0, coldPayloadNoEnh.ColdFreezeDurationFrames);

        NpcZoneState m1 = worker.StaticNpcs[1u];
        m1.CurLife = 5000;
        CombatDamageCalculator.ResolveHitAndApplyDamage(state, m1, isPvp: false, in coldPayloadNoEnh, new Random(205));
        Assert.Equal(0, m1.StatusEffects.FreezeRemainingFrames);

        // With ColdEnhancePercent == 25, FreezeRemainingFrames == 25 (NOT 36 + 25 = 61!)
        state.SkillCombatModifiers.ColdEnhancePercent = 25;
        SkillDamagePayload coldPayloadWithEnh = CombatDamageCalculator.BuildPlayerSkillDamagePayload(state, skill88, 1);
        Assert.Equal(25, coldPayloadWithEnh.ColdFreezeDurationFrames);

        NpcZoneState m2 = worker.StaticNpcs[2u];
        m2.CurLife = 5000;
        CombatDamageCalculator.ResolveHitAndApplyDamage(state, m2, isPvp: false, in coldPayloadWithEnh, new Random(205));
        Assert.Equal(25, m2.StatusEffects.FreezeRemainingFrames);

        // 2. poisondamage_v with p0 == 0 must NOT inject EffectiveEnergy poison damage or DoT
        SkillDamagePayload zeroPoisonPayload = CombatDamageCalculator.BuildPlayerSkillDamagePayload(state, zeroPoisonSkill, 1);
        Assert.Equal(0, zeroPoisonPayload.PoisonDamagePerTick);

        NpcZoneState m3 = worker.StaticNpcs[3u];
        m3.CurLife = 5000;
        player.CastSkill(5077, -1, (int)m3.NpcId);
        await harness.StepFramesAsync(4);
        Assert.False(m3.StatusEffects.IsPoisoned);
        Assert.Equal(5000u, m3.CurLife);
    }

    [Fact]
    public async Task MagicPoison_ShouldBlendEquipmentAndSkillLayersViaMixPoisonDamage_AndPreserveSub10Interval()
    {
        SkillTemplate poisonMagicSkill = CreateSyntheticSkill(
            id: 5078,
            code: "test-magic-poison-mix",
            series: ElementSeries.Wood,
            isPhysical: false,
            isMelee: true,
            useAttackRating: false,
            doHurtAnimation: false,
            missileId: 0,
            new SkillEffectScalingTemplate(
                63,
                "poisondamage_v",
                "damage",
                new SkillParameterCurveTemplate("Constant", 20),
                new SkillParameterCurveTemplate("Constant", 60),
                new SkillParameterCurveTemplate("Constant", 10)));

        var catalog = new OverlaySkillTemplateCatalog(SharedGameAssetsFixture.Default.SkillTemplateCatalog, poisonMagicSkill);
        MapDescriptor map = CreateThreeMonsterMap();
        ZoneSimulationHarness harness = CreateHarness(seed: 206, map, catalog);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle player = await harness.AttachSimulatedPlayerAsync(CreateTestPlayerDto(
            "MagicPoisonMixer",
            ElementSeries.Wood,
            52080,
            101696,
            energy: 0,
            skills: [new CharacterSkillDto(5078, 1, 0)]));
        PlayerZoneState state = RequireState(player);
        state.FightMode = 1;

        // Simultaneous eq.MagicPoison* (100, dur 60, interval 10) and sk.MagicPoison* (100, dur 60, interval 6)
        // MixPoisonDamage(100, 60, 10, 100, 60, 6) = (213, 60, 8) -> preserves sub-10 interval 8, dur 60!
        // MixPoisonDamage(20, 60, 10, 213, 60, 8) = (257, 60, 9)
        state.EquipmentCombatModifiers.MagicPoisonDamagePerTick = 100;
        state.EquipmentCombatModifiers.MagicPoisonDurationFrames = 60;
        state.EquipmentCombatModifiers.MagicPoisonIntervalFrames = 10;
        state.SkillCombatModifiers.MagicPoisonDamagePerTick = 100;
        state.SkillCombatModifiers.MagicPoisonDurationFrames = 60;
        state.SkillCombatModifiers.MagicPoisonIntervalFrames = 6;

        SkillDamagePayload payload = CombatDamageCalculator.BuildPlayerSkillDamagePayload(state, poisonMagicSkill, 1);
        Assert.Equal(257, payload.PoisonDamagePerTick);
        Assert.Equal(60, payload.PoisonDurationFrames);
        Assert.Equal(9, payload.PoisonIntervalFrames);

        NpcZoneState m1 = worker.StaticNpcs[1u];
        player.CastSkill(5078, -1, (int)m1.NpcId);
        await harness.StepFramesAsync(4);
        Assert.True(m1.StatusEffects.IsPoisoned);
        Assert.Equal(9, m1.StatusEffects.PoisonIntervalFrames);
    }

    [Fact]
    public async Task IgnoreDamage_ShouldBlockPoisonDoTApplication()
    {
        SkillTemplate poisonSkill = CreateSyntheticSkill(
            id: 5079,
            code: "test-ignore-damage-poison",
            series: ElementSeries.Wood,
            isPhysical: false,
            isMelee: true,
            useAttackRating: false,
            doHurtAnimation: false,
            missileId: 0,
            new SkillEffectScalingTemplate(
                63,
                "poisondamage_v",
                "damage",
                new SkillParameterCurveTemplate("Constant", 500),
                new SkillParameterCurveTemplate("Constant", 60),
                new SkillParameterCurveTemplate("Constant", 10)));

        var catalog = new OverlaySkillTemplateCatalog(SharedGameAssetsFixture.Default.SkillTemplateCatalog, poisonSkill);
        MapDescriptor map = CreateThreeMonsterMap();
        ZoneSimulationHarness harness = CreateHarness(seed: 207, map, catalog);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle attacker = await harness.AttachSimulatedPlayerAsync(CreateTestPlayerDto(
            "PoisonCaster",
            ElementSeries.Wood,
            52080,
            101696,
            energy: 0,
            skills: [new CharacterSkillDto(5079, 1, 0)]));
        PlayerZoneState atkState = RequireState(attacker);
        atkState.FightMode = 1;

        NpcZoneState m1 = worker.StaticNpcs[1u];
        m1.CurLife = 5000;
        m1.CombatModifiers.IgnoreDamageCount = 1;

        attacker.CastSkill(5079, -1, (int)m1.NpcId);
        await harness.StepFramesAsync(4);
        Assert.Equal(5000u, m1.CurLife);
        Assert.False(m1.StatusEffects.IsPoisoned);
    }

    [Fact]
    [Trait("Category", "LegacyData")]
    public async Task ShieldsSorbAndManaEffects_ShouldEnforceStaticReflectClearDynamicClampSorbCapAndManaConversion()
    {
        SkillTemplate multiChannelSkill = CreateSyntheticSkill(
            id: 5080,
            code: "test-shields-sorb-mana",
            series: ElementSeries.Metal,
            isPhysical: false,
            isMelee: true,
            useAttackRating: false,
            doHurtAnimation: false,
            missileId: 0,
            new SkillEffectScalingTemplate(59, "physicsdamage_v", "damage", new SkillParameterCurveTemplate("Constant", 100), null, new SkillParameterCurveTemplate("Constant", 100)),
            new SkillEffectScalingTemplate(61, "firedamage_v", "damage", new SkillParameterCurveTemplate("Constant", 100), null, new SkillParameterCurveTemplate("Constant", 100)),
            new SkillEffectScalingTemplate(63, "poisondamage_v", "damage", new SkillParameterCurveTemplate("Constant", 200), new SkillParameterCurveTemplate("Constant", 20), new SkillParameterCurveTemplate("Constant", 10)));

        var catalog = new OverlaySkillTemplateCatalog(SharedGameAssetsFixture.Default.SkillTemplateCatalog, multiChannelSkill);
        MapDescriptor map = CreateThreeMonsterMap();
        ZoneSimulationHarness harness = CreateHarness(seed: 208, map, catalog);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle attacker = await harness.AttachSimulatedPlayerAsync(CreateTestPlayerDto(
            "ShieldTesterAtk",
            ElementSeries.Metal,
            52000,
            101696,
            life: 5000,
            energy: 0,
            skills: [new CharacterSkillDto(5080, 1, 0)]));
        SimulatedPlayerHandle defender = await harness.AttachSimulatedPlayerAsync(CreateTestPlayerDto(
            "ShieldTesterDef",
            ElementSeries.Metal,
            52032,
            101696,
            life: 5000,
            mana: 1000));

        PlayerZoneState atkState = RequireState(attacker);
        PlayerZoneState defState = RequireState(defender);
        atkState.FightMode = 1;
        atkState.BaseCamp = EntityCamp.Justice;
        atkState.CurrentCamp = EntityCamp.Justice;
        atkState.PkState = PlayerPkMode.Slaughter;
        defState.FightMode = 1;
        defState.BaseCamp = EntityCamp.Evil;
        defState.CurrentCamp = EntityCamp.Evil;

        // 1. StaticMagicShield on attacker broken by defender's MeleeDamageReturn -> emits 0x87 clear for 721 on attacker!
        atkState.SkillCombatModifiers.StaticMagicShield = 50;
        defState.SkillCombatModifiers.MeleeDamageReturnPercent = 100;
        // Defender has DynamicMagicShield = 1000 (clamps Phys=100->1 and Fire=100->1), SorbDamage = 600 (clamped to 500/1000),
        // Poison2DecMana = 50%, Damage2Mana = 50%
        defState.SkillCombatModifiers.DynamicMagicShield = 1000;
        defState.SkillCombatModifiers.SorbDamagePerMille = 600; // clamped to 500
        defState.SkillCombatModifiers.Poison2DecManaPercent = 50;
        defState.SkillCombatModifiers.Damage2ManaPercent = 50;

        // Cast 5080:
        // Phys raw=100 -> Dynamic absorb 500 -> clamped to 1 -> reflect 1 -> PK 20% = 0 HP dealt (since 1*20/100=0)
        // Fire raw=100 -> Dynamic absorb 500 -> clamped to 1 -> reflect 1 -> PK 20% = 0 HP dealt
        // Wait: test StaticMagicShield break on reflect with a 1000-dmg single hit first so reflect = 1000 (> 50 shield)!
        defState.SkillCombatModifiers.DynamicMagicShield = 0;
        defState.SkillCombatModifiers.SorbDamagePerMille = 0;
        defState.SkillCombatModifiers.Poison2DecManaPercent = 0;
        defState.SkillCombatModifiers.Damage2ManaPercent = 0;

        attacker.CastSkill(5080, -1, (int)defState.RuntimeNpcId);
        await harness.StepFramesAsync(4);
        Assert.Equal(0, atkState.SkillCombatModifiers.StaticMagicShield);
        Assert.Contains(attacker.Recorder.SyncStateEffects, e => e.NpcId == atkState.RuntimeNpcId && e.SkillId == 721 && e.RemainTimeFrames == 0);

        // 2. DynamicMagicShield (clamps Phys=100->1, Fire=100->1) + SorbDamage (cap 500/1000 on 200 poison: 200*20% PK = 40 -> 40 - 20 = 20) + Poison2DecMana (50% of 40 = 20) + Damage2Mana (50% of 20 = 10)
        // Use non-PvP monster m1 (Wood, 0 res) for exact integer DynamicMagicShield + Sorb + Mana conversion checks without PK 20% truncation:
        NpcZoneState m1 = worker.StaticNpcs[1u];
        m1.CurLife = 5000;
        m1.MaxMana = 1000;
        m1.CurrentMana = 500;
        m1.CombatModifiers.DynamicMagicShield = 1000; // Phys(100)->1, Fire(100)->1
        m1.CombatModifiers.SorbDamagePerMille = 800;  // Clamped to 500 -> 50% reduction on Poison(200) = 100
        m1.CombatModifiers.Poison2DecManaPercent = 50; // -50% of 200 = -100 mana on poison channel (500 -> 400)
        m1.CombatModifiers.Damage2ManaPercent = 50;    // +50% of (1+1+100) = +51 mana (400 -> 450? Wait: 1->+0, 1->+0, 100->+50 => 450!)
        // Phys(1) -> Sorb 50% = 1 - 0 = 1 -> HP -1, D2M (1*50)/100 = 0
        // Fire(1) -> Sorb 50% = 1 - 0 = 1 -> HP -1, D2M (1*50)/100 = 0
        // Poison(200) -> P2M (200*50)/100 = -100 mana (500->400) -> Sorb 50% = 100 -> HP -100 -> D2M (100*50)/100 = +50 mana (400->450)
        // Total HP lost = 102 (5000 -> 4898), final Mana = 450!
        attacker.CastSkill(5080, -1, (int)m1.NpcId);
        await harness.StepFramesAsync(4);
        Assert.Equal(5000u - 102u, m1.CurLife);
        Assert.Equal(450, m1.CurrentMana);
    }
}
