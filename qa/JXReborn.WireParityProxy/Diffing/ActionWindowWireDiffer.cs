using JXReborn.Adapter.Legacy.Packets;
using JXReborn.DualParityServer.Diffing;
using JXReborn.WireParityProxy.Wire;

namespace JXReborn.WireParityProxy.Diffing;

/// <summary>
/// Layer B differ comparing Primary vs Shadow S2C response bursts within a closed <see cref="WireActionWindow"/>,
/// filtering background heartbeat/ping frames and reporting missing/extra opcodes and fixed-length mismatches.
/// </summary>
public sealed class ActionWindowWireDiffer
{
    private static readonly HashSet<byte> IgnoredBackgroundS2cOpcodes =
    [
        ProtocolOpcodes.s2c_ping,
        ProtocolOpcodes.s2c_synccurplayernormal,
        ProtocolOpcodes.s2c_syncplayermin,
        ProtocolOpcodes.s2c_syncnpcmin,
        ProtocolOpcodes.s2c_syncnpcminplayer,
        ProtocolOpcodes.s2c_objsyncpos,
        ProtocolOpcodes.s2c_extend,
        // DoHurt recoil rolls an independent 50% PRNG coin flip in Legacy KNpc::DoHurt, causing stochastic 0x56 bursts across dual backends.
        ProtocolOpcodes.s2c_npchurt,
        // AOI entity hydration (0x4C) is tick-paced across rapid c2s_requestnpc bursts (straddling 55ms action windows)
        // and carries Vietnamese TCVN3 vs Legacy GBK NPC name lengths (+2..+7B); wire/state parity is enforced by ProtocolSafetyGate & ProxyShadowClientState.
        ProtocolOpcodes.s2c_syncnpc,
        // Asynchronous s3relay/Lua CityWar summary broadcast (4 + 115*N bytes), unrelated to the enclosing C2S action window.
        ProtocolOpcodes.s2c_synccitysummary,
        // Asynchronous map-wide ambient weather roll (2 bytes [0x8F, weatherId]), unrelated to the enclosing C2S action window.
        ProtocolOpcodes.s2c_syncweather
    ];

    // Tick-asynchronous AOI patrol/appearance frames and legacy-only login subsystem dumps (KFileSyncGS 0xC8,
    // partner/limittime 0xCF/0xCE, internal task dump 0xA7) whose state effects are already verified by SemanticDiffEngine.
    // Interactive gameplay opcodes (0x63, 0x88, 0x84, 0x97, 0x5F, 0x60, 0x61, 0xC6, 0x47, 0x7B, 0x7C, 0x57, 0x5A) stay Critical.
    private static readonly HashSet<byte> AsyncAoiAndSubsystemWarningOpcodes =
    [
        ProtocolOpcodes.s2c_npcwalk,
        ProtocolOpcodes.s2c_npcrun,
        ProtocolOpcodes.s2c_npcremove,
        ProtocolOpcodes.s2c_npcsyncstateinfo,
        ProtocolOpcodes.s2c_objadd,
        ProtocolOpcodes.s2c_syncpos,
        ProtocolOpcodes.s2c_castskilldirectly,
        ProtocolOpcodes.s2c_syncstateeffect,
        ProtocolOpcodes.s2c_pingreply,
        ProtocolOpcodes.s2c_playerattributesync,
        ProtocolOpcodes.s2c_synctaskvalue,
        ProtocolOpcodes.s2c_requestnpcfail,
        0xC8,
        0xCE,
        0xCF
    ];

    private int _findingSeq;

    public IReadOnlyList<ParityFinding> CompareWindow(
        WireActionWindow window,
        IReadOnlyList<JxWireFrame> primaryS2c,
        IReadOnlyList<JxWireFrame> shadowS2c,
        int mapId,
        int playerMpsX,
        int playerMpsY)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(primaryS2c);
        ArgumentNullException.ThrowIfNull(shadowS2c);

        var findings = new List<ParityFinding>();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        IReadOnlyList<string> primaryOpcodeTags = primaryS2c.Select(f => $"0x{f.Opcode:X2}").Distinct().ToArray();
        IReadOnlyList<string> shadowOpcodeTags = shadowS2c.Select(f => $"0x{f.Opcode:X2}").Distinct().ToArray();

