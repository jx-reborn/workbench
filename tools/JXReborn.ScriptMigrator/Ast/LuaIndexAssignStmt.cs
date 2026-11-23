namespace JXReborn.ScriptMigrator.Ast;

public sealed record LuaIndexAssignStmt(
    string TargetName,
    LuaExpression Index,
    LuaExpression Value,
    int Line) : LuaStatement(Line);
