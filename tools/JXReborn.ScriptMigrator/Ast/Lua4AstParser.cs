using System;
using System.Collections.Generic;
using System.Text;
using JXReborn.LegacyImporter.Legacy;

namespace JXReborn.ScriptMigrator.Ast;

/// <summary>
/// Deterministic, strictly fail-closed recursive-descent AST parser for the supported Lua 4.0
/// script subset. Any unsupported syntax (loops, upvalues, varargs, method calls, keyed tables,
/// multi-return, table mutation) returns a structured failure reason rather than guessing or skipping.
/// </summary>
public sealed class Lua4AstParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public LuaParseResult Parse(ReadOnlySpan<byte> rawBytes)
    {
        string latin1Src = Encoding.Latin1.GetString(rawBytes);
        List<Lua4Token> tokens = Lua4Lexer.Tokenize(latin1Src);
        var state = new ParserState(tokens);

        try
        {
            var topLevel = new List<LuaStatement>();
            var functions = new List<LuaFunctionDecl>();
            var functionNames = new HashSet<string>(StringComparer.Ordinal);

            while (!state.IsAtEnd)
            {
                state.SkipSemicolons();
                if (state.IsAtEnd)
                {
                    break;
                }

                if (state.Current.Kind == Lua4TokenKind.Function)
                {
                    LuaFunctionDecl fn = state.ParseFunctionDecl();
                    if (!functionNames.Add(fn.Name))
                    {
                        return Fail($"duplicate-function:{fn.Name}@line{fn.Line}");
                    }

                    functions.Add(fn);
                }
                else
                {
                    topLevel.Add(state.ParseStatement());
                }

                state.SkipSemicolons();
            }

            return new LuaParseResult(new LuaProgram(topLevel, functions), null);
        }
        catch (LuaAstParseFailureException ex)
        {
            return Fail(ex.Reason);
        }
    }

    /// <summary>
    /// Parses an included library/header file in declaration-isolating mode: if an individual helper
    /// function or pure top-level table literal uses unsupported syntax (e.g. <c>keyed-table</c> or
    /// <c>varargs</c>), that specific symbol is recorded with <see cref="LuaUnsupportedStmt"/> /
    /// <see cref="LuaUnsupportedExpr"/> rather than aborting the whole header. If any reachable code
    /// calls or reads that symbol, semantic validation still strictly fails closed with the exact reason.
    /// </summary>
    public LuaParseResult ParseLibrary(ReadOnlySpan<byte> rawBytes)
    {
        string latin1Src = Encoding.Latin1.GetString(rawBytes);
        List<Lua4Token> tokens = Lua4Lexer.Tokenize(latin1Src);
        var state = new ParserState(tokens);

        try
        {
            var topLevel = new List<LuaStatement>();
            var functionsByOrder = new List<LuaFunctionDecl>();
            var fnIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);

            state.ParseLibraryDeclarationsUntil(stopOnEnd: false, topLevel, functionsByOrder, fnIndexByName);

            return new LuaParseResult(new LuaProgram(topLevel, functionsByOrder), null);
        }
        catch (LuaAstParseFailureException ex)
        {
            return Fail(ex.Reason);
        }
    }

    public static string DecodeLuaStringLiteral(string rawTokenText)
    {
        if (string.IsNullOrEmpty(rawTokenText))
        {
            return string.Empty;
        }

        // Lua4Lexer.Tokenize has already unescaped Lua escape sequences (\n, \t, \\, \ddd) once
        // into byte-preserving Latin1 characters. Do NOT unescape a second time.
        byte[] raw = Encoding.Latin1.GetBytes(rawTokenText);
        if (raw.Length == 0)
        {
            return string.Empty;
        }

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

    private static LuaParseResult Fail(string reason) => new(null, reason);

    private sealed class LuaAstParseFailureException(string reason) : Exception(reason)
    {
        public string Reason { get; } = reason;
    }

    private sealed class ParserState(List<Lua4Token> tokens)
    {
        private readonly List<Lua4Token> _tokens = tokens;
        private int _pos;

        public Lua4Token Current => _pos < _tokens.Count ? _tokens[_pos] : _tokens[^1];

        public bool IsAtEnd => Current.Kind == Lua4TokenKind.Eof;

        private Lua4Token Peek(int offset) =>
            _pos + offset < _tokens.Count ? _tokens[_pos + offset] : _tokens[^1];

        private Lua4Token Advance()
        {
            Lua4Token tok = Current;
            if (_pos < _tokens.Count - 1)
            {
                _pos++;
            }

            return tok;
        }

        private Lua4Token Expect(Lua4TokenKind expected)
        {
            if (Current.Kind != expected)
            {
                throw Abort($"syntax-error:expected-{expected}-got-{Current.Kind}@line{Current.Line}");
            }

            return Advance();
        }

        public void SkipSemicolons()
        {
            while (Current.Kind == Lua4TokenKind.Semicolon)
            {
                Advance();
            }
        }

        public LuaFunctionDecl ParseFunctionDecl()
        {
            Lua4Token fnTok = Expect(Lua4TokenKind.Function);
            if (Current.Kind != Lua4TokenKind.Identifier)
            {
                throw Abort($"unsupported-syntax:anonymous-or-non-simple-function@line{fnTok.Line}");
            }

            string name = Advance().Text;
            if (Current.Kind is Lua4TokenKind.Dot or Lua4TokenKind.Colon)
            {
                throw Abort($"unsupported-syntax:member-function({name})@line{fnTok.Line}");
            }

            Expect(Lua4TokenKind.LParen);
            var parameters = new List<string>();
            if (Current.Kind != Lua4TokenKind.RParen)
            {
                while (true)
                {
                    if (Current.Kind == Lua4TokenKind.Dots)
                    {
                        throw Abort($"unsupported-syntax:varargs@line{Current.Line}");
                    }

                    if (Current.Kind != Lua4TokenKind.Identifier)
                    {
                        throw Abort($"syntax-error:expected-param-got-{Current.Kind}@line{Current.Line}");
                    }

                    parameters.Add(Advance().Text);
                    if (Current.Kind == Lua4TokenKind.Comma)
                    {
                        Advance();
                        continue;
                    }

                    break;
                }
            }

            Expect(Lua4TokenKind.RParen);
            List<LuaStatement> body = ParseBlockUntil(Lua4TokenKind.End);
            Expect(Lua4TokenKind.End);
            SkipSemicolons();
            return new LuaFunctionDecl(name, parameters, body, fnTok.Line);
        }

        public LuaFunctionDecl? ParseFunctionDeclLibrarySafe()
        {
            int startPos = _pos;
            Lua4Token fnTok = Expect(Lua4TokenKind.Function);
            if (Current.Kind != Lua4TokenKind.Identifier)
            {
                SkipFromFunctionHeaderToMatchingEnd(startPos + 1);
                return null;
            }

            string name = Advance().Text;
            if (Current.Kind is Lua4TokenKind.Dot or Lua4TokenKind.Colon)
            {
                // Table/OOP method definition `function tbl:Method(...)` cannot be called as a bare identifier;
                // skip its balanced body cleanly (any caller attempting `tbl:Method()` fails closed on member-access).
                SkipFromFunctionHeaderToMatchingEnd(startPos + 1);
                return null;
            }

            _pos = startPos;
            try
            {
                return ParseFunctionDecl();
            }
            catch (LuaAstParseFailureException ex)
            {
                SkipFromFunctionHeaderToMatchingEnd(startPos + 1);
                SkipSemicolons();
                return new LuaFunctionDecl(name, [], [new LuaUnsupportedStmt(ex.Reason, fnTok.Line)], fnTok.Line);
            }
        }

        public void ParseLibraryDeclarationsUntil(
            bool stopOnEnd,
            List<LuaStatement> topLevel,
            List<LuaFunctionDecl> functionsByOrder,
            Dictionary<string, int> fnIndexByName)
        {
            while (!IsAtEnd)
            {
                SkipSemicolons();
                if (IsAtEnd || (stopOnEnd && Current.Kind == Lua4TokenKind.End))
                {
                    break;
                }

                if (Current.Kind == Lua4TokenKind.Function)
                {
                    LuaFunctionDecl? fn = ParseFunctionDeclLibrarySafe();
                    if (fn is not null)
                    {
                        if (fnIndexByName.TryGetValue(fn.Name, out int existingIdx))
                        {
                            functionsByOrder[existingIdx] = fn;
                        }
                        else
                        {
                            fnIndexByName[fn.Name] = functionsByOrder.Count;
                            functionsByOrder.Add(fn);
                        }
                    }
                }
                else if (Current.Kind == Lua4TokenKind.If && TryEnterTopLevelIncludeGuard())
                {
                    ParseLibraryDeclarationsUntil(stopOnEnd: true, topLevel, functionsByOrder, fnIndexByName);
                    Expect(Lua4TokenKind.End);
                }
                else
                {
                    topLevel.Add(ParseTopLevelLibraryStatement());
                }

                SkipSemicolons();
            }
        }

        private bool TryEnterTopLevelIncludeGuard()
        {
            int savedPos = _pos;
            Advance(); // consume `if`

            int parens = 0;
            while (Current.Kind == Lua4TokenKind.LParen)
            {
                parens++;
                Advance();
            }

            bool matchedGuardCond = false;
            if (Current.Kind == Lua4TokenKind.Not && Peek(1).Kind == Lua4TokenKind.Identifier)
            {
                Advance();
                Advance();
                matchedGuardCond = true;
            }
            else if (Current.Kind == Lua4TokenKind.Identifier &&
                     Peek(1).Kind == Lua4TokenKind.Eq &&
                     Peek(2).Kind == Lua4TokenKind.Nil)
            {
                Advance();
                Advance();
                Advance();
                matchedGuardCond = true;
            }
            else if (Current.Kind == Lua4TokenKind.Identifier &&
                     Current.Text is "MODEL_GAMESERVER" or "GAMESERVER" &&
                     Peek(1).Kind == Lua4TokenKind.Eq &&
                     Peek(2).Kind == Lua4TokenKind.Number &&
                     Peek(2).Text == "1")
            {
                Advance();
                Advance();
                Advance();
                while (Current.Kind == Lua4TokenKind.Or &&
                       Peek(1).Kind == Lua4TokenKind.Identifier &&
                       Peek(2).Kind == Lua4TokenKind.Eq &&
                       Peek(3).Kind == Lua4TokenKind.Number)
                {
                    Advance();
                    Advance();
                    Advance();
                    Advance();
                }

                matchedGuardCond = true;
            }
            else if (Current.Kind == Lua4TokenKind.Number && Current.Text == "1")
            {
                Advance();
                matchedGuardCond = true;
            }

            if (!matchedGuardCond)
            {
                _pos = savedPos;
                return false;
            }

            for (int p = 0; p < parens; p++)
            {
                if (Current.Kind != Lua4TokenKind.RParen)
                {
                    _pos = savedPos;
                    return false;
                }

                Advance();
            }

            if (Current.Kind != Lua4TokenKind.Then)
            {
                _pos = savedPos;
                return false;
            }

            Advance(); // consume `then`

            // Verify there is no `else` or `elseif` at depth 0 before the matching `end`
            int depth = 0;
            for (int scan = _pos; scan < _tokens.Count; scan++)
            {
                Lua4TokenKind k = _tokens[scan].Kind;
                if (k == Lua4TokenKind.Eof)
                {
                    _pos = savedPos;
                    return false;
                }

                if (k is Lua4TokenKind.If or Lua4TokenKind.For or Lua4TokenKind.While or Lua4TokenKind.Function)
                {
                    depth++;
                }
                else if (k == Lua4TokenKind.Do)
                {
                    // Standalone `do ... end` increments depth; `while ... do` / `for ... do` already counted `while`/`for`
                    bool partOfLoop = false;
                    for (int back = scan - 1; back >= _pos; back--)
                    {
                        Lua4TokenKind bk = _tokens[back].Kind;
                        if (bk is Lua4TokenKind.While or Lua4TokenKind.For)
                        {
                            partOfLoop = true;
                            break;
                        }

                        if (bk is Lua4TokenKind.Do or Lua4TokenKind.Then or Lua4TokenKind.End or Lua4TokenKind.Semicolon)
                        {
                            break;
                        }
                    }

                    if (!partOfLoop)
                    {
                        depth++;
                    }
                }
                else if (depth == 0 && k is Lua4TokenKind.Else or Lua4TokenKind.ElseIf)
                {
                    _pos = savedPos;
                    return false;
                }
                else if (k == Lua4TokenKind.End)
                {
                    if (depth == 0)
                    {
                        return true;
                    }

                    depth--;
                }
            }

            _pos = savedPos;
            return false;
        }

        public LuaStatement ParseTopLevelLibraryStatement()
        {
            int startPos = _pos;
            try
            {
                return ParseStatement();
            }
            catch (LuaAstParseFailureException ex)
            {
                // Isolate pure unsupported top-level assignments or library control-flow blocks
                _pos = startPos;
                if (TryRecoverPureUnsupportedTableAssignment(ex.Reason, out LuaStatement? recovered) && recovered is not null)
                {
                    return recovered;
                }

                _pos = startPos;
                if (Current.Kind is Lua4TokenKind.If or Lua4TokenKind.For or Lua4TokenKind.While or Lua4TokenKind.Do)
                {
                    int line = Current.Line;
                    SkipBalancedBlockAtCurrentKeyword();
                    return new LuaUnsupportedStmt(ex.Reason, line);
                }

                throw;
            }
        }

        private void SkipBalancedBlockAtCurrentKeyword()
        {
            int depth = 0;
            while (!IsAtEnd)
            {
                Lua4TokenKind k = Current.Kind;
                if (k is Lua4TokenKind.Function or Lua4TokenKind.If or Lua4TokenKind.Do)
                {
                    depth++;
                }
                else if (k == Lua4TokenKind.End)
                {
                    depth--;
                    if (depth <= 0)
                    {
                        Advance();
                        return;
                    }
                }

                Advance();
            }
        }

        private bool TryRecoverPureUnsupportedTableAssignment(string reason, out LuaStatement? stmt)
        {
            stmt = null;
            int line = Current.Line;
            bool isLocal = false;
            if (Current.Kind == Lua4TokenKind.Local)
            {
                isLocal = true;
                Advance();
                if (Current.Kind == Lua4TokenKind.Function)
                {
                    int afterFnPos = _pos + 1;
                    Advance();
                    if (Current.Kind == Lua4TokenKind.Identifier)
                    {
                        string localFnName = Advance().Text;
                        SkipFromFunctionHeaderToMatchingEnd(afterFnPos);
                        stmt = new LuaLocalDeclStmt(localFnName, new LuaUnsupportedExpr(reason, line), line);
                        return true;
                    }

                    return false;
                }
            }

            if (Current.Kind != Lua4TokenKind.Identifier)
            {
                return false;
            }

            string varName = Advance().Text;
            bool hadFieldChain = false;
            if (!isLocal)
            {
                while (Current.Kind == Lua4TokenKind.Dot && Peek(1).Kind == Lua4TokenKind.Identifier)
                {
                    Advance();
                    Advance();
                    hadFieldChain = true;
                }
            }

            if (Current.Kind != Lua4TokenKind.Assign)
            {
                return false;
            }

            Advance();
            if (Current.Kind == Lua4TokenKind.LBrace)
            {
                if (!TrySkipBalancedPureBraces())
                {
                    return false;
                }
            }
            else if (Current.Kind == Lua4TokenKind.Function)
            {
                SkipFromFunctionHeaderToMatchingEnd(_pos + 1);
            }
            else if (Current.Kind == Lua4TokenKind.Identifier &&
                     string.Equals(Current.Text, "inherit", StringComparison.Ordinal) &&
                     Peek(1).Kind == Lua4TokenKind.LParen)
            {
                Advance(); // 'inherit'
                if (!TrySkipBalancedParensIncludingFunctions())
                {
                    return false;
                }
            }
            else if (hadFieldChain && Current.Kind is Lua4TokenKind.Nil or Lua4TokenKind.Number or Lua4TokenKind.String)
            {
                Advance();
            }
            else
            {
                return false;
            }

            var unsupported = new LuaUnsupportedExpr(reason, line);
            stmt = isLocal
                ? new LuaLocalDeclStmt(varName, unsupported, line)
                : new LuaAssignStmt(varName, unsupported, line);
            return true;
        }

        private bool TrySkipBalancedPureBraces()
        {
            if (Current.Kind != Lua4TokenKind.LBrace)
            {
                return false;
            }

            int depth = 0;
            while (!IsAtEnd)
            {
                Lua4TokenKind k = Current.Kind;
                if (k == Lua4TokenKind.LBrace)
                {
                    depth++;
                    Advance();
                    continue;
                }

                if (k == Lua4TokenKind.RBrace)
                {
                    depth--;
                    Advance();
                    if (depth == 0)
                    {
                        return true;
                    }

                    continue;
                }

                // If the table embeds an anonymous `function(...) ... end`, skip its balanced body too
                // (defining a function value inside a table has zero side effect until invoked).
                if (k == Lua4TokenKind.Function)
                {
                    int afterFn = _pos + 1;
                    SkipFromFunctionHeaderToMatchingEnd(afterFn);
                    continue;
                }

                // Reject unguarded calls `foo(...)` inside a skipped table literal because calling a function at top-level may mutate state
                if (k == Lua4TokenKind.Identifier && Peek(1).Kind == Lua4TokenKind.LParen)
                {
                    return false;
                }

                Advance();
            }

            return false;
        }

        private bool TrySkipBalancedParensIncludingFunctions()
        {
            if (Current.Kind != Lua4TokenKind.LParen)
            {
                return false;
            }

            int parenDepth = 0;
            while (!IsAtEnd)
            {
                if (Current.Kind == Lua4TokenKind.Function)
                {
                    SkipFromFunctionHeaderToMatchingEnd(_pos + 1);
                    continue;
                }

                if (Current.Kind == Lua4TokenKind.LParen)
                {
                    parenDepth++;
                }
                else if (Current.Kind == Lua4TokenKind.RParen)
                {
                    parenDepth--;
                    if (parenDepth == 0)
                    {
                        Advance();
                        return true;
                    }
                }

                Advance();
            }

            return false;
        }

        private void SkipFromFunctionHeaderToMatchingEnd(int posAfterFunctionKeyword)
        {
            _pos = posAfterFunctionKeyword;
            int depth = 1;
            while (!IsAtEnd && depth > 0)
            {
                Lua4TokenKind k = Current.Kind;
                if (k is Lua4TokenKind.Function or Lua4TokenKind.If or Lua4TokenKind.Do)
                {
                    depth++;
                }
                else if (k == Lua4TokenKind.For || k == Lua4TokenKind.While)
                {
                    // Note: `for ... do ... end` and `while ... do ... end` contain a `do` keyword that belongs to the loop!
                    // Counting `Function`, `If`, `Do` (and NOT `For`/`While`) matches every `End` 1-to-1 in Lua 4.0!
                }
                else if (k == Lua4TokenKind.End)
                {
                    depth--;
                }

                Advance();
            }
        }

        private List<LuaStatement> ParseBlockUntil(params Lua4TokenKind[] stopTokens)
        {
            var stmts = new List<LuaStatement>();
            while (!IsAtEnd && !IsIn(Current.Kind, stopTokens))
            {
                SkipSemicolons();
                if (IsAtEnd || IsIn(Current.Kind, stopTokens))
                {
                    break;
                }

                stmts.Add(ParseStatement());
                SkipSemicolons();
            }

            return stmts;
        }

        public LuaStatement ParseStatement()
        {
            Lua4Token tok = Current;
            switch (tok.Kind)
            {
                case Lua4TokenKind.If:
                    return ParseIfStatement();
                case Lua4TokenKind.Local:
                    return ParseLocalDecl();
                case Lua4TokenKind.Return:
                    return ParseReturnStatement();
                case Lua4TokenKind.For:
                    return ParseForStatement();
                case Lua4TokenKind.While:
                    return ParseWhileStatement();
                case Lua4TokenKind.Break:
                    Advance();
                    return new LuaBreakStmt(tok.Line);
                case Lua4TokenKind.Do:
                    return ParseDoBlockStatement();
                case Lua4TokenKind.Repeat:
                    throw Abort($"unsupported-syntax:loop-or-break({tok.Text})@line{tok.Line}");
                case Lua4TokenKind.Function:
                    throw Abort($"unsupported-syntax:nested-function@line{tok.Line}");
                case Lua4TokenKind.Identifier:
                    return ParseIdentifierStatement();
                case Lua4TokenKind.UpvalueIdentifier:
                    return ParseUpvalueStatement();
                default:
                    throw Abort($"syntax-error:unexpected-{tok.Kind}({tok.Text})@line{tok.Line}");
            }
        }

        private LuaStatement ParseForStatement()
        {
            Lua4Token forTok = Expect(Lua4TokenKind.For);
            Lua4Token iterTok = Expect(Lua4TokenKind.Identifier);
            if (Current.Kind is Lua4TokenKind.Comma or Lua4TokenKind.In)
            {
                throw Abort($"unsupported-syntax:table-for@line{forTok.Line}");
            }

            Expect(Lua4TokenKind.Assign);
            LuaExpression start = ParseExpression();
            Expect(Lua4TokenKind.Comma);
            LuaExpression limit = ParseExpression();
            LuaExpression? step = null;
            if (Current.Kind == Lua4TokenKind.Comma)
            {
                Advance();
                step = ParseExpression();
            }

            Expect(Lua4TokenKind.Do);
            List<LuaStatement> body = ParseBlockUntil(Lua4TokenKind.End);
            Expect(Lua4TokenKind.End);
            return new LuaNumericForStmt(iterTok.Text, start, limit, step, body, forTok.Line);
        }

        private LuaWhileStmt ParseWhileStatement()
        {
            Lua4Token whileTok = Expect(Lua4TokenKind.While);
            LuaExpression cond = ParseExpression();
            Expect(Lua4TokenKind.Do);
            List<LuaStatement> body = ParseBlockUntil(Lua4TokenKind.End);
            Expect(Lua4TokenKind.End);
            return new LuaWhileStmt(cond, body, whileTok.Line);
        }

        private LuaDoBlockStmt ParseDoBlockStatement()
        {
            Lua4Token doTok = Expect(Lua4TokenKind.Do);
            List<LuaStatement> body = ParseBlockUntil(Lua4TokenKind.End);
            Expect(Lua4TokenKind.End);
            return new LuaDoBlockStmt(body, doTok.Line);
        }

        private LuaIfStmt ParseIfStatement()
        {
            Lua4Token ifTok = Expect(Lua4TokenKind.If);
            LuaExpression cond = ParseExpression();
            Expect(Lua4TokenKind.Then);
            List<LuaStatement> thenBody = ParseBlockUntil(Lua4TokenKind.ElseIf, Lua4TokenKind.Else, Lua4TokenKind.End);

            var elseIfs = new List<LuaElseIfBranch>();
            while (Current.Kind == Lua4TokenKind.ElseIf)
            {
                Lua4Token eiTok = Advance();
                LuaExpression eiCond = ParseExpression();
                Expect(Lua4TokenKind.Then);
                List<LuaStatement> eiBody = ParseBlockUntil(Lua4TokenKind.ElseIf, Lua4TokenKind.Else, Lua4TokenKind.End);
                elseIfs.Add(new LuaElseIfBranch(eiCond, eiBody, eiTok.Line));
            }

            List<LuaStatement> elseBody = [];
            if (Current.Kind == Lua4TokenKind.Else)
            {
                Advance();
                elseBody = ParseBlockUntil(Lua4TokenKind.End);
            }

            Expect(Lua4TokenKind.End);
            return new LuaIfStmt(cond, thenBody, elseIfs, elseBody, ifTok.Line);
        }

        private LuaStatement ParseLocalDecl()
        {
            Lua4Token localTok = Expect(Lua4TokenKind.Local);
            if (Current.Kind == Lua4TokenKind.Function)
            {
                throw Abort($"unsupported-syntax:local-function@line{localTok.Line}");
            }

            var names = new List<string> { Expect(Lua4TokenKind.Identifier).Text };
            while (Current.Kind == Lua4TokenKind.Comma)
            {
                Advance();
                names.Add(Expect(Lua4TokenKind.Identifier).Text);
            }

            var inits = new List<LuaExpression>();
            if (Current.Kind == Lua4TokenKind.Assign)
            {
                Advance();
                inits.Add(ParseExpression());
                while (Current.Kind == Lua4TokenKind.Comma)
                {
                    Advance();
                    inits.Add(ParseExpression());
                }
            }

            if (names.Count == 1 && inits.Count <= 1)
            {
                return new LuaLocalDeclStmt(names[0], inits.Count == 1 ? inits[0] : null, localTok.Line);
            }

            return new LuaMultiLocalDeclStmt(names, inits, localTok.Line);
        }

        private LuaReturnStmt ParseReturnStatement()
        {
            Lua4Token retTok = Expect(Lua4TokenKind.Return);
            if (Current.Kind is Lua4TokenKind.End or Lua4TokenKind.Else or Lua4TokenKind.ElseIf or Lua4TokenKind.Semicolon or Lua4TokenKind.Eof)
            {
                return new LuaReturnStmt(null, retTok.Line);
            }

            LuaExpression first = ParseExpression();
            if (Current.Kind != Lua4TokenKind.Comma)
            {
                return new LuaReturnStmt(first, retTok.Line);
            }

            var extras = new List<LuaExpression>();
            while (Current.Kind == Lua4TokenKind.Comma)
            {
                Advance();
                extras.Add(ParseExpression());
            }

            return new LuaReturnStmt(first, retTok.Line, extras);
        }

        private LuaStatement ParseIdentifierStatement()
        {
            Lua4Token identTok = Expect(Lua4TokenKind.Identifier);

            if (Current.Kind == Lua4TokenKind.Assign)
            {
                Advance();
                LuaExpression firstRhs = ParseExpression();
                if (Current.Kind != Lua4TokenKind.Comma)
                {
                    return new LuaAssignStmt(identTok.Text, firstRhs, identTok.Line);
                }

                var values = new List<LuaExpression> { firstRhs };
                while (Current.Kind == Lua4TokenKind.Comma)
                {
                    Advance();
                    values.Add(ParseExpression());
                }

                return new LuaMultiAssignStmt([identTok.Text], values, identTok.Line);
            }

            if (Current.Kind == Lua4TokenKind.LParen)
            {
                LuaCallExpr call = ParseCallArguments(identTok);
                if (Current.Kind is Lua4TokenKind.Dot or Lua4TokenKind.Colon or Lua4TokenKind.LBracket)
                {
                    LuaExpression chained = ParsePostfixContinuation(call);
                    return CompletePostfixStatement(chained, identTok.Line);
                }

                if (Current.Kind == Lua4TokenKind.LParen)
                {
                    throw Abort($"unsupported-syntax:chained-call({identTok.Text})@line{identTok.Line}");
                }

                return new LuaCallStmt(call, identTok.Line);
            }

            if (Current.Kind == Lua4TokenKind.Comma)
            {
                var targets = new List<string> { identTok.Text };
                while (Current.Kind == Lua4TokenKind.Comma)
                {
                    Advance();
                    if (Current.Kind != Lua4TokenKind.Identifier)
                    {
                        throw Abort($"unsupported-syntax:complex-multi-assign({identTok.Text})@line{identTok.Line}");
                    }

                    Lua4Token nextTarget = Advance();
                    if (Current.Kind is Lua4TokenKind.LBracket or Lua4TokenKind.Dot or Lua4TokenKind.Colon)
                    {
                        throw Abort($"unsupported-syntax:complex-multi-assign({nextTarget.Text})@line{identTok.Line}");
                    }

                    targets.Add(nextTarget.Text);
                }

                Expect(Lua4TokenKind.Assign);
                var values = new List<LuaExpression> { ParseExpression() };
                while (Current.Kind == Lua4TokenKind.Comma)
                {
                    Advance();
                    values.Add(ParseExpression());
                }

                return new LuaMultiAssignStmt(targets, values, identTok.Line);
            }

            if (Current.Kind is Lua4TokenKind.Dot or Lua4TokenKind.Colon)
            {
                LuaExpression receiver = new LuaIdentifierExpr(identTok.Text, identTok.Line);
                LuaExpression postfix = ParsePostfixContinuation(receiver);
                return CompletePostfixStatement(postfix, identTok.Line);
            }

            if (Current.Kind == Lua4TokenKind.LBracket)
            {
                Advance();
                LuaExpression idxExpr = ParseExpression();
                Expect(Lua4TokenKind.RBracket);
                if (Current.Kind == Lua4TokenKind.Assign)
                {
                    Advance();
                    LuaExpression valExpr = ParseExpression();
                    if (Current.Kind == Lua4TokenKind.Comma)
                    {
                        throw Abort($"unsupported-syntax:multi-assign({identTok.Text})@line{identTok.Line}");
                    }

                    return new LuaIndexAssignStmt(identTok.Text, idxExpr, valExpr, identTok.Line);
                }

                if (Current.Kind is Lua4TokenKind.Dot or Lua4TokenKind.Colon or Lua4TokenKind.LBracket)
                {
                    LuaExpression baseIdx = new LuaIndexExpr(new LuaIdentifierExpr(identTok.Text, identTok.Line), idxExpr, identTok.Line);
                    LuaExpression postfix = ParsePostfixContinuation(baseIdx);
                    return CompletePostfixStatement(postfix, identTok.Line);
                }

                throw Abort($"unsupported-syntax:table-mutation({identTok.Text})@line{identTok.Line}");
            }

            throw Abort($"syntax-error:unexpected-after-ident({identTok.Text}:{Current.Kind})@line{Current.Line}");
        }

        private LuaStatement ParseUpvalueStatement()
        {
            Lua4Token upTok = Expect(Lua4TokenKind.UpvalueIdentifier);
            if (Current.Kind == Lua4TokenKind.LParen)
            {
                List<LuaExpression> args = ParseParenthesizedArgList();
                var call = new LuaCallExpr("%" + upTok.Text, args, upTok.Line);
                if (Current.Kind is Lua4TokenKind.Dot or Lua4TokenKind.Colon or Lua4TokenKind.LBracket)
                {
                    LuaExpression chained = ParsePostfixContinuation(call);
                    return CompletePostfixStatement(chained, upTok.Line);
                }

                return new LuaCallStmt(call, upTok.Line);
            }

            LuaExpression receiver = new LuaUpvalueExpr(upTok.Text, upTok.Line);
            if (Current.Kind is Lua4TokenKind.Dot or Lua4TokenKind.Colon or Lua4TokenKind.LBracket)
            {
                LuaExpression postfix = ParsePostfixContinuation(receiver);
                return CompletePostfixStatement(postfix, upTok.Line);
            }

            throw Abort($"unsupported-syntax:upvalue-mutation(%{upTok.Text})@line{upTok.Line}");
        }

        private LuaStatement CompletePostfixStatement(LuaExpression postfix, int line)
        {
            if (postfix is LuaMemberCallExpr mcall && Current.Kind != Lua4TokenKind.Assign)
            {
                return new LuaMemberCallStmt(mcall, line);
            }

            if (postfix is LuaMemberAccessExpr macc && Current.Kind == Lua4TokenKind.Assign)
            {
                Advance();
                LuaExpression rhs = ParseExpression();
                if (Current.Kind == Lua4TokenKind.Comma)
                {
                    throw Abort($"unsupported-syntax:multi-assign({macc.FieldName})@line{line}");
                }

                return new LuaMemberAssignStmt(macc.Target, macc.FieldName, rhs, line);
            }

            if (postfix is LuaIndexExpr && Current.Kind == Lua4TokenKind.Assign)
            {
                throw Abort($"unsupported-syntax:nested-index-assign@line{line}");
            }

            throw Abort($"unsupported-syntax:postfix-stmt({postfix.GetType().Name})@line{line}");
        }

        private LuaCallExpr ParseCallArguments(Lua4Token calleeTok)
        {
            Expect(Lua4TokenKind.LParen);
            var args = new List<LuaExpression>();
            if (Current.Kind != Lua4TokenKind.RParen)
            {
                while (true)
                {
                    args.Add(ParseExpression());
                    if (Current.Kind == Lua4TokenKind.Comma)
                    {
                        Advance();
                        continue;
                    }

                    break;
                }
            }

            Expect(Lua4TokenKind.RParen);
            return new LuaCallExpr(calleeTok.Text, args, calleeTok.Line);
        }

        private LuaExpression ParseExpression() => ParseOr();

        private LuaExpression ParseOr()
        {
            LuaExpression left = ParseAnd();
            while (Current.Kind == Lua4TokenKind.Or)
            {
                int line = Advance().Line;
                LuaExpression right = ParseAnd();
                left = new LuaBinaryExpr(LuaBinaryOp.Or, left, right, line);
            }

            return left;
        }

        private LuaExpression ParseAnd()
        {
            LuaExpression left = ParseComparison();
            while (Current.Kind == Lua4TokenKind.And)
            {
                int line = Advance().Line;
                LuaExpression right = ParseComparison();
                left = new LuaBinaryExpr(LuaBinaryOp.And, left, right, line);
            }

            return left;
        }

        private LuaExpression ParseComparison()
        {
            LuaExpression left = ParseConcat();
            while (Current.Kind is Lua4TokenKind.Eq or Lua4TokenKind.Neq or Lua4TokenKind.Lt or Lua4TokenKind.Le or Lua4TokenKind.Gt or Lua4TokenKind.Ge)
            {
                Lua4Token opTok = Advance();
                LuaBinaryOp op = opTok.Kind switch
                {
                    Lua4TokenKind.Eq => LuaBinaryOp.Eq,
                    Lua4TokenKind.Neq => LuaBinaryOp.Neq,
                    Lua4TokenKind.Lt => LuaBinaryOp.Lt,
                    Lua4TokenKind.Le => LuaBinaryOp.Le,
                    Lua4TokenKind.Gt => LuaBinaryOp.Gt,
                    Lua4TokenKind.Ge => LuaBinaryOp.Ge,
                    _ => throw Abort($"syntax-error:unexpected-op({opTok.Kind})@line{opTok.Line}"),
                };
                LuaExpression right = ParseConcat();
                left = new LuaBinaryExpr(op, left, right, opTok.Line);
            }

            return left;
        }

        private LuaExpression ParseConcat()
        {
            LuaExpression left = ParseAdditive();
            if (Current.Kind == Lua4TokenKind.Concat)
            {
                int line = Advance().Line;
                LuaExpression right = ParseConcat();
                return new LuaBinaryExpr(LuaBinaryOp.Concat, left, right, line);
            }

            return left;
        }

        private LuaExpression ParseAdditive()
        {
            LuaExpression left = ParseMultiplicative();
            while (Current.Kind is Lua4TokenKind.Plus or Lua4TokenKind.Minus)
            {
                Lua4Token opTok = Advance();
                LuaBinaryOp op = opTok.Kind == Lua4TokenKind.Plus ? LuaBinaryOp.Add : LuaBinaryOp.Sub;
                LuaExpression right = ParseMultiplicative();
                left = new LuaBinaryExpr(op, left, right, opTok.Line);
            }

            return left;
        }

        private LuaExpression ParseMultiplicative()
        {
            LuaExpression left = ParseUnary();
            while (Current.Kind is Lua4TokenKind.Star or Lua4TokenKind.Slash or Lua4TokenKind.Percent)
            {
                Lua4Token opTok = Advance();
                LuaBinaryOp op = opTok.Kind switch
                {
                    Lua4TokenKind.Star => LuaBinaryOp.Mul,
                    Lua4TokenKind.Slash => LuaBinaryOp.Div,
                    Lua4TokenKind.Percent => LuaBinaryOp.Mod,
                    _ => throw Abort($"syntax-error:unexpected-op({opTok.Kind})@line{opTok.Line}"),
                };
                LuaExpression right = ParseUnary();
                left = new LuaBinaryExpr(op, left, right, opTok.Line);
            }

            return left;
        }

        private LuaExpression ParseUnary()
        {
            if (Current.Kind == Lua4TokenKind.Minus)
            {
                int line = Advance().Line;
                return new LuaUnaryExpr(LuaUnaryOp.Negate, ParseUnary(), line);
            }

            if (Current.Kind == Lua4TokenKind.Not)
            {
                int line = Advance().Line;
                return new LuaUnaryExpr(LuaUnaryOp.Not, ParseUnary(), line);
            }

            return ParsePower();
        }

        private LuaExpression ParsePower()
        {
            LuaExpression left = ParsePostfix();
            if (Current.Kind == Lua4TokenKind.Caret)
            {
                int line = Advance().Line;
                LuaExpression right = ParseUnary();
                return new LuaBinaryExpr(LuaBinaryOp.Pow, left, right, line);
            }

            return left;
        }

        private LuaExpression ParsePostfix()
        {
            LuaExpression expr = ParsePrimary();
            return ParsePostfixContinuation(expr);
        }

        private LuaExpression ParsePostfixContinuation(LuaExpression expr)
        {
            while (true)
            {
                if (Current.Kind == Lua4TokenKind.LBracket)
                {
                    int line = Advance().Line;
                    LuaExpression idx = ParseExpression();
                    Expect(Lua4TokenKind.RBracket);
                    expr = new LuaIndexExpr(expr, idx, line);
                    continue;
                }

                if (Current.Kind == Lua4TokenKind.Dot)
                {
                    int line = Advance().Line;
                    Lua4Token fieldTok = Expect(Lua4TokenKind.Identifier);
                    if (Current.Kind == Lua4TokenKind.LParen)
                    {
                        List<LuaExpression> args = ParseParenthesizedArgList();
                        expr = new LuaMemberCallExpr(expr, fieldTok.Text, IsColonCall: false, args, line);
                    }
                    else
                    {
                        expr = new LuaMemberAccessExpr(expr, fieldTok.Text, line);
                    }

                    continue;
                }

                if (Current.Kind == Lua4TokenKind.Colon)
                {
                    int line = Advance().Line;
                    Lua4Token methodTok = Expect(Lua4TokenKind.Identifier);
                    if (Current.Kind != Lua4TokenKind.LParen)
                    {
                        throw Abort($"syntax-error:expected-LParen-after-colon({methodTok.Text})@line{line}");
                    }

                    List<LuaExpression> args = ParseParenthesizedArgList();
                    expr = new LuaMemberCallExpr(expr, methodTok.Text, IsColonCall: true, args, line);
                    continue;
                }

                if (Current.Kind == Lua4TokenKind.LParen)
                {
                    if (expr is LuaUpvalueExpr upv)
                    {
                        List<LuaExpression> args = ParseParenthesizedArgList();
                        expr = new LuaCallExpr("%" + upv.Name, args, upv.Line);
                        continue;
                    }

                    throw Abort($"unsupported-syntax:indirect-call@line{Current.Line}");
                }

                break;
            }

            return expr;
        }

        private List<LuaExpression> ParseParenthesizedArgList()
        {
            Expect(Lua4TokenKind.LParen);
            var args = new List<LuaExpression>();
            if (Current.Kind != Lua4TokenKind.RParen)
            {
                while (true)
                {
                    args.Add(ParseExpression());
                    if (Current.Kind == Lua4TokenKind.Comma)
                    {
                        Advance();
                        continue;
                    }

                    break;
                }
            }

            Expect(Lua4TokenKind.RParen);
            return args;
        }

        private LuaExpression ParsePrimary()
        {
            Lua4Token tok = Current;
            switch (tok.Kind)
            {
                case Lua4TokenKind.Number:
                    Advance();
                    return new LuaNumberExpr(tok.NumberValue, tok.Text, tok.Line);
                case Lua4TokenKind.String:
                    Advance();
                    return new LuaStringExpr(DecodeLuaStringLiteral(tok.Text), tok.Text, tok.Line);
                case Lua4TokenKind.Nil:
                    Advance();
                    return new LuaNilExpr(tok.Line);
                case Lua4TokenKind.Identifier:
                    Advance();
                    if (Current.Kind == Lua4TokenKind.LParen)
                    {
                        return ParseCallArguments(tok);
                    }

                    return new LuaIdentifierExpr(tok.Text, tok.Line);
                case Lua4TokenKind.LParen:
                    Advance();
                    LuaExpression inner = ParseExpression();
                    Expect(Lua4TokenKind.RParen);
                    return inner;
                case Lua4TokenKind.LBrace:
                    return ParseTableLiteral();
                case Lua4TokenKind.UpvalueIdentifier:
                    Advance();
                    return new LuaUpvalueExpr(tok.Text, tok.Line);
                case Lua4TokenKind.Function:
                    return ParseAnonFunctionExpr();
                default:
                    throw Abort($"syntax-error:unexpected-expr-{tok.Kind}({tok.Text})@line{tok.Line}");
            }
        }

        private LuaAnonFunctionExpr ParseAnonFunctionExpr()
        {
            Lua4Token fnTok = Expect(Lua4TokenKind.Function);
            Expect(Lua4TokenKind.LParen);
            var parameters = new List<string>();
            if (Current.Kind != Lua4TokenKind.RParen)
            {
                while (true)
                {
                    if (Current.Kind == Lua4TokenKind.Dots)
                    {
                        throw Abort($"unsupported-syntax:varargs@line{Current.Line}");
                    }

                    if (Current.Kind != Lua4TokenKind.Identifier)
                    {
                        throw Abort($"syntax-error:expected-param-got-{Current.Kind}@line{Current.Line}");
                    }

                    parameters.Add(Advance().Text);
                    if (Current.Kind == Lua4TokenKind.Comma)
                    {
                        Advance();
                        continue;
                    }

                    break;
                }
            }

            Expect(Lua4TokenKind.RParen);
            List<LuaStatement> body = ParseBlockUntil(Lua4TokenKind.End);
            Expect(Lua4TokenKind.End);
            return new LuaAnonFunctionExpr(parameters, body, fnTok.Line);
        }

        private LuaExpression ParseTableLiteral()
        {
            Lua4Token braceTok = Expect(Lua4TokenKind.LBrace);
            var entries = new List<LuaTableFieldEntry>();
            bool hasKeyed = false;

            while (Current.Kind != Lua4TokenKind.RBrace && !IsAtEnd)
            {
                int entryLine = Current.Line;
                if (Current.Kind == Lua4TokenKind.LBracket)
                {
                    hasKeyed = true;
                    Advance();
                    LuaExpression keyExpr = ParseExpression();
                    Expect(Lua4TokenKind.RBracket);
                    Expect(Lua4TokenKind.Assign);
                    LuaExpression valExpr = ParseExpression();
                    entries.Add(new LuaTableFieldEntry(null, keyExpr, valExpr, entryLine));
                }
                else if (Current.Kind == Lua4TokenKind.Identifier && Peek(1).Kind == Lua4TokenKind.Assign)
                {
                    hasKeyed = true;
                    string fieldName = Advance().Text;
                    Advance(); // '='
                    LuaExpression valExpr = ParseExpression();
                    entries.Add(new LuaTableFieldEntry(fieldName, null, valExpr, entryLine));
                }
                else
                {
                    LuaExpression elemExpr = ParseExpression();
                    entries.Add(new LuaTableFieldEntry(null, null, elemExpr, entryLine));
                }

                if (Current.Kind is Lua4TokenKind.Comma or Lua4TokenKind.Semicolon)
                {
                    Advance();
                    continue;
                }

                break;
            }

            Expect(Lua4TokenKind.RBrace);
            if (!hasKeyed)
            {
                var seq = new List<LuaExpression>(entries.Count);
                for (int i = 0; i < entries.Count; i++)
                {
                    seq.Add(entries[i].Value);
                }

                return new LuaTableLiteralExpr(seq, braceTok.Line);
            }

            return new LuaKeyedTableLiteralExpr(entries, braceTok.Line);
        }

        private static bool IsIn(Lua4TokenKind kind, Lua4TokenKind[] set)
        {
            for (int i = 0; i < set.Length; i++)
            {
                if (set[i] == kind)
                {
                    return true;
                }
            }

            return false;
        }

        private static LuaAstParseFailureException Abort(string reason) => new(reason);
    }
}
