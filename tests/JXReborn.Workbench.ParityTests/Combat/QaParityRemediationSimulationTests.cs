using JXReborn.Contracts.Characters;
using JXReborn.GameAssets.Items;
using JXReborn.GameAssets.LootTables;
using JXReborn.GameAssets.NpcTemplates;
using JXReborn.GameAssets.PlayerTemplates;
using JXReborn.GameAssets.Shops;
using JXReborn.GameAssets.WorldMaps;
using JXReborn.Primitives.Characters;
using JXReborn.Primitives.Combat;
using JXReborn.Primitives.Items;
using JXReborn.Primitives.Shops;
using JXReborn.Primitives.WorldZones;
using JXReborn.Scripts.Hosting;
using JXReborn.Packs.TieuDaoTu.Hosting;
using JXReborn.TestBase.Fixtures;
using JXReborn.TestBase.SimulationHarnesses;
using JXReborn.ZoneEngine.Combat;
using JXReborn.ZoneEngine.Items;
using JXReborn.ZoneEngine.NpcEntities;
using JXReborn.ZoneEngine.Parties;
using JXReborn.ZoneEngine.PlayerSessions;
using JXReborn.ZoneEngine.Scripting;
using JXReborn.ZoneEngine.Shops;
using JXReborn.ZoneEngine.SpatialGrid;
using JXReborn.ZoneEngine.WorldZones;
using Xunit;

namespace JXReborn.ZoneEngine.Tests.Combat;

public sealed class QaParityRemediationSimulationTests
{
    [Fact]
    [Trait("Category", "LegacyData")]
    public async Task StationaryTrainingDummies_413To415_ShouldHaveOneLifeZeroSpeedZeroAggroAndZeroDrop()
    {
        SharedGameAssetsFixture assets = SharedGameAssetsFixture.Default;
        Assert.True(assets.NpcTemplateCatalog.TryGetTemplate(413, out NpcTemplate? dummy413) && dummy413 is not null);
        Assert.True(assets.NpcTemplateCatalog.TryGetTemplate(414, out NpcTemplate? dummy414) && dummy414 is not null);
        Assert.True(assets.NpcTemplateCatalog.TryGetTemplate(415, out NpcTemplate? dummy415) && dummy415 is not null);

        Assert.Equal(0, dummy413!.WalkSpeed);
        Assert.Equal(0, dummy414!.WalkSpeed);
        Assert.Equal(0, dummy415!.WalkSpeed);

        (ZoneSimulationHarness harness, MapZoneWorker worker53) = CreateRealMap53Harness(assets);
        List<NpcZoneState> dummies = worker53.StaticNpcs.Values
            .Where(n => n.TemplateId is 413 or 414 or 415)
            .ToList();
        Assert.NotEmpty(dummies);

        NpcZoneState dummy = dummies[0];
        Assert.True(dummy.IsStationaryDummy);
        Assert.False(dummy.IsAutoAggressive);
        Assert.False(dummy.CanPatrol);
        Assert.Equal(0, dummy.EffectiveWalkSpeed);
        Assert.Equal(0, dummy.EffectiveRunSpeed);
        Assert.Equal(1u, dummy.MaxLife);
        Assert.Equal(1, dummy.ExpReward);

        int origX = dummy.MpsX;
        int origY = dummy.MpsY;

        CharacterDto dto = CreateTestCharacter("DummyTester", ElementSeries.Metal, origX - 32, origY);
        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(dto);
        PlayerZoneState player = handle.GetZoneState()!;

        // Step idle ticks nearby -> dummy must never aggro or move
        await harness.StepFramesAsync(20);
        Assert.Equal(0u, dummy.AggroTargetNpcId);
        Assert.Equal(origX, dummy.MpsX);
        Assert.Equal(origY, dummy.MpsY);

        int groundCountBefore = CountDynamicGroundObjects(worker53);
        int expBefore = (int)player.Experience;

        // Kill dummy cleanly via melee skill 53 -> grants 1 exp (no auto-heal, no loot, no silver)
        player.BonusAttackRating = 50_000;
        for (int attempt = 0; attempt < 20 && dummy.Doing != EntityDoingPosture.Death; attempt++)
        {
            player.SetSkillCooldown(53, 0);
            harness.EnqueueCommand(53, new CastSkillZoneCommand(player.CharacterId, player.LeaseEpoch, 53, -1, (int)dummy.NpcId));
            await harness.StepFramesAsync(6);
        }

        Assert.Equal(EntityDoingPosture.Death, dummy.Doing);
        Assert.Equal(expBefore + 1, (int)player.Experience);
        Assert.Equal(groundCountBefore, CountDynamicGroundObjects(worker53));
    }

