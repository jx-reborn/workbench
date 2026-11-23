using JXReborn.Workbench.ParityTests.Configuration;
using System.Text.Json;
using JXReborn.LegacyImporter.Legacy;
using JXReborn.GameAssets.Configuration;
using JXReborn.GameAssets.Items;
using JXReborn.GameAssets.PlayerTemplates;
using JXReborn.GameAssets.Skills;
using JXReborn.Primitives.Characters;
using JXReborn.Primitives.Combat;
using JXReborn.Primitives.Items;
using JXReborn.TestBase.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JXReborn.GameAssets.Tests.Catalogs;

[Trait("Category", "LegacyData")]
public sealed class SkillAndMissileCatalogParityTests
{
    private static IOptions<GameAssetsOptions> CreateOptions() =>
        Options.Create(new GameAssetsOptions
        {
            DataDirectory = TestWorkspacePaths.DataDirectory,
            ArtifactsDirectory = TestWorkspacePaths.EffectiveArtifactsDirectory,
            DataSnapshotFile = TestWorkspacePaths.EffectiveDataSnapshotFile,
            PreferBinarySnapshots = true,
        });

    [Fact]
    public void SkillTemplateCatalog_NewSkillMetadataFields_ShouldLoadFromSnapshotAndPreserveClientSendMode()
    {
        var skillCatalog = new SkillTemplateCatalog(CreateOptions(), NullLogger<SkillTemplateCatalog>.Instance);

        // Skill 1 (Basic melee projectile): SkillStyle=0 with IsMelee=true must NOT collapse to SkillStyleKind.Melee
        Assert.True(skillCatalog.TryGetSkill(1, out SkillTemplate? skill1));
        Assert.NotNull(skill1);
        Assert.Equal(SkillStyleKind.Projectile, skill1.Style);
        Assert.True(skill1.IsMelee);
        Assert.Equal(64, skill1.MissileId);

        // Skill 16 (La Hán Trận): StateSpecialId & StatePriority from skills.txt
        Assert.True(skillCatalog.TryGetSkill(16, out SkillTemplate? skill16));
        Assert.NotNull(skill16);
        Assert.Equal(SkillStyleKind.Aura, skill16.Style);
        Assert.Equal(45, skill16.StateSpecialId);
        Assert.Equal(2, skill16.StatePriority);
        Assert.False(skill16.IsBaseSkill);

        // Skill 128, 1057, 1368 (SkillStyle=0 multi-missile): preserve both MissileId and ChildSkillCount
        Assert.True(skillCatalog.TryGetSkill(128, out SkillTemplate? skill128));
        Assert.NotNull(skill128);
        Assert.Equal(SkillStyleKind.Projectile, skill128.Style);
        Assert.Equal(48, skill128.MissileId);
        Assert.Equal(8, skill128.ChildSkillCount);
        Assert.Equal(3, skill128.Param1);
        Assert.Equal(1, skill128.Param2);

        Assert.True(skillCatalog.TryGetSkill(1057, out SkillTemplate? skill1057));
        Assert.NotNull(skill1057);
        Assert.Equal(319, skill1057.MissileId);
        Assert.Equal(3, skill1057.ChildSkillCount);

        Assert.True(skillCatalog.TryGetSkill(1368, out SkillTemplate? skill1368));
        Assert.NotNull(skill1368);
        Assert.Equal(418, skill1368.MissileId);
        Assert.Equal(9, skill1368.ChildSkillCount);
        Assert.Equal(5, skill1368.MissileGenerateDelayFrames);

        // Skill 251: ClientSend=2 (visual-only) preserved in ClientSendMode alongside ClientSend=true
        Assert.True(skillCatalog.TryGetSkill(251, out SkillTemplate? skill251));
        Assert.NotNull(skill251);
        Assert.True(skill251.ClientSend);
        Assert.Equal((byte)2, skill251.ClientSendMode);

        // Skill 384: RelativePosType=2
        Assert.True(skillCatalog.TryGetSkill(384, out SkillTemplate? skill384));
        Assert.NotNull(skill384);
        Assert.Equal(2, skill384.RelativePosType);
        Assert.False(skill384.HeelAtParent);

        // Skill 400 (Kiếp Phú Tế Bần): SkillStyle=13 -> Steal
        Assert.True(skillCatalog.TryGetSkill(400, out SkillTemplate? skill400));
        Assert.NotNull(skill400);
        Assert.Equal(SkillStyleKind.Steal, skill400.Style);
        Assert.Equal("order", skill400.MissileGenerateStyle);
        Assert.Equal(1, skill400.MissileGenerateDelayFrames);

        // Skill 696: BaseSkill=0 with ChildSkillId=687 as skillId (not missileId), EventSkills, Targeting, ByMissile, MinFramesPerCast
        Assert.True(skillCatalog.TryGetSkill(696, out SkillTemplate? skill696));
        Assert.NotNull(skill696);
        Assert.Equal(SkillStyleKind.Projectile, skill696.Style);
        Assert.False(skill696.IsBaseSkill);
        Assert.Equal(0, skill696.MissileId);
        Assert.Equal(687, skill696.ChildSkillId);
        Assert.Equal(16, skill696.ChildSkillCount);
        Assert.True(skill696.ByMissile);
        Assert.Equal("sameTime", skill696.MissileGenerateStyle);
        Assert.Equal(5, skill696.MissileGenerateDelayFrames);
        Assert.Equal(5, skill696.MinFramesPerCast);
        Assert.Equal(5, skill696.MinFramesPerCastOnHorse);
        Assert.Equal(11, skill696.CharAnimId);
        Assert.Equal(100, skill696.Param2);
        Assert.NotNull(skill696.EventSkills);
        Assert.Equal(1, skill696.EventSkills.EventSkillLevel);
        Assert.Equal(680, skill696.EventSkills.OnVanishSkillId);
        Assert.Equal(1, skill696.EventSkills.OnVanishMinParentLevel);
        Assert.NotNull(skill696.Targeting);
        Assert.True(skill696.Targeting.TargetEnemy);
        Assert.False(skill696.Targeting.TargetSelf);

        // Skill 710 (Mê Ảnh Tung): MisslesForm=13 -> meleeMovePos
        Assert.True(skillCatalog.TryGetSkill(710, out SkillTemplate? skill710));
        Assert.NotNull(skill710);
        Assert.Equal(SkillStyleKind.Melee, skill710.Style);
        Assert.Equal("meleeMovePos", skill710.MissileForm);

        // Skill 721: SkillStyle=14 -> InstantMissile
        Assert.True(skillCatalog.TryGetSkill(721, out SkillTemplate? skill721));
        Assert.NotNull(skill721);
        Assert.Equal(SkillStyleKind.InstantMissile, skill721.Style);
        Assert.Equal(275, skill721.MissileId);
        Assert.Equal(123, skill721.StateSpecialId);
        Assert.Equal(4, skill721.StatePriority);
        Assert.True(skill721.ByMissile);
        Assert.Null(skill721.ExpScaling);
    }

