using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JXReborn.Contracts.Characters;
using JXReborn.GameAssets.NpcTemplates;
using JXReborn.GameAssets.Skills;
using JXReborn.GameAssets.WorldMaps;
using JXReborn.Primitives.Characters;
using JXReborn.Primitives.Combat;
using JXReborn.Primitives.WorldZones;
using JXReborn.TestBase.Fixtures;
using JXReborn.TestBase.SimulationHarnesses;
using JXReborn.ZoneEngine.Combat;
using JXReborn.ZoneEngine.NpcEntities;
using JXReborn.ZoneEngine.PlayerSessions;
using JXReborn.ZoneEngine.WorldZones;
using Xunit;

namespace JXReborn.ZoneEngine.Tests.Skills;

/// <summary>
/// Ground-truth audit tests for the 6 classic Metal, Wood, and Water sects
/// (Shaolin 0, Tianwang 1, Tangmen 2, Wudu 3, Emei 4, Cuiyan 5), anchored directly to
/// <c>legacy-server/bin/server1/settings/skills.txt</c>, <c>missles.txt</c>, <c>attribconstdata.ini</c>,
/// <c>script/skill/{shaolin,tianwang,tangmen,wudu,emei,cuiyan}.lua</c>, and <c>legacy-server/discovery/</c>.
/// </summary>
public sealed class MetalWoodWaterSectSkillsAuditTests
{
    private static ZoneSimulationHarness CreateSixSectHarness(params StaticNpcSpawnTemplate[] npcSpawns)
    {
        SharedGameAssetsFixture assets = SharedGameAssetsFixture.Default;
        var mapDesc = new MapDescriptor(
            MapId: 53,
            MapNameUnicode: "Ba Lăng Huyện",
            RawSubPath: "balanghuyen",
            RegionBeginX: 0,
            RegionBeginY: 0,
            RegionWidth: 200,
            RegionHeight: 200)
        {
            RegionNpcSpawns = npcSpawns,
        };

        var options = new ZoneSimulationHarnessOptions
        {
            RandomSeed = 42,
            TicksPerSecond = 18,
            Assets = new ZoneAssetServices
            {
                SkillTemplateCatalog = assets.SkillTemplateCatalog,
                MissileTemplateCatalog = assets.MissileTemplateCatalog,
                PlayerTemplateCatalog = assets.PlayerTemplateCatalog,
                ItemTemplateCatalog = assets.ItemTemplateCatalog,
                NpcTemplateCatalog = assets.NpcTemplateCatalog,
            },
        };
        options.InitialMaps.Add(mapDesc);
        return new ZoneSimulationHarness(options);
    }

    private static CharacterDto CreateSectCharacterDto(
        string roleName,
        CharacterGender gender,
        ElementSeries series,
        byte factionId,
        int mpsX,
        int mpsY,
        IReadOnlyList<CharacterSkillDto> skills,
        byte level = 120,
        ushort maxLife = 4000,
        ushort maxMana = 2000,
        ushort maxStamina = 500,
        ushort strength = 260,
        ushort dexterity = 200,
        ushort vitality = 200,
        ushort energy = 260)
    {
        return new CharacterDto(
            Id: Guid.NewGuid(),
            AccountId: Guid.NewGuid(),
            SlotIndex: 0,
            RoleNameUnicode: roleName,
            Gender: gender,
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
            CurrentStamina: maxStamina,
            MaxStamina: maxStamina,
            Strength: strength,
            Dexterity: dexterity,
            Vitality: vitality,
            Energy: energy,
            Lucky: 0,
            UnallocatedAttributePoints: 0,
            UnallocatedSkillPoints: 0,
            InventoryMoney: 10000,
            RepositoryMoney: 0,
            CurrentFactionId: factionId,
            FirstFactionId: factionId,
            FactionJoinCount: 1,
            Skills: skills);
    }

    private static SkillEffectScalingTemplate RequireEffect(SkillTemplate skill, string attributeCode)
    {
        Assert.NotEmpty(skill.EffectScaling);
        foreach (SkillEffectScalingTemplate eff in skill.EffectScaling)
        {
            if (string.Equals(eff.AttributeCode, attributeCode, StringComparison.OrdinalIgnoreCase))
            {
                return eff;
            }
        }

        throw new InvalidOperationException($"Skill {skill.Id} ({skill.Name}) does not contain effect '{attributeCode}'.");
    }