    [Fact]
    public void CanonicalLootMoneyFormula_ShouldEqualMoneyScaleTimesExpDividedBy100()
    {
        SharedGameAssetsFixture assets = SharedGameAssetsFixture.Default;
        Assert.True(assets.LootTableCatalog.TryGetLootTable("default-monster-loot", out LootTableTemplate? defaultLoot) && defaultLoot is not null);
        Assert.Equal(50, defaultLoot!.MoneyScale);

        var regionGrid = new SpatialRegionGrid();
        var monster = new NpcZoneState(
            npcId: 5001u,
            templateId: 1,
            name: "Nhim",
            level: 1,
            kind: EntityKind.Monster,
            camp: EntityCamp.Animal,
            series: ElementSeries.Wood,
            isSpecialNpc: false,
            mapId: 53,
            mpsX: 52000,
            mpsY: 101696,
            walkSpeed: 4,
            runSpeed: 8,
            maxLife: 100,
            scriptPath: string.Empty)
        {
            ExpReward = 80,
            TreasureDropCount = 1,
            LootTableCode = "default-monster-loot",
        };

        GroundObjectSlot? moneyDrop = null;
        for (int seed = 1; seed <= 64 && moneyDrop is null; seed++)
        {
            var groundSys = new GroundObjectTickSystem(seed);
            List<GroundObjectSlot> drops = groundSys.EvaluateAndSpawnMonsterLoot(
                53,
                null,
                assets.LootTableCatalog,
                null,
                regionGrid,
                monster,
                killerPlayer: null,
                currentTick: 10);
            moneyDrop = drops.FirstOrDefault(static d => d.Kind == GroundObjectKind.Money);
        }

        Assert.NotNull(moneyDrop);
        // (50 * 80) / 100 = 40 silver (NOT huge level*600 inflation)
        Assert.Equal(40, moneyDrop!.MoneyAmount);
    }

    [Fact]
    public async Task MeleeSkill53_ShouldDelayDamageByWaitFrames_BeforeLandingHit()
    {
        SharedGameAssetsFixture assets = SharedGameAssetsFixture.Default;
        (ZoneSimulationHarness harness, MapZoneWorker worker53) = CreateRealMap53Harness(assets);

        NpcZoneState targetMonster = worker53.StaticNpcs.Values.First(n => n.Kind == EntityKind.Monster && !n.IsStationaryDummy);
        targetMonster.AiMode = 0;
        targetMonster.CurLife = targetMonster.MaxLife;
        uint fullLife = targetMonster.CurLife;

        var sink = new PlayerOutboundEventRecorder();
        CharacterDto dto = CreateTestCharacter("MeleeDelayTester", ElementSeries.Metal, targetMonster.MpsX - 32, targetMonster.MpsY);
        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(dto, customSink: sink);
        PlayerZoneState player = handle.GetZoneState()!;
        player.BonusAttackRating = 50_000;

        bool hitLandedAfterWaitFrames = false;
        for (int attempt = 0; attempt < 10 && !hitLandedAfterWaitFrames; attempt++)
        {
            targetMonster.CurLife = fullLife;
            targetMonster.Doing = EntityDoingPosture.Stand;
            player.SetSkillCooldown(53, 0);

            harness.EnqueueCommand(53, new CastSkillZoneCommand(player.CharacterId, player.LeaseEpoch, 53, -1, (int)targetMonster.NpcId));

            // Cast tick + 3 ticks (< WaitFrames=5) -> damage must NOT have landed prematurely
            await harness.StepFramesAsync(4);
            Assert.Equal(fullLife, targetMonster.CurLife);

            // Reaching castTick + 5 -> delayed melee hit lands now
            await harness.StepFramesAsync(2);
            if (targetMonster.CurLife < fullLife || targetMonster.Doing == EntityDoingPosture.Death)
            {
                hitLandedAfterWaitFrames = true;
            }
        }

        Assert.True(hitLandedAfterWaitFrames, "Expected melee skill 53 hit to land after WaitFrames=5 delay.");
    }

