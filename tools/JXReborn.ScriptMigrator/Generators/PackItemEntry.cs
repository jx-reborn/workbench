namespace JXReborn.ScriptMigrator.Generators;

public sealed record PackItemEntry(
    string Code,
    int Genre,
    int DetailType,
    int ParticularType,
    int Level,
    byte GridWidthCells,
    byte GridHeightCells,
    ushort MaxStackCount);
