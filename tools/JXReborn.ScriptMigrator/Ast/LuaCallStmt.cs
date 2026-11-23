namespace JXReborn.ScriptMigrator.Ast;

public sealed record LuaCallStmt(
    LuaCallExpr Call,
    int Line) : LuaStatement(Line);