    [Fact]
    public async Task SeriesBaseResistsAndNegativeAttackRatingAxe_ShouldMatchLevelAddAndEmitResistSync()
    {
        SharedGameAssetsFixture assets = SharedGameAssetsFixture.Default;
        PlayerSeriesGrowthRuleTemplate metalRule = assets.PlayerTemplateCatalog.GetSeriesGrowthRule(ElementSeries.Metal);
        PlayerSeriesGrowthRuleTemplate fireRule = assets.PlayerTemplateCatalog.GetSeriesGrowthRule(ElementSeries.Fire);

        // Must NOT be hardcoded 17% across all elements
        Assert.NotEqual(17, metalRule.BasePhysicsResist);
        Assert.NotEqual(metalRule.BaseFireResist, fireRule.BaseFireResist);

        (ZoneSimulationHarness harness, _) = CreateRealMap53Harness(assets);
        var sink = new PlayerOutboundEventRecorder();
        CharacterDto dto = CreateTestCharacter("ResistAndAxeTester", ElementSeries.Metal, 52032, 101696);
        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(dto, customSink: sink);
        PlayerZoneState player = handle.GetZoneState()!;

        Assert.Equal(metalRule.BaseFireResist, player.FireResistPercent);
        Assert.Equal(metalRule.BasePhysicsResist, player.PhysicsResistPercent);

        int baseAttackRating = player.EffectiveAttackRating;
        Assert.Equal((player.EffectiveDexterity * 4) + (player.Level * 5), baseAttackRating);

        // Equip Metal axe carrying attackratingenhance_p = -30% (AttrId 167)
        var axeTemplate = new ItemTemplate(
            Id: 9001,
            Code: "thiet-phu-test",
            Name: "Thiết Phủ",
            Genre: ItemGenreKind.Equipment,
            Quality: ItemQualityKind.Normal,
            DetailType: 0,
            ParticularType: 2,
            Level: 1,
            ElementSeries: ElementSeries.Metal,
            GridWidthCells: 2,
            GridHeightCells: 3,
            ObjDataId: 4,
            AppearanceResId: 1,
            PriceGold: 100,
            MaxStackCount: 1,
            BaseAttributes:
            [
                new ItemBaseAttributeTemplate(167, "attackratingenhance_p", -30, -30),
                new ItemBaseAttributeTemplate(102, "fireres_p", 10, 10),
            ],
            Requirements: []);

        var axeSlot = new PlayerItemSlot(
            runtimeItemId: player.Inventory.AllocateRuntimeItemId(),
            domainItemId: Guid.NewGuid(),
            template: axeTemplate,
            genre: ItemGenreKind.Equipment,
            detailType: 0,
            particularType: 2,
            level: 1,
            series: ElementSeries.Metal,
            quality: ItemQualityKind.Normal,
            randomSeed: 4u,
            generatorVersion: 2,
            luck: 0,
            currentDurability: 50,
            maxDurability: 50,
            stackCount: 1,
            containerPlace: ItemContainerPlace.EquipRoom,
            gridX: 0,
            gridY: 0);

        Assert.True(player.Inventory.TryAddOrAutoPlaceItem(axeSlot, ItemContainerPlace.EquipRoom, 0, 0, player));
        sink.Clear();

        harness.EnqueueCommand(53, new MoveInventoryItemZoneCommand(
            player.CharacterId,
            player.LeaseEpoch,
            ItemContainerPlace.EquipRoom, 0, 0,
            ItemContainerPlace.Equip, (byte)EquipmentPartKind.Weapon, 0));
        await harness.StepFramesAsync(1);

        Assert.Equal(-30, player.BonusAttackRatingPercent);
        Assert.Equal((baseAttackRating * 70) / 100, player.EffectiveAttackRating);
        Assert.Equal(metalRule.BaseFireResist + 10, player.FireResistPercent);
        Assert.Contains(sink.Events, e => e.Domain == OutboundNotificationDomain.Combat && e.MethodName == nameof(ICombatOutboundSink.SendResistSync));
    }

    [Fact]
    public async Task PveDeath_ShouldPunishExpAndSilver_DropHalfOfLostSilver_EmitMsgShow10_AndWaitForClientRevive()
    {
        SharedGameAssetsFixture assets = SharedGameAssetsFixture.Default;
        (ZoneSimulationHarness harness, MapZoneWorker worker53) = CreateRealMap53Harness(assets);

        var sink = new PlayerOutboundEventRecorder();
        CharacterDto dto = CreateTestCharacter("PveVictim", ElementSeries.Metal, 52032, 101696, level: 10, exp: 5000, money: 10_000);
        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(dto, customSink: sink);
        PlayerZoneState player = handle.GetZoneState()!;
        player.SpawnProtectUntilTick = 0;

        int groundMoneyCountBefore = CountDynamicMoneyObjects(worker53);

        worker53.ApplyDamageToPlayer(attackerNpcId: 8001u, player, player.CurrentLife + 100);

        Assert.Equal(EntityDoingPosture.Death, player.Doing);
        Assert.Equal(0, player.CurrentLife);
        Assert.Equal(5_000, player.InventoryMoney);
        Assert.True(player.Experience < 5_000);

        RecordedOutboundEvent msgShow = Assert.Single(
            sink.Events,
            e => e.Domain == OutboundNotificationDomain.World && e.MethodName == nameof(IWorldOutboundSink.SendMsgShow));
        Assert.Equal((ushort)0x000A, msgShow.Arguments[0]);
        Assert.Equal(5_000, msgShow.Arguments[1]);

        List<GroundObjectSlot> moneyPiles = FindDynamicMoneyObjects(worker53);
        Assert.Equal(groundMoneyCountBefore + 1, moneyPiles.Count);
        Assert.Contains(moneyPiles, static m => m.MoneyAmount == 2_500);

        // Legacy KPlayer::Revive (0x080c7890): remains dead until client clicks "Về Thành Dưỡng Sức" (0x74)
        await harness.StepFramesAsync(185);
        Assert.Equal(EntityDoingPosture.Death, player.Doing);

        worker53.TryEnqueueCommand(new RevivePlayerZoneCommand(player.CharacterId, player.LeaseEpoch, ReviveType: 0));
        await harness.StepFramesAsync(1);
        Assert.Equal(EntityDoingPosture.Stand, player.Doing);
        Assert.True(player.CurrentLife > 0);
    }

