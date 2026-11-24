using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace JXReborn.ScriptMigrator.Verification;

/// <summary>
/// Canonical observable side-effect trace produced identically by both <see cref="LuaOracleHarness"/>
/// and <see cref="RecordingScriptContext"/> so any behavioral divergence is caught deterministically.
/// </summary>
public sealed class ScriptEffectTrace
{
    private readonly SortedDictionary<int, SortedDictionary<ushort, int>> _teammateTasks = [];
    private readonly SortedDictionary<int, SortedDictionary<ushort, int>> _teammateTempTasks = [];
    private readonly SortedDictionary<int, long> _teammateExp = [];

    public List<string> Events { get; } = [];

    public bool OpenedStoreBox { get; set; }

    public (ushort MapId, int ReviveId)? RevivalPoint { get; set; }

    public (ushort MapId, int MpsX, int MpsY)? TempDeathRevival { get; set; }

    public bool UseReviveIdOnLogin { get; set; }

    public bool IsTownPortalDisabled { get; set; }

    public bool IsPkSwitchForbidden { get; set; }

    public bool IsDeathPunishDisabled { get; set; }

    public int ActiveScriptTimerId { get; set; }

    public SortedSet<ushort> UnlockedStations { get; } = [];

    public SortedSet<int> RecordedWayPoints { get; } = [];

    public int ProtectFrames { get; set; }

    public List<(int SkillId, int Level, int StateType, int Duration)> SkillStates { get; } = [];

    public bool DidTeleport { get; set; }

    public ushort FinalMapId { get; set; }

    public int FinalMpsX { get; set; }

    public int FinalMpsY { get; set; }

    public byte FinalFightMode { get; set; }

    public byte FinalCurrentFactionId { get; set; } = 255;

    public byte InitialCurrentFactionId { get; private init; } = 255;

    public int FinalMoney { get; set; }

    public long ExpGranted { get; set; }

    public int FinalRepute { get; set; }

    public int FinalFuYuan { get; set; }

    public byte FinalBaseCamp { get; set; }

    public byte FinalCurrentCamp { get; set; }

    public ushort CurrentRevivalMapId { get; set; }

    public int CurrentRevivalPointId { get; set; }

    public byte FinalPkMode { get; set; }

    public sbyte FinalPkValue { get; set; }

    public byte FinalRankId { get; set; }

    public byte? ObjectPropState { get; set; }

    public int TeamSize { get; init; }

    public int InitiatorPartySlot { get; init; } = 1;

    public int CurrentPlayerIndex { get; private set; } = 1;

    public bool IsCurrentPlayerCaptain => TeamSize > 0 && CurrentPlayerIndex == 1;

    public SortedDictionary<ushort, int> Tasks { get; } = [];

    public SortedDictionary<ushort, int> TempTasks { get; } = [];

    public SortedDictionary<int, int> GlobalValues { get; } = [];

    public IReadOnlyDictionary<int, SortedDictionary<ushort, int>> TeammateTasksBySlot => _teammateTasks;

    public IReadOnlyDictionary<int, long> TeammateExpBySlot => _teammateExp;

    public string? ItemResult { get; set; }

    public static ScriptEffectTrace CreateFromVector(ScriptStateVector vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        int initSlot = vector.TeamSize > 0 ? Math.Clamp(vector.InitiatorPartySlot, 1, vector.TeamSize) : 1;
        var trace = new ScriptEffectTrace
        {
            FinalMapId = vector.MapId,
            FinalMpsX = vector.MpsX,
            FinalMpsY = vector.MpsY,
            FinalFightMode = vector.FightMode,
            InitialCurrentFactionId = vector.CurrentFactionId,
            FinalCurrentFactionId = vector.CurrentFactionId,
            FinalMoney = vector.InventoryMoney,
            FinalRepute = vector.Repute,
            FinalFuYuan = vector.FuYuan,
            FinalBaseCamp = vector.BaseCamp,
            FinalCurrentCamp = vector.CurrentCamp,
            CurrentRevivalMapId = vector.InitialRevivalMapId,
            CurrentRevivalPointId = vector.InitialRevivalPointId,
            FinalPkMode = vector.PkMode,
            FinalPkValue = vector.PkValue,
            FinalRankId = vector.RankId,
            ActiveScriptTimerId = vector.ActiveTimerId,
            TeamSize = Math.Max(0, vector.TeamSize),
            InitiatorPartySlot = initSlot,
            CurrentPlayerIndex = initSlot,
        };

        if (vector.Tasks is not null)
        {
            foreach (KeyValuePair<ushort, int> kv in vector.Tasks)
            {
                if (kv.Value != 0)
                {
                    trace.Tasks[kv.Key] = kv.Value;
                }
            }
        }

        if (vector.TeammateTasks is not null && vector.TeamSize >= 2)
        {
            for (int slot = 1; slot <= vector.TeamSize; slot++)
            {
                if (slot == initSlot) continue;
                var slotDict = new SortedDictionary<ushort, int>();
                foreach (KeyValuePair<ushort, int> kv in vector.TeammateTasks)
                {
                    if (kv.Value != 0)
                    {
                        slotDict[kv.Key] = kv.Value;
                    }
                }

                if (slotDict.Count > 0)
                {
                    trace._teammateTasks[slot] = slotDict;
                }
            }
        }

        if (vector.TempTasks is not null)
        {
            foreach (KeyValuePair<ushort, int> kv in vector.TempTasks)
            {
                if (kv.Value != 0)
                {
                    trace.TempTasks[kv.Key] = kv.Value;
                }
            }
        }

        if (vector.GlobalValues is not null)
        {
            foreach (KeyValuePair<int, int> kv in vector.GlobalValues)
            {
                if (kv.Value != 0)
                {
                    trace.GlobalValues[kv.Key] = kv.Value;
                }
            }
        }

        return trace;
    }

