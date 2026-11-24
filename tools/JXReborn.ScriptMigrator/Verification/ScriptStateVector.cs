using System;
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
    int FuYuan = 0,
    byte BaseCamp = 0,
    byte CurrentCamp = 0,
    ushort InitialRevivalMapId = 53,
    int InitialRevivalPointId = 19,
    byte PkMode = 0,
    sbyte PkValue = 0,
    byte RankId = 0,
    string RoleName = "JxHero",
    int RandomSeed = 20261123,
    DateTime? SimulatedLocalTime = null,
    IReadOnlyDictionary<ushort, int>? Tasks = null,
    IReadOnlyDictionary<ushort, int>? TempTasks = null,
    IReadOnlyDictionary<int, int>? GlobalValues = null,
    IReadOnlyDictionary<string, int>? OwnedItemCounts = null,
    IReadOnlySet<string>? OwnedItemCodes = null,
    IReadOnlyDictionary<int, int>? LearnedSkills = null,
    IReadOnlyList<int>? DialogChoices = null,
    IReadOnlyList<int>? NumberInputs = null,
    int TeamSize = 0,
    int InitiatorPartySlot = 1,
    int ActiveTimerId = 0,
    IReadOnlyDictionary<ushort, int>? TeammateTasks = null);
