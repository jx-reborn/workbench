using System.Collections.Generic;

namespace JXReborn.ScriptMigrator.Ast;

/// <summary>
/// Represents a Lua 4.0 method or member invocation (<c>Receiver:MemberName(args)</c> or <c>Receiver.MemberName(args)</c>).
/// </summary>
public sealed record LuaMemberCallExpr(
    LuaExpression Receiver,
    string MemberName,
    bool IsColonCall,
    IReadOnlyList<LuaExpression> Arguments,
    int Line) : LuaExpression(Line);
