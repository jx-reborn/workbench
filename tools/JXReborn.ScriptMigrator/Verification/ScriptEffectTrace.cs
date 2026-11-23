using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace JXReborn.ScriptMigrator.Verification;

/// <summary>
/// Canonical observable side-effect trace produced identically by both <see cref="LuaOracleHarness"/>
/// and <see cref="RecordingScriptContext"/> so any behavioral divergence is caught deterministically.
/// </summary>
public sealed class ScriptEffectTrace
{
    public List<string> Events { get; } = [];

    public bool OpenedStoreBox { get; set; }

    public (ushort MapId, int ReviveId)? RevivalPoint { get; set; }

    public SortedSet<ushort> UnlockedStations { get; } = [];

    public SortedSet<int> RecordedWayPoints { get; } = [];

    public int ProtectFrames { get; set; }

    public List<(int SkillId, int Level, int StateType, int Duration)> SkillStates { get; } = [];

    public bool DidTeleport { get; set; }

    public ushort FinalMapId { get; set; }

    public int FinalMpsX { get; set; }

    public int FinalMpsY { get; set; }

    public byte FinalFightMode { get; set; }

    public int FinalMoney { get; set; }

    public long ExpGranted { get; set; }

    public int FinalRepute { get; set; }

    public SortedDictionary<ushort, int> Tasks { get; } = [];

    public SortedDictionary<ushort, int> TempTasks { get; } = [];

    public SortedDictionary<int, int> GlobalValues { get; } = [];

    public string? ItemResult { get; set; }

    public static ScriptEffectTrace CreateFromVector(ScriptStateVector vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        var trace = new ScriptEffectTrace
        {
            FinalMapId = vector.MapId,
            FinalMpsX = vector.MpsX,
            FinalMpsY = vector.MpsY,
            FinalFightMode = vector.FightMode,
            FinalMoney = vector.InventoryMoney,
            FinalRepute = vector.Repute,
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

    public void SetTaskValue(ushort id, int val)
    {
        if (val == 0)
        {
            Tasks.Remove(id);
        }
        else
        {
            Tasks[id] = val;
        }
    }

    public int GetTaskValue(ushort id) => Tasks.GetValueOrDefault(id, 0);

    public void SetTempTaskValue(ushort id, int val)
    {
        if (val == 0)
        {
            TempTasks.Remove(id);
        }
        else
        {
            TempTasks[id] = val;
        }
    }

    public int GetTempTaskValue(ushort id) => TempTasks.GetValueOrDefault(id, 0);

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

        if (OpenedStoreBox)
        {
            sb.Append(",StoreBox=true");
        }

        if (RevivalPoint is { } rev)
        {
            sb.Append(",Revival=").Append(rev.MapId).Append(':').Append(rev.ReviveId);
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

        if (ItemResult is not null)
        {
            sb.Append(",ItemResult=").Append(ItemResult);
        }

        if (Events.Count > 0)
        {
            sb.Append(" | Events=[")
              .Append(string.Join(" -> ", Events))
              .Append(']');
        }

        return sb.ToString();
    }

    public static string EscapeEventText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
    }

    public static string FormatSayEvent(string prompt, IReadOnlyList<string> optionLabels) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"SAY(\"{EscapeEventText(prompt)}\",[{string.Join(",", optionLabels.Select(static o => $"\"{EscapeEventText(o)}\""))}])");

    public static string FormatTalkEvent(IReadOnlyList<string> pages) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"TALK([{string.Join(",", pages.Select(static p => $"\"{EscapeEventText(p)}\""))}])");
}
