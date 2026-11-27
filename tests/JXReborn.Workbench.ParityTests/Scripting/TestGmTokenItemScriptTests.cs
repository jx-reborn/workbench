using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using JXReborn.GameAssets.Items;
using JXReborn.GameAssets.PlayerTemplates;
using JXReborn.GameAssets.WorldMaps;
using JXReborn.Primitives.Characters;
using JXReborn.Primitives.Encodings;
using JXReborn.Primitives.Items;
using JXReborn.Primitives.WorldZones;
using JXReborn.Scripts.Dialogs;
using JXReborn.Scripts.Items;
using JXReborn.TestBase.Fakes;
using JXReborn.TestBase.Fixtures;
using JXReborn.TestBase.SimulationHarnesses;
using JXReborn.Workbench.Scripts.Items;
using JXReborn.ZoneEngine.Items;
using JXReborn.ZoneEngine.PlayerSessions;
using JXReborn.ZoneEngine.Scripting;
using JXReborn.ZoneEngine.WorldZones;
using Xunit;

namespace JXReborn.ZoneEngine.Tests.Scripting;

public sealed class TestGmTokenItemScriptTests
{
    private static (ZoneSimulationHarness Harness, ZoneExecutionContext Context) CreateHarness(
        bool enableGmToken,
        IPlayerTemplateCatalog? playerCatalog = null)
    {
        var options = new ZoneSimulationHarnessOptions();
        if (playerCatalog is not null)
        {
            options.Assets = new ZoneAssetServices { PlayerTemplateCatalog = playerCatalog };
        }

        var harness = new ZoneSimulationHarness(options);
        MapZoneWorker worker = harness.Workers.First();
        worker.ExecutionContext.Options.EnableTestGmToken = enableGmToken;
        worker.ExecutionContext.Options.AutoGrantGmTokenOnLogin = enableGmToken;
        worker.ExecutionContext.Options.TestGmTokenItemCode = TestGmTokenItemScript.GmTokenItemCode;
        worker.ExecutionContext.Options.TestGmTokenScriptCode = TestGmTokenItemScript.GmTokenScriptPath;
        return (harness, worker.ExecutionContext);
    }

    private static PlayerZoneState CreatePlayer(
        byte level = 1,
        int money = 0,
        ElementSeries series = ElementSeries.Earth,
        CharacterGender gender = CharacterGender.Male,
        ushort maxStamina = 100) =>
        new(
            runtimeNpcId: 1001u,
            accountId: Guid.NewGuid(),
            characterId: Guid.NewGuid(),
            leaseEpoch: 1L,
            roleNameUnicode: "GmTokenTester",
            gender: gender,
            series: series,
            level: level,
            experience: 0,
            leadExperience: 0,
            mapId: 53,
            currentMpsX: 51200,
            currentMpsY: 102400,
            currentLife: 90,
            maxLife: 100,
            currentMana: 20,
            maxMana: 40,
            currentStamina: maxStamina,
            maxStamina: maxStamina,
            strength: 20,
            dexterity: 15,
            vitality: 25,
            energy: 20,
            lucky: 0,
            unallocatedAttributePoints: 0,
            unallocatedSkillPoints: 0,
            inventoryMoney: money,
            repositoryMoney: 0,
            currentFactionId: 255,
            firstFactionId: 255,
            factionJoinCount: 0,
            initialTick: 1L,
            outboundSink: new PlayerOutboundEventRecorder());

    private static (ZoneScriptItemContext ItemCtx, PlayerItemSlot TokenSlot) CreateGmTokenContext(
        PlayerZoneState player,
        ZoneExecutionContext execCtx,
        bool addToBag = false)
    {
        var tpl = new ItemTemplate(
            Id: 4779,
            Code: TestGmTokenItemScript.GmTokenItemCode,
            Name: "Cẩm nang Tân Thủ",
            Genre: ItemGenreKind.MagicScript,
            Quality: ItemQualityKind.Normal,
            DetailType: 1,
            ParticularType: TestGmTokenItemScript.GmTokenParticularType,
            Level: 1,
            ElementSeries: ElementSeries.Metal,
            GridWidthCells: 1,
            GridHeightCells: 1,
            ObjDataId: 38,
            AppearanceResId: 0,
            PriceGold: 100,
            MaxStackCount: 1,
            BaseAttributes: [],
            Requirements: []);

        var slot = new PlayerItemSlot(
            runtimeItemId: player.Inventory.AllocateRuntimeItemId(),
            domainItemId: Guid.NewGuid(),
            template: tpl,
            genre: ItemGenreKind.MagicScript,
            detailType: 1,
            particularType: TestGmTokenItemScript.GmTokenParticularType,
            level: 1,
            series: ElementSeries.Metal,
            quality: ItemQualityKind.Normal,
            randomSeed: 0,
            generatorVersion: 1,
            luck: 0,
            currentDurability: 0,
            maxDurability: 0,
            stackCount: 1,
            containerPlace: ItemContainerPlace.EquipRoom,
            gridX: 0,
            gridY: 0,
            bindState: ItemBindState.BoundPermanent);

        if (addToBag)
        {
            _ = player.Inventory.TryAddOrAutoPlaceItem(slot, ItemContainerPlace.EquipRoom, 0, 0, player);
        }

        return (new ZoneScriptItemContext(player, slot, targetNpcId: 0, execCtx), slot);
    }

    private static async Task ExecutePathAsync(
        TestGmTokenItemScript script,
        ZoneScriptItemContext itemCtx,
        ZoneScriptDialogContext dialogCtx,
        PlayerZoneState player,
        params int[] optionPath)
    {
        _ = await script.OnUseItemAsync(itemCtx);
        Assert.True(player.TryGetFeatureSlot(PlayerDialogFeatureSlot.SlotKey, out PlayerDialogFeatureSlot? dialogSlot));
        foreach (int idx in optionPath)
        {
            Assert.True(dialogSlot!.TrySnapshotAndClearOptionCallback(idx, out var cb), $"Option index {idx} not found.");
            await cb!(dialogCtx);
        }
    }

