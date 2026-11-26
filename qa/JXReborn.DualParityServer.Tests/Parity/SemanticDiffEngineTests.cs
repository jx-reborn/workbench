using JXReborn.ClientDriver;
using JXReborn.DualParityServer.Diffing;
using JXReborn.DualParityServer.Tests.Fixtures;
using Xunit;

namespace JXReborn.DualParityServer.Tests.Parity;

public sealed class SemanticDiffEngineTests
{
    [Fact]
    public void Compare_WhenOnlyEphemeralIdsAndMinorPositionDiffer_ShouldPass()
    {
        var engine = new SemanticDiffEngine(new DiffOptions { PositionToleranceMps = 64 });

        WorldDomSnapshot legacy = TestSnapshotFactory.Create(
            pid: 1111,
            tick: 1000,
            playerDwId: 5001,
            mpsX: 1600,
            mpsY: 3200,
            items: [TestSnapshotFactory.CreateItem(serverItemId: 88001, place: 0, genre: 1, detail: 0, particular: 1, stack: 3)]);

        WorldDomSnapshot reborn = TestSnapshotFactory.Create(
            pid: 2222,
            tick: 9999,
            playerDwId: 9002,
            mpsX: 1630,
            mpsY: 3180,
            items: [TestSnapshotFactory.CreateItem(serverItemId: 12345, place: 0, genre: 1, detail: 0, particular: 1, stack: 3)]);

        StepComparisonResult result = engine.Compare(1, "IdleCheck", "AssertParity()", legacy, reborn);

        Assert.True(result.Passed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Compare_OnDialogPromptAndOptionsDivergence_ShouldFlagCriticalFindings()
    {
        var engine = new SemanticDiffEngine();

        WorldDomSnapshot legacy = TestSnapshotFactory.Create(
            dialog: TestSnapshotFactory.CreateDialog("Chao hiep khach! Co can ho tro gi khong?", "Nhan nhiem vu", "Thoat"));

        WorldDomSnapshot reborn = TestSnapshotFactory.Create(
            dialog: TestSnapshotFactory.CreateDialog("Chao ban! Chon chuc nang:", "Nhan nhiem vu", "Huy bo", "Thoat"));

        StepComparisonResult result = engine.Compare(
            stepIndex: 2,
            stepName: "DaTauDialog",
            actionSummary: "InteractNpc('Da Tau')",
            legacyAfter: legacy,
            rebornAfter: reborn,
            npcName: "Da Tau");

        Assert.False(result.Passed);
        Assert.Equal(2, result.CriticalCount);
        Assert.Contains(result.Findings, f => f.FieldPath == "Dialog.Prompt" && f.ClusterKey == "NpcDialog::Map53::Da Tau::Dialog.Prompt");
        Assert.Contains(result.Findings, f => f.FieldPath == "Dialog.Options.Count");
    }

    [Fact]
    public void Compare_OnInventoryMoneyAndPositionDrift_ShouldFlagFindings()
    {
        var engine = new SemanticDiffEngine(new DiffOptions { PositionToleranceMps = 64, CriticalPositionDriftMps = 256 });

        WorldDomSnapshot legacy = TestSnapshotFactory.Create(
            mpsX: 51150,
            mpsY: 102713,
            money: 5000,
            items: [TestSnapshotFactory.CreateItem(serverItemId: 100, place: 0, genre: 1, detail: 0, particular: 0, stack: 1)]);

        WorldDomSnapshot reborn = TestSnapshotFactory.Create(
            mpsX: 51550,
            mpsY: 102713,
            money: 4500,
            items: []);

        StepComparisonResult result = engine.Compare(3, "QuestReward", "SelectDialog(0)", legacy, reborn);

        Assert.False(result.Passed);
        Assert.Contains(result.Findings, f => f.Category == FindingCategory.PlayerState && f.FieldPath == "Player.Money");
        Assert.Contains(result.Findings, f => f.Category == FindingCategory.Position && f.Severity == FindingSeverity.Critical);
        Assert.Contains(result.Findings, f => f.Category == FindingCategory.Inventory);
    }

    [Fact]
    public void Compare_OnNpcsprRichDescribeWithDifferentDisplayNameEncoding_ShouldNormalizeNpcNameAndStillVerifyNpcSidAndBody()
    {
        // Ground truth: Map 53 Step550_C2S_0x6E (Chủ tiệm tạp hóa, NPCSID=219) observed in findings.jsonl
        // Legacy emits raw GBK NPC name bytes inside <<...>>; Reborn emits localized UTF-8->TCVN3 name.
        var engine = new SemanticDiffEngine();

        const string legacyPrompt = "<#><link=image[0,8]:#npcspr:?NPCSID=219?ACTION=0><<ễểằừặèÀẽ°ồ>><link>: Trước đây ta là người bán hàng rong, tích cóp được ít vốn, mở tiệm tạp hóa nhỏ này";
        const string rebornMatchingPrompt = "<#><link=image[0,8]:#npcspr:?NPCSID=219?ACTION=0><<Chủ tiệm tạp hóa>><link>: Trước đây ta là người bán hàng rong, tích cóp được ít vốn, mở tiệm tạp hóa nhỏ này";
        const string rebornWrongNpcSidPrompt = "<#><link=image[0,8]:#npcspr:?NPCSID=198?ACTION=0><<Chủ tiệm tạp hóa>><link>: Trước đây ta là người bán hàng rong, tích cóp được ít vốn, mở tiệm tạp hóa nhỏ này";
        const string rebornWrongBodyPrompt = "<#><link=image[0,8]:#npcspr:?NPCSID=219?ACTION=0><<Chủ tiệm tạp hóa>><link>: Nội dung sai";

        StepComparisonResult matchResult = engine.Compare(
            550,
            "OpenGrocerShop",
            "InteractNpc('Chủ tiệm tạp hóa')",
            TestSnapshotFactory.Create(dialog: TestSnapshotFactory.CreateDialog(legacyPrompt, "Giao dịch")),
            TestSnapshotFactory.Create(dialog: TestSnapshotFactory.CreateDialog(rebornMatchingPrompt, "Giao dịch")));

        Assert.True(matchResult.Passed);
        Assert.Empty(matchResult.Findings);

        StepComparisonResult wrongSidResult = engine.Compare(
            550,
            "OpenGrocerShop",
            "InteractNpc('Chủ tiệm tạp hóa')",
            TestSnapshotFactory.Create(dialog: TestSnapshotFactory.CreateDialog(legacyPrompt, "Giao dịch")),
            TestSnapshotFactory.Create(dialog: TestSnapshotFactory.CreateDialog(rebornWrongNpcSidPrompt, "Giao dịch")));

        Assert.False(wrongSidResult.Passed);
        Assert.Contains(wrongSidResult.Findings, f => f.FieldPath == "Dialog.Prompt");

        StepComparisonResult wrongBodyResult = engine.Compare(
            550,
            "OpenGrocerShop",
            "InteractNpc('Chủ tiệm tạp hóa')",
            TestSnapshotFactory.Create(dialog: TestSnapshotFactory.CreateDialog(legacyPrompt, "Giao dịch")),
            TestSnapshotFactory.Create(dialog: TestSnapshotFactory.CreateDialog(rebornWrongBodyPrompt, "Giao dịch")));

        Assert.False(wrongBodyResult.Passed);
        Assert.Contains(wrongBodyResult.Findings, f => f.FieldPath == "Dialog.Prompt");
    }

    [Fact]
    public void Compare_AcrossConsecutivePassiveWindowsWithUnchangedMoneyAndEquipmentSeriesVariance_ShouldDeduplicateSnowballAndReportSeriesAsWarning()
    {
        var engine = new SemanticDiffEngine();

        // Step 1009/929 Ground Truth: Du Khách random(0,5) money (50300 vs 50700) + A Phương Bố Cân (0,7,6,Lv1) random(0,4) Series (S2 vs S1) + 1 Jade Pendant (4,182,0,Lv1)
        WorldDomSnapshot legacyStep1 = TestSnapshotFactory.Create(
            money: 50300,
            items:
            [
                TestSnapshotFactory.CreateItem(serverItemId: 1, place: 3, genre: 0, detail: 7, particular: 6, level: 1, series: 2, stack: 1),
                TestSnapshotFactory.CreateItem(serverItemId: 2, place: 3, genre: 4, detail: 182, particular: 0, level: 1, series: 0, stack: 1),
            ]);
        WorldDomSnapshot rebornStep1 = TestSnapshotFactory.Create(
            money: 50700,
            items:
            [
                TestSnapshotFactory.CreateItem(serverItemId: 10, place: 3, genre: 0, detail: 7, particular: 6, level: 1, series: 1, stack: 1),
                TestSnapshotFactory.CreateItem(serverItemId: 11, place: 3, genre: 4, detail: 182, particular: 0, level: 1, series: 0, stack: 1),
            ]);

        StepComparisonResult r1 = engine.Compare(1009, "SellCalligraphy", "C2S 0x5F", legacyStep1, rebornStep1);
        Assert.Single(r1.Findings, static f => f.FieldPath == "Player.Money" && f.Severity == FindingSeverity.Critical);
        Assert.DoesNotContain(r1.Findings, static f => f.Category == FindingCategory.Inventory && f.Severity == FindingSeverity.Critical);
        Assert.Contains(r1.Findings, static f => f.Category == FindingCategory.Inventory && f.Severity == FindingSeverity.Warning);

        // Passive walk window: neither Legacy nor Reborn money or inventory changed -> must NOT snowball-repeat unchanged findings!
        StepComparisonResult r2 = engine.Compare(1010, "PassiveWalk", "C2S 0x4B", legacyStep1, rebornStep1, legacyBefore: legacyStep1, rebornBefore: rebornStep1);
        Assert.Empty(r2.Findings);

        // New genuine quantity divergence (e.g., Reborn double-grants Jade Pendant 1 -> 2) MUST still fire as Critical immediately!
        WorldDomSnapshot rebornDuplicate = TestSnapshotFactory.Create(
            money: 50700,
            items:
            [
                TestSnapshotFactory.CreateItem(serverItemId: 10, place: 3, genre: 0, detail: 7, particular: 6, level: 1, series: 1, stack: 1),
                TestSnapshotFactory.CreateItem(serverItemId: 11, place: 3, genre: 4, detail: 182, particular: 0, level: 1, series: 0, stack: 1),
                TestSnapshotFactory.CreateItem(serverItemId: 12, place: 3, genre: 4, detail: 182, particular: 0, level: 1, series: 0, stack: 1),
            ]);
        StepComparisonResult r3 = engine.Compare(1011, "DoubleGrantBug", "C2S 0x72", legacyStep1, rebornDuplicate, legacyBefore: legacyStep1, rebornBefore: rebornStep1);
        Assert.Single(r3.Findings);
        Assert.Equal(FindingSeverity.Critical, r3.Findings[0].Severity);
        Assert.Equal("Inventory[Place3:(4,182,0,Lv1)]", r3.Findings[0].FieldPath);
    }

    [Fact]
    public void Compare_AfterPositionOnlyDriftResetOrCrossMapSessionHandoff_ShouldNotReEmitCarryOverMoneyDiscrepancy()
    {
        // Ground truth: Map 53 mui2-final Steps 1071..1473 where Du Khach random(1000..1500) left L=50400 vs R=50600,
        // and ResetPositionStateTrackingOnly / cross-map reconnect (legacyBefore=null) previously re-emitted 18 Criticals.
        var engine = new SemanticDiffEngine();
        WorldDomSnapshot legacy1 = TestSnapshotFactory.Create(money: 50400, mpsX: 50000, mpsY: 100000);
        WorldDomSnapshot reborn1 = TestSnapshotFactory.Create(money: 50600, mpsX: 50600, mpsY: 100000);

        StepComparisonResult r1 = engine.Compare(1071, "SellCalligraphy", "C2S 0x5F", legacy1, reborn1);
        Assert.Contains(r1.Findings, static f => f.FieldPath == "Player.Money" && f.Severity == FindingSeverity.Critical);
        Assert.Contains(r1.Findings, static f => f.FieldPath == "Player.Mps" && f.Severity == FindingSeverity.Critical);

        // Position resync resets ONLY position tracking, preserving Player.Money carry-over deduplication
        engine.ResetPositionStateTrackingOnly();

        // Cross-map portal handoff starts a new GS session (legacyBefore: null, rebornBefore: null) with same Money
        WorldDomSnapshot legacyMap59 = TestSnapshotFactory.Create(mapId: 59, money: 50400, mpsX: 45600, mpsY: 111104);
        WorldDomSnapshot rebornMap59 = TestSnapshotFactory.Create(mapId: 59, money: 50600, mpsX: 45600, mpsY: 111104);
        StepComparisonResult r2 = engine.Compare(1350, "CrossMap59Sync", "C2S 0x43", legacyMap59, rebornMap59, legacyBefore: null, rebornBefore: null);
        Assert.DoesNotContain(r2.Findings, static f => f.FieldPath == "Player.Money");
    }

    [Fact]
    public void Compare_WhenLegacyAndRebornSelectDifferentLinesFromSameVerifiedVillagerPool_ShouldDowngradeToWarningButKeepCrossNpcMismatchCritical()
    {
        // Ground truth: ambient-npcs.dialogs.json entry 'ba-lang-la-gia-tham-tu' (Step 689/712) & A Phuong Lv1 (Step 81).
        var engine = new SemanticDiffEngine();

        WorldDomSnapshot legacyLaGia = TestSnapshotFactory.Create(
            dialog: TestSnapshotFactory.CreateDialog("Ôi! Không ngờ ta lấy phải một người nghèo túng, cả đời phải cực khổ, ta thật hối hận quá!"));
        WorldDomSnapshot rebornLaGia = TestSnapshotFactory.Create(
            dialog: TestSnapshotFactory.CreateDialog("Thời thanh xuân ta cũng có kha khá nhan sắc, nếu lúc đó có thể xuất giá vào nhà giàu có, thì cuộc sống có lẽ không còn phải lo nữa. "));

        StepComparisonResult samePoolResult = engine.Compare(689, "LaGiaThamTu", "C2S 0x6E", legacyLaGia, rebornLaGia);
        Assert.DoesNotContain(samePoolResult.Findings, static f => f.Severity == FindingSeverity.Critical);
        Assert.Contains(samePoolResult.Findings, static f => f.FieldPath == "Dialog.Prompt.RandomPool" && f.Severity == FindingSeverity.Warning);

        // Cross-NPC wrong prompt MUST remain Critical!
        WorldDomSnapshot rebornWrongNpc = TestSnapshotFactory.Create(
            dialog: TestSnapshotFactory.CreateDialog("Nha môn thông cáo"));
        StepComparisonResult wrongPoolResult = engine.Compare(690, "WrongNpcBug", "C2S 0x6E", legacyLaGia, rebornWrongNpc);
        Assert.Contains(wrongPoolResult.Findings, static f => f.FieldPath == "Dialog.Prompt" && f.Severity == FindingSeverity.Critical);
    }

    [Fact]
    public void Compare_OnDuKhachCalligraphyRandomSilverAndSubsequentEqualSpend_ShouldClassifyPoolAsWarningAndSuppressOffsetSnowball()
    {
        // Ground truth: Map 53 mui3-zero-crit Step 786 (ConfirmSellCalligraphy, 100 * (random(0,5) + 10) -> L=+1300, R=+1200)
        // followed by Step 807 (-100 Lightfoot) and Step 960 (Map 59 -100 Boatman).
        var engine = new SemanticDiffEngine();
        WorldDomSnapshot legacyBefore = TestSnapshotFactory.Create(money: 49300);
        WorldDomSnapshot rebornBefore = TestSnapshotFactory.Create(money: 49300);

        WorldDomSnapshot legacySale = TestSnapshotFactory.Create(
            money: 50600,
            dialog: TestSnapshotFactory.CreateDialog(
                "Nếu ngươi đã đồng ý bán cho ta, thì không được đổi ý đấy! Để xem ta còn bao nhiêu tiền, 5, 10, 15, 20... Tốt rồi, đưa ngươi 1300ngân lượng."));
        WorldDomSnapshot rebornSale = TestSnapshotFactory.Create(
            money: 50500,
            dialog: TestSnapshotFactory.CreateDialog(
                "Nếu ngươi đã đồng ý bán cho ta, thì không được đổi ý đấy! Để xem ta còn bao nhiêu tiền, 5, 10, 15, 20... Tốt rồi, đưa ngươi 1200ngân lượng."));

        StepComparisonResult saleResult = engine.Compare(786, "ConfirmSellCalligraphy", "C2S 0x5F", legacySale, rebornSale, legacyBefore, rebornBefore);
        Assert.NotEmpty(saleResult.Findings);
        Assert.DoesNotContain(saleResult.Findings, static f => f.Severity == FindingSeverity.Critical);
        Assert.Contains(saleResult.Findings, static f => f.FieldPath == "Dialog.Prompt.RandomPool" && f.Severity == FindingSeverity.Warning);
        Assert.Contains(saleResult.Findings, static f => f.FieldPath == "Player.Money.RandomRewardPool" && f.Severity == FindingSeverity.Warning);

        // Equal -100 spend on Step 807 preserves the +100 RNG offset without firing Critical
        WorldDomSnapshot legacyEnroll = TestSnapshotFactory.Create(money: 50500);
        WorldDomSnapshot rebornEnroll = TestSnapshotFactory.Create(money: 50400);
        StepComparisonResult enrollResult = engine.Compare(807, "AcceptLightfootPay100Silver", "C2S 0x5F", legacyEnroll, rebornEnroll, legacySale, rebornSale);
        Assert.DoesNotContain(enrollResult.Findings, static f => f.Severity == FindingSeverity.Critical);

        // Genuine money bug (Reborn deducts -200 instead of -100 -> offset changes from +100 to +200) MUST fire Critical!
        WorldDomSnapshot legacyBoat = TestSnapshotFactory.Create(mapId: 59, money: 50400);
        WorldDomSnapshot rebornBugBoat = TestSnapshotFactory.Create(mapId: 59, money: 50200);
        StepComparisonResult bugResult = engine.Compare(960, "WrongBoatFeeBug", "C2S 0x5F", legacyBoat, rebornBugBoat, legacyEnroll, rebornEnroll);
        Assert.Contains(bugResult.Findings, static f => f.FieldPath == "Player.Money" && f.Severity == FindingSeverity.Critical);
    }

    [Fact]
    public void Compare_OnAmbientMultiLevelFieldMonsterKill_ShouldClassifyPositiveScaledExpVarianceAsWarningWhileKeepingZeroKillCritical()
    {
        // Ground truth: Map 53 mui3-zero-crit Step 902 (wandering Huou Dom Lv1..5 -> L=+22 EXP vs R=+38 EXP at Player Lv20).
        var engine = new SemanticDiffEngine();
        WorldDomSnapshot legacyBefore = TestSnapshotFactory.Create(level: 20, exp: 3);
        WorldDomSnapshot rebornBefore = TestSnapshotFactory.Create(level: 20, exp: 3);

        // 1. If Reborn awards 0 EXP when Legacy awards +22 EXP -> MUST stay Critical!
        WorldDomSnapshot legacyKill = TestSnapshotFactory.Create(level: 20, exp: 25);
        WorldDomSnapshot rebornNoKill = TestSnapshotFactory.Create(level: 20, exp: 3);
        StepComparisonResult missedKill = engine.Compare(902, "MissedKillBug", "C2S 0x4D", legacyKill, rebornNoKill, legacyBefore, rebornBefore);
        Assert.Contains(missedKill.Findings, static f => f.FieldPath == "Player.Exp" && f.Severity == FindingSeverity.Critical);

        // 2. When BOTH award positive scaled starter monster EXP (Lv2 +22 vs Lv3 +38) -> Warning + offset tracked
        var cleanEngine = new SemanticDiffEngine();
        WorldDomSnapshot rebornLv3Kill = TestSnapshotFactory.Create(level: 20, exp: 41);
        StepComparisonResult varianceResult = cleanEngine.Compare(902, "DefeatAmbientSpottedDeer", "C2S 0x4D", legacyKill, rebornLv3Kill, legacyBefore, rebornBefore);
        Assert.NotEmpty(varianceResult.Findings);
        Assert.DoesNotContain(varianceResult.Findings, static f => f.Severity == FindingSeverity.Critical);
        Assert.Contains(varianceResult.Findings, static f => f.FieldPath == "Player.Exp.AmbientCombatVariance" && f.Severity == FindingSeverity.Warning);

        // Subsequent non-combat step preserves the -16 EXP offset without snowballing
        StepComparisonResult nextStep = cleanEngine.Compare(903, "AfterKillWalk", "C2S 0x4B", legacyKill, rebornLv3Kill, legacyKill, rebornLv3Kill);
        Assert.DoesNotContain(nextStep.Findings, static f => f.Severity == FindingSeverity.Critical);
    }

    [Fact]
    public void Compare_OnDaTauMainDialogPrompt_ShouldDowngradeValidRolledQuestsWithSameNumberToWarningButKeepTruncatedObjectiveOrMismatchedNumberCritical()
    {
        // Ground truth:
        // - Step 191 (F-20261125-0027): independent RNG roll on Quest #1 (Type 4 FindMaps vs Type 2 FindGoods) -> Warning (Dialog.Prompt.RandomPool)
        // - Step 193 (F-20261125-0028): truncated Reborn prompt "<enter><enter> Đây là nhiệm vụ thứ <color=green>1<color>," -> Critical (Dialog.Prompt)
        // - Quest counter desync (<color=green>1<color> vs <color=green>2<color>) -> Critical (Dialog.Prompt)
        var engine = new SemanticDiffEngine();

        WorldDomSnapshot legacyRolled = TestSnapshotFactory.Create(
            dialog: TestSnapshotFactory.CreateDialog(
                "<enter><enter> Đây là nhiệm vụ thứ <color=green>1<color>, Ngươi hãy đến <color=yellow>Linh Cốc động<color> tìm giúp ta <color=yellow>12<color> quyển <color=yellow> Địa Đồ chí  <color>."));
        WorldDomSnapshot rebornValidRoll = TestSnapshotFactory.Create(
            dialog: TestSnapshotFactory.CreateDialog(
                "<enter><enter> Đây là nhiệm vụ thứ <color=green>1<color>, Hãy giúp ta đi tìm món này: <color=yellow>1 cái <color=yellow>Dây chuyền đồng (Mộc)<color><color>."));

        StepComparisonResult validRollResult = engine.Compare(191, "Step191_C2S_0x5F", "C2S 0x5F", legacyRolled, rebornValidRoll);
        Assert.DoesNotContain(validRollResult.Findings, static f => f.Severity == FindingSeverity.Critical);
        Assert.Contains(validRollResult.Findings, static f => f.FieldPath == "Dialog.Prompt.RandomPool" && f.Severity == FindingSeverity.Warning);

        WorldDomSnapshot rebornTruncated = TestSnapshotFactory.Create(
            dialog: TestSnapshotFactory.CreateDialog(
                "<enter><enter> Đây là nhiệm vụ thứ <color=green>1<color>,"));
        StepComparisonResult truncatedResult = engine.Compare(193, "Step193_C2S_0x6E", "C2S 0x6E", legacyRolled, rebornTruncated);
        Assert.Contains(truncatedResult.Findings, static f => f.FieldPath == "Dialog.Prompt" && f.Severity == FindingSeverity.Critical);

        WorldDomSnapshot rebornWrongNumber = TestSnapshotFactory.Create(
            dialog: TestSnapshotFactory.CreateDialog(
                "<enter><enter> Đây là nhiệm vụ thứ <color=green>2<color>, Hãy giúp ta đi tìm món này: <color=yellow>1 cái <color=yellow>Dây chuyền đồng (Mộc)<color><color>."));
        StepComparisonResult wrongNumberResult = engine.Compare(194, "Step194_C2S_0x6E", "C2S 0x6E", legacyRolled, rebornWrongNumber);
        Assert.Contains(wrongNumberResult.Findings, static f => f.FieldPath == "Dialog.Prompt" && f.Severity == FindingSeverity.Critical);
    }

    [Fact]
    public void Compare_OnTieuLanTask66RandomRingTierReward_ShouldClassifyPoolAsWarningWhileKeepingMissingRingCritical()
    {
        // Ground truth: legacy-server/bin/server1/script/中原南区/稻香村/npc/稻香村_小兰对话.lua lines 17..39:
        // Task 66 turn-in ("Thật cảm ơn huynh! Ta có một chiếc nhẫn, xin huynh nhận cho!") rolls random(0,3)==0 ->
        // 25% AddItem(0,3,0,2,...) (Cảm Lãm Thạch Giới Chỉ Lv2) vs 75% AddItem(0,3,0,1,...) (Hoàng Ngọc Giới Chỉ Lv1).
        var engine = new SemanticDiffEngine();
        const string TieuLanTurnInPrompt = "Thật cảm ơn huynh! Ta có một chiếc nhẫn, xin huynh nhận cho!";

        WorldDomSnapshot legacyLv2Ring = TestSnapshotFactory.Create(
            mapId: 101,
            dialog: TestSnapshotFactory.CreateDialog(TieuLanTurnInPrompt),
            items: [TestSnapshotFactory.CreateItem(serverItemId: 501, place: 3, genre: 0, detail: 3, particular: 0, level: 2, series: 1)]);
        WorldDomSnapshot rebornLv1Ring = TestSnapshotFactory.Create(
            mapId: 101,
            dialog: TestSnapshotFactory.CreateDialog(TieuLanTurnInPrompt),
            items: [TestSnapshotFactory.CreateItem(serverItemId: 901, place: 3, genre: 0, detail: 3, particular: 0, level: 1, series: 3)]);

        StepComparisonResult turnInResult = engine.Compare(190, "TurnInTieuLanTask66", "C2S 0x6E", legacyLv2Ring, rebornLv1Ring);
        Assert.NotEmpty(turnInResult.Findings);
        Assert.DoesNotContain(turnInResult.Findings, static f => f.Severity == FindingSeverity.Critical);
        Assert.Contains(turnInResult.Findings, static f => f.FieldPath == "Inventory.RandomRewardPool" && f.Severity == FindingSeverity.Warning);

        // Subsequent step after closing dialog must not snowball into Critical inventory diff
        WorldDomSnapshot legacyAfterClose = TestSnapshotFactory.Create(
            mapId: 101,
            items: [TestSnapshotFactory.CreateItem(serverItemId: 501, place: 3, genre: 0, detail: 3, particular: 0, level: 2, series: 1)]);
        WorldDomSnapshot rebornAfterClose = TestSnapshotFactory.Create(
            mapId: 101,
            items: [TestSnapshotFactory.CreateItem(serverItemId: 901, place: 3, genre: 0, detail: 3, particular: 0, level: 1, series: 3)]);
        StepComparisonResult nextStepResult = engine.Compare(191, "CloseTieuLanTurnInTalk", "C2S 0x5F", legacyAfterClose, rebornAfterClose, legacyLv2Ring, rebornLv1Ring);
        Assert.DoesNotContain(nextStepResult.Findings, static f => f.Severity == FindingSeverity.Critical);

        // Genuine bug (Reborn grants no ring at all) MUST remain Critical!
        var freshEngine = new SemanticDiffEngine();
        WorldDomSnapshot rebornMissingRing = TestSnapshotFactory.Create(
            mapId: 101,
            dialog: TestSnapshotFactory.CreateDialog(TieuLanTurnInPrompt),
            items: []);
        StepComparisonResult missingResult = freshEngine.Compare(190, "MissingRingBug", "C2S 0x6E", legacyLv2Ring, rebornMissingRing);
        Assert.Contains(missingResult.Findings, static f => f.Category == FindingCategory.Inventory && f.Severity == FindingSeverity.Critical);
    }
}
