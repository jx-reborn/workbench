using System.Collections.Generic;

namespace JXReborn.ScriptMigrator.Models;

/// <summary>
/// Ground-truth static analysis record for a single legacy Lua file.
/// </summary>
public sealed class ScriptInventoryEntry
{
    public required string RelativePath { get; init; }
    public required string CanonicalScriptPath { get; init; }
    public required string Utf8DisplayPath { get; init; }
    public required LegacyScriptScope Scope { get; init; }
    public required int Bucket { get; init; }
    public required string BucketName { get; init; }
    public required bool IsExcludedSimBot { get; init; }
    public required long ByteSize { get; init; }
    public required int LineCount { get; init; }
    public required string Sha256 { get; init; }
    public required string NormalizedAstHash { get; init; }
    public required bool IsPureStaticDialog { get; init; }
    public required IReadOnlyList<string> CalledApis { get; init; }
    public required IReadOnlyList<int> ReferencedTaskIds { get; init; }
    public required IReadOnlyList<int> ReferencedTempTaskIds { get; init; }
    public required IReadOnlyList<int> ReferencedGlobalValueIds { get; init; }
    public required IReadOnlyList<string> IncludedFiles { get; init; }
    public required IReadOnlyList<string> DefinedFunctions { get; init; }
}
