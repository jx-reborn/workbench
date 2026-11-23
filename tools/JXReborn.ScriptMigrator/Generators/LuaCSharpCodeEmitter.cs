using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using JXReborn.ScriptMigrator.Ast;
using JXReborn.ScriptMigrator.Models;

namespace JXReborn.ScriptMigrator.Generators;

/// <summary>
/// Emits clean, warning-free, <c>1 type / file</c>, <c>sealed</c>/<c>static</c> C# 14 source files
/// with exact <c>[LegacySource]</c> SHA-256 provenance into <c>Scripts/Generated/</c>.
/// </summary>
public sealed class LuaCSharpCodeEmitter
{
    private readonly IReadOnlyDictionary<int, string> _eventItemCodeByDetailId;

    public LuaCSharpCodeEmitter(IReadOnlyDictionary<int, string> eventItemCodeByDetailId)
    {
        _eventItemCodeByDetailId = eventItemCodeByDetailId ?? throw new ArgumentNullException(nameof(eventItemCodeByDetailId));
    }

    public IReadOnlyList<string> EmitAll(
        string generatedDir,
        IReadOnlyList<CompiledLuaScript> compiledScripts)
    {
        Directory.CreateDirectory(generatedDir);
        var writtenFiles = new List<string>();

        // Always emit GeneratedLuaRuntime.cs when >= 1 script is generated
        if (compiledScripts.Count == 0)
        {
            return writtenFiles;
        }

        string runtimePath = Path.Combine(generatedDir, "GeneratedLuaRuntime.cs");
        WriteUtf8(runtimePath, BuildGeneratedLuaRuntimeSource());
        writtenFiles.Add(runtimePath);

        // Partition scripts into compact parametric archetypes vs general AST classes
        var portalTraps = new List<(CompiledLuaScript Script, (ushort TargetMapId, int MpsX, int MpsY, sbyte FightMode, int WayPointId, int ProtectFrames, int SkillId, int SkillLevel, int SkillStateType, int SkillDuration) Data)>();
        var fightToggleTraps = new List<(CompiledLuaScript Script, (int OutMpsX, int OutMpsY, byte OutFightMode, int InMpsX, int InMpsY, byte InFightMode, ushort StationId, int ProtectFrames, int SkillId, int SkillLevel, int SkillStateType, int SkillDuration) Data)>();
        var staticDialogTraps = new List<(CompiledLuaScript Script, (byte Mode, string[] Lines) Data)>();
        var noOpTraps = new List<CompiledLuaScript>();
        var generalTraps = new List<CompiledLuaScript>();

        var signboardObjs = new List<(CompiledLuaScript Script, (byte Mode, string[] Lines) Data)>();
        var storageBoxObjs = new List<(CompiledLuaScript Script, (int ReviveId, ushort ReviveMapId, string RequiredFaction) Data)>();
        var ladderBoardObjs = new List<(CompiledLuaScript Script, int[] LadderIds)>();
        var returnPortalObjs = new List<CompiledLuaScript>();
        var generalObjs = new List<CompiledLuaScript>();

        var generalItems = new List<CompiledLuaScript>();
        var generalMonsters = new List<CompiledLuaScript>();
        var generalNpcs = new List<CompiledLuaScript>();

        foreach (CompiledLuaScript s in compiledScripts.OrderBy(static x => x.Utf8ScriptPath, StringComparer.OrdinalIgnoreCase))
        {
            switch (s.Kind)
            {
                case GeneratedScriptKind.Trap:
                    if (LuaParametricPatternMatcher.TryMatchPortalTrap(s, out var pData))
                    {
                        portalTraps.Add((s, pData));
                    }
                    else if (LuaParametricPatternMatcher.TryMatchFightToggleTrap(s, out var fData))
                    {
                        fightToggleTraps.Add((s, fData));
                    }
                    else if (LuaParametricPatternMatcher.TryMatchStaticDialog(s, GeneratedScriptKind.Trap, out var sdData))
                    {
                        staticDialogTraps.Add((s, sdData));
                    }
                    else if (LuaParametricPatternMatcher.TryMatchNoOpTrap(s))
                    {
                        noOpTraps.Add(s);
                    }
                    else
                    {
                        generalTraps.Add(s);
                    }

                    break;

                case GeneratedScriptKind.Object:
                    if (LuaParametricPatternMatcher.TryMatchStaticDialog(s, GeneratedScriptKind.Object, out var sbData))
                    {
                        signboardObjs.Add((s, sbData));
                    }
                    else if (LuaParametricPatternMatcher.TryMatchStorageBoxObject(s, out var boxData))
                    {
                        storageBoxObjs.Add((s, boxData));
                    }
                    else if (LuaParametricPatternMatcher.TryMatchLadderBoardObject(s, out var ladData))
                    {
                        ladderBoardObjs.Add((s, ladData));
                    }
                    else if (LuaParametricPatternMatcher.TryMatchReturnPortalObject(s))
                    {
                        returnPortalObjs.Add(s);
                    }
                    else
                    {
                        generalObjs.Add(s);
                    }

                    break;

                case GeneratedScriptKind.Item:
                    generalItems.Add(s);
                    break;

                case GeneratedScriptKind.Monster:
                    generalMonsters.Add(s);
                    break;

                case GeneratedScriptKind.Npc:
                    generalNpcs.Add(s);
                    break;
            }
        }

        EmitIfNonEmpty(generatedDir, "GeneratedPortalTrapScript.cs", portalTraps.Count > 0, () => BuildPortalTrapClass(portalTraps), writtenFiles);
        EmitIfNonEmpty(generatedDir, "GeneratedFightToggleTrapScript.cs", fightToggleTraps.Count > 0, () => BuildFightToggleTrapClass(fightToggleTraps), writtenFiles);
        EmitIfNonEmpty(generatedDir, "GeneratedStaticDialogTrapScript.cs", staticDialogTraps.Count > 0, () => BuildStaticDialogTrapClass(staticDialogTraps), writtenFiles);
        EmitIfNonEmpty(generatedDir, "GeneratedNoOpTrapScript.cs", noOpTraps.Count > 0, () => BuildNoOpTrapClass(noOpTraps), writtenFiles);
        EmitIfNonEmpty(generatedDir, "GeneratedGeneralTrapScript.cs", generalTraps.Count > 0, () => BuildGeneralAsyncScriptClass("GeneratedGeneralTrapScript", "ITrapScript", "IScriptTrapContext", "OnStepTrapAsync", "trap-generated-ast", "context.TrapScriptPath", generalTraps), writtenFiles);

        EmitIfNonEmpty(generatedDir, "GeneratedSignboardObjectScript.cs", signboardObjs.Count > 0, () => BuildSignboardObjectClass(signboardObjs), writtenFiles);
        EmitIfNonEmpty(generatedDir, "GeneratedStorageBoxObjectScript.cs", storageBoxObjs.Count > 0, () => BuildStorageBoxObjectClass(storageBoxObjs), writtenFiles);
        EmitIfNonEmpty(generatedDir, "GeneratedLadderBoardObjectScript.cs", ladderBoardObjs.Count > 0, () => BuildLadderBoardObjectClass(ladderBoardObjs), writtenFiles);
        EmitIfNonEmpty(generatedDir, "GeneratedPortalObjectScript.cs", returnPortalObjs.Count > 0, () => BuildReturnPortalObjectClass(returnPortalObjs), writtenFiles);
        EmitIfNonEmpty(generatedDir, "GeneratedGeneralObjectScript.cs", generalObjs.Count > 0, () => BuildGeneralAsyncScriptClass("GeneratedGeneralObjectScript", "IObjInteractionScript", "IScriptObjectContext", "OnInteractObjectAsync", "obj-generated-ast", "context.ObjectScriptPath", generalObjs), writtenFiles);

        EmitIfNonEmpty(generatedDir, "GeneratedGeneralItemScript.cs", generalItems.Count > 0, () => BuildGeneralItemScriptClass(generalItems), writtenFiles);
        EmitIfNonEmpty(generatedDir, "GeneratedMonsterDeathScript.cs", generalMonsters.Count > 0, () => BuildGeneralMonsterScriptClass(generalMonsters), writtenFiles);
        EmitIfNonEmpty(generatedDir, "GeneratedNpcDialogScript.cs", generalNpcs.Count > 0, () => BuildGeneralAsyncScriptClass("GeneratedNpcDialogScript", "INpcDialogScript", "IScriptDialogContext", "OnDialogAsync", "npc-generated-dialog-ast", "context.SourceNpcScriptPath", generalNpcs), writtenFiles);

        return writtenFiles;
    }

    private static void EmitIfNonEmpty(
        string generatedDir,
        string fileName,
        bool hasEntries,
        Func<string> buildSource,
        List<string> writtenFiles)
    {
        string path = Path.Combine(generatedDir, fileName);
        if (!hasEntries)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        WriteUtf8(path, buildSource());
        writtenFiles.Add(path);
    }