    public void SwitchActivePlayer(int playerIndex)
    {
        int maxSlots = Math.Max(1, TeamSize);
        if ((uint)(playerIndex - 1) < (uint)maxSlots)
        {
            CurrentPlayerIndex = playerIndex;
        }
    }

    public string PrefixForActivePlayer(string rawEvent) =>
        CurrentPlayerIndex == InitiatorPartySlot ? rawEvent : $"P{CurrentPlayerIndex}:{rawEvent}";

    public void AddExperienceForActivePlayer(long exp)
    {
        if (exp <= 0)
        {
            return;
        }

        if (CurrentPlayerIndex == InitiatorPartySlot)
        {
            ExpGranted += exp;
        }
        else
        {
            _teammateExp[CurrentPlayerIndex] = _teammateExp.GetValueOrDefault(CurrentPlayerIndex, 0L) + exp;
        }
    }

    public void SetTaskValue(ushort id, int val)
    {
        if (CurrentPlayerIndex != InitiatorPartySlot)
        {
            if (!_teammateTasks.TryGetValue(CurrentPlayerIndex, out SortedDictionary<ushort, int>? tMap))
            {
                if (val == 0) return;
                tMap = [];
                _teammateTasks[CurrentPlayerIndex] = tMap;
            }

            if (val == 0)
            {
                tMap.Remove(id);
            }
            else
            {
                tMap[id] = val;
            }

            return;
        }

        if (val == 0)
        {
            Tasks.Remove(id);
        }
        else
        {
            Tasks[id] = val;
        }
    }

    public int GetTaskValue(ushort id)
    {
        if (CurrentPlayerIndex != InitiatorPartySlot)
        {
            return _teammateTasks.TryGetValue(CurrentPlayerIndex, out SortedDictionary<ushort, int>? tMap)
                ? tMap.GetValueOrDefault(id, 0)
                : 0;
        }

        return Tasks.GetValueOrDefault(id, 0);
    }

    public void SetTempTaskValue(ushort id, int val)
    {
        if (CurrentPlayerIndex != InitiatorPartySlot)
        {
            if (!_teammateTempTasks.TryGetValue(CurrentPlayerIndex, out SortedDictionary<ushort, int>? tMap))
            {
                if (val == 0) return;
                tMap = [];
                _teammateTempTasks[CurrentPlayerIndex] = tMap;
            }

            if (val == 0) tMap.Remove(id);
            else tMap[id] = val;
            return;
        }

        if (val == 0)
        {
            TempTasks.Remove(id);
        }
        else
        {
            TempTasks[id] = val;
        }
    }

    public int GetTempTaskValue(ushort id)
    {
        if (CurrentPlayerIndex != InitiatorPartySlot)
        {
            return _teammateTempTasks.TryGetValue(CurrentPlayerIndex, out SortedDictionary<ushort, int>? tMap)
                ? tMap.GetValueOrDefault(id, 0)
                : 0;
        }

        return TempTasks.GetValueOrDefault(id, 0);
    }

    public void SetGlobalVal(int id, int val)
    {
        if (val == 0)
        {
            GlobalValues.Remove(id);
        }
        else
        {
            GlobalValues[id] = val;
        }
    }

    public int GetGlobalVal(int id) => GlobalValues.GetValueOrDefault(id, 0);

