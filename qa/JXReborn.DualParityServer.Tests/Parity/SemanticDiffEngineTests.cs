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
}
