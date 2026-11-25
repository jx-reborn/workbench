using System;
using System.Collections.Generic;
using System.Linq;
using JXReborn.ScriptMigrator.Ast;
using JXReborn.ScriptMigrator.Generators;

namespace JXReborn.ScriptMigrator.Verification;

/// <summary>
/// Walks a parsed <see cref="LuaProgram"/> AST to extract comparison boundaries on player state,
/// tasks, party state, timers, items, skills, factions, fight mode, and interactive dialog options,
/// producing a compact high-coverage set of <see cref="ScriptStateVector"/> probes.
/// </summary>
public static class AstBoundaryStateProber
{
    public static IReadOnlyList<ScriptStateVector> BuildVectors(
        LuaProgram? program,
        IReadOnlyDictionary<int, string> eventItemCodeByDetailId,
        PackItemCatalogIndex? itemCatalogIndex = null)
    {
        var vectors = new List<ScriptStateVector>
        {
            new("baseline-peace-lv50", Level: 50, FightMode: 0, Gender: 0, InventoryMoney: 100_000, FreeBagCells: 20),
        };

        if (program is null)
        {
            return vectors;
        }

        var ctx = new ProbeCollector(eventItemCodeByDetailId, itemCatalogIndex);
        foreach (LuaStatement stmt in program.TopLevelStatements)
        {
            ctx.VisitStmt(stmt);
        }

        foreach (LuaFunctionDecl fn in program.Functions)
        {
            foreach (LuaStatement stmt in fn.Body)
            {
                ctx.VisitStmt(stmt);
            }
        }

        if (ctx.SawFightState)
        {
            vectors.Add(new ScriptStateVector("fightmode-1", Level: 50, FightMode: 1));
        }

        if (ctx.SawGender)
        {
            vectors.Add(new ScriptStateVector("gender-female", Level: 50, Gender: 1));
        }

        if (ctx.SawTimer)
        {
            vectors.Add(new ScriptStateVector("timer-active-other", ActiveTimerId: 5));
            vectors.Add(new ScriptStateVector("timer-active-8", ActiveTimerId: 8));
        }

        if (ctx.SawClock)
        {
            vectors.Add(new ScriptStateVector(
                "clock-weekday-mon",
                SimulatedLocalTime: new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Unspecified),
                DialogChoices: [0, 0]));
            vectors.Add(new ScriptStateVector(
                "clock-weekend-sat",
                SimulatedLocalTime: new DateTime(2026, 11, 21, 20, 0, 0, DateTimeKind.Unspecified),
                DialogChoices: [0, 0]));
        }

        foreach (byte lv in ctx.LevelProbes.Take(4))
        {
            vectors.Add(new ScriptStateVector($"level-{lv}", Level: lv));
        }

        foreach (byte facId in ctx.FactionProbes.Take(4))
        {
            byte seriesForFac = facId switch
            {
                0 or 1 => 0,
                2 or 3 => 1,
                4 or 5 => 2,
                6 or 7 => 3,
                8 or 9 or 10 => 4,
                _ => 0,
            };
            vectors.Add(new ScriptStateVector($"faction-{facId}", Series: seriesForFac, CurrentFactionId: facId, EffectiveFactionId: facId));
        }

        foreach (byte camp in ctx.CampProbes.Take(3))
        {
            vectors.Add(new ScriptStateVector($"camp-{camp}", BaseCamp: camp, CurrentCamp: camp));
        }

        foreach (int money in ctx.MoneyProbes.Take(3))
        {
            vectors.Add(new ScriptStateVector($"money-{money}", InventoryMoney: money));
        }

        if (ctx.SawBagCheck)
        {
            vectors.Add(new ScriptStateVector("bag-full", FreeBagCells: 0));
        }

