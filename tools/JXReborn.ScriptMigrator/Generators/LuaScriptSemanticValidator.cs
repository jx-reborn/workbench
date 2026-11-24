using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using JXReborn.ScriptMigrator.Ast;
using JXReborn.ScriptMigrator.Models;

namespace JXReborn.ScriptMigrator.Generators;

/// <summary>
/// Strictly fail-closed static semantic validator and monomorphic type-checker for linked Lua 4.0 scripts.
/// Verifies zero unresolved includes, definite variable assignment, strict C-API allowlist per script kind,
/// exact item catalog existence (<see cref="PackItemCatalogIndex"/>), valid static/parameterized dialog callbacks,
/// acyclic call graphs, bounded loops, and exact Lua 4.0 fallthrough-<c>nil</c> / float-division semantics.
/// </summary>
public sealed class LuaScriptSemanticValidator
{
    private readonly IReadOnlyDictionary<int, string> _eventItemCodeByDetailId;
    private readonly PackItemCatalogIndex? _itemCatalogIndex;
    private IReadOnlyDictionary<string, LuaExpression>? _currentTopLevelConstExprs;

    public LuaScriptSemanticValidator(
        IReadOnlyDictionary<int, string> eventItemCodeByDetailId,
        PackItemCatalogIndex? itemCatalogIndex = null)
    {
        _eventItemCodeByDetailId = eventItemCodeByDetailId ?? throw new ArgumentNullException(nameof(eventItemCodeByDetailId));
        _itemCatalogIndex = itemCatalogIndex;
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
        _currentTopLevelConstExprs = topLevelConstExprs;
        foreach (LuaStatement stmt in program.TopLevelStatements)
        {
            if (stmt is LuaUnsupportedStmt unsupStmt)
            {
                reasons.Add(unsupStmt.Reason);
                continue;
            }

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

        // Reject any reachable function containing an isolated parser failure node first
        foreach (LuaFunctionDecl fn in program.Functions)
        {
            CollectUnsupportedNodes(fn.Body, reasons);
        }

        if (reasons.Count > 0)
        {
            return (null, reasons.ToArray());
        }

        // 3. Build call graph & detect recursion among local/linked functions.
        var localCalls = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (LuaFunctionDecl fn in program.Functions)
        {
            var edges = new HashSet<string>(StringComparer.Ordinal);
            CollectLocalCallEdges(fn.Body, functionsByName, topLevelConstExprs, edges);
            localCalls[fn.Name] = edges;
        }

        if (HasCycle(localCalls))
        {
            reasons.Add("unsupported-syntax:recursive-local-functions");
            return (null, reasons.ToArray());
        }

        // Order functions bottom-up (callees before callers) via topological sort; reverse is top-down (callers before callees).
        List<LuaFunctionDecl> orderedFunctions = TopologicalOrder(program.Functions, localCalls);

        // 4. Infer monomorphic parameter types for helper functions from call sites & dialog callbacks.
        var functionParamTypes = new Dictionary<string, IReadOnlyList<LuaValueType>>(StringComparer.Ordinal);
        if (kind == GeneratedScriptKind.Item && entryFn.Parameters.Count == 1)
        {
            functionParamTypes[expectedEntry] = [LuaValueType.Int];
        }
        else
        {
            functionParamTypes[expectedEntry] = [];
        }

        foreach (LuaFunctionDecl fn in program.Functions)
        {
            if (!string.Equals(fn.Name, expectedEntry, StringComparison.Ordinal) && fn.Parameters.Count == 0)
            {
                functionParamTypes[fn.Name] = [];
            }
        }

        InferHelperParameterTypesTopDown(
            orderedFunctions,
            expectedEntry,
            functionsByName,
            topLevelConsts,
            topLevelConstExprs,
            functionParamTypes,
            reasons);

        foreach (LuaFunctionDecl fn in program.Functions)
        {
            if (!string.Equals(fn.Name, expectedEntry, StringComparison.Ordinal) &&
                !functionParamTypes.ContainsKey(fn.Name))
            {
                reasons.Add($"unbound-helper-params:{fn.Name}({fn.Parameters.Count})");
            }
        }

        if (reasons.Count > 0)
        {
            return (null, reasons.ToArray());
        }

        // 5. Validate each function bottom-up (callees before callers so return types & async flags propagate).
        var fnLocalTypes = new Dictionary<string, IReadOnlyDictionary<string, LuaValueType>>(StringComparer.Ordinal);
        var fnIsAsync = new Dictionary<string, bool>(StringComparer.Ordinal);
        var fnReturnTypes = new Dictionary<string, LuaValueType>(StringComparer.Ordinal);
        var fnReturnTuples = new Dictionary<string, IReadOnlyList<LuaValueType>>(StringComparer.Ordinal);

        foreach (LuaFunctionDecl fn in orderedFunctions)
        {
            bool isEntry = string.Equals(fn.Name, expectedEntry, StringComparison.Ordinal);
            var localTypes = new Dictionary<string, LuaValueType>(StringComparer.Ordinal);
            var assigned = new HashSet<string>(topLevelConsts.Keys, StringComparer.Ordinal)
            {
                "SubWorld",
            };

            // Bind typed function parameters
            IReadOnlyList<LuaValueType> paramTypes = functionParamTypes[fn.Name];
            for (int p = 0; p < paramTypes.Count && p < fn.Parameters.Count; p++)
            {
                string pName = fn.Parameters[p];
                localTypes[pName] = paramTypes[p];
                assigned.Add(pName);
            }

            // Pre-infer empty list element types (`local tb = {}` + `tinsert(tb, ...)` / `tb[i] = ...`)
            Dictionary<string, LuaValueType> emptyListHints = InferEmptyTableListTypes(fn.Body);

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
            IReadOnlyList<LuaValueType>? returnTuple = null;

            ValidateBlock(
                fn.Body,
                kind,
                isEntry,
                fn.Name,
                loopDepth: 0,
                functionsByName,
                functionParamTypes,
                topLevelConsts,
                localTypes,
                emptyListHints,
                assigned,
                constArrays,
                fnIsAsync,
                fnReturnTypes,
                fnReturnTuples,
                ref isAsync,
                ref returnType,
                ref returnTuple,
                reasons);

            // If a helper function returns Int on >= 1 branch AND can fall off the end without returning,
            // Lua 4.0 returns `nil` on fallthrough -> widen Int to NullableInt (`int?`) for 100% nil-equality parity!
            if (!isEntry && returnType == LuaValueType.Int && returnTuple is null && !AllPathsReturn(fn.Body))
            {
                returnType = LuaValueType.NullableInt;
            }

            if (kind == GeneratedScriptKind.Monster && isAsync)
            {
                reasons.Add($"async-not-supported-in-monster-ondeath:{fn.Name}");
            }

            // Remove read-only built-in `SubWorld` if not shadowed, and remove parameters from hoisted locals
            for (int p = 0; p < paramTypes.Count && p < fn.Parameters.Count; p++)
            {
                localTypes.Remove(fn.Parameters[p]);
            }

            fnLocalTypes[fn.Name] = localTypes;
            fnIsAsync[fn.Name] = isAsync;
            fnReturnTypes[fn.Name] = returnType;
            if (returnTuple is not null)
            {
                fnReturnTuples[fn.Name] = returnTuple;
            }
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
                FunctionReturnTypes: fnReturnTypes,
                FunctionReturnTuples: fnReturnTuples),
            []);
    }

    private void InferHelperParameterTypesTopDown(
        List<LuaFunctionDecl> bottomUpFunctions,
        string expectedEntry,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        IReadOnlyDictionary<string, LuaValueType> topLevelConsts,
        IReadOnlyDictionary<string, LuaExpression> topLevelConstExprs,
        Dictionary<string, IReadOnlyList<LuaValueType>> functionParamTypes,
        SortedSet<string> reasons)
    {
        void BindParams(string calleeName, IReadOnlyList<LuaValueType> candidateTypes)
        {
            if (string.Equals(calleeName, expectedEntry, StringComparison.Ordinal) ||
                !functionsByName.TryGetValue(calleeName, out LuaFunctionDecl? targetFn))
            {
                return;
            }

            if (targetFn.Parameters.Count == 0)
            {
                return;
            }

            if (candidateTypes.Count != targetFn.Parameters.Count)
            {
                reasons.Add($"invalid-helper-arity:{calleeName}({candidateTypes.Count}!={targetFn.Parameters.Count})");
                return;
            }

            var normalized = new LuaValueType[candidateTypes.Count];
            for (int i = 0; i < candidateTypes.Count; i++)
            {
                normalized[i] = candidateTypes[i] == LuaValueType.NullableInt ? LuaValueType.Int : candidateTypes[i];
                if (normalized[i] is LuaValueType.Void or LuaValueType.Nil)
                {
                    return;
                }
            }

            if (functionParamTypes.TryGetValue(calleeName, out IReadOnlyList<LuaValueType>? existing))
            {
                for (int i = 0; i < existing.Count; i++)
                {
                    if (existing[i] != normalized[i])
                    {
                        reasons.Add($"polymorphic-helper-params:{calleeName}");
                        return;
                    }
                }
            }
            else
            {
                functionParamTypes[calleeName] = normalized;
            }
        }

        // Fixed-point propagation down the acyclic call graph (at most 3 passes needed in practice)
        for (int pass = 0; pass < 3; pass++)
        {
            for (int idx = bottomUpFunctions.Count - 1; idx >= 0; idx--)
            {
                LuaFunctionDecl fn = bottomUpFunctions[idx];
                var scopeTypes = new Dictionary<string, LuaValueType>(topLevelConsts, StringComparer.Ordinal)
                {
                    ["SubWorld"] = LuaValueType.Int,
                };

                if (functionParamTypes.TryGetValue(fn.Name, out IReadOnlyList<LuaValueType>? myParams))
                {
                    for (int p = 0; p < myParams.Count && p < fn.Parameters.Count; p++)
                    {
                        scopeTypes[fn.Parameters[p]] = myParams[p];
                    }
                }

                ScanBlockForParamBindings(fn.Body, scopeTypes, topLevelConstExprs, BindParams);
            }
        }
    }

