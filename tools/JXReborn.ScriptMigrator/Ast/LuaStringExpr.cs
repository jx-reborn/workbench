namespace JXReborn.ScriptMigrator.Ast;

public sealed record LuaStringExpr(
    string DecodedUtf8,
    string RawLatin1,
    int Line) : LuaExpression(Line);
