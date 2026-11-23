using System;
using System.Collections.Generic;
using System.Text;
using JXReborn.LegacyImporter.Legacy;
using JXReborn.LegacyImporter.Sources;
using JXReborn.Packs.TieuDaoTu.Scripts.Generated;
using JXReborn.ScriptMigrator.Models;

namespace JXReborn.ScriptMigrator.Verification;

/// <summary>
/// Executes raw legacy Lua 4.0 scripts inside <see cref="Lua4Interpreter"/> with full gameplay C-API
/// host bindings and interactive <c>Say</c>/<c>Talk</c> callback chaining to produce a ground-truth
/// <see cref="ScriptEffectTrace"/>.
/// </summary>
public sealed class LuaOracleHarness
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly ILegacyServerSource _source;
    private readonly IReadOnlyDictionary<int, string> _eventItemCodeByDetailId;
    private readonly Dictionary<string, List<Lua4Token>> _sharedTokenCache = new(StringComparer.OrdinalIgnoreCase);

    public LuaOracleHarness(
        ILegacyServerSource source,
        IReadOnlyDictionary<int, string> eventItemCodeByDetailId)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _eventItemCodeByDetailId = eventItemCodeByDetailId ?? throw new ArgumentNullException(nameof(eventItemCodeByDetailId));
    }

    public ScriptEffectTrace Execute(
        string scriptVirtualOrDiskKey,
        GeneratedScriptKind kind,
        ScriptStateVector vector)
    {
        ArgumentNullException.ThrowIfNull(vector);

        var trace = ScriptEffectTrace.CreateFromVector(vector);
        var bagItems = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (vector.OwnedItemCodes is not null)
        {
            foreach (string c in vector.OwnedItemCodes)
            {
                bagItems[c] = 1;
            }
        }

        var learnedSkills = new Dictionary<int, int>();
        if (vector.LearnedSkills is not null)
        {
            foreach (KeyValuePair<int, int> kv in vector.LearnedSkills)
            {
                if (kv.Value > 0)
                {
                    learnedSkills[kv.Key] = kv.Value;
                }
            }
        }

        var rng = new Random(vector.RandomSeed);
        string? pendingTalkCallback = null;
        string[]? pendingOptionCallbacks = null;

        var interp = new Lua4Interpreter(_source, _sharedTokenCache, lenientMissingGlobals: false);
        RegisterGameplayGlobals(
            interp,
            vector,
            trace,
            bagItems,
            learnedSkills,
            rng,
            cb => pendingTalkCallback = cb,
            cbs => pendingOptionCallbacks = cbs);

        interp.ResetStepBudget(500_000);
        if (!interp.ExecuteFile(scriptVirtualOrDiskKey))
        {
            throw new InvalidOperationException($"LuaOracleHarness could not load legacy script '{scriptVirtualOrDiskKey}'.");
        }

        string entryName = kind == GeneratedScriptKind.Monster ? "OnDeath" : "main";
        interp.ResetStepBudget(500_000);
        Lua4Value[] ret = interp.CallGlobal(entryName);

        if (kind == GeneratedScriptKind.Item)
        {
            bool keep = ret.Length > 0 && !ret[0].IsNil && (int)ret[0].AsNumberOrDefault() != 0;
            trace.ItemResult = keep ? "KeepItem" : "ConsumeOne";
        }

        // Drain interactive Talk / Say callbacks identically to ZoneEngine
        IReadOnlyList<int> choices = vector.DialogChoices ?? [];
        int choiceIdx = 0;
        int guard = 0;
        while (guard++ < 32)
        {
            if (!string.IsNullOrEmpty(pendingTalkCallback))
            {
                string cb = pendingTalkCallback;
                pendingTalkCallback = null;
                if (interp.GetGlobal(cb).IsFunction)
                {
                    interp.ResetStepBudget(500_000);
                    _ = interp.CallGlobal(cb);
                }

                continue;
            }

            if (pendingOptionCallbacks is { Length: > 0 } optCbs)
            {
                pendingOptionCallbacks = null;
                if (choiceIdx >= choices.Count)
                {
                    break;
                }

                int pick = choices[choiceIdx++];
                if ((uint)pick < (uint)optCbs.Length)
                {
                    string cb = optCbs[pick];
                    if (!string.IsNullOrEmpty(cb) &&
                        !cb.Equals("no", StringComparison.OrdinalIgnoreCase) &&
                        !cb.Equals("OnCancel", StringComparison.OrdinalIgnoreCase) &&
                        interp.GetGlobal(cb).IsFunction)
                    {
                        interp.ResetStepBudget(500_000);
                        _ = interp.CallGlobal(cb);
                        continue;
                    }
                }

                break;
            }

            break;
        }

        return trace;
    }

    private void RegisterGameplayGlobals(
        Lua4Interpreter interp,
        ScriptStateVector vector,
        ScriptEffectTrace trace,
        Dictionary<string, int> bagItems,
        Dictionary<int, int> learnedSkills,
        Random rng,
        Action<string?> setTalkCallback,
        Action<string[]?> setOptionCallbacks)
    {
        static int IntArg(Lua4Value[] a, int i, int def = 0) =>
            i < a.Length ? (int)Math.Truncate(a[i].AsNumberOrDefault(def)) : def;

        string StrArg(Lua4Value[] a, int i) =>
            i < a.Length ? DecodeRuntimeLuaString(a[i].AsLuaString()) : string.Empty;

        // Deterministic random matching GeneratedLuaRuntime.UseDeterministicRandom
        interp.SetGlobal("random", Lua4Value.FromFunction(a =>
        {
            if (a.Length == 0)
            {
                return [Lua4Value.FromNumber(rng.NextDouble())];
            }

            if (a.Length == 1)
            {
                int max = IntArg(a, 0, 1);
                return [Lua4Value.FromNumber(max <= 1 ? 1 : rng.Next(1, max + 1))];
            }

            int min = IntArg(a, 0, 0);
            int max2 = IntArg(a, 1, min);
            return [Lua4Value.FromNumber(max2 <= min ? min : rng.Next(min, max2 + 1))];
        }));

        // Getters
        interp.SetGlobal("GetLevel", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(vector.Level)]));
        interp.SetGlobal("GetSex", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(vector.Gender)]));
        interp.SetGlobal("GetSeries", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(vector.Series)]));
        interp.SetGlobal("GetCash", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(trace.FinalMoney)]));
        interp.SetGlobal("GetRepute", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(trace.FinalRepute)]));
        interp.SetGlobal("GetFightState", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(trace.FinalFightMode)]));
        interp.SetGlobal("CalcFreeItemCellCount", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(vector.FreeBagCells)]));
        interp.SetGlobal("GetName", Lua4Value.FromFunction(_ => [Lua4Value.FromString(vector.RoleName)]));
        interp.SetGlobal("GetFaction", Lua4Value.FromFunction(_ => [Lua4Value.FromString(GeneratedLuaRuntime.GetFactionName(vector.CurrentFactionId))]));
        interp.SetGlobal("GetLastAddFaction", Lua4Value.FromFunction(_ => [Lua4Value.FromString(GeneratedLuaRuntime.GetFactionName(vector.EffectiveFactionId))]));

        // Tasks & Bit/Byte helpers
        interp.SetGlobal("GetTask", Lua4Value.FromFunction(a => [Lua4Value.FromNumber(trace.GetTaskValue((ushort)IntArg(a, 0)))]));
        interp.SetGlobal("SetTask", Lua4Value.FromFunction(a =>
        {
            trace.SetTaskValue((ushort)IntArg(a, 0), IntArg(a, 1));
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("GetTaskTemp", Lua4Value.FromFunction(a => [Lua4Value.FromNumber(trace.GetTempTaskValue((ushort)IntArg(a, 0)))]));
        interp.SetGlobal("SetTaskTemp", Lua4Value.FromFunction(a =>
        {
            trace.SetTempTaskValue((ushort)IntArg(a, 0), IntArg(a, 1));
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("GetGlbValue", Lua4Value.FromFunction(a => [Lua4Value.FromNumber(trace.GetGlobalVal(IntArg(a, 0)))]));
        interp.SetGlobal("SetGlbValue", Lua4Value.FromFunction(a =>
        {
            trace.SetGlobalVal(IntArg(a, 0), IntArg(a, 1));
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("GetByte", Lua4Value.FromFunction(a => [Lua4Value.FromNumber(GeneratedLuaRuntime.GetByte(IntArg(a, 0), IntArg(a, 1)))]));
        interp.SetGlobal("SetByte", Lua4Value.FromFunction(a => [Lua4Value.FromNumber(GeneratedLuaRuntime.SetByte(IntArg(a, 0), IntArg(a, 1), IntArg(a, 2)))]));
        interp.SetGlobal("GetBit", Lua4Value.FromFunction(a => [Lua4Value.FromNumber(GeneratedLuaRuntime.GetBit(IntArg(a, 0), IntArg(a, 1)))]));
        interp.SetGlobal("SetBit", Lua4Value.FromFunction(a => [Lua4Value.FromNumber(GeneratedLuaRuntime.SetBit(IntArg(a, 0), IntArg(a, 1), IntArg(a, 2)))]));

        // Economy, Exp, Repute, Skills, Items
        interp.SetGlobal("Pay", Lua4Value.FromFunction(a =>
        {
            int amt = IntArg(a, 0);
            if (amt < 0 || trace.FinalMoney < amt)
            {
                return [Lua4Value.FromNumber(0)];
            }

            trace.FinalMoney -= amt;
            return [Lua4Value.FromNumber(1)];
        }));
        interp.SetGlobal("Earn", Lua4Value.FromFunction(a =>
        {
            int amt = IntArg(a, 0);
            if (amt > 0)
            {
                trace.FinalMoney += amt;
            }

            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("AddOwnExp", Lua4Value.FromFunction(a =>
        {
            int exp = IntArg(a, 0);
            if (exp > 0)
            {
                trace.ExpGranted += exp;
            }

            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("AddRepute", Lua4Value.FromFunction(a =>
        {
            trace.FinalRepute += IntArg(a, 0);
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("HaveMagic", Lua4Value.FromFunction(a =>
            [Lua4Value.FromNumber(learnedSkills.GetValueOrDefault(IntArg(a, 0), -1))]));
        interp.SetGlobal("AddMagic", Lua4Value.FromFunction(a =>
        {
            int sid = IntArg(a, 0);
            ushort lvl = a.Length >= 2 ? (ushort)IntArg(a, 1, 1) : (ushort)1;
            learnedSkills[sid] = lvl;
            trace.Events.Add($"LEARN_SKILL({sid},{lvl})");
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("AddProp", Lua4Value.FromFunction(a =>
        {
            trace.Events.Add($"ADD_ATTR_PTS({IntArg(a, 0)})");
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("AddMagicPoint", Lua4Value.FromFunction(a =>
        {
            trace.Events.Add($"ADD_SKILL_PTS({IntArg(a, 0)})");
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("HaveItem", Lua4Value.FromFunction(a =>
        {
            int eid = IntArg(a, 0);
            bool has = _eventItemCodeByDetailId.TryGetValue(eid, out string? code) && bagItems.GetValueOrDefault(code, 0) >= 1;
            return [Lua4Value.FromNumber(has ? 1 : 0)];
        }));
        interp.SetGlobal("AddEventItem", Lua4Value.FromFunction(a =>
        {
            int eid = IntArg(a, 0);
            if (_eventItemCodeByDetailId.TryGetValue(eid, out string? code))
            {
                bagItems[code] = bagItems.GetValueOrDefault(code, 0) + 1;
                trace.Events.Add($"GRANT_ITEM({code},1)");
            }

            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("DelItem", Lua4Value.FromFunction(a =>
        {
            int eid = IntArg(a, 0);
            if (_eventItemCodeByDetailId.TryGetValue(eid, out string? code))
            {
                int cur = bagItems.GetValueOrDefault(code, 0);
                if (cur >= 1)
                {
                    if (cur == 1)
                    {
                        bagItems.Remove(code);
                    }
                    else
                    {
                        bagItems[code] = cur - 1;
                    }

                    trace.Events.Add($"CONSUME_ITEM({code},1)");
                }
                else
                {
                    trace.Events.Add($"CONSUME_ITEM_FAIL({code},1)");
                }
            }

            return [Lua4Value.Nil];
        }));

        // Movement, FightState, World Bookmarks, Buffs, UI
        interp.SetGlobal("SetFightState", Lua4Value.FromFunction(a =>
        {
            trace.FinalFightMode = (byte)(IntArg(a, 0) != 0 ? 1 : 0);
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("NewWorld", Lua4Value.FromFunction(a =>
        {
            trace.DidTeleport = true;
            trace.FinalMapId = (ushort)IntArg(a, 0);
            trace.FinalMpsX = IntArg(a, 1) * 32;
            trace.FinalMpsY = IntArg(a, 2) * 32;
            return [Lua4Value.FromNumber(1)];
        }));
        interp.SetGlobal("SetPos", Lua4Value.FromFunction(a =>
        {
            trace.DidTeleport = true;
            trace.FinalMpsX = IntArg(a, 0) * 32;
            trace.FinalMpsY = IntArg(a, 1) * 32;
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("ReturnFromPortal", Lua4Value.FromFunction(_ =>
        {
            trace.Events.Add("RETURN_PORTAL");
            return [Lua4Value.FromNumber(1)];
        }));
        interp.SetGlobal("SetProtectTime", Lua4Value.FromFunction(a =>
        {
            int dur = IntArg(a, 0);
            if (dur > 0)
            {
                trace.ProtectFrames = dur;
            }

            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("AddSkillState", Lua4Value.FromFunction(a =>
        {
            int sid = IntArg(a, 0);
            if (sid > 0)
            {
                trace.SkillStates.Add((sid, IntArg(a, 1), IntArg(a, 2), IntArg(a, 3)));
            }

            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("AddStation", Lua4Value.FromFunction(a =>
        {
            ushort st = (ushort)IntArg(a, 0);
            if (st > 0)
            {
                trace.UnlockedStations.Add(st);
            }

            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("AddTermini", Lua4Value.FromFunction(a =>
        {
            int wp = IntArg(a, 0);
            if (wp > 0)
            {
                trace.RecordedWayPoints.Add(wp);
            }

            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("SetRevPos", Lua4Value.FromFunction(a =>
        {
            ushort mapId = a.Length >= 2 ? (ushort)IntArg(a, 0) : trace.FinalMapId;
            int revId = a.Length >= 2 ? IntArg(a, 1) : IntArg(a, 0);
            if (revId > 0)
            {
                trace.RevivalPoint = (mapId, revId);
            }

            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("OpenBox", Lua4Value.FromFunction(_ =>
        {
            trace.OpenedStoreBox = true;
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("Sale", Lua4Value.FromFunction(a =>
        {
            int shopId = IntArg(a, 0);
            byte curr = a.Length >= 2 ? (byte)IntArg(a, 1) : (byte)0;
            int scale = a.Length >= 3 ? IntArg(a, 2, 100) : 100;
            trace.Events.Add($"OPEN_SHOP({shopId},{curr},{scale})");
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("ShowLadder", Lua4Value.FromFunction(a =>
        {
            var ids = new List<int>(Math.Max(0, a.Length - 1));
            for (int i = 1; i < a.Length; i++)
            {
                ids.Add(IntArg(a, i));
            }

            trace.Events.Add($"LADDER([{string.Join(",", ids)}])");
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("Msg2Player", Lua4Value.FromFunction(a =>
        {
            trace.Events.Add($"MSG(\"{ScriptEffectTrace.EscapeEventText(StrArg(a, 0))}\")");
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("AddNote", Lua4Value.FromFunction(a =>
        {
            trace.Events.Add($"NOTE(\"{ScriptEffectTrace.EscapeEventText(StrArg(a, 0))}\")");
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("Talk", Lua4Value.FromFunction(a =>
        {
            string cb = a.Length >= 2 ? a[1].AsLuaString().Trim() : string.Empty;
            var pages = new List<string>(Math.Max(0, a.Length - 2));
            for (int i = 2; i < a.Length; i++)
            {
                pages.Add(StrArg(a, i));
            }

            trace.Events.Add(ScriptEffectTrace.FormatTalkEvent(pages));
            setTalkCallback(string.IsNullOrEmpty(cb) ? null : cb);
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("Say", Lua4Value.FromFunction(a =>
        {
            string prompt = StrArg(a, 0);
            int declaredCount = a.Length >= 2 ? IntArg(a, 1) : 0;
            if (a.Length <= 2 || declaredCount <= 0)
            {
                trace.Events.Add(ScriptEffectTrace.FormatSayEvent(prompt, []));
                setOptionCallbacks(null);
                return [Lua4Value.Nil];
            }

            var rawSpecs = new List<string>();
            if (a.Length == 3 && a[2].IsTable && a[2].Table is not null)
            {
                for (int idx = 1; ; idx++)
                {
                    Lua4Value elem = a[2].GetTableEntry(Lua4Value.FromNumber(idx));
                    if (elem.IsNil)
                    {
                        break;
                    }

                    rawSpecs.Add(DecodeRuntimeLuaString(elem.AsLuaString()));
                }
            }
            else
            {
                for (int i = 2; i < a.Length; i++)
                {
                    rawSpecs.Add(StrArg(a, i));
                }
            }

            var labels = new List<string>(rawSpecs.Count);
            var callbacks = new List<string>(rawSpecs.Count);
            foreach (string spec in rawSpecs)
            {
                int slash = spec.LastIndexOf('/');
                labels.Add(slash >= 0 ? spec[..slash] : spec);
                callbacks.Add(slash >= 0 ? spec[(slash + 1)..].Trim() : string.Empty);
            }

            trace.Events.Add(ScriptEffectTrace.FormatSayEvent(prompt, labels));
            setOptionCallbacks(callbacks.Count > 0 ? callbacks.ToArray() : null);
            return [Lua4Value.Nil];
        }));
    }

    private static string DecodeRuntimeLuaString(string latin1)
    {
        if (string.IsNullOrEmpty(latin1))
        {
            return string.Empty;
        }

        byte[] raw = Encoding.Latin1.GetBytes(latin1);
        if (IsStrictValidUtf8WithMultibyte(raw))
        {
            return Encoding.UTF8.GetString(raw);
        }

        return HybridLegacyTextDecoder.DecodeSmart(raw, preferVietnamese: true);
    }

    private static bool IsStrictValidUtf8WithMultibyte(ReadOnlySpan<byte> bytes)
    {
        bool hasNonAscii = false;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] >= 0x80)
            {
                hasNonAscii = true;
                break;
            }
        }

        if (!hasNonAscii)
        {
            return false;
        }

        try
        {
            _ = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
