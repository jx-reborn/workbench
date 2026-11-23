using JXReborn.Contracts.Characters;
using JXReborn.GameAssets.Configuration;
using JXReborn.GameAssets.NpcTemplates;
using JXReborn.GameAssets.Skills;
using JXReborn.GameAssets.WorldMaps;
using JXReborn.Primitives.Characters;
using JXReborn.Primitives.WorldZones;
using JXReborn.TestBase.Configuration;
using JXReborn.TestBase.SimulationHarnesses;
using JXReborn.ZoneEngine.Combat;
using JXReborn.ZoneEngine.Configuration;
using JXReborn.ZoneEngine.NpcEntities;
using JXReborn.ZoneEngine.PlayerSessions;
using JXReborn.ZoneEngine.SpatialGrid;
using JXReborn.ZoneEngine.WorldZones;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JXReborn.ZoneEngine.Tests.Combat;

/// <summary>
/// Verifies global 60% animation wind-up and full animation lock (<c>nTotalFrame = 18</c> at 0% speed)
/// for Skill 14 (Hàng Long Bất Vũ) and Skill 128 (Kháng Long Hữu Hối).
/// </summary>
[Trait("Category", "LegacyData")]
public sealed class SkillCastWindupTimingTests
{
    private static async Task<(MapZoneWorker Worker, Guid CharId, PlayerZoneState Player, PlayerOutboundEventRecorder Sink, NpcZoneState Monster)> CreateArenaAsync(
        ushort skillId,
        byte skillLevel,
        int monsterOffsetX)
    {
        IOptions<GameAssetsOptions> opts = Options.Create(new GameAssetsOptions
        {
            DataDirectory = TestWorkspacePaths.DataDirectory,
            ArtifactsDirectory = TestWorkspacePaths.EffectiveArtifactsDirectory,
            DataSnapshotFile = TestWorkspacePaths.EffectiveDataSnapshotFile,
            PreferBinarySnapshots = true,
        });
        var skillCatalog = new SkillTemplateCatalog(opts, NullLogger<SkillTemplateCatalog>.Instance);
        var missileCatalog = new MissileTemplateCatalog(opts);

        var mapDesc = new MapDescriptor(
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
                    TemplateId: 413,
                    MapId: 53,
                    SpawnMpsX: 52000 + monsterOffsetX,
                    SpawnMpsY: 101696,
                    Level: 10,
                    Kind: EntityKind.Monster,
                    Camp: EntityCamp.Animal,
                    Series: ElementSeries.Wood,
                    IsSpecialNpc: false,
                    DisplayName: "Cọc gỗ",
                    ScriptPath: string.Empty),
            ],
        };

        var worker = new MapZoneWorker(
            mapDesc,
            new InMemorySessionLeaseAppServiceStub(),
            new ZoneEngineOptions { RandomSeed = 2 },
            TimeProvider.System,
            NullLogger.Instance,
            skillTemplateCatalog: skillCatalog,
            missileTemplateCatalog: missileCatalog);

        var sink = new PlayerOutboundEventRecorder();
        Guid charId = Guid.NewGuid();
        var dto = new CharacterDto(
            Id: charId,
            AccountId: Guid.NewGuid(),
            SlotIndex: 0,
            RoleNameUnicode: "WindupHero",
            Gender: CharacterGender.Male,
            Series: ElementSeries.Fire,
            Level: 150,
            Experience: 0,
            LeadExperience: 0,
            MapId: 53,
            RevivalMapId: 53,
            RevivalPointId: 19,
            MapMpsX: 52000,
            MapMpsY: 101696,
            CurrentLife: 5000,
            MaxLife: 5000,
            CurrentMana: 5000,
            MaxMana: 5000,
            CurrentStamina: 500,
            MaxStamina: 500,
            Strength: 100,
            Dexterity: 100,
            Vitality: 100,
            Energy: 100,
            Lucky: 0,
            UnallocatedAttributePoints: 0,
            UnallocatedSkillPoints: 0,
            InventoryMoney: 1000,
            RepositoryMoney: 0,
            CurrentFactionId: 6,
            FirstFactionId: 6,
            FactionJoinCount: 1,
            Skills:
            [
                new CharacterSkillDto(skillId, skillLevel, 0),
            ]);

        worker.TryEnqueueCommand(new AttachPlayerZoneCommand(dto, 1L, sink));
        worker.TryEnqueueCommand(new CompletePlayerSyncZoneCommand(charId, 1L, 0x1111u, 1u));
        await worker.StepSingleTickAsync();

        Assert.NotEmpty(worker.RegionGrid.ActiveRegions);
        PlayerZoneState? player = null;
        int foundPlayers = 0;
        foreach (SpatialRegionSlot region in worker.RegionGrid.ActiveRegions)
        {
            foreach (PlayerZoneState p in region.Players)
            {
                p.FightMode = 1;
                player = p;
                foundPlayers++;
            }
        }

        Assert.Equal(1, foundPlayers);
        Assert.NotNull(player);
        NpcZoneState monster = worker.StaticNpcs[1u];
        return (worker, charId, player!, sink, monster);
    }

    [Fact]
    public async Task CastSkill128KhangLongHuuHoi_ShouldRespect60PercentWindupAndFull18FrameAnimationLock()
    {
        var (worker, charId, _, sink, monster) = await CreateArenaAsync(skillId: 128, skillLevel: 20, monsterOffsetX: 96);
        uint initialHp = monster.CurLife;

        // Tick 0: cast Skill 128 (CastFrame=18, 0% CastSpeed -> totalAnimFrames=18, castWindupFrames=10)
        worker.TryEnqueueCommand(new CastSkillZoneCommand(charId, 1L, 128, -1, (int)monster.NpcId));
        await worker.StepSingleTickAsync();
        Assert.Single(sink.SkillCastBroadcasts);

        // During wind-up ticks 1..9: no missile spawned and monster HP must remain untouched
        for (int t = 0; t < 9; t++)
        {
            await worker.StepSingleTickAsync();
        }

        Assert.Equal(0, worker.ActiveMissileCount);
        Assert.Equal(initialHp, monster.CurLife);

        // Re-casting at tick +9 (inside 18-frame full animation lock) MUST be rejected
        worker.TryEnqueueCommand(new CastSkillZoneCommand(charId, 1L, 128, -1, (int)monster.NpcId));
        await worker.StepSingleTickAsync();
        Assert.Single(sink.SkillCastBroadcasts);

        // At tick +10 wind-up fires -> dragons spawn and then hit monster during flight
        for (int t = 0; t < 8; t++)
        {
            await worker.StepSingleTickAsync();
        }

        Assert.True(monster.CurLife < initialHp, "Expected Kháng Long Hữu Hối to deal damage after 60% wind-up + dragon flight.");
    }

    [Fact]
    public async Task CastSkill14HangLongBatVu_ShouldNotDealDamageBeforeFrame10Windup()
    {
        var (worker, charId, _, sink, monster) = await CreateArenaAsync(skillId: 14, skillLevel: 5, monsterOffsetX: 64);
        uint initialHp = monster.CurLife;

        worker.TryEnqueueCommand(new CastSkillZoneCommand(charId, 1L, 14, -1, (int)monster.NpcId));
        await worker.StepSingleTickAsync();
        Assert.Single(sink.SkillCastBroadcasts);

        // Ticks 1..9: strictly inside 60% wind-up -> no damage yet
        for (int t = 0; t < 9; t++)
        {
            await worker.StepSingleTickAsync();
        }

        Assert.Equal(initialHp, monster.CurLife);

        // Tick 10+: missile 66 spawns and deals damage
        for (int t = 0; t < 6; t++)
        {
            await worker.StepSingleTickAsync();
        }

        Assert.True(monster.CurLife < initialHp, "Expected Hàng Long Bất Vũ to deal damage after 60% wind-up.");
    }

    [Fact]
    public async Task CastSkill128KhangLongHuuHoiLv1_At256Mps_ShouldImpactOnExactLegacyExplosionFrame20()
    {
        // Matches character Dragon (Skill 128 Lv1, Speed=28 MPS/frame, CollidRange=1, CastFrame=18, 0% CastSpeed)
        // casting at a monster 256 MPS (8 cells) away:
        // - Tick 0: cast command received, 0x5A broadcast
        // - Tick 10: wind-up fires -> Missile 48 spawns at d=0, enters DoWait (KMissle::CreateMissle -> DoWait)
        // - Tick 11..19: KMissle::OnFly checks collision before stepping (d=0, 28, ..., 224; at d=224 distance is 32 > 16)
        // - Tick 20: KMissle::OnFly checks d=252 (|256-252|=4 <= 16 exact cell) -> HIT!
        //   NpcLifeSync arrives on game.exe on tick 21, matching game.exe's local AnimFile4 explosion tick 21.
        var (worker, charId, _, sink, monster) = await CreateArenaAsync(skillId: 128, skillLevel: 1, monsterOffsetX: 256);
        uint initialHp = monster.CurLife;

        // Tick 0: cast Skill 128 Lv1
        worker.TryEnqueueCommand(new CastSkillZoneCommand(charId, 1L, 128, -1, (int)monster.NpcId));
        await worker.StepSingleTickAsync();
        Assert.Single(sink.SkillCastBroadcasts);

        // Ticks 1..19: dragon has NOT entered the monster's cell yet -> HP MUST stay full
        for (int t = 1; t <= 19; t++)
        {
            await worker.StepSingleTickAsync();
            Assert.True(
                monster.CurLife == initialHp,
                $"Kháng Long Hữu Hối dealt damage too early on tick {t} (expected tick 20, aligning with game.exe AnimFile4 explosion).");
        }

        // Tick 20: dragon reaches d=252 (within 16 MPS exact cell of monster at 256) -> deals damage now
        await worker.StepSingleTickAsync();
        Assert.True(
            monster.CurLife < initialHp,
            "Expected Kháng Long Hữu Hối Lv1 (Speed=28) to deal damage on tick 20 at 256 MPS.");
    }

    [Theory]
    [InlineData(64, 14)]
    [InlineData(160, 17)]
    [InlineData(256, 20)]
    [InlineData(400, 26)]
    public async Task CastSkill128KhangLongHuuHoiLv1_DistanceSweep_ShouldImpactOnExactExpectedTick(
        int distanceMps,
        int expectedImpactTick)
    {
        var (worker, charId, _, sink, monster) = await CreateArenaAsync(skillId: 128, skillLevel: 1, monsterOffsetX: distanceMps);
        uint initialHp = monster.CurLife;

        worker.TryEnqueueCommand(new CastSkillZoneCommand(charId, 1L, 128, -1, (int)monster.NpcId));
        await worker.StepSingleTickAsync();
        Assert.Single(sink.SkillCastBroadcasts);

        for (int t = 1; t < expectedImpactTick; t++)
        {
            await worker.StepSingleTickAsync();
            Assert.True(
                monster.CurLife == initialHp,
                $"Distance={distanceMps} MPS dealt damage early on tick {t} (expected tick {expectedImpactTick}).");
        }

        await worker.StepSingleTickAsync();
        Assert.True(
            monster.CurLife < initialHp,
            $"Distance={distanceMps} MPS did not deal damage on expected tick {expectedImpactTick}.");
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(50, 17)]
    [InlineData(-30, 25)]
    public async Task CastSkill128KhangLongHuuHoiLv1_CastSpeedSweep_ShouldShiftWindupAndImpactTick(
        int castSpeedBonus,
        int expectedImpactTick)
    {
        var (worker, charId, player, sink, monster) = await CreateArenaAsync(skillId: 128, skillLevel: 1, monsterOffsetX: 256);
        player.EquipmentCombatModifiers.CastSpeedBonus = castSpeedBonus;
        uint initialHp = monster.CurLife;

        worker.TryEnqueueCommand(new CastSkillZoneCommand(charId, 1L, 128, -1, (int)monster.NpcId));
        await worker.StepSingleTickAsync();
        Assert.Single(sink.SkillCastBroadcasts);

        for (int t = 1; t < expectedImpactTick; t++)
        {
            await worker.StepSingleTickAsync();
            Assert.True(
                monster.CurLife == initialHp,
                $"CastSpeedBonus={castSpeedBonus}% dealt damage early on tick {t} (expected tick {expectedImpactTick}).");
        }

        await worker.StepSingleTickAsync();
        Assert.True(
            monster.CurLife < initialHp,
            $"CastSpeedBonus={castSpeedBonus}% did not deal damage on expected tick {expectedImpactTick}.");
    }

    [Fact]
    public async Task CastSkill128KhangLongHuuHoiLv1_WhenCasterDiesDuringWindup_ShouldCancelAndDealZeroPhantomDamage()
    {
        var (worker, charId, player, sink, monster) = await CreateArenaAsync(skillId: 128, skillLevel: 1, monsterOffsetX: 160);
        uint initialHp = monster.CurLife;

        // Tick 0: cast Skill 128 (windup = 10 frames)
        worker.TryEnqueueCommand(new CastSkillZoneCommand(charId, 1L, 128, -1, (int)monster.NpcId));
        await worker.StepSingleTickAsync();
        Assert.Single(sink.SkillCastBroadcasts);

        // Step 5 frames (mid wind-up), then kill caster
        for (int t = 0; t < 5; t++)
        {
            await worker.StepSingleTickAsync();
        }

        player.CurrentLife = 0;
        player.Doing = EntityDoingPosture.Death;

        // Step past original wind-up + flight window -> must never spawn missile or deal phantom damage
        for (int t = 0; t < 20; t++)
        {
            await worker.StepSingleTickAsync();
        }

        Assert.Equal(0, worker.ActiveMissileCount);
        Assert.Equal(initialHp, monster.CurLife);
    }
}
