using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using JXReborn.LegacyImporter.Legacy;
using JXReborn.LegacyImporter.Sources;
using JXReborn.Packs.TieuDaoTu.Scripts.Generated;
using JXReborn.ScriptMigrator.Generators;
using JXReborn.ScriptMigrator.Models;
using JXReborn.ZoneEngine.WorldZones;

namespace JXReborn.ScriptMigrator.Verification;

/// <summary>
/// Executes raw legacy Lua 4.0 scripts inside <see cref="Lua4Interpreter"/> with full gameplay C-API
/// host bindings, <c>tbAwardTemplet</c> / <c>DailogClass</c> / <c>CreateNewSayEx</c> framework support,
/// party <c>PlayerIndex</c> context switching, and interactive callback chaining to produce a
/// ground-truth <see cref="ScriptEffectTrace"/>.
/// </summary>
public sealed class LuaOracleHarness
{
    private static readonly string[] AwardTypeStartupScripts =
    [
        @"\script\lib\awardtype\simple.lua",
        @"\script\lib\awardtype\exp.lua",
        @"\script\lib\awardtype\jxb.lua",
        @"\script\lib\awardtype\repute.lua",
        @"\script\lib\awardtype\item.lua",
        @"\script\lib\awardtype\fun.lua",
    ];

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

