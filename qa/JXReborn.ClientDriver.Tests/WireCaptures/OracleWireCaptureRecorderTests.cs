using JXReborn.Adapter.Legacy.Packets;
using JXReborn.ClientDriver.Tests.DifferentialParity;
using JXReborn.TestBase.WireCaptures;
using Xunit;

namespace JXReborn.ClientDriver.Tests.WireCaptures;

/// <summary>
/// Independent ground-truth recorder driving real <c>game.exe</c> against the legacy reference oracle (<c>T=0</c>)
/// to capture and sign <c>*.capture.json</c> files for fast offline TDD Red-to-Green replay on <c>JXReborn</c>.
/// Run via: <c>.\qa\scripts\run-parity.ps1 -Mode Record -Filter "OracleWireCaptureRecorderTests"</c>.
/// </summary>
[Collection(HeadlessGameExeCollectionDefinition.Name)]
[Trait("Category", "DifferentialParity")]
public sealed class OracleWireCaptureRecorderTests
{
    private const ushort BaLangHuyenMapId = 53;
    private const int PlaceEquip = 2;
    private const int PlaceEquipRoom = 3;
    private const int EquipSlotWeapon = 3;

    private readonly ITestOutputHelper _output;

    public OracleWireCaptureRecorderTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Record_W01_RightClickEquippedWeapon_ShouldPersistSignedCaptureFromLiveOracle()
    {
        OracleServerOptions? oracleOpts = OracleServerOptions.TryFromEnvironment();
        if (oracleOpts is null || oracleOpts.IsReplayMode)
        {
            Assert.Skip("Skipped Oracle wire capture recording (requires Live/Record mode with LEGACY_ORACLE_HOST).");
        }

        const string captureId = "W01-RightClickEquippedWeapon";
        await using JxHeadlessClient client = await JxHeadlessClient.SpawnAsync(new JxHeadlessOptions
        {
            ServerIp = oracleOpts.Host,
            GatewayPort = oracleOpts.BishopPort
        });

        _ = await client.LoginAndEnterWorldAsync(
            oracleOpts.Host,
            oracleOpts.BishopPort,
            account: "gmw01",
            password: oracleOpts.Password,
            roleName: "HeroEquipW01",
            gender: 0,
            series: 0,
            nativePlaceId: 53);
        _ = await client.DismissSystemLoginHintIfOpenAsync();

        // Symmetric Setup via MECE Atomic GM Token: Teleport Map 106 -> Level 20 -> Cang Kiem Lv2
        _ = await TestGmTokenDriver.InvokePathAsync(client, TestGmTokenDriver.MenuTeleport, TestGmTokenDriver.TeleportBaLang53);
        WorldDomSnapshot afterTp = await client.WaitUntilAsync(static s => s.Player.MapId == BaLangHuyenMapId && s.Player.SyncReady, TimeSpan.FromSeconds(8));
        uint settle1 = afterTp.TickCount + 20;
        _ = await client.WaitUntilAsync(s => s.TickCount >= settle1, TimeSpan.FromSeconds(5));

        _ = await TestGmTokenDriver.InvokePathAsync(client, TestGmTokenDriver.PathLevel20);
        WorldDomSnapshot preStep1 = await TestGmTokenDriver.InvokePathAsync(client, TestGmTokenDriver.PathEqSword2);
        InventoryItemSnapshot cangKiem = preStep1.Items.FirstOrDefault(
            static i => i.Place == PlaceEquipRoom && i.Genre == 0 && i.DetailType == 0 && i.ParticularType == 0 && i.Level == 2)
            ?? throw new InvalidOperationException("Cang Kiem (0,0,0,lv=2) not found in bag after Kit W01.");
        uint cangKiemServerId = cangKiem.ServerItemId;

        var symbols = new Dictionary<string, WireCaptureSymbolDefinition>(StringComparer.Ordinal)
        {
            ["$actor:P1"] = new(WireCaptureSymbolKind.PlayerSelf, "P1", preStep1.Player.DwId),
            ["$item:cang_kiem"] = new(
                WireCaptureSymbolKind.Item,
                "P1",
                cangKiemServerId,
                ItemGenre: 0,
                ItemDetailType: 0,
                ItemParticularType: 0,
                ItemLevel: 2,
                PreferredPlace: PlaceEquipRoom)
        };

        // Step 1: Right-click Cang Kiem in bag to auto-equip/swap onto character Weapon slot
        _ = await client.BeginWireCaptureAsync();
        _ = await client.UseItemAsync(cangKiem.ArrayIndex, cangKiem.Place, cangKiem.GridX, cangKiem.GridY);
        WorldDomSnapshot afterStep1 = await client.WaitUntilAsync(
            s => s.Player.HandItemIdx == 0 &&
                 s.Items.Any(i => i.ServerItemId == cangKiemServerId && i.Place == PlaceEquip && i.GridX == EquipSlotWeapon),
            TimeSpan.FromSeconds(5));
        uint step1EndTick = afterStep1.TickCount + 6;
        afterStep1 = await client.WaitUntilAsync(s => s.TickCount >= step1EndTick, TimeSpan.FromSeconds(3));
        EndWireCaptureResult capStep1 = await client.EndWireCaptureAsync();

        // Step 2: Click Weapon slot on character equipment panel -> place into empty bottom-right 1x3 bag cell in 6x6 EquipRoom
        (int dropBagX, int dropBagY) = FindFreeBagCell(afterStep1.Items, width: 1, height: 3);
        _ = await client.BeginWireCaptureAsync();
        _ = await client.MoveItemAsync(PlaceEquip, EquipSlotWeapon, 0, PlaceEquip, EquipSlotWeapon, 0);
        _ = await client.WaitUntilAsync(static s => s.Player.HandItemIdx > 0, TimeSpan.FromSeconds(5));
        _ = await client.MoveItemAsync(PlaceEquipRoom, dropBagX, dropBagY, PlaceEquipRoom, dropBagX, dropBagY);
        WorldDomSnapshot afterStep2 = await client.WaitUntilAsync(
            s => s.Player.HandItemIdx == 0 &&
                 s.Items.Any(i => i.ServerItemId == cangKiemServerId && i.Place == PlaceEquipRoom),
            TimeSpan.FromSeconds(5));
        uint step2EndTick = afterStep2.TickCount + 6;
        afterStep2 = await client.WaitUntilAsync(s => s.TickCount >= step2EndTick, TimeSpan.FromSeconds(3));
        EndWireCaptureResult capStep2 = await client.EndWireCaptureAsync();

        var doc = WireCaptureStore.CreateSignedDocument(
            captureId: captureId,
            title: "Right-click auto-equip Cang Kiem onto character weapon slot and unequip back to bag",
            oracleProfile: oracleOpts.Profile,
            oraclePackageArchiveSha256: oracleOpts.PackageArchiveSha256,
            actors:
            [
                new WireCaptureActorDefinition(
                    ActorId: "P1",
                    AccountName: "gmw01",
                    RoleName: "HeroEquipW01",
                    Gender: 0,
                    Series: 0,
                    NativePlaceId: 53,
                    GmMenuSelections:
                    [
                        new WireCaptureGmMenuSelection("Teleport Ba Lang 53", [TestGmTokenDriver.MenuTeleport, TestGmTokenDriver.TeleportBaLang53], WaitTicksAfterSelection: 20, ExpectMapIdAfterSelection: BaLangHuyenMapId),
                        new WireCaptureGmMenuSelection("Level 20", TestGmTokenDriver.PathLevel20, WaitTicksAfterSelection: 20),
                        new WireCaptureGmMenuSelection("Grant Cang Kiem Lv2", TestGmTokenDriver.PathEqSword2, WaitTicksAfterSelection: 20)
                    ],
                    SettleTicksAfterSetup: 5,
                    ExpectedPreState: new WireCaptureActorStateExpectation(
                        MapId: BaLangHuyenMapId,
                        Level: 20,
                        BagContainsSymbols: ["$item:cang_kiem"]))
            ],
            symbols: symbols,
            steps:
            [
                new WireCaptureStep(
                    StepId: "01_RightClickAutoEquipWeapon",
                    ActorId: "P1",
                    UserActionDescription: "Right-click Cang Kiem in bag to equip onto character weapon slot",
                    C2sFrames: WireCaptureAutoSymbolizer.BuildSymbolicC2sFrames(capStep1, symbols, preStep1),
                    ObservationTicks: Math.Max(8, (int)(capStep1.EndTickFrames - capStep1.BaseTickFrames) + 2),
                    ExpectedS2cContract: WireCaptureAutoSymbolizer.BuildExpectedS2cContract(capStep1, symbols, includePeriodicVitalSyncs: false),
                    ExpectedActorStateAfter: new Dictionary<string, WireCaptureActorStateExpectation>(StringComparer.Ordinal)
                    {
                        ["P1"] = new(
                            HasEquippedWeapon: true,
                            EquippedSymbols: ["$item:cang_kiem"],
                            BagExcludesSymbols: ["$item:cang_kiem"])
                    }),
                new WireCaptureStep(
                    StepId: "02_UnequipWeaponFromCharacterToBag",
                    ActorId: "P1",
                    UserActionDescription: "Pick equipped weapon from character weapon slot and place into bag",
                    C2sFrames: WireCaptureAutoSymbolizer.BuildSymbolicC2sFrames(capStep2, symbols, afterStep1),
                    ObservationTicks: Math.Max(10, (int)(capStep2.EndTickFrames - capStep2.BaseTickFrames) + 4),
                    ExpectedS2cContract: WireCaptureAutoSymbolizer.BuildExpectedS2cContract(capStep2, symbols, includePeriodicVitalSyncs: false),
                    ExpectedActorStateAfter: new Dictionary<string, WireCaptureActorStateExpectation>(StringComparer.Ordinal)
                    {
                        ["P1"] = new(
                            HasEquippedWeapon: false,
                            BagContainsSymbols: ["$item:cang_kiem"])
                    })
            ]);

        string savedPath = WireCaptureStore.Save(doc);
        _output.WriteLine($"[WireCapture:Saved] {captureId} -> {savedPath} (sha256={doc.Metadata.ContentSha256})");
    }

