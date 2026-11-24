namespace JXReborn.ScriptMigrator.Ast;

/// <summary>
/// Represents a statement-level method or member call (<c>Receiver:MemberName(args)</c> or <c>Receiver.MemberName(args)</c>).
/// </summary>
public sealed record LuaMemberCallStmt(
    LuaMemberCallExpr Call,
    int Line) : LuaStatement(Line);
