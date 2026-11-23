using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using JXReborn.LegacyImporter.Legacy;
using JXReborn.ScriptMigrator.Models;

namespace JXReborn.ScriptMigrator.Analyzers;

/// <summary>
/// Deterministic token-level static analyzer for legacy Lua 4.0 scripts.
/// Supports 2-pass cross-file constant resolution (via <c>Include</c>), JX-Linux wrapper Task APIs,
/// strict <c>IsPureStaticDialog</c> classification, and include-preserving <c>NormalizedAstHash</c>.
/// </summary>
public sealed class LuaStaticAnalyzer
{
    private static readonly FrozenSet<string> DirectTaskApis = new[]
    {
        "GetTask",
        "SetTask",
        "nt_getTask",
        "nt_setTask",
        "SyncTaskValue",
        "GetBitTask",
        "SetBitTask",
        "PlayerFunLib.AddTaskDaily",
        "PlayerFunLib.GetTaskDailyCount",
        "PlayerFunLib.CheckTaskDaily",
        "PlayerFunLib.SetTaskBit",
        "PlayerFunLib.CheckTaskBit",
        "TaskManager.GetTask",
        "TaskManager.SetTask",
        "TaskManager.AddTask",
        "tbVNG_BitTask_Lib.getBitTask",
        "tbVNG_BitTask_Lib.setBitTask",
        "tbVNG_BitTask_Lib.addTask",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> BitByteApis = new[]
    {
        "GetByte",
        "SetByte",
        "GetBit",
        "SetBit",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> TempTaskApis = new[]
    {
        "GetTaskTemp",
        "SetTaskTemp",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> GlobalValueApis = new[]
    {
        "GetGlbValue",
        "SetGlbValue",
        "GetGlbMissionV",
        "SetGlbMissionV",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> PureStaticDialogAllowedCalls = new[]
    {
        "Talk",
        "Say",
        "random",
        "getn",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<Lua4TokenKind> BranchingKeywords = new[]
    {
        Lua4TokenKind.If,
        Lua4TokenKind.ElseIf,
        Lua4TokenKind.For,
        Lua4TokenKind.While,
        Lua4TokenKind.Repeat,
    }.ToFrozenSet();

    /// <summary>
    /// Pass 1 helper: extracts top-level / single-assignment integer constants (excluding mutable
    /// <c>local</c> variables initialized to 0) so cross-file <c>Include</c> resolution is accurate.
    /// </summary>
    public static Dictionary<string, int> ExtractExportedIntConstants(ReadOnlySpan<byte> rawBytes)
    {
        List<Lua4Token> tokens = Lua4Lexer.Tokenize(Encoding.Latin1.GetString(rawBytes));
        return ExtractSingleAssignmentIntConstants(tokens);
    }

    public static LuaAnalysisResult Analyze(
        ReadOnlySpan<byte> rawBytes,
        IReadOnlyDictionary<string, int>? externalIntConstants = null)
    {
        string latin1Src = Encoding.Latin1.GetString(rawBytes);
        int lineCount = 1;
        for (int i = 0; i < latin1Src.Length; i++)
        {
            if (latin1Src[i] == '\n')
            {
                lineCount++;
            }
        }

        List<Lua4Token> tokens = Lua4Lexer.Tokenize(latin1Src);
        Dictionary<string, int> localIntConstants = ExtractSingleAssignmentIntConstants(tokens);

        var calledApis = new HashSet<string>(StringComparer.Ordinal);
        var taskIds = new SortedSet<int>();
        var tempTaskIds = new SortedSet<int>();
        var globalValueIds = new SortedSet<int>();
        var includedFiles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var definedFunctions = new SortedSet<string>(StringComparer.Ordinal);

        bool hasControlFlowBranching = false;
        bool hasDialogCall = false;
        bool hasInteractiveDialogCallback = false;
        var astBuilder = new StringBuilder(tokens.Count * 6);

        for (int i = 0; i < tokens.Count; i++)
        {
            Lua4Token tok = tokens[i];
            if (tok.Kind == Lua4TokenKind.Eof)
            {
                break;
            }

            if (BranchingKeywords.Contains(tok.Kind))
            {
                hasControlFlowBranching = true;
            }

            // Build normalized token stream (preserve Include target string so different includes never collide)
            bool isIncludeStringArg = tok.Kind == Lua4TokenKind.String &&
                i >= 2 &&
                tokens[i - 1].Kind == Lua4TokenKind.LParen &&
                tokens[i - 2].Kind == Lua4TokenKind.Identifier &&
                (tokens[i - 2].Text.Equals("Include", StringComparison.OrdinalIgnoreCase) ||
                 tokens[i - 2].Text.Equals("IncludeLib", StringComparison.OrdinalIgnoreCase));

            astBuilder.Append((int)tok.Kind);
            if (tok.Kind == Lua4TokenKind.Identifier)
            {
                astBuilder.Append(':').Append(tok.Text);
            }
            else if (tok.Kind == Lua4TokenKind.Number)
            {
                astBuilder.Append(":#");
            }
            else if (tok.Kind == Lua4TokenKind.String)
            {
                astBuilder.Append(isIncludeStringArg ? $":{tok.Text}" : ":$");
            }

            astBuilder.Append(';');

            // Track function definitions: function foo(...)
            if (tok.Kind == Lua4TokenKind.Function &&
                i + 1 < tokens.Count &&
                tokens[i + 1].Kind == Lua4TokenKind.Identifier)
            {
                definedFunctions.Add(tokens[i + 1].Text);
            }

            // Track function calls: Ident(...) — excluding function definitions `function Ident(...)`
            bool isFunctionDefName = i > 0 && tokens[i - 1].Kind == Lua4TokenKind.Function;
            if (!isFunctionDefName &&
                tok.Kind == Lua4TokenKind.Identifier &&
                i + 1 < tokens.Count &&
                tokens[i + 1].Kind == Lua4TokenKind.LParen)
            {
                string fnName = tok.Text;
                bool isMemberCall = i > 0 && tokens[i - 1].Kind is Lua4TokenKind.Dot or Lua4TokenKind.Colon;
                string apiKey = isMemberCall && i >= 2 && tokens[i - 2].Kind == Lua4TokenKind.Identifier
                    ? $"{tokens[i - 2].Text}.{fnName}"
                    : fnName;

                calledApis.Add(apiKey);

                if (fnName is "Talk" or "Say" or "Describe" or "CreateNewSayEx")
                {
                    hasDialogCall = true;
                    if (IsInteractiveDialogCall(fnName, tokens, i + 2))
                    {
                        hasInteractiveDialogCallback = true;
                    }
                }

                if ((fnName.Equals("Include", StringComparison.OrdinalIgnoreCase) ||
                     fnName.Equals("IncludeLib", StringComparison.OrdinalIgnoreCase)) &&
                    i + 2 < tokens.Count &&
                    tokens[i + 2].Kind == Lua4TokenKind.String)
                {
                    string rawInc = tokens[i + 2].Text;
                    byte[] incBytes = Encoding.Latin1.GetBytes(rawInc);
                    string decodedInc = HybridLegacyTextDecoder.DecodeSmart(incBytes, preferVietnamese: false)
                        .Replace('\\', '/')
                        .Trim();
                    if (decodedInc.Length > 0)
                    {
                        includedFiles.Add(decodedInc);
                    }
                }

                if (DirectTaskApis.Contains(apiKey) || DirectTaskApis.Contains(fnName))
                {
                    if (TryResolveFirstIntArg(tokens, i + 2, localIntConstants, externalIntConstants, out int taskId) && taskId > 0)
                    {
                        taskIds.Add(taskId);
                    }
                }
                else if (BitByteApis.Contains(fnName))
                {
                    // Handle GetByte(GetTask(id), byteIdx) / SetBit(GetTask(id), bitIdx, val)
                    if (i + 4 < tokens.Count &&
                        tokens[i + 2].Kind == Lua4TokenKind.Identifier &&
                        (tokens[i + 2].Text.Equals("GetTask", StringComparison.Ordinal) ||
                         tokens[i + 2].Text.Equals("nt_getTask", StringComparison.Ordinal)) &&
                        tokens[i + 3].Kind == Lua4TokenKind.LParen &&
                        TryResolveFirstIntArg(tokens, i + 4, localIntConstants, externalIntConstants, out int innerTaskId) &&
                        innerTaskId > 0)
                    {
                        taskIds.Add(innerTaskId);
                    }
                }
                else if (TempTaskApis.Contains(fnName) &&
                         TryResolveFirstIntArg(tokens, i + 2, localIntConstants, externalIntConstants, out int tempId) &&
                         tempId > 0)
                {
                    tempTaskIds.Add(tempId);
                }
                else if (GlobalValueApis.Contains(fnName) &&
                         TryResolveFirstIntArg(tokens, i + 2, localIntConstants, externalIntConstants, out int glbId) &&
                         glbId > 0)
                {
                    globalValueIds.Add(glbId);
                }
            }
        }

        // Strict pure static dialog:
        // - has Talk/Say, NO interactive callbacks/menus, NO branching, NO task/temp/glb state, NO includes
        // - only defines `main` (at most 1 function) and only calls trivial pure-dialog APIs
        bool isPureStaticDialog =
            hasDialogCall &&
            !hasInteractiveDialogCallback &&
            !hasControlFlowBranching &&
            taskIds.Count == 0 &&
            tempTaskIds.Count == 0 &&
            globalValueIds.Count == 0 &&
            includedFiles.Count == 0 &&
            definedFunctions.Count == 1 &&
            definedFunctions.Contains("main") &&
            calledApis.All(api => PureStaticDialogAllowedCalls.Contains(api));

        string astHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(astBuilder.ToString())));

        return new LuaAnalysisResult
        {
            LineCount = lineCount,
            NormalizedAstHash = astHash[..16],
            IsPureStaticDialog = isPureStaticDialog,
            CalledApis = calledApis.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            ReferencedTaskIds = taskIds.ToArray(),
            ReferencedTempTaskIds = tempTaskIds.ToArray(),
            ReferencedGlobalValueIds = globalValueIds.ToArray(),
            IncludedFiles = includedFiles.ToArray(),
            DefinedFunctions = definedFunctions.ToArray(),
        };
    }

    private static Dictionary<string, int> ExtractSingleAssignmentIntConstants(List<Lua4Token> tokens)
    {
        var assignmentCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var values = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = 0; i < tokens.Count; i++)
        {
            Lua4Token tok = tokens[i];
            if (tok.Kind == Lua4TokenKind.Identifier &&
                i + 2 < tokens.Count &&
                tokens[i + 1].Kind == Lua4TokenKind.Assign)
            {
                bool isLocal = i > 0 && tokens[i - 1].Kind == Lua4TokenKind.Local;
                assignmentCounts[tok.Text] = assignmentCounts.GetValueOrDefault(tok.Text) + 1;

                if (tokens[i + 2].Kind == Lua4TokenKind.Number)
                {
                    int val = (int)Math.Truncate(tokens[i + 2].NumberValue);
                    // Exclude `local x = 0` mutable accumulators; only retain valid positive IDs
                    if (val is > 0 and <= 65535 && !(isLocal && val == 0))
                    {
                        values[tok.Text] = val;
                    }
                }
            }
        }

        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, int> kv in values)
        {
            if (assignmentCounts.GetValueOrDefault(kv.Key) == 1)
            {
                result[kv.Key] = kv.Value;
            }
        }

        return result;
    }

    private static bool IsInteractiveDialogCall(string fnName, List<Lua4Token> tokens, int firstArgIndex)
    {
        if (fnName is "Describe" or "CreateNewSayEx")
        {
            return true;
        }

        // Find the second top-level argument inside (...)
        int arg2Index = FindNthArgStart(tokens, firstArgIndex, targetArgIndex: 1);
        if (arg2Index < 0 || arg2Index >= tokens.Count)
        {
            return false;
        }

        if (fnName == "Say")
        {
            // Say(msg, 0) is static; Say(msg, n, ...) where n != 0 or expression is interactive
            Lua4Token countTok = tokens[arg2Index];
            return !(countTok.Kind == Lua4TokenKind.Number && Math.Abs(countTok.NumberValue) < 0.0001);
        }

        if (fnName == "Talk")
        {
            // Talk(n, "", ...) or Talk(n, nil, ...) is static; non-empty callback string is interactive
            Lua4Token cbTok = tokens[arg2Index];
            if (cbTok.Kind == Lua4TokenKind.Nil)
            {
                return false;
            }

            if (cbTok.Kind == Lua4TokenKind.String)
            {
                return !string.IsNullOrWhiteSpace(cbTok.Text);
            }

            return true;
        }

        return false;
    }

    private static int FindNthArgStart(List<Lua4Token> tokens, int firstArgIndex, int targetArgIndex)
    {
        if (targetArgIndex == 0)
        {
            return firstArgIndex;
        }

        int depth = 0;
        int currentArg = 0;
        for (int i = firstArgIndex; i < tokens.Count; i++)
        {
            Lua4TokenKind kind = tokens[i].Kind;
            if (kind == Lua4TokenKind.Eof)
            {
                return -1;
            }

            if (kind is Lua4TokenKind.LParen or Lua4TokenKind.LBrace or Lua4TokenKind.LBracket)
            {
                depth++;
            }
            else if (kind is Lua4TokenKind.RParen or Lua4TokenKind.RBrace or Lua4TokenKind.RBracket)
            {
                if (depth == 0)
                {
                    return -1;
                }

                depth--;
            }
            else if (kind == Lua4TokenKind.Comma && depth == 0)
            {
                currentArg++;
                if (currentArg == targetArgIndex)
                {
                    return i + 1;
                }
            }
        }

        return -1;
    }

    private static bool TryResolveFirstIntArg(
        List<Lua4Token> tokens,
        int argIndex,
        Dictionary<string, int> localIntConstants,
        IReadOnlyDictionary<string, int>? externalIntConstants,
        out int value)
    {
        value = 0;
        if (argIndex >= tokens.Count)
        {
            return false;
        }

        Lua4Token arg = tokens[argIndex];
        if (arg.Kind == Lua4TokenKind.Number)
        {
            int parsed = (int)Math.Truncate(arg.NumberValue);
            if (parsed is >= 0 and <= 65535)
            {
                value = parsed;
                return true;
            }
        }
        else if (arg.Kind == Lua4TokenKind.Identifier)
        {
            if (localIntConstants.TryGetValue(arg.Text, out int resolved))
            {
                value = resolved;
                return true;
            }

            if (externalIntConstants is not null && externalIntConstants.TryGetValue(arg.Text, out int extResolved))
            {
                value = extResolved;
                return true;
            }
        }

        return false;
    }
}