    [Fact]
    public async Task ShaolinSkillsAndLaHanTranAura_AtLevel1And20_ShouldMatchLegacyReflectStunAndReturnSkillCurves()
    {
        // Ground truth:
        //   - legacy-server/bin/server1/settings/skills.txt & script/skill/shaolin.lua:
        //     * 14 Hang Long Bat Vu (Fist 1x), 271 Long Trao Ho Trao (Fist 3x, 2 hits), 318 Dat Ma Do Giang (Fist 90, meleeMany 2x317)
        //     * 11 Hoanh Tao Luc Hop (Staff 3x), 319 Hoanh Tao Thien Quan (Staff 90, OnCollide=382)
        //     * 19 Ma Kha Vo Luong (Blade 4x, order:9), 321 Vo Tuong Tram (Blade 90, order:9)
        //     * 16 La Han Tran (Aura -> Child 202): meleedamagereturn_p 1..45%, rangedamagereturn_p 1..45%, adddefense_v 40..800
        //     * 20 Su Tu Hong: atFirer + OnStart=22, MinFramesPerCast=54, stun_p 15..65% (duration 5..27 frames)
        //     * 21 Dich Can Kinh: passive allres_p
        //     * 709 Dai Thua Nhu Lai Chu (120): passive poisondamagereturn_p & returnskill_p
        ISkillTemplateCatalog skillCatalog = SharedGameAssetsFixture.Default.SkillTemplateCatalog;
        Assert.NotEmpty(skillCatalog.AllSkills);

        int[] shaolinCoreIds = [4, 6, 8, 10, 11, 14, 15, 16, 19, 20, 21, 271, 273, 318, 319, 321, 709];
        Assert.NotEmpty(shaolinCoreIds);
        foreach (int id in shaolinCoreIds)
        {
            Assert.True(skillCatalog.TryGetSkill(id, out SkillTemplate? found) && found is not null, $"Missing Shaolin skill {id}.");
        }

        Assert.True(skillCatalog.TryGetSkill(318, out SkillTemplate? datMa) && datMa is not null);
        Assert.Equal(SkillStyleKind.Melee, datMa.Style);
        Assert.Equal("meleeMany", datMa.MissileForm);
        Assert.Equal(317, datMa.ChildSkillId);
        Assert.Equal(2, datMa.ChildSkillCount);
        Assert.Equal(-1, datMa.EquipLimit);

        Assert.True(skillCatalog.TryGetSkill(319, out SkillTemplate? hoanhTao90) && hoanhTao90 is not null);
        Assert.Equal("atFirer", hoanhTao90.MissileForm);
        Assert.Equal(2, hoanhTao90.EquipLimit);
        Assert.NotNull(hoanhTao90.EventSkills);
        Assert.Equal(382, hoanhTao90.EventSkills.OnCollideSkillId);

        Assert.True(skillCatalog.TryGetSkill(321, out SkillTemplate? voTuongTram) && voTuongTram is not null);
        Assert.Equal("line", voTuongTram.MissileForm);
        Assert.Equal("order", voTuongTram.MissileGenerateStyle);
        Assert.Equal(9, voTuongTram.MissileGenerateDelayFrames);
        Assert.Equal(1, voTuongTram.EquipLimit);

        Assert.True(skillCatalog.TryGetSkill(20, out SkillTemplate? suTuHong) && suTuHong is not null);
        Assert.Equal(54, suTuHong.MinFramesPerCast);
        Assert.NotNull(suTuHong.EventSkills);
        Assert.Equal(22, suTuHong.EventSkills.OnStartSkillId);
        SkillEffectScalingTemplate suTuStun = RequireEffect(suTuHong, "stun_p");
        Assert.Equal(15, suTuStun.Param0!.Evaluate(1));
        Assert.Equal(65, suTuStun.Param0.Evaluate(20));
        Assert.Equal(5, suTuStun.Param1!.Evaluate(1));
        Assert.Equal(27, suTuStun.Param1.Evaluate(20));

        Assert.True(skillCatalog.TryGetSkill(709, out SkillTemplate? shaolin120) && shaolin120 is not null);
        Assert.Equal(SkillStyleKind.Passive, shaolin120.Style);
        SkillEffectScalingTemplate returnSkillEff = RequireEffect(shaolin120, "returnskill_p");
        SkillEffectScalingTemplate poisonReturnEff = RequireEffect(shaolin120, "poisondamagereturn_p");
        Assert.True(returnSkillEff.Param0!.Evaluate(20) > returnSkillEff.Param0.Evaluate(1));
        Assert.True(poisonReturnEff.Param0!.Evaluate(20) > poisonReturnEff.Param0.Evaluate(1));

        // Runtime 18-Hz verification: La Han Tran (16 -> Child 202) pulses reflect & defense to caster and allied monk within 180 mps
        ZoneSimulationHarness harness = CreateSixSectHarness();
        SimulatedPlayerHandle monkCaptain = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "Huyền Bi",
                CharacterGender.Male,
                ElementSeries.Metal,
                factionId: 0,
                mpsX: 51900,
                mpsY: 101696,
                skills: [new CharacterSkillDto(16, 20, 0)]));
        SimulatedPlayerHandle monkAlly = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "Huyền Từ",
                CharacterGender.Male,
                ElementSeries.Metal,
                factionId: 0,
                mpsX: 52000,
                mpsY: 101696,
                skills: [new CharacterSkillDto(14, 20, 0)]));

        PlayerZoneState? captainState = monkCaptain.GetZoneState();
        PlayerZoneState? allyState = monkAlly.GetZoneState();
        Assert.NotNull(captainState);
        Assert.NotNull(allyState);

        captainState.Camp = EntityCamp.Justice;
        allyState.Camp = EntityCamp.Justice;
        captainState.PartyId = 101u;
        captainState.IsPartyCaptain = true;
        allyState.PartyId = 101u;
        captainState.ActiveAuraSkillId = 16;
        captainState.NextAuraPulseTick = 0;

        await harness.StepFramesAsync(12);

        Assert.Equal(45, captainState.SkillCombatModifiers.MeleeDamageReturnPercent);
        Assert.Equal(45, captainState.SkillCombatModifiers.RangeDamageReturnPercent);
        Assert.Equal(800, captainState.SkillCombatModifiers.ExDefenseDelta);
        Assert.Equal(45, allyState.SkillCombatModifiers.MeleeDamageReturnPercent);
        Assert.Equal(45, allyState.SkillCombatModifiers.RangeDamageReturnPercent);
        Assert.Equal(800, allyState.SkillCombatModifiers.ExDefenseDelta);
    }

    [Fact]
    public async Task TianwangMultiHitSpearDashAndShieldSkills_WhenCastInZoneHarness_ShouldExecuteFiveStrikesDashAndNegativeFireResist()
    {
        // Ground truth:
        //   - legacy-server/bin/server1/settings/skills.txt & script/skill/tianwang.lua:
        //     * Spear multi-hit progression: 30 Hoi Phong Lac Nhan (2 hits), 35 Duong Quan Tam Diep (3 hits),
        //       41 Huyet Chien Bat Phuong (4 hits), 323 Truy Tinh Truc Nguyet (5 hits, delay=3, ChildSkillId=327)
        //     * Hammer 90: 325 Truy Phong Quyet -> Child 408 (spread 1..3 rays via skill_misslenum_v)
        //     * Blade 90: 322 Pha Thien Tram -> Child 326 (wall, HorseLimit=1 dismounted-only? Check skills.txt HorseLimit=0 on 322!)
        //     * 40 Doan Hon Thich: meleeRunAttack dash in 16-mps steps + stun_p (16%..35%, 5..18f), Param2=18..1
        //     * 42 Kim Chung Trao: +physicsres_p (12..50%), +coldres_p (7..45%), +poisonres_p (12..49%), -fireres_p (-5..-15%)
        //     * 708 Dao Hu Thien: passive allres_p, allresmax_p, lifereplenish_p, ignoreskill_p
        ISkillTemplateCatalog skillCatalog = SharedGameAssetsFixture.Default.SkillTemplateCatalog;
        IMissileTemplateCatalog missileCatalog = SharedGameAssetsFixture.Default.MissileTemplateCatalog;

        (int SkillId, int ExpectedHits, int ExpectedDelay)[] spearChain =
        [
            (30, 2, 10),
            (35, 3, 8),
            (41, 4, 4),
            (323, 5, 3),
        ];
        Assert.NotEmpty(spearChain);
        foreach ((int skillId, int expectedHits, int expectedDelay) in spearChain)
        {
            Assert.True(skillCatalog.TryGetSkill(skillId, out SkillTemplate? spearSkill) && spearSkill is not null);
            Assert.Equal(SkillStyleKind.Melee, spearSkill.Style);
            Assert.Equal("meleeMany", spearSkill.MissileForm);
            Assert.Equal(expectedHits, spearSkill.ChildSkillCount);
            Assert.Equal(expectedDelay, spearSkill.MissileGenerateDelayFrames);
            Assert.Equal(3, spearSkill.EquipLimit);
        }

        Assert.True(skillCatalog.TryGetSkill(325, out SkillTemplate? truyPhong) && truyPhong is not null);
        Assert.Equal(408, truyPhong.ChildSkillId);
        Assert.True(skillCatalog.TryGetSkill(408, out SkillTemplate? truyPhongChild) && truyPhongChild is not null);
        Assert.Equal("spread", truyPhongChild.MissileForm);
        Assert.Equal(1, SkillEffectiveParams.Resolve(truyPhongChild, missile: null, skillLevel: 1).ChildSkillCount);
        Assert.Equal(3, SkillEffectiveParams.Resolve(truyPhongChild, missile: null, skillLevel: 20).ChildSkillCount);

        Assert.True(skillCatalog.TryGetSkill(708, out SkillTemplate? daoHuThien) && daoHuThien is not null);
        Assert.Equal(SkillStyleKind.Passive, daoHuThien.Style);
        Assert.True(RequireEffect(daoHuThien, "ignoreskill_p").Param0!.Evaluate(20) >= 89);
        Assert.Equal(35, RequireEffect(daoHuThien, "lifereplenish_p").Param0!.Evaluate(20));

        // Runtime ZoneSimulationHarness verification: Kim Chung Trao (42), Doan Hon Thich (40), and Truy Tinh Truc Nguyet (323)
        var dummySpawn = new StaticNpcSpawnTemplate(
            TemplateId: 586,
            MapId: 53,
            SpawnMpsX: 52080,
            SpawnMpsY: 101696,
            Level: 80,
            Kind: EntityKind.Monster,
            Camp: EntityCamp.Animal,
            Series: ElementSeries.Wood,
            IsSpecialNpc: false,
            DisplayName: "Mộc Nhân Thiên Vương",
            ScriptPath: string.Empty);

        ZoneSimulationHarness harness = CreateSixSectHarness(dummySpawn);
        SimulatedPlayerHandle tianwang = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "Dương Anh",
                CharacterGender.Female,
                ElementSeries.Metal,
                factionId: 1,
                mpsX: 51840,
                mpsY: 101696,
                skills:
                [
                    new CharacterSkillDto(40, 20, 0),
                    new CharacterSkillDto(42, 20, 0),
                    new CharacterSkillDto(323, 20, 0),
                ]));

        PlayerZoneState? player = tianwang.GetZoneState();
        Assert.NotNull(player);
        Assert.True(harness.TryGetZone(53, out MapZoneWorker? worker) && worker is not null);
        NpcZoneState dummy = Assert.Single(worker.StaticNpcs.Values);
        // Ground truth: KNpc::DoHurt @ 0x08097000 locks caster in do_hurt on monster counter-hit; mark training dummy stationary ("muren") to isolate skill combo frames.
        dummy.LevelScriptCode = "muren";
        dummy.CurLife = Math.Min(dummy.MaxLife, 200000u);

        player.FightMode = 1;
        player.Camp = EntityCamp.Justice;
        // Ground truth: skills.txt Skill 323 (Truy Tinh Truc Nguyet) has EquiptLimited = 3 (Spear) & KSkill::CanCastSkill @ 0x08101010 enforces weapon match.
        player.WeaponType = 0;
        player.WeaponParticular = 3;

        // 1. Cast Kim Chung Trao (42) -> positive physical/cold/poison resists & negative fire resist (-15%)
        tianwang.CastSkill(42, player.CurrentMpsX, player.CurrentMpsY);
        await harness.StepFramesAsync(15);
        Assert.Equal(50, player.SkillCombatModifiers.PhysicsResistPercent);
        Assert.Equal(45, player.SkillCombatModifiers.ColdResistPercent);
        Assert.Equal(49, player.SkillCombatModifiers.PoisonResistPercent);
        Assert.Equal(-15, player.SkillCombatModifiers.FireResistPercent);

        // 2. Cast Doan Hon Thich (40) from 240 mps away -> dashes forward in 16-mps steps without emitting s2c_npcrun
        int startX = player.CurrentMpsX;
        harness.EnqueueCommand(53, new CastSkillZoneCommand(tianwang.CharacterId, tianwang.LeaseEpoch, 40, -1, (int)dummy.NpcId));
        await harness.StepFramesAsync(18);
        Assert.True(player.CurrentMpsX > startX + 128, $"Expected meleeRunAttack dash > 128 mps, actual X={player.CurrentMpsX} from {startX}.");
        Assert.True(Math.Abs(dummy.MpsX - player.CurrentMpsX) <= 80);

        // 3. Cast Truy Tinh Truc Nguyet (323) in melee range -> 5 sequential hits scheduled at +1, +4, +7, +10, +13 frames
        uint hpBeforeSpear = dummy.CurLife;
        int distinctDamageFrames = 0;
        uint prevLife = hpBeforeSpear;
        harness.EnqueueCommand(53, new CastSkillZoneCommand(tianwang.CharacterId, tianwang.LeaseEpoch, 323, -1, (int)dummy.NpcId));
        for (int frame = 0; frame < 20; frame++)
        {
            await harness.StepFramesAsync(1);
            if (dummy.CurLife < prevLife)
            {
                distinctDamageFrames++;
                prevLife = dummy.CurLife;
            }
        }

        Assert.Equal(5, distinctDamageFrames);
        Assert.True(dummy.CurLife < hpBeforeSpear);
        _ = missileCatalog;
    }

    [Theory]
    [InlineData(347, 157, 540, 348)]
    [InlineData(303, 127, 540, 304)]
    [InlineData(343, 153, 540, 344)]
    [InlineData(345, 155, 540, 346)]
    [InlineData(349, 159, 540, 350)]
    [InlineData(351, 161, 1080, 352)]
    public async Task TangmenGroundTraps_WhenPlacedAndSteppedOnByMonster_ShouldWaitDormantThenDetonateWithSecondaryBurst(
        int trapSkillId,
        int expectedMissileId,
        int expectedLifeFrames,
        int expectedCollideSkillId)
    {
        // Ground truth:
        //   - legacy-server/bin/server1/settings/skills.txt & missles.txt & script/skill/tangmen.lua:
        //     All 6 Tangmen traps (347 Dia Diem Hoa, 303 Doc Thich Cot, 343 Xuyen Tam Thich,
        //     345 Han Bang Thich, 349 Loi Kich Thuat, 351 Loan Hoan Kich) spawn stationary ground mines
        //     (MissileForm="atFirer", MoveKind=Stand, Speed=0, CollideRadiusCells=2 -> HitRadiusMps=64,
        //      AutoExplode=false, CollideVanish=true, PierceCount=1) that lie dormant on the ground
        //     and detonate + trigger OnCollideSkillId (4-ray burst) ONLY when an enemy steps into range.
        ISkillTemplateCatalog skillCatalog = SharedGameAssetsFixture.Default.SkillTemplateCatalog;
        IMissileTemplateCatalog missileCatalog = SharedGameAssetsFixture.Default.MissileTemplateCatalog;

        Assert.True(skillCatalog.TryGetSkill(trapSkillId, out SkillTemplate? trapSkill) && trapSkill is not null);
        Assert.Equal(SkillStyleKind.Projectile, trapSkill.Style);
        Assert.Equal("atFirer", trapSkill.MissileForm);
        Assert.Equal(expectedMissileId, trapSkill.MissileId);
        Assert.NotNull(trapSkill.EventSkills);
        Assert.Equal(expectedCollideSkillId, trapSkill.EventSkills.OnCollideSkillId);

        Assert.True(missileCatalog.TryGetMissile(expectedMissileId, out MissileTemplate? trapMissile) && trapMissile is not null);
        Assert.Equal(MissileMoveKind.Stand, trapMissile.MoveKind);
        Assert.Equal(0, trapMissile.SpeedMpsPerFrame);
        Assert.Equal(expectedLifeFrames, trapMissile.LifeFrames);
        Assert.Equal(2, trapMissile.CollideRadiusCells);
        Assert.Equal(64, trapMissile.HitRadiusMps);
        Assert.False(trapMissile.AutoExplode);
        Assert.True(trapMissile.CollideVanish);
        Assert.Equal(1, trapMissile.PierceCount);

        Assert.True(skillCatalog.TryGetSkill(expectedCollideSkillId, out SkillTemplate? burstSkill) && burstSkill is not null);
        Assert.Equal(4, burstSkill.ChildSkillCount);

        // Both (51800, 101696) and (52100, 101696) reside in SpatialRegion (101, 99), 300 mps apart (> 64 mps trap radius).
        var monsterSpawn = new StaticNpcSpawnTemplate(
            TemplateId: 586,
            MapId: 53,
            SpawnMpsX: 52100,
            SpawnMpsY: 101696,
            Level: 60,
            Kind: EntityKind.Monster,
            Camp: EntityCamp.Animal,
            Series: ElementSeries.Earth,
            IsSpecialNpc: false,
            DisplayName: "Mộc Nhân Dẫm Bẫy",
            ScriptPath: string.Empty);

        ZoneSimulationHarness harness = CreateSixSectHarness(monsterSpawn);
        SimulatedPlayerHandle tangmen = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "Đường Bất Nhiễm",
                CharacterGender.Male,
                ElementSeries.Wood,
                factionId: 2,
                mpsX: 51800,
                mpsY: 101696,
                skills: [new CharacterSkillDto((ushort)trapSkillId, 20, 0)]));

        PlayerZoneState? player = tangmen.GetZoneState();
        Assert.NotNull(player);
        player.FightMode = 1;
        player.Camp = EntityCamp.Justice;

        Assert.True(harness.TryGetZone(53, out MapZoneWorker? worker) && worker is not null);
        NpcZoneState monster = Assert.Single(worker.StaticNpcs.Values);
        uint initialMonsterLife = Math.Min(monster.MaxLife, 50000u);
        monster.CurLife = initialMonsterLife;

        // Place trap at (51800, 101696) even when clicking far away (52300, 102000)
        tangmen.CastSkill((ushort)trapSkillId, 52300, 102000);
        await harness.StepFramesAsync(30);

        // Monster at 300 mps away must remain untouched while the ground trap waits on the floor
        Assert.Equal(initialMonsterLife, monster.CurLife);
        Assert.False(monster.StatusEffects.IsPoisoned);

        // Enemy steps onto trap coordinates (51800, 101696) -> trap detonates!
        monster.MpsX = 51800;
        monster.MpsY = 101696;
        await harness.StepFramesAsync(15);

        Assert.True(
            monster.CurLife < initialMonsterLife || monster.StatusEffects.IsPoisoned,
            $"Expected trap {trapSkillId} to detonate and damage/poison monster upon stepping onto (51800, 101696).");
    }

    [Fact]
    public async Task TangmenScatterProjectilesAndMeAnhTung_AtLevel20_ShouldMatchEightRayBurstAndInstantTeleport()
    {
        // Ground truth:
        //   - 302 Bao Vu Le Hoa: atTarget M[96] (LifeFrames=36) + OnFlySkillId=301 @ 30f -> 301 Truy Tinh Truc Dien (circle, 8 rays)
        //   - 58 Thien La Dia Vong: line + OnCollideSkillId=227 (circle, 3 rays)
        //   - 339 Nhiep Hon Nguyet Anh: line order:5 + OnCollideSkillId=340
        //   - 342 Cuu Cung Phi Tinh: spread centerExtendLine:2, 5 projectiles
        //   - 710 Me Anh Tung: meleeMovePos instant teleport up to Param1=120..400 mps, MinFramesPerCast=270..90f (horse 360..144f)
        ISkillTemplateCatalog skillCatalog = SharedGameAssetsFixture.Default.SkillTemplateCatalog;

        Assert.True(skillCatalog.TryGetSkill(302, out SkillTemplate? baoVu) && baoVu is not null);
        Assert.Equal("atTarget", baoVu.MissileForm);
        Assert.NotNull(baoVu.EventSkills);
        Assert.Equal(301, baoVu.EventSkills.OnFlySkillId);
        Assert.Equal(30, baoVu.EventSkills.OnFlyIntervalFrames);

        Assert.True(skillCatalog.TryGetSkill(301, out SkillTemplate? baoVuScatter) && baoVuScatter is not null);
        Assert.Equal("circle", baoVuScatter.MissileForm);
        Assert.Equal(8, baoVuScatter.ChildSkillCount);

        Assert.True(skillCatalog.TryGetSkill(58, out SkillTemplate? thienLa) && thienLa is not null);
        Assert.NotNull(thienLa.EventSkills);
        Assert.Equal(227, thienLa.EventSkills.OnCollideSkillId);
        Assert.True(skillCatalog.TryGetSkill(227, out SkillTemplate? thienLaSub) && thienLaSub is not null);
        Assert.Equal(3, thienLaSub.ChildSkillCount);

        Assert.True(skillCatalog.TryGetSkill(339, out SkillTemplate? nhiepHon) && nhiepHon is not null);
        Assert.NotNull(nhiepHon.EventSkills);
        Assert.Equal(340, nhiepHon.EventSkills.OnCollideSkillId);

        Assert.True(skillCatalog.TryGetSkill(342, out SkillTemplate? cuuCung) && cuuCung is not null);
        Assert.Equal("spread", cuuCung.MissileForm);
        Assert.Equal(5, cuuCung.ChildSkillCount);

        Assert.True(skillCatalog.TryGetSkill(710, out SkillTemplate? meAnhTung) && meAnhTung is not null);
        Assert.Equal(SkillStyleKind.Melee, meAnhTung.Style);
        Assert.Equal("meleeMovePos", meAnhTung.MissileForm);
        SkillEffectiveParams lv1Params = SkillEffectiveParams.Resolve(meAnhTung, missile: null, skillLevel: 1);
        SkillEffectiveParams lv20Params = SkillEffectiveParams.Resolve(meAnhTung, missile: null, skillLevel: 20);
        Assert.Equal(120, lv1Params.Param1);
        Assert.Equal(270, lv1Params.MinFramesPerCast);
        Assert.Equal(360, lv1Params.MinFramesPerCastOnHorse);
        Assert.Equal(400, lv20Params.Param1);
        Assert.Equal(90, lv20Params.MinFramesPerCast);
        Assert.Equal(144, lv20Params.MinFramesPerCastOnHorse);

        ZoneSimulationHarness harness = CreateSixSectHarness();
        SimulatedPlayerHandle tangmen = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "Đường Cừu",
                CharacterGender.Male,
                ElementSeries.Wood,
                factionId: 2,
                mpsX: 52000,
                mpsY: 101696,
                skills: [new CharacterSkillDto(710, 20, 0)]));

        PlayerZoneState? player = tangmen.GetZoneState();
        Assert.NotNull(player);
        player.FightMode = 1;

        tangmen.CastSkill(710, 52320, 101696);
        await harness.StepFramesAsync(2);

        Assert.Equal(52320, player.CurrentMpsX);
        Assert.Equal(101696, player.CurrentMpsY);
    }

    [Fact]
    public async Task WuduNegativeResistCursesAndHapTinhYem_WhenAppliedInCombat_ShouldAmplifyPhysicalPoisonDamageAndBurnMana()
    {
        // Ground truth:
        //   - legacy-server/bin/server1/script/skill/wudu.lua & attribconstdata.ini:
        //     * 356 Xuyen Y Pha Giap: physicsres_p (-9% -> -49%)
        //     * 72 Xuyen Tam Doc Thich: poisonres_p (-29% -> -49%)
        //     * 64 Bang Lam Huyen Tinh / 67 Cuu Thien Cuong Loi / 70 Xich Diem Thuc Thien: -9% -> -49%
        //     * 390 Doan Can Hu Cot: allres_p (-1% -> -40%)
        //     * 73 Van Doc Thuc Tam: poisontimereduce_p (-100% -> -200%), tripling poison duration at Lv20!
        //     * 711 Hap Tinh Yem (120) -> autoattackskill 719 (poison2decmana_p 30% -> 85%)
        ISkillTemplateCatalog skillCatalog = SharedGameAssetsFixture.Default.SkillTemplateCatalog;

        (int SkillId, string AttrCode, int ExpectedLv1, int ExpectedLv20)[] curseSpecs =
        [
            (356, "physicsres_p", -9, -49),
            (72, "poisonres_p", -29, -49),
            (64, "coldres_p", -9, -49),
            (67, "lightingres_p", -9, -49),
            (70, "fireres_p", -9, -49),
            (390, "allres_p", -1, -40),
            (73, "poisontimereduce_p", -100, -200),
        ];
        Assert.NotEmpty(curseSpecs);
        foreach ((int skillId, string attrCode, int expectedLv1, int expectedLv20) in curseSpecs)
        {
            Assert.True(skillCatalog.TryGetSkill(skillId, out SkillTemplate? curse) && curse is not null);
            SkillEffectScalingTemplate eff = RequireEffect(curse, attrCode);
            Assert.Equal(expectedLv1, eff.Param0!.Evaluate(1));
            Assert.Equal(expectedLv20, eff.Param0.Evaluate(20));
        }

        Assert.True(skillCatalog.TryGetSkill(353, out SkillTemplate? amPhong) && amPhong is not null);
        Assert.Equal(354, amPhong.EventSkills!.OnVanishSkillId);
        Assert.True(skillCatalog.TryGetSkill(355, out SkillTemplate? huyenAm) && huyenAm is not null);
        Assert.Equal(383, huyenAm.EventSkills!.OnCollideSkillId);

        Assert.True(skillCatalog.TryGetSkill(711, out SkillTemplate? hapTinhYem) && hapTinhYem is not null);
        SkillEffectScalingTemplate autoProc = RequireEffect(hapTinhYem, "autoattackskill");
        Assert.Equal((719 << 8) | 20, autoProc.Param0!.Evaluate(20));
        Assert.True(skillCatalog.TryGetSkill(719, out SkillTemplate? hapTinhCurse) && hapTinhCurse is not null);
        Assert.Equal(85, RequireEffect(hapTinhCurse, "poison2decmana_p").Param0!.Evaluate(20));

        // Runtime ZoneSimulationHarness verification: Xuyen Y Pha Giap (356) applies -49% PhysicsResistPercent on target monster
        var monsterSpawn = new StaticNpcSpawnTemplate(
            TemplateId: 586,
            MapId: 53,
            SpawnMpsX: 52000,
            SpawnMpsY: 101696,
            Level: 60,
            Kind: EntityKind.Monster,
            Camp: EntityCamp.Animal,
            Series: ElementSeries.Earth,
            IsSpecialNpc: false,
            DisplayName: "Mộc Nhân Trúng Bùa",
            ScriptPath: string.Empty);

        ZoneSimulationHarness harness = CreateSixSectHarness(monsterSpawn);
        SimulatedPlayerHandle wudu = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "Hắc Diện Lang Quân",
                CharacterGender.Male,
                ElementSeries.Wood,
                factionId: 3,
                mpsX: 51800,
                mpsY: 101696,
                skills:
                [
                    new CharacterSkillDto(356, 20, 0),
                    new CharacterSkillDto(73, 20, 0),
                ]));

        PlayerZoneState? player = wudu.GetZoneState();
        Assert.NotNull(player);
        player.FightMode = 1;
        player.Camp = EntityCamp.Evil;

        Assert.True(harness.TryGetZone(53, out MapZoneWorker? worker) && worker is not null);
        NpcZoneState monster = Assert.Single(worker.StaticNpcs.Values);
        monster.PhysicsResistPercent = 0;

        harness.EnqueueCommand(53, new CastSkillZoneCommand(wudu.CharacterId, wudu.LeaseEpoch, 356, -1, (int)monster.NpcId));
        await harness.StepFramesAsync(15);
        Assert.Equal(-49, ((ICombatEntityState)monster).PhysicsResistPercent);

        // Verify exact damage amplification (-49% physical resist with matching Yan floor <= -49 per
        // KNpc::CalcDamage @ 0x0809e898..0x0809e8a2 max(resYin, resYan) -> 200 * 149 / 100 = 298 HP)
        // and Van Doc Thuc Tam (-200% PoisonTimeReducePercent -> 60 * (100 - (-200)) / 100 = 180 frames)
        // plus Hap Tinh Yem_Quyet Chu (85% Poison2DecManaPercent) burning defender mana.
        monster.CombatModifiers.PhysicsResistYanPercent = -49;
        monster.CombatModifiers.PoisonTimeReducePercent = -200;
        var physAndPoisonPayload = new SkillDamagePayload(
            SkillId: 65,
            SkillLevel: 20,
            ElementSeries: ElementSeries.Wood,
            IsPhysical: true,
            IsMelee: false,
            UseAttackRating: false,
            DoHurtAnimation: false,
            AttackRatingScore: 1000,
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
            PoisonDamagePerTick: 100,
            PoisonDurationFrames: 60,
            PoisonIntervalFrames: 10,
            MagicMinDamage: 0,
            MagicMaxDamage: 0,
            HasElementSeries: false);

        CombatHitResolutionResult cursedHit = CombatDamageCalculator.ResolveHitAndApplyDamage(
            attacker: null,
            defender: monster,
            isPvp: false,
            in physAndPoisonPayload,
            new Random(1));
        Assert.Equal(298, cursedHit.PhysicsHpLost);
        Assert.Equal(180, monster.StatusEffects.PoisonRemainingFrames);
    }

    [Fact]
    public async Task EmeiPiercingPalmAurasAndBeNguyetPhatTran_WhenActivated_ShouldExpandFiveSupportAurasAndBoostMaxLife()
    {
        // Ground truth:
        //   - legacy-server/bin/server1/script/skill/emei.lua:
        //     * 380 Phong Suong Toai Anh: atTarget M[186] (CollideVanish=false, AutoExplode=true) + OnStartSkillId=331 (circle)
        //     * 328 Tam Nga Te Tuyet: wall x3 + OnStartSkillId=329
        //     * 86 Luu Thuy (fastwalkrun_p 9..66%), 89 Mong Diep (lifereplenish_v + manareplenish_v),
        //       92 Phat Tam Tu Huu (lifemax_p 30..125%), 282 Thanh Am Phan Xuong, 332 Pho Do Chung Sinh
        //     * 712 Be Nguyet Phat Tran (120) -> Child 718 expanding all 5 auras [86, 89, 92, 282, 332]
        //       at level 1 (Lv1), 19 (Lv19), and 40 (Lv20).
        ISkillTemplateCatalog skillCatalog = SharedGameAssetsFixture.Default.SkillTemplateCatalog;
        IMissileTemplateCatalog missileCatalog = SharedGameAssetsFixture.Default.MissileTemplateCatalog;

        Assert.True(skillCatalog.TryGetSkill(380, out SkillTemplate? phongSuong) && phongSuong is not null);
        Assert.Equal("atTarget", phongSuong.MissileForm);
        Assert.Equal(331, phongSuong.EventSkills!.OnStartSkillId);
        Assert.True(missileCatalog.TryGetMissile(phongSuong.MissileId, out MissileTemplate? phongSuongMissile) && phongSuongMissile is not null);
        Assert.False(phongSuongMissile.CollideVanish);
        Assert.True(phongSuongMissile.AutoExplode);

        Assert.True(skillCatalog.TryGetSkill(328, out SkillTemplate? tamNga) && tamNga is not null);
        Assert.Equal("wall", tamNga.MissileForm);
        Assert.Equal(3, tamNga.ChildSkillCount);
        Assert.Equal(329, tamNga.EventSkills!.OnStartSkillId);

        Assert.True(skillCatalog.TryGetSkill(712, out SkillTemplate? beNguyet) && beNguyet is not null);
        Assert.True(beNguyet.IsAura);
        Assert.Equal(718, beNguyet.ChildSkillId);
        Assert.True(skillCatalog.TryGetSkill(718, out SkillTemplate? beNguyetChild) && beNguyetChild is not null);

        List<(SkillTemplate Skill, int Level)> appendedLv19 =
            SkillStateEffectApplier.ExtractAppendedStateSkills(beNguyetChild, skillLevel: 19, skillCatalog);
        List<(SkillTemplate Skill, int Level)> appendedLv20 =
            SkillStateEffectApplier.ExtractAppendedStateSkills(beNguyetChild, skillLevel: 20, skillCatalog);

        Assert.NotEmpty(appendedLv19);
        Assert.NotEmpty(appendedLv20);
        Assert.Equal(5, appendedLv19.Count);
        Assert.Equal(5, appendedLv20.Count);

        // Ground truth: skills.txt parent Aura skills [86, 89, 92, 282, 332] carry empty KMagicAttrib arrays and
        // delegate modifier execution to ChildSkillIds [206, 207, 208, 281, 333]; ExtractAppendedStateSkills(718) expands those 5 child skills.
        var expectedSupportIds = new HashSet<int> { 206, 207, 208, 281, 333 };
        var observedSupportIds = new HashSet<int>();
        foreach ((SkillTemplate appendedSkill, int level) in appendedLv19)
        {
            observedSupportIds.Add(appendedSkill.Id);
            Assert.Equal(19, level);
        }

        Assert.Equal(expectedSupportIds, observedSupportIds);

        Assert.NotEmpty(appendedLv20);
        foreach ((_, int level) in appendedLv20)
        {
            Assert.Equal(40, level);
        }

        // Runtime 18-Hz verification: Phat Tam Tu Huu (92) Lv20 (+125% lifemax_p) boosts EffectiveMaxLife on caster & party member
        ZoneSimulationHarness harness = CreateSixSectHarness();
        SimulatedPlayerHandle emeiCaptain = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "Thanh Hiểu Sư Thái",
                CharacterGender.Female,
                ElementSeries.Water,
                factionId: 4,
                mpsX: 52000,
                mpsY: 101696,
                maxLife: 2000,
                skills: [new CharacterSkillDto(92, 20, 0)]));
        SimulatedPlayerHandle partyAlly = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "Chu Chỉ Nhược",
                CharacterGender.Female,
                ElementSeries.Water,
                factionId: 4,
                mpsX: 52096,
                mpsY: 101696,
                maxLife: 2000,
                skills: [new CharacterSkillDto(85, 20, 0)]));

        PlayerZoneState? captainState = emeiCaptain.GetZoneState();
        PlayerZoneState? allyState = partyAlly.GetZoneState();
        Assert.NotNull(captainState);
        Assert.NotNull(allyState);

        captainState.Camp = EntityCamp.Justice;
        allyState.Camp = EntityCamp.Justice;
        captainState.PartyId = 202u;
        captainState.IsPartyCaptain = true;
        allyState.PartyId = 202u;
        captainState.ActiveAuraSkillId = 92;
        captainState.NextAuraPulseTick = 0;

        await harness.StepFramesAsync(12);

        Assert.Equal(125, captainState.SkillCombatModifiers.LifeMaxPercent);
        Assert.Equal(125, allyState.SkillCombatModifiers.LifeMaxPercent);
        Assert.True(captainState.EffectiveMaxLife >= 4500);
        Assert.True(allyState.EffectiveMaxLife >= 4500);
    }

    [Fact]
    public async Task CuiyanIceSkillsAndNguTuyetAnStealth_WhenCastAndAttacking_ShouldEnterHideModeSuppressAuraAndRevealOnCast()
    {
        // Ground truth:
        //   - legacy-server/bin/server1/script/skill/cuiyan.lua & attribconstdata.ini:
        //     * 108 Muc Da Luu Tinh: atTarget M[9] (HeightMps=320, ZSpeed=-40960, ZAccel=40960, IsRangeDamage=true, DamageRadiusCells=2)
        //     * 111 Bich Hai Trieu Sinh: atFirer + OnStartSkillId=112 (circle, 16 rays)
        //     * 336 Bang Tung Vo Anh: spread 1..5 rays via skill_misslenum_v, M[146] CollideFollowTarget=true, CanSlow=true
        //     * 337 Bang Tam Tien Tu: line M[147] + OnFlySkillId=338 every 3 frames
        //     * 100 Ho The Han Bang: meleedamagereturn_p & rangedamagereturn_p
        //     * 713 Ngu Tuyet An (120): hide (Attr 200, HideMode=1, 90..540f), suppresses active aura pulses while hidden,
        //       and clears stealth immediately before broadcasting 0x5A when casting an offensive skill!
        ISkillTemplateCatalog skillCatalog = SharedGameAssetsFixture.Default.SkillTemplateCatalog;
        IMissileTemplateCatalog missileCatalog = SharedGameAssetsFixture.Default.MissileTemplateCatalog;

        Assert.True(skillCatalog.TryGetSkill(108, out SkillTemplate? mucDa) && mucDa is not null);
        Assert.True(missileCatalog.TryGetMissile(mucDa.MissileId, out MissileTemplate? mucDaMissile) && mucDaMissile is not null);
        Assert.Equal(320, mucDaMissile.HeightMps);
        Assert.Equal(-40960, mucDaMissile.ZSpeedMpsPerFrame);
        Assert.Equal(40960, mucDaMissile.ZAccelMpsPerFrame2);
        Assert.True(mucDaMissile.IsRangeDamage);
        Assert.Equal(2, mucDaMissile.DamageRadiusCells);

        Assert.True(skillCatalog.TryGetSkill(111, out SkillTemplate? bichHai) && bichHai is not null);
        Assert.Equal(112, bichHai.EventSkills!.OnStartSkillId);
        Assert.True(skillCatalog.TryGetSkill(112, out SkillTemplate? bichHaiWave) && bichHaiWave is not null);
        Assert.Equal("circle", bichHaiWave.MissileForm);
        Assert.Equal(16, bichHaiWave.ChildSkillCount);

        Assert.True(skillCatalog.TryGetSkill(336, out SkillTemplate? bangTung) && bangTung is not null);
        Assert.Equal("spread", bangTung.MissileForm);
        Assert.True(missileCatalog.TryGetMissile(bangTung.MissileId, out MissileTemplate? bangTungMissile) && bangTungMissile is not null);
        Assert.True(bangTungMissile.CollideFollowTarget);
        Assert.True(bangTungMissile.CanSlow);
        Assert.Equal(1, SkillEffectiveParams.Resolve(bangTung, bangTungMissile, skillLevel: 1).ChildSkillCount);
        Assert.Equal(5, SkillEffectiveParams.Resolve(bangTung, bangTungMissile, skillLevel: 20).ChildSkillCount);

        Assert.True(skillCatalog.TryGetSkill(337, out SkillTemplate? bangTam) && bangTam is not null);
        Assert.Equal(338, bangTam.EventSkills!.OnFlySkillId);
        Assert.Equal(3, bangTam.EventSkills.OnFlyIntervalFrames);

        Assert.True(skillCatalog.TryGetSkill(100, out SkillTemplate? hoTheHanBang) && hoTheHanBang is not null);
        Assert.NotNull(RequireEffect(hoTheHanBang, "meleedamagereturn_p"));
        Assert.NotNull(RequireEffect(hoTheHanBang, "rangedamagereturn_p"));

        // Runtime 18-Hz verification: Ngu Tuyet An (713) enters HideMode=1, blocks aura pulsing while hidden, and clears on cast
        ZoneSimulationHarness harness = CreateSixSectHarness();
        SimulatedPlayerHandle cuiyan = await harness.AttachSimulatedPlayerAsync(
            CreateSectCharacterDto(
                "Doãn Hàm Yên",
                CharacterGender.Female,
                ElementSeries.Water,
                factionId: 5,
                mpsX: 52000,
                mpsY: 101696,
                skills:
                [
                    new CharacterSkillDto(86, 20, 0),
                    new CharacterSkillDto(108, 20, 0),
                    new CharacterSkillDto(713, 20, 0),
                ]));

        PlayerZoneState? player = cuiyan.GetZoneState();
        Assert.NotNull(player);
        player.FightMode = 1;
        // Ground truth: skills.txt Skill 108 (Muc Da Luu Tinh) has EquiptLimited = 1 (Blade) & KSkill::CanCastSkill @ 0x08101010 enforces weapon match.
        player.WeaponType = 0;
        player.WeaponParticular = 1;

        cuiyan.CastSkill(713, player.CurrentMpsX, player.CurrentMpsY);
        await harness.StepFramesAsync(20);
        Assert.Equal(1, ((ICombatEntityState)player).HideMode);

        // While hidden, turning on ActiveAuraSkillId=86 (Luu Thuy) must NOT pulse speed buff
        player.ActiveAuraSkillId = 86;
        player.NextAuraPulseTick = 0;
        await harness.StepFramesAsync(12);
        Assert.Equal(0, player.SkillCombatModifiers.FastWalkRunPercent);

        // Casting Muc Da Luu Tinh (108) immediately breaks stealth (ClearHide in attribconstdata.ini)
        cuiyan.CastSkill(108, 52128, 101696);
        await harness.StepFramesAsync(2);
        Assert.Equal(0, ((ICombatEntityState)player).HideMode);
    }

    [Fact]
    public async Task LowLevelSectSkillSynergies_WhenLearnedAtLevel20AcrossSixSects_ShouldMatchLuaAddSkillDamageCurves()
    {
        // Ground truth:
        //   - legacy-server/bin/server1/script/skill/{shaolin,tianwang,tangmen,wudu,emei,cuiyan}.lua
        //     & KSkill::CastInitiativeSkill synergy mask (Melee | Projectile | InstantMissile):
        //     * Shaolin 318 Dat Ma Do Giang <- 14 (+150%) + 271 (+110%) = +260%
        //     * Tianwang 323 Truy Tinh Truc Nguyet -> Child 327? Check 323 <- 30 (+60%) + 35 (+65%) + 41 (+70%) = +195%
        //     * Tangmen 302 Bao Vu Le Hoa <- 45 (+35%) + 54 (+45%) + 58 (+55%) = +135%
        //     * Wudu 355 Huyen Am Tram <- 65 (+60%) + 74 (+60%) = +120%
        //     * Emei 380 Phong Suong Toai Anh <- 80 (+75%) + 91 (+80%) = +155%
        //     * Cuiyan 336 Bang Tung Vo Anh <- 99 (+55%) + 108 (+60%) = +115%
        ISkillTemplateCatalog skillCatalog = SharedGameAssetsFixture.Default.SkillTemplateCatalog;

        (byte FactionId, ElementSeries Series, ushort TargetSkillId, ushort[] LowLevelSkillIds, bool UsesColdChannel)[] cases =
        [
            (0, ElementSeries.Metal, 317, [14, 271], false),
            (1, ElementSeries.Metal, 323, [30, 35, 41], false),
            (2, ElementSeries.Wood, 302, [45, 54, 58], false),
            (3, ElementSeries.Wood, 355, [65, 74], false),
            (4, ElementSeries.Water, 380, [80, 91], true),
            (5, ElementSeries.Water, 336, [99, 108], false),
        ];
        Assert.NotEmpty(cases);

        ZoneSimulationHarness harness = CreateSixSectHarness();
        int idx = 0;
        foreach ((byte factionId, ElementSeries series, ushort targetSkillId, ushort[] lowSkillIds, bool usesColdChannel) in cases)
        {
            idx++;
            ushort evalSkillId = targetSkillId == 317 ? (ushort)318 : targetSkillId;
            Assert.True(skillCatalog.TryGetSkill(evalSkillId, out SkillTemplate? targetSkill) && targetSkill is not null);

            SimulatedPlayerHandle soloHandle = await harness.AttachSimulatedPlayerAsync(
                CreateSectCharacterDto(
                    $"Solo_{idx}",
                    CharacterGender.Female,
                    series,
                    factionId,
                    mpsX: 52000,
                    mpsY: 101696,
                    skills: [new CharacterSkillDto(evalSkillId, 20, 0)]));

            var fullSkillDtos = new List<CharacterSkillDto> { new(evalSkillId, 20, 0) };
            Assert.NotEmpty(lowSkillIds);
            foreach (ushort lowId in lowSkillIds)
            {
                fullSkillDtos.Add(new CharacterSkillDto(lowId, 20, 0));
            }

            SimulatedPlayerHandle synergyHandle = await harness.AttachSimulatedPlayerAsync(
                CreateSectCharacterDto(
                    $"Synergy_{idx}",
                    CharacterGender.Female,
                    series,
                    factionId,
                    mpsX: 52064,
                    mpsY: 101696,
                    skills: fullSkillDtos));

            PlayerZoneState? soloPlayer = soloHandle.GetZoneState();
            PlayerZoneState? synergyPlayer = synergyHandle.GetZoneState();
            Assert.NotNull(soloPlayer);
            Assert.NotNull(synergyPlayer);

            SkillDamagePayload soloPayload = CombatDamageCalculator.BuildPlayerSkillDamagePayload(
                soloPlayer,
                targetSkill,
                skillLevel: 20,
                skillCatalog);
            SkillDamagePayload synergyPayload = CombatDamageCalculator.BuildPlayerSkillDamagePayload(
                synergyPlayer,
                targetSkill,
                skillLevel: 20,
                skillCatalog);

            if (usesColdChannel)
            {
                Assert.True(
                    synergyPayload.ColdMaxDamage > soloPayload.ColdMaxDamage,
                    $"Expected synergy ColdMaxDamage ({synergyPayload.ColdMaxDamage}) > solo ({soloPayload.ColdMaxDamage}) for skill {evalSkillId}.");
            }
            else
            {
                Assert.True(
                    synergyPayload.PhysicsMaxDamage > soloPayload.PhysicsMaxDamage,
                    $"Expected synergy PhysicsMaxDamage ({synergyPayload.PhysicsMaxDamage}) > solo ({soloPayload.PhysicsMaxDamage}) for skill {evalSkillId}.");
            }
        }
    }
}