    private static string BuildGeneratedLuaRuntimeSource()
    {
        return """
// <auto-generated>
// Generated by JXReborn.ScriptMigrator (fail-closed Lua 4.0 AST transpiler).
// Do not edit manually; regenerate via `JXReborn.ScriptMigrator generate-scripts`.
// </auto-generated>
#nullable enable

using System;
using System.Globalization;
using System.Threading;
using JXReborn.Packs.TieuDaoTu.Hosting;

namespace JXReborn.Packs.TieuDaoTu.Scripts.Generated;

public static class GeneratedLuaRuntime
{
    private static readonly AsyncLocal<Func<int, int, int>?> RandomOverride = new();

    public static IDisposable UseDeterministicRandom(Func<int, int, int> nextInclusive)
    {
        ArgumentNullException.ThrowIfNull(nextInclusive);
        Func<int, int, int>? previous = RandomOverride.Value;
        RandomOverride.Value = nextInclusive;
        return new RandomScope(previous);
    }

    private sealed class RandomScope(Func<int, int, int>? previous) : IDisposable
    {
        public void Dispose() => RandomOverride.Value = previous;
    }
    public static string NormalizeScriptPathLower(string? rawPath)
    {
        string utf8 = LegacyScriptPathNormalizer.NormalizeToUtf8(rawPath);
        return utf8.Length == 0 ? string.Empty : utf8.ToLowerInvariant();
    }

    public static int GetByte(int value, int byteIndex)
    {
        uint idx = unchecked((uint)(byteIndex - 1));
        if (idx >= 4u)
        {
            return 0;
        }

        int shift = (int)(idx * 8u);
        return (int)((unchecked((uint)value) >> shift) & 0xFFu);
    }

    public static int SetByte(int value, int byteIndex, int byteValue)
    {
        uint idx = unchecked((uint)(byteIndex - 1));
        if (idx >= 4u)
        {
            return value;
        }

        int shift = (int)(idx * 8u);
        uint mask = ~(0xFFu << shift);
        uint updated = (unchecked((uint)value) & mask) | ((unchecked((uint)byteValue) & 0xFFu) << shift);
        return unchecked((int)updated);
    }

    public static int GetBit(int value, int bitIndex)
    {
        uint idx = unchecked((uint)(bitIndex - 1));
        if (idx >= 32u)
        {
            return 0;
        }

        return (value >> (int)idx) & 1;
    }

    public static int SetBit(int value, int bitIndex, int bitValue)
    {
        uint idx = unchecked((uint)(bitIndex - 1));
        if (idx >= 32u)
        {
            return value;
        }

        uint u = unchecked((uint)value);
        uint mask = 1u << (int)idx;
        u = bitValue == 1 ? (u | mask) : (u & ~mask);
        return unchecked((int)u);
    }

    public static string GetFactionName(byte factionId) =>
        factionId switch
        {
            0 => "shaolin",
            1 => "tianwang",
            2 => "tangmen",
            3 => "wudu",
            4 => "emei",
            5 => "cuiyan",
            6 => "gaibang",
            7 => "tianren",
            8 => "wudang",
            9 => "kunlun",
            10 => "huashan",
            _ => string.Empty,
        };

    public static int LuaRandom(int maxInclusive)
    {
        if (maxInclusive <= 1)
        {
            return 1;
        }

        if (RandomOverride.Value is { } custom)
        {
            return custom(1, maxInclusive);
        }

        return Random.Shared.Next(1, maxInclusive + 1);
    }

    public static int LuaRandom(int minInclusive, int maxInclusive)
    {
        if (maxInclusive <= minInclusive)
        {
            return minInclusive;
        }

        if (RandomOverride.Value is { } custom)
        {
            return custom(minInclusive, maxInclusive);
        }

        return Random.Shared.Next(minInclusive, maxInclusive + 1);
    }

    public static string GetAt1Based(string[] items, int luaIndex)
    {
        int idx = luaIndex - 1;
        return (uint)idx < (uint)items.Length ? items[idx] : string.Empty;
    }

    public static int GetAt1Based(int[] items, int luaIndex)
    {
        int idx = luaIndex - 1;
        return (uint)idx < (uint)items.Length ? items[idx] : 0;
    }

    public static string FormatInt(int value) => value.ToString(CultureInfo.InvariantCulture);
}
""";
    }

