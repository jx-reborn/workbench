namespace JXReborn.ScriptMigrator.Ast;

public sealed record LuaLocalDeclStmt(
    string Name,
    LuaExpression? Initializer,
    int Line) : LuaStatement(Line);
