using System;
using System.Collections.Generic;
using System.Linq;
using JXReborn.ScriptMigrator.Ast;
using JXReborn.ScriptMigrator.Models;

namespace JXReborn.ScriptMigrator.Generators;

/// <summary>
/// Strictly fail-closed static semantic validator and type-checker for parsed Lua 4.0 scripts.
/// Verifies zero includes, definite variable assignment, strict C-API allowlist per script kind,
/// exact Genre-4 EventItem ID existence, valid static dialog callbacks, acyclic local calls,
/// and exact item usage return semantics.
/// </summary>
public sealed class LuaScriptSemanticValidator
{
    private readonly IReadOnlyDictionary<int, string> _eventItemCodeByDetailId;

    public LuaScriptSemanticValidator(IReadOnlyDictionary<int, string> eventItemCodeByDetailId)
    {
        _eventItemCodeByDetailId = eventItemCodeByDetailId ?? throw new ArgumentNullException(nameof(eventItemCodeByDetailId));
    }

    public (CompiledLuaScript? Script, IReadOnlyList<string> BlockingReasons) Validate(
        ScriptInventoryEntry entry,
        string utf8ScriptPath,
        GeneratedScriptKind kind,
        LuaProgram program)
    {
        var reasons = new SortedSet<string>(StringComparer.Ordinal);

        // 1. Validate top-level statements: ONLY immutable constant declarations allowed.
        var topLevelConsts = new Dictionary<string, LuaValueType>(StringComparer.Ordinal);
        var topLevelConstExprs = new Dictionary<string, LuaExpression>(StringComparer.Ordinal);
        foreach (LuaStatement stmt in program.TopLevelStatements)
        {
            if (stmt is LuaCallStmt callStmt)
            {
                if (callStmt.Call.CalleeName.Equals("Include", StringComparison.OrdinalIgnoreCase) ||
                    callStmt.Call.CalleeName.Equals("IncludeLib", StringComparison.OrdinalIgnoreCase) ||
                    callStmt.Call.CalleeName.Equals("dofile", StringComparison.OrdinalIgnoreCase))
                {
                    string target = callStmt.Call.Arguments.Count > 0 && callStmt.Call.Arguments[0] is LuaStringExpr s
                        ? s.DecodedUtf8
                        : "?";
                    reasons.Add($"include:{target}");
                }
                else
                {
                    reasons.Add($"unsupported-toplevel-call:{callStmt.Call.CalleeName}");
                }

                continue;
            }

            if (stmt is LuaLocalDeclStmt localDecl)
            {
                if (localDecl.Initializer is null)
                {
                    reasons.Add($"uninitialized-toplevel:{localDecl.Name}");
                    continue;
                }

                if (!TryValidateConstantExpr(localDecl.Initializer, topLevelConsts, out LuaValueType constType, out string? constErr))
                {
                    reasons.Add(constErr ?? $"non-const-toplevel:{localDecl.Name}");
                    continue;
                }

                topLevelConsts[localDecl.Name] = constType;
                topLevelConstExprs[localDecl.Name] = localDecl.Initializer;
                continue;
            }

            if (stmt is LuaAssignStmt assignStmt)
            {
                if (!TryValidateConstantExpr(assignStmt.Value, topLevelConsts, out LuaValueType constType, out string? constErr))
                {
                    reasons.Add(constErr ?? $"non-const-toplevel:{assignStmt.TargetName}");
                    continue;
                }

                if (topLevelConsts.ContainsKey(assignStmt.TargetName))
                {
                    reasons.Add($"reassigned-toplevel:{assignStmt.TargetName}");
                    continue;
                }

                topLevelConsts[assignStmt.TargetName] = constType;
                topLevelConstExprs[assignStmt.TargetName] = assignStmt.Value;
                continue;
            }

            reasons.Add($"unsupported-toplevel-stmt:{stmt.GetType().Name}");
        }

        if (reasons.Count > 0)
        {
            return (null, reasons.ToArray());
        }

        // 2. Locate entrypoint function.
        string expectedEntry = kind == GeneratedScriptKind.Monster ? "OnDeath" : "main";
        var functionsByName = program.Functions.ToDictionary(static f => f.Name, StringComparer.Ordinal);
        if (!functionsByName.TryGetValue(expectedEntry, out LuaFunctionDecl? entryFn))
        {
            reasons.Add($"missing-entrypoint:{expectedEntry}");
            return (null, reasons.ToArray());
        }

        // 3. Build call graph & detect recursion among local functions.
        var localCalls = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (LuaFunctionDecl fn in program.Functions)
        {
            var edges = new HashSet<string>(StringComparer.Ordinal);
            CollectLocalCallEdges(fn.Body, functionsByName, edges);
            localCalls[fn.Name] = edges;
        }

        if (HasCycle(localCalls))
        {
            reasons.Add("unsupported-syntax:recursive-local-functions");
            return (null, reasons.ToArray());
        }

        // Order functions bottom-up (callees before callers) via topological sort.
        List<LuaFunctionDecl> orderedFunctions = TopologicalOrder(program.Functions, localCalls);

        // Reject helper functions with parameters unless 0-arg (keep semantics 100% crisp and fail-closed).
        // Entry function may declare unused conventional parameters (e.g., `main(sel)` or `OnDeath(nNpcIdx)`),
        // which are NOT added to the readable variable scope so reading them fails closed cleanly.
        var functionParamTypes = new Dictionary<string, IReadOnlyList<LuaValueType>>(StringComparer.Ordinal);
        foreach (LuaFunctionDecl fn in program.Functions)
        {
            if (!string.Equals(fn.Name, expectedEntry, StringComparison.Ordinal) && fn.Parameters.Count > 0)
            {
                reasons.Add($"unsupported-helper-params:{fn.Name}({fn.Parameters.Count})");
            }

            functionParamTypes[fn.Name] = [];
        }

        if (reasons.Count > 0)
        {
            return (null, reasons.ToArray());
        }

        // 4. Validate each function bottom-up.
        var fnLocalTypes = new Dictionary<string, IReadOnlyDictionary<string, LuaValueType>>(StringComparer.Ordinal);
        var fnIsAsync = new Dictionary<string, bool>(StringComparer.Ordinal);
        var fnReturnTypes = new Dictionary<string, LuaValueType>(StringComparer.Ordinal);

        foreach (LuaFunctionDecl fn in orderedFunctions)
        {
            bool isEntry = string.Equals(fn.Name, expectedEntry, StringComparison.Ordinal);
            var localTypes = new Dictionary<string, LuaValueType>(StringComparer.Ordinal);
            var assigned = new HashSet<string>(topLevelConsts.Keys, StringComparer.Ordinal);
            var constArrays = new Dictionary<string, LuaTableLiteralExpr>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, LuaExpression> kv in topLevelConstExprs)
            {
                if (kv.Value is LuaTableLiteralExpr tbl)
                {
                    constArrays[kv.Key] = tbl;
                }
            }

            bool isAsync = false;
            var returnType = LuaValueType.Void;

            ValidateBlock(
                fn.Body,
                kind,
                isEntry,
                fn.Name,
                functionsByName,
                topLevelConsts,
                localTypes,
                assigned,
                constArrays,
                fnIsAsync,
                fnReturnTypes,
                ref isAsync,
                ref returnType,
                reasons);

            if (kind == GeneratedScriptKind.Monster && isAsync)
            {
                reasons.Add($"async-not-supported-in-monster-ondeath:{fn.Name}");
            }

            fnLocalTypes[fn.Name] = localTypes;
            fnIsAsync[fn.Name] = isAsync;
            fnReturnTypes[fn.Name] = returnType;
        }

