using System.Runtime.InteropServices;
using JXReborn.Adapter.Legacy.Packets;
using JXReborn.Primitives.Characters;
using JXReborn.Primitives.Encodings;
using JXReborn.WireParityProxy.ShadowStates;
using Xunit;

namespace JXReborn.WireParityProxy.Tests.ShadowStates;

public sealed class ProxyShadowClientStateScriptActionTests
{
    [Fact]
    public void ApplyS2c_WithUiDescribeDialog_ShouldParsePromptAndOptionsWithoutRawHeader()
    {
        var state = new ProxyShadowClientState("legacy-linux");
        byte[] payload = ScriptActionS2CPacket.CreateDescribePayload(
            "<link>Hệ thống",
            ["Xem chi tiết", "Thoát"]);

        state.ApplyS2c(payload, DateTimeOffset.UtcNow);

        Assert.True(state.Dialog.IsOpen);
        Assert.False(state.Dialog.IsTalk);
        Assert.Equal("<link>Hệ thống", Tcvn3Encoding.DecodeNullTerminated(Convert.FromHexString(state.Dialog.PromptTcvn3Hex)));
        Assert.Equal(2, state.Dialog.OptionsTcvn3Hex.Count);
        Assert.Equal("Xem chi tiết", Tcvn3Encoding.DecodeNullTerminated(Convert.FromHexString(state.Dialog.OptionsTcvn3Hex[0])));
        Assert.Equal("Thoát", Tcvn3Encoding.DecodeNullTerminated(Convert.FromHexString(state.Dialog.OptionsTcvn3Hex[1])));
    }

    [Fact]
    public void ApplyS2c_WithUiSelectDialog_ShouldParseNullDelimitedPromptAndOptions()
    {
        var state = new ProxyShadowClientState("reborn-server");
        byte[] payload = ScriptActionS2CPacket.CreateSayPayload("Xã Phu:", ["Ba Lăng", "Thoát"]);

        state.ApplyS2c(payload, DateTimeOffset.UtcNow);

        Assert.True(state.Dialog.IsOpen);
        Assert.False(state.Dialog.IsTalk);
        Assert.Equal("Xã Phu:", Tcvn3Encoding.DecodeNullTerminated(Convert.FromHexString(state.Dialog.PromptTcvn3Hex)));
        Assert.Equal(2, state.Dialog.OptionsTcvn3Hex.Count);
        Assert.Equal("Ba Lăng", Tcvn3Encoding.DecodeNullTerminated(Convert.FromHexString(state.Dialog.OptionsTcvn3Hex[0])));
        Assert.Equal("Thoát", Tcvn3Encoding.DecodeNullTerminated(Convert.FromHexString(state.Dialog.OptionsTcvn3Hex[1])));
    }

    [Fact]
    public void ApplyS2c_WithNonDialogScriptAction_ShouldNotOpenOrOverwriteDialog()
    {
        var state = new ProxyShadowClientState("legacy-linux");
        byte[] nonDialogBarrier = ScriptActionS2CPacket.CreateCustomPayload(
            operateType: 0,
            uiId: 0x1F,
            count: 1,
            param: 0,
            content: [0x01, 0x02, 0x03, 0x04]);

        state.ApplyS2c(nonDialogBarrier, DateTimeOffset.UtcNow);
        Assert.False(state.Dialog.IsOpen);

        byte[] describePayload = ScriptActionS2CPacket.CreateDescribePayload("Prompt", ["OK"]);
        state.ApplyS2c(describePayload, DateTimeOffset.UtcNow);
        uint openSeq = state.Dialog.Sequence;

        byte[] nonDialogSkillNotice = ScriptActionS2CPacket.CreateCustomPayload(
            operateType: 0,
            uiId: 0x04,
            count: 1,
            param: 0,
            content: "Skill"u8);
        state.ApplyS2c(nonDialogSkillNotice, DateTimeOffset.UtcNow);

        Assert.True(state.Dialog.IsOpen);
        Assert.Equal(openSeq, state.Dialog.Sequence);
        Assert.Equal("Prompt", Tcvn3Encoding.DecodeNullTerminated(Convert.FromHexString(state.Dialog.PromptTcvn3Hex)));
        Assert.Single(state.Dialog.OptionsTcvn3Hex);
    }

