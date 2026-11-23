using System.Collections.Generic;

namespace JXReborn.ScriptMigrator.Ast;

public sealed record LuaFunctionDecl(
    string Name,
    IReadOnlyList<string> Parameters,
    IReadOnlyList<LuaStatement> Body,
    int Line);