    [Fact]
    public void MissileTemplateCatalog_NewMissileFieldsAndMoveKinds_ShouldMatchLegacyServer()
    {
        var missileCatalog = new MissileTemplateCatalog(CreateOptions());

        // MoveKind=2 -> Random (jx_linux_y KMissle::StepFly 0x0808cbd0)
        Assert.True(missileCatalog.TryGetMissile(85, out MissileTemplate? missile85));
        Assert.NotNull(missile85);
        Assert.Equal(MissileMoveKind.Random, missile85.MoveKind);
        Assert.True(missile85.CollideVanish);
        Assert.Equal(1, missile85.PierceCount);
        Assert.True(missile85.CanDestroy);
        Assert.True(missile85.CanSlow);
        Assert.False(missile85.CollideFollowTarget);
        Assert.Equal(0, missile85.ResponseSkillId);
        Assert.Equal(32, missile85.DamageRadiusMps);

        // MoveKind=3 -> Circle, ColVanish=0 -> PierceCount=0 (unlimited), DamageRadiusCells=3 -> 96 mps
        Assert.True(missileCatalog.TryGetMissile(190, out MissileTemplate? missile190));
        Assert.NotNull(missile190);
        Assert.Equal(MissileMoveKind.Circle, missile190.MoveKind);
        Assert.False(missile190.CollideVanish);
        Assert.Equal(0, missile190.PierceCount);
        Assert.True(missile190.CanDestroy);
        Assert.True(missile190.CanSlow);
        Assert.True(missile190.CollideFollowTarget);
        Assert.Equal(3, missile190.DamageRadiusCells);
        Assert.Equal(96, missile190.DamageRadiusMps);

        // MoveKind=4 -> Helix
        Assert.True(missileCatalog.TryGetMissile(261, out MissileTemplate? missile261));
        Assert.NotNull(missile261);
        Assert.Equal(MissileMoveKind.Helix, missile261.MoveKind);
        Assert.False(missile261.CollideVanish);
        Assert.Equal(0, missile261.PierceCount);
        Assert.True(missile261.CollideFollowTarget);

        // MoveKind=100 -> RollBack (151 ColVanish=false -> PierceCount=0; 152 ColVanish=true -> PierceCount=1)
        Assert.True(missileCatalog.TryGetMissile(151, out MissileTemplate? missile151));
        Assert.NotNull(missile151);
        Assert.Equal(MissileMoveKind.RollBack, missile151.MoveKind);
        Assert.False(missile151.CollideVanish);
        Assert.Equal(0, missile151.PierceCount);

        Assert.True(missileCatalog.TryGetMissile(152, out MissileTemplate? missile152));
        Assert.NotNull(missile152);
        Assert.Equal(MissileMoveKind.RollBack, missile152.MoveKind);
        Assert.True(missile152.CollideVanish);
        Assert.Equal(1, missile152.PierceCount);
    }