    private static void ScanBlockForParamBindings(
        IReadOnlyList<LuaStatement> stmts,
        Dictionary<string, LuaValueType> scopeTypes,
        IReadOnlyDictionary<string, LuaExpression> topLevelConstExprs,
        Action<string, IReadOnlyList<LuaValueType>> bindParams)
    {
        foreach (LuaStatement stmt in stmts)
        {
            switch (stmt)
            {
                case LuaLocalDeclStmt ld:
                    if (ld.Initializer is not null)
                    {
                        LuaValueType t = QuickInferExprType(ld.Initializer, scopeTypes, bindParams);
                        if (t != LuaValueType.Void)
                        {
                            scopeTypes[ld.Name] = t;
                        }
                    }

                    break;

                case LuaMultiLocalDeclStmt mld:
                    if (mld.Initializers.Count == 1 && mld.Initializers[0] is LuaCallExpr mc &&
                        mc.CalleeName is "GetWorldPos" or "GetPlayerRev")
                    {
                        foreach (string n in mld.Names)
                        {
                            if (n != "_") scopeTypes[n] = LuaValueType.Int;
                        }
                    }
                    else
                    {
                        for (int i = 0; i < mld.Initializers.Count; i++)
                        {
                            LuaValueType t = QuickInferExprType(mld.Initializers[i], scopeTypes, bindParams);
                            if (i < mld.Names.Count && mld.Names[i] != "_" && t != LuaValueType.Void)
                            {
                                scopeTypes[mld.Names[i]] = t;
                            }
                        }
                    }

                    break;

                case LuaAssignStmt asg:
                {
                    LuaValueType t = QuickInferExprType(asg.Value, scopeTypes, bindParams);
                    if (t != LuaValueType.Void)
                    {
                        scopeTypes[asg.TargetName] = t;
                    }

                    break;
                }

                case LuaMultiAssignStmt masg:
                    if (masg.Values.Count == 1 && masg.Values[0] is LuaCallExpr mac &&
                        mac.CalleeName is "GetWorldPos" or "GetPlayerRev")
                    {
                        foreach (string n in masg.TargetNames)
                        {
                            if (n != "_") scopeTypes[n] = LuaValueType.Int;
                        }
                    }
                    else
                    {
                        for (int i = 0; i < masg.Values.Count; i++)
                        {
                            LuaValueType t = QuickInferExprType(masg.Values[i], scopeTypes, bindParams);
                            if (i < masg.TargetNames.Count && masg.TargetNames[i] != "_" && t != LuaValueType.Void)
                            {
                                scopeTypes[masg.TargetNames[i]] = t;
                            }
                        }
                    }

                    break;

                case LuaIndexAssignStmt iasg:
                    _ = QuickInferExprType(iasg.Index, scopeTypes, bindParams);
                    _ = QuickInferExprType(iasg.Value, scopeTypes, bindParams);
                    break;

                case LuaCallStmt cs:
                    _ = QuickInferExprType(cs.Call, scopeTypes, bindParams);
                    break;

                case LuaReturnStmt ret:
                    if (ret.Value is not null) _ = QuickInferExprType(ret.Value, scopeTypes, bindParams);
                    if (ret.ExtraValues is not null)
                    {
                        foreach (LuaExpression ev in ret.ExtraValues) _ = QuickInferExprType(ev, scopeTypes, bindParams);
                    }

                    break;

                case LuaIfStmt ifs:
                    _ = QuickInferExprType(ifs.Condition, scopeTypes, bindParams);
                    ScanBlockForParamBindings(ifs.ThenBody, new Dictionary<string, LuaValueType>(scopeTypes, StringComparer.Ordinal), topLevelConstExprs, bindParams);
                    foreach (LuaElseIfBranch ei in ifs.ElseIfBranches)
                    {
                        _ = QuickInferExprType(ei.Condition, scopeTypes, bindParams);
                        ScanBlockForParamBindings(ei.Body, new Dictionary<string, LuaValueType>(scopeTypes, StringComparer.Ordinal), topLevelConstExprs, bindParams);
                    }

                    ScanBlockForParamBindings(ifs.ElseBody, new Dictionary<string, LuaValueType>(scopeTypes, StringComparer.Ordinal), topLevelConstExprs, bindParams);
                    break;

                case LuaNumericForStmt nf:
                    _ = QuickInferExprType(nf.Start, scopeTypes, bindParams);
                    _ = QuickInferExprType(nf.Limit, scopeTypes, bindParams);
                    if (nf.Step is not null) _ = QuickInferExprType(nf.Step, scopeTypes, bindParams);
                    var forScope = new Dictionary<string, LuaValueType>(scopeTypes, StringComparer.Ordinal)
                    {
                        [nf.IteratorName] = LuaValueType.Int,
                    };
                    ScanBlockForParamBindings(nf.Body, forScope, topLevelConstExprs, bindParams);
                    break;

                case LuaWhileStmt ws:
                    _ = QuickInferExprType(ws.Condition, scopeTypes, bindParams);
                    ScanBlockForParamBindings(ws.Body, new Dictionary<string, LuaValueType>(scopeTypes, StringComparer.Ordinal), topLevelConstExprs, bindParams);
                    break;

                case LuaDoBlockStmt db:
                    ScanBlockForParamBindings(db.Body, new Dictionary<string, LuaValueType>(scopeTypes, StringComparer.Ordinal), topLevelConstExprs, bindParams);
                    break;
            }
        }
    }

    private static LuaValueType QuickInferExprType(
        LuaExpression expr,
        IReadOnlyDictionary<string, LuaValueType> scopeTypes,
        Action<string, IReadOnlyList<LuaValueType>> bindParams)
    {
        switch (expr)
        {
            case LuaNumberExpr num:
                return Math.Abs(num.Value - Math.Truncate(num.Value)) > 1e-9 ? LuaValueType.Number : LuaValueType.Int;
            case LuaStringExpr str:
                TryBindParameterizedCallbackString(str.DecodedUtf8, bindParams);
                return LuaValueType.String;
            case LuaIdentifierExpr id:
                return scopeTypes.GetValueOrDefault(id.Name, LuaValueType.Int);
            case LuaIndexExpr idx:
                _ = QuickInferExprType(idx.Index, scopeTypes, bindParams);
                return QuickInferExprType(idx.Target, scopeTypes, bindParams) is LuaValueType.StringArray or LuaValueType.StringList
                    ? LuaValueType.String
                    : LuaValueType.Int;
            case LuaUnaryExpr un:
                LuaValueType ut = QuickInferExprType(un.Operand, scopeTypes, bindParams);
                return un.Operator == LuaUnaryOp.Not ? LuaValueType.Bool : ut;
            case LuaBinaryExpr bin:
                LuaValueType lt = QuickInferExprType(bin.Left, scopeTypes, bindParams);
                LuaValueType rt = QuickInferExprType(bin.Right, scopeTypes, bindParams);
                if (bin.Operator == LuaBinaryOp.Concat) return LuaValueType.String;
                if (bin.Operator is LuaBinaryOp.Or or LuaBinaryOp.And or LuaBinaryOp.Eq or LuaBinaryOp.Neq or LuaBinaryOp.Lt or LuaBinaryOp.Le or LuaBinaryOp.Gt or LuaBinaryOp.Ge)
                {
                    return LuaValueType.Bool;
                }

                if (bin.Operator == LuaBinaryOp.Div && !TryEvalConstantInt(bin, out _))
                {
                    return LuaValueType.Number;
                }

                return lt == LuaValueType.Number || rt == LuaValueType.Number ? LuaValueType.Number : LuaValueType.Int;
            case LuaTableLiteralExpr tbl:
                foreach (LuaExpression e in tbl.Elements) _ = QuickInferExprType(e, scopeTypes, bindParams);
                return tbl.Elements.Count > 0 && QuickInferExprType(tbl.Elements[0], scopeTypes, bindParams) == LuaValueType.String
                    ? LuaValueType.StringArray
                    : LuaValueType.IntArray;
            case LuaCallExpr call:
                if (call.CalleeName == "AskClientForNumber" && call.Arguments.Count >= 1 && call.Arguments[0] is LuaStringExpr askCb)
                {
                    bindParams(askCb.DecodedUtf8.Trim(), [LuaValueType.Int]);
                }

                var argTypes = new LuaValueType[call.Arguments.Count];
                for (int i = 0; i < call.Arguments.Count; i++)
                {
                    argTypes[i] = QuickInferExprType(call.Arguments[i], scopeTypes, bindParams);
                }

                bindParams(call.CalleeName, argTypes);

                return call.CalleeName switch
                {
                    "GetName" or "GetFaction" or "GetLastAddFaction" or "tostring" or "strsub" or "strlower" or "strupper" or "format" or "GetLocalDate" or "date" or "GetNpcName" or "NpcName2Replace" => LuaValueType.String,
                    _ => LuaValueType.Int,
                };
            default:
                return LuaValueType.Int;
        }
    }

    private static void TryBindParameterizedCallbackString(
        string text,
        Action<string, IReadOnlyList<LuaValueType>> bindParams)
    {
        int slash = text.LastIndexOf('/');
        string spec = slash >= 0 ? text[(slash + 1)..].Trim() : text.Trim();
        if (!spec.StartsWith('#'))
        {
            return;
        }

        spec = spec[1..].TrimStart();
        int lParen = spec.IndexOf('(');
        int rParen = spec.LastIndexOf(')');
        if (lParen <= 0 || rParen <= lParen)
        {
            return;
        }

        string fnName = spec[..lParen].Trim();
        string inner = spec[(lParen + 1)..rParen].Trim();
        if (inner.Length == 0)
        {
            bindParams(fnName, []);
            return;
        }

        string[] parts = inner.Split(',');
        var types = new LuaValueType[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i].Trim();
            types[i] = p.StartsWith('"') || p.StartsWith('\'') || p.Contains("%s", StringComparison.Ordinal)
                ? LuaValueType.String
                : LuaValueType.Int;
        }

