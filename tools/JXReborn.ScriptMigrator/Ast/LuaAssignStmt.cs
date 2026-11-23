namespace JXReborn.ScriptMigrator.Ast;

public sealed record LuaAssignStmt(
    string TargetName,
    LuaExpression Value,
    int Line) : LuaStatement(Line);