    [Fact]
    public async Task Record_W02_EatManaPotionRegenTicks_ShouldPersistSignedCaptureFromLiveOracle()
    {
        OracleServerOptions? oracleOpts = OracleServerOptions.TryFromEnvironment();
        if (oracleOpts is null || oracleOpts.IsReplayMode)
        {
            Assert.Skip("Skipped Oracle wire capture recording (requires Live/Record mode with LEGACY_ORACLE_HOST).");
        }

        const string captureId = "W02-EatManaPotionRegenTicks";
        await using JxHeadlessClient client = await JxHeadlessClient.SpawnAsync(new JxHeadlessOptions
        {
            ServerIp = oracleOpts.Host,
            GatewayPort = oracleOpts.BishopPort
        });

        _ = await client.LoginAndEnterWorldAsync(
            oracleOpts.Host,
            oracleOpts.BishopPort,
            account: "gmw02",
            password: oracleOpts.Password,
            roleName: "HeroManaW02",
            gender: 0,
            series: 0,
            nativePlaceId: 53);
        _ = await client.DismissSystemLoginHintIfOpenAsync();

        _ = await TestGmTokenDriver.InvokePathAsync(client, TestGmTokenDriver.MenuTeleport, TestGmTokenDriver.TeleportBaLang53);
        WorldDomSnapshot afterTp = await client.WaitUntilAsync(static s => s.Player.MapId == BaLangHuyenMapId && s.Player.SyncReady, TimeSpan.FromSeconds(8));
        uint settle1 = afterTp.TickCount + 20;
        _ = await client.WaitUntilAsync(s => s.TickCount >= settle1, TimeSpan.FromSeconds(5));

        _ = await TestGmTokenDriver.InvokePathAsync(client, TestGmTokenDriver.PathLevel50);
        _ = await TestGmTokenDriver.InvokePathAsync(client, TestGmTokenDriver.PathAddAttr30);
        WorldDomSnapshot preStep1 = await TestGmTokenDriver.InvokePathAsync(client, TestGmTokenDriver.PathMedMpMid);
        InventoryItemSnapshot manaPotion = preStep1.Items.FirstOrDefault(
            static i => i.Place == PlaceEquipRoom && i.Genre == 1 && i.DetailType == 1 && i.ParticularType == 0 && i.Level == 2)
            ?? throw new InvalidOperationException("Ngung Than dan (trung) (1,1,0,lv=2) not found in bag after Kit W02.");
        uint manaPotionServerId = manaPotion.ServerItemId;

        var symbols = new Dictionary<string, WireCaptureSymbolDefinition>(StringComparer.Ordinal)
        {
            ["$actor:P1"] = new(WireCaptureSymbolKind.PlayerSelf, "P1", preStep1.Player.DwId),
            ["$item:mana_potion"] = new(
                WireCaptureSymbolKind.Item,
                "P1",
                manaPotionServerId,
                ItemGenre: 1,
                ItemDetailType: 1,
                ItemParticularType: 0,
                ItemLevel: 2,
                PreferredPlace: PlaceEquipRoom)
        };

        // Step 1: Allocate +25 Energy (Noi Cong) so MaxMana increases while CurMana stays lower, creating a real Mana deficit
        _ = await client.BeginWireCaptureAsync();
        _ = await client.AddBaseAttributeAsync(attribute: 3, points: 25);
        uint step1Settle = preStep1.TickCount + 8;
        WorldDomSnapshot afterEnergy = await client.WaitUntilAsync(
            s => s.Player.ManaMax > preStep1.Player.ManaMax && s.TickCount >= step1Settle,
            TimeSpan.FromSeconds(5));
        EndWireCaptureResult capStep1 = await client.EndWireCaptureAsync();

        InventoryItemSnapshot latestPotion = afterEnergy.Items.First(i => i.ServerItemId == manaPotionServerId);
        _output.WriteLine($"[W02:AfterEnergy] Mana={afterEnergy.Player.Mana}/{afterEnergy.Player.ManaMax}, PotionPos=({latestPotion.GridX},{latestPotion.GridY})");

        // Step 2: Consume Ngung Than dan (trung) and record 115 ticks of periodic 18-Hz Mana regeneration
        _ = await client.BeginWireCaptureAsync();
        _ = await client.UseItemAsync(latestPotion.ArrayIndex, latestPotion.Place, latestPotion.GridX, latestPotion.GridY);
        uint targetEndTick = afterEnergy.TickCount + 115;
        WorldDomSnapshot postStep = await client.WaitUntilAsync(s => s.TickCount >= targetEndTick, TimeSpan.FromSeconds(12));
        EndWireCaptureResult capStep2 = await client.EndWireCaptureAsync();

        _output.WriteLine($"[W02:Post] Mana={postStep.Player.Mana}/{postStep.Player.ManaMax}, CapturedFrames={capStep2.Frames.Count}");

        var doc = WireCaptureStore.CreateSignedDocument(
            captureId: captureId,
            title: "Allocate Energy to create Mana deficit, consume Ngung Than dan (trung), and regenerate Mana over 18-Hz ticks",
            oracleProfile: oracleOpts.Profile,
            oraclePackageArchiveSha256: oracleOpts.PackageArchiveSha256,
            actors:
            [
                new WireCaptureActorDefinition(
                    ActorId: "P1",
                    AccountName: "gmw02",
                    RoleName: "HeroManaW02",
                    Gender: 0,
                    Series: 0,
                    NativePlaceId: 53,
                    GmMenuSelections:
                    [
                        new WireCaptureGmMenuSelection("Teleport Ba Lang 53", [TestGmTokenDriver.MenuTeleport, TestGmTokenDriver.TeleportBaLang53], WaitTicksAfterSelection: 20, ExpectMapIdAfterSelection: BaLangHuyenMapId),
                        new WireCaptureGmMenuSelection("Level 50", TestGmTokenDriver.PathLevel50, WaitTicksAfterSelection: 20),
                        new WireCaptureGmMenuSelection("Add +30 Attributes", TestGmTokenDriver.PathAddAttr30, WaitTicksAfterSelection: 20),
                        new WireCaptureGmMenuSelection("Grant Ngung Than Dan Trung", TestGmTokenDriver.PathMedMpMid, WaitTicksAfterSelection: 20)
                    ],
                    SettleTicksAfterSetup: 5,
                    ExpectedPreState: new WireCaptureActorStateExpectation(
                        MapId: BaLangHuyenMapId,
                        Level: 50,
                        BagContainsSymbols: ["$item:mana_potion"]))
            ],
            symbols: symbols,
            steps:
            [
                new WireCaptureStep(
                    StepId: "01_AllocateEnergyToCreateManaDeficit",
                    ActorId: "P1",
                    UserActionDescription: "Allocate 25 Energy points to expand MaxMana above CurrentMana",
                    C2sFrames: WireCaptureAutoSymbolizer.BuildSymbolicC2sFrames(capStep1, symbols, preStep1),
                    ObservationTicks: 8,
                    ExpectedS2cContract: [],
                    ExpectedActorStateAfter: new Dictionary<string, WireCaptureActorStateExpectation>(StringComparer.Ordinal)
                    {
                        ["P1"] = new(
                            CurrentMana: new WireCaptureNumericExpectation(WireCaptureFieldMatcherKind.ExactInt64, ExpectedValue: afterEnergy.Player.Mana),
                            MaxMana: new WireCaptureNumericExpectation(WireCaptureFieldMatcherKind.ExactInt64, ExpectedValue: afterEnergy.Player.ManaMax))
                    }),
                new WireCaptureStep(
                    StepId: "02_ConsumeManaPotionAndRegen",
                    ActorId: "P1",
                    UserActionDescription: "Right-click Ngung Than dan (trung) in bag and observe periodic Mana regeneration ticks",
                    C2sFrames: WireCaptureAutoSymbolizer.BuildSymbolicC2sFrames(capStep2, symbols, afterEnergy),
                    ObservationTicks: 115,
                    ExpectedS2cContract: WireCaptureAutoSymbolizer.BuildExpectedS2cContract(
                        capStep2,
                        symbols,
                        includePeriodicVitalSyncs: true,
                        initialMana: afterEnergy.Player.Mana),
                    ExpectedActorStateAfter: new Dictionary<string, WireCaptureActorStateExpectation>(StringComparer.Ordinal)
                    {
                        ["P1"] = new(
                            CurrentMana: new WireCaptureNumericExpectation(WireCaptureFieldMatcherKind.ExactInt64, ExpectedValue: postStep.Player.Mana),
                            BagExcludesSymbols: ["$item:mana_potion"])
                    })
            ]);

        string savedPath = WireCaptureStore.Save(doc);
        _output.WriteLine($"[WireCapture:Saved] {captureId} -> {savedPath} (sha256={doc.Metadata.ContentSha256})");
    }

