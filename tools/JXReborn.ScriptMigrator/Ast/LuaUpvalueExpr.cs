namespace JXReborn.ScriptMigrator.Ast;

public sealed record LuaUpvalueExpr(
    string Name,
    int Line) : LuaExpression(Line);
