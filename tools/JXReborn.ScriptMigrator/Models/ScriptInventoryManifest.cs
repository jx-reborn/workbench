using System.Collections.Generic;

namespace JXReborn.ScriptMigrator.Models;

/// <summary>
/// Complete 100% inventory manifest of all legacy Lua files in <c>server1/script</c> and <c>gateway/s3relay/script</c>.
/// </summary>
public sealed class ScriptInventoryManifest
{
    public required string GeneratedAtUtc { get; init; }
    public required string LegacyRootPath { get; init; }
    public required int TotalFiles { get; init; }
    public required int Server1TotalFiles { get; init; }
    public required int Server1ActiveFiles { get; init; }
    public required int Server1ExcludedFiles { get; init; }
    public required int S3RelayTotalFiles { get; init; }
    public required int S3RelayActiveFiles { get; init; }
    public required int S3RelayExcludedFiles { get; init; }
    public required int PureStaticDialogCandidates { get; init; }
    public required IReadOnlyDictionary<string, int> BucketCounts { get; init; }
    public required IReadOnlyDictionary<string, int> TopApiCallFileCounts { get; init; }
    public required IReadOnlyList<ScriptInventoryEntry> Entries { get; init; }
}
