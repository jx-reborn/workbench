using JXReborn.Adapter.Legacy.Packets;
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
}