    [Fact]
    public async Task Record_W03_CastHangLongBatVuOnDummy_ShouldPersistSignedCaptureFromLiveOracle()
    {
        OracleServerOptions? oracleOpts = OracleServerOptions.TryFromEnvironment();
        if (oracleOpts is null || oracleOpts.IsReplayMode)
        {
            Assert.Skip("Skipped Oracle wire capture recording (requires Live/Record mode with LEGACY_ORACLE_HOST).");
        }

        const string captureId = "W03-CastHangLongBatVuOnDummy";
        await using JxHeadlessClient client = await JxHeadlessClient.SpawnAsync(new JxHeadlessOptions
        {
            ServerIp = oracleOpts.Host,
            GatewayPort = oracleOpts.BishopPort
        });

        _ = await client.LoginAndEnterWorldAsync(
            oracleOpts.Host,
            oracleOpts.BishopPort,
            account: "gmw03",
            password: oracleOpts.Password,
            roleName: "HeroDragonW03",
            gender: 0,
            series: 0,
            nativePlaceId: 53);
        _ = await client.DismissSystemLoginHintIfOpenAsync();

        _ = await TestGmTokenDriver.InvokePathAsync(client, TestGmTokenDriver.MenuTeleport, TestGmTokenDriver.TeleportBaLang53);
        WorldDomSnapshot afterTp = await client.WaitUntilAsync(static s => s.Player.MapId == BaLangHuyenMapId && s.Player.SyncReady, TimeSpan.FromSeconds(8));
        uint settle1 = afterTp.TickCount + 20;
        _ = await client.WaitUntilAsync(s => s.TickCount >= settle1, TimeSpan.FromSeconds(5));

        _ = await TestGmTokenDriver.InvokePathAsync(client, TestGmTokenDriver.PathLevel30);
        _ = await TestGmTokenDriver.InvokePathAsync(client, TestGmTokenDriver.PathFightMode1);
        _ = await TestGmTokenDriver.InvokePathAsync(client, TestGmTokenDriver.PathLearnHangLongBatVu5);
        WorldDomSnapshot preStep = await TestGmTokenDriver.InvokePathAsync(client, TestGmTokenDriver.PathSpawnDummy413);
        NpcSnapshot dummy = preStep.Npcs
            .Where(n => n.DwId != preStep.Player.DwId && n.Kind == 0)
            .OrderBy(n =>
            {
                long dx = n.MpsX - preStep.Player.MpsX;
                long dy = n.MpsY - preStep.Player.MpsY;
                return (dx * dx) + (dy * dy);
            })
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Training dummy (Coc go 413) not found in AOI after Kit W03.");

        var symbols = new Dictionary<string, WireCaptureSymbolDefinition>(StringComparer.Ordinal)
        {
            ["$actor:P1"] = new(WireCaptureSymbolKind.PlayerSelf, "P1", preStep.Player.DwId),
            ["$npc:dummy"] = new(WireCaptureSymbolKind.Npc, "P1", dummy.DwId, NpcTemplateId: 413)
        };

        _ = await client.BeginWireCaptureAsync();
        _ = await client.UseSkillOnTargetAsync(skillId: 14, targetNpcDwId: dummy.DwId);
        uint targetEndTick = preStep.TickCount + 36;
        WorldDomSnapshot postStep = await client.WaitUntilAsync(s => s.TickCount >= targetEndTick, TimeSpan.FromSeconds(6));
        EndWireCaptureResult cap = await client.EndWireCaptureAsync();

        NpcSnapshot? postDummy = postStep.Npcs.FirstOrDefault(n => n.DwId == dummy.DwId);
        int postDummyLifePer128 = postDummy is not null && postDummy.LifeMax > 0
            ? Math.Clamp((postDummy.Life * 128) / postDummy.LifeMax, 0, 128)
            : 0;
        _output.WriteLine(
            $"[W03:Post] PlayerMana={preStep.Player.Mana}->{postStep.Player.Mana}, DummyLife={dummy.Life}/{dummy.LifeMax} -> {postDummy?.Life}/{postDummy?.LifeMax} (lifePer128={postDummyLifePer128}), Frames={cap.Frames.Count}");

        var doc = WireCaptureStore.CreateSignedDocument(
            captureId: captureId,
            title: "Cast Hang Long Bat Vu (Skill 14 Lv5) onto stationary training dummy",
            oracleProfile: oracleOpts.Profile,
            oraclePackageArchiveSha256: oracleOpts.PackageArchiveSha256,
            actors:
            [
                new WireCaptureActorDefinition(
                    ActorId: "P1",
                    AccountName: "gmw03",
                    RoleName: "HeroDragonW03",
                    Gender: 0,
                    Series: 0,
                    NativePlaceId: 53,
                    GmMenuSelections:
                    [
                        new WireCaptureGmMenuSelection("Teleport Ba Lang 53", [TestGmTokenDriver.MenuTeleport, TestGmTokenDriver.TeleportBaLang53], WaitTicksAfterSelection: 20, ExpectMapIdAfterSelection: BaLangHuyenMapId),
                        new WireCaptureGmMenuSelection("Level 30", TestGmTokenDriver.PathLevel30, WaitTicksAfterSelection: 20),
                        new WireCaptureGmMenuSelection("FightMode 1", TestGmTokenDriver.PathFightMode1, WaitTicksAfterSelection: 20),
                        new WireCaptureGmMenuSelection("Learn Skill 14 Lv5", TestGmTokenDriver.PathLearnHangLongBatVu5, WaitTicksAfterSelection: 20),
                        new WireCaptureGmMenuSelection("Spawn Dummy 413", TestGmTokenDriver.PathSpawnDummy413, WaitTicksAfterSelection: 20)
                    ],
                    SettleTicksAfterSetup: 5,
                    ExpectedPreState: new WireCaptureActorStateExpectation(
                        MapId: BaLangHuyenMapId,
                        Level: 30))
            ],
            symbols: symbols,
            steps:
            [
                new WireCaptureStep(
                    StepId: "01_CastHangLongBatVuOnDummy",
                    ActorId: "P1",
                    UserActionDescription: "Cast Hang Long Bat Vu (Skill 14 Lv5) on training dummy (Coc go 413)",
                    C2sFrames: WireCaptureAutoSymbolizer.BuildSymbolicC2sFrames(cap, symbols, preStep),
                    ObservationTicks: 36,
                    ExpectedS2cContract: WireCaptureAutoSymbolizer.BuildExpectedS2cContract(
                        cap,
                        symbols,
                        includePeriodicVitalSyncs: true,
                        initialMana: preStep.Player.Mana),
                    ExpectedActorStateAfter: new Dictionary<string, WireCaptureActorStateExpectation>(StringComparer.Ordinal)
                    {
                        ["P1"] = new(
                            CurrentMana: new WireCaptureNumericExpectation(WireCaptureFieldMatcherKind.ExactInt64, ExpectedValue: postStep.Player.Mana),
                            TargetNpcSymbol: "$npc:dummy",
                            TargetNpcLifePercent128: new WireCaptureNumericExpectation(
                                WireCaptureFieldMatcherKind.RangeInt64,
                                MinValue: Math.Max(0, postDummyLifePer128 - 2),
                                MaxValue: Math.Min(127, postDummyLifePer128 + 2)))
                    })
            ]);

        string savedPath = WireCaptureStore.Save(doc);
        _output.WriteLine($"[WireCapture:Saved] {captureId} -> {savedPath} (sha256={doc.Metadata.ContentSha256})");
    }

