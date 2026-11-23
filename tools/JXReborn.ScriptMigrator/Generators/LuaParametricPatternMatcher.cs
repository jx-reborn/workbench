using System;
using System.Collections.Generic;
using JXReborn.ScriptMigrator.Ast;
using JXReborn.ScriptMigrator.Models;

namespace JXReborn.ScriptMigrator.Generators;

/// <summary>
/// Recognizes exact, zero-loss parametric AST archetypes (portals, city-gate fight toggles,
/// static dialogs, no-op traps, signboards, storage boxes, ladder boards, return portals)
/// so identical clusters emit into compact O(1) table-driven C# classes instead of repetitive boilerplate.
/// </summary>
public static class LuaParametricPatternMatcher
{
    public static bool TryMatchPortalTrap(
        CompiledLuaScript script,
        out (ushort TargetMapId, int MpsX, int MpsY, sbyte FightMode, int WayPointId, int ProtectFrames, int SkillId, int SkillLevel, int SkillStateType, int SkillDuration) data)
    {
        data = default;
        if (script.Kind != GeneratedScriptKind.Trap ||
            script.Program.TopLevelStatements.Count != 0 ||
            script.Program.Functions.Count != 1)
        {
            return false;
        }

        LuaFunctionDecl mainFn = script.Program.Functions[0];
        bool hasNewWorld = false;
        ushort mapId = 0;
        int mpsX = 0;
        int mpsY = 0;
        sbyte fightMode = -1; // -1 = preserve current fight mode
        int wayPointId = 0;
        int protectFrames = 0;
        int skillId = 0;
        int skillLevel = 0;
        int skillStateType = 0;
        int skillDuration = 0;

        foreach (LuaStatement stmt in mainFn.Body)
        {
            if (stmt is LuaReturnStmt)
            {
                continue;
            }

            if (stmt is not LuaCallStmt { Call: var call })
            {
                return false;
            }

            switch (call.CalleeName)
            {
                case "SetFightState" when call.Arguments.Count == 1 && LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], out int fm):
                    fightMode = (sbyte)(fm != 0 ? 1 : 0);
                    break;
                case "NewWorld" when !hasNewWorld && call.Arguments.Count == 3 &&
                                     LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], out int m) &&
                                     LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[1], out int x) &&
                                     LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[2], out int y) &&
                                     m is > 0 and <= ushort.MaxValue:
                    hasNewWorld = true;
                    mapId = (ushort)m;
                    mpsX = x * 32;
                    mpsY = y * 32;
                    break;
                case "AddTermini" when wayPointId == 0 && call.Arguments.Count == 1 &&
                                       LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], out int wp) && wp > 0:
                    wayPointId = wp;
                    break;
                case "SetProtectTime" when protectFrames == 0 && call.Arguments.Count == 1 &&
                                           LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], out int pf) && pf > 0:
                    protectFrames = pf;
                    break;
                case "AddSkillState" when skillId == 0 && call.Arguments.Count is 4 or 5 &&
                                          LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], out int sid) &&
                                          LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[1], out int slvl) &&
                                          LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[2], out int stype) &&
                                          LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[3], out int sdur):
                    skillId = sid;
                    skillLevel = slvl;
                    skillStateType = stype;
                    skillDuration = sdur;
                    break;
                default:
                    return false;
            }
        }

        if (!hasNewWorld)
        {
            return false;
        }

        data = (mapId, mpsX, mpsY, fightMode, wayPointId, protectFrames, skillId, skillLevel, skillStateType, skillDuration);
        return true;
    }

    public static bool TryMatchFightToggleTrap(
        CompiledLuaScript script,
        out (int OutMpsX, int OutMpsY, byte OutFightMode, int InMpsX, int InMpsY, byte InFightMode, ushort StationId, int ProtectFrames, int SkillId, int SkillLevel, int SkillStateType, int SkillDuration) data)
    {
        data = default;
        if (script.Kind != GeneratedScriptKind.Trap ||
            script.Program.TopLevelStatements.Count != 0 ||
            script.Program.Functions.Count != 1)
        {
            return false;
        }

        LuaFunctionDecl mainFn = script.Program.Functions[0];
        bool hasToggleIf = false;
        int outX = 0, outY = 0, inX = 0, inY = 0;
        byte outFight = 1, inFight = 0;
        ushort stationId = 0;
        int protectFrames = 0;
        int skillId = 0, skillLevel = 0, skillStateType = 0, skillDuration = 0;

        foreach (LuaStatement stmt in mainFn.Body)
        {
            if (stmt is LuaReturnStmt)
            {
                continue;
            }

            if (stmt is LuaIfStmt ifStmt && !hasToggleIf && ifStmt.ElseIfBranches.Count == 0 && ifStmt.ElseBody.Count > 0)
            {
                if (!TryMatchFightZeroComparison(ifStmt.Condition, out bool equalsZero))
                {
                    return false;
                }

                IReadOnlyList<LuaStatement> zeroBranch = equalsZero ? ifStmt.ThenBody : ifStmt.ElseBody;
                IReadOnlyList<LuaStatement> nonZeroBranch = equalsZero ? ifStmt.ElseBody : ifStmt.ThenBody;

                if (!TryExtractSetPosAndFightState(zeroBranch, out outX, out outY, out outFight) ||
                    !TryExtractSetPosAndFightState(nonZeroBranch, out inX, out inY, out inFight))
                {
                    return false;
                }

                hasToggleIf = true;
                continue;
            }

            if (stmt is LuaCallStmt { Call: var call })
            {
                switch (call.CalleeName)
                {
                    case "AddStation" when stationId == 0 && call.Arguments.Count == 1 &&
                                           LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], out int st) &&
                                           st is > 0 and <= ushort.MaxValue:
                        stationId = (ushort)st;
                        continue;
                    case "SetProtectTime" when protectFrames == 0 && call.Arguments.Count == 1 &&
                                               LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], out int pf) && pf > 0:
                        protectFrames = pf;
                        continue;
                    case "AddSkillState" when skillId == 0 && call.Arguments.Count is 4 or 5 &&
                                              LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], out int sid) &&
                                              LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[1], out int slvl) &&
                                              LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[2], out int stype) &&
                                              LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[3], out int sdur):
                        skillId = sid;
                        skillLevel = slvl;
                        skillStateType = stype;
                        skillDuration = sdur;
                        continue;
                }
            }

            return false;
        }

        if (!hasToggleIf)
        {
            return false;
        }

        data = (outX, outY, outFight, inX, inY, inFight, stationId, protectFrames, skillId, skillLevel, skillStateType, skillDuration);
        return true;
    }

    public static bool TryMatchNoOpTrap(CompiledLuaScript script)
    {
        if (script.Kind != GeneratedScriptKind.Trap ||
            script.Program.TopLevelStatements.Count != 0 ||
            script.Program.Functions.Count != 1)
        {
            return false;
        }

        foreach (LuaStatement stmt in script.Program.Functions[0].Body)
        {
            if (stmt is not LuaReturnStmt)
            {
                return false;
            }
        }

        return true;
    }

    public static bool TryMatchStaticDialog(
        CompiledLuaScript script,
        GeneratedScriptKind expectedKind,
        out (byte Mode, string[] Lines) data)
    {
        // Mode: 0 = Say(line), 1 = Talk(lines), 2 = Msg2Player(line)
        data = default;
        if (script.Kind != expectedKind ||
            script.Program.TopLevelStatements.Count != 0 ||
            script.Program.Functions.Count != 1)
        {
            return false;
        }

        List<LuaStatement> nonReturn = [];
        foreach (LuaStatement s in script.Program.Functions[0].Body)
        {
            if (s is not LuaReturnStmt)
            {
                nonReturn.Add(s);
            }
        }

        if (nonReturn.Count != 1 || nonReturn[0] is not LuaCallStmt { Call: var call })
        {
            return false;
        }

        if (call.CalleeName == "Say" &&
            call.Arguments.Count == 2 &&
            LuaScriptSemanticValidator.TryEvalConstantString(call.Arguments[0], out string? sayPrompt) &&
            sayPrompt is not null &&
            LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[1], out int sayCount) &&
            sayCount == 0)
        {
            data = (0, [sayPrompt]);
            return true;
        }

        if (call.CalleeName == "Talk" &&
            call.Arguments.Count >= 3 &&
            ((call.Arguments[1] is LuaStringExpr cb && string.IsNullOrWhiteSpace(cb.DecodedUtf8)) ||
             call.Arguments[1] is LuaNilExpr))
        {
            var pages = new List<string>(call.Arguments.Count - 2);
            for (int i = 2; i < call.Arguments.Count; i++)
            {
                if (!LuaScriptSemanticValidator.TryEvalConstantString(call.Arguments[i], out string? page) || page is null)
                {
                    return false;
                }

                pages.Add(page);
            }

            data = (1, pages.ToArray());
            return true;
        }

        if (call.CalleeName == "Msg2Player" &&
            call.Arguments.Count == 1 &&
            LuaScriptSemanticValidator.TryEvalConstantString(call.Arguments[0], out string? msg) &&
            msg is not null)
        {
            data = (2, [msg]);
            return true;
        }

        return false;
    }

    public static bool TryMatchStorageBoxObject(
        CompiledLuaScript script,
        out (int ReviveId, ushort ReviveMapId, string RequiredFaction) data)
    {
        data = default;
        if (script.Kind != GeneratedScriptKind.Object ||
            script.Program.TopLevelStatements.Count != 0 ||
            script.Program.Functions.Count != 1)
        {
            return false;
        }

        bool hasOpenBox = false;
        int reviveId = 0;
        ushort reviveMapId = 0; // 0 = current map
        string requiredFaction = string.Empty;

        foreach (LuaStatement stmt in script.Program.Functions[0].Body)
        {
            if (stmt is LuaReturnStmt)
            {
                continue;
            }

            if (stmt is LuaCallStmt { Call: { CalleeName: "OpenBox", Arguments.Count: 0 } })
            {
                hasOpenBox = true;
                continue;
            }

            if (stmt is LuaCallStmt { Call: { CalleeName: "SetRevPos" } revCall } && reviveId == 0)
            {
                if (!TryExtractSetRevPos(revCall, out reviveMapId, out reviveId))
                {
                    return false;
                }

                continue;
            }

            if (stmt is LuaIfStmt ifStmt &&
                reviveId == 0 &&
                ifStmt.ElseIfBranches.Count == 0 &&
                ifStmt.ElseBody.Count == 0 &&
                ifStmt.ThenBody.Count == 1 &&
                ifStmt.ThenBody[0] is LuaCallStmt { Call: { CalleeName: "SetRevPos" } gatedRev } &&
                TryMatchFactionEquals(ifStmt.Condition, out string faction) &&
                TryExtractSetRevPos(gatedRev, out reviveMapId, out reviveId))
            {
                requiredFaction = faction;
                continue;
            }

            return false;
        }

        if (!hasOpenBox)
        {
            return false;
        }

        data = (reviveId, reviveMapId, requiredFaction);
        return true;
    }

    public static bool TryMatchLadderBoardObject(CompiledLuaScript script, out int[] ladderIds)
    {
        ladderIds = [];
        if (script.Kind != GeneratedScriptKind.Object ||
            script.Program.TopLevelStatements.Count != 0 ||
            script.Program.Functions.Count != 1)
        {
            return false;
        }

        List<LuaStatement> nonReturn = [];
        foreach (LuaStatement s in script.Program.Functions[0].Body)
        {
            if (s is not LuaReturnStmt)
            {
                nonReturn.Add(s);
            }
        }

        if (nonReturn.Count != 1 || nonReturn[0] is not LuaCallStmt { Call: { CalleeName: "ShowLadder" } call })
        {
            return false;
        }

        if (call.Arguments.Count < 2 ||
            !LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], out int count) ||
            count != call.Arguments.Count - 1)
        {
            return false;
        }

        var ids = new int[count];
        for (int i = 0; i < count; i++)
        {
            if (!LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[i + 1], out ids[i]))
            {
                return false;
            }
        }

        ladderIds = ids;
        return true;
    }

    public static bool TryMatchReturnPortalObject(CompiledLuaScript script)
    {
        if (script.Kind != GeneratedScriptKind.Object ||
            script.Program.TopLevelStatements.Count != 0 ||
            script.Program.Functions.Count != 1)
        {
            return false;
        }

        List<LuaStatement> nonReturn = [];
        foreach (LuaStatement s in script.Program.Functions[0].Body)
        {
            if (s is not LuaReturnStmt)
            {
                nonReturn.Add(s);
            }
        }

        return nonReturn.Count == 1 &&
               nonReturn[0] is LuaCallStmt { Call: { CalleeName: "ReturnFromPortal", Arguments.Count: 0 } };
    }

    private static bool TryMatchFightZeroComparison(LuaExpression cond, out bool equalsZero)
    {
        equalsZero = true;
        if (cond is not LuaBinaryExpr { Operator: LuaBinaryOp.Eq or LuaBinaryOp.Neq } bin)
        {
            return false;
        }

        bool leftIsGetFight = bin.Left is LuaCallExpr { CalleeName: "GetFightState", Arguments.Count: 0 };
        bool rightIsGetFight = bin.Right is LuaCallExpr { CalleeName: "GetFightState", Arguments.Count: 0 };
        LuaExpression other;
        if (leftIsGetFight)
        {
            other = bin.Right;
        }
        else if (rightIsGetFight)
        {
            other = bin.Left;
        }
        else
        {
            return false;
        }

        if (!LuaScriptSemanticValidator.TryEvalConstantInt(other, out int cmpVal))
        {
            return false;
        }

        if (cmpVal == 0)
        {
            equalsZero = bin.Operator == LuaBinaryOp.Eq;
            return true;
        }

        if (cmpVal == 1)
        {
            equalsZero = bin.Operator == LuaBinaryOp.Neq;
            return true;
        }

        return false;
    }

    private static bool TryExtractSetPosAndFightState(
        IReadOnlyList<LuaStatement> body,
        out int mpsX,
        out int mpsY,
        out byte fightMode)
    {
        mpsX = 0;
        mpsY = 0;
        fightMode = 0;
        bool hasPos = false;
        bool hasFight = false;

        foreach (LuaStatement stmt in body)
        {
            if (stmt is LuaReturnStmt)
            {
                continue;
            }

            if (stmt is not LuaCallStmt { Call: var call })
            {
                return false;
            }

            if (call.CalleeName == "SetPos" && !hasPos && call.Arguments.Count == 2 &&
                LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], out int x) &&
                LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[1], out int y))
            {
                mpsX = x * 32;
                mpsY = y * 32;
                hasPos = true;
            }
            else if (call.CalleeName == "SetFightState" && !hasFight && call.Arguments.Count == 1 &&
                     LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], out int f))
            {
                fightMode = (byte)(f != 0 ? 1 : 0);
                hasFight = true;
            }
            else
            {
                return false;
            }
        }

        return hasPos && hasFight;
    }

    private static bool TryExtractSetRevPos(LuaCallExpr call, out ushort mapId, out int reviveId)
    {
        mapId = 0;
        reviveId = 0;
        if (call.Arguments.Count == 1 &&
            LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], out int r1) &&
            r1 > 0)
        {
            mapId = 0;
            reviveId = r1;
            return true;
        }

        if (call.Arguments.Count == 2 &&
            LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], out int m) &&
            LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[1], out int r2) &&
            m is > 0 and <= ushort.MaxValue &&
            r2 > 0)
        {
            mapId = (ushort)m;
            reviveId = r2;
            return true;
        }

        return false;
    }

    private static bool TryMatchFactionEquals(LuaExpression cond, out string factionName)
    {
        factionName = string.Empty;
        if (cond is not LuaBinaryExpr { Operator: LuaBinaryOp.Eq } bin)
        {
            return false;
        }

        if (bin.Left is LuaCallExpr { CalleeName: "GetFaction", Arguments.Count: 0 } &&
            LuaScriptSemanticValidator.TryEvalConstantString(bin.Right, out string? r) &&
            !string.IsNullOrWhiteSpace(r))
        {
            factionName = r;
            return true;
        }

        if (bin.Right is LuaCallExpr { CalleeName: "GetFaction", Arguments.Count: 0 } &&
            LuaScriptSemanticValidator.TryEvalConstantString(bin.Left, out string? l) &&
            !string.IsNullOrWhiteSpace(l))
        {
            factionName = l;
            return true;
        }

        return false;
    }
}
