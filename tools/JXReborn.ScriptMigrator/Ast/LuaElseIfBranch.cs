using System.Collections.Generic;

namespace JXReborn.ScriptMigrator.Ast;

public sealed record LuaElseIfBranch(
    LuaExpression Condition,
    IReadOnlyList<LuaStatement> Body,
    int Line);
