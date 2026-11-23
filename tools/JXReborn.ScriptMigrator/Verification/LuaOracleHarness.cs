using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using JXReborn.LegacyImporter.Legacy;
using JXReborn.LegacyImporter.Sources;
using JXReborn.Packs.TieuDaoTu.Scripts.Generated;
using JXReborn.ScriptMigrator.Generators;
using JXReborn.ScriptMigrator.Models;

namespace JXReborn.ScriptMigrator.Verification;

/// <summary>
/// Executes raw legacy Lua 4.0 scripts inside <see cref="Lua4Interpreter"/> with full gameplay C-API
/// host bindings and interactive <c>Say</c>/<c>Describe</c>/<c>Talk</c>/<c>AskClientForNumber</c>
/// callback chaining (including <c>#fn(arg1, arg2)</c> parameterized callbacks) to produce a
/// ground-truth <see cref="ScriptEffectTrace"/>.
/// </summary>
public sealed class LuaOracleHarness
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly ILegacyServerSource _source;
    private readonly IReadOnlyDictionary<int, string> _eventItemCodeByDetailId;
    private readonly PackItemCatalogIndex? _itemCatalogIndex;
    private readonly Dictionary<string, List<Lua4Token>> _sharedTokenCache = new(StringComparer.OrdinalIgnoreCase);

    public LuaOracleHarness(
        ILegacyServerSource source,
        IReadOnlyDictionary<int, string> eventItemCodeByDetailId,
        PackItemCatalogIndex? itemCatalogIndex = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _eventItemCodeByDetailId = eventItemCodeByDetailId ?? throw new ArgumentNullException(nameof(eventItemCodeByDetailId));
        _itemCatalogIndex = itemCatalogIndex;
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

        if (vector.OwnedItemCounts is not null)
        {
            foreach (KeyValuePair<string, int> kv in vector.OwnedItemCounts)
            {
                if (kv.Value > 0)
                {
                    bagItems[kv.Key] = kv.Value;
                }
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
        DateTime simTime = vector.SimulatedLocalTime ?? new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
        string? pendingTalkCallback = null;
        string[]? pendingOptionCallbacks = null;
        (string Callback, int Min, int Max)? pendingNumberPrompt = null;

        var interp = new Lua4Interpreter(_source, _sharedTokenCache, lenientMissingGlobals: false);
        RegisterGameplayGlobals(
            interp,
            vector,
            trace,
            bagItems,
            learnedSkills,
            rng,
            simTime,
            cb => pendingTalkCallback = cb,
            cbs => pendingOptionCallbacks = cbs,
            np => pendingNumberPrompt = np);

        interp.ResetStepBudget(500_000);
        if (!interp.ExecuteFile(scriptVirtualOrDiskKey))
        {
            throw new InvalidOperationException($"LuaOracleHarness could not load legacy script '{scriptVirtualOrDiskKey}'.");
        }

        string entryName = kind == GeneratedScriptKind.Monster ? "OnDeath" : "main";
        interp.ResetStepBudget(500_000);
        Lua4Value[] ret = kind == GeneratedScriptKind.Item
            ? interp.CallGlobal(entryName, Lua4Value.FromNumber(40001))
            : interp.CallGlobal(entryName);

        if (kind == GeneratedScriptKind.Item)
        {
            bool keep = ret.Length > 0 && !ret[0].IsNil && (int)ret[0].AsNumberOrDefault() != 0;
            trace.ItemResult = keep ? "KeepItem" : "ConsumeOne";
        }

        IReadOnlyList<int> choices = vector.DialogChoices ?? [];
        IReadOnlyList<int> numberInputs = vector.NumberInputs ?? [];
        int choiceIdx = 0;
        int numIdx = 0;
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

            if (pendingNumberPrompt is { } numPrompt)
            {
                pendingNumberPrompt = null;
                if (numIdx >= numberInputs.Count)
                {
                    break;
                }

                int clamped = Math.Clamp(numberInputs[numIdx++], numPrompt.Min, Math.Max(numPrompt.Min, numPrompt.Max));
                if (interp.GetGlobal(numPrompt.Callback).IsFunction)
                {
                    interp.ResetStepBudget(500_000);
                    _ = interp.CallGlobal(numPrompt.Callback, Lua4Value.FromNumber(clamped));
                    continue;
                }

                break;
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
                    string rawCb = optCbs[pick];
                    if (!string.IsNullOrEmpty(rawCb) &&
                        !rawCb.Equals("no", StringComparison.OrdinalIgnoreCase) &&
                        !rawCb.Equals("OnCancel", StringComparison.OrdinalIgnoreCase) &&
                        !rawCb.Equals("Cancel", StringComparison.OrdinalIgnoreCase))
                    {
                        (string fnName, Lua4Value[] cbArgs) = ParseLuaOptionCallbackInvocation(rawCb);
                        if (!string.IsNullOrEmpty(fnName) && interp.GetGlobal(fnName).IsFunction)
                        {
                            interp.ResetStepBudget(500_000);
                            _ = interp.CallGlobal(fnName, cbArgs);
                            continue;
                        }
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
        DateTime simTime,
        Action<string?> setTalkCallback,
        Action<string[]?> setOptionCallbacks,
        Action<(string Callback, int Min, int Max)?> setNumberPrompt)
    {
        static int IntArg(Lua4Value[] a, int i, int def = 0) =>
            i < a.Length ? (int)Math.Truncate(a[i].AsNumberOrDefault(def)) : def;

        string StrArg(Lua4Value[] a, int i) =>
            i < a.Length ? DecodeRuntimeLuaString(a[i].AsLuaString()) : string.Empty;

        interp.SetGlobal("SubWorld", Lua4Value.FromNumber(trace.FinalMapId));
        interp.SetGlobal("PlayerIndex", Lua4Value.FromNumber(1));

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

        // Deterministic date / time matching GeneratedLuaRuntime
        Func<Lua4Value[], Lua4Value[]> dateFn = a =>
        {
            string? fmt = a.Length > 0 && !a[0].IsNil ? a[0].AsLuaString() : null;
            return [Lua4Value.FromString(FormatLegacyDate(simTime, fmt))];
        };
        interp.SetGlobal("GetLocalDate", Lua4Value.FromFunction(dateFn));
        interp.SetGlobal("date", Lua4Value.FromFunction(dateFn));
        interp.SetGlobal("GetCurServerTime", Lua4Value.FromFunction(_ =>
        {
            DateTime utc = simTime.Kind == DateTimeKind.Utc ? simTime : DateTime.SpecifyKind(simTime, DateTimeKind.Utc);
            int secs = unchecked((int)new DateTimeOffset(utc).ToUnixTimeSeconds());
            return [Lua4Value.FromNumber(secs)];
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
        interp.SetGlobal("GetLastFactionNumber", Lua4Value.FromFunction(_ =>
            [Lua4Value.FromNumber(vector.EffectiveFactionId == 255 ? -1 : vector.EffectiveFactionId)]));
        interp.SetGlobal("GetLastDiagNpc", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(20001)]));
        interp.SetGlobal("GetNpcName", Lua4Value.FromFunction(_ => [Lua4Value.FromString("NPC")]));
        interp.SetGlobal("NpcName2Replace", Lua4Value.FromFunction(a => [a.Length > 0 ? a[0] : Lua4Value.FromString(string.Empty)]));

        // Camp, PK, Rank, WorldPos, Revival
        interp.SetGlobal("GetCamp", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(trace.FinalBaseCamp)]));
        interp.SetGlobal("SetCamp", Lua4Value.FromFunction(a =>
        {
            trace.FinalBaseCamp = (byte)IntArg(a, 0);
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("GetCurCamp", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(trace.FinalCurrentCamp)]));
        interp.SetGlobal("SetCurCamp", Lua4Value.FromFunction(a =>
        {
            trace.FinalCurrentCamp = (byte)IntArg(a, 0);
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("RestoreCamp", Lua4Value.FromFunction(_ =>
        {
            trace.FinalCurrentCamp = trace.FinalBaseCamp;
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("GetPK", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(trace.FinalPkValue)]));
        interp.SetGlobal("SetPK", Lua4Value.FromFunction(a =>
        {
            trace.FinalPkValue = (sbyte)Math.Clamp(IntArg(a, 0), 0, 10);
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("GetRank", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(trace.FinalRankId)]));
        interp.SetGlobal("SetRank", Lua4Value.FromFunction(a =>
        {
            trace.FinalRankId = (byte)IntArg(a, 0);
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("GetWorldPos", Lua4Value.FromFunction(_ =>
        [
            Lua4Value.FromNumber(trace.FinalMapId),
            Lua4Value.FromNumber(trace.FinalMpsX / 32),
            Lua4Value.FromNumber(trace.FinalMpsY / 32),
        ]));
        interp.SetGlobal("GetPlayerRev", Lua4Value.FromFunction(_ =>
        [
            Lua4Value.FromNumber(trace.CurrentRevivalMapId),
            Lua4Value.FromNumber(trace.CurrentRevivalPointId),
        ]));

        // Tasks & Bit/Byte helpers
        interp.SetGlobal("GetTask", Lua4Value.FromFunction(a => [Lua4Value.FromNumber(trace.GetTaskValue((ushort)IntArg(a, 0)))]));
        interp.SetGlobal("SetTask", Lua4Value.FromFunction(a =>
        {
            trace.SetTaskValue((ushort)IntArg(a, 0), IntArg(a, 1));
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("SyncTaskValue", Lua4Value.FromFunction(_ => [Lua4Value.Nil]));
        interp.SetGlobal("WriteLog", Lua4Value.FromFunction(_ => [Lua4Value.Nil]));
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
        interp.SetGlobal("DelMagic", Lua4Value.FromFunction(a =>
        {
            int sid = IntArg(a, 0);
            learnedSkills.Remove(sid);
            trace.Events.Add($"DEL_SKILL({sid})");
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

        bool TryResolveEventCode(int eid, out string code)
        {
            if (_itemCatalogIndex is not null && _itemCatalogIndex.TryResolveEventItem(eid, out code))
            {
                return true;
            }

            if (_eventItemCodeByDetailId.TryGetValue(eid, out string? fallback))
            {
                code = fallback;
                return true;
            }

            code = string.Empty;
            return false;
        }

        void GrantBagCode(string code, int count, bool isEquip)
        {
            bagItems[code] = bagItems.GetValueOrDefault(code, 0) + count;
            trace.Events.Add(isEquip ? $"GRANT_EQUIP({code})" : $"GRANT_ITEM({code},{count})");
        }

        bool ConsumeBagCode(string code, int count)
        {
            int cur = bagItems.GetValueOrDefault(code, 0);
            if (cur < count)
            {
                trace.Events.Add($"CONSUME_ITEM_FAIL({code},{count})");
                return false;
            }

            if (cur == count)
            {
                bagItems.Remove(code);
            }
            else
            {
                bagItems[code] = cur - count;
            }

            trace.Events.Add($"CONSUME_ITEM({code},{count})");
            return true;
        }

        interp.SetGlobal("HaveItem", Lua4Value.FromFunction(a =>
        {
            int eid = IntArg(a, 0);
            bool has = TryResolveEventCode(eid, out string code) && bagItems.GetValueOrDefault(code, 0) >= 1;
            return [Lua4Value.FromNumber(has ? 1 : 0)];
        }));
        interp.SetGlobal("AddEventItem", Lua4Value.FromFunction(a =>
        {
            int eid = IntArg(a, 0);
            if (TryResolveEventCode(eid, out string code))
            {
                GrantBagCode(code, 1, isEquip: false);
            }

            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("DelItem", Lua4Value.FromFunction(a =>
        {
            int eid = IntArg(a, 0);
            if (TryResolveEventCode(eid, out string code))
            {
                _ = ConsumeBagCode(code, 1);
            }

            return [Lua4Value.Nil];
        }));

        Func<Lua4Value[], Lua4Value[]> addItemFn = a =>
        {
            if (_itemCatalogIndex is not null &&
                _itemCatalogIndex.TryResolveByGdpl(IntArg(a, 0), IntArg(a, 1), IntArg(a, 2), IntArg(a, 3, 1), out PackItemEntry entry))
            {
                GrantBagCode(entry.Code, 1, isEquip: IntArg(a, 0) == 0);
                return [Lua4Value.FromNumber(1)];
            }

            return [Lua4Value.FromNumber(0)];
        };
        interp.SetGlobal("AddItem", Lua4Value.FromFunction(addItemFn));
        interp.SetGlobal("AddItemNoStack", Lua4Value.FromFunction(addItemFn));
        interp.SetGlobal("AddStackItem", Lua4Value.FromFunction(a =>
        {
            int count = Math.Max(1, IntArg(a, 0, 1));
            if (_itemCatalogIndex is not null &&
                _itemCatalogIndex.TryResolveByGdpl(IntArg(a, 1), IntArg(a, 2), IntArg(a, 3), IntArg(a, 4, 1), out PackItemEntry entry))
            {
                GrantBagCode(entry.Code, count, isEquip: false);
                return [Lua4Value.FromNumber(1)];
            }

            return [Lua4Value.FromNumber(0)];
        }));
        interp.SetGlobal("DelCommonItem", Lua4Value.FromFunction(a =>
        {
            if (_itemCatalogIndex is not null &&
                _itemCatalogIndex.TryResolveByGdp(IntArg(a, 0), IntArg(a, 1), IntArg(a, 2), out PackItemEntry entry))
            {
                bool ok = ConsumeBagCode(entry.Code, 1);
                return [Lua4Value.FromNumber(ok ? 1 : 0)];
            }

            return [Lua4Value.FromNumber(0)];
        }));
        interp.SetGlobal("CalcEquiproomItemCount", Lua4Value.FromFunction(a =>
        {
            if (_itemCatalogIndex is not null &&
                _itemCatalogIndex.TryResolveByGdpl(IntArg(a, 0), IntArg(a, 1), IntArg(a, 2), IntArg(a, 3, 1), out PackItemEntry entry))
            {
                return [Lua4Value.FromNumber(bagItems.GetValueOrDefault(entry.Code, 0))];
            }

            return [Lua4Value.FromNumber(0)];
        }));
        interp.SetGlobal("ConsumeEquiproomItem", Lua4Value.FromFunction(a =>
        {
            int count = IntArg(a, 0, 1);
            if (_itemCatalogIndex is not null &&
                _itemCatalogIndex.TryResolveByGdpl(IntArg(a, 1), IntArg(a, 2), IntArg(a, 3), IntArg(a, 4, 1), out PackItemEntry entry))
            {
                bool ok = ConsumeBagCode(entry.Code, count);
                return [Lua4Value.FromNumber(ok ? 1 : 0)];
            }

            return [Lua4Value.FromNumber(0)];
        }));

        // Object prop state, Movement, FightState, World Bookmarks, Buffs, UI
        interp.SetGlobal("SetPropState", Lua4Value.FromFunction(a =>
        {
            byte st = a.Length == 0 ? (byte)1 : (byte)(IntArg(a, 0, 1) != 0 ? 1 : 0);
            trace.ObjectPropState = st;
            return [Lua4Value.Nil];
        }));
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
            interp.SetGlobal("SubWorld", Lua4Value.FromNumber(trace.FinalMapId));
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
                trace.CurrentRevivalMapId = mapId;
                trace.CurrentRevivalPointId = revId;
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
        Func<Lua4Value[], Lua4Value[]> newsFn = a =>
        {
            trace.Events.Add($"NEWS(\"{ScriptEffectTrace.EscapeEventText(StrArg(a, 0))}\")");
            return [Lua4Value.Nil];
        };
        interp.SetGlobal("Msg2SubWorld", Lua4Value.FromFunction(newsFn));
        interp.SetGlobal("AddGlobalNews", Lua4Value.FromFunction(newsFn));
        interp.SetGlobal("AddGlobalCountNews", Lua4Value.FromFunction(newsFn));
        interp.SetGlobal("Msg2Team", Lua4Value.FromFunction(a =>
        {
            trace.Events.Add($"TEAM_MSG(\"{ScriptEffectTrace.EscapeEventText(StrArg(a, 0))}\")");
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("AskClientForNumber", Lua4Value.FromFunction(a =>
        {
            string cb = a.Length >= 1 ? a[0].AsLuaString().Trim() : string.Empty;
            int min = IntArg(a, 1, 0);
            int max = IntArg(a, 2, 0);
            string title = StrArg(a, 3);
            trace.Events.Add($"ASK_NUM(\"{ScriptEffectTrace.EscapeEventText(title)}\",{min},{max})");
            setNumberPrompt(string.IsNullOrEmpty(cb) ? null : (cb, min, max));
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

        Func<Lua4Value[], Lua4Value[]> sayOrDescribeFn = a =>
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
                for (int idx = 1; idx <= declaredCount; idx++)
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
                for (int i = 2; i < a.Length && rawSpecs.Count < declaredCount; i++)
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
        };
        interp.SetGlobal("Say", Lua4Value.FromFunction(sayOrDescribeFn));
        interp.SetGlobal("Describe", Lua4Value.FromFunction(sayOrDescribeFn));
    }

    private static (string FnName, Lua4Value[] Args) ParseLuaOptionCallbackInvocation(string rawCb)
    {
        string trimmed = rawCb.Trim();
        if (!trimmed.StartsWith('#'))
        {
            return (trimmed, []);
        }

        string rest = trimmed[1..].TrimStart();
        int lParen = rest.IndexOf('(');
        int rParen = rest.LastIndexOf(')');
        if (lParen <= 0 || rParen < lParen)
        {
            return (rest, []);
        }

        string fnName = rest[..lParen].Trim();
        string inner = rest[(lParen + 1)..rParen].Trim();
        if (inner.Length == 0)
        {
            return (fnName, []);
        }

        var args = new List<Lua4Value>();
        var cur = new StringBuilder();
        char quote = '\0';
        bool wasQuoted = false;

        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    cur.Append(c);
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                wasQuoted = true;
                continue;
            }

            if (c == ',')
            {
                args.Add(ToCallbackArgValue(cur.ToString().Trim(), wasQuoted));
                cur.Clear();
                wasQuoted = false;
                continue;
            }

            cur.Append(c);
        }

        args.Add(ToCallbackArgValue(cur.ToString().Trim(), wasQuoted));
        return (fnName, args.ToArray());
    }

    private static Lua4Value ToCallbackArgValue(string token, bool wasQuoted)
    {
        if (wasQuoted)
        {
            return Lua4Value.FromString(token);
        }

        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
        {
            return Lua4Value.FromNumber(d);
        }

        return Lua4Value.FromString(token);
    }

    private static string FormatLegacyDate(DateTime now, string? format)
    {
        string fmt = string.IsNullOrEmpty(format) ? "%y-%m-%d %H:%M:%S" : format;
        var sb = new StringBuilder(fmt.Length + 8);
        for (int i = 0; i < fmt.Length; i++)
        {
            if (fmt[i] != '%' || i + 1 >= fmt.Length)
            {
                sb.Append(fmt[i]);
                continue;
            }

            i++;
            switch (fmt[i])
            {
                case '%': sb.Append('%'); break;
                case 'Y': sb.Append(now.Year.ToString("D4", CultureInfo.InvariantCulture)); break;
                case 'y': sb.Append((now.Year % 100).ToString("D2", CultureInfo.InvariantCulture)); break;
                case 'm': sb.Append(now.Month.ToString("D2", CultureInfo.InvariantCulture)); break;
                case 'd': sb.Append(now.Day.ToString("D2", CultureInfo.InvariantCulture)); break;
                case 'H': sb.Append(now.Hour.ToString("D2", CultureInfo.InvariantCulture)); break;
                case 'M': sb.Append(now.Minute.ToString("D2", CultureInfo.InvariantCulture)); break;
                case 'S': sb.Append(now.Second.ToString("D2", CultureInfo.InvariantCulture)); break;
                case 'w': sb.Append(((int)now.DayOfWeek).ToString(CultureInfo.InvariantCulture)); break;
                case 'j': sb.Append(now.DayOfYear.ToString("D3", CultureInfo.InvariantCulture)); break;
                default:
                    sb.Append('%').Append(fmt[i]);
                    break;
            }
        }

        return sb.ToString();
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
