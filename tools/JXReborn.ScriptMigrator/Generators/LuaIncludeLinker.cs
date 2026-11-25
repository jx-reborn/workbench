using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using JXReborn.LegacyImporter.Sources;
using JXReborn.ScriptMigrator.Ast;

namespace JXReborn.ScriptMigrator.Generators;

/// <summary>
/// Resolves transitive <c>Include(...)</c> / <c>dofile(...)</c> dependencies via <see cref="ILegacyServerSource"/>,
/// alpha-renames chunk-local constants + <c>%upvalue</c> references so multi-file includes never collide,
/// and performs sound Reachability Tree-Shaking from the entrypoint (<c>main</c> / <c>OnDeath</c>) and all
/// dialog/UI callback strings so unused helper functions and unused pure header tables are eliminated cleanly.
/// </summary>
public sealed class LuaIncludeLinker
{
    private static readonly HashSet<string> SafeEngineIncludeLibs = new(StringComparer.OrdinalIgnoreCase)
    {
        "SETTING",
        "ITEM",
        "FILESYS",
        "BATTLE",
        "PARTNER",
        "TONG",
        "RELAYLADDER",
        "TASKSYS",
        "TITLE",
        "LEAGUEMATCH",
        "LEAGUE",
        "NPCINFO",
        "TIMER",
        "GM",
        "SPREADER",
        "TONGWAR",
    };

    private static readonly HashSet<string> LoweredLibraryIntrinsics = new(StringComparer.Ordinal)
    {
        "CreateNewSayEx",
        "CreateTaskSay",
        "TaskSay",
        "RCNY_ApDung",
        "RCNY_PhatDoVang",
        "RCNY_DoVang",
        "EXPAWARD_ExpThat",
        "_WritePlayerLog",
        "getItemInfo",
        "loadsavevalue",
        "savevalue",
    };

    private static readonly HashSet<string> LoweredFrameworkGlobals = new(StringComparer.Ordinal)
    {
        "tbAwardTemplet",
        "DailogClass",
        "PlayerFunLib",
        "RCNY_TONG_NHOM",
        "RCNY_BO",
        "RCNY_NHOM",
        "ObjBuffer",
        "tbLog",
        "tbVngTransLog",
        "tbVngLib_File",
        "tbVNG_BitTask_Lib",
        "tbvng_ChestExpAward",
    };

    private readonly ILegacyServerSource _source;
    private readonly Lua4AstParser _parser;
    private readonly Dictionary<string, LuaParseResult> _parsedLibraryCache = new(StringComparer.OrdinalIgnoreCase);

