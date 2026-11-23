namespace JXReborn.ScriptMigrator.Ast;

public sealed record LuaBinaryExpr(
    LuaBinaryOp Operator,
    LuaExpression Left,
    LuaExpression Right,
    int Line) : LuaExpression(Line);
