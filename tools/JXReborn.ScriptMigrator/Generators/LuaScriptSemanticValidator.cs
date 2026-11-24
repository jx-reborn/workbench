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
/// exact item catalog existence (<see cref="PackItemCatalogIndex"/>), structured constant tables,
/// declarative <c>tbAwardTemplet</c> / <c>CreateNewSayEx</c> / pure <c>DailogClass</c> lowering,
/// party <c>PlayerIndex</c> + synchronous callback monomorphization, acyclic call graphs, and bounded loops.
/// </summary>
public sealed class LuaScriptSemanticValidator
{
    private static readonly HashSet<string> AllowedAwardFields = new(StringComparer.Ordinal)
    {
        "szName",
        "tbProp",
        "nCount",
        "nRate",
        "nQuality",
        "nBindState",
        "nExpiredTime",
        "nExpiredTime2",
        "nUsageTime",
        "nVersion",
        "nRandSeed",
        "tbParam",
        "nUpgradeLevel",
        "nCurDurability",
        "nMaxDurability",
        "nExp",
        "nExp_tl",
        "nJxb",
        "nRepute",
        "szFailMsg",
    };

    private static readonly HashSet<string> HarmlessRuntimeAwardMetadataFields = new(StringComparer.Ordinal)
    {
        "nExpiredTime",
        "nExpiredTime2",
        "nUsageTime",
    };

    private static readonly HashSet<string> TruthyBuiltinFunctionGlobals = new(StringComparer.Ordinal)
    {
        "NpcName2Replace",
    };

