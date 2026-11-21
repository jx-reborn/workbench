using System.Buffers.Binary;
using JXReborn.Adapter.Legacy.Packets;
using JXReborn.DualParityServer.Diffing;
using JXReborn.WireParityProxy.Diffing;
using JXReborn.WireParityProxy.ShadowStates;
using JXReborn.WireParityProxy.Wire;
using Xunit;

namespace JXReborn.WireParityProxy.Tests.Diffing;

public sealed class ProtocolSafetyGateAndWireDifferTests
{
    [Fact]
    public void ProtocolSafetyGate_WhenShadowSendsMalformedFrameLength_ShouldReturnCriticalFinding()
    {
        var gate = new ProtocolSafetyGate();
        // s2c_syncmoney (0x61) fixed size is 5 bytes; sending 3 bytes must violate game.exe contract
        byte[] malformed = [ProtocolOpcodes.s2c_syncmoney, 0x01, 0x02];

        ParityFinding? finding = gate.ObserveShadowS2c(malformed, stepIndex: 1, mapId: 53, playerMpsX: 100, playerMpsY: 200, stepName: "Step001");

        Assert.NotNull(finding);
        Assert.Equal(FindingSeverity.Critical, finding.Severity);
        Assert.Equal(FindingCategory.WireProtocol, finding.Category);
        Assert.Equal(1, gate.ViolationCount);
    }

    [Fact]
    public void ActionWindowWireDiffer_ShouldFlagMissingShadowOpcodeAndIgnoreBackgroundPing()
    {
        var differ = new ActionWindowWireDiffer();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var c2s = new JxWireFrame(ProtocolOpcodes.c2s_playereatitem, [ProtocolOpcodes.c2s_playereatitem, 0, 1, 0, 0, 0, 0, 3], now, 1);
        var window = new WireActionWindow(1, "Step001_Eat", c2s, c2s.DecryptedPayload, false, null);

        var primaryS2c = new List<JxWireFrame>
        {
            new(ProtocolOpcodes.s2c_ping, [ProtocolOpcodes.s2c_ping, 0, 0, 0, 0], now, 1),
            new(ProtocolOpcodes.s2c_removeitem, [ProtocolOpcodes.s2c_removeitem, 1, 0, 0, 0], now, 2)
        };
        var shadowS2c = new List<JxWireFrame>
        {
            // Missing s2c_removeitem, only has background ping
            new(ProtocolOpcodes.s2c_ping, [ProtocolOpcodes.s2c_ping, 0, 0, 0, 0], now, 1)
        };

        IReadOnlyList<ParityFinding> findings = differ.CompareWindow(window, primaryS2c, shadowS2c, mapId: 53, playerMpsX: 100, playerMpsY: 200);

        Assert.Single(findings);
        Assert.Equal(FindingSeverity.Critical, findings[0].Severity);
        Assert.Equal("S2C.Missing[0x60]", findings[0].FieldPath);
    }

    [Fact]
    public void StateDriftDetector_WhenMapIdDiverges_ShouldFlagCriticalDrift()
    {
        var detector = new StateDriftDetector(positionToleranceMps: 64, criticalPositionDriftMps: 256);
        var primary = new ProxyShadowClientState("legacy-linux");
        var shadow = new ProxyShadowClientState("reborn-server");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        byte[] world53 = new byte[5];
        world53[0] = ProtocolOpcodes.s2c_syncworld;
        BinaryPrimitives.WriteInt32LittleEndian(world53.AsSpan(1, 4), 53);

        byte[] world20 = new byte[5];
        world20[0] = ProtocolOpcodes.s2c_syncworld;
        BinaryPrimitives.WriteInt32LittleEndian(world20.AsSpan(1, 4), 20);

        primary.ApplyS2c(world53, now);
        shadow.ApplyS2c(world20, now);

        DriftAssessment assessment = detector.Evaluate(primary, shadow);

        Assert.True(assessment.IsDrifted);
        Assert.True(assessment.MapMismatch);
        Assert.Equal(FindingSeverity.Critical, assessment.Severity);
    }

    [Fact]
    public void ActionWindowWireDiffer_ShouldIgnoreStochasticNpcHurtFrame()
    {
        var differ = new ActionWindowWireDiffer();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        byte[] c2sPayload = [ProtocolOpcodes.c2s_npcskill, 0, 0, 0, 0, 37, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0];
        var c2s = new JxWireFrame(ProtocolOpcodes.c2s_npcskill, c2sPayload, now, 1);
        var window = new WireActionWindow(1, "Step064_C2S_0x4D", c2s, c2s.DecryptedPayload, false, null);

        byte[] skillCast = new byte[25];
        skillCast[0] = ProtocolOpcodes.s2c_skillcast;
        byte[] hurtFrame = new byte[22];
        hurtFrame[0] = ProtocolOpcodes.s2c_npchurt;

        var primaryS2c = new List<JxWireFrame>
        {
            new(ProtocolOpcodes.s2c_skillcast, skillCast, now, 1)
        };
        var shadowS2c = new List<JxWireFrame>
        {
            new(ProtocolOpcodes.s2c_skillcast, skillCast, now, 1),
            new(ProtocolOpcodes.s2c_npchurt, hurtFrame, now, 2)
        };

        IReadOnlyList<ParityFinding> findings = differ.CompareWindow(window, primaryS2c, shadowS2c, mapId: 53, playerMpsX: 52096, playerMpsY: 101696);

        Assert.Empty(findings);
    }
}
