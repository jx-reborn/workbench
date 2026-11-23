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
}
