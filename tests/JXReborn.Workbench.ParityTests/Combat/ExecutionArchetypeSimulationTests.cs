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
using JXReborn.ZoneEngine.Items;
using JXReborn.ZoneEngine.NpcEntities;
using JXReborn.ZoneEngine.PlayerSessions;
using JXReborn.ZoneEngine.WorldZones;
using Xunit;

namespace JXReborn.ZoneEngine.Tests.Combat;

[Trait("Category", "LegacyData")]
public sealed class ExecutionArchetypeSimulationTests
{
    private static MapDescriptor CreateArenaMap(params (int MpsX, int MpsY, ElementSeries Series, string Name)[] monsters)
    {
        var spawns = new List<StaticNpcSpawnTemplate>(monsters.Length);
        for (int i = 0; i < monsters.Length; i++)
        {
            (int mpsX, int mpsY, ElementSeries series, string name) = monsters[i];
            spawns.Add(new StaticNpcSpawnTemplate(
                TemplateId: 1,
                MapId: 53,
                SpawnMpsX: mpsX,
                SpawnMpsY: mpsY,
                Level: 10,
                Kind: EntityKind.Monster,
                Camp: EntityCamp.Animal,
                Series: series,
                IsSpecialNpc: false,
                DisplayName: name,
                ScriptPath: string.Empty));
        }

        return new MapDescriptor(
            MapId: 53,
            MapNameUnicode: "Ba Lăng Huyện",
            RawSubPath: "balanghuyen",
            RegionBeginX: 0,
            RegionBeginY: 0,
            RegionWidth: 200,
            RegionHeight: 200)
        {
            RegionNpcSpawns = spawns,
        };
    }

    private static ZoneSimulationHarness CreateHarness(
        int seed,
        MapDescriptor map,
        ISkillTemplateCatalog? skillCatalog = null,
        bool includeMapObstacles = true,
        IMissileTemplateCatalog? missileCatalog = null,
        IMapCatalog? mapCatalog = null)
    {
        SharedGameAssetsFixture assets = SharedGameAssetsFixture.Default;
        var services = new ZoneAssetServices
        {
            MapCatalog = mapCatalog ?? (includeMapObstacles ? assets.MapCatalog : null),
            NpcTemplateCatalog = assets.NpcTemplateCatalog,
            TrapScriptCatalog = assets.TrapScriptCatalog,
            SkillTemplateCatalog = skillCatalog ?? assets.SkillTemplateCatalog,
            MissileTemplateCatalog = missileCatalog ?? assets.MissileTemplateCatalog,
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
            // Isolate post-cast missile trajectory/archetype physics from the 18-frame player wind-up (tested in SkillCastWindupTimingTests)
            w.ExecutionContext.Options.PlayerBaseAttackFrame = 0;
            w.ExecutionContext.Options.PlayerBaseCastFrame = 0;
        }

        return harness;
    }

    private static CharacterDto CreateTestPlayerDto(
        string name,
        ElementSeries series,
        int mpsX,
        int mpsY,
        IReadOnlyList<CharacterSkillDto> skills,
        int life = 8000,
        int mana = 5000) =>
        new(
            Id: Guid.NewGuid(),
            AccountId: Guid.NewGuid(),
            SlotIndex: 0,
            RoleNameUnicode: name,
            Gender: CharacterGender.Male,
            Series: series,
            Level: 80,
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
            CurrentStamina: 1000,
            MaxStamina: 1000,
            Strength: 100,
            Dexterity: 100,
            Vitality: 100,
            Energy: 100,
            Lucky: 0,
            UnallocatedAttributePoints: 0,
            UnallocatedSkillPoints: 0,
            InventoryMoney: 10000,
            RepositoryMoney: 0,
            CurrentFactionId: 255,
            FirstFactionId: 255,
            FactionJoinCount: 0,
            Skills: skills);

    private static SkillTemplate RequireSkill(ISkillTemplateCatalog catalog, int skillId)
    {
        Assert.True(catalog.TryGetSkill(skillId, out SkillTemplate? skill) && skill is not null);
        return skill!;
    }

    private static void ConfigurePassiveMonsters(MapZoneWorker worker, uint life = 10_000)
    {
        Assert.True(worker.StaticNpcs.Count >= 3);
        foreach (NpcZoneState npc in worker.StaticNpcs.Values)
        {
            npc.AiMode = 0;
            npc.AiState = MonsterAiStateKind.IdlePatrol;
            npc.Doing = EntityDoingPosture.Stand;
            npc.StatusEffects.ClearAll();
            npc.Defense = 0;
            npc.CurLife = life;
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

    private static PlayerItemSlot CreateAndEquipWeapon(
        PlayerZoneState player,
        uint runtimeItemId,
        int minDamage = 120,
        int maxDamage = 120,
        params ItemMagicAttributeSlot[] affixes)
    {
        var weaponTpl = new ItemTemplate(
            Id: 9100 + (int)runtimeItemId,
            Code: $"test-weapon-{runtimeItemId}",
            Name: $"Test Weapon {runtimeItemId}",
            Genre: ItemGenreKind.Equipment,
            Quality: ItemQualityKind.Normal,
            DetailType: 0,
            ParticularType: 0,
            Level: 1,
            ElementSeries: player.Series,
            GridWidthCells: 1,
            GridHeightCells: 2,
            ObjDataId: 1,
            AppearanceResId: 1,
            PriceGold: 100,
            MaxStackCount: 1,
            BaseAttributes: [new ItemBaseAttributeTemplate(1, "damage_v", minDamage, maxDamage)],
            Requirements: []);

        var slot = new PlayerItemSlot(
            runtimeItemId: runtimeItemId,
            domainItemId: Guid.NewGuid(),
            template: weaponTpl,
            genre: ItemGenreKind.Equipment,
            detailType: 0,
            particularType: 0,
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
            DetailType: 2,
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

    [Fact]
    public async Task AtFirerMeleeAndProjectileAoE_WithThreeMonsters_ShouldHitAllTargetsAndEmit0x5AAnd0x56()
    {
        MapDescriptor map = CreateArenaMap(
            (52096, 101696, ElementSeries.Wood, "Quái Đông"),
            (52000, 101696, ElementSeries.Fire, "Quái Tây"),
            (52048, 101744, ElementSeries.Earth, "Quái Nam"));

        // Synthetic direct IsMelee AoE skill (MissileId = 0, atFirer) to cover non-missile melee AoE as well
        var syntheticMeleeAoe = new SkillTemplate(
            Id: 9901,
            Code: "syn-melee-aoe",
            Name: "Synthetic Melee AoE",
            Style: SkillStyleKind.Projectile,
            ElementSeries: ElementSeries.Metal,
            IsOffensive: true,
            IsPhysical: true,
            IsMelee: true,
            IsAura: false,
            IsWeaponSkill: false,
            IsExpSkill: false,
            UseAttackRating: false,
            DoHurtAnimation: true,
            PeaceCanUse: false,
            ReqLevel: 1,
            MaxLevel: 20,
            EquipLimit: -2,
            HorseLimit: 0,
            CastRangeMps: 128,
            WaitFrames: 2,
            MissileId: 0,
            MissileForm: "atFirer",
            ChildSkillId: 0,
            ChildSkillLevel: -1,
            ChildSkillCount: 1,
            CostScaling: new SkillCostScalingTemplate("mana", 0),
            EffectScaling:
            [
                new SkillEffectScalingTemplate(
                    59,
                    "physicsdamage_v",
                    "damage",
                    new SkillParameterCurveTemplate("Linear", 150, 0),
                    null,
                    new SkillParameterCurveTemplate("Linear", 150, 0)),
            ]);

        var overlay = new OverlaySkillTemplateCatalog(SharedGameAssetsFixture.Default.SkillTemplateCatalog, syntheticMeleeAoe);
        var harness = CreateHarness(42, map, overlay);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "AoeTester",
                ElementSeries.Metal,
                52048,
                101696,
                skills:
                [
                    new CharacterSkillDto(11, 20, 0),
                    new CharacterSkillDto(14, 20, 0),
                    new CharacterSkillDto(319, 20, 0),
                    new CharacterSkillDto(1056, 20, 0),
                    new CharacterSkillDto(9901, 1, 0),
                ]));

        PlayerZoneState state = handle.GetZoneState()!;
        state.FightMode = 1;
        state.BonusAttackRating = 50_000;
        CreateAndEquipWeapon(state, 1u, minDamage: 120, maxDamage: 120);

        NpcZoneState m1 = worker.StaticNpcs[1u];
        NpcZoneState m2 = worker.StaticNpcs[2u];
        NpcZoneState m3 = worker.StaticNpcs[3u];

        // 1) Skill 11 (IsMelee=true, MissileId=86, atFirer, hitR=128, MissileGenerateDelayFrames=6) -> hits all 3 monsters simultaneously
        uint[] before11 = [m1.CurLife, m2.CurLife, m3.CurLife];
        handle.CastSkill(11, -1, (int)m1.NpcId);
        await harness.StepFramesAsync(8);
        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 11u);
        Assert.True(m1.CurLife < before11[0], "Skill 11 should damage m1.");
        Assert.True(m2.CurLife < before11[1], "Skill 11 should damage m2 concurrently.");
        Assert.True(m3.CurLife < before11[2], "Skill 11 should damage m3 concurrently.");

        // 2) Skill 14 (IsMelee=true, MissileId=66, atFirer, WaitFrames=5) -> hits all 3 monsters + DoHurt 0x56
        ConfigurePassiveMonsters(worker);
        state.SetSkillCooldown(14, 0);
        uint[] before14 = [m1.CurLife, m2.CurLife, m3.CurLife];
        handle.CastSkill(14, -1, (int)m1.NpcId);
        await harness.StepFramesAsync(8);
        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 14u);
        Assert.True(m1.CurLife < before14[0], "Skill 14 should damage m1.");
        Assert.True(m2.CurLife < before14[1], "Skill 14 should damage m2.");
        Assert.True(m3.CurLife < before14[2], "Skill 14 should damage m3.");

        // 3) Skill 319 (Hoành Tảo Thiên Quân) & Skill 1056 (Vi Đà Hiến Xử)
        ConfigurePassiveMonsters(worker);
        handle.CastSkill(319, -1, (int)m1.NpcId);
        await harness.StepFramesAsync(5);
        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 319u);
        Assert.True(m1.CurLife < 10_000u && m2.CurLife < 10_000u && m3.CurLife < 10_000u, "Skill 319 should damage all 3 monsters.");