    [Fact]
    public async Task OnUseItemAsync_WhenEnableTestGmTokenIsFalse_ShouldBeNoOpAndOpenNoDialog()
    {
        var (_, execCtx) = CreateHarness(enableGmToken: false);
        PlayerZoneState player = CreatePlayer();
        var (itemCtx, _) = CreateGmTokenContext(player, execCtx);
        var script = new TestGmTokenItemScript();

        ItemUsageResult res = await script.OnUseItemAsync(itemCtx);

        Assert.Equal(ItemUsageResult.KeepItem, res);
        Assert.False(player.TryGetFeatureSlot(PlayerDialogFeatureSlot.SlotKey, out _));
    }

    [Fact]
    public async Task OnUseItemAsync_CharacterEconomyAndDurabilityAtomics_ShouldMutateSingleAxesOnly()
    {
        var (harness, execCtx) = CreateHarness(enableGmToken: true);
        PlayerZoneState player = CreatePlayer(level: 1, money: 0);

        var weaponTpl = new ItemTemplate(
            Id: 1,
            Code: "starter-sword",
            Name: "Thiết Kiếm",
            Genre: ItemGenreKind.Equipment,
            Quality: ItemQualityKind.Normal,
            DetailType: 0,
            ParticularType: 0,
            Level: 1,
            ElementSeries: ElementSeries.Metal,
            GridWidthCells: 1,
            GridHeightCells: 3,
            ObjDataId: 1,
            AppearanceResId: 1,
            PriceGold: 100,
            MaxStackCount: 1,
            BaseAttributes: [],
            Requirements: []);
        var weaponSlot = new PlayerItemSlot(
            runtimeItemId: player.Inventory.AllocateRuntimeItemId(),
            domainItemId: Guid.NewGuid(),
            template: weaponTpl,
            genre: ItemGenreKind.Equipment,
            detailType: 0,
            particularType: 0,
            level: 1,
            series: ElementSeries.Metal,
            quality: ItemQualityKind.Normal,
            randomSeed: 0,
            generatorVersion: 1,
            luck: 0,
            currentDurability: 20,
            maxDurability: 20,
            stackCount: 1,
            containerPlace: ItemContainerPlace.EquipRoom,
            gridX: 0,
            gridY: 0);
        _ = player.Inventory.TryAddOrAutoPlaceItem(weaponSlot, ItemContainerPlace.EquipRoom, 0, 0, player);

        var (itemCtx, _) = CreateGmTokenContext(player, execCtx);
        var dialogCtx = new ZoneScriptDialogContext(player, sourceNpc: null, execCtx);
        var script = new TestGmTokenItemScript();

        // [0] Character -> [0] Level -> [1] Level 10
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 0, 0, 1);
        Assert.Equal(10, player.Level);
        Assert.Equal(45, player.UnallocatedAttributePoints);
        Assert.Equal(9, player.UnallocatedSkillPoints);
        Assert.Equal(0, player.InventoryMoney); // No side-effect on economy

