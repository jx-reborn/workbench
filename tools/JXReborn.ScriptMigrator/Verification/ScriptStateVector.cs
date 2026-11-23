using System.Collections.Generic;

namespace JXReborn.ScriptMigrator.Verification;

/// <summary>
/// Deterministic input player/world state and interactive dialog choice sequence for differential script execution.
/// </summary>
public sealed record ScriptStateVector(
    string Label,
    byte Level = 50,
    byte Gender = 0,
    byte Series = 0,
    byte CurrentFactionId = 255,
    byte EffectiveFactionId = 255,
    ushort MapId = 53,
    int MpsX = 51200,
    int MpsY = 102400,
    byte FightMode = 0,
    int InventoryMoney = 100_000,
    int FreeBagCells = 20,
    int Repute = 100,
    string RoleName = "JxHero",
    int RandomSeed = 20261123,
    IReadOnlyDictionary<ushort, int>? Tasks = null,
    IReadOnlyDictionary<ushort, int>? TempTasks = null,
    IReadOnlyDictionary<int, int>? GlobalValues = null,
    IReadOnlySet<string>? OwnedItemCodes = null,
    IReadOnlyDictionary<int, int>? LearnedSkills = null,
    IReadOnlyList<int>? DialogChoices = null);