    public string ToCanonicalString()
    {
        var sb = new StringBuilder(256);
        sb.Append("Map=").Append(FinalMapId)
          .Append(",Pos=(").Append(FinalMpsX).Append(',').Append(FinalMpsY).Append(')')
          .Append(",Teleported=").Append(DidTeleport)
          .Append(",Fight=").Append(FinalFightMode)
          .Append(",Money=").Append(FinalMoney)
          .Append(",Exp+=").Append(ExpGranted)
          .Append(",Repute=").Append(FinalRepute);

        if (FinalCurrentFactionId != InitialCurrentFactionId)
        {
            sb.Append(",Faction=").Append(FinalCurrentFactionId);
        }

        if (OpenedStoreBox)
        {
            sb.Append(",StoreBox=true");
        }

        if (ObjectPropState is { } propState)
        {
            sb.Append(",PropState=").Append(propState);
        }

        if (FinalFuYuan != 0)
        {
            sb.Append(",FuYuan=").Append(FinalFuYuan);
        }

        if (FinalBaseCamp != 0 || FinalCurrentCamp != 0)
        {
            sb.Append(",Camp=").Append(FinalBaseCamp).Append(':').Append(FinalCurrentCamp);
        }

        if (FinalPkMode != 0 || FinalPkValue != 0)
        {
            sb.Append(",PK=").Append(FinalPkMode).Append(':').Append(FinalPkValue);
        }

        if (FinalRankId != 0)
        {
            sb.Append(",Rank=").Append(FinalRankId);
        }

        if (RevivalPoint is { } rev)
        {
            sb.Append(",Revival=").Append(rev.MapId).Append(':').Append(rev.ReviveId);
        }

        if (TempDeathRevival is { } tempRev)
        {
            sb.Append(",TempRev=").Append(tempRev.MapId).Append(':').Append(tempRev.MpsX).Append(':').Append(tempRev.MpsY);
        }

        if (UseReviveIdOnLogin)
        {
            sb.Append(",LogoutRV=true");
        }

        if (IsTownPortalDisabled)
        {
            sb.Append(",NoTownP=true");
        }

        if (IsPkSwitchForbidden)
        {
            sb.Append(",NoPkSwitch=true");
        }

        if (IsDeathPunishDisabled)
        {
            sb.Append(",NoPunish=true");
        }

        if (ProtectFrames > 0)
        {
            sb.Append(",Protect=").Append(ProtectFrames);
        }

        if (UnlockedStations.Count > 0)
        {
            sb.Append(",Stations=[").Append(string.Join(",", UnlockedStations)).Append(']');
        }

        if (RecordedWayPoints.Count > 0)
        {
            sb.Append(",WayPoints=[").Append(string.Join(",", RecordedWayPoints)).Append(']');
        }

        if (SkillStates.Count > 0)
        {
            sb.Append(",SkillStates=[")
              .Append(string.Join(";", SkillStates.Select(static s => $"{s.SkillId}:{s.Level}:{s.StateType}:{s.Duration}")))
              .Append(']');
        }

        if (Tasks.Count > 0)
        {
            sb.Append(",Tasks={")
              .Append(string.Join(",", Tasks.Select(static kv => $"{kv.Key}={kv.Value}")))
              .Append('}');
        }

        foreach (KeyValuePair<int, SortedDictionary<ushort, int>> slotKv in _teammateTasks)
        {
            if (slotKv.Value.Count > 0)
            {
                sb.Append($",P{slotKv.Key}Tasks={{")
                  .Append(string.Join(",", slotKv.Value.Select(static kv => $"{kv.Key}={kv.Value}")))
                  .Append('}');
            }
        }

        foreach (KeyValuePair<int, long> expKv in _teammateExp)
        {
            if (expKv.Value > 0)
            {
                sb.Append($",P{expKv.Key}Exp+=").Append(expKv.Value);
            }
        }

        if (TempTasks.Count > 0)
        {
            sb.Append(",TempTasks={")
              .Append(string.Join(",", TempTasks.Select(static kv => $"{kv.Key}={kv.Value}")))
              .Append('}');
        }

        if (GlobalValues.Count > 0)
        {
            sb.Append(",Glb={")
              .Append(string.Join(",", GlobalValues.Select(static kv => $"{kv.Key}={kv.Value}")))
              .Append('}');
        }

        if (!string.IsNullOrEmpty(ItemResult))
        {
            sb.Append(",ItemRes=").Append(ItemResult);
        }

        if (Events.Count > 0)
        {
            sb.Append(",Events=[").Append(string.Join(" | ", Events)).Append(']');
        }

        return sb.ToString();
    }

    public static string EscapeEventText(string text) =>
        text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    public static string FormatSayEvent(string prompt, IReadOnlyList<string> optionLabels) =>
        $"SAY(\"{EscapeEventText(prompt)}\",[{string.Join(",", optionLabels.Select(static o => $"\"{EscapeEventText(o)}\""))}])";

    public static string FormatTalkEvent(IReadOnlyList<string> pages) =>
        $"TALK([{string.Join(",", pages.Select(static p => $"\"{EscapeEventText(p)}\""))}])";
}