    [Fact]
    public void ApplyS2c_WithStringTableSayTalkAnd64BitExpSync_ShouldResolveVietnameseTextFromStringResourcesAndUpdateExp64()
    {
        var state = new ProxyShadowClientState("legacy-linux");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // 1. 0xC6 s2c_syncexp (9 bytes, int64)
        byte[] exp64Payload = new byte[PlayerExpSync64S2CPacket.PayloadSize];
        new PlayerExpSync64S2CPacket(5_000_001_235L).WritePayload(exp64Payload);
        state.ApplyS2c(exp64Payload, now);
        Assert.Equal(5_000_001_235L, state.Exp);

        // 2. Step 922 Thẩm Cửu Say(11256, 4, "Mua ", "Ta đến làm nhiệm vụ A Phương", "Tìm hiểu", "Không mua") (73B, Param1=1)
        byte[] sayById = ScriptActionS2CPacket.CreateSayByStringIdPayload(
            11256,
            ["Mua ", "Ta đến làm nhiệm vụ A Phương", "Tìm hiểu", "Không mua"]);
        state.ApplyS2c(sayById, now);

        Assert.True(state.Dialog.IsOpen);
        Assert.False(state.Dialog.IsTalk);
        Assert.Equal(4, state.Dialog.OptionsTcvn3Hex.Count);
        Assert.Equal("Ta đến làm nhiệm vụ A Phương", Tcvn3Encoding.DecodeNullTerminated(Convert.FromHexString(state.Dialog.OptionsTcvn3Hex[1])));
        string resolvedPrompt11256 = Tcvn3Encoding.DecodeNullTerminated(Convert.FromHexString(state.Dialog.PromptTcvn3Hex));
        Assert.Contains("2 Kim Nguyên bảo", resolvedPrompt11256, StringComparison.Ordinal);

        // 3. Step 924 Thẩm Cửu Talk(1, "", 11260) (24B, Param1=1)
        byte[] talkById = ScriptActionS2CPacket.CreateTalkByStringIdsPayload([11260], expectCompletionCallback: false);
        state.ApplyS2c(talkById, now);

        Assert.True(state.Dialog.IsOpen);
        Assert.True(state.Dialog.IsTalk);
        string resolvedTalk11260 = Tcvn3Encoding.DecodeNullTerminated(Convert.FromHexString(state.Dialog.PromptTcvn3Hex));
        Assert.Contains("Khuyên tai đã gói lại rồi! Xin giữ lấy!", resolvedTalk11260, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyS2c_OnSyncPos0xC5NpcWalk0x50AndNpcRun0x51_ShouldUpdateSelfAndNpcCoordinatesForAccurateTranslation()
    {
        // Ground truth: Map 53 mui2-final Steps 167, 222, 280, 381 where intra-map teleport emits s2c_syncpos (0xC5, 17B)
        // and running to signboards emits s2c_npcrun (0x51, 13B) / s2c_npcwalk (0x50, 13B).
        var state = new ProxyShadowClientState("reborn-server");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        byte[] selfSync = new SyncNpcFullS2CPacket(
            Camp: 0,
            Series: ElementSeries.Metal,
            LifePercent: 128,
            MapMpsY: 101696,
            DoingState: 1,
            StateInfo: 0,
            MapMpsX: 52032,
            MenuState: 0,
            GoldType: 0,
            Kind: EntityKind.Player,
            NpcId: 777u,
            MasterId: -1,
            TemplateId: 0,
            Level: 1,
            RankFfId: 0,
            NameUnicode: "PosTester").ToPayloadBytes();
        state.ApplyS2c(selfSync, now);

        byte[] monsterSync = new SyncNpcFullS2CPacket(
            Camp: 5,
            Series: ElementSeries.Wood,
            LifePercent: 128,
            MapMpsY: 95000,
            DoingState: 1,
            StateInfo: 0,
            MapMpsX: 48000,
            MenuState: 0,
            GoldType: 0,
            Kind: EntityKind.Monster,
            NpcId: 888u,
            MasterId: -1,
            TemplateId: 42,
            Level: 3,
            RankFfId: 0,
            NameUnicode: "HuouDom").ToPayloadBytes();
        state.ApplyS2c(monsterSync, now);

        // 1. 0xC5 SyncNpcPosS2CPacket (17B) on intra-map gm_teleport to Yangtze Wharf (49472, 93696)
        SyncNpcPosS2CPacket syncPos = SyncNpcPosS2CPacket.Create(npcId: 777u, mapMpsX: 49472, mapMpsY: 93696);
        byte[] syncPosBytes = new byte[SyncNpcPosS2CPacket.Size];
        MemoryMarshal.Write(syncPosBytes, in syncPos);
        state.ApplyS2c(syncPosBytes, now);
        Assert.Equal(49472, state.MpsX);
        Assert.Equal(93696, state.MpsY);

        // 2. 0x51 NpcRunS2CPacket (13B) on SelfNpcId (777u) MUST NOT jump self ahead to DestMpsX/DestMpsY (Steps 467/499 drift=671 ground truth),
        // because legacy-linux only updates self position via 0x4E/0xC5 while reborn-server echoes 0x50/0x51 to self.
        byte[] runBytes = new byte[NpcRunS2CPacket.PayloadSize];
        new NpcRunS2CPacket(NpcId: 777u, DestMpsX: 52083, DestMpsY: 101248).WritePayload(runBytes);
        state.ApplyS2c(runBytes, now);
        Assert.Equal(49472, state.MpsX);
        Assert.Equal(93696, state.MpsY);

        // 3. 0x50 NpcWalkS2CPacket (13B) when non-self monster (888u) patrols to (48374, 95026) DOES update NPC position
        byte[] walkBytes = new byte[NpcWalkS2CPacket.PayloadSize];
        new NpcWalkS2CPacket(NpcId: 888u, DestMpsX: 48374, DestMpsY: 95026).WritePayload(walkBytes);
        state.ApplyS2c(walkBytes, now);
        Assert.Equal(48374, state.SnapshotNpcs()[888u].MpsX);
        Assert.Equal(95026, state.SnapshotNpcs()[888u].MpsY);

        // 4. 0-option signboard Say("Ba Lăng huyện<---->Miêu Lĩnh", []) closes locally when player sends c2s_npcwalk (0x4B) (Step 222 ground truth)
        byte[] signboardSay = ScriptActionS2CPacket.CreateSayPayload("Ba Lăng huyện<---->Miêu Lĩnh", []);
        state.ApplyS2c(signboardSay, now);
        Assert.True(state.Dialog.IsOpen);
        state.RecordOutboundC2s([ProtocolOpcodes.c2s_npcwalk, 0, 0, 0, 0, 0, 0, 0, 0], now);
        Assert.False(state.Dialog.IsOpen);

        // 5. Cross-map s2c_syncworld (0x49, Map 199, Region (102,90)) immediately moves Mps out of Map 53 coords (Step 1063 ground truth)
        SyncWorldS2CPacket world199 = SyncWorldS2CPacket.Create(199, 52608, 92992, 100, "Vu Lang Dong");
        byte[] world199Bytes = new byte[SyncWorldS2CPacket.Size];
        MemoryMarshal.Write(world199Bytes, in world199);
        state.ApplyS2c(world199Bytes, now);
        Assert.Equal(199, state.MapId);
        Assert.InRange(state.MpsX, 52224, 52736);
        Assert.InRange(state.MpsY, 92160, 93184);
    }
}
