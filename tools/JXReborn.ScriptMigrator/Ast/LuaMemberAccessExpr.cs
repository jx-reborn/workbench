namespace JXReborn.ScriptMigrator.Ast;

/// <summary>
/// Represents field access <c>Target.FieldName</c>.
/// </summary>
public sealed record LuaMemberAccessExpr(
    LuaExpression Target,
    string FieldName,
    int Line) : LuaExpression(Line);
