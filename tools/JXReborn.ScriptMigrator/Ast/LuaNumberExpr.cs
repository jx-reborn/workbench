namespace JXReborn.ScriptMigrator.Ast;

public sealed record LuaNumberExpr(
    double Value,
    string RawText,
    int Line) : LuaExpression(Line);