    [Fact]
    public void SkillTemplateCatalog_WeaponSkillMappingsAndConstRules_ShouldLoadAllSixSectionsAndResolveBasicWeaponSkills()
    {
        ISkillTemplateCatalog skillCatalog = new SkillTemplateCatalog(CreateOptions(), NullLogger<SkillTemplateCatalog>.Instance);

        Assert.Equal(32, skillCatalog.WeaponSkillMappings.Count);
        Assert.Equal(53, skillCatalog.GetBasicWeaponSkillId(1));  // Tay không
        Assert.Equal(1, skillCatalog.GetBasicWeaponSkillId(8));   // Trường thương 1
        Assert.Equal(2, skillCatalog.GetBasicWeaponSkillId(20));  // Ám khí
        Assert.Equal(53, skillCatalog.GetBasicWeaponSkillId(999));

        AttributeConstRulesTemplate? rules = skillCatalog.AttributeConstRules;
        Assert.NotNull(rules);

        Assert.NotNull(rules.IgnoreSkillRule);
        Assert.Equal(724, rules.IgnoreSkillRule.StateSkillId);
        Assert.Equal(15, rules.IgnoreSkillRule.DurationFrames);
        Assert.Equal(12, rules.IgnoreSkillRule.EligibleCurseSkillIds.Count);

        Assert.NotNull(rules.ReturnSkillRule);
        Assert.Equal(725, rules.ReturnSkillRule.StateSkillId);
        Assert.Equal(25, rules.ReturnSkillRule.DurationFrames);
        Assert.Equal(46, rules.ReturnSkillRule.EligibleCurseSkillIds.Count);

        Assert.NotNull(rules.AutoReplyExcludedSkillIds);
        Assert.Equal([1181, 1561, 1562, 1563, 1564, 1565, 1185], rules.AutoReplyExcludedSkillIds);

        Assert.NotNull(rules.AutoAttackExcludedSkillIds);
        Assert.Equal([389, 1181, 1185], rules.AutoAttackExcludedSkillIds);

        Assert.NotNull(rules.StaticMagicShieldClearSkillIds);
        Assert.Equal([721], rules.StaticMagicShieldClearSkillIds);

        Assert.NotNull(rules.HideRule);
        Assert.Equal(70, rules.HideRule.Alpha);
        Assert.Equal([713, 1235, 1258, 1267], rules.HideRule.ClearSkillIds);
    }

