using System.Collections.Generic;

namespace JXReborn.ScriptMigrator.Ast;

public sealed record LuaMultiLocalDeclStmt(
    IReadOnlyList<string> Names,
    IReadOnlyList<LuaExpression> Initializers,
    int Line) : LuaStatement(Line);
