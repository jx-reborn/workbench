using System.Collections.Generic;

namespace JXReborn.ScriptMigrator.Ast;

public sealed record LuaIfStmt(
    LuaExpression Condition,
    IReadOnlyList<LuaStatement> ThenBody,
    IReadOnlyList<LuaElseIfBranch> ElseIfBranches,
    IReadOnlyList<LuaStatement> ElseBody,
    int Line) : LuaStatement(Line);
