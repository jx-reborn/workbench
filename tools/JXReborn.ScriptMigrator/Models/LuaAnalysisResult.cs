using System.Collections.Generic;

namespace JXReborn.ScriptMigrator.Models;

/// <summary>
/// Static analysis output produced by <see cref="Analyzers.LuaStaticAnalyzer"/> for a single Lua source.
/// </summary>
public sealed class LuaAnalysisResult
{
    public required int LineCount { get; init; }
    public required string NormalizedAstHash { get; init; }
    public required bool IsPureStaticDialog { get; init; }
    public required IReadOnlyList<string> CalledApis { get; init; }
    public required IReadOnlyList<int> ReferencedTaskIds { get; init; }
    public required IReadOnlyList<int> ReferencedTempTaskIds { get; init; }
    public required IReadOnlyList<int> ReferencedGlobalValueIds { get; init; }
    public required IReadOnlyList<string> IncludedFiles { get; init; }
    public required IReadOnlyList<string> DefinedFunctions { get; init; }
}