    [Fact]
    [Trait("Category", "LegacyData")]
    public async Task BaLangPharmacistShop39_AndPortraitCommand_ShouldOpenPotionsShopAndSyncPortrait()
    {
        SharedGameAssetsFixture assets = SharedGameAssetsFixture.Default;
        Assert.True(assets.ShopCatalog.TryGetShop(39, out ShopTemplate? shop39) && shop39 is not null);
        Assert.Contains(shop39!.Goods, static g => g.ItemCode == "kim-sang-duoc-tieu");
        Assert.DoesNotContain(shop39.Goods, static g => g.ItemCode == "co-hieu");

        (ZoneSimulationHarness harness, MapZoneWorker worker53) = CreateRealMap53Harness(assets);
        var sink = new PlayerOutboundEventRecorder();
        CharacterDto dto = CreateTestCharacter("ShopAndPortraitTester", ElementSeries.Water, 52032, 101696);
        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(dto, customSink: sink);
        PlayerZoneState player = handle.GetZoneState()!;

        NpcZoneState pharmacist = worker53.StaticNpcs.Values
            .First(n => string.Equals(n.ScriptCode, "npc-town-pharmacist", StringComparison.Ordinal));

        player.CurrentMpsX = pharmacist.MpsX;
        player.CurrentMpsY = pharmacist.MpsY;
        player.TargetMpsX = pharmacist.MpsX;
        player.TargetMpsY = pharmacist.MpsY;

        harness.EnqueueCommand(53, new DialogNpcZoneCommand(player.CharacterId, player.LeaseEpoch, pharmacist.NpcId));
        await harness.StepFramesAsync(1);
        harness.EnqueueCommand(53, new SelectDialogUiZoneCommand(player.CharacterId, player.LeaseEpoch, 0));
        await harness.StepFramesAsync(1);

        Assert.True(player.TryGetFeatureSlot(PlayerShopSessionSlot.SlotKey, out PlayerShopSessionSlot? shopSlot) && shopSlot is not null);
        Assert.True(shopSlot!.IsOpen);
        // Ground truth: server1/script/两湖区/巴陵县/npc/巴陵县_药店老板对话.lua calls Sale(99, 1)
        Assert.Equal(99, shopSlot.ActiveShopId);
        Assert.Equal(ShopCurrencyType.Gold, shopSlot.CurrencyType);

        // Portrait command
        harness.EnqueueCommand(53, new SetPlayerPortraitZoneCommand(player.CharacterId, player.LeaseEpoch, 14));
        await harness.StepFramesAsync(1);

        Assert.Equal((byte)14, player.PortraitId);
        Assert.Equal((byte)14, worker53.BuildCharacterWorldSnapshot(player).PortraitId);
        RecordedOutboundEvent portraitEvent = Assert.Single(
            sink.Events,
            e => e.Domain == OutboundNotificationDomain.World && e.MethodName == nameof(IWorldOutboundSink.SendPortraitSync));
        Assert.Equal((byte)14, portraitEvent.Arguments[0]);
    }

