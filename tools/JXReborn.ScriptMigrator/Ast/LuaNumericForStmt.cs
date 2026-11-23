using System.Collections.Generic;

namespace JXReborn.ScriptMigrator.Ast;

public sealed record LuaNumericForStmt(
    string IteratorName,
    LuaExpression Start,
    LuaExpression Limit,
    LuaExpression? Step,
    IReadOnlyList<LuaStatement> Body,
    int Line) : LuaStatement(Line);