    private readonly IReadOnlyDictionary<int, string> _eventItemCodeByDetailId;
    private readonly PackItemCatalogIndex? _itemCatalogIndex;
    private IReadOnlyDictionary<string, LuaExpression>? _currentTopLevelConstExprs;
    private IReadOnlyDictionary<string, IReadOnlyList<LuaValueType>>? _currentConstTupleCols;

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
        LuaProgram program,
        IReadOnlyList<string>? includedVirtualKeys = null)
    {
        var reasons = new SortedSet<string>(StringComparer.Ordinal);

        // 1. Validate top-level statements: ONLY immutable constant declarations allowed.
        var topLevelConsts = new Dictionary<string, LuaValueType>(StringComparer.Ordinal);
        var topLevelConstExprs = new Dictionary<string, LuaExpression>(StringComparer.Ordinal);
        var constTupleCols = new Dictionary<string, IReadOnlyList<LuaValueType>>(StringComparer.Ordinal);
        var topLevelAwardNodes = new Dictionary<string, CompiledAwardNode>(StringComparer.Ordinal);
        _currentTopLevelConstExprs = topLevelConstExprs;
        _currentConstTupleCols = constTupleCols;

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
                    callStmt.Call.CalleeName.Equals("IL", StringComparison.OrdinalIgnoreCase) ||
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

                if (!TryValidateConstantExpr(
                    localDecl.Name,
                    localDecl.Initializer,
                    topLevelConsts,
                    topLevelConstExprs,
                    constTupleCols,
                    topLevelAwardNodes,
                    out LuaValueType constType,
                    out string? constErr))
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
                if (!TryValidateConstantExpr(
                    assignStmt.TargetName,
                    assignStmt.Value,
                    topLevelConsts,
                    topLevelConstExprs,
                    constTupleCols,
                    topLevelAwardNodes,
                    out LuaValueType constType,
                    out string? constErr))
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

        // 2. Locate entrypoint function.
        string expectedEntry = kind == GeneratedScriptKind.Monster ? "OnDeath" : "main";
        var functionsByName = program.Functions.ToDictionary(static f => f.Name, StringComparer.Ordinal);
        if (!functionsByName.TryGetValue(expectedEntry, out LuaFunctionDecl? entryFn))
        {
            reasons.Add($"missing-entrypoint:{expectedEntry}");
            return (null, reasons.ToArray());
        }

        // Reject any reachable function containing an isolated parser failure node first
        int countBeforeBodyUnsup = reasons.Count;
        foreach (LuaFunctionDecl fn in program.Functions)
        {
            CollectUnsupportedNodes(fn.Body, reasons);
        }

        if (reasons.Count > countBeforeBodyUnsup)
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
            constTupleCols,
            functionParamTypes,
            reasons);

        bool hasUnboundHelperParams = false;
        foreach (LuaFunctionDecl fn in program.Functions)
        {
            if (!string.Equals(fn.Name, expectedEntry, StringComparison.Ordinal) &&
                !functionParamTypes.ContainsKey(fn.Name))
            {
                reasons.Add($"unbound-helper-params:{fn.Name}({fn.Parameters.Count})");
                hasUnboundHelperParams = true;
            }
        }

        if (hasUnboundHelperParams)
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
                "PlayerIndex",
            };

            IReadOnlyList<LuaValueType> paramTypes = functionParamTypes[fn.Name];
            for (int p = 0; p < paramTypes.Count && p < fn.Parameters.Count; p++)
            {
                string pName = fn.Parameters[p];
                localTypes[pName] = paramTypes[p];
                assigned.Add(pName);
            }

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
                topLevelAwardNodes,
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

            if (!isEntry && returnType == LuaValueType.Int && returnTuple is null && !AllPathsReturn(fn.Body))
            {
                returnType = LuaValueType.NullableInt;
            }

            if (kind == GeneratedScriptKind.Monster && isAsync)
            {
                reasons.Add($"async-not-supported-in-monster-ondeath:{fn.Name}");
            }

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
                FunctionReturnTuples: fnReturnTuples,
                ConstTupleColumnTypes: constTupleCols,
                TopLevelAwardNodes: topLevelAwardNodes,
                IncludedVirtualKeys: includedVirtualKeys),
            []);
    }

    private void InferHelperParameterTypesTopDown(
        List<LuaFunctionDecl> bottomUpFunctions,
        string expectedEntry,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        IReadOnlyDictionary<string, LuaValueType> topLevelConsts,
        IReadOnlyDictionary<string, LuaExpression> topLevelConstExprs,
        IReadOnlyDictionary<string, IReadOnlyList<LuaValueType>> constTupleCols,
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

        for (int pass = 0; pass < 3; pass++)
        {
            for (int idx = bottomUpFunctions.Count - 1; idx >= 0; idx--)
            {
                LuaFunctionDecl fn = bottomUpFunctions[idx];
                var scopeTypes = new Dictionary<string, LuaValueType>(topLevelConsts, StringComparer.Ordinal)
                {
                    ["SubWorld"] = LuaValueType.Int,
                    ["PlayerIndex"] = LuaValueType.Int,
                };

                if (functionParamTypes.TryGetValue(fn.Name, out IReadOnlyList<LuaValueType>? myParams))
                {
                    for (int p = 0; p < myParams.Count && p < fn.Parameters.Count; p++)
                    {
                        scopeTypes[fn.Parameters[p]] = myParams[p];
                    }
                }

                ScanBlockForParamBindings(fn.Body, scopeTypes, functionsByName, topLevelConstExprs, constTupleCols, BindParams);
            }
        }
    }

    private static void ScanBlockForParamBindings(
        IReadOnlyList<LuaStatement> stmts,
        Dictionary<string, LuaValueType> scopeTypes,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        IReadOnlyDictionary<string, LuaExpression> topLevelConstExprs,
        IReadOnlyDictionary<string, IReadOnlyList<LuaValueType>> constTupleCols,
        Action<string, IReadOnlyList<LuaValueType>> bindParams)
    {
        foreach (LuaStatement stmt in stmts)
        {
            switch (stmt)
            {
                case LuaLocalDeclStmt ld:
                    if (ld.Initializer is not null)
                    {
                        LuaValueType t = QuickInferExprType(ld.Initializer, scopeTypes, functionsByName, constTupleCols, bindParams);
                        if (t != LuaValueType.Void)
                        {
                            scopeTypes[ld.Name] = t;
                        }
                    }

                    break;

                case LuaMultiLocalDeclStmt mld:
                    if (mld.Initializers.Count == 1 && mld.Initializers[0] is LuaCallExpr mc &&
                        mc.CalleeName is "GetWorldPos" or "GetPlayerRev" or "RevID2WXY")
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
                            LuaValueType t = QuickInferExprType(mld.Initializers[i], scopeTypes, functionsByName, constTupleCols, bindParams);
                            if (i < mld.Names.Count && mld.Names[i] != "_" && t != LuaValueType.Void)
                            {
                                scopeTypes[mld.Names[i]] = t;
                            }
                        }
                    }

                    break;

                case LuaAssignStmt asg:
                {
                    LuaValueType t = QuickInferExprType(asg.Value, scopeTypes, functionsByName, constTupleCols, bindParams);
                    if (t != LuaValueType.Void && asg.TargetName != "PlayerIndex")
                    {
                        scopeTypes[asg.TargetName] = t;
                    }

                    break;
                }

                case LuaMultiAssignStmt masg:
                    if (masg.Values.Count == 1 && masg.Values[0] is LuaCallExpr mac &&
                        mac.CalleeName is "GetWorldPos" or "GetPlayerRev" or "RevID2WXY")
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
                            LuaValueType t = QuickInferExprType(masg.Values[i], scopeTypes, functionsByName, constTupleCols, bindParams);
                            if (i < masg.TargetNames.Count && masg.TargetNames[i] != "_" && t != LuaValueType.Void)
                            {
                                scopeTypes[masg.TargetNames[i]] = t;
                            }
                        }
                    }

                    break;

                case LuaIndexAssignStmt iasg:
                    _ = QuickInferExprType(iasg.Index, scopeTypes, functionsByName, constTupleCols, bindParams);
                    _ = QuickInferExprType(iasg.Value, scopeTypes, functionsByName, constTupleCols, bindParams);
                    break;

                case LuaMemberAssignStmt mas:
                    _ = QuickInferExprType(mas.Target, scopeTypes, functionsByName, constTupleCols, bindParams);
                    _ = QuickInferExprType(mas.Value, scopeTypes, functionsByName, constTupleCols, bindParams);
                    break;

                case LuaCallStmt cs:
                    if (cs.Call.CalleeName == "tinsert" &&
                        cs.Call.Arguments.Count >= 2 &&
                        cs.Call.Arguments[^1] is LuaTableLiteralExpr optTuple)
                    {
                        TryQuickBindDialogOptionTuple(optTuple, scopeTypes, functionsByName, constTupleCols, bindParams);
                    }
                    else if (cs.Call.CalleeName == "CreateNewSayEx" &&
                             cs.Call.Arguments.Count == 2 &&
                             cs.Call.Arguments[1] is LuaTableLiteralExpr inlineOpts)
                    {
                        foreach (LuaExpression el in inlineOpts.Elements)
                        {
                            if (el is LuaTableLiteralExpr tup)
                            {
                                TryQuickBindDialogOptionTuple(tup, scopeTypes, functionsByName, constTupleCols, bindParams);
                            }
                        }
                    }

                    _ = QuickInferExprType(cs.Call, scopeTypes, functionsByName, constTupleCols, bindParams);
                    break;

                case LuaMemberCallStmt mcs:
                    if (mcs.Call.MemberName == "AddOptEntry" && mcs.Call.Arguments.Count is 2 or 3)
                    {
                        if (mcs.Call.Arguments[1] is LuaIdentifierExpr cbIdent)
                        {
                            if (mcs.Call.Arguments.Count == 2)
                            {
                                bindParams(cbIdent.Name, []);
                            }
                            else if (mcs.Call.Arguments[2] is LuaTableLiteralExpr argTbl)
                            {
                                var pTypes = argTbl.Elements
                                    .Select(a => QuickInferExprType(a, scopeTypes, functionsByName, constTupleCols, bindParams))
                                    .ToArray();
                                bindParams(cbIdent.Name, pTypes);
                            }
                        }
                    }

                    _ = QuickInferExprType(mcs.Call, scopeTypes, functionsByName, constTupleCols, bindParams);
                    break;

                case LuaReturnStmt ret:
                    if (ret.Value is not null) _ = QuickInferExprType(ret.Value, scopeTypes, functionsByName, constTupleCols, bindParams);
                    if (ret.ExtraValues is not null)
                    {
                        foreach (LuaExpression ev in ret.ExtraValues) _ = QuickInferExprType(ev, scopeTypes, functionsByName, constTupleCols, bindParams);
                    }

                    break;

                case LuaIfStmt ifs:
                    _ = QuickInferExprType(ifs.Condition, scopeTypes, functionsByName, constTupleCols, bindParams);
                    ScanBlockForParamBindings(ifs.ThenBody, new Dictionary<string, LuaValueType>(scopeTypes, StringComparer.Ordinal), functionsByName, topLevelConstExprs, constTupleCols, bindParams);
                    foreach (LuaElseIfBranch ei in ifs.ElseIfBranches)
                    {
                        _ = QuickInferExprType(ei.Condition, scopeTypes, functionsByName, constTupleCols, bindParams);
                        ScanBlockForParamBindings(ei.Body, new Dictionary<string, LuaValueType>(scopeTypes, StringComparer.Ordinal), functionsByName, topLevelConstExprs, constTupleCols, bindParams);
                    }

                    ScanBlockForParamBindings(ifs.ElseBody, new Dictionary<string, LuaValueType>(scopeTypes, StringComparer.Ordinal), functionsByName, topLevelConstExprs, constTupleCols, bindParams);
                    break;

                case LuaNumericForStmt nf:
                    _ = QuickInferExprType(nf.Start, scopeTypes, functionsByName, constTupleCols, bindParams);
                    _ = QuickInferExprType(nf.Limit, scopeTypes, functionsByName, constTupleCols, bindParams);
                    if (nf.Step is not null) _ = QuickInferExprType(nf.Step, scopeTypes, functionsByName, constTupleCols, bindParams);
                    var forScope = new Dictionary<string, LuaValueType>(scopeTypes, StringComparer.Ordinal)
                    {
                        [nf.IteratorName] = LuaValueType.Int,
                    };
                    ScanBlockForParamBindings(nf.Body, forScope, functionsByName, topLevelConstExprs, constTupleCols, bindParams);
                    break;

                case LuaWhileStmt ws:
                    _ = QuickInferExprType(ws.Condition, scopeTypes, functionsByName, constTupleCols, bindParams);
                    ScanBlockForParamBindings(ws.Body, new Dictionary<string, LuaValueType>(scopeTypes, StringComparer.Ordinal), functionsByName, topLevelConstExprs, constTupleCols, bindParams);
                    break;

                case LuaDoBlockStmt db:
                    ScanBlockForParamBindings(db.Body, new Dictionary<string, LuaValueType>(scopeTypes, StringComparer.Ordinal), functionsByName, topLevelConstExprs, constTupleCols, bindParams);
                    break;
            }
        }
    }

    private static void TryQuickBindDialogOptionTuple(
        LuaTableLiteralExpr optTuple,
        IReadOnlyDictionary<string, LuaValueType> scopeTypes,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        IReadOnlyDictionary<string, IReadOnlyList<LuaValueType>> constTupleCols,
        Action<string, IReadOnlyList<LuaValueType>> bindParams)
    {
        if (optTuple.Elements.Count is 2 or 3 && optTuple.Elements[1] is LuaIdentifierExpr cbIdent)
        {
            if (optTuple.Elements.Count == 2)
            {
                bindParams(cbIdent.Name, []);
            }
            else if (optTuple.Elements[2] is LuaTableLiteralExpr argTbl)
            {
                var pTypes = argTbl.Elements
                    .Select(a => QuickInferExprType(a, scopeTypes, functionsByName, constTupleCols, bindParams))
                    .ToArray();
                bindParams(cbIdent.Name, pTypes);
            }
        }
    }

    private static LuaValueType QuickInferExprType(
        LuaExpression expr,
        IReadOnlyDictionary<string, LuaValueType> scopeTypes,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        IReadOnlyDictionary<string, IReadOnlyList<LuaValueType>> constTupleCols,
        Action<string, IReadOnlyList<LuaValueType>> bindParams)
    {
        switch (expr)
        {
            case LuaNumberExpr num:
                return Math.Abs(num.Value - Math.Truncate(num.Value)) > 1e-9 ||
                       num.Value < int.MinValue ||
                       num.Value > int.MaxValue
                    ? LuaValueType.Number
                    : LuaValueType.Int;
            case LuaStringExpr str:
                TryBindParameterizedCallbackString(str.DecodedUtf8, bindParams);
                return LuaValueType.String;
            case LuaIdentifierExpr id:
                if (scopeTypes.TryGetValue(id.Name, out LuaValueType st))
                {
                    return st;
                }

                if (functionsByName.TryGetValue(id.Name, out LuaFunctionDecl? fnDecl) && fnDecl.Parameters.Count == 0)
                {
                    return LuaValueType.SyncCallback;
                }

                if (TruthyBuiltinFunctionGlobals.Contains(id.Name))
                {
                    return LuaValueType.Bool;
                }

                return LuaValueType.Int;
            case LuaIndexExpr idx:
                if (idx.Target is LuaIndexExpr innerIdx &&
                    innerIdx.Target is LuaIdentifierExpr tupleTblId &&
                    constTupleCols.TryGetValue(tupleTblId.Name, out IReadOnlyList<LuaValueType>? cols) &&
                    TryEvalConstantInt(idx.Index, out int col1) &&
                    col1 >= 1 &&
                    col1 <= cols.Count)
                {
                    return cols[col1 - 1];
                }

                _ = QuickInferExprType(idx.Index, scopeTypes, functionsByName, constTupleCols, bindParams);
                return QuickInferExprType(idx.Target, scopeTypes, functionsByName, constTupleCols, bindParams) switch
                {
                    LuaValueType.StringArray or LuaValueType.StringList or LuaValueType.IntKeyedStringMap => LuaValueType.String,
                    LuaValueType.Int2DArray or LuaValueType.IntKeyedIntArrayMap => LuaValueType.IntArray,
                    LuaValueType.String2DArray => LuaValueType.StringArray,
                    _ => LuaValueType.Int,
                };
            case LuaUnaryExpr un:
                LuaValueType ut = QuickInferExprType(un.Operand, scopeTypes, functionsByName, constTupleCols, bindParams);
                return un.Operator == LuaUnaryOp.Not ? LuaValueType.Bool : ut;
            case LuaBinaryExpr bin:
                LuaValueType lt = QuickInferExprType(bin.Left, scopeTypes, functionsByName, constTupleCols, bindParams);
                LuaValueType rt = QuickInferExprType(bin.Right, scopeTypes, functionsByName, constTupleCols, bindParams);
                if (bin.Operator == LuaBinaryOp.Concat) return LuaValueType.String;
                if (bin.Operator == LuaBinaryOp.Or && lt == LuaValueType.NullableInt && rt == LuaValueType.Int)
                {
                    return LuaValueType.Int;
                }

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
                if (IsLikelyDialogOptionTupleListLiteral(tbl))
                {
                    foreach (LuaExpression e in tbl.Elements)
                    {
                        if (e is LuaTableLiteralExpr tup)
                        {
                            TryQuickBindDialogOptionTuple(tup, scopeTypes, functionsByName, constTupleCols, bindParams);
                        }
                    }

                    return LuaValueType.DialogOptionList;
                }

                foreach (LuaExpression e in tbl.Elements) _ = QuickInferExprType(e, scopeTypes, functionsByName, constTupleCols, bindParams);
                return tbl.Elements.Count > 0 && QuickInferExprType(tbl.Elements[0], scopeTypes, functionsByName, constTupleCols, bindParams) == LuaValueType.String
                    ? LuaValueType.StringArray
                    : LuaValueType.IntArray;
            case LuaMemberCallExpr mcall:
                if (mcall.Receiver is LuaIdentifierExpr { Name: "DailogClass" } && mcall.MemberName == "new")
                {
                    foreach (LuaExpression a in mcall.Arguments) _ = QuickInferExprType(a, scopeTypes, functionsByName, constTupleCols, bindParams);
                    return LuaValueType.DialogBuilder;
                }

                foreach (LuaExpression a in mcall.Arguments) _ = QuickInferExprType(a, scopeTypes, functionsByName, constTupleCols, bindParams);
                return LuaValueType.Int;
            case LuaCallExpr call:
                if (call.CalleeName == "AskClientForNumber" && call.Arguments.Count >= 1 && call.Arguments[0] is LuaStringExpr askCb)
                {
                    bindParams(askCb.DecodedUtf8.Trim(), [LuaValueType.Int]);
                }

                if (call.CalleeName == "tonumber" && IsLongDateToNumberCall(call))
                {
                    return LuaValueType.Number;
                }

                var argTypes = new LuaValueType[call.Arguments.Count];
                for (int i = 0; i < call.Arguments.Count; i++)
                {
                    argTypes[i] = QuickInferExprType(call.Arguments[i], scopeTypes, functionsByName, constTupleCols, bindParams);
                }

                bindParams(call.CalleeName, argTypes);

                return call.CalleeName switch
                {
                    "GetName" or "GetAccount" or "GetFaction" or "GetLastAddFaction" or "tostring" or "strsub" or "strlower" or "strupper" or "format" or "GetLocalDate" or "date" or "GetNpcName" or "NpcName2Replace" => LuaValueType.String,
                    "tonumber" => LuaValueType.NullableInt,
                    _ => LuaValueType.Int,
                };
            default:
                return LuaValueType.Int;
        }
    }

    public static bool IsLongDateToNumberCall(LuaCallExpr tonumberCall)
    {
        if (tonumberCall.CalleeName != "tonumber" ||
            tonumberCall.Arguments.Count != 1 ||
            tonumberCall.Arguments[0] is not LuaCallExpr { CalleeName: "GetLocalDate" or "date", Arguments.Count: 1 } dateCall ||
            !TryEvalConstantString(dateCall.Arguments[0], out string? fmt) ||
            string.IsNullOrEmpty(fmt))
        {
            return false;
        }

        // Count max decimal digits produced by strftime specifiers (%Y=4, %j=3, %y/%m/%d/%H/%M/%S=2, %w=1)
        int digits = 0;
        for (int i = 0; i < fmt.Length; i++)
        {
            if (fmt[i] == '%' && i + 1 < fmt.Length)
            {
                i++;
                digits += fmt[i] switch
                {
                    'Y' => 4,
                    'j' => 3,
                    'y' or 'm' or 'd' or 'H' or 'M' or 'S' => 2,
                    'w' => 1,
                    _ => 0,
                };
            }
        }

        return digits >= 10;
    }

    private static bool IsLikelyDialogOptionTupleListLiteral(LuaTableLiteralExpr tbl)
    {
        if (tbl.Elements.Count == 0)
        {
            return false;
        }

        bool sawMultiOrSingleOptionTuple = false;
        foreach (LuaExpression el in tbl.Elements)
        {
            if (el is not LuaTableLiteralExpr inner || inner.Elements.Count is < 1 or > 3)
            {
                return false;
            }

            if (inner.Elements.Count == 1 ||
                inner.Elements[1] is LuaIdentifierExpr ||
                (inner.Elements.Count == 3 && inner.Elements[2] is LuaTableLiteralExpr))
            {
                sawMultiOrSingleOptionTuple = true;
            }
            else
            {
                return false;
            }
        }

        return sawMultiOrSingleOptionTuple;
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
                        if (valArg is LuaTableLiteralExpr { Elements.Count: >= 1 and <= 3 })
                        {
                            hints.TryAdd(id.Name, LuaValueType.DialogOptionList);
                            break;
                        }

                        LuaValueType elemGuess = valArg is LuaStringExpr or LuaBinaryExpr { Operator: LuaBinaryOp.Concat } or LuaCallExpr { CalleeName: "format" or "tostring" or "strsub" }
                            ? LuaValueType.StringList
                            : LuaValueType.IntList;
                        hints.TryAdd(id.Name, elemGuess);
                        break;
                    }
                    case LuaCallStmt { Call: { CalleeName: "CreateNewSayEx", Arguments.Count: 2 } cns }
                        when cns.Arguments[1] is LuaIdentifierExpr optId:
                    {
                        hints.TryAdd(optId.Name, LuaValueType.DialogOptionList);
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
        IReadOnlyDictionary<string, CompiledAwardNode> topLevelAwardNodes,
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
                            _ = ValidateExpr(localDecl.Initializer, kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                        }

                        break;
                    }

                    if (topLevelConsts.ContainsKey(localDecl.Name) || localDecl.Name is "SubWorld" or "PlayerIndex")
                    {
                        reasons.Add($"shadow-toplevel-const:{localDecl.Name}");
                        break;
                    }

                    if (localDecl.Initializer is null)
                    {
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
                            topLevelAwardNodes,
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
                    if (localDecl.Initializer is LuaTableLiteralExpr { Elements.Count: > 0 } tbl && rhsType is LuaValueType.StringArray or LuaValueType.IntArray)
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
                        topLevelAwardNodes,
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
                        _ = ValidateExpr(assignStmt.Value, kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                        break;
                    }

                    if (assignStmt.TargetName == "SubWorld")
                    {
                        reasons.Add($"mutate-readonly-global:SubWorld@line{assignStmt.Line}");
                        break;
                    }

                    if (assignStmt.TargetName == "PlayerIndex")
                    {
                        LuaValueType piType = ValidateExpr(assignStmt.Value, kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                        if (!IsIntCompatible(piType))
                        {
                            reasons.Add($"invalid-playerindex-assign:{piType}@line{assignStmt.Line}");
                        }

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
                            topLevelAwardNodes,
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
                    if (assignStmt.Value is LuaTableLiteralExpr { Elements.Count: > 0 } tbl && rhsType is LuaValueType.StringArray or LuaValueType.IntArray)
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
                        topLevelAwardNodes,
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

                    LuaValueType idxType = ValidateExpr(idxAssign.Index, kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                    LuaValueType valType = ValidateExpr(idxAssign.Value, kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
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

                case LuaMemberAssignStmt memberAssign:
                {
                    LuaValueType targetType = ValidateExpr(memberAssign.Target, kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                    LuaValueType valType = ValidateExpr(memberAssign.Value, kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                    if (targetType == LuaValueType.DialogBuilder && memberAssign.FieldName == "szTitleMsg")
                    {
                        if (valType != LuaValueType.String)
                        {
                            reasons.Add($"invalid-dialog-title-type:{valType}@line{memberAssign.Line}");
                        }
                        else if (TryEvalConstantString(memberAssign.Value, out string? titleLit) &&
                                 titleLit is not null &&
                                 titleLit.Contains("<lua", StringComparison.OrdinalIgnoreCase))
                        {
                            reasons.Add($"dynamic-lua-tag-in-title@line{memberAssign.Line}");
                        }
                    }
                    else if (targetType == LuaValueType.AwardTemplate &&
                             HarmlessRuntimeAwardMetadataFields.Contains(memberAssign.FieldName) &&
                             IsIntCompatible(valType))
                    {
                        // Metadata-only runtime expiration assignment (`%tbItem.nExpiredTime = ...`)
                    }
                    else
                    {
                        string targetDesc = memberAssign.Target is LuaIdentifierExpr tid ? tid.Name : targetType.ToString();
                        reasons.Add($"unsupported-member-assign:{targetDesc}.{memberAssign.FieldName}@line{memberAssign.Line}");
                    }

                    break;
                }

                case LuaMemberCallStmt memberCallStmt:
                    _ = ValidateMemberCall(
                        memberCallStmt.Call,
                        isStatement: true,
                        kind,
                        functionsByName,
                        functionParamTypes,
                        topLevelConsts,
                        topLevelAwardNodes,
                        localTypes,
                        assigned,
                        fnIsAsync,
                        fnReturnTypes,
                        ref isAsync,
                        reasons);
                    break;

                case LuaIfStmt ifStmt:
                {
                    LuaValueType condType = ValidateExpr(
                        ifStmt.Condition,
                        kind,
                        functionsByName,
                        functionParamTypes,
                        topLevelConsts,
                        topLevelAwardNodes,
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
                    ValidateBlock(ifStmt.ThenBody, kind, isEntry, fnName, loopDepth, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, emptyListHints, thenAssigned, new Dictionary<string, LuaTableLiteralExpr>(constArrays, StringComparer.Ordinal), fnIsAsync, fnReturnTypes, fnReturnTuples, ref isAsync, ref returnType, ref returnTuple, reasons);
                    branchAssignedSets.Add(thenAssigned);

                    foreach (LuaElseIfBranch ei in ifStmt.ElseIfBranches)
                    {
                        LuaValueType eiCondType = ValidateExpr(
                            ei.Condition,
                            kind,
                            functionsByName,
                            functionParamTypes,
                            topLevelConsts,
                            topLevelAwardNodes,
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
                        ValidateBlock(ei.Body, kind, isEntry, fnName, loopDepth, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, emptyListHints, eiAssigned, new Dictionary<string, LuaTableLiteralExpr>(constArrays, StringComparer.Ordinal), fnIsAsync, fnReturnTypes, fnReturnTuples, ref isAsync, ref returnType, ref returnTuple, reasons);
                        branchAssignedSets.Add(eiAssigned);
                    }

                    if (ifStmt.ElseBody.Count > 0)
                    {
                        var elseAssigned = new HashSet<string>(assigned, StringComparer.Ordinal);
                        ValidateBlock(ifStmt.ElseBody, kind, isEntry, fnName, loopDepth, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, emptyListHints, elseAssigned, new Dictionary<string, LuaTableLiteralExpr>(constArrays, StringComparer.Ordinal), fnIsAsync, fnReturnTypes, fnReturnTuples, ref isAsync, ref returnType, ref returnTuple, reasons);
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

                    LuaValueType stType = ValidateExpr(forStmt.Start, kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                    LuaValueType limType = ValidateExpr(forStmt.Limit, kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                    if (!IsIntCompatible(stType) || !IsIntCompatible(limType))
                    {
                        reasons.Add($"non-int-for-bounds@line{forStmt.Line}");
                    }

                    if (forStmt.Step is not null)
                    {
                        LuaValueType stepType = ValidateExpr(forStmt.Step, kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
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
                    ValidateBlock(forStmt.Body, kind, isEntry, fnName, loopDepth + 1, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, emptyListHints, bodyAssigned, new Dictionary<string, LuaTableLiteralExpr>(constArrays, StringComparer.Ordinal), fnIsAsync, fnReturnTypes, fnReturnTuples, ref isAsync, ref returnType, ref returnTuple, reasons);
                    break;
                }

                case LuaWhileStmt whileStmt:
                {
                    LuaValueType wCondType = ValidateExpr(whileStmt.Condition, kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                    if (!IsConditionCompatible(wCondType))
                    {
                        reasons.Add($"non-bool-condition@line{whileStmt.Line}");
                    }

                    var bodyAssigned = new HashSet<string>(assigned, StringComparer.Ordinal);
                    ValidateBlock(whileStmt.Body, kind, isEntry, fnName, loopDepth + 1, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, emptyListHints, bodyAssigned, new Dictionary<string, LuaTableLiteralExpr>(constArrays, StringComparer.Ordinal), fnIsAsync, fnReturnTypes, fnReturnTuples, ref isAsync, ref returnType, ref returnTuple, reasons);
                    break;
                }

                case LuaBreakStmt breakStmt:
                    if (loopDepth <= 0)
                    {
                        reasons.Add($"break-outside-loop@line{breakStmt.Line}");
                    }

                    break;

                case LuaDoBlockStmt doStmt:
                    ValidateBlock(doStmt.Body, kind, isEntry, fnName, loopDepth, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, emptyListHints, assigned, constArrays, fnIsAsync, fnReturnTypes, fnReturnTuples, ref isAsync, ref returnType, ref returnTuple, reasons);
                    break;

                case LuaCallStmt callStmt:
                    _ = ValidateCall(
                        callStmt.Call,
                        isStatement: true,
                        kind,
                        functionsByName,
                        functionParamTypes,
                        topLevelConsts,
                        topLevelAwardNodes,
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
                            NormalizeStorageType(ValidateExpr(retStmt.Value!, kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons)),
                        };
                        foreach (LuaExpression ev in retStmt.ExtraValues)
                        {
                            tuple.Add(NormalizeStorageType(ValidateExpr(ev, kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons)));
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
                            LuaValueType itemRet = ValidateExpr(retStmt.Value, kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
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
                            LuaValueType entryRet = ValidateExpr(retStmt.Value, kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
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
                                topLevelAwardNodes,
                                localTypes,
                                assigned,
                                fnIsAsync,
                                fnReturnTypes,
                                ref isAsync,
                                reasons));
                            if (rt == LuaValueType.Nil)
                            {
                                rt = LuaValueType.NullableInt;
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
        IReadOnlyDictionary<string, CompiledAwardNode> topLevelAwardNodes,
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
            if (topLevelConsts.ContainsKey(name) || name is "SubWorld" or "PlayerIndex")
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

        // Unpack single multi-return call: `local w, x, y = GetWorldPos()`, `m, r = GetPlayerRev()`, `w, x, y = RevID2WXY(...)`
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
            else if (singleCall.CalleeName == "RevID2WXY")
            {
                _ = ValidateCall(singleCall, isStatement: false, kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, constArrays, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                tupleTypes = [LuaValueType.Int, LuaValueType.Int, LuaValueType.Int];
            }
            else if (fnReturnTuples.TryGetValue(singleCall.CalleeName, out IReadOnlyList<LuaValueType>? userTuple))
            {
                _ = ValidateCall(singleCall, isStatement: false, kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, constArrays, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
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
            rhsTypes[i] = NormalizeStorageType(ValidateExpr(values[i], kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons));
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
        IReadOnlyDictionary<string, CompiledAwardNode> topLevelAwardNodes,
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
                if (ident.Name is "SubWorld" or "PlayerIndex")
                {
                    return LuaValueType.Int;
                }

                if (assigned.Contains(ident.Name))
                {
                    if (topLevelConsts.TryGetValue(ident.Name, out LuaValueType topType))
                    {
                        return topType;
                    }

                    if (localTypes.TryGetValue(ident.Name, out LuaValueType locType))
                    {
                        return locType;
                    }
                }

                if (functionsByName.TryGetValue(ident.Name, out LuaFunctionDecl? fnDecl))
                {
                    if (fnDecl.Parameters.Count == 0)
                    {
                        if (kind == GeneratedScriptKind.Monster && fnIsAsync.GetValueOrDefault(ident.Name, false))
                        {
                            reasons.Add($"async-callback-in-monster:{ident.Name}");
                            return LuaValueType.Void;
                        }

                        return LuaValueType.SyncCallback;
                    }

                    reasons.Add($"parameterized-fn-ref-not-allowed:{ident.Name}@line{ident.Line}");
                    return LuaValueType.Void;
                }

                if (TruthyBuiltinFunctionGlobals.Contains(ident.Name))
                {
                    return LuaValueType.Bool;
                }

                reasons.Add($"undefined-var:{ident.Name}");
                return LuaValueType.Void;

            case LuaTableLiteralExpr tbl:
                if (tbl.Elements.Count == 0)
                {
                    reasons.Add($"empty-table-literal@line{tbl.Line}");
                    return LuaValueType.Void;
                }

                if (IsLikelyDialogOptionTupleListLiteral(tbl))
                {
                    foreach (LuaExpression el in tbl.Elements)
                    {
                        ValidateDialogOptionTuple((LuaTableLiteralExpr)el, kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                    }

                    return LuaValueType.DialogOptionList;
                }

                LuaValueType elemType = LuaValueType.Void;
                for (int i = 0; i < tbl.Elements.Count; i++)
                {
                    LuaValueType et = NormalizeStorageType(ValidateExpr(tbl.Elements[i], kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons));
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

            case LuaKeyedTableLiteralExpr ktbl:
                reasons.Add($"unsupported-runtime-keyed-table@line{ktbl.Line}");
                return LuaValueType.Void;

            case LuaIndexExpr idxExpr:
            {
                // Special case: `constTupleTable[rowIdx][constCol]`
                if (idxExpr.Target is LuaIndexExpr rowIdxExpr &&
                    rowIdxExpr.Target is LuaIdentifierExpr tupleTblIdent &&
                    topLevelConsts.TryGetValue(tupleTblIdent.Name, out LuaValueType outerTblType) &&
                    outerTblType == LuaValueType.ConstTupleTable &&
                    _currentConstTupleCols is not null &&
                    _currentConstTupleCols.TryGetValue(tupleTblIdent.Name, out IReadOnlyList<LuaValueType>? tupleCols))
                {
                    LuaValueType rType = ValidateExpr(rowIdxExpr.Index, kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                    if (!IsIntCompatible(rType))
                    {
                        reasons.Add($"non-int-index@line{rowIdxExpr.Line}");
                        return LuaValueType.Void;
                    }

                    if (!TryEvalConstantInt(idxExpr.Index, _currentTopLevelConstExprs, out int col1Based) ||
                        col1Based < 1 ||
                        col1Based > tupleCols.Count)
                    {
                        reasons.Add($"dynamic-or-oob-tuple-col:{tupleTblIdent.Name}@line{idxExpr.Line}");
                        return LuaValueType.Void;
                    }

                    return tupleCols[col1Based - 1];
                }

                LuaValueType targetType = ValidateExpr(idxExpr.Target, kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                LuaValueType indexType = ValidateExpr(idxExpr.Index, kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                if (!IsIntCompatible(indexType))
                {
                    reasons.Add($"non-int-index@line{idxExpr.Line}");
                    return LuaValueType.Void;
                }

                return targetType switch
                {
                    LuaValueType.StringArray or LuaValueType.StringList or LuaValueType.IntKeyedStringMap => LuaValueType.String,
                    LuaValueType.IntArray or LuaValueType.IntList => LuaValueType.Int,
                    LuaValueType.IntKeyedIntMap => LuaValueType.NullableInt,
                    LuaValueType.Int2DArray or LuaValueType.IntKeyedIntArrayMap => LuaValueType.IntArray,
                    LuaValueType.String2DArray => LuaValueType.StringArray,
                    _ => FailExpr($"index-non-array:{targetType}@line{idxExpr.Line}", reasons),
                };
            }

            case LuaMemberAccessExpr macc:
            {
                string baseDesc = macc.Target is LuaIdentifierExpr id ? id.Name : "expr";
                reasons.Add($"unsupported-member-access:{baseDesc}.{macc.FieldName}@line{macc.Line}");
                return LuaValueType.Void;
            }

            case LuaMemberCallExpr mcall:
                return ValidateMemberCall(
                    mcall,
                    isStatement: false,
                    kind,
                    functionsByName,
                    functionParamTypes,
                    topLevelConsts,
                    topLevelAwardNodes,
                    localTypes,
                    assigned,
                    fnIsAsync,
                    fnReturnTypes,
                    ref isAsync,
                    reasons);

            case LuaUnaryExpr un:
            {
                LuaValueType opType = ValidateExpr(un.Operand, kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
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
                LuaValueType lt = ValidateExpr(bin.Left, kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                LuaValueType rt = ValidateExpr(bin.Right, kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);

                switch (bin.Operator)
                {
                    case LuaBinaryOp.Or when lt == LuaValueType.NullableInt && rt == LuaValueType.Int:
                        return LuaValueType.Int;

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

                        return TryEvalConstantInt(bin, _currentTopLevelConstExprs, out _) ? LuaValueType.Int : LuaValueType.Number;

                    case LuaBinaryOp.Pow:
                        if (!IsNumeric(lt) || !IsNumeric(rt))
                        {
                            reasons.Add($"non-int-arith:Pow@line{bin.Line}");
                            return LuaValueType.Number;
                        }

                        return TryEvalConstantInt(bin, _currentTopLevelConstExprs, out _) ? LuaValueType.Int : LuaValueType.Number;

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
                    topLevelAwardNodes,
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

    private LuaValueType ValidateMemberCall(
        LuaMemberCallExpr mcall,
        bool isStatement,
        GeneratedScriptKind kind,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        IReadOnlyDictionary<string, IReadOnlyList<LuaValueType>> functionParamTypes,
        IReadOnlyDictionary<string, LuaValueType> topLevelConsts,
        IReadOnlyDictionary<string, CompiledAwardNode> topLevelAwardNodes,
        IReadOnlyDictionary<string, LuaValueType> localTypes,
        IReadOnlySet<string> assigned,
        IReadOnlyDictionary<string, bool> fnIsAsync,
        IReadOnlyDictionary<string, LuaValueType> fnReturnTypes,
        ref bool isAsync,
        SortedSet<string> reasons)
    {
        // 1. tbAwardTemplet:GiveAwardByList / tbAwardTemplet:Give
        if (mcall.IsColonCall &&
            mcall.Receiver is LuaIdentifierExpr { Name: "tbAwardTemplet" } &&
            mcall.MemberName is "GiveAwardByList" or "Give")
        {
            if (mcall.Arguments.Count < 1 || mcall.Arguments.Count > 3)
            {
                reasons.Add($"invalid-awardtemplet-arity:{mcall.Arguments.Count}@line{mcall.Line}");
                return LuaValueType.Int;
            }

            LuaExpression awardExpr = mcall.Arguments[0];
            if (awardExpr is LuaIdentifierExpr awardIdent)
            {
                if (!topLevelAwardNodes.ContainsKey(awardIdent.Name))
                {
                    reasons.Add($"non-static-awardtemplet-table:{awardIdent.Name}@line{mcall.Line}");
                }
            }
            else if (!TryCompileAwardNode(awardExpr, _currentTopLevelConstExprs, out _, out string? awardErr))
            {
                reasons.Add(awardErr ?? $"invalid-awardtemplet-table@line{mcall.Line}");
            }

            if (mcall.MemberName == "GiveAwardByList")
            {
                // GiveAwardByList(tbItem, szLogTitle [, nAwardCount])
                if (mcall.Arguments.Count >= 2)
                {
                    _ = ValidateLogArgOrTable(mcall.Arguments[1], kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                }

                if (mcall.Arguments.Count == 3)
                {
                    LuaValueType cntType = ValidateExpr(mcall.Arguments[2], kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                    if (!IsIntCompatible(cntType))
                    {
                        reasons.Add($"invalid-awardtemplet-count:{cntType}@line{mcall.Line}");
                    }
                }
            }
            else
            {
                // Give(tbItem [, nAwardCount [, tbLogTitle]])
                if (mcall.Arguments.Count >= 2)
                {
                    LuaValueType cntType = ValidateExpr(mcall.Arguments[1], kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                    if (!IsIntCompatible(cntType))
                    {
                        reasons.Add($"invalid-awardtemplet-count:{cntType}@line{mcall.Line}");
                    }
                }

                if (mcall.Arguments.Count == 3)
                {
                    _ = ValidateLogArgOrTable(mcall.Arguments[2], kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                }
            }

            return isStatement ? LuaValueType.Void : LuaValueType.Int;
        }

        // 2. DailogClass:new(szNpcName)
        if (mcall.IsColonCall &&
            mcall.Receiver is LuaIdentifierExpr { Name: "DailogClass" } &&
            mcall.MemberName == "new")
        {
            if (mcall.Arguments.Count > 1 ||
                (mcall.Arguments.Count == 1 && ValidateExpr(mcall.Arguments[0], kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons) != LuaValueType.String))
            {
                reasons.Add($"invalid-dailogclass-new@line{mcall.Line}");
            }

            return LuaValueType.DialogBuilder;
        }

        // 3. tbDailog:AddOptEntry / tbDailog:Show
        if (mcall.IsColonCall && mcall.Receiver is LuaIdentifierExpr receiverId &&
            assigned.Contains(receiverId.Name) &&
            localTypes.TryGetValue(receiverId.Name, out LuaValueType recvType) &&
            recvType == LuaValueType.DialogBuilder)
        {
            if (!isStatement)
            {
                reasons.Add($"statement-used-as-expr:DailogClass:{mcall.MemberName}@line{mcall.Line}");
            }

            if (mcall.MemberName == "Show" && mcall.Arguments.Count == 0)
            {
                return LuaValueType.Void;
            }

            if (mcall.MemberName == "AddOptEntry" && mcall.Arguments.Count is 2 or 3)
            {
                var syntheticTupleElems = new List<LuaExpression>(mcall.Arguments);
                ValidateDialogOptionTuple(
                    new LuaTableLiteralExpr(syntheticTupleElems, mcall.Line),
                    kind,
                    functionsByName,
                    functionParamTypes,
                    topLevelConsts,
                    topLevelAwardNodes,
                    localTypes,
                    assigned,
                    fnIsAsync,
                    fnReturnTypes,
                    ref isAsync,
                    reasons);
                return LuaValueType.Void;
            }
        }

        string receiverDesc = mcall.Receiver is LuaIdentifierExpr rid ? rid.Name : "expr";
        string sep = mcall.IsColonCall ? ":" : ".";
        reasons.Add($"unsupported-member-call:{receiverDesc}{sep}{mcall.MemberName}");
        return LuaValueType.Void;
    }

    private LuaValueType ValidateLogArgOrTable(
        LuaExpression expr,
        GeneratedScriptKind kind,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        IReadOnlyDictionary<string, IReadOnlyList<LuaValueType>> functionParamTypes,
        IReadOnlyDictionary<string, LuaValueType> topLevelConsts,
        IReadOnlyDictionary<string, CompiledAwardNode> topLevelAwardNodes,
        IReadOnlyDictionary<string, LuaValueType> localTypes,
        IReadOnlySet<string> assigned,
        IReadOnlyDictionary<string, bool> fnIsAsync,
        IReadOnlyDictionary<string, LuaValueType> fnReturnTypes,
        ref bool isAsync,
        SortedSet<string> reasons)
    {
        if (expr is LuaNilExpr)
        {
            return LuaValueType.Nil;
        }

        if (expr is LuaTableLiteralExpr tbl)
        {
            foreach (LuaExpression el in tbl.Elements)
            {
                _ = ValidateLogArgOrTable(el, kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
            }

            return LuaValueType.StringArray;
        }

        if (expr is LuaKeyedTableLiteralExpr ktbl)
        {
            foreach (LuaTableFieldEntry fe in ktbl.Entries)
            {
                if (fe.BracketKey is not null)
                {
                    _ = ValidateLogArgOrTable(fe.BracketKey, kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                }

                _ = ValidateLogArgOrTable(fe.Value, kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
            }

            return LuaValueType.StringArray;
        }

        return ValidateExpr(expr, kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
    }

    private void ValidateDialogOptionTuple(
        LuaTableLiteralExpr optTuple,
        GeneratedScriptKind kind,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        IReadOnlyDictionary<string, IReadOnlyList<LuaValueType>> functionParamTypes,
        IReadOnlyDictionary<string, LuaValueType> topLevelConsts,
        IReadOnlyDictionary<string, CompiledAwardNode> topLevelAwardNodes,
        IReadOnlyDictionary<string, LuaValueType> localTypes,
        IReadOnlySet<string> assigned,
        IReadOnlyDictionary<string, bool> fnIsAsync,
        IReadOnlyDictionary<string, LuaValueType> fnReturnTypes,
        ref bool isAsync,
        SortedSet<string> reasons)
    {
        if (optTuple.Elements.Count is < 1 or > 3)
        {
            reasons.Add($"invalid-dialog-option-tuple-arity:{optTuple.Elements.Count}@line{optTuple.Line}");
            return;
        }

        LuaValueType labelType = ValidateExpr(optTuple.Elements[0], kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
        if (labelType != LuaValueType.String)
        {
            reasons.Add($"invalid-dialog-option-label:{labelType}@line{optTuple.Line}");
            return;
        }

        if (optTuple.Elements.Count == 1 || optTuple.Elements[1] is LuaNilExpr)
        {
            return;
        }

        if (optTuple.Elements[1] is not LuaIdentifierExpr cbIdent || !functionsByName.ContainsKey(cbIdent.Name))
        {
            string cbDesc = optTuple.Elements[1] is LuaIdentifierExpr uId ? uId.Name : optTuple.Elements[1].GetType().Name;
            reasons.Add($"missing-or-dynamic-dialog-callback:{cbDesc}@line{optTuple.Line}");
            return;
        }

        IReadOnlyList<LuaValueType> expectedParams = functionParamTypes.GetValueOrDefault(cbIdent.Name, []);
        if (optTuple.Elements.Count == 2)
        {
            if (expectedParams.Count != 0)
            {
                reasons.Add($"dialog-callback-arity-mismatch:{cbIdent.Name}(0!={expectedParams.Count})@line{optTuple.Line}");
            }

            return;
        }

        if (optTuple.Elements[2] is not LuaTableLiteralExpr paramTbl)
        {
            reasons.Add($"dynamic-dialog-callback-params:{cbIdent.Name}@line{optTuple.Line}");
            return;
        }

        if (paramTbl.Elements.Count != expectedParams.Count)
        {
            reasons.Add($"dialog-callback-arity-mismatch:{cbIdent.Name}({paramTbl.Elements.Count}!={expectedParams.Count})@line{optTuple.Line}");
            return;
        }

        for (int i = 0; i < paramTbl.Elements.Count; i++)
        {
            LuaValueType actual = ValidateExpr(paramTbl.Elements[i], kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
            if (!IsArgAssignableToParam(actual, expectedParams[i]))
            {
                reasons.Add($"invalid-dialog-callback-param:{cbIdent.Name}[{i}]({actual}!={expectedParams[i]})");
            }
        }
    }

    private LuaValueType ValidateCall(
        LuaCallExpr call,
        bool isStatement,
        GeneratedScriptKind kind,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        IReadOnlyDictionary<string, IReadOnlyList<LuaValueType>> functionParamTypes,
        IReadOnlyDictionary<string, LuaValueType> topLevelConsts,
        IReadOnlyDictionary<string, CompiledAwardNode> topLevelAwardNodes,
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
            api.Equals("IL", StringComparison.OrdinalIgnoreCase) ||
            api.Equals("dofile", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add($"include:{api}");
            return LuaValueType.Void;
        }

        // Check local SyncCallback parameter invocation (`fnCallback()`)
        if (assigned.Contains(api) && localTypes.TryGetValue(api, out LuaValueType localCalleeType) && localCalleeType == LuaValueType.SyncCallback)
        {
            if (args.Count != 0)
            {
                reasons.Add($"invalid-synccallback-arity:{api}({args.Count})@line{call.Line}");
            }

            if (kind != GeneratedScriptKind.Monster)
            {
                isAsync = true;
            }

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
                LuaValueType actualArg = ValidateExpr(args[i], kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
                if (!IsArgAssignableToParam(actualArg, expectedParams[i]))
                {
                    reasons.Add($"invalid-helper-arg-type:{api}[{i}]({actualArg}!={expectedParams[i]})");
                }

                if (expectedParams[i] == LuaValueType.SyncCallback &&
                    args[i] is LuaIdentifierExpr cbId &&
                    fnIsAsync.GetValueOrDefault(cbId.Name, false))
                {
                    isAsync = true;
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
            ValidateExpr(args[index], kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref localAsync, reasons);

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
                case "GetPKFlag":
                case "GetForbidChangePK":
                case "GetPunish":
                case "GetRank":
                case "GetLastFactionNumber":
                case "GetLastDiagNpc":
                case "GetCurServerTime":
                case "GetTimerId":
                case "GetLogoutRV":
                case "IsDisabledUseTownP":
                case "GetTeamSize":
                case "IsCaptain":
                    RequireArgCount(0);
                    return LuaValueType.Int;

                // Multi-return getters usable in scalar position
                case "GetWorldPos":
                case "GetPlayerRev":
                    RequireArgCount(0);
                    return LuaValueType.Int;

                case "RevID2WXY":
                    if (args.Count == 1 && args[0] is LuaCallExpr { CalleeName: "GetPlayerRev", Arguments.Count: 0 })
                    {
                        return LuaValueType.Int;
                    }

                    if (RequireArgCount(2))
                    {
                        if (!IsIntCompatible(ArgType(0)) || !IsIntCompatible(ArgType(1)))
                        {
                            reasons.Add($"invalid-arg-type:RevID2WXY@line{call.Line}");
                        }
                    }

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
                case "GetTeamMember":
                    if (RequireArgCount(1) && !IsIntCompatible(ArgType(0)))
                    {
                        reasons.Add($"invalid-arg-type:{api}@line{call.Line}");
                    }

                    return LuaValueType.Int;

                case "CountFreeRoomByWH":
                    if (RequireArgRange(2, 4))
                    {
                        for (int i = 0; i < args.Count; i++)
                        {
                            if (!IsIntCompatible(ArgType(i)))
                            {
                                reasons.Add($"invalid-arg-type:CountFreeRoomByWH@line{call.Line}");
                            }
                        }
                    }

                    return LuaValueType.Int;

                case "IsMyItem":
                    if (kind != GeneratedScriptKind.Item)
                    {
                        reasons.Add("item-only-api:IsMyItem");
                    }

                    if (RequireArgCount(1) && !IsIntCompatible(ArgType(0)))
                    {
                        reasons.Add($"invalid-arg-type:IsMyItem@line{call.Line}");
                    }

                    return LuaValueType.Int;

                case "RemoveItemByIndex":
                    if (RequireArgCount(1) && !IsIntCompatible(ArgType(0)))
                    {
                        reasons.Add($"invalid-arg-type:RemoveItemByIndex@line{call.Line}");
                    }

                    return isStatement ? LuaValueType.Void : LuaValueType.Int;

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

                    return IsLongDateToNumberCall(call) ? LuaValueType.Number : LuaValueType.NullableInt;

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

                case "Pay":
                case "Add120SkillExp":
                    if (RequireArgCount(1) && !IsNumeric(ArgType(0)))
                    {
                        reasons.Add($"invalid-arg-type:{api}@line{call.Line}");
                    }

                    return api == "Add120SkillExp" && isStatement ? LuaValueType.Void : LuaValueType.Int;

                case "SetFaction":
                    if (RequireArgCount(1))
                    {
                        if (!TryEvalConstantString(args[0], out string? facName) || !IsKnownFactionOrEmpty(facName))
                        {
                            reasons.Add($"invalid-setfaction-arg@line{call.Line}");
                        }
                    }

                    return isStatement ? LuaValueType.Void : LuaValueType.Int;

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

                case "getn":
                    if (RequireArgCount(1))
                    {
                        LuaValueType at = ArgType(0);
                        if (at is not (LuaValueType.StringArray or LuaValueType.IntArray or LuaValueType.Int2DArray or LuaValueType.String2DArray or LuaValueType.ConstTupleTable or LuaValueType.StringList or LuaValueType.IntList or LuaValueType.DialogOptionList))
                        {
                            reasons.Add($"invalid-arg-type:getn({at})@line{call.Line}");
                        }
                    }

                    return LuaValueType.Int;

                case "tinsert":
                    if (!isStatement)
                    {
                        reasons.Add($"statement-used-as-expr:tinsert@line{call.Line}");
                    }

                    if (RequireArgRange(2, 3))
                    {
                        LuaValueType lt = ArgType(0);
                        LuaExpression valExpr = args.Count == 2 ? args[1] : args[2];
                        if (args.Count == 3 && !IsIntCompatible(ArgType(1)))
                        {
                            reasons.Add($"invalid-arg-type:tinsert-pos@line{call.Line}");
                        }

                        if (lt == LuaValueType.DialogOptionList && valExpr is LuaTableLiteralExpr optTuple)
                        {
                            ValidateDialogOptionTuple(optTuple, kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref localAsync, reasons);
                        }
                        else
                        {
                            LuaValueType valType = args.Count == 2 ? ArgType(1) : ArgType(2);
                            if (lt == LuaValueType.StringList && valType is LuaValueType.String or LuaValueType.Int)
                            {
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
                    }

                    return LuaValueType.Void;

                // EventItem queries: HaveItem(idOrName), GetItemCount(idOrName), GetItemCountEx(idOrName)
                case "HaveItem":
                case "GetItemCount":
                case "GetItemCountEx":
                    if (RequireArgCount(1))
                    {
                        _ = ArgType(0);
                        ValidateEventItemOperand(args[0], api, call.Line, topLevelConsts, constArrays, reasons);
                    }

                    return LuaValueType.Int;

                // EventItem mutations: AddEventItem(idOrNameOrCol), DelItem(idOrNameOrCol)
                case "AddEventItem":
                case "DelItem":
                    if (!isStatement)
                    {
                        reasons.Add($"statement-used-as-expr:{api}@line{call.Line}");
                    }

                    if (RequireArgCount(1))
                    {
                        _ = ArgType(0);
                        ValidateEventItemOperand(args[0], api, call.Line, topLevelConsts, constArrays, reasons);
                    }

                    return LuaValueType.Void;

                // AddGoldItem(0, rowIdOrCol)
                case "AddGoldItem":
                    if (RequireArgCount(2))
                    {
                        if (!TryResolveConstInt(args[0], out int goldGenre) || goldGenre != 0)
                        {
                            reasons.Add($"invalid-addgolditem-genre@line{call.Line}");
                        }

                        _ = ArgType(1);
                        if (TryResolveConstInt(args[1], out int rowId))
                        {
                            if (_itemCatalogIndex is null || !_itemCatalogIndex.TryResolveGoldEquipByRowId(rowId, out _))
                            {
                                reasons.Add($"unknown-gold-item-row:{rowId}");
                            }
                        }
                        else if (TryExtractConstTableIntCandidates(args[1], _currentTopLevelConstExprs, out List<int>? candRows) && candRows.Count > 0)
                        {
                            foreach (int r in candRows)
                            {
                                if (_itemCatalogIndex is null || !_itemCatalogIndex.TryResolveGoldEquipByRowId(r, out _))
                                {
                                    reasons.Add($"unknown-gold-item-row:{r}");
                                }
                            }
                        }
                        else
                        {
                            reasons.Add($"dynamic-gold-item:AddGoldItem@line{call.Line}");
                        }
                    }

                    return isStatement ? LuaValueType.Void : LuaValueType.Int;

                // General GDPL item APIs
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

                        ValidateGdplOperands(args[0], args[1], args[2], args[3], api, call.Line, topLevelConsts, constArrays, reasons);
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

                        ValidateGdplOperands(args[1], args[2], args[3], args[4], api, call.Line, topLevelConsts, constArrays, reasons);
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
                        ValidateGdplOperands(args[0], args[1], args[2], args[3], api, call.Line, topLevelConsts, constArrays, reasons);
                    }

                    return LuaValueType.Int;

                case "ConsumeEquiproomItem":
                    if (RequireArgCount(5))
                    {
                        if (!IsIntCompatible(ArgType(0)))
                        {
                            reasons.Add($"invalid-arg-type:ConsumeEquiproomItem@line{call.Line}");
                        }

                        ValidateGdplOperands(args[1], args[2], args[3], args[4], api, call.Line, topLevelConsts, constArrays, reasons);
                    }

                    return LuaValueType.Int;

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
                case "StopTimer":
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
                case "SetPKFlag":
                case "ForbidChangePK":
                case "SetPunish":
                case "SetLogoutRV":
                case "DisabledUseTownP":
                case "DelMagic":
                case "SyncTaskValue":
                case "OpenStoreBox":
                    if (!isStatement)
                    {
                        reasons.Add($"statement-used-as-expr:{api}@line{call.Line}");
                    }

                    if (RequireArgCount(1) && !(api is "Earn" or "AddOwnExp" ? IsNumeric(ArgType(0)) : IsIntCompatible(ArgType(0))))
                    {
                        reasons.Add($"invalid-arg-type:{api}@line{call.Line}");
                    }

                    return LuaValueType.Void;

                case "SetTimer":
                    if (RequireArgCount(2))
                    {
                        if (!IsNumeric(ArgType(0)) || !IsIntCompatible(ArgType(1)))
                        {
                            reasons.Add($"invalid-arg-type:SetTimer@line{call.Line}");
                        }
                    }

                    return isStatement ? LuaValueType.Void : LuaValueType.Int;

                case "SetTempRevPos":
                    if (!isStatement)
                    {
                        reasons.Add($"statement-used-as-expr:SetTempRevPos@line{call.Line}");
                    }

                    if (args.Count is not (1 or 3))
                    {
                        reasons.Add($"invalid-arity:SetTempRevPos({args.Count})@line{call.Line}");
                    }
                    else
                    {
                        for (int i = 0; i < args.Count; i++)
                        {
                            if (!IsNumeric(ArgType(i)))
                            {
                                reasons.Add($"invalid-arg-type:SetTempRevPos@line{call.Line}");
                            }
                        }
                    }

                    return LuaValueType.Void;

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

                case "LG_ApplyDoScript":
                    if (!isStatement)
                    {
                        reasons.Add($"statement-used-as-expr:LG_ApplyDoScript@line{call.Line}");
                    }

                    if (args.Count >= 6 &&
                        TryEvalConstantString(args[3], out string? relayScript) &&
                        relayScript is not null &&
                        relayScript.Replace('/', '\\').Equals(@"\script\event\msg2allworld.lua", StringComparison.OrdinalIgnoreCase))
                    {
                        for (int i = 0; i < args.Count; i++)
                        {
                            _ = ArgType(i);
                        }
                    }
                    else
                    {
                        reasons.Add($"api:LG_ApplyDoScript");
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

                case "SetRevPos":
                    if (!isStatement)
                    {
                        reasons.Add($"statement-used-as-expr:{api}@line{call.Line}");
                    }

                    if (args.Count == 1 && args[0] is LuaCallExpr { CalleeName: "GetPlayerRev", Arguments.Count: 0 })
                    {
                        return LuaValueType.Void;
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

                case "OpenBox":
                    if (!isStatement)
                    {
                        reasons.Add($"statement-used-as-expr:{api}@line{call.Line}");
                    }

                    RequireArgCount(0);
                    return LuaValueType.Void;

                // ShowLadder(count, id1, ...): LuaShowLadder clamps count to Math.Min(declaredCount, argc - 1)
                case "ShowLadder":
                    if (!isStatement)
                    {
                        reasons.Add($"statement-used-as-expr:{api}@line{call.Line}");
                    }

                    if (args.Count < 2 || !TryResolveConstInt(args[0], out int ladderCount) || ladderCount <= 0)
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

                case "NewWorld":
                    if (RequireArgCount(3))
                    {
                        if (!IsNumeric(ArgType(0)) || !IsNumeric(ArgType(1)) || !IsNumeric(ArgType(2)))
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
                        if (!IsNumeric(ArgType(0)) || !IsNumeric(ArgType(1)))
                        {
                            reasons.Add($"invalid-arg-type:SetPos@line{call.Line}");
                        }
                    }

                    localAsync = true;
                    return LuaValueType.Void;

                case "ReturnFromPortal":
                    RequireArgCount(0);
                    localAsync = true;
                    return isStatement ? LuaValueType.Void : LuaValueType.Int;

                case "Talk":
                    if (!isStatement)
                    {
                        reasons.Add($"statement-used-as-expr:Talk@line{call.Line}");
                    }

                    ValidateTalkCall(call, kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref localAsync, reasons);
                    return LuaValueType.Void;

                case "Say":
                case "Describe":
                    if (!isStatement)
                    {
                        reasons.Add($"statement-used-as-expr:{api}@line{call.Line}");
                    }

                    ValidateSayOrDescribeCall(call, kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, constArrays, fnIsAsync, fnReturnTypes, ref localAsync, reasons);
                    return LuaValueType.Void;

                case "CreateNewSayEx":
                    if (!isStatement)
                    {
                        reasons.Add($"statement-used-as-expr:CreateNewSayEx@line{call.Line}");
                    }

                    if (RequireArgCount(2))
                    {
                        LuaValueType titleType = ArgType(0);
                        LuaValueType optType = ArgType(1);
                        if (titleType != LuaValueType.String)
                        {
                            reasons.Add($"invalid-createnewsayex-title:{titleType}@line{call.Line}");
                        }

                        if (optType != LuaValueType.DialogOptionList)
                        {
                            reasons.Add($"invalid-createnewsayex-options:{optType}@line{call.Line}");
                        }
                    }

                    return LuaValueType.Void;

                case "CreateTaskSay":
                    if (!isStatement)
                    {
                        reasons.Add($"statement-used-as-expr:CreateTaskSay@line{call.Line}");
                    }

                    if (RequireArgCount(1))
                    {
                        LuaValueType tbType = ArgType(0);
                        if (tbType is not (LuaValueType.StringArray or LuaValueType.StringList))
                        {
                            reasons.Add($"invalid-createtasksay-arg:{tbType}@line{call.Line}");
                        }
                        else if (args[0] is LuaTableLiteralExpr ctsLit)
                        {
                            for (int i = 1; i < ctsLit.Elements.Count; i++)
                            {
                                if (TryEvalConstantString(ctsLit.Elements[i], out string? optStr) && optStr is not null)
                                {
                                    ValidateSayOptionString(optStr, functionsByName, reasons);
                                }
                            }
                        }
                        else if (args[0] is LuaIdentifierExpr ctsId &&
                                 constArrays is not null &&
                                 constArrays.TryGetValue(ctsId.Name, out LuaTableLiteralExpr? ctsArr))
                        {
                            for (int i = 1; i < ctsArr.Elements.Count; i++)
                            {
                                if (TryEvalConstantString(ctsArr.Elements[i], out string? optStr) && optStr is not null)
                                {
                                    ValidateSayOptionString(optStr, functionsByName, reasons);
                                }
                            }
                        }
                    }

                    return LuaValueType.Void;

                case "TaskSay":
                    if (!isStatement)
                    {
                        reasons.Add($"statement-used-as-expr:TaskSay@line{call.Line}");
                    }

                    if (RequireArgCount(2))
                    {
                        if (ArgType(0) != LuaValueType.String)
                        {
                            reasons.Add($"invalid-tasksay-caption@line{call.Line}");
                        }

                        LuaValueType optArrType = ArgType(1);
                        if (optArrType is not (LuaValueType.StringArray or LuaValueType.StringList))
                        {
                            reasons.Add($"invalid-tasksay-options:{optArrType}@line{call.Line}");
                        }
                    }

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

    private void ValidateEventItemOperand(
        LuaExpression arg,
        string api,
        int line,
        IReadOnlyDictionary<string, LuaValueType> topLevelConsts,
        IReadOnlyDictionary<string, LuaTableLiteralExpr>? constArrays,
        SortedSet<string> reasons)
    {
        if (TryEvalConstantIntWithScope(arg, topLevelConsts, constArrays, out int eventId))
        {
            if (!HasEventItem(eventId))
            {
                reasons.Add($"unknown-event-item:{eventId}");
            }

            return;
        }

        if (TryEvalConstantString(arg, out string? itemName) && itemName is not null)
        {
            if (_itemCatalogIndex is null || !_itemCatalogIndex.TryResolveEventItemByName(itemName, out _, out _))
            {
                reasons.Add($"unknown-event-item-name:{itemName.Trim()}");
            }

            return;
        }

        if (TryExtractConstTableIntCandidates(arg, _currentTopLevelConstExprs, out List<int>? candIds) && candIds.Count > 0)
        {
            foreach (int id in candIds)
            {
                if (!HasEventItem(id))
                {
                    reasons.Add($"unknown-event-item:{id}");
                }
            }

            return;
        }

        reasons.Add($"dynamic-event-item:{api}@line{line}");
    }

    private void ValidateGdplOperands(
        LuaExpression gExpr,
        LuaExpression dExpr,
        LuaExpression pExpr,
        LuaExpression lvExpr,
        string api,
        int line,
        IReadOnlyDictionary<string, LuaValueType> topLevelConsts,
        IReadOnlyDictionary<string, LuaTableLiteralExpr>? constArrays,
        SortedSet<string> reasons)
    {
        if (TryEvalConstantIntWithScope(gExpr, topLevelConsts, constArrays, out int g) &&
            TryEvalConstantIntWithScope(dExpr, topLevelConsts, constArrays, out int d) &&
            TryEvalConstantIntWithScope(lvExpr, topLevelConsts, constArrays, out int lv))
        {
            if (TryEvalConstantIntWithScope(pExpr, topLevelConsts, constArrays, out int p))
            {
                if (_itemCatalogIndex is null || !_itemCatalogIndex.TryResolveByGdpl(g, d, p, lv, out _))
                {
                    reasons.Add($"unknown-gdpl:{g},{d},{p},{lv}");
                }

                return;
            }

            if (TryExtractConstTableIntCandidates(pExpr, _currentTopLevelConstExprs, out List<int>? pCandidates) && pCandidates.Count > 0)
            {
                foreach (int candP in pCandidates)
                {
                    if (_itemCatalogIndex is null || !_itemCatalogIndex.TryResolveByGdpl(g, d, candP, lv, out _))
                    {
                        reasons.Add($"unknown-gdpl:{g},{d},{candP},{lv}");
                    }
                }

                return;
            }
        }

        reasons.Add($"dynamic-gdpl:{api}@line{line}");
    }

    public static bool TryExtractConstTableIntCandidates(
        LuaExpression expr,
        IReadOnlyDictionary<string, LuaExpression>? topLevelConstExprs,
        out List<int> candidates)
    {
        candidates = [];
        if (topLevelConstExprs is null)
        {
            return false;
        }

        // Pattern 1: `tbl[idxExpr]` where `tbl` is 1D table of ints
        if (expr is LuaIndexExpr { Target: LuaIdentifierExpr tbl1Id } &&
            topLevelConstExprs.TryGetValue(tbl1Id.Name, out LuaExpression? rawTbl1) &&
            rawTbl1 is LuaTableLiteralExpr seq1)
        {
            foreach (LuaExpression el in seq1.Elements)
            {
                if (!TryEvalConstantInt(el, topLevelConstExprs, out int v))
                {
                    return false;
                }

                candidates.Add(v);
            }

            return candidates.Count > 0;
        }

        // Pattern 2: `tbl[rowExpr][constCol]` where `tbl` is 2D/tuple table
        if (expr is LuaIndexExpr { Target: LuaIndexExpr { Target: LuaIdentifierExpr tbl2Id }, Index: var colExpr } &&
            TryEvalConstantInt(colExpr, topLevelConstExprs, out int col1Based) &&
            col1Based >= 1 &&
            topLevelConstExprs.TryGetValue(tbl2Id.Name, out LuaExpression? rawTbl2) &&
            rawTbl2 is LuaTableLiteralExpr seq2)
        {
            foreach (LuaExpression rowExpr in seq2.Elements)
            {
                if (rowExpr is not LuaTableLiteralExpr rowTbl)
                {
                    return false;
                }

                if (col1Based <= rowTbl.Elements.Count)
                {
                    if (!TryEvalConstantInt(rowTbl.Elements[col1Based - 1], topLevelConstExprs, out int v))
                    {
                        return false;
                    }

                    candidates.Add(v);
                }
            }

            return candidates.Count > 0;
        }

        return false;
    }

    private bool HasEventItem(int detailType) =>
        (_itemCatalogIndex is not null && _itemCatalogIndex.TryResolveEventItem(detailType, out _)) ||
        _eventItemCodeByDetailId.ContainsKey(detailType);

    private static bool IsKnownFactionOrEmpty(string? name) =>
        name is not null && name.Trim().ToLowerInvariant() is
            "" or "shaolin" or "tianwang" or "tangmen" or "wudu" or "emei" or "cuiyan" or "gaibang" or "tianren" or "wudang" or "kunlun" or "huashan";

    private void ValidateTalkCall(
        LuaCallExpr call,
        GeneratedScriptKind kind,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        IReadOnlyDictionary<string, IReadOnlyList<LuaValueType>> functionParamTypes,
        IReadOnlyDictionary<string, LuaValueType> topLevelConsts,
        IReadOnlyDictionary<string, CompiledAwardNode> topLevelAwardNodes,
        IReadOnlyDictionary<string, LuaValueType> localTypes,
        IReadOnlySet<string> assigned,
        IReadOnlyDictionary<string, bool> fnIsAsync,
        IReadOnlyDictionary<string, LuaValueType> fnReturnTypes,
        ref bool isAsync,
        SortedSet<string> reasons)
    {
        // Binary Ground Truth (`LuaTalkUI` @ KLua_2_1_UIAndDialog.cpp:25): `if (2 < lua_gettop(L))` — < 3 args is a runtime no-op!
        if (call.Arguments.Count < 3)
        {
            for (int i = 0; i < call.Arguments.Count; i++)
            {
                _ = ValidateExpr(call.Arguments[i], kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
            }

            return;
        }

        if (!TryEvalConstantInt(call.Arguments[0], _currentTopLevelConstExprs, out int count) || count <= 0)
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
            LuaValueType pt = ValidateExpr(call.Arguments[i], kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
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
        IReadOnlyDictionary<string, CompiledAwardNode> topLevelAwardNodes,
        IReadOnlyDictionary<string, LuaValueType> localTypes,
        IReadOnlySet<string> assigned,
        IReadOnlyDictionary<string, LuaTableLiteralExpr>? constArrays,
        IReadOnlyDictionary<string, bool> fnIsAsync,
        IReadOnlyDictionary<string, LuaValueType> fnReturnTypes,
        ref bool isAsync,
        SortedSet<string> reasons)
    {
        if (call.Arguments.Count < 1)
        {
            reasons.Add($"invalid-say-arity@line{call.Line}");
            return;
        }

        LuaValueType promptType = ValidateExpr(call.Arguments[0], kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
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

        // Binary Ground Truth (`LuaSelectUI` @ KLua_2_2_DialogUI.cpp:294): 1-arg `Say(prompt)` sends 0 options identically to `Say(prompt, 0)`
        if (call.Arguments.Count == 1)
        {
            if (call.CalleeName == "Describe")
            {
                reasons.Add($"invalid-say-arity@line{call.Line}");
            }

            return;
        }

        // Static Say(prompt, 0)
        if (call.Arguments.Count == 2)
        {
            if (!TryEvalConstantInt(call.Arguments[1], _currentTopLevelConstExprs, out int zeroCount) || zeroCount != 0)
            {
                reasons.Add($"invalid-say-2arg-count@line{call.Line}");
            }

            return;
        }

        if (TryEvalConstantInt(call.Arguments[1], _currentTopLevelConstExprs, out int inlineCount) &&
            inlineCount > 0 &&
            call.Arguments.Count >= 2 + inlineCount)
        {
            int checkEnd = 2 + inlineCount;
            for (int i = 2; i < call.Arguments.Count; i++)
            {
                LuaValueType ot = ValidateExpr(call.Arguments[i], kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
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

        if (call.Arguments.Count == 3)
        {
            LuaValueType countType = ValidateExpr(call.Arguments[1], kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
            LuaValueType tabType = ValidateExpr(call.Arguments[2], kind, functionsByName, functionParamTypes, topLevelConsts, topLevelAwardNodes, localTypes, assigned, fnIsAsync, fnReturnTypes, ref isAsync, reasons);
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

    public bool TryCompileAwardNode(
        LuaExpression expr,
        IReadOnlyDictionary<string, LuaExpression>? constExprs,
        out CompiledAwardNode? node,
        out string? error)
    {
        node = null;
        error = null;

        if (expr is LuaIdentifierExpr id && constExprs is not null && constExprs.TryGetValue(id.Name, out LuaExpression? refExpr))
        {
            return TryCompileAwardNode(refExpr, constExprs, out node, out error);
        }

        // Sequential group of award entries: `{ entry1, entry2, ... }`
        if (expr is LuaTableLiteralExpr seqTbl)
        {
            if (seqTbl.Elements.Count == 0)
            {
                error = $"empty-award-group@line{seqTbl.Line}";
                return false;
            }

            var children = new List<CompiledAwardNode>(seqTbl.Elements.Count);
            bool anyRate = false;
            bool allRate = true;
            foreach (LuaExpression el in seqTbl.Elements)
            {
                if (!TryCompileAwardNode(el, constExprs, out CompiledAwardNode? child, out error) || child is null)
                {
                    return false;
                }

                if (child.RatePercent > 0.0)
                {
                    anyRate = true;
                }
                else
                {
                    allRate = false;
                }

                children.Add(child);
            }

            if (anyRate && !allRate)
            {
                error = $"mixed-rate-award-group@line{seqTbl.Line}";
                return false;
            }

            node = new CompiledAwardNode(
                anyRate ? CompiledAwardNodeKind.RandomWeightedGroup : CompiledAwardNodeKind.SequentialGroup,
                Children: children);
            return true;
        }

        // Keyed table: either `[1] = {...}, [2] = {...}` OR leaf award `{ szName=..., tbProp=..., nExp=... }`
        if (expr is LuaKeyedTableLiteralExpr keyedTbl)
        {
            if (keyedTbl.Entries.Count == 0)
            {
                error = $"empty-award-table@line{keyedTbl.Line}";
                return false;
            }

            // Check if this is an indexed `[1] = {...}, [2] = {...}` award group
            if (keyedTbl.Entries.All(static e => e.FieldName is null))
            {
                var ordered = new SortedDictionary<int, LuaExpression>();
                int nextImplicit = 1;
                foreach (LuaTableFieldEntry e in keyedTbl.Entries)
                {
                    int idxKey;
                    if (e.BracketKey is null)
                    {
                        idxKey = nextImplicit++;
                    }
                    else if (!TryEvalConstantInt(e.BracketKey, constExprs, out idxKey) || idxKey < 1)
                    {
                        error = $"non-sequential-award-key@line{e.Line}";
                        return false;
                    }

                    ordered[idxKey] = e.Value;
                }

                if (ordered.Keys.First() != 1 || ordered.Keys.Last() != ordered.Count)
                {
                    error = $"sparse-award-group@line{keyedTbl.Line}";
                    return false;
                }

                return TryCompileAwardNode(new LuaTableLiteralExpr(ordered.Values.ToList(), keyedTbl.Line), constExprs, out node, out error);
            }

            // Leaf award table
            var fields = new Dictionary<string, LuaExpression>(StringComparer.Ordinal);
            foreach (LuaTableFieldEntry fe in keyedTbl.Entries)
            {
                if (fe.FieldName is null || !AllowedAwardFields.Contains(fe.FieldName))
                {
                    error = $"unsupported-award-field:{fe.FieldName ?? "[key]"}@line{fe.Line}";
                    return false;
                }

                fields[fe.FieldName] = fe.Value;
            }

            double rate = 0.0;
            if (fields.TryGetValue("nRate", out LuaExpression? rateExpr))
            {
                if (!TryEvalConstantDouble(rateExpr, constExprs, out rate) || rate <= 0.0)
                {
                    error = $"invalid-award-rate@line{rateExpr.Line}";
                    return false;
                }
            }

            int count = 1;
            if (fields.TryGetValue("nCount", out LuaExpression? countExpr))
            {
                if (!TryEvalConstantInt(countExpr, constExprs, out count) || count <= 0)
                {
                    error = $"invalid-award-count@line{countExpr.Line}";
                    return false;
                }
            }

            int bindState = 0;
            if (fields.TryGetValue("nBindState", out LuaExpression? bindExpr) &&
                !TryEvalConstantInt(bindExpr, constExprs, out bindState))
            {
                error = $"dynamic-award-bindstate@line{bindExpr.Line}";
                return false;
            }

            string? explicitName = null;
            if (fields.TryGetValue("szName", out LuaExpression? nameExpr))
            {
                if (!TryEvalConstantString(nameExpr, out explicitName))
                {
                    error = $"dynamic-award-name@line{nameExpr.Line}";
                    return false;
                }
            }

            if (fields.TryGetValue("tbProp", out LuaExpression? propExpr))
            {
                if (propExpr is not LuaTableLiteralExpr propTbl || propTbl.Elements.Count < 2)
                {
                    error = $"invalid-award-tbprop@line{propExpr.Line}";
                    return false;
                }

                var propInts = new int[propTbl.Elements.Count];
                for (int i = 0; i < propTbl.Elements.Count; i++)
                {
                    if (!TryEvalConstantInt(propTbl.Elements[i], constExprs, out propInts[i]))
                    {
                        error = $"dynamic-award-tbprop@line{propExpr.Line}";
                        return false;
                    }
                }

                int quality = 0;
                if (fields.TryGetValue("nQuality", out LuaExpression? qualExpr) &&
                    !TryEvalConstantInt(qualExpr, constExprs, out quality))
                {
                    error = $"dynamic-award-quality@line{qualExpr.Line}";
                    return false;
                }

                bool consumeSeriesRng = propInts.Length < 5;
                int? fixedSeries = propInts.Length >= 5 ? propInts[4] : null;

                if (quality == 1)
                {
                    if (propInts[0] != 0 || _itemCatalogIndex is null || !_itemCatalogIndex.TryResolveGoldEquipByRowId(propInts[1], out PackItemEntry goldEntry))
                    {
                        error = $"unknown-award-gold-row:{propInts[1]}@line{propExpr.Line}";
                        return false;
                    }

                    node = new CompiledAwardNode(
                        CompiledAwardNodeKind.LeafGoldEquip,
                        RatePercent: rate,
                        Count: count,
                        ItemCode: goldEntry.Code,
                        ItemNameUtf8: explicitName ?? goldEntry.NameUtf8,
                        MaxStackCount: 1,
                        ConsumeRandomSeriesRoll: consumeSeriesRng,
                        FixedSeries: fixedSeries,
                        BindStateRaw: bindState);
                    return true;
                }

                if (quality != 0 || propInts.Length < 3)
                {
                    error = $"unsupported-award-quality:{quality}@line{propExpr.Line}";
                    return false;
                }

                int g = propInts[0];
                int d = propInts[1];
                int p = propInts[2];
                int lv = propInts.Length >= 4 ? propInts[3] : 1;
                if (_itemCatalogIndex is null || !_itemCatalogIndex.TryResolveByGdpl(g, d, p, lv, out PackItemEntry itemEntry))
                {
                    error = $"unknown-award-gdpl:{g},{d},{p},{lv}";
                    return false;
                }

                node = new CompiledAwardNode(
                    CompiledAwardNodeKind.LeafItem,
                    RatePercent: rate,
                    Count: count,
                    ItemCode: itemEntry.Code,
                    ItemNameUtf8: explicitName ?? itemEntry.NameUtf8,
                    MaxStackCount: g == 0 ? (ushort)1 : itemEntry.MaxStackCount,
                    ConsumeRandomSeriesRoll: consumeSeriesRng,
                    FixedSeries: fixedSeries,
                    BindStateRaw: bindState,
                    NumericAmount: g == 0 ? 1 : 0);
                return true;
            }

            if (fields.TryGetValue("nExp", out LuaExpression? expExpr) && TryEvalConstantDouble(expExpr, constExprs, out double expVal) && expVal > 0)
            {
                node = new CompiledAwardNode(CompiledAwardNodeKind.LeafExp, RatePercent: rate, Count: count, NumericAmount: (long)Math.Truncate(expVal));
                return true;
            }

            if (fields.TryGetValue("nExp_tl", out LuaExpression? expTlExpr) && TryEvalConstantDouble(expTlExpr, constExprs, out double expTlVal) && expTlVal > 0)
            {
                node = new CompiledAwardNode(CompiledAwardNodeKind.LeafExpTl, RatePercent: rate, Count: count, NumericAmount: (long)Math.Truncate(expTlVal));
                return true;
            }

            if (fields.TryGetValue("nJxb", out LuaExpression? jxbExpr) && TryEvalConstantInt(jxbExpr, constExprs, out int jxbVal) && jxbVal > 0)
            {
                node = new CompiledAwardNode(CompiledAwardNodeKind.LeafMoney, RatePercent: rate, Count: count, NumericAmount: jxbVal);
                return true;
            }

            if (fields.TryGetValue("nRepute", out LuaExpression? repExpr) && TryEvalConstantInt(repExpr, constExprs, out int repVal) && repVal > 0)
            {
                node = new CompiledAwardNode(CompiledAwardNodeKind.LeafRepute, RatePercent: rate, Count: count, NumericAmount: repVal);
                return true;
            }

            error = $"unrecognized-award-leaf@line{keyedTbl.Line}";
            return false;
        }

        error = $"non-table-award@line{expr.Line}";
        return false;
    }

    private bool TryValidateConstantExpr(
        string? targetConstName,
        LuaExpression expr,
        IReadOnlyDictionary<string, LuaValueType> existingConsts,
        IReadOnlyDictionary<string, LuaExpression> existingConstExprs,
        Dictionary<string, IReadOnlyList<LuaValueType>> constTupleCols,
        Dictionary<string, CompiledAwardNode> topLevelAwardNodes,
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

        if (TryEvalConstantInt(expr, existingConstExprs, out _))
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
            if (targetConstName is not null)
            {
                if (constTupleCols.TryGetValue(ident.Name, out IReadOnlyList<LuaValueType>? cols))
                {
                    constTupleCols[targetConstName] = cols;
                }

                if (topLevelAwardNodes.TryGetValue(ident.Name, out CompiledAwardNode? an))
                {
                    topLevelAwardNodes[targetConstName] = an;
                }
            }

            return true;
        }

        if (expr is LuaBinaryExpr bin &&
            TryValidateConstantExpr(null, bin.Left, existingConsts, existingConstExprs, constTupleCols, topLevelAwardNodes, out LuaValueType blt, out error) &&
            TryValidateConstantExpr(null, bin.Right, existingConsts, existingConstExprs, constTupleCols, topLevelAwardNodes, out LuaValueType brt, out error))
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
            // First try homogeneous 1D or 2D array
            bool allSame = true;
            LuaValueType elemType = LuaValueType.Void;
            for (int i = 0; i < tbl.Elements.Count; i++)
            {
                if (!TryValidateConstantExpr(null, tbl.Elements[i], existingConsts, existingConstExprs, constTupleCols, topLevelAwardNodes, out LuaValueType et, out _))
                {
                    allSame = false;
                    break;
                }

                if (i == 0)
                {
                    elemType = et;
                }
                else if (et != elemType)
                {
                    allSame = false;
                    break;
                }
            }

            if (allSame)
            {
                switch (elemType)
                {
                    case LuaValueType.String:
                        type = LuaValueType.StringArray;
                        return true;
                    case LuaValueType.Int:
                        type = LuaValueType.IntArray;
                        return true;
                    case LuaValueType.IntArray:
                        type = LuaValueType.Int2DArray;
                        return true;
                    case LuaValueType.StringArray:
                        type = LuaValueType.String2DArray;
                        return true;
                    case LuaValueType.AwardTemplate when targetConstName is not null && TryCompileAwardNode(tbl, existingConstExprs, out CompiledAwardNode? grpNode, out _):
                        topLevelAwardNodes[targetConstName] = grpNode!;
                        type = LuaValueType.AwardTemplate;
                        return true;
                }
            }

            // Try ConstTupleTable: every element is a non-empty sequential table of scalar constants (max 7 columns)
            if (targetConstName is not null && TryValidateConstTupleRows(tbl, existingConsts, existingConstExprs, constTupleCols, topLevelAwardNodes, out List<LuaValueType>? colTypes) && colTypes is not null)
            {
                constTupleCols[targetConstName] = colTypes;
                type = LuaValueType.ConstTupleTable;
                return true;
            }

            // Try AwardTemplate group
            if (targetConstName is not null && TryCompileAwardNode(tbl, existingConstExprs, out CompiledAwardNode? seqAwardNode, out _))
            {
                topLevelAwardNodes[targetConstName] = seqAwardNode!;
                type = LuaValueType.AwardTemplate;
                return true;
            }

            type = LuaValueType.Void;
            error = $"heterogeneous-toplevel-table@line{tbl.Line}";
            return false;
        }

        if (expr is LuaKeyedTableLiteralExpr ktbl && ktbl.Entries.Count > 0)
        {
            // 1. Try integer-keyed constant dictionary (`[k] = v`)
            if (ktbl.Entries.All(static e => e.FieldName is null && e.BracketKey is not null))
            {
                bool validMap = true;
                LuaValueType valElemType = LuaValueType.Void;
                var seenKeys = new HashSet<int>();
                for (int i = 0; i < ktbl.Entries.Count; i++)
                {
                    LuaTableFieldEntry fe = ktbl.Entries[i];
                    if (!TryEvalConstantInt(fe.BracketKey!, existingConstExprs, out int intKey) ||
                        !seenKeys.Add(intKey) ||
                        !TryValidateConstantExpr(null, fe.Value, existingConsts, existingConstExprs, constTupleCols, topLevelAwardNodes, out LuaValueType vt, out _))
                    {
                        validMap = false;
                        break;
                    }

                    if (i == 0)
                    {
                        valElemType = vt;
                    }
                    else if (vt != valElemType)
                    {
                        validMap = false;
                        break;
                    }
                }

                if (validMap)
                {
                    switch (valElemType)
                    {
                        case LuaValueType.Int:
                            type = LuaValueType.IntKeyedIntMap;
                            return true;
                        case LuaValueType.String:
                            type = LuaValueType.IntKeyedStringMap;
                            return true;
                        case LuaValueType.IntArray:
                            type = LuaValueType.IntKeyedIntArrayMap;
                            return true;
                    }
                }
            }

            // 2. Try static tbAwardTemplet template
            if (targetConstName is not null && TryCompileAwardNode(ktbl, existingConstExprs, out CompiledAwardNode? awardNode, out string? awardErr) && awardNode is not null)
            {
                topLevelAwardNodes[targetConstName] = awardNode;
                type = LuaValueType.AwardTemplate;
                return true;
            }

            type = LuaValueType.Void;
            error = $"unsupported-keyed-toplevel-table@line{ktbl.Line}";
            return false;
        }

        type = LuaValueType.Void;
        error = $"non-const-toplevel-expr@line{expr.Line}";
        return false;
    }

    private bool TryValidateConstTupleRows(
        LuaTableLiteralExpr outer,
        IReadOnlyDictionary<string, LuaValueType> existingConsts,
        IReadOnlyDictionary<string, LuaExpression> existingConstExprs,
        Dictionary<string, IReadOnlyList<LuaValueType>> constTupleCols,
        Dictionary<string, CompiledAwardNode> topLevelAwardNodes,
        out List<LuaValueType>? columnTypes)
    {
        columnTypes = null;
        int maxCols = 0;
        var rowElemTypes = new List<LuaValueType[]>(outer.Elements.Count);
        foreach (LuaExpression rowExpr in outer.Elements)
        {
            if (rowExpr is not LuaTableLiteralExpr row || row.Elements.Count is < 2 or > 7)
            {
                return false;
            }

            maxCols = Math.Max(maxCols, row.Elements.Count);
            var cts = new LuaValueType[row.Elements.Count];
            for (int c = 0; c < row.Elements.Count; c++)
            {
                if (!TryValidateConstantExpr(null, row.Elements[c], existingConsts, existingConstExprs, constTupleCols, topLevelAwardNodes, out LuaValueType ct, out _) ||
                    ct is not (LuaValueType.Int or LuaValueType.Number or LuaValueType.String))
                {
                    return false;
                }

                cts[c] = ct;
            }

            rowElemTypes.Add(cts);
        }

        var unifiedCols = new List<LuaValueType>(maxCols);
        for (int c = 0; c < maxCols; c++)
        {
            LuaValueType colType = LuaValueType.Void;
            foreach (LuaValueType[] r in rowElemTypes)
            {
                if (c >= r.Length)
                {
                    continue;
                }

                LuaValueType cell = r[c];
                if (colType == LuaValueType.Void)
                {
                    colType = cell;
                }
                else if (colType != cell)
                {
                    if (IsNumeric(colType) && IsNumeric(cell))
                    {
                        colType = LuaValueType.Number;
                    }
                    else
                    {
                        return false;
                    }
                }
            }

            if (colType == LuaValueType.Void)
            {
                return false;
            }

            unifiedCols.Add(colType);
        }

        columnTypes = unifiedCols;
        return true;
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
        t is LuaValueType.Bool or LuaValueType.Nil or LuaValueType.Int or LuaValueType.NullableInt or LuaValueType.Number or LuaValueType.String or LuaValueType.SyncCallback;

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
                case LuaMemberAssignStmt mas:
                    CollectUnsupportedExprNodes(mas.Target, reasons);
                    CollectUnsupportedExprNodes(mas.Value, reasons);
                    break;
                case LuaCallStmt cs:
                    CollectUnsupportedExprNodes(cs.Call, reasons);
                    break;
                case LuaMemberCallStmt mcs:
                    CollectUnsupportedExprNodes(mcs.Call, reasons);
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
            case LuaMemberCallExpr mc:
                CollectUnsupportedExprNodes(mc.Receiver, reasons);
                foreach (LuaExpression a in mc.Arguments) CollectUnsupportedExprNodes(a, reasons);
                break;
            case LuaMemberAccessExpr ma:
                CollectUnsupportedExprNodes(ma.Target, reasons);
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
            case LuaKeyedTableLiteralExpr kt:
                foreach (LuaTableFieldEntry fe in kt.Entries)
                {
                    if (fe.BracketKey is not null) CollectUnsupportedExprNodes(fe.BracketKey, reasons);
                    CollectUnsupportedExprNodes(fe.Value, reasons);
                }

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
                case LuaMemberCallStmt mcs:
                    CollectExprCalls(mcs.Call, functionsByName, topLevelConstExprs, edges);
                    break;
                case LuaMemberAssignStmt mas:
                    CollectExprCalls(mas.Target, functionsByName, topLevelConstExprs, edges);
                    CollectExprCalls(mas.Value, functionsByName, topLevelConstExprs, edges);
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

            case LuaIdentifierExpr id:
                if (functionsByName.ContainsKey(id.Name))
                {
                    edges.Add(id.Name);
                }
                else if (topLevelConstExprs.TryGetValue(id.Name, out LuaExpression? constVal) && constVal is LuaTableLiteralExpr constTbl)
                {
                    foreach (LuaExpression el in constTbl.Elements)
                    {
                        CollectExprCalls(el, functionsByName, topLevelConstExprs, edges);
                    }
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

            case LuaMemberCallExpr mcall:
                CollectExprCalls(mcall.Receiver, functionsByName, topLevelConstExprs, edges);
                foreach (LuaExpression arg in mcall.Arguments)
                {
                    CollectExprCalls(arg, functionsByName, topLevelConstExprs, edges);
                }

                break;

            case LuaMemberAccessExpr macc:
                CollectExprCalls(macc.Target, functionsByName, topLevelConstExprs, edges);
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

            case LuaKeyedTableLiteralExpr ktbl:
                foreach (LuaTableFieldEntry fe in ktbl.Entries)
                {
                    if (fe.BracketKey is not null) CollectExprCalls(fe.BracketKey, functionsByName, topLevelConstExprs, edges);
                    CollectExprCalls(fe.Value, functionsByName, topLevelConstExprs, edges);
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