    [Fact]
    [Trait("Category", "LegacyData")]
    public async Task LootLockedGroundMoneyAndItem_ShouldEmitDistinctMsgShow_0x0011And0x0008()
    {
        SharedGameAssetsFixture assets = SharedGameAssetsFixture.Default;
        (ZoneSimulationHarness harness, MapZoneWorker worker53) = CreateRealMap53Harness(assets);

        CharacterDto ownerDto = CreateTestCharacter("DropOwner", ElementSeries.Metal, 52032, 101696);
        CharacterDto thiefDto = CreateTestCharacter("DropThief", ElementSeries.Wood, 52032, 101696);

        var thiefSink = new PlayerOutboundEventRecorder();
        SimulatedPlayerHandle ownerHandle = await harness.AttachSimulatedPlayerAsync(ownerDto);
        SimulatedPlayerHandle thiefHandle = await harness.AttachSimulatedPlayerAsync(thiefDto, customSink: thiefSink);
        PlayerZoneState owner = ownerHandle.GetZoneState()!;
        PlayerZoneState thief = thiefHandle.GetZoneState()!;

        GroundObjectSlot lockedMoney = worker53.GroundObjectSystem.SpawnMoneyGroundObject(
            53, assets.MapCatalog, worker53.RegionGrid, 40, owner.CurrentMpsX, owner.CurrentMpsY, owner.CharacterId, worker53.CurrentFrameTick);
        Assert.True(assets.ItemTemplateCatalog.TryGetItemByCode("kim-sang-duoc-tieu", out ItemTemplate? potionTpl));
        Assert.NotNull(potionTpl);
        var lockedItemSlot = new PlayerItemSlot(
            runtimeItemId: 999901u,
            domainItemId: Guid.NewGuid(),
            template: potionTpl!,
            genre: ItemGenreKind.Medicine,
            detailType: (short)potionTpl!.DetailType,
            particularType: (short)potionTpl.ParticularType,
            level: potionTpl.Level,
            series: ElementSeries.Metal,
            quality: ItemQualityKind.Normal,
            randomSeed: 0u,
            generatorVersion: ItemConsts.GeneratorVersion,
            luck: 0,
            currentDurability: 1,
            maxDurability: 1,
            stackCount: 1,
            containerPlace: ItemContainerPlace.None,
            gridX: 0,
            gridY: 0);
        GroundObjectSlot lockedItem = worker53.GroundObjectSystem.SpawnItemGroundObject(
            53, assets.MapCatalog, worker53.RegionGrid, lockedItemSlot, owner.CurrentMpsX, owner.CurrentMpsY, owner.CharacterId, worker53.CurrentFrameTick);

        thiefSink.Clear();
        harness.EnqueueCommand(53, new PickupGroundObjectZoneCommand(thief.CharacterId, thief.LeaseEpoch, lockedMoney.ObjId, ItemContainerPlace.EquipRoom, 0, 0));
        await harness.StepFramesAsync(1);
        RecordedOutboundEvent moneyLockMsg = Assert.Single(
            thiefSink.Events,
            e => e.Domain == OutboundNotificationDomain.World && e.MethodName == nameof(IWorldOutboundSink.SendMsgShow));
        Assert.Equal((ushort)0x0011, moneyLockMsg.Arguments[0]);

        thiefSink.Clear();
        harness.EnqueueCommand(53, new PickupGroundObjectZoneCommand(thief.CharacterId, thief.LeaseEpoch, lockedItem.ObjId, ItemContainerPlace.EquipRoom, 0, 0));
        await harness.StepFramesAsync(1);
        RecordedOutboundEvent itemLockMsg = Assert.Single(
            thiefSink.Events,
            e => e.Domain == OutboundNotificationDomain.World && e.MethodName == nameof(IWorldOutboundSink.SendMsgShow));
        Assert.Equal((ushort)0x0008, itemLockMsg.Arguments[0]);
    }

    [Fact]
    public async Task NovicePartyAutoCreateViaInviteAndAccept_ShouldFormPartyCleanlyWithoutDeadlock()
    {
        SharedGameAssetsFixture assets = SharedGameAssetsFixture.Default;
        (ZoneSimulationHarness harness, MapZoneWorker worker53) = CreateRealMap53Harness(assets);

        SimulatedPlayerHandle capHandle = await harness.AttachSimulatedPlayerAsync(CreateTestCharacter("NoviceCap", ElementSeries.Metal, 52032, 101696, level: 1));
        SimulatedPlayerHandle memHandle = await harness.AttachSimulatedPlayerAsync(CreateTestCharacter("NoviceMem", ElementSeries.Earth, 52064, 101696, level: 1));
        PlayerZoneState cap = capHandle.GetZoneState()!;
        PlayerZoneState mem = memHandle.GetZoneState()!;

        harness.EnqueueCommand(53, new InvitePlayerToPartyZoneCommand(cap.CharacterId, cap.LeaseEpoch, mem.RuntimeNpcId));
        await harness.StepFramesAsync(1);
        Assert.NotEqual(0u, cap.PartyId);
        Assert.True(cap.IsPartyCaptain);

        harness.EnqueueCommand(53, new ReplyPartyInviteZoneCommand(mem.CharacterId, mem.LeaseEpoch, cap.RuntimeNpcId, Accept: true));
        await harness.StepFramesAsync(1);
        Assert.Equal(cap.PartyId, mem.PartyId);
        Assert.True(worker53.ExecutionContext.TryGetZoneFeature<PartyManager>(out var pm) && pm is not null);
        Assert.True(pm!.TryGetParty(cap.PartyId, out PartyModel? party) && party is not null);
        Assert.Equal(2, party!.MemberCount);
    }

