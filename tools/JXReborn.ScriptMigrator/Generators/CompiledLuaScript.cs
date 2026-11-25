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
    IReadOnlyDictionary<string, LuaValueType> FunctionReturnTypes,
    IReadOnlyDictionary<string, IReadOnlyList<LuaValueType>>? FunctionReturnTuples = null,
    IReadOnlyDictionary<string, IReadOnlyList<LuaValueType>>? ConstTupleColumnTypes = null,
    IReadOnlyDictionary<string, CompiledAwardNode>? TopLevelAwardNodes = null,
    IReadOnlyDictionary<string, IReadOnlyDictionary<int, CompiledAwardNode>>? TopLevelAwardMaps = null,
    IReadOnlyDictionary<string, IReadOnlyDictionary<int, IReadOnlyDictionary<string, int>>>? TopLevelIntRecordMaps = null,
    IReadOnlyList<string>? IncludedVirtualKeys = null,
    IReadOnlyDictionary<string, IReadOnlyList<(string VarName, ushort TaskId)>>? FunctionPreloadedTaskGlobals = null,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? FunctionLocalTupleTables = null);
