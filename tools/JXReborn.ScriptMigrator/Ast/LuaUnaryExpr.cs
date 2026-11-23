namespace JXReborn.ScriptMigrator.Ast;

public sealed record LuaUnaryExpr(
    LuaUnaryOp Operator,
    LuaExpression Operand,
    int Line) : LuaExpression(Line);
