using JXReborn.ClientDriver;
using JXReborn.DualParityServer.Diffing;
using JXReborn.DualParityServer.Tests.Fixtures;
using Xunit;

namespace JXReborn.DualParityServer.Tests.Parity;

public sealed class GeneratedVillagerRandomPoolDiffTests
{
    [Fact]
    public void Compare_WhenNamNhacQuestGatedVillagersRollDifferentRandomFallbackLines_ShouldDowngradeToWarningAndKeepCrossNpcCritical()
    {
        // Ground Truth:
        // 1) \script\两湖区\南岳镇\npc\南岳镇_路人7谷之兰对话.lua (Cốc Chi Lan, random(0,1))
        // 2) \script\两湖区\南岳镇\npc\南岳镇_路人4赵银娣对话.lua (Triệu Ngân Đễ, random(0,1))
        var engine = new SemanticDiffEngine();

        const string cocChiLan0 = "Độc Cô phu nhân thật có phúc, tướng công là một đại anh hùng,thật khiến ta ngưỡng mộ";
        const string cocChiLan1 = "Quan trọng nhất của một đời con gái chính là không nên gả nhầm người. Sau này ta cũng phải lấy một Đại hiệp có tình cảm dịu dàng";

        const string trieuNganDe0 = "Ta muốn đi học kinh thư, nhưng cha ta không cho, nói con gái đi học không thể ra tiến cử công danh, chỉ là lãng phí tiền.";
        const string trieuNganDe1 = "Cha ta cứ đánh ta, nói ta là kẻ tiêu tiền, ngay cả lúc có mẹ ta cũng đánh. Nếu như ta có bản lĩnh như huynh, cha ta sẽ không còn đánh ta nữa";

        StepComparisonResult rCocChiLan = engine.Compare(
            182,
            "Step182_C2S_0x6E",
            "C2S 0x6E",
            TestSnapshotFactory.Create(mapId: 54, dialog: TestSnapshotFactory.CreateDialog(cocChiLan0)),
            TestSnapshotFactory.Create(mapId: 54, dialog: TestSnapshotFactory.CreateDialog(cocChiLan1)));

        Assert.True(rCocChiLan.Passed);
        Assert.Equal(0, rCocChiLan.CriticalCount);
        Assert.Contains(rCocChiLan.Findings, static f => f.FieldPath == "Dialog.Prompt.RandomPool" && f.Severity == FindingSeverity.Warning);

        StepComparisonResult rTrieuNganDe = engine.Compare(
            206,
            "Step206_C2S_0x6E",
            "C2S 0x6E",
            TestSnapshotFactory.Create(mapId: 54, dialog: TestSnapshotFactory.CreateDialog(trieuNganDe1)),
            TestSnapshotFactory.Create(mapId: 54, dialog: TestSnapshotFactory.CreateDialog(trieuNganDe0)));

        Assert.True(rTrieuNganDe.Passed);
        Assert.Equal(0, rTrieuNganDe.CriticalCount);
        Assert.Contains(rTrieuNganDe.Findings, static f => f.FieldPath == "Dialog.Prompt.RandomPool" && f.Severity == FindingSeverity.Warning);

        // Cross-NPC mismatch MUST remain Critical!
        StepComparisonResult rCrossNpc = engine.Compare(
            210,
            "CrossNpcMismatch",
            "C2S 0x6E",
            TestSnapshotFactory.Create(mapId: 54, dialog: TestSnapshotFactory.CreateDialog(cocChiLan0)),
            TestSnapshotFactory.Create(mapId: 54, dialog: TestSnapshotFactory.CreateDialog(trieuNganDe0)));

        Assert.False(rCrossNpc.Passed);
        Assert.Contains(rCrossNpc.Findings, static f => f.FieldPath == "Dialog.Prompt" && f.Severity == FindingSeverity.Critical);
    }
}
