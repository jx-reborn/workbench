using System.Collections.Generic;

namespace JXReborn.ScriptMigrator.Models;

/// <summary>
/// Summary report produced by <c>DifferentialScriptEquivalenceVerifier</c> comparing legacy Lua 4.0 execution
/// traces against C# runtime script traces across boundary state vectors.
/// </summary>
public sealed class EquivalenceAuditReport
{
    public required string GeneratedAtUtc { get; init; }
    public required int VerifiedGeneratedScripts { get; init; }
    public required int VerifiedAmbientDialogs { get; init; }
    public required int TotalStateVectorsExecuted { get; init; }
    public required int MatchedVectors { get; init; }
    public required int MismatchedScripts { get; init; }
    public required IReadOnlyList<EquivalenceMismatchRecord> Mismatches { get; init; }
}