    [Fact]
    public async Task Record_W04_EatSmallManaPotionFullDuration_ShouldPersistSignedCaptureFromLiveOracle()
    {
        OracleServerOptions? oracleOpts = OracleServerOptions.TryFromEnvironment();
        if (oracleOpts is null || oracleOpts.IsReplayMode)
        {
            Assert.Skip("Skipped Oracle wire capture recording (requires Live/Record mode with LEGACY_ORACLE_HOST).");
        }

        const string captureId = "W04-EatSmallManaPotionFullDuration";
        await using JxHeadlessClient client = await JxHeadlessClient.SpawnAsync(new JxHeadlessOptions
        {
            ServerIp = oracleOpts.Host,
            GatewayPort = oracleOpts.BishopPort
        });

        _ = await client.LoginAndEnterWorldAsync(
            oracleOpts.Host,
            oracleOpts.BishopPort,
            account: "gmw04",
            password: oracleOpts.Password,
            roleName: "HeroManaW04",
            gender: 0,
            series: 0,
            nativePlaceId: 53);
        _ = await client.DismissSystemLoginHintIfOpenAsync();

        // Symmetric setup using MECE Atomic GM Token: Tp106 -> Lv50 -> Ngung Than dan tieu (1,1,0,lv=1)
        _ = await TestGmTokenDriver.InvokePathAsync(client, TestGmTokenDriver.MenuTeleport, TestGmTokenDriver.TeleportBaLang53);
        WorldDomSnapshot afterTp = await client.WaitUntilAsync(static s => s.Player.MapId == BaLangHuyenMapId && s.Player.SyncReady, TimeSpan.FromSeconds(8));
        uint settle1 = afterTp.TickCount + 20;
        _ = await client.WaitUntilAsync(s => s.TickCount >= settle1, TimeSpan.FromSeconds(5));

        _ = await TestGmTokenDriver.InvokePathAsync(client, TestGmTokenDriver.PathLevel50);
        WorldDomSnapshot preStep1 = await TestGmTokenDriver.InvokePathAsync(client, TestGmTokenDriver.PathMedMpSmall);
        InventoryItemSnapshot smallManaPotion = preStep1.Items.FirstOrDefault(
            static i => i.Place == PlaceEquipRoom && i.Genre == 1 && i.DetailType == 1 && i.ParticularType == 0 && i.Level == 1)
            ?? throw new InvalidOperationException("Ngung Than dan (tieu) (1,1,0,lv=1) not found in bag after Kit J13.");
        uint smallPotionServerId = smallManaPotion.ServerItemId;

        var symbols = new Dictionary<string, WireCaptureSymbolDefinition>(StringComparer.Ordinal)
        {
            ["$actor:P1"] = new(WireCaptureSymbolKind.PlayerSelf, "P1", preStep1.Player.DwId),
            ["$item:small_mana_potion"] = new(
                WireCaptureSymbolKind.Item,
                "P1",
                smallPotionServerId,
                ItemGenre: 1,
                ItemDetailType: 1,
                ItemParticularType: 0,
                ItemLevel: 1,
                PreferredPlace: PlaceEquipRoom)
        };

        // Step 1: Allocate +120 Energy so MaxMana expands by +120 (65 -> 185), allowing full uncapped observation of all 200 durationFrames
        _ = await client.BeginWireCaptureAsync();
        _ = await client.AddBaseAttributeAsync(attribute: 3, points: 120);
        uint step1Settle = preStep1.TickCount + 8;
        WorldDomSnapshot afterEnergy = await client.WaitUntilAsync(
            s => s.Player.ManaMax > preStep1.Player.ManaMax && s.TickCount >= step1Settle,
            TimeSpan.FromSeconds(5));
        EndWireCaptureResult capStep1 = await client.EndWireCaptureAsync();

        InventoryItemSnapshot latestPotion = afterEnergy.Items.First(i => i.ServerItemId == smallPotionServerId);
        _output.WriteLine($"[W04:AfterEnergy] Mana={afterEnergy.Player.Mana}/{afterEnergy.Player.ManaMax}, PotionPos=({latestPotion.GridX},{latestPotion.GridY})");

        // Step 2: Consume Ngung Than dan (tieu) (1,1,0,lv=1) and observe 220 ticks across its entire duration until natural expiry
        _ = await client.BeginWireCaptureAsync();
        _ = await client.UseItemAsync(latestPotion.ArrayIndex, latestPotion.Place, latestPotion.GridX, latestPotion.GridY);
        uint targetEndTick = afterEnergy.TickCount + 220;
        WorldDomSnapshot postStep = await client.WaitUntilAsync(s => s.TickCount >= targetEndTick, TimeSpan.FromSeconds(18));
        EndWireCaptureResult capStep2 = await client.EndWireCaptureAsync();

        _output.WriteLine($"[W04:Post] Mana={postStep.Player.Mana}/{postStep.Player.ManaMax}, CapturedFrames={capStep2.Frames.Count}");

        var doc = WireCaptureStore.CreateSignedDocument(
            captureId: captureId,
            title: "Consume Ngung Than dan (tieu) (1,1,0,lv=1) with 120 Mana deficit and observe full 200-frame regeneration curve",
            oracleProfile: oracleOpts.Profile,
            oraclePackageArchiveSha256: oracleOpts.PackageArchiveSha256,
            actors:
            [
                new WireCaptureActorDefinition(
                    ActorId: "P1",
                    AccountName: "gmw04",
                    RoleName: "HeroManaW04",
                    Gender: 0,
                    Series: 0,
                    NativePlaceId: 53,
                    GmMenuSelections:
                    [
                        new WireCaptureGmMenuSelection("Teleport Ba Lang 53", [TestGmTokenDriver.MenuTeleport, TestGmTokenDriver.TeleportBaLang53], WaitTicksAfterSelection: 20, ExpectMapIdAfterSelection: BaLangHuyenMapId),
                        new WireCaptureGmMenuSelection("Level 50", TestGmTokenDriver.PathLevel50, WaitTicksAfterSelection: 20),
                        new WireCaptureGmMenuSelection("Grant Ngung Than Dan Tieu", TestGmTokenDriver.PathMedMpSmall, WaitTicksAfterSelection: 20)
                    ],
                    SettleTicksAfterSetup: 5,
                    ExpectedPreState: new WireCaptureActorStateExpectation(
                        MapId: BaLangHuyenMapId,
                        Level: 50,
                        BagContainsSymbols: ["$item:small_mana_potion"]))
            ],
            symbols: symbols,
            steps:
            [
                new WireCaptureStep(
                    StepId: "01_Allocate120EnergyForFullManaDeficit",
                    ActorId: "P1",
                    UserActionDescription: "Allocate 120 Energy points so MaxMana exceeds CurrentMana by 120",
                    C2sFrames: WireCaptureAutoSymbolizer.BuildSymbolicC2sFrames(capStep1, symbols, preStep1),
                    ObservationTicks: 8,
                    ExpectedS2cContract: [],
                    ExpectedActorStateAfter: new Dictionary<string, WireCaptureActorStateExpectation>(StringComparer.Ordinal)
                    {
                        ["P1"] = new(
                            CurrentMana: new WireCaptureNumericExpectation(WireCaptureFieldMatcherKind.ExactInt64, ExpectedValue: afterEnergy.Player.Mana),
                            MaxMana: new WireCaptureNumericExpectation(WireCaptureFieldMatcherKind.ExactInt64, ExpectedValue: afterEnergy.Player.ManaMax))
                    }),
                new WireCaptureStep(
                    StepId: "02_ConsumeSmallManaPotionFullDuration",
                    ActorId: "P1",
                    UserActionDescription: "Right-click Ngung Than dan (tieu) in bag and observe every periodic Mana regeneration tick until natural expiration",
                    C2sFrames: WireCaptureAutoSymbolizer.BuildSymbolicC2sFrames(capStep2, symbols, afterEnergy),
                    ObservationTicks: 220,
                    ExpectedS2cContract: WireCaptureAutoSymbolizer.BuildExpectedS2cContract(
                        capStep2,
                        symbols,
                        includePeriodicVitalSyncs: true,
                        initialMana: afterEnergy.Player.Mana),
                    ExpectedActorStateAfter: new Dictionary<string, WireCaptureActorStateExpectation>(StringComparer.Ordinal)
                    {
                        ["P1"] = new(
                            CurrentMana: new WireCaptureNumericExpectation(WireCaptureFieldMatcherKind.ExactInt64, ExpectedValue: postStep.Player.Mana),
                            BagExcludesSymbols: ["$item:small_mana_potion"])
                    })
            ]);

        string savedPath = WireCaptureStore.Save(doc);
        _output.WriteLine($"[WireCapture:Saved] {captureId} -> {savedPath} (sha256={doc.Metadata.ContentSha256})");
    }

    [Fact]
    public async Task Record_W05_NpcShopBuyMedicine_ShouldPersistSignedCaptureFromLiveOracle()
    {
        OracleServerOptions? oracleOpts = OracleServerOptions.TryFromEnvironment();
        if (oracleOpts is null || oracleOpts.IsReplayMode)
        {
            Assert.Skip("Skipped Oracle wire capture recording (requires Live/Record mode with LEGACY_ORACLE_HOST).");
        }

        const string captureId = "W05-NpcShopBuyMedicine";
        await using JxHeadlessClient client = await JxHeadlessClient.SpawnAsync(new JxHeadlessOptions
        {
            ServerIp = oracleOpts.Host,
            GatewayPort = oracleOpts.BishopPort
        });

        _ = await client.LoginAndEnterWorldAsync(
            oracleOpts.Host,
            oracleOpts.BishopPort,
            account: "gmw05",
            password: oracleOpts.Password,
            roleName: "HeroShopW05",
            gender: 0,
            series: 0,
            nativePlaceId: 53);
        _ = await client.DismissSystemLoginHintIfOpenAsync();

        _ = await TestGmTokenDriver.InvokePathAsync(client, TestGmTokenDriver.PathMoney50k);
        WorldDomSnapshot preStep = await TestGmTokenDriver.InvokePathAsync(client, TestGmTokenDriver.PathOpenShop39);
        (int freeX, int freeY) = FindFreeBagCell(preStep.Items, width: 1, height: 1);

        var symbols = new Dictionary<string, WireCaptureSymbolDefinition>(StringComparer.Ordinal)
        {
            ["$actor:P1"] = new(WireCaptureSymbolKind.PlayerSelf, "P1", preStep.Player.DwId)
        };

        _ = await client.BeginWireCaptureAsync();
        _ = await client.BuyShopItemAsync(buyIndex: 0, place: PlaceEquipRoom, gridX: freeX, gridY: freeY);
        WorldDomSnapshot postStep = await client.WaitUntilAsync(
            s => s.Player.Money < preStep.Player.Money,
            TimeSpan.FromSeconds(5));
        uint endTick = postStep.TickCount + 6;
        postStep = await client.WaitUntilAsync(s => s.TickCount >= endTick, TimeSpan.FromSeconds(3));
        EndWireCaptureResult cap = await client.EndWireCaptureAsync();

        HashSet<byte> tracked = [ProtocolOpcodes.s2c_syncmoney, ProtocolOpcodes.s2c_syncitem];
        var doc = WireCaptureStore.CreateSignedDocument(
            captureId: captureId,
            title: "Open Pharmacist Shop 39 and buy medicine item (0x5D -> 0x61 + 0x5F)",
            oracleProfile: oracleOpts.Profile,
            oraclePackageArchiveSha256: oracleOpts.PackageArchiveSha256,
            actors:
            [
                new WireCaptureActorDefinition(
                    ActorId: "P1",
                    AccountName: "gmw05",
                    RoleName: "HeroShopW05",
                    Gender: 0,
                    Series: 0,
                    NativePlaceId: 53,
                    GmMenuSelections:
                    [
                        new WireCaptureGmMenuSelection("Grant 50k Silver", TestGmTokenDriver.PathMoney50k, WaitTicksAfterSelection: 20),
                        new WireCaptureGmMenuSelection("Open Shop 39", TestGmTokenDriver.PathOpenShop39, WaitTicksAfterSelection: 20)
                    ],
                    SettleTicksAfterSetup: 5,
                    ExpectedPreState: new WireCaptureActorStateExpectation(
                        MapId: 53,
                        InventoryMoney: new WireCaptureNumericExpectation(WireCaptureFieldMatcherKind.ExactInt64, ExpectedValue: preStep.Player.Money)))
            ],
            symbols: symbols,
            steps:
            [
                new WireCaptureStep(
                    StepId: "01_BuyMedicineFromShop39",
                    ActorId: "P1",
                    UserActionDescription: "Buy first medicine from Pharmacist Shop 39 into free bag cell",
                    C2sFrames: WireCaptureAutoSymbolizer.BuildSymbolicC2sFrames(cap, symbols, preStep),
                    ObservationTicks: Math.Max(10, (int)(cap.EndTickFrames - cap.BaseTickFrames) + 4),
                    ExpectedS2cContract: WireCaptureAutoSymbolizer.BuildExpectedS2cContract(cap, symbols, includePeriodicVitalSyncs: false, extraTrackedOpcodes: tracked),
                    ExpectedActorStateAfter: new Dictionary<string, WireCaptureActorStateExpectation>(StringComparer.Ordinal)
                    {
                        ["P1"] = new(InventoryMoney: new WireCaptureNumericExpectation(WireCaptureFieldMatcherKind.ExactInt64, ExpectedValue: postStep.Player.Money))
                    })
            ]);

        string savedPath = WireCaptureStore.Save(doc);
        _output.WriteLine($"[WireCapture:Saved] {captureId} -> {savedPath} (sha256={doc.Metadata.ContentSha256})");
    }