        // [2] Economy -> [0] +50,000 silver
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 2, 0);
        Assert.Equal(50_000, player.InventoryMoney);
        Assert.Equal(20, weaponSlot.CurrentDurability); // No side-effect on durability

        // [3] Items -> [3] Durability -> [0] Degrade -5
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 3, 3, 0);
        Assert.Equal(15, weaponSlot.CurrentDurability);

        // [3] Items -> [3] Durability -> [1] Set durability = 1
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 3, 3, 1);
        Assert.Equal(1, weaponSlot.CurrentDurability);
    }

    [Fact]
    public async Task OnUseItemAsync_FactionSkillMaterialsAndCombatAtomics_ShouldComposeEndgameAndCraftingStates()
    {
        var (harness, execCtx) = CreateHarness(enableGmToken: true);
        PlayerZoneState player = CreatePlayer(level: 1, money: 0);
        var (itemCtx, _) = CreateGmTokenContext(player, execCtx);
        var dialogCtx = new ZoneScriptDialogContext(player, sourceNpc: null, execCtx);
        var script = new TestGmTokenItemScript();

        // Level 80: [0, 0, 5]
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 0, 0, 5);
        // Camp Free: [1, 0, 4]
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 1, 0, 4);
        // +500 Repute: [1, 3, 0]; +3M LeadExp: [1, 3, 1]; Transmigration=1: [1, 4, 0]
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 1, 3, 0);
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 1, 3, 1);
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 1, 4, 0);
        // Learn Skill 14 Lv5: [1, 2, 0]
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 1, 2, 0);
        // Materials: 3x Huyen Tinh [3, 2, 0], Raw Ore [3, 2, 1], Attr Ore [3, 2, 2], Nhac Vuong Kiem [3, 2, 3], Sat Thu Gian [3, 2, 4]
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 3, 2, 0);
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 3, 2, 1);
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 3, 2, 2);
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 3, 2, 3);
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 3, 2, 4);
        // Equipment: Blue Ring [3, 1, 3], Purple Sword [3, 1, 4]
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 3, 1, 3);
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 3, 1, 4);
        // Combat: FightMode=1 [4, 0], PkValue=10 [4, 3]
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 4, 0);
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 4, 3);

        Assert.Equal(80, player.Level);
        Assert.Equal(EntityCamp.Free, player.BaseCamp);
        Assert.Equal(EntityCamp.Free, player.CurrentCamp);
        Assert.Equal(500, player.Repute);
        Assert.Equal(3_000_000, player.LeadExperience);
        Assert.Equal(1, player.GetTaskValue(99));
        Assert.True(player.TryGetLearnedSkill(14, out var learnedSkill));
        Assert.Equal((ushort)5, learnedSkill.BaseLevel);
        Assert.Equal((byte)1, player.FightMode);
        Assert.Equal((sbyte)10, player.PkValue);
        Assert.Equal(3, player.Inventory.GetBagItems().Count(static i => i.Genre == ItemGenreKind.MagicScript && i.ParticularType == 147 && i.Level == 3));
        Assert.Contains(player.Inventory.GetBagItems(), static i => i.Genre == ItemGenreKind.Equipment && i.DetailType == 3 && i.HasAnyMagicAffix);
        Assert.Contains(player.Inventory.GetBagItems(), static i => i.Genre == ItemGenreKind.MagicScript && i.ParticularType == 149);
        Assert.Contains(player.Inventory.GetBagItems(), static i => i.Genre == ItemGenreKind.Equipment && i.Quality == ItemQualityKind.Purple);
        Assert.Contains(player.Inventory.GetBagItems(), static i => i.Genre == ItemGenreKind.MagicScript && i.ParticularType == 200 && i.MagicAttributes[0].AttributeId == 126);
        Assert.Contains(player.Inventory.GetBagItems(), static i => i.Genre == ItemGenreKind.QuestKey && i.DetailType == 195);
        Assert.Contains(player.Inventory.GetBagItems(), static i => i.Genre == ItemGenreKind.MagicScript && i.ParticularType == 400 && i.Level == 90);
    }

    [Fact]
    public async Task OnUseItemAsync_BagBoundaryFillAndClear_ShouldReachZeroFreeCellsAndKeepGmTokenOnClear()
    {
        var (harness, execCtx) = CreateHarness(enableGmToken: true);
        PlayerZoneState player = CreatePlayer(level: 10, money: 0);
        var (itemCtx, tokenSlot) = CreateGmTokenContext(player, execCtx, addToBag: true);
        var dialogCtx = new ZoneScriptDialogContext(player, sourceNpc: null, execCtx);
        var script = new TestGmTokenItemScript();

        // [3] Items -> [4] Bag Boundary -> [1] Fill Bag to 0 free cells
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 3, 4, 1);
        Assert.Equal(0, dialogCtx.FreeBagCellCount);

        // [3] Items -> [4] Bag Boundary -> [0] Clear Bag keeping GM Token
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 3, 4, 0);
        PlayerItemSlot[] remaining = player.Inventory.GetBagItems().ToArray();
        Assert.Single(remaining);
        Assert.Equal(tokenSlot.RuntimeItemId, remaining[0].RuntimeItemId);
    }

    [Fact]
    public async Task OnUseItemAsync_TestWeaponsMenu_ShouldGrantAllNineWeaponTypes()
    {
        var (harness, execCtx) = CreateHarness(enableGmToken: true);
        PlayerZoneState player = CreatePlayer(level: 10, money: 0);
        var (itemCtx, _) = CreateGmTokenContext(player, execCtx);
        var dialogCtx = new ZoneScriptDialogContext(player, sourceNpc: null, execCtx);
        var script = new TestGmTokenItemScript();

        // [3] Items -> [1] Equipment -> [5] Vũ khí thử nghiệm (9 loại) -> [0..8]
        for (int weaponIdx = 0; weaponIdx < 9; weaponIdx++)
        {
            await ExecutePathAsync(script, itemCtx, dialogCtx, player, 3, 1, 5, weaponIdx);
        }

        // [9] Quay lại returns to equipment menu cleanly without throwing
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 3, 1, 5, 9);

        PlayerItemSlot[] bagItems = player.Inventory.GetBagItems().ToArray();
        Assert.Equal(9, bagItems.Length);

        (short Detail, short Particular)[] expectedWeapons =
        [
            (0, 0), // Kiếm
            (0, 1), // Đao
            (0, 2), // Côn
            (0, 3), // Thương
            (0, 4), // Chùy
            (0, 5), // Song Đao
            (1, 0), // Phi Tiêu
            (1, 1), // Phi Đao
            (1, 2), // Tụ Tiễn
        ];

        foreach ((short detail, short particular) in expectedWeapons)
        {
            Assert.Contains(
                bagItems,
                slot => slot.Genre == ItemGenreKind.Equipment &&
                        slot.DetailType == detail &&
                        slot.ParticularType == particular &&
                        slot.Level == 1 &&
                        slot.ContainerPlace == ItemContainerPlace.EquipRoom);
        }
    }

    [Fact]
    public async Task OnUseItemAsync_LearnAllCurrentFactionSkills_ShouldLearnFactionSkillsAtLevel1()
    {
        int[] shaolinBaseSkills = [10, 14, 4, 6, 8, 15, 16, 20, 11, 19, 271, 21, 273];
        int[] shaolinAdvancedSkills = [318, 319, 321, 709];
        var shaolinTemplate = new FactionTemplate(
            Id: 0,
            Code: "thieu-lam-phai",
            Name: "Thiếu Lâm phái",
            Series: (byte)ElementSeries.Metal,
            Camp: 1,
            AllowedGenders: [0],
            SkillIds: shaolinBaseSkills,
            Titles: [new FactionRankTitleTemplate(1, "Tảo Địa Tăng")]);

        var catalog = new StubFactionPlayerTemplateCatalog(shaolinTemplate);
        var (_, execCtx) = CreateHarness(enableGmToken: true, playerCatalog: catalog);
        PlayerZoneState player = CreatePlayer(level: 120, series: ElementSeries.Metal);
        var (itemCtx, _) = CreateGmTokenContext(player, execCtx);
        var dialogCtx = new ZoneScriptDialogContext(player, sourceNpc: null, execCtx);
        var script = new TestGmTokenItemScript();

        // Guard: before joining any faction, [1] Faction -> [5] Join Faction -> [12] Learn All is a no-op
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 1, 5, 12);
        Assert.False(player.TryGetLearnedSkill(10, out _));

        // Join Thiếu Lâm (0): [1, 5, 0]
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 1, 5, 0);
        Assert.Equal((byte)0, player.CurrentFactionId);

        // Learn all current faction skills (base + Lv90 + Lv120) at level 1: [1, 5, 12]
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 1, 5, 12);

        foreach (int skillId in shaolinBaseSkills.Concat(shaolinAdvancedSkills))
        {
            Assert.True(player.TryGetLearnedSkill(skillId, out var learned), $"Skill {skillId} was not learned.");
            Assert.Equal((ushort)1, learned.BaseLevel);
        }
    }

    [Fact]
    public async Task GmToken_EnsureLevel_FemaleCharacter_ShouldUseFemaleStaminaPerLevel()
    {
        var (harness, execCtx) = CreateHarness(enableGmToken: true);
        PlayerZoneState player = CreatePlayer(
            level: 1,
            series: ElementSeries.Water,
            gender: CharacterGender.Female,
            maxStamina: 180);
        var (itemCtx, _) = CreateGmTokenContext(player, execCtx);
        var dialogCtx = new ZoneScriptDialogContext(player, sourceNpc: null, execCtx);
        var script = new TestGmTokenItemScript();

        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 0, 0, 6);

        Assert.Equal((byte)120, player.Level);
        Assert.Equal((ushort)1132, player.MaxStamina);
    }

    [Fact]
    public async Task GmToken_AllMenus_ShouldMatchPackLegacyCamNangGmLua_1To1()
    {
        var (harness, execCtx) = CreateHarness(enableGmToken: true);
        PlayerZoneState player = CreatePlayer(level: 10);
        var recorder = (PlayerOutboundEventRecorder)player.OutboundSink;
        var (itemCtx, _) = CreateGmTokenContext(player, execCtx);
        var dialogCtx = new ZoneScriptDialogContext(player, sourceNpc: null, execCtx);
        var script = new TestGmTokenItemScript();

        Dictionary<string, LegacyLuaMenu> luaMenus = ParsePackLegacyCamNangGmMenus();
        Assert.NotEmpty(luaMenus);

        (int[] Path, string LuaFunc, int ExpectedCount)[] menuSpecs =
        [
            ([], "GMT_OpenRootMenu", 9),
            ([0], "GMT_MenuCharacter", 6),
            ([0, 0], "GMT_MenuLevel", 10),
            ([0, 1], "GMT_MenuAttributes", 5),
            ([0, 2], "GMT_MenuVitals", 3),
            ([0, 3], "GMT_MenuActivityPoints", 4),
            ([0, 4], "GMT_MenuRank", 3),
            ([1], "GMT_MenuFactionAndSkill", 7),
            ([1, 0], "GMT_MenuCamp", 6),
            ([1, 1], "GMT_MenuSkillPoints", 4),
            ([1, 2], "GMT_MenuLearnSkill", 6),
            ([1, 3], "GMT_MenuReputeLead", 4),
            ([1, 4], "GMT_MenuTransmigration", 3),
            ([1, 5], "GMT_MenuJoinFaction", 14),
            ([2], "GMT_MenuEconomy", 9),
            ([2, 7], "GMT_MenuExpandableBox", 4),
            ([3], "GMT_MenuItems", 8),
            ([3, 0], "GMT_MenuMedicines", 5),
            ([3, 1], "GMT_MenuEquipment", 7),
            ([3, 1, 5], "GMT_MenuTestWeapons", 10),
            ([3, 2], "GMT_MenuMaterials", 6),
            ([3, 3], "GMT_MenuDurability", 3),
            ([3, 4], "GMT_MenuBagBoundary", 3),
            ([3, 6], "GMT_MenuMounts", 7),
            ([4], "GMT_MenuCombat", 6),
            ([5], "GMT_MenuTeleport", 10),
            ([6], "GMT_MenuWorldEntities", 6),
            ([7], "GMT_MenuTasksAndDiag", 6),
        ];

        foreach ((int[] path, string luaFunc, int expectedCount) in menuSpecs)
        {
            recorder.SentDialogMenus.Clear();
            await ExecutePathAsync(script, itemCtx, dialogCtx, player, path);
            Assert.NotEmpty(recorder.SentDialogMenus);
            (string csPrompt, IReadOnlyList<string> csOptions, bool isRich) = recorder.SentDialogMenus[^1];
            Assert.False(isRich);
            Assert.Equal(expectedCount, csOptions.Count);

            Assert.True(
                luaMenus.TryGetValue(luaFunc, out LegacyLuaMenu? luaMenu),
                $"Legacy Lua menu '{luaFunc}' missing in pack_legacy_profiles.py.");
            Assert.Equal(luaMenu!.DeclaredCount, luaMenu.Options.Count);
            Assert.Equal(csOptions.Count, luaMenu.Options.Count);
            Assert.Equal(csPrompt, luaMenu.Prompt);

            for (int i = 0; i < csOptions.Count; i++)
            {
                Assert.DoesNotContain("/", csOptions[i], StringComparison.Ordinal);
                Assert.Equal(csOptions[i], luaMenu.Options[i]);
            }
        }
    }

    [Fact]
    public async Task GmToken_ExtendedCharacterEconomyMountsAndEntities_ShouldMatchCanonicalBehavior()
    {
        var (harness, execCtx) = CreateHarness(enableGmToken: true);
        PlayerZoneState player = CreatePlayer(level: 10);
        var (itemCtx, _) = CreateGmTokenContext(player, execCtx);
        var dialogCtx = new ZoneScriptDialogContext(player, sourceNpc: null, execCtx);
        var script = new TestGmTokenItemScript();

        // [0, 2, 0] restores vitals; [0, 2, 1] lowers HP to 10%
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 0, 2, 1);
        Assert.Equal(Math.Max(1, player.EffectiveMaxLife / 10), player.CurrentLife);
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 0, 2, 0);
        Assert.Equal(player.EffectiveMaxLife, player.CurrentLife);

        // [0, 3, 0] +1,000 FuYuan syncs both FuYuan and Task 151
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 0, 3, 0);
        Assert.Equal(1000, player.FuYuan);
        Assert.Equal(1000, player.GetTaskValue(151));

        // [0, 3, 1] BattleScore syncs BattleScore, Task 747, and Task 751
        await ExecuteNumberInputPathAsync(script, itemCtx, dialogCtx, player, 2500, 0, 3, 1);
        Assert.Equal(2500, player.BattleScore);
        Assert.Equal(2500, player.GetTaskValue(PlayerTaskConsts.SongJinBattleScore));
        Assert.Equal(2500, player.GetTaskValue(751));

        // [0, 3, 2] Honor syncs Honor and Task 2501
        await ExecuteNumberInputPathAsync(script, itemCtx, dialogCtx, player, 750, 0, 3, 2);
        Assert.Equal(750, player.Honor);
        Assert.Equal(750, player.GetTaskValue(2501));

        // [0, 4, 0] sets Rank via number input; [0, 4, 1] clears Rank
        await ExecuteNumberInputPathAsync(script, itemCtx, dialogCtx, player, 55, 0, 4, 0);
        Assert.Equal((byte)55, player.RankId);
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 0, 4, 1);
        Assert.Equal((byte)0, player.RankId);

        // [1, 2, 2] sets skill 14 to Lv12; [1, 2, 3] forgets & refunds 12; [1, 2, 4] washes remaining skills
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 1, 2, 2);
        await SubmitPendingNumberAsync(dialogCtx, player, 14);
        await SubmitPendingNumberAsync(dialogCtx, player, 12);
        Assert.True(player.TryGetLearnedSkill(14, out var skill14));
        Assert.Equal((ushort)12, skill14.BaseLevel);

        ushort spBeforeForget = player.UnallocatedSkillPoints;
        await ExecuteNumberInputPathAsync(script, itemCtx, dialogCtx, player, 14, 1, 2, 3);
        Assert.False(player.TryGetLearnedSkill(14, out _));
        Assert.Equal((ushort)(spBeforeForget + 12), player.UnallocatedSkillPoints);

        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 1, 2, 0);
        Assert.True(player.TryGetLearnedSkill(14, out var skill14Lv5));
        Assert.Equal((ushort)5, skill14Lv5.BaseLevel);
        ushort spBeforeBlockedWash = player.UnallocatedSkillPoints;
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 1, 2, 4);
        Assert.True(player.TryGetLearnedSkill(14, out var skill14StillLv5));
        Assert.Equal((ushort)5, skill14StillLv5.BaseLevel);
        Assert.Equal(spBeforeBlockedWash, player.UnallocatedSkillPoints);

        // Boost to Lv50 ([0, 0, 4]) then wash skills ([1, 2, 4])
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 0, 0, 4);
        ushort spBeforeWash = player.UnallocatedSkillPoints;
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 1, 2, 4);
        Assert.True(player.TryGetLearnedSkill(14, out var skill14AfterWash));
        Assert.Equal((ushort)0, skill14AfterWash.BaseLevel);
        Assert.Equal((ushort)(spBeforeWash + 5), player.UnallocatedSkillPoints);

        // [2, 7, 0..2] unlocks Expandable Box 1..3
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 2, 7, 0);
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 2, 7, 1);
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 2, 7, 2);
        Assert.Equal((byte)0b111, player.RepositoryExpandBoxMask);

        // [3, 6, 0..5] grants canonical mounts matching Legacy horse.txt
        for (int mountIdx = 0; mountIdx < 6; mountIdx++)
        {
            await ExecutePathAsync(script, itemCtx, dialogCtx, player, 3, 6, mountIdx);
        }

        PlayerItemSlot[] bagItems = player.Inventory.GetBagItems().ToArray();
        Assert.Contains(bagItems, static i => i.Genre == ItemGenreKind.Equipment && i.DetailType == 10 && i.ParticularType == 5 && i.Level == 6);
        Assert.Contains(bagItems, static i => i.Genre == ItemGenreKind.Equipment && i.DetailType == 10 && i.ParticularType == 5 && i.Level == 7);
        Assert.Contains(bagItems, static i => i.Genre == ItemGenreKind.Equipment && i.DetailType == 10 && i.ParticularType == 5 && i.Level == 8);
        Assert.Contains(bagItems, static i => i.Genre == ItemGenreKind.Equipment && i.DetailType == 10 && i.ParticularType == 5 && i.Level == 9);
        Assert.Contains(bagItems, static i => i.Genre == ItemGenreKind.Equipment && i.DetailType == 10 && i.ParticularType == 5 && i.Level == 10);
        Assert.Contains(bagItems, static i => i.Genre == ItemGenreKind.Equipment && i.DetailType == 10 && i.ParticularType == 6 && i.Level == 10);

        // [5, 7] sets canonical revival point on Map 53 -> RevID 19
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 5, 7);
        Assert.Equal((ushort)53, player.RevivalMapId);
        Assert.Equal(19, player.RevivalPointId);

        // [6, 0] spawns Template 586; [6, 1] spawns Template 576; [6, 3] spawns custom Template ID; [6, 4] spawns Template 413 Lv10
        MapZoneWorker zone = harness.GetZone(53);
        int npcsBeforeSpawn = zone.StaticNpcs.Count;
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 6, 0);
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 6, 1);
        await ExecuteNumberInputPathAsync(script, itemCtx, dialogCtx, player, 576, 6, 3);
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 6, 4);
        Assert.Equal(npcsBeforeSpawn + 4, zone.StaticNpcs.Count);
        Assert.Contains(zone.StaticNpcs.Values, static n => n.TemplateId == 586);
        Assert.Equal(2, zone.StaticNpcs.Values.Count(static n => n.TemplateId == 576));
        Assert.Contains(zone.StaticNpcs.Values, static n => n.TemplateId == 413 && n.Level == 10);
    }

    [Fact]
    public async Task GmToken_TeleportCoordsArbitraryTaskAndQuestItemByIds_ShouldExecuteSymmetrically()
    {
        SharedGameAssetsFixture assets = SharedGameAssetsFixture.Default;
        Assert.True(assets.MapCatalog.TryGetMapDescriptor(53, out MapDescriptor? map53) && map53 is not null);
        Assert.True(assets.MapCatalog.TryGetMapDescriptor(78, out MapDescriptor? map78) && map78 is not null);

        string repoRoot = FindWorkbenchRoot();
        string packerPath = Path.Combine(repoRoot, "qa", "scripts", "pack_legacy_profiles.py");
        string packerText = File.ReadAllText(packerPath, Encoding.UTF8);
        int fnStart = packerText.IndexOf("def build_camnang_gm_lua(", StringComparison.Ordinal);
        int fnEnd = packerText.IndexOf("def build_clean_login_lua(", fnStart, StringComparison.Ordinal);
        string luaBlock = packerText[fnStart..fnEnd];

        string[] requiredShortPrompts =
        [
            "Nhập Map ID (1..1000):",
            "Nhập Tile X (1..4096):",
            "Nhập Tile Y (1..8192):",
            "Nhập Task ID (0..65535):",
            "Nhập giá trị Task:",
            "Nhập Genre (0..10):",
            "Nhập Detail (0..1000):",
            "Nhập Particular (0..10000):",
            "Nhập Level (1..100):",
        ];
        foreach (string prompt in requiredShortPrompts)
        {
            byte[] tcvn3Bytes = Tcvn3Encoding.EncodeBytes(prompt);
            Assert.True(tcvn3Bytes.Length <= 31, $"Prompt '{prompt}' exceeds Legacy 31-byte AskClientForNumber limit ({tcvn3Bytes.Length}B).");
            Assert.Contains($"{{L(\"{prompt}\")}}", luaBlock, StringComparison.Ordinal);
        }

        var harnessOptions = new ZoneSimulationHarnessOptions
        {
            Assets = new ZoneAssetServices
            {
                MapCatalog = assets.MapCatalog,
                ItemTemplateCatalog = assets.ItemTemplateCatalog,
                PlayerTemplateCatalog = assets.PlayerTemplateCatalog,
            }
        };
        harnessOptions.InitialMaps.Add(map53!);
        harnessOptions.InitialMaps.Add(map78!);

        var harness = new ZoneSimulationHarness(harnessOptions);
        Assert.NotEmpty(harness.Workers);
        foreach (MapZoneWorker w in harness.Workers)
        {
            w.ExecutionContext.Options.EnableTestGmToken = true;
            w.ExecutionContext.Options.AutoGrantGmTokenOnLogin = true;
            w.ExecutionContext.Options.TestGmTokenItemCode = TestGmTokenItemScript.GmTokenItemCode;
            w.ExecutionContext.Options.TestGmTokenScriptCode = TestGmTokenItemScript.GmTokenScriptPath;
        }

        var charDto = new Contracts.Characters.CharacterDto(
            Id: Guid.NewGuid(),
            AccountId: Guid.NewGuid(),
            SlotIndex: 0,
            RoleNameUnicode: "GmTokenAdvTester",
            Gender: CharacterGender.Male,
            Series: ElementSeries.Earth,
            Level: 10,
            Experience: 0,
            LeadExperience: 0,
            MapId: 53,
            RevivalMapId: 53,
            RevivalPointId: 19,
            MapMpsX: 51200,
            MapMpsY: 102400,
            CurrentLife: 100,
            MaxLife: 100,
            CurrentMana: 40,
            MaxMana: 40,
            CurrentStamina: 100,
            MaxStamina: 100,
            Strength: 20,
            Dexterity: 15,
            Vitality: 25,
            Energy: 20,
            Lucky: 0,
            UnallocatedAttributePoints: 0,
            UnallocatedSkillPoints: 0,
            InventoryMoney: 0,
            RepositoryMoney: 0,
            CurrentFactionId: 255,
            FirstFactionId: 255,
            FactionJoinCount: 0,
            Skills: []);
        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(charDto);
        PlayerZoneState player = handle.GetZoneState()!;
        ZoneExecutionContext execCtx53 = harness.GetZone(53).ExecutionContext;
        var (itemCtx, tokenSlot) = CreateGmTokenContext(player, execCtx53);
        var dialogCtx = new ZoneScriptDialogContext(player, sourceNpc: null, execCtx53);
        var script = new TestGmTokenItemScript();

        // [5, 8]: Teleport to exact (MapId, TileX, TileY) -> (78, 1593, 3206)
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, 5, 8);
        await SubmitPendingNumberAsync(dialogCtx, player, 78);
        await SubmitPendingNumberAsync(dialogCtx, player, 1593);
        await SubmitPendingNumberAsync(dialogCtx, player, 3206);
        await harness.StepFramesAsync(2);
        Assert.Equal((ushort)78, player.MapId);
        Assert.Equal(1593 * 32, player.CurrentMpsX);
        Assert.Equal(3206 * 32, player.CurrentMpsY);
        Assert.Equal((byte)0, player.FightMode);

        // [7, 4]: Set arbitrary Task value -> Task(5) = 60*256+20 (15380), Task(17) = 514
        ZoneExecutionContext execCtx78 = harness.GetZone(78).ExecutionContext;
        var itemCtx78 = new ZoneScriptItemContext(player, tokenSlot, targetNpcId: 0, execCtx78);
        var dialogCtx78 = new ZoneScriptDialogContext(player, sourceNpc: null, execCtx78);
        await ExecutePathAsync(script, itemCtx78, dialogCtx78, player, 7, 4);
        await SubmitPendingNumberAsync(dialogCtx78, player, 5);
        await SubmitPendingNumberAsync(dialogCtx78, player, 15380);
        Assert.Equal(15380, player.GetTaskValue(5));

        await ExecutePathAsync(script, itemCtx78, dialogCtx78, player, 7, 4);
        await SubmitPendingNumberAsync(dialogCtx78, player, 17);
        await SubmitPendingNumberAsync(dialogCtx78, player, 514);
        Assert.Equal(514, player.GetTaskValue(17));

        // [3, 5]: Grant QuestKey item (4, 118, 0, 1) -> resolves real pack item luong-ngan-khoang (Detail=118, Particular=1)
        await ExecutePathAsync(script, itemCtx78, dialogCtx78, player, 3, 5);
        await SubmitPendingNumberAsync(dialogCtx78, player, 4);
        await SubmitPendingNumberAsync(dialogCtx78, player, 118);
        await SubmitPendingNumberAsync(dialogCtx78, player, 0);
        await SubmitPendingNumberAsync(dialogCtx78, player, 1);

        // [3, 5]: Grant MagicScript (6, 1, 400, 90) -> Sát Thủ Giản Lv90
        await ExecutePathAsync(script, itemCtx78, dialogCtx78, player, 3, 5);
        await SubmitPendingNumberAsync(dialogCtx78, player, 6);
        await SubmitPendingNumberAsync(dialogCtx78, player, 1);
        await SubmitPendingNumberAsync(dialogCtx78, player, 400);
        await SubmitPendingNumberAsync(dialogCtx78, player, 90);

        PlayerItemSlot[] bagItems = player.Inventory.GetBagItems().ToArray();
        Assert.Contains(bagItems, static i => i.Genre == ItemGenreKind.QuestKey && i.DetailType == 118 && i.Template?.Code == "luong-ngan-khoang");
        Assert.Contains(bagItems, static i => i.Genre == ItemGenreKind.MagicScript && i.DetailType == 1 && i.ParticularType == 400 && i.Level == 90);
    }

    [Fact]
    public async Task OnUseItemAsync_WhenProxyReconcileScratchTasksArePopulated_ShouldSyncMapPositionVitalsEconomyPkCampAndSkillsWithoutOpeningDialog()
    {
        SharedGameAssetsFixture assets = SharedGameAssetsFixture.Default;
        Assert.True(assets.MapCatalog.TryGetMapDescriptor(53, out MapDescriptor? desc53) && desc53 is not null);
        Assert.True(assets.MapCatalog.TryGetMapDescriptor(199, out MapDescriptor? desc199) && desc199 is not null);

        var harnessOpts = new ZoneSimulationHarnessOptions
        {
            Assets = new ZoneAssetServices
            {
                MapCatalog = assets.MapCatalog,
                PlayerTemplateCatalog = assets.PlayerTemplateCatalog,
                ItemTemplateCatalog = assets.ItemTemplateCatalog,
                SkillTemplateCatalog = assets.SkillTemplateCatalog,
            },
        };
        harnessOpts.InitialMaps.Add(desc53!);
        harnessOpts.InitialMaps.Add(desc199!);

        var harness = new ZoneSimulationHarness(harnessOpts);
        foreach (MapZoneWorker w in harness.Workers)
        {
            w.ExecutionContext.Options.EnableTestGmToken = true;
            w.ExecutionContext.Options.AutoGrantGmTokenOnLogin = true;
            w.ExecutionContext.Options.TestGmTokenItemCode = TestGmTokenItemScript.GmTokenItemCode;
            w.ExecutionContext.Options.TestGmTokenScriptCode = TestGmTokenItemScript.GmTokenScriptPath;
        }

        MapZoneWorker worker199 = harness.GetZone(199);
        var recorder = new PlayerOutboundEventRecorder(() => harness.TotalSteppedFrames);
        var dto = new JXReborn.Contracts.Characters.CharacterDto(
            Id: Guid.NewGuid(),
            AccountId: Guid.NewGuid(),
            SlotIndex: 0,
            RoleNameUnicode: "CoolantShadow",
            Gender: CharacterGender.Male,
            Series: ElementSeries.Water,
            Level: 10,
            Experience: 0,
            LeadExperience: 0,
            MapId: 199,
            RevivalMapId: 53,
            RevivalPointId: 19,
            MapMpsX: 52331,
            MapMpsY: 93322,
            CurrentLife: 50,
            MaxLife: 200,
            CurrentMana: 20,
            MaxMana: 100,
            CurrentStamina: 40,
            MaxStamina: 120,
            Strength: 25,
            Dexterity: 25,
            Vitality: 25,
            Energy: 25,
            Lucky: 0,
            UnallocatedAttributePoints: 0,
            UnallocatedSkillPoints: 0,
            InventoryMoney: 0,
            RepositoryMoney: 0,
            CurrentFactionId: CharacterConsts.NoFactionId,
            FirstFactionId: CharacterConsts.NoFactionId,
            FactionJoinCount: 0,
            Skills: [new JXReborn.Contracts.Characters.CharacterSkillDto(53, 1, 0)],
            FightMode: 1);

        SimulatedPlayerHandle handle = await harness.AttachSimulatedPlayerAsync(dto, leaseEpoch: 1, customSink: recorder);
        PlayerZoneState player = handle.GetZoneState()!;
        recorder.SentDialogMenus.Clear();

        // Populate reserved scratch tasks 65400.. (delivered over existing 0xAB TestSyncTaskValuesProtocolId = 0xFF01)
        const long targetExp = 5_000_000_000L;
        player.SetTaskValue(65400, 0x52454331); // "REC1" sentinel
        player.SetTaskValue(65401, 53);         // Target MapId (Ba Lăng Huyện)
        player.SetTaskValue(65402, 51200);      // Target MpsX (1600 * 32)
        player.SetTaskValue(65403, 102400);     // Target MpsY (3200 * 32)
        player.SetTaskValue(65404, 0);          // Target FightMode = 0
        player.SetTaskValue(65405, 120);        // Target Level = 120
        player.SetTaskValue(65406, unchecked((int)(targetExp & 0xFFFFFFFFL)));
        player.SetTaskValue(65407, unchecked((int)((ulong)targetExp >> 32)));
        player.SetTaskValue(65408, 1850);       // Target Life
        player.SetTaskValue(65409, 640);        // Target Mana
        player.SetTaskValue(65410, 320);        // Target Stamina
        player.SetTaskValue(65411, 876_543);    // Target InventoryMoney
        player.SetTaskValue(65412, 120_000);    // Target RepositoryMoney
        player.SetTaskValue(65413, 1);          // Target PkFlag
        player.SetTaskValue(65414, 3);          // Target PkValue
        player.SetTaskValue(65415, (int)EntityCamp.Justice);
        player.SetTaskValue(65416, (int)EntityCamp.Free);
        player.SetTaskValue(65417, 55);         // Target UnallocatedAttributePoints
        player.SetTaskValue(65418, 12);         // Target UnallocatedSkillPoints
        player.SetTaskValue(65420, 2);          // Learned skill count
        player.SetTaskValue(65421, 37);
        player.SetTaskValue(65422, 15);
        player.SetTaskValue(65423, 93);
        player.SetTaskValue(65424, 20);

        var (itemCtx, _) = CreateGmTokenContext(player, worker199.ExecutionContext);
        var script = new TestGmTokenItemScript();

        ItemUsageResult useResult = await script.OnUseItemAsync(itemCtx);
        await harness.StepFramesAsync(2);

        Assert.Equal(ItemUsageResult.KeepItem, useResult);
        Assert.Empty(recorder.SentDialogMenus);
        Assert.Equal(0, player.GetTaskValue(65400));

        Assert.True(harness.GetZone(53).TryGetPlayer(player.CharacterId, out PlayerZoneState? reconciled) && reconciled is not null);
        Assert.Equal((ushort)53, reconciled!.MapId);
        Assert.Equal(51200, reconciled.CurrentMpsX);
        Assert.Equal(102400, reconciled.CurrentMpsY);
        Assert.Equal((byte)0, reconciled.FightMode);
        Assert.Equal((byte)120, reconciled.Level);
        Assert.Equal(targetExp, reconciled.Experience);
        Assert.Equal(876_543, reconciled.InventoryMoney);
        Assert.Equal(120_000, reconciled.RepositoryMoney);
        Assert.Equal((JXReborn.Primitives.Combat.PlayerPkMode)1, reconciled.PkState);
        Assert.Equal((sbyte)3, reconciled.PkValue);
        Assert.Equal(EntityCamp.Justice, reconciled.BaseCamp);
        Assert.Equal(EntityCamp.Free, reconciled.CurrentCamp);
        Assert.Equal((ushort)55, reconciled.UnallocatedAttributePoints);
        Assert.Equal((ushort)12, reconciled.UnallocatedSkillPoints);
        Assert.True(reconciled.TryGetLearnedSkill(37, out var skill37));
        Assert.Equal((ushort)15, skill37.BaseLevel);
        Assert.True(reconciled.TryGetLearnedSkill(93, out var skill93));
        Assert.Equal((ushort)20, skill93.BaseLevel);
    }

    private static async Task ExecuteNumberInputPathAsync(
        TestGmTokenItemScript script,
        ZoneScriptItemContext itemCtx,
        ZoneScriptDialogContext dialogCtx,
        PlayerZoneState player,
        int submittedNumber,
        params int[] optionPath)
    {
        await ExecutePathAsync(script, itemCtx, dialogCtx, player, optionPath);
        await SubmitPendingNumberAsync(dialogCtx, player, submittedNumber);
    }

    private static async Task SubmitPendingNumberAsync(
        ZoneScriptDialogContext dialogCtx,
        PlayerZoneState player,
        int submittedNumber)
    {
        Assert.True(player.TryGetFeatureSlot(PlayerDialogFeatureSlot.SlotKey, out PlayerDialogFeatureSlot? dialogSlot));
        Assert.True(dialogSlot!.TrySnapshotAndClearNumberCallback(submittedNumber, out var numberCb, out int validatedValue));
        await numberCb!(dialogCtx, validatedValue);
    }

    private static Dictionary<string, LegacyLuaMenu> ParsePackLegacyCamNangGmMenus()
    {
        string repoRoot = FindWorkbenchRoot();
        string packerPath = Path.Combine(repoRoot, "qa", "scripts", "pack_legacy_profiles.py");
        string content = File.ReadAllText(packerPath, Encoding.UTF8);

        int fnStart = content.IndexOf("def build_camnang_gm_lua(", StringComparison.Ordinal);
        Assert.True(fnStart >= 0, $"build_camnang_gm_lua not found in {packerPath}");
        int fnEnd = content.IndexOf("def build_clean_login_lua(", fnStart, StringComparison.Ordinal);
        Assert.True(fnEnd > fnStart, "build_clean_login_lua boundary not found.");

        string block = content[fnStart..fnEnd];
        var menus = new Dictionary<string, LegacyLuaMenu>(StringComparer.Ordinal);
        MatchCollection fnMatches = Regex.Matches(
            block,
            @"function\s+(GMT_\w+)\(\)\s*Say\(\{L\(""([^""]+)""\)\},\s*(\d+),(.*?)\nend",
            RegexOptions.Singleline);

        Assert.NotEmpty(fnMatches);
        foreach (Match m in fnMatches)
        {
            string fnName = m.Groups[1].Value;
            string prompt = VerifyTcvn3RoundTrip(m.Groups[2].Value);
            int declaredCount = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            var opts = new List<string>();
            MatchCollection optionMatches = Regex.Matches(m.Groups[4].Value, @"\{L\(""([^""]+)""\)\}");
            Assert.NotEmpty(optionMatches);
            foreach (Match om in optionMatches)
            {
                opts.Add(VerifyTcvn3RoundTrip(om.Groups[1].Value));
            }

            menus[fnName] = new LegacyLuaMenu(prompt, declaredCount, opts);
        }

        return menus;
    }

    private static string VerifyTcvn3RoundTrip(string unicodeText)
    {
        byte[] encoded = Tcvn3Encoding.EncodeBytes(unicodeText);
        string decoded = Tcvn3Encoding.DecodeNullTerminated(encoded);
        Assert.Equal(unicodeText, decoded);
        return unicodeText;
    }

    private static string FindWorkbenchRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "qa", "scripts", "pack_legacy_profiles.py")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate workbench root containing qa/scripts/pack_legacy_profiles.py.");
    }
}
