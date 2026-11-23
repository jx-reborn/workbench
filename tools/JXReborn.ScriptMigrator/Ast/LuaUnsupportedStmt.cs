namespace JXReborn.ScriptMigrator.Ast;

public sealed record LuaUnsupportedStmt(
    string Reason,
    int Line) : LuaStatement(Line);
