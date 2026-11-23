using System.Collections.Generic;

namespace JXReborn.ScriptMigrator.Ast;

public sealed record LuaProgram(
    IReadOnlyList<LuaStatement> TopLevelStatements,
    IReadOnlyList<LuaFunctionDecl> Functions);
