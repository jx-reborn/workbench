namespace JXReborn.ScriptMigrator.Ast;

public sealed record LuaUnsupportedExpr(
    string Reason,
    int Line) : LuaExpression(Line);
