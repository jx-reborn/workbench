using System.Collections.Generic;

namespace JXReborn.ScriptMigrator.Ast;

public sealed record LuaDoBlockStmt(
    IReadOnlyList<LuaStatement> Body,
    int Line) : LuaStatement(Line);
