namespace JXReborn.ScriptMigrator.Ast;

public sealed record LuaIdentifierExpr(
    string Name,
    int Line) : LuaExpression(Line);
