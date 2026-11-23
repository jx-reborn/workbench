using System.Collections.Generic;

namespace JXReborn.ScriptMigrator.Models;

/// <summary>
/// Result of auditing <c>pack-tieudaotu</c> script bindings and C# registrations against the 100% legacy manifest.
/// </summary>
public sealed class CoverageAuditReport
{
    public required string GeneratedAtUtc { get; init; }
    public required int TotalLegacyActiveScripts { get; init; }
    public required int RegisteredCSharpScriptCodes { get; init; }
    public int LegacySourceAttributedScripts { get; init; }
    public int AccountedSkillFormulaScripts { get; init; }
    public required int AmbientDialogEntries { get; init; }
    public required int UniqueMapNpcScriptPaths { get; init; }
    public required int ResolvedMapNpcScriptPaths { get; init; }
    public required int UniqueMapObjectScriptPaths { get; init; }
    public required int ResolvedMapObjectScriptPaths { get; init; }
    public required int UniqueMapPortalScriptPaths { get; init; }
    public required int UniqueActiveItemScriptPaths { get; init; }
    public required int ResolvedActiveItemScriptPaths { get; init; }
    public required int BrokenScriptPaths { get; init; }
    public required int UnaccountedLuaFiles { get; init; }
    public required IReadOnlyList<string> UnresolvedNpcPathsSample { get; init; }
    public required IReadOnlyList<string> UnresolvedObjectPathsSample { get; init; }
    public required IReadOnlyList<string> UnresolvedItemPathsSample { get; init; }
    public required IReadOnlyList<string> BrokenPathsSample { get; init; }
}
