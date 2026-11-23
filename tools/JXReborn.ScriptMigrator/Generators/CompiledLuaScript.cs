using System.Collections.Generic;
using JXReborn.ScriptMigrator.Ast;
using JXReborn.ScriptMigrator.Models;

namespace JXReborn.ScriptMigrator.Generators;

public sealed record CompiledLuaScript(
    ScriptInventoryEntry Entry,
    string Utf8ScriptPath,
    GeneratedScriptKind Kind,
    LuaProgram Program,
    string EntryFunctionName,
    bool EntryIsAsync,
    IReadOnlyDictionary<string, LuaValueType> TopLevelConstantTypes,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, LuaValueType>> FunctionLocalTypes,
    IReadOnlyDictionary<string, bool> FunctionIsAsync,
    IReadOnlyDictionary<string, IReadOnlyList<LuaValueType>> FunctionParamTypes,
    IReadOnlyDictionary<string, LuaValueType> FunctionReturnTypes);