    [Fact]
    public async Task Record_W06_RepositoryDepositAndWithdraw_ShouldPersistSignedCaptureFromLiveOracle()
    {
        OracleServerOptions? oracleOpts = OracleServerOptions.TryFromEnvironment();
        if (oracleOpts is null || oracleOpts.IsReplayMode)
        {
            Assert.Skip("Skipped Oracle wire capture recording (requires Live/Record mode with LEGACY_ORACLE_HOST).");
        }

        const string captureId = "W06-RepositoryDepositAndWithdraw";
        await using JxHeadlessClient client = await JxHeadlessClient.SpawnAsync(new JxHeadlessOptions
        {
            ServerIp = oracleOpts.Host,
            GatewayPort = oracleOpts.BishopPort
        });

        _ = await client.LoginAndEnterWorldAsync(
            oracleOpts.Host,
            oracleOpts.BishopPort,
            account: "gmw06",
            password: oracleOpts.Password,
            roleName: "HeroBoxW06",
            gender: 0,
            series: 0,
            nativePlaceId: 53);
        _ = await client.DismissSystemLoginHintIfOpenAsync();

        _ = await TestGmTokenDriver.InvokePathAsync(client, TestGmTokenDriver.PathMoney100k);
        WorldDomSnapshot preStep = await TestGmTokenDriver.InvokePathAsync(client, TestGmTokenDriver.PathOpenStoreBox);

        var symbols = new Dictionary<string, WireCaptureSymbolDefinition>(StringComparer.Ordinal)
        {
            ["$actor:P1"] = new(WireCaptureSymbolKind.PlayerSelf, "P1", preStep.Player.DwId)
        };

        // Step 1: Deposit 20,000 silver
        _ = await client.BeginWireCaptureAsync();
        _ = await client.StoreMoneyAsync(direction: 0, amount: 20_000);
        WorldDomSnapshot afterDeposit = await client.WaitUntilAsync(
            s => s.Player.Money == preStep.Player.Money - 20_000,
            TimeSpan.FromSeconds(5));
        uint depEnd = afterDeposit.TickCount + 6;
        afterDeposit = await client.WaitUntilAsync(s => s.TickCount >= depEnd, TimeSpan.FromSeconds(3));
        EndWireCaptureResult capDeposit = await client.EndWireCaptureAsync();

        // Step 2: Withdraw 10,000 silver
        _ = await client.BeginWireCaptureAsync();
        _ = await client.StoreMoneyAsync(direction: 1, amount: 10_000);
        WorldDomSnapshot afterWithdraw = await client.WaitUntilAsync(
            s => s.Player.Money == afterDeposit.Player.Money + 10_000,
            TimeSpan.FromSeconds(5));
        uint wdEnd = afterWithdraw.TickCount + 6;
        afterWithdraw = await client.WaitUntilAsync(s => s.TickCount >= wdEnd, TimeSpan.FromSeconds(3));
        EndWireCaptureResult capWithdraw = await client.EndWireCaptureAsync();

        HashSet<byte> tracked = [ProtocolOpcodes.s2c_syncmoney];
        var doc = WireCaptureStore.CreateSignedDocument(
            captureId: captureId,
            title: "Open StoreBox, deposit 20,000 silver and withdraw 10,000 silver (0x73 -> 0x61)",
            oracleProfile: oracleOpts.Profile,
            oraclePackageArchiveSha256: oracleOpts.PackageArchiveSha256,
            actors:
            [
                new WireCaptureActorDefinition(
                    ActorId: "P1",
                    AccountName: "gmw06",
                    RoleName: "HeroBoxW06",
                    Gender: 0,
                    Series: 0,
                    NativePlaceId: 53,
                    GmMenuSelections:
                    [
                        new WireCaptureGmMenuSelection("Grant 100k Silver", TestGmTokenDriver.PathMoney100k, WaitTicksAfterSelection: 20),
                        new WireCaptureGmMenuSelection("Open StoreBox", TestGmTokenDriver.PathOpenStoreBox, WaitTicksAfterSelection: 20)
                    ],
                    SettleTicksAfterSetup: 5,
                    ExpectedPreState: new WireCaptureActorStateExpectation(
                        MapId: 53,
                        InventoryMoney: new WireCaptureNumericExpectation(WireCaptureFieldMatcherKind.ExactInt64, ExpectedValue: preStep.Player.Money)))
            ],
            symbols: symbols,
            steps:
            [
                new WireCaptureStep(
                    StepId: "01_Deposit20kSilver",
                    ActorId: "P1",
                    UserActionDescription: "Deposit 20,000 silver from bag to StoreBox",
                    C2sFrames: WireCaptureAutoSymbolizer.BuildSymbolicC2sFrames(capDeposit, symbols, preStep),
                    ObservationTicks: Math.Max(10, (int)(capDeposit.EndTickFrames - capDeposit.BaseTickFrames) + 4),
                    ExpectedS2cContract: WireCaptureAutoSymbolizer.BuildExpectedS2cContract(capDeposit, symbols, includePeriodicVitalSyncs: false, extraTrackedOpcodes: tracked),
                    ExpectedActorStateAfter: new Dictionary<string, WireCaptureActorStateExpectation>(StringComparer.Ordinal)
                    {
                        ["P1"] = new(InventoryMoney: new WireCaptureNumericExpectation(WireCaptureFieldMatcherKind.ExactInt64, ExpectedValue: afterDeposit.Player.Money))
                    }),
                new WireCaptureStep(
                    StepId: "02_Withdraw10kSilver",
                    ActorId: "P1",
                    UserActionDescription: "Withdraw 10,000 silver from StoreBox to bag",
                    C2sFrames: WireCaptureAutoSymbolizer.BuildSymbolicC2sFrames(capWithdraw, symbols, afterDeposit),
                    ObservationTicks: Math.Max(10, (int)(capWithdraw.EndTickFrames - capWithdraw.BaseTickFrames) + 4),
                    ExpectedS2cContract: WireCaptureAutoSymbolizer.BuildExpectedS2cContract(capWithdraw, symbols, includePeriodicVitalSyncs: false, extraTrackedOpcodes: tracked),
                    ExpectedActorStateAfter: new Dictionary<string, WireCaptureActorStateExpectation>(StringComparer.Ordinal)
                    {
                        ["P1"] = new(InventoryMoney: new WireCaptureNumericExpectation(WireCaptureFieldMatcherKind.ExactInt64, ExpectedValue: afterWithdraw.Player.Money))
                    })
            ]);

        string savedPath = WireCaptureStore.Save(doc);
        _output.WriteLine($"[WireCapture:Saved] {captureId} -> {savedPath} (sha256={doc.Metadata.ContentSha256})");
    }

    [Fact]
    public async Task Record_W07_NovicePkModeSwitch_ShouldPersistSignedCaptureFromLiveOracle()
    {
        OracleServerOptions? oracleOpts = OracleServerOptions.TryFromEnvironment();
        if (oracleOpts is null || oracleOpts.IsReplayMode)
        {
            Assert.Skip("Skipped Oracle wire capture recording (requires Live/Record mode with LEGACY_ORACLE_HOST).");
        }

        const string captureId = "W07-NovicePkModeSwitch";
        await using JxHeadlessClient client = await JxHeadlessClient.SpawnAsync(new JxHeadlessOptions
        {
            ServerIp = oracleOpts.Host,
            GatewayPort = oracleOpts.BishopPort
        });

        WorldDomSnapshot preStep = await client.LoginAndEnterWorldAsync(
            oracleOpts.Host,
            oracleOpts.BishopPort,
            account: "gmw07",
            password: oracleOpts.Password,
            roleName: "HeroPkW07",
            gender: 0,
            series: 0,
            nativePlaceId: 53);
        preStep = await client.DismissSystemLoginHintIfOpenAsync();

        var symbols = new Dictionary<string, WireCaptureSymbolDefinition>(StringComparer.Ordinal)
        {
            ["$actor:P1"] = new(WireCaptureSymbolKind.PlayerSelf, "P1", preStep.Player.DwId)
        };

        _ = await client.BeginWireCaptureAsync();
        _ = await client.SwitchPkAsync(1);
        WorldDomSnapshot after1 = await client.WaitUntilAsync(static s => s.Player.PkFlag == 1, TimeSpan.FromSeconds(4));
        _ = await client.SwitchPkAsync(2);
        WorldDomSnapshot after2 = await client.WaitUntilAsync(static s => s.Player.PkFlag == 2, TimeSpan.FromSeconds(4));
        _ = await client.SwitchPkAsync(0);
        WorldDomSnapshot after0 = await client.WaitUntilAsync(static s => s.Player.PkFlag == 0, TimeSpan.FromSeconds(4));
        uint endTick = after0.TickCount + 6;
        _ = await client.WaitUntilAsync(s => s.TickCount >= endTick, TimeSpan.FromSeconds(3));
        EndWireCaptureResult cap = await client.EndWireCaptureAsync();

        HashSet<byte> tracked = [ProtocolOpcodes.s2c_pksyncnormalflag];
        var doc = WireCaptureStore.CreateSignedDocument(
            captureId: captureId,
            title: "Novice switches PK mode Peace (0) -> Fight (1) -> Slaughter (2) -> Peace (0)",
            oracleProfile: oracleOpts.Profile,
            oraclePackageArchiveSha256: oracleOpts.PackageArchiveSha256,
            actors:
            [
                new WireCaptureActorDefinition(
                    ActorId: "P1",
                    AccountName: "gmw07",
                    RoleName: "HeroPkW07",
                    Gender: 0,
                    Series: 0,
                    NativePlaceId: 53,
                    GmMenuSelections: [],
                    SettleTicksAfterSetup: 5,
                    ExpectedPreState: new WireCaptureActorStateExpectation(MapId: 53))
            ],
            symbols: symbols,
            steps:
            [
                new WireCaptureStep(
                    StepId: "01_SwitchPkModes0120",
                    ActorId: "P1",
                    UserActionDescription: "Switch PK mode 0 -> 1 -> 2 -> 0 in Ba Lang Huyen",
                    C2sFrames: WireCaptureAutoSymbolizer.BuildSymbolicC2sFrames(cap, symbols, preStep),
                    ObservationTicks: Math.Max(15, (int)(cap.EndTickFrames - cap.BaseTickFrames) + 6),
                    ExpectedS2cContract: WireCaptureAutoSymbolizer.BuildExpectedS2cContract(cap, symbols, includePeriodicVitalSyncs: false, defaultTickToleranceFrames: 8, extraTrackedOpcodes: tracked),
                    ExpectedActorStateAfter: new Dictionary<string, WireCaptureActorStateExpectation>(StringComparer.Ordinal)
                    {
                        ["P1"] = new(MapId: 53)
                    })
            ]);

        string savedPath = WireCaptureStore.Save(doc);
        _output.WriteLine($"[WireCapture:Saved] {captureId} -> {savedPath} (sha256={doc.Metadata.ContentSha256})");
    }

