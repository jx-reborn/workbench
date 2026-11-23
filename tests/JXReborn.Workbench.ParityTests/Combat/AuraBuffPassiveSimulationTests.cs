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
using JXReborn.ZoneEngine.Factions;
using JXReborn.ZoneEngine.Items;
using JXReborn.ZoneEngine.NpcEntities;
using JXReborn.ZoneEngine.Parties;
using JXReborn.ZoneEngine.PlayerSessions;
using JXReborn.ZoneEngine.WorldZones;
using Xunit;

namespace JXReborn.ZoneEngine.Tests.Combat;

[Trait("Category", "LegacyData")]
public sealed class AuraBuffPassiveSimulationTests
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
        bool includePartyModule = false)
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

        var options = new ZoneSimulationHarnessOptions
        {
            RandomSeed = seed,
            Assets = services,
            InitialMaps = { map },
        };

        if (includePartyModule)
        {
            options.CustomModules.Add(new PartyZoneModule());
        }

        var harness = new ZoneSimulationHarness(options);
        foreach (MapZoneWorker w in harness.Workers)
        {
            // Isolate passive/buff/curse math from the 18-frame player wind-up (tested in SkillCastWindupTimingTests)
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
        byte level = 120,
        int life = 8000,
        int mana = 5000,
        byte currentFactionId = 255,
        ushort unallocatedSkillPoints = 0) =>
        new(
            Id: Guid.NewGuid(),
            AccountId: Guid.NewGuid(),
            SlotIndex: 0,
            RoleNameUnicode: name,
            Gender: CharacterGender.Male,
            Series: series,
            Level: level,
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
            UnallocatedSkillPoints: unallocatedSkillPoints,
            InventoryMoney: 10000,
            RepositoryMoney: 0,
            CurrentFactionId: currentFactionId,
            FirstFactionId: currentFactionId,
            FactionJoinCount: currentFactionId == 255 ? (ushort)0 : (ushort)1,
            Skills: skills);

    private static void ConfigurePassiveMonsters(MapZoneWorker worker, uint life = 10_000)
    {
        Assert.True(worker.StaticNpcs.Count >= 3);
        Assert.Equal(3, worker.StaticNpcs.Count);
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

    private static SkillTemplate RequireSkill(ISkillTemplateCatalog catalog, int skillId)
    {
        Assert.True(catalog.TryGetSkill(skillId, out SkillTemplate? skill) && skill is not null);
        return skill!;
    }

    private static PlayerItemSlot CreateAndEquipWeapon(
        PlayerZoneState player,
        uint runtimeItemId,
        int particularType = 0,
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
            ParticularType: particularType,
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
            particularType: (short)particularType,
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

    [Fact]
    public async Task CastAura_10FrameLoopAndHideSuppression_WithThreeMonsters_ShouldEmit0x85ChildSkill0x87And0x7A()
    {
        MapDescriptor arena = CreateArenaMap(
            (52064, 101696, ElementSeries.Metal, "Quái Độc 1"),
            (52096, 101696, ElementSeries.Wood, "Quái Độc 2"),
            (52032, 101760, ElementSeries.Earth, "Quái Độc 3"));

        ZoneSimulationHarness harness = CreateHarness(42, arena);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "AuraCaster",
                ElementSeries.Wood,
                52032,
                101696,
                [
                    new CharacterSkillDto(69, 20, 0),
                    new CharacterSkillDto(16, 20, 0),
                    new CharacterSkillDto(713, 10, 0),
                ]));

        PlayerZoneState state = handle.GetZoneState()!;
        state.FightMode = 1;
        handle.Recorder.Clear();

        // 1. Activate Aura 69 (Vô Hình Độc -> ChildSkillId 203, StateSpecialId 6)
        harness.EnqueueCommand(53, new ChangeAuraSkillZoneCommand(state.CharacterId, handle.LeaseEpoch, 69));
        await harness.StepFramesAsync(1);

        Assert.Contains(
            handle.Recorder.NpcStateInfoBroadcasts,
            e => e.NpcId == state.RuntimeNpcId && e.StateSpecialIds.Contains((byte)6));

        // Step 12 frames so 10-frame loop fires and Missile 94 hits all 3 monsters
        handle.Recorder.Clear();
        await harness.StepFramesAsync(12);

        Assert.Contains(
            handle.Recorder.SkillCastBroadcasts,
            e => e.CastDirectly &&
                 e.CasterNpcId == state.RuntimeNpcId &&
                 e.SkillId == 203u &&
                 e.TargetMpsX == -1 &&
                 e.TargetMpsYOrNpcId == (int)state.RuntimeNpcId &&
                 e.ExtraFlag == 0);
        Assert.DoesNotContain(
            handle.Recorder.SkillCastBroadcasts,
            e => e.CastDirectly && e.SkillId == 69u);

        NpcZoneState m1 = worker.StaticNpcs[1u];
        NpcZoneState m2 = worker.StaticNpcs[2u];
        NpcZoneState m3 = worker.StaticNpcs[3u];
        Assert.True(
            m1.CurLife < 10_000u && m2.CurLife < 10_000u && m3.CurLife < 10_000u,
            $"Aura 69->203 should damage all 3 monsters (m1={m1.CurLife}, m2={m2.CurLife}, m3={m3.CurLife}).");

        // OnStartSkillId 1731 (Vô Hình Độc Buff) should apply 0x87 to caster
        Assert.Contains(
            handle.Recorder.SyncStateEffects,
            e => e.NpcId == state.RuntimeNpcId && e.SkillId == 1731 && e.RemainTimeFrames > 0);

        // 2. Switch Aura to 16 (La Hán Trận -> ChildSkillId 202, StateSpecialId 45)
        handle.Recorder.Clear();
        harness.EnqueueCommand(53, new ChangeAuraSkillZoneCommand(state.CharacterId, handle.LeaseEpoch, 16));
        await harness.StepFramesAsync(12);

        Assert.Contains(
            handle.Recorder.NpcStateInfoBroadcasts,
            e => e.NpcId == state.RuntimeNpcId && e.StateSpecialIds.Contains((byte)45));
        Assert.Contains(
            handle.Recorder.SkillCastBroadcasts,
            e => e.CastDirectly && e.SkillId == 202u && e.ExtraFlag == 0);
        Assert.Contains(
            handle.Recorder.SyncStateEffects,
            e => e.NpcId == state.RuntimeNpcId && e.SkillId == 202 && e.RemainTimeFrames > 0);
        Assert.True(state.SkillCombatModifiers.MeleeDamageReturnPercent > 0);
        Assert.True(state.SkillCombatModifiers.ExDefenseDelta > 0);
        Assert.Equal(0, state.EquipmentCombatModifiers.MeleeDamageReturnPercent);

        // 3. Cast 713 (hide=1) -> suppresses CastAura 0x85 loop
        handle.CastSkill(713, state.CurrentMpsX, state.CurrentMpsY);
        await harness.StepFramesAsync(1);
        Assert.True(((ICombatEntityState)state).HideMode > 0);

        handle.Recorder.Clear();
        await harness.StepFramesAsync(15);
        Assert.DoesNotContain(
            handle.Recorder.SkillCastBroadcasts,
            e => e.CastDirectly && e.SkillId == 202u);

        // 4. Clear aura (0) -> clears ActiveAuraSkillId and broadcasts 0x7A immediately while letting child skill 202 expire naturally via ProcessState (KNpc::SetAuraSkill(0) parity)
        handle.Recorder.Clear();
        harness.EnqueueCommand(53, new ChangeAuraSkillZoneCommand(state.CharacterId, handle.LeaseEpoch, 0));
        await harness.StepFramesAsync(1);
        Assert.Equal(0, state.ActiveAuraSkillId);
        Assert.NotEmpty(handle.Recorder.NpcStateInfoBroadcasts);
        Assert.NotNull(state.StatusEffects.FindStateSkillEffect(202));

        handle.Recorder.Clear();
        await harness.StepFramesAsync(5);
        Assert.Null(state.StatusEffects.FindStateSkillEffect(202));
        Assert.NotEmpty(handle.Recorder.NpcStateInfoBroadcasts);
        Assert.DoesNotContain((byte)45, handle.Recorder.NpcStateInfoBroadcasts[^1].StateSpecialIds);
        Assert.DoesNotContain(
            handle.Recorder.SyncStateEffects,
            e => e.NpcId == state.RuntimeNpcId && e.SkillId == 202 && e.RemainTimeFrames == 0);
    }

    [Fact]
    public async Task CastAura_NgaMiWuDangAndPartyAuraCoexistence_WithThreeMonsters_ShouldEmitBoth0x85AndParty0x87()
    {
        MapDescriptor arena = CreateArenaMap(
            (52200, 101696, ElementSeries.Metal, "Quái Đồng Đội 1"),
            (52250, 101696, ElementSeries.Fire, "Quái Đồng Đội 2"),
            (52200, 101760, ElementSeries.Earth, "Quái Đồng Đội 3"));

        ZoneSimulationHarness harness = CreateHarness(42, arena, includePartyModule: true);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle captainHandle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "AuraCaptain",
                ElementSeries.Water,
                52032,
                101696,
                [
                    new CharacterSkillDto(86, 10, 0),
                    new CharacterSkillDto(89, 10, 0),
                    new CharacterSkillDto(92, 10, 0),
                    new CharacterSkillDto(159, 10, 0),
                    new CharacterSkillDto(712, 20, 0),
                ]));

        SimulatedPlayerHandle memberHandle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "AuraMember",
                ElementSeries.Wood,
                52128,
                101696,
                []));

        PlayerZoneState captain = captainHandle.GetZoneState()!;
        PlayerZoneState member = memberHandle.GetZoneState()!;
        int memberBasePhysRes = ((ICombatEntityState)member).PhysicsResistPercent;

        harness.EnqueueCommand(53, new CreatePartyZoneCommand(captain.CharacterId, captainHandle.LeaseEpoch));
        await harness.StepFramesAsync(1);
        harness.EnqueueCommand(53, new ApplyJoinPartyZoneCommand(member.CharacterId, memberHandle.LeaseEpoch, captain.RuntimeNpcId));
        await harness.StepFramesAsync(1);
        harness.EnqueueCommand(53, new AcceptPartyMemberZoneCommand(captain.CharacterId, captainHandle.LeaseEpoch, member.RuntimeNpcId));
        await harness.StepFramesAsync(1);

        // Toggle 86 -> 89 -> 92 -> 159 (Thất Tinh Trận -> child 211, StateSpecialId 20)
        harness.EnqueueCommand(53, new ChangeAuraSkillZoneCommand(captain.CharacterId, captainHandle.LeaseEpoch, 86));
        await harness.StepFramesAsync(12);
        Assert.Contains(captainHandle.Recorder.SkillCastBroadcasts, e => e.CastDirectly && e.SkillId == 206u && e.ExtraFlag == 0);
        Assert.Contains(memberHandle.Recorder.SyncStateEffects, e => e.NpcId == member.RuntimeNpcId && e.SkillId == 206);

        captainHandle.Recorder.Clear();
        memberHandle.Recorder.Clear();
        harness.EnqueueCommand(53, new ChangeAuraSkillZoneCommand(captain.CharacterId, captainHandle.LeaseEpoch, 159));
        await harness.StepFramesAsync(12);

        // Both CastAura 10-frame (0x85 211 + 0x87 211 + 0x7A 20) and PartyAuraTickSystem 6-frame (0x87 159 attr 114=+10) emit cleanly
        Assert.Contains(captainHandle.Recorder.SkillCastBroadcasts, e => e.CastDirectly && e.SkillId == 211u && e.ExtraFlag == 0);
        Assert.Contains(captainHandle.Recorder.NpcStateInfoBroadcasts, e => e.NpcId == captain.RuntimeNpcId && e.StateSpecialIds.Contains((byte)20));
        Assert.Contains(memberHandle.Recorder.SyncStateEffects, e => e.NpcId == member.RuntimeNpcId && e.SkillId == 211);
        Assert.Contains(
            memberHandle.Recorder.SyncStateEffects,
            e => e.NpcId == member.RuntimeNpcId &&
                 e.SkillId == 159 &&
                 e.RemainTimeFrames == 180 &&
                 e.MagicAttribs.Any(a => a.AttributeType == CombatConsts.AllResistPercentAttributeId && a.Value0 == 10));
        Assert.NotNull(member.StatusEffects.FindStateSkillEffect(159));
        Assert.True(member.SkillCombatModifiers.PhysicsResistPercent >= 10, $"Expected member SkillCombatModifiers.PhysicsResistPercent >= 10, got {member.SkillCombatModifiers.PhysicsResistPercent}");
        Assert.True(((ICombatEntityState)member).PhysicsResistPercent >= memberBasePhysRes + 10, $"Expected member PhysicsResistPercent >= {memberBasePhysRes + 10}, got {((ICombatEntityState)member).PhysicsResistPercent}");

        // Hidden captain (713) must stop radiating party aura 0x87
        captain.SetLearnedSkill(713, 10, 0);
        captain.FightMode = 1;
        captainHandle.CastSkill(713, captain.CurrentMpsX, captain.CurrentMpsY);
        await harness.StepFramesAsync(1);
        Assert.True(((ICombatEntityState)captain).HideMode > 0);
        memberHandle.Recorder.Clear();
        await harness.StepFramesAsync(12);
        Assert.DoesNotContain(memberHandle.Recorder.SyncStateEffects, e => e.SkillId == 159);

        // Reveal captain before switching to 712
        captainHandle.CastSkill(86, captain.CurrentMpsX, captain.CurrentMpsY);
        await harness.StepFramesAsync(1);
        Assert.Equal(0, ((ICombatEntityState)captain).HideMode);

        // Bế Nguyệt Phất Trần (712 -> 718 with skill_appendskill)
        captainHandle.Recorder.Clear();
        harness.EnqueueCommand(53, new ChangeAuraSkillZoneCommand(captain.CharacterId, captainHandle.LeaseEpoch, 712));
        await harness.StepFramesAsync(12);
        Assert.Contains(captainHandle.Recorder.SkillCastBroadcasts, e => e.CastDirectly && e.SkillId == 718u && e.ExtraFlag == 0);
        Assert.True(captain.EffectiveMaxLife > captain.MaxLife, "712->718 appending 92 (lifemax_p) should increase EffectiveMaxLife.");
    }

    [Fact]
    public async Task CurseMissiles_Missle20And290_WithThreeMonstersAndEnemyPlayer_ShouldEmit0x5A0x870x7AAndReduceResists()
    {
        MapDescriptor arena = CreateArenaMap(
            (52128, 101696, ElementSeries.Metal, "Quái Bùa 1"),
            (52144, 101696, ElementSeries.Wood, "Quái Bùa 2"),
            (52128, 101712, ElementSeries.Fire, "Quái Bùa 3"));

        ZoneSimulationHarness harness = CreateHarness(42, arena);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle casterHandle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "CurseCaster",
                ElementSeries.Wood,
                52032,
                101696,
                [
                    new CharacterSkillDto(64, 10, 0),
                    new CharacterSkillDto(67, 10, 0),
                    new CharacterSkillDto(70, 10, 0),
                    new CharacterSkillDto(72, 10, 0),
                    new CharacterSkillDto(73, 10, 0),
                    new CharacterSkillDto(356, 10, 0),
                    new CharacterSkillDto(390, 10, 0),
                    new CharacterSkillDto(90, 10, 0),
                    new CharacterSkillDto(174, 10, 0),
                    new CharacterSkillDto(175, 10, 0),
                    new CharacterSkillDto(392, 10, 0),
                    new CharacterSkillDto(393, 10, 0),
                    new CharacterSkillDto(394, 10, 0),
                ]));

        SimulatedPlayerHandle enemyHandle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto("CurseTarget", ElementSeries.Earth, 52128, 101696, []));

        PlayerZoneState caster = casterHandle.GetZoneState()!;
        PlayerZoneState enemy = enemyHandle.GetZoneState()!;
        caster.BaseCamp = EntityCamp.Justice;
        caster.CurrentCamp = EntityCamp.Justice;
        caster.FightMode = 1;
        caster.PkState = PlayerPkMode.Fight;
        enemy.FightMode = 1;
        enemy.PkState = PlayerPkMode.Fight;
        enemy.BaseCamp = EntityCamp.Evil;
        enemy.CurrentCamp = EntityCamp.Evil;

        casterHandle.Recorder.Clear();
        enemyHandle.Recorder.Clear();

        // Cast 390 (allres_p < 0, Missle 20, spec=12) and 64 (coldres_p < 0, Missle 20, spec=8) and 174 (fastwalkrun_p < 0, spec=66) and 392 (Missle 290, spec=63)
        casterHandle.CastSkill(390, 52128, 101696);
        await harness.StepFramesAsync(3);
        casterHandle.CastSkill(64, 52128, 101696);
        await harness.StepFramesAsync(3);
        casterHandle.CastSkill(174, 52128, 101696);
        await harness.StepFramesAsync(3);
        casterHandle.CastSkill(392, 52128, 101696);
        await harness.StepFramesAsync(3);

        Assert.Contains(casterHandle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 390u);
        Assert.Contains(enemyHandle.Recorder.SyncStateEffects, e => e.NpcId == enemy.RuntimeNpcId && e.SkillId == 390 && e.RemainTimeFrames > 0);
        Assert.Contains(enemyHandle.Recorder.SyncStateEffects, e => e.NpcId == enemy.RuntimeNpcId && e.SkillId == 64 && e.RemainTimeFrames > 0);
        Assert.Contains(enemyHandle.Recorder.SyncStateEffects, e => e.NpcId == enemy.RuntimeNpcId && e.SkillId == 174 && e.RemainTimeFrames > 0);
        Assert.Contains(enemyHandle.Recorder.SyncStateEffects, e => e.NpcId == enemy.RuntimeNpcId && e.SkillId == 392 && e.RemainTimeFrames > 0);

        Assert.Contains(
            enemyHandle.Recorder.NpcStateInfoBroadcasts,
            e => e.NpcId == enemy.RuntimeNpcId &&
                 e.StateSpecialIds.Contains((byte)12) &&
                 e.StateSpecialIds.Contains((byte)8) &&
                 e.StateSpecialIds.Contains((byte)66) &&
                 e.StateSpecialIds.Contains((byte)63));

        ICombatEntityState enemyCombat = enemy;
        Assert.True(enemyCombat.ColdResistPercent < 0, $"Expected negative ColdResistPercent after 64+390, got {enemyCombat.ColdResistPercent}");
        Assert.True(enemyCombat.PhysicsResistPercent < 0, $"Expected negative PhysicsResistPercent after 390, got {enemyCombat.PhysicsResistPercent}");
        Assert.True(enemy.EffectiveRunSpeed < enemy.RunSpeed, "Curse 174 should reduce EffectiveRunSpeed.");

        // Verify all 3 clustered monsters also received the Missle 20 AoE curse
        Assert.True(worker.StaticNpcs[1u].CombatModifiers.ColdResistPercent < 0);
        Assert.True(worker.StaticNpcs[2u].CombatModifiers.ColdResistPercent < 0);
        Assert.True(worker.StaticNpcs[3u].CombatModifiers.ColdResistPercent < 0);
    }

    [Fact]
    public async Task Style2Buffs_Priority6Slot0x7AExpirationAndNoClearOnDeath_WithThreeMonsters_ShouldTickExpireAndPreserveOnRevive()
    {
        SharedGameAssetsFixture assets = SharedGameAssetsFixture.Default;
        SkillTemplate shortBuffBase = RequireSkill(assets.SkillTemplateCatalog, 42);
        var shortTimedBuff = shortBuffBase with
        {
            Id = 9942,
            Code = "test-short-kim-chung-trao",
            StateSpecialId = 49,
            StatePriority = 3,
            EffectScaling =
            [
                new SkillEffectScalingTemplate(
                    104,
                    "physicsres_p",
                    "state",
                    new SkillParameterCurveTemplate("Linear", 35, 0),
                    new SkillParameterCurveTemplate("Linear", 5, 0),
                    null),
            ],
        };

        var noClearOnDeathBuff = shortBuffBase with
        {
            Id = 9943,
            Code = "test-noclear-buff",
            StateSpecialId = 77,
            StatePriority = 9,
            NoClearOnDeath = true,
            EffectScaling =
            [
                new SkillEffectScalingTemplate(
                    102,
                    "fireres_p",
                    "state",
                    new SkillParameterCurveTemplate("Linear", 25, 0),
                    new SkillParameterCurveTemplate("Linear", 500, 0),
                    null),
            ],
        };

        var overlayCatalog = new OverlaySkillTemplateCatalog(
            assets.SkillTemplateCatalog,
            shortTimedBuff,
            noClearOnDeathBuff);

        MapDescriptor arena = CreateArenaMap(
            (52100, 101696, ElementSeries.Metal, "Quái Buff 1"),
            (52130, 101696, ElementSeries.Wood, "Quái Buff 2"),
            (52160, 101696, ElementSeries.Fire, "Quái Buff 3"));

        ZoneSimulationHarness harness = CreateHarness(42, arena, skillCatalog: overlayCatalog);
        ConfigurePassiveMonsters(harness.GetZone(53));

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "BuffTester",
                ElementSeries.Metal,
                52032,
                101696,
                [
                    new CharacterSkillDto(9942, 1, 0),
                    new CharacterSkillDto(9943, 1, 0),
                    new CharacterSkillDto(16, 10, 0),
                    new CharacterSkillDto(33, 5, 0),
                    new CharacterSkillDto(100, 5, 0),
                    new CharacterSkillDto(109, 5, 0),
                    new CharacterSkillDto(130, 5, 0),
                    new CharacterSkillDto(150, 5, 0),
                    new CharacterSkillDto(173, 5, 0),
                    new CharacterSkillDto(1508, 10, 0),
                    new CharacterSkillDto(157, 20, 0),
                ]));

        SimulatedPlayerHandle enemyL20Handle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "CurserL20",
                ElementSeries.Wood,
                52064,
                101696,
                [new CharacterSkillDto(390, 20, 0)]));

        SimulatedPlayerHandle enemyL1Handle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "CurserL1",
                ElementSeries.Wood,
                52064,
                101696,
                [new CharacterSkillDto(390, 1, 0)]));

        PlayerZoneState state = handle.GetZoneState()!;
        PlayerZoneState enemyL20 = enemyL20Handle.GetZoneState()!;
        PlayerZoneState enemyL1 = enemyL1Handle.GetZoneState()!;
        state.BaseCamp = EntityCamp.Justice;
        state.CurrentCamp = EntityCamp.Justice;
        state.FightMode = 1;
        state.PkState = PlayerPkMode.Fight;
        state.SpawnProtectUntilTick = 0;
        enemyL20.BaseCamp = EntityCamp.Evil;
        enemyL20.CurrentCamp = EntityCamp.Evil;
        enemyL20.FightMode = 1;
        enemyL20.PkState = PlayerPkMode.Fight;
        enemyL1.BaseCamp = EntityCamp.Evil;
        enemyL1.CurrentCamp = EntityCamp.Evil;
        enemyL1.FightMode = 1;
        enemyL1.PkState = PlayerPkMode.Fight;

        // Non-downgrade check on StateSpecialId>0 curse (390 L20 not overwritten by L1)
        enemyL20Handle.CastSkill(390, state.CurrentMpsX, state.CurrentMpsY);
        await harness.StepFramesAsync(3);
        ActiveStateSkillEffect? curse390 = state.StatusEffects.FindStateSkillEffect(390);
        Assert.NotNull(curse390);
        Assert.Equal(20, curse390!.SkillLevel);
        int resAfterL20 = ((ICombatEntityState)state).ColdResistPercent;

        enemyL1Handle.CastSkill(390, state.CurrentMpsX, state.CurrentMpsY);
        await harness.StepFramesAsync(3);
        Assert.Equal(20, state.StatusEffects.FindStateSkillEffect(390)!.SkillLevel);
        Assert.Equal(resAfterL20, ((ICombatEntityState)state).ColdResistPercent);

        // Cleanse 390 for clean buff slot tests
        handle.GetZoneState()!.StatusEffects.RemoveStateSkillEffect(390);
        SkillStateEffectApplier.RecalculateEntitySkillModifiers(state, overlayCatalog);
        handle.Recorder.Clear();

        // Non-downgrade & equal-level refresh on StateSpecialId>0 buff (157 L20 not overwritten by L1)
        handle.CastSkill(157, state.CurrentMpsX, state.CurrentMpsY);
        await harness.StepFramesAsync(3);
        Assert.Equal(20, state.StatusEffects.FindStateSkillEffect(157)!.SkillLevel);
        int manaShieldL20 = ((ICombatEntityState)state).ManaShieldPercent;
        int remainingBeforeRefresh = state.StatusEffects.FindStateSkillEffect(157)!.RemainingFrames;

        state.SetLearnedSkill(157, 1, 0);
        handle.CastSkill(157, state.CurrentMpsX, state.CurrentMpsY);
        await harness.StepFramesAsync(1);
        Assert.Equal(20, state.StatusEffects.FindStateSkillEffect(157)!.SkillLevel);
        Assert.Equal(manaShieldL20, ((ICombatEntityState)state).ManaShieldPercent);

        // Equal-level refresh restores full duration and emits 0x87
        state.SetLearnedSkill(157, 20, 0);
        handle.Recorder.Clear();
        handle.CastSkill(157, state.CurrentMpsX, state.CurrentMpsY);
        await harness.StepFramesAsync(1);
        Assert.True(state.StatusEffects.FindStateSkillEffect(157)!.RemainingFrames > remainingBeforeRefresh);
        Assert.Contains(handle.Recorder.SyncStateEffects, e => e.NpcId == state.RuntimeNpcId && e.SkillId == 157 && e.Level == 20);

        state.StatusEffects.RemoveStateSkillEffect(157);
        SkillStateEffectApplier.RecalculateEntitySkillModifiers(state, overlayCatalog);
        handle.Recorder.Clear();

        // Capture base FireResistPercent before casting temporary Style=2 buffs (like 173)
        int beforeFireRes = ((ICombatEntityState)state).FireResistPercent;

        // Cast 7 concurrent StateSpecialId>0 buffs to test 6-slot priority ordering (33:46, 100:31, 109:33, 130:43, 150:30, 173:13, 1508:236 prio=2)
        int[] buffIds = [33, 100, 109, 130, 150, 173, 1508];
        for (int i = 0; i < buffIds.Length; i++)
        {
            handle.CastSkill((ushort)buffIds[i], state.CurrentMpsX, state.CurrentMpsY);
            await harness.StepFramesAsync(1);
        }

        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => !e.CastDirectly && e.SkillId == 1508u);
        Assert.Contains(handle.Recorder.SyncStateEffects, e => e.NpcId == state.RuntimeNpcId && e.SkillId == 1508 && e.RemainTimeFrames > 0);
        Assert.True(state.SkillCombatModifiers.AttackSpeedBonus > 0);
        Assert.True(state.SkillCombatModifiers.MeleeDamageReturnPercent > 0);
        Assert.True(state.EffectiveStrength > state.Strength, "1508 should add strength_v to EffectiveStrength.");

        // Highest priority (prio 3) buffs occupy slots before prio 2 (1508 -> 236 displaced by 6 prio-3 buffs)
        byte[] lastSlots = handle.Recorder.NpcStateInfoBroadcasts[^1].StateSpecialIds;
        Assert.Equal(6, lastSlots.Length);
        Assert.DoesNotContain((byte)236, lastSlots);

        // Short 5-frame buff 9942 expiration
        int beforePhysicsRes = ((ICombatEntityState)state).PhysicsResistPercent;
        handle.Recorder.Clear();
        handle.CastSkill(9942, state.CurrentMpsX, state.CurrentMpsY);
        await harness.StepFramesAsync(1);
        Assert.Contains(handle.Recorder.SyncStateEffects, e => e.NpcId == state.RuntimeNpcId && e.SkillId == 9942 && e.RemainTimeFrames == 5);
        Assert.Equal(beforePhysicsRes + 35, ((ICombatEntityState)state).PhysicsResistPercent);

        handle.Recorder.Clear();
        await harness.StepFramesAsync(6);
        Assert.DoesNotContain(handle.Recorder.SyncStateEffects, e => e.NpcId == state.RuntimeNpcId && e.SkillId == 9942);
        Assert.Equal(beforePhysicsRes, ((ICombatEntityState)state).PhysicsResistPercent);

        // Grant, cast, and verify NoClearOnDeath buff 9943 + ActiveAura 16 (StateSpecialId 45) across real HandlePlayerDeath + Revive.
        // Step 3 frames so immediate CastAura child missile 92 (LifeTime=2, AutoExplode=1) completes its 3-tick vanish before death/same-spot revive while child state 202 remains active.
        harness.EnqueueCommand(53, new ChangeAuraSkillZoneCommand(state.CharacterId, handle.LeaseEpoch, 16));
        await harness.StepFramesAsync(3);
        Assert.Equal(16, state.ActiveAuraSkillId);
        Assert.NotNull(state.StatusEffects.FindStateSkillEffect(202));

        int activeFireResBefore9943 = ((ICombatEntityState)state).FireResistPercent;
        handle.Recorder.Clear();
        handle.CastSkill(9943, state.CurrentMpsX, state.CurrentMpsY);
        await harness.StepFramesAsync(1);
        Assert.Contains(handle.Recorder.SyncStateEffects, e => e.NpcId == state.RuntimeNpcId && e.SkillId == 9943 && e.NoClearOnDeath);
        Assert.Equal(activeFireResBefore9943 + 25, ((ICombatEntityState)state).FireResistPercent);
        Assert.Contains((byte)77, handle.Recorder.NpcStateInfoBroadcasts[^1].StateSpecialIds);

        // Real death via ApplyDamageToPlayer -> HandlePlayerDeath clears normal Style=2 buffs (emitting 0x87 RemainTimeFrames=0) & ActiveAuraSkillId (45) while preserving 9943
        handle.Recorder.Clear();
        harness.GetZone(53).ApplyDamageToPlayer(enemyL20.RuntimeNpcId, state, state.CurrentLife + 5000);
        Assert.Equal(EntityDoingPosture.Death, state.Doing);
        Assert.Equal(0, state.ActiveAuraSkillId);
        Assert.Contains(handle.Recorder.SyncStateEffects, e => e.NpcId == state.RuntimeNpcId && e.RemainTimeFrames == 0);
        Assert.DoesNotContain((byte)45, state.StatusEffects.StateSpecialIds.ToArray());
        Assert.Contains((byte)77, state.StatusEffects.StateSpecialIds.ToArray());

        // Revive preserves 9943 (fireres_p + StateSpecialId 77) while keeping normal buffs & ActiveAuraSkillId cleared
        handle.Recorder.Clear();
        harness.EnqueueCommand(53, new RevivePlayerZoneCommand(state.CharacterId, handle.LeaseEpoch, 0));
        await harness.StepFramesAsync(1);

        Assert.Equal(0, state.SkillCombatModifiers.AttackSpeedBonus);
        Assert.Equal(0, state.ActiveAuraSkillId);
        Assert.NotNull(state.StatusEffects.FindStateSkillEffect(9943));
        Assert.Equal(beforeFireRes + 25, ((ICombatEntityState)state).FireResistPercent);
        Assert.Contains((byte)77, state.StatusEffects.StateSpecialIds.ToArray());
        Assert.DoesNotContain((byte)45, state.StatusEffects.StateSpecialIds.ToArray());
    }

    [Fact]
    public async Task BlockRate_Blocking1585AndStyle2Curse_WithThreeMonsters_ShouldNotApplyDebuffAndPreserve0x7ASlots()
    {
        MapDescriptor arena = CreateArenaMap(
            (52100, 101696, ElementSeries.Metal, "Quái Block 1"),
            (52130, 101696, ElementSeries.Wood, "Quái Block 2"),
            (52160, 101696, ElementSeries.Fire, "Quái Block 3"));

        SharedGameAssetsFixture assets = SharedGameAssetsFixture.Default;
        SkillTemplate style2Base = RequireSkill(assets.SkillTemplateCatalog, 390);
        var style2EnemyCurse = style2Base with
        {
            Id = 9585,
            Code = "test-style2-enemy-curse",
            Style = SkillStyleKind.TargetBuff,
            IsOffensive = true,
            IsMelee = false,
            MissileId = 0,
            StateSpecialId = 63,
            Targeting = new SkillTargetingTemplate(false, true, false, false, false, false, false),
        };
        var overlayCatalog = new OverlaySkillTemplateCatalog(assets.SkillTemplateCatalog, style2EnemyCurse);

        ZoneSimulationHarness harness = CreateHarness(42, arena, skillCatalog: overlayCatalog);
        ConfigurePassiveMonsters(harness.GetZone(53));

        SimulatedPlayerHandle defenderHandle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "BlockDefender",
                ElementSeries.Water,
                52032,
                101696,
                [new CharacterSkillDto(157, 10, 0)]));

        SimulatedPlayerHandle attackerHandle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "BlockAttacker",
                ElementSeries.Earth,
                52064,
                101696,
                [
                    new CharacterSkillDto(1585, 10, 0),
                    new CharacterSkillDto(9585, 10, 0),
                ]));

        PlayerZoneState defender = defenderHandle.GetZoneState()!;
        PlayerZoneState attacker = attackerHandle.GetZoneState()!;
        defender.BaseCamp = EntityCamp.Justice;
        defender.CurrentCamp = EntityCamp.Justice;
        defender.FightMode = 1;
        defender.PkState = PlayerPkMode.Fight;
        defender.SpawnProtectUntilTick = 0;
        attacker.BaseCamp = EntityCamp.Evil;
        attacker.CurrentCamp = EntityCamp.Evil;
        attacker.FightMode = 1;
        attacker.PkState = PlayerPkMode.Fight;

        // Defender activates ManaShield 157 (StateSpecialId=19) and has 100% BlockRatePercent
        defenderHandle.CastSkill(157, defender.CurrentMpsX, defender.CurrentMpsY);
        await harness.StepFramesAsync(1);
        defender.EquipmentCombatModifiers.AddAttribute(0, "addblockrate", 100, 0, 0);
        int baseDefense = defender.EffectiveDefense;
        int basePhysRes = ((ICombatEntityState)defender).PhysicsResistPercent;

        // 1. Enemy casts melee-tagged 1585 directly at defender -> blocked -> must NOT apply debuff, must preserve 19 alongside 159 in 0x7A
        defenderHandle.Recorder.Clear();
        attackerHandle.CastSkill(1585, -1, (int)defender.RuntimeNpcId);
        await harness.StepFramesAsync(10);

        Assert.Null(defender.StatusEffects.FindStateSkillEffect(1585));
        Assert.Equal(baseDefense, defender.EffectiveDefense);
        Assert.Equal(basePhysRes, ((ICombatEntityState)defender).PhysicsResistPercent);
        Assert.Contains(
            defenderHandle.Recorder.NpcStateInfoBroadcasts,
            b => b.NpcId == defender.RuntimeNpcId &&
                 b.StateSpecialIds.Contains(CombatConsts.BlockStateSpecialId) &&
                 b.StateSpecialIds.Contains((byte)19));

        // 2. Enemy casts Style=2 offensive curse (9585) -> blocked -> must NOT apply debuff
        defenderHandle.Recorder.Clear();
        attackerHandle.CastSkill(9585, -1, (int)defender.RuntimeNpcId);
        await harness.StepFramesAsync(2);

        Assert.Null(defender.StatusEffects.FindStateSkillEffect(9585));
        Assert.Equal(basePhysRes, ((ICombatEntityState)defender).PhysicsResistPercent);
        Assert.Contains(
            defenderHandle.Recorder.NpcStateInfoBroadcasts,
            b => b.NpcId == defender.RuntimeNpcId &&
                 b.StateSpecialIds.Contains(CombatConsts.BlockStateSpecialId) &&
                 b.StateSpecialIds.Contains((byte)19));
    }

    [Fact]
    public async Task ManaShield157_Hide713_AndCounterCurses716_WithThreeAggressiveMonsters_ShouldEnforceCollapseStealthAndCleanse()
    {
        MapDescriptor arena = CreateArenaMap(
            (52064, 101696, ElementSeries.Metal, "Quái Chủ Động 1"),
            (52096, 101696, ElementSeries.Wood, "Quái Chủ Động 2"),
            (52064, 101728, ElementSeries.Fire, "Quái Chủ Động 3"));

        ZoneSimulationHarness harness = CreateHarness(42, arena);
        MapZoneWorker worker = harness.GetZone(53);
        Assert.Equal(3, worker.StaticNpcs.Count);
        foreach (NpcZoneState npc in worker.StaticNpcs.Values)
        {
            npc.AiMode = 3;
            npc.VisionRadiusMps = 400;
            npc.AttackRadiusMps = 128;
            npc.PhysicsMinDamage = 200;
            npc.PhysicsMaxDamage = 200;
            npc.CurLife = 10_000;
        }

        SimulatedPlayerHandle casterHandle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "DefenseTester",
                ElementSeries.Water,
                52032,
                101696,
                [
                    new CharacterSkillDto(157, 10, 0),
                    new CharacterSkillDto(713, 10, 0),
                    new CharacterSkillDto(716, 20, 0),
                    new CharacterSkillDto(1382, 10, 0),
                ],
                life: 5000,
                mana: 500));

        SimulatedPlayerHandle enemyHandle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "EnemyCurser",
                ElementSeries.Wood,
                52064,
                101696,
                [new CharacterSkillDto(390, 20, 0)]));

        PlayerZoneState player = casterHandle.GetZoneState()!;
        PlayerZoneState enemy = enemyHandle.GetZoneState()!;
        player.BaseCamp = EntityCamp.Justice;
        player.CurrentCamp = EntityCamp.Justice;
        player.FightMode = 1;
        player.PkState = PlayerPkMode.Fight;
        enemy.FightMode = 0;
        enemy.PkState = PlayerPkMode.Fight;
        enemy.BaseCamp = EntityCamp.Evil;
        enemy.CurrentCamp = EntityCamp.Evil;

        // 1. Hide (713) stealth: aggressive monsters ignore hidden player AND AOI observer receives 0x4F (NpcRemoves)
        enemyHandle.Recorder.Clear();
        casterHandle.CastSkill(713, player.CurrentMpsX, player.CurrentMpsY);
        await harness.StepFramesAsync(1);
        Assert.True(((ICombatEntityState)player).HideMode > 0);
        Assert.Contains(player.RuntimeNpcId, enemyHandle.Recorder.NpcRemoves);
        int lifeBeforeHiddenWait = player.CurrentLife;
        await harness.StepFramesAsync(20);
        Assert.Equal(lifeBeforeHiddenWait, player.CurrentLife);
        Assert.Equal(3, worker.StaticNpcs.Count);
        foreach (NpcZoneState npc in worker.StaticNpcs.Values)
        {
            Assert.Equal(0u, npc.AggroTargetNpcId);
        }

        // Reveal via DoSkill (1382): emits 0x87 RemainTimeFrames=0 for 713, restores HideMode=0, and sends AOI full sync to observer
        casterHandle.Recorder.Clear();
        enemyHandle.Recorder.Clear();
        casterHandle.CastSkill(1382, 52064, 101696);
        await harness.StepFramesAsync(1);
        Assert.Equal(0, ((ICombatEntityState)player).HideMode);
        Assert.Contains(casterHandle.Recorder.SyncStateEffects, e => e.NpcId == player.RuntimeNpcId && e.SkillId == 713 && e.RemainTimeFrames == 0);
        Assert.Contains(enemyHandle.Recorder.FullSyncs, s => s.NpcId == player.RuntimeNpcId);

        // Reveal via equipping/mounting a horse (SetRideHorse): clears 713 and broadcasts AOI full sync
        player.SetSkillCooldown(713, 0);
        casterHandle.CastSkill(713, player.CurrentMpsX, player.CurrentMpsY);
        await harness.StepFramesAsync(1);
        Assert.True(((ICombatEntityState)player).HideMode > 0);
        var horseTpl = new ItemTemplate(
            Id: 9199,
            Code: "test-horse",
            Name: "Chiếu Dạ Ngọc Sư Tử",
            Genre: ItemGenreKind.Equipment,
            Quality: ItemQualityKind.Normal,
            DetailType: 10,
            ParticularType: 0,
            Level: 1,
            ElementSeries: ElementSeries.Metal,
            GridWidthCells: 1,
            GridHeightCells: 1,
            ObjDataId: 1,
            AppearanceResId: 1,
            PriceGold: 100,
            MaxStackCount: 1,
            BaseAttributes: [],
            Requirements: []);
        var horseSlot = new PlayerItemSlot(
            runtimeItemId: 999u,
            domainItemId: Guid.NewGuid(),
            template: horseTpl,
            genre: ItemGenreKind.Equipment,
            detailType: 10,
            particularType: 0,
            level: 1,
            series: ElementSeries.Metal,
            quality: ItemQualityKind.Normal,
            randomSeed: 1u,
            generatorVersion: ItemConsts.GeneratorVersion,
            luck: 0,
            currentDurability: 100,
            maxDurability: 100,
            stackCount: 1,
            containerPlace: ItemContainerPlace.EquipRoom,
            gridX: 5,
            gridY: 0);
        byte horseGridX = 0;
        byte horseGridY = 0;
        bool placedHorse = false;
        for (byte y = 0; y < 10 && !placedHorse; y++)
        {
            for (byte x = 0; x < 6 && !placedHorse; x++)
            {
                if (player.Inventory.TryPlaceItemDirect(horseSlot, ItemContainerPlace.EquipRoom, x, y, player))
                {
                    horseGridX = x;
                    horseGridY = y;
                    placedHorse = true;
                }
            }
        }
        Assert.True(placedHorse);
        casterHandle.Recorder.Clear();
        enemyHandle.Recorder.Clear();
        harness.EnqueueCommand(53, new MoveInventoryItemZoneCommand(
            player.CharacterId,
            casterHandle.LeaseEpoch,
            ItemContainerPlace.EquipRoom,
            horseGridX,
            horseGridY,
            ItemContainerPlace.Equip,
            (byte)EquipmentPartKind.Horse,
            0));
        await harness.StepFramesAsync(1);
        Assert.Equal(0, ((ICombatEntityState)player).HideMode);
        Assert.Contains(casterHandle.Recorder.SyncStateEffects, e => e.NpcId == player.RuntimeNpcId && e.SkillId == 713 && e.RemainTimeFrames == 0);
        Assert.Contains(enemyHandle.Recorder.FullSyncs, s => s.NpcId == player.RuntimeNpcId);

        // 2. ManaShield (157, 83% absorb) collapse when CurrentMana < manaDmg
        player.CurrentMana = 500;
        player.CurrentLife = 5000;
        casterHandle.CastSkill(157, player.CurrentMpsX, player.CurrentMpsY);
        await harness.StepFramesAsync(1);
        Assert.Equal(83, ((ICombatEntityState)player).ManaShieldPercent);

        player.CurrentMana = 30; // < 200 * 83 / 100 = 166
        var rawPayload = new SkillDamagePayload
        {
            SkillId = 1,
            SkillLevel = 1,
            IsPhysical = true,
            PhysicsMinDamage = 200,
            PhysicsMaxDamage = 200,
            DamageRatePercent = 100,
            UseAttackRating = false,
            IgnoreDefensePercent = 100,
        };
        CombatHitResolutionResult shieldCollapse = CombatDamageCalculator.ResolveHitAndApplyDamage(
            enemy,
            player,
            isPvp: false,
            in rawPayload,
            new Random(42));
        Assert.Equal(0, player.CurrentMana);
        Assert.Equal(200, shieldCollapse.TotalHpDamageDealt);

        // 3. Curse 390 then cleanse + immunity via 716 -> 738 (ignorenegativestate_p = 100%)
        enemy.FightMode = 1;
        enemyHandle.CastSkill(390, player.CurrentMpsX, player.CurrentMpsY);
        await harness.StepFramesAsync(3);
        Assert.True(((ICombatEntityState)player).FireResistPercent < 0);

        casterHandle.Recorder.Clear();
        player.CurrentMana = 500;
        casterHandle.CastSkill(716, player.CurrentMpsX, player.CurrentMpsY);
        await harness.StepFramesAsync(3);
        Assert.Contains(casterHandle.Recorder.SyncStateEffects, e => e.NpcId == player.RuntimeNpcId && e.SkillId == 390 && e.RemainTimeFrames == 0);
        Assert.True(((ICombatEntityState)player).FireResistPercent >= 0);
        Assert.True(((ICombatEntityState)player).IgnoreNegativeStatePercent >= 100);
    }

    [Fact]
    public async Task Style3Passives_AndAutoCastAnd1349ManaClamp_WithThreeMonsters_ShouldPopulateSkillModifiersAndEmit0x87And0x85()
    {
        MapDescriptor arena = CreateArenaMap(
            (52064, 101696, ElementSeries.Metal, "Quái Nội Công 1"),
            (52096, 101696, ElementSeries.Wood, "Quái Nội Công 2"),
            (52128, 101696, ElementSeries.Earth, "Quái Nội Công 3"));

        ZoneSimulationHarness harness = CreateHarness(42, arena);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "PassiveWarrior",
                ElementSeries.Water,
                52032,
                101696,
                [
                    new CharacterSkillDto(1349, 20, 0),
                    new CharacterSkillDto(709, 20, 0),
                    new CharacterSkillDto(711, 20, 0),
                    new CharacterSkillDto(714, 20, 0),
                    new CharacterSkillDto(717, 20, 0),
                    new CharacterSkillDto(1365, 20, 0),
                    new CharacterSkillDto(1364, 20, 0),
                    new CharacterSkillDto(173, 5, 0),
                    new CharacterSkillDto(1081, 1, 0),
                    new CharacterSkillDto(4, 0, 0),
                ],
                level: 150,
                life: 8000,
                mana: 5000,
                unallocatedSkillPoints: 5));

        PlayerZoneState state = handle.GetZoneState()!;
        CreateAndEquipWeapon(
            state,
            runtimeItemId: 1u,
            particularType: 1,
            minDamage: 100,
            maxDamage: 100,
            new ItemMagicAttributeSlot(SlotIndex: 0, AttributeId: 260, Value0: 1081, Value1: 0, Value2: 30));

        // 1. Login computed Style=3 passives into SkillCombatModifiers, left EquipmentCombatModifiers clean, clamped EffectiveMaxMana >= 0 (1349: -20000)
        Assert.True(state.SkillCombatModifiers.DeadlyStrikeEnhancePercent >= 26);
        Assert.True(state.SkillCombatModifiers.ReturnSkillPercent >= 56);
        Assert.True(state.SkillCombatModifiers.PoisonDamageReturnPercent >= 45);
        Assert.Equal(0, state.EquipmentCombatModifiers.DeadlyStrikeEnhancePercent);
        Assert.Equal(0, state.EffectiveMaxMana);
        Assert.Contains(
            handle.Recorder.SyncStateEffects,
            e => e.NpcId == state.RuntimeNpcId && e.SkillId == 1349 && e.RemainTimeFrames == -1);
        Assert.Contains(
            handle.Recorder.SyncStateEffects,
            e => e.NpcId == state.RuntimeNpcId && e.SkillId == 709 && e.RemainTimeFrames == -1);

        // Skill 173 (SelfBuff) addskilldamage1 on 1081 must NOT leak before casting (30 from weapon only) and must NOT double-count (45, not 60) after casting
        SharedGameAssetsFixture assets = SharedGameAssetsFixture.Default;
        Assert.Equal(30, CombatDamageCalculator.ComputeSynergyAddDamagePercent(state, 1081, assets.SkillTemplateCatalog));
        state.FightMode = 1;
        state.SkillCombatModifiers.ManaMaxFlat = 0; // temporarily allow mana to cast 173 & 1364
        state.CurrentMana = 500;
        handle.CastSkill(173, state.CurrentMpsX, state.CurrentMpsY);
        await harness.StepFramesAsync(1);
        Assert.Equal(45, CombatDamageCalculator.ComputeSynergyAddDamagePercent(state, 1081, assets.SkillTemplateCatalog));

        // 2. Allocate skill point into passive skill 4 (Thiếu Lâm Côn pháp: deadlystrikeenhance_p + addphysicsdamage_p)
        handle.Recorder.Clear();
        harness.EnqueueCommand(53, new AllocateSkillPointZoneCommand(state.CharacterId, handle.LeaseEpoch, 4, 2));
        await harness.StepFramesAsync(1);
        Assert.Contains(
            handle.Recorder.SyncStateEffects,
            e => e.NpcId == state.RuntimeNpcId && e.SkillId == 4 && e.RemainTimeFrames == -1);
        Assert.Equal(0, state.EffectiveMaxMana);

        // 3. Real end-to-end AutoAttack procs (711->719 & 714->720) apply debuffs across >= 3 monsters without manual test injection
        state.FightMode = 1;
        state.BaseCamp = EntityCamp.Justice;
        state.CurrentCamp = EntityCamp.Justice;
        state.PkState = PlayerPkMode.Fight;
        state.SpawnProtectUntilTick = 0;
        handle.Recorder.Clear();

        foreach (NpcZoneState m in worker.StaticNpcs.Values)
        {
            m.MpsX = 52064;
            m.MpsY = 101696;
            m.CurLife = 500_000;
        }

        // Attack clustered monsters repeatedly with real basic melee/projectile hits until 711->719 and 714->720 proc across all 3 monsters
        var got719 = new HashSet<uint>();
        var got720 = new HashSet<uint>();
        for (int attempt = 0; attempt < 80 && (got719.Count < 3 || got720.Count < 3); attempt++)
        {
            foreach (NpcZoneState m in worker.StaticNpcs.Values)
            {
                handle.CastSkill(1, -1, (int)m.NpcId);
                await harness.StepFramesAsync(2);
                if (m.CombatModifiers.Poison2DecManaPercent > 0)
                {
                    got719.Add(m.NpcId);
                }

                if (m.CombatModifiers.PhysicsResistPercent < 0)
                {
                    got720.Add(m.NpcId);
                }
            }
        }

        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => e.CastDirectly && e.SkillId == 719u && e.ExtraFlag == 0);
        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => e.CastDirectly && e.SkillId == 720u && e.ExtraFlag == 0);
        Assert.Equal(3, got719.Count);
        Assert.Equal(3, got720.Count);

        // 4. Two simultaneous AutoRescue procs (717->721 + 1365->1366) fire both without mid-loop wipe, plus 1364 AutoReply (1363)
        handle.CastSkill(1364, state.CurrentMpsX, state.CurrentMpsY);
        await harness.StepFramesAsync(1);
        Assert.NotEmpty(state.SkillCombatModifiers.AutoReplySkills.Entries);

        SimulatedPlayerHandle enemyHandle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "AutoTriggerEnemy",
                ElementSeries.Wood,
                52064,
                101696,
                [
                    new CharacterSkillDto(1, 1, 0),
                    new CharacterSkillDto(390, 20, 0),
                ],
                level: 150));
        PlayerZoneState enemy = enemyHandle.GetZoneState()!;
        enemy.BaseCamp = EntityCamp.Evil;
        enemy.CurrentCamp = EntityCamp.Evil;
        enemy.FightMode = 1;
        enemy.PkState = PlayerPkMode.Fight;
        enemy.SpawnProtectUntilTick = 0;

        CreateAndEquipWeapon(
            enemy,
            runtimeItemId: 50u,
            particularType: 0,
            minDamage: 6000,
            maxDamage: 6000,
            new ItemMagicAttributeSlot(SlotIndex: 0, AttributeId: 58, Value0: 100, Value1: 0, Value2: 0));
        enemy.EquipmentCombatModifiers.AddAttribute(56, "attackrating_v", 50000, 0, 0);

        bool bothSameHit = false;
        for (int round = 0; round < 20 && !bothSameHit; round++)
        {
            state.CurrentLife = state.EffectiveMaxLife;
            state.StatusEffects.RemoveStateSkillEffect(721);
            state.StatusEffects.RemoveStateSkillEffect(1366);
            SkillStateEffectApplier.RecalculateEntitySkillModifiers(state, assets.SkillTemplateCatalog);
            state.SetSkillCooldown(721, 0);
            state.SetSkillCooldown(1366, 0);
            state.LastPingReceivedTick = worker.CurrentFrameTick;
            enemy.LastPingReceivedTick = worker.CurrentFrameTick;
            await harness.StepFramesAsync(185);
            state.LastPingReceivedTick = worker.CurrentFrameTick;
            enemy.LastPingReceivedTick = worker.CurrentFrameTick;
            await harness.StepFramesAsync(180);

            state.CurrentLife = (state.EffectiveMaxLife / 4) + 200; // just above MaxLife/4, drops below on 1 PvP hit
            handle.Recorder.Clear();
            enemy.SetSkillCooldown(1, 0);
            enemyHandle.CastSkill(1, -1, (int)state.RuntimeNpcId);
            await harness.StepFramesAsync(6);

            bool saw721 = handle.Recorder.SkillCastBroadcasts.Any(e => e.CastDirectly && e.SkillId == 721u && e.ExtraFlag == 0);
            bool saw1366 = handle.Recorder.SkillCastBroadcasts.Any(e => e.CastDirectly && e.SkillId == 1366u && e.ExtraFlag == 0);
            bothSameHit = saw721 && saw1366;
        }

        Assert.True(bothSameHit, "Both simultaneous AutoRescue procs (721 & 1366) must fire on the same <25% HP crossing without mid-loop wipe.");

        // Trigger 1364 AutoReply (1363) under enemy hits
        state.CurrentLife = state.EffectiveMaxLife;
        for (int hit = 0; hit < 200 && !handle.Recorder.SkillCastBroadcasts.Any(e => e.CastDirectly && e.SkillId == 1363u); hit++)
        {
            state.CurrentLife = state.EffectiveMaxLife;
            state.LastPingReceivedTick = worker.CurrentFrameTick;
            enemy.LastPingReceivedTick = worker.CurrentFrameTick;
            enemy.SetSkillCooldown(1, 0);
            enemyHandle.CastSkill(1, -1, (int)state.RuntimeNpcId);
            await harness.StepFramesAsync(6);
            if (hit % 20 == 19)
            {
                await harness.StepFramesAsync(140);
                state.LastPingReceivedTick = worker.CurrentFrameTick;
                enemy.LastPingReceivedTick = worker.CurrentFrameTick;
                await harness.StepFramesAsync(140);
            }
        }
        Assert.Contains(handle.Recorder.SkillCastBroadcasts, e => e.CastDirectly && e.SkillId == 1363u && e.ExtraFlag == 0);

        // 5. Counter-curses 725 (returnskill_p via 709) & 724 (ignoreskill_p via 708)
        state.CurrentLife = state.EffectiveMaxLife;
        for (int i = 0; i < 8 && enemy.StatusEffects.FindStateSkillEffect(390) is null; i++)
        {
            enemyHandle.CastSkill(390, state.CurrentMpsX, state.CurrentMpsY);
            await harness.StepFramesAsync(3);
        }
        Assert.NotNull(enemy.StatusEffects.FindStateSkillEffect(390));
        Assert.Contains(handle.Recorder.SyncStateEffects, e => e.NpcId == state.RuntimeNpcId && e.SkillId == 725);

        // Replace 709 with 708 (ignoreskill_p = 89%) and verify 724 avoidance
        SimulatedPlayerHandle ignoreHandle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "IgnoreDefender",
                ElementSeries.Metal,
                52032,
                101696,
                [new CharacterSkillDto(708, 20, 0)],
                level: 150));
        PlayerZoneState ignorePlayer = ignoreHandle.GetZoneState()!;
        ignorePlayer.BaseCamp = EntityCamp.Justice;
        ignorePlayer.CurrentCamp = EntityCamp.Justice;
        ignorePlayer.FightMode = 1;
        ignorePlayer.PkState = PlayerPkMode.Fight;
        ignorePlayer.SpawnProtectUntilTick = 0;
        ignoreHandle.Recorder.Clear();
        for (int i = 0; i < 8 && ignoreHandle.Recorder.SyncStateEffects.All(e => e.SkillId != 724); i++)
        {
            enemyHandle.CastSkill(390, ignorePlayer.CurrentMpsX, ignorePlayer.CurrentMpsY);
            await harness.StepFramesAsync(3);
        }
        Assert.Contains(ignoreHandle.Recorder.SyncStateEffects, e => e.NpcId == ignorePlayer.RuntimeNpcId && e.SkillId == 724);

        // 6. WashSkills clears Style=3 passives and emits 0x87 RemainTimeFrames=0
        handle.Recorder.Clear();
        harness.EnqueueCommand(53, new WashPlayerPointsZoneCommand(state.CharacterId, WashPointsMode.SkillsOnly, handle.LeaseEpoch));
        await harness.StepFramesAsync(1);

        Assert.Contains(
            handle.Recorder.SyncStateEffects,
            e => e.NpcId == state.RuntimeNpcId && e.SkillId == 1349 && e.RemainTimeFrames == 0);
        Assert.Equal(5000, state.EffectiveMaxMana);
    }

    [Fact]
    public async Task Skill1358_1364_1585_AndPassiveNoClearOnDeath_WithThreeMonsters_ShouldApplyModifiersAndPreservePassivesOnRevive()
    {
        MapDescriptor arena = CreateArenaMap(
            (52064, 101696, ElementSeries.Metal, "Quái Hoa Sơn 1"),
            (52096, 101696, ElementSeries.Wood, "Quái Hoa Sơn 2"),
            (52128, 101696, ElementSeries.Earth, "Quái Hoa Sơn 3"));

        ZoneSimulationHarness harness = CreateHarness(42, arena);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "HuaShanMaster",
                ElementSeries.Wood,
                52032,
                101696,
                [
                    new CharacterSkillDto(1349, 20, 0),
                    new CharacterSkillDto(1358, 10, 0),
                    new CharacterSkillDto(1364, 10, 0),
                    new CharacterSkillDto(1365, 20, 0),
                ],
                level: 150,
                life: 8000,
                mana: 5000));

        SimulatedPlayerHandle enemyHandle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "CurseEnemy",
                ElementSeries.Earth,
                52064,
                101696,
                [new CharacterSkillDto(1585, 10, 0)],
                level: 120));

        PlayerZoneState state = handle.GetZoneState()!;
        PlayerZoneState enemy = enemyHandle.GetZoneState()!;
        state.BaseCamp = EntityCamp.Justice;
        state.CurrentCamp = EntityCamp.Justice;
        state.FightMode = 1;
        state.PkState = PlayerPkMode.Fight;
        enemy.BaseCamp = EntityCamp.Evil;
        enemy.CurrentCamp = EntityCamp.Evil;
        enemy.FightMode = 1;
        enemy.PkState = PlayerPkMode.Fight;

        int baseMaxLife = state.EffectiveMaxLife;
        int baseRunSpeed = state.EffectiveRunSpeed;
        int baseDefense = state.EffectiveDefense;
        int basePhysicsResist = ((ICombatEntityState)state).PhysicsResistPercent;

        // 1. Cast Style=2 buffs 1358 (lifemax_yan_p, fastwalkrun_p) & 1364 (AutoReplySkillSets)
        handle.CastSkill(1358, state.CurrentMpsX, state.CurrentMpsY);
        await harness.StepFramesAsync(1);
        handle.CastSkill(1364, state.CurrentMpsX, state.CurrentMpsY);
        await harness.StepFramesAsync(1);

        Assert.True(state.EffectiveMaxLife > baseMaxLife, "1358 lifemax_yan_p should boost EffectiveMaxLife.");
        Assert.True(state.EffectiveRunSpeed > baseRunSpeed, "1358 fastwalkrun_p should boost EffectiveRunSpeed.");
        Assert.NotEmpty(state.SkillCombatModifiers.AutoReplySkills.Entries);
        Assert.NotEmpty(state.SkillCombatModifiers.AutoRescueSkills.Entries);

        // 2. Enemy casts 1585 (addexdefense_v = -50, allres_p = -25; WaitFrames = 8) targeting player explicitly
        enemyHandle.CastSkill(1585, -1, (int)state.RuntimeNpcId);
        await harness.StepFramesAsync(10);
        Assert.True(state.EffectiveDefense < baseDefense, "1585 addexdefense_v should reduce EffectiveDefense.");
        Assert.True(((ICombatEntityState)state).PhysicsResistPercent < basePhysicsResist);

        // 3. Death + Revive clears Style=2 buffs (1358/1364) and curse (1585) while preserving Style=3 NoClearOnDeath passives (1349, 1365)
        state.CurrentLife = 0;
        state.Doing = EntityDoingPosture.Death;
        harness.EnqueueCommand(53, new RevivePlayerZoneCommand(state.CharacterId, handle.LeaseEpoch, 0));
        await harness.StepFramesAsync(1);

        Assert.Equal(0, state.EffectiveMaxMana);
        Assert.NotEmpty(state.SkillCombatModifiers.AutoRescueSkills.Entries);
        Assert.Empty(state.SkillCombatModifiers.AutoReplySkills.Entries);
        Assert.Equal(basePhysicsResist, ((ICombatEntityState)state).PhysicsResistPercent);
    }

    [Fact]
    public async Task StealthHide713_AoiEntryRegionCrossMovementAndNpcRequest_WithThreeMonsters_ShouldSuppressObserverSyncUntilUnhidden()
    {
        MapDescriptor arena = CreateArenaMap(
            (52700, 101760, ElementSeries.Metal, "Quái Ẩn Thân 1"),
            (52740, 101760, ElementSeries.Wood, "Quái Ẩn Thân 2"),
            (52780, 101760, ElementSeries.Earth, "Quái Ẩn Thân 3"));

        ZoneSimulationHarness harness = CreateHarness(42, arena);
        ConfigurePassiveMonsters(harness.GetZone(53));

        // Attach stealthed player at RegionX = 102 (52720 / 512 == 102) and far observer at RegionX = 104 (53300 / 512 == 104)
        SimulatedPlayerHandle stealthHandle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "StealthPlayer",
                ElementSeries.Water,
                52720,
                101696,
                [
                    new CharacterSkillDto(713, 10, 0),
                    new CharacterSkillDto(16, 10, 0),
                    new CharacterSkillDto(1382, 10, 0),
                ]));

        SimulatedPlayerHandle farObserverHandle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto("FarObserver", ElementSeries.Metal, 53300, 101696, []));

        PlayerZoneState stealthPlayer = stealthHandle.GetZoneState()!;
        PlayerZoneState farObserver = farObserverHandle.GetZoneState()!;
        stealthPlayer.FightMode = 1;

        // Enter stealth (HideMode > 0)
        stealthHandle.CastSkill(713, stealthPlayer.CurrentMpsX, stealthPlayer.CurrentMpsY);
        await harness.StepFramesAsync(1);
        Assert.True(((ICombatEntityState)stealthPlayer).HideMode > 0);

        // (a) Newly entering near observer in RegionX = 102 must NOT receive FullSync or PlayerAppearance for hidden player
        SimulatedPlayerHandle nearObserverHandle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto("NearObserver", ElementSeries.Fire, 52700, 101696, []));
        PlayerZoneState nearObserver = nearObserverHandle.GetZoneState()!;

        Assert.DoesNotContain(nearObserverHandle.Recorder.FullSyncs, s => s.NpcId == stealthPlayer.RuntimeNpcId);
        Assert.DoesNotContain(nearObserverHandle.Recorder.PlayerAppearanceSyncs, s => s.RuntimeNpcId == stealthPlayer.RuntimeNpcId);
        Assert.DoesNotContain(nearObserverHandle.Recorder.NpcStateInfoBroadcasts, s => s.NpcId == stealthPlayer.RuntimeNpcId);

        nearObserverHandle.Recorder.Clear();
        farObserverHandle.Recorder.Clear();

        // (b) & (c) Walk across 512-mps region boundary (52720 -> 52760, RegionX 102 -> 103) into far observer's AOI (RegionX 104), then Run
        stealthHandle.Move(52760, 101696, isRun: false);
        await harness.StepFramesAsync(12);
        Assert.Equal(103, stealthPlayer.RegionX);

        stealthHandle.Move(52800, 101696, isRun: true);
        await harness.StepFramesAsync(6);

        // Trigger 0x7A state-info broadcast while hidden via aura toggle (16 -> StateSpecialId 45)
        harness.EnqueueCommand(53, new ChangeAuraSkillZoneCommand(stealthPlayer.CharacterId, stealthHandle.LeaseEpoch, 16));
        await harness.StepFramesAsync(1);

        // (d) Query hidden player explicitly via c2s_npcreq (RequestEntitySyncZoneCommand)
        harness.EnqueueCommand(53, new RequestEntitySyncZoneCommand(farObserver.CharacterId, farObserverHandle.LeaseEpoch, stealthPlayer.RuntimeNpcId, IsPlayerRequest: true, IsFeatureRequest: false));
        harness.EnqueueCommand(53, new RequestEntitySyncZoneCommand(nearObserver.CharacterId, nearObserverHandle.LeaseEpoch, stealthPlayer.RuntimeNpcId, IsPlayerRequest: true, IsFeatureRequest: false));
        await harness.StepFramesAsync(1);

        Assert.DoesNotContain(nearObserverHandle.Recorder.FullSyncs, s => s.NpcId == stealthPlayer.RuntimeNpcId);
        Assert.DoesNotContain(nearObserverHandle.Recorder.PlayerAppearanceSyncs, s => s.RuntimeNpcId == stealthPlayer.RuntimeNpcId);
        Assert.DoesNotContain(nearObserverHandle.Recorder.WalkBroadcasts, s => s.NpcId == stealthPlayer.RuntimeNpcId);
        Assert.DoesNotContain(nearObserverHandle.Recorder.RunBroadcasts, s => s.NpcId == stealthPlayer.RuntimeNpcId);
        Assert.DoesNotContain(nearObserverHandle.Recorder.NpcStateInfoBroadcasts, s => s.NpcId == stealthPlayer.RuntimeNpcId);

        Assert.DoesNotContain(farObserverHandle.Recorder.FullSyncs, s => s.NpcId == stealthPlayer.RuntimeNpcId);
        Assert.DoesNotContain(farObserverHandle.Recorder.PlayerAppearanceSyncs, s => s.RuntimeNpcId == stealthPlayer.RuntimeNpcId);
        Assert.DoesNotContain(farObserverHandle.Recorder.WalkBroadcasts, s => s.NpcId == stealthPlayer.RuntimeNpcId);
        Assert.DoesNotContain(farObserverHandle.Recorder.RunBroadcasts, s => s.NpcId == stealthPlayer.RuntimeNpcId);
        Assert.DoesNotContain(farObserverHandle.Recorder.NpcStateInfoBroadcasts, s => s.NpcId == stealthPlayer.RuntimeNpcId);

        // Unhide (HideMode == 0) -> observers DO receive FullSync + PlayerAppearance
        nearObserverHandle.Recorder.Clear();
        farObserverHandle.Recorder.Clear();
        stealthHandle.CastSkill(1382, stealthPlayer.CurrentMpsX + 32, stealthPlayer.CurrentMpsY);
        await harness.StepFramesAsync(1);
        Assert.Equal(0, ((ICombatEntityState)stealthPlayer).HideMode);

        Assert.Contains(nearObserverHandle.Recorder.FullSyncs, s => s.NpcId == stealthPlayer.RuntimeNpcId);
        Assert.Contains(nearObserverHandle.Recorder.PlayerAppearanceSyncs, s => s.RuntimeNpcId == stealthPlayer.RuntimeNpcId);
        Assert.Contains(nearObserverHandle.Recorder.NpcStateInfoBroadcasts, s => s.NpcId == stealthPlayer.RuntimeNpcId && s.StateSpecialIds.Contains((byte)45));
        Assert.Contains(farObserverHandle.Recorder.FullSyncs, s => s.NpcId == stealthPlayer.RuntimeNpcId);
        Assert.Contains(farObserverHandle.Recorder.PlayerAppearanceSyncs, s => s.RuntimeNpcId == stealthPlayer.RuntimeNpcId);
        Assert.Contains(farObserverHandle.Recorder.NpcStateInfoBroadcasts, s => s.NpcId == stealthPlayer.RuntimeNpcId && s.StateSpecialIds.Contains((byte)45));
    }

    [Fact]
    public async Task StealthHide713_Periodic90TickAndJumpSitCamp_WithThreeMonsters_ShouldSuppressObserverBroadcasts()
    {
        MapDescriptor arena = CreateArenaMap(
            (52700, 101760, ElementSeries.Metal, "Quái Ẩn 90T 1"),
            (52740, 101760, ElementSeries.Wood, "Quái Ẩn 90T 2"),
            (52780, 101760, ElementSeries.Earth, "Quái Ẩn 90T 3"));

        ZoneSimulationHarness harness = CreateHarness(42, arena);
        MapZoneWorker worker = harness.GetZone(53);
        ConfigurePassiveMonsters(worker);

        SimulatedPlayerHandle stealthHandle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "Stealth90Tick",
                ElementSeries.Water,
                52720,
                101696,
                [new CharacterSkillDto(713, 10, 0)],
                level: 120,
                currentFactionId: 5));

        SimulatedPlayerHandle observerHandle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto("Stealth90Observer", ElementSeries.Metal, 52700, 101696, []));

        PlayerZoneState stealthPlayer = stealthHandle.GetZoneState()!;
        stealthPlayer.BaseCamp = EntityCamp.Justice;
        stealthPlayer.CurrentCamp = EntityCamp.Justice;
        stealthPlayer.FightMode = 1;

        // Enter stealth (713 lv 10 -> 321 frames duration)
        stealthHandle.CastSkill(713, stealthPlayer.CurrentMpsX, stealthPlayer.CurrentMpsY);
        await harness.StepFramesAsync(1);
        Assert.True(((ICombatEntityState)stealthPlayer).HideMode > 0);

        // (a) Step past a 90-tick boundary while stationary and hidden: must NOT emit 0x4D CompactSync for hidden player
        observerHandle.Recorder.Clear();
        long startBucket = worker.CurrentFrameTick / 90;
        int framesStepped = 0;
        while (worker.CurrentFrameTick / 90 == startBucket && framesStepped < 95)
        {
            await harness.StepFramesAsync(1);
            framesStepped++;
        }

        Assert.True(worker.CurrentFrameTick > 0 && worker.CurrentFrameTick / 90 > startBucket);
        Assert.True(((ICombatEntityState)stealthPlayer).HideMode > 0);
        Assert.Contains(observerHandle.Recorder.CompactSyncs, c => c.NpcId <= 3u);
        Assert.DoesNotContain(observerHandle.Recorder.CompactSyncs, c => c.NpcId == stealthPlayer.RuntimeNpcId);

        // (b) Jump, Sit, Temporary PK Camp (0x58), and Graduate Faction BaseCamp (0x59) while hidden
        observerHandle.Recorder.Clear();
        stealthHandle.Recorder.Clear();

        harness.EnqueueCommand(53, new JumpPlayerZoneCommand(stealthPlayer.CharacterId, stealthHandle.LeaseEpoch, 52752, 101696));
        harness.EnqueueCommand(53, new TogglePlayerSitZoneCommand(stealthPlayer.CharacterId, stealthHandle.LeaseEpoch, IsSitting: true));
        harness.EnqueueCommand(53, new SetTemporaryPkCampZoneCommand(stealthPlayer.CharacterId, stealthHandle.LeaseEpoch, EnableFreePkCamp: true));
        harness.EnqueueCommand(53, new GraduateFactionZoneCommand(stealthPlayer.CharacterId, stealthHandle.LeaseEpoch));
        await harness.StepFramesAsync(1);

        Assert.True(((ICombatEntityState)stealthPlayer).HideMode > 0);
        Assert.NotEmpty(stealthHandle.Recorder.SelfPositionSyncs);
        Assert.Equal(EntityDoingPosture.Sit, stealthPlayer.Doing);
        Assert.Equal(EntityCamp.Free, stealthPlayer.BaseCamp);
        Assert.Equal(EntityCamp.Free, stealthPlayer.CurrentCamp);

        Assert.DoesNotContain(observerHandle.Recorder.JumpBroadcasts, j => j.NpcId == stealthPlayer.RuntimeNpcId);
        Assert.DoesNotContain(observerHandle.Recorder.SitBroadcasts, id => id == stealthPlayer.RuntimeNpcId);
        Assert.DoesNotContain(observerHandle.Recorder.NpcChangeCurCampBroadcasts, c => c.WorldNpcId == stealthPlayer.RuntimeNpcId);
        Assert.DoesNotContain(observerHandle.Recorder.NpcChangeBaseCampBroadcasts, c => c.WorldNpcId == stealthPlayer.RuntimeNpcId);
    }

    [Fact]
    public async Task StealthHide713_RevealWithAuraAndDisguise_WithThreeMonsters_ShouldSync0x7AAndDisguiseToLateObserver()
    {
        MapDescriptor arena = CreateArenaMap(
            (52700, 101760, ElementSeries.Metal, "Quái Reveal 1"),
            (52740, 101760, ElementSeries.Wood, "Quái Reveal 2"),
            (52780, 101760, ElementSeries.Earth, "Quái Reveal 3"));

        ZoneSimulationHarness harness = CreateHarness(42, arena);
        ConfigurePassiveMonsters(harness.GetZone(53));

        SimulatedPlayerHandle stealthHandle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto(
                "StealthDisguised",
                ElementSeries.Water,
                52720,
                101696,
                [
                    new CharacterSkillDto(713, 10, 0),
                    new CharacterSkillDto(16, 10, 0),
                    new CharacterSkillDto(1382, 10, 0),
                ]));

        PlayerZoneState stealthPlayer = stealthHandle.GetZoneState()!;
        stealthPlayer.FightMode = 1;

        var maskTemplate = new ItemTemplate(
            Id: 9205,
            Code: "test-disguise-mask",
            Name: "Mặt Nạ Biến Hình",
            Genre: ItemGenreKind.Equipment,
            Quality: ItemQualityKind.Normal,
            DetailType: 11,
            ParticularType: 0,
            Level: 1,
            ElementSeries: ElementSeries.Metal,
            GridWidthCells: 1,
            GridHeightCells: 1,
            ObjDataId: 1,
            AppearanceResId: 1,
            PriceGold: 100,
            MaxStackCount: 1,
            BaseAttributes: [new ItemBaseAttributeTemplate(184, "changefeature", 205, 205)],
            Requirements: []);

        var maskSlot = new PlayerItemSlot(
            runtimeItemId: 1005u,
            domainItemId: Guid.NewGuid(),
            template: maskTemplate,
            genre: ItemGenreKind.Equipment,
            detailType: 11,
            particularType: 0,
            level: 1,
            series: ElementSeries.Metal,
            quality: ItemQualityKind.Normal,
            randomSeed: 1u,
            generatorVersion: ItemConsts.GeneratorVersion,
            luck: 0,
            currentDurability: 100,
            maxDurability: 100,
            stackCount: 1,
            containerPlace: ItemContainerPlace.EquipRoom,
            gridX: 0,
            gridY: 0);

        Assert.True(stealthPlayer.Inventory.TryPlaceItemDirect(maskSlot, ItemContainerPlace.EquipRoom, 0, 0, stealthPlayer));
        harness.EnqueueCommand(53, new MoveInventoryItemZoneCommand(
            stealthPlayer.CharacterId,
            stealthHandle.LeaseEpoch,
            ItemContainerPlace.EquipRoom,
            0,
            0,
            ItemContainerPlace.Equip,
            (byte)EquipmentPartKind.Mask,
            0));
        harness.EnqueueCommand(53, new ChangeAuraSkillZoneCommand(stealthPlayer.CharacterId, stealthHandle.LeaseEpoch, 16));
        await harness.StepFramesAsync(1);
        Assert.Equal(205, stealthPlayer.DisguiseNpcTemplateId);

        // Hide after equipping mask and activating Aura 16 (StateSpecialId = 45)
        stealthHandle.CastSkill(713, stealthPlayer.CurrentMpsX, stealthPlayer.CurrentMpsY);
        await harness.StepFramesAsync(1);
        Assert.True(((ICombatEntityState)stealthPlayer).HideMode > 0);

        // Late observer attaches while StealthPlayer is hidden -> must receive 0 syncs while hidden
        SimulatedPlayerHandle lateObserverHandle = await harness.AttachSimulatedPlayerAsync(
            CreateTestPlayerDto("LateObserver", ElementSeries.Fire, 52700, 101696, []));

        Assert.DoesNotContain(lateObserverHandle.Recorder.FullSyncs, s => s.NpcId == stealthPlayer.RuntimeNpcId);
        Assert.DoesNotContain(lateObserverHandle.Recorder.NpcStateInfoBroadcasts, s => s.NpcId == stealthPlayer.RuntimeNpcId);
        Assert.DoesNotContain(lateObserverHandle.Recorder.NpcDisguiseFeatureSyncs, d => d.NpcId == stealthPlayer.RuntimeNpcId);

        // Reveal via DoSkill (1382) -> late observer must receive FullSync, Appearance, 0x7A (45), and Disguise Feature Sync (205)
        lateObserverHandle.Recorder.Clear();
        stealthHandle.CastSkill(1382, stealthPlayer.CurrentMpsX + 32, stealthPlayer.CurrentMpsY);
        await harness.StepFramesAsync(1);
        Assert.Equal(0, ((ICombatEntityState)stealthPlayer).HideMode);

        Assert.Contains(lateObserverHandle.Recorder.FullSyncs, s => s.NpcId == stealthPlayer.RuntimeNpcId);
        Assert.Contains(lateObserverHandle.Recorder.PlayerAppearanceSyncs, s => s.RuntimeNpcId == stealthPlayer.RuntimeNpcId);
        Assert.Contains(lateObserverHandle.Recorder.NpcStateInfoBroadcasts, s => s.NpcId == stealthPlayer.RuntimeNpcId && s.StateSpecialIds.Contains((byte)45));
        Assert.Contains(
            lateObserverHandle.Recorder.NpcDisguiseFeatureSyncs,
            d => d.NpcId == stealthPlayer.RuntimeNpcId &&
                 d.FeatureState == 1 &&
                 d.FeatureNpcSettingIdx == 205);
    }
}
