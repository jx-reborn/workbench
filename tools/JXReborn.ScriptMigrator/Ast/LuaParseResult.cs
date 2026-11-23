namespace JXReborn.ScriptMigrator.Ast;

public sealed record LuaParseResult(
    LuaProgram? Program,
    string? FailureReason)
{
    public bool IsSuccess => Program is not null && string.IsNullOrEmpty(FailureReason);
}
