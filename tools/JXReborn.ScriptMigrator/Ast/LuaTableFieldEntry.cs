namespace JXReborn.ScriptMigrator.Ast;

/// <summary>
/// Represents a single entry inside a keyed or mixed Lua 4.0 table constructor:
/// <list type="bullet">
/// <item><description><c>FieldName != null</c>: <c>ident = Value</c></description></item>
/// <item><description><c>BracketKey != null</c>: <c>[BracketKey] = Value</c></description></item>
/// <item><description>Both <c>null</c>: 1-based positional element <c>Value</c></description></item>
/// </list>
/// </summary>
public sealed record LuaTableFieldEntry(
    string? FieldName,
    LuaExpression? BracketKey,
    LuaExpression Value,
    int Line);
