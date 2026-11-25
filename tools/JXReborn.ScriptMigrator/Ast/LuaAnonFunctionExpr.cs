using System.Collections.Generic;

namespace JXReborn.ScriptMigrator.Ast;

public sealed record LuaAnonFunctionExpr(
    IReadOnlyList<string> Parameters,
    IReadOnlyList<LuaStatement> Body,
    int Line) : LuaExpression(Line);