        if (!string.IsNullOrWhiteSpace(window.TranslationMissReason))
        {
            findings.Add(CreateFinding(
                FindingSeverity.Critical,
                FindingCategory.WireProtocol,
                window,
                mapId,
                playerMpsX,
                playerMpsY,
                fieldPath: "C2S.EntityTranslation",
                legacyValue: $"C2S 0x{window.PrimaryC2s.Opcode:X2} translated cleanly",
                rebornValue: window.TranslationMissReason,
                primaryOpcodeTags,
                shadowOpcodeTags,
                now));
        }

        Dictionary<byte, List<JxWireFrame>> primaryByOp = GroupSignificant(primaryS2c);
        Dictionary<byte, List<JxWireFrame>> shadowByOp = GroupSignificant(shadowS2c);

        foreach ((byte opcode, List<JxWireFrame> pFrames) in primaryByOp)
        {
            if (!shadowByOp.TryGetValue(opcode, out List<JxWireFrame>? sFrames))
            {
                if (IsStochasticOrPhaseDependentOpcode(window.PrimaryC2s.Opcode, opcode))
                {
                    continue;
                }

                FindingSeverity missingSeverity = AsyncAoiAndSubsystemWarningOpcodes.Contains(opcode) ||
                    (opcode == ProtocolOpcodes.s2c_scriptaction && IsNonDialogOrAsyncNewsScriptActionOnly(pFrames))
                    ? FindingSeverity.Warning
                    : FindingSeverity.Critical;

                findings.Add(CreateFinding(
                    missingSeverity,
                    FindingCategory.WireProtocol,
                    window,
                    mapId,
                    playerMpsX,
                    playerMpsY,
                    fieldPath: $"S2C.Missing[0x{opcode:X2}]",
                    legacyValue: $"{pFrames.Count} frame(s) (len={pFrames[0].DecryptedPayload.Length})",
                    rebornValue: "0 frames",
                    primaryOpcodeTags,
                    shadowOpcodeTags,
                    now));
                continue;
            }

            if (pFrames[0].DecryptedPayload.Length != sFrames[0].DecryptedPayload.Length)
            {
                findings.Add(CreateFinding(
                    FindingSeverity.Warning,
                    FindingCategory.WireProtocol,
                    window,
                    mapId,
                    playerMpsX,
                    playerMpsY,
                    fieldPath: $"S2C.Length[0x{opcode:X2}]",
                    legacyValue: $"{pFrames[0].DecryptedPayload.Length} bytes",
                    rebornValue: $"{sFrames[0].DecryptedPayload.Length} bytes",
                    primaryOpcodeTags,
                    shadowOpcodeTags,
                    now));
            }

            CompareSemanticPayloadFields(
                opcode,
                pFrames[0].DecryptedPayload,
                sFrames[0].DecryptedPayload,
                window,
                mapId,
                playerMpsX,
                playerMpsY,
                primaryOpcodeTags,
                shadowOpcodeTags,
                now,
                findings);
        }

        foreach ((byte opcode, List<JxWireFrame> sFrames) in shadowByOp)
        {
            if (!primaryByOp.ContainsKey(opcode))
            {
                if (IsStochasticOrPhaseDependentOpcode(window.PrimaryC2s.Opcode, opcode))
                {
                    continue;
                }

                findings.Add(CreateFinding(
                    FindingSeverity.Warning,
                    FindingCategory.WireProtocol,
                    window,
                    mapId,
                    playerMpsX,
                    playerMpsY,
                    fieldPath: $"S2C.Extra[0x{opcode:X2}]",
                    legacyValue: "0 frames",
                    rebornValue: $"{sFrames.Count} frame(s) (len={sFrames[0].DecryptedPayload.Length})",
                    primaryOpcodeTags,
                    shadowOpcodeTags,
                    now));
            }
        }

