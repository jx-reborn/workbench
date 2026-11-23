using System.Collections.Generic;

namespace JXReborn.ScriptMigrator.Ast;

public sealed record LuaCallExpr(
    string CalleeName,
    IReadOnlyList<LuaExpression> Arguments,
    int Line) : LuaExpression(Line);