    [Fact]
    public async Task Record_W08_MultiActorNearbyChat_ShouldPersistSignedCaptureFromLiveOracle()
    {
        OracleServerOptions? oracleOpts = OracleServerOptions.TryFromEnvironment();
        if (oracleOpts is null || oracleOpts.IsReplayMode)
        {
            Assert.Skip("Skipped Oracle wire capture recording (requires Live/Record mode with LEGACY_ORACLE_HOST).");
        }

        const string captureId = "W08-MultiActorNearbyChat";
        await using JxHeadlessClient clientA = await JxHeadlessClient.SpawnAsync(new JxHeadlessOptions { ServerIp = oracleOpts.Host, GatewayPort = oracleOpts.BishopPort });
        await using JxHeadlessClient clientB = await JxHeadlessClient.SpawnAsync(new JxHeadlessOptions { ServerIp = oracleOpts.Host, GatewayPort = oracleOpts.BishopPort });

        _ = await clientA.LoginAndEnterWorldAsync(oracleOpts.Host, oracleOpts.BishopPort, "gmw08a", oracleOpts.Password, "ChatPeerA", 0, 0, 53);
        _ = await clientA.DismissSystemLoginHintIfOpenAsync();
        _ = await clientB.LoginAndEnterWorldAsync(oracleOpts.Host, oracleOpts.BishopPort, "gmw08b", oracleOpts.Password, "ChatPeerB", 1, 2, 53);
        _ = await clientB.DismissSystemLoginHintIfOpenAsync();

        // Bring both peers to exact Ba Lang center via MECE Atomic Teleport
        WorldDomSnapshot preA = await TestGmTokenDriver.TeleportToCityAsync(clientA, TestGmTokenDriver.TeleportBaLang53, 53);
        WorldDomSnapshot preB = await TestGmTokenDriver.TeleportToCityAsync(clientB, TestGmTokenDriver.TeleportBaLang53, 53);

        var symbols = new Dictionary<string, WireCaptureSymbolDefinition>(StringComparer.Ordinal)
        {
            ["$actor:P1"] = new(WireCaptureSymbolKind.PlayerSelf, "P1", preA.Player.DwId),
            ["$actor:P2"] = new(WireCaptureSymbolKind.PlayerSelf, "P2", preB.Player.DwId)
        };

        _ = await clientA.BeginWireCaptureAsync();
        _ = await clientB.BeginWireCaptureAsync();
        _ = await clientA.SendChatAsync(channel: 2, messageUnicode: "Chao RecordPlay");
        uint endTickB = preB.TickCount + 12;
        _ = await clientB.WaitUntilAsync(s => s.TickCount >= endTickB, TimeSpan.FromSeconds(5));
        EndWireCaptureResult capA = await clientA.EndWireCaptureAsync();
        EndWireCaptureResult capB = await clientB.EndWireCaptureAsync();

        HashSet<byte> tracked = [ProtocolOpcodes.s2c_chatgetsomeone];
        var doc = WireCaptureStore.CreateSignedDocument(
            captureId: captureId,
            title: "Multi-actor nearby chat: P1 broadcasts nearby message and P2 receives s2c_chatgetsomeone (0x7E)",
            oracleProfile: oracleOpts.Profile,
            oraclePackageArchiveSha256: oracleOpts.PackageArchiveSha256,
            actors:
            [
                new WireCaptureActorDefinition(
                    ActorId: "P1",
                    AccountName: "gmw08a",
                    RoleName: "ChatPeerA",
                    Gender: 0,
                    Series: 0,
                    NativePlaceId: 53,
                    GmMenuSelections:
                    [
                        new WireCaptureGmMenuSelection("Teleport Ba Lang 53", [TestGmTokenDriver.MenuTeleport, TestGmTokenDriver.TeleportBaLang53], WaitTicksAfterSelection: 20, ExpectMapIdAfterSelection: 53)
                    ],
                    SettleTicksAfterSetup: 5,
                    ExpectedPreState: new WireCaptureActorStateExpectation(MapId: 53)),
                new WireCaptureActorDefinition(
                    ActorId: "P2",
                    AccountName: "gmw08b",
                    RoleName: "ChatPeerB",
                    Gender: 1,
                    Series: 2,
                    NativePlaceId: 53,
                    GmMenuSelections:
                    [
                        new WireCaptureGmMenuSelection("Teleport Ba Lang 53", [TestGmTokenDriver.MenuTeleport, TestGmTokenDriver.TeleportBaLang53], WaitTicksAfterSelection: 20, ExpectMapIdAfterSelection: 53)
                    ],
                    SettleTicksAfterSetup: 5,
                    ExpectedPreState: new WireCaptureActorStateExpectation(MapId: 53))
            ],
            symbols: symbols,
            steps:
            [
                new WireCaptureStep(
                    StepId: "01_P1SendsNearbyChatObservedByP2",
                    ActorId: "P1",
                    UserActionDescription: "P1 sends nearby chat in Ba Lang center; P2 receives 0x7E",
                    C2sFrames: WireCaptureAutoSymbolizer.BuildSymbolicC2sFrames(capA, symbols, preA),
                    ObservationTicks: Math.Max(14, (int)(capB.EndTickFrames - capB.BaseTickFrames) + 4),
                    ExpectedS2cContract: WireCaptureAutoSymbolizer.BuildExpectedS2cContract(
                        capB,
                        symbols,
                        includePeriodicVitalSyncs: false,
                        defaultTickToleranceFrames: 8,
                        observerActorId: "P2",
                        extraTrackedOpcodes: tracked),
                    ExpectedActorStateAfter: new Dictionary<string, WireCaptureActorStateExpectation>(StringComparer.Ordinal)
                    {
                        ["P1"] = new(MapId: 53),
                        ["P2"] = new(MapId: 53)
                    })
            ]);

        string savedPath = WireCaptureStore.Save(doc);
        _output.WriteLine($"[WireCapture:Saved] {captureId} -> {savedPath} (sha256={doc.Metadata.ContentSha256})");
    }

    [Fact]
    public async Task Record_W09_MultiActorPartyInvite_ShouldPersistSignedCaptureFromLiveOracle()
    {
        OracleServerOptions? oracleOpts = OracleServerOptions.TryFromEnvironment();
        if (oracleOpts is null || oracleOpts.IsReplayMode)
        {
            Assert.Skip("Skipped Oracle wire capture recording (requires Live/Record mode with LEGACY_ORACLE_HOST).");
        }

        const string captureId = "W09-MultiActorPartyInvite";
        await using JxHeadlessClient clientA = await JxHeadlessClient.SpawnAsync(new JxHeadlessOptions { ServerIp = oracleOpts.Host, GatewayPort = oracleOpts.BishopPort });
        await using JxHeadlessClient clientB = await JxHeadlessClient.SpawnAsync(new JxHeadlessOptions { ServerIp = oracleOpts.Host, GatewayPort = oracleOpts.BishopPort });

        _ = await clientA.LoginAndEnterWorldAsync(oracleOpts.Host, oracleOpts.BishopPort, "gmw09a", oracleOpts.Password, "PartyPeerA", 0, 0, 53);
        _ = await clientA.DismissSystemLoginHintIfOpenAsync();
        _ = await clientB.LoginAndEnterWorldAsync(oracleOpts.Host, oracleOpts.BishopPort, "gmw09b", oracleOpts.Password, "PartyPeerB", 1, 2, 53);
        _ = await clientB.DismissSystemLoginHintIfOpenAsync();

        WorldDomSnapshot preA = await TestGmTokenDriver.TeleportToCityAsync(clientA, TestGmTokenDriver.TeleportBaLang53, 53);
        WorldDomSnapshot preB = await TestGmTokenDriver.TeleportToCityAsync(clientB, TestGmTokenDriver.TeleportBaLang53, 53);

        var symbols = new Dictionary<string, WireCaptureSymbolDefinition>(StringComparer.Ordinal)
        {
            ["$actor:P1"] = new(WireCaptureSymbolKind.PlayerSelf, "P1", preA.Player.DwId),
            ["$actor:P2"] = new(WireCaptureSymbolKind.PlayerSelf, "P2", preB.Player.DwId)
        };

        _ = await clientA.BeginWireCaptureAsync();
        _ = await clientB.BeginWireCaptureAsync();
        _ = await clientA.CreateTeamAsync();
        uint midTick = preA.TickCount + 6;
        _ = await clientA.WaitUntilAsync(s => s.TickCount >= midTick, TimeSpan.FromSeconds(4));
        _ = await clientA.InviteToTeamAsync(preB.Player.DwId);
        uint endTickB = preB.TickCount + 18;
        _ = await clientB.WaitUntilAsync(s => s.TickCount >= endTickB, TimeSpan.FromSeconds(5));
        EndWireCaptureResult capA = await clientA.EndWireCaptureAsync();
        EndWireCaptureResult capB = await clientB.EndWireCaptureAsync();

        HashSet<byte> tracked = [ProtocolOpcodes.s2c_teamprotocol];
        var combinedContract = new List<WireCaptureExpectedS2cEvent>();
        combinedContract.AddRange(WireCaptureAutoSymbolizer.BuildExpectedS2cContract(capA, symbols, includePeriodicVitalSyncs: false, defaultTickToleranceFrames: 10, observerActorId: "P1", extraTrackedOpcodes: tracked));
        combinedContract.AddRange(WireCaptureAutoSymbolizer.BuildExpectedS2cContract(capB, symbols, includePeriodicVitalSyncs: false, defaultTickToleranceFrames: 10, observerActorId: "P2", extraTrackedOpcodes: tracked));

        var doc = WireCaptureStore.CreateSignedDocument(
            captureId: captureId,
            title: "Multi-actor party: P1 creates team and invites P2; both receive s2c_teamprotocol (0x69)",
            oracleProfile: oracleOpts.Profile,
            oraclePackageArchiveSha256: oracleOpts.PackageArchiveSha256,
            actors:
            [
                new WireCaptureActorDefinition(
                    ActorId: "P1",
                    AccountName: "gmw09a",
                    RoleName: "PartyPeerA",
                    Gender: 0,
                    Series: 0,
                    NativePlaceId: 53,
                    GmMenuSelections:
                    [
                        new WireCaptureGmMenuSelection("Teleport Ba Lang 53", [TestGmTokenDriver.MenuTeleport, TestGmTokenDriver.TeleportBaLang53], WaitTicksAfterSelection: 20, ExpectMapIdAfterSelection: 53)
                    ],
                    SettleTicksAfterSetup: 5,
                    ExpectedPreState: new WireCaptureActorStateExpectation(MapId: 53)),
                new WireCaptureActorDefinition(
                    ActorId: "P2",
                    AccountName: "gmw09b",
                    RoleName: "PartyPeerB",
                    Gender: 1,
                    Series: 2,
                    NativePlaceId: 53,
                    GmMenuSelections:
                    [
                        new WireCaptureGmMenuSelection("Teleport Ba Lang 53", [TestGmTokenDriver.MenuTeleport, TestGmTokenDriver.TeleportBaLang53], WaitTicksAfterSelection: 20, ExpectMapIdAfterSelection: 53)
                    ],
                    SettleTicksAfterSetup: 5,
                    ExpectedPreState: new WireCaptureActorStateExpectation(MapId: 53))
            ],
            symbols: symbols,
            steps:
            [
                new WireCaptureStep(
                    StepId: "01_P1CreatesTeamAndInvitesP2",
                    ActorId: "P1",
                    UserActionDescription: "P1 creates team and invites P2; both actors receive s2c_teamprotocol",
                    C2sFrames: WireCaptureAutoSymbolizer.BuildSymbolicC2sFrames(capA, symbols, preA),
                    ObservationTicks: Math.Max(22, (int)(capB.EndTickFrames - capB.BaseTickFrames) + 6),
                    ExpectedS2cContract: combinedContract,
                    ExpectedActorStateAfter: new Dictionary<string, WireCaptureActorStateExpectation>(StringComparer.Ordinal)
                    {
                        ["P1"] = new(MapId: 53),
                        ["P2"] = new(MapId: 53)
                    })
            ]);

        string savedPath = WireCaptureStore.Save(doc);
        _output.WriteLine($"[WireCapture:Saved] {captureId} -> {savedPath} (sha256={doc.Metadata.ContentSha256})");
    }