        bindParams(fnName, types);
    }

    private static Dictionary<string, LuaValueType> InferEmptyTableListTypes(IReadOnlyList<LuaStatement> stmts)
    {
        var hints = new Dictionary<string, LuaValueType>(StringComparer.Ordinal);
        void Walk(IReadOnlyList<LuaStatement> block)
        {
            foreach (LuaStatement s in block)
            {
                switch (s)
                {
                    case LuaCallStmt { Call: { CalleeName: "tinsert", Arguments.Count: >= 2 } tc }
                        when tc.Arguments[0] is LuaIdentifierExpr id:
                    {
                        LuaExpression valArg = tc.Arguments.Count == 2 ? tc.Arguments[1] : tc.Arguments[2];
                        LuaValueType elemGuess = valArg is LuaStringExpr or LuaBinaryExpr { Operator: LuaBinaryOp.Concat } or LuaCallExpr { CalleeName: "format" or "tostring" or "strsub" }
                            ? LuaValueType.StringList
                            : LuaValueType.IntList;
                        hints.TryAdd(id.Name, elemGuess);
                        break;
                    }
                    case LuaIndexAssignStmt iasg:
                    {
                        LuaValueType elemGuess = iasg.Value is LuaStringExpr or LuaBinaryExpr { Operator: LuaBinaryOp.Concat } or LuaCallExpr { CalleeName: "format" or "tostring" or "strsub" }
                            ? LuaValueType.StringList
                            : LuaValueType.IntList;
                        hints.TryAdd(iasg.TargetName, elemGuess);
                        break;
                    }
                    case LuaIfStmt ifs:
                        Walk(ifs.ThenBody);
                        foreach (LuaElseIfBranch ei in ifs.ElseIfBranches) Walk(ei.Body);
                        Walk(ifs.ElseBody);
                        break;
                    case LuaNumericForStmt nf:
                        Walk(nf.Body);
                        break;
                    case LuaWhileStmt ws:
                        Walk(ws.Body);
                        break;
                    case LuaDoBlockStmt db:
                        Walk(db.Body);
                        break;
                }
            }
        }

        Walk(stmts);
        return hints;
    }

    private void ValidateBlock(
        IReadOnlyList<LuaStatement> statements,
        GeneratedScriptKind kind,
        bool isEntry,
        string fnName,
        int loopDepth,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        IReadOnlyDictionary<string, IReadOnlyList<LuaValueType>> functionParamTypes,
        IReadOnlyDictionary<string, LuaValueType> topLevelConsts,
        Dictionary<string, LuaValueType> localTypes,
        IReadOnlyDictionary<string, LuaValueType> emptyListHints,
        HashSet<string> assigned,
        Dictionary<string, LuaTableLiteralExpr> constArrays,
        IReadOnlyDictionary<string, bool> fnIsAsync,
        IReadOnlyDictionary<string, LuaValueType> fnReturnTypes,
        IReadOnlyDictionary<string, IReadOnlyList<LuaValueType>> fnReturnTuples,
        ref bool isAsync,
        ref LuaValueType returnType,
        ref IReadOnlyList<LuaValueType>? returnTuple,
        SortedSet<string> reasons)
    {
        for (int i = 0; i < statements.Count; i++)
        {
            LuaStatement stmt = statements[i];
            switch (stmt)
            {
                case LuaUnsupportedStmt unsup:
                    reasons.Add(unsup.Reason);
                    break;

                case LuaLocalDeclStmt localDecl:
                {
                    if (localDecl.Name == "_")
                    {
                        if (localDecl.Initializer is not null)
                        {
                            _ = ValidateExpr(localDecl.Initializer, kind, functionsByName, functionParamTypes, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                        }

                        break;
                    }

                    if (topLevelConsts.ContainsKey(localDecl.Name))
                    {
                        reasons.Add($"shadow-toplevel-const:{localDecl.Name}");
                        break;
                    }

                    if (localDecl.Initializer is null)
                    {
                        // Uninitialized `local x;` — definite assignment is enforced on read!
                        constArrays.Remove(localDecl.Name);
                        break;
                    }

                    LuaValueType rhsType;
                    if (localDecl.Initializer is LuaTableLiteralExpr { Elements.Count: 0 })
                    {
                        if (!emptyListHints.TryGetValue(localDecl.Name, out rhsType))
                        {
                            reasons.Add($"untyped-empty-table:{localDecl.Name}@line{localDecl.Line}");
                            break;
                        }
                    }
                    else
                    {
                        rhsType = ValidateExpr(
                            localDecl.Initializer,
                            kind,
                            functionsByName,
                            functionParamTypes,
                            topLevelConsts,
                            localTypes,
                            assigned,
                            fnIsAsync,
                            fnReturnTypes,
                            ref isAsync,
                            reasons);
                    }

                    if (rhsType == LuaValueType.Void)
                    {
                        reasons.Add($"void-assign:{localDecl.Name}");
                        break;
                    }

                    rhsType = NormalizeStorageType(rhsType);
                    if (localTypes.TryGetValue(localDecl.Name, out LuaValueType existing) && !TryUnifyVarType(existing, rhsType, out rhsType))
                    {
                        reasons.Add($"type-mismatch:{localDecl.Name}({existing}->{rhsType})");
                        break;
                    }

                    localTypes[localDecl.Name] = rhsType;
                    assigned.Add(localDecl.Name);
                    if (localDecl.Initializer is LuaTableLiteralExpr { Elements.Count: > 0 } tbl)
                    {
                        constArrays[localDecl.Name] = tbl;
                    }
                    else
                    {
                        constArrays.Remove(localDecl.Name);
                    }

                    break;
                }

                case LuaMultiLocalDeclStmt multiLocal:
                {
                    ValidateMultiAssignment(
                        multiLocal.Names,
                        multiLocal.Initializers,
                        isLocalDecl: true,
                        multiLocal.Line,
                        kind,
                        functionsByName,
                        functionParamTypes,
                        topLevelConsts,
                        localTypes,
                        assigned,
                        constArrays,
                        fnIsAsync,
                        fnReturnTypes,
                        fnReturnTuples,
                        ref isAsync,
                        reasons);
                    break;
                }

                case LuaAssignStmt assignStmt:
                {
                    if (assignStmt.TargetName == "_")
                    {
                        _ = ValidateExpr(assignStmt.Value, kind, functionsByName, functionParamTypes, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                        break;
                    }

                    if (topLevelConsts.ContainsKey(assignStmt.TargetName))
                    {
                        reasons.Add($"mutate-toplevel-const:{assignStmt.TargetName}");
                        break;
                    }

                    LuaValueType rhsType;
                    if (assignStmt.Value is LuaTableLiteralExpr { Elements.Count: 0 })
                    {
                        if (!emptyListHints.TryGetValue(assignStmt.TargetName, out rhsType))
                        {
                            reasons.Add($"untyped-empty-table:{assignStmt.TargetName}@line{assignStmt.Line}");
                            break;
                        }
                    }
                    else
                    {
                        rhsType = ValidateExpr(
                            assignStmt.Value,
                            kind,
                            functionsByName,
                            functionParamTypes,
                            topLevelConsts,
                            localTypes,
                            assigned,
                            fnIsAsync,
                            fnReturnTypes,
                            ref isAsync,
                            reasons);
                    }

                    if (rhsType == LuaValueType.Void)
                    {
                        reasons.Add($"void-assign:{assignStmt.TargetName}");
                        break;
                    }

                    rhsType = NormalizeStorageType(rhsType);
                    if (localTypes.TryGetValue(assignStmt.TargetName, out LuaValueType existing) && !TryUnifyVarType(existing, rhsType, out rhsType))
                    {
                        reasons.Add($"type-mismatch:{assignStmt.TargetName}({existing}->{rhsType})");
                        break;
                    }

                    localTypes[assignStmt.TargetName] = rhsType;
                    assigned.Add(assignStmt.TargetName);
                    if (assignStmt.Value is LuaTableLiteralExpr { Elements.Count: > 0 } tbl)
                    {
                        constArrays[assignStmt.TargetName] = tbl;
                    }
                    else
                    {
                        constArrays.Remove(assignStmt.TargetName);
                    }

                    break;
                }

                case LuaMultiAssignStmt multiAssign:
                {
                    ValidateMultiAssignment(
                        multiAssign.TargetNames,
                        multiAssign.Values,
                        isLocalDecl: false,
                        multiAssign.Line,
                        kind,
                        functionsByName,
                        functionParamTypes,
                        topLevelConsts,
                        localTypes,
                        assigned,
                        constArrays,
                        fnIsAsync,
                        fnReturnTypes,
                        fnReturnTuples,
                        ref isAsync,
                        reasons);
                    break;
                }

                case LuaIndexAssignStmt idxAssign:
                {
                    if (!assigned.Contains(idxAssign.TargetName) || !localTypes.TryGetValue(idxAssign.TargetName, out LuaValueType listType))
                    {
                        reasons.Add($"undefined-var:{idxAssign.TargetName}");
                        break;
                    }

                    LuaValueType idxType = ValidateExpr(idxAssign.Index, kind, functionsByName, functionParamTypes, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                    LuaValueType valType = ValidateExpr(idxAssign.Value, kind, functionsByName, functionParamTypes, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                    if (!IsIntCompatible(idxType))
                    {
                        reasons.Add($"non-int-index@line{idxAssign.Line}");
                    }

                    if (listType == LuaValueType.StringList && valType is LuaValueType.String or LuaValueType.Int)
                    {
                        constArrays.Remove(idxAssign.TargetName);
                    }
                    else if (listType == LuaValueType.IntList && IsIntCompatible(valType))
                    {
                        constArrays.Remove(idxAssign.TargetName);
                    }
                    else
                    {
                        reasons.Add($"invalid-table-mutation:{idxAssign.TargetName}({listType}<-{valType})@line{idxAssign.Line}");
                    }

                    break;
                }

                case LuaIfStmt ifStmt:
                {
                    LuaValueType condType = ValidateExpr(
                        ifStmt.Condition,
                        kind,
                        functionsByName,
                        functionParamTypes,
                        topLevelConsts,
                        localTypes,
                        assigned,
                        fnIsAsync,
                        fnReturnTypes,
                        ref isAsync,
                        reasons);
                    if (!IsConditionCompatible(condType))
                    {
                        reasons.Add($"non-bool-condition@line{ifStmt.Line}");
                    }

                    var branchAssignedSets = new List<HashSet<string>>();

                    var thenAssigned = new HashSet<string>(assigned, StringComparer.Ordinal);
                    ValidateBlock(ifStmt.ThenBody, kind, isEntry, fnName, loopDepth, functionsByName, functionParamTypes, topLevelConsts, localTypes, emptyListHints, thenAssigned, new Dictionary<string, LuaTableLiteralExpr>(constArrays, StringComparer.Ordinal), fnIsAsync, fnReturnTypes, fnReturnTuples, ref isAsync, ref returnType, ref returnTuple, reasons);
                    branchAssignedSets.Add(thenAssigned);

                    foreach (LuaElseIfBranch ei in ifStmt.ElseIfBranches)
                    {
                        LuaValueType eiCondType = ValidateExpr(
                            ei.Condition,
                            kind,
                            functionsByName,
                            functionParamTypes,
                            topLevelConsts,
                            localTypes,
                            assigned,
                            fnIsAsync,
                            fnReturnTypes,
                            ref isAsync,
                            reasons);
                        if (!IsConditionCompatible(eiCondType))
                        {
                            reasons.Add($"non-bool-condition@line{ei.Line}");
                        }

                        var eiAssigned = new HashSet<string>(assigned, StringComparer.Ordinal);
                        ValidateBlock(ei.Body, kind, isEntry, fnName, loopDepth, functionsByName, functionParamTypes, topLevelConsts, localTypes, emptyListHints, eiAssigned, new Dictionary<string, LuaTableLiteralExpr>(constArrays, StringComparer.Ordinal), fnIsAsync, fnReturnTypes, fnReturnTuples, ref isAsync, ref returnType, ref returnTuple, reasons);
                        branchAssignedSets.Add(eiAssigned);
                    }

                    if (ifStmt.ElseBody.Count > 0)
                    {
                        var elseAssigned = new HashSet<string>(assigned, StringComparer.Ordinal);
                        ValidateBlock(ifStmt.ElseBody, kind, isEntry, fnName, loopDepth, functionsByName, functionParamTypes, topLevelConsts, localTypes, emptyListHints, elseAssigned, new Dictionary<string, LuaTableLiteralExpr>(constArrays, StringComparer.Ordinal), fnIsAsync, fnReturnTypes, fnReturnTuples, ref isAsync, ref returnType, ref returnTuple, reasons);
                        branchAssignedSets.Add(elseAssigned);

                        var intersection = new HashSet<string>(branchAssignedSets[0], StringComparer.Ordinal);
                        for (int b = 1; b < branchAssignedSets.Count; b++)
                        {
                            intersection.IntersectWith(branchAssignedSets[b]);
                        }

                        assigned.UnionWith(intersection);
                    }

                    break;
                }

                case LuaNumericForStmt forStmt:
                {
                    if (topLevelConsts.ContainsKey(forStmt.IteratorName))
                    {
                        reasons.Add($"shadow-toplevel-const:{forStmt.IteratorName}");
                        break;
                    }

                    LuaValueType stType = ValidateExpr(forStmt.Start, kind, functionsByName, functionParamTypes, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                    LuaValueType limType = ValidateExpr(forStmt.Limit, kind, functionsByName, functionParamTypes, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                    if (!IsIntCompatible(stType) || !IsIntCompatible(limType))
                    {
                        reasons.Add($"non-int-for-bounds@line{forStmt.Line}");
                    }

                    if (forStmt.Step is not null)
                    {
                        LuaValueType stepType = ValidateExpr(forStmt.Step, kind, functionsByName, functionParamTypes, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                        if (!IsIntCompatible(stepType))
                        {
                            reasons.Add($"non-int-for-step@line{forStmt.Line}");
                        }
                    }

                    if (localTypes.TryGetValue(forStmt.IteratorName, out LuaValueType prevIterType))
                    {
                        if (!TryUnifyVarType(prevIterType, LuaValueType.Int, out LuaValueType unifiedIter))
                        {
                            reasons.Add($"type-mismatch:{forStmt.IteratorName}({prevIterType}->Int)");
                            break;
                        }

                        localTypes[forStmt.IteratorName] = unifiedIter;
                    }
                    else
                    {
                        localTypes[forStmt.IteratorName] = LuaValueType.Int;
                    }

                    var bodyAssigned = new HashSet<string>(assigned, StringComparer.Ordinal) { forStmt.IteratorName };
                    ValidateBlock(forStmt.Body, kind, isEntry, fnName, loopDepth + 1, functionsByName, functionParamTypes, topLevelConsts, localTypes, emptyListHints, bodyAssigned, new Dictionary<string, LuaTableLiteralExpr>(constArrays, StringComparer.Ordinal), fnIsAsync, fnReturnTypes, fnReturnTuples, ref isAsync, ref returnType, ref returnTuple, reasons);
                    break;
                }

                case LuaWhileStmt whileStmt:
                {
                    LuaValueType wCondType = ValidateExpr(whileStmt.Condition, kind, functionsByName, functionParamTypes, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                    if (!IsConditionCompatible(wCondType))
                    {
                        reasons.Add($"non-bool-condition@line{whileStmt.Line}");
                    }

                    var bodyAssigned = new HashSet<string>(assigned, StringComparer.Ordinal);
                    ValidateBlock(whileStmt.Body, kind, isEntry, fnName, loopDepth + 1, functionsByName, functionParamTypes, topLevelConsts, localTypes, emptyListHints, bodyAssigned, new Dictionary<string, LuaTableLiteralExpr>(constArrays, StringComparer.Ordinal), fnIsAsync, fnReturnTypes, fnReturnTuples, ref isAsync, ref returnType, ref returnTuple, reasons);
                    break;
                }

                case LuaBreakStmt breakStmt:
                    if (loopDepth <= 0)
                    {
                        reasons.Add($"break-outside-loop@line{breakStmt.Line}");
                    }

                    break;

                case LuaDoBlockStmt doStmt:
                    ValidateBlock(doStmt.Body, kind, isEntry, fnName, loopDepth, functionsByName, functionParamTypes, topLevelConsts, localTypes, emptyListHints, assigned, constArrays, fnIsAsync, fnReturnTypes, fnReturnTuples, ref isAsync, ref returnType, ref returnTuple, reasons);
                    break;

                case LuaCallStmt callStmt:
                    ValidateCall(
                        callStmt.Call,
                        isStatement: true,
                        kind,
                        functionsByName,
                        functionParamTypes,
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
                    if (retStmt.ExtraValues is { Count: > 0 })
                    {
                        if (isEntry)
                        {
                            reasons.Add($"multi-return-on-entry:{fnName}@line{retStmt.Line}");
                            break;
                        }

                        var tuple = new List<LuaValueType>(1 + retStmt.ExtraValues.Count)
                        {
                            NormalizeStorageType(ValidateExpr(retStmt.Value!, kind, functionsByName, functionParamTypes, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons)),
                        };
                        foreach (LuaExpression ev in retStmt.ExtraValues)
                        {
                            tuple.Add(NormalizeStorageType(ValidateExpr(ev, kind, functionsByName, functionParamTypes, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons)));
                        }

                        if (returnTuple is not null && !returnTuple.SequenceEqual(tuple))
                        {
                            reasons.Add($"inconsistent-return-tuple:{fnName}");
                        }
                        else
                        {
                            returnTuple = tuple;
                            returnType = tuple[0];
                        }

                        break;
                    }

                    if (kind == GeneratedScriptKind.Item && isEntry)
                    {
                        if (retStmt.Value is not null && retStmt.Value is not LuaNilExpr)
                        {
                            LuaValueType itemRet = ValidateExpr(retStmt.Value, kind, functionsByName, functionParamTypes, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                            if (!IsIntCompatible(itemRet))
                            {
                                reasons.Add($"invalid-item-return:{itemRet}@line{retStmt.Line}");
                            }
                        }

                        returnType = LuaValueType.Int;
                    }
                    else if (retStmt.Value is not null)
                    {
                        if (isEntry)
                        {
                            LuaValueType entryRet = ValidateExpr(retStmt.Value, kind, functionsByName, functionParamTypes, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                            if (entryRet is not (LuaValueType.Nil or LuaValueType.Int or LuaValueType.NullableInt))
                            {
                                reasons.Add($"non-int-entry-return:{fnName}({entryRet})@line{retStmt.Line}");
                            }
                        }
                        else
                        {
                            LuaValueType rt = NormalizeStorageType(ValidateExpr(
                                retStmt.Value,
                                kind,
                                functionsByName,
                                functionParamTypes,
                                topLevelConsts,
                                localTypes,
                                assigned,
                                fnIsAsync,
                                fnReturnTypes,
                                ref isAsync,
                                reasons));
                            if (rt == LuaValueType.Nil)
                            {
                                rt = returnType == LuaValueType.Int ? LuaValueType.NullableInt : LuaValueType.NullableInt;
                            }

                            if (returnType == LuaValueType.Void)
                            {
                                returnType = rt;
                            }
                            else if (!TryUnifyVarType(returnType, rt, out LuaValueType unifiedRet))
                            {
                                reasons.Add($"inconsistent-return-type:{fnName}({returnType}->{rt})");
                            }
                            else
                            {
                                returnType = unifiedRet;
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

    private void ValidateMultiAssignment(
        IReadOnlyList<string> targetNames,
        IReadOnlyList<LuaExpression> values,
        bool isLocalDecl,
        int line,
        GeneratedScriptKind kind,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        IReadOnlyDictionary<string, IReadOnlyList<LuaValueType>> functionParamTypes,
        IReadOnlyDictionary<string, LuaValueType> topLevelConsts,
        Dictionary<string, LuaValueType> localTypes,
        HashSet<string> assigned,
        Dictionary<string, LuaTableLiteralExpr> constArrays,
        IReadOnlyDictionary<string, bool> fnIsAsync,
        IReadOnlyDictionary<string, LuaValueType> fnReturnTypes,
        IReadOnlyDictionary<string, IReadOnlyList<LuaValueType>> fnReturnTuples,
        ref bool isAsync,
        SortedSet<string> reasons)
    {
        foreach (string name in targetNames)
        {
            if (name == "_") continue;
            if (topLevelConsts.ContainsKey(name))
            {
                reasons.Add(isLocalDecl ? $"shadow-toplevel-const:{name}" : $"mutate-toplevel-const:{name}");
                return;
            }
        }

        if (isLocalDecl && values.Count == 0)
        {
            foreach (string name in targetNames)
            {
                constArrays.Remove(name);
            }

            return;
        }

        // Unpack single multi-return call: `local w, x, y = GetWorldPos()` or `m, r = GetPlayerRev()`
        if (values.Count == 1 && values[0] is LuaCallExpr singleCall)
        {
            IReadOnlyList<LuaValueType>? tupleTypes = null;
            if (singleCall.CalleeName == "GetWorldPos" && singleCall.Arguments.Count == 0)
            {
                tupleTypes = [LuaValueType.Int, LuaValueType.Int, LuaValueType.Int];
            }
            else if (singleCall.CalleeName == "GetPlayerRev" && singleCall.Arguments.Count == 0)
            {
                tupleTypes = [LuaValueType.Int, LuaValueType.Int];
            }
            else if (fnReturnTuples.TryGetValue(singleCall.CalleeName, out IReadOnlyList<LuaValueType>? userTuple))
            {
                _ = ValidateCall(singleCall, isStatement: false, kind, functionsByName, functionParamTypes, topLevelConsts, localTypes, assigned, constArrays, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                tupleTypes = userTuple;
            }

            if (tupleTypes is not null)
            {
                if (targetNames.Count > tupleTypes.Count)
                {
                    reasons.Add($"multi-assign-excess-targets:{singleCall.CalleeName}@line{line}");
                    return;
                }

                for (int i = 0; i < targetNames.Count; i++)
                {
                    string tName = targetNames[i];
                    if (tName == "_") continue;
                    LuaValueType vt = tupleTypes[i];
                    if (localTypes.TryGetValue(tName, out LuaValueType prev) && !TryUnifyVarType(prev, vt, out vt))
                    {
                        reasons.Add($"type-mismatch:{tName}({prev}->{vt})");
                        return;
                    }

                    localTypes[tName] = vt;
                    assigned.Add(tName);
                    constArrays.Remove(tName);
                }

                return;
            }
        }

        if (values.Count < targetNames.Count)
        {
            reasons.Add($"multi-assign-arity-mismatch({targetNames.Count}>{values.Count})@line{line}");
            return;
        }

        var rhsTypes = new LuaValueType[values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            rhsTypes[i] = NormalizeStorageType(ValidateExpr(values[i], kind, functionsByName, functionParamTypes, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons));
        }

        for (int i = 0; i < targetNames.Count; i++)
        {
            string tName = targetNames[i];
            if (tName == "_") continue;
            LuaValueType vt = rhsTypes[i];
            if (vt == LuaValueType.Void)
            {
                reasons.Add($"void-assign:{tName}");
                continue;
            }

            if (localTypes.TryGetValue(tName, out LuaValueType prev) && !TryUnifyVarType(prev, vt, out vt))
            {
                reasons.Add($"type-mismatch:{tName}({prev}->{vt})");
                continue;
            }

            localTypes[tName] = vt;
            assigned.Add(tName);
            constArrays.Remove(tName);
        }
    }

    private LuaValueType ValidateExpr(
        LuaExpression expr,
        GeneratedScriptKind kind,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        IReadOnlyDictionary<string, IReadOnlyList<LuaValueType>> functionParamTypes,
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
            case LuaUnsupportedExpr unsup:
                reasons.Add(unsup.Reason);
                return LuaValueType.Void;

            case LuaNumberExpr num:
                if (Math.Abs(num.Value - Math.Truncate(num.Value)) > 1e-9 ||
                    num.Value < int.MinValue ||
                    num.Value > int.MaxValue)
                {
                    return LuaValueType.Number;
                }

                return LuaValueType.Int;

            case LuaStringExpr:
                return LuaValueType.String;

            case LuaNilExpr:
                return LuaValueType.Nil;

            case LuaUpvalueExpr up:
                reasons.Add($"unresolved-upvalue:%{up.Name}@line{up.Line}");
                return LuaValueType.Void;

            case LuaIdentifierExpr ident:
                if (ident.Name == "SubWorld")
                {
                    return LuaValueType.Int;
                }

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
                    LuaValueType et = NormalizeStorageType(ValidateExpr(tbl.Elements[i], kind, functionsByName, functionParamTypes, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons));
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
                LuaValueType targetType = ValidateExpr(idxExpr.Target, kind, functionsByName, functionParamTypes, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                LuaValueType indexType = ValidateExpr(idxExpr.Index, kind, functionsByName, functionParamTypes, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                if (!IsIntCompatible(indexType))
                {
                    reasons.Add($"non-int-index@line{idxExpr.Line}");
                    return LuaValueType.Void;
                }

                return targetType switch
                {
                    LuaValueType.StringArray or LuaValueType.StringList => LuaValueType.String,
                    LuaValueType.IntArray or LuaValueType.IntList => LuaValueType.Int,
                    _ => FailExpr($"index-non-array:{targetType}@line{idxExpr.Line}", reasons),
                };
            }

            case LuaUnaryExpr un:
            {
                LuaValueType opType = ValidateExpr(un.Operand, kind, functionsByName, functionParamTypes, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                if (un.Operator == LuaUnaryOp.Negate)
                {
                    if (opType == LuaValueType.Number)
                    {
                        return LuaValueType.Number;
                    }

                    if (!IsIntCompatible(opType))
                    {
                        reasons.Add($"unary-negate-non-int@line{un.Line}");
                    }

                    return LuaValueType.Int;
                }

                if (!IsConditionCompatible(opType))
                {
                    reasons.Add($"unary-not-non-bool@line{un.Line}");
                }

                return LuaValueType.Bool;
            }

            case LuaBinaryExpr bin:
            {
                LuaValueType lt = ValidateExpr(bin.Left, kind, functionsByName, functionParamTypes, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                LuaValueType rt = ValidateExpr(bin.Right, kind, functionsByName, functionParamTypes, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);

                switch (bin.Operator)
                {
                    case LuaBinaryOp.And:
                    case LuaBinaryOp.Or:
                        if (!IsConditionCompatible(lt) || !IsConditionCompatible(rt))
                        {
                            reasons.Add($"non-bool-logic:{bin.Operator}@line{bin.Line}");
                        }

                        return LuaValueType.Bool;

                    case LuaBinaryOp.Eq:
                    case LuaBinaryOp.Neq:
                        if (!AreEqualityComparable(lt, rt))
                        {
                            reasons.Add($"invalid-equality:{lt}-{rt}@line{bin.Line}");
                        }

                        return LuaValueType.Bool;

                    case LuaBinaryOp.Lt:
                    case LuaBinaryOp.Le:
                    case LuaBinaryOp.Gt:
                    case LuaBinaryOp.Ge:
                        if (!IsNumeric(lt) || !IsNumeric(rt))
                        {
                            reasons.Add($"non-int-comparison:{bin.Operator}@line{bin.Line}");
                        }

                        return LuaValueType.Bool;

                    case LuaBinaryOp.Concat:
                        if (lt is not (LuaValueType.String or LuaValueType.Int or LuaValueType.NullableInt) ||
                            rt is not (LuaValueType.String or LuaValueType.Int or LuaValueType.NullableInt))
                        {
                            reasons.Add($"invalid-concat:{lt}-{rt}@line{bin.Line}");
                        }

                        return LuaValueType.String;

                    case LuaBinaryOp.Add:
                    case LuaBinaryOp.Sub:
                    case LuaBinaryOp.Mul:
                        if (!IsNumeric(lt) || !IsNumeric(rt))
                        {
                            reasons.Add($"non-int-arith:{bin.Operator}@line{bin.Line}");
                            return LuaValueType.Int;
                        }

                        return lt == LuaValueType.Number || rt == LuaValueType.Number
                            ? LuaValueType.Number
                            : LuaValueType.Int;

                    case LuaBinaryOp.Mod:
                        if (!IsIntCompatible(lt) || !IsIntCompatible(rt))
                        {
                            reasons.Add($"non-int-arith:Mod@line{bin.Line}");
                        }

                        return LuaValueType.Int;

                    case LuaBinaryOp.Div:
                        if (!IsNumeric(lt) || !IsNumeric(rt))
                        {
                            reasons.Add($"non-int-arith:Div@line{bin.Line}");
                            return LuaValueType.Number;
                        }

                        return TryEvalConstantInt(bin, out _) ? LuaValueType.Int : LuaValueType.Number;

                    case LuaBinaryOp.Pow:
                        if (!IsNumeric(lt) || !IsNumeric(rt))
                        {
                            reasons.Add($"non-int-arith:Pow@line{bin.Line}");
                            return LuaValueType.Number;
                        }

                        return TryEvalConstantInt(bin, out _) ? LuaValueType.Int : LuaValueType.Number;

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
                    functionParamTypes,
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
        IReadOnlyDictionary<string, IReadOnlyList<LuaValueType>> functionParamTypes,
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

        // Check intra-file / linked helper function call
        if (functionsByName.ContainsKey(api))
        {
            IReadOnlyList<LuaValueType> expectedParams = functionParamTypes.GetValueOrDefault(api, []);
            if (args.Count != expectedParams.Count)
            {
                reasons.Add($"invalid-helper-call-arity:{api}({args.Count}!={expectedParams.Count})");
                return LuaValueType.Void;
            }

            for (int i = 0; i < args.Count; i++)
            {
                LuaValueType actualArg = ValidateExpr(args[i], kind, functionsByName, functionParamTypes, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                if (!IsArgAssignableToParam(actualArg, expectedParams[i]))
                {
                    reasons.Add($"invalid-helper-arg-type:{api}[{i}]({actualArg}!={expectedParams[i]})");
                }
            }

            if (fnIsAsync.TryGetValue(api, out bool calleeAsync) && calleeAsync)
            {
                isAsync = true;
            }

            return fnReturnTypes.GetValueOrDefault(api, LuaValueType.Void);
        }

        bool localAsync = isAsync;
        LuaValueType ArgType(int index) =>
            ValidateExpr(args[index], kind, functionsByName, functionParamTypes, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref localAsync, reasons);

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

        bool TryResolveConstInt(LuaExpression e, out int val) =>
            TryEvalConstantIntWithScope(e, topLevelConsts, constArrays, out val);

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
                case "GetCamp":
                case "GetCurCamp":
                case "GetPK":
                case "GetRank":
                case "GetLastFactionNumber":
                case "GetLastDiagNpc":
                case "GetCurServerTime":
                    RequireArgCount(0);
                    return LuaValueType.Int;

                // Multi-return getters usable in scalar position (Lua 4.0 adjusts to 1st return value)
                case "GetWorldPos":
                case "GetPlayerRev":
                    RequireArgCount(0);
                    return LuaValueType.Int;

                // Zero-arg String getters
                case "GetName":
                case "GetAccount":
                case "GetFaction":
                case "GetLastAddFaction":
                    RequireArgCount(0);
                    return LuaValueType.String;

                // 1-arg Int -> Int getters
                case "GetTask":
                case "GetTaskTemp":
                case "GetGlbValue":
                case "HaveMagic":
                case "CheckStoreBoxState":
                case "SubWorldIdx2ID":
                case "SubWorldID2Idx":
                    if (RequireArgCount(1) && !IsIntCompatible(ArgType(0)))
                    {
                        reasons.Add($"invalid-arg-type:{api}@line{call.Line}");
                    }

                    return LuaValueType.Int;

                // GetNpcName(npcIdx) -> String
                case "GetNpcName":
                    if (RequireArgCount(1) && !IsIntCompatible(ArgType(0)))
                    {
                        reasons.Add($"invalid-arg-type:GetNpcName@line{call.Line}");
                    }

                    return LuaValueType.String;

                case "NpcName2Replace":
                    if (RequireArgCount(1) && ArgType(0) != LuaValueType.String)
                    {
                        reasons.Add($"invalid-arg-type:NpcName2Replace@line{call.Line}");
                    }

                    return LuaValueType.String;

                // Math & Coercion stdlib
                case "floor":
                case "ceil":
                    if (RequireArgCount(1) && !IsNumeric(ArgType(0)))
                    {
                        reasons.Add($"invalid-arg-type:{api}@line{call.Line}");
                    }

                    return LuaValueType.Int;

                case "abs":
                    if (RequireArgCount(1))
                    {
                        LuaValueType at = ArgType(0);
                        if (!IsNumeric(at))
                        {
                            reasons.Add($"invalid-arg-type:abs@line{call.Line}");
                        }

                        return at == LuaValueType.Number ? LuaValueType.Number : LuaValueType.Int;
                    }

                    return LuaValueType.Int;

                case "mod":
                case "min":
                case "max":
                    if (RequireArgCount(2))
                    {
                        LuaValueType a0 = ArgType(0);
                        LuaValueType a1 = ArgType(1);
                        if (!IsNumeric(a0) || !IsNumeric(a1))
                        {
                            reasons.Add($"invalid-arg-type:{api}@line{call.Line}");
                        }

                        return a0 == LuaValueType.Number || a1 == LuaValueType.Number
                            ? LuaValueType.Number
                            : LuaValueType.Int;
                    }

                    return LuaValueType.Int;

                case "tonumber":
                    if (RequireArgCount(1))
                    {
                        LuaValueType tt = ArgType(0);
                        if (tt is not (LuaValueType.String or LuaValueType.Int or LuaValueType.NullableInt or LuaValueType.Number))
                        {
                            reasons.Add($"invalid-arg-type:tonumber({tt})@line{call.Line}");
                        }
                    }

                    return LuaValueType.NullableInt;

                case "tostring":
                    if (RequireArgCount(1))
                    {
                        LuaValueType st = ArgType(0);
                        if (st is not (LuaValueType.String or LuaValueType.Int or LuaValueType.NullableInt or LuaValueType.Number))
                        {
                            reasons.Add($"invalid-arg-type:tostring({st})@line{call.Line}");
                        }
                    }

                    return LuaValueType.String;

                case "strlen":
                    if (RequireArgCount(1) && ArgType(0) is not (LuaValueType.String or LuaValueType.Int))
                    {
                        reasons.Add($"invalid-arg-type:strlen@line{call.Line}");
                    }

                    return LuaValueType.Int;

                case "strlower":
                case "strupper":
                    if (RequireArgCount(1) && ArgType(0) != LuaValueType.String)
                    {
                        reasons.Add($"invalid-arg-type:{api}@line{call.Line}");
                    }

                    return LuaValueType.String;

                case "strsub":
                    if (RequireArgRange(2, 3))
                    {
                        if (ArgType(0) != LuaValueType.String || !IsIntCompatible(ArgType(1)) ||
                            (args.Count == 3 && !IsIntCompatible(ArgType(2))))
                        {
                            reasons.Add($"invalid-arg-type:strsub@line{call.Line}");
                        }
                    }

                    return LuaValueType.String;

                case "format":
                    if (args.Count < 1 || ArgType(0) != LuaValueType.String)
                    {
                        reasons.Add($"invalid-arg-type:format@line{call.Line}");
                    }
                    else
                    {
                        for (int i = 1; i < args.Count; i++)
                        {
                            LuaValueType ft = ArgType(i);
                            if (ft is not (LuaValueType.String or LuaValueType.Int or LuaValueType.NullableInt or LuaValueType.Number))
                            {
                                reasons.Add($"invalid-format-arg:{ft}@line{call.Line}");
                            }
                        }

                        if (args[0] is LuaStringExpr fmtLit)
                        {
                            ValidateDynamicCallbackInFormatLiteral(fmtLit.DecodedUtf8, functionsByName, reasons);
                        }
                    }

                    return LuaValueType.String;

                case "GetLocalDate":
                case "date":
                    if (RequireArgRange(0, 1) && args.Count == 1 && ArgType(0) != LuaValueType.String)
                    {
                        reasons.Add($"invalid-arg-type:{api}@line{call.Line}");
                    }

                    return LuaValueType.String;

                // Pay(amount) -> Int (usable as expression or statement)
                case "Pay":
                    if (RequireArgCount(1) && !IsIntCompatible(ArgType(0)))
                    {
                        reasons.Add($"invalid-arg-type:{api}@line{call.Line}");
                    }

                    return LuaValueType.Int;

                // 2-arg Int -> Int
                case "GetByte":
                case "GetBit":
                    if (RequireArgCount(2))
                    {
                        if (!IsIntCompatible(ArgType(0)) || !IsIntCompatible(ArgType(1)))
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
                        if (!IsIntCompatible(ArgType(0)) || !IsIntCompatible(ArgType(1)) || !IsIntCompatible(ArgType(2)))
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
                            if (!IsIntCompatible(ArgType(i)))
                            {
                                reasons.Add($"invalid-arg-type:random@line{call.Line}");
                            }
                        }
                    }

                    return LuaValueType.Int;

                // getn(arrayOrList) -> Int
                case "getn":
                    if (RequireArgCount(1))
                    {
                        LuaValueType at = ArgType(0);
                        if (at is not (LuaValueType.StringArray or LuaValueType.IntArray or LuaValueType.StringList or LuaValueType.IntList))
                        {
                            reasons.Add($"invalid-arg-type:getn({at})@line{call.Line}");
                        }
                    }

                    return LuaValueType.Int;

                // tinsert(list, [pos,] val)
                case "tinsert":
                    if (!isStatement)
                    {
                        reasons.Add($"statement-used-as-expr:tinsert@line{call.Line}");
                    }

                    if (RequireArgRange(2, 3))
                    {
                        LuaValueType lt = ArgType(0);
                        LuaValueType valType = args.Count == 2 ? ArgType(1) : ArgType(2);
                        if (args.Count == 3 && !IsIntCompatible(ArgType(1)))
                        {
                            reasons.Add($"invalid-arg-type:tinsert-pos@line{call.Line}");
                        }

                        if (lt == LuaValueType.StringList && valType is LuaValueType.String or LuaValueType.Int)
                        {
                            // If inserting constant option string into dialog table, verify any callback exists
                            LuaExpression valExpr = args.Count == 2 ? args[1] : args[2];
                            if (TryEvalConstantString(valExpr, out string? optStr) && optStr is not null && optStr.Contains('/', StringComparison.Ordinal))
                            {
                                ValidateSayOptionString(optStr, functionsByName, reasons);
                            }
                        }
                        else if (lt == LuaValueType.IntList && IsIntCompatible(valType))
                        {
                        }
                        else
                        {
                            reasons.Add($"invalid-arg-type:tinsert({lt},{valType})@line{call.Line}");
                        }
                    }

                    return LuaValueType.Void;

                // HaveItem(constEventId) -> Int
                case "HaveItem":
                    if (RequireArgCount(1))
                    {
                        if (!TryResolveConstInt(args[0], out int eventId))
                        {
                            reasons.Add($"dynamic-event-item:HaveItem@line{call.Line}");
                        }
                        else if (!HasEventItem(eventId))
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
                        if (!TryResolveConstInt(args[0], out int eventId))
                        {
                            reasons.Add($"dynamic-event-item:{api}@line{call.Line}");
                        }
                        else if (!HasEventItem(eventId))
                        {
                            reasons.Add($"unknown-event-item:{eventId}");
                        }
                    }

                    return LuaValueType.Void;

                // General GDPL item APIs (usable as statement or Int expression)
                case "AddItem":
                case "AddItemNoStack":
                    if (RequireArgRange(6, 7))
                    {
                        for (int i = 0; i < args.Count; i++)
                        {
                            if (!IsIntCompatible(ArgType(i)))
                            {
                                reasons.Add($"invalid-arg-type:{api}@line{call.Line}");
                            }
                        }

                        if (!TryResolveConstInt(args[0], out int g) ||
                            !TryResolveConstInt(args[1], out int d) ||
                            !TryResolveConstInt(args[2], out int p) ||
                            !TryResolveConstInt(args[3], out int lv))
                        {
                            reasons.Add($"dynamic-gdpl:{api}@line{call.Line}");
                        }
                        else if (_itemCatalogIndex is null || !_itemCatalogIndex.TryResolveByGdpl(g, d, p, lv, out _))
                        {
                            reasons.Add($"unknown-gdpl:{g},{d},{p},{lv}");
                        }
                    }

                    return LuaValueType.Int;

                case "AddStackItem":
                    if (RequireArgRange(7, 8))
                    {
                        for (int i = 0; i < args.Count; i++)
                        {
                            if (!IsIntCompatible(ArgType(i)))
                            {
                                reasons.Add($"invalid-arg-type:AddStackItem@line{call.Line}");
                            }
                        }

                        if (!TryResolveConstInt(args[1], out int g) ||
                            !TryResolveConstInt(args[2], out int d) ||
                            !TryResolveConstInt(args[3], out int p) ||
                            !TryResolveConstInt(args[4], out int lv))
                        {
                            reasons.Add($"dynamic-gdpl:AddStackItem@line{call.Line}");
                        }
                        else if (_itemCatalogIndex is null || !_itemCatalogIndex.TryResolveByGdpl(g, d, p, lv, out _))
                        {
                            reasons.Add($"unknown-gdpl:{g},{d},{p},{lv}");
                        }
                    }

                    return LuaValueType.Int;

                case "DelCommonItem":
                    if (RequireArgRange(3, 3))
                    {
                        if (!TryResolveConstInt(args[0], out int g) ||
                            !TryResolveConstInt(args[1], out int d) ||
                            !TryResolveConstInt(args[2], out int p))
                        {
                            reasons.Add($"dynamic-gdp:DelCommonItem@line{call.Line}");
                        }
                        else if (_itemCatalogIndex is null || !_itemCatalogIndex.TryResolveByGdp(g, d, p, out _))
                        {
                            reasons.Add($"unknown-gdp:{g},{d},{p}");
                        }
                    }

                    return LuaValueType.Int;

                case "CalcEquiproomItemCount":
                    if (RequireArgCount(4))
                    {
                        if (!TryResolveConstInt(args[0], out int g) ||
                            !TryResolveConstInt(args[1], out int d) ||
                            !TryResolveConstInt(args[2], out int p) ||
                            !TryResolveConstInt(args[3], out int lv))
                        {
                            reasons.Add($"dynamic-gdpl:CalcEquiproomItemCount@line{call.Line}");
                        }
                        else if (_itemCatalogIndex is null || !_itemCatalogIndex.TryResolveByGdpl(g, d, p, lv, out _))
                        {
                            reasons.Add($"unknown-gdpl:{g},{d},{p},{lv}");
                        }
                    }

                    return LuaValueType.Int;

                case "ConsumeEquiproomItem":
                    if (RequireArgCount(5))
                    {
                        if (!IsIntCompatible(ArgType(0)))
                        {
                            reasons.Add($"invalid-arg-type:ConsumeEquiproomItem@line{call.Line}");
                        }

                        if (!TryResolveConstInt(args[1], out int g) ||
                            !TryResolveConstInt(args[2], out int d) ||
                            !TryResolveConstInt(args[3], out int p) ||
                            !TryResolveConstInt(args[4], out int lv))
                        {
                            reasons.Add($"dynamic-gdpl:ConsumeEquiproomItem@line{call.Line}");
                        }
                        else if (_itemCatalogIndex is null || !_itemCatalogIndex.TryResolveByGdpl(g, d, p, lv, out _))
                        {
                            reasons.Add($"unknown-gdpl:{g},{d},{p},{lv}");
                        }
                    }

                    return LuaValueType.Int;

                // Object-only prop state
                case "SetPropState":
                    if (kind != GeneratedScriptKind.Object)
                    {
                        reasons.Add("object-only-api:SetPropState");
                    }

                    if (!isStatement)
                    {
                        reasons.Add($"statement-used-as-expr:SetPropState@line{call.Line}");
                    }

                    if (RequireArgRange(0, 1) && args.Count == 1 && !IsIntCompatible(ArgType(0)))
                    {
                        reasons.Add($"invalid-arg-type:SetPropState@line{call.Line}");
                    }

                    return LuaValueType.Void;

                // 0-arg statements
                case "RestoreCamp":
                case "SaveNow":
                    if (!isStatement)
                    {
                        reasons.Add($"statement-used-as-expr:{api}@line{call.Line}");
                    }

                    RequireArgCount(0);
                    return LuaValueType.Void;

                // 1-arg Int statements
                case "Earn":
                case "AddOwnExp":
                case "AddRepute":
                case "SetFightState":
                case "SetProtectTime":
                case "AddStation":
                case "AddTermini":
                case "SetRank":
                case "SetCamp":
                case "SetCurCamp":
                case "SetPK":
                case "DelMagic":
                case "SyncTaskValue":
                case "OpenStoreBox":
                    if (!isStatement)
                    {
                        reasons.Add($"statement-used-as-expr:{api}@line{call.Line}");
                    }

                    if (RequireArgCount(1) && !IsIntCompatible(ArgType(0)))
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

                    if (RequireArgCount(1) && !IsIntCompatible(ArgType(0)))
                    {
                        reasons.Add($"invalid-arg-type:{api}@line{call.Line}");
                    }

                    return LuaValueType.Void;

                // 1-arg String / broadcast statements
                case "Msg2Player":
                case "AddNote":
                case "Msg2SubWorld":
                case "AddGlobalNews":
                case "Msg2Team":
                    if (!isStatement)
                    {
                        reasons.Add($"statement-used-as-expr:{api}@line{call.Line}");
                    }

                    if (RequireArgCount(1))
                    {
                        LuaValueType mt = ArgType(0);
                        if (mt is not (LuaValueType.String or LuaValueType.Int or LuaValueType.NullableInt))
                        {
                            reasons.Add($"invalid-arg-type:{api}({mt})@line{call.Line}");
                        }
                    }

                    return LuaValueType.Void;

                case "AddGlobalCountNews":
                    if (!isStatement)
                    {
                        reasons.Add($"statement-used-as-expr:AddGlobalCountNews@line{call.Line}");
                    }

                    if (RequireArgCount(2))
                    {
                        if (ArgType(0) is not (LuaValueType.String or LuaValueType.Int) || !IsIntCompatible(ArgType(1)))
                        {
                            reasons.Add($"invalid-arg-type:AddGlobalCountNews@line{call.Line}");
                        }
                    }

                    return LuaValueType.Void;

                case "WriteLog":
                case "WriteGoldLog":
                case "UseSilver":
                case "print":
                    if (!isStatement)
                    {
                        reasons.Add($"statement-used-as-expr:{api}@line{call.Line}");
                    }

                    for (int i = 0; i < args.Count; i++)
                    {
                        _ = ArgType(i);
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
                        if (!IsIntCompatible(ArgType(0)) || !IsIntCompatible(ArgType(1)))
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
                            if (!IsIntCompatible(ArgType(i)))
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
                            if (!IsIntCompatible(ArgType(i)))
                            {
                                reasons.Add($"invalid-arg-type:AddSkillState@line{call.Line}");
                            }
                        }

                        if (args.Count == 5 && (!TryResolveConstInt(args[4], out int noClear) || noClear != 0))
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
                            if (!IsIntCompatible(ArgType(i)))
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

                    if (args.Count < 2 || !TryResolveConstInt(args[0], out int ladderCount) || ladderCount <= 0 || ladderCount != args.Count - 1)
                    {
                        reasons.Add($"invalid-showladder-args@line{call.Line}");
                    }
                    else
                    {
                        for (int i = 1; i < args.Count; i++)
                        {
                            if (!IsIntCompatible(ArgType(i)))
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
                            if (!IsIntCompatible(ArgType(i)))
                            {
                                reasons.Add($"invalid-arg-type:Sale@line{call.Line}");
                            }
                        }
                    }

                    return LuaValueType.Void;

                // AskClientForNumber(cbStr, min, max, title)
                case "AskClientForNumber":
                    if (!isStatement)
                    {
                        reasons.Add($"statement-used-as-expr:AskClientForNumber@line{call.Line}");
                    }

                    if (RequireArgCount(4))
                    {
                        if (args[0] is LuaStringExpr askCbStr)
                        {
                            string cb = askCbStr.DecodedUtf8.Trim();
                            if (cb.Length == 0 || !functionsByName.ContainsKey(cb))
                            {
                                reasons.Add($"missing-asknumber-callback:{cb}");
                            }
                            else if (functionParamTypes.TryGetValue(cb, out IReadOnlyList<LuaValueType>? cbParams) &&
                                     (cbParams.Count != 1 || cbParams[0] != LuaValueType.Int))
                            {
                                reasons.Add($"invalid-asknumber-callback-sig:{cb}");
                            }
                        }
                        else
                        {
                            reasons.Add($"dynamic-asknumber-callback@line{call.Line}");
                        }

                        if (!IsIntCompatible(ArgType(1)) || !IsIntCompatible(ArgType(2)) || ArgType(3) != LuaValueType.String)
                        {
                            reasons.Add($"invalid-arg-type:AskClientForNumber@line{call.Line}");
                        }
                    }

                    return LuaValueType.Void;

                // Async teleport & portal APIs (NewWorld also usable in if-condition / statement)
                case "NewWorld":
                    if (RequireArgCount(3))
                    {
                        if (!IsIntCompatible(ArgType(0)) || !IsIntCompatible(ArgType(1)) || !IsIntCompatible(ArgType(2)))
                        {
                            reasons.Add($"invalid-arg-type:NewWorld@line{call.Line}");
                        }
                    }

                    localAsync = true;
                    return isStatement ? LuaValueType.Void : LuaValueType.Int;

                case "SetPos":
                    if (!isStatement)
                    {
                        reasons.Add($"statement-used-as-expr:{api}@line{call.Line}");
                    }

                    if (RequireArgCount(2))
                    {
                        if (!IsIntCompatible(ArgType(0)) || !IsIntCompatible(ArgType(1)))
                        {
                            reasons.Add($"invalid-arg-type:SetPos@line{call.Line}");
                        }
                    }

                    localAsync = true;
                    return LuaValueType.Void;

                case "ReturnFromPortal":
                    if (RequireArgCount(0))
                    {
                    }

                    localAsync = true;
                    return isStatement ? LuaValueType.Void : LuaValueType.Int;

                // Talk(count, callbackStr, page1, page2, ...)
                case "Talk":
                    if (!isStatement)
                    {
                        reasons.Add($"statement-used-as-expr:Talk@line{call.Line}");
                    }

                    ValidateTalkCall(call, kind, functionsByName, functionParamTypes, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref localAsync, reasons);
                    return LuaValueType.Void;

                // Say / Describe
                case "Say":
                case "Describe":
                    if (!isStatement)
                    {
                        reasons.Add($"statement-used-as-expr:{api}@line{call.Line}");
                    }

                    ValidateSayOrDescribeCall(call, kind, functionsByName, functionParamTypes, topLevelConsts, localTypes, assigned, constArrays, fnIsAsync, fnReturnTypes, ref localAsync, reasons);
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

    private bool HasEventItem(int detailType) =>
        (_itemCatalogIndex is not null && _itemCatalogIndex.TryResolveEventItem(detailType, out _)) ||
        _eventItemCodeByDetailId.ContainsKey(detailType);

    private void ValidateTalkCall(
        LuaCallExpr call,
        GeneratedScriptKind kind,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        IReadOnlyDictionary<string, IReadOnlyList<LuaValueType>> functionParamTypes,
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

        if (call.Arguments[1] is LuaStringExpr cbStr)
        {
            string cbName = cbStr.DecodedUtf8.Trim();
            if (cbName.Length > 0)
            {
                if (!functionsByName.TryGetValue(cbName, out LuaFunctionDecl? cbFn))
                {
                    reasons.Add($"missing-talk-callback:{cbName}");
                }
                else if (cbFn.Parameters.Count > 0)
                {
                    reasons.Add($"talk-callback-requires-0-params:{cbName}");
                }
            }
        }
        else if (call.Arguments[1] is not LuaNilExpr)
        {
            reasons.Add($"dynamic-talk-callback@line{call.Line}");
        }

        bool anyIntPage = false;
        bool anyStringPage = false;
        for (int i = 2; i < call.Arguments.Count; i++)
        {
            LuaValueType pt = ValidateExpr(call.Arguments[i], kind, functionsByName, functionParamTypes, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
            if (pt == LuaValueType.String)
            {
                anyStringPage = true;
            }
            else if (pt is LuaValueType.Int or LuaValueType.NullableInt)
            {
                anyIntPage = true;
            }
            else
            {
                reasons.Add($"invalid-talk-page-type:{pt}@line{call.Line}");
            }
        }

        if (anyIntPage && anyStringPage)
        {
            reasons.Add($"mixed-talk-page-types@line{call.Line}");
        }
    }

    private void ValidateSayOrDescribeCall(
        LuaCallExpr call,
        GeneratedScriptKind kind,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        IReadOnlyDictionary<string, IReadOnlyList<LuaValueType>> functionParamTypes,
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

        LuaValueType promptType = ValidateExpr(call.Arguments[0], kind, functionsByName, functionParamTypes, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
        if (promptType is not (LuaValueType.String or LuaValueType.Int or LuaValueType.NullableInt))
        {
            reasons.Add($"invalid-say-prompt-type:{promptType}@line{call.Line}");
            return;
        }

        if (call.CalleeName == "Describe" && promptType != LuaValueType.String)
        {
            reasons.Add($"invalid-describe-prompt-type:{promptType}@line{call.Line}");
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

        // Say(prompt, count, opt1, opt2, ...) — supports constant or runtime String options; LuaSelectUI reads first `count` options when trailing extra args exist
        if (TryEvalConstantInt(call.Arguments[1], out int inlineCount) &&
            inlineCount > 0 &&
            call.Arguments.Count >= 2 + inlineCount)
        {
            int checkEnd = 2 + inlineCount;
            for (int i = 2; i < call.Arguments.Count; i++)
            {
                LuaValueType ot = ValidateExpr(call.Arguments[i], kind, functionsByName, functionParamTypes, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                if (ot != LuaValueType.String)
                {
                    reasons.Add($"invalid-say-option-type:{ot}@line{call.Line}");
                    return;
                }

                if (i < checkEnd && TryEvalConstantString(call.Arguments[i], out string? optStr) && optStr is not null)
                {
                    ValidateSayOptionString(optStr, functionsByName, reasons);
                }
            }

            return;
        }

        // Say(prompt, countExpr, tableOrListExpr) where 3rd arg is StringArray or StringList
        if (call.Arguments.Count == 3)
        {
            LuaValueType countType = ValidateExpr(call.Arguments[1], kind, functionsByName, functionParamTypes, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
            LuaValueType tabType = ValidateExpr(call.Arguments[2], kind, functionsByName, functionParamTypes, topLevelConsts, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
            if (IsIntCompatible(countType) && tabType is LuaValueType.StringArray or LuaValueType.StringList)
            {
                if (call.Arguments[2] is LuaIdentifierExpr tabIdent &&
                    constArrays is not null &&
                    constArrays.TryGetValue(tabIdent.Name, out LuaTableLiteralExpr? tabLit))
                {
                    foreach (LuaExpression el in tabLit.Elements)
                    {
                        if (TryEvalConstantString(el, out string? optStr) && optStr is not null)
                        {
                            ValidateSayOptionString(optStr, functionsByName, reasons);
                        }
                    }
                }

                return;
            }
        }

        reasons.Add($"unsupported-say-form@line{call.Line}");
    }

    private static void ValidateDynamicCallbackInFormatLiteral(
        string formatStr,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        SortedSet<string> reasons)
    {
        if (!formatStr.Contains('/', StringComparison.Ordinal))
        {
            return;
        }

        ValidateSayOptionString(formatStr, functionsByName, reasons);
    }

    private static void ValidateSayOptionString(
        string optionSpec,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        SortedSet<string> reasons)
    {
        int slashIdx = optionSpec.LastIndexOf('/');
        if (slashIdx < 0)
        {
            return;
        }

        string rawCb = optionSpec[(slashIdx + 1)..].Trim();
        if (rawCb.Length == 0 ||
            rawCb.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            rawCb.Equals("OnCancel", StringComparison.OrdinalIgnoreCase) ||
            rawCb.Equals("Cancel", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!LuaIncludeLinker.TryExtractOptionCallbackName(optionSpec, out string cbName) ||
            !functionsByName.ContainsKey(cbName))
        {
            reasons.Add($"missing-say-callback:{rawCb}");
        }
    }

    public static bool AllPathsReturn(IReadOnlyList<LuaStatement> stmts)
    {
        for (int i = 0; i < stmts.Count; i++)
        {
            switch (stmts[i])
            {
                case LuaReturnStmt:
                    return true;
                case LuaDoBlockStmt db when AllPathsReturn(db.Body):
                    return true;
                case LuaIfStmt ifs when ifs.ElseBody.Count > 0:
                    bool all = AllPathsReturn(ifs.ThenBody) && AllPathsReturn(ifs.ElseBody);
                    foreach (LuaElseIfBranch ei in ifs.ElseIfBranches)
                    {
                        all &= AllPathsReturn(ei.Body);
                    }

                    if (all)
                    {
                        return true;
                    }

                    break;
            }
        }

        return false;
    }

    private static bool IsIntCompatible(LuaValueType t) => t is LuaValueType.Int or LuaValueType.NullableInt;

    private static bool IsNumeric(LuaValueType t) => t is LuaValueType.Int or LuaValueType.NullableInt or LuaValueType.Number;

    private static bool IsConditionCompatible(LuaValueType t) =>
        t is LuaValueType.Bool or LuaValueType.Nil or LuaValueType.Int or LuaValueType.NullableInt;

    private static bool IsArgAssignableToParam(LuaValueType arg, LuaValueType param)
    {
        if (arg == param) return true;
        if (param == LuaValueType.Int && arg == LuaValueType.NullableInt) return true;
        if (param == LuaValueType.NullableInt && arg is LuaValueType.Int or LuaValueType.Nil) return true;
        if (param == LuaValueType.Number && IsNumeric(arg)) return true;
        return false;
    }

    private static LuaValueType NormalizeStorageType(LuaValueType t) =>
        t == LuaValueType.Nil ? LuaValueType.NullableInt : t;

    private static bool TryUnifyVarType(LuaValueType a, LuaValueType b, out LuaValueType unified)
    {
        if (a == b)
        {
            unified = a;
            return true;
        }

        if ((a == LuaValueType.Int && b == LuaValueType.NullableInt) ||
            (a == LuaValueType.NullableInt && b == LuaValueType.Int))
        {
            unified = LuaValueType.NullableInt;
            return true;
        }

        if (IsNumeric(a) && IsNumeric(b) && (a == LuaValueType.Number || b == LuaValueType.Number))
        {
            unified = LuaValueType.Number;
            return true;
        }

        unified = a;
        return false;
    }

    private static bool AreEqualityComparable(LuaValueType lt, LuaValueType rt)
    {
        if (lt == LuaValueType.Void || rt == LuaValueType.Void)
        {
            return false;
        }

        if (lt == rt && lt is LuaValueType.Int or LuaValueType.NullableInt or LuaValueType.Number or LuaValueType.String or LuaValueType.Nil)
        {
            return true;
        }

        if (IsNumeric(lt) && IsNumeric(rt))
        {
            return true;
        }

        if ((lt == LuaValueType.Nil && rt is LuaValueType.Int or LuaValueType.NullableInt or LuaValueType.Number or LuaValueType.String) ||
            (rt == LuaValueType.Nil && lt is LuaValueType.Int or LuaValueType.NullableInt or LuaValueType.Number or LuaValueType.String))
        {
            return true;
        }

        return false;
    }

    public bool TryEvalConstantIntWithScope(
        LuaExpression expr,
        IReadOnlyDictionary<string, LuaValueType>? topConsts,
        IReadOnlyDictionary<string, LuaTableLiteralExpr>? constArrays,
        out int value)
    {
        _ = topConsts;
        _ = constArrays;
        return TryEvalConstantInt(expr, _currentTopLevelConstExprs, out value);
    }

    public static bool TryEvalConstantInt(LuaExpression expr, out int value) =>
        TryEvalConstantInt(expr, null, out value);

    public static bool TryEvalConstantInt(
        LuaExpression expr,
        IReadOnlyDictionary<string, LuaExpression>? constExprs,
        out int value)
    {
        switch (expr)
        {
            case LuaIdentifierExpr id when constExprs is not null && constExprs.TryGetValue(id.Name, out LuaExpression? ce):
                return TryEvalConstantInt(ce, constExprs, out value);

            case LuaNumberExpr num:
                if (Math.Abs(num.Value - Math.Truncate(num.Value)) < 1e-9 &&
                    num.Value is >= int.MinValue and <= int.MaxValue)
                {
                    value = (int)Math.Truncate(num.Value);
                    return true;
                }

                value = 0;
                return false;

            case LuaUnaryExpr { Operator: LuaUnaryOp.Negate } un when TryEvalConstantInt(un.Operand, constExprs, out int inner):
                value = -inner;
                return true;

            case LuaBinaryExpr bin when TryEvalConstantInt(bin.Left, constExprs, out int l) && TryEvalConstantInt(bin.Right, constExprs, out int r):
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

    public static bool TryEvalConstantDouble(LuaExpression expr, IReadOnlyDictionary<string, LuaExpression>? constExprs, out double value)
    {
        switch (expr)
        {
            case LuaNumberExpr num:
                value = num.Value;
                return true;
            case LuaIdentifierExpr id when constExprs is not null && constExprs.TryGetValue(id.Name, out LuaExpression? ce):
                return TryEvalConstantDouble(ce, constExprs, out value);
            case LuaUnaryExpr { Operator: LuaUnaryOp.Negate } un when TryEvalConstantDouble(un.Operand, constExprs, out double inner):
                value = -inner;
                return true;
            case LuaBinaryExpr bin when TryEvalConstantDouble(bin.Left, constExprs, out double l) && TryEvalConstantDouble(bin.Right, constExprs, out double r):
                switch (bin.Operator)
                {
                    case LuaBinaryOp.Add: value = l + r; return true;
                    case LuaBinaryOp.Sub: value = l - r; return true;
                    case LuaBinaryOp.Mul: value = l * r; return true;
                    case LuaBinaryOp.Div when r != 0.0: value = l / r; return true;
                    case LuaBinaryOp.Mod when r != 0.0: value = l % r; return true;
                    case LuaBinaryOp.Pow: value = Math.Pow(l, r); return true;
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
        if (expr is LuaUnsupportedExpr unsup)
        {
            type = LuaValueType.Void;
            error = unsup.Reason;
            return false;
        }

        if (TryEvalConstantInt(expr, out _))
        {
            type = LuaValueType.Int;
            return true;
        }

        if (expr is LuaNumberExpr)
        {
            type = LuaValueType.Number;
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

        if (expr is LuaBinaryExpr bin &&
            TryValidateConstantExpr(bin.Left, existingConsts, out LuaValueType blt, out error) &&
            TryValidateConstantExpr(bin.Right, existingConsts, out LuaValueType brt, out error))
        {
            if (bin.Operator == LuaBinaryOp.Concat &&
                blt is LuaValueType.String or LuaValueType.Int &&
                brt is LuaValueType.String or LuaValueType.Int)
            {
                type = LuaValueType.String;
                return true;
            }

            if (bin.Operator is LuaBinaryOp.Add or LuaBinaryOp.Sub or LuaBinaryOp.Mul &&
                IsNumeric(blt) && IsNumeric(brt))
            {
                type = blt == LuaValueType.Number || brt == LuaValueType.Number ? LuaValueType.Number : LuaValueType.Int;
                return true;
            }

            if (bin.Operator == LuaBinaryOp.Div && IsNumeric(blt) && IsNumeric(brt))
            {
                type = LuaValueType.Number;
                return true;
            }
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

    private static void CollectUnsupportedNodes(IReadOnlyList<LuaStatement> stmts, SortedSet<string> reasons)
    {
        foreach (LuaStatement stmt in stmts)
        {
            switch (stmt)
            {
                case LuaUnsupportedStmt us:
                    reasons.Add(us.Reason);
                    break;
                case LuaIfStmt ifs:
                    CollectUnsupportedExprNodes(ifs.Condition, reasons);
                    CollectUnsupportedNodes(ifs.ThenBody, reasons);
                    foreach (LuaElseIfBranch ei in ifs.ElseIfBranches)
                    {
                        CollectUnsupportedExprNodes(ei.Condition, reasons);
                        CollectUnsupportedNodes(ei.Body, reasons);
                    }

                    CollectUnsupportedNodes(ifs.ElseBody, reasons);
                    break;
                case LuaNumericForStmt nf:
                    CollectUnsupportedExprNodes(nf.Start, reasons);
                    CollectUnsupportedExprNodes(nf.Limit, reasons);
                    if (nf.Step is not null) CollectUnsupportedExprNodes(nf.Step, reasons);
                    CollectUnsupportedNodes(nf.Body, reasons);
                    break;
                case LuaWhileStmt ws:
                    CollectUnsupportedExprNodes(ws.Condition, reasons);
                    CollectUnsupportedNodes(ws.Body, reasons);
                    break;
                case LuaDoBlockStmt db:
                    CollectUnsupportedNodes(db.Body, reasons);
                    break;
                case LuaLocalDeclStmt ld when ld.Initializer is not null:
                    CollectUnsupportedExprNodes(ld.Initializer, reasons);
                    break;
                case LuaAssignStmt asg:
                    CollectUnsupportedExprNodes(asg.Value, reasons);
                    break;
                case LuaCallStmt cs:
                    CollectUnsupportedExprNodes(cs.Call, reasons);
                    break;
                case LuaReturnStmt ret:
                    if (ret.Value is not null) CollectUnsupportedExprNodes(ret.Value, reasons);
                    if (ret.ExtraValues is not null)
                    {
                        foreach (LuaExpression ev in ret.ExtraValues) CollectUnsupportedExprNodes(ev, reasons);
                    }

                    break;
            }
        }
    }

    private static void CollectUnsupportedExprNodes(LuaExpression expr, SortedSet<string> reasons)
    {
        switch (expr)
        {
            case LuaUnsupportedExpr ue:
                reasons.Add(ue.Reason);
                break;
            case LuaCallExpr c:
                foreach (LuaExpression a in c.Arguments) CollectUnsupportedExprNodes(a, reasons);
                break;
            case LuaBinaryExpr b:
                CollectUnsupportedExprNodes(b.Left, reasons);
                CollectUnsupportedExprNodes(b.Right, reasons);
                break;
            case LuaUnaryExpr u:
                CollectUnsupportedExprNodes(u.Operand, reasons);
                break;
            case LuaIndexExpr idx:
                CollectUnsupportedExprNodes(idx.Target, reasons);
                CollectUnsupportedExprNodes(idx.Index, reasons);
                break;
            case LuaTableLiteralExpr t:
                foreach (LuaExpression e in t.Elements) CollectUnsupportedExprNodes(e, reasons);
                break;
        }
    }

    private static void CollectLocalCallEdges(
        IReadOnlyList<LuaStatement> stmts,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        IReadOnlyDictionary<string, LuaExpression> topLevelConstExprs,
        HashSet<string> edges)
    {
        foreach (LuaStatement stmt in stmts)
        {
            switch (stmt)
            {
                case LuaCallStmt cs:
                    CollectExprCalls(cs.Call, functionsByName, topLevelConstExprs, edges);
                    break;
                case LuaLocalDeclStmt ld when ld.Initializer is not null:
                    CollectExprCalls(ld.Initializer, functionsByName, topLevelConstExprs, edges);
                    break;
                case LuaMultiLocalDeclStmt mld:
                    foreach (LuaExpression init in mld.Initializers) CollectExprCalls(init, functionsByName, topLevelConstExprs, edges);
                    break;
                case LuaAssignStmt asg:
                    CollectExprCalls(asg.Value, functionsByName, topLevelConstExprs, edges);
                    break;
                case LuaMultiAssignStmt masg:
                    foreach (LuaExpression v in masg.Values) CollectExprCalls(v, functionsByName, topLevelConstExprs, edges);
                    break;
                case LuaIndexAssignStmt iasg:
                    CollectExprCalls(iasg.Index, functionsByName, topLevelConstExprs, edges);
                    CollectExprCalls(iasg.Value, functionsByName, topLevelConstExprs, edges);
                    break;
                case LuaReturnStmt ret:
                    if (ret.Value is not null) CollectExprCalls(ret.Value, functionsByName, topLevelConstExprs, edges);
                    if (ret.ExtraValues is not null)
                    {
                        foreach (LuaExpression ev in ret.ExtraValues) CollectExprCalls(ev, functionsByName, topLevelConstExprs, edges);
                    }

                    break;
                case LuaIfStmt ifs:
                    CollectExprCalls(ifs.Condition, functionsByName, topLevelConstExprs, edges);
                    CollectLocalCallEdges(ifs.ThenBody, functionsByName, topLevelConstExprs, edges);
                    foreach (LuaElseIfBranch ei in ifs.ElseIfBranches)
                    {
                        CollectExprCalls(ei.Condition, functionsByName, topLevelConstExprs, edges);
                        CollectLocalCallEdges(ei.Body, functionsByName, topLevelConstExprs, edges);
                    }

                    CollectLocalCallEdges(ifs.ElseBody, functionsByName, topLevelConstExprs, edges);
                    break;
                case LuaNumericForStmt nf:
                    CollectExprCalls(nf.Start, functionsByName, topLevelConstExprs, edges);
                    CollectExprCalls(nf.Limit, functionsByName, topLevelConstExprs, edges);
                    if (nf.Step is not null) CollectExprCalls(nf.Step, functionsByName, topLevelConstExprs, edges);
                    CollectLocalCallEdges(nf.Body, functionsByName, topLevelConstExprs, edges);
                    break;
                case LuaWhileStmt ws:
                    CollectExprCalls(ws.Condition, functionsByName, topLevelConstExprs, edges);
                    CollectLocalCallEdges(ws.Body, functionsByName, topLevelConstExprs, edges);
                    break;
                case LuaDoBlockStmt db:
                    CollectLocalCallEdges(db.Body, functionsByName, topLevelConstExprs, edges);
                    break;
            }
        }
    }

    private static void CollectExprCalls(
        LuaExpression expr,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        IReadOnlyDictionary<string, LuaExpression> topLevelConstExprs,
        HashSet<string> edges)
    {
        switch (expr)
        {
            case LuaStringExpr str:
                if (LuaIncludeLinker.TryExtractOptionCallbackName(str.DecodedUtf8, out string cb) &&
                    functionsByName.ContainsKey(cb))
                {
                    edges.Add(cb);
                }

                break;

            case LuaIdentifierExpr id when topLevelConstExprs.TryGetValue(id.Name, out LuaExpression? constVal) && constVal is LuaTableLiteralExpr constTbl:
                foreach (LuaExpression el in constTbl.Elements)
                {
                    CollectExprCalls(el, functionsByName, topLevelConstExprs, edges);
                }

                break;

            case LuaCallExpr call:
                if (functionsByName.ContainsKey(call.CalleeName))
                {
                    edges.Add(call.CalleeName);
                }

                foreach (LuaExpression arg in call.Arguments)
                {
                    CollectExprCalls(arg, functionsByName, topLevelConstExprs, edges);
                }

                break;

            case LuaBinaryExpr bin:
                CollectExprCalls(bin.Left, functionsByName, topLevelConstExprs, edges);
                CollectExprCalls(bin.Right, functionsByName, topLevelConstExprs, edges);
                break;

            case LuaUnaryExpr un:
                CollectExprCalls(un.Operand, functionsByName, topLevelConstExprs, edges);
                break;

            case LuaIndexExpr idx:
                CollectExprCalls(idx.Target, functionsByName, topLevelConstExprs, edges);
                CollectExprCalls(idx.Index, functionsByName, topLevelConstExprs, edges);
                break;

            case LuaTableLiteralExpr tbl:
                foreach (LuaExpression el in tbl.Elements)
                {
                    CollectExprCalls(el, functionsByName, topLevelConstExprs, edges);
                }

                break;
        }
    }

    private static bool HasCycle(IReadOnlyDictionary<string, HashSet<string>> graph)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);

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
