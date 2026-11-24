using JXReborn.ClientDriver;
using JXReborn.DualParityServer.Diffing;
using JXReborn.WireParityProxy.Configuration;
using JXReborn.WireParityProxy.ShadowStates;
using JXReborn.WireParityProxy.Wire;

namespace JXReborn.WireParityProxy.Diffing;

/// <summary>
/// Coordinates all 3 verification layers (Protocol Safety Gate, Action-Window Wire Diff,
/// and Semantic State Diff) plus State Drift Detection for a live proxy session.
/// </summary>
public sealed class WireParityDiffCoordinator
{
    private readonly ActionWindowWireDiffer _wireDiffer = new();
    private readonly SemanticDiffEngine _semanticDiff;
    private readonly StateDriftDetector _driftDetector;
    private readonly Lock _sync = new();
    private readonly List<WireStepReport> _steps = [];
    private readonly List<ParityFinding> _allFindings = [];
    private readonly List<(WireActionWindow Window, IReadOnlyList<JxWireFrame> PrimaryS2c, IReadOnlyList<JxWireFrame> ShadowS2c)> _capturedWindows = [];

    private WorldDomSnapshot? _prevPrimarySnapshot;
    private WorldDomSnapshot? _prevShadowSnapshot;
    private DriftAssessment _latestDrift = new(false, FindingSeverity.Info, 0, false, 0, "Not started");

    public WireParityDiffCoordinator(WireParityProxyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _semanticDiff = new SemanticDiffEngine(new DiffOptions
        {
            PositionToleranceMps = options.PositionToleranceMps,
            CriticalPositionDriftMps = options.CriticalPositionDriftMps,
            CompareInventory = true,
            CompareSystemMessages = false,
            CompareNearbyStaticNpcs = false
        });
        _driftDetector = new StateDriftDetector(options.PositionToleranceMps, options.CriticalPositionDriftMps);
    }

    public DriftAssessment LatestDrift
    {
        get
        {
            lock (_sync)
            {
                return _latestDrift;
            }
        }
    }

    public void RecordImmediateFinding(ParityFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        lock (_sync)
        {
            _allFindings.Add(finding);
        }
    }

    public WireStepReport CompleteActionWindow(
        WireActionWindow window,
        ProxyShadowClientState primaryState,
        ProxyShadowClientState shadowState)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(primaryState);
        ArgumentNullException.ThrowIfNull(shadowState);

        DateTimeOffset closedAt = DateTimeOffset.UtcNow;
        (IReadOnlyList<JxWireFrame> primaryS2c, IReadOnlyList<JxWireFrame> shadowS2c) = window.CloseAndSnapshot(closedAt);

        IReadOnlyList<ParityFinding> wireFindings = _wireDiffer.CompareWindow(
            window,
            primaryS2c,
            shadowS2c,
            primaryState.MapId,
            primaryState.MpsX,
            primaryState.MpsY);

        WorldDomSnapshot primarySnap = primaryState.ToWorldDomSnapshot();
        WorldDomSnapshot shadowSnap = shadowState.ToWorldDomSnapshot();

        StepComparisonResult stateResult;
        DriftAssessment drift;
        lock (_sync)
        {
            stateResult = _semanticDiff.Compare(
                stepIndex: window.ActionSequence,
                stepName: window.ActionLabel,
                actionSummary: $"C2S 0x{window.PrimaryC2s.Opcode:X2}",
                legacyAfter: primarySnap,
                rebornAfter: shadowSnap,
                legacyBefore: _prevPrimarySnapshot,
                rebornBefore: _prevShadowSnapshot);

            _prevPrimarySnapshot = primarySnap;
            _prevShadowSnapshot = shadowSnap;

            drift = _driftDetector.Evaluate(primaryState, shadowState);
            _latestDrift = drift;
        }

        var combinedFindings = new List<ParityFinding>(wireFindings.Count + stateResult.Findings.Count);
        combinedFindings.AddRange(wireFindings);
        combinedFindings.AddRange(stateResult.Findings);

        var report = new WireStepReport(
            StepIndex: window.ActionSequence,
            ActionLabel: window.ActionLabel,
            C2sOpcode: window.PrimaryC2s.Opcode,
            C2sHex: window.PrimaryC2s.ToHexString(),
            C2sWasRewritten: window.C2sWasRewritten,
            PrimaryS2cCount: primaryS2c.Count,
            ShadowS2cCount: shadowS2c.Count,
            PrimaryS2cOpcodes: primaryS2c.Select(f => $"0x{f.Opcode:X2}").Distinct().ToArray(),
            ShadowS2cOpcodes: shadowS2c.Select(f => $"0x{f.Opcode:X2}").Distinct().ToArray(),
            Drift: drift,
            Findings: combinedFindings,
            CompletedAtUtc: closedAt);

        lock (_sync)
        {
            _steps.Add(report);
            _allFindings.AddRange(combinedFindings);
            _capturedWindows.Add((window, primaryS2c, shadowS2c));
        }

        return report;
    }

    public IReadOnlyList<WireStepReport> GetSteps()
    {
        lock (_sync)
        {
            return _steps.ToArray();
        }
    }

    public IReadOnlyList<ParityFinding> GetFindings()
    {
        lock (_sync)
        {
            return _allFindings.ToArray();
        }
    }

    public bool TryGetCapturedWindow(
        int stepIndex,
        out (WireActionWindow Window, IReadOnlyList<JxWireFrame> PrimaryS2c, IReadOnlyList<JxWireFrame> ShadowS2c) entry)
    {
        lock (_sync)
        {
            foreach (var item in _capturedWindows)
            {
                if (item.Window.ActionSequence == stepIndex)
                {
                    entry = item;
                    return true;
                }
            }
        }

        entry = default;
        return false;
    }

    public void ResetDrift()
    {
        lock (_sync)
        {
            _driftDetector.Reset();
            _semanticDiff.ResetStateTracking();
            _latestDrift = new DriftAssessment(false, FindingSeverity.Info, 0, false, 0, "Reset");
        }
    }
}