    [Fact]
    public async Task Record_W10_MultiActorPlayerTradeInvite_ShouldPersistSignedCaptureFromLiveOracle()
    {
        OracleServerOptions? oracleOpts = OracleServerOptions.TryFromEnvironment();
        if (oracleOpts is null || oracleOpts.IsReplayMode)
        {
            Assert.Skip("Skipped Oracle wire capture recording (requires Live/Record mode with LEGACY_ORACLE_HOST).");
        }

        const string captureId = "W10-MultiActorPlayerTradeInvite";
        await using JxHeadlessClient clientA = await JxHeadlessClient.SpawnAsync(new JxHeadlessOptions { ServerIp = oracleOpts.Host, GatewayPort = oracleOpts.BishopPort });
        await using JxHeadlessClient clientB = await JxHeadlessClient.SpawnAsync(new JxHeadlessOptions { ServerIp = oracleOpts.Host, GatewayPort = oracleOpts.BishopPort });

        _ = await clientA.LoginAndEnterWorldAsync(oracleOpts.Host, oracleOpts.BishopPort, "gmw10a", oracleOpts.Password, "TradePeerA", 0, 0, 53);
        _ = await clientA.DismissSystemLoginHintIfOpenAsync();
        _ = await clientB.LoginAndEnterWorldAsync(oracleOpts.Host, oracleOpts.BishopPort, "gmw10b", oracleOpts.Password, "TradePeerB", 1, 2, 53);
        _ = await clientB.DismissSystemLoginHintIfOpenAsync();

        _ = await TestGmTokenDriver.EnsureLevel10Async(clientA);
        _ = await TestGmTokenDriver.EnsureLevel10Async(clientB);
        WorldDomSnapshot preA = await TestGmTokenDriver.TeleportToCityAsync(clientA, TestGmTokenDriver.TeleportBaLang53, 53);
        WorldDomSnapshot preB = await TestGmTokenDriver.TeleportToCityAsync(clientB, TestGmTokenDriver.TeleportBaLang53, 53);

        var symbols = new Dictionary<string, WireCaptureSymbolDefinition>(StringComparer.Ordinal)
        {
            ["$actor:P1"] = new(WireCaptureSymbolKind.PlayerSelf, "P1", preA.Player.DwId),
            ["$actor:P2"] = new(WireCaptureSymbolKind.PlayerSelf, "P2", preB.Player.DwId)
        };

        _ = await clientA.BeginWireCaptureAsync();
        _ = await clientB.BeginWireCaptureAsync();
        uint s2cSeqB = preB.RecentS2c.LastOrDefault()?.Seq ?? 0u;
        _ = await clientB.SetTradeOpenAsync(open: true);
        _ = await clientB.WaitUntilAsync(s => s.Player.MenuState == 2, TimeSpan.FromSeconds(4));
        _ = await clientA.InviteTradeAsync(preB.Player.DwId);
        _ = await clientB.WaitUntilAsync(
            s => s.RecentS2c.Any(p => p.Seq > s2cSeqB && p.Opcode == ProtocolOpcodes.s2c_tradeapplystart),
            TimeSpan.FromSeconds(5));
        EndWireCaptureResult capA = await clientA.EndWireCaptureAsync();
        EndWireCaptureResult capB = await clientB.EndWireCaptureAsync();

        HashSet<byte> tracked = [ProtocolOpcodes.s2c_tradeapplystart];
        var doc = WireCaptureStore.CreateSignedDocument(
            captureId: captureId,
            title: "Multi-actor trade invite: P1 opens trade and invites P2; P2 receives s2c_tradeapplystart (0x8B)",
            oracleProfile: oracleOpts.Profile,
            oraclePackageArchiveSha256: oracleOpts.PackageArchiveSha256,
            actors:
            [
                new WireCaptureActorDefinition(
                    ActorId: "P1",
                    AccountName: "gmw10a",
                    RoleName: "TradePeerA",
                    Gender: 0,
                    Series: 0,
                    NativePlaceId: 53,
                    GmMenuSelections:
                    [
                        new WireCaptureGmMenuSelection("Level 10", TestGmTokenDriver.PathLevel10, WaitTicksAfterSelection: 20),
                        new WireCaptureGmMenuSelection("Teleport Ba Lang 53", [TestGmTokenDriver.MenuTeleport, TestGmTokenDriver.TeleportBaLang53], WaitTicksAfterSelection: 20, ExpectMapIdAfterSelection: 53)
                    ],
                    SettleTicksAfterSetup: 5,
                    ExpectedPreState: new WireCaptureActorStateExpectation(MapId: 53, Level: 10)),
                new WireCaptureActorDefinition(
                    ActorId: "P2",
                    AccountName: "gmw10b",
                    RoleName: "TradePeerB",
                    Gender: 1,
                    Series: 2,
                    NativePlaceId: 53,
                    GmMenuSelections:
                    [
                        new WireCaptureGmMenuSelection("Level 10", TestGmTokenDriver.PathLevel10, WaitTicksAfterSelection: 20),
                        new WireCaptureGmMenuSelection("Teleport Ba Lang 53", [TestGmTokenDriver.MenuTeleport, TestGmTokenDriver.TeleportBaLang53], WaitTicksAfterSelection: 20, ExpectMapIdAfterSelection: 53)
                    ],
                    SettleTicksAfterSetup: 5,
                    ExpectedPreState: new WireCaptureActorStateExpectation(MapId: 53, Level: 10))
            ],
            symbols: symbols,
            steps:
            [
                new WireCaptureStep(
                    StepId: "01_P1InvitesP2ToTrade",
                    ActorId: "P1",
                    UserActionDescription: "P1 opens trade state and sends trade invite to P2; P2 receives 0x8B",
                    C2sFrames: WireCaptureAutoSymbolizer.BuildSymbolicC2sFrames(capA, symbols, preA),
                    ObservationTicks: Math.Max(22, (int)(capB.EndTickFrames - capB.BaseTickFrames) + 6),
                    ExpectedS2cContract: WireCaptureAutoSymbolizer.BuildExpectedS2cContract(
                        capB,
                        symbols,
                        includePeriodicVitalSyncs: false,
                        defaultTickToleranceFrames: 10,
                        observerActorId: "P2",
                        extraTrackedOpcodes: tracked),
                    ExpectedActorStateAfter: new Dictionary<string, WireCaptureActorStateExpectation>(StringComparer.Ordinal)
                    {
                        ["P1"] = new(MapId: 53),
                        ["P2"] = new(MapId: 53)
                    })
            ]);

        string savedPath = WireCaptureStore.Save(doc);
        _output.WriteLine($"[WireCapture:Saved] {captureId} -> {savedPath} (sha256={doc.Metadata.ContentSha256})");
    }