    private static string BuildPortalTrapClass(
        List<(CompiledLuaScript Script, (ushort TargetMapId, int MpsX, int MpsY, sbyte FightMode, int WayPointId, int ProtectFrames, int SkillId, int SkillLevel, int SkillStateType, int SkillDuration) Data)> items)
    {
        var sb = new StringBuilder(items.Count * 220);
        AppendHeader(sb, "System", "System.Collections.Frozen", "System.Collections.Generic", "System.Threading", "System.Threading.Tasks", "JXReborn.ZoneEngine.Scripting");
        AppendLegacySources(sb, items.Select(static x => x.Script));
        sb.AppendLine("public sealed class GeneratedPortalTrapScript : ITrapScript");
        sb.AppendLine("{");
        sb.AppendLine("    private static readonly FrozenDictionary<string, (ushort TargetMapId, int MpsX, int MpsY, sbyte FightMode, int WayPointId, int ProtectFrames, int SkillId, int SkillLevel, int SkillStateType, int SkillDuration)> Portals =");
        sb.AppendLine("        new Dictionary<string, (ushort, int, int, sbyte, int, int, int, int, int, int)>(StringComparer.OrdinalIgnoreCase)");
        sb.AppendLine("        {");
        foreach (var (s, d) in items)
        {
            sb.Append("            [")
              .Append(ToVerbatimLiteral(s.Utf8ScriptPath.ToLowerInvariant()))
              .Append("] = (")
              .Append(d.TargetMapId).Append(", ")
              .Append(d.MpsX).Append(", ")
              .Append(d.MpsY).Append(", ")
              .Append(d.FightMode).Append(", ")
              .Append(d.WayPointId).Append(", ")
              .Append(d.ProtectFrames).Append(", ")
              .Append(d.SkillId).Append(", ")
              .Append(d.SkillLevel).Append(", ")
              .Append(d.SkillStateType).Append(", ")
              .Append(d.SkillDuration)
              .AppendLine("),");
        }

        sb.AppendLine("        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);");
        sb.AppendLine();
        sb.AppendLine("    public string ScriptCode => \"trap-generated-portal\";");
        sb.AppendLine();
        sb.AppendLine("    public async ValueTask OnStepTrapAsync(IScriptTrapContext context, CancellationToken cancellationToken = default)");
        sb.AppendLine("    {");
        sb.AppendLine("        ArgumentNullException.ThrowIfNull(context);");
        sb.AppendLine("        string key = GeneratedLuaRuntime.NormalizeScriptPathLower(context.TrapScriptPath);");
        sb.AppendLine("        if (!Portals.TryGetValue(key, out var p))");
        sb.AppendLine("        {");
        sb.AppendLine("            return;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        byte fightMode = p.FightMode >= 0 ? (byte)p.FightMode : context.CurrentFightMode;");
        sb.AppendLine("        if (p.FightMode >= 0)");
        sb.AppendLine("        {");
        sb.AppendLine("            context.SetFightMode(fightMode);");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        _ = await context.TeleportPlayerAsync(p.TargetMapId, p.MpsX, p.MpsY, fightMode, cancellationToken);");
        sb.AppendLine("        if (p.WayPointId > 0)");
        sb.AppendLine("        {");
        sb.AppendLine("            context.RecordWayPoint(p.WayPointId);");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        if (p.ProtectFrames > 0)");
        sb.AppendLine("        {");
        sb.AppendLine("            context.SetProtectTime(p.ProtectFrames);");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        if (p.SkillId > 0)");
        sb.AppendLine("        {");
        sb.AppendLine("            context.AddSkillState(p.SkillId, p.SkillLevel, p.SkillStateType, p.SkillDuration);");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildFightToggleTrapClass(
        List<(CompiledLuaScript Script, (int OutMpsX, int OutMpsY, byte OutFightMode, int InMpsX, int InMpsY, byte InFightMode, ushort StationId, int ProtectFrames, int SkillId, int SkillLevel, int SkillStateType, int SkillDuration) Data)> items)
    {
        var sb = new StringBuilder(items.Count * 240);
        AppendHeader(sb, "System", "System.Collections.Frozen", "System.Collections.Generic", "System.Threading", "System.Threading.Tasks", "JXReborn.ZoneEngine.Scripting");
        AppendLegacySources(sb, items.Select(static x => x.Script));
        sb.AppendLine("public sealed class GeneratedFightToggleTrapScript : ITrapScript");
        sb.AppendLine("{");
        sb.AppendLine("    private static readonly FrozenDictionary<string, (int OutMpsX, int OutMpsY, byte OutFightMode, int InMpsX, int InMpsY, byte InFightMode, ushort StationId, int ProtectFrames, int SkillId, int SkillLevel, int SkillStateType, int SkillDuration)> Gates =");
        sb.AppendLine("        new Dictionary<string, (int, int, byte, int, int, byte, ushort, int, int, int, int, int)>(StringComparer.OrdinalIgnoreCase)");
        sb.AppendLine("        {");
        foreach (var (s, d) in items)
        {
            sb.Append("            [")
              .Append(ToVerbatimLiteral(s.Utf8ScriptPath.ToLowerInvariant()))
              .Append("] = (")
              .Append(d.OutMpsX).Append(", ")
              .Append(d.OutMpsY).Append(", ")
              .Append(d.OutFightMode).Append(", ")
              .Append(d.InMpsX).Append(", ")
              .Append(d.InMpsY).Append(", ")
              .Append(d.InFightMode).Append(", ")
              .Append(d.StationId).Append(", ")
              .Append(d.ProtectFrames).Append(", ")
              .Append(d.SkillId).Append(", ")
              .Append(d.SkillLevel).Append(", ")
              .Append(d.SkillStateType).Append(", ")
              .Append(d.SkillDuration)
              .AppendLine("),");
        }

        sb.AppendLine("        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);");
        sb.AppendLine();
        sb.AppendLine("    public string ScriptCode => \"trap-generated-fight-toggle\";");
        sb.AppendLine();
        sb.AppendLine("    public async ValueTask OnStepTrapAsync(IScriptTrapContext context, CancellationToken cancellationToken = default)");
        sb.AppendLine("    {");
        sb.AppendLine("        ArgumentNullException.ThrowIfNull(context);");
        sb.AppendLine("        string key = GeneratedLuaRuntime.NormalizeScriptPathLower(context.TrapScriptPath);");
        sb.AppendLine("        if (!Gates.TryGetValue(key, out var g))");
        sb.AppendLine("        {");
        sb.AppendLine("            return;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        bool leavingSafeZone = context.CurrentFightMode == 0;");
        sb.AppendLine("        int destX = leavingSafeZone ? g.OutMpsX : g.InMpsX;");
        sb.AppendLine("        int destY = leavingSafeZone ? g.OutMpsY : g.InMpsY;");
        sb.AppendLine("        byte nextFightMode = leavingSafeZone ? g.OutFightMode : g.InFightMode;");
        sb.AppendLine("        context.SetFightMode(nextFightMode);");
        sb.AppendLine("        _ = await context.TeleportPlayerAsync(context.MapId, destX, destY, nextFightMode, cancellationToken);");
        sb.AppendLine();
        sb.AppendLine("        if (g.StationId > 0)");
        sb.AppendLine("        {");
        sb.AppendLine("            context.UnlockStation(g.StationId);");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        if (g.ProtectFrames > 0)");
        sb.AppendLine("        {");
        sb.AppendLine("            context.SetProtectTime(g.ProtectFrames);");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        if (g.SkillId > 0)");
        sb.AppendLine("        {");
        sb.AppendLine("            context.AddSkillState(g.SkillId, g.SkillLevel, g.SkillStateType, g.SkillDuration);");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildStaticDialogTrapClass(List<(CompiledLuaScript Script, (byte Mode, string[] Lines) Data)> items)
    {
        var sb = new StringBuilder(items.Count * 240);
        AppendHeader(sb, "System", "System.Collections.Frozen", "System.Collections.Generic", "System.Threading", "System.Threading.Tasks", "JXReborn.ZoneEngine.Scripting");
        AppendLegacySources(sb, items.Select(static x => x.Script));
        sb.AppendLine("public sealed class GeneratedStaticDialogTrapScript : ITrapScript");
        sb.AppendLine("{");
        sb.AppendLine("    private static readonly FrozenDictionary<string, (byte Mode, string[] Lines)> Entries =");
        sb.AppendLine("        new Dictionary<string, (byte, string[])>(StringComparer.OrdinalIgnoreCase)");
        sb.AppendLine("        {");
        foreach (var (s, d) in items)
        {
            sb.Append("            [")
              .Append(ToVerbatimLiteral(s.Utf8ScriptPath.ToLowerInvariant()))
              .Append("] = (")
              .Append(d.Mode)
              .Append(", [")
              .Append(string.Join(", ", d.Lines.Select(ToEscapedStringLiteral)))
              .AppendLine("]),");
        }

        sb.AppendLine("        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);");
        sb.AppendLine();
        sb.AppendLine("    public string ScriptCode => \"trap-generated-dialog\";");
        sb.AppendLine();
        sb.AppendLine("    public ValueTask OnStepTrapAsync(IScriptTrapContext context, CancellationToken cancellationToken = default)");
        sb.AppendLine("    {");
        sb.AppendLine("        ArgumentNullException.ThrowIfNull(context);");
        sb.AppendLine("        string key = GeneratedLuaRuntime.NormalizeScriptPathLower(context.TrapScriptPath);");
        sb.AppendLine("        if (Entries.TryGetValue(key, out var e) && e.Lines.Length > 0)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (e.Mode == 1) context.Talk(e.Lines);");
        sb.AppendLine("            else if (e.Mode == 2) context.SendSystemMessage(e.Lines[0]);");
        sb.AppendLine("            else context.Say(e.Lines[0]);");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        return ValueTask.CompletedTask;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildNoOpTrapClass(List<CompiledLuaScript> items)
    {
        var sb = new StringBuilder(items.Count * 140);
        AppendHeader(sb, "System.Threading", "System.Threading.Tasks", "JXReborn.ZoneEngine.Scripting");
        AppendLegacySources(sb, items);
        sb.AppendLine("public sealed class GeneratedNoOpTrapScript : ITrapScript");
        sb.AppendLine("{");
        sb.AppendLine("    public string ScriptCode => \"trap-generated-noop\";");
        sb.AppendLine();
        sb.AppendLine("    public ValueTask OnStepTrapAsync(IScriptTrapContext context, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildSignboardObjectClass(List<(CompiledLuaScript Script, (byte Mode, string[] Lines) Data)> items)
    {
        var sb = new StringBuilder(items.Count * 240);
        AppendHeader(sb, "System", "System.Collections.Frozen", "System.Collections.Generic", "System.Threading", "System.Threading.Tasks", "JXReborn.ZoneEngine.Scripting");
        AppendLegacySources(sb, items.Select(static x => x.Script));
        sb.AppendLine("public sealed class GeneratedSignboardObjectScript : IObjInteractionScript");
        sb.AppendLine("{");
        sb.AppendLine("    private static readonly FrozenDictionary<string, (byte Mode, string[] Lines)> Entries =");
        sb.AppendLine("        new Dictionary<string, (byte, string[])>(StringComparer.OrdinalIgnoreCase)");
        sb.AppendLine("        {");
        foreach (var (s, d) in items)
        {
            sb.Append("            [")
              .Append(ToVerbatimLiteral(s.Utf8ScriptPath.ToLowerInvariant()))
              .Append("] = (")
              .Append(d.Mode)
              .Append(", [")
              .Append(string.Join(", ", d.Lines.Select(ToEscapedStringLiteral)))
              .AppendLine("]),");
        }

        sb.AppendLine("        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);");
        sb.AppendLine();
        sb.AppendLine("    public string ScriptCode => \"obj-generated-signboard\";");
        sb.AppendLine();
        sb.AppendLine("    public ValueTask OnInteractObjectAsync(IScriptObjectContext context, CancellationToken cancellationToken = default)");
        sb.AppendLine("    {");
        sb.AppendLine("        ArgumentNullException.ThrowIfNull(context);");
        sb.AppendLine("        string key = GeneratedLuaRuntime.NormalizeScriptPathLower(context.ObjectScriptPath);");
        sb.AppendLine("        if (Entries.TryGetValue(key, out var e) && e.Lines.Length > 0)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (e.Mode == 1) context.Talk(e.Lines);");
        sb.AppendLine("            else if (e.Mode == 2) context.SendSystemMessage(e.Lines[0]);");
        sb.AppendLine("            else context.Say(e.Lines[0]);");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        return ValueTask.CompletedTask;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildStorageBoxObjectClass(List<(CompiledLuaScript Script, (int ReviveId, ushort ReviveMapId, string RequiredFaction) Data)> items)
    {
        var sb = new StringBuilder(items.Count * 200);
        AppendHeader(sb, "System", "System.Collections.Frozen", "System.Collections.Generic", "System.Threading", "System.Threading.Tasks", "JXReborn.ZoneEngine.Scripting");
        AppendLegacySources(sb, items.Select(static x => x.Script));
        sb.AppendLine("public sealed class GeneratedStorageBoxObjectScript : IObjInteractionScript");
        sb.AppendLine("{");
        sb.AppendLine("    private static readonly FrozenDictionary<string, (int ReviveId, ushort ReviveMapId, string RequiredFaction)> Boxes =");
        sb.AppendLine("        new Dictionary<string, (int, ushort, string)>(StringComparer.OrdinalIgnoreCase)");
        sb.AppendLine("        {");
        foreach (var (s, d) in items)
        {
            sb.Append("            [")
              .Append(ToVerbatimLiteral(s.Utf8ScriptPath.ToLowerInvariant()))
              .Append("] = (")
              .Append(d.ReviveId).Append(", ")
              .Append(d.ReviveMapId).Append(", ")
              .Append(ToEscapedStringLiteral(d.RequiredFaction))
              .AppendLine("),");
        }

        sb.AppendLine("        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);");
        sb.AppendLine();
        sb.AppendLine("    public string ScriptCode => \"obj-generated-storage-box\";");
        sb.AppendLine();
        sb.AppendLine("    public ValueTask OnInteractObjectAsync(IScriptObjectContext context, CancellationToken cancellationToken = default)");
        sb.AppendLine("    {");
        sb.AppendLine("        ArgumentNullException.ThrowIfNull(context);");
        sb.AppendLine("        context.OpenStoreBox();");
        sb.AppendLine("        string key = GeneratedLuaRuntime.NormalizeScriptPathLower(context.ObjectScriptPath);");
        sb.AppendLine("        if (Boxes.TryGetValue(key, out var b) && b.ReviveId > 0)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (b.RequiredFaction.Length == 0 ||");
        sb.AppendLine("                string.Equals(GeneratedLuaRuntime.GetFactionName(context.CurrentFactionId), b.RequiredFaction, StringComparison.Ordinal))");
        sb.AppendLine("            {");
        sb.AppendLine("                context.SetRevivalPoint(b.ReviveMapId > 0 ? b.ReviveMapId : context.MapId, b.ReviveId);");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        return ValueTask.CompletedTask;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildLadderBoardObjectClass(List<(CompiledLuaScript Script, int[] LadderIds)> items)
    {
        var sb = new StringBuilder(items.Count * 180);
        AppendHeader(sb, "System", "System.Collections.Frozen", "System.Collections.Generic", "System.Threading", "System.Threading.Tasks", "JXReborn.ZoneEngine.Scripting");
        AppendLegacySources(sb, items.Select(static x => x.Script));
        sb.AppendLine("public sealed class GeneratedLadderBoardObjectScript : IObjInteractionScript");
        sb.AppendLine("{");
        sb.AppendLine("    private static readonly FrozenDictionary<string, int[]> Ladders =");
        sb.AppendLine("        new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)");
        sb.AppendLine("        {");
        foreach (var (s, ids) in items)
        {
            sb.Append("            [")
              .Append(ToVerbatimLiteral(s.Utf8ScriptPath.ToLowerInvariant()))
              .Append("] = [")
              .Append(string.Join(", ", ids))
              .AppendLine("],");
        }

        sb.AppendLine("        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);");
        sb.AppendLine();
        sb.AppendLine("    public string ScriptCode => \"obj-generated-ladder-board\";");
        sb.AppendLine();
        sb.AppendLine("    public ValueTask OnInteractObjectAsync(IScriptObjectContext context, CancellationToken cancellationToken = default)");
        sb.AppendLine("    {");
        sb.AppendLine("        ArgumentNullException.ThrowIfNull(context);");
        sb.AppendLine("        string key = GeneratedLuaRuntime.NormalizeScriptPathLower(context.ObjectScriptPath);");
        sb.AppendLine("        if (Ladders.TryGetValue(key, out int[]? ids) && ids.Length > 0)");
        sb.AppendLine("        {");
        sb.AppendLine("            context.ShowLadder(ids);");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        return ValueTask.CompletedTask;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildReturnPortalObjectClass(List<CompiledLuaScript> items)
    {
        var sb = new StringBuilder(items.Count * 140);
        AppendHeader(sb, "System", "System.Threading", "System.Threading.Tasks", "JXReborn.ZoneEngine.Scripting");
        AppendLegacySources(sb, items);
        sb.AppendLine("public sealed class GeneratedPortalObjectScript : IObjInteractionScript");
        sb.AppendLine("{");
        sb.AppendLine("    public string ScriptCode => \"obj-generated-town-portal\";");
        sb.AppendLine();
        sb.AppendLine("    public async ValueTask OnInteractObjectAsync(IScriptObjectContext context, CancellationToken cancellationToken = default)");
        sb.AppendLine("    {");
        sb.AppendLine("        ArgumentNullException.ThrowIfNull(context);");
        sb.AppendLine("        _ = await context.TryReturnFromPortalAsync(cancellationToken);");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private string BuildGeneralAsyncScriptClass(
        string className,
        string interfaceName,
        string contextInterfaceName,
        string entryMethodName,
        string scriptCode,
        string pathExpr,
        List<CompiledLuaScript> scripts)
    {
        var sb = new StringBuilder(scripts.Count * 1024);
        AppendHeader(sb, "System", "System.Threading", "System.Threading.Tasks", "JXReborn.ZoneEngine.Scripting");
        AppendLegacySources(sb, scripts);
        sb.AppendLine($"public sealed class {className} : {interfaceName}");
        sb.AppendLine("{");
        sb.AppendLine($"    public string ScriptCode => {ToEscapedStringLiteral(scriptCode)};");
        sb.AppendLine();
        sb.AppendLine($"    public ValueTask {entryMethodName}({contextInterfaceName} context, CancellationToken cancellationToken = default)");
        sb.AppendLine("    {");
        sb.AppendLine("        ArgumentNullException.ThrowIfNull(context);");
        sb.AppendLine($"        switch (GeneratedLuaRuntime.NormalizeScriptPathLower({pathExpr}))");
        sb.AppendLine("        {");
        for (int idx = 0; idx < scripts.Count; idx++)
        {
            CompiledLuaScript s = scripts[idx];
            string entryId = MakeFunctionMethodName(idx, s.EntryFunctionName);
            sb.Append("            case ")
              .Append(ToVerbatimLiteral(s.Utf8ScriptPath.ToLowerInvariant()))
              .Append(": return ")
              .Append(entryId)
              .AppendLine("(context, cancellationToken);");
        }

        sb.AppendLine("            default: return ValueTask.CompletedTask;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");

        for (int idx = 0; idx < scripts.Count; idx++)
        {
            EmitScriptMembers(sb, idx, scripts[idx], "IScriptDialogContext", isItemScript: false, isMonsterScript: false);
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private string BuildGeneralItemScriptClass(List<CompiledLuaScript> scripts)
    {
        var sb = new StringBuilder(scripts.Count * 1024);
        AppendHeader(sb, "System", "System.Threading", "System.Threading.Tasks", "JXReborn.ZoneEngine.Scripting");
        AppendLegacySources(sb, scripts);
        sb.AppendLine("public sealed class GeneratedGeneralItemScript : IItemUsageScript");
        sb.AppendLine("{");
        sb.AppendLine("    public string ScriptCode => \"item-generated-ast\";");
        sb.AppendLine();
        sb.AppendLine("    public ValueTask<ItemUsageResult> OnUseItemAsync(IScriptItemContext context, CancellationToken cancellationToken = default)");
        sb.AppendLine("    {");
        sb.AppendLine("        ArgumentNullException.ThrowIfNull(context);");
        sb.AppendLine("        switch (GeneratedLuaRuntime.NormalizeScriptPathLower(context.ItemScriptPath))");
        sb.AppendLine("        {");
        for (int idx = 0; idx < scripts.Count; idx++)
        {
            CompiledLuaScript s = scripts[idx];
            string entryId = MakeFunctionMethodName(idx, s.EntryFunctionName);
            sb.Append("            case ")
              .Append(ToVerbatimLiteral(s.Utf8ScriptPath.ToLowerInvariant()))
              .Append(": return ")
              .Append(entryId)
              .AppendLine("(context, cancellationToken);");
        }

        sb.AppendLine("            default: return ValueTask.FromResult(ItemUsageResult.KeepItem);");
        sb.AppendLine("        }");
        sb.AppendLine("    }");

        for (int idx = 0; idx < scripts.Count; idx++)
        {
            EmitScriptMembers(sb, idx, scripts[idx], "IScriptItemContext", isItemScript: true, isMonsterScript: false);
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private string BuildGeneralMonsterScriptClass(List<CompiledLuaScript> scripts)
    {
        var sb = new StringBuilder(scripts.Count * 768);
        AppendHeader(sb, "System", "JXReborn.ZoneEngine.Scripting");
        AppendLegacySources(sb, scripts);
        sb.AppendLine("public sealed class GeneratedMonsterDeathScript : INpcCombatScript");
        sb.AppendLine("{");
        sb.AppendLine("    public string ScriptCode => \"npc-generated-combat-ast\";");
        sb.AppendLine();
        sb.AppendLine("    public void OnNpcDeath(IScriptCombatContext context)");
        sb.AppendLine("    {");
        sb.AppendLine("        ArgumentNullException.ThrowIfNull(context);");
        sb.AppendLine("        switch (GeneratedLuaRuntime.NormalizeScriptPathLower(context.SlainNpcScriptPath))");
        sb.AppendLine("        {");
        for (int idx = 0; idx < scripts.Count; idx++)
        {
            CompiledLuaScript s = scripts[idx];
            string entryId = MakeFunctionMethodName(idx, s.EntryFunctionName);
            sb.Append("            case ")
              .Append(ToVerbatimLiteral(s.Utf8ScriptPath.ToLowerInvariant()))
              .Append(": ")
              .Append(entryId)
              .AppendLine("(context); return;");
        }

        sb.AppendLine("        }");
        sb.AppendLine("    }");

        for (int idx = 0; idx < scripts.Count; idx++)
        {
            EmitScriptMembers(sb, idx, scripts[idx], "IScriptCombatContext", isItemScript: false, isMonsterScript: true);
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private void EmitScriptMembers(
        StringBuilder sb,
        int scriptIndex,
        CompiledLuaScript script,
        string ctxType,
        bool isItemScript,
        bool isMonsterScript)
    {
        // Emit top-level constants as private static readonly fields
        var constFieldMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var topLevelConstArrays = new Dictionary<string, LuaTableLiteralExpr>(StringComparer.Ordinal);
        foreach (LuaStatement stmt in script.Program.TopLevelStatements)
        {
            string name;
            LuaExpression init;
            if (stmt is LuaLocalDeclStmt ld && ld.Initializer is not null)
            {
                name = ld.Name;
                init = ld.Initializer;
            }
            else if (stmt is LuaAssignStmt asg)
            {
                name = asg.TargetName;
                init = asg.Value;
            }
            else
            {
                continue;
            }

            string fieldName = $"C{scriptIndex}_{SanitizeIdentifier(name)}";
            constFieldMap[name] = fieldName;
            if (init is LuaTableLiteralExpr tbl)
            {
                topLevelConstArrays[name] = tbl;
            }

            LuaValueType vt = script.TopLevelConstantTypes[name];
            string csType = ToCSharpType(vt);
            sb.AppendLine();
            sb.Append($"    private static readonly {csType} {fieldName} = ");
            sb.Append(EmitExpression(init, scriptIndex, script, constFieldMap, null, isMonsterScript));
            sb.AppendLine(";");
        }

        var functionsByName = script.Program.Functions.ToDictionary(static f => f.Name, StringComparer.Ordinal);

        foreach (LuaFunctionDecl fn in script.Program.Functions)
        {
            bool isEntry = string.Equals(fn.Name, script.EntryFunctionName, StringComparison.Ordinal);
            bool fnAsync = script.FunctionIsAsync[fn.Name];
            LuaValueType retType = script.FunctionReturnTypes[fn.Name];
            string methodName = MakeFunctionMethodName(scriptIndex, fn.Name);

            sb.AppendLine();
            if (isMonsterScript)
            {
                string csRet = isEntry || retType == LuaValueType.Void ? "void" : ToCSharpType(retType);
                sb.AppendLine($"    private static {csRet} {methodName}({ctxType} context)");
            }
            else if (isItemScript && isEntry)
            {
                string asyncKw = fnAsync ? "async " : string.Empty;
                sb.AppendLine($"    private static {asyncKw}ValueTask<ItemUsageResult> {methodName}({ctxType} context, CancellationToken cancellationToken)");
            }
            else if (!isEntry && retType != LuaValueType.Void && !fnAsync)
            {
                sb.AppendLine($"    private static {ToCSharpType(retType)} {methodName}({ctxType} context, CancellationToken cancellationToken)");
            }
            else
            {
                string asyncKw = fnAsync ? "async " : string.Empty;
                string csRet = !isEntry && retType != LuaValueType.Void ? $"ValueTask<{ToCSharpType(retType)}>" : "ValueTask";
                sb.AppendLine($"    private static {asyncKw}{csRet} {methodName}({ctxType} context, CancellationToken cancellationToken)");
            }

            sb.AppendLine("    {");

            // Hoist all function-local variables cleanly at method top
            IReadOnlyDictionary<string, LuaValueType> locals = script.FunctionLocalTypes[fn.Name];
            var localVarMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, LuaValueType> kv in locals.OrderBy(static k => k.Key, StringComparer.Ordinal))
            {
                string csName = $"v_{SanitizeIdentifier(kv.Key)}";
                localVarMap[kv.Key] = csName;
                sb.AppendLine($"        {ToCSharpType(kv.Value)} {csName} = {DefaultValueExpr(kv.Value)};");
            }

            var fnConstArrays = new Dictionary<string, LuaTableLiteralExpr>(topLevelConstArrays, StringComparer.Ordinal);
            bool endsWithReturn = EmitBlock(
                sb,
                fn.Body,
                indent: "        ",
                scriptIndex,
                script,
                fn,
                isEntry,
                fnAsync,
                retType,
                isItemScript,
                isMonsterScript,
                functionsByName,
                constFieldMap,
                localVarMap,
                fnConstArrays);

            // Suppress unused-local warnings if a legacy Lua script assigned a variable and never read it
            if (!endsWithReturn)
            {
                if (isItemScript && isEntry)
                {
                    sb.AppendLine(fnAsync
                        ? "        return ItemUsageResult.ConsumeOne;"
                        : "        return ValueTask.FromResult(ItemUsageResult.ConsumeOne);");
                }
                else if (!isMonsterScript && !fnAsync)
                {
                    if (isEntry || retType == LuaValueType.Void)
                    {
                        sb.AppendLine("        return ValueTask.CompletedTask;");
                    }
                    else
                    {
                        sb.AppendLine($"        return {DefaultValueExpr(retType)};");
                    }
                }
                else if (!isEntry && retType != LuaValueType.Void)
                {
                    sb.AppendLine($"        return {DefaultValueExpr(retType)};");
                }
            }

            sb.AppendLine("    }");
        }
    }

    private bool EmitBlock(
        StringBuilder sb,
        IReadOnlyList<LuaStatement> statements,
        string indent,
        int scriptIndex,
        CompiledLuaScript script,
        LuaFunctionDecl currentFn,
        bool isEntry,
        bool fnAsync,
        LuaValueType retType,
        bool isItemScript,
        bool isMonsterScript,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        IReadOnlyDictionary<string, string> constFieldMap,
        IReadOnlyDictionary<string, string> localVarMap,
        Dictionary<string, LuaTableLiteralExpr> constArrays)
    {
        for (int i = 0; i < statements.Count; i++)
        {
            LuaStatement stmt = statements[i];

            // Atomic ordering peel: `SetPos(x, y); SetFightState(f)` -> `SetFightMode(f)` then `TeleportPlayerAsync`
            if (!isMonsterScript &&
                i + 1 < statements.Count &&
                stmt is LuaCallStmt { Call: { CalleeName: "SetPos", Arguments.Count: 2 } posCall } &&
                statements[i + 1] is LuaCallStmt { Call: { CalleeName: "SetFightState", Arguments.Count: 1 } fightCall })
            {
                string fmExpr = EmitExpression(fightCall.Arguments[0], scriptIndex, script, constFieldMap, localVarMap, isMonsterScript);
                string xExpr = EmitExpression(posCall.Arguments[0], scriptIndex, script, constFieldMap, localVarMap, isMonsterScript);
                string yExpr = EmitExpression(posCall.Arguments[1], scriptIndex, script, constFieldMap, localVarMap, isMonsterScript);
                sb.AppendLine($"{indent}context.SetFightMode((byte)(({fmExpr}) != 0 ? 1 : 0));");
                sb.AppendLine($"{indent}_ = await context.TeleportPlayerAsync(context.MapId, ({xExpr}) * 32, ({yExpr}) * 32, context.CurrentFightMode, cancellationToken);");
                i++;
                continue;
            }

            switch (stmt)
            {
                case LuaLocalDeclStmt ld when ld.Initializer is not null:
                {
                    string lhs = localVarMap[ld.Name];
                    string rhs = EmitExpression(ld.Initializer, scriptIndex, script, constFieldMap, localVarMap, isMonsterScript);
                    sb.AppendLine($"{indent}{lhs} = {rhs};");
                    if (ld.Initializer is LuaTableLiteralExpr tbl)
                    {
                        constArrays[ld.Name] = tbl;
                    }
                    else
                    {
                        constArrays.Remove(ld.Name);
                    }

                    break;
                }

                case LuaAssignStmt asg:
                {
                    string lhs = localVarMap[asg.TargetName];
                    string rhs = EmitExpression(asg.Value, scriptIndex, script, constFieldMap, localVarMap, isMonsterScript);
                    sb.AppendLine($"{indent}{lhs} = {rhs};");
                    if (asg.Value is LuaTableLiteralExpr tbl)
                    {
                        constArrays[asg.TargetName] = tbl;
                    }
                    else
                    {
                        constArrays.Remove(asg.TargetName);
                    }

                    break;
                }

                case LuaIfStmt ifStmt:
                {
                    string cond = EmitExpression(ifStmt.Condition, scriptIndex, script, constFieldMap, localVarMap, isMonsterScript);
                    sb.AppendLine($"{indent}if ({cond})");
                    sb.AppendLine($"{indent}{{");
                    bool thenRet = EmitBlock(sb, ifStmt.ThenBody, indent + "    ", scriptIndex, script, currentFn, isEntry, fnAsync, retType, isItemScript, isMonsterScript, functionsByName, constFieldMap, localVarMap, new Dictionary<string, LuaTableLiteralExpr>(constArrays, StringComparer.Ordinal));
                    sb.AppendLine($"{indent}}}");

                    bool allBranchesReturn = thenRet;
                    foreach (LuaElseIfBranch ei in ifStmt.ElseIfBranches)
                    {
                        string eiCond = EmitExpression(ei.Condition, scriptIndex, script, constFieldMap, localVarMap, isMonsterScript);
                        sb.AppendLine($"{indent}else if ({eiCond})");
                        sb.AppendLine($"{indent}{{");
                        bool eiRet = EmitBlock(sb, ei.Body, indent + "    ", scriptIndex, script, currentFn, isEntry, fnAsync, retType, isItemScript, isMonsterScript, functionsByName, constFieldMap, localVarMap, new Dictionary<string, LuaTableLiteralExpr>(constArrays, StringComparer.Ordinal));
                        sb.AppendLine($"{indent}}}");
                        allBranchesReturn &= eiRet;
                    }

                    if (ifStmt.ElseBody.Count > 0)
                    {
                        sb.AppendLine($"{indent}else");
                        sb.AppendLine($"{indent}{{");
                        bool elseRet = EmitBlock(sb, ifStmt.ElseBody, indent + "    ", scriptIndex, script, currentFn, isEntry, fnAsync, retType, isItemScript, isMonsterScript, functionsByName, constFieldMap, localVarMap, new Dictionary<string, LuaTableLiteralExpr>(constArrays, StringComparer.Ordinal));
                        sb.AppendLine($"{indent}}}");
                        allBranchesReturn &= elseRet;
                    }
                    else
                    {
                        allBranchesReturn = false;
                    }

                    if (allBranchesReturn)
                    {
                        return true;
                    }

                    break;
                }

                case LuaCallStmt cs:
                    EmitCallStatement(sb, cs.Call, indent, scriptIndex, script, isItemScript, isMonsterScript, functionsByName, constFieldMap, localVarMap, constArrays);
                    break;

                case LuaReturnStmt ret:
                    EmitReturnStatement(sb, ret, indent, scriptIndex, script, isEntry, fnAsync, retType, isItemScript, isMonsterScript, constFieldMap, localVarMap);
                    return true;
            }
        }

        return false;
    }

    private void EmitReturnStatement(
        StringBuilder sb,
        LuaReturnStmt ret,
        string indent,
        int scriptIndex,
        CompiledLuaScript script,
        bool isEntry,
        bool fnAsync,
        LuaValueType retType,
        bool isItemScript,
        bool isMonsterScript,
        IReadOnlyDictionary<string, string> constFieldMap,
        IReadOnlyDictionary<string, string> localVarMap)
    {
        if (isItemScript && isEntry)
        {
            string res = ret.Value is not null && LuaScriptSemanticValidator.TryEvalConstantInt(ret.Value, out int v) && v != 0
                ? "ItemUsageResult.KeepItem"
                : "ItemUsageResult.ConsumeOne";
            sb.AppendLine(fnAsync ? $"{indent}return {res};" : $"{indent}return ValueTask.FromResult({res});");
            return;
        }

        if (isMonsterScript)
        {
            if (!isEntry && retType != LuaValueType.Void && ret.Value is not null)
            {
                string val = EmitExpression(ret.Value, scriptIndex, script, constFieldMap, localVarMap, isMonsterScript);
                sb.AppendLine($"{indent}return {val};");
            }
            else
            {
                sb.AppendLine($"{indent}return;");
            }

            return;
        }

        if (!isEntry && retType != LuaValueType.Void && ret.Value is not null)
        {
            string val = EmitExpression(ret.Value, scriptIndex, script, constFieldMap, localVarMap, isMonsterScript);
            sb.AppendLine($"{indent}return {val};");
            return;
        }

        sb.AppendLine(fnAsync ? $"{indent}return;" : $"{indent}return ValueTask.CompletedTask;");
    }

    private void EmitCallStatement(
        StringBuilder sb,
        LuaCallExpr call,
        string indent,
        int scriptIndex,
        CompiledLuaScript script,
        bool isItemScript,
        bool isMonsterScript,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        IReadOnlyDictionary<string, string> constFieldMap,
        IReadOnlyDictionary<string, string> localVarMap,
        IReadOnlyDictionary<string, LuaTableLiteralExpr> constArrays)
    {
        string api = call.CalleeName;
        IReadOnlyList<LuaExpression> args = call.Arguments;

        string E(int idx) => EmitExpression(args[idx], scriptIndex, script, constFieldMap, localVarMap, isMonsterScript);
        string EStr(int idx) => EmitAsStringExpression(args[idx], scriptIndex, script, constFieldMap, localVarMap, isMonsterScript);

        if (functionsByName.ContainsKey(api))
        {
            string methodName = MakeFunctionMethodName(scriptIndex, api);
            if (isMonsterScript)
            {
                sb.AppendLine($"{indent}{methodName}(context);");
            }
            else if (script.FunctionIsAsync[api])
            {
                sb.AppendLine($"{indent}await {methodName}(context, cancellationToken);");
            }
            else if (script.FunctionReturnTypes[api] == LuaValueType.Void)
            {
                sb.AppendLine($"{indent}_ = {methodName}(context, cancellationToken);");
            }
            else
            {
                sb.AppendLine($"{indent}_ = {methodName}(context, cancellationToken);");
            }

            return;
        }

        switch (api)
        {
            case "SetTask":
                sb.AppendLine($"{indent}context.SetTask((ushort)({E(0)}), {E(1)});");
                break;
            case "SetTaskTemp":
                sb.AppendLine($"{indent}context.SetTempTask((ushort)({E(0)}), {E(1)});");
                break;
            case "SetGlbValue":
                sb.AppendLine($"{indent}context.SetGlobalValue({E(0)}, {E(1)});");
                break;
            case "Msg2Player":
                sb.AppendLine($"{indent}context.SendSystemMessage({EStr(0)});");
                break;
            case "AddNote":
                sb.AppendLine($"{indent}context.AddNote({EStr(0)});");
                break;
            case "Earn":
                sb.AppendLine($"{indent}context.GrantMoney({E(0)});");
                break;
            case "Pay":
                sb.AppendLine($"{indent}_ = context.TryPayMoney({E(0)});");
                break;
            case "AddOwnExp":
                sb.AppendLine($"{indent}context.GrantExperience({E(0)});");
                break;
            case "AddRepute":
                sb.AppendLine($"{indent}context.AddRepute({E(0)});");
                break;
            case "AddEventItem":
            {
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(args[0], out int eid);
                string code = _eventItemCodeByDetailId[eid];
                sb.AppendLine($"{indent}_ = context.TryGrantItemByCode({ToEscapedStringLiteral(code)});");
                break;
            }
            case "DelItem":
            {
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(args[0], out int eid);
                string code = _eventItemCodeByDetailId[eid];
                sb.AppendLine($"{indent}_ = context.TryConsumeItemByCode({ToEscapedStringLiteral(code)});");
                break;
            }
            case "SetFightState":
                sb.AppendLine($"{indent}context.SetFightMode((byte)(({E(0)}) != 0 ? 1 : 0));");
                break;
            case "SetProtectTime":
                sb.AppendLine($"{indent}context.SetProtectTime({E(0)});");
                break;
            case "AddSkillState":
                sb.AppendLine($"{indent}context.AddSkillState({E(0)}, {E(1)}, {E(2)}, {E(3)});");
                break;
            case "AddStation":
                sb.AppendLine($"{indent}context.UnlockStation((ushort)({E(0)}));");
                break;
            case "AddTermini":
                sb.AppendLine($"{indent}context.RecordWayPoint({E(0)});");
                break;
            case "SetRevPos":
                if (args.Count == 1)
                {
                    sb.AppendLine($"{indent}context.SetRevivalPoint(context.MapId, {E(0)});");
                }
                else
                {
                    sb.AppendLine($"{indent}context.SetRevivalPoint((ushort)({E(0)}), {E(1)});");
                }

                break;
            case "OpenBox":
                sb.AppendLine($"{indent}context.OpenStoreBox();");
                break;
            case "ShowLadder":
            {
                var ids = new List<string>(args.Count - 1);
                for (int i = 1; i < args.Count; i++)
                {
                    ids.Add(E(i));
                }

                sb.AppendLine($"{indent}context.ShowLadder([{string.Join(", ", ids)}]);");
                break;
            }
            case "Sale":
                if (args.Count == 1)
                {
                    sb.AppendLine($"{indent}context.OpenShop({E(0)});");
                }
                else if (args.Count == 2)
                {
                    sb.AppendLine($"{indent}context.OpenShop({E(0)}, (byte)({E(1)}));");
                }
                else
                {
                    sb.AppendLine($"{indent}context.OpenShop({E(0)}, (byte)({E(1)}), {E(2)});");
                }

                break;
            case "AddMagic":
                if (args.Count == 1)
                {
                    sb.AppendLine($"{indent}_ = context.TryLearnSkill({E(0)}, 1);");
                }
                else
                {
                    sb.AppendLine($"{indent}_ = context.TryLearnSkill({E(0)}, (ushort)({E(1)}));");
                }

                break;
            case "AddProp":
                sb.AppendLine($"{indent}context.AddBonusAttributePoints({E(0)});");
                break;
            case "AddMagicPoint":
                sb.AppendLine($"{indent}context.AddBonusSkillPoints({E(0)});");
                break;
            case "NewWorld":
                sb.AppendLine($"{indent}_ = await context.TeleportPlayerAsync((ushort)({E(0)}), ({E(1)}) * 32, ({E(2)}) * 32, context.CurrentFightMode, cancellationToken);");
                break;
            case "SetPos":
                sb.AppendLine($"{indent}_ = await context.TeleportPlayerAsync(context.MapId, ({E(0)}) * 32, ({E(1)}) * 32, context.CurrentFightMode, cancellationToken);");
                break;
            case "ReturnFromPortal":
                sb.AppendLine($"{indent}_ = await context.TryReturnFromPortalAsync(cancellationToken);");
                break;
            case "Talk":
            {
                var pages = new List<string>(args.Count - 2);
                for (int i = 2; i < args.Count; i++)
                {
                    pages.Add(EStr(i));
                }

                string cbDelegate = "null";
                if (args[1] is LuaStringExpr cbStr)
                {
                    string cbName = cbStr.DecodedUtf8.Trim();
                    if (cbName.Length > 0 && functionsByName.TryGetValue(cbName, out LuaFunctionDecl? cbFn) && cbFn.Body.Count > 0)
                    {
                        cbDelegate = BuildCallbackDelegateExpr(scriptIndex, script, cbName, isItemScript);
                    }
                }

                sb.AppendLine($"{indent}context.Talk([{string.Join(", ", pages)}], {cbDelegate});");
                break;
            }
            case "Say":
            {
                string prompt = EStr(0);
                if (args.Count == 2)
                {
                    sb.AppendLine($"{indent}context.Say({prompt});");
                    break;
                }

                var optStrings = new List<string>();
                if (args.Count == 3 && args[2] is LuaIdentifierExpr tabIdent && constArrays.TryGetValue(tabIdent.Name, out LuaTableLiteralExpr? tabLit))
                {
                    foreach (LuaExpression el in tabLit.Elements)
                    {
                        if (LuaScriptSemanticValidator.TryEvalConstantString(el, out string? s) && s is not null)
                        {
                            optStrings.Add(s);
                        }
                    }
                }
                else
                {
                    for (int i = 2; i < args.Count; i++)
                    {
                        if (LuaScriptSemanticValidator.TryEvalConstantString(args[i], out string? s) && s is not null)
                        {
                            optStrings.Add(s);
                        }
                    }
                }

                var emittedOpts = new List<string>(optStrings.Count);
                foreach (string optSpec in optStrings)
                {
                    int slash = optSpec.LastIndexOf('/');
                    string label = slash >= 0 ? optSpec[..slash] : optSpec;
                    string cbName = slash >= 0 ? optSpec[(slash + 1)..].Trim() : string.Empty;
                    string cbExpr = "null";
                    if (cbName.Length > 0 &&
                        !cbName.Equals("no", StringComparison.OrdinalIgnoreCase) &&
                        !cbName.Equals("OnCancel", StringComparison.OrdinalIgnoreCase) &&
                        functionsByName.TryGetValue(cbName, out LuaFunctionDecl? cbFn) &&
                        cbFn.Body.Count > 0)
                    {
                        cbExpr = BuildCallbackDelegateExpr(scriptIndex, script, cbName, isItemScript);
                    }

                    emittedOpts.Add($"new DialogOption({ToEscapedStringLiteral(label)}, {cbExpr})");
                }

                sb.AppendLine($"{indent}context.Say({prompt}, {string.Join(", ", emittedOpts)});");
                break;
            }
        }
    }

    private static string BuildCallbackDelegateExpr(int scriptIndex, CompiledLuaScript script, string cbName, bool isItemScript)
    {
        string methodName = MakeFunctionMethodName(scriptIndex, cbName);
        string ctxArg = isItemScript ? "(IScriptItemContext)ctx" : "ctx";
        bool cbAsync = script.FunctionIsAsync[cbName];
        LuaValueType cbRet = script.FunctionReturnTypes[cbName];

        if (!cbAsync && cbRet != LuaValueType.Void)
        {
            return $"static ctx => {{ _ = {methodName}({ctxArg}, CancellationToken.None); return ValueTask.CompletedTask; }}";
        }

        if (cbAsync && cbRet != LuaValueType.Void)
        {
            return $"static async ctx => {{ _ = await {methodName}({ctxArg}, CancellationToken.None); }}";
        }

        return $"static ctx => {methodName}({ctxArg}, CancellationToken.None)";
    }

    private string EmitAsStringExpression(
        LuaExpression expr,
        int scriptIndex,
        CompiledLuaScript script,
        IReadOnlyDictionary<string, string> constFieldMap,
        IReadOnlyDictionary<string, string>? localVarMap,
        bool isMonsterScript)
    {
        LuaValueType t = InferExpressionType(expr, script);
        string raw = EmitExpression(expr, scriptIndex, script, constFieldMap, localVarMap, isMonsterScript);
        return t == LuaValueType.Int ? $"GeneratedLuaRuntime.FormatInt({raw})" : raw;
    }

    private string EmitExpression(
        LuaExpression expr,
        int scriptIndex,
        CompiledLuaScript script,
        IReadOnlyDictionary<string, string> constFieldMap,
        IReadOnlyDictionary<string, string>? localVarMap,
        bool isMonsterScript)
    {
        switch (expr)
        {
            case LuaNumberExpr num:
                return ((int)Math.Truncate(num.Value)).ToString(CultureInfo.InvariantCulture);

            case LuaStringExpr str:
                return ToEscapedStringLiteral(str.DecodedUtf8);

            case LuaIdentifierExpr id:
                if (localVarMap is not null && localVarMap.TryGetValue(id.Name, out string? locName))
                {
                    return locName;
                }

                return constFieldMap[id.Name];

            case LuaTableLiteralExpr tbl:
                return "[" + string.Join(", ", tbl.Elements.Select(e => EmitExpression(e, scriptIndex, script, constFieldMap, localVarMap, isMonsterScript))) + "]";

            case LuaIndexExpr idx:
                return $"GeneratedLuaRuntime.GetAt1Based({EmitExpression(idx.Target, scriptIndex, script, constFieldMap, localVarMap, isMonsterScript)}, {EmitExpression(idx.Index, scriptIndex, script, constFieldMap, localVarMap, isMonsterScript)})";

            case LuaUnaryExpr un:
            {
                string op = un.Operator == LuaUnaryOp.Negate ? "-" : "!";
                return $"({op}({EmitExpression(un.Operand, scriptIndex, script, constFieldMap, localVarMap, isMonsterScript)}))";
            }

            case LuaBinaryExpr bin:
            {
                if (bin.Operator is LuaBinaryOp.Div or LuaBinaryOp.Pow &&
                    LuaScriptSemanticValidator.TryEvalConstantInt(bin, out int folded))
                {
                    return folded.ToString(CultureInfo.InvariantCulture);
                }

                if (bin.Operator == LuaBinaryOp.Concat)
                {
                    string ls = EmitAsStringExpression(bin.Left, scriptIndex, script, constFieldMap, localVarMap, isMonsterScript);
                    string rs = EmitAsStringExpression(bin.Right, scriptIndex, script, constFieldMap, localVarMap, isMonsterScript);
                    return $"({ls} + {rs})";
                }

                if (bin.Operator is LuaBinaryOp.Eq or LuaBinaryOp.Neq &&
                    InferExpressionType(bin.Left, script) == LuaValueType.String)
                {
                    string ls = EmitExpression(bin.Left, scriptIndex, script, constFieldMap, localVarMap, isMonsterScript);
                    string rs = EmitExpression(bin.Right, scriptIndex, script, constFieldMap, localVarMap, isMonsterScript);
                    string eq = $"string.Equals({ls}, {rs}, StringComparison.Ordinal)";
                    return bin.Operator == LuaBinaryOp.Eq ? eq : $"(!{eq})";
                }

                string l = EmitExpression(bin.Left, scriptIndex, script, constFieldMap, localVarMap, isMonsterScript);
                string r = EmitExpression(bin.Right, scriptIndex, script, constFieldMap, localVarMap, isMonsterScript);
                string opStr = bin.Operator switch
                {
                    LuaBinaryOp.Or => "||",
                    LuaBinaryOp.And => "&&",
                    LuaBinaryOp.Eq => "==",
                    LuaBinaryOp.Neq => "!=",
                    LuaBinaryOp.Lt => "<",
                    LuaBinaryOp.Le => "<=",
                    LuaBinaryOp.Gt => ">",
                    LuaBinaryOp.Ge => ">=",
                    LuaBinaryOp.Add => "+",
                    LuaBinaryOp.Sub => "-",
                    LuaBinaryOp.Mul => "*",
                    LuaBinaryOp.Mod => "%",
                    _ => "+",
                };
                return $"({l} {opStr} {r})";
            }

            case LuaCallExpr call:
                return EmitCallExpression(call, scriptIndex, script, constFieldMap, localVarMap, isMonsterScript);

            default:
                throw new InvalidOperationException($"Unsupported expression node '{expr.GetType().Name}'.");
        }
    }

    private string EmitCallExpression(
        LuaCallExpr call,
        int scriptIndex,
        CompiledLuaScript script,
        IReadOnlyDictionary<string, string> constFieldMap,
        IReadOnlyDictionary<string, string>? localVarMap,
        bool isMonsterScript)
    {
        string E(int idx) => EmitExpression(call.Arguments[idx], scriptIndex, script, constFieldMap, localVarMap, isMonsterScript);

        if (script.FunctionReturnTypes.ContainsKey(call.CalleeName))
        {
            string methodName = MakeFunctionMethodName(scriptIndex, call.CalleeName);
            if (isMonsterScript)
            {
                return $"{methodName}(context)";
            }

            return script.FunctionIsAsync[call.CalleeName]
                ? $"(await {methodName}(context, cancellationToken))"
                : $"{methodName}(context, cancellationToken)";
        }

        return call.CalleeName switch
        {
            "GetLevel" => "(int)context.Level",
            "GetSex" => "(int)context.Gender",
            "GetSeries" => "(int)context.Series",
            "GetCash" => "context.InventoryMoney",
            "GetRepute" => "context.Repute",
            "GetFightState" => "(int)context.CurrentFightMode",
            "CalcFreeItemCellCount" => "context.FreeBagCellCount",
            "GetName" => "context.RoleNameUtf8",
            "GetFaction" => "GeneratedLuaRuntime.GetFactionName(context.CurrentFactionId)",
            "GetLastAddFaction" => "GeneratedLuaRuntime.GetFactionName(context.EffectiveFactionId)",
            "GetTask" => $"context.GetTask((ushort)({E(0)}))",
            "GetTaskTemp" => $"context.GetTempTask((ushort)({E(0)}))",
            "GetGlbValue" => $"context.GetGlobalValue({E(0)})",
            "HaveMagic" => $"context.GetLearnedSkillLevel({E(0)})",
            "Pay" => $"(context.TryPayMoney({E(0)}) ? 1 : 0)",
            "GetByte" => $"GeneratedLuaRuntime.GetByte({E(0)}, {E(1)})",
            "SetByte" => $"GeneratedLuaRuntime.SetByte({E(0)}, {E(1)}, {E(2)})",
            "GetBit" => $"GeneratedLuaRuntime.GetBit({E(0)}, {E(1)})",
            "SetBit" => $"GeneratedLuaRuntime.SetBit({E(0)}, {E(1)}, {E(2)})",
            "random" => call.Arguments.Count == 1
                ? $"GeneratedLuaRuntime.LuaRandom({E(0)})"
                : $"GeneratedLuaRuntime.LuaRandom({E(0)}, {E(1)})",
            "getn" => $"({E(0)}).Length",
            "HaveItem" => EmitHaveItemExpr(call.Arguments[0]),
            _ => throw new InvalidOperationException($"Unsupported call expression '{call.CalleeName}'."),
        };
    }

    private string EmitHaveItemExpr(LuaExpression arg)
    {
        _ = LuaScriptSemanticValidator.TryEvalConstantInt(arg, out int eid);
        string code = _eventItemCodeByDetailId[eid];
        return $"(context.HasBagItem({ToEscapedStringLiteral(code)}) ? 1 : 0)";
    }

    private static LuaValueType InferExpressionType(LuaExpression expr, CompiledLuaScript script)
    {
        switch (expr)
        {
            case LuaNumberExpr:
                return LuaValueType.Int;
            case LuaStringExpr:
                return LuaValueType.String;
            case LuaIdentifierExpr id:
                if (script.TopLevelConstantTypes.TryGetValue(id.Name, out LuaValueType tt))
                {
                    return tt;
                }

                foreach (IReadOnlyDictionary<string, LuaValueType> locals in script.FunctionLocalTypes.Values)
                {
                    if (locals.TryGetValue(id.Name, out LuaValueType lt))
                    {
                        return lt;
                    }
                }

                return LuaValueType.Int;
            case LuaIndexExpr idx:
                return InferExpressionType(idx.Target, script) == LuaValueType.StringArray
                    ? LuaValueType.String
                    : LuaValueType.Int;
            case LuaBinaryExpr bin:
                return bin.Operator switch
                {
                    LuaBinaryOp.Concat => LuaValueType.String,
                    LuaBinaryOp.Or or LuaBinaryOp.And or LuaBinaryOp.Eq or LuaBinaryOp.Neq or LuaBinaryOp.Lt or LuaBinaryOp.Le or LuaBinaryOp.Gt or LuaBinaryOp.Ge => LuaValueType.Bool,
                    _ => LuaValueType.Int,
                };
            case LuaUnaryExpr un:
                return un.Operator == LuaUnaryOp.Not ? LuaValueType.Bool : LuaValueType.Int;
            case LuaCallExpr call:
                if (call.CalleeName is "GetName" or "GetFaction" or "GetLastAddFaction")
                {
                    return LuaValueType.String;
                }

                if (script.FunctionReturnTypes.TryGetValue(call.CalleeName, out LuaValueType fnRet))
                {
                    return fnRet;
                }

                return LuaValueType.Int;
            default:
                return LuaValueType.Int;
        }
    }

    private static void AppendHeader(StringBuilder sb, params string[] usings)
    {
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("// Generated by JXReborn.ScriptMigrator (fail-closed Lua 4.0 AST transpiler).");
        sb.AppendLine("// Do not edit manually; regenerate via `JXReborn.ScriptMigrator generate-scripts`.");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CS0162, CS0219, CS8321");
        sb.AppendLine();
        foreach (string u in usings)
        {
            sb.AppendLine($"using {u};");
        }

        sb.AppendLine();
        sb.AppendLine("namespace JXReborn.Packs.TieuDaoTu.Scripts.Generated;");
        sb.AppendLine();
    }

    private static void AppendLegacySources(StringBuilder sb, IEnumerable<CompiledLuaScript> scripts)
    {
        foreach (CompiledLuaScript s in scripts)
        {
            sb.Append("[LegacySource(")
              .Append(ToVerbatimLiteral(s.Utf8ScriptPath))
              .Append(", ")
              .Append(ToEscapedStringLiteral(s.Entry.Sha256))
              .AppendLine(")]");
        }
    }

    private static string MakeFunctionMethodName(int scriptIndex, string luaFnName) =>
        $"S{scriptIndex}_{SanitizeIdentifier(luaFnName)}";

    private static string SanitizeIdentifier(string name)
    {
        var sb = new StringBuilder(name.Length);
        bool allAsciiSafe = true;
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_')
            {
                sb.Append(c);
            }
            else
            {
                allAsciiSafe = false;
                sb.Append('_');
            }
        }

        if (!allAsciiSafe || sb.Length == 0)
        {
            string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8];
            return $"u_{hash}";
        }

        return sb.ToString();
    }

    private static string ToCSharpType(LuaValueType t) =>
        t switch
        {
            LuaValueType.Int => "int",
            LuaValueType.String => "string",
            LuaValueType.Bool => "bool",
            LuaValueType.StringArray => "string[]",
            LuaValueType.IntArray => "int[]",
            _ => "void",
        };

    private static string DefaultValueExpr(LuaValueType t) =>
        t switch
        {
            LuaValueType.Int => "0",
            LuaValueType.String => "string.Empty",
            LuaValueType.Bool => "false",
            LuaValueType.StringArray => "[]",
            LuaValueType.IntArray => "[]",
            _ => "default",
        };

    private static string ToVerbatimLiteral(string value) =>
        "@\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string ToEscapedStringLiteral(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        sb.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                case '\0': sb.Append("\\0"); break;
                default:
                    if (char.IsControl(c))
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }

    private static void WriteUtf8(string path, string content)
    {
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
