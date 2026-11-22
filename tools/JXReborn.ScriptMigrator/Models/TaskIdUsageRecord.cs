using System.Collections.Generic;

namespace JXReborn.ScriptMigrator.Models;

/// <summary>
/// Tracks every legacy Lua script referencing a specific TaskId, TempTaskId, or GlobalValueId.
/// </summary>
public sealed class TaskIdUsageRecord
{
    public required int Id { get; init; }
    public required IReadOnlyList<string> Files { get; init; }
}