    [Fact]
    public async Task Record_W11_MultiActorFriendInvite_ShouldPersistSignedCaptureFromLiveOracle()
    {
        OracleServerOptions? oracleOpts = OracleServerOptions.TryFromEnvironment();
        if (oracleOpts is null || oracleOpts.IsReplayMode)
        {
            Assert.Skip("Skipped Oracle wire capture recording (requires Live/Record mode with LEGACY_ORACLE_HOST).");
        }

        const string captureId = "W11-MultiActorFriendInvite";
        await using JxHeadlessClient clientA = await JxHeadlessClient.SpawnAsync(new JxHeadlessOptions { ServerIp = oracleOpts.Host, GatewayPort = oracleOpts.BishopPort });
        await using JxHeadlessClient clientB = await JxHeadlessClient.SpawnAsync(new JxHeadlessOptions { ServerIp = oracleOpts.Host, GatewayPort = oracleOpts.BishopPort });

        _ = await clientA.LoginAndEnterWorldAsync(oracleOpts.Host, oracleOpts.BishopPort, "gmw11a", oracleOpts.Password, "FriendPeerA", 0, 0, 53);
        _ = await clientA.DismissSystemLoginHintIfOpenAsync();
        _ = await clientB.LoginAndEnterWorldAsync(oracleOpts.Host, oracleOpts.BishopPort, "gmw11b", oracleOpts.Password, "FriendPeerB", 1, 2, 53);
        _ = await clientB.DismissSystemLoginHintIfOpenAsync();

        WorldDomSnapshot preA = await TestGmTokenDriver.TeleportToCityAsync(clientA, TestGmTokenDriver.TeleportBaLang53, 53);
        WorldDomSnapshot preB = await TestGmTokenDriver.TeleportToCityAsync(clientB, TestGmTokenDriver.TeleportBaLang53, 53);

        var symbols = new Dictionary<string, WireCaptureSymbolDefinition>(StringComparer.Ordinal)
        {
            ["$actor:P1"] = new(WireCaptureSymbolKind.PlayerSelf, "P1", preA.Player.DwId),
            ["$actor:P2"] = new(WireCaptureSymbolKind.PlayerSelf, "P2", preB.Player.DwId)
        };

        _ = await clientA.BeginWireCaptureAsync();
        _ = await clientB.BeginWireCaptureAsync();
        uint s2cSeqB = preB.RecentS2c.LastOrDefault()?.Seq ?? 0u;
        _ = await clientA.InviteFriendAsync("FriendPeerB");
        _ = await clientB.WaitUntilAsync(
            s => s.RecentS2c.Any(p => p.Seq > s2cSeqB && p.Opcode == ProtocolOpcodes.s2c_chatapplyaddfriend),
            TimeSpan.FromSeconds(5));
        EndWireCaptureResult capA = await clientA.EndWireCaptureAsync();
        EndWireCaptureResult capB = await clientB.EndWireCaptureAsync();

        HashSet<byte> tracked = [ProtocolOpcodes.s2c_chatapplyaddfriend];
        var doc = WireCaptureStore.CreateSignedDocument(
            captureId: captureId,
            title: "Multi-actor social graph: P1 sends friend invite to P2; P2 receives s2c_chatapplyaddfriend (0x6A)",
            oracleProfile: oracleOpts.Profile,
            oraclePackageArchiveSha256: oracleOpts.PackageArchiveSha256,
            actors:
            [
                new WireCaptureActorDefinition(
                    ActorId: "P1",
                    AccountName: "gmw11a",
                    RoleName: "FriendPeerA",
                    Gender: 0,
                    Series: 0,
                    NativePlaceId: 53,
                    GmMenuSelections:
                    [
                        new WireCaptureGmMenuSelection("Teleport Ba Lang 53", [TestGmTokenDriver.MenuTeleport, TestGmTokenDriver.TeleportBaLang53], WaitTicksAfterSelection: 20, ExpectMapIdAfterSelection: 53)
                    ],
                    SettleTicksAfterSetup: 5,
                    ExpectedPreState: new WireCaptureActorStateExpectation(MapId: 53)),
                new WireCaptureActorDefinition(
                    ActorId: "P2",
                    AccountName: "gmw11b",
                    RoleName: "FriendPeerB",
                    Gender: 1,
                    Series: 2,
                    NativePlaceId: 53,
                    GmMenuSelections:
                    [
                        new WireCaptureGmMenuSelection("Teleport Ba Lang 53", [TestGmTokenDriver.MenuTeleport, TestGmTokenDriver.TeleportBaLang53], WaitTicksAfterSelection: 20, ExpectMapIdAfterSelection: 53)
                    ],
                    SettleTicksAfterSetup: 5,
                    ExpectedPreState: new WireCaptureActorStateExpectation(MapId: 53))
            ],
            symbols: symbols,
            steps:
            [
                new WireCaptureStep(
                    StepId: "01_P1InvitesP2AsFriend",
                    ActorId: "P1",
                    UserActionDescription: "P1 sends friend invite to FriendPeerB; P2 receives 0x6A",
                    C2sFrames: WireCaptureAutoSymbolizer.BuildSymbolicC2sFrames(capA, symbols, preA),
                    ObservationTicks: Math.Max(18, (int)(capB.EndTickFrames - capB.BaseTickFrames) + 6),
                    ExpectedS2cContract: WireCaptureAutoSymbolizer.BuildExpectedS2cContract(
                        capB,
                        symbols,
                        includePeriodicVitalSyncs: false,
                        defaultTickToleranceFrames: 10,
                        observerActorId: "P2",
                        extraTrackedOpcodes: tracked),
                    ExpectedActorStateAfter: new Dictionary<string, WireCaptureActorStateExpectation>(StringComparer.Ordinal)
                    {
                        ["P1"] = new(MapId: 53),
                        ["P2"] = new(MapId: 53)
                    })
            ]);

        string savedPath = WireCaptureStore.Save(doc);
        _output.WriteLine($"[WireCapture:Saved] {captureId} -> {savedPath} (sha256={doc.Metadata.ContentSha256})");
    }

    [Fact]
    public async Task Record_W12_PlayerAttributeAllocation_ShouldPersistSignedCaptureFromLiveOracle()
    {
        OracleServerOptions? oracleOpts = OracleServerOptions.TryFromEnvironment();
        if (oracleOpts is null || oracleOpts.IsReplayMode)
        {
            Assert.Skip("Skipped Oracle wire capture recording (requires Live/Record mode with LEGACY_ORACLE_HOST).");
        }

        const string captureId = "W12-PlayerAttributeAllocation";
        await using JxHeadlessClient client = await JxHeadlessClient.SpawnAsync(new JxHeadlessOptions
        {
            ServerIp = oracleOpts.Host,
            GatewayPort = oracleOpts.BishopPort
        });

        _ = await client.LoginAndEnterWorldAsync(oracleOpts.Host, oracleOpts.BishopPort, "gmw12", oracleOpts.Password, "AttrHeroW12", 0, 0, 53);
        _ = await client.DismissSystemLoginHintIfOpenAsync();
        WorldDomSnapshot preStep = await TestGmTokenDriver.InvokePathAsync(client, TestGmTokenDriver.PathLevel10);

        var symbols = new Dictionary<string, WireCaptureSymbolDefinition>(StringComparer.Ordinal)
        {
            ["$actor:P1"] = new(WireCaptureSymbolKind.PlayerSelf, "P1", preStep.Player.DwId)
        };

        _ = await client.BeginWireCaptureAsync();
        _ = await client.AddBaseAttributeAsync(attribute: 0, points: 5);
        _ = await client.AddBaseAttributeAsync(attribute: 1, points: 5);
        uint endTick = preStep.TickCount + 12;
        _ = await client.WaitUntilAsync(s => s.TickCount >= endTick, TimeSpan.FromSeconds(5));
        EndWireCaptureResult cap = await client.EndWireCaptureAsync();

        HashSet<byte> tracked = [ProtocolOpcodes.s2c_playerattributesync];
        var doc = WireCaptureStore.CreateSignedDocument(
            captureId: captureId,
            title: "Allocate +5 Strength and +5 Dexterity at Level 10 and verify s2c_playerattributesync (0x5D)",
            oracleProfile: oracleOpts.Profile,
            oraclePackageArchiveSha256: oracleOpts.PackageArchiveSha256,
            actors:
            [
                new WireCaptureActorDefinition(
                    ActorId: "P1",
                    AccountName: "gmw12",
                    RoleName: "AttrHeroW12",
                    Gender: 0,
                    Series: 0,
                    NativePlaceId: 53,
                    GmMenuSelections:
                    [
                        new WireCaptureGmMenuSelection("Level 10", TestGmTokenDriver.PathLevel10, WaitTicksAfterSelection: 20)
                    ],
                    SettleTicksAfterSetup: 5,
                    ExpectedPreState: new WireCaptureActorStateExpectation(MapId: 53, Level: 10))
            ],
            symbols: symbols,
            steps:
            [
                new WireCaptureStep(
                    StepId: "01_AllocateStrengthAndDexterity",
                    ActorId: "P1",
                    UserActionDescription: "Allocate +5 Strength and +5 Dexterity",
                    C2sFrames: WireCaptureAutoSymbolizer.BuildSymbolicC2sFrames(cap, symbols, preStep),
                    ObservationTicks: Math.Max(14, (int)(cap.EndTickFrames - cap.BaseTickFrames) + 4),
                    ExpectedS2cContract: WireCaptureAutoSymbolizer.BuildExpectedS2cContract(cap, symbols, includePeriodicVitalSyncs: false, defaultTickToleranceFrames: 8, extraTrackedOpcodes: tracked),
                    ExpectedActorStateAfter: new Dictionary<string, WireCaptureActorStateExpectation>(StringComparer.Ordinal)
                    {
                        ["P1"] = new(MapId: 53, Level: 10)
                    })
            ]);

        string savedPath = WireCaptureStore.Save(doc);
        _output.WriteLine($"[WireCapture:Saved] {captureId} -> {savedPath} (sha256={doc.Metadata.ContentSha256})");
    }

    private static (int GridX, int GridY) FindFreeBagCell(IReadOnlyList<InventoryItemSnapshot> items, int width, int height)
    {
        const int BagCols = 6;
        const int BagRows = 6;

        for (int x = BagCols - width; x >= 0; x--)
        {
            for (int y = BagRows - height; y >= 0; y--)
            {
                bool collides = false;
                foreach (InventoryItemSnapshot item in items)
                {
                    if (item.Place != PlaceEquipRoom)
                    {
                        continue;
                    }

                    int iw = Math.Max(1, item.Width);
                    int ih = Math.Max(1, item.Height);
                    if (x < item.GridX + iw && x + width > item.GridX &&
                        y < item.GridY + ih && y + height > item.GridY)
                    {
                        collides = true;
                        break;
                    }
                }

                if (!collides)
                {
                    return (x, y);
                }
            }
        }

        throw new InvalidOperationException($"No free {width}x{height} cell found in 6x6 EquipRoom.");
    }
}