        if (reasons.Count > 0)
        {
            return (null, reasons.ToArray());
        }

        return (
            new CompiledLuaScript(
                Entry: entry,
                Utf8ScriptPath: utf8ScriptPath,
                Kind: kind,
                Program: program,
                EntryFunctionName: expectedEntry,
                EntryIsAsync: fnIsAsync[expectedEntry],
                TopLevelConstantTypes: topLevelConsts,
                FunctionLocalTypes: fnLocalTypes,
                FunctionIsAsync: fnIsAsync,
                FunctionParamTypes: functionParamTypes,
                FunctionReturnTypes: fnReturnTypes),
            []);
    }

    private void ValidateBlock(
        IReadOnlyList<LuaStatement> statements,
        GeneratedScriptKind kind,
        bool isEntry,
        string fnName,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        IReadOnlyDictionary<string, LuaValueType> topLevelConsts,
        Dictionary<string, LuaValueType> localTypes,
        HashSet<string> assigned,
        Dictionary<string, LuaTableLiteralExpr> constArrays,
        IReadOnlyDictionary<string, bool> fnIsAsync,
        IReadOnlyDictionary<string, LuaValueType> fnReturnTypes,
        ref bool isAsync,
        ref LuaValueType returnType,
        SortedSet<string> reasons)
    {
        for (int i = 0; i < statements.Count; i++)
        {
            LuaStatement stmt = statements[i];
            switch (stmt)
            {
                case LuaLocalDeclStmt localDecl:
                {
                    if (topLevelConsts.ContainsKey(localDecl.Name))
                    {
                        reasons.Add($"shadow-toplevel-const:{localDecl.Name}");
                        break;
                    }

                    if (localDecl.Initializer is null)
                    {
                        reasons.Add($"uninitialized-local:{localDecl.Name}");
                        break;
                    }

                    LuaValueType rhsType = ValidateExpr(
                        localDecl.Initializer,
                        kind,
                        functionsByName,
                        topLevelConsts,
                        localTypes,
                        assigned,
                        fnIsAsync,
                        fnReturnTypes,
                        ref isAsync,
                        reasons);
                    if (rhsType == LuaValueType.Void)
                    {
                        reasons.Add($"void-assign:{localDecl.Name}");
                        break;
                    }

                    if (localTypes.TryGetValue(localDecl.Name, out LuaValueType existing) && existing != rhsType)
                    {
                        reasons.Add($"type-mismatch:{localDecl.Name}({existing}->{rhsType})");
                        break;
                    }

                    localTypes[localDecl.Name] = rhsType;
                    assigned.Add(localDecl.Name);
                    if (localDecl.Initializer is LuaTableLiteralExpr tbl)
                    {
                        constArrays[localDecl.Name] = tbl;
                    }
                    else
                    {
                        constArrays.Remove(localDecl.Name);
                    }

                    break;
                }

                case LuaAssignStmt assignStmt:
                {
                    if (topLevelConsts.ContainsKey(assignStmt.TargetName))
                    {
                        reasons.Add($"mutate-toplevel-const:{assignStmt.TargetName}");
                        break;
                    }

                    LuaValueType rhsType = ValidateExpr(
                        assignStmt.Value,
                        kind,
                        functionsByName,
                        topLevelConsts,
                        localTypes,
                        assigned,
                        fnIsAsync,
                        fnReturnTypes,
                        ref isAsync,
                        reasons);
                    if (rhsType == LuaValueType.Void)
                    {
                        reasons.Add($"void-assign:{assignStmt.TargetName}");
                        break;
                    }

                    if (localTypes.TryGetValue(assignStmt.TargetName, out LuaValueType existing) && existing != rhsType)
                    {
                        reasons.Add($"type-mismatch:{assignStmt.TargetName}({existing}->{rhsType})");
                        break;
                    }

                    localTypes[assignStmt.TargetName] = rhsType;
                    assigned.Add(assignStmt.TargetName);
                    if (assignStmt.Value is LuaTableLiteralExpr tbl)
                    {
                        constArrays[assignStmt.TargetName] = tbl;
                    }
                    else
                    {
                        constArrays.Remove(assignStmt.TargetName);
                    }

                    break;
                }

                case LuaIfStmt ifStmt:
                {
                    LuaValueType condType = ValidateExpr(
                        ifStmt.Condition,
                        kind,
                        functionsByName,
                        topLevelConsts,
                        localTypes,
                        assigned,
                        fnIsAsync,
                        fnReturnTypes,
                        ref isAsync,
                        reasons);
                    if (condType != LuaValueType.Bool)
                    {
                        reasons.Add($"non-bool-condition@line{ifStmt.Line}");
                    }

                    var branchAssignedSets = new List<HashSet<string>>();

                    var thenAssigned = new HashSet<string>(assigned, StringComparer.Ordinal);
                    ValidateBlock(ifStmt.ThenBody, kind, isEntry, fnName, functionsByName, topLevelConsts, localTypes, thenAssigned, new Dictionary<string, LuaTableLiteralExpr>(constArrays, StringComparer.Ordinal), fnIsAsync, fnReturnTypes, ref isAsync, ref returnType, reasons);
                    branchAssignedSets.Add(thenAssigned);

                    foreach (LuaElseIfBranch ei in ifStmt.ElseIfBranches)
                    {
                        LuaValueType eiCondType = ValidateExpr(
                            ei.Condition,
                            kind,
                            functionsByName,
                            topLevelConsts,
                            localTypes,
                            assigned,
                            fnIsAsync,
                            fnReturnTypes,
                            ref isAsync,
                            reasons);
                        if (eiCondType != LuaValueType.Bool)
                        {
                            reasons.Add($"non-bool-condition@line{ei.Line}");
                        }

                        var eiAssigned = new HashSet<string>(assigned, StringComparer.Ordinal);
                        ValidateBlock(ei.Body, kind, isEntry, fnName, functionsByName, topLevelConsts, localTypes, eiAssigned, new Dictionary<string, LuaTableLiteralExpr>(constArrays, StringComparer.Ordinal), fnIsAsync, fnReturnTypes, ref isAsync, ref returnType, reasons);
                        branchAssignedSets.Add(eiAssigned);
                    }

                    if (ifStmt.ElseBody.Count > 0)
                    {
                        var elseAssigned = new HashSet<string>(assigned, StringComparer.Ordinal);
                        ValidateBlock(ifStmt.ElseBody, kind, isEntry, fnName, functionsByName, topLevelConsts, localTypes, elseAssigned, new Dictionary<string, LuaTableLiteralExpr>(constArrays, StringComparer.Ordinal), fnIsAsync, fnReturnTypes, ref isAsync, ref returnType, reasons);
                        branchAssignedSets.Add(elseAssigned);

                        // Any variable assigned in ALL branches (then + all elseif + else) is definitely assigned afterwards.
                        var intersection = new HashSet<string>(branchAssignedSets[0], StringComparer.Ordinal);
                        for (int b = 1; b < branchAssignedSets.Count; b++)
                        {
                            intersection.IntersectWith(branchAssignedSets[b]);
                        }

                        assigned.UnionWith(intersection);
                    }

                    break;
                }

                case LuaCallStmt callStmt:
                    ValidateCall(
                        callStmt.Call,
                        isStatement: true,
                        kind,
                        functionsByName,
                        topLevelConsts,
                        localTypes,
                        assigned,
                        constArrays,
                        fnIsAsync,
                        fnReturnTypes,
                        ref isAsync,
                        reasons);
                    break;

                case LuaReturnStmt retStmt:
                    if (kind == GeneratedScriptKind.Item && isEntry)
                    {
                        if (retStmt.Value is not null)
                        {
                            if (!TryEvalConstantInt(retStmt.Value, out _))
                            {
                                reasons.Add($"dynamic-item-return@line{retStmt.Line}");
                            }
                        }

                        returnType = LuaValueType.Int;
                    }
                    else if (retStmt.Value is not null)
                    {
                        if (isEntry)
                        {
                            // In non-item entrypoints (e.g. `return 0` in NPC/Trap), only allow constant int/nil returns that map to simple exit.
                            if (retStmt.Value is not LuaNilExpr && !TryEvalConstantInt(retStmt.Value, out _))
                            {
                                reasons.Add($"non-const-entry-return:{fnName}@line{retStmt.Line}");
                            }
                        }
                        else
                        {
                            LuaValueType rt = ValidateExpr(
                                retStmt.Value,
                                kind,
                                functionsByName,
                                topLevelConsts,
                                localTypes,
                                assigned,
                                fnIsAsync,
                                fnReturnTypes,
                                ref isAsync,
                                reasons);
                            if (returnType != LuaValueType.Void && returnType != rt)
                            {
                                reasons.Add($"inconsistent-return-type:{fnName}");
                            }
                            else
                            {
                                returnType = rt;
                            }
                        }
                    }

                    break;

                default:
                    reasons.Add($"unsupported-stmt:{stmt.GetType().Name}@line{stmt.Line}");
                    break;
            }
        }
    }

    private LuaValueType ValidateExpr(
        LuaExpression expr,
        GeneratedScriptKind kind,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        IReadOnlyDictionary<string, LuaValueType> topLevelConsts,
        IReadOnlyDictionary<string, LuaValueType> localTypes,
        IReadOnlySet<string> assigned,
        IReadOnlyDictionary<string, bool> fnIsAsync,
        IReadOnlyDictionary<string, LuaValueType> fnReturnTypes,
        ref bool isAsync,
        SortedSet<string> reasons)
    {
        switch (expr)
        {
            case LuaNumberExpr num:
                if (Math.Abs(num.Value - Math.Truncate(num.Value)) > 1e-9 ||
                    num.Value < int.MinValue ||
                    num.Value > int.MaxValue)
                {
                    reasons.Add($"non-int-number:{num.RawText}@line{num.Line}");
                }

                return LuaValueType.Int;

            case LuaStringExpr:
                return LuaValueType.String;

            case LuaNilExpr:
                reasons.Add($"unsupported-nil-expr@line{expr.Line}");
                return LuaValueType.Void;

            case LuaIdentifierExpr ident:
                if (!assigned.Contains(ident.Name))
                {
                    reasons.Add($"undefined-var:{ident.Name}");
                    return LuaValueType.Void;
                }

                if (topLevelConsts.TryGetValue(ident.Name, out LuaValueType topType))
                {
                    return topType;
                }

                if (localTypes.TryGetValue(ident.Name, out LuaValueType locType))
                {
                    return locType;
                }

                reasons.Add($"undefined-var:{ident.Name}");
                return LuaValueType.Void;

            case LuaTableLiteralExpr tbl:
                if (tbl.Elements.Count == 0)
                {
                    reasons.Add($"empty-table-literal@line{tbl.Line}");
                    return LuaValueType.Void;
                }

                LuaValueType elemType = LuaValueType.Void;
                for (int i = 0; i < tbl.Elements.Count; i++)
                {
                    LuaValueType et = ValidateExpr(tbl.Elements[i], kind, functionsByName, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                    if (i == 0)
                    {
                        elemType = et;
                    }
                    else if (et != elemType)
                    {
                        reasons.Add($"heterogeneous-table@line{tbl.Line}");
                        return LuaValueType.Void;
                    }
                }

                return elemType switch
                {
                    LuaValueType.String => LuaValueType.StringArray,
                    LuaValueType.Int => LuaValueType.IntArray,
                    _ => FailExpr($"unsupported-table-elem-type:{elemType}@line{tbl.Line}", reasons),
                };

            case LuaIndexExpr idxExpr:
            {
                LuaValueType targetType = ValidateExpr(idxExpr.Target, kind, functionsByName, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                LuaValueType indexType = ValidateExpr(idxExpr.Index, kind, functionsByName, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                if (indexType != LuaValueType.Int)
                {
                    reasons.Add($"non-int-index@line{idxExpr.Line}");
                    return LuaValueType.Void;
                }

                return targetType switch
                {
                    LuaValueType.StringArray => LuaValueType.String,
                    LuaValueType.IntArray => LuaValueType.Int,
                    _ => FailExpr($"index-non-array:{targetType}@line{idxExpr.Line}", reasons),
                };
            }

            case LuaUnaryExpr un:
            {
                LuaValueType opType = ValidateExpr(un.Operand, kind, functionsByName, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                if (un.Operator == LuaUnaryOp.Negate)
                {
                    if (opType != LuaValueType.Int)
                    {
                        reasons.Add($"unary-negate-non-int@line{un.Line}");
                    }

                    return LuaValueType.Int;
                }

                if (opType != LuaValueType.Bool)
                {
                    reasons.Add($"unary-not-non-bool@line{un.Line}");
                }

                return LuaValueType.Bool;
            }

            case LuaBinaryExpr bin:
            {
                LuaValueType lt = ValidateExpr(bin.Left, kind, functionsByName, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                LuaValueType rt = ValidateExpr(bin.Right, kind, functionsByName, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);

                switch (bin.Operator)
                {
                    case LuaBinaryOp.And:
                    case LuaBinaryOp.Or:
                        if (lt != LuaValueType.Bool || rt != LuaValueType.Bool)
                        {
                            reasons.Add($"non-bool-logic:{bin.Operator}@line{bin.Line}");
                        }

                        return LuaValueType.Bool;

                    case LuaBinaryOp.Eq:
                    case LuaBinaryOp.Neq:
                        if (lt != rt || lt is not (LuaValueType.Int or LuaValueType.String))
                        {
                            reasons.Add($"invalid-equality:{lt}-{rt}@line{bin.Line}");
                        }

                        return LuaValueType.Bool;

                    case LuaBinaryOp.Lt:
                    case LuaBinaryOp.Le:
                    case LuaBinaryOp.Gt:
                    case LuaBinaryOp.Ge:
                        if (lt != LuaValueType.Int || rt != LuaValueType.Int)
                        {
                            reasons.Add($"non-int-comparison:{bin.Operator}@line{bin.Line}");
                        }

                        return LuaValueType.Bool;

                    case LuaBinaryOp.Concat:
                        if (lt is not (LuaValueType.String or LuaValueType.Int) ||
                            rt is not (LuaValueType.String or LuaValueType.Int))
                        {
                            reasons.Add($"invalid-concat:{lt}-{rt}@line{bin.Line}");
                        }

                        return LuaValueType.String;

                    case LuaBinaryOp.Add:
                    case LuaBinaryOp.Sub:
                    case LuaBinaryOp.Mul:
                    case LuaBinaryOp.Mod:
                        if (lt != LuaValueType.Int || rt != LuaValueType.Int)
                        {
                            reasons.Add($"non-int-arith:{bin.Operator}@line{bin.Line}");
                        }

                        return LuaValueType.Int;

                    case LuaBinaryOp.Div:
                        if (!TryEvalConstantInt(bin, out _))
                        {
                            reasons.Add($"unsupported-runtime-float-div@line{bin.Line}");
                        }

                        return LuaValueType.Int;

                    case LuaBinaryOp.Pow:
                        if (!TryEvalConstantInt(bin, out _))
                        {
                            reasons.Add($"unsupported-runtime-pow@line{bin.Line}");
                        }

                        return LuaValueType.Int;

                    default:
                        reasons.Add($"unsupported-binary-op:{bin.Operator}@line{bin.Line}");
                        return LuaValueType.Void;
                }
            }

            case LuaCallExpr call:
                return ValidateCall(
                    call,
                    isStatement: false,
                    kind,
                    functionsByName,
                    topLevelConsts,
                    localTypes,
                    assigned,
                    constArrays: null,
                    fnIsAsync,
                    fnReturnTypes,
                    ref isAsync,
                    reasons);

            default:
                reasons.Add($"unsupported-expr:{expr.GetType().Name}@line{expr.Line}");
                return LuaValueType.Void;
        }
    }

    private LuaValueType ValidateCall(
        LuaCallExpr call,
        bool isStatement,
        GeneratedScriptKind kind,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        IReadOnlyDictionary<string, LuaValueType> topLevelConsts,
        IReadOnlyDictionary<string, LuaValueType> localTypes,
        IReadOnlySet<string> assigned,
        IReadOnlyDictionary<string, LuaTableLiteralExpr>? constArrays,
        IReadOnlyDictionary<string, bool> fnIsAsync,
        IReadOnlyDictionary<string, LuaValueType> fnReturnTypes,
        ref bool isAsync,
        SortedSet<string> reasons)
    {
        string api = call.CalleeName;
        IReadOnlyList<LuaExpression> args = call.Arguments;

        if (api.Equals("Include", StringComparison.OrdinalIgnoreCase) ||
            api.Equals("IncludeLib", StringComparison.OrdinalIgnoreCase) ||
            api.Equals("dofile", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add($"include:{api}");
            return LuaValueType.Void;
        }

        // Check intra-file helper function call
        if (functionsByName.ContainsKey(api))
        {
            if (args.Count != 0)
            {
                reasons.Add($"unsupported-helper-call-args:{api}({args.Count})");
                return LuaValueType.Void;
            }

            if (fnIsAsync.TryGetValue(api, out bool calleeAsync) && calleeAsync)
            {
                isAsync = true;
            }

            return fnReturnTypes.GetValueOrDefault(api, LuaValueType.Void);
        }

        // Helper to validate exact arg types (copy ref parameter for local function capture)
        bool localAsync = isAsync;
        LuaValueType ArgType(int index) =>
            ValidateExpr(args[index], kind, functionsByName, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref localAsync, reasons);

        bool RequireArgCount(int exact)
        {
            if (args.Count != exact)
            {
                reasons.Add($"invalid-arity:{api}({args.Count}!=expected:{exact})");
                return false;
            }

            return true;
        }

        bool RequireArgRange(int min, int max)
        {
            if (args.Count < min || args.Count > max)
            {
                reasons.Add($"invalid-arity:{api}({args.Count} not in {min}..{max})");
                return false;
            }

            return true;
        }

        try
        {
        switch (api)
        {
            // Zero-arg Int getters
            case "GetLevel":
            case "GetSex":
            case "GetSeries":
            case "GetCash":
            case "GetRepute":
            case "GetFightState":
            case "CalcFreeItemCellCount":
                RequireArgCount(0);
                return LuaValueType.Int;

            // Zero-arg String getters
            case "GetName":
            case "GetFaction":
            case "GetLastAddFaction":
                RequireArgCount(0);
                return LuaValueType.String;

            // 1-arg Int -> Int getters
            case "GetTask":
            case "GetTaskTemp":
            case "GetGlbValue":
            case "HaveMagic":
                if (RequireArgCount(1) && ArgType(0) != LuaValueType.Int)
                {
                    reasons.Add($"invalid-arg-type:{api}@line{call.Line}");
                }

                return LuaValueType.Int;

            // Pay(amount) -> Int (usable as expression or statement)
            case "Pay":
                if (RequireArgCount(1) && ArgType(0) != LuaValueType.Int)
                {
                    reasons.Add($"invalid-arg-type:{api}@line{call.Line}");
                }

                return LuaValueType.Int;

            // 2-arg Int -> Int
            case "GetByte":
            case "GetBit":
                if (RequireArgCount(2))
                {
                    if (ArgType(0) != LuaValueType.Int || ArgType(1) != LuaValueType.Int)
                    {
                        reasons.Add($"invalid-arg-type:{api}@line{call.Line}");
                    }
                }

                return LuaValueType.Int;

            // 3-arg Int -> Int
            case "SetByte":
            case "SetBit":
                if (RequireArgCount(3))
                {
                    if (ArgType(0) != LuaValueType.Int || ArgType(1) != LuaValueType.Int || ArgType(2) != LuaValueType.Int)
                    {
                        reasons.Add($"invalid-arg-type:{api}@line{call.Line}");
                    }
                }

                return LuaValueType.Int;

            // random(max) or random(min, max) -> Int
            case "random":
                if (RequireArgRange(1, 2))
                {
                    for (int i = 0; i < args.Count; i++)
                    {
                        if (ArgType(i) != LuaValueType.Int)
                        {
                            reasons.Add($"invalid-arg-type:random@line{call.Line}");
                        }
                    }
                }

                return LuaValueType.Int;

            // getn(array) -> Int
            case "getn":
                if (RequireArgCount(1))
                {
                    LuaValueType at = ArgType(0);
                    if (at is not (LuaValueType.StringArray or LuaValueType.IntArray))
                    {
                        reasons.Add($"invalid-arg-type:getn({at})@line{call.Line}");
                    }
                }

                return LuaValueType.Int;

            // HaveItem(constEventId) -> Int
            case "HaveItem":
                if (RequireArgCount(1))
                {
                    if (!TryEvalConstantInt(args[0], out int eventId))
                    {
                        reasons.Add($"dynamic-event-item:HaveItem@line{call.Line}");
                    }
                    else if (!_eventItemCodeByDetailId.ContainsKey(eventId))
                    {
                        reasons.Add($"unknown-event-item:{eventId}");
                    }
                }

                return LuaValueType.Int;

            // EventItem statements: AddEventItem(constId), DelItem(constId)
            case "AddEventItem":
            case "DelItem":
                if (!isStatement)
                {
                    reasons.Add($"statement-used-as-expr:{api}@line{call.Line}");
                }

                if (RequireArgCount(1))
                {
                    if (!TryEvalConstantInt(args[0], out int eventId))
                    {
                        reasons.Add($"dynamic-event-item:{api}@line{call.Line}");
                    }
                    else if (!_eventItemCodeByDetailId.ContainsKey(eventId))
                    {
                        reasons.Add($"unknown-event-item:{eventId}");
                    }
                }

                return LuaValueType.Void;

            // 1-arg Int statements
            case "Earn":
            case "AddOwnExp":
            case "AddRepute":
            case "SetFightState":
            case "SetProtectTime":
            case "AddStation":
            case "AddTermini":
                if (!isStatement)
                {
                    reasons.Add($"statement-used-as-expr:{api}@line{call.Line}");
                }

                if (RequireArgCount(1) && ArgType(0) != LuaValueType.Int)
                {
                    reasons.Add($"invalid-arg-type:{api}@line{call.Line}");
                }

                return LuaValueType.Void;

            // Item-only bonus point statements
            case "AddProp":
            case "AddMagicPoint":
                if (kind != GeneratedScriptKind.Item)
                {
                    reasons.Add($"item-only-api:{api}");
                }

                if (!isStatement)
                {
                    reasons.Add($"statement-used-as-expr:{api}@line{call.Line}");
                }

                if (RequireArgCount(1) && ArgType(0) != LuaValueType.Int)
                {
                    reasons.Add($"invalid-arg-type:{api}@line{call.Line}");
                }

                return LuaValueType.Void;

            // 1-arg String statements
            case "Msg2Player":
            case "AddNote":
                if (!isStatement)
                {
                    reasons.Add($"statement-used-as-expr:{api}@line{call.Line}");
                }

                if (RequireArgCount(1))
                {
                    LuaValueType mt = ArgType(0);
                    if (mt is not (LuaValueType.String or LuaValueType.Int))
                    {
                        reasons.Add($"invalid-arg-type:{api}({mt})@line{call.Line}");
                    }
                }

                return LuaValueType.Void;

            // 2-arg Int statements
            case "SetTask":
            case "SetTaskTemp":
            case "SetGlbValue":
                if (!isStatement)
                {
                    reasons.Add($"statement-used-as-expr:{api}@line{call.Line}");
                }

                if (RequireArgCount(2))
                {
                    if (ArgType(0) != LuaValueType.Int || ArgType(1) != LuaValueType.Int)
                    {
                        reasons.Add($"invalid-arg-type:{api}@line{call.Line}");
                    }
                }

                return LuaValueType.Void;

            // AddMagic(skillId[, level])
            case "AddMagic":
                if (!isStatement)
                {
                    reasons.Add($"statement-used-as-expr:{api}@line{call.Line}");
                }

                if (RequireArgRange(1, 2))
                {
                    for (int i = 0; i < args.Count; i++)
                    {
                        if (ArgType(i) != LuaValueType.Int)
                        {
                            reasons.Add($"invalid-arg-type:AddMagic@line{call.Line}");
                        }
                    }
                }

                return LuaValueType.Void;

            // AddSkillState(skillId, level, stateType, duration[, 0])
            case "AddSkillState":
                if (!isStatement)
                {
                    reasons.Add($"statement-used-as-expr:{api}@line{call.Line}");
                }

                if (RequireArgRange(4, 5))
                {
                    for (int i = 0; i < 4; i++)
                    {
                        if (ArgType(i) != LuaValueType.Int)
                        {
                            reasons.Add($"invalid-arg-type:AddSkillState@line{call.Line}");
                        }
                    }

                    if (args.Count == 5 && (!TryEvalConstantInt(args[4], out int noClear) || noClear != 0))
                    {
                        reasons.Add($"unsupported-addskillstate-noclear@line{call.Line}");
                    }
                }

                return LuaValueType.Void;

            // SetRevPos(revId) or SetRevPos(mapId, revId)
            case "SetRevPos":
                if (!isStatement)
                {
                    reasons.Add($"statement-used-as-expr:{api}@line{call.Line}");
                }

                if (RequireArgRange(1, 2))
                {
                    for (int i = 0; i < args.Count; i++)
                    {
                        if (ArgType(i) != LuaValueType.Int)
                        {
                            reasons.Add($"invalid-arg-type:SetRevPos@line{call.Line}");
                        }
                    }
                }

                return LuaValueType.Void;

            // OpenBox()
            case "OpenBox":
                if (!isStatement)
                {
                    reasons.Add($"statement-used-as-expr:{api}@line{call.Line}");
                }

                RequireArgCount(0);
                return LuaValueType.Void;

            // ShowLadder(count, id1, ...)
            case "ShowLadder":
                if (!isStatement)
                {
                    reasons.Add($"statement-used-as-expr:{api}@line{call.Line}");
                }

                if (args.Count < 2 || !TryEvalConstantInt(args[0], out int ladderCount) || ladderCount <= 0 || ladderCount != args.Count - 1)
                {
                    reasons.Add($"invalid-showladder-args@line{call.Line}");
                }
                else
                {
                    for (int i = 1; i < args.Count; i++)
                    {
                        if (ArgType(i) != LuaValueType.Int)
                        {
                            reasons.Add($"invalid-arg-type:ShowLadder@line{call.Line}");
                        }
                    }
                }

                return LuaValueType.Void;

            // Sale(shopId[, currency[, scale]])
            case "Sale":
                if (!isStatement)
                {
                    reasons.Add($"statement-used-as-expr:{api}@line{call.Line}");
                }

                if (RequireArgRange(1, 3))
                {
                    for (int i = 0; i < args.Count; i++)
                    {
                        if (ArgType(i) != LuaValueType.Int)
                        {
                            reasons.Add($"invalid-arg-type:Sale@line{call.Line}");
                        }
                    }
                }

                return LuaValueType.Void;

            // Async teleport & portal APIs
            case "NewWorld":
                if (!isStatement)
                {
                    reasons.Add($"statement-used-as-expr:{api}@line{call.Line}");
                }

                if (RequireArgCount(3))
                {
                    if (ArgType(0) != LuaValueType.Int || ArgType(1) != LuaValueType.Int || ArgType(2) != LuaValueType.Int)
                    {
                        reasons.Add($"invalid-arg-type:NewWorld@line{call.Line}");
                    }
                }

                localAsync = true;
                return LuaValueType.Void;

            case "SetPos":
                if (!isStatement)
                {
                    reasons.Add($"statement-used-as-expr:{api}@line{call.Line}");
                }

                if (RequireArgCount(2))
                {
                    if (ArgType(0) != LuaValueType.Int || ArgType(1) != LuaValueType.Int)
                    {
                        reasons.Add($"invalid-arg-type:SetPos@line{call.Line}");
                    }
                }

                localAsync = true;
                return LuaValueType.Void;

            case "ReturnFromPortal":
                if (!isStatement)
                {
                    reasons.Add($"statement-used-as-expr:{api}@line{call.Line}");
                }

                RequireArgCount(0);
                localAsync = true;
                return LuaValueType.Void;

            // Talk(count, callbackStr, page1, page2, ...)
            case "Talk":
                if (!isStatement)
                {
                    reasons.Add($"statement-used-as-expr:Talk@line{call.Line}");
                }

                ValidateTalkCall(call, kind, functionsByName, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref localAsync, reasons);
                return LuaValueType.Void;

            // Say(prompt, count, opt1, ...) or Say(prompt, count, constArray)
            case "Say":
                if (!isStatement)
                {
                    reasons.Add($"statement-used-as-expr:Say@line{call.Line}");
                }

                ValidateSayCall(call, kind, functionsByName, topLevelConsts, localTypes, assigned, constArrays, fnIsAsync, fnReturnTypes, ref localAsync, reasons);
                return LuaValueType.Void;

            default:
                reasons.Add($"api:{api}");
                return LuaValueType.Void;
        }
        }
        finally
        {
            isAsync = localAsync;
        }
    }

    private void ValidateTalkCall(
        LuaCallExpr call,
        GeneratedScriptKind kind,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        IReadOnlyDictionary<string, LuaValueType> topLevelConsts,
        IReadOnlyDictionary<string, LuaValueType> localTypes,
        IReadOnlySet<string> assigned,
        IReadOnlyDictionary<string, bool> fnIsAsync,
        IReadOnlyDictionary<string, LuaValueType> fnReturnTypes,
        ref bool isAsync,
        SortedSet<string> reasons)
    {
        if (call.Arguments.Count < 3)
        {
            reasons.Add($"invalid-talk-arity@line{call.Line}");
            return;
        }

        if (!TryEvalConstantInt(call.Arguments[0], out int count) || count <= 0)
        {
            reasons.Add($"dynamic-talk-count@line{call.Line}");
            return;
        }

        // Validate callback string (empty string or nil = no callback; non-empty must name a 0-arg local function)
        if (call.Arguments[1] is LuaStringExpr cbStr)
        {
            string cbName = cbStr.DecodedUtf8.Trim();
            if (cbName.Length > 0 && !functionsByName.ContainsKey(cbName))
            {
                reasons.Add($"missing-talk-callback:{cbName}");
            }
        }
        else if (call.Arguments[1] is not LuaNilExpr)
        {
            reasons.Add($"dynamic-talk-callback@line{call.Line}");
        }

        for (int i = 2; i < call.Arguments.Count; i++)
        {
            LuaValueType pt = ValidateExpr(call.Arguments[i], kind, functionsByName, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
            if (pt is not (LuaValueType.String or LuaValueType.Int))
            {
                reasons.Add($"invalid-talk-page-type:{pt}@line{call.Line}");
            }
        }
    }

    private void ValidateSayCall(
        LuaCallExpr call,
        GeneratedScriptKind kind,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        IReadOnlyDictionary<string, LuaValueType> topLevelConsts,
        IReadOnlyDictionary<string, LuaValueType> localTypes,
        IReadOnlySet<string> assigned,
        IReadOnlyDictionary<string, LuaTableLiteralExpr>? constArrays,
        IReadOnlyDictionary<string, bool> fnIsAsync,
        IReadOnlyDictionary<string, LuaValueType> fnReturnTypes,
        ref bool isAsync,
        SortedSet<string> reasons)
    {
        if (call.Arguments.Count < 2)
        {
            reasons.Add($"invalid-say-arity@line{call.Line}");
            return;
        }

        LuaValueType promptType = ValidateExpr(call.Arguments[0], kind, functionsByName, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
        if (promptType is not (LuaValueType.String or LuaValueType.Int))
        {
            reasons.Add($"invalid-say-prompt-type:{promptType}@line{call.Line}");
            return;
        }

        // Static Say(prompt, 0)
        if (call.Arguments.Count == 2)
        {
            if (!TryEvalConstantInt(call.Arguments[1], out int zeroCount) || zeroCount != 0)
            {
                reasons.Add($"invalid-say-2arg-count@line{call.Line}");
            }

            return;
        }

        // Say(prompt, count, "opt1/cb1", "opt2/cb2", ...)
        if (TryEvalConstantInt(call.Arguments[1], out int inlineCount) &&
            inlineCount > 0 &&
            call.Arguments.Count == 2 + inlineCount)
        {
            for (int i = 2; i < call.Arguments.Count; i++)
            {
                if (!TryEvalConstantString(call.Arguments[i], out string? optStr) || optStr is null)
                {
                    reasons.Add($"dynamic-say-option@line{call.Line}");
                    return;
                }

                ValidateSayOptionString(optStr, functionsByName, reasons);
            }

            return;
        }

        // Say(prompt, getn(tab), tab) where tab is a constant string array literal
        if (call.Arguments.Count == 3 &&
            call.Arguments[2] is LuaIdentifierExpr tabIdent &&
            constArrays is not null &&
            constArrays.TryGetValue(tabIdent.Name, out LuaTableLiteralExpr? tabLit))
        {
            LuaValueType countType = ValidateExpr(call.Arguments[1], kind, functionsByName, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
            if (countType != LuaValueType.Int)
            {
                reasons.Add($"invalid-say-table-count@line{call.Line}");
                return;
            }

            foreach (LuaExpression el in tabLit.Elements)
            {
                if (!TryEvalConstantString(el, out string? optStr) || optStr is null)
                {
                    reasons.Add($"dynamic-say-table-option@line{call.Line}");
                    return;
                }

                ValidateSayOptionString(optStr, functionsByName, reasons);
            }

            return;
        }

        reasons.Add($"unsupported-say-form@line{call.Line}");
    }

    private static void ValidateSayOptionString(
        string optionSpec,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        SortedSet<string> reasons)
    {
        int slashIdx = optionSpec.LastIndexOf('/');
        if (slashIdx < 0)
        {
            // No callback -> closes dialog cleanly
            return;
        }

        string cbName = optionSpec[(slashIdx + 1)..].Trim();
        if (cbName.Length == 0 ||
            cbName.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            cbName.Equals("OnCancel", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!functionsByName.ContainsKey(cbName))
        {
            reasons.Add($"missing-say-callback:{cbName}");
        }
    }

    public static bool TryEvalConstantInt(LuaExpression expr, out int value)
    {
        switch (expr)
        {
            case LuaNumberExpr num:
                if (Math.Abs(num.Value - Math.Truncate(num.Value)) < 1e-9 &&
                    num.Value is >= int.MinValue and <= int.MaxValue)
                {
                    value = (int)Math.Truncate(num.Value);
                    return true;
                }

                value = 0;
                return false;

            case LuaUnaryExpr { Operator: LuaUnaryOp.Negate } un when TryEvalConstantInt(un.Operand, out int inner):
                value = -inner;
                return true;

            case LuaBinaryExpr bin when TryEvalConstantInt(bin.Left, out int l) && TryEvalConstantInt(bin.Right, out int r):
                switch (bin.Operator)
                {
                    case LuaBinaryOp.Add:
                        value = unchecked(l + r);
                        return true;
                    case LuaBinaryOp.Sub:
                        value = unchecked(l - r);
                        return true;
                    case LuaBinaryOp.Mul:
                        value = unchecked(l * r);
                        return true;
                    case LuaBinaryOp.Div when r != 0 && l % r == 0:
                        value = l / r;
                        return true;
                    case LuaBinaryOp.Mod when r != 0:
                        value = l % r;
                        return true;
                    case LuaBinaryOp.Pow when r is >= 0 and <= 30:
                        long p = 1;
                        for (int k = 0; k < r; k++)
                        {
                            p *= l;
                            if (p is < int.MinValue or > int.MaxValue)
                            {
                                value = 0;
                                return false;
                            }
                        }

                        value = (int)p;
                        return true;
                }

                value = 0;
                return false;

            default:
                value = 0;
                return false;
        }
    }

    public static bool TryEvalConstantString(LuaExpression expr, out string? value)
    {
        switch (expr)
        {
            case LuaStringExpr str:
                value = str.DecodedUtf8;
                return true;
            case LuaBinaryExpr { Operator: LuaBinaryOp.Concat } bin
                when TryEvalConstantString(bin.Left, out string? l) && TryEvalConstantString(bin.Right, out string? r):
                value = l + r;
                return true;
            default:
                value = null;
                return false;
        }
    }

    private static bool TryValidateConstantExpr(
        LuaExpression expr,
        IReadOnlyDictionary<string, LuaValueType> existingConsts,
        out LuaValueType type,
        out string? error)
    {
        error = null;
        if (TryEvalConstantInt(expr, out _))
        {
            type = LuaValueType.Int;
            return true;
        }

        if (TryEvalConstantString(expr, out _))
        {
            type = LuaValueType.String;
            return true;
        }

        if (expr is LuaIdentifierExpr ident && existingConsts.TryGetValue(ident.Name, out LuaValueType idType))
        {
            type = idType;
            return true;
        }

        if (expr is LuaTableLiteralExpr tbl && tbl.Elements.Count > 0)
        {
            LuaValueType elemType = LuaValueType.Void;
            for (int i = 0; i < tbl.Elements.Count; i++)
            {
                if (!TryValidateConstantExpr(tbl.Elements[i], existingConsts, out LuaValueType et, out error))
                {
                    type = LuaValueType.Void;
                    return false;
                }

                if (i == 0)
                {
                    elemType = et;
                }
                else if (et != elemType)
                {
                    type = LuaValueType.Void;
                    error = $"heterogeneous-toplevel-table@line{tbl.Line}";
                    return false;
                }
            }

            if (elemType == LuaValueType.String)
            {
                type = LuaValueType.StringArray;
                return true;
            }

            if (elemType == LuaValueType.Int)
            {
                type = LuaValueType.IntArray;
                return true;
            }
        }

        type = LuaValueType.Void;
        error = $"non-const-toplevel-expr@line{expr.Line}";
        return false;
    }

    private static LuaValueType FailExpr(string reason, SortedSet<string> reasons)
    {
        reasons.Add(reason);
        return LuaValueType.Void;
    }

    private static void CollectLocalCallEdges(
        IReadOnlyList<LuaStatement> stmts,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        HashSet<string> edges)
    {
        foreach (LuaStatement stmt in stmts)
        {
            switch (stmt)
            {
                case LuaCallStmt cs:
                    CollectExprCalls(cs.Call, functionsByName, edges);
                    break;
                case LuaLocalDeclStmt ld when ld.Initializer is not null:
                    CollectExprCalls(ld.Initializer, functionsByName, edges);
                    break;
                case LuaAssignStmt asg:
                    CollectExprCalls(asg.Value, functionsByName, edges);
                    break;
                case LuaReturnStmt ret when ret.Value is not null:
                    CollectExprCalls(ret.Value, functionsByName, edges);
                    break;
                case LuaIfStmt ifs:
                    CollectExprCalls(ifs.Condition, functionsByName, edges);
                    CollectLocalCallEdges(ifs.ThenBody, functionsByName, edges);
                    foreach (LuaElseIfBranch ei in ifs.ElseIfBranches)
                    {
                        CollectExprCalls(ei.Condition, functionsByName, edges);
                        CollectLocalCallEdges(ei.Body, functionsByName, edges);
                    }

                    CollectLocalCallEdges(ifs.ElseBody, functionsByName, edges);
                    break;
            }
        }
    }

    private static void CollectExprCalls(
        LuaExpression expr,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        HashSet<string> edges)
    {
        switch (expr)
        {
            case LuaCallExpr call:
                if (functionsByName.ContainsKey(call.CalleeName))
                {
                    edges.Add(call.CalleeName);
                }

                // Also track dialog callback edges for Talk/Say so cycle detection is thorough
                if (call.CalleeName == "Talk" && call.Arguments.Count >= 2 && call.Arguments[1] is LuaStringExpr cbStr)
                {
                    string cb = cbStr.DecodedUtf8.Trim();
                    if (cb.Length > 0 && functionsByName.ContainsKey(cb))
                    {
                        edges.Add(cb);
                    }
                }
                else if (call.CalleeName == "Say" && call.Arguments.Count >= 3)
                {
                    for (int i = 2; i < call.Arguments.Count; i++)
                    {
                        if (TryEvalConstantString(call.Arguments[i], out string? opt) && opt is not null)
                        {
                            int slash = opt.LastIndexOf('/');
                            if (slash >= 0)
                            {
                                string cb = opt[(slash + 1)..].Trim();
                                if (cb.Length > 0 && functionsByName.ContainsKey(cb))
                                {
                                    edges.Add(cb);
                                }
                            }
                        }
                    }
                }

                foreach (LuaExpression arg in call.Arguments)
                {
                    CollectExprCalls(arg, functionsByName, edges);
                }

                break;

            case LuaBinaryExpr bin:
                CollectExprCalls(bin.Left, functionsByName, edges);
                CollectExprCalls(bin.Right, functionsByName, edges);
                break;

            case LuaUnaryExpr un:
                CollectExprCalls(un.Operand, functionsByName, edges);
                break;

            case LuaIndexExpr idx:
                CollectExprCalls(idx.Target, functionsByName, edges);
                CollectExprCalls(idx.Index, functionsByName, edges);
                break;

            case LuaTableLiteralExpr tbl:
                foreach (LuaExpression el in tbl.Elements)
                {
                    CollectExprCalls(el, functionsByName, edges);
                }

                break;
        }
    }

    private static bool HasCycle(IReadOnlyDictionary<string, HashSet<string>> graph)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0=unvisited, 1=visiting, 2=done

        bool Dfs(string node)
        {
            state[node] = 1;
            if (graph.TryGetValue(node, out HashSet<string>? neighbors))
            {
                foreach (string next in neighbors)
                {
                    int nextState = state.GetValueOrDefault(next, 0);
                    if (nextState == 1)
                    {
                        return true;
                    }

                    if (nextState == 0 && Dfs(next))
                    {
                        return true;
                    }
                }
            }

            state[node] = 2;
            return false;
        }

        foreach (string node in graph.Keys)
        {
            if (state.GetValueOrDefault(node, 0) == 0 && Dfs(node))
            {
                return true;
            }
        }

        return false;
    }

    private static List<LuaFunctionDecl> TopologicalOrder(
        IReadOnlyList<LuaFunctionDecl> functions,
        IReadOnlyDictionary<string, HashSet<string>> graph)
    {
        var byName = functions.ToDictionary(static f => f.Name, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<LuaFunctionDecl>(functions.Count);

        void Visit(string name)
        {
            if (!visited.Add(name))
            {
                return;
            }

            if (graph.TryGetValue(name, out HashSet<string>? callees))
            {
                foreach (string callee in callees)
                {
                    if (byName.ContainsKey(callee))
                    {
                        Visit(callee);
                    }
                }
            }

            ordered.Add(byName[name]);
        }

        foreach (LuaFunctionDecl fn in functions)
        {
            Visit(fn.Name);
        }

        return ordered;
    }
}
