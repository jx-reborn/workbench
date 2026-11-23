using System.Collections.Generic;
using JXReborn.ScriptMigrator.Ast;

namespace JXReborn.ScriptMigrator.Generators;

public sealed record LuaLinkResult(
    LuaProgram? LinkedProgram,
    IReadOnlyList<string> IncludedVirtualKeys,
    IReadOnlyList<string> BlockingReasons)
{
    public bool IsSuccess => LinkedProgram is not null && BlockingReasons.Count == 0;
}
