using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JXReborn.Contracts.Characters;
using JXReborn.GameAssets.NpcTemplates;
using JXReborn.GameAssets.PlayerTemplates;
using JXReborn.GameAssets.Skills;
using JXReborn.GameAssets.WorldMaps;
using JXReborn.Primitives.Characters;
using JXReborn.Primitives.Combat;
using JXReborn.Primitives.WorldZones;
using JXReborn.TestBase.Fixtures;
using JXReborn.TestBase.SimulationHarnesses;
using JXReborn.ZoneEngine.Combat;
using JXReborn.ZoneEngine.Factions;
using JXReborn.ZoneEngine.NpcEntities;
using JXReborn.ZoneEngine.PlayerSessions;
using JXReborn.ZoneEngine.WorldZones;
using Xunit;

namespace JXReborn.ZoneEngine.Tests.Skills;

/// <summary>
/// Ground-Truth audit suite covering Classic Fire &amp; Earth Sects (<c>Cái Bang</c> Faction 6, <c>Thiên Nhẫn</c> Faction 7,
/// <c>Võ Đang</c> Faction 8, <c>Côn Lôn</c> Faction 9) and global skill mechanics (<c>addskilldamage1..6</c> 9x synergy,
/// <c>SkillExp</c> proficiency progression, <c>Skill 210</c> <c>Khinh Công</c>, <c>EquipLimit</c>, and 9x curse prerequisites)
/// anchored to <c>legacy-server/bin/server1/settings/skills.txt</c>, <c>missles.txt</c>, <c>script/skill/*.lua</c>,
/// <c>053_ba-lang-huyen.map.json</c>, and decompiled <c>jx_linux_y</c> binaries in <c>legacy-server/discovery/</c>.
/// </summary>
public sealed class FireEarthSectSkillsAndSynergyAuditTests
{
    private static ZoneSimulationHarness CreateCombatArenaHarness(
        int dummyMpsX = 52160,
        int dummyMpsY = 101696,
        int randomSeed = 42,
        bool useRealMapObstacles = false)
    {
        SharedGameAssetsFixture fixture = SharedGameAssetsFixture.Default;

        MapDescriptor arenaMap;
        if (useRealMapObstacles)
        {
            Assert.True(fixture.MapCatalog.TryGetMapDescriptor(53, out MapDescriptor? realMap53) && realMap53 is not null);
            arenaMap = realMap53;
        }
        else
        {
            arenaMap = new MapDescriptor(
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
                        SpawnMpsX: dummyMpsX,
                        SpawnMpsY: dummyMpsY,
                        Level: 90,
                        Kind: EntityKind.Monster,
                        Camp: EntityCamp.Animal,
                        Series: ElementSeries.Wood,
                        IsSpecialNpc: false,
                        DisplayName: "Mộc Nhân",
                        ScriptPath: string.Empty),
                ],
            };
        }

        var assets = new ZoneAssetServices
        {
            MapCatalog = useRealMapObstacles ? fixture.MapCatalog : null,
            NpcTemplateCatalog = fixture.NpcTemplateCatalog,
            SkillTemplateCatalog = fixture.SkillTemplateCatalog,
            MissileTemplateCatalog = fixture.MissileTemplateCatalog,
            PlayerTemplateCatalog = fixture.PlayerTemplateCatalog,
            ItemTemplateCatalog = fixture.ItemTemplateCatalog,
            LootTableCatalog = fixture.LootTableCatalog,
            ItemGenerator = fixture.ItemGenerator,
        };

        var options = new ZoneSimulationHarnessOptions
        {
            RandomSeed = randomSeed,
            TicksPerSecond = 18,
            Assets = assets,
        };
        options.InitialMaps.Add(arenaMap);

        var harness = new ZoneSimulationHarness(options);
        if (!useRealMapObstacles)
        {
            MapZoneWorker worker = harness.GetZone(53);
            NpcZoneState dummy = Assert.Single(worker.StaticNpcs.Values);
            dummy.AiMode = 0;
            dummy.ActiveRadiusMps = 0;
            dummy.CurLife = 5_000_000u;
        }

        return harness;
    }

    private static CharacterDto CreateSectCharacterDto(
        string roleName,
        ElementSeries series,
        byte factionId,
        CharacterSkillDto[] skills,
        int mpsX = 52032,
        int mpsY = 101696,
        byte level = 120,
        ushort maxLife = 4000,
        ushort maxMana = 4000,
        ushort unallocatedSkillPoints = 5,
        PlayerPkMode pkState = PlayerPkMode.Fight)
    {
        return new CharacterDto(
            Id: Guid.NewGuid(),
            AccountId: Guid.NewGuid(),
            SlotIndex: 0,
            RoleNameUnicode: roleName,
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
            CurrentLife: maxLife,
            MaxLife: maxLife,
            CurrentMana: maxMana,
            MaxMana: maxMana,
            CurrentStamina: 300,
            MaxStamina: 300,
            Strength: 200,
            Dexterity: 200,
            Vitality: 150,
            Energy: 200,
            Lucky: 0,
            UnallocatedAttributePoints: 0,
            UnallocatedSkillPoints: unallocatedSkillPoints,
            InventoryMoney: 10_000,
            RepositoryMoney: 0,
            CurrentFactionId: factionId,
            FirstFactionId: factionId,
            FactionJoinCount: 1,
            Skills: skills,
            PkState: pkState,
            FightMode: 0);
    }

    private static List<ActiveMissileSlot> GetActiveMissilesForSkill(MapZoneWorker worker, int skillId)
    {
        var matches = new List<ActiveMissileSlot>();
        for (int i = 0; i < worker.MissileSystem.PoolCapacity; i++)
        {
            ActiveMissileSlot slot = worker.MissileSystem.GetSlot(i);
            if (slot.IsActive && slot.SkillId == skillId)
            {
                matches.Add(slot);
            }
        }

        return matches;
    }

    private static HashSet<int> CaptureObservedActiveMissileSkillsOverFrames(
        ZoneSimulationHarness harness,
        MapZoneWorker worker,
        int frames,
        out Func<Task> runCaptureAsync)
    {
        var observedSkillIds = new HashSet<int>();
        runCaptureAsync = async () =>
        {
            for (int f = 0; f < frames; f++)
            {
                for (int i = 0; i < worker.MissileSystem.PoolCapacity; i++)
                {
                    ActiveMissileSlot slot = worker.MissileSystem.GetSlot(i);
                    if (slot.IsActive)
                    {
                        observedSkillIds.Add(slot.SkillId);
                    }
                }

                await harness.StepFramesAsync(1);

                for (int i = 0; i < worker.MissileSystem.PoolCapacity; i++)
                {
                    ActiveMissileSlot slot = worker.MissileSystem.GetSlot(i);
                    if (slot.IsActive)
                    {
                        observedSkillIds.Add(slot.SkillId);
                    }
                }
            }
        };

        return observedSkillIds;
    }

    private static SkillEffectScalingTemplate RequireEffectAttribute(SkillTemplate skill, int attributeId)
    {
        for (int i = 0; i < skill.EffectScaling.Count; i++)
        {
            if (skill.EffectScaling[i].AttributeId == attributeId)
            {
                return skill.EffectScaling[i];
            }
        }

        Assert.Fail($"Expected attributeId={attributeId} on skill {skill.Id} ({skill.Name}).");
        return null!;
    }

    [Fact]
    public void FireAndEarthSectInductionCatalogs_FactionsSixThroughNine_ShouldGrantExactStarterSkillTreesWithNonEmptyScaling()
    {
        SharedGameAssetsFixture fixture = SharedGameAssetsFixture.Default;
        IPlayerTemplateCatalog playerCatalog = fixture.PlayerTemplateCatalog;
        ISkillTemplateCatalog skillCatalog = fixture.SkillTemplateCatalog;

        (byte FactionId, string Code, byte ExpectedSeries, byte ExpectedCamp, int[] ExpectedStarterSkills)[] expectedFactions =
        [
            (6, "cai-bang", (byte)ElementSeries.Fire, (byte)EntityCamp.Justice, [119, 122, 115, 116, 129, 124, 274, 277, 125, 128, 130, 360]),
            (7, "thien-nhan-giao", (byte)ElementSeries.Fire, (byte)EntityCamp.Evil, [135, 145, 131, 132, 136, 137, 138, 140, 141, 364, 143, 142, 148, 150]),
            (8, "vo-dang-phai", (byte)ElementSeries.Earth, (byte)EntityCamp.Justice, [153, 155, 151, 152, 159, 158, 164, 160, 157, 165, 166, 267]),
            (9, "con-lon-phai", (byte)ElementSeries.Earth, (byte)EntityCamp.Balance, [169, 179, 167, 168, 171, 392, 174, 172, 173, 178, 393, 175, 181, 90, 176, 182, 275, 630]),
        ];

        Assert.NotEmpty(expectedFactions);
        foreach ((byte factionId, string code, byte expectedSeries, byte expectedCamp, int[] expectedSkills) in expectedFactions)
        {
            Assert.True(playerCatalog.TryGetFactionById(factionId, out FactionTemplate? faction) && faction is not null);
            Assert.Equal(code, faction.Code);
            Assert.Equal(expectedSeries, faction.Series);
            Assert.Equal(expectedCamp, faction.Camp);
            Assert.Equal(expectedSkills, faction.SkillIds.ToArray());

            Assert.NotEmpty(faction.SkillIds);
            foreach (int skillId in faction.SkillIds)
            {
                Assert.True(skillCatalog.TryGetSkill(skillId, out SkillTemplate? skill) && skill is not null);
                Assert.NotEmpty(skill.EffectScaling);
            }
        }

        int[] masteryAnd120Skills = [357, 359, 361, 362, 365, 368, 372, 375, 391, 394, 714, 715, 716, 717];
        Assert.NotEmpty(masteryAnd120Skills);
        foreach (int skillId in masteryAnd120Skills)
        {
            Assert.True(skillCatalog.TryGetSkill(skillId, out SkillTemplate? skill) && skill is not null);
            Assert.NotEmpty(skill.EffectScaling);
        }
    }

    [Fact]
    public async Task GaibangPalmMissiles_LevelBreakpointsInLuaCurves_ShouldScaleDragonCountAndFollowTrajectory()
    {
        SharedGameAssetsFixture fixture = SharedGameAssetsFixture.Default;
        Assert.True(fixture.SkillTemplateCatalog.TryGetSkill(128, out SkillTemplate? khangLong) && khangLong is not null);
        Assert.True(fixture.MissileTemplateCatalog.TryGetMissile(khangLong.MissileId, out MissileTemplate? khangLongMissile) && khangLongMissile is not null);
        Assert.True(fixture.SkillTemplateCatalog.TryGetSkill(357, out SkillTemplate? phiLong) && phiLong is not null);
        Assert.True(fixture.MissileTemplateCatalog.TryGetMissile(phiLong.MissileId, out MissileTemplate? phiLongMissile) && phiLongMissile is not null);

        // gaibang.lua: Kháng Long Hữu Hối (128) scales ChildSkillCount 1 (lv1..10) -> 2 (lv11) -> 8 (lv15) -> 15 (lv20) -> 18 (lv25..30)
        Assert.Equal(1, SkillEffectiveParams.Resolve(khangLong, khangLongMissile, 10).ChildSkillCount);
        Assert.Equal(2, SkillEffectiveParams.Resolve(khangLong, khangLongMissile, 11).ChildSkillCount);
        Assert.Equal(8, SkillEffectiveParams.Resolve(khangLong, khangLongMissile, 15).ChildSkillCount);
        Assert.Equal(15, SkillEffectiveParams.Resolve(khangLong, khangLongMissile, 20).ChildSkillCount);
        Assert.Equal(18, SkillEffectiveParams.Resolve(khangLong, khangLongMissile, 25).ChildSkillCount);

        // gaibang.lua: Phi Long Tại Thiên (357, Missile 166 MoveKind.Follow) scales 1 (lv1..11) -> 2 (lv12..15) -> 3 (lv16..19) -> 4 (lv20..30)
        Assert.Equal(166, phiLong.MissileId);
        Assert.Equal(MissileMoveKind.Follow, phiLongMissile.MoveKind);
        Assert.Equal(1, SkillEffectiveParams.Resolve(phiLong, phiLongMissile, 11).ChildSkillCount);
        Assert.Equal(2, SkillEffectiveParams.Resolve(phiLong, phiLongMissile, 12).ChildSkillCount);
        Assert.Equal(3, SkillEffectiveParams.Resolve(phiLong, phiLongMissile, 16).ChildSkillCount);
        Assert.Equal(4, SkillEffectiveParams.Resolve(phiLong, phiLongMissile, 20).ChildSkillCount);

        ZoneSimulationHarness harness = CreateCombatArenaHarness(dummyMpsX: 52320, dummyMpsY: 101696, randomSeed: 42);
        MapZoneWorker worker = harness.GetZone(53);
        NpcZoneState dummy = Assert.Single(worker.StaticNpcs.Values);

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "CáiBangChưởng",
                ElementSeries.Fire,
                factionId: 6,
                skills:
                [
                    new CharacterSkillDto(116, 20, 0),
                    new CharacterSkillDto(122, 20, 0),
                    new CharacterSkillDto(128, 20, 0),
                    new CharacterSkillDto(274, 20, 0),
                    new CharacterSkillDto(357, 20, 0),
                ]));

        PlayerZoneState? caster = handle.GetZoneState();
        Assert.NotNull(caster);
        caster.FightMode = 1;
        uint initialDummyLife = dummy.CurLife;

        // Cast Phi Long Tại Thiên (357) at lv20 targeting the monster (DoOrdinSkill @ 0x0809c160 + OnSkill IsReachFrame(60) @ 0x0809bdb0: CastFrame=18 at CastSpeed=0 -> 10 windup frames)
        handle.CastSkill(357, -1, (int)dummy.NpcId);
        await harness.StepFramesAsync(11);

        List<ActiveMissileSlot> activeDragons = GetActiveMissilesForSkill(worker, 357);
        Assert.Equal(4, activeDragons.Count);
        Assert.NotEmpty(activeDragons);
        foreach (ActiveMissileSlot dragon in activeDragons)
        {
            Assert.Equal(166, dragon.MissileTemplateId);
            Assert.Equal(MissileMoveKind.Follow, dragon.MoveKind);
            Assert.Equal(dummy.NpcId, dragon.FollowTargetNpcId);
        }

        await harness.StepFramesAsync(18);
        Assert.True(dummy.CurLife < initialDummyLife, $"Expected 4 homing dragons of skill 357 to deal fire damage, CurLife={dummy.CurLife}");
    }

    [Fact]
    public async Task GaibangPassivesAndBuffs_LevelTwentyRuntimeModifiers_ShouldApplyExactLifePenaltyFireMagicAndRunSpeed()
    {
        ZoneSimulationHarness harness = CreateCombatArenaHarness();
        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "ĐệTửCáiBang",
                ElementSeries.Fire,
                factionId: 6,
                maxLife: 4000,
                maxMana: 2000,
                skills:
                [
                    new CharacterSkillDto(116, 20, 0), // Cái Bang Chưởng Pháp: +275 addfiremagic_v
                    new CharacterSkillDto(274, 20, 0), // Giáng Long Chưởng: -25% lifemax_p, +50% manamax_p, +750 addfiremagic_v
                    new CharacterSkillDto(360, 20, 0), // Tiêu Diêu Công: +65 attackspeed_v, +65 castspeed_v
                    new CharacterSkillDto(277, 20, 0), // Hoạt Bất Lưu Thủ: +66% fastwalkrun_p (3240 frames)
                    new CharacterSkillDto(130, 20, 0), // Túy Điệp Cuồng Vũ: +20% allres_p, +144 addfiremagic_v, +30% deadlystrike
                ]));

        PlayerZoneState? player = handle.GetZoneState();
        Assert.NotNull(player);
        player.FightMode = 1;

        // Giáng Long Chưởng lv20: LifeMaxPercent = -25%, ManaMaxPercent = +50%, MagicFireMin/Max = 275 + 750 = 1025
        Assert.Equal(-25, player.SkillCombatModifiers.LifeMaxPercent);
        Assert.Equal(50, player.SkillCombatModifiers.ManaMaxPercent);
        Assert.Equal(4000 - ((4000 * 25) / 100), player.CurrentLifeMaxYin);
        Assert.Equal(2000 + ((2000 * 50) / 100), player.EffectiveMaxMana);
        Assert.Equal(1025, player.SkillCombatModifiers.MagicFireMinDamage);
        Assert.Equal(1025, player.SkillCombatModifiers.MagicFireMaxDamage);
        Assert.Equal(65, ((ICombatEntityState)player).AttackSpeedBonus);
        Assert.Equal(65, ((ICombatEntityState)player).CastSpeedBonus);

        int baseRunSpeed = player.EffectiveRunSpeed;
        Assert.Equal(10, baseRunSpeed);

        // Cast Hoạt Bất Lưu Thủ (277) on self (WaitFrames = 5, M114 LifeFrames = 2) -> +66% FastWalkRunPercent -> RunSpeed 10 -> 16
        handle.CastSkill(277, player.CurrentMpsX, player.CurrentMpsY);
        await harness.StepFramesAsync(10);
        Assert.Equal(66, player.SkillCombatModifiers.FastWalkRunPercent);
        Assert.Equal(16, player.EffectiveRunSpeed);

        // Cast Túy Điệp Cuồng Vũ (130) SelfBuff (DoOrdinSkill @ 0x0809c160: CastFrame=18 at CastSpeed=65 -> 10 total frames, 6 windup frames)
        handle.CastSkill(130, player.CurrentMpsX, player.CurrentMpsY);
        await harness.StepFramesAsync(7);
        Assert.Equal(20, player.SkillCombatModifiers.FireResistPercent);
        Assert.Equal(20, player.SkillCombatModifiers.PhysicsResistPercent);
        Assert.Equal(1169, player.SkillCombatModifiers.MagicFireMinDamage);
    }

    [Fact]
    public async Task TianrenFireWallAndMeteorShower_LevelTwentyRuntimeSimulation_ShouldTickContinuousPillarsAndSpawnGroundFireOnVanish()
    {
        SharedGameAssetsFixture fixture = SharedGameAssetsFixture.Default;
        Assert.True(fixture.MissileTemplateCatalog.TryGetMissile(171, out MissileTemplate? meteorMissile) && meteorMissile is not null);
        Assert.True(fixture.MissileTemplateCatalog.TryGetMissile(170, out MissileTemplate? groundFireMissile) && groundFireMissile is not null);

        // Thiên Ngoại Lưu Tinh (M171): Height=320, ZSpeed=-40960, ZAccel=1024, Life=14 -> OnVanish Nghiệp Hỏa Phần Thành (363, M170: Life=54, DamageInterval=6)
        Assert.Equal(320, meteorMissile.HeightMps);
        Assert.Equal(-40960, meteorMissile.ZSpeedMpsPerFrame);
        Assert.Equal(14, meteorMissile.LifeFrames);
        Assert.Equal(54, groundFireMissile.LifeFrames);
        Assert.Equal(6, groundFireMissile.DamageIntervalFrames);

        ZoneSimulationHarness harness = CreateCombatArenaHarness(dummyMpsX: 52224, dummyMpsY: 101696, randomSeed: 7);
        MapZoneWorker worker = harness.GetZone(53);
        NpcZoneState dummy = Assert.Single(worker.StaticNpcs.Values);

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "ThánhHỏaThiênNhẫn",
                ElementSeries.Fire,
                factionId: 7,
                skills:
                [
                    new CharacterSkillDto(145, 20, 0),
                    new CharacterSkillDto(138, 20, 0),
                    new CharacterSkillDto(148, 20, 0),
                    new CharacterSkillDto(362, 20, 0),
                ]));

        PlayerZoneState? caster = handle.GetZoneState();
        Assert.NotNull(caster);
        caster.FightMode = 1;

        // 1. Cast Thôi Sơn Điền Hải (138, WaitFrames=10, centerExtendLine delay=5 -> all 10 M55 Life=120 pillars active by frame 36)
        uint lifeBeforeWall = dummy.CurLife;
        handle.CastSkill(138, dummy.MpsX, dummy.MpsY);
        await harness.StepFramesAsync(36);

        List<ActiveMissileSlot> wallPillars = GetActiveMissilesForSkill(worker, 138);
        Assert.Equal(10, wallPillars.Count);
        await harness.StepFramesAsync(18);
        uint lifeAfterWallTicks = dummy.CurLife;
        Assert.True(lifeAfterWallTicks < lifeBeforeWall, "Expected Thôi Sơn Điền Hải firewall pillars to tick continuous fire damage.");

        // Clear remaining firewall life and cast Thiên Ngoại Lưu Tinh (362, CharAnimId=11 -> DoOrdinSkill @ 0x0809c160 10-frame windup at CastSpeed=0, M171 Life=14)
        await harness.StepFramesAsync(120);
        dummy.CurLife = 5_000_000u;

        handle.CastSkill(362, dummy.MpsX, dummy.MpsY);
        await harness.StepFramesAsync(11);
        Assert.NotEmpty(GetActiveMissilesForSkill(worker, 362));

        // After 14 frames, meteor (362) vanishes and spawns Nghiệp Hỏa Phần Thành (363) burning the ground for 54 frames
        await harness.StepFramesAsync(15);
        List<ActiveMissileSlot> groundFires = GetActiveMissilesForSkill(worker, 363);
        Assert.NotEmpty(groundFires);
        Assert.All(groundFires, slot => Assert.Equal(170, slot.MissileTemplateId));

        uint lifeBeforeGroundTicks = dummy.CurLife;
        await harness.StepFramesAsync(18);
        Assert.True(dummy.CurLife < lifeBeforeGroundTicks, "Expected Nghiệp Hỏa Phần Thành (363) ground fire to tick periodic fire damage.");
    }

    [Fact]
    public async Task WudangManaShield_SufficientManaWithFistMasteryOffset_ShouldAbsorbExactSeventyFourPercentIntoMana()
    {
        ZoneSimulationHarness harness = CreateCombatArenaHarness(randomSeed: 11);
        SimulatedPlayerHandle defenderHandle = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "VõĐangTọaVọng",
                ElementSeries.Earth,
                factionId: 8,
                maxLife: 5000,
                maxMana: 5000,
                skills:
                [
                    new CharacterSkillDto(152, 20, 0), // Võ Đang Quyền Pháp: manashield_p = -25%
                    new CharacterSkillDto(157, 20, 0), // Tọa Vọng Vô Ngã (SelfBuff): manashield_p = +99%
                ]));

        PlayerZoneState? defender = defenderHandle.GetZoneState();
        Assert.NotNull(defender);
        defender.FightMode = 1;
        defender.SpawnProtectUntilTick = 0;

        // Before casting Tọa Vọng Vô Ngã (157), passive 152 contributes -25%
        Assert.Equal(-25, ((ICombatEntityState)defender).ManaShieldPercent);

        // Cast Tọa Vọng Vô Ngã (157, CharAnimId=11 -> DoOrdinSkill @ 0x0809c160 10-frame windup at CastSpeed=0) -> net ManaShieldPercent = 99 - 25 = 74%
        defenderHandle.CastSkill(157, defender.CurrentMpsX, defender.CurrentMpsY);
        await harness.StepFramesAsync(11);
        Assert.Equal(74, ((ICombatEntityState)defender).ManaShieldPercent);

        defender.CurrentLife = 4000;
        defender.CurrentMana = 4000;
        defender.PhysicsResistPercent = 0;
        defender.BasePhysicsResist = 0;

        var incomingPayload = new SkillDamagePayload(
            SkillId: 53,
            SkillLevel: 1,
            ElementSeries: ElementSeries.Metal,
            IsPhysical: true,
            IsMelee: true,
            UseAttackRating: false,
            DoHurtAnimation: false,
            AttackRatingScore: 10_000,
            PhysicsMinDamage: 1000,
            PhysicsMaxDamage: 1000,
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
            MagicMaxDamage: 0,
            HasElementSeries: false,
            CasterKind: EntityKind.Monster);

        CombatHitResolutionResult hit = CombatDamageCalculator.ResolveHitAndApplyDamage(
            attacker: null,
            defender: defender,
            isPvp: false,
            in incomingPayload,
            new Random(1));

        // Raw damage = 1000 -> manaAbsorb = (1000 * 74) / 100 = 740 mana, HP damage = 1000 - 740 = 260
        Assert.True(hit.HitLanded);
        Assert.Equal(260, hit.TotalHpDamageDealt);
        Assert.Equal(4000 - 740, defender.CurrentMana);
        Assert.Equal(4000 - 260, defender.CurrentLife);
    }

    [Fact]
    public async Task WudangManaShield_ManaDepletedMidHit_ShouldCollapseManaToZeroAndApplyFullDamageToLife()
    {
        ZoneSimulationHarness harness = CreateCombatArenaHarness(randomSeed: 11);
        SimulatedPlayerHandle defenderHandle = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "VõĐangCạnNộiLực",
                ElementSeries.Earth,
                factionId: 8,
                maxLife: 5000,
                maxMana: 5000,
                skills:
                [
                    new CharacterSkillDto(152, 20, 0), // -25% manashield_p
                    new CharacterSkillDto(157, 20, 0), // +99% manashield_p -> net 74%
                ]));

        PlayerZoneState? defender = defenderHandle.GetZoneState();
        Assert.NotNull(defender);
        defender.FightMode = 1;
        defender.SpawnProtectUntilTick = 0;

        // DoOrdinSkill @ 0x0809c160: CastFrame=18 at CastSpeed=0 -> 10-frame windup before SelfBuff 157 applies.
        defenderHandle.CastSkill(157, defender.CurrentMpsX, defender.CurrentMpsY);
        await harness.StepFramesAsync(11);
        Assert.Equal(74, ((ICombatEntityState)defender).ManaShieldPercent);

        // Set CurrentMana = 500 (< required 740 mana absorb for a 1000-damage hit).
        // Per KNpc::CalcDamage @ 0x0809e790: when CurrentMana < manaAbsorb, CurrentMana drops to 0 AND HP takes 100% of damage (1000)!
        defender.CurrentLife = 4000;
        defender.CurrentMana = 500;
        defender.PhysicsResistPercent = 0;
        defender.BasePhysicsResist = 0;

        var incomingPayload = new SkillDamagePayload(
            SkillId: 53,
            SkillLevel: 1,
            ElementSeries: ElementSeries.Metal,
            IsPhysical: true,
            IsMelee: true,
            UseAttackRating: false,
            DoHurtAnimation: false,
            AttackRatingScore: 10_000,
            PhysicsMinDamage: 1000,
            PhysicsMaxDamage: 1000,
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
            MagicMaxDamage: 0,
            HasElementSeries: false,
            CasterKind: EntityKind.Monster);

        CombatHitResolutionResult hit = CombatDamageCalculator.ResolveHitAndApplyDamage(
            attacker: null,
            defender: defender,
            isPvp: false,
            in incomingPayload,
            new Random(1));

        Assert.True(hit.HitLanded);
        Assert.Equal(0, defender.CurrentMana);
        Assert.Equal(1000, hit.TotalHpDamageDealt);
        Assert.Equal(3000, defender.CurrentLife);
    }

    [Fact]
    public async Task WudangChunYuBuRan_WhenAfflictedByFreezeAndSlowCurse_ShouldCleanseNegativeStatesAndBlockIncomingCurses()
    {
        ZoneSimulationHarness harness = CreateCombatArenaHarness(randomSeed: 3);
        SimulatedPlayerHandle wudangHandle = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "VõĐangThanhTịnh",
                ElementSeries.Earth,
                factionId: 8,
                mpsX: 52032,
                mpsY: 101696,
                skills:
                [
                    new CharacterSkillDto(716, 20, 0), // Xuất Ứ Bất Nhiễm -> OnStart 738 (ignorenegativestate_p=100%, 54 frames)
                ]));

        SimulatedPlayerHandle kunlunEnemyHandle = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "CônLônHạBùa",
                ElementSeries.Earth,
                factionId: 9,
                mpsX: 52128,
                mpsY: 101696,
                skills:
                [
                    new CharacterSkillDto(174, 20, 0), // Kỷ Bán Phù: -52% fastwalkrun_p (1620 frames)
                ]));

        PlayerZoneState? wudang = wudangHandle.GetZoneState();
        PlayerZoneState? kunlunEnemy = kunlunEnemyHandle.GetZoneState();
        Assert.NotNull(wudang);
        Assert.NotNull(kunlunEnemy);

        wudang.FightMode = 1;
        kunlunEnemy.FightMode = 1;
        wudang.SpawnProtectUntilTick = 0;
        kunlunEnemy.SpawnProtectUntilTick = 0;

        // Enemy Côn Lôn curses Võ Đang with Kỷ Bán Phù (174; DoOrdinSkill @ 0x0809c160 10-frame windup at CastSpeed=0 + M15 flight), and Võ Đang is also frozen
        kunlunEnemyHandle.CastSkill(174, -1, (int)wudang.RuntimeNpcId);
        await harness.StepFramesAsync(15);
        wudang.StatusEffects.ApplyColdFreeze(120);

        Assert.NotNull(wudang.StatusEffects.FindStateSkillEffect(174));
        Assert.Equal(-52, wudang.SkillCombatModifiers.FastWalkRunPercent);
        Assert.True(wudang.StatusEffects.IsFrozen);

        // Cast Xuất Ứ Bất Nhiễm (716 -> 738; DoOrdinSkill @ 0x0809c160 10-frame windup at CastSpeed=0; 15% missrate -> retry across cooldown if missed).
        // Ground truth: wudang.lua:426-429 & 464-467 + KNpcAttribModify::IgnoreNegativeStateP @ 0x080ad480 (+=):
        // 716 applies +1 ignorenegativestate_p for 18 frames (self+ally) while 738 applies +100 for 54 frames (self);
        // stepping 30 frames (10 windup + 20 active) expires 716's 18-frame +1 while leaving 34 frames on 738's +100 immunity.
        bool cleansed = false;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            wudang.CurrentMana = wudang.EffectiveMaxMana;
            wudangHandle.CastSkill(716, wudang.CurrentMpsX, wudang.CurrentMpsY);
            await harness.StepFramesAsync(30);
            if (wudang.StatusEffects.FindStateSkillEffect(738) is not null)
            {
                cleansed = true;
                break;
            }

            await harness.StepFramesAsync(280);
            wudang.StatusEffects.ApplyColdFreeze(120);
        }

        Assert.True(cleansed, "Expected Xuất Ứ Bất Nhiễm (716 -> 738) to apply state 738 on self.");
        Assert.False(wudang.StatusEffects.IsFrozen);
        Assert.Null(wudang.StatusEffects.FindStateSkillEffect(174));
        Assert.Equal(0, wudang.SkillCombatModifiers.FastWalkRunPercent);
        Assert.Equal(100, ((ICombatEntityState)wudang).IgnoreNegativeStatePercent);

        // While 738 is active (54 frames), re-casting Kỷ Bán Phù (174) is 100% blocked by IgnoreNegativeStatePercent
        kunlunEnemyHandle.CastSkill(174, -1, (int)wudang.RuntimeNpcId);
        await harness.StepFramesAsync(15);
        Assert.Null(wudang.StatusEffects.FindStateSkillEffect(174));
    }

    [Fact]
    public async Task KunlunSpeedTalismansAndAutoRescue_LevelTwentyRuntimeSimulation_ShouldBuffAllySlowEnemyAndTriggerMagicShield()
    {
        // Place stationary arena dummy at 52800 so it does not overlap enemy player at 52160 and intercept single-pierce Missile 57.
        ZoneSimulationHarness harness = CreateCombatArenaHarness(dummyMpsX: 52800, randomSeed: 5);
        SimulatedPlayerHandle kunlunCasterHandle = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "CônLônTiênPhù",
                ElementSeries.Earth,
                factionId: 9,
                mpsX: 52032,
                mpsY: 101696,
                maxLife: 4000,
                skills:
                [
                    new CharacterSkillDto(171, 20, 0), // Thanh Phong Phù: +60% fastwalkrun_p for allies (2160 frames)
                    new CharacterSkillDto(174, 20, 0), // Kỷ Bán Phù: -52% fastwalkrun_p on enemy (1620 frames)
                    new CharacterSkillDto(275, 20, 0), // Sương Ngạo Côn Lôn: +65 attackspeed_v, +51 castspeed_v
                    new CharacterSkillDto(717, 20, 0), // Lưỡng Nghi Chân Khí: autorescueskill -> 721 (100x MaxMana shield) + 722 (+35% speed)
                ]));

        SimulatedPlayerHandle allyHandle = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "ĐồngĐộiCônLôn",
                ElementSeries.Earth,
                factionId: 9,
                mpsX: 52064,
                mpsY: 101696,
                skills: [],
                pkState: PlayerPkMode.Peace));

        SimulatedPlayerHandle enemyHandle = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "ĐịchThủThiênNhẫn",
                ElementSeries.Fire,
                factionId: 7,
                mpsX: 52160,
                mpsY: 101696,
                skills: [new CharacterSkillDto(145, 20, 0)]));

        PlayerZoneState? kunlun = kunlunCasterHandle.GetZoneState();
        PlayerZoneState? ally = allyHandle.GetZoneState();
        PlayerZoneState? enemy = enemyHandle.GetZoneState();
        Assert.NotNull(kunlun);
        Assert.NotNull(ally);
        Assert.NotNull(enemy);

        kunlun.FightMode = 1;
        ally.FightMode = 1;
        enemy.FightMode = 1;
        kunlun.SpawnProtectUntilTick = 0;
        ally.SpawnProtectUntilTick = 0;
        enemy.SpawnProtectUntilTick = 0;

        // Passive Sương Ngạo Côn Lôn (275) at lv20: +65 AttackSpeed, +51 CastSpeed
        Assert.Equal(65, ((ICombatEntityState)kunlun).AttackSpeedBonus);
        Assert.Equal(51, ((ICombatEntityState)kunlun).CastSpeedBonus);

        // 1. Cast Thanh Phong Phù (171) on allied player -> ally EffectiveRunSpeed increases from 10 to 16 (+60%)
        Assert.Equal(10, ally.EffectiveRunSpeed);
        kunlunCasterHandle.CastSkill(171, ally.CurrentMpsX, ally.CurrentMpsY);
        await harness.StepFramesAsync(10);
        Assert.Equal(60, ally.SkillCombatModifiers.FastWalkRunPercent);
        Assert.Equal(16, ally.EffectiveRunSpeed);

        // 2. Cast Kỷ Bán Phù (174) on enemy player -> enemy EffectiveRunSpeed slows from 10 to 4 (-52%)
        Assert.Equal(10, enemy.EffectiveRunSpeed);
        kunlunCasterHandle.CastSkill(174, -1, (int)enemy.RuntimeNpcId);
        await harness.StepFramesAsync(10);
        Assert.Equal(-52, enemy.SkillCombatModifiers.FastWalkRunPercent);
        Assert.Equal(4, enemy.EffectiveRunSpeed);

        // 3. Trigger Lưỡng Nghi Chân Khí (717 lv20, 65% proc rate when crossing < 25% MaxLife = 1000 HP).
        // Ground truth: KNpc::CalcDamage @ 0x0809e790 applies 20% PK damage scaling ((225 fire + 200 energy) * 20 / 100 = 85 HP damage),
        // and DoOrdinSkill @ 0x0809c160 requires 10 windup frames + M57 flight (~8 frames). Starting at 1050 HP crosses 1050 -> 965 (< 1000).
        bool rescueTriggered = false;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            kunlun.CurrentLife = 1050;
            kunlun.FireResistPercent = 0;
            kunlun.BaseFireResist = 0;
            enemyHandle.CastSkill(145, -1, (int)kunlun.RuntimeNpcId);
            await harness.StepFramesAsync(22);
            if (((ICombatEntityState)kunlun).StaticMagicShield > 0)
            {
                rescueTriggered = true;
                break;
            }

            await harness.StepFramesAsync(370);
        }

        Assert.True(rescueTriggered, "Expected Lưỡng Nghi Chân Khí (717) auto-rescue to proc 721 + 722 below 25% HP.");
        // Ground truth: KNpcAttribModify::StaticMagicShieldP @ 0x080b1000 & kunlun.lua:435-436,464:
        // staticmagicshield_p[20] = 10000 -> shield = (EffectiveMaxMana * 10000) / 100 (100x MaxMana = 400,000 at MaxMana = 4000).
        Assert.Equal((kunlun.EffectiveMaxMana * 10_000) / 100, ((ICombatEntityState)kunlun).StaticMagicShield);
        Assert.NotNull(kunlun.StatusEffects.FindStateSkillEffect(721));
        Assert.NotNull(kunlun.StatusEffects.FindStateSkillEffect(722));
    }

    [Fact]
    public async Task NineXSkillSynergy_AllEightFireAndEarthMasteryTreesAtLevelTwenty_ShouldMatchExactLuaSynergyPercentages()
    {
        SharedGameAssetsFixture fixture = SharedGameAssetsFixture.Default;
        ISkillTemplateCatalog skillCatalog = fixture.SkillTemplateCatalog;
        ZoneSimulationHarness harness = CreateCombatArenaHarness();

        (string Name, ElementSeries Series, byte FactionId, int[] PrerequisiteSkills, (int TargetSkillId, int ExpectedSynergyPercent)[] ExpectedTotals)[] cases =
        [
            ("GaibangPalm", ElementSeries.Fire, 6, [122, 128], [(357, 105)]),
            ("GaibangStaff", ElementSeries.Fire, 6, [119, 125], [(359, 100)]),
            ("TianrenSpear", ElementSeries.Fire, 7, [135, 141, 142], [(361, 165)]),
            ("TianrenFire", ElementSeries.Fire, 7, [145, 138, 148], [(362, 215), (363, 87)]),
            ("WudangQi", ElementSeries.Earth, 8, [153, 164, 165], [(365, 360)]),
            ("WudangSword", ElementSeries.Earth, 8, [155, 158, 267], [(368, 185), (162, 268)]),
            ("KunlunBlade", ElementSeries.Earth, 9, [169, 176], [(372, 100), (373, 126)]),
            ("KunlunLightning", ElementSeries.Earth, 9, [179, 172, 182], [(375, 195)]),
        ];

        Assert.NotEmpty(cases);
        foreach ((string name, ElementSeries series, byte factionId, int[] prereqs, (int TargetSkillId, int ExpectedSynergyPercent)[] expectedTotals) in cases)
        {
            CharacterSkillDto[] dtoSkills = prereqs.Select(id => new CharacterSkillDto((ushort)id, 20, 0)).ToArray();
            SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
                CreateSectCharacterDto($"Synergy_{name}", series, factionId, dtoSkills));
            PlayerZoneState? player = handle.GetZoneState();
            Assert.NotNull(player);

            Assert.NotEmpty(expectedTotals);
            foreach ((int targetSkillId, int expectedPct) in expectedTotals)
            {
                int actualPct = CombatDamageCalculator.ComputeSynergyAddDamagePercent(player, targetSkillId, skillCatalog);
                Assert.Equal(expectedPct, actualPct);
            }
        }

        // Verify runtime payload scaling on Võ Đang Thiên Địa Vô Cực (365) at lv20:
        // Base lightingdamage_v at lv20 is [8..350]; with +360% synergy (Nộ Lôi Chỉ +110%, Bác Cập +120%, Vô Ngã +130%) -> * 4.6.
        // Ground truth: KPlayer::GetEngergyDamage @ 0x080bf2a0 & KNpc::CalcLightAttribDamage (combat.damage.light_attrib_damage.json:56)
        // add m_nCurEngergy onto non-physical lightingdamage_v, so pass energy: 0 to isolate pure skill + synergy scaling.
        Assert.True(skillCatalog.TryGetSkill(365, out SkillTemplate? thienDiaVoCuc) && thienDiaVoCuc is not null);
        SimulatedPlayerHandle wudangBare = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto("WudangNoSynergy", ElementSeries.Earth, 8, [new CharacterSkillDto(365, 20, 0)]) with { Energy = 0 });
        SimulatedPlayerHandle wudangFull = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "WudangFullSynergy",
                ElementSeries.Earth,
                8,
                [
                    new CharacterSkillDto(153, 20, 0),
                    new CharacterSkillDto(164, 20, 0),
                    new CharacterSkillDto(165, 20, 0),
                    new CharacterSkillDto(365, 20, 0),
                ]) with { Energy = 0 });

        PlayerZoneState? pBare = wudangBare.GetZoneState();
        PlayerZoneState? pFull = wudangFull.GetZoneState();
        Assert.NotNull(pBare);
        Assert.NotNull(pFull);

        SkillDamagePayload barePayload = CombatDamageCalculator.BuildPlayerSkillDamagePayload(pBare, thienDiaVoCuc, 20, skillCatalog);
        SkillDamagePayload fullPayload = CombatDamageCalculator.BuildPlayerSkillDamagePayload(pFull, thienDiaVoCuc, 20, skillCatalog);

        Assert.Equal(8, barePayload.LightMinDamage);
        Assert.Equal(350, barePayload.LightMaxDamage);
        Assert.Equal(8 + ((8 * 360) / 100), fullPayload.LightMinDamage);
        Assert.Equal(350 + ((350 * 360) / 100), fullPayload.LightMaxDamage);
    }

    [Fact]
    public async Task NineXSkillProficiency_LandingCombatHitsWithWudang365AndGaibang357_ShouldAccumulateSkillExpAndSyncBitmask()
    {
        SharedGameAssetsFixture fixture = SharedGameAssetsFixture.Default;
        Assert.True(fixture.SkillTemplateCatalog.TryGetSkill(365, out SkillTemplate? skill365) && skill365 is not null);
        Assert.True(fixture.SkillTemplateCatalog.TryGetSkill(357, out SkillTemplate? skill357) && skill357 is not null);

        // Ground-Truth skill_skillexp_v[1]: Thiên Địa Vô Cực (365) = 7350, Phi Long Tại Thiên (357) = 2150
        Assert.Equal(7350, RequireEffectAttribute(skill365, 8).Param0!.Evaluate(1));
        Assert.Equal(2150, RequireEffectAttribute(skill357, 8).Param0!.Evaluate(1));

        ZoneSimulationHarness harness = CreateCombatArenaHarness(dummyMpsX: 52192, dummyMpsY: 101696, randomSeed: 42);
        MapZoneWorker worker = harness.GetZone(53);
        NpcZoneState dummy = Assert.Single(worker.StaticNpcs.Values);

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "LuyệnSkill9x",
                ElementSeries.Fire,
                factionId: 6,
                level: 90,
                skills:
                [
                    new CharacterSkillDto(357, 1, 0, ExpPercent: 0, SkillExp: 0),
                ]));

        PlayerZoneState? player = handle.GetZoneState();
        Assert.NotNull(player);
        player.FightMode = 1;
        handle.Recorder.Clear();

        for (int castIdx = 0; castIdx < 45; castIdx++)
        {
            dummy.CurLife = 5_000_000u;
            player.CurrentMana = player.EffectiveMaxMana;
            handle.CastSkill(357, -1, (int)dummy.NpcId);
            await harness.StepFramesAsync(22);
        }

        Assert.True(player.TryGetLearnedSkillSlot(357, out LearnedSkillSlot slot357));
        Assert.InRange(slot357.SkillExp, 18u, 60u);

        ushort expectedPercent = (ushort)((slot357.SkillExp << 10) / 2150u);
        Assert.True(expectedPercent >= 8, $"Expected >= 8/1024 threshold for 0x5E sync, got {expectedPercent} at SkillExp={slot357.SkillExp}.");
        Assert.NotEmpty(handle.Recorder.PlayerSkillLevelSyncs);

        var lastSync = handle.Recorder.PlayerSkillLevelSyncs[^1];
        Assert.Equal(357, lastSync.SkillId);
        Assert.Equal(1, lastSync.SkillLevel);
        Assert.Equal(expectedPercent & 0xFFF8, lastSync.SkillExpPercent & 0xFFF8);
    }

    [Fact]
    public async Task GaibangPhiLongTaiThien_LevelTwentyMissileCollision_ShouldTriggerLongChienUDaSubSkill389()
    {
        SharedGameAssetsFixture fixture = SharedGameAssetsFixture.Default;
        Assert.True(fixture.SkillTemplateCatalog.TryGetSkill(357, out SkillTemplate? phiLong) && phiLong is not null);

        // gaibang.lua: skill 357 defines dynamic attribute 269 (skill_collideevent) -> p0=0 (lv1..10), p0=1 & p2=389 (Long Chiến Ư Dã) at lv11..30
        SkillEffectScalingTemplate collideAttr = RequireEffectAttribute(phiLong, 269);
        Assert.Equal(0, collideAttr.Param0!.Evaluate(10));
        Assert.Equal(1, collideAttr.Param0!.Evaluate(20));
        Assert.Equal(389, collideAttr.Param2!.Evaluate(20));

        ZoneSimulationHarness harness = CreateCombatArenaHarness(dummyMpsX: 52256, dummyMpsY: 101696, randomSeed: 42);
        MapZoneWorker worker = harness.GetZone(53);
        NpcZoneState dummy = Assert.Single(worker.StaticNpcs.Values);

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "CáiBangPhiLong20",
                ElementSeries.Fire,
                factionId: 6,
                skills: [new CharacterSkillDto(357, 20, 0)]));

        PlayerZoneState? caster = handle.GetZoneState();
        Assert.NotNull(caster);
        caster.FightMode = 1;

        HashSet<int> observedMissileSkills = CaptureObservedActiveMissileSkillsOverFrames(
            harness,
            worker,
            frames: 32,
            out Func<Task> stepAndRecordAsync);

        handle.CastSkill(357, -1, (int)dummy.NpcId);
        await stepAndRecordAsync();

        Assert.Contains(357, observedMissileSkills);
        // Legacy KSkill::AddMagicAttribute_MissleAndSetting @ 0x081023e0 enables m_bCollideEvent=1, m_nCollideSkillId=389 at level >= 11.
        Assert.Contains(389, observedMissileSkills);
    }

    [Fact]
    public async Task KunlunNgaoTuyetTieuPhong_LevelTwentyBladeCollision_ShouldSpawnKhieuPhongSecondaryTornado373()
    {
        SharedGameAssetsFixture fixture = SharedGameAssetsFixture.Default;
        Assert.True(fixture.SkillTemplateCatalog.TryGetSkill(372, out SkillTemplate? ngaoTuyet) && ngaoTuyet is not null);

        // kunlun.lua: skill 372 defines dynamic attribute 269 (skill_collideevent) -> p0=0 (lv1..10), p0=1 & p2=373 (Khiếu Phong Tam Liên Kích) at lv11..30
        SkillEffectScalingTemplate collideAttr = RequireEffectAttribute(ngaoTuyet, 269);
        Assert.Equal(0, collideAttr.Param0!.Evaluate(10));
        Assert.Equal(1, collideAttr.Param0!.Evaluate(20));
        Assert.Equal(373, collideAttr.Param2!.Evaluate(20));

        ZoneSimulationHarness harness = CreateCombatArenaHarness(dummyMpsX: 52224, dummyMpsY: 101696, randomSeed: 42);
        MapZoneWorker worker = harness.GetZone(53);
        NpcZoneState dummy = Assert.Single(worker.StaticNpcs.Values);

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "CônLônĐao9x",
                ElementSeries.Earth,
                factionId: 9,
                skills: [new CharacterSkillDto(372, 20, 0)]));

        PlayerZoneState? caster = handle.GetZoneState();
        Assert.NotNull(caster);
        caster.FightMode = 1;
        caster.WeaponType = 0;
        caster.WeaponParticular = 1; // Blade (EquipLimit = 1)

        HashSet<int> observedMissileSkills = CaptureObservedActiveMissileSkillsOverFrames(
            harness,
            worker,
            frames: 28,
            out Func<Task> stepAndRecordAsync);

        handle.CastSkill(372, -1, (int)dummy.NpcId);
        await stepAndRecordAsync();

        Assert.Contains(372, observedMissileSkills);
        // At level 20, Ngạo Tuyết Tiêu Phong (372) must spawn secondary tornado Khiếu Phong Tam Liên Kích (373) on collision.
        Assert.Contains(373, observedMissileSkills);
    }

    [Fact]
    public async Task WudangNhanKiemHopNhat_LevelTwentyMeleeRayCollision_ShouldTriggerHuyenNhatVoTuong162InsteadOfKunlun373()
    {
        SharedGameAssetsFixture fixture = SharedGameAssetsFixture.Default;
        Assert.True(fixture.SkillTemplateCatalog.TryGetSkill(368, out SkillTemplate? nhanKiem) && nhanKiem is not null);

        // wudang.lua: skill 368 overrides static skills.txt Col=373 via attribute 269 (skill_collideevent):
        // lv1..15 -> p0=0 (disabled), lv16..30 -> p0=1 & p2=162 (Huyền Nhất Vô Tượng, Wudang sword burst)
        SkillEffectScalingTemplate collideAttr = RequireEffectAttribute(nhanKiem, 269);
        Assert.Equal(0, collideAttr.Param0!.Evaluate(15));
        Assert.Equal(1, collideAttr.Param0!.Evaluate(20));
        Assert.Equal(162, collideAttr.Param2!.Evaluate(20));

        ZoneSimulationHarness harness = CreateCombatArenaHarness(dummyMpsX: 52096, dummyMpsY: 101696, randomSeed: 42);
        MapZoneWorker worker = harness.GetZone(53);
        NpcZoneState dummy = Assert.Single(worker.StaticNpcs.Values);

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "VõĐangKiếm9x",
                ElementSeries.Earth,
                factionId: 8,
                mpsX: 52032,
                mpsY: 101696,
                skills: [new CharacterSkillDto(368, 20, 0)]));

        PlayerZoneState? caster = handle.GetZoneState();
        Assert.NotNull(caster);
        caster.FightMode = 1;
        caster.WeaponType = 0;
        caster.WeaponParticular = 0; // Sword (EquipLimit = 0)
        caster.BonusAttackRating = 50_000;

        HashSet<int> observedMissileSkills = CaptureObservedActiveMissileSkillsOverFrames(
            harness,
            worker,
            frames: 20,
            out Func<Task> stepAndRecordAsync);

        handle.CastSkill(368, -1, (int)dummy.NpcId);
        await stepAndRecordAsync();

        Assert.Contains(368, observedMissileSkills);
        Assert.Contains(162, observedMissileSkills);
        Assert.DoesNotContain(373, observedMissileSkills);
    }

    [Fact]
    public async Task TianrenMaAmPhePhach_EnemyAfflictedWithRandMoveCurse723_ShouldWanderRandomlyAndRejectOffensiveSkillCast()
    {
        ZoneSimulationHarness harness = CreateCombatArenaHarness(randomSeed: 9);
        SimulatedPlayerHandle tianrenHandle = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "ThiênNhẫnMaÂm",
                ElementSeries.Fire,
                factionId: 7,
                mpsX: 52032,
                mpsY: 101696,
                skills: [new CharacterSkillDto(715, 20, 0)]));

        SimulatedPlayerHandle victimHandle = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "NạnNhânHoảngLoạn",
                ElementSeries.Earth,
                factionId: 8,
                mpsX: 52096,
                mpsY: 101696,
                skills: [new CharacterSkillDto(153, 20, 0)]));

        PlayerZoneState? tianren = tianrenHandle.GetZoneState();
        PlayerZoneState? victim = victimHandle.GetZoneState();
        Assert.NotNull(tianren);
        Assert.NotNull(victim);

        tianren.FightMode = 1;
        victim.FightMode = 1;
        tianren.SpawnProtectUntilTick = 0;
        victim.SpawnProtectUntilTick = 0;

        // Cast Ma Âm Phệ Phách (715 -> OnStart 723, DoOrdinSkill @ 0x0809c160 10-frame windup at CastSpeed=0, M273 AoE radius 224 mps, randmove p0=1 for 72 frames at lv20)
        bool panicApplied = false;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            tianren.CurrentMana = tianren.EffectiveMaxMana;
            tianrenHandle.CastSkill(715, tianren.CurrentMpsX, tianren.CurrentMpsY);
            await harness.StepFramesAsync(12);
            if (victim.SkillCombatModifiers.RandMoveCount > 0)
            {
                panicApplied = true;
                break;
            }

            await harness.StepFramesAsync(370);
        }

        Assert.True(panicApplied, "Expected Ma Âm Phệ Phách (715 -> 723) to set RandMoveCount > 0 on nearby enemy.");
        int initialVictimX = victim.CurrentMpsX;
        int initialVictimY = victim.CurrentMpsY;
        victimHandle.Recorder.SkillCastBroadcasts.Clear();

        // Legacy KNpc::OnRandMove (@ 0x08099f00) + KSkill::CanCastSkill (@ 0x08101010):
        // While RandMoveCount > 0 (72 frames), the victim cannot cast skills and involuntarily wanders to random nearby coordinates.
        victimHandle.CastSkill(153, -1, (int)tianren.RuntimeNpcId);
        await harness.StepFramesAsync(24);

        Assert.Empty(victimHandle.Recorder.SkillCastBroadcasts);
        bool wanderedInvoluntarily = victim.CurrentMpsX != initialVictimX || victim.CurrentMpsY != initialVictimY;
        Assert.True(wanderedInvoluntarily, "Expected player afflicted with Ma Âm Phệ Phách (randmove) to wander involuntarily.");
    }

    [Fact]
    public async Task WeaponRestrictedSectSkills_WhenCasterIsUnarmed_ShouldRejectCastOfStaff359AndSpear361()
    {
        SharedGameAssetsFixture fixture = SharedGameAssetsFixture.Default;
        Assert.True(fixture.SkillTemplateCatalog.TryGetSkill(359, out SkillTemplate? thienHaVoCau) && thienHaVoCau is not null);
        Assert.True(fixture.SkillTemplateCatalog.TryGetSkill(361, out SkillTemplate? vanLongKich) && vanLongKich is not null);

        // skills.txt Ground Truth: Thiên Hạ Vô Cẩu (359) requires EquipLimit=2 (Cudgel/Bổng); Vân Long Kích (361) requires EquipLimit=3 (Spear/Thương)
        Assert.Equal(2, thienHaVoCau.EquipLimit);
        Assert.Equal(3, vanLongKich.EquipLimit);

        ZoneSimulationHarness harness = CreateCombatArenaHarness(dummyMpsX: 52080, dummyMpsY: 101696, randomSeed: 42);
        MapZoneWorker worker = harness.GetZone(53);
        NpcZoneState dummy = Assert.Single(worker.StaticNpcs.Values);

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "TayKhôngĐánhBổngThương",
                ElementSeries.Fire,
                factionId: 6,
                mpsX: 52032,
                mpsY: 101696,
                skills:
                [
                    new CharacterSkillDto(359, 20, 0),
                    new CharacterSkillDto(361, 20, 0),
                ]));

        PlayerZoneState? caster = handle.GetZoneState();
        Assert.NotNull(caster);
        caster.FightMode = 1;
        Assert.Equal(-1, caster.WeaponType); // Unarmed
        handle.Recorder.SkillCastBroadcasts.Clear();

        // Legacy KSkill::CheckLimited (@ 0x08101010) rejects weapon-limited skills when unarmed or holding wrong WeaponParticular
        handle.CastSkill(359, dummy.MpsX, dummy.MpsY);
        handle.CastSkill(361, -1, (int)dummy.NpcId);
        await harness.StepFramesAsync(10);

        Assert.Empty(handle.Recorder.SkillCastBroadcasts);
        Assert.Equal(0, worker.ActiveMissileCount);
    }

    [Fact]
    public async Task QinggongJump_ExcessiveDistanceIntermediateWallAndLowObstacleLanding_ShouldClampRangeAndRejectBlockedTrajectory()
    {
        SharedGameAssetsFixture fixture = SharedGameAssetsFixture.Default;
        IMapCatalog mapCatalog = fixture.MapCatalog;

        // Verify real Ground-Truth obstacle cells on Map 53 (Ba Lăng huyện, 053_ba-lang-huyen.map.json):
        // 1) Long passable span: (42000, 93200) [obs=0] -> (45008, 93200) [obs=0], 3008 mps apart (> legacy max jump 480 mps)
        Assert.Equal(0, mapCatalog.GetCellObstacle(53, 42000, 93200));
        Assert.Equal(0, mapCatalog.GetCellObstacle(53, 45008, 93200));

        // 2) Intermediate high wall (obs=1): (42256, 93200) [obs=0] -> (42256, 93616) [obs=0] with obs=1 wall at (42256, 93520..93584)
        Assert.Equal(0, mapCatalog.GetCellObstacle(53, 42256, 93200));
        Assert.Equal(1, mapCatalog.GetCellObstacle(53, 42256, 93520));
        Assert.Equal(0, mapCatalog.GetCellObstacle(53, 42256, 93616));

        // 3) Low jumpable obstacle landing cell (obs=4): (49136, 99408) [obs=0] -> (49136, 99856) [obs=4]
        Assert.Equal(0, mapCatalog.GetCellObstacle(53, 49136, 99408));
        Assert.Equal(4, mapCatalog.GetCellObstacle(53, 49136, 99856));

        ZoneSimulationHarness harness = CreateCombatArenaHarness(useRealMapObstacles: true, randomSeed: 42);

        // Case A: 3008-mps cross-map jump must be clamped to legacy JumpFrame * JumpSpeed (<= 480 mps)
        SimulatedPlayerHandle longJumperHandle = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "KhinhCôngXa",
                ElementSeries.Fire,
                factionId: 6,
                mpsX: 42000,
                mpsY: 93200,
                skills: [new CharacterSkillDto(210, 1, 0)]));
        PlayerZoneState? longJumper = longJumperHandle.GetZoneState();
        Assert.NotNull(longJumper);

        longJumperHandle.Jump(45008, 93200);
        await harness.StepFramesAsync(24);
        int jumpedDistance = Math.Abs(longJumper.CurrentMpsX - 42000);
        Assert.InRange(jumpedDistance, 0, 480);

        // Case B: Jumping across an intermediate obs=1 high wall must stop before the wall (CurrentMpsY < 93520)
        SimulatedPlayerHandle wallJumperHandle = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "KhinhCôngXuyênTường",
                ElementSeries.Fire,
                factionId: 6,
                mpsX: 42256,
                mpsY: 93200,
                skills: [new CharacterSkillDto(210, 1, 0)]));
        PlayerZoneState? wallJumper = wallJumperHandle.GetZoneState();
        Assert.NotNull(wallJumper);

        wallJumperHandle.Jump(42256, 93616);
        await harness.StepFramesAsync(24);
        Assert.True(wallJumper.CurrentMpsY < 93520, $"Expected intermediate obs=1 wall at Y=93520 to block jump, landed at Y={wallJumper.CurrentMpsY}.");

        // Case C: Landing directly inside an obs=4 cell must be rejected or shortened to an obs=0 cell (KNpc::NewJump @ 0x08099fd0)
        SimulatedPlayerHandle lowObsJumperHandle = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "KhinhCôngĐápRào",
                ElementSeries.Earth,
                factionId: 8,
                mpsX: 49136,
                mpsY: 99408,
                skills: [new CharacterSkillDto(210, 1, 0)]));
        PlayerZoneState? lowObsJumper = lowObsJumperHandle.GetZoneState();
        Assert.NotNull(lowObsJumper);

        lowObsJumperHandle.Jump(49136, 99856);
        await harness.StepFramesAsync(24);
        Assert.Equal(0, mapCatalog.GetCellObstacle(53, lowObsJumper.CurrentMpsX, lowObsJumper.CurrentMpsY));
    }

    [Fact]
    public async Task CurseNineXAllocation_Tianren391AndKunlun394WithoutPrerequisiteCurseLevels_ShouldRejectSkillPointAllocation()
    {
        SharedGameAssetsFixture fixture = SharedGameAssetsFixture.Default;
        ISkillTemplateCatalog skillCatalog = fixture.SkillTemplateCatalog;
        var progressionManager = new FactionProgressionManager();

        ZoneSimulationHarness harness = CreateCombatArenaHarness();

        // 1. Thiên Nhẫn Nhiếp Hồn Loạn Tâm (391): lvlup_shehun_luanxin.lua -> SOSkillLevelUp requires {136, 137, 140, 364, 143} >= 5 for level 1
        SimulatedPlayerHandle tianrenHandle = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "ThiênNhẫnBùa9x",
                ElementSeries.Fire,
                factionId: 7,
                level: 80,
                unallocatedSkillPoints: 10,
                skills:
                [
                    new CharacterSkillDto(136, 0, 0),
                    new CharacterSkillDto(137, 0, 0),
                    new CharacterSkillDto(140, 0, 0),
                    new CharacterSkillDto(364, 0, 0),
                    new CharacterSkillDto(143, 0, 0),
                    new CharacterSkillDto(391, 0, 0),
                ]));

        // 2. Côn Lôn Túy Tiên Tá Cốt (394): lvlup_zuixian_cuogu.lua -> SOSkillLevelUp requires {392, 174, 393, 175, 90} >= 5 for level 1
        SimulatedPlayerHandle kunlunHandle = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "CônLônBùa9x",
                ElementSeries.Earth,
                factionId: 9,
                level: 80,
                unallocatedSkillPoints: 10,
                skills:
                [
                    new CharacterSkillDto(392, 0, 0),
                    new CharacterSkillDto(174, 0, 0),
                    new CharacterSkillDto(393, 0, 0),
                    new CharacterSkillDto(175, 0, 0),
                    new CharacterSkillDto(90, 0, 0),
                    new CharacterSkillDto(394, 0, 0),
                ]));

        PlayerZoneState? tianren = tianrenHandle.GetZoneState();
        PlayerZoneState? kunlun = kunlunHandle.GetZoneState();
        Assert.NotNull(tianren);
        Assert.NotNull(kunlun);

        FactionProgressionResult res391 = progressionManager.TryAllocateSkillPoint(tianren, 391, skillCatalog);
        FactionProgressionResult res394 = progressionManager.TryAllocateSkillPoint(kunlun, 394, skillCatalog);

        Assert.NotEqual(FactionProgressionResult.Success, res391);
        Assert.NotEqual(FactionProgressionResult.Success, res394);
        Assert.True(tianren.TryGetLearnedSkillLevel(391, out ushort lvl391) && lvl391 == 0);
        Assert.True(kunlun.TryGetLearnedSkillLevel(394, out ushort lvl394) && lvl394 == 0);
    }
}
