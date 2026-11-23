namespace JXReborn.WireParityProxy.Scenarios;

/// <summary>
/// Summary report produced after running a <see cref="WireParityScenario"/> through <see cref="WireScenarioRunner"/>.
/// </summary>
public sealed record WireScenarioRunReport(
    string ScenarioName,
    ushort TargetMapId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    int TotalSteps,
    int PassedSteps,
    int StepFailures,
    int TotalFindings,
    int CriticalFindings,
    int WarningFindings,
    int ProtocolViolations,
    IReadOnlyList<WireScenarioStepResult> StepResults,
    string? ReportDirectory);
