using System.Collections.Generic;

namespace JXReborn.ScriptMigrator.Models;

public sealed class ScriptGenerationResult
{
    public required string KindFilter { get; init; }
    public required int TotalCandidatesEvaluated { get; init; }
    public required int GeneratedScriptCount { get; init; }
    public required int GeneratedTrapCount { get; init; }
    public required int GeneratedObjectCount { get; init; }
    public required int GeneratedItemCount { get; init; }
    public required int GeneratedMonsterCount { get; init; }
    public required int GeneratedNpcCount { get; init; }
    public required int SkippedAlreadyOwnedCount { get; init; }
    public required int FailedClosedCount { get; init; }
    public required IReadOnlyList<string> WrittenCSharpFiles { get; init; }
    public required IReadOnlyList<ManualMigrationQueueItem> ManualQueue { get; init; }
}
