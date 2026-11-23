namespace JXReborn.ScriptMigrator.Ast;

public sealed record LuaIndexExpr(
    LuaExpression Target,
    LuaExpression Index,
    int Line) : LuaExpression(Line);
