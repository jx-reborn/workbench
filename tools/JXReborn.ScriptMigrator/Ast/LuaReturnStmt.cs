namespace JXReborn.ScriptMigrator.Ast;

public sealed record LuaReturnStmt(
    LuaExpression? Value,
    int Line) : LuaStatement(Line);
