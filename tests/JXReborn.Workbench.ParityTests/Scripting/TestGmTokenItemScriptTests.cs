using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JXReborn.GameAssets.Items;
using JXReborn.GameAssets.PlayerTemplates;
using JXReborn.Primitives.Characters;
using JXReborn.Primitives.Items;
using JXReborn.Primitives.WorldZones;
using JXReborn.Scripts.Dialogs;
using JXReborn.Scripts.Items;
using JXReborn.TestBase.Fakes;
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
        var (_, execCtx) = CreateHarness(enableGmToken: true);
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
        var (_, execCtx) = CreateHarness(enableGmToken: true);
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
        var (_, execCtx) = CreateHarness(enableGmToken: true);
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
        var (_, execCtx) = CreateHarness(enableGmToken: true);
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
        var (_, execCtx) = CreateHarness(enableGmToken: true);
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

    private sealed class StubFactionPlayerTemplateCatalog : IPlayerTemplateCatalog
    {
        private readonly FakePlayerTemplateCatalog _inner = new();
        private readonly Dictionary<byte, FactionTemplate> _factions;

        public StubFactionPlayerTemplateCatalog(params FactionTemplate[] factions)
        {
            _factions = factions.ToDictionary(static f => f.Id);
            AllFactions = factions;
        }

        public IReadOnlyCollection<CharacterBaseTemplate> CharacterTemplates => _inner.CharacterTemplates;
        public IReadOnlyCollection<StarterVillageTemplate> StarterVillages => _inner.StarterVillages;
        public IReadOnlyList<StarterItemTemplate> DefaultStarterItems => _inner.DefaultStarterItems;
        public PkRulesTemplate PkRules => _inner.PkRules;
        public IReadOnlyList<FactionTemplate> AllFactions { get; }

        public bool TryGetCharacterTemplate(ElementSeries series, CharacterGender gender, out CharacterBaseTemplate? template) =>
            _inner.TryGetCharacterTemplate(series, gender, out template);

        public bool TryGetStarterVillage(ushort villageMapId, out StarterVillageTemplate? village) =>
            _inner.TryGetStarterVillage(villageMapId, out village);

        public PlayerSeriesGrowthRuleTemplate GetSeriesGrowthRule(ElementSeries series) =>
            _inner.GetSeriesGrowthRule(series);

        public long GetRequiredExpForNextLevel(int currentLevel) =>
            _inner.GetRequiredExpForNextLevel(currentLevel);

        public bool IsRoleNameProhibited(string roleNameUnicode) =>
            _inner.IsRoleNameProhibited(roleNameUnicode);

        public PkPenaltyRuleTemplate GetPkPenaltyRule(int pkValue) =>
            _inner.GetPkPenaltyRule(pkValue);

        public bool TryGetFactionById(byte factionId, out FactionTemplate? faction) =>
            _factions.TryGetValue(factionId, out faction);

        public bool TryGetFactionByCode(string code, out FactionTemplate? faction)
        {
            faction = AllFactions.FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase));
            return faction is not null;
        }
    }
}
