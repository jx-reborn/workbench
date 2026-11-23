using System.Collections.Generic;

namespace JXReborn.ScriptMigrator.Ast;

public sealed record LuaWhileStmt(
    LuaExpression Condition,
    IReadOnlyList<LuaStatement> Body,
    int Line) : LuaStatement(Line);
