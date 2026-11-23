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

    public static string DecodeLuaStringLiteral(string rawTokenText)
    {
        if (string.IsNullOrEmpty(rawTokenText))
        {
            return string.Empty;
        }

        byte[] raw = HybridLegacyTextDecoder.UnescapeLuaStringBytes(Encoding.Latin1.GetBytes(rawTokenText));
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
                case Lua4TokenKind.While:
                case Lua4TokenKind.Repeat:
                case Lua4TokenKind.Break:
                    throw Abort($"unsupported-syntax:loop-or-break({tok.Text})@line{tok.Line}");
                case Lua4TokenKind.Do:
                    throw Abort($"unsupported-syntax:do-block@line{tok.Line}");
                case Lua4TokenKind.Function:
                    throw Abort($"unsupported-syntax:nested-function@line{tok.Line}");
                case Lua4TokenKind.Identifier:
                    return ParseIdentifierStatement();
                default:
                    throw Abort($"syntax-error:unexpected-{tok.Kind}({tok.Text})@line{tok.Line}");
            }
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

        private LuaLocalDeclStmt ParseLocalDecl()
        {
            Lua4Token localTok = Expect(Lua4TokenKind.Local);
            if (Current.Kind == Lua4TokenKind.Function)
            {
                throw Abort($"unsupported-syntax:local-function@line{localTok.Line}");
            }

            Lua4Token nameTok = Expect(Lua4TokenKind.Identifier);
            if (Current.Kind == Lua4TokenKind.Comma)
            {
                throw Abort($"unsupported-syntax:multi-local-decl@line{localTok.Line}");
            }

            LuaExpression? init = null;
            if (Current.Kind == Lua4TokenKind.Assign)
            {
                Advance();
                init = ParseExpression();
                if (Current.Kind == Lua4TokenKind.Comma)
                {
                    throw Abort($"unsupported-syntax:multi-local-init@line{localTok.Line}");
                }
            }

            return new LuaLocalDeclStmt(nameTok.Text, init, localTok.Line);
        }

        private LuaReturnStmt ParseReturnStatement()
        {
            Lua4Token retTok = Expect(Lua4TokenKind.Return);
            if (Current.Kind is Lua4TokenKind.End or Lua4TokenKind.Else or Lua4TokenKind.ElseIf or Lua4TokenKind.Semicolon or Lua4TokenKind.Eof)
            {
                return new LuaReturnStmt(null, retTok.Line);
            }

            LuaExpression val = ParseExpression();
            if (Current.Kind == Lua4TokenKind.Comma)
            {
                throw Abort($"unsupported-syntax:multi-return@line{retTok.Line}");
            }

            return new LuaReturnStmt(val, retTok.Line);
        }

        private LuaStatement ParseIdentifierStatement()
        {
            Lua4Token identTok = Expect(Lua4TokenKind.Identifier);

            if (Current.Kind == Lua4TokenKind.Assign)
            {
                Advance();
                LuaExpression rhs = ParseExpression();
                if (Current.Kind == Lua4TokenKind.Comma)
                {
                    throw Abort($"unsupported-syntax:multi-assign@line{identTok.Line}");
                }

                return new LuaAssignStmt(identTok.Text, rhs, identTok.Line);
            }

            if (Current.Kind == Lua4TokenKind.LParen)
            {
                LuaCallExpr call = ParseCallArguments(identTok);
                if (Current.Kind is Lua4TokenKind.Dot or Lua4TokenKind.Colon or Lua4TokenKind.LBracket or Lua4TokenKind.LParen)
                {
                    throw Abort($"unsupported-syntax:chained-call({identTok.Text})@line{identTok.Line}");
                }

                return new LuaCallStmt(call, identTok.Line);
            }

            if (Current.Kind == Lua4TokenKind.Comma)
            {
                throw Abort($"unsupported-syntax:multi-assign({identTok.Text})@line{identTok.Line}");
            }

            if (Current.Kind is Lua4TokenKind.Dot or Lua4TokenKind.Colon)
            {
                throw Abort($"unsupported-syntax:member-access({identTok.Text})@line{identTok.Line}");
            }

            if (Current.Kind == Lua4TokenKind.LBracket)
            {
                throw Abort($"unsupported-syntax:table-mutation({identTok.Text})@line{identTok.Line}");
            }

            throw Abort($"syntax-error:unexpected-after-ident({identTok.Text}:{Current.Kind})@line{Current.Line}");
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

                if (Current.Kind is Lua4TokenKind.Dot or Lua4TokenKind.Colon)
                {
                    string baseName = expr is LuaIdentifierExpr id ? id.Name : "expr";
                    throw Abort($"unsupported-syntax:member-access({baseName})@line{Current.Line}");
                }

                if (Current.Kind == Lua4TokenKind.LParen)
                {
                    throw Abort($"unsupported-syntax:indirect-call@line{Current.Line}");
                }

                break;
            }

            return expr;
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
                    return ParseSequentialTableLiteral();
                case Lua4TokenKind.UpvalueIdentifier:
                    throw Abort($"unsupported-syntax:upvalue(%{tok.Text})@line{tok.Line}");
                case Lua4TokenKind.Function:
                    throw Abort($"unsupported-syntax:anonymous-function@line{tok.Line}");
                default:
                    throw Abort($"syntax-error:unexpected-expr-{tok.Kind}({tok.Text})@line{tok.Line}");
            }
        }

        private LuaTableLiteralExpr ParseSequentialTableLiteral()
        {
            Lua4Token braceTok = Expect(Lua4TokenKind.LBrace);
            var elements = new List<LuaExpression>();

            while (Current.Kind != Lua4TokenKind.RBrace && !IsAtEnd)
            {
                // Fail closed on keyed table entries: `key = val` or `[key] = val`
                if (Current.Kind == Lua4TokenKind.LBracket ||
                    (Current.Kind == Lua4TokenKind.Identifier && Peek(1).Kind == Lua4TokenKind.Assign))
                {
                    throw Abort($"unsupported-syntax:keyed-table@line{Current.Line}");
                }

                elements.Add(ParseExpression());
                if (Current.Kind is Lua4TokenKind.Comma or Lua4TokenKind.Semicolon)
                {
                    Advance();
                    continue;
                }

                break;
            }

            Expect(Lua4TokenKind.RBrace);
            return new LuaTableLiteralExpr(elements, braceTok.Line);
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