        // Match KLuaScriptSet::LoadScriptByRelPath (`KLua_2_2_LoggingAndScript.cpp`): `Include` loads each header once per VM
        var includedOnce = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_source.TryResolveServerFile(scriptVirtualOrDiskKey, out string? entryResolvedKey) && !string.IsNullOrEmpty(entryResolvedKey))
        {
            includedOnce.Add(entryResolvedKey);
        }

        interp.SetGlobal("Include", Lua4Value.FromFunction(a =>
        {
            if (a.Length > 0 && a[0].IsString && !string.IsNullOrWhiteSpace(a[0].Text))
            {
                byte[] rawPath = Encoding.Latin1.GetBytes(a[0].Text!);
                if (_source.TryResolveServerFile(rawPath, out string? resolvedKey) &&
                    !string.IsNullOrEmpty(resolvedKey) &&
                    includedOnce.Add(resolvedKey))
                {
                    _ = interp.ExecuteFile(resolvedKey);
                }
            }

            return [Lua4Value.Nil];
        }));

        interp.ResetStepBudget(500_000);
        if (!interp.ExecuteFile(scriptVirtualOrDiskKey))
        {
            throw new InvalidOperationException($"LuaOracleHarness could not load legacy script '{scriptVirtualOrDiskKey}'.");
        }

        // In legacy server1, autoexec.lua registers awardtype/*.lua into tbAwardTemplet at startup.
        if (interp.GetGlobal("tbAwardTemplet").IsTable)
        {
            foreach (string awardTypeScript in AwardTypeStartupScripts)
            {
                interp.ResetStepBudget(500_000);
                _ = interp.ExecuteFile(awardTypeScript);
            }
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

        static Lua4Value ToUtf8BackedLuaString(string utf8) =>
            Lua4Value.FromString(Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(utf8)));

        void SyncActivePlayerFromLua()
        {
            int pi = (int)Math.Truncate(interp.GetGlobal("PlayerIndex").AsNumberOrDefault(trace.InitiatorPartySlot));
            trace.SwitchActivePlayer(pi);
        }

        interp.SetGlobal("IL", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(1)]));
        interp.SetGlobal("SubWorld", Lua4Value.FromNumber(trace.FinalMapId));
        interp.SetGlobal("PlayerIndex", Lua4Value.FromNumber(trace.InitiatorPartySlot));

        // Lua 4.0 `call(fn, argsTable[, mode])` & `CallPlayerFunction(playerIdx, fn, ...)`
        interp.SetGlobal("call", Lua4Value.FromFunction(a =>
        {
            if (a.Length >= 1 && a[0].IsFunction && a[0].Function is not null)
            {
                int n = a.Length >= 2 && a[1].IsTable ? a[1].GetTableN() : 0;
                var unpacked = new Lua4Value[n];
                for (int k = 1; k <= n; k++)
                {
                    unpacked[k - 1] = a[1].GetTableEntry(Lua4Value.FromNumber(k));
                }

                return a[0].Function!(unpacked);
            }

            return [Lua4Value.Nil];
        }));

        interp.SetGlobal("CallPlayerFunction", Lua4Value.FromFunction(a =>
        {
            if (a.Length >= 2 && a[1].IsFunction && a[1].Function is not null)
            {
                Lua4Value prevPi = interp.GetGlobal("PlayerIndex");
                interp.SetGlobal("PlayerIndex", a[0]);
                SyncActivePlayerFromLua();
                try
                {
                    var callArgs = new Lua4Value[a.Length - 2];
                    for (int i = 2; i < a.Length; i++)
                    {
                        callArgs[i - 2] = a[i];
                    }

                    return a[1].Function!(callArgs);
                }
                finally
                {
                    interp.SetGlobal("PlayerIndex", prevPi);
                    SyncActivePlayerFromLua();
                }
            }

            return [Lua4Value.Nil];
        }));

        interp.SetGlobal("curpack", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(0)]));
        interp.SetGlobal("usepack", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(0)]));

        // String buffer helpers used by `task_string.lua` (`SetTaskSayColor`) + `strfind` / `gsub`
        string stringScratchBuffer = string.Empty;
        interp.SetGlobal("PushString", Lua4Value.FromFunction(a =>
        {
            stringScratchBuffer = a.Length > 0 ? a[0].AsLuaString() : string.Empty;
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("AppendString", Lua4Value.FromFunction(a =>
        {
            if (a.Length > 0)
            {
                stringScratchBuffer += a[0].AsLuaString();
            }

            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("ReplaceString", Lua4Value.FromFunction(a =>
        {
            if (a.Length >= 2)
            {
                string from = a[0].AsLuaString();
                string to = a[1].AsLuaString();
                if (from.Length > 0)
                {
                    stringScratchBuffer = stringScratchBuffer.Replace(from, to, StringComparison.Ordinal);
                }
            }

            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("PopString", Lua4Value.FromFunction(_ => [Lua4Value.FromString(stringScratchBuffer)]));

        interp.SetGlobal("strfind", Lua4Value.FromFunction(a =>
        {
            if (a.Length < 2)
            {
                return [Lua4Value.Nil];
            }

            string s = a[0].AsLuaString();
            string pat = a[1].AsLuaString();
            int init0 = Math.Max(0, IntArg(a, 2, 1) - 1);
            if (init0 >= s.Length)
            {
                return [Lua4Value.Nil];
            }

            if (pat == "<lua (.-)/>")
            {
                int luaStart = s.IndexOf("<lua ", init0, StringComparison.Ordinal);
                if (luaStart < 0)
                {
                    return [Lua4Value.Nil];
                }

                int luaEnd = s.IndexOf("/>", luaStart + 5, StringComparison.Ordinal);
                if (luaEnd < 0)
                {
                    return [Lua4Value.Nil];
                }

                return
                [
                    Lua4Value.FromNumber(luaStart + 1),
                    Lua4Value.FromNumber(luaEnd + 2),
                    Lua4Value.FromString(s[(luaStart + 5)..luaEnd]),
                ];
            }

            int found = s.IndexOf(pat, init0, StringComparison.Ordinal);
            return found >= 0
                ? [Lua4Value.FromNumber(found + 1), Lua4Value.FromNumber(found + pat.Length)]
                : [Lua4Value.Nil];
        }));

        interp.SetGlobal("gsub", Lua4Value.FromFunction(a =>
        {
            if (a.Length < 3)
            {
                return a.Length > 0 ? [a[0]] : [Lua4Value.FromString(string.Empty)];
            }

            string s = a[0].AsLuaString();
            string from = a[1].AsLuaString();
            string to = a[2].AsLuaString();
            return [Lua4Value.FromString(from.Length > 0 ? s.Replace(from, to, StringComparison.Ordinal) : s)];
        }));

        interp.SetGlobal("format", Lua4Value.FromFunction(a =>
        {
            if (a.Length == 0)
            {
                return [Lua4Value.FromString(string.Empty)];
            }

            string fmtUtf8 = DecodeRuntimeLuaString(a[0].AsLuaString());
            if (a.Length == 1)
            {
                return [ToUtf8BackedLuaString(fmtUtf8)];
            }

            var fmtArgs = new object?[a.Length - 1];
            for (int i = 1; i < a.Length; i++)
            {
                Lua4Value v = a[i];
                fmtArgs[i - 1] = v.IsNil
                    ? null
                    : v.IsNumber
                        ? v.AsNumberOrDefault(0)
                        : DecodeRuntimeLuaString(v.AsLuaString());
            }

            return [ToUtf8BackedLuaString(GeneratedLuaRuntime.LuaFormat(fmtUtf8, fmtArgs))];
        }));

        // Partner & NPC link helpers (`task_string.lua`) + benign log/tabfile stubs
        interp.SetGlobal("PARTNER_GetCurPartner", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(0), Lua4Value.FromNumber(0)]));
        interp.SetGlobal("PARTNER_GetSettingIdx", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(0)]));
        interp.SetGlobal("PARTNER_GetName", Lua4Value.FromFunction(_ => [Lua4Value.FromString(string.Empty)]));
        interp.SetGlobal("GetNpcSettingIdx", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(0)]));
        interp.SetGlobal("GetTongName", Lua4Value.FromFunction(_ => [Lua4Value.FromString(string.Empty), Lua4Value.FromNumber(0)]));
        interp.SetGlobal("GetAccount", Lua4Value.FromFunction(_ => [Lua4Value.FromString(string.Empty)]));
        interp.SetGlobal("TabFile_Load", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(0)]));
        interp.SetGlobal("TabFile_GetRowCount", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(0)]));
        interp.SetGlobal("TabFile_GetCell", Lua4Value.FromFunction(_ => [Lua4Value.FromString(string.Empty)]));

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

        // Getters & Party APIs
        interp.SetGlobal("GetLevel", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(vector.Level)]));
        interp.SetGlobal("GetSex", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(vector.Gender)]));
        interp.SetGlobal("GetSeries", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(vector.Series)]));
        interp.SetGlobal("GetCash", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(trace.FinalMoney)]));
        interp.SetGlobal("GetRepute", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(trace.FinalRepute)]));
        interp.SetGlobal("GetFightState", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(trace.FinalFightMode)]));
        interp.SetGlobal("CalcFreeItemCellCount", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(vector.FreeBagCells)]));
        interp.SetGlobal("CountFreeRoomByWH", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(vector.FreeBagCells > 0 ? 1 : 0)]));
        interp.SetGlobal("GetName", Lua4Value.FromFunction(_ => [ToUtf8BackedLuaString(vector.RoleName)]));
        interp.SetGlobal("GetFaction", Lua4Value.FromFunction(_ =>
            [Lua4Value.FromString(GeneratedLuaRuntime.GetFactionName(trace.FinalCurrentFactionId))]));
        interp.SetGlobal("GetLastAddFaction", Lua4Value.FromFunction(_ =>
        {
            byte eff = trace.FinalCurrentFactionId != 255 ? trace.FinalCurrentFactionId : vector.EffectiveFactionId;
            return [Lua4Value.FromString(GeneratedLuaRuntime.GetFactionName(eff))];
        }));
        interp.SetGlobal("GetLastFactionNumber", Lua4Value.FromFunction(_ =>
        {
            byte eff = trace.FinalCurrentFactionId != 255 ? trace.FinalCurrentFactionId : vector.EffectiveFactionId;
            return [Lua4Value.FromNumber(eff == 255 ? -1 : eff)];
        }));
        interp.SetGlobal("SetFaction", Lua4Value.FromFunction(a =>
        {
            string fac = StrArg(a, 0);
            if (string.IsNullOrEmpty(fac))
            {
                trace.FinalCurrentFactionId = 255;
                return [Lua4Value.FromNumber(1)];
            }

            if (TryMapFactionNameToId(fac, out byte fid))
            {
                trace.FinalCurrentFactionId = fid;
                return [Lua4Value.FromNumber(1)];
            }

            return [Lua4Value.FromNumber(0)];
        }));
        interp.SetGlobal("GetLastDiagNpc", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(20001)]));
        interp.SetGlobal("GetNpcName", Lua4Value.FromFunction(_ => [Lua4Value.FromString("NPC")]));
        interp.SetGlobal("NpcName2Replace", Lua4Value.FromFunction(a => [a.Length > 0 ? a[0] : Lua4Value.FromString(string.Empty)]));

        interp.SetGlobal("GetTeamSize", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(trace.TeamSize)]));
        interp.SetGlobal("GetTeamMember", Lua4Value.FromFunction(a =>
        {
            int ord = IntArg(a, 0);
            int slot = trace.TeamSize > 0 && (uint)(ord - 1) < (uint)trace.TeamSize ? ord : 0;
            return [Lua4Value.FromNumber(slot)];
        }));
        interp.SetGlobal("IsCaptain", Lua4Value.FromFunction(_ =>
        {
            SyncActivePlayerFromLua();
            return [Lua4Value.FromNumber(trace.IsCurrentPlayerCaptain ? 1 : 0)];
        }));

        // Camp, PK, Punish, Rank, Timer, WorldPos, Revival
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
        interp.SetGlobal("GetPKFlag", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(trace.FinalPkMode)]));
        interp.SetGlobal("SetPKFlag", Lua4Value.FromFunction(a =>
        {
            trace.FinalPkMode = (byte)IntArg(a, 0);
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("GetForbidChangePK", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(trace.IsPkSwitchForbidden ? 1 : 0)]));
        interp.SetGlobal("ForbidChangePK", Lua4Value.FromFunction(a =>
        {
            trace.IsPkSwitchForbidden = IntArg(a, 0) == 1;
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("GetPunish", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(trace.IsDeathPunishDisabled ? 0 : 1)]));
        interp.SetGlobal("SetPunish", Lua4Value.FromFunction(a =>
        {
            trace.IsDeathPunishDisabled = IntArg(a, 0) == 0;
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("GetLogoutRV", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(trace.UseReviveIdOnLogin ? 1 : 0)]));
        interp.SetGlobal("SetLogoutRV", Lua4Value.FromFunction(a =>
        {
            trace.UseReviveIdOnLogin = IntArg(a, 0) != 0;
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("IsDisabledUseTownP", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(trace.IsTownPortalDisabled ? 1 : 0)]));
        interp.SetGlobal("DisabledUseTownP", Lua4Value.FromFunction(a =>
        {
            trace.IsTownPortalDisabled = IntArg(a, 0) != 0;
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("GetTimerId", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(trace.ActiveScriptTimerId)]));
        interp.SetGlobal("SetTimer", Lua4Value.FromFunction(a =>
        {
            int frames = IntArg(a, 0);
            int tid = IntArg(a, 1);
            if (frames <= 0 || tid <= 0)
            {
                trace.ActiveScriptTimerId = 0;
                trace.Events.Add("STOP_TIMER");
            }
            else
            {
                trace.ActiveScriptTimerId = tid;
                trace.Events.Add($"SET_TIMER({frames},{tid})");
            }

            return [Lua4Value.FromNumber(1)];
        }));
        interp.SetGlobal("StopTimer", Lua4Value.FromFunction(_ =>
        {
            trace.ActiveScriptTimerId = 0;
            trace.Events.Add("STOP_TIMER");
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
        interp.SetGlobal("RevID2WXY", Lua4Value.FromFunction(a =>
        {
            ushort mapId = (ushort)IntArg(a, 0);
            int revId = IntArg(a, 1);
            if (RevivalPositionCatalog.TryGetCoordinates(mapId, revId, out int rx, out int ry))
            {
                return [Lua4Value.FromNumber(mapId), Lua4Value.FromNumber(rx), Lua4Value.FromNumber(ry)];
            }

            return [Lua4Value.FromNumber(mapId), Lua4Value.FromNumber(0), Lua4Value.FromNumber(0)];
        }));

        // Tasks & Bit/Byte helpers
        interp.SetGlobal("GetTask", Lua4Value.FromFunction(a =>
        {
            SyncActivePlayerFromLua();
            return [Lua4Value.FromNumber(trace.GetTaskValue((ushort)IntArg(a, 0)))];
        }));
        interp.SetGlobal("SetTask", Lua4Value.FromFunction(a =>
        {
            SyncActivePlayerFromLua();
            trace.SetTaskValue((ushort)IntArg(a, 0), IntArg(a, 1));
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("SyncTaskValue", Lua4Value.FromFunction(_ => [Lua4Value.Nil]));
        interp.SetGlobal("WriteLog", Lua4Value.FromFunction(_ => [Lua4Value.Nil]));
        interp.SetGlobal("WriteGoldLog", Lua4Value.FromFunction(_ => [Lua4Value.Nil]));
        interp.SetGlobal("UseSilver", Lua4Value.FromFunction(_ => [Lua4Value.Nil]));
        interp.SetGlobal("SaveNow", Lua4Value.FromFunction(_ => [Lua4Value.Nil]));
        interp.SetGlobal("LG_ApplyDoScript", Lua4Value.FromFunction(_ => [Lua4Value.Nil]));
        interp.SetGlobal("GetTaskTemp", Lua4Value.FromFunction(a =>
        {
            SyncActivePlayerFromLua();
            return [Lua4Value.FromNumber(trace.GetTempTaskValue((ushort)IntArg(a, 0)))];
        }));
        interp.SetGlobal("SetTaskTemp", Lua4Value.FromFunction(a =>
        {
            SyncActivePlayerFromLua();
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
        Func<Lua4Value[], Lua4Value[]> grantExpFn = a =>
        {
            SyncActivePlayerFromLua();
            long exp = a.Length > 0 ? (long)Math.Truncate(a[0].AsNumberOrDefault(0)) : 0L;
            trace.AddExperienceForActivePlayer(exp);
            return [Lua4Value.Nil];
        };
        interp.SetGlobal("AddOwnExp", Lua4Value.FromFunction(grantExpFn));
        interp.SetGlobal("tl_addPlayerExp", Lua4Value.FromFunction(grantExpFn));
        interp.SetGlobal("Add120SkillExp", Lua4Value.FromFunction(a =>
        {
            const ushort TaskIdActive120Skill = 2463;
            const ushort TaskIdToday120Exp = 2464;
            const ushort TaskIdLast120Date = 2465;
            const int Max120SkillExpPerDay = 33_000_000;

            SyncActivePlayerFromLua();
            int addExp = IntArg(a, 0);
            if (addExp == 0)
            {
                return [Lua4Value.FromNumber(0)];
            }

            int skillId = trace.GetTaskValue(TaskIdActive120Skill);
            if ((uint)(skillId - 1) >= 1999u)
            {
                return [Lua4Value.FromNumber(0)];
            }

            int lastDate = trace.GetTaskValue(TaskIdLast120Date);
            int todayAdd = trace.GetTaskValue(TaskIdToday120Exp);
            DateTime utc = simTime.Kind == DateTimeKind.Utc ? simTime : DateTime.SpecifyKind(simTime, DateTimeKind.Utc);
            int curDate = (int)((uint)unchecked((int)new DateTimeOffset(utc).ToUnixTimeSeconds()) / 86400u);
            if (lastDate != curDate)
            {
                todayAdd = 0;
                trace.SetTaskValue(TaskIdLast120Date, curDate);
                trace.SetTaskValue(TaskIdToday120Exp, 0);
            }

            if (addExp > 0)
            {
                if ((uint)Max120SkillExpPerDay <= (uint)todayAdd)
                {
                    return [Lua4Value.FromNumber(0)];
                }

                if (Max120SkillExpPerDay - todayAdd < addExp)
                {
                    addExp = Max120SkillExpPerDay - todayAdd;
                }

                trace.SetTaskValue(TaskIdToday120Exp, addExp + todayAdd);
            }

            trace.Events.Add($"SKILL_EXP({skillId},{addExp})");
            return [Lua4Value.FromNumber(1)];
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

        bool TryResolveEventArg(Lua4Value[] a, out string code)
        {
            if (a.Length == 0)
            {
                code = string.Empty;
                return false;
            }

            if (a[0].Kind == 1) // number
            {
                int eid = IntArg(a, 0);
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

            string name = StrArg(a, 0);
            if (_itemCatalogIndex is not null && _itemCatalogIndex.TryResolveEventItemByName(name, out _, out code))
            {
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
            bool has = TryResolveEventArg(a, out string code) && bagItems.GetValueOrDefault(code, 0) >= 1;
            return [Lua4Value.FromNumber(has ? 1 : 0)];
        }));
        Func<Lua4Value[], Lua4Value[]> getItemCountFn = a =>
        {
            int count = TryResolveEventArg(a, out string code) ? bagItems.GetValueOrDefault(code, 0) : 0;
            return [Lua4Value.FromNumber(count)];
        };
        interp.SetGlobal("GetItemCount", Lua4Value.FromFunction(getItemCountFn));
        interp.SetGlobal("GetItemCountEx", Lua4Value.FromFunction(getItemCountFn));
        interp.SetGlobal("AddEventItem", Lua4Value.FromFunction(a =>
        {
            if (TryResolveEventArg(a, out string code))
            {
                GrantBagCode(code, 1, isEquip: false);
            }

            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("DelItem", Lua4Value.FromFunction(a =>
        {
            if (TryResolveEventArg(a, out string code))
            {
                _ = ConsumeBagCode(code, 1);
            }

            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("AddGoldItem", Lua4Value.FromFunction(a =>
        {
            int rowId = IntArg(a, 1);
            if (_itemCatalogIndex is not null && _itemCatalogIndex.TryResolveGoldEquipByRowId(rowId, out PackItemEntry gold))
            {
                GrantBagCode(gold.Code, 1, isEquip: true);
                return [Lua4Value.FromNumber(1)];
            }

            return [Lua4Value.FromNumber(0)];
        }));
        interp.SetGlobal("IsMyItem", Lua4Value.FromFunction(a => [Lua4Value.FromNumber(IntArg(a, 0) == 40001 ? 1 : 0)]));
        interp.SetGlobal("RemoveItemByIndex", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(1)]));

        // `awardtype/item.lua` NewItemEx & runtime item-handle host bindings
        var pendingCreatedItems = new Dictionary<int, (string Code, string NameUtf8, ushort MaxStack, int StackCount, bool IsEquip)>();
        int nextCreatedItemIdx = 50001;
        interp.SetGlobal("NewItemEx", Lua4Value.FromFunction(a =>
        {
            int quality = IntArg(a, 2);
            int g = IntArg(a, 3);
            int d = IntArg(a, 4);
            int p = IntArg(a, 5);
            int lv = IntArg(a, 6, 1);

            if (quality == 1)
            {
                // In `awardtype/item.lua:191`, `nRow = 1` was subtracted from `tbProp[2]`
                if (_itemCatalogIndex is not null && _itemCatalogIndex.TryResolveGoldEquipByRowId(d + 1, out PackItemEntry gold))
                {
                    int idx = nextCreatedItemIdx++;
                    pendingCreatedItems[idx] = (gold.Code, gold.NameUtf8, 1, 1, IsEquip: true);
                    return [Lua4Value.FromNumber(idx)];
                }

                return [Lua4Value.FromNumber(-1)];
            }

            if (_itemCatalogIndex is not null && _itemCatalogIndex.TryResolveByGdpl(g, d, p, lv, out PackItemEntry item))
            {
                int idx = nextCreatedItemIdx++;
                pendingCreatedItems[idx] = (item.Code, item.NameUtf8, g == 0 ? (ushort)1 : item.MaxStackCount, 1, IsEquip: g == 0);
                return [Lua4Value.FromNumber(idx)];
            }

            return [Lua4Value.FromNumber(-1)];
        }));
        interp.SetGlobal("IsItemStackable", Lua4Value.FromFunction(a =>
            pendingCreatedItems.TryGetValue(IntArg(a, 0), out var it) && it.MaxStack > 1
                ? [Lua4Value.FromNumber(1)]
                : [Lua4Value.FromNumber(0)]));
        interp.SetGlobal("GetItemStackCount", Lua4Value.FromFunction(a =>
            pendingCreatedItems.TryGetValue(IntArg(a, 0), out var it)
                ? [Lua4Value.FromNumber(it.StackCount)]
                : [Lua4Value.FromNumber(1)]));
        interp.SetGlobal("GetItemMaxStackCount", Lua4Value.FromFunction(a =>
            pendingCreatedItems.TryGetValue(IntArg(a, 0), out var it)
                ? [Lua4Value.FromNumber(it.MaxStack)]
                : [Lua4Value.FromNumber(1)]));
        interp.SetGlobal("SetItemStackCount", Lua4Value.FromFunction(a =>
        {
            int idx = IntArg(a, 0);
            if (pendingCreatedItems.TryGetValue(idx, out var it))
            {
                pendingCreatedItems[idx] = it with { StackCount = Math.Max(1, IntArg(a, 1, 1)) };
            }

            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("GetItemName", Lua4Value.FromFunction(a =>
            pendingCreatedItems.TryGetValue(IntArg(a, 0), out var it)
                ? [ToUtf8BackedLuaString(it.NameUtf8)]
                : [Lua4Value.FromString(string.Empty)]));
        interp.SetGlobal("AddItemByIndex", Lua4Value.FromFunction(a =>
        {
            if (pendingCreatedItems.TryGetValue(IntArg(a, 0), out var it))
            {
                GrantBagCode(it.Code, it.StackCount, it.IsEquip);
            }

            return [Lua4Value.FromNumber(1)];
        }));
        interp.SetGlobal("ITEM_SetExpiredTime", Lua4Value.FromFunction(_ => [Lua4Value.Nil]));
        interp.SetGlobal("ITEM_SetExpiredTime2", Lua4Value.FromFunction(_ => [Lua4Value.Nil]));
        interp.SetGlobal("ITEM_SetLeftUsageTime", Lua4Value.FromFunction(_ => [Lua4Value.Nil]));
        interp.SetGlobal("SetItemBindState", Lua4Value.FromFunction(_ => [Lua4Value.Nil]));
        interp.SetGlobal("SetCurDurability", Lua4Value.FromFunction(_ => [Lua4Value.Nil]));
        interp.SetGlobal("SetMaxDurability", Lua4Value.FromFunction(_ => [Lua4Value.Nil]));

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
                trace.TempDeathRevival = null;
            }

            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("SetTempRevPos", Lua4Value.FromFunction(a =>
        {
            if (a.Length == 1)
            {
                int revId = IntArg(a, 0);
                if (RevivalPositionCatalog.TryGetCoordinates(trace.FinalMapId, revId, out int rx, out int ry))
                {
                    trace.TempDeathRevival = (trace.FinalMapId, rx, ry);
                }
                else
                {
                    trace.TempDeathRevival = (57, 50976, 102208);
                }
            }
            else if (a.Length >= 3)
            {
                trace.TempDeathRevival = ((ushort)IntArg(a, 0), IntArg(a, 1), IntArg(a, 2));
            }

            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("OpenBox", Lua4Value.FromFunction(_ =>
        {
            trace.OpenedStoreBox = true;
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("CheckStoreBoxState", Lua4Value.FromFunction(_ => [Lua4Value.FromNumber(0)]));
        interp.SetGlobal("OpenStoreBox", Lua4Value.FromFunction(a =>
        {
            trace.Events.Add($"UNLOCK_BOX({(byte)IntArg(a, 0)})");
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
            if (a.Length < 2)
            {
                return [Lua4Value.Nil];
            }

            int declared = Math.Max(0, IntArg(a, 0));
            int count = Math.Min(declared, a.Length - 1);
            var ids = new List<int>(count);
            for (int i = 0; i < count; i++)
            {
                ids.Add(IntArg(a, i + 1));
            }

            trace.Events.Add($"LADDER([{string.Join(",", ids)}])");
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("Msg2Player", Lua4Value.FromFunction(a =>
        {
            SyncActivePlayerFromLua();
            trace.Events.Add(trace.PrefixForActivePlayer($"MSG(\"{ScriptEffectTrace.EscapeEventText(StrArg(a, 0))}\")"));
            return [Lua4Value.Nil];
        }));
        interp.SetGlobal("AddNote", Lua4Value.FromFunction(a =>
        {
            SyncActivePlayerFromLua();
            trace.Events.Add(trace.PrefixForActivePlayer($"NOTE(\"{ScriptEffectTrace.EscapeEventText(StrArg(a, 0))}\")"));
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
            // Binary Ground Truth (`LuaTalkUI` @ KLua_2_1_UIAndDialog.cpp:25): `if (2 < lua_gettop(L))`
            if (a.Length < 3)
            {
                return [Lua4Value.Nil];
            }

            SyncActivePlayerFromLua();
            string cb = a[1].AsLuaString().Trim();
            var pages = new List<string>(a.Length - 2);
            bool byStringId = a[2].Kind == 1;
            for (int i = 2; i < a.Length; i++)
            {
                pages.Add(byStringId ? $"STR_ID:{IntArg(a, i)}" : StrArg(a, i));
            }

            trace.Events.Add(trace.PrefixForActivePlayer(ScriptEffectTrace.FormatTalkEvent(pages)));
            setTalkCallback(string.IsNullOrEmpty(cb) ? null : cb);
            return [Lua4Value.Nil];
        }));

        Func<Lua4Value[], Lua4Value[]> sayOrDescribeFn = a =>
        {
            if (a.Length < 1)
            {
                return [Lua4Value.Nil];
            }

            string prompt = a[0].Kind == 1 ? $"STR_ID:{IntArg(a, 0)}" : StrArg(a, 0);
            int declaredCount = a.Length >= 2 && a[1].Kind == 1 ? IntArg(a, 1) : 0;
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

    private static bool TryMapFactionNameToId(string? factionName, out byte factionId)
    {
        factionId = (factionName?.Trim().ToLowerInvariant()) switch
        {
            "shaolin" => 0,
            "tianwang" => 1,
            "tangmen" => 2,
            "wudu" => 3,
            "emei" => 4,
            "cuiyan" => 5,
            "gaibang" => 6,
            "tianren" => 7,
            "wudang" => 8,
            "kunlun" => 9,
            "huashan" => 10,
            _ => 255,
        };
        return factionId != 255;
    }
}