    public LuaIncludeLinker(ILegacyServerSource source, Lua4AstParser parser)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
    }

    public LuaLinkResult LinkAndTreeShake(
        string entryCanonicalOrRelPath,
        LuaProgram entryProgram,
        string expectedEntrypoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryCanonicalOrRelPath);
        ArgumentNullException.ThrowIfNull(entryProgram);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedEntrypoint);

        var activeStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_source.TryResolveServerFile(entryCanonicalOrRelPath, out string? resolvedEntryKey) &&
            !string.IsNullOrWhiteSpace(resolvedEntryKey))
        {
            activeStack.Add(resolvedEntryKey);
        }
        else
        {
            activeStack.Add(entryCanonicalOrRelPath);
        }

        var includedKeys = new List<string>();
        var blockingReasons = new SortedSet<string>(StringComparer.Ordinal);
        var constTable = new Dictionary<string, (LuaStatement Statement, LuaExpression ValueExpr, int ChunkId)>(StringComparer.Ordinal);
        var constOrder = new List<string>();
        var impureStatementsByChunk = new Dictionary<int, List<LuaStatement>>();
        var functionsByName = new Dictionary<string, (LuaFunctionDecl Fn, int ChunkId)>(StringComparer.Ordinal);
        var functionOrder = new List<string>();
        int nextChunkId = 0;

        FlattenChunk(
            program: entryProgram,
            isEntryChunk: true,
            activeStack: activeStack,
            includedKeys: includedKeys,
            blockingReasons: blockingReasons,
            constTable: constTable,
            constOrder: constOrder,
            impureStatementsByChunk: impureStatementsByChunk,
            functionsByName: functionsByName,
            functionOrder: functionOrder,
            ref nextChunkId);

        if (blockingReasons.Count > 0)
        {
            return new LuaLinkResult(null, includedKeys, blockingReasons.ToArray());
        }

        if (!functionsByName.ContainsKey(expectedEntrypoint))
        {
            return new LuaLinkResult(null, includedKeys, [$"missing-entrypoint:{expectedEntrypoint}"]);
        }

        // Reachability Tree-Shaking starting from expectedEntrypoint
        var reachableFunctions = new HashSet<string>(StringComparer.Ordinal);
        var reachableConsts = new HashSet<string>(StringComparer.Ordinal);
        var touchedChunks = new HashSet<int> { 0 }; // Entry chunk (0) is always active
        var fnQueue = new Queue<string>();
        var constQueue = new Queue<string>();

        reachableFunctions.Add(expectedEntrypoint);
        fnQueue.Enqueue(expectedEntrypoint);

        void DrainFunctionQueue()
        {
            while (fnQueue.Count > 0)
            {
                string fnName = fnQueue.Dequeue();
                (LuaFunctionDecl fn, int fnChunkId) = functionsByName[fnName];
                touchedChunks.Add(fnChunkId);

                ScanStatementsForReachability(
                    fn.Body,
                    functionsByName,
                    constTable,
                    reachableFunctions,
                    reachableConsts,
                    fnQueue,
                    constQueue);
            }
        }

        DrainFunctionQueue();

        // Also scan active top-level impure statements (e.g. RCNY_ApDung(tbAward) in entry chunk)
        foreach (int chunkId in touchedChunks.OrderBy(static id => id).ToArray())
        {
            if (impureStatementsByChunk.TryGetValue(chunkId, out List<LuaStatement>? impures))
            {
                ScanStatementsForReachability(
                    impures,
                    functionsByName,
                    constTable,
                    reachableFunctions,
                    reachableConsts,
                    fnQueue,
                    constQueue);
            }
        }

        DrainFunctionQueue();

        // Transitively expand constants referenced by reachable constants
        while (constQueue.Count > 0)
        {
            string cName = constQueue.Dequeue();
            if (!constTable.TryGetValue(cName, out var cInfo))
            {
                continue;
            }

            touchedChunks.Add(cInfo.ChunkId);
            ScanExpressionForReachability(
                cInfo.ValueExpr,
                functionsByName,
                constTable,
                reachableFunctions,
                reachableConsts,
                fnQueue,
                constQueue);

            DrainFunctionQueue();
        }

        // Build pruned top-level statement list:
        // 1. Reachable top-level constants in declaration order (must precede impure calls referencing them, e.g. RCNY_ApDung(tbAward))
        // 2. Any impure top-level statements from entry chunk (0) or any touched included chunk
        var finalTopLevel = new List<LuaStatement>();
        var seenConsts = new HashSet<string>(StringComparer.Ordinal);
        foreach (string cName in constOrder)
        {
            if (reachableConsts.Contains(cName) && seenConsts.Add(cName) && constTable.TryGetValue(cName, out var cEntry))
            {
                finalTopLevel.Add(cEntry.Statement);
            }
        }

        foreach (int chunkId in touchedChunks.OrderBy(static id => id))
        {
            if (impureStatementsByChunk.TryGetValue(chunkId, out List<LuaStatement>? impures))
            {
                finalTopLevel.AddRange(impures);
            }
        }

        var finalFunctions = new List<LuaFunctionDecl>();
        var seenFns = new HashSet<string>(StringComparer.Ordinal);
        foreach (string fName in functionOrder)
        {
            if (reachableFunctions.Contains(fName) && seenFns.Add(fName) && functionsByName.TryGetValue(fName, out var fEntry))
            {
                finalFunctions.Add(fEntry.Fn);
            }
        }

        return new LuaLinkResult(new LuaProgram(finalTopLevel, finalFunctions), includedKeys, []);
    }

    private void FlattenChunk(
        LuaProgram program,
        bool isEntryChunk,
        HashSet<string> activeStack,
        List<string> includedKeys,
        SortedSet<string> blockingReasons,
        Dictionary<string, (LuaStatement Statement, LuaExpression ValueExpr, int ChunkId)> constTable,
        List<string> constOrder,
        Dictionary<int, List<LuaStatement>> impureStatementsByChunk,
        Dictionary<string, (LuaFunctionDecl Fn, int ChunkId)> functionsByName,
        List<string> functionOrder,
        ref int nextChunkId)
    {
        int myChunkId = nextChunkId++;
        var chunkLocalRenameMap = new Dictionary<string, string>(StringComparer.Ordinal);

        // First collect chunk-level `local X = ...` names so both `%X` and bare `X` inside this chunk rewrite cleanly
        foreach (LuaStatement stmt in program.TopLevelStatements)
        {
            if (stmt is LuaLocalDeclStmt ld)
            {
                string renamed = $"__chunk{myChunkId}_{ld.Name}";
                chunkLocalRenameMap[ld.Name] = renamed;
                if (ld.Initializer is LuaAnonFunctionExpr)
                {
                    chunkLocalRenameMap["%" + ld.Name] = renamed;
                }
            }
        }

        foreach (LuaStatement rawStmt in program.TopLevelStatements)
        {
            LuaStatement stmt = chunkLocalRenameMap.Count > 0
                ? RewriteStatementChunkLocals(rawStmt, chunkLocalRenameMap, shadowedLocals: [])
                : rawStmt;

            if (stmt is LuaCallStmt callStmt)
            {
                string callee = callStmt.Call.CalleeName;
                if (callee.Equals("IncludeLib", StringComparison.OrdinalIgnoreCase) ||
                    callee.Equals("IL", StringComparison.OrdinalIgnoreCase))
                {
                    string lib = callStmt.Call.Arguments.Count > 0 && callStmt.Call.Arguments[0] is LuaStringExpr ls
                        ? ls.DecodedUtf8.Trim()
                        : "?";
                    if (!SafeEngineIncludeLibs.Contains(lib))
                    {
                        blockingReasons.Add($"includelib:{lib}");
                    }

                    continue;
                }

                if (callee.Equals("Include", StringComparison.OrdinalIgnoreCase) ||
                    callee.Equals("dofile", StringComparison.OrdinalIgnoreCase))
                {
                    if (callStmt.Call.Arguments.Count == 0 || callStmt.Call.Arguments[0] is not LuaStringExpr pathExpr)
                    {
                        blockingReasons.Add("dynamic-include");
                        continue;
                    }

                    if (!TryResolveIncludeKey(pathExpr, out string? incKey) || string.IsNullOrWhiteSpace(incKey))
                    {
                        if (isEntryChunk)
                        {
                            blockingReasons.Add($"missing-include:{pathExpr.DecodedUtf8}");
                        }
                        else
                        {
                            AddImpure(impureStatementsByChunk, myChunkId, new LuaUnsupportedStmt($"missing-include:{pathExpr.DecodedUtf8}", callStmt.Line));
                        }

                        continue;
                    }

                    if (!activeStack.Add(incKey))
                    {
                        // Cycle guard matching Lua4Interpreter._activeFileStack
                        continue;
                    }

                    try
                    {
                        includedKeys.Add(incKey);
                        LuaParseResult incParsed = ParseIncludedLibraryCached(incKey);
                        if (!incParsed.IsSuccess || incParsed.Program is null)
                        {
                            if (isEntryChunk)
                            {
                                blockingReasons.Add($"include-parse:{incKey}({incParsed.FailureReason ?? "failed"})");
                            }
                            else
                            {
                                AddImpure(impureStatementsByChunk, myChunkId, new LuaUnsupportedStmt($"include-parse:{incKey}({incParsed.FailureReason ?? "failed"})", callStmt.Line));
                            }

                            continue;
                        }

                        FlattenChunk(
                            program: incParsed.Program,
                            isEntryChunk: false,
                            activeStack: activeStack,
                            includedKeys: includedKeys,
                            blockingReasons: blockingReasons,
                            constTable: constTable,
                            constOrder: constOrder,
                            impureStatementsByChunk: impureStatementsByChunk,
                            functionsByName: functionsByName,
                            functionOrder: functionOrder,
                            ref nextChunkId);
                    }
                    finally
                    {
                        activeStack.Remove(incKey);
                    }

                    continue;
                }

                // Other top-level call (e.g. TabFile_Load): record as impure statement for this chunk
                AddImpure(impureStatementsByChunk, myChunkId, stmt);
                continue;
            }

            if (stmt is LuaLocalDeclStmt localDecl)
            {
                string targetName = chunkLocalRenameMap.GetValueOrDefault(localDecl.Name, localDecl.Name);
                LuaExpression valExpr = localDecl.Initializer ?? new LuaNilExpr(localDecl.Line);
                if (valExpr is LuaAnonFunctionExpr anonFn)
                {
                    valExpr = anonFn with { Body = PruneUnreachableStatements(anonFn.Body) };
                }

                var normalizedStmt = new LuaAssignStmt(targetName, valExpr, localDecl.Line);
                constTable[targetName] = (normalizedStmt, valExpr, myChunkId);
                constOrder.Add(targetName);
                continue;
            }

            if (stmt is LuaAssignStmt assignStmt)
            {
                if (!isEntryChunk && LoweredFrameworkGlobals.Contains(assignStmt.TargetName))
                {
                    continue;
                }

                if (isEntryChunk && constTable.TryGetValue(assignStmt.TargetName, out var prev) && prev.ChunkId == myChunkId)
                {
                    // Reassigning a global twice in the entry chunk itself is kept as impure so semantic validator flags it
                    AddImpure(impureStatementsByChunk, myChunkId, assignStmt);
                    continue;
                }

                constTable[assignStmt.TargetName] = (assignStmt, assignStmt.Value, myChunkId);
                constOrder.Add(assignStmt.TargetName);
                continue;
            }

            if (!isEntryChunk && stmt is LuaMemberAssignStmt)
            {
                // Library OOP prototype field initialization (`ExpType.pFun = ...`, `Class.tbTemplate = {}`)
                continue;
            }

            if (!isEntryChunk &&
                stmt is LuaMemberCallStmt libMemberCall &&
                libMemberCall.Call.MemberName is "_init" or "Reg" or "RegType")
            {
                // Library OOP startup registration (`G_DIALOG:_init()`, `ExpType:Reg()`, `EventSys:GetType(...):Reg(...)`)
                continue;
            }

            if (!isEntryChunk &&
                stmt is LuaIfStmt { Condition: LuaIdentifierExpr { Name: "login_add" } })
            {
                // Library startup login hook (`if login_add then login_add(...) end`)
                continue;
            }

            AddImpure(impureStatementsByChunk, myChunkId, stmt);
        }

        foreach (LuaFunctionDecl rawFn in program.Functions)
        {
            if (!isEntryChunk && LoweredLibraryIntrinsics.Contains(rawFn.Name))
            {
                continue;
            }

            LuaFunctionDecl fn = chunkLocalRenameMap.Count > 0
                ? RewriteFunctionChunkLocals(rawFn, chunkLocalRenameMap)
                : rawFn;
            fn = fn with { Body = PruneUnreachableStatements(fn.Body) };

            if (!functionsByName.ContainsKey(fn.Name))
            {
                functionOrder.Add(fn.Name);
            }

            functionsByName[fn.Name] = (fn, myChunkId);
        }
    }

    private LuaParseResult ParseIncludedLibraryCached(string virtualKey)
    {
        if (_parsedLibraryCache.TryGetValue(virtualKey, out LuaParseResult? cached))
        {
            return cached;
        }

        if (!_source.TryReadServerFile(virtualKey, out byte[]? bytes) || bytes is null)
        {
            var missing = new LuaParseResult(null, $"missing-file:{virtualKey}");
            _parsedLibraryCache[virtualKey] = missing;
            return missing;
        }

        LuaParseResult parsed = _parser.ParseLibrary(bytes);
        _parsedLibraryCache[virtualKey] = parsed;
        return parsed;
    }

    private bool TryResolveIncludeKey(LuaStringExpr pathExpr, out string? virtualKey)
    {
        // 1. Try exact raw Latin1 bytes from token first (matches Lua4Interpreter.ExecuteFile 100%!)
        byte[] rawBytes = Encoding.Latin1.GetBytes(pathExpr.RawLatin1);
        if (_source.TryResolveServerFile(rawBytes, out virtualKey) && !string.IsNullOrWhiteSpace(virtualKey))
        {
            return true;
        }

        // 2. Fallback to decoded UTF-8 string resolution
        return _source.TryResolveServerFile(pathExpr.DecodedUtf8, out virtualKey) && !string.IsNullOrWhiteSpace(virtualKey);
    }

    private static void AddImpure(Dictionary<int, List<LuaStatement>> dict, int chunkId, LuaStatement stmt)
    {
        if (!dict.TryGetValue(chunkId, out List<LuaStatement>? list))
        {
            list = [];
            dict[chunkId] = list;
        }

        list.Add(stmt);
    }

    private static void ScanStatementsForReachability(
        IReadOnlyList<LuaStatement> stmts,
        Dictionary<string, (LuaFunctionDecl Fn, int ChunkId)> functionsByName,
        Dictionary<string, (LuaStatement Statement, LuaExpression ValueExpr, int ChunkId)> constTable,
        HashSet<string> reachableFunctions,
        HashSet<string> reachableConsts,
        Queue<string> fnQueue,
        Queue<string>? constQueue = null)
    {
        foreach (LuaStatement stmt in stmts)
        {
            switch (stmt)
            {
                case LuaCallStmt cs:
                    ScanExpressionForReachability(cs.Call, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                    break;
                case LuaMemberCallStmt mcs:
                    ScanExpressionForReachability(mcs.Call, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                    break;
                case LuaMemberAssignStmt mas:
                    ScanExpressionForReachability(mas.Target, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                    ScanExpressionForReachability(mas.Value, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                    break;
                case LuaLocalDeclStmt ld when ld.Initializer is not null:
                    ScanExpressionForReachability(ld.Initializer, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                    break;
                case LuaMultiLocalDeclStmt mld:
                    foreach (LuaExpression init in mld.Initializers)
                    {
                        ScanExpressionForReachability(init, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                    }

                    break;
                case LuaAssignStmt asg:
                    if (constTable.ContainsKey(asg.TargetName) && reachableConsts.Add(asg.TargetName))
                    {
                        constQueue?.Enqueue(asg.TargetName);
                    }

                    ScanExpressionForReachability(asg.Value, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                    break;
                case LuaMultiAssignStmt masg:
                    foreach (LuaExpression v in masg.Values)
                    {
                        ScanExpressionForReachability(v, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                    }

                    break;
                case LuaIndexAssignStmt iasg:
                    if (constTable.ContainsKey(iasg.TargetName) && reachableConsts.Add(iasg.TargetName))
                    {
                        constQueue?.Enqueue(iasg.TargetName);
                    }

                    ScanExpressionForReachability(iasg.Index, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                    ScanExpressionForReachability(iasg.Value, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                    break;
                case LuaReturnStmt ret:
                    if (ret.Value is not null)
                    {
                        ScanExpressionForReachability(ret.Value, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                    }

                    if (ret.ExtraValues is not null)
                    {
                        foreach (LuaExpression ev in ret.ExtraValues)
                        {
                            ScanExpressionForReachability(ev, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                        }
                    }

                    break;
                case LuaIfStmt ifs:
                    ScanExpressionForReachability(ifs.Condition, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                    ScanStatementsForReachability(ifs.ThenBody, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                    foreach (LuaElseIfBranch ei in ifs.ElseIfBranches)
                    {
                        ScanExpressionForReachability(ei.Condition, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                        ScanStatementsForReachability(ei.Body, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                    }

                    ScanStatementsForReachability(ifs.ElseBody, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                    break;
                case LuaNumericForStmt nf:
                    ScanExpressionForReachability(nf.Start, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                    ScanExpressionForReachability(nf.Limit, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                    if (nf.Step is not null)
                    {
                        ScanExpressionForReachability(nf.Step, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                    }

                    ScanStatementsForReachability(nf.Body, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                    break;
                case LuaWhileStmt ws:
                    ScanExpressionForReachability(ws.Condition, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                    ScanStatementsForReachability(ws.Body, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                    break;
                case LuaDoBlockStmt db:
                    ScanStatementsForReachability(db.Body, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                    break;
            }
        }
    }

    private static void ScanExpressionForReachability(
        LuaExpression expr,
        Dictionary<string, (LuaFunctionDecl Fn, int ChunkId)> functionsByName,
        Dictionary<string, (LuaStatement Statement, LuaExpression ValueExpr, int ChunkId)> constTable,
        HashSet<string> reachableFunctions,
        HashSet<string> reachableConsts,
        Queue<string> fnQueue,
        Queue<string>? constQueue = null)
    {
        switch (expr)
        {
            case LuaIdentifierExpr id:
                if (constTable.ContainsKey(id.Name) && reachableConsts.Add(id.Name))
                {
                    constQueue?.Enqueue(id.Name);
                }

                if (functionsByName.ContainsKey(id.Name) && reachableFunctions.Add(id.Name))
                {
                    fnQueue.Enqueue(id.Name);
                }

                break;

            case LuaUpvalueExpr up:
                if (constTable.ContainsKey(up.Name) && reachableConsts.Add(up.Name))
                {
                    constQueue?.Enqueue(up.Name);
                }

                break;

            case LuaStringExpr str:
                // Sound callback scan: check bare callback names (Talk/AskClientForNumber) and `/cb` or `/#cb(...)` option strings
                TryMarkCallbackReachable(str.DecodedUtf8, functionsByName, reachableFunctions, fnQueue);
                break;

            case LuaCallExpr call:
                if (functionsByName.ContainsKey(call.CalleeName) && reachableFunctions.Add(call.CalleeName))
                {
                    fnQueue.Enqueue(call.CalleeName);
                }
                else if (constTable.ContainsKey(call.CalleeName) && reachableConsts.Add(call.CalleeName))
                {
                    constQueue?.Enqueue(call.CalleeName);
                }

                int skipArgIdx = -1;
                if (call.CalleeName == "AskClientForNumber" &&
                    call.Arguments.Count >= 1 &&
                    call.Arguments[0] is LuaBinaryExpr { Operator: LuaBinaryOp.Concat, Left: LuaStringExpr askPrefix } askBin)
                {
                    skipArgIdx = 0;
                    MarkNumberedCallbackFamilyReachable(askPrefix.DecodedUtf8, functionsByName, reachableFunctions, fnQueue);
                    ScanExpressionForReachability(askBin.Right, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                }
                else if (call.CalleeName == "Talk" &&
                         call.Arguments.Count >= 3 &&
                         call.Arguments[1] is LuaBinaryExpr { Operator: LuaBinaryOp.Concat, Left: LuaStringExpr talkPrefix } talkBin)
                {
                    skipArgIdx = 1;
                    MarkNumberedCallbackFamilyReachable(talkPrefix.DecodedUtf8, functionsByName, reachableFunctions, fnQueue);
                    ScanExpressionForReachability(talkBin.Right, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                }

                for (int i = 0; i < call.Arguments.Count; i++)
                {
                    if (i == skipArgIdx)
                    {
                        continue;
                    }

                    ScanExpressionForReachability(call.Arguments[i], functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                }

                break;

            case LuaBinaryExpr bin:
                ScanExpressionForReachability(bin.Left, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                ScanExpressionForReachability(bin.Right, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                break;

            case LuaUnaryExpr un:
                ScanExpressionForReachability(un.Operand, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                break;

            case LuaIndexExpr idx:
                ScanExpressionForReachability(idx.Target, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                ScanExpressionForReachability(idx.Index, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                break;

            case LuaTableLiteralExpr tbl:
                foreach (LuaExpression el in tbl.Elements)
                {
                    ScanExpressionForReachability(el, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                }

                break;

            case LuaKeyedTableLiteralExpr ktbl:
                foreach (LuaTableFieldEntry entry in ktbl.Entries)
                {
                    if (entry.BracketKey is not null)
                    {
                        ScanExpressionForReachability(entry.BracketKey, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                    }

                    ScanExpressionForReachability(entry.Value, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                }

                break;

            case LuaMemberAccessExpr macc:
                ScanExpressionForReachability(macc.Target, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                break;

            case LuaMemberCallExpr mcall:
                ScanExpressionForReachability(mcall.Receiver, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                foreach (LuaExpression arg in mcall.Arguments)
                {
                    ScanExpressionForReachability(arg, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                }

                break;

            case LuaAnonFunctionExpr anon:
                ScanStatementsForReachability(anon.Body, functionsByName, constTable, reachableFunctions, reachableConsts, fnQueue, constQueue);
                break;
        }
    }

    public static bool TryExtractOptionCallbackName(string optionOrCallbackSpec, out string callbackName)
    {
        callbackName = string.Empty;
        if (string.IsNullOrWhiteSpace(optionOrCallbackSpec))
        {
            return false;
        }

        string candidate = optionOrCallbackSpec.Trim();
        int slash = candidate.LastIndexOf('/');
        if (slash >= 0)
        {
            candidate = candidate[(slash + 1)..].Trim();
        }

        if (candidate.StartsWith('#'))
        {
            candidate = candidate[1..].TrimStart();
            int paren = candidate.IndexOf('(');
            if (paren > 0)
            {
                candidate = candidate[..paren].Trim();
            }
        }

        if (candidate.Length == 0)
        {
            return false;
        }

        for (int i = 0; i < candidate.Length; i++)
        {
            char c = candidate[i];
            bool valid = i == 0
                ? (char.IsLetter(c) || c == '_' || c >= 0x80)
                : (char.IsLetterOrDigit(c) || c == '_' || c >= 0x80);
            if (!valid)
            {
                return false;
            }
        }

        callbackName = candidate;
        return true;
    }

    private static void MarkNumberedCallbackFamilyReachable(
        string prefix,
        Dictionary<string, (LuaFunctionDecl Fn, int ChunkId)> functionsByName,
        HashSet<string> reachableFunctions,
        Queue<string> fnQueue)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return;
        }

        foreach (string fnName in functionsByName.Keys)
        {
            if (fnName.Length > prefix.Length &&
                fnName.StartsWith(prefix, StringComparison.Ordinal) &&
                int.TryParse(fnName.AsSpan(prefix.Length), out _) &&
                reachableFunctions.Add(fnName))
            {
                fnQueue.Enqueue(fnName);
            }
        }
    }

    private static void TryMarkCallbackReachable(
        string rawString,
        Dictionary<string, (LuaFunctionDecl Fn, int ChunkId)> functionsByName,
        HashSet<string> reachableFunctions,
        Queue<string> fnQueue)
    {
        if (TryExtractOptionCallbackName(rawString, out string cbName) &&
            functionsByName.ContainsKey(cbName) &&
            reachableFunctions.Add(cbName))
        {
            fnQueue.Enqueue(cbName);
        }
    }

    private static LuaFunctionDecl RewriteFunctionChunkLocals(
        LuaFunctionDecl fn,
        IReadOnlyDictionary<string, string> chunkLocalMap)
    {
        var shadowed = new HashSet<string>(fn.Parameters, StringComparer.Ordinal);
        List<LuaStatement> body = RewriteBlockChunkLocals(fn.Body, chunkLocalMap, shadowed);
        return fn with { Body = body };
    }

    private static List<LuaStatement> RewriteBlockChunkLocals(
        IReadOnlyList<LuaStatement> stmts,
        IReadOnlyDictionary<string, string> chunkLocalMap,
        HashSet<string> shadowed)
    {
        var localShadow = new HashSet<string>(shadowed, StringComparer.Ordinal);
        var result = new List<LuaStatement>(stmts.Count);
        foreach (LuaStatement stmt in stmts)
        {
            result.Add(RewriteStatementChunkLocals(stmt, chunkLocalMap, localShadow));
        }

        return result;
    }

    private static LuaStatement RewriteStatementChunkLocals(
        LuaStatement stmt,
        IReadOnlyDictionary<string, string> chunkLocalMap,
        HashSet<string> shadowedLocals)
    {
        switch (stmt)
        {
            case LuaLocalDeclStmt ld:
                LuaExpression? init = ld.Initializer is not null
                    ? RewriteExprChunkLocals(ld.Initializer, chunkLocalMap, shadowedLocals)
                    : null;
                shadowedLocals.Add(ld.Name);
                return ld with { Initializer = init };

            case LuaMultiLocalDeclStmt mld:
                var inits = mld.Initializers.Select(e => RewriteExprChunkLocals(e, chunkLocalMap, shadowedLocals)).ToArray();
                foreach (string n in mld.Names)
                {
                    shadowedLocals.Add(n);
                }

                return mld with { Initializers = inits };

            case LuaAssignStmt asg:
                string target = !shadowedLocals.Contains(asg.TargetName) && chunkLocalMap.TryGetValue(asg.TargetName, out string? mapped)
                    ? mapped
                    : asg.TargetName;
                return asg with { TargetName = target, Value = RewriteExprChunkLocals(asg.Value, chunkLocalMap, shadowedLocals) };

            case LuaMultiAssignStmt masg:
                var targets = masg.TargetNames
                    .Select(t => !shadowedLocals.Contains(t) && chunkLocalMap.TryGetValue(t, out string? m) ? m : t)
                    .ToArray();
                var vals = masg.Values.Select(v => RewriteExprChunkLocals(v, chunkLocalMap, shadowedLocals)).ToArray();
                return masg with { TargetNames = targets, Values = vals };

            case LuaIndexAssignStmt iasg:
                string idxTarget = !shadowedLocals.Contains(iasg.TargetName) && chunkLocalMap.TryGetValue(iasg.TargetName, out string? im)
                    ? im
                    : iasg.TargetName;
                return iasg with
                {
                    TargetName = idxTarget,
                    Index = RewriteExprChunkLocals(iasg.Index, chunkLocalMap, shadowedLocals),
                    Value = RewriteExprChunkLocals(iasg.Value, chunkLocalMap, shadowedLocals),
                };

            case LuaCallStmt cs:
                return cs with { Call = (LuaCallExpr)RewriteExprChunkLocals(cs.Call, chunkLocalMap, shadowedLocals) };

            case LuaMemberCallStmt mcs:
                return mcs with { Call = (LuaMemberCallExpr)RewriteExprChunkLocals(mcs.Call, chunkLocalMap, shadowedLocals) };

            case LuaMemberAssignStmt mas:
                return mas with
                {
                    Target = RewriteExprChunkLocals(mas.Target, chunkLocalMap, shadowedLocals),
                    Value = RewriteExprChunkLocals(mas.Value, chunkLocalMap, shadowedLocals),
                };

            case LuaReturnStmt ret:
                return ret with
                {
                    Value = ret.Value is not null ? RewriteExprChunkLocals(ret.Value, chunkLocalMap, shadowedLocals) : null,
                    ExtraValues = ret.ExtraValues?.Select(e => RewriteExprChunkLocals(e, chunkLocalMap, shadowedLocals)).ToArray(),
                };

            case LuaIfStmt ifs:
                return ifs with
                {
                    Condition = RewriteExprChunkLocals(ifs.Condition, chunkLocalMap, shadowedLocals),
                    ThenBody = RewriteBlockChunkLocals(ifs.ThenBody, chunkLocalMap, shadowedLocals),
                    ElseIfBranches = ifs.ElseIfBranches
                        .Select(ei => ei with
                        {
                            Condition = RewriteExprChunkLocals(ei.Condition, chunkLocalMap, shadowedLocals),
                            Body = RewriteBlockChunkLocals(ei.Body, chunkLocalMap, shadowedLocals),
                        })
                        .ToArray(),
                    ElseBody = RewriteBlockChunkLocals(ifs.ElseBody, chunkLocalMap, shadowedLocals),
                };

            case LuaNumericForStmt nf:
                LuaExpression start = RewriteExprChunkLocals(nf.Start, chunkLocalMap, shadowedLocals);
                LuaExpression limit = RewriteExprChunkLocals(nf.Limit, chunkLocalMap, shadowedLocals);
                LuaExpression? step = nf.Step is not null ? RewriteExprChunkLocals(nf.Step, chunkLocalMap, shadowedLocals) : null;
                var forShadow = new HashSet<string>(shadowedLocals, StringComparer.Ordinal) { nf.IteratorName };
                return nf with { Start = start, Limit = limit, Step = step, Body = RewriteBlockChunkLocals(nf.Body, chunkLocalMap, forShadow) };

            case LuaWhileStmt ws:
                return ws with
                {
                    Condition = RewriteExprChunkLocals(ws.Condition, chunkLocalMap, shadowedLocals),
                    Body = RewriteBlockChunkLocals(ws.Body, chunkLocalMap, shadowedLocals),
                };

            case LuaDoBlockStmt db:
                return db with { Body = RewriteBlockChunkLocals(db.Body, chunkLocalMap, shadowedLocals) };

            default:
                return stmt;
        }
    }

    private static LuaExpression RewriteExprChunkLocals(
        LuaExpression expr,
        IReadOnlyDictionary<string, string> chunkLocalMap,
        HashSet<string> shadowedLocals) =>
        expr switch
        {
            LuaUpvalueExpr up when chunkLocalMap.TryGetValue(up.Name, out string? mapped) =>
                new LuaIdentifierExpr(mapped, up.Line),
            LuaIdentifierExpr id when !shadowedLocals.Contains(id.Name) && chunkLocalMap.TryGetValue(id.Name, out string? mapped) =>
                new LuaIdentifierExpr(mapped, id.Line),
            LuaCallExpr call =>
                call with
                {
                    CalleeName = chunkLocalMap.TryGetValue(call.CalleeName, out string? mappedFn) ? mappedFn : call.CalleeName,
                    Arguments = call.Arguments.Select(a => RewriteExprChunkLocals(a, chunkLocalMap, shadowedLocals)).ToArray(),
                },
            LuaBinaryExpr bin =>
                bin with
                {
                    Left = RewriteExprChunkLocals(bin.Left, chunkLocalMap, shadowedLocals),
                    Right = RewriteExprChunkLocals(bin.Right, chunkLocalMap, shadowedLocals),
                },
            LuaUnaryExpr un =>
                un with { Operand = RewriteExprChunkLocals(un.Operand, chunkLocalMap, shadowedLocals) },
            LuaIndexExpr idx =>
                idx with
                {
                    Target = RewriteExprChunkLocals(idx.Target, chunkLocalMap, shadowedLocals),
                    Index = RewriteExprChunkLocals(idx.Index, chunkLocalMap, shadowedLocals),
                },
            LuaTableLiteralExpr tbl =>
                tbl with { Elements = tbl.Elements.Select(e => RewriteExprChunkLocals(e, chunkLocalMap, shadowedLocals)).ToArray() },
            LuaKeyedTableLiteralExpr ktbl =>
                ktbl with
                {
                    Entries = ktbl.Entries.Select(e => e with
                    {
                        BracketKey = e.BracketKey is not null ? RewriteExprChunkLocals(e.BracketKey, chunkLocalMap, shadowedLocals) : null,
                        Value = RewriteExprChunkLocals(e.Value, chunkLocalMap, shadowedLocals),
                    }).ToArray(),
                },
            LuaMemberAccessExpr macc =>
                macc with { Target = RewriteExprChunkLocals(macc.Target, chunkLocalMap, shadowedLocals) },
            LuaMemberCallExpr mcall =>
                mcall with
                {
                    Receiver = RewriteExprChunkLocals(mcall.Receiver, chunkLocalMap, shadowedLocals),
                    Arguments = mcall.Arguments.Select(a => RewriteExprChunkLocals(a, chunkLocalMap, shadowedLocals)).ToArray(),
                },
            LuaAnonFunctionExpr anon =>
                anon with
                {
                    Body = RewriteBlockChunkLocals(
                        anon.Body,
                        chunkLocalMap,
                        new HashSet<string>(shadowedLocals.Concat(anon.Parameters), StringComparer.Ordinal)),
                },
            _ => expr,
        };

    private static List<LuaStatement> PruneUnreachableStatements(IReadOnlyList<LuaStatement> stmts)
    {
        var pruned = new List<LuaStatement>(stmts.Count);
        foreach (LuaStatement stmt in stmts)
        {
            switch (stmt)
            {
                case LuaReturnStmt:
                case LuaBreakStmt:
                    pruned.Add(stmt);
                    return pruned;

                case LuaIfStmt ifs:
                    List<LuaStatement> thenBody = PruneUnreachableStatements(ifs.ThenBody);
                    List<LuaElseIfBranch> elseIfs = ifs.ElseIfBranches
                        .Select(ei => ei with { Body = PruneUnreachableStatements(ei.Body) })
                        .ToList();
                    List<LuaStatement> elseBody = PruneUnreachableStatements(ifs.ElseBody);
                    pruned.Add(ifs with { ThenBody = thenBody, ElseIfBranches = elseIfs, ElseBody = elseBody });
                    if (elseIfs.Count == 0 &&
                        elseBody.Count == 0 &&
                        TryEvalConstTruthy(ifs.Condition, out bool isTruthy) &&
                        isTruthy &&
                        BlockEndsWithReturnOrBreak(thenBody))
                    {
                        return pruned;
                    }

                    break;

                case LuaNumericForStmt nf:
                    pruned.Add(nf with { Body = PruneUnreachableStatements(nf.Body) });
                    break;

                case LuaWhileStmt ws:
                    pruned.Add(ws with { Body = PruneUnreachableStatements(ws.Body) });
                    break;

                case LuaDoBlockStmt db:
                    pruned.Add(db with { Body = PruneUnreachableStatements(db.Body) });
                    break;

                default:
                    pruned.Add(stmt);
                    break;
            }
        }

        return pruned;
    }

    private static bool BlockEndsWithReturnOrBreak(IReadOnlyList<LuaStatement> stmts) =>
        stmts.Count > 0 && stmts[^1] is LuaReturnStmt or LuaBreakStmt;

    private static bool TryEvalConstTruthy(LuaExpression expr, out bool result)
    {
        if (TryEvalConstNumber(expr, out double num))
        {
            result = true; // Any number (including 0) is truthy in Lua 4.0! Wait: comparisons below return bool explicitly.
            return true;
        }

        switch (expr)
        {
            case LuaNilExpr:
                result = false;
                return true;
            case LuaStringExpr:
                result = true;
                return true;
            case LuaBinaryExpr bin when bin.Operator is LuaBinaryOp.Eq or LuaBinaryOp.Neq or LuaBinaryOp.Lt or LuaBinaryOp.Le or LuaBinaryOp.Gt or LuaBinaryOp.Ge:
                if (TryEvalConstNumber(bin.Left, out double l) && TryEvalConstNumber(bin.Right, out double r))
                {
                    result = bin.Operator switch
                    {
                        LuaBinaryOp.Eq => l == r,
                        LuaBinaryOp.Neq => l != r,
                        LuaBinaryOp.Lt => l < r,
                        LuaBinaryOp.Le => l <= r,
                        LuaBinaryOp.Gt => l > r,
                        LuaBinaryOp.Ge => l >= r,
                        _ => false,
                    };
                    return true;
                }

                break;
            case LuaBinaryExpr bin when bin.Operator == LuaBinaryOp.And:
                if (TryEvalConstTruthy(bin.Left, out bool lAnd) && !lAnd)
                {
                    result = false;
                    return true;
                }

                if (TryEvalConstTruthy(bin.Left, out _) && TryEvalConstTruthy(bin.Right, out bool rAnd))
                {
                    result = rAnd;
                    return true;
                }

                break;
            case LuaUnaryExpr { Operator: LuaUnaryOp.Not } un when TryEvalConstTruthy(un.Operand, out bool inner):
                result = !inner;
                return true;
        }

        result = false;
        return false;
    }

    private static bool TryEvalConstNumber(LuaExpression expr, out double value)
    {
        switch (expr)
        {
            case LuaNumberExpr num:
                value = num.Value;
                return true;
            case LuaUnaryExpr { Operator: LuaUnaryOp.Negate } un when TryEvalConstNumber(un.Operand, out double inner):
                value = -inner;
                return true;
            default:
                value = 0;
                return false;
        }
    }
}
