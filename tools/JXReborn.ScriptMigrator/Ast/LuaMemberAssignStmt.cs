namespace JXReborn.ScriptMigrator.Ast;

/// <summary>
/// Represents field assignment <c>Target.FieldName = Value</c>.
/// </summary>
public sealed record LuaMemberAssignStmt(
    LuaExpression Target,
    string FieldName,
    LuaExpression Value,
    int Line) : LuaStatement(Line);
