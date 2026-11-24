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
    private readonly PackItemCatalogIndex? _itemCatalogIndex;

    public LuaCSharpCodeEmitter(
        IReadOnlyDictionary<int, string> eventItemCodeByDetailId,
        PackItemCatalogIndex? itemCatalogIndex = null)
    {
        _eventItemCodeByDetailId = eventItemCodeByDetailId ?? throw new ArgumentNullException(nameof(eventItemCodeByDetailId));
        _itemCatalogIndex = itemCatalogIndex;
    }

    public IReadOnlyList<string> EmitAll(
        string generatedDir,
        IReadOnlyList<CompiledLuaScript> compiledScripts)
    {
        Directory.CreateDirectory(generatedDir);
        var writtenFiles = new List<string>();

        if (compiledScripts.Count == 0)
        {
            return writtenFiles;
        }

        string runtimePath = Path.Combine(generatedDir, "GeneratedLuaRuntime.cs");
        WriteUtf8(runtimePath, BuildGeneratedLuaRuntimeSource());
        writtenFiles.Add(runtimePath);

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
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JXReborn.Packs.TieuDaoTu.Hosting;
using JXReborn.ZoneEngine.Scripting;

namespace JXReborn.Packs.TieuDaoTu.Scripts.Generated;

public static class GeneratedLuaRuntime
{
    private static readonly AsyncLocal<Func<int, int, int>?> RandomOverride = new();
    private static readonly AsyncLocal<DateTime?> ClockOverride = new();

    public static IDisposable UseDeterministicRandom(Func<int, int, int> nextInclusive)
    {
        ArgumentNullException.ThrowIfNull(nextInclusive);
        Func<int, int, int>? previous = RandomOverride.Value;
        RandomOverride.Value = nextInclusive;
        return new RandomScope(previous);
    }

    public static IDisposable UseDeterministicClock(DateTime simulatedLocalTime)
    {
        DateTime? previous = ClockOverride.Value;
        ClockOverride.Value = simulatedLocalTime;
        return new ClockScope(previous);
    }

    private sealed class RandomScope(Func<int, int, int>? previous) : IDisposable
    {
        public void Dispose() => RandomOverride.Value = previous;
    }

    private sealed class ClockScope(DateTime? previous) : IDisposable
    {
        public void Dispose() => ClockOverride.Value = previous;
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

    public static string GetAt1Based(IReadOnlyList<string> items, int luaIndex)
    {
        int idx = luaIndex - 1;
        return (uint)idx < (uint)items.Count ? items[idx] : string.Empty;
    }

    public static int GetAt1Based(IReadOnlyList<int> items, int luaIndex)
    {
        int idx = luaIndex - 1;
        return (uint)idx < (uint)items.Count ? items[idx] : 0;
    }

    public static void SetAt1Based<T>(List<T> items, int luaIndex, T value, T padDefault)
    {
        if (luaIndex <= 0)
        {
            return;
        }

        while (items.Count < luaIndex - 1)
        {
            items.Add(padDefault);
        }

        if (items.Count == luaIndex - 1)
        {
            items.Add(value);
        }
        else
        {
            items[luaIndex - 1] = value;
        }
    }

    public static void InsertAt1Based<T>(List<T> items, int luaPos, T value)
    {
        int idx = Math.Clamp(luaPos - 1, 0, items.Count);
        items.Insert(idx, value);
    }

    public static string FormatInt(int value) => value.ToString(CultureInfo.InvariantCulture);

    public static string FormatNullableInt(int? value) =>
        value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "nil";

    public static string FormatDouble(double value) => value.ToString("G15", CultureInfo.InvariantCulture);

    public static int? LuaToNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv))
        {
            return iv;
        }

        if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double dv))
        {
            return (int)Math.Truncate(dv);
        }

        return null;
    }

    public static int LuaMod(int x, int y) => y == 0 ? 0 : x % y;

    public static double LuaMod(double x, double y) => y == 0.0 ? 0.0 : x % y;

    public static string LuaStrSub(string? s, int start, int? endOpt = null)
    {
        if (string.IsNullOrEmpty(s))
        {
            return string.Empty;
        }

        int len = s.Length;
        int end = endOpt ?? len;
        if (start < 0) start = len + start + 1;
        if (end < 0) end = len + end + 1;
        start = Math.Max(1, start);
        end = Math.Min(len, end);
        return start > end ? string.Empty : s.Substring(start - 1, end - start + 1);
    }

    public static string LuaFormat(string fmt, params ReadOnlySpan<object?> args)
    {
        if (string.IsNullOrEmpty(fmt))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(fmt.Length + 16);
        int argIdx = 0;
        for (int i = 0; i < fmt.Length; i++)
        {
            if (fmt[i] != '%' || i + 1 >= fmt.Length)
            {
                sb.Append(fmt[i]);
                continue;
            }

            i++;
            if (fmt[i] == '%')
            {
                sb.Append('%');
                continue;
            }

            while (i < fmt.Length && (fmt[i] is '-' or '+' or ' ' or '0' or '#' or '.' || (fmt[i] is >= '0' and <= '9')))
            {
                i++;
            }

            if (i >= fmt.Length)
            {
                break;
            }

            char spec = fmt[i];
            object? val = argIdx < args.Length ? args[argIdx++] : null;
            double numVal = val switch
            {
                int iv => iv,
                long lv => lv,
                double dv => dv,
                string sv when double.TryParse(sv, NumberStyles.Float, CultureInfo.InvariantCulture, out double pd) => pd,
                _ => 0.0,
            };

            switch (spec)
            {
                case 'd':
                case 'i':
                case 'u':
                    sb.Append(((long)numVal).ToString(CultureInfo.InvariantCulture));
                    break;
                case 'f':
                case 'g':
                case 'e':
                    sb.Append(numVal.ToString("G15", CultureInfo.InvariantCulture));
                    break;
                case 'x':
                    sb.Append(((long)numVal).ToString("x", CultureInfo.InvariantCulture));
                    break;
                case 'X':
                    sb.Append(((long)numVal).ToString("X", CultureInfo.InvariantCulture));
                    break;
                default:
                    sb.Append(val switch
                    {
                        null => "nil",
                        string s => s,
                        int iv => iv.ToString(CultureInfo.InvariantCulture),
                        double dv => dv.ToString("G15", CultureInfo.InvariantCulture),
                        _ => val.ToString() ?? string.Empty,
                    });
                    break;
            }
        }

        return sb.ToString();
    }

    public static int GetCurServerTime()
    {
        DateTime dt = ClockOverride.Value ?? DateTime.Now;
        DateTime utc = dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return unchecked((int)new DateTimeOffset(utc).ToUnixTimeSeconds());
    }

    public static string FormatLegacyDate(string? format = null)
    {
        DateTime now = ClockOverride.Value ?? DateTime.Now;
        string fmt = string.IsNullOrEmpty(format) ? "%y-%m-%d %H:%M:%S" : format;
        var sb = new StringBuilder(fmt.Length + 8);
        for (int i = 0; i < fmt.Length; i++)
        {
            if (fmt[i] != '%' || i + 1 >= fmt.Length)
            {
                sb.Append(fmt[i]);
                continue;
            }

            i++;
            switch (fmt[i])
            {
                case '%': sb.Append('%'); break;
                case 'Y': sb.Append(now.Year.ToString("D4", CultureInfo.InvariantCulture)); break;
                case 'y': sb.Append((now.Year % 100).ToString("D2", CultureInfo.InvariantCulture)); break;
                case 'm': sb.Append(now.Month.ToString("D2", CultureInfo.InvariantCulture)); break;
                case 'd': sb.Append(now.Day.ToString("D2", CultureInfo.InvariantCulture)); break;
                case 'H': sb.Append(now.Hour.ToString("D2", CultureInfo.InvariantCulture)); break;
                case 'M': sb.Append(now.Minute.ToString("D2", CultureInfo.InvariantCulture)); break;
                case 'S': sb.Append(now.Second.ToString("D2", CultureInfo.InvariantCulture)); break;
                case 'w': sb.Append(((int)now.DayOfWeek).ToString(CultureInfo.InvariantCulture)); break;
                case 'j': sb.Append(now.DayOfYear.ToString("D3", CultureInfo.InvariantCulture)); break;
                default:
                    sb.Append('%').Append(fmt[i]);
                    break;
            }
        }

        return sb.ToString();
    }

    public static DialogOption[] BuildDynamicDialogOptions(
        IReadOnlyList<string> rawOptions,
        int declaredCount,
        Func<IScriptDialogContext, string, string[], ValueTask> invokeCallback)
    {
        if (declaredCount <= 0 || rawOptions.Count == 0)
        {
            return [];
        }

        int count = Math.Min(declaredCount, rawOptions.Count);
        var built = new DialogOption[count];
        for (int i = 0; i < count; i++)
        {
            built[i] = ParseSingleDialogOption(rawOptions[i], invokeCallback);
        }

        return built;
    }

    public static DialogOption ParseSingleDialogOption(
        string optionSpec,
        Func<IScriptDialogContext, string, string[], ValueTask> invokeCallback)
    {
        int slash = optionSpec.LastIndexOf('/');
        if (slash < 0)
        {
            return new DialogOption(optionSpec, null);
        }

        string label = optionSpec[..slash];
        string rawCb = optionSpec[(slash + 1)..].Trim();
        if (rawCb.Length == 0 ||
            rawCb.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            rawCb.Equals("OnCancel", StringComparison.OrdinalIgnoreCase) ||
            rawCb.Equals("Cancel", StringComparison.OrdinalIgnoreCase))
        {
            return new DialogOption(label, null);
        }

        (string cbName, string[] cbArgs) = SplitCallbackNameAndArgs(rawCb);
        if (cbName.Length == 0)
        {
            return new DialogOption(label, null);
        }

        return new DialogOption(label, ctx => invokeCallback(ctx, cbName, cbArgs));
    }

    public static (string Name, string[] RawArgs) SplitCallbackNameAndArgs(string rawCb)
    {
        string trimmed = rawCb.Trim();
        if (!trimmed.StartsWith('#'))
        {
            return (trimmed, []);
        }

        string rest = trimmed[1..].TrimStart();
        int lParen = rest.IndexOf('(');
        int rParen = rest.LastIndexOf(')');
        if (lParen <= 0 || rParen < lParen)
        {
            return (rest, []);
        }

        string fnName = rest[..lParen].Trim();
        string inner = rest[(lParen + 1)..rParen].Trim();
        if (inner.Length == 0)
        {
            return (fnName, []);
        }

        var parts = new List<string>();
        var cur = new StringBuilder();
        char quote = '\0';
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    cur.Append(c);
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            if (c == ',')
            {
                parts.Add(cur.ToString().Trim());
                cur.Clear();
                continue;
            }

            cur.Append(c);
        }

        parts.Add(cur.ToString().Trim());
        return (fnName, parts.ToArray());
    }

    public static int ParseCallbackIntArg(string[] args, int index) =>
        index < args.Length && int.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;

    public static string ParseCallbackStrArg(string[] args, int index) =>
        index < args.Length ? args[index] : string.Empty;
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
        AppendHeader(
            sb,
            "System",
            "System.Collections.Generic",
            "System.Threading",
            "System.Threading.Tasks",
            "JXReborn.Primitives.Characters",
            "JXReborn.Primitives.Combat",
            "JXReborn.Primitives.WorldZones",
            "JXReborn.ZoneEngine.Scripting");
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
            EmitScriptMembers(sb, idx, scripts[idx], contextInterfaceName, isItemScript: false, isMonsterScript: false);
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private string BuildGeneralItemScriptClass(List<CompiledLuaScript> scripts)
    {
        var sb = new StringBuilder(scripts.Count * 1024);
        AppendHeader(
            sb,
            "System",
            "System.Collections.Generic",
            "System.Threading",
            "System.Threading.Tasks",
            "JXReborn.Primitives.Characters",
            "JXReborn.Primitives.Combat",
            "JXReborn.Primitives.WorldZones",
            "JXReborn.ZoneEngine.Scripting");
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
        AppendHeader(
            sb,
            "System",
            "System.Collections.Generic",
            "JXReborn.Primitives.Characters",
            "JXReborn.Primitives.Combat",
            "JXReborn.Primitives.WorldZones",
            "JXReborn.ZoneEngine.Scripting");
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
        var constFieldMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var constExprMap = new Dictionary<string, LuaExpression>(StringComparer.Ordinal);
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
            constExprMap[name] = init;
            if (init is LuaTableLiteralExpr tbl)
            {
                topLevelConstArrays[name] = tbl;
            }

            LuaValueType vt = script.TopLevelConstantTypes[name];
            string csType = ToCSharpType(vt);
            sb.AppendLine();
            sb.Append($"    private static readonly {csType} {fieldName} = ");
            sb.Append(EmitExpression(init, scriptIndex, script, null, constFieldMap, constExprMap, null, isMonsterScript));
            sb.AppendLine(";");
        }

        var functionsByName = script.Program.Functions.ToDictionary(static f => f.Name, StringComparer.Ordinal);
        bool needsCallbackRouter = !isMonsterScript && script.Program.Functions.Count > 1;

        if (needsCallbackRouter)
        {
            EmitScriptCallbackRouter(sb, scriptIndex, script, ctxType);
        }

        foreach (LuaFunctionDecl fn in script.Program.Functions)
        {
            bool isEntry = string.Equals(fn.Name, script.EntryFunctionName, StringComparison.Ordinal);
            bool fnAsync = script.FunctionIsAsync[fn.Name];
            LuaValueType retType = script.FunctionReturnTypes[fn.Name];
            IReadOnlyList<LuaValueType>? retTuple = script.FunctionReturnTuples is not null &&
                                                     script.FunctionReturnTuples.TryGetValue(fn.Name, out IReadOnlyList<LuaValueType>? rt)
                ? rt
                : null;
            IReadOnlyList<LuaValueType> paramTypes = script.FunctionParamTypes.GetValueOrDefault(fn.Name, []);
            string methodName = MakeFunctionMethodName(scriptIndex, fn.Name);

            var paramTypeLookup = new Dictionary<string, LuaValueType>(StringComparer.Ordinal);
            var localVarMap = new Dictionary<string, string>(StringComparer.Ordinal);

            var paramDeclParts = new List<string> { $"{ctxType} context" };
            if (!isEntry)
            {
                for (int p = 0; p < paramTypes.Count && p < fn.Parameters.Count; p++)
                {
                    string rawP = fn.Parameters[p];
                    string csParamName = $"p_{SanitizeIdentifier(rawP)}";
                    paramDeclParts.Add($"{ToCSharpType(paramTypes[p])} {csParamName}");
                    localVarMap[rawP] = csParamName;
                    paramTypeLookup[rawP] = paramTypes[p];
                }
            }

            if (!isMonsterScript)
            {
                paramDeclParts.Add("CancellationToken cancellationToken");
            }

            string paramList = string.Join(", ", paramDeclParts);
            string rawRetCsType = retTuple is not null
                ? "(" + string.Join(", ", retTuple.Select(ToCSharpType)) + ")"
                : ToCSharpType(retType);

            sb.AppendLine();
            if (isMonsterScript)
            {
                string csRet = isEntry || (retTuple is null && retType == LuaValueType.Void) ? "void" : rawRetCsType;
                sb.AppendLine($"    private static {csRet} {methodName}({paramList})");
            }
            else if (isItemScript && isEntry)
            {
                string asyncKw = fnAsync ? "async " : string.Empty;
                sb.AppendLine($"    private static {asyncKw}ValueTask<ItemUsageResult> {methodName}({paramList})");
            }
            else if (!isEntry && (retTuple is not null || retType != LuaValueType.Void) && !fnAsync)
            {
                sb.AppendLine($"    private static {rawRetCsType} {methodName}({paramList})");
            }
            else
            {
                string asyncKw = fnAsync ? "async " : string.Empty;
                string csRet = !isEntry && (retTuple is not null || retType != LuaValueType.Void)
                    ? $"ValueTask<{rawRetCsType}>"
                    : "ValueTask";
                sb.AppendLine($"    private static {asyncKw}{csRet} {methodName}({paramList})");
            }

            sb.AppendLine("    {");

            // Bind main(nItemIdx) for Item scripts
            if (isItemScript && isEntry && paramTypes.Count == 1 && fn.Parameters.Count >= 1)
            {
                string rawItemParam = fn.Parameters[0];
                string csItemParam = $"p_{SanitizeIdentifier(rawItemParam)}";
                localVarMap[rawItemParam] = csItemParam;
                paramTypeLookup[rawItemParam] = LuaValueType.Int;
                sb.AppendLine($"        int {csItemParam} = (int)context.RuntimeItemId;");
            }

            // Hoist all function-local variables cleanly at method top
            IReadOnlyDictionary<string, LuaValueType> locals = script.FunctionLocalTypes[fn.Name];
            foreach (KeyValuePair<string, LuaValueType> kv in locals.OrderBy(static k => k.Key, StringComparer.Ordinal))
            {
                if (localVarMap.ContainsKey(kv.Key))
                {
                    continue;
                }

                string csName = $"v_{SanitizeIdentifier(kv.Key)}";
                localVarMap[kv.Key] = csName;
                sb.AppendLine($"        {ToCSharpType(kv.Value)} {csName} = {DefaultValueExpr(kv.Value)};");
            }

            var fnScopeTypes = new Dictionary<string, LuaValueType>(locals, StringComparer.Ordinal);
            foreach (KeyValuePair<string, LuaValueType> pkv in paramTypeLookup)
            {
                fnScopeTypes[pkv.Key] = pkv.Value;
            }

            var fnConstArrays = new Dictionary<string, LuaTableLiteralExpr>(topLevelConstArrays, StringComparer.Ordinal);
            int tempCounter = 0;
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
                retTuple,
                isItemScript,
                isMonsterScript,
                needsCallbackRouter,
                ctxType,
                functionsByName,
                fnScopeTypes,
                constFieldMap,
                constExprMap,
                localVarMap,
                fnConstArrays,
                ref tempCounter);

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
                    if (isEntry || (retTuple is null && retType == LuaValueType.Void))
                    {
                        sb.AppendLine("        return ValueTask.CompletedTask;");
                    }
                    else
                    {
                        sb.AppendLine($"        return {DefaultReturnExpr(retType, retTuple)};");
                    }
                }
                else if (!isEntry && (retTuple is not null || retType != LuaValueType.Void))
                {
                    sb.AppendLine($"        return {DefaultReturnExpr(retType, retTuple)};");
                }
            }

            sb.AppendLine("    }");
        }
    }

    private static void EmitScriptCallbackRouter(
        StringBuilder sb,
        int scriptIndex,
        CompiledLuaScript script,
        string ctxType)
    {
        string routerName = $"S{scriptIndex}_InvokeCallback";
        string castCtx = ctxType == "IScriptDialogContext" ? "rawCtx" : $"({ctxType})rawCtx";

        sb.AppendLine();
        sb.AppendLine($"    private static ValueTask {routerName}(IScriptDialogContext rawCtx, string cbName, string[] cbArgs)");
        sb.AppendLine("    {");
        sb.AppendLine("        switch (cbName)");
        sb.AppendLine("        {");
        foreach (LuaFunctionDecl fn in script.Program.Functions)
        {
            if (string.Equals(fn.Name, script.EntryFunctionName, StringComparison.Ordinal))
            {
                continue;
            }

            IReadOnlyList<LuaValueType> pTypes = script.FunctionParamTypes.GetValueOrDefault(fn.Name, []);
            string methodName = MakeFunctionMethodName(scriptIndex, fn.Name);
            var callArgs = new List<string> { castCtx };
            for (int p = 0; p < pTypes.Count; p++)
            {
                callArgs.Add(pTypes[p] == LuaValueType.String
                    ? $"GeneratedLuaRuntime.ParseCallbackStrArg(cbArgs, {p})"
                    : $"GeneratedLuaRuntime.ParseCallbackIntArg(cbArgs, {p})");
            }

            callArgs.Add("CancellationToken.None");
            string callStr = $"{methodName}({string.Join(", ", callArgs)})";

            bool fnAsync = script.FunctionIsAsync[fn.Name];
            bool hasNonVoidReturn = (script.FunctionReturnTuples is not null && script.FunctionReturnTuples.ContainsKey(fn.Name)) ||
                                    script.FunctionReturnTypes[fn.Name] != LuaValueType.Void;

            sb.Append("            case ").Append(ToEscapedStringLiteral(fn.Name)).Append(": ");
            if (!fnAsync && hasNonVoidReturn)
            {
                sb.AppendLine($"_ = {callStr}; return ValueTask.CompletedTask;");
            }
            else if (fnAsync && hasNonVoidReturn)
            {
                sb.AppendLine($"return IgnoreResultAsync({callStr});");
            }
            else
            {
                sb.AppendLine($"return {callStr};");
            }
        }

        sb.AppendLine("            default: return ValueTask.CompletedTask;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");

        if (script.Program.Functions.Any(f =>
            !string.Equals(f.Name, script.EntryFunctionName, StringComparison.Ordinal) &&
            script.FunctionIsAsync[f.Name] &&
            ((script.FunctionReturnTuples is not null && script.FunctionReturnTuples.ContainsKey(f.Name)) ||
             script.FunctionReturnTypes[f.Name] != LuaValueType.Void)))
        {
            sb.AppendLine();
            sb.AppendLine($"    private static async ValueTask IgnoreResultAsync<T>(ValueTask<T> task) => _ = await task;");
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
        IReadOnlyList<LuaValueType>? retTuple,
        bool isItemScript,
        bool isMonsterScript,
        bool hasRouter,
        string ctxType,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        IReadOnlyDictionary<string, LuaValueType> fnScopeTypes,
        IReadOnlyDictionary<string, string> constFieldMap,
        IReadOnlyDictionary<string, LuaExpression> constExprMap,
        IReadOnlyDictionary<string, string> localVarMap,
        Dictionary<string, LuaTableLiteralExpr> constArrays,
        ref int tempCounter)
    {
        for (int i = 0; i < statements.Count; i++)
        {
            LuaStatement stmt = statements[i];

            if (!isMonsterScript &&
                i + 1 < statements.Count &&
                stmt is LuaCallStmt { Call: { CalleeName: "SetPos", Arguments.Count: 2 } posCall } &&
                statements[i + 1] is LuaCallStmt { Call: { CalleeName: "SetFightState", Arguments.Count: 1 } fightCall })
            {
                string fmExpr = EmitIntExpr(fightCall.Arguments[0], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                string xExpr = EmitIntExpr(posCall.Arguments[0], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                string yExpr = EmitIntExpr(posCall.Arguments[1], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                sb.AppendLine($"{indent}context.SetFightMode((byte)(({fmExpr}) != 0 ? 1 : 0));");
                sb.AppendLine($"{indent}_ = await context.TeleportPlayerAsync(context.MapId, ({xExpr}) * 32, ({yExpr}) * 32, context.CurrentFightMode, cancellationToken);");
                i++;
                continue;
            }

            switch (stmt)
            {
                case LuaLocalDeclStmt ld:
                {
                    if (ld.Initializer is null)
                    {
                        constArrays.Remove(ld.Name);
                        break;
                    }

                    if (ld.Name == "_")
                    {
                        string discarded = EmitExpression(ld.Initializer, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                        sb.AppendLine($"{indent}_ = {discarded};");
                        break;
                    }

                    string lhs = localVarMap[ld.Name];
                    LuaValueType lhsType = fnScopeTypes[ld.Name];
                    string rhs = ld.Initializer is LuaTableLiteralExpr { Elements.Count: 0 }
                        ? DefaultValueExpr(lhsType)
                        : EmitAssignableExpression(ld.Initializer, lhsType, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    sb.AppendLine($"{indent}{lhs} = {rhs};");
                    if (ld.Initializer is LuaTableLiteralExpr { Elements.Count: > 0 } tbl)
                    {
                        constArrays[ld.Name] = tbl;
                    }
                    else
                    {
                        constArrays.Remove(ld.Name);
                    }

                    break;
                }

                case LuaMultiLocalDeclStmt mld:
                    EmitMultiAssign(sb, mld.Names, mld.Initializers, indent, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, constArrays, isMonsterScript, ref tempCounter);
                    break;

                case LuaAssignStmt asg:
                {
                    if (asg.TargetName == "_")
                    {
                        string discarded = EmitExpression(asg.Value, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                        sb.AppendLine($"{indent}_ = {discarded};");
                        break;
                    }

                    string lhs = localVarMap[asg.TargetName];
                    LuaValueType lhsType = fnScopeTypes[asg.TargetName];
                    string rhs = asg.Value is LuaTableLiteralExpr { Elements.Count: 0 }
                        ? DefaultValueExpr(lhsType)
                        : EmitAssignableExpression(asg.Value, lhsType, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    sb.AppendLine($"{indent}{lhs} = {rhs};");
                    if (asg.Value is LuaTableLiteralExpr { Elements.Count: > 0 } tbl)
                    {
                        constArrays[asg.TargetName] = tbl;
                    }
                    else
                    {
                        constArrays.Remove(asg.TargetName);
                    }

                    break;
                }

                case LuaMultiAssignStmt masg:
                    EmitMultiAssign(sb, masg.TargetNames, masg.Values, indent, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, constArrays, isMonsterScript, ref tempCounter);
                    break;

                case LuaIndexAssignStmt iasg:
                {
                    string listVar = localVarMap[iasg.TargetName];
                    LuaValueType listType = fnScopeTypes[iasg.TargetName];
                    string idxExpr = EmitIntExpr(iasg.Index, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    if (listType == LuaValueType.StringList)
                    {
                        string valStr = EmitAsStringExpression(iasg.Value, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                        sb.AppendLine($"{indent}GeneratedLuaRuntime.SetAt1Based({listVar}, {idxExpr}, {valStr}, string.Empty);");
                    }
                    else
                    {
                        string valInt = EmitIntExpr(iasg.Value, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                        sb.AppendLine($"{indent}GeneratedLuaRuntime.SetAt1Based({listVar}, {idxExpr}, {valInt}, 0);");
                    }

                    constArrays.Remove(iasg.TargetName);
                    break;
                }

                case LuaIfStmt ifStmt:
                {
                    string cond = EmitBoolCondition(ifStmt.Condition, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    sb.AppendLine($"{indent}if ({cond})");
                    sb.AppendLine($"{indent}{{");
                    bool thenRet = EmitBlock(sb, ifStmt.ThenBody, indent + "    ", scriptIndex, script, currentFn, isEntry, fnAsync, retType, retTuple, isItemScript, isMonsterScript, hasRouter, ctxType, functionsByName, fnScopeTypes, constFieldMap, constExprMap, localVarMap, new Dictionary<string, LuaTableLiteralExpr>(constArrays, StringComparer.Ordinal), ref tempCounter);
                    sb.AppendLine($"{indent}}}");

                    bool allBranchesReturn = thenRet;
                    foreach (LuaElseIfBranch ei in ifStmt.ElseIfBranches)
                    {
                        string eiCond = EmitBoolCondition(ei.Condition, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                        sb.AppendLine($"{indent}else if ({eiCond})");
                        sb.AppendLine($"{indent}{{");
                        bool eiRet = EmitBlock(sb, ei.Body, indent + "    ", scriptIndex, script, currentFn, isEntry, fnAsync, retType, retTuple, isItemScript, isMonsterScript, hasRouter, ctxType, functionsByName, fnScopeTypes, constFieldMap, constExprMap, localVarMap, new Dictionary<string, LuaTableLiteralExpr>(constArrays, StringComparer.Ordinal), ref tempCounter);
                        sb.AppendLine($"{indent}}}");
                        allBranchesReturn &= eiRet;
                    }

                    if (ifStmt.ElseBody.Count > 0)
                    {
                        sb.AppendLine($"{indent}else");
                        sb.AppendLine($"{indent}{{");
                        bool elseRet = EmitBlock(sb, ifStmt.ElseBody, indent + "    ", scriptIndex, script, currentFn, isEntry, fnAsync, retType, retTuple, isItemScript, isMonsterScript, hasRouter, ctxType, functionsByName, fnScopeTypes, constFieldMap, constExprMap, localVarMap, new Dictionary<string, LuaTableLiteralExpr>(constArrays, StringComparer.Ordinal), ref tempCounter);
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

                case LuaNumericForStmt forStmt:
                {
                    int tId = tempCounter++;
                    string startVar = $"__forStart{tId}";
                    string limitVar = $"__forLimit{tId}";
                    string stepVar = $"__forStep{tId}";
                    string iterVar = localVarMap[forStmt.IteratorName];
                    LuaValueType iterType = fnScopeTypes[forStmt.IteratorName];

                    string startExpr = EmitIntExpr(forStmt.Start, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    string limitExpr = EmitIntExpr(forStmt.Limit, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    string stepExpr = forStmt.Step is not null
                        ? EmitIntExpr(forStmt.Step, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript)
                        : "1";

                    sb.AppendLine($"{indent}{{");
                    sb.AppendLine($"{indent}    int {startVar} = {startExpr};");
                    sb.AppendLine($"{indent}    int {limitVar} = {limitExpr};");
                    sb.AppendLine($"{indent}    int {stepVar} = {stepExpr};");
                    sb.AppendLine($"{indent}    if ({stepVar} != 0)");
                    sb.AppendLine($"{indent}    {{");
                    string idxLoop = $"__forIdx{tId}";
                    sb.AppendLine($"{indent}        for (int {idxLoop} = {startVar}; {stepVar} > 0 ? {idxLoop} <= {limitVar} : {idxLoop} >= {limitVar}; {idxLoop} += {stepVar})");
                    sb.AppendLine($"{indent}        {{");
                    sb.AppendLine($"{indent}            {iterVar} = {(iterType == LuaValueType.NullableInt ? $"(int?){idxLoop}" : idxLoop)};");
                    _ = EmitBlock(sb, forStmt.Body, indent + "            ", scriptIndex, script, currentFn, isEntry, fnAsync, retType, retTuple, isItemScript, isMonsterScript, hasRouter, ctxType, functionsByName, fnScopeTypes, constFieldMap, constExprMap, localVarMap, new Dictionary<string, LuaTableLiteralExpr>(constArrays, StringComparer.Ordinal), ref tempCounter);
                    sb.AppendLine($"{indent}        }}");
                    sb.AppendLine($"{indent}    }}");
                    sb.AppendLine($"{indent}}}");
                    break;
                }

                case LuaWhileStmt whileStmt:
                {
                    int tId = tempCounter++;
                    string guardVar = $"__whileGuard{tId}";
                    string wCond = EmitBoolCondition(whileStmt.Condition, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    sb.AppendLine($"{indent}int {guardVar} = 0;");
                    sb.AppendLine($"{indent}while ({wCond})");
                    sb.AppendLine($"{indent}{{");
                    sb.AppendLine($"{indent}    if (++{guardVar} > 10000) break;");
                    _ = EmitBlock(sb, whileStmt.Body, indent + "    ", scriptIndex, script, currentFn, isEntry, fnAsync, retType, retTuple, isItemScript, isMonsterScript, hasRouter, ctxType, functionsByName, fnScopeTypes, constFieldMap, constExprMap, localVarMap, new Dictionary<string, LuaTableLiteralExpr>(constArrays, StringComparer.Ordinal), ref tempCounter);
                    sb.AppendLine($"{indent}}}");
                    break;
                }

                case LuaBreakStmt:
                    sb.AppendLine($"{indent}break;");
                    return false;

                case LuaDoBlockStmt doStmt:
                    sb.AppendLine($"{indent}{{");
                    bool doRet = EmitBlock(sb, doStmt.Body, indent + "    ", scriptIndex, script, currentFn, isEntry, fnAsync, retType, retTuple, isItemScript, isMonsterScript, hasRouter, ctxType, functionsByName, fnScopeTypes, constFieldMap, constExprMap, localVarMap, new Dictionary<string, LuaTableLiteralExpr>(constArrays, StringComparer.Ordinal), ref tempCounter);
                    sb.AppendLine($"{indent}}}");
                    if (doRet)
                    {
                        return true;
                    }

                    break;

                case LuaCallStmt cs:
                    EmitCallStatement(sb, cs.Call, indent, scriptIndex, script, isItemScript, isMonsterScript, hasRouter, functionsByName, fnScopeTypes, constFieldMap, constExprMap, localVarMap, constArrays);
                    break;

                case LuaReturnStmt ret:
                    EmitReturnStatement(sb, ret, indent, scriptIndex, script, isEntry, fnAsync, retType, retTuple, isItemScript, isMonsterScript, fnScopeTypes, constFieldMap, constExprMap, localVarMap);
                    return true;
            }
        }

        return false;
    }

    private void EmitMultiAssign(
        StringBuilder sb,
        IReadOnlyList<string> targetNames,
        IReadOnlyList<LuaExpression> values,
        string indent,
        int scriptIndex,
        CompiledLuaScript script,
        IReadOnlyDictionary<string, LuaValueType> fnScopeTypes,
        IReadOnlyDictionary<string, string> constFieldMap,
        IReadOnlyDictionary<string, LuaExpression> constExprMap,
        IReadOnlyDictionary<string, string> localVarMap,
        Dictionary<string, LuaTableLiteralExpr> constArrays,
        bool isMonsterScript,
        ref int tempCounter)
    {
        if (values.Count == 0)
        {
            return;
        }

        foreach (string n in targetNames)
        {
            if (n != "_") constArrays.Remove(n);
        }

        if (values.Count == 1 && values[0] is LuaCallExpr singleCall)
        {
            if (singleCall.CalleeName == "GetWorldPos")
            {
                string[] exprs = ["(int)context.MapId", "context.CurrentMpsX / 32", "context.CurrentMpsY / 32"];
                for (int i = 0; i < targetNames.Count && i < exprs.Length; i++)
                {
                    if (targetNames[i] == "_") continue;
                    sb.AppendLine($"{indent}{localVarMap[targetNames[i]]} = {exprs[i]};");
                }

                return;
            }

            if (singleCall.CalleeName == "GetPlayerRev")
            {
                string[] exprs = ["(int)context.RevivalMapId", "context.RevivalPointId"];
                for (int i = 0; i < targetNames.Count && i < exprs.Length; i++)
                {
                    if (targetNames[i] == "_") continue;
                    sb.AppendLine($"{indent}{localVarMap[targetNames[i]]} = {exprs[i]};");
                }

                return;
            }

            if (script.FunctionReturnTuples is not null &&
                script.FunctionReturnTuples.TryGetValue(singleCall.CalleeName, out IReadOnlyList<LuaValueType>? tupTypes))
            {
                int tId = tempCounter++;
                string tupVar = $"__tup{tId}";
                string callExpr = EmitCallExpression(singleCall, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                sb.AppendLine($"{indent}var {tupVar} = {callExpr};");
                for (int i = 0; i < targetNames.Count && i < tupTypes.Count; i++)
                {
                    if (targetNames[i] == "_") continue;
                    sb.AppendLine($"{indent}{localVarMap[targetNames[i]]} = {tupVar}.Item{i + 1};");
                }

                return;
            }
        }

        // Evaluate all RHS expressions into temporaries first (exact Lua simultaneous assignment!)
        int baseId = tempCounter++;
        for (int i = 0; i < values.Count; i++)
        {
            if (i < targetNames.Count && targetNames[i] != "_")
            {
                LuaValueType targetType = fnScopeTypes[targetNames[i]];
                string rhs = EmitAssignableExpression(values[i], targetType, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                sb.AppendLine($"{indent}{ToCSharpType(targetType)} __rhs{baseId}_{i} = {rhs};");
            }
            else
            {
                string rhs = EmitExpression(values[i], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                sb.AppendLine($"{indent}_ = {rhs};");
            }
        }

        for (int i = 0; i < targetNames.Count && i < values.Count; i++)
        {
            if (targetNames[i] == "_") continue;
            sb.AppendLine($"{indent}{localVarMap[targetNames[i]]} = __rhs{baseId}_{i};");
        }
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
        IReadOnlyList<LuaValueType>? retTuple,
        bool isItemScript,
        bool isMonsterScript,
        IReadOnlyDictionary<string, LuaValueType> fnScopeTypes,
        IReadOnlyDictionary<string, string> constFieldMap,
        IReadOnlyDictionary<string, LuaExpression> constExprMap,
        IReadOnlyDictionary<string, string> localVarMap)
    {
        if (isItemScript && isEntry)
        {
            if (ret.Value is null or LuaNilExpr)
            {
                sb.AppendLine(fnAsync ? $"{indent}return ItemUsageResult.ConsumeOne;" : $"{indent}return ValueTask.FromResult(ItemUsageResult.ConsumeOne);");
                return;
            }

            if (LuaScriptSemanticValidator.TryEvalConstantInt(ret.Value, constExprMap, out int v))
            {
                string res = v != 0 ? "ItemUsageResult.KeepItem" : "ItemUsageResult.ConsumeOne";
                sb.AppendLine(fnAsync ? $"{indent}return {res};" : $"{indent}return ValueTask.FromResult({res});");
                return;
            }

            string dynInt = EmitIntExpr(ret.Value, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
            string dynRes = $"(({dynInt}) != 0 ? ItemUsageResult.KeepItem : ItemUsageResult.ConsumeOne)";
            sb.AppendLine(fnAsync ? $"{indent}return {dynRes};" : $"{indent}return ValueTask.FromResult({dynRes});");
            return;
        }

        if (!isEntry && retTuple is not null && ret.Value is not null && ret.ExtraValues is { Count: > 0 })
        {
            var elems = new List<string>(1 + ret.ExtraValues.Count)
            {
                EmitAssignableExpression(ret.Value, retTuple[0], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript),
            };
            for (int i = 0; i < ret.ExtraValues.Count; i++)
            {
                elems.Add(EmitAssignableExpression(ret.ExtraValues[i], retTuple[i + 1], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript));
            }

            sb.AppendLine($"{indent}return ({string.Join(", ", elems)});");
            return;
        }

        if (isMonsterScript)
        {
            if (!isEntry && retType != LuaValueType.Void && ret.Value is not null)
            {
                string val = EmitAssignableExpression(ret.Value, retType, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                sb.AppendLine($"{indent}return {val};");
            }
            else if (!isEntry && retType != LuaValueType.Void)
            {
                sb.AppendLine($"{indent}return {DefaultValueExpr(retType)};");
            }
            else
            {
                sb.AppendLine($"{indent}return;");
            }

            return;
        }

        if (!isEntry && retType != LuaValueType.Void)
        {
            string val = ret.Value is not null
                ? EmitAssignableExpression(ret.Value, retType, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript)
                : DefaultValueExpr(retType);
            sb.AppendLine($"{indent}return {val};");
            return;
        }

        if (isEntry && ret.Value is not null && ret.Value is not LuaNilExpr && !LuaScriptSemanticValidator.TryEvalConstantInt(ret.Value, constExprMap, out _))
        {
            string sideEffectVal = EmitExpression(ret.Value, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
            sb.AppendLine($"{indent}_ = {sideEffectVal};");
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
        bool hasRouter,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        IReadOnlyDictionary<string, LuaValueType> fnScopeTypes,
        IReadOnlyDictionary<string, string> constFieldMap,
        IReadOnlyDictionary<string, LuaExpression> constExprMap,
        IReadOnlyDictionary<string, string> localVarMap,
        IReadOnlyDictionary<string, LuaTableLiteralExpr> constArrays)
    {
        string api = call.CalleeName;
        IReadOnlyList<LuaExpression> args = call.Arguments;

        string E(int idx) => EmitExpression(args[idx], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
        string EInt(int idx) => EmitIntExpr(args[idx], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
        string EStr(int idx) => EmitAsStringExpression(args[idx], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);

        if (functionsByName.ContainsKey(api))
        {
            string invokeExpr = BuildHelperInvocationExpr(call, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
            if (isMonsterScript)
            {
                sb.AppendLine($"{indent}{invokeExpr};");
            }
            else if (script.FunctionIsAsync[api])
            {
                sb.AppendLine($"{indent}await {invokeExpr};");
            }
            else
            {
                sb.AppendLine($"{indent}_ = {invokeExpr};");
            }

            return;
        }

        switch (api)
        {
            case "SetTask":
                sb.AppendLine($"{indent}context.SetTask((ushort)({EInt(0)}), {EInt(1)});");
                break;
            case "SetTaskTemp":
                sb.AppendLine($"{indent}context.SetTempTask((ushort)({EInt(0)}), {EInt(1)});");
                break;
            case "SetGlbValue":
                sb.AppendLine($"{indent}context.SetGlobalValue({EInt(0)}, {EInt(1)});");
                break;
            case "SyncTaskValue":
                sb.AppendLine($"{indent}_ = {EInt(0)};");
                break;
            case "WriteLog":
            case "WriteGoldLog":
            case "UseSilver":
            case "SaveNow":
            case "print":
                for (int i = 0; i < args.Count; i++)
                {
                    sb.AppendLine($"{indent}_ = {E(i)};");
                }

                break;
            case "OpenStoreBox":
                sb.AppendLine($"{indent}context.UnlockExpandableBox((byte)({EInt(0)}));");
                break;
            case "Msg2Player":
                sb.AppendLine($"{indent}context.SendSystemMessage({EStr(0)});");
                break;
            case "AddNote":
                sb.AppendLine($"{indent}context.AddNote({EStr(0)});");
                break;
            case "Msg2SubWorld":
            case "AddGlobalNews":
                sb.AppendLine($"{indent}context.BroadcastGlobalNews({EStr(0)});");
                break;
            case "AddGlobalCountNews":
                sb.AppendLine($"{indent}_ = {EInt(1)}; context.BroadcastGlobalNews({EStr(0)});");
                break;
            case "Msg2Team":
                sb.AppendLine($"{indent}context.BroadcastToTeam({EStr(0)});");
                break;
            case "Earn":
                sb.AppendLine($"{indent}context.GrantMoney({EInt(0)});");
                break;
            case "Pay":
                sb.AppendLine($"{indent}_ = context.TryPayMoney({EInt(0)});");
                break;
            case "AddOwnExp":
                sb.AppendLine($"{indent}context.GrantExperience({EInt(0)});");
                break;
            case "AddRepute":
                sb.AppendLine($"{indent}context.AddRepute({EInt(0)});");
                break;
            case "SetPropState":
                if (args.Count == 0)
                {
                    sb.AppendLine($"{indent}context.SetPropState(1);");
                }
                else
                {
                    sb.AppendLine($"{indent}context.SetPropState((byte)(({EInt(0)}) != 0 ? 1 : 0));");
                }

                break;
            case "SetRank":
                sb.AppendLine($"{indent}context.SetRank((byte)({EInt(0)}));");
                break;
            case "SetCamp":
                sb.AppendLine($"{indent}context.SetBaseCamp((EntityCamp)({EInt(0)}));");
                break;
            case "SetCurCamp":
                sb.AppendLine($"{indent}context.SetCurrentCamp((EntityCamp)({EInt(0)}));");
                break;
            case "RestoreCamp":
                sb.AppendLine($"{indent}context.RestoreCurrentCamp();");
                break;
            case "SetPK":
                sb.AppendLine($"{indent}context.SetPkValue((sbyte)({EInt(0)}));");
                break;
            case "DelMagic":
                sb.AppendLine($"{indent}_ = context.TryRemoveSkill({EInt(0)});");
                break;
            case "tinsert":
            {
                LuaValueType lt = InferExpressionType(args[0], script, fnScopeTypes);
                if (args.Count == 2)
                {
                    string valExpr = lt == LuaValueType.StringList ? EStr(1) : EInt(1);
                    sb.AppendLine($"{indent}({E(0)}).Add({valExpr});");
                }
                else
                {
                    string valExpr = lt == LuaValueType.StringList ? EStr(2) : EInt(2);
                    sb.AppendLine($"{indent}GeneratedLuaRuntime.InsertAt1Based({E(0)}, {EInt(1)}, {valExpr});");
                }

                break;
            }
            case "AddEventItem":
            {
                string code = ResolveEventItemCode(args[0], constExprMap);
                sb.AppendLine($"{indent}_ = context.TryGrantItemByCode({ToEscapedStringLiteral(code)});");
                break;
            }
            case "DelItem":
            {
                string code = ResolveEventItemCode(args[0], constExprMap);
                sb.AppendLine($"{indent}_ = context.TryConsumeItemByCode({ToEscapedStringLiteral(code)});");
                break;
            }
            case "AddItem":
            case "AddItemNoStack":
            case "AddStackItem":
            case "DelCommonItem":
            case "ConsumeEquiproomItem":
                sb.AppendLine($"{indent}_ = {EmitCallExpression(call, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript)};");
                break;
            case "SetFightState":
                sb.AppendLine($"{indent}context.SetFightMode((byte)(({EInt(0)}) != 0 ? 1 : 0));");
                break;
            case "SetProtectTime":
                sb.AppendLine($"{indent}context.SetProtectTime({EInt(0)});");
                break;
            case "AddSkillState":
                sb.AppendLine($"{indent}context.AddSkillState({EInt(0)}, {EInt(1)}, {EInt(2)}, {EInt(3)});");
                break;
            case "AddStation":
                sb.AppendLine($"{indent}context.UnlockStation((ushort)({EInt(0)}));");
                break;
            case "AddTermini":
                sb.AppendLine($"{indent}context.RecordWayPoint({EInt(0)});");
                break;
            case "SetRevPos":
                if (args.Count == 1)
                {
                    sb.AppendLine($"{indent}context.SetRevivalPoint(context.MapId, {EInt(0)});");
                }
                else
                {
                    sb.AppendLine($"{indent}context.SetRevivalPoint((ushort)({EInt(0)}), {EInt(1)});");
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
                    ids.Add(EInt(i));
                }

                sb.AppendLine($"{indent}context.ShowLadder([{string.Join(", ", ids)}]);");
                break;
            }
            case "Sale":
                if (args.Count == 1)
                {
                    sb.AppendLine($"{indent}context.OpenShop({EInt(0)});");
                }
                else if (args.Count == 2)
                {
                    sb.AppendLine($"{indent}context.OpenShop({EInt(0)}, (byte)({EInt(1)}));");
                }
                else
                {
                    sb.AppendLine($"{indent}context.OpenShop({EInt(0)}, (byte)({EInt(1)}), {EInt(2)});");
                }

                break;
            case "AddMagic":
                if (args.Count == 1)
                {
                    sb.AppendLine($"{indent}_ = context.TryLearnSkill({EInt(0)}, 1);");
                }
                else
                {
                    sb.AppendLine($"{indent}_ = context.TryLearnSkill({EInt(0)}, (ushort)({EInt(1)}));");
                }

                break;
            case "AddProp":
                sb.AppendLine($"{indent}context.AddBonusAttributePoints({EInt(0)});");
                break;
            case "AddMagicPoint":
                sb.AppendLine($"{indent}context.AddBonusSkillPoints({EInt(0)});");
                break;
            case "AskClientForNumber":
            {
                string cbName = ((LuaStringExpr)args[0]).DecodedUtf8.Trim();
                string methodName = MakeFunctionMethodName(scriptIndex, cbName);
                string castCtx = script.Kind == GeneratedScriptKind.Npc ? "ctx" : $"({ResolveContextInterfaceName(script.Kind)})ctx";
                bool cbAsync = script.FunctionIsAsync[cbName];
                bool cbHasRet = script.FunctionReturnTypes[cbName] != LuaValueType.Void;
                string lambda = !cbAsync && cbHasRet
                    ? $"static (ctx, num) => {{ _ = {methodName}({castCtx}, num, CancellationToken.None); return ValueTask.CompletedTask; }}"
                    : cbAsync && cbHasRet
                        ? $"static async (ctx, num) => {{ _ = await {methodName}({castCtx}, num, CancellationToken.None); }}"
                        : $"static (ctx, num) => {methodName}({castCtx}, num, CancellationToken.None)";
                sb.AppendLine($"{indent}context.AskClientForNumber({EStr(3)}, {EInt(1)}, {EInt(2)}, {lambda});");
                break;
            }
            case "NewWorld":
                sb.AppendLine($"{indent}_ = await context.TeleportPlayerAsync((ushort)({EInt(0)}), ({EInt(1)}) * 32, ({EInt(2)}) * 32, context.CurrentFightMode, cancellationToken);");
                break;
            case "SetPos":
                sb.AppendLine($"{indent}_ = await context.TeleportPlayerAsync(context.MapId, ({EInt(0)}) * 32, ({EInt(1)}) * 32, context.CurrentFightMode, cancellationToken);");
                break;
            case "ReturnFromPortal":
                sb.AppendLine($"{indent}_ = await context.TryReturnFromPortalAsync(cancellationToken);");
                break;
            case "Talk":
            {
                string cbDelegate = "null";
                if (args[1] is LuaStringExpr cbStr)
                {
                    string cbName = cbStr.DecodedUtf8.Trim();
                    if (cbName.Length > 0 && functionsByName.TryGetValue(cbName, out LuaFunctionDecl? cbFn) && cbFn.Body.Count > 0)
                    {
                        cbDelegate = BuildZeroArgCallbackDelegateExpr(scriptIndex, script, cbName);
                    }
                }

                LuaValueType firstPageType = InferExpressionType(args[2], script, fnScopeTypes);
                if (firstPageType is LuaValueType.Int or LuaValueType.NullableInt)
                {
                    var intPages = new List<string>(args.Count - 2);
                    for (int i = 2; i < args.Count; i++)
                    {
                        intPages.Add(EInt(i));
                    }

                    sb.AppendLine($"{indent}context.TalkByStringIds([{string.Join(", ", intPages)}], {cbDelegate});");
                    break;
                }

                var pages = new List<string>(args.Count - 2);
                for (int i = 2; i < args.Count; i++)
                {
                    pages.Add(EStr(i));
                }

                sb.AppendLine($"{indent}context.Talk([{string.Join(", ", pages)}], {cbDelegate});");
                break;
            }
            case "Say":
            case "Describe":
            {
                LuaValueType promptType = InferExpressionType(args[0], script, fnScopeTypes);
                bool isSayByStringId = api == "Say" && promptType is LuaValueType.Int or LuaValueType.NullableInt;
                string hostMethod = isSayByStringId ? "SayByStringId" : (api == "Describe" ? "Describe" : "Say");
                string prompt = isSayByStringId ? EInt(0) : EStr(0);
                if (args.Count == 2)
                {
                    sb.AppendLine($"{indent}context.{hostMethod}({prompt});");
                    break;
                }

                string routerRef = hasRouter ? $"S{scriptIndex}_InvokeCallback" : "static (_, _, _) => ValueTask.CompletedTask";

                // 3-arg dynamic StringList or non-literal StringArray
                if (args.Count == 3)
                {
                    LuaValueType tabType = InferExpressionType(args[2], script, fnScopeTypes);
                    if (tabType is LuaValueType.StringList or LuaValueType.StringArray)
                    {
                        if (args[2] is LuaIdentifierExpr tabIdent &&
                            constArrays.TryGetValue(tabIdent.Name, out LuaTableLiteralExpr? tabLit) &&
                            tabLit.Elements.All(static el => LuaScriptSemanticValidator.TryEvalConstantString(el, out _)))
                        {
                            // Pure constant table literal -> emit inline zero-overhead DialogOptions
                            var staticOpts = new List<string>(tabLit.Elements.Count);
                            foreach (LuaExpression el in tabLit.Elements)
                            {
                                _ = LuaScriptSemanticValidator.TryEvalConstantString(el, out string? s);
                                staticOpts.Add(EmitConstantDialogOptionExpr(s ?? string.Empty, scriptIndex, script, functionsByName, routerRef));
                            }

                            sb.AppendLine($"{indent}context.{hostMethod}({prompt}, {string.Join(", ", staticOpts)});");
                            break;
                        }

                        sb.AppendLine($"{indent}context.{hostMethod}({prompt}, GeneratedLuaRuntime.BuildDynamicDialogOptions({E(2)}, {EInt(1)}, {routerRef}));");
                        break;
                    }
                }

                int maxOptArgEnd = args.Count;
                if (LuaScriptSemanticValidator.TryEvalConstantInt(args[1], constExprMap, out int declaredOptCount) &&
                    declaredOptCount > 0 &&
                    2 + declaredOptCount < args.Count)
                {
                    maxOptArgEnd = 2 + declaredOptCount;
                }

                var inlineOpts = new List<string>(maxOptArgEnd - 2);
                for (int i = 2; i < maxOptArgEnd; i++)
                {
                    if (LuaScriptSemanticValidator.TryEvalConstantString(args[i], out string? constOpt) && constOpt is not null)
                    {
                        inlineOpts.Add(EmitConstantDialogOptionExpr(constOpt, scriptIndex, script, functionsByName, routerRef));
                    }
                    else
                    {
                        inlineOpts.Add($"GeneratedLuaRuntime.ParseSingleDialogOption({EStr(i)}, {routerRef})");
                    }
                }

                sb.AppendLine($"{indent}context.{hostMethod}({prompt}, {string.Join(", ", inlineOpts)});");
                break;
            }
        }
    }

    private static string EmitConstantDialogOptionExpr(
        string optSpec,
        int scriptIndex,
        CompiledLuaScript script,
        IReadOnlyDictionary<string, LuaFunctionDecl> functionsByName,
        string routerRef)
    {
        int slash = optSpec.LastIndexOf('/');
        string label = slash >= 0 ? optSpec[..slash] : optSpec;
        string rawCb = slash >= 0 ? optSpec[(slash + 1)..].Trim() : string.Empty;
        if (rawCb.Length == 0 ||
            rawCb.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            rawCb.Equals("OnCancel", StringComparison.OrdinalIgnoreCase) ||
            rawCb.Equals("Cancel", StringComparison.OrdinalIgnoreCase))
        {
            return $"new DialogOption({ToEscapedStringLiteral(label)}, null)";
        }

        if (rawCb.StartsWith('#'))
        {
            return $"GeneratedLuaRuntime.ParseSingleDialogOption({ToEscapedStringLiteral(optSpec)}, {routerRef})";
        }

        if (functionsByName.TryGetValue(rawCb, out LuaFunctionDecl? cbFn) && cbFn.Body.Count > 0 && cbFn.Parameters.Count == 0)
        {
            string cbExpr = BuildZeroArgCallbackDelegateExpr(scriptIndex, script, rawCb);
            return $"new DialogOption({ToEscapedStringLiteral(label)}, {cbExpr})";
        }

        return $"new DialogOption({ToEscapedStringLiteral(label)}, null)";
    }

    private static string ResolveContextInterfaceName(GeneratedScriptKind kind) =>
        kind switch
        {
            GeneratedScriptKind.Trap => "IScriptTrapContext",
            GeneratedScriptKind.Object => "IScriptObjectContext",
            GeneratedScriptKind.Item => "IScriptItemContext",
            GeneratedScriptKind.Monster => "IScriptCombatContext",
            _ => "IScriptDialogContext",
        };

    private static string BuildZeroArgCallbackDelegateExpr(int scriptIndex, CompiledLuaScript script, string cbName)
    {
        string methodName = MakeFunctionMethodName(scriptIndex, cbName);
        string iface = ResolveContextInterfaceName(script.Kind);
        string ctxArg = iface == "IScriptDialogContext" ? "ctx" : $"({iface})ctx";
        bool cbAsync = script.FunctionIsAsync[cbName];
        bool cbHasRet = (script.FunctionReturnTuples is not null && script.FunctionReturnTuples.ContainsKey(cbName)) ||
                        script.FunctionReturnTypes[cbName] != LuaValueType.Void;

        if (!cbAsync && cbHasRet)
        {
            return $"static ctx => {{ _ = {methodName}({ctxArg}, CancellationToken.None); return ValueTask.CompletedTask; }}";
        }

        if (cbAsync && cbHasRet)
        {
            return $"static async ctx => {{ _ = await {methodName}({ctxArg}, CancellationToken.None); }}";
        }

        return $"static ctx => {methodName}({ctxArg}, CancellationToken.None)";
    }

    private string BuildHelperInvocationExpr(
        LuaCallExpr call,
        int scriptIndex,
        CompiledLuaScript script,
        IReadOnlyDictionary<string, LuaValueType>? fnScopeTypes,
        IReadOnlyDictionary<string, string> constFieldMap,
        IReadOnlyDictionary<string, LuaExpression> constExprMap,
        IReadOnlyDictionary<string, string>? localVarMap,
        bool isMonsterScript)
    {
        string methodName = MakeFunctionMethodName(scriptIndex, call.CalleeName);
        IReadOnlyList<LuaValueType> paramTypes = script.FunctionParamTypes.GetValueOrDefault(call.CalleeName, []);
        var parts = new List<string>(call.Arguments.Count + 2) { "context" };
        for (int i = 0; i < call.Arguments.Count; i++)
        {
            LuaValueType pt = i < paramTypes.Count ? paramTypes[i] : LuaValueType.Int;
            parts.Add(EmitAssignableExpression(call.Arguments[i], pt, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript));
        }

        if (!isMonsterScript)
        {
            parts.Add("cancellationToken");
        }

        return $"{methodName}({string.Join(", ", parts)})";
    }

    private string EmitAssignableExpression(
        LuaExpression expr,
        LuaValueType targetType,
        int scriptIndex,
        CompiledLuaScript script,
        IReadOnlyDictionary<string, LuaValueType>? fnScopeTypes,
        IReadOnlyDictionary<string, string> constFieldMap,
        IReadOnlyDictionary<string, LuaExpression> constExprMap,
        IReadOnlyDictionary<string, string>? localVarMap,
        bool isMonsterScript)
    {
        LuaValueType actualType = InferExpressionType(expr, script, fnScopeTypes);
        if (targetType == LuaValueType.Int && actualType == LuaValueType.NullableInt)
        {
            return EmitIntExpr(expr, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
        }

        if (targetType == LuaValueType.NullableInt && actualType == LuaValueType.Nil)
        {
            return "((int?)null)";
        }

        if (targetType == LuaValueType.Number && actualType is LuaValueType.Int or LuaValueType.NullableInt)
        {
            return $"((double)({EmitIntExpr(expr, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript)}))";
        }

        return EmitExpression(expr, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
    }

    private string EmitIntExpr(
        LuaExpression expr,
        int scriptIndex,
        CompiledLuaScript script,
        IReadOnlyDictionary<string, LuaValueType>? fnScopeTypes,
        IReadOnlyDictionary<string, string> constFieldMap,
        IReadOnlyDictionary<string, LuaExpression> constExprMap,
        IReadOnlyDictionary<string, string>? localVarMap,
        bool isMonsterScript)
    {
        LuaValueType t = InferExpressionType(expr, script, fnScopeTypes);
        string raw = EmitExpression(expr, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
        return t == LuaValueType.NullableInt ? $"(({raw}) ?? 0)" : raw;
    }

    private string EmitDoubleExpr(
        LuaExpression expr,
        int scriptIndex,
        CompiledLuaScript script,
        IReadOnlyDictionary<string, LuaValueType>? fnScopeTypes,
        IReadOnlyDictionary<string, string> constFieldMap,
        IReadOnlyDictionary<string, LuaExpression> constExprMap,
        IReadOnlyDictionary<string, string>? localVarMap,
        bool isMonsterScript)
    {
        LuaValueType t = InferExpressionType(expr, script, fnScopeTypes);
        if (t == LuaValueType.Number)
        {
            return EmitExpression(expr, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
        }

        return $"((double)({EmitIntExpr(expr, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript)}))";
    }

    private string EmitBoolCondition(
        LuaExpression expr,
        int scriptIndex,
        CompiledLuaScript script,
        IReadOnlyDictionary<string, LuaValueType>? fnScopeTypes,
        IReadOnlyDictionary<string, string> constFieldMap,
        IReadOnlyDictionary<string, LuaExpression> constExprMap,
        IReadOnlyDictionary<string, string>? localVarMap,
        bool isMonsterScript)
    {
        LuaValueType t = InferExpressionType(expr, script, fnScopeTypes);
        if (t == LuaValueType.Bool)
        {
            return EmitExpression(expr, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
        }

        if (t == LuaValueType.Nil)
        {
            return "false";
        }

        string raw = EmitExpression(expr, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
        if (t == LuaValueType.NullableInt)
        {
            return $"({raw}).HasValue";
        }

        // In Lua 4.0, ALL numbers (including 0!) and strings are truthy!
        return $"({raw} is var _)";
    }

    private string EmitAsStringExpression(
        LuaExpression expr,
        int scriptIndex,
        CompiledLuaScript script,
        IReadOnlyDictionary<string, LuaValueType>? fnScopeTypes,
        IReadOnlyDictionary<string, string> constFieldMap,
        IReadOnlyDictionary<string, LuaExpression> constExprMap,
        IReadOnlyDictionary<string, string>? localVarMap,
        bool isMonsterScript)
    {
        LuaValueType t = InferExpressionType(expr, script, fnScopeTypes);
        string raw = EmitExpression(expr, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
        return t switch
        {
            LuaValueType.Int => $"GeneratedLuaRuntime.FormatInt({raw})",
            LuaValueType.NullableInt => $"GeneratedLuaRuntime.FormatNullableInt({raw})",
            LuaValueType.Number => $"GeneratedLuaRuntime.FormatDouble({raw})",
            _ => raw,
        };
    }

    private string EmitExpression(
        LuaExpression expr,
        int scriptIndex,
        CompiledLuaScript script,
        IReadOnlyDictionary<string, LuaValueType>? fnScopeTypes,
        IReadOnlyDictionary<string, string> constFieldMap,
        IReadOnlyDictionary<string, LuaExpression> constExprMap,
        IReadOnlyDictionary<string, string>? localVarMap,
        bool isMonsterScript)
    {
        switch (expr)
        {
            case LuaNumberExpr num:
                if (Math.Abs(num.Value - Math.Truncate(num.Value)) > 1e-9 ||
                    num.Value < int.MinValue ||
                    num.Value > int.MaxValue)
                {
                    return num.Value.ToString("R", CultureInfo.InvariantCulture);
                }

                return ((int)Math.Truncate(num.Value)).ToString(CultureInfo.InvariantCulture);

            case LuaStringExpr str:
                return ToEscapedStringLiteral(str.DecodedUtf8);

            case LuaNilExpr:
                return "((int?)null)";

            case LuaIdentifierExpr id:
                if (id.Name == "SubWorld")
                {
                    return "((int)context.MapId)";
                }

                if (localVarMap is not null && localVarMap.TryGetValue(id.Name, out string? locName))
                {
                    return locName;
                }

                return constFieldMap[id.Name];

            case LuaTableLiteralExpr tbl:
                return "[" + string.Join(", ", tbl.Elements.Select(e => EmitExpression(e, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript))) + "]";

            case LuaIndexExpr idx:
                return $"GeneratedLuaRuntime.GetAt1Based({EmitExpression(idx.Target, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript)}, {EmitIntExpr(idx.Index, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript)})";

            case LuaUnaryExpr un:
            {
                if (un.Operator == LuaUnaryOp.Not)
                {
                    return $"(!({EmitBoolCondition(un.Operand, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript)}))";
                }

                LuaValueType ut = InferExpressionType(un.Operand, script, fnScopeTypes);
                string operand = ut == LuaValueType.Number
                    ? EmitDoubleExpr(un.Operand, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript)
                    : EmitIntExpr(un.Operand, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                return $"(-({operand}))";
            }

            case LuaBinaryExpr bin:
            {
                if (bin.Operator is LuaBinaryOp.Div or LuaBinaryOp.Pow &&
                    LuaScriptSemanticValidator.TryEvalConstantInt(bin, constExprMap, out int folded))
                {
                    return folded.ToString(CultureInfo.InvariantCulture);
                }

                if (bin.Operator == LuaBinaryOp.Concat)
                {
                    string ls = EmitAsStringExpression(bin.Left, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    string rs = EmitAsStringExpression(bin.Right, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    return $"({ls} + {rs})";
                }

                if (bin.Operator is LuaBinaryOp.And or LuaBinaryOp.Or)
                {
                    string lb = EmitBoolCondition(bin.Left, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    string rb = EmitBoolCondition(bin.Right, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    string boolOp = bin.Operator == LuaBinaryOp.Or ? "||" : "&&";
                    return $"({lb} {boolOp} {rb})";
                }

                LuaValueType lt = InferExpressionType(bin.Left, script, fnScopeTypes);
                LuaValueType rt = InferExpressionType(bin.Right, script, fnScopeTypes);

                if (bin.Operator is LuaBinaryOp.Eq or LuaBinaryOp.Neq)
                {
                    if (lt == LuaValueType.Nil && rt == LuaValueType.Nil)
                    {
                        return bin.Operator == LuaBinaryOp.Eq ? "true" : "false";
                    }

                    if ((lt == LuaValueType.Nil && rt != LuaValueType.NullableInt) ||
                        (rt == LuaValueType.Nil && lt != LuaValueType.NullableInt))
                    {
                        return bin.Operator == LuaBinaryOp.Eq ? "false" : "true";
                    }

                    if (lt == LuaValueType.String && rt == LuaValueType.String)
                    {
                        string ls = EmitExpression(bin.Left, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                        string rs = EmitExpression(bin.Right, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                        string eq = $"string.Equals({ls}, {rs}, StringComparison.Ordinal)";
                        return bin.Operator == LuaBinaryOp.Eq ? eq : $"(!{eq})";
                    }

                    if (lt == LuaValueType.Number || rt == LuaValueType.Number)
                    {
                        string ld = EmitDoubleExpr(bin.Left, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                        string rd = EmitDoubleExpr(bin.Right, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                        return bin.Operator == LuaBinaryOp.Eq ? $"({ld} == {rd})" : $"({ld} != {rd})";
                    }

                    string le = EmitExpression(bin.Left, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    string re = EmitExpression(bin.Right, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    return bin.Operator == LuaBinaryOp.Eq ? $"({le} == {re})" : $"({le} != {re})";
                }

                if (bin.Operator is LuaBinaryOp.Lt or LuaBinaryOp.Le or LuaBinaryOp.Gt or LuaBinaryOp.Ge)
                {
                    string relOp = bin.Operator switch
                    {
                        LuaBinaryOp.Lt => "<",
                        LuaBinaryOp.Le => "<=",
                        LuaBinaryOp.Gt => ">",
                        _ => ">=",
                    };
                    if (lt == LuaValueType.Number || rt == LuaValueType.Number)
                    {
                        string ld = EmitDoubleExpr(bin.Left, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                        string rd = EmitDoubleExpr(bin.Right, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                        return $"({ld} {relOp} {rd})";
                    }

                    string li = EmitIntExpr(bin.Left, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    string ri = EmitIntExpr(bin.Right, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    return $"({li} {relOp} {ri})";
                }

                if (bin.Operator == LuaBinaryOp.Div)
                {
                    string ld = EmitDoubleExpr(bin.Left, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    string rd = EmitDoubleExpr(bin.Right, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    return $"({ld} / {rd})";
                }

                if (bin.Operator == LuaBinaryOp.Pow)
                {
                    string ld = EmitDoubleExpr(bin.Left, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    string rd = EmitDoubleExpr(bin.Right, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    return $"Math.Pow({ld}, {rd})";
                }

                if (lt == LuaValueType.Number || rt == LuaValueType.Number)
                {
                    string ld = EmitDoubleExpr(bin.Left, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    string rd = EmitDoubleExpr(bin.Right, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    string dOp = bin.Operator switch
                    {
                        LuaBinaryOp.Sub => "-",
                        LuaBinaryOp.Mul => "*",
                        _ => "+",
                    };
                    return $"({ld} {dOp} {rd})";
                }

                string lInt = EmitIntExpr(bin.Left, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                string rInt = EmitIntExpr(bin.Right, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                string opStr = bin.Operator switch
                {
                    LuaBinaryOp.Sub => "-",
                    LuaBinaryOp.Mul => "*",
                    LuaBinaryOp.Mod => "%",
                    _ => "+",
                };
                return $"({lInt} {opStr} {rInt})";
            }

            case LuaCallExpr call:
                return EmitCallExpression(call, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);

            default:
                throw new InvalidOperationException($"Unsupported expression node '{expr.GetType().Name}'.");
        }
    }

    private string EmitCallExpression(
        LuaCallExpr call,
        int scriptIndex,
        CompiledLuaScript script,
        IReadOnlyDictionary<string, LuaValueType>? fnScopeTypes,
        IReadOnlyDictionary<string, string> constFieldMap,
        IReadOnlyDictionary<string, LuaExpression> constExprMap,
        IReadOnlyDictionary<string, string>? localVarMap,
        bool isMonsterScript)
    {
        string E(int idx) => EmitExpression(call.Arguments[idx], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
        string EInt(int idx) => EmitIntExpr(call.Arguments[idx], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
        string EDbl(int idx) => EmitDoubleExpr(call.Arguments[idx], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
        string EStr(int idx) => EmitAsStringExpression(call.Arguments[idx], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);

        if (script.FunctionReturnTypes.ContainsKey(call.CalleeName))
        {
            string invoke = BuildHelperInvocationExpr(call, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
            if (isMonsterScript)
            {
                return invoke;
            }

            return script.FunctionIsAsync[call.CalleeName] ? $"(await {invoke})" : invoke;
        }

        switch (call.CalleeName)
        {
            case "GetLevel": return "(int)context.Level";
            case "GetSex": return "(int)context.Gender";
            case "GetSeries": return "(int)context.Series";
            case "GetCash": return "context.InventoryMoney";
            case "GetRepute": return "context.Repute";
            case "GetFightState": return "(int)context.CurrentFightMode";
            case "CalcFreeItemCellCount": return "context.FreeBagCellCount";
            case "GetCamp": return "(int)context.BaseCamp";
            case "GetCurCamp": return "(int)context.CurrentCamp";
            case "GetPK": return "(int)context.PkValue";
            case "GetRank": return "(int)context.RankId";
            case "GetLastFactionNumber": return "(context.EffectiveFactionId == 255 ? -1 : (int)context.EffectiveFactionId)";
            case "GetLastDiagNpc": return "(int)(context.SourceNpcId ?? 0u)";
            case "GetCurServerTime": return "GeneratedLuaRuntime.GetCurServerTime()";
            case "GetWorldPos": return "(int)context.MapId";
            case "GetPlayerRev": return "(int)context.RevivalMapId";
            case "SubWorldIdx2ID": return $"({EInt(0)})";
            case "SubWorldID2Idx": return $"(({EInt(0)}) > 0 ? ({EInt(0)}) : -1)";
            case "GetName": return "context.RoleNameUtf8";
            case "GetAccount": return "string.Empty";
            case "GetNpcName": return $"(_ = {EInt(0)}, context.SourceNpcNameUtf8 ?? string.Empty).Item2";
            case "NpcName2Replace": return EStr(0);
            case "GetFaction": return "GeneratedLuaRuntime.GetFactionName(context.CurrentFactionId)";
            case "GetLastAddFaction": return "GeneratedLuaRuntime.GetFactionName(context.EffectiveFactionId)";
            case "GetTask": return $"context.GetTask((ushort)({EInt(0)}))";
            case "GetTaskTemp": return $"context.GetTempTask((ushort)({EInt(0)}))";
            case "GetGlbValue": return $"context.GetGlobalValue({EInt(0)})";
            case "HaveMagic": return $"context.GetLearnedSkillLevel({EInt(0)})";
            case "CheckStoreBoxState": return $"(context.IsExpandableBoxUnlocked((byte)({EInt(0)})) ? 1 : 0)";
            case "Pay": return $"(context.TryPayMoney({EInt(0)}) ? 1 : 0)";
            case "GetByte": return $"GeneratedLuaRuntime.GetByte({EInt(0)}, {EInt(1)})";
            case "SetByte": return $"GeneratedLuaRuntime.SetByte({EInt(0)}, {EInt(1)}, {EInt(2)})";
            case "GetBit": return $"GeneratedLuaRuntime.GetBit({EInt(0)}, {EInt(1)})";
            case "SetBit": return $"GeneratedLuaRuntime.SetBit({EInt(0)}, {EInt(1)}, {EInt(2)})";
            case "random":
                return call.Arguments.Count == 1
                    ? $"GeneratedLuaRuntime.LuaRandom({EInt(0)})"
                    : $"GeneratedLuaRuntime.LuaRandom({EInt(0)}, {EInt(1)})";
            case "getn":
            {
                LuaValueType at = InferExpressionType(call.Arguments[0], script, fnScopeTypes);
                return at is LuaValueType.StringList or LuaValueType.IntList
                    ? $"({E(0)}).Count"
                    : $"({E(0)}).Length";
            }
            case "floor": return $"((int)Math.Floor({EDbl(0)}))";
            case "ceil": return $"((int)Math.Ceiling({EDbl(0)}))";
            case "abs":
                return InferExpressionType(call.Arguments[0], script, fnScopeTypes) == LuaValueType.Number
                    ? $"Math.Abs({EDbl(0)})"
                    : $"Math.Abs({EInt(0)})";
            case "mod":
                return InferExpressionType(call, script, fnScopeTypes) == LuaValueType.Number
                    ? $"GeneratedLuaRuntime.LuaMod({EDbl(0)}, {EDbl(1)})"
                    : $"GeneratedLuaRuntime.LuaMod({EInt(0)}, {EInt(1)})";
            case "min":
                return InferExpressionType(call, script, fnScopeTypes) == LuaValueType.Number
                    ? $"Math.Min({EDbl(0)}, {EDbl(1)})"
                    : $"Math.Min({EInt(0)}, {EInt(1)})";
            case "max":
                return InferExpressionType(call, script, fnScopeTypes) == LuaValueType.Number
                    ? $"Math.Max({EDbl(0)}, {EDbl(1)})"
                    : $"Math.Max({EInt(0)}, {EInt(1)})";
            case "tonumber":
                return InferExpressionType(call.Arguments[0], script, fnScopeTypes) == LuaValueType.String
                    ? $"GeneratedLuaRuntime.LuaToNumber({E(0)})"
                    : $"((int?)({EInt(0)}))";
            case "tostring":
                return EStr(0);
            case "strlen":
                return $"({EStr(0)}).Length";
            case "strlower":
                return $"({E(0)}).ToLowerInvariant()";
            case "strupper":
                return $"({E(0)}).ToUpperInvariant()";
            case "strsub":
                return call.Arguments.Count == 2
                    ? $"GeneratedLuaRuntime.LuaStrSub({E(0)}, {EInt(1)})"
                    : $"GeneratedLuaRuntime.LuaStrSub({E(0)}, {EInt(1)}, {EInt(2)})";
            case "format":
            {
                if (call.Arguments.Count == 1)
                {
                    return E(0);
                }

                var fArgs = new List<string>(call.Arguments.Count - 1);
                for (int i = 1; i < call.Arguments.Count; i++)
                {
                    fArgs.Add(E(i));
                }

                return $"GeneratedLuaRuntime.LuaFormat({E(0)}, {string.Join(", ", fArgs)})";
            }
            case "GetLocalDate":
            case "date":
                return call.Arguments.Count == 0
                    ? "GeneratedLuaRuntime.FormatLegacyDate()"
                    : $"GeneratedLuaRuntime.FormatLegacyDate({E(0)})";
            case "HaveItem":
            {
                string code = ResolveEventItemCode(call.Arguments[0], constExprMap);
                return $"(context.HasBagItem({ToEscapedStringLiteral(code)}) ? 1 : 0)";
            }
            case "AddItem":
            case "AddItemNoStack":
            {
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], constExprMap, out int g);
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[1], constExprMap, out int d);
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[2], constExprMap, out int p);
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[3], constExprMap, out int lv);
                _ = _itemCatalogIndex!.TryResolveByGdpl(g, d, p, lv, out PackItemEntry itemEntry);
                string litCode = ToEscapedStringLiteral(itemEntry.Code);
                if (g == 0)
                {
                    string seriesArg = $"(ElementSeries)Math.Clamp({EInt(4)}, 0, 4)";
                    return $"(context.TryGrantEquipmentByCode({litCode}, {seriesArg}) ? 1 : 0)";
                }

                return $"(context.TryGrantItemByCode({litCode}, 1) ? 1 : 0)";
            }
            case "AddStackItem":
            {
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[1], constExprMap, out int g);
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[2], constExprMap, out int d);
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[3], constExprMap, out int p);
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[4], constExprMap, out int lv);
                _ = _itemCatalogIndex!.TryResolveByGdpl(g, d, p, lv, out PackItemEntry itemEntry);
                return $"(context.TryGrantItemByCode({ToEscapedStringLiteral(itemEntry.Code)}, {EInt(0)}) ? 1 : 0)";
            }
            case "DelCommonItem":
            {
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], constExprMap, out int g);
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[1], constExprMap, out int d);
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[2], constExprMap, out int p);
                _ = _itemCatalogIndex!.TryResolveByGdp(g, d, p, out PackItemEntry itemEntry);
                return $"(context.TryConsumeItemByCode({ToEscapedStringLiteral(itemEntry.Code)}, 1) ? 1 : 0)";
            }
            case "CalcEquiproomItemCount":
            {
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], constExprMap, out int g);
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[1], constExprMap, out int d);
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[2], constExprMap, out int p);
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[3], constExprMap, out int lv);
                _ = _itemCatalogIndex!.TryResolveByGdpl(g, d, p, lv, out PackItemEntry itemEntry);
                return $"context.GetBagItemCount({ToEscapedStringLiteral(itemEntry.Code)})";
            }
            case "ConsumeEquiproomItem":
            {
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[1], constExprMap, out int g);
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[2], constExprMap, out int d);
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[3], constExprMap, out int p);
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[4], constExprMap, out int lv);
                _ = _itemCatalogIndex!.TryResolveByGdpl(g, d, p, lv, out PackItemEntry itemEntry);
                return $"(context.TryConsumeItemByCode({ToEscapedStringLiteral(itemEntry.Code)}, {EInt(0)}) ? 1 : 0)";
            }
            case "NewWorld":
                return $"((await context.TeleportPlayerAsync((ushort)({EInt(0)}), ({EInt(1)}) * 32, ({EInt(2)}) * 32, context.CurrentFightMode, cancellationToken)) ? 1 : 0)";
            case "ReturnFromPortal":
                return "((await context.TryReturnFromPortalAsync(cancellationToken)) ? 1 : 0)";
            default:
                throw new InvalidOperationException($"Unsupported call expression '{call.CalleeName}'.");
        }
    }

    private string ResolveEventItemCode(LuaExpression arg, IReadOnlyDictionary<string, LuaExpression> constExprMap)
    {
        _ = LuaScriptSemanticValidator.TryEvalConstantInt(arg, constExprMap, out int eid);
        if (_itemCatalogIndex is not null && _itemCatalogIndex.TryResolveEventItem(eid, out string catalogCode))
        {
            return catalogCode;
        }

        return _eventItemCodeByDetailId[eid];
    }

    private static LuaValueType InferExpressionType(
        LuaExpression expr,
        CompiledLuaScript script,
        IReadOnlyDictionary<string, LuaValueType>? fnScopeTypes)
    {
        switch (expr)
        {
            case LuaNumberExpr num:
                return Math.Abs(num.Value - Math.Truncate(num.Value)) > 1e-9 ||
                       num.Value < int.MinValue ||
                       num.Value > int.MaxValue
                    ? LuaValueType.Number
                    : LuaValueType.Int;
            case LuaStringExpr:
                return LuaValueType.String;
            case LuaNilExpr:
                return LuaValueType.Nil;
            case LuaIdentifierExpr id:
                if (id.Name == "SubWorld")
                {
                    return LuaValueType.Int;
                }

                if (fnScopeTypes is not null && fnScopeTypes.TryGetValue(id.Name, out LuaValueType st))
                {
                    return st;
                }

                if (script.TopLevelConstantTypes.TryGetValue(id.Name, out LuaValueType tt))
                {
                    return tt;
                }

                return LuaValueType.Int;
            case LuaIndexExpr idx:
                return InferExpressionType(idx.Target, script, fnScopeTypes) is LuaValueType.StringArray or LuaValueType.StringList
                    ? LuaValueType.String
                    : LuaValueType.Int;
            case LuaBinaryExpr bin:
            {
                if (bin.Operator == LuaBinaryOp.Concat)
                {
                    return LuaValueType.String;
                }

                if (bin.Operator is LuaBinaryOp.Or or LuaBinaryOp.And or LuaBinaryOp.Eq or LuaBinaryOp.Neq or LuaBinaryOp.Lt or LuaBinaryOp.Le or LuaBinaryOp.Gt or LuaBinaryOp.Ge)
                {
                    return LuaValueType.Bool;
                }

                if (bin.Operator is LuaBinaryOp.Div or LuaBinaryOp.Pow)
                {
                    return LuaScriptSemanticValidator.TryEvalConstantInt(bin, out _)
                        ? LuaValueType.Int
                        : LuaValueType.Number;
                }

                LuaValueType lt = InferExpressionType(bin.Left, script, fnScopeTypes);
                LuaValueType rt = InferExpressionType(bin.Right, script, fnScopeTypes);
                return lt == LuaValueType.Number || rt == LuaValueType.Number
                    ? LuaValueType.Number
                    : LuaValueType.Int;
            }
            case LuaUnaryExpr un:
                return un.Operator == LuaUnaryOp.Not
                    ? LuaValueType.Bool
                    : InferExpressionType(un.Operand, script, fnScopeTypes) == LuaValueType.Number
                        ? LuaValueType.Number
                        : LuaValueType.Int;
            case LuaCallExpr call:
                if (script.FunctionReturnTypes.TryGetValue(call.CalleeName, out LuaValueType fnRet))
                {
                    return fnRet;
                }

                return call.CalleeName switch
                {
                    "GetName" or "GetNpcName" or "NpcName2Replace" or "GetFaction" or "GetLastAddFaction" or "tostring" or "strsub" or "strlower" or "strupper" or "format" or "GetLocalDate" or "date" => LuaValueType.String,
                    "tonumber" => LuaValueType.NullableInt,
                    "abs" when call.Arguments.Count == 1 && InferExpressionType(call.Arguments[0], script, fnScopeTypes) == LuaValueType.Number => LuaValueType.Number,
                    "mod" or "min" or "max" when call.Arguments.Count == 2 &&
                                                 (InferExpressionType(call.Arguments[0], script, fnScopeTypes) == LuaValueType.Number ||
                                                  InferExpressionType(call.Arguments[1], script, fnScopeTypes) == LuaValueType.Number) => LuaValueType.Number,
                    _ => LuaValueType.Int,
                };
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
            LuaValueType.NullableInt => "int?",
            LuaValueType.Number => "double",
            LuaValueType.String => "string",
            LuaValueType.Bool => "bool",
            LuaValueType.StringArray => "string[]",
            LuaValueType.IntArray => "int[]",
            LuaValueType.StringList => "List<string>",
            LuaValueType.IntList => "List<int>",
            _ => "void",
        };

    private static string DefaultValueExpr(LuaValueType t) =>
        t switch
        {
            LuaValueType.Int => "0",
            LuaValueType.NullableInt => "null",
            LuaValueType.Number => "0.0",
            LuaValueType.String => "string.Empty",
            LuaValueType.Bool => "false",
            LuaValueType.StringArray => "[]",
            LuaValueType.IntArray => "[]",
            LuaValueType.StringList => "new List<string>()",
            LuaValueType.IntList => "new List<int>()",
            _ => "default",
        };

    private static string DefaultReturnExpr(LuaValueType retType, IReadOnlyList<LuaValueType>? retTuple)
    {
        if (retTuple is not null)
        {
            return "(" + string.Join(", ", retTuple.Select(DefaultValueExpr)) + ")";
        }

        return DefaultValueExpr(retType);
    }

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
