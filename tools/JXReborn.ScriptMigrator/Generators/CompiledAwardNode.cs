using System.Collections.Generic;

namespace JXReborn.ScriptMigrator.Generators;

public enum CompiledAwardNodeKind
{
    SequentialGroup,
    RandomWeightedGroup,
    LeafItem,
    LeafGoldEquip,
    LeafExp,
    LeafExpTl,
    LeafMoney,
    LeafRepute,
    LeafChestExpAward,
    LeafBingoBoxExpAward,
}

/// <summary>
/// Statically verified, catalog-resolved representation of a <c>tbAwardTemplet</c> reward tree.
/// </summary>
public sealed record CompiledAwardNode(
    CompiledAwardNodeKind Kind,
    double RatePercent = 0.0,
    int Count = 1,
    string ItemCode = "",
    string ItemNameUtf8 = "",
    ushort MaxStackCount = 1,
    bool ConsumeRandomSeriesRoll = false,
    int? FixedSeries = null,
    int BindStateRaw = 0,
    long NumericAmount = 0,
    IReadOnlyList<CompiledAwardNode>? Children = null,
    string? CatalogItemNameUtf8 = null,
    string? CallbackKind = null,
    string? CallbackTitleUtf8 = null,
    int ExpiredTimeMinutes = 0,
    IReadOnlyList<int>? SpecParams = null);