        foreach ((ushort taskId, int val) in ctx.TaskProbes.Take(8))
        {
            var taskDict = new Dictionary<ushort, int> { [taskId] = val };
            vectors.Add(new ScriptStateVector(
                $"task-{taskId}-eq-{val}",
                Tasks: taskDict,
                DialogChoices: [0, 0]));

            if (ctx.SawParty)
            {
                vectors.Add(new ScriptStateVector(
                    $"party2-task-{taskId}-eq-{val}",
                    Tasks: taskDict,
                    TeamSize: 2,
                    InitiatorPartySlot: 1,
                    TeammateTasks: new Dictionary<ushort, int> { [taskId] = val },
                    DialogChoices: [0, 0]));
            }
        }

        if (ctx.SawParty && ctx.TaskProbes.Count == 0)
        {
            vectors.Add(new ScriptStateVector("party2-baseline", TeamSize: 2, InitiatorPartySlot: 1, DialogChoices: [0, 0]));
        }

        foreach ((ushort tempId, int val) in ctx.TempTaskProbes.Take(4))
        {
            vectors.Add(new ScriptStateVector(
                $"temptask-{tempId}-eq-{val}",
                TempTasks: new Dictionary<ushort, int> { [tempId] = val }));
        }

        foreach ((int gvId, int val) in ctx.GlobalValueProbes.Take(4))
        {
            vectors.Add(new ScriptStateVector(
                $"globalval-{gvId}-eq-{val}",
                GlobalValues: new Dictionary<int, int> { [gvId] = val }));
        }

