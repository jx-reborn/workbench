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

    [Fact]
    public void ActionWindowWireDiffer_OnChangeAuraSkill_ShouldIgnorePhaseDependent0x85And0x87Presence_WhileKeepingStrictLengthAndOtherOpcodes()
    {
        var differ = new ActionWindowWireDiffer();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        byte[] stateInfo = new byte[11];
        stateInfo[0] = ProtocolOpcodes.s2c_npcsyncstateinfo;
        byte[] castDirect = new byte[25];
        castDirect[0] = ProtocolOpcodes.s2c_castskilldirectly;
        byte[] stateEff36 = new byte[36];
        stateEff36[0] = ProtocolOpcodes.s2c_syncstateeffect;
        byte[] stateEff52 = new byte[52];
        stateEff52[0] = ProtocolOpcodes.s2c_syncstateeffect;

        // 1. C2S 0x6F with phase-dependent 0x85/0x87 presence only on Shadow -> no findings
        byte[] auraC2s = [ProtocolOpcodes.c2s_changeauraskill, 69, 0, 0, 0];
        var auraFrame = new JxWireFrame(ProtocolOpcodes.c2s_changeauraskill, auraC2s, now, 1);
        var auraWindow = new WireActionWindow(1, "Step087_C2S_0x6F", auraFrame, auraFrame.DecryptedPayload, false, null);

        IReadOnlyList<ParityFinding> presenceFindings = differ.CompareWindow(
            auraWindow,
            primaryS2c: [new(ProtocolOpcodes.s2c_npcsyncstateinfo, stateInfo, now, 1)],
            shadowS2c:
            [
                new(ProtocolOpcodes.s2c_npcsyncstateinfo, stateInfo, now, 1),
                new(ProtocolOpcodes.s2c_castskilldirectly, castDirect, now, 2),
                new(ProtocolOpcodes.s2c_syncstateeffect, stateEff36, now, 3),
            ],
            mapId: 53,
            playerMpsX: 52032,
            playerMpsY: 101696);

        Assert.Empty(presenceFindings);

        // 2. C2S 0x6F when BOTH emit 0x87 with mismatched payload lengths -> strict S2C.Length[0x87]
        IReadOnlyList<ParityFinding> lengthFindings = differ.CompareWindow(
            auraWindow,
            primaryS2c:
            [
                new(ProtocolOpcodes.s2c_npcsyncstateinfo, stateInfo, now, 4),
                new(ProtocolOpcodes.s2c_syncstateeffect, stateEff36, now, 5),
            ],
            shadowS2c:
            [
                new(ProtocolOpcodes.s2c_npcsyncstateinfo, stateInfo, now, 4),
                new(ProtocolOpcodes.s2c_syncstateeffect, stateEff52, now, 5),
            ],
            mapId: 53,
            playerMpsX: 52032,
            playerMpsY: 101696);

        Assert.Contains(lengthFindings, f => f.FieldPath == "S2C.Length[0x87]");

        // 3. C2S 0x4D still flags S2C.Extra[0x85] strictly
        byte[] skillC2s = [ProtocolOpcodes.c2s_npcskill, 0, 0, 0, 0, 69, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0];
        var skillFrame = new JxWireFrame(ProtocolOpcodes.c2s_npcskill, skillC2s, now, 2);
        var skillWindow = new WireActionWindow(2, "Step088_C2S_0x4D", skillFrame, skillFrame.DecryptedPayload, false, null);
        byte[] skillCast = new byte[25];
        skillCast[0] = ProtocolOpcodes.s2c_skillcast;

        IReadOnlyList<ParityFinding> nonAuraFindings = differ.CompareWindow(
            skillWindow,
            primaryS2c: [new(ProtocolOpcodes.s2c_skillcast, skillCast, now, 6)],
            shadowS2c:
            [
                new(ProtocolOpcodes.s2c_skillcast, skillCast, now, 6),
                new(ProtocolOpcodes.s2c_castskilldirectly, castDirect, now, 7),
            ],
            mapId: 53,
            playerMpsX: 52032,
            playerMpsY: 101696);

        Assert.Contains(nonAuraFindings, f => f.FieldPath == "S2C.Extra[0x85]");
    }

    [Fact]
    public void ActionWindowWireDiffer_OnNpcSkill_ShouldIgnoreStochasticItemAbrade0x9B_WhileKeepingRepairStrict()
    {
        var differ = new ActionWindowWireDiffer();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        byte[] skillCast = new byte[25];
        skillCast[0] = ProtocolOpcodes.s2c_skillcast;
        byte[] itemRepair = new byte[13];
        itemRepair[0] = ProtocolOpcodes.s2c_itemrepair;

        // 1. C2S 0x4D with weapon abrade 0x9B only on Primary -> no findings
        byte[] skillC2s = [ProtocolOpcodes.c2s_npcskill, 0, 0, 0, 0, 129, 1, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0];
        var skillFrame = new JxWireFrame(ProtocolOpcodes.c2s_npcskill, skillC2s, now, 1);
        var skillWindow = new WireActionWindow(1, "Step095_C2S_0x4D", skillFrame, skillFrame.DecryptedPayload, false, null);

        IReadOnlyList<ParityFinding> abradeFindings = differ.CompareWindow(
            skillWindow,
            primaryS2c:
            [
                new(ProtocolOpcodes.s2c_skillcast, skillCast, now, 1),
                new(ProtocolOpcodes.s2c_itemrepair, itemRepair, now, 2),
            ],
            shadowS2c: [new(ProtocolOpcodes.s2c_skillcast, skillCast, now, 1)],
            mapId: 53,
            playerMpsX: 52032,
            playerMpsY: 101696);

        Assert.Empty(abradeFindings);

        // 2. C2S 0x7A (c2s_repairitem) still flags missing 0x9B strictly
        byte[] repairC2s = [ProtocolOpcodes.c2s_repairitem, 0, 0, 0, 0, 0, 0, 0, 0];
        var repairFrame = new JxWireFrame(ProtocolOpcodes.c2s_repairitem, repairC2s, now, 2);
        var repairWindow = new WireActionWindow(2, "Step096_C2S_0x7A", repairFrame, repairFrame.DecryptedPayload, false, null);

        IReadOnlyList<ParityFinding> repairFindings = differ.CompareWindow(
            repairWindow,
            primaryS2c: [new(ProtocolOpcodes.s2c_itemrepair, itemRepair, now, 3)],
            shadowS2c: [],
            mapId: 53,
            playerMpsX: 52032,
            playerMpsY: 101696);

        Assert.Contains(repairFindings, f => f.FieldPath == "S2C.Missing[0x9B]");
    }

    [Fact]
    public void ActionWindowWireDiffer_ShouldIgnoreTickBoundary0x4CAndAsyncCitySummary0xAC_WhileKeepingOtherS2cStrict()
    {
        var differ = new ActionWindowWireDiffer();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        byte[] reqNpcC2s = [ProtocolOpcodes.c2s_requestnpc, 0xC7, 0x43, 0x00, 0x00];
        var reqFrame = new JxWireFrame(ProtocolOpcodes.c2s_requestnpc, reqNpcC2s, now, 1);
        var window = new WireActionWindow(1, "Step042_C2S_0x49", reqFrame, reqFrame.DecryptedPayload, true, null);

        byte[] legacyNpc4C = new byte[38];
        legacyNpc4C[0] = ProtocolOpcodes.s2c_syncnpc;
        BinaryPrimitives.WriteUInt16LittleEndian(legacyNpc4C.AsSpan(1, 2), (ushort)(legacyNpc4C.Length - 1));

        byte[] rebornNpc4C = new byte[41];
        rebornNpc4C[0] = ProtocolOpcodes.s2c_syncnpc;
        BinaryPrimitives.WriteUInt16LittleEndian(rebornNpc4C.AsSpan(1, 2), (ushort)(rebornNpc4C.Length - 1));

        byte[] legacyCitySummary0xAC = new byte[234];
        legacyCitySummary0xAC[0] = ProtocolOpcodes.s2c_synccitysummary;
        BinaryPrimitives.WriteUInt16LittleEndian(legacyCitySummary0xAC.AsSpan(1, 2), (ushort)(legacyCitySummary0xAC.Length - 1));

        IReadOnlyList<ParityFinding> ignoredFindings = differ.CompareWindow(
            window,
            primaryS2c:
            [
                new(ProtocolOpcodes.s2c_syncnpc, legacyNpc4C, now, 1),
                new(ProtocolOpcodes.s2c_synccitysummary, legacyCitySummary0xAC, now, 2),
            ],
            shadowS2c:
            [
                new(ProtocolOpcodes.s2c_syncnpc, rebornNpc4C, now, 1),
            ],
            mapId: 53,
            playerMpsX: 52032,
            playerMpsY: 101696);

        Assert.Empty(ignoredFindings);

        // Non-ignored S2C opcodes (e.g., s2c_removeitem) remain strictly checked.
        IReadOnlyList<ParityFinding> strictFindings = differ.CompareWindow(
            window,
            primaryS2c: [new(ProtocolOpcodes.s2c_removeitem, [ProtocolOpcodes.s2c_removeitem, 1, 0, 0, 0], now, 3)],
            shadowS2c: [],
            mapId: 53,
            playerMpsX: 52032,
            playerMpsY: 101696);

        Assert.Single(strictFindings);
        Assert.Equal("S2C.Missing[0x60]", strictFindings[0].FieldPath);
    }

    [Fact]
    public void ProtocolSafetyGate_WhenShadowSendsValid0xAC_ShouldPassValidation()
    {
        var gate = new ProtocolSafetyGate();
        byte[] citySummary = new byte[234];
        citySummary[0] = ProtocolOpcodes.s2c_synccitysummary;
        BinaryPrimitives.WriteUInt16LittleEndian(citySummary.AsSpan(1, 2), (ushort)(citySummary.Length - 1));
        citySummary[3] = 2;

        ParityFinding? finding = gate.ObserveShadowS2c(citySummary, stepIndex: 42, mapId: 53, playerMpsX: 52032, playerMpsY: 101696, stepName: "Step042_C2S_0x49");

        Assert.Null(finding);
        Assert.Equal(0, gate.ViolationCount);
    }

    [Fact]
    public void ActionWindowWireDiffer_OnAsyncAoiAndLegacyLoginSubsystems_ShouldClassifyMissingAsWarningWhileKeepingGameplayOpcodesCritical()
    {
        // Ground truth: Map 53 mui2-final async AOI opcodes (0x50, 0x4F, 0x7A, 0x64, 0x51, 0x99) & login subsystem syncs (0xC8, 0xCF, 0xCE, 0xA7)
        // must downgrade to Warning, while interactive gameplay opcodes (0x63, 0x88, 0x84, 0x97, 0x61, 0xC6) remain strictly Critical.
        var differ = new ActionWindowWireDiffer();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var c2s = new JxWireFrame(ProtocolOpcodes.c2s_syncend, [ProtocolOpcodes.c2s_syncend, 1, 0, 0, 0, 2, 0, 0, 0], now, 1);
        var window = new WireActionWindow(1, "Step001_C2S_0x43", c2s, c2s.DecryptedPayload, false, null);

        byte[] realSignboard0x63 = ScriptActionS2CPacket.CreateSayPayload("Nha môn thông cáo", []);
        byte[] emptyAck0x63 = new byte[20];
        emptyAck0x63[0] = ProtocolOpcodes.s2c_scriptaction;
        BinaryPrimitives.WriteUInt16LittleEndian(emptyAck0x63.AsSpan(1, 2), 19);
        emptyAck0x63[4] = 99; // non-dialog subsystem UI ack alongside 0xA7 login burst (Step 4 ground truth)

        var primaryS2c = new List<JxWireFrame>
        {
            new(ProtocolOpcodes.s2c_npcwalk, new byte[13], now, 1),
            new(ProtocolOpcodes.s2c_npcremove, new byte[5], now, 2),
            new(ProtocolOpcodes.s2c_npcsyncstateinfo, new byte[11], now, 3),
            new(ProtocolOpcodes.s2c_objadd, new byte[35], now, 4),
            new(ProtocolOpcodes.s2c_synctaskvalue, new byte[9], now, 5),
            new(ProtocolOpcodes.s2c_requestnpcfail, new byte[5], now, 6),
            new(ProtocolOpcodes.s2c_castskilldirectly, new byte[25], now, 7),
            new(0xC8, new byte[1027], now, 8),
            new(ProtocolOpcodes.s2c_scriptaction, realSignboard0x63, now, 9),
            new(ProtocolOpcodes.s2c_openstorebox, new byte[3], now, 10),
            new(ProtocolOpcodes.s2c_skillcast, new byte[25], now, 11),
        };

        IReadOnlyList<ParityFinding> findings = differ.CompareWindow(window, primaryS2c, shadowS2c: [], mapId: 53, playerMpsX: 52032, playerMpsY: 101696);
        Assert.NotEmpty(findings);

        Assert.Contains(findings, static f => f.FieldPath == "S2C.Missing[0x50]" && f.Severity == FindingSeverity.Warning);
        Assert.Contains(findings, static f => f.FieldPath == "S2C.Missing[0x4F]" && f.Severity == FindingSeverity.Warning);
        Assert.Contains(findings, static f => f.FieldPath == "S2C.Missing[0x7A]" && f.Severity == FindingSeverity.Warning);
        Assert.Contains(findings, static f => f.FieldPath == "S2C.Missing[0x64]" && f.Severity == FindingSeverity.Warning);
        Assert.Contains(findings, static f => f.FieldPath == "S2C.Missing[0xA7]" && f.Severity == FindingSeverity.Warning);
        Assert.Contains(findings, static f => f.FieldPath == "S2C.Missing[0x8A]" && f.Severity == FindingSeverity.Warning);
        Assert.Contains(findings, static f => f.FieldPath == "S2C.Missing[0x85]" && f.Severity == FindingSeverity.Warning);
        Assert.Contains(findings, static f => f.FieldPath == "S2C.Missing[0xC8]" && f.Severity == FindingSeverity.Warning);

        Assert.Contains(findings, static f => f.FieldPath == "S2C.Missing[0x63]" && f.Severity == FindingSeverity.Critical);
        Assert.Contains(findings, static f => f.FieldPath == "S2C.Missing[0x88]" && f.Severity == FindingSeverity.Critical);
        Assert.Contains(findings, static f => f.FieldPath == "S2C.Missing[0x5A]" && f.Severity == FindingSeverity.Critical);

        // Step 4 ground truth: 20-byte non-dialog 0x63 ack alongside 0xA7 login dump downgrades to Warning
        IReadOnlyList<ParityFinding> emptyAckFindings = differ.CompareWindow(
            window,
            primaryS2c: [new(ProtocolOpcodes.s2c_scriptaction, emptyAck0x63, now, 10)],
            shadowS2c: [],
            mapId: 53,
            playerMpsX: 51984,
            playerMpsY: 101681);
        Assert.Contains(emptyAckFindings, static f => f.FieldPath == "S2C.Missing[0x63]" && f.Severity == FindingSeverity.Warning);
    }
}