    [Fact]
    public async Task ChangePkMode_FightToSlaughter_ShouldBroadcastNormalSyncAndNotResetPeaceCooldown()
    {
        SharedGameAssetsFixture assets = SharedGameAssetsFixture.Default;
        (ZoneSimulationHarness harness, _) = CreateRealMap53Harness(assets);

        var sink = new PlayerOutboundEventRecorder();
        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateTestCharacter("PkTester", ElementSeries.Fire, 52032, 101696, level: 20, exp: 1000),
            customSink: sink);
        PlayerZoneState player = handle.GetZoneState()!;
        player.FightMode = 1;
        sink.Clear();

        async Task StepWithKeepAliveAsync(int totalFrames)
        {
            for (int remaining = totalFrames; remaining > 0;)
            {
                int chunk = Math.Min(60, remaining);
                await harness.StepFramesAsync(chunk);
                player.LastPingReceivedTick += chunk;
                remaining -= chunk;
            }
        }

        // Peace -> Fight at tick T0
        harness.EnqueueCommand(53, new ChangePkModeZoneCommand(player.CharacterId, player.LeaseEpoch, PlayerPkMode.Fight));
        await harness.StepFramesAsync(1);
        Assert.Equal(PlayerPkMode.Fight, player.PkState);
        Assert.Contains(sink.Events, e => e.Domain == OutboundNotificationDomain.Combat && e.MethodName == nameof(ICombatOutboundSink.SendPkNormalFlagSync));
        Assert.Contains(sink.Events, e => e.Domain == OutboundNotificationDomain.World && e.MethodName == nameof(IWorldOutboundSink.SendPlayerNormalSync));

        // Halfway through 3-minute cooldown, switch Fight -> Slaughter (should NOT reset Peace cooldown)
        await StepWithKeepAliveAsync(1800);
        sink.Clear();
        harness.EnqueueCommand(53, new ChangePkModeZoneCommand(player.CharacterId, player.LeaseEpoch, PlayerPkMode.Slaughter));
        await harness.StepFramesAsync(1);
        Assert.Equal(PlayerPkMode.Slaughter, player.PkState);
        Assert.Contains(sink.Events, e => e.Domain == OutboundNotificationDomain.World && e.MethodName == nameof(IWorldOutboundSink.SendPlayerNormalSync));

        // Before 3240 frames from T0, switching Slaughter -> Peace must be rejected
        await StepWithKeepAliveAsync(1400);
        harness.EnqueueCommand(53, new ChangePkModeZoneCommand(player.CharacterId, player.LeaseEpoch, PlayerPkMode.Peace));
        await harness.StepFramesAsync(1);
        Assert.Equal(PlayerPkMode.Slaughter, player.PkState);

