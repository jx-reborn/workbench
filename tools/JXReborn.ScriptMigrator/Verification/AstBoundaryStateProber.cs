using System;
using System.Collections.Generic;
using System.Linq;
using JXReborn.ScriptMigrator.Ast;
using JXReborn.ScriptMigrator.Generators;

namespace JXReborn.ScriptMigrator.Verification;

/// <summary>
/// Walks a parsed <see cref="LuaProgram"/> AST to extract comparison boundaries on player state,
/// tasks, items, skills, factions, fight mode, and interactive <c>Say</c> options, producing a
/// compact high-coverage set of <see cref="ScriptStateVector"/> probes.
/// </summary>
public static class AstBoundaryStateProber
{
    public static IReadOnlyList<ScriptStateVector> BuildVectors(
        LuaProgram? program,
        IReadOnlyDictionary<int, string> eventItemCodeByDetailId)
    {
        var vectors = new List<ScriptStateVector>
        {
            new("baseline-peace-lv50", Level: 50, FightMode: 0, Gender: 0, InventoryMoney: 100_000, FreeBagCells: 20),
        };

        if (program is null)
        {
            return vectors;
        }

        var ctx = new ProbeCollector(eventItemCodeByDetailId);
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

        foreach (byte lv in ctx.LevelProbes.Take(4))
        {
            vectors.Add(new ScriptStateVector($"level-{lv}", Level: lv));
        }

        foreach (byte facId in ctx.FactionProbes.Take(4))
        {
            vectors.Add(new ScriptStateVector($"faction-{facId}", CurrentFactionId: facId, EffectiveFactionId: facId));
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
            vectors.Add(new ScriptStateVector(
                $"task-{taskId}-eq-{val}",
                Tasks: new Dictionary<ushort, int> { [taskId] = val }));
        }

        if (ctx.ItemCodeProbes.Count > 0)
        {
            vectors.Add(new ScriptStateVector(
                "owned-event-items",
                OwnedItemCodes: new HashSet<string>(ctx.ItemCodeProbes, StringComparer.OrdinalIgnoreCase)));
        }

        if (ctx.SkillProbes.Count > 0)
        {
            var skills = ctx.SkillProbes.ToDictionary(static id => id, static _ => 1);
            vectors.Add(new ScriptStateVector("learned-skills", LearnedSkills: skills));
        }

        int maxOpt = Math.Min(ctx.MaxSayOptions, 6);
        for (int opt = 0; opt < maxOpt; opt++)
        {
            vectors.Add(new ScriptStateVector($"dialog-opt-{opt}", DialogChoices: [opt, 0]));
        }

        return vectors;
    }

    private sealed class ProbeCollector(IReadOnlyDictionary<int, string> eventItemCodes)
    {
        public bool SawFightState { get; private set; }
        public bool SawGender { get; private set; }
        public bool SawBagCheck { get; private set; }
        public int MaxSayOptions { get; private set; }
        public SortedSet<byte> LevelProbes { get; } = [];
        public SortedSet<byte> FactionProbes { get; } = [];
        public SortedSet<int> MoneyProbes { get; } = [];
        public HashSet<(ushort TaskId, int Value)> TaskProbes { get; } = [];
        public SortedSet<string> ItemCodeProbes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SortedSet<int> SkillProbes { get; } = [];

        public void VisitStmt(LuaStatement stmt)
        {
            switch (stmt)
            {
                case LuaLocalDeclStmt ld when ld.Initializer is not null:
                    VisitExpr(ld.Initializer);
                    break;
                case LuaAssignStmt asg:
                    VisitExpr(asg.Value);
                    break;
                case LuaCallStmt cs:
                    VisitExpr(cs.Call);
                    break;
                case LuaReturnStmt ret when ret.Value is not null:
                    VisitExpr(ret.Value);
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
                case LuaUnaryExpr un:
                    VisitExpr(un.Operand);
                    break;

                case LuaIndexExpr idx:
                    VisitExpr(idx.Target);
                    VisitExpr(idx.Index);
                    break;

                case LuaTableLiteralExpr tbl:
                    foreach (LuaExpression e in tbl.Elements) VisitExpr(e);
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
                case "CalcFreeItemCellCount":
                    SawBagCheck = true;
                    break;
                case "Pay" when call.Arguments.Count == 1 && LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], out int cost) && cost > 0:
                    MoneyProbes.Add(Math.Max(0, cost - 1));
                    MoneyProbes.Add(cost);
                    break;
                case "HaveItem" or "DelItem" when call.Arguments.Count >= 1 &&
                                                  LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], out int eid) &&
                                                  eventItemCodes.TryGetValue(eid, out string? itemCode):
                    ItemCodeProbes.Add(itemCode);
                    break;
                case "HaveMagic" when call.Arguments.Count == 1 &&
                                      LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], out int sid) && sid > 0:
                    SkillProbes.Add(sid);
                    break;
                case "Say" when call.Arguments.Count >= 3:
                    MaxSayOptions = Math.Max(MaxSayOptions, call.Arguments.Count - 2);
                    break;
            }
        }

        private void InspectComparison(LuaExpression probeTarget, LuaExpression constantSide)
        {
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

                case "GetCash":
                    MoneyProbes.Add(Math.Max(0, val - 1));
                    MoneyProbes.Add(Math.Max(0, val));
                    break;

                case "GetTask" when call.Arguments.Count == 1 &&
                                    LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], out int tid) &&
                                    tid is >= 0 and <= ushort.MaxValue:
                    TaskProbes.Add(((ushort)tid, val));
                    TaskProbes.Add(((ushort)tid, val + 1));
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