    [Fact]
    public void PlayerTemplateCatalog_Faction10HuaShan_ShouldContainCanonicalSeriesCampAndFifteenSkills()
    {
        var playerCatalog = new PlayerTemplateCatalog(CreateOptions(), NullLogger<PlayerTemplateCatalog>.Instance);

        Assert.Equal(11, playerCatalog.AllFactions.Count);
        Assert.True(playerCatalog.TryGetFactionById(10, out FactionTemplate? huaShan));
        Assert.NotNull(huaShan);
        Assert.Equal("hoa-son-phai", huaShan.Code);
        Assert.Equal((byte)2, huaShan.Series);
        Assert.Equal((byte)3, huaShan.Camp);
        Assert.Equal(new byte[] { 0, 1 }, huaShan.AllowedGenders);
        Assert.Equal(
            [1347, 1372, 1349, 1374, 1350, 1375, 1351, 1376, 1354, 1378, 1355, 1379, 1358, 1360, 1380],
            huaShan.SkillIds);

        var fallbackOptions = Options.Create(new GameAssetsOptions
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), "NonExistentData_" + Guid.NewGuid().ToString("N")),
            ArtifactsDirectory = Path.Combine(Path.GetTempPath(), "NonExistentArtifacts_" + Guid.NewGuid().ToString("N")),
            PreferBinarySnapshots = false,
        });
        var fallbackCatalog = new PlayerTemplateCatalog(fallbackOptions, NullLogger<PlayerTemplateCatalog>.Instance);
        Assert.Equal(11, fallbackCatalog.AllFactions.Count);
        Assert.True(fallbackCatalog.TryGetFactionById(10, out FactionTemplate? fallbackHuaShan));
        Assert.NotNull(fallbackHuaShan);
        Assert.Equal("faction-10", fallbackHuaShan.Code);
    }

    [Fact]
    public void Skill1349AndEffectScalingParam2_WhenLoadedFromCatalog_ShouldEvaluateHuaShanManaPenaltyAndZeroDefaultParam2()
    {
        var skillCatalog = new SkillTemplateCatalog(CreateOptions(), NullLogger<SkillTemplateCatalog>.Instance);

        Assert.True(skillCatalog.TryGetSkill(1349, out SkillTemplate? skill1349));
        Assert.NotNull(skill1349);

        SkillEffectScalingTemplate? manaMaxEffect = skill1349.EffectScaling.FirstOrDefault(
            e => string.Equals(e.AttributeCode, "manamax_v", StringComparison.Ordinal));
        Assert.NotNull(manaMaxEffect);
        Assert.Equal(89, manaMaxEffect.AttributeId);
        Assert.NotNull(manaMaxEffect.Param0);
        Assert.Equal(-20000, manaMaxEffect.Param0.Evaluate(20));

        SkillEffectScalingTemplate? manaMaxYanEffect = skill1349.EffectScaling.FirstOrDefault(
            e => string.Equals(e.AttributeCode, "manamax_yan_v", StringComparison.Ordinal));
        Assert.NotNull(manaMaxYanEffect);
        Assert.Equal(235, manaMaxYanEffect.AttributeId);
        Assert.NotNull(manaMaxYanEffect.Param0);
        Assert.Equal(-20000, manaMaxYanEffect.Param0.Evaluate(20));

        SkillEffectScalingTemplate? deadlyStrikeEffect = skill1349.EffectScaling.FirstOrDefault(
            e => string.Equals(e.AttributeCode, "deadlystrikeenhance_p", StringComparison.Ordinal));
        Assert.NotNull(deadlyStrikeEffect);
        Assert.Equal(146, deadlyStrikeEffect.AttributeId);
        Assert.NotNull(deadlyStrikeEffect.Param0);
        Assert.Equal(26, deadlyStrikeEffect.Param0.Evaluate(20));

        // Skill 16 meleedamagereturn_p omits param2 in JSON -> must default to BaseValue=0, PerLevelValue=0 (not 15, 8)
        Assert.True(skillCatalog.TryGetSkill(16, out SkillTemplate? skill16));
        Assert.NotNull(skill16);
        SkillEffectScalingTemplate? returnEffect = skill16.EffectScaling.FirstOrDefault(
            e => string.Equals(e.AttributeCode, "meleedamagereturn_p", StringComparison.Ordinal));
        Assert.NotNull(returnEffect);
        Assert.NotNull(returnEffect.Param2);
        Assert.Equal(0, returnEffect.Param2.BaseValue);
        Assert.Equal(0, returnEffect.Param2.PerLevelValue);
    }

    [Fact]
    public void MagicAttributeRegistry_WhenLoadedFromServer1_ShouldUseCanonicalLegacyServerAttributeIds()
    {
        MagicAttributeRegistry registry = MagicAttributeRegistry.Load(WorkbenchPaths.Server1RootPath);

        Assert.Equal(21, registry.GetId("missle_missrate"));
        Assert.Equal(22, registry.GetId("missle_hitcount"));
        Assert.Equal(59, registry.GetId("physicsdamage_v"));
        Assert.Equal(75, registry.GetId("seriesdamage_p"));
        Assert.Equal(85, registry.GetId("lifemax_v"));
        Assert.Equal(114, registry.GetId("allres_p"));
        Assert.Equal(149, registry.GetId("manashield_p"));
        Assert.Equal(191, registry.GetId("ignoreskill_p"));
        Assert.Equal(192, registry.GetId("returnskill_p"));
        Assert.Equal(260, registry.GetId("addskilldamage1"));
        Assert.Equal(265, registry.GetId("addskilldamage6"));

        Assert.Equal("lifemax_v", registry.GetCode(85));
        Assert.Equal("allres_p", registry.GetCode(114));
        Assert.Equal("manashield_p", registry.GetCode(149));
    }

    [Fact]
    public void EquipmentAbrasionConsts_IndestructibleAndDurabilityIds_ShouldMatchCanonicalMagicAffixesJson()
    {
        string magicAffixesPath = Path.Combine(TestWorkspacePaths.DataDirectory, "items", "magic-affixes.items.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(magicAffixesPath));
        JsonElement affixes = doc.RootElement.GetProperty("magicAffixes");

        List<int> indestructibleIds = [];
        foreach (JsonElement affix in affixes.EnumerateArray())
        {
            string? code = affix.GetProperty("attributeCode").GetString();
            int id = affix.GetProperty("attributeId").GetInt32();
            if (string.Equals(code, "indestructible_b", StringComparison.Ordinal))
            {
                indestructibleIds.Add(id);
            }
        }

        Assert.NotEmpty(indestructibleIds);
        Assert.All(indestructibleIds, id => Assert.Equal(43, id));

        MagicAttributeRegistry registry = MagicAttributeRegistry.Load(WorkbenchPaths.Server1RootPath);
        Assert.Equal(43, registry.GetId("indestructible_b"));
        Assert.Equal(38, registry.GetId("requiresex"));
        Assert.Equal(31, registry.GetId("durability_v"));

        Assert.Equal((short)43, EquipmentAbrasionConsts.IndestructibleAttributeId);
        Assert.Equal((short)31, EquipmentAbrasionConsts.DurabilityAttributeId);
    }

    [Fact]
    public void SkillTemplateCatalog_CreateDefaultSkills_ShouldUseCanonicalAttributeIdsAndTrimmedCodes()
    {
        IReadOnlyList<SkillTemplate> defaults = SkillTemplateCatalog.CreateDefaultSkills();

        Assert.All(defaults, skill => Assert.Equal(skill.Code.Trim(), skill.Code));
        SkillTemplate? npcStrike = defaults.FirstOrDefault(s => s.Id == 29);
        Assert.NotNull(npcStrike);
        Assert.Equal("npc-melee-strike", npcStrike.Code);

        var expectedIdsByCode = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["physicsdamage_v"] = 59,
            ["colddamage_v"] = 60,
            ["firedamage_v"] = 61,
            ["lightingdamage_v"] = 62,
            ["poisondamage_v"] = 63,
        };

        Assert.NotEmpty(defaults);
        int checkedEffects = 0;
        foreach (SkillTemplate skill in defaults)
        {
            foreach (SkillEffectScalingTemplate eff in skill.EffectScaling)
            {
                Assert.True(
                    expectedIdsByCode.TryGetValue(eff.AttributeCode, out int expectedId),
                    $"Unexpected fallback AttributeCode '{eff.AttributeCode}' on skill {skill.Id}.");
                Assert.Equal(expectedId, eff.AttributeId);
                checkedEffects++;
            }
        }
        Assert.True(checkedEffects > 0);
    }

    [Fact]
    public void ItemTemplateCatalog_CreateDefaultMagicAffixes_ShouldMatchCanonicalMagicAttributeRegistryIds()
    {
        MagicAttributeRegistry registry = MagicAttributeRegistry.Load(WorkbenchPaths.Server1RootPath);
        IReadOnlyList<MagicAffixTemplate> defaults = ItemTemplateCatalog.CreateDefaultMagicAffixes();

        Assert.NotEmpty(defaults);

        var byId = defaults.ToDictionary(a => a.Id);

        Assert.Equal(166, byId[5].AttributeId);
        Assert.Equal("attackratingenhance_v", byId[5].AttributeCode);

        Assert.Equal(150, byId[6].AttributeId);
        Assert.Equal("adddefense_v", byId[6].AttributeCode);

        Assert.Equal(104, byId[101].AttributeId);
        Assert.Equal("physicsres_p", byId[101].AttributeCode);

        Assert.Equal(101, byId[201].AttributeId);
        Assert.Equal("poisonres_p", byId[201].AttributeCode);

        Assert.Equal(105, byId[301].AttributeId);
        Assert.Equal("coldres_p", byId[301].AttributeCode);

        Assert.Equal(102, byId[401].AttributeId);
        Assert.Equal("fireres_p", byId[401].AttributeCode);

        Assert.Equal(103, byId[501].AttributeId);
        Assert.Equal("lightingres_p", byId[501].AttributeCode);

        Assert.Equal(114, byId[503].AttributeId);
        Assert.Equal("allres_p", byId[503].AttributeCode);

        foreach (MagicAffixTemplate affix in defaults)
        {
            Assert.Equal(registry.GetId(affix.AttributeCode), affix.AttributeId);
            Assert.Equal(registry.GetCode(affix.AttributeId), affix.AttributeCode);
        }
    }

    [Fact]
    public void ItemTemplateCatalog_AndItemGenerator_FallbackBaseAttributes_ShouldUseCanonicalAttributeIds()
    {
        MagicAttributeRegistry registry = MagicAttributeRegistry.Load(WorkbenchPaths.Server1RootPath);
        IReadOnlyList<ItemTemplate> defaultItems = ItemTemplateCatalog.CreateDefaultItems();

        Assert.NotEmpty(defaultItems);
        int checkedBaseAttrs = 0;
        foreach (ItemTemplate item in defaultItems)
        {
            foreach (ItemBaseAttributeTemplate baseAttr in item.BaseAttributes)
            {
                int canonicalId = registry.GetId(baseAttr.AttributeCode);
                Assert.True(canonicalId > 0, $"Fallback item '{item.Code}' uses non-canonical AttributeCode '{baseAttr.AttributeCode}'.");
                Assert.Equal(canonicalId, baseAttr.AttributeId);
                checkedBaseAttrs++;
            }
        }
        Assert.True(checkedBaseAttrs > 0);

        ItemTemplate sword = defaultItems.First(i => i.Code == "equip-melee-sword-lvl1");
        Assert.Contains(sword.BaseAttributes, a => a.AttributeId == 28 && a.AttributeCode == "weapondamagemin_v");
        Assert.Contains(sword.BaseAttributes, a => a.AttributeId == 29 && a.AttributeCode == "weapondamagemax_v");
        Assert.Contains(sword.BaseAttributes, a => a.AttributeId == 31 && a.AttributeCode == "durability_v");
        Assert.Contains(sword.Requirements, r => r.RequirementCode == "strength" && r.RequirementType == 32);
        Assert.Contains(sword.Requirements, r => r.RequirementCode == "level" && r.RequirementType == 36);

        ItemTemplate armor = defaultItems.First(i => i.Code == "equip-armor-cloth-lvl1");
        Assert.Contains(armor.BaseAttributes, a => a.AttributeId == 30 && a.AttributeCode == "armordefense_v");
        Assert.Contains(armor.BaseAttributes, a => a.AttributeId == 31 && a.AttributeCode == "durability_v");

        ItemTemplate hpPotion = defaultItems.First(i => i.Code == "kim-sang-duoc");
        Assert.Contains(hpPotion.BaseAttributes, a => a.AttributeId == 87 && a.AttributeCode == "life_v");

        ItemTemplate mpPotion = defaultItems.First(i => i.Code == "ngung-than-dan");
        Assert.Contains(mpPotion.BaseAttributes, a => a.AttributeId == 91 && a.AttributeCode == "mana_v");

        var generator = new ItemGenerator(new EmptyItemTemplateCatalogStub());

        GeneratedItemData fallbackEquip = generator.GenerateEquipment(
            detailType: 9,
            particularType: 0,
            series: ElementSeries.Metal,
            level: 2,
            magicLevelThresholds: []);
        Assert.Contains(fallbackEquip.Template.BaseAttributes, a => a.AttributeId == 30 && a.AttributeCode == "armordefense_v");

        GeneratedItemData fallbackHpMed = generator.GenerateMedicine(detailType: 9, particularType: 1, level: 1);
        Assert.Contains(fallbackHpMed.Template.BaseAttributes, a => a.AttributeId == 87 && a.AttributeCode == "life_v");

        GeneratedItemData fallbackMpMed = generator.GenerateMedicine(detailType: 9, particularType: 2, level: 1);
        Assert.Contains(fallbackMpMed.Template.BaseAttributes, a => a.AttributeId == 91 && a.AttributeCode == "mana_v");
    }
}
