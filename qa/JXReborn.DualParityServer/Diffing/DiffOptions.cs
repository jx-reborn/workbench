namespace JXReborn.DualParityServer.Diffing;

/// <summary>
/// Normalization and tolerance rules controlling what <see cref="SemanticDiffEngine"/> flags vs ignores.
/// </summary>
public sealed class DiffOptions
{
    public int PositionToleranceMps { get; init; } = 64;

    public int CriticalPositionDriftMps { get; init; } = 256;

    public bool CompareInventory { get; init; } = true;

    public bool CompareSystemMessages { get; init; } = true;

    public bool CompareNearbyStaticNpcs { get; init; } = true;

    public bool DeduplicateUnchangedStateSnowballs { get; init; } = true;

    public IReadOnlySet<string> KnownDeviationClusterKeys { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> IgnoredSystemMessageSubstrings { get; init; } = [];
}
