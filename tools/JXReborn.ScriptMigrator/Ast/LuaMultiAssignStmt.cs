using System.Collections.Generic;

namespace JXReborn.ScriptMigrator.Ast;

public sealed record LuaMultiAssignStmt(
    IReadOnlyList<string> TargetNames,
    IReadOnlyList<LuaExpression> Values,
    int Line) : LuaStatement(Line);
