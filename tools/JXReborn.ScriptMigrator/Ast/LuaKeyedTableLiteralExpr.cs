using System.Collections.Generic;

namespace JXReborn.ScriptMigrator.Ast;

/// <summary>
/// Represents a Lua 4.0 table literal containing named (<c>k = v</c>), bracketed (<c>[k] = v</c>),
/// or mixed positional + keyed entries.
/// </summary>
public sealed record LuaKeyedTableLiteralExpr(
    IReadOnlyList<LuaTableFieldEntry> Entries,
    int Line) : LuaExpression(Line);
