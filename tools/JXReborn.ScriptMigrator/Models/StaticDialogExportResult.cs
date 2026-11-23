using System.Collections.Generic;

namespace JXReborn.ScriptMigrator.Models;

/// <summary>
/// Summary report produced by <see cref="Generators.StaticDialogExporter"/>.
/// </summary>
public sealed class StaticDialogExportResult
{
    public required int TotalPureStaticCandidates { get; init; }
    public required int ExportedEntries { get; init; }
    public required int SkippedCandidates { get; init; }
    public required IReadOnlyList<string> SkippedPaths { get; init; }
}
