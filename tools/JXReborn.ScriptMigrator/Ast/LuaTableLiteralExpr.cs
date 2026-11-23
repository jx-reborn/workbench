using System.Collections.Generic;

namespace JXReborn.ScriptMigrator.Ast;

public sealed record LuaTableLiteralExpr(
    IReadOnlyList<LuaExpression> Elements,
    int Line) : LuaExpression(Line);