        return findings;
    }

    private void CompareSemanticPayloadFields(
        byte opcode,
        ReadOnlyMemory<byte> primaryPayload,
        ReadOnlyMemory<byte> shadowPayload,
        WireActionWindow window,
        int mapId,
        int playerMpsX,
        int playerMpsY,
        IReadOnlyList<string> primaryOpcodeTags,
        IReadOnlyList<string> shadowOpcodeTags,
        DateTimeOffset now,
        List<ParityFinding> findings)
    {
        switch (opcode)
        {
            case ProtocolOpcodes.s2c_opensalebox when
                OpenSaleBoxS2CPacket.TryParse(primaryPayload.Span, out OpenSaleBoxS2CPacket pShop) &&
                OpenSaleBoxS2CPacket.TryParse(shadowPayload.Span, out OpenSaleBoxS2CPacket sShop) &&
                pShop != sShop:
                findings.Add(CreateFinding(
                    FindingSeverity.Critical,
                    FindingCategory.WireProtocol,
                    window,
                    mapId,
                    playerMpsX,
                    playerMpsY,
                    fieldPath: "S2C.Payload[0x84].ShopSaleBox",
                    legacyValue: $"ShopId={pShop.ShopId}, Scale={pShop.ScalePercent}, Currency={pShop.CurrencyType}",
                    rebornValue: $"ShopId={sShop.ShopId}, Scale={sShop.ScalePercent}, Currency={sShop.CurrencyType}",
                    primaryOpcodeTags,
                    shadowOpcodeTags,
                    now));
                break;

            case ProtocolOpcodes.s2c_openstorebox when
                OpenStoreBoxS2CPacket.TryParse(primaryPayload.Span, out OpenStoreBoxS2CPacket pBox) &&
                OpenStoreBoxS2CPacket.TryParse(shadowPayload.Span, out OpenStoreBoxS2CPacket sBox) &&
                pBox != sBox:
                findings.Add(CreateFinding(
                    FindingSeverity.Critical,
                    FindingCategory.WireProtocol,
                    window,
                    mapId,
                    playerMpsX,
                    playerMpsY,
                    fieldPath: "S2C.Payload[0x88].StoreBox",
                    legacyValue: $"ExpandMask=0x{pBox.ExpandBoxMask:X2}, PwdState={pBox.PasswordState}",
                    rebornValue: $"ExpandMask=0x{sBox.ExpandBoxMask:X2}, PwdState={sBox.PasswordState}",
                    primaryOpcodeTags,
                    shadowOpcodeTags,
                    now));
                break;

            case ProtocolOpcodes.s2c_pksyncnormalflag when
                PkSyncNormalFlagS2CPacket.TryParse(primaryPayload.Span, out PkSyncNormalFlagS2CPacket pPk) &&
                PkSyncNormalFlagS2CPacket.TryParse(shadowPayload.Span, out PkSyncNormalFlagS2CPacket sPk) &&
                pPk.PkMode != sPk.PkMode:
                findings.Add(CreateFinding(
                    FindingSeverity.Critical,
                    FindingCategory.WireProtocol,
                    window,
                    mapId,
                    playerMpsX,
                    playerMpsY,
                    fieldPath: "S2C.Payload[0x90].PkMode",
                    legacyValue: pPk.PkMode.ToString(),
                    rebornValue: sPk.PkMode.ToString(),
                    primaryOpcodeTags,
                    shadowOpcodeTags,
                    now));
                break;

            case ProtocolOpcodes.s2c_showladder when
                ShowLadderS2CPacket.TryParse(primaryPayload.Span, out ShowLadderS2CPacket pLad) &&
                ShowLadderS2CPacket.TryParse(shadowPayload.Span, out ShowLadderS2CPacket sLad) &&
                !pLad.LadderIds.AsSpan().SequenceEqual(sLad.LadderIds):
                findings.Add(CreateFinding(
                    FindingSeverity.Critical,
                    FindingCategory.WireProtocol,
                    window,
                    mapId,
                    playerMpsX,
                    playerMpsY,
                    fieldPath: "S2C.Payload[0x97].LadderIds",
                    legacyValue: $"[{string.Join(",", pLad.LadderIds)}]",
                    rebornValue: $"[{string.Join(",", sLad.LadderIds)}]",
                    primaryOpcodeTags,
                    shadowOpcodeTags,
                    now));
                break;

            case ProtocolOpcodes.s2c_playerskilllevel when
                PlayerSkillLevelS2CPacket.TryParse(primaryPayload.Span, out PlayerSkillLevelS2CPacket pSk) &&
                PlayerSkillLevelS2CPacket.TryParse(shadowPayload.Span, out PlayerSkillLevelS2CPacket sSk) &&
                (pSk.SkillId != sSk.SkillId || pSk.SkillLevel != sSk.SkillLevel):
                findings.Add(CreateFinding(
                    FindingSeverity.Critical,
                    FindingCategory.WireProtocol,
                    window,
                    mapId,
                    playerMpsX,
                    playerMpsY,
                    fieldPath: "S2C.Payload[0x5E].SkillLevel",
                    legacyValue: $"SkillId={pSk.SkillId}, Level={pSk.SkillLevel}",
                    rebornValue: $"SkillId={sSk.SkillId}, Level={sSk.SkillLevel}",
                    primaryOpcodeTags,
                    shadowOpcodeTags,
                    now));
                break;

            case ProtocolOpcodes.s2c_scriptaction when
                ScriptActionS2CPacket.TryParse(primaryPayload.Span, out ScriptActionS2CPacket pSa) &&
                ScriptActionS2CPacket.TryParse(shadowPayload.Span, out ScriptActionS2CPacket sSa) &&
                pSa.UiId != sSa.UiId:
                findings.Add(CreateFinding(
                    FindingSeverity.Critical,
                    FindingCategory.WireProtocol,
                    window,
                    mapId,
                    playerMpsX,
                    playerMpsY,
                    fieldPath: "S2C.Payload[0x63].UiId",
                    legacyValue: $"UiId={pSa.UiId}",
                    rebornValue: $"UiId={sSa.UiId}",
                    primaryOpcodeTags,
                    shadowOpcodeTags,
                    now));
                break;
        }
    }

    private static bool IsStochasticOrPhaseDependentOpcode(byte c2sOpcode, byte s2cOpcode) =>
        (c2sOpcode == ProtocolOpcodes.c2s_changeauraskill &&
         (s2cOpcode == ProtocolOpcodes.s2c_castskilldirectly ||
          s2cOpcode == ProtocolOpcodes.s2c_syncstateeffect)) ||
        (c2sOpcode == ProtocolOpcodes.c2s_npcskill &&
         s2cOpcode == ProtocolOpcodes.s2c_itemrepair);

    private const byte UiNewsInfo = 5;

    private static bool IsNonDialogOrAsyncNewsScriptActionOnly(List<JxWireFrame> frames) =>
        frames.Count > 0 &&
        frames.All(static f =>
            (ScriptActionS2CPacket.TryParse(f.DecryptedPayload, out ScriptActionS2CPacket newsSa) &&
             newsSa.UiId == UiNewsInfo) ||
            (f.DecryptedPayload.Length <= 20 &&
             (!ScriptActionS2CPacket.TryParse(f.DecryptedPayload, out ScriptActionS2CPacket sa) ||
              (sa.UiId != ScriptActionS2CPacket.UiSelectDialog &&
               sa.UiId != ScriptActionS2CPacket.UiTalkDialog &&
               sa.UiId != ScriptActionS2CPacket.UiDescribeDialog &&
               sa.UiId != ScriptActionS2CPacket.UiPriseDialog &&
               sa.UiId != ScriptActionS2CPacket.UiQuestNote) ||
              sa.Content.Length == 0)));

    private static Dictionary<byte, List<JxWireFrame>> GroupSignificant(IReadOnlyList<JxWireFrame> frames)
    {
        var grouped = new Dictionary<byte, List<JxWireFrame>>();
        foreach (JxWireFrame frame in frames)
        {
            if (IgnoredBackgroundS2cOpcodes.Contains(frame.Opcode))
            {
                continue;
            }

            if (!grouped.TryGetValue(frame.Opcode, out List<JxWireFrame>? list))
            {
                list = [];
                grouped[frame.Opcode] = list;
            }

            list.Add(frame);
        }

        return grouped;
    }

    private ParityFinding CreateFinding(
        FindingSeverity severity,
        FindingCategory category,
        WireActionWindow window,
        int mapId,
        int playerMpsX,
        int playerMpsY,
        string fieldPath,
        string legacyValue,
        string rebornValue,
        IReadOnlyList<string> legacyOps,
        IReadOnlyList<string> rebornOps,
        DateTimeOffset now)
    {
        int seq = Interlocked.Increment(ref _findingSeq);
        return new ParityFinding(
            FindingId: $"WIRE-{now:yyyyMMdd}-{seq:D4}",
            ClusterKey: $"{category}::Map{mapId}::C2S_0x{window.PrimaryC2s.Opcode:X2}::{fieldPath}",
            Severity: severity,
            Category: category,
            StepIndex: window.ActionSequence,
            StepName: window.ActionLabel,
            FieldPath: fieldPath,
            LegacyValue: legacyValue,
            RebornValue: rebornValue,
            Repro: new FindingReproContext(mapId, [playerMpsX, playerMpsY], null, [window.ActionLabel]),
            StateDelta: new FindingStateDelta(0, 0, 0, 0, 0, 0, mapId, mapId),
            LegacyRecentS2cOpcodes: legacyOps,
            RebornRecentS2cOpcodes: rebornOps,
            Artifacts: new FindingArtifacts(),
            TimestampUtc: now);
    }
}
