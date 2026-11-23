using System.Collections.Generic;

namespace JXReborn.ScriptMigrator.Ast;

public sealed record LuaReturnStmt(
    LuaExpression? Value,
    int Line,
    IReadOnlyList<LuaExpression>? ExtraValues = null) : LuaStatement(Line);
