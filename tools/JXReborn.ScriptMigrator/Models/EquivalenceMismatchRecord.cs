namespace JXReborn.ScriptMigrator.Models;

/// <summary>
/// Records a single behavioral divergence between the legacy Lua 4.0 VM ground truth and a C# script handler.
/// </summary>
public sealed record EquivalenceMismatchRecord(
    string Utf8ScriptPath,
    string ScriptKind,
    string StateVectorSummary,
    string ExpectedLuaTrace,
    string ActualCSharpTrace,
    string DiffSummary);
