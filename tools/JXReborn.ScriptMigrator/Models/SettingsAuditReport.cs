using System.Collections.Generic;

namespace JXReborn.ScriptMigrator.Models;

/// <summary>
/// Audit summary of <c>server1/settings/</c> and <c>server1/settings/maplist.ini</c> against <c>pack-tieudaotu/data/</c>.
/// </summary>
public sealed class SettingsAuditReport
{
    public required string GeneratedAtUtc { get; init; }
    public required int TotalSettingsFiles { get; init; }
    public required long TotalSettingsBytes { get; init; }
    public required int MapListDeclaredCount { get; init; }
    public required int ExportedPackMapCount { get; init; }
    public required IReadOnlyList<int> MissingMapIds { get; init; }
    public required IReadOnlyDictionary<string, int> SettingsByTopFolder { get; init; }
    public required IReadOnlyList<string> UncoveredRootSettingsFiles { get; init; }
}