        ConfigurePassiveMonsters(worker);
        handle.CastSkill(1056, -1, (int)m1.NpcId);
        await harness.StepFramesAsync(5);
        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 1056u);
        Assert.True(m1.CurLife < 10_000u && m2.CurLife < 10_000u && m3.CurLife < 10_000u, "Skill 1056 should damage all 3 monsters.");

        // 4) Direct melee AoE without missile (9901) -> hits all 3 monsters (ResolveImmediateSkillHit multi-target) & triggers 0x56 DoHurt
        ConfigurePassiveMonsters(worker);
        handle.Recorder.Clear();
        for (int attempt = 0; attempt < 8; attempt++)
        {
            m1.StatusEffects.HurtRecoveryRemainingFrames = 0;
            m2.StatusEffects.HurtRecoveryRemainingFrames = 0;
            m3.StatusEffects.HurtRecoveryRemainingFrames = 0;
            state.SetSkillCooldown(9901, 0);
            uint b1 = m1.CurLife;
            uint b2 = m2.CurLife;
            uint b3 = m3.CurLife;
            handle.CastSkill(9901, -1, (int)m1.NpcId);
            await harness.StepFramesAsync(4);
            if (attempt == 0)
            {
                Assert.True(m1.CurLife < b1 && m2.CurLife < b2 && m3.CurLife < b3, "Direct melee AoE 9901 should damage all 3 monsters.");
            }
            if (handle.Recorder.HurtBroadcasts.Count > 0)
            {
                break;
            }
        }

        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 9901u);
        Assert.NotEmpty(handle.Recorder.HurtBroadcasts);
    }

    [Fact]
    public async Task PierceCountDamageIntervalAndAutoExplode_WithThreeMonsters_ShouldRespectLimitsAndSplash()
    {
        MapDescriptor map = CreateArenaMap(
            (52096, 101696, ElementSeries.Wood, "Quái 1"),
            (52160, 101696, ElementSeries.Fire, "Quái 2"),
            (52224, 101696, ElementSeries.Metal, "Quái 3"));

        var harness = CreateHarness(42, map);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "PierceAndTickTester",
                ElementSeries.Metal,
                52032,
                101696,
                skills:
                [
                    new CharacterSkillDto(88, 10, 0),
                    new CharacterSkillDto(14, 10, 0),
                    new CharacterSkillDto(105, 10, 0),
                ]));

        PlayerZoneState state = handle.GetZoneState()!;
        state.FightMode = 1;
        state.BonusAttackRating = 50_000;
        CreateAndEquipWeapon(state, 1u, minDamage: 120, maxDamage: 120);

        NpcZoneState m1 = worker.StaticNpcs[1u];
        NpcZoneState m2 = worker.StaticNpcs[2u];
        NpcZoneState m3 = worker.StaticNpcs[3u];

        // (a) Skill 88 (ColVanish=true, PierceCount=1) hits ONLY m1 and vanishes before m2/m3
        handle.CastSkill(88, 52256, 101696);
        await harness.StepFramesAsync(8);
        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 88u);
        Assert.True(m1.CurLife < 10_000u, "Skill 88 should hit first monster.");
        Assert.Equal(10_000u, m2.CurLife);
        Assert.Equal(10_000u, m3.CurLife);

        // (b) Skill 14 (Missile 66, ColVanish=false, PierceCount=0, DamageIntervalFrames=10, LifeFrames=15)
        // Move player to (52160, 101696) so all 3 monsters are within 128 mps of atFirer
        ConfigurePassiveMonsters(worker);
        state.CurrentMpsX = 52160;
        state.CurrentMpsY = 101696;
        handle.CastSkill(14, 52160, 101696);

        // With PlayerBaseAttackFrame=0 (isolated missile-interval test), missile spawns immediately; step 2 frames for 1st AoE tick
        await harness.StepFramesAsync(2);
        uint m1AfterFirst = m1.CurLife;
        uint m2AfterFirst = m2.CurLife;
        uint m3AfterFirst = m3.CurLife;
        Assert.True(m1AfterFirst < 10_000u && m2AfterFirst < 10_000u && m3AfterFirst < 10_000u, "Skill 14 first tick must hit all 3 monsters.");

        // Within DamageIntervalFrames (t < 10): must NOT re-hit
        await harness.StepFramesAsync(5);
        Assert.Equal(m1AfterFirst, m1.CurLife);
        Assert.Equal(m2AfterFirst, m2.CurLife);
        Assert.Equal(m3AfterFirst, m3.CurLife);

        // After DamageIntervalFrames >= 10 (still within LifeFrames=15): MUST re-hit all 3 monsters
        await harness.StepFramesAsync(6);
        Assert.True(m1.CurLife < m1AfterFirst, "Skill 14 should re-hit m1 after DamageIntervalFrames=10.");
        Assert.True(m2.CurLife < m2AfterFirst, "Skill 14 should re-hit m2 after DamageIntervalFrames=10.");
        Assert.True(m3.CurLife < m3AfterFirst, "Skill 14 should re-hit m3 after DamageIntervalFrames=10.");

        // (c) AutoExplode + DamageRadiusMps splash: Skill 105 (M8, Height=450, ZAccel=1024, Life=30, randomOrder:10 x 4x4)
        ConfigurePassiveMonsters(worker);
        m1.MpsX = 52160;
        m1.MpsY = 101696;
        m2.MpsX = 52192;
        m2.MpsY = 101696;
        m3.MpsX = 52128;
        m3.MpsY = 101696;
        handle.CastSkill(105, 52160, 101696);
        await harness.StepFramesAsync(190);
        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 105u);
        Assert.True(m1.CurLife < 10_000u && m2.CurLife < 10_000u && m3.CurLife < 10_000u, "AutoExplode splash should damage all 3 monsters in DamageRadiusMps.");
    }

    [Fact]
    public async Task CastCircleZoneWallSpreadAndMissileNumScaling_WithThreeMonsters_ShouldScaleAndHitAllTargets()
    {
        MapDescriptor map = CreateArenaMap(
            (52128, 101696, ElementSeries.Wood, "Quái 1"),
            (52160, 101728, ElementSeries.Fire, "Quái 2"),
            (52192, 101664, ElementSeries.Earth, "Quái 3"));

        var harness = CreateHarness(42, map);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "FormScalingTester",
                ElementSeries.Fire,
                52032,
                101696,
                skills:
                [
                    new CharacterSkillDto(182, 10, 0),
                    new CharacterSkillDto(128, 1, 0),
                    new CharacterSkillDto(357, 20, 0),
                    new CharacterSkillDto(1368, 1, 0),
                    new CharacterSkillDto(94, 1, 0),
                ]));

        PlayerZoneState state = handle.GetZoneState()!;
        state.FightMode = 1;
        state.BonusAttackRating = 50_000;
        CreateAndEquipWeapon(state, 1u, minDamage: 120, maxDamage: 120);

        NpcZoneState m1 = worker.StaticNpcs[1u];
        NpcZoneState m2 = worker.StaticNpcs[2u];
        NpcZoneState m3 = worker.StaticNpcs[3u];

        // 1) CastZone / atTarget: Skill 182 (ChildSkillCount=4, randomOrder delay=5, M18 dmgC=2 hitR=64)
        // Staggered MissileGenerateStyle means not all 4x4 missiles spawn on frame 0
        handle.CastSkill(182, 52160, 101696);
        await harness.StepFramesAsync(1);
        int immediateCount = worker.ActiveMissileCount;
        Assert.True(immediateCount < 16, $"Staggered randomOrder:5 should delay subsequent missiles (immediate={immediateCount}).");
        await harness.StepFramesAsync(90);
        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 182u);
        Assert.True(m1.CurLife < 10_000u && m2.CurLife < 10_000u && m3.CurLife < 10_000u, "Skill 182 atTarget x4 should hit all 3 monsters.");

        // 2) CastWall (Skill 357 P1=32, P2=65537=0x10001 caster-centered wall shooting forward)
        ConfigurePassiveMonsters(worker);
        handle.CastSkill(357, 52160, 101696);
        await harness.StepFramesAsync(20);
        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 357u);
        Assert.True(m1.CurLife < 10_000u && m2.CurLife < 10_000u && m3.CurLife < 10_000u, "CastWall 357 should hit all 3 monsters.");

        // 2b) CastZone (Skill 94 zone) emits 0x5A and spawns 5x5 M5 zone missiles
        int activeBefore94 = worker.ActiveMissileCount;
        handle.CastSkill(94, 52032, 101696);
        await harness.StepFramesAsync(1);
        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 94u);
        Assert.True(worker.ActiveMissileCount > activeBefore94, "Skill 94 CastZone should spawn M5 zone missiles.");
        await harness.StepFramesAsync(25);

        // 2) skill_misslenum_v scaling on Skill 128 (Lv 1 => 1 missile vs Lv 20 => 15 missiles in wide spread)
        // Spread monsters perpendicular to East direction (wide Y spread)
        ConfigurePassiveMonsters(worker);
        m1.MpsX = 52160; m1.MpsY = 101568; // -128 Y
        m2.MpsX = 52160; m2.MpsY = 101696; // 0 Y (center)
        m3.MpsX = 52160; m3.MpsY = 101824; // +128 Y

        handle.CastSkill(128, 52160, 101696);
        await harness.StepFramesAsync(12);
        Assert.True(m2.CurLife < 10_000u, "Lv1 Skill 128 center dragon should hit m2.");
        Assert.Equal(10_000u, m1.CurLife);
        Assert.Equal(10_000u, m3.CurLife);

        state.SetLearnedSkill(128, 20);
        state.SetSkillCooldown(128, 0);
        handle.CastSkill(128, 52160, 101696);
        await harness.StepFramesAsync(12);
        Assert.True(m1.CurLife < 10_000u && m3.CurLife < 10_000u, "Lv20 Skill 128 (skill_misslenum_v=15) should fan out and hit side monsters m1 and m3.");

        // 3) CastCircle with skill_misslenum_v scaling on Skill 1368 (Lv1=4 vs Lv20=9 missiles)
        await harness.StepFramesAsync(5);
        int baselineActive = worker.ActiveMissileCount;
        state.SetLearnedSkill(1368, 1);
        handle.CastSkill(1368, 52160, 101696);
        await harness.StepFramesAsync(1);
        int lv1Spawned = worker.ActiveMissileCount - baselineActive;
        await harness.StepFramesAsync(30);

        int baselineBefore20 = worker.ActiveMissileCount;
        state.SetLearnedSkill(1368, 20);
        state.SetSkillCooldown(1368, 0);
        handle.CastSkill(1368, 52160, 101696);
        await harness.StepFramesAsync(1);
        int lv20Spawned = worker.ActiveMissileCount - baselineBefore20;
        Assert.Equal(4, lv1Spawned);
        Assert.Equal(9, lv20Spawned);
        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 1368u);
    }

    [Fact]
    public async Task TangMenTrapsAndMultiLayerSubSkills_WithThreeMonsters_ShouldTriggerEventsWithoutExtra0x85()
    {
        MapDescriptor map = CreateArenaMap(
            (52096, 101696, ElementSeries.Earth, "Quái Bẫy 1"),
            (52128, 101696, ElementSeries.Wood, "Quái Bẫy 2"),
            (52064, 101696, ElementSeries.Fire, "Quái Bẫy 3"));

        // Clone 696 with ClientSendMode=0 and damaging ChildSkillId=301 to test BaseSkill=false CastChildSkill
        SkillTemplate real696 = RequireSkill(SharedGameAssetsFixture.Default.SkillTemplateCatalog, 696);
        SkillTemplate childCastSkill = real696 with
        {
            Id = 9902,
            Code = "syn-childskill-cast",
            ClientSendMode = 0,
            ChildSkillId = 301,
            ChildSkillLevel = 10,
            ChildSkillCount = 4,
            EventSkills = null,
        };
        var overlay = new OverlaySkillTemplateCatalog(SharedGameAssetsFixture.Default.SkillTemplateCatalog, childCastSkill);

        var harness = CreateHarness(42, map, overlay);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "TrapAndSubSkillTester",
                ElementSeries.Wood,
                52096,
                101696,
                skills:
                [
                    new CharacterSkillDto(303, 10, 0),
                    new CharacterSkillDto(351, 10, 0),
                    new CharacterSkillDto(1110, 10, 0),
                    new CharacterSkillDto(1648, 27, 0),
                    new CharacterSkillDto(696, 1, 0),
                    new CharacterSkillDto(9902, 1, 0),
                ]));

        PlayerZoneState state = handle.GetZoneState()!;
        state.FightMode = 1;
        state.BonusAttackRating = 50_000;
        CreateAndEquipWeapon(state, 1u, minDamage: 120, maxDamage: 120);

        NpcZoneState m1 = worker.StaticNpcs[1u];
        NpcZoneState m2 = worker.StaticNpcs[2u];
        NpcZoneState m3 = worker.StaticNpcs[3u];

        // 1) Đường Môn trap 303 -> CollideEvent 304 (atTarget x4 randomOrder) hits all 3 monsters
        handle.Recorder.Clear();
        handle.CastSkill(303, 52096, 101696);
        await harness.StepFramesAsync(30);
        Assert.Equal(1, handle.Recorder.SkillCastBroadcasts.Count(e => !e.CastDirectly && e.SkillId == 303u));
        Assert.DoesNotContain(handle.Recorder.SkillCastBroadcasts, e => e.CastDirectly);
        Assert.True(m1.CurLife < 10_000u && m2.CurLife < 10_000u && m3.CurLife < 10_000u, "Trap 303 -> 304 should damage all 3 monsters.");

        // 2) 1110 (Parabola M374 -> VanishEvent 1113 trap -> CollideEvent 352)
        ConfigurePassiveMonsters(worker);
        state.CurrentMpsX = 52000;
        state.CurrentMpsY = 101696;
        handle.Recorder.Clear();
        handle.CastSkill(1110, 52096, 101696);
        await harness.StepFramesAsync(45);
        Assert.Equal(1, handle.Recorder.SkillCastBroadcasts.Count(e => !e.CastDirectly && e.SkillId == 1110u));
        Assert.DoesNotContain(handle.Recorder.SkillCastBroadcasts, e => e.CastDirectly);
        Assert.True(m1.CurLife < 10_000u && m2.CurLife < 10_000u && m3.CurLife < 10_000u, "1110 -> 1113 -> 352 chain should damage all 3 monsters.");

        // 3) 1648 (M553 Height=500 > 20 never collides directly!) -> OnStart=1649 (ByMissile=true) + OnVanish=1650 (frame 14)
        ConfigurePassiveMonsters(worker);
        m1.MpsX = 52224; m1.MpsY = 101696;
        m2.MpsX = 52256; m2.MpsY = 101696;
        m3.MpsX = 52192; m3.MpsY = 101696;
        handle.Recorder.Clear();
        handle.CastSkill(1648, 52224, 101696);
        await harness.StepFramesAsync(30);
        Assert.Equal(1, handle.Recorder.SkillCastBroadcasts.Count(e => !e.CastDirectly && e.SkillId == 1648u));
        Assert.DoesNotContain(handle.Recorder.SkillCastBroadcasts, e => e.CastDirectly);
        Assert.True(m1.CurLife < 10_000u && m2.CurLife < 10_000u && m3.CurLife < 10_000u, "1648 -> 1649/1650 should damage all 3 monsters.");

        // 4) ClientSendMode=2 (696) emits 0x5A and spawns 0 server missiles; BaseSkill=false (9902) CastChildSkill spawns ChildSkillId=301 without 0x85
        await harness.StepFramesAsync(60);
        int activeBefore696 = worker.ActiveMissileCount;
        int pendingBefore696 = worker.MissileSystem.PendingSpawnCount;
        handle.Recorder.Clear();
        handle.CastSkill(696, 52224, 101696);
        await harness.StepFramesAsync(1);
        Assert.Equal(1, handle.Recorder.SkillCastBroadcasts.Count(e => !e.CastDirectly && e.SkillId == 696u));
        Assert.Equal(activeBefore696, worker.ActiveMissileCount);
        Assert.Equal(pendingBefore696, worker.MissileSystem.PendingSpawnCount);

        ConfigurePassiveMonsters(worker);
        state.CurrentMpsX = 52224;
        state.CurrentMpsY = 101760;
        m1.MpsX = 52224; m1.MpsY = 101760;
        m2.MpsX = 52256; m2.MpsY = 101760;
        m3.MpsX = 52192; m3.MpsY = 101760;
        handle.Recorder.Clear();
        handle.CastSkill(9902, 52224, 101760);
        await harness.StepFramesAsync(15);
        Assert.Equal(1, handle.Recorder.SkillCastBroadcasts.Count(e => !e.CastDirectly && e.SkillId == 9902u));
        Assert.DoesNotContain(handle.Recorder.SkillCastBroadcasts, e => e.CastDirectly);
        Assert.True(m1.CurLife < 10_000u && m2.CurLife < 10_000u && m3.CurLife < 10_000u, "BaseSkill=false CastChildSkill 9902 -> 301 should hit all 3 monsters.");
    }

    [Fact]
    public async Task TrajectoriesRollBackParabolaCircleHelixAndGroundFollow_WithThreeMonsters_ShouldHitExpectedTargets()
    {
        MapDescriptor map = CreateArenaMap(
            (52096, 101696, ElementSeries.Wood, "Quái A"),
            (52160, 101696, ElementSeries.Fire, "Quái B"),
            (52224, 101696, ElementSeries.Metal, "Quái C"));

        var harness = CreateHarness(42, map, includeMapObstacles: false);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "TrajectoryTester",
                ElementSeries.Wood,
                52160,
                101696,
                skills:
                [
                    new CharacterSkillDto(341, 10, 0),
                    new CharacterSkillDto(45, 10, 0),
                    new CharacterSkillDto(384, 10, 0),
                    new CharacterSkillDto(357, 20, 0),
                    new CharacterSkillDto(1368, 20, 0),
                ]));

        PlayerZoneState state = handle.GetZoneState()!;
        state.FightMode = 1;
        state.BonusAttackRating = 50_000;
        CreateAndEquipWeapon(state, 1u, minDamage: 120, maxDamage: 120);

        NpcZoneState m1 = worker.StaticNpcs[1u];
        NpcZoneState m2 = worker.StaticNpcs[2u];
        NpcZoneState m3 = worker.StaticNpcs[3u];

        // 1) RollBack boomerang 341 (Life=32, Speed=30): flies East 16 frames (+480 mps), then reverses West 16 frames back to caster
        // Place m1 off-path during outward pass, then step m1 into return path at (52192, 101696) after blades have passed East
        m1.MpsX = 52192; m1.MpsY = 101850;
        m2.MpsX = 52500; m2.MpsY = 101696;
        m3.MpsX = 52564; m3.MpsY = 101696;
        handle.CastSkill(341, 52400, 101696);
        await harness.StepFramesAsync(10);
        Assert.Equal(10_000u, m1.CurLife); // Untouched during outward flight
        m1.MpsX = 52192; m1.MpsY = 101696; // Steps onto return trajectory
        await harness.StepFramesAsync(28);
        Assert.True(m1.CurLife < 10_000u, "RollBack 341 should reverse at half-life and hit m1 on return trip.");

        // 2) Parabola 45 (M35: Speed=16, Z=10, ZAccel=5120): cast from 52000 to target 52192 (dist=192)
        // Airborne Z > 20 over m1 (52064) mid-flight, lands at 52192 to hit m2!
        ConfigurePassiveMonsters(worker);
        state.CurrentMpsX = 52000; state.CurrentMpsY = 101696;
        m1.MpsX = 52064; m1.MpsY = 101696;
        m2.MpsX = 52192; m2.MpsY = 101696;
        m3.MpsX = 52224; m3.MpsY = 101696;
        handle.CastSkill(45, 52192, 101696);
        await harness.StepFramesAsync(20);
        Assert.Equal(10_000u, m1.CurLife); // Arced high over m1 (Z > 20)
        Assert.True(m2.CurLife < 10_000u, "Parabola 45 should descend and hit m2 at target.");

        // 3) Circle/Helix 384 (M190, p1=1, p3=64, Life=24) orbits around caster (NOT radius=1 mps!) and hits monsters at ~64..80 mps
        ConfigurePassiveMonsters(worker);
        state.CurrentMpsX = 52160; state.CurrentMpsY = 101696;
        m1.MpsX = 52160; m1.MpsY = 101760; // 64 South
        m2.MpsX = 52224; m2.MpsY = 101696; // 64 East
        m3.MpsX = 52096; m3.MpsY = 101696; // 64 West
        handle.CastSkill(384, 52160, 101696);
        await harness.StepFramesAsync(28);
        int orbitHits = (m1.CurLife < 10_000u ? 1 : 0) + (m2.CurLife < 10_000u ? 1 : 0) + (m3.CurLife < 10_000u ? 1 : 0);
        Assert.True(orbitHits >= 2, $"Orbit/Circle 384 (M190) should sweep ~70 mps radius and hit multiple monsters (hits={orbitHits}).");

        // 4) Ground-aim Follow: Skill 1368 (circle M418 Follow) cast at ground coordinates (no targetNpcId)
        ConfigurePassiveMonsters(worker);
        state.CurrentMpsX = 52000; state.CurrentMpsY = 101696;
        m1.MpsX = 52000; m1.MpsY = 101536; // North (160 mps)
        m2.MpsX = 52160; m2.MpsY = 101696; // East (160 mps)
        m3.MpsX = 52000; m3.MpsY = 101856; // South (160 mps)
        handle.CastSkill(1368, 52160, 101696);
        await harness.StepFramesAsync(25);
        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 1368u);
        Assert.True(m1.CurLife < 10_000u, "Ground-aim Follow 1368 should acquire and hit m1.");
        Assert.True(m2.CurLife < 10_000u, "Ground-aim Follow 1368 should acquire and hit m2.");
        Assert.True(m3.CurLife < 10_000u, "Ground-aim Follow 1368 should acquire and hit m3.");
    }

    [Fact]
    public async Task Style1MeleeFormsStyle4SummonStyle13StealStyle14InstantAndCanDetonate_WithThreeMonsters_ShouldDispatchAll()
    {
        MapDescriptor map = CreateArenaMap(
            (52096, 101696, ElementSeries.Wood, "Quái 1"),
            (52128, 101696, ElementSeries.Fire, "Quái 2"),
            (52064, 101696, ElementSeries.Earth, "Quái 3"));

        SkillTemplate real1566 = RequireSkill(SharedGameAssetsFixture.Default.SkillTemplateCatalog, 1566);
        SkillTemplate summonWith179 = real1566 with
        {
            Id = 9903,
            Code = "syn-summon-179",
            ChildSkillCount = 3,
            EffectScaling =
            [
                new SkillEffectScalingTemplate(
                    179,
                    "createnpc",
                    "state",
                    new SkillParameterCurveTemplate("Linear", 1, 0),
                    new SkillParameterCurveTemplate("Linear", 10, 0),
                    new SkillParameterCurveTemplate("Linear", 180, 0)),
            ],
        };
        SkillTemplate real723 = RequireSkill(SharedGameAssetsFixture.Default.SkillTemplateCatalog, 723);
        SkillTemplate instantDmg723 = real723 with
        {
            Id = 9904,
            Code = "syn-instant-dmg",
            EffectScaling =
            [
                new SkillEffectScalingTemplate(
                    62,
                    "lightingdamage_v",
                    "damage",
                    new SkillParameterCurveTemplate("Linear", 200, 0),
                    null,
                    new SkillParameterCurveTemplate("Linear", 200, 0)),
            ],
        };

        var overlay = new OverlaySkillTemplateCatalog(
            SharedGameAssetsFixture.Default.SkillTemplateCatalog,
            summonWith179,
            instantDmg723);

        var harness = CreateHarness(42, map, overlay);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "ArchetypeTester",
                ElementSeries.Metal,
                52032,
                101696,
                skills:
                [
                    new CharacterSkillDto(10, 10, 0),
                    new CharacterSkillDto(38, 10, 0),
                    new CharacterSkillDto(40, 10, 0),
                    new CharacterSkillDto(210, 10, 0),
                    new CharacterSkillDto(318, 10, 0),
                    new CharacterSkillDto(710, 20, 0),
                    new CharacterSkillDto(400, 10, 0),
                    new CharacterSkillDto(721, 10, 0),
                    new CharacterSkillDto(9904, 1, 0),
                    new CharacterSkillDto(1566, 1, 0),
                    new CharacterSkillDto(9903, 1, 0),
                    new CharacterSkillDto(1380, 10, 0),
                    new CharacterSkillDto(1384, 20, 0),
                ]));

        PlayerZoneState state = handle.GetZoneState()!;
        state.FightMode = 1;
        state.BonusAttackRating = 50_000;
        CreateAndEquipWeapon(state, 1u, minDamage: 120, maxDamage: 120);

        NpcZoneState m1 = worker.StaticNpcs[1u];
        NpcZoneState m2 = worker.StaticNpcs[2u];
        NpcZoneState m3 = worker.StaticNpcs[3u];

        // 1) Style 1 meleeMovePos (710): blink up to skill_param1_v (400 at Lv20) -> SelfPositionSyncs + AOI NpcPosSyncBroadcasts
        SimulatedPlayerHandle observerHandle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto("MovePosObserver", ElementSeries.Water, 52064, 101696, skills: []));
        observerHandle.Recorder.Clear();
        handle.Recorder.Clear();
        handle.CastSkill(710, 52256, 101696);
        await harness.StepFramesAsync(5);
        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 710u);
        Assert.True(state.CurrentMpsX > 52100, "Skill 710 meleeMovePos should blink player toward target.");
        Assert.NotEmpty(handle.Recorder.SelfPositionSyncs);
        Assert.Contains(observerHandle.Recorder.NpcPosSyncBroadcasts, e => e.NpcId == state.RuntimeNpcId && e.MpsX == state.CurrentMpsX);

        // 2) Style 1 meleeJump (210): jump to coordinate -> JumpBroadcasts + SelfPositionSyncs
        handle.Recorder.Clear();
        handle.CastSkill(210, 52032, 101696);
        await harness.StepFramesAsync(3);
        Assert.NotEmpty(handle.Recorder.JumpBroadcasts);
        Assert.NotEmpty(handle.Recorder.SelfPositionSyncs);

        // 3) Style 1 meleeBlur (10), meleeJumpAttack (38) & meleeRunAttack (40): dash/jump + ChildSkill -> movement broadcasts + damage
        ConfigurePassiveMonsters(worker);
        state.CurrentMpsX = 52032; state.CurrentMpsY = 101696;
        m1.MpsX = 52096; m1.MpsY = 101696;
        m3.MpsX = 52096; m3.MpsY = 101760;
        handle.Recorder.Clear();
        handle.CastSkill(10, -1, (int)m1.NpcId);
        await harness.StepFramesAsync(6);
        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 10u);
        Assert.True(m1.CurLife < 10_000u, "Skill 10 meleeBlur should damage m1 via ChildSkill 216.");

        ConfigurePassiveMonsters(worker);
        state.CurrentMpsX = 52032; state.CurrentMpsY = 101696;
        handle.Recorder.Clear();
        handle.CastSkill(38, -1, (int)m1.NpcId);
        await harness.StepFramesAsync(6);
        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 38u);
        Assert.NotEmpty(handle.Recorder.JumpBroadcasts);
        Assert.True(m1.CurLife < 10_000u, "Skill 38 meleeJumpAttack should jump and damage m1 via ChildSkill 223.");

        ConfigurePassiveMonsters(worker);
        state.CurrentMpsX = 52000; state.CurrentMpsY = 101696;
        handle.Recorder.Clear();
        handle.CastSkill(40, -1, (int)m1.NpcId);
        await harness.StepFramesAsync(15);
        Assert.NotEmpty(handle.Recorder.RunBroadcasts);
        Assert.True(m1.CurLife < 10_000u, "Skill 40 meleeRunAttack should dash and hit via ChildSkill 224.");

        // 4) Style 1 meleeMany (318 -> ChildSkill 317 x2): hits twice!
        ConfigurePassiveMonsters(worker);
        state.CurrentMpsX = 52064; state.CurrentMpsY = 101696;
        m1.MpsX = 52096; m1.MpsY = 101696;
        handle.CastSkill(318, -1, (int)m1.NpcId);
        await harness.StepFramesAsync(4);
        uint dmgAfter1stStrike = 10_000u - m1.CurLife;
        await harness.StepFramesAsync(12);
        uint dmgAfter2ndStrike = 10_000u - m1.CurLife;
        Assert.True(dmgAfter2ndStrike > dmgAfter1stStrike && dmgAfter1stStrike > 0, "Skill 318 meleeMany should deliver 2 staggered ChildSkill 317 strikes.");

        // 5) Style 4 SummonNpc: real 1566 (no attrib 179) is safe no-op; 9903 (with 179) caps at 3 partners and despawns after CreateNpcTimeoutFrames=180
        int baseNpcCount = worker.StaticNpcs.Count;
        ConfigurePassiveMonsters(worker);
        handle.CastSkill(1566, state.CurrentMpsX, state.CurrentMpsY);
        await harness.StepFramesAsync(4);
        Assert.Equal(baseNpcCount, worker.StaticNpcs.Count);
        Assert.Equal(10_000u, m1.CurLife);

        for (int sIdx = 0; sIdx < 5; sIdx++)
        {
            state.SetSkillCooldown(9903, 0);
            handle.CastSkill(9903, state.CurrentMpsX, state.CurrentMpsY);
            await harness.StepFramesAsync(1);
        }
        Assert.Equal(baseNpcCount + 3, worker.StaticNpcs.Count);

        await harness.StepFramesAsync(190);
        Assert.Equal(baseNpcCount, worker.StaticNpcs.Count);

        // 6) Style 13 Steal (400 on hostile player -> ChildSkill 197 hits!) & Style 14 InstantMissile (721 + 9904)
        SimulatedPlayerHandle stealVictimHandle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "StealVictim",
                ElementSeries.Wood,
                state.CurrentMpsX - 64,
                state.CurrentMpsY,
                skills: []));
        PlayerZoneState stealVictim = stealVictimHandle.GetZoneState()!;
        stealVictim.BaseCamp = EntityCamp.Evil;
        stealVictim.CurrentCamp = EntityCamp.Evil;
        stealVictim.FightMode = 1;
        stealVictim.PkState = PlayerPkMode.Fight;
        stealVictim.SpawnProtectUntilTick = 0;
        state.BaseCamp = EntityCamp.Justice;
        state.CurrentCamp = EntityCamp.Justice;
        state.PkState = PlayerPkMode.Fight;
        int victimLifeBefore400 = stealVictim.CurrentLife;
        handle.Recorder.Clear();
        handle.CastSkill(400, -1, (int)stealVictim.RuntimeNpcId);
        await harness.StepFramesAsync(8);
        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 400u);
        Assert.True(stealVictim.CurrentLife < victimLifeBefore400, "Skill 400 Steal on hostile player should fire ChildSkill 197 and deal damage.");

        handle.CastSkill(721, state.CurrentMpsX, state.CurrentMpsY);
        await harness.StepFramesAsync(1);
        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 721u);

        ConfigurePassiveMonsters(worker);
        m1.MpsX = 52096; m1.MpsY = 101696;
        m2.MpsX = 52128; m2.MpsY = 101696;
        m3.MpsX = 52064; m3.MpsY = 101696;
        handle.CastSkill(9904, state.CurrentMpsX, state.CurrentMpsY);
        await harness.StepFramesAsync(1);
        Assert.Equal(0, worker.ActiveMissileCount);
        Assert.True(m1.CurLife < 10_000u && m2.CurLife < 10_000u && m3.CurLife < 10_000u, "Style 14 InstantMissile should hit all 3 monsters immediately and vanish.");

        // 7) Hua Shan CanDetonate (1380 spawns M419/M428; 1384 detonates active M419 into 1411 ONCE -> 4 M420 missiles, not 8)
        ConfigurePassiveMonsters(worker);
        state.CurrentMpsX = 52000; state.CurrentMpsY = 101696;
        m1.MpsX = 52224; m1.MpsY = 101696;
        m2.MpsX = 52256; m2.MpsY = 101696;
        m3.MpsX = 52192; m3.MpsY = 101696;
        handle.CastSkill(1380, 52224, 101696);
        await harness.StepFramesAsync(8);
        Assert.Equal(2, worker.ActiveMissileCount);

        ConfigurePassiveMonsters(worker);
        handle.CastSkill(1384, state.CurrentMpsX, state.CurrentMpsY);
        await harness.StepFramesAsync(1);
        int m420Active = 0;
        for (int slotIdx = 0; slotIdx < worker.MissileSystem.PoolCapacity; slotIdx++)
        {
            var slot = worker.MissileSystem.GetSlot(slotIdx);
            if (slot.IsActive && slot.MissileTemplateId == 420)
            {
                m420Active++;
            }
        }
        Assert.Equal(4, m420Active + worker.MissileSystem.PendingSpawnCount);
        await harness.StepFramesAsync(6);
        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 1384u);
        Assert.True(m1.CurLife < 10_000u && m2.CurLife < 10_000u && m3.CurLife < 10_000u, "CanDetonate 1384 should detonate active M419 into 1411 AoE hitting all 3 distant monsters.");
    }

    [Fact]
    public async Task SkillCooldownsAndSpeedScaling_WithMountedMinCastAttackSpeedCastSpeedReduceCdAndClearAllCd_ShouldEnforceExactReadyTicks()
    {
        MapDescriptor map = CreateArenaMap(
            (52096, 101696, ElementSeries.Wood, "Quái CD 1"),
            (52128, 101696, ElementSeries.Fire, "Quái CD 2"),
            (52160, 101696, ElementSeries.Metal, "Quái CD 3"));

        SkillTemplate real88 = RequireSkill(SharedGameAssetsFixture.Default.SkillTemplateCatalog, 88);
        SkillTemplate physWait10 = real88 with { Id = 9905, Code = "syn-phys-wait10", IsPhysical = true, WaitFrames = 10, MinFramesPerCast = 0, MinFramesPerCastOnHorse = 0 };
        SkillTemplate magicWait10 = real88 with { Id = 9906, Code = "syn-magic-wait10", IsPhysical = false, WaitFrames = 10, MinFramesPerCast = 0, MinFramesPerCastOnHorse = 0 };
        var overlay = new OverlaySkillTemplateCatalog(SharedGameAssetsFixture.Default.SkillTemplateCatalog, physWait10, magicWait10);

        var harness = CreateHarness(42, map, overlay);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "CooldownTester",
                ElementSeries.Metal,
                52032,
                101696,
                skills:
                [
                    new CharacterSkillDto(710, 20, 0),
                    new CharacterSkillDto(1380, 10, 0),
                    new CharacterSkillDto(9905, 1, 0),
                    new CharacterSkillDto(9906, 1, 0),
                ]));

        PlayerZoneState state = handle.GetZoneState()!;
        state.FightMode = 1;

        // 1) Skill 710 Lv20: skill_mintimepercast_v = 90 (unmounted) vs skill_mintimepercastonhorse_v = 144 (mounted)
        handle.Recorder.Clear();
        handle.CastSkill(710, 52064, 101696);
        await harness.StepFramesAsync(1);
        Assert.Equal(1, handle.Recorder.SkillCastBroadcasts.Count(e => !e.CastDirectly && e.SkillId == 710u));

        // Before 90 frames: rejected
        await harness.StepFramesAsync(50);
        handle.CastSkill(710, 52064, 101696);
        await harness.StepFramesAsync(1);
        Assert.Equal(1, handle.Recorder.SkillCastBroadcasts.Count(e => !e.CastDirectly && e.SkillId == 710u));

        // At >= 90 frames: succeeds
        await harness.StepFramesAsync(42);
        state.HorseResType = 1; // Mounted -> next cooldown is 144 frames!
        handle.CastSkill(710, 52064, 101696);
        await harness.StepFramesAsync(1);
        Assert.Equal(2, handle.Recorder.SkillCastBroadcasts.Count(e => !e.CastDirectly && e.SkillId == 710u));

        await harness.StepFramesAsync(100);
        handle.CastSkill(710, 52064, 101696);
        await harness.StepFramesAsync(1);
        Assert.Equal(2, handle.Recorder.SkillCastBroadcasts.Count(e => !e.CastDirectly && e.SkillId == 710u));

        await harness.StepFramesAsync(46);
        handle.CastSkill(710, 52064, 101696);
        await harness.StepFramesAsync(1);
        Assert.Equal(3, handle.Recorder.SkillCastBroadcasts.Count(e => !e.CastDirectly && e.SkillId == 710u));

        // 2) attackspeed_v (+100) halves WaitFrames=10 -> 5 for physical 9905; castspeed_v (+100) halves WaitFrames=10 -> 5 for magic 9906;
        //    reduceskillcd1 (341) reduces Skill 1380 (MinFramesPerCast=63) by 30 frames -> ready at 33 frames
        CreateAndEquipArmor(
            state,
            1u,
            new ItemMagicAttributeSlot(0, 341, 1380, 0, 30, 0),
            new ItemMagicAttributeSlot(2, 115, 100, 0, 0, 0),
            new ItemMagicAttributeSlot(4, 116, 100, 0, 0, 0));

        handle.Recorder.Clear();
        handle.CastSkill(9905, 52096, 101696);
        handle.CastSkill(9906, 52096, 101696);
        await harness.StepFramesAsync(1);
        Assert.Equal(1, handle.Recorder.SkillCastBroadcasts.Count(e => !e.CastDirectly && e.SkillId == 9905u));
        Assert.Equal(1, handle.Recorder.SkillCastBroadcasts.Count(e => !e.CastDirectly && e.SkillId == 9906u));

        // At +6 frames (> 5 adjusted, < 10 base): both physical & magic can re-cast!
        await harness.StepFramesAsync(6);
        handle.CastSkill(9905, 52096, 101696);
        handle.CastSkill(9906, 52096, 101696);
        await harness.StepFramesAsync(1);
        Assert.Equal(2, handle.Recorder.SkillCastBroadcasts.Count(e => !e.CastDirectly && e.SkillId == 9905u));
        Assert.Equal(2, handle.Recorder.SkillCastBroadcasts.Count(e => !e.CastDirectly && e.SkillId == 9906u));

        handle.Recorder.Clear();
        handle.CastSkill(1380, 52096, 101696);
        await harness.StepFramesAsync(1);
        Assert.Equal(1, handle.Recorder.SkillCastBroadcasts.Count(e => !e.CastDirectly && e.SkillId == 1380u));

        // At 20 frames (< 33): still on cooldown
        await harness.StepFramesAsync(20);
        handle.CastSkill(1380, 52096, 101696);
        await harness.StepFramesAsync(1);
        Assert.Equal(1, handle.Recorder.SkillCastBroadcasts.Count(e => !e.CastDirectly && e.SkillId == 1380u));

        // At 35 frames (>= 33, < 63 base): cooldown reduced by 30 frames so cast succeeds!
        await harness.StepFramesAsync(15);
        handle.CastSkill(1380, 52096, 101696);
        await harness.StepFramesAsync(1);
        Assert.Equal(2, handle.Recorder.SkillCastBroadcasts.Count(e => !e.CastDirectly && e.SkillId == 1380u));

        // 3) clearallcd (344, 100%) clears existing skill cooldowns (e.g. 1380) when another skill (9905) is cast
        CreateAndEquipArmor(state, 2u, new ItemMagicAttributeSlot(0, 344, 100, 0, 0, 0));
        await harness.StepFramesAsync(10);
        handle.CastSkill(9905, 52096, 101696);
        await harness.StepFramesAsync(1);
        handle.CastSkill(1380, 52096, 101696);
        await harness.StepFramesAsync(1);
        Assert.Equal(3, handle.Recorder.SkillCastBroadcasts.Count(e => !e.CastDirectly && e.SkillId == 1380u));
    }

    [Fact]
    public async Task IsMeleeLineAoEAndPiercing_WithThreeMonsters_ShouldSpawnMissilesWhileTargetOnlyRemainsSingleTarget()
    {
        MapDescriptor map = CreateArenaMap(
            (52064, 101696, ElementSeries.Wood, "Quái Line 1"),
            (52096, 101696, ElementSeries.Fire, "Quái Line 2"),
            (52128, 101696, ElementSeries.Earth, "Quái Line 3"));

        var harness = CreateHarness(42, map);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "MeleeLineTester",
                ElementSeries.Metal,
                52032,
                101696,
                skills:
                [
                    new CharacterSkillDto(1055, 10, 0),
                    new CharacterSkillDto(361, 10, 0),
                    new CharacterSkillDto(40, 10, 0),
                    new CharacterSkillDto(53, 1, 0),
                    new CharacterSkillDto(1, 1, 0),
                ]));

        PlayerZoneState state = handle.GetZoneState()!;
        state.FightMode = 1;
        state.BonusAttackRating = 50_000;
        CreateAndEquipWeapon(state, 1u, minDamage: 120, maxDamage: 120);

        NpcZoneState m1 = worker.StaticNpcs[1u];
        NpcZoneState m2 = worker.StaticNpcs[2u];
        NpcZoneState m3 = worker.StaticNpcs[3u];

        // 1) Skill 1055 -> 1083 (M345 IsRangeDamage=true, DmgCells=3=96 mps, Pierce=0) hits all 3 monsters
        handle.CastSkill(1055, -1, (int)m1.NpcId);
        await harness.StepFramesAsync(14);
        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 1055u);
        Assert.True(m1.CurLife < 10_000u && m2.CurLife < 10_000u && m3.CurLife < 10_000u, "1055 -> 1083 (M345) should spawn AoE missiles and hit all 3 monsters.");

        // 2) Skill 361 (M169 Pierce=0, ColVanish=false) pierces through all 3 lined-up monsters
        ConfigurePassiveMonsters(worker);
        state.CurrentMpsX = 52032; state.CurrentMpsY = 101696;
        handle.CastSkill(361, -1, (int)m1.NpcId);
        await harness.StepFramesAsync(10);
        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 361u);
        Assert.True(m1.CurLife < 10_000u && m2.CurLife < 10_000u && m3.CurLife < 10_000u, "Skill 361 (M169) should pierce and hit all 3 lined-up monsters.");

        // 3) Skill 40 -> 224 (M284 IsRangeDamage=true, DmgCells=2=64 mps) splashes across 3 clustered monsters
        ConfigurePassiveMonsters(worker);
        state.CurrentMpsX = 52000; state.CurrentMpsY = 101696;
        m1.MpsX = 52096; m1.MpsY = 101696;
        m2.MpsX = 52128; m2.MpsY = 101696;
        m3.MpsX = 52096; m3.MpsY = 101728;
        handle.CastSkill(40, -1, (int)m1.NpcId);
        await harness.StepFramesAsync(15);
        await harness.StepFramesAsync(15); // clear MinFramesPerCast=27 so a second cast guards against the 5% MaxHitPercent=95 miss cap
        state.CurrentMpsX = 52000; state.CurrentMpsY = 101696;
        handle.CastSkill(40, -1, (int)m1.NpcId);
        await harness.StepFramesAsync(15);
        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 40u);
        Assert.True(m1.CurLife < 10_000u && m2.CurLife < 10_000u && m3.CurLife < 10_000u, $"40 -> 224 (M284) should splash across all 3 clustered monsters (m1={m1.CurLife}, m2={m2.CurLife}, m3={m3.CurLife}, pos=({state.CurrentMpsX},{state.CurrentMpsY})).");

        // 4) Skill 53 & Skill 1 (Targeting.TargetOnly == true) remain single-target even when 3 monsters are stacked
        ConfigurePassiveMonsters(worker);
        state.CurrentMpsX = 52032; state.CurrentMpsY = 101696;
        m1.MpsX = 52064; m1.MpsY = 101696;
        m2.MpsX = 52064; m2.MpsY = 101712;
        m3.MpsX = 52080; m3.MpsY = 101696;

        handle.CastSkill(53, -1, (int)m1.NpcId);
        await harness.StepFramesAsync(8);
        Assert.True(m1.CurLife < 10_000u, "Skill 53 should hit targeted m1.");
        Assert.Equal(10_000u, m2.CurLife);
        Assert.Equal(10_000u, m3.CurLife);

        ConfigurePassiveMonsters(worker);
        handle.CastSkill(1, -1, (int)m1.NpcId);
        await harness.StepFramesAsync(8);
        Assert.True(m1.CurLife < 10_000u, "Skill 1 should hit targeted m1.");
        Assert.Equal(10_000u, m2.CurLife);
        Assert.Equal(10_000u, m3.CurLife);
    }

    [Fact]
    public async Task InstantMissileRecursionCapAndProjectileOnStartChain_WithThreeMonsters_ShouldCapDepthAndResolveInstant()
    {
        MapDescriptor map = CreateArenaMap(
            (52096, 101696, ElementSeries.Wood, "Quái IM 1"),
            (52112, 101696, ElementSeries.Fire, "Quái IM 2"),
            (52128, 101696, ElementSeries.Earth, "Quái IM 3"));

        SkillTemplate real723 = RequireSkill(SharedGameAssetsFixture.Default.SkillTemplateCatalog, 723);
        var chainSkills = new List<SkillTemplate>();
        for (int i = 0; i < 10; i++)
        {
            int nextId = i < 9 ? 9910 + i + 1 : 0;
            chainSkills.Add(real723 with
            {
                Id = 9910 + i,
                Code = $"syn-im-chain-{i}",
                WaitFrames = 0,
                MinFramesPerCast = 0,
                MinFramesPerCastOnHorse = 0,
                MissileForm = "atTarget",
                EffectScaling =
                [
                    new SkillEffectScalingTemplate(
                        62,
                        "lightingdamage_v",
                        "damage",
                        new SkillParameterCurveTemplate("Linear", 100, 0),
                        null,
                        new SkillParameterCurveTemplate("Linear", 100, 0)),
                ],
                EventSkills = nextId > 0 ? new SkillEventSkillsTemplate(-1, nextId, 1, 0, 0, 0, 0, 0, 0, 0) : null,
            });
        }

        // Cyclic InstantMissile (9920 -> 9920)
        chainSkills.Add(real723 with
        {
            Id = 9920,
            Code = "syn-im-cycle",
            WaitFrames = 0,
            MinFramesPerCast = 0,
            MinFramesPerCastOnHorse = 0,
            MissileForm = "atTarget",
            EffectScaling =
            [
                new SkillEffectScalingTemplate(
                    62,
                    "lightingdamage_v",
                    "damage",
                    new SkillParameterCurveTemplate("Linear", 100, 0),
                    null,
                    new SkillParameterCurveTemplate("Linear", 100, 0)),
            ],
            EventSkills = new SkillEventSkillsTemplate(-1, 9920, 1, 0, 0, 0, 0, 0, 0, 0),
        });

        // Projectile (MissileId=0) -> OnStart -> InstantMissile (9904)
        SkillTemplate real88 = RequireSkill(SharedGameAssetsFixture.Default.SkillTemplateCatalog, 88);
        chainSkills.Add(real723 with
        {
            Id = 9904,
            Code = "syn-im-target",
            WaitFrames = 0,
            MinFramesPerCast = 0,
            MinFramesPerCastOnHorse = 0,
            MissileForm = "atTarget",
            EffectScaling =
            [
                new SkillEffectScalingTemplate(
                    62,
                    "lightingdamage_v",
                    "damage",
                    new SkillParameterCurveTemplate("Linear", 200, 0),
                    null,
                    new SkillParameterCurveTemplate("Linear", 200, 0)),
            ],
        });
        chainSkills.Add(real88 with
        {
            Id = 9921,
            Code = "syn-proj-to-im",
            Style = SkillStyleKind.Projectile,
            IsPhysical = false,
            IsMelee = false,
            MissileId = 0,
            MissileForm = "atTarget",
            WaitFrames = 0,
            MinFramesPerCast = 0,
            MinFramesPerCastOnHorse = 0,
            EffectScaling = [],
            EventSkills = new SkillEventSkillsTemplate(-1, 9904, 1, 0, 0, 0, 0, 0, 0, 0),
        });

        var overlay = new OverlaySkillTemplateCatalog(SharedGameAssetsFixture.Default.SkillTemplateCatalog, [.. chainSkills]);
        var harness = CreateHarness(42, map, overlay);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "InstantChainTester",
                ElementSeries.Metal,
                52032,
                101696,
                skills:
                [
                    new CharacterSkillDto(9910, 1, 0),
                    new CharacterSkillDto(9920, 1, 0),
                    new CharacterSkillDto(9921, 1, 0),
                ]));

        PlayerZoneState state = handle.GetZoneState()!;
        state.FightMode = 1;

        NpcZoneState m1 = worker.StaticNpcs[1u];
        NpcZoneState m2 = worker.StaticNpcs[2u];
        NpcZoneState m3 = worker.StaticNpcs[3u];

        // 1) 10-chain InstantMissile stops at MaxEventRecursionDepth = 4 (max 5 executions = 5 * (100 + Energy 100) = 1000 dmg, NOT 2000)
        handle.CastSkill(9910, 52096, 101696);
        await harness.StepFramesAsync(1);
        uint chainDmg = 10_000u - m1.CurLife;
        Assert.Equal(1000u, chainDmg);
        Assert.True(m2.CurLife < 10_000u && m3.CurLife < 10_000u, "InstantMissile chain should hit all 3 monsters.");

        // 2) Cyclic InstantMissile terminates cleanly without StackOverflowException
        ConfigurePassiveMonsters(worker);
        handle.CastSkill(9920, 52096, 101696);
        await harness.StepFramesAsync(1);
        Assert.Equal(1000u, 10_000u - m1.CurLife);

        // 3) Projectile -> OnStart -> InstantMissile resolves cleanly with 0 active slots
        ConfigurePassiveMonsters(worker);
        handle.Recorder.Clear();
        handle.CastSkill(9921, 52096, 101696);
        await harness.StepFramesAsync(1);
        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 9921u);
        Assert.Equal(0, worker.ActiveMissileCount);
        Assert.True(m1.CurLife < 10_000u && m2.CurLife < 10_000u && m3.CurLife < 10_000u, "Projectile -> OnStart -> InstantMissile should hit all 3 monsters.");
    }

    [Fact]
    public async Task CastChildSkillNonBaseForms_WithThreeMonsters_ShouldPreserveOriginAndTargetCoordinatesAcrossAllForms()
    {
        MapDescriptor map = CreateArenaMap(
            (52560, 101696, ElementSeries.Wood, "Quái Form 1"),
            (52400, 101856, ElementSeries.Fire, "Quái Form 2"),
            (52400, 101536, ElementSeries.Earth, "Quái Form 3"));

        SkillTemplate real88 = RequireSkill(SharedGameAssetsFixture.Default.SkillTemplateCatalog, 88);
        SkillTemplate real182 = RequireSkill(SharedGameAssetsFixture.Default.SkillTemplateCatalog, 182);

        // Child atFirer skill for CastCircle target-perimeter test (spawns M18 atFirer)
        SkillTemplate childAtFirer = real182 with
        {
            Id = 9930,
            Code = "syn-child-atfirer",
            IsBaseSkill = true,
            MissileForm = "atFirer",
            ChildSkillCount = 1,
            MissileGenerateStyle = "none",
            MissileGenerateDelayFrames = 0,
        };

        // CastCircle IsBaseSkill=false Param1=1 (target centered), Param2=160, 4 points around target
        SkillTemplate parentCircle = real88 with
        {
            Id = 9931,
            Code = "syn-parent-circle",
            IsBaseSkill = false,
            MissileId = 0,
            MissileForm = "circle",
            Param1 = 1,
            Param2 = 160,
            ChildSkillId = 9930,
            ChildSkillLevel = 10,
            ChildSkillCount = 4,
            CastRangeMps = 600,
            WaitFrames = 0,
            MinFramesPerCast = 0,
            MinFramesPerCastOnHorse = 0,
        };

        // Directional Line child skill (M169, Pierce=0, ColVanish=false, Speed=32, Life=6) for CastZone / CastWall / CastSpread / CastLine
        SkillTemplate childLine = real88 with
        {
            Id = 9932,
            Code = "syn-child-line",
            IsBaseSkill = true,
            IsPhysical = false,
            MissileId = 169,
            MissileForm = "line",
            ChildSkillCount = 1,
            WaitFrames = 0,
            EffectScaling =
            [
                new SkillEffectScalingTemplate(
                    62,
                    "lightingdamage_v",
                    "damage",
                    new SkillParameterCurveTemplate("Linear", 150, 0),
                    null,
                    new SkillParameterCurveTemplate("Linear", 150, 0)),
            ],
        };

        SkillTemplate parentZone = parentCircle with { Id = 9933, Code = "syn-parent-zone", MissileForm = "atTarget", Param1 = 0, Param2 = 0, ChildSkillId = 9932, ChildSkillCount = 2 };
        SkillTemplate parentWall = parentCircle with { Id = 9934, Code = "syn-parent-wall", MissileForm = "wall", Param1 = 32, Param2 = 0, ChildSkillId = 9932, ChildSkillCount = 3 };
        SkillTemplate parentSpread = parentCircle with { Id = 9935, Code = "syn-parent-spread", MissileForm = "spread", Param1 = 2, Param2 = 0, ChildSkillId = 9932, ChildSkillCount = 3 };
        SkillTemplate parentLine = parentCircle with { Id = 9936, Code = "syn-parent-line", MissileForm = "line", Param1 = 0, Param2 = 0, ChildSkillId = 9932, ChildSkillCount = 2 };

        // CastZone NxN (N=4) + Param1=1 circle clip (spawns 12 missiles instead of 16)
        SkillTemplate zoneCircleClip = real182 with
        {
            Id = 9937,
            Code = "syn-zone-clip-4x4",
            IsBaseSkill = true,
            MissileForm = "zone",
            Param1 = 1,
            ChildSkillCount = 4,
            MissileGenerateStyle = "none",
            MissileGenerateDelayFrames = 0,
            WaitFrames = 0,
        };

        var overlay = new OverlaySkillTemplateCatalog(
            SharedGameAssetsFixture.Default.SkillTemplateCatalog,
            childAtFirer,
            parentCircle,
            childLine,
            parentZone,
            parentWall,
            parentSpread,
            parentLine,
            zoneCircleClip);

        var harness = CreateHarness(42, map, overlay, includeMapObstacles: false);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "ChildFormTester",
                ElementSeries.Fire,
                52000,
                101696,
                skills:
                [
                    new CharacterSkillDto(9931, 1, 0),
                    new CharacterSkillDto(9933, 1, 0),
                    new CharacterSkillDto(9934, 1, 0),
                    new CharacterSkillDto(9935, 1, 0),
                    new CharacterSkillDto(9936, 1, 0),
                    new CharacterSkillDto(9937, 1, 0),
                ]));

        PlayerZoneState state = handle.GetZoneState()!;
        state.FightMode = 1;

        NpcZoneState m1 = worker.StaticNpcs[1u];
        NpcZoneState m2 = worker.StaticNpcs[2u];
        NpcZoneState m3 = worker.StaticNpcs[3u];

        // 1) CastCircle BaseSkill=false Param1=1 Param2=160 -> spawns at target circle (52400, 101696) hitting all 3 perimeter monsters
        handle.CastSkill(9931, 52400, 101696);
        await harness.StepFramesAsync(3);
        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 9931u);
        Assert.True(m1.CurLife < 10_000u && m2.CurLife < 10_000u && m3.CurLife < 10_000u, "CastCircle IsBaseSkill=false should spawn child skills on target perimeter.");

        // 2) CastZone / CastWall / CastSpread / CastLine IsBaseSkill=false shooting North (origin=(52000,101696) -> target=(52000,101504))
        // Place 3 monsters North of caster (so South dir=0 fallback would miss completely)
        await harness.StepFramesAsync(10);
        uint[] northIds = [9933u, 9934u, 9935u, 9936u];
        foreach (uint sid in northIds)
        {
            ConfigurePassiveMonsters(worker);
            m1.MpsX = 52000; m1.MpsY = 101568;
            m2.MpsX = 51968; m2.MpsY = 101536;
            m3.MpsX = 52032; m3.MpsY = 101536;
            state.SetSkillCooldown((int)sid, 0);
            handle.CastSkill((ushort)sid, 52000, 101504);
            await harness.StepFramesAsync(12);
            if (sid == 9933u || sid == 9934u || sid == 9935u)
            {
                Assert.True(m1.CurLife < 10_000u && m2.CurLife < 10_000u && m3.CurLife < 10_000u, $"IsBaseSkill=false skill {sid} should preserve North direction and hit all 3 monsters.");
            }
            else
            {
                Assert.True(m1.CurLife < 10_000u, "IsBaseSkill=false CastLine 9936 should fire North toward target and hit m1.");
            }
        }

        // 3) CastZone 4x4 with Param1==1 circle clip spawns 12 missiles (not 16) and hits 3 monsters around caster
        await harness.StepFramesAsync(30);
        ConfigurePassiveMonsters(worker);
        m1.MpsX = 52400; m1.MpsY = 101696;
        m2.MpsX = 52432; m2.MpsY = 101696;
        m3.MpsX = 52464; m3.MpsY = 101696;
        int beforeClip = worker.ActiveMissileCount;
        handle.CastSkill(9937, 52000, 101696);
        await harness.StepFramesAsync(1);
        Assert.Equal(12, worker.ActiveMissileCount - beforeClip);

        await harness.StepFramesAsync(30);
        m1.MpsX = 52000; m1.MpsY = 101696;
        m2.MpsX = 52032; m2.MpsY = 101696;
        m3.MpsX = 51968; m3.MpsY = 101696;
        handle.CastSkill(9937, 52000, 101696);
        await harness.StepFramesAsync(3);
        Assert.True(m1.CurLife < 10_000u && m2.CurLife < 10_000u && m3.CurLife < 10_000u, "CastZone 4x4 circle clip should hit all 3 monsters.");
    }

    [Fact]
    public async Task CanDetonateEnemyRule_WithPeaceModeSameCampAlly_ShouldNotDetonateAllyMissiles()
    {
        MapDescriptor map = CreateArenaMap(
            (52224, 101696, ElementSeries.Wood, "Quái HS 1"),
            (52256, 101696, ElementSeries.Fire, "Quái HS 2"),
            (52192, 101696, ElementSeries.Earth, "Quái HS 3"));

        SkillTemplate real1384 = RequireSkill(SharedGameAssetsFixture.Default.SkillTemplateCatalog, 1384);
        SkillTemplate enemyOnlyDetonate = real1384 with
        {
            Id = 9940,
            Code = "syn-detonate-enemy-only",
            EffectScaling =
            [
                new SkillEffectScalingTemplate(
                    0,
                    "candetonate1",
                    "state",
                    new SkillParameterCurveTemplate("Linear", (419 << 8) | 1, 0),
                    null,
                    new SkillParameterCurveTemplate("Linear", 500, 0)),
            ],
        };

        var overlay = new OverlaySkillTemplateCatalog(SharedGameAssetsFixture.Default.SkillTemplateCatalog, enemyOnlyDetonate);
        var harness = CreateHarness(42, map, overlay);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle allyHandle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto("PeaceAlly", ElementSeries.Water, 52000, 101696, [new CharacterSkillDto(1380, 10, 0)]));
        SimulatedPlayerHandle casterHandle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto("HuaShanCaster", ElementSeries.Water, 52032, 101696, [new CharacterSkillDto(9940, 1, 0)]));

        PlayerZoneState ally = allyHandle.GetZoneState()!;
        PlayerZoneState caster = casterHandle.GetZoneState()!;
        ally.BaseCamp = EntityCamp.Justice;
        ally.CurrentCamp = EntityCamp.Justice;
        ally.PkState = PlayerPkMode.Peace;
        ally.FightMode = 1;
        caster.BaseCamp = EntityCamp.Justice;
        caster.CurrentCamp = EntityCamp.Justice;
        caster.PkState = PlayerPkMode.Peace;
        caster.FightMode = 1;

        allyHandle.CastSkill(1380, 52224, 101696);
        await harness.StepFramesAsync(8);
        Assert.Equal(2, worker.ActiveMissileCount);

        casterHandle.CastSkill(9940, caster.CurrentMpsX, caster.CurrentMpsY);
        await harness.StepFramesAsync(1);
        Assert.Contains(casterHandle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 9940u);

        int m419Count = 0;
        for (int i = 0; i < worker.MissileSystem.PoolCapacity; i++)
        {
            var s = worker.MissileSystem.GetSlot(i);
            if (s.IsActive && s.MissileTemplateId == 419)
            {
                m419Count++;
            }
        }

        Assert.Equal(1, m419Count);
    }

    [Fact]
    public async Task MeleeRunAttackAndJumpBlockedCells_WithSolidWallAndThreeMonsters_ShouldHaltAtWallAndRejectBlockedDest()
    {
        MapDescriptor map = CreateArenaMap(
            (52096, 101696, ElementSeries.Wood, "Quái Wall 1"),
            (52160, 101696, ElementSeries.Fire, "Quái Blocked"),
            (52128, 101696, ElementSeries.Earth, "Quái Wall 3"));

        // Block cellX = 52048/32 = 1626 (wall between 52000 and 52096) AND destination cellX = 52160/32 = 1630
        int cellY = 101696 / WorldCoordinateConsts.CellHeightMps;
        var blockedMap = new OverlayMapObstacleCatalog(
            SharedGameAssetsFixture.Default.MapCatalog,
            (52048 / WorldCoordinateConsts.CellWidthMps, cellY),
            (52160 / WorldCoordinateConsts.CellWidthMps, cellY));

        var harness = CreateHarness(42, map, mapCatalog: blockedMap);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "WallAndBlockTester",
                ElementSeries.Metal,
                52000,
                101696,
                skills:
                [
                    new CharacterSkillDto(38, 10, 0),
                    new CharacterSkillDto(40, 10, 0),
                    new CharacterSkillDto(210, 10, 0),
                ]));

        PlayerZoneState state = handle.GetZoneState()!;
        state.FightMode = 1;
        CreateAndEquipWeapon(state, 1u, minDamage: 120, maxDamage: 120);

        NpcZoneState mWall = worker.StaticNpcs[1u];    // (52096, 101696) behind wall at 52048
        NpcZoneState mBlocked = worker.StaticNpcs[2u]; // (52160, 101696) standing on blocked cell

        // 1) Skill 40 meleeRunAttack toward walkable target across wall (52048): must halt BEFORE wall, never teleport to 52096
        handle.CastSkill(40, -1, (int)mWall.NpcId);
        await harness.StepFramesAsync(10);
        Assert.True(state.CurrentMpsX < 52032, $"Skill 40 must halt before wall at 52032..52063 (actual X={state.CurrentMpsX}).");

        // 2) Skill 38, 40, and 210 onto unwalkable blocked cell (52160, 101696) must reject cleanly: 0x5A=0, mana=0, cooldown=0, dmg=0
        state.CurrentMpsX = 52128; state.CurrentMpsY = 101696;
        int manaBefore = state.CurrentMana;
        handle.Recorder.Clear();

        state.SetSkillCooldown(38, 0);
        handle.CastSkill(38, -1, (int)mBlocked.NpcId);
        await harness.StepFramesAsync(8);
        Assert.Empty(handle.Recorder.SkillCastBroadcasts);
        Assert.Empty(handle.Recorder.JumpBroadcasts);
        Assert.Equal(manaBefore, state.CurrentMana);
        Assert.Equal(10_000u, mBlocked.CurLife);
        Assert.True(state.IsSkillOffCooldown(38, worker.CurrentFrameTick));

        state.SetSkillCooldown(40, 0);
        handle.CastSkill(40, -1, (int)mBlocked.NpcId);
        await harness.StepFramesAsync(8);
        Assert.Empty(handle.Recorder.SkillCastBroadcasts);
        Assert.Empty(handle.Recorder.RunBroadcasts);
        Assert.Equal(manaBefore, state.CurrentMana);
        Assert.Equal(10_000u, mBlocked.CurLife);
        Assert.True(state.IsSkillOffCooldown(40, worker.CurrentFrameTick));

        state.SetSkillCooldown(210, 0);
        handle.CastSkill(210, 52160, 101696);
        await harness.StepFramesAsync(4);
        Assert.Empty(handle.Recorder.SkillCastBroadcasts);
        Assert.Empty(handle.Recorder.JumpBroadcasts);
        Assert.Equal(manaBefore, state.CurrentMana);
        Assert.True(state.IsSkillOffCooldown(210, worker.CurrentFrameTick));
    }

    [Fact]
    public async Task AutoExplodeGroundImpactAndByMissileHeading_WithThreeMonsters_ShouldExplodeAtGroundAndKeepParentDir()
    {
        MapDescriptor map = CreateArenaMap(
            (52160, 101664, ElementSeries.Wood, "Quái North 1"),
            (52160, 101632, ElementSeries.Fire, "Quái North 2"),
            (52160, 101600, ElementSeries.Earth, "Quái North 3"),
            (52160, 101856, ElementSeries.Metal, "Quái South Decoy"));

        // M9950: Height=25, ZAccel=30720, Life=30, AutoExplode=true -> skips [1,20] directly to Z=0 on frame 2
        var groundImpactMissile = new MissileTemplate(
            Id: 9950,
            Code: "syn-ground-impact",
            Name: "Ground Impact",
            MoveKind: MissileMoveKind.Stand,
            FollowKind: "none",
            SpeedMpsPerFrame: 0,
            HeightMps: 25,
            ZSpeedMpsPerFrame: 0,
            ZAccelMpsPerFrame2: 30720,
            LifeFrames: 30,
            HitRadiusMps: 32,
            CollideRadiusCells: 1,
            DamageRadiusCells: 4,
            IsRangeDamage: true,
            DamageIntervalFrames: 0,
            PierceCount: 0,
            CollideVanish: false,
            CanCollideFriend: false,
            AutoExplode: true,
            MustHit: false,
            SubMissileId: 0);

        // M9951: North-flying ByMissile parent
        var byMissileParent = new MissileTemplate(
            Id: 9951,
            Code: "syn-bymissile-parent",
            Name: "ByMissile Parent",
            MoveKind: MissileMoveKind.Line64,
            FollowKind: "none",
            SpeedMpsPerFrame: 32,
            HeightMps: 10,
            ZSpeedMpsPerFrame: 0,
            ZAccelMpsPerFrame2: 0,
            LifeFrames: 20,
            HitRadiusMps: 32,
            CollideRadiusCells: 1,
            DamageRadiusCells: 1,
            IsRangeDamage: false,
            DamageIntervalFrames: 0,
            PierceCount: 1,
            CollideVanish: true,
            CanCollideFriend: false,
            AutoExplode: false,
            MustHit: false,
            SubMissileId: 0);

        SkillTemplate real88 = RequireSkill(SharedGameAssetsFixture.Default.SkillTemplateCatalog, 88);
        SkillTemplate groundImpactSkill = real88 with
        {
            Id = 9950,
            Code = "syn-ground-impact-skill",
            IsPhysical = false,
            MissileForm = "atTarget",
            MissileId = 9950,
            WaitFrames = 0,
            MinFramesPerCast = 0,
            MinFramesPerCastOnHorse = 0,
            EffectScaling =
            [
                new SkillEffectScalingTemplate(
                    62,
                    "lightingdamage_v",
                    "damage",
                    new SkillParameterCurveTemplate("Linear", 150, 0),
                    null,
                    new SkillParameterCurveTemplate("Linear", 150, 0)),
            ],
        };

        SkillTemplate byMissileChildLine = real88 with
        {
            Id = 9952,
            Code = "syn-bymissile-child-line",
            IsPhysical = false,
            MissileId = 169,
            MissileForm = "line",
            WaitFrames = 0,
            EffectScaling =
            [
                new SkillEffectScalingTemplate(
                    62,
                    "lightingdamage_v",
                    "damage",
                    new SkillParameterCurveTemplate("Linear", 150, 0),
                    null,
                    new SkillParameterCurveTemplate("Linear", 150, 0)),
            ],
        };

        SkillTemplate byMissileParentSkill = real88 with
        {
            Id = 9951,
            Code = "syn-bymissile-parent-skill",
            IsPhysical = false,
            ByMissile = true,
            MissileForm = "line",
            MissileId = 9951,
            WaitFrames = 0,
            MinFramesPerCast = 0,
            MinFramesPerCastOnHorse = 0,
            EffectScaling =
            [
                new SkillEffectScalingTemplate(
                    62,
                    "lightingdamage_v",
                    "damage",
                    new SkillParameterCurveTemplate("Linear", 10, 0),
                    null,
                    new SkillParameterCurveTemplate("Linear", 10, 0)),
            ],
            EventSkills = new SkillEventSkillsTemplate(-1, 0, 0, 0, 0, 0, 9952, 1, 0, 0),
        };

        var skillOverlay = new OverlaySkillTemplateCatalog(
            SharedGameAssetsFixture.Default.SkillTemplateCatalog,
            groundImpactSkill,
            byMissileParentSkill,
            byMissileChildLine);
        var missileOverlay = new OverlayMissileTemplateCatalog(
            SharedGameAssetsFixture.Default.MissileTemplateCatalog,
            groundImpactMissile,
            byMissileParent);

        var harness = CreateHarness(42, map, skillOverlay, includeMapObstacles: false, missileCatalog: missileOverlay);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "GroundAndHeadingTester",
                ElementSeries.Metal,
                52160,
                101760,
                skills:
                [
                    new CharacterSkillDto(9950, 1, 0),
                    new CharacterSkillDto(9951, 1, 0),
                ]));

        PlayerZoneState state = handle.GetZoneState()!;
        state.FightMode = 1;

        NpcZoneState mNorth1 = worker.StaticNpcs[1u];
        NpcZoneState mNorth2 = worker.StaticNpcs[2u];
        NpcZoneState mNorth3 = worker.StaticNpcs[3u];
        NpcZoneState mSouthDecoy = worker.StaticNpcs[4u];

        // 1) AutoExplode skipping [1,20] to Z<=0 on frame 2 must explode by frame 3 (not frame 31)
        handle.CastSkill(9950, 52160, 101632);
        await harness.StepFramesAsync(3);
        Assert.Equal(0, worker.ActiveMissileCount);
        Assert.True(mNorth1.CurLife < 10_000u && mNorth2.CurLife < 10_000u && mNorth3.CurLife < 10_000u, "Falling AutoExplode skipping [1,20] to Z<=0 should explode immediately on ground impact.");

        // 2) North-flying ByMissile parent colliding with mNorth1 fires Line child North (hitting mNorth2), NOT South (mSouthDecoy untouched)
        ConfigurePassiveMonsters(worker);
        state.CurrentMpsX = 52160; state.CurrentMpsY = 101792;
        handle.CastSkill(9951, 52160, 101664);
        await harness.StepFramesAsync(10);
        Assert.True(mNorth1.CurLife < 10_000u, "Parent should collide with mNorth1.");
        Assert.True(mNorth2.CurLife < 10_000u, "ByMissile OnCollide->Line child should inherit North heading and hit mNorth2.");
        Assert.Equal(10_000u, mSouthDecoy.CurLife);
    }

    [Fact]
    public async Task SingleBladeRollBackDamageIntervalThrottlePostDeathCancelAndImmediateParams_WithThreeMonsters_ShouldMatchCpp()
    {
        MapDescriptor map = CreateArenaMap(
            (52128, 101696, ElementSeries.Wood, "Quái Edge 1"),
            (52224, 101696, ElementSeries.Fire, "Quái Edge 2"),
            (52192, 101696, ElementSeries.Earth, "Quái Edge 3"));

        // M9960: HitR=32 < DmgR=128 (4 cells), DmgInt=10, Life=25, IsRangeDamage=true
        var aoeThrottledMissile = new MissileTemplate(
            Id: 9960,
            Code: "syn-aoe-throttle",
            Name: "AoE Throttle",
            MoveKind: MissileMoveKind.Stand,
            FollowKind: "none",
            SpeedMpsPerFrame: 0,
            HeightMps: 10,
            ZSpeedMpsPerFrame: 0,
            ZAccelMpsPerFrame2: 0,
            LifeFrames: 25,
            HitRadiusMps: 32,
            CollideRadiusCells: 1,
            DamageRadiusCells: 4,
            IsRangeDamage: true,
            DamageIntervalFrames: 10,
            PierceCount: 0,
            CollideVanish: false,
            CanCollideFriend: false,
            AutoExplode: false,
            MustHit: false,
            SubMissileId: 0);

        // M9962: 1-frame OnCollide child missile so frame-1 child vanishes immediately before frame 2
        var onCollideChildMissile = new MissileTemplate(
            Id: 9962,
            Code: "syn-oncollide-child-1f",
            Name: "OnCollide 1F",
            MoveKind: MissileMoveKind.Stand,
            FollowKind: "none",
            SpeedMpsPerFrame: 0,
            HeightMps: 10,
            ZSpeedMpsPerFrame: 0,
            ZAccelMpsPerFrame2: 0,
            LifeFrames: 1,
            HitRadiusMps: 32,
            CollideRadiusCells: 1,
            DamageRadiusCells: 1,
            IsRangeDamage: false,
            DamageIntervalFrames: 0,
            PierceCount: 1,
            CollideVanish: true,
            CanCollideFriend: false,
            AutoExplode: false,
            MustHit: false,
            SubMissileId: 0);

        SkillTemplate real341 = RequireSkill(SharedGameAssetsFixture.Default.SkillTemplateCatalog, 341);
        SkillTemplate singleBladeRollBack = real341 with
        {
            Id = 9961,
            Code = "syn-single-rollback",
            ChildSkillCount = 1,
            WaitFrames = 0,
            MinFramesPerCast = 0,
            MinFramesPerCastOnHorse = 0,
        };

        SkillTemplate real88 = RequireSkill(SharedGameAssetsFixture.Default.SkillTemplateCatalog, 88);
        SkillTemplate onCollideInstant = real88 with
        {
            Id = 9962,
            Code = "syn-oncollide-child",
            IsPhysical = false,
            MissileId = 9962,
            MissileForm = "atTarget",
            WaitFrames = 0,
            EffectScaling =
            [
                new SkillEffectScalingTemplate(
                    62,
                    "lightingdamage_v",
                    "damage",
                    new SkillParameterCurveTemplate("Linear", 100, 0),
                    null,
                    new SkillParameterCurveTemplate("Linear", 100, 0)),
            ],
        };

        SkillTemplate throttledAoeSkill = real88 with
        {
            Id = 9960,
            Code = "syn-throttled-aoe",
            IsPhysical = false,
            ByMissile = true,
            MissileForm = "atTarget",
            MissileId = 9960,
            WaitFrames = 0,
            MinFramesPerCast = 0,
            MinFramesPerCastOnHorse = 0,
            EffectScaling =
            [
                new SkillEffectScalingTemplate(
                    62,
                    "lightingdamage_v",
                    "damage",
                    new SkillParameterCurveTemplate("Linear", 50, 0),
                    null,
                    new SkillParameterCurveTemplate("Linear", 50, 0)),
            ],
            EventSkills = new SkillEventSkillsTemplate(-1, 0, 0, 0, 0, 0, 9962, 1, 0, 0),
        };

        // Immediate melee line skill (MissileId=0) with EffectScaling skill_misslenum_v=3 & skill_attackradius=160
        SkillTemplate immediateScaledMelee = real88 with
        {
            Id = 9963,
            Code = "syn-imm-scaled",
            IsPhysical = false,
            IsMelee = true,
            MissileId = 0,
            MissileForm = "line",
            ChildSkillCount = 1,
            CastRangeMps = 64,
            WaitFrames = 1,
            MinFramesPerCast = 0,
            MinFramesPerCastOnHorse = 0,
            EffectScaling =
            [
                new SkillEffectScalingTemplate(
                    4,
                    "skill_misslenum_v",
                    "skillMechanic",
                    new SkillParameterCurveTemplate("Linear", 3, 0),
                    null,
                    null),
                new SkillEffectScalingTemplate(
                    266,
                    "skill_attackradius",
                    "skillMechanic",
                    new SkillParameterCurveTemplate("Linear", 160, 0),
                    null,
                    null),
                new SkillEffectScalingTemplate(
                    62,
                    "lightingdamage_v",
                    "damage",
                    new SkillParameterCurveTemplate("Linear", 100, 0),
                    null,
                    new SkillParameterCurveTemplate("Linear", 100, 0)),
            ],
        };

        var skillOverlay = new OverlaySkillTemplateCatalog(
            SharedGameAssetsFixture.Default.SkillTemplateCatalog,
            singleBladeRollBack,
            throttledAoeSkill,
            onCollideInstant,
            immediateScaledMelee);
        var missileOverlay = new OverlayMissileTemplateCatalog(
            SharedGameAssetsFixture.Default.MissileTemplateCatalog,
            aoeThrottledMissile,
            onCollideChildMissile);

        var harness = CreateHarness(42, map, skillOverlay, includeMapObstacles: false, missileCatalog: missileOverlay);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "EdgeCasesTester",
                ElementSeries.Metal,
                52032,
                101696,
                skills:
                [
                    new CharacterSkillDto(9961, 10, 0),
                    new CharacterSkillDto(9960, 1, 0),
                    new CharacterSkillDto(9963, 1, 0),
                    new CharacterSkillDto(182, 10, 0),
                ]));

        PlayerZoneState state = handle.GetZoneState()!;
        state.FightMode = 1;
        state.BonusAttackRating = 50_000;
        CreateAndEquipWeapon(state, 1u, minDamage: 120, maxDamage: 120);

        NpcZoneState m1 = worker.StaticNpcs[1u];
        NpcZoneState m2 = worker.StaticNpcs[2u];
        NpcZoneState m3 = worker.StaticNpcs[3u];

        // 1) Single-blade RollBack hits stationary m1 on outward pass AND again on return pass
        handle.CastSkill(9961, 52400, 101696);
        await harness.StepFramesAsync(10);
        uint outwardDmg = 10_000u - m1.CurLife;
        Assert.True(outwardDmg > 0, "Single-blade RollBack should hit m1 on outward pass.");
        await harness.StepFramesAsync(26);
        uint totalRollBackDmg = 10_000u - m1.CurLife;
        Assert.True(totalRollBackDmg > outwardDmg, $"Single-blade RollBack should re-hit stationary m1 on return pass (out={outwardDmg}, total={totalRollBackDmg}).");

        // 2) DamageIntervalFrames=10 throttles OnCollideSkillId & keeps HitR<DmgR target eligible
        ConfigurePassiveMonsters(worker);
        m1.MpsX = 52128; m1.MpsY = 101696; // Inside HitR=32 at spawn (52128, 101696)
        m2.MpsX = 52224; m2.MpsY = 101696; // 96 mps away (outside HitR=32, inside DmgR=128)
        m3.MpsX = 52400; m3.MpsY = 101696; // Off-target initially
        handle.CastSkill(9960, 52128, 101696);
        await harness.StepFramesAsync(1);
        uint m2AfterF1 = m2.CurLife;
        Assert.True(m2AfterF1 < 10_000u, "Frame 1 AoE splash should damage m2.");
        await harness.StepFramesAsync(1); // Allow Frame 1's OnCollide child M9962 (LifeFrames=1) to resolve & expire before m3 enters 52128

        // Frame 3..4: m1 leaves HitR, m3 enters HitR -> DamageIntervalFrames=10 must throttle both damage & OnCollideSkillId!
        m1.MpsX = 52600;
        m3.MpsX = 52128;
        await harness.StepFramesAsync(2);
        Assert.Equal(10_000u, m3.CurLife); // Throttled (not hit on frame 3/4)
        Assert.Equal(m2AfterF1, m2.CurLife);

        // Frame 12 (>= DamageIntervalFrames=10): m2 in DmgR=128 MUST be re-hit even after m1 left HitR!
        m3.MpsX = 52600;
        await harness.StepFramesAsync(10);
        Assert.True(m2.CurLife < m2AfterF1, $"Target in DmgR=128 should re-hit after DamageIntervalFrames=10 even when HitR target left (m2={m2.CurLife}, f1={m2AfterF1}).");

        // 3) ResolveImmediateSkillHit uses SkillEffectiveParams (skill_misslenum_v=3 & skill_attackradius=160)
        await harness.StepFramesAsync(20);
        ConfigurePassiveMonsters(worker);
        state.CurrentMpsX = 52032; state.CurrentMpsY = 101696;
        m1.MpsX = 52096; m1.MpsY = 101696;
        m2.MpsX = 52160; m2.MpsY = 101696;
        m3.MpsX = 52032; m3.MpsY = 101824;
        handle.CastSkill(9963, -1, (int)m1.NpcId);
        await harness.StepFramesAsync(3);
        Assert.True(m1.CurLife < 10_000u && m2.CurLife < 10_000u && m3.CurLife < 10_000u, "Immediate melee skill with skill_misslenum_v=3 & skill_attackradius=160 should hit all 3 monsters.");

        // 4) Launcher death cancels PendingMissileSpawnSlot
        ConfigurePassiveMonsters(worker);
        m1.MpsX = 52400; m1.MpsY = 101696;
        m2.MpsX = 52432; m2.MpsY = 101696;
        m3.MpsX = 52368; m3.MpsY = 101696;
        handle.CastSkill(182, 52160, 101696);
        await harness.StepFramesAsync(1);
        Assert.True(worker.MissileSystem.PendingSpawnCount > 0);
        state.CurrentLife = 0;
        state.Doing = EntityDoingPosture.Death;
        await harness.StepFramesAsync(2);
        Assert.Equal(0, worker.MissileSystem.PendingSpawnCount);
        m1.MpsX = 52160; m1.MpsY = 101696;
        m2.MpsX = 52192; m2.MpsY = 101696;
        m3.MpsX = 52128; m3.MpsY = 101696;
        await harness.StepFramesAsync(60);
        Assert.Equal(10_000u, m1.CurLife);
        Assert.Equal(10_000u, m2.CurLife);
        Assert.Equal(10_000u, m3.CurLife);
    }

    [Fact]
    public async Task FlyingCollideVanishAoE_WithHitRadiusSmallerThanDamageRadius_ShouldIgnoreOutsideHitRadiusAndSplashOnDirectHit()
    {
        MapDescriptor map = CreateArenaMap(
            (52200, 101896, ElementSeries.Wood, "Quái Perp 200"),
            (52350, 101696, ElementSeries.Fire, "Quái Direct"),
            (52350, 101996, ElementSeries.Earth, "Quái Splash"));

        // M9981: M271-style flying Line64 projectile with HitR=64 < DmgR=448 (14 cells), CollideVanish=true
        var m9981 = new MissileTemplate(
            Id: 9981,
            Code: "syn-m271-fly-aoe",
            Name: "M271 Style",
            MoveKind: MissileMoveKind.Line64,
            FollowKind: "none",
            SpeedMpsPerFrame: 32,
            HeightMps: 10,
            ZSpeedMpsPerFrame: 0,
            ZAccelMpsPerFrame2: 0,
            LifeFrames: 25,
            HitRadiusMps: 64,
            CollideRadiusCells: 2,
            DamageRadiusCells: 14,
            IsRangeDamage: true,
            DamageIntervalFrames: 0,
            PierceCount: 1,
            CollideVanish: true,
            CanCollideFriend: false,
            CanDestroy: false,
            CanSlow: false,
            AutoExplode: false,
            MustHit: false,
            ResponseSkillId: 0,
            SubMissileId: 0);

        SkillTemplate real88 = RequireSkill(SharedGameAssetsFixture.Default.SkillTemplateCatalog, 88);
        SkillTemplate flyAoeSkill = real88 with
        {
            Id = 9981,
            Code = "syn-m271-skill",
            IsPhysical = false,
            MissileForm = "line",
            MissileId = 9981,
            WaitFrames = 0,
            MinFramesPerCast = 0,
            MinFramesPerCastOnHorse = 0,
            EffectScaling =
            [
                new SkillEffectScalingTemplate(
                    62,
                    "lightingdamage_v",
                    "damage",
                    new SkillParameterCurveTemplate("Linear", 150, 0),
                    null,
                    new SkillParameterCurveTemplate("Linear", 150, 0)),
            ],
        };

        var skillOverlay = new OverlaySkillTemplateCatalog(SharedGameAssetsFixture.Default.SkillTemplateCatalog, flyAoeSkill);
        var missileOverlay = new OverlayMissileTemplateCatalog(SharedGameAssetsFixture.Default.MissileTemplateCatalog, m9981);

        var harness = CreateHarness(42, map, skillOverlay, includeMapObstacles: false, missileCatalog: missileOverlay);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "HitRadiusTester",
                ElementSeries.Metal,
                52000,
                101696,
                skills: [new CharacterSkillDto(9981, 1, 0)]));

        PlayerZoneState state = handle.GetZoneState()!;
        state.FightMode = 1;

        NpcZoneState mPerp = worker.StaticNpcs[1u];
        NpcZoneState mDirect = worker.StaticNpcs[2u];
        NpcZoneState mSplash = worker.StaticNpcs[3u];

        handle.CastSkill(9981, 52400, 101696);
        await harness.StepFramesAsync(7); // Flies East to X=52224, passing 200 mps North of mPerp (HitR=64, DmgR=448)

        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 9981u);
        Assert.True(worker.ActiveMissileCount > 0, "Flying CollideVanish=true missile must not trigger collision 200 mps outside HitRadiusMps=64.");
        Assert.Equal(10_000u, mPerp.CurLife);

        await harness.StepFramesAsync(5); // Reaches mDirect at X=52350 -> triggers on HitRadiusMps=64 and splashes DmgRadius=448

        Assert.Equal(0, worker.ActiveMissileCount);
        Assert.True(mDirect.CurLife < 10_000u, "Direct target inside HitRadiusMps=64 should be hit.");
        Assert.True(mSplash.CurLife < 10_000u, "Splash target 300 mps away inside DamageRadiusMps=448 should take splash damage.");
        Assert.True(mPerp.CurLife < 10_000u, "Perp target inside DamageRadiusMps=448 of detonation point should also take splash damage.");
    }

    [Fact]
    public async Task HuaShanOnStartDetonate_With1376And1351AgainstThreeMonstersAndEnemyMissile_ShouldDetonateMatchingMissiles()
    {
        MapDescriptor map = CreateArenaMap(
            (52160, 101696, ElementSeries.Wood, "Quái HS 1"),
            (52192, 101696, ElementSeries.Fire, "Quái HS 2"),
            (52320, 101696, ElementSeries.Earth, "Quái HS 3"));

        var harness = CreateHarness(42, map, includeMapObstacles: false);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle hsHandle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "HuaShanDetonator",
                ElementSeries.Metal,
                52032,
                101696,
                skills: [new CharacterSkillDto(1376, 10, 0)]));

        PlayerZoneState hsPlayer = hsHandle.GetZoneState()!;
        hsPlayer.FightMode = 1;
        hsPlayer.BaseCamp = EntityCamp.Justice;
        hsPlayer.CurrentCamp = EntityCamp.Justice;
        hsPlayer.PkState = PlayerPkMode.Fight;

        NpcZoneState m1 = worker.StaticNpcs[1u];
        NpcZoneState m2 = worker.StaticNpcs[2u];
        NpcZoneState m3 = worker.StaticNpcs[3u];

        // 1) Cast 1376 once -> places M421 without self-detonating
        hsHandle.CastSkill(1376, 52160, 101696);
        await harness.StepFramesAsync(6); // M275 (Life=5) expires, leaving 1st M421 active
        Assert.Contains(hsHandle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 1376u);
        Assert.Equal(1, worker.ActiveMissileCount);

        // 2) Cast 1376 a second time within 360 mps -> OnStart 1377 detonates 1st M421 into 1412 (M422) damaging all 3 monsters
        ConfigurePassiveMonsters(worker);
        m3.MpsX = 52128; m3.MpsY = 101696;
        hsHandle.CastSkill(1376, 52160, 101696);
        await harness.StepFramesAsync(12);
        Assert.True(m1.CurLife < 10_000u && m2.CurLife < 10_000u && m3.CurLife < 10_000u,
            "Second cast of 1376 (OnStart 1377) should detonate first M421 into 1412 (M422) and damage all 3 monsters.");

        // 3) Clean arena for 1351 -> 1352 (isEnemy=1 detonate 323): enemy casts 1061 (5x M323 via centerExtendLine:2),
        // after 4 frames all 5 M323 are active within 128 mps of HuaShan caster, and 1351 -> 1352 detonates them before hitting m3
        var harness2 = CreateHarness(42, map, includeMapObstacles: false);
        MapZoneWorker worker2 = harness2.GetZone(53);
        ConfigurePassiveMonsters(worker2);

        SimulatedPlayerHandle hsHandle2 = await harness2.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "HuaShanDetonator2",
                ElementSeries.Metal,
                52128,
                101696,
                skills: [new CharacterSkillDto(1351, 30, 0)]));
        SimulatedPlayerHandle enemyHandle2 = await harness2.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "EnemyCaster2",
                ElementSeries.Water,
                52064,
                101696,
                skills: [new CharacterSkillDto(1061, 20, 0)]));

        PlayerZoneState hsPlayer2 = hsHandle2.GetZoneState()!;
        hsPlayer2.FightMode = 1;
        hsPlayer2.BaseCamp = EntityCamp.Justice;
        hsPlayer2.CurrentCamp = EntityCamp.Justice;
        hsPlayer2.PkState = PlayerPkMode.Fight;

        PlayerZoneState enemyPlayer2 = enemyHandle2.GetZoneState()!;
        enemyPlayer2.FightMode = 1;
        enemyPlayer2.BaseCamp = EntityCamp.Evil;
        enemyPlayer2.CurrentCamp = EntityCamp.Evil;
        enemyPlayer2.PkState = PlayerPkMode.Fight;

        NpcZoneState m2_1 = worker2.StaticNpcs[1u];
        NpcZoneState m2_2 = worker2.StaticNpcs[2u];
        NpcZoneState m2_3 = worker2.StaticNpcs[3u];
        m2_1.MpsX = 52320; m2_1.MpsY = 101856;
        m2_2.MpsX = 52320; m2_2.MpsY = 101920;
        m2_3.MpsX = 52320; m2_3.MpsY = 101696; // 256 mps East of EnemyCaster2 (in M323's path)

        enemyHandle2.CastSkill(1061, 52400, 101696);
        await harness2.StepFramesAsync(4);
        hsHandle2.CastSkill(1351, 52128, 101300); // Cast North so 1351's M413 does not travel East toward m2_3
        await harness2.StepFramesAsync(12);

        Assert.Contains(hsHandle2.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 1351u);
        Assert.Equal(10_000u, m2_3.CurLife); // All enemy M323 missiles were detonated by 1351 -> 1352 before reaching m2_3
    }

    [Fact]
    public async Task MonsterAiAndDelayedChildCast_WithInstantMissileStyle14_ShouldInvokeInstantResolutionWithZeroActiveSlots()
    {
        MapDescriptor map = CreateArenaMap(
            (52160, 101696, ElementSeries.Wood, "Quái IM 1"),
            (52160, 101696, ElementSeries.Fire, "Quái IM 2"),
            (52160, 101696, ElementSeries.Earth, "Quái IM 3"));

        SkillTemplate real723 = RequireSkill(SharedGameAssetsFixture.Default.SkillTemplateCatalog, 723);

        // 10-frame flying missile template so if InstantMissile wrongly falls through to SpawnSkillMissiles it lingers active > 0
        var nonVanishingProbeMissile = new MissileTemplate(
            Id: 9982,
            Code: "syn-im-probe",
            Name: "IM Probe",
            MoveKind: MissileMoveKind.Line64,
            FollowKind: "none",
            SpeedMpsPerFrame: 32,
            HeightMps: 10,
            ZSpeedMpsPerFrame: 0,
            ZAccelMpsPerFrame2: 0,
            LifeFrames: 10,
            HitRadiusMps: 64,
            CollideRadiusCells: 2,
            DamageRadiusCells: 2,
            IsRangeDamage: true,
            DamageIntervalFrames: 0,
            PierceCount: 0,
            CollideVanish: false,
            CanCollideFriend: false,
            CanDestroy: false,
            CanSlow: false,
            AutoExplode: false,
            MustHit: false,
            ResponseSkillId: 0,
            SubMissileId: 0);

        SkillTemplate monsterInstantSkill = real723 with
        {
            Id = 9982,
            Code = "syn-npc-im",
            Style = SkillStyleKind.InstantMissile,
            IsPhysical = false,
            MissileId = 9982,
            MissileForm = "atTarget",
            WaitFrames = 0,
            MinFramesPerCast = 0,
            MinFramesPerCastOnHorse = 0,
            EffectScaling =
            [
                new SkillEffectScalingTemplate(
                    62,
                    "lightingdamage_v",
                    "damage",
                    new SkillParameterCurveTemplate("Linear", 180, 0),
                    null,
                    new SkillParameterCurveTemplate("Linear", 180, 0)),
            ],
        };

        SkillTemplate real88 = RequireSkill(SharedGameAssetsFixture.Default.SkillTemplateCatalog, 88);
        SkillTemplate delayedParentSkill = real88 with
        {
            Id = 9983,
            Code = "syn-delayed-im-parent",
            IsPhysical = false,
            IsBaseSkill = false,
            MissileForm = "line",
            ChildSkillId = 9982,
            ChildSkillCount = 2,
            MissileGenerateStyle = "order",
            MissileGenerateDelayFrames = 2,
            WaitFrames = 0,
            MinFramesPerCast = 0,
            MinFramesPerCastOnHorse = 0,
        };

        var skillOverlay = new OverlaySkillTemplateCatalog(
            SharedGameAssetsFixture.Default.SkillTemplateCatalog,
            monsterInstantSkill,
            delayedParentSkill);
        var missileOverlay = new OverlayMissileTemplateCatalog(
            SharedGameAssetsFixture.Default.MissileTemplateCatalog,
            nonVanishingProbeMissile);

        var harness = CreateHarness(42, map, skillOverlay, includeMapObstacles: false, missileCatalog: missileOverlay);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "InstantMissileTester",
                ElementSeries.Metal,
                52128,
                101696,
                skills: [new CharacterSkillDto(9983, 1, 0)],
                life: 8000));

        PlayerZoneState state = handle.GetZoneState()!;
        state.FightMode = 1;

        NpcZoneState m1 = worker.StaticNpcs[1u];
        NpcZoneState m2 = worker.StaticNpcs[2u];
        NpcZoneState m3 = worker.StaticNpcs[3u];

        // Part A: Monster AI casts Style=14 InstantMissile skill 9982
        m1.MpsX = 52128; m1.MpsY = 101696;
        m1.AiMode = 1;
        m1.AiState = MonsterAiStateKind.Attack;
        m1.AggroTargetNpcId = state.RuntimeNpcId;
        m1.NextAttackReadyTick = 0;
        m1.AttackRadiusMps = 300;
        m1.Skills = [new NpcSkillSlotTemplate(9982, 1, 100)];

        await harness.StepFramesAsync(1);
        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => e.CastDirectly && e.SkillId == 9982u);
        Assert.Equal(0, worker.ActiveMissileCount);
        Assert.True(state.CurrentLife < 8000, "Monster AI Style=14 InstantMissile should resolve immediately and damage player.");

        // Disable m1 AI and test Part B: Delayed order:2 IsBaseSkill=false -> InstantMissile child (9983 -> 9982)
        ConfigurePassiveMonsters(worker);
        m1.MpsX = 52160; m1.MpsY = 101696;
        handle.CastSkill(9983, 52160, 101696);
        Assert.Equal(0, worker.ActiveMissileCount);
        await harness.StepFramesAsync(3); // Step past delay=2 pending child spawn

        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 9983u);
        Assert.Equal(0, worker.ActiveMissileCount);
        Assert.True(m1.CurLife < 10_000u && m2.CurLife < 10_000u && m3.CurLife < 10_000u,
            "Delayed IsBaseSkill=false -> InstantMissile child should resolve via SpawnAndResolveInstantMissile with 0 lingering slots.");
    }

    [Fact]
    public async Task MissileTrajectories_M464SpiralAndM85Circle_ShouldExpandOutwardAndOrbitAtFixedRadius()
    {
        // M464 (Speed=270, Param2=70, Param3=0): radius = CurrentLife + 50 + Speed = 321..390 mps around target
        MapDescriptor map = CreateArenaMap(
            (52635, 101696, ElementSeries.Wood, "Quái Spiral E"),
            (52300, 101361, ElementSeries.Fire, "Quái Spiral N"),
            (51965, 101696, ElementSeries.Earth, "Quái Spiral W"),
            (52200, 101696, ElementSeries.Water, "Quái Decoy East"));

        SkillTemplate real88 = RequireSkill(SharedGameAssetsFixture.Default.SkillTemplateCatalog, 88);
        SkillTemplate m85OrbitSkill = real88 with
        {
            Id = 9984,
            Code = "syn-m85-circle",
            IsPhysical = false,
            MissileId = 85, // MoveKind=2 (Circle in missles.txt, exported as Random), Speed=16 -> fixed radius 50+16=66
            MissileForm = "spread",
            Param1 = 8,
            Param2 = 0,
            ChildSkillCount = 3,
            WaitFrames = 0,
            MinFramesPerCast = 0,
            MinFramesPerCastOnHorse = 0,
            EffectScaling =
            [
                new SkillEffectScalingTemplate(
                    62,
                    "lightingdamage_v",
                    "damage",
                    new SkillParameterCurveTemplate("Linear", 150, 0),
                    null,
                    new SkillParameterCurveTemplate("Linear", 150, 0)),
            ],
        };

        var skillOverlay = new OverlaySkillTemplateCatalog(SharedGameAssetsFixture.Default.SkillTemplateCatalog, m85OrbitSkill);
        var harness = CreateHarness(42, map, skillOverlay, includeMapObstacles: false);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "SpiralAndM85Tester",
                ElementSeries.Fire,
                52000,
                101696,
                skills:
                [
                    new CharacterSkillDto(1237, 10, 0),
                    new CharacterSkillDto(9984, 1, 0),
                ]));

        PlayerZoneState state = handle.GetZoneState()!;
        state.FightMode = 1;

        NpcZoneState mEast = worker.StaticNpcs[1u];
        NpcZoneState mNorth = worker.StaticNpcs[2u];
        NpcZoneState mWest = worker.StaticNpcs[3u];
        NpcZoneState mDecoy = worker.StaticNpcs[4u];

        // 1) M464 (Skill 1237) expands outward at CurrentLife + 50 + 270 (~335 mps), hitting all 3 monsters at 335 mps from (52300, 101696)
        handle.CastSkill(1237, 52300, 101696);
        await harness.StepFramesAsync(72);

        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 1237u);
        Assert.True(mEast.CurLife < 10_000u && mNorth.CurLife < 10_000u && mWest.CurLife < 10_000u,
            $"M464 should spiral outward at (CurrentLife+50+Speed) and hit all 3 monsters at 335 mps (E={mEast.CurLife}, N={mNorth.CurLife}, W={mWest.CurLife}).");

        // 2) M85 (Skill 9984): fixed-radius (50 + Speed = 66 mps) Circle around caster (52000, 101696), NOT flying East 200 mps to decoy
        ConfigurePassiveMonsters(worker);
        mEast.MpsX = 52066; mEast.MpsY = 101696;     // 66 mps East (dir=0)
        mNorth.MpsX = 52000; mNorth.MpsY = 101630;    // 66 mps North (dir=48)
        mWest.MpsX = 52047; mWest.MpsY = 101743;      // 66 mps SE (dir=8)
        mDecoy.MpsX = 52200; mDecoy.MpsY = 101696;    // 200 mps East (random/line flight would hit or fly near here)

        handle.CastSkill(9984, 52200, 101696);
        await harness.StepFramesAsync(20);

        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 9984u);
        Assert.True(mEast.CurLife < 10_000u && mNorth.CurLife < 10_000u && mWest.CurLife < 10_000u,
            $"M85 (missles.txt MoveKind=2 Circle) should orbit at fixed radius 50+16=66 mps (E={mEast.CurLife}, N={mNorth.CurLife}, W={mWest.CurLife}).");
        Assert.Equal(10_000u, mDecoy.CurLife);
    }

    [Fact]
    public async Task ShortRangeZeroHeightParabola_WithTwoFrameFlight_ShouldSurviveFrameOneAndHitTargetOnFrameTwo()
    {
        MapDescriptor map = CreateArenaMap(
            (52096, 101696, ElementSeries.Wood, "Quái Para 1"),
            (52096, 101696, ElementSeries.Fire, "Quái Para 2"),
            (52096, 101696, ElementSeries.Earth, "Quái Para 3"));

        var shortParaMissile = new MissileTemplate(
            Id: 9985,
            Code: "syn-short-para",
            Name: "Short Parabola",
            MoveKind: MissileMoveKind.Parabola,
            FollowKind: "none",
            SpeedMpsPerFrame: 32,
            HeightMps: 0,
            ZSpeedMpsPerFrame: 0,
            ZAccelMpsPerFrame2: 1024,
            LifeFrames: 20,
            HitRadiusMps: 32,
            CollideRadiusCells: 1,
            DamageRadiusCells: 3,
            IsRangeDamage: true,
            DamageIntervalFrames: 0,
            PierceCount: 0,
            CollideVanish: true,
            CanCollideFriend: false,
            CanDestroy: false,
            CanSlow: false,
            AutoExplode: true,
            MustHit: false,
            ResponseSkillId: 0,
            SubMissileId: 0);

        SkillTemplate real45 = RequireSkill(SharedGameAssetsFixture.Default.SkillTemplateCatalog, 45);
        SkillTemplate shortParaSkill = real45 with
        {
            Id = 9985,
            Code = "syn-short-para-skill",
            IsPhysical = false,
            MissileForm = "line",
            MissileId = 9985,
            WaitFrames = 0,
            MinFramesPerCast = 0,
            MinFramesPerCastOnHorse = 0,
            EffectScaling =
            [
                new SkillEffectScalingTemplate(
                    62,
                    "lightingdamage_v",
                    "damage",
                    new SkillParameterCurveTemplate("Linear", 150, 0),
                    null,
                    new SkillParameterCurveTemplate("Linear", 150, 0)),
            ],
        };

        var skillOverlay = new OverlaySkillTemplateCatalog(SharedGameAssetsFixture.Default.SkillTemplateCatalog, shortParaSkill);
        var missileOverlay = new OverlayMissileTemplateCatalog(SharedGameAssetsFixture.Default.MissileTemplateCatalog, shortParaMissile);

        var harness = CreateHarness(42, map, skillOverlay, includeMapObstacles: false, missileCatalog: missileOverlay);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "ShortParabolaTester",
                ElementSeries.Fire,
                52032,
                101696,
                skills: [new CharacterSkillDto(9985, 1, 0)]));

        PlayerZoneState state = handle.GetZoneState()!;
        state.FightMode = 1;

        NpcZoneState m1 = worker.StaticNpcs[1u];
        NpcZoneState m2 = worker.StaticNpcs[2u];
        NpcZoneState m3 = worker.StaticNpcs[3u];

        // dist=64, speed=32 => flightFrames=2; HeightMps=0 must survive frame 1 before FirstReclaimFrame=2
        handle.CastSkill(9985, 52096, 101696);
        await harness.StepFramesAsync(1);

        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 9985u);
        Assert.Equal(1, worker.ActiveMissileCount);

        await harness.StepFramesAsync(2);
        Assert.True(m1.CurLife < 10_000u && m2.CurLife < 10_000u && m3.CurLife < 10_000u,
            "Short-range Parabola should reach target on frame 2 and damage all 3 stacked monsters.");
    }

    [Fact]
    public async Task ImmediateMeleeLineSkill_WithZeroMissileIdAndTargetOnlyFalse_ShouldHitAllThreeStackedMonsters()
    {
        MapDescriptor map = CreateArenaMap(
            (52096, 101696, ElementSeries.Wood, "Quái ImmLine 1"),
            (52096, 101696, ElementSeries.Fire, "Quái ImmLine 2"),
            (52096, 101696, ElementSeries.Earth, "Quái ImmLine 3"));

        SkillTemplate real88 = RequireSkill(SharedGameAssetsFixture.Default.SkillTemplateCatalog, 88);
        SkillTemplate immediateLineSkill = real88 with
        {
            Id = 9986,
            Code = "syn-imm-line-no-targetonly",
            IsPhysical = false,
            IsMelee = true,
            MissileId = 0,
            MissileForm = "line",
            ChildSkillCount = 1,
            CastRangeMps = 96,
            WaitFrames = 1,
            MinFramesPerCast = 0,
            MinFramesPerCastOnHorse = 0,
            EventSkills = null,
            Targeting = new SkillTargetingTemplate(
                TargetSelf: false,
                TargetAlly: false,
                TargetEnemy: true,
                TargetNoNpc: false,
                TargetOnly: false,
                TargetOther: false),
            EffectScaling =
            [
                new SkillEffectScalingTemplate(
                    62,
                    "lightingdamage_v",
                    "damage",
                    new SkillParameterCurveTemplate("Linear", 150, 0),
                    null,
                    new SkillParameterCurveTemplate("Linear", 150, 0)),
            ],
        };

        var skillOverlay = new OverlaySkillTemplateCatalog(SharedGameAssetsFixture.Default.SkillTemplateCatalog, immediateLineSkill);
        var harness = CreateHarness(42, map, skillOverlay, includeMapObstacles: false);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "ImmediateLineTester",
                ElementSeries.Metal,
                52032,
                101696,
                skills: [new CharacterSkillDto(9986, 1, 0)]));

        PlayerZoneState state = handle.GetZoneState()!;
        state.FightMode = 1;

        NpcZoneState m1 = worker.StaticNpcs[1u];
        NpcZoneState m2 = worker.StaticNpcs[2u];
        NpcZoneState m3 = worker.StaticNpcs[3u];

        handle.CastSkill(9986, -1, (int)m1.NpcId);
        await harness.StepFramesAsync(3);

        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 9986u);
        Assert.True(m1.CurLife < 10_000u && m2.CurLife < 10_000u && m3.CurLife < 10_000u,
            $"Immediate melee line skill with MissileId=0 and TargetOnly=false should hit all 3 stacked monsters (m1={m1.CurLife}, m2={m2.CurLife}, m3={m3.CurLife}).");
    }
}