        // Once 3240 frames have elapsed from T0, switching Slaughter -> Peace succeeds
        await StepWithKeepAliveAsync(50);
        harness.EnqueueCommand(53, new ChangePkModeZoneCommand(player.CharacterId, player.LeaseEpoch, PlayerPkMode.Peace));
        await harness.StepFramesAsync(1);
        Assert.Equal(PlayerPkMode.Peace, player.PkState);
    }

    [Fact]
    public async Task PlayerStamina_ShouldDrainByPkModeAndForceWalkAtZeroAndRegenOnSit()
    {
        SharedGameAssetsFixture assets = SharedGameAssetsFixture.Default;
        (ZoneSimulationHarness harness, _) = CreateRealMap53Harness(assets);

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateTestCharacter("Runner", ElementSeries.Metal, 52032, 101696, level: 20));
        PlayerZoneState player = handle.GetZoneState()!;
        player.CurrentStamina = 30;
        player.MaxStamina = 100;
        player.HorseResType = LegacyEquipmentResResolver.UnmountedHorseRes;
        player.Doing = EntityDoingPosture.Run;
        player.TargetMpsX = player.CurrentMpsX + 5000;
        player.TargetMpsY = player.CurrentMpsY;
        player.PkState = PlayerPkMode.Peace;

        // Ground truth: KNpc::ProcessState drains/regens stamina on 10-frame intervals (m_LoopFrames % 10 == 0).
        // Stepping 10 frames hits exactly 1 cadence tick in Peace mode -> drains 1 stamina
        await harness.StepFramesAsync(10);
        Assert.Equal((ushort)29, player.CurrentStamina);

        // 10 frames in Slaughter mode hits 1 cadence tick -> drains 18 stamina
        player.PkState = PlayerPkMode.Slaughter;
        player.Doing = EntityDoingPosture.Run;
        player.CurrentMpsX = 52032;
        player.CurrentMpsY = 101696;
        player.TargetMpsX = 52500;
        player.TargetMpsY = 101696;
        await harness.StepFramesAsync(10);
        Assert.Equal((ushort)11, player.CurrentStamina);

        // Next 10 frames in Slaughter drains <= 18 -> clamps to 0 and forces Walk
        player.Doing = EntityDoingPosture.Run;
        player.CurrentMpsX = 52032;
        player.CurrentMpsY = 101696;
        player.TargetMpsX = 52500;
        player.TargetMpsY = 101696;
        await harness.StepFramesAsync(10);
        Assert.Equal((ushort)0, player.CurrentStamina);
        Assert.Equal(EntityDoingPosture.Walk, player.Doing);

        // Sitting regenerates Max(1, 100 * 10 / 1000) + 1 = 2 stamina per 10-frame cadence tick
        player.Doing = EntityDoingPosture.Sit;
        await harness.StepFramesAsync(10);
        Assert.Equal((ushort)2, player.CurrentStamina);
    }

    [Fact]
    public async Task ExpiredItem_ShouldBeRejectedOnEquipAndUse_WhilePermanentItemHasZeroExpiry()
    {
        SharedGameAssetsFixture assets = SharedGameAssetsFixture.Default;
        (ZoneSimulationHarness harness, _) = CreateRealMap53Harness(assets);

        var sink = new PlayerOutboundEventRecorder();
        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateTestCharacter("ItemTester", ElementSeries.Water, 52032, 101696, level: 20),
            customSink: sink);
        PlayerZoneState player = handle.GetZoneState()!;

        var medTemplate = new ItemTemplate(
            Id: 9010, Code: "expired-med", Name: "Thuốc Hết Hạn",
            Genre: ItemGenreKind.Medicine, Quality: ItemQualityKind.Normal,
            DetailType: 0, ParticularType: 0, Level: 1,
            ElementSeries: ElementSeries.Metal,
            GridWidthCells: 1, GridHeightCells: 1,
            ObjDataId: 1, AppearanceResId: 1, PriceGold: 10, MaxStackCount: 5,
            BaseAttributes: [new ItemBaseAttributeTemplate(138, "lifepotion_v", 50, 50)],
            Requirements: []);
        var expiredMed = new PlayerItemSlot(
            runtimeItemId: player.Inventory.AllocateRuntimeItemId(),
            domainItemId: Guid.NewGuid(),
            template: medTemplate,
            genre: ItemGenreKind.Medicine, detailType: 0, particularType: 0,
            level: 1, series: ElementSeries.Metal, quality: ItemQualityKind.Normal,
            randomSeed: 1u, generatorVersion: 2, luck: 0,
            currentDurability: 1, maxDurability: 5, stackCount: 1,
            containerPlace: ItemContainerPlace.EquipRoom, gridX: 0, gridY: 0,
            expireTimeUnixSeconds: 1000u);
        Assert.True(player.Inventory.TryAddOrAutoPlaceItem(expiredMed, ItemContainerPlace.EquipRoom, 0, 0, player));
        player.CurrentLife = 100;
        sink.Clear();

        harness.EnqueueCommand(53, new UseInventoryItemZoneCommand(player.CharacterId, player.LeaseEpoch, expiredMed.RuntimeItemId, ItemContainerPlace.EquipRoom, 0, 0));
        await harness.StepFramesAsync(1);
        Assert.Equal(100, player.CurrentLife);
        Assert.Contains(sink.Events, e => e.MethodName == nameof(IPlayerOutboundSink.SendSystemMessage));

        var eqTemplate = new ItemTemplate(
            Id: 9011, Code: "expired-weapon", Name: "Vũ Khí Hết Hạn",
            Genre: ItemGenreKind.Equipment, Quality: ItemQualityKind.Normal,
            DetailType: 0, ParticularType: 0, Level: 1,
            ElementSeries: ElementSeries.Metal,
            GridWidthCells: 1, GridHeightCells: 2,
            ObjDataId: 1, AppearanceResId: 1, PriceGold: 100, MaxStackCount: 1,
            BaseAttributes: [], Requirements: []);
        var expiredEq = new PlayerItemSlot(
            runtimeItemId: player.Inventory.AllocateRuntimeItemId(),
            domainItemId: Guid.NewGuid(),
            template: eqTemplate,
            genre: ItemGenreKind.Equipment, detailType: 0, particularType: 0,
            level: 1, series: ElementSeries.Metal, quality: ItemQualityKind.Normal,
            randomSeed: 2u, generatorVersion: 2, luck: 0,
            currentDurability: 20, maxDurability: 20, stackCount: 1,
            containerPlace: ItemContainerPlace.EquipRoom, gridX: 2, gridY: 0,
            expireTimeUnixSeconds: 1000u);
        var permanentEq = new PlayerItemSlot(
            runtimeItemId: player.Inventory.AllocateRuntimeItemId(),
            domainItemId: Guid.NewGuid(),
            template: eqTemplate,
            genre: ItemGenreKind.Equipment, detailType: 0, particularType: 0,
            level: 1, series: ElementSeries.Metal, quality: ItemQualityKind.Normal,
            randomSeed: 3u, generatorVersion: 2, luck: 0,
            currentDurability: 20, maxDurability: 20, stackCount: 1,
            containerPlace: ItemContainerPlace.EquipRoom, gridX: 3, gridY: 0,
            expireTimeUnixSeconds: 0u);

        long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.True(expiredEq.IsExpired(nowUnix));
        Assert.False(PlayerInventoryGrid.CanEquipItem(player, expiredEq, EquipmentPartKind.Weapon));
        Assert.False(permanentEq.IsExpired(nowUnix));
        Assert.True(PlayerInventoryGrid.CanEquipItem(player, permanentEq, EquipmentPartKind.Weapon));
    }

    private static (ZoneSimulationHarness Harness, MapZoneWorker Worker53) CreateRealMap53Harness(SharedGameAssetsFixture assets)
    {
        Assert.True(assets.MapCatalog.TryGetMapDescriptor(53, out MapDescriptor? map53) && map53 is not null);

        var loader = new CollectibleScriptAssemblyLoader();
        ScriptRegistrySnapshot snapshot = loader.CreateSnapshotFromAssembly(1, typeof(TieuDaoTuScriptCodeResolver).Assembly);
        var scriptHost = new ZoneScriptHost(snapshot);

        var opt = new ZoneSimulationHarnessOptions
        {
            RandomSeed = 42,
        };
        opt.InitialMaps.Add(map53!);
        opt.CustomModules.Add(new ScriptingZoneModule());
        opt.CustomModules.Add(new NpcShopZoneModule(assets.ShopCatalog));
        opt.CustomModules.Add(new PartyZoneModule());
        opt.Assets = new ZoneAssetServices
        {
            MapCatalog = assets.MapCatalog,
            NpcTemplateCatalog = assets.NpcTemplateCatalog,
            TrapScriptCatalog = assets.TrapScriptCatalog,
            SkillTemplateCatalog = assets.SkillTemplateCatalog,
            MissileTemplateCatalog = assets.MissileTemplateCatalog,
            PlayerTemplateCatalog = assets.PlayerTemplateCatalog,
            ItemTemplateCatalog = assets.ItemTemplateCatalog,
            LootTableCatalog = assets.LootTableCatalog,
            ShopCatalog = assets.ShopCatalog,
            StationCatalog = assets.StationCatalog,
            StallTaxCatalog = assets.StallTaxCatalog,
            ItemGenerator = assets.ItemGenerator,
            ScriptHost = scriptHost,
        };

        var harness = new ZoneSimulationHarness(opt);
        return (harness, harness.GetZone(53));
    }

    private static int CountDynamicGroundObjects(MapZoneWorker worker)
    {
        int count = 0;
        foreach (SpatialRegionSlot r in worker.RegionGrid.ActiveRegions)
        {
            for (int i = 0; i < r.GroundObjects.Count; i++)
            {
                if (r.GroundObjects[i].Kind is GroundObjectKind.Money or GroundObjectKind.Item)
                {
                    count++;
                }
            }
        }
        return count;
    }

    private static int CountDynamicMoneyObjects(MapZoneWorker worker) => FindDynamicMoneyObjects(worker).Count;

    private static List<GroundObjectSlot> FindDynamicMoneyObjects(MapZoneWorker worker)
    {
        var result = new List<GroundObjectSlot>();
        foreach (SpatialRegionSlot r in worker.RegionGrid.ActiveRegions)
        {
            for (int i = 0; i < r.GroundObjects.Count; i++)
            {
                if (r.GroundObjects[i].Kind == GroundObjectKind.Money)
                {
                    result.Add(r.GroundObjects[i]);
                }
            }
        }
        return result;
    }

    private static CharacterDto CreateTestCharacter(
        string name,
        ElementSeries series,
        int x,
        int y,
        byte level = 10,
        long exp = 0,
        int money = 5000) =>
        new(
            Id: Guid.NewGuid(),
            AccountId: Guid.NewGuid(),
            SlotIndex: 0,
            RoleNameUnicode: name,
            Gender: CharacterGender.Male,
            Series: series,
            Level: level,
            Experience: exp,
            LeadExperience: 0,
            MapId: 53,
            RevivalMapId: 53,
            RevivalPointId: 19,
            MapMpsX: x,
            MapMpsY: y,
            CurrentLife: 1000,
            MaxLife: 1000,
            CurrentMana: 500,
            MaxMana: 500,
            CurrentStamina: 500,
            MaxStamina: 500,
            Strength: 100,
            Dexterity: 80,
            Vitality: 100,
            Energy: 50,
            Lucky: 0,
            UnallocatedAttributePoints: 0,
            UnallocatedSkillPoints: 0,
            InventoryMoney: money,
            RepositoryMoney: 0,
            CurrentFactionId: CharacterConsts.NoFactionId,
            FirstFactionId: CharacterConsts.NoFactionId,
            FactionJoinCount: 0,
            Skills: [new CharacterSkillDto(53, 1, 0), new CharacterSkillDto(1, 1, 0)]);
}