        if (ctx.ItemCodeProbes.Count > 0 || ctx.ItemCountProbes.Count > 0)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string c in ctx.ItemCodeProbes)
            {
                counts[c] = 5;
            }

            foreach ((string c, int n) in ctx.ItemCountProbes)
            {
                counts[c] = Math.Max(counts.GetValueOrDefault(c, 0), n);
            }

            vectors.Add(new ScriptStateVector(
                "owned-items",
                OwnedItemCodes: new HashSet<string>(counts.Keys, StringComparer.OrdinalIgnoreCase),
                OwnedItemCounts: counts,
                DialogChoices: [0, 0]));
        }

        if (ctx.SkillProbes.Count > 0)
        {
            var skills = ctx.SkillProbes.ToDictionary(static id => id, static _ => 1);
            vectors.Add(new ScriptStateVector("learned-skills", LearnedSkills: skills));
        }

        foreach (int numInput in ctx.NumberInputProbes.Take(3))
        {
            vectors.Add(new ScriptStateVector(
                $"num-input-{numInput}",
                DialogChoices: [0, 0],
                NumberInputs: [numInput]));
        }

        int maxOpt = Math.Min(ctx.MaxSayOptions, 6);
        for (int opt = 0; opt < maxOpt; opt++)
        {
            vectors.Add(new ScriptStateVector($"dialog-opt-{opt}", DialogChoices: [opt, 0]));
        }

        if (ctx.SawItemExpiration)
        {
            // Simulated default clock is 2026-11-23 12:00:00 UTC (1795435200); probe valid future & expired past.
            vectors.Add(new ScriptStateVector("item-exp-valid", ItemExpirationUnixSeconds: 1893456000u));
            vectors.Add(new ScriptStateVector("item-exp-expired", ItemExpirationUnixSeconds: 1700000000u));
        }

        foreach (int pType in ctx.ParticularTypeProbes.Take(4))
        {
            vectors.Add(new ScriptStateVector($"item-particular-{pType}", ParticularType: pType));
        }

        return vectors;
    }

    private sealed class ProbeCollector(
        IReadOnlyDictionary<int, string> eventItemCodes,
        PackItemCatalogIndex? itemCatalogIndex)
    {
        public bool SawFightState { get; private set; }
        public bool SawGender { get; private set; }
        public bool SawBagCheck { get; private set; }
        public bool SawClock { get; private set; }
        public bool SawTimer { get; private set; }
        public bool SawParty { get; private set; }
        public int MaxSayOptions { get; private set; }
        public SortedSet<byte> LevelProbes { get; } = [];
        public SortedSet<byte> FactionProbes { get; } = [];
        public SortedSet<byte> CampProbes { get; } = [];
        public SortedSet<int> MoneyProbes { get; } = [];
        public HashSet<(ushort TaskId, int Value)> TaskProbes { get; } = [];
        public HashSet<(ushort TaskId, int Value)> TempTaskProbes { get; } = [];
        public HashSet<(int Key, int Value)> GlobalValueProbes { get; } = [];
        public SortedSet<string> ItemCodeProbes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> ItemCountProbes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SortedSet<int> SkillProbes { get; } = [];
        public SortedSet<int> NumberInputProbes { get; } = [];
        public SortedSet<int> ParticularTypeProbes { get; } = [];
        public bool SawItemExpiration { get; private set; }
        public bool SawItemProp { get; private set; }

        public void VisitStmt(LuaStatement stmt)
        {
            switch (stmt)
            {
                case LuaLocalDeclStmt ld when ld.Initializer is not null:
                    VisitExpr(ld.Initializer);
                    break;
                case LuaMultiLocalDeclStmt mld:
                    foreach (LuaExpression init in mld.Initializers) VisitExpr(init);
                    break;
                case LuaAssignStmt asg:
                    if (asg.TargetName == "PlayerIndex") SawParty = true;
                    VisitExpr(asg.Value);
                    break;
                case LuaMultiAssignStmt masg:
                    foreach (LuaExpression v in masg.Values) VisitExpr(v);
                    break;
                case LuaIndexAssignStmt iasg:
                    VisitExpr(iasg.Index);
                    VisitExpr(iasg.Value);
                    break;
                case LuaMemberAssignStmt mas:
                    VisitExpr(mas.Target);
                    VisitExpr(mas.Value);
                    break;
                case LuaCallStmt cs:
                    VisitExpr(cs.Call);
                    break;
                case LuaMemberCallStmt mcs:
                    VisitExpr(mcs.Call);
                    break;
                case LuaReturnStmt ret:
                    if (ret.Value is not null) VisitExpr(ret.Value);
                    if (ret.ExtraValues is not null)
                    {
                        foreach (LuaExpression extra in ret.ExtraValues) VisitExpr(extra);
                    }

                    break;
                case LuaDoBlockStmt dob:
                    foreach (LuaStatement s in dob.Body) VisitStmt(s);
                    break;
                case LuaWhileStmt wh:
                    VisitExpr(wh.Condition);
                    foreach (LuaStatement s in wh.Body) VisitStmt(s);
                    break;
                case LuaNumericForStmt nf:
                    VisitExpr(nf.Start);
                    VisitExpr(nf.Limit);
                    if (nf.Step is not null) VisitExpr(nf.Step);
                    foreach (LuaStatement s in nf.Body) VisitStmt(s);
                    break;
                case LuaIfStmt ifs:
                    VisitExpr(ifs.Condition);
                    foreach (LuaStatement s in ifs.ThenBody) VisitStmt(s);
                    foreach (LuaElseIfBranch ei in ifs.ElseIfBranches)
                    {
                        VisitExpr(ei.Condition);
                        foreach (LuaStatement s in ei.Body) VisitStmt(s);
                    }

                    foreach (LuaStatement s in ifs.ElseBody) VisitStmt(s);
                    break;
            }
        }

        private void VisitExpr(LuaExpression expr)
        {
            switch (expr)
            {
                case LuaIdentifierExpr { Name: "PlayerIndex" }:
                    SawParty = true;
                    break;

                case LuaUnaryExpr un:
                    VisitExpr(un.Operand);
                    break;

                case LuaIndexExpr idx:
                    VisitExpr(idx.Target);
                    VisitExpr(idx.Index);
                    break;

                case LuaMemberAccessExpr ma:
                    VisitExpr(ma.Target);
                    break;

                case LuaMemberCallExpr mc:
                    if (mc.MemberName is "AddOptEntry" or "Show")
                    {
                        MaxSayOptions = Math.Max(MaxSayOptions, 4);
                    }

                    VisitExpr(mc.Receiver);
                    foreach (LuaExpression a in mc.Arguments) VisitExpr(a);
                    break;

                case LuaTableLiteralExpr tbl:
                    foreach (LuaExpression e in tbl.Elements) VisitExpr(e);
                    break;

                case LuaKeyedTableLiteralExpr ktbl:
                    foreach (LuaTableFieldEntry fe in ktbl.Entries)
                    {
                        if (fe.BracketKey is not null) VisitExpr(fe.BracketKey);
                        VisitExpr(fe.Value);
                    }

                    break;

                case LuaBinaryExpr bin:
                    InspectComparison(bin.Left, bin.Right);
                    InspectComparison(bin.Right, bin.Left);
                    VisitExpr(bin.Left);
                    VisitExpr(bin.Right);
                    break;

                case LuaCallExpr call:
                    InspectCall(call);
                    foreach (LuaExpression a in call.Arguments) VisitExpr(a);
                    break;
            }
        }

        private void InspectCall(LuaCallExpr call)
        {
            switch (call.CalleeName)
            {
                case "GetFightState":
                    SawFightState = true;
                    break;
                case "GetSex":
                    SawGender = true;
                    break;
                case "CalcFreeItemCellCount" or "CountFreeRoomByWH":
                    SawBagCheck = true;
                    break;
                case "GetLocalDate" or "date" or "GetCurServerTime":
                    SawClock = true;
                    break;
                case "GetTimerId":
                    SawTimer = true;
                    break;
                case "GetTeamSize" or "GetTeamMember" or "IsCaptain":
                    SawParty = true;
                    break;
                case "Add120SkillExp":
                    SawClock = true;
                    TaskProbes.Add((2463, 708));
                    break;
                case "Pay" when call.Arguments.Count == 1 && LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], out int cost) && cost > 0:
                    MoneyProbes.Add(Math.Max(0, cost - 1));
                    MoneyProbes.Add(cost);
                    break;
                case "HaveItem" or "DelItem" or "GetItemCount" or "GetItemCountEx" when call.Arguments.Count >= 1:
                    if (LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], out int eid) &&
                        eventItemCodes.TryGetValue(eid, out string? itemCode))
                    {
                        ItemCountProbes[itemCode] = Math.Max(ItemCountProbes.GetValueOrDefault(itemCode, 0), 5);
                    }
                    else if (itemCatalogIndex is not null &&
                             LuaScriptSemanticValidator.TryEvalConstantString(call.Arguments[0], out string? itemName) &&
                             itemName is not null &&
                             itemCatalogIndex.TryResolveEventItemByName(itemName, out _, out string byNameCode))
                    {
                        ItemCountProbes[byNameCode] = Math.Max(ItemCountProbes.GetValueOrDefault(byNameCode, 0), 5);
                    }

                    break;
                case "DelCommonItem" when itemCatalogIndex is not null &&
                                          call.Arguments.Count >= 3 &&
                                          LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], out int dg) &&
                                          LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[1], out int dd) &&
                                          LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[2], out int dp) &&
                                          itemCatalogIndex.TryResolveByGdp(dg, dd, dp, out PackItemEntry dEntry):
                    ItemCountProbes[dEntry.Code] = Math.Max(ItemCountProbes.GetValueOrDefault(dEntry.Code, 0), 5);
                    break;
                case "CalcEquiproomItemCount" when itemCatalogIndex is not null &&
                                                   call.Arguments.Count == 4 &&
                                                   LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], out int cg) &&
                                                   LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[1], out int cd) &&
                                                   LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[2], out int cp) &&
                                                   LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[3], out int cl) &&
                                                   itemCatalogIndex.TryResolveByGdpl(cg, cd, cp, cl, out PackItemEntry cEntry):
                    ItemCountProbes[cEntry.Code] = Math.Max(ItemCountProbes.GetValueOrDefault(cEntry.Code, 0), 5);
                    break;
                case "ConsumeEquiproomItem" when itemCatalogIndex is not null &&
                                                 call.Arguments.Count == 5 &&
                                                 LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], out int reqCnt) &&
                                                 LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[1], out int xg) &&
                                                 LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[2], out int xd) &&
                                                 LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[3], out int xp) &&
                                                 LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[4], out int xl) &&
                                                 itemCatalogIndex.TryResolveByGdpl(xg, xd, xp, xl, out PackItemEntry xEntry):
                    ItemCountProbes[xEntry.Code] = Math.Max(ItemCountProbes.GetValueOrDefault(xEntry.Code, 0), Math.Max(1, reqCnt));
                    break;
                case "GetItemProp":
                    SawItemProp = true;
                    break;
                case "ITEM_GetExpiredTime":
                    SawItemExpiration = true;
                    break;
                case "HaveCommonItem" when itemCatalogIndex is not null &&
                                            call.Arguments.Count >= 3 &&
                                            LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], out int hg) &&
                                            LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[1], out int hd) &&
                                            LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[2], out int hp) &&
                                            itemCatalogIndex.TryResolveByGdp(hg, hd, hp, out PackItemEntry hEntry):
                    ItemCountProbes[hEntry.Code] = Math.Max(ItemCountProbes.GetValueOrDefault(hEntry.Code, 0), 5);
                    break;
                case "CalcItemCount" when itemCatalogIndex is not null &&
                                           call.Arguments.Count >= 5 &&
                                           LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[1], out int cig) &&
                                           LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[2], out int cid) &&
                                           LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[3], out int cip) &&
                                           LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[4], out int cil) &&
                                           itemCatalogIndex.TryResolveByGdpl(cig, cid, cip, cil, out PackItemEntry ciEntry):
                    ItemCountProbes[ciEntry.Code] = Math.Max(ItemCountProbes.GetValueOrDefault(ciEntry.Code, 0), 5);
                    break;
                case "ConsumeItem" when itemCatalogIndex is not null &&
                                         call.Arguments.Count >= 6 &&
                                         LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[1], out int reqCnt2) &&
                                         LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[2], out int csg) &&
                                         LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[3], out int csd) &&
                                         LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[4], out int csp) &&
                                         LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[5], out int csl) &&
                                         itemCatalogIndex.TryResolveByGdpl(csg, csd, csp, csl, out PackItemEntry csEntry):
                    ItemCountProbes[csEntry.Code] = Math.Max(ItemCountProbes.GetValueOrDefault(csEntry.Code, 0), Math.Max(1, reqCnt2));
                    break;
                case "HaveMagic" or "DelMagic" when call.Arguments.Count == 1 &&
                                                    LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], out int sid) && sid > 0:
                    SkillProbes.Add(sid);
                    break;
                case "AskClientForNumber" when call.Arguments.Count >= 4:
                    if (LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[1], out int minN) &&
                        LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[2], out int maxN) &&
                        maxN >= minN)
                    {
                        NumberInputProbes.Add(minN);
                        NumberInputProbes.Add(minN + ((maxN - minN) / 2));
                    }
                    else
                    {
                        NumberInputProbes.Add(1);
                    }

                    break;
                case "Say" when call.Arguments.Count >= 3:
                    MaxSayOptions = Math.Max(MaxSayOptions, call.Arguments.Count - 2);
                    break;
                case "Say" when call.Arguments.Count == 2:
                case "Describe" when call.Arguments.Count >= 2:
                case "CreateTaskSay" when call.Arguments.Count == 1:
                case "TaskSay" when call.Arguments.Count == 2:
                case "CreateNewSayEx" when call.Arguments.Count == 2:
                case "tinsert":
                    MaxSayOptions = Math.Max(MaxSayOptions, 4);
                    break;
                case "Prise" when call.Arguments.Count >= 2:
                    MaxSayOptions = Math.Max(MaxSayOptions, call.Arguments.Count - 1);
                    break;
            }
        }

        private void InspectComparison(LuaExpression probeTarget, LuaExpression constantSide)
        {
            if (probeTarget is LuaIdentifierExpr { Name: "nP" or "nParticular" or "nPart" } &&
                SawItemProp &&
                LuaScriptSemanticValidator.TryEvalConstantInt(constantSide, out int pVal) &&
                pVal > 0)
            {
                ParticularTypeProbes.Add(pVal);
                return;
            }

            if (probeTarget is not LuaCallExpr call)
            {
                return;
            }

            if (call.CalleeName is "GetFaction" or "GetLastAddFaction" &&
                LuaScriptSemanticValidator.TryEvalConstantString(constantSide, out string? facStr) &&
                TryMapFactionId(facStr, out byte facId))
            {
                FactionProbes.Add(facId);
                return;
            }

            if (!LuaScriptSemanticValidator.TryEvalConstantInt(constantSide, out int val))
            {
                return;
            }

            switch (call.CalleeName)
            {
                case "GetLevel":
                    LevelProbes.Add((byte)Math.Clamp(val - 1, 1, 200));
                    LevelProbes.Add((byte)Math.Clamp(val, 1, 200));
                    LevelProbes.Add((byte)Math.Clamp(val + 1, 1, 200));
                    break;

                case "GetCamp" or "GetCurCamp":
                    CampProbes.Add((byte)Math.Clamp(val, 0, 255));
                    break;

                case "GetCash":
                    MoneyProbes.Add(Math.Max(0, val - 1));
                    MoneyProbes.Add(Math.Max(0, val));
                    break;

                case "GetTask" or "nt_getTask" when call.Arguments.Count == 1 &&
                                                    LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], out int tid) &&
                                                    tid is >= 0 and <= ushort.MaxValue:
                    TaskProbes.Add(((ushort)tid, val));
                    TaskProbes.Add(((ushort)tid, val + 1));
                    break;

                case "GetTaskTemp" when call.Arguments.Count == 1 &&
                                        LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], out int ttid) &&
                                        ttid is >= 0 and <= ushort.MaxValue:
                    TempTaskProbes.Add(((ushort)ttid, val));
                    break;

                case "GetGlbValue" when call.Arguments.Count == 1 &&
                                        LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], out int gvid):
                    GlobalValueProbes.Add((gvid, val));
                    break;

                case "GetByte" when call.Arguments.Count == 2 &&
                                    call.Arguments[0] is LuaCallExpr { CalleeName: "GetTask", Arguments.Count: 1 } gt &&
                                    LuaScriptSemanticValidator.TryEvalConstantInt(gt.Arguments[0], out int bTid) &&
                                    LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[1], out int byteIdx) &&
                                    bTid is >= 0 and <= ushort.MaxValue &&
                                    byteIdx is >= 1 and <= 4:
                    TaskProbes.Add(((ushort)bTid, (val & 0xFF) << ((byteIdx - 1) * 8)));
                    break;

                case "GetBit" when call.Arguments.Count == 2 &&
                                   call.Arguments[0] is LuaCallExpr { CalleeName: "GetTask", Arguments.Count: 1 } gbt &&
                                   LuaScriptSemanticValidator.TryEvalConstantInt(gbt.Arguments[0], out int bitTid) &&
                                   LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[1], out int bitIdx) &&
                                   bitTid is >= 0 and <= ushort.MaxValue &&
                                   bitIdx is >= 1 and <= 32:
                    TaskProbes.Add(((ushort)bitTid, 1 << (bitIdx - 1)));
                    break;
            }
        }

        private static bool TryMapFactionId(string? name, out byte id)
        {
            id = (name?.Trim().ToLowerInvariant()) switch
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
            return id != 255;
        }
    }
}
