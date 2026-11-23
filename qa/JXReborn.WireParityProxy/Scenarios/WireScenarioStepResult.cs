namespace JXReborn.WireParityProxy.Scenarios;

/// <summary>
/// Execution telemetry for a single <see cref="WireScenarioStep"/>.
/// </summary>
public sealed record WireScenarioStepResult(
    int StepNumber,
    string Name,
    string? Region,
    string Type,
    bool Executed,
    string Status,
    string? ErrorMessage,
    int MapIdAfter,
    int TileXAfter,
    int TileYAfter,
    int NewFindingsCount,
    int CriticalFindingsCount,
    IReadOnlyList<string> ExportedCaptures,
    long ElapsedMs);
