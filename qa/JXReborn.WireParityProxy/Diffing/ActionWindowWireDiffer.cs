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
        ProtocolOpcodes.s2c_extend
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
                findings.Add(CreateFinding(
                    FindingSeverity.Critical,
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
        }

        foreach ((byte opcode, List<JxWireFrame> sFrames) in shadowByOp)
        {
            if (!primaryByOp.ContainsKey(opcode))
            {
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
