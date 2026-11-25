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
/// with exact <c>[LegacySource]</c> and included-header SHA-256 provenance into <c>Scripts/Generated/</c>.
/// </summary>
public sealed class LuaCSharpCodeEmitter
{
    private readonly IReadOnlyDictionary<int, string> _eventItemCodeByDetailId;
    private readonly PackItemCatalogIndex? _itemCatalogIndex;
    private IReadOnlyDictionary<string, string>? _currentFnLocalTupleTables;

    public LuaCSharpCodeEmitter(
        IReadOnlyDictionary<int, string> eventItemCodeByDetailId,
        PackItemCatalogIndex? itemCatalogIndex = null)
    {
        _eventItemCodeByDetailId = eventItemCodeByDetailId ?? throw new ArgumentNullException(nameof(eventItemCodeByDetailId));
        _itemCatalogIndex = itemCatalogIndex;
    }

    public IReadOnlyList<string> EmitAll(
        string generatedDir,
        IReadOnlyList<CompiledLuaScript> compiledScripts,
        IReadOnlyDictionary<string, string>? includedHeaderSha256ByPath = null)
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

        EmitIfNonEmpty(
            generatedDir,
            "GeneratedIncludedHeaderProvenance.cs",
            includedHeaderSha256ByPath is { Count: > 0 },
            () => BuildIncludedHeaderProvenanceClass(includedHeaderSha256ByPath!),
            writtenFiles);

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

    private static string BuildIncludedHeaderProvenanceClass(IReadOnlyDictionary<string, string> headerShaByPath)
    {
        var sb = new StringBuilder(headerShaByPath.Count * 120 + 512);
        AppendHeader(sb, "System", "System.Collections.Frozen", "System.Collections.Generic");
        sb.AppendLine("public static class GeneratedIncludedHeaderProvenance");
        sb.AppendLine("{");
        sb.AppendLine("    public static readonly FrozenDictionary<string, string> Sha256ByHeaderPath =");
        sb.AppendLine("        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)");
        sb.AppendLine("        {");
        foreach (KeyValuePair<string, string> kv in headerShaByPath.OrderBy(static x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("            [")
              .Append(ToVerbatimLiteral(kv.Key))
              .Append("] = ")
              .Append(ToEscapedStringLiteral(kv.Value))
              .AppendLine(",");
        }

        sb.AppendLine("        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);");
        sb.AppendLine("}");
        return sb.ToString();
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
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JXReborn.Packs.TieuDaoTu.Hosting;
using JXReborn.Primitives.Characters;
using JXReborn.Primitives.Items;
using JXReborn.ZoneEngine.Factions;
using JXReborn.ZoneEngine.Scripting;

namespace JXReborn.Packs.TieuDaoTu.Scripts.Generated;

public static class GeneratedLuaRuntime
{
    private static readonly AsyncLocal<Func<int, int, int>?> RandomOverride = new();
    private static readonly AsyncLocal<DateTime?> ClockOverride = new();

    public enum RuntimeAwardKind : byte
    {
        Sequential = 0,
        RandomWeighted = 1,
        Item = 2,
        GoldEquip = 3,
        Exp = 4,
        ExpTl = 5,
        Money = 6,
        Repute = 7,
        ChestExpAward = 8,
        BingoBoxExpAward = 9,
    }

    public sealed record RuntimeAwardNode(
        RuntimeAwardKind Kind,
        double RatePercent = 0.0,
        int Count = 1,
        string ItemCode = "",
        string ItemNameUtf8 = "",
        ushort MaxStackCount = 1,
        bool IsEquipment = false,
        bool ConsumeRandomSeriesRoll = false,
        int? FixedSeries = null,
        int BindStateRaw = 0,
        long NumericAmount = 0,
        RuntimeAwardNode[]? Children = null,
        string CatalogItemNameUtf8 = "",
        string? CallbackKind = null,
        string? CallbackTitleUtf8 = null,
        int ExpiredTimeMinutes = 0,
        int[]? SpecParams = null);

    public sealed class DailogBuilder(string? npcName = null)
    {
        private readonly List<DialogOption> _options = [];

        public string NpcName { get; } = npcName ?? string.Empty;

        public string TitleMsg { get; set; } =
            "<npc>Chào mừng bạn đến với <color=yellow>Võ Lâm Truyền Kỳ<color=yellow><enter>- <color=Red>Sơn Hà Xã Tắc<color>";

        public void AddOptEntry(string textUtf8, Func<IScriptDialogContext, ValueTask>? callback = null)
        {
            for (int i = 0; i < _options.Count; i++)
            {
                if (string.Equals(_options[i].TextUtf8, textUtf8, StringComparison.Ordinal))
                {
                    return;
                }
            }

            _options.Add(new DialogOption(textUtf8, callback));
        }

        public void Show(IScriptDialogContext context)
        {
            var finalOpts = new DialogOption[_options.Count + 1];
            for (int i = 0; i < _options.Count; i++)
            {
                finalOpts[i] = _options[i];
            }

            finalOpts[_options.Count] = new DialogOption("Kết thúc đối thoại", null);
            context.Describe(FormatTaskSayColor(context, TitleMsg), finalOpts);
        }
    }

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

    public static bool TryMapFactionNameToId(string? factionName, out byte factionId)
    {
        factionId = (factionName?.Trim().ToLowerInvariant()) switch
        {
            "shaolin" => 0,
            "tianwang" => 1,
            "tangmen" => 2,
            "wudu" => 3,
            "emei" => 4,
            "cuiyan" => 5,
            "gaibang" => 6,
            "tianren" => 7,
            "wudang" => 8,
            "kunlun" => 9,
            "huashan" => 10,
            _ => 255,
        };
        return factionId != 255;
    }

    public static int SetPlayerFaction(IScriptDialogContext context, string? factionName)
    {
        if (string.IsNullOrEmpty(factionName))
        {
            if (context.CurrentFactionId != 255)
            {
                _ = context.TryGraduateFaction();
            }

            return 1;
        }

        if (!TryMapFactionNameToId(factionName, out byte fid))
        {
            return 0;
        }

        if (context.CurrentFactionId != 255)
        {
            _ = context.TryGraduateFaction();
        }

        return context.TryJoinFaction(fid) == FactionProgressionResult.Success ? 1 : 0;
    }

    public static int Add120SkillExp(IScriptDialogContext context, int addExp)
    {
        const ushort TaskIdActive120Skill = 2463;
        const ushort TaskIdToday120Exp = 2464;
        const ushort TaskIdLast120Date = 2465;
        const int Max120SkillExpPerDay = 33_000_000;

        if (addExp == 0)
        {
            return 0;
        }

        int skillId = context.GetTask(TaskIdActive120Skill);
        if ((uint)(skillId - 1) >= 1999u)
        {
            return 0;
        }

        int lastDate = context.GetTask(TaskIdLast120Date);
        int todayAdd = context.GetTask(TaskIdToday120Exp);
        int curDate = (int)((uint)GetCurServerTime() / 86400u);
        if (lastDate != curDate)
        {
            todayAdd = 0;
            context.SetTask(TaskIdLast120Date, curDate);
            context.SetTask(TaskIdToday120Exp, 0);
        }

        if (addExp > 0)
        {
            if ((uint)Max120SkillExpPerDay <= (uint)todayAdd)
            {
                return 0;
            }

            if (Max120SkillExpPerDay - todayAdd < addExp)
            {
                addExp = Max120SkillExpPerDay - todayAdd;
            }

            context.SetTask(TaskIdToday120Exp, addExp + todayAdd);
        }

        context.AddDirectSkillExp(skillId, addExp);
        return 1;
    }

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

    public static int[] GetAt1Based(int[][] items, int luaIndex)
    {
        int idx = luaIndex - 1;
        return (uint)idx < (uint)items.Length ? items[idx] : [];
    }

    public static string[] GetAt1Based(string[][] items, int luaIndex)
    {
        int idx = luaIndex - 1;
        return (uint)idx < (uint)items.Length ? items[idx] : [];
    }

    public static TRow GetRowAt1Based<TRow>(TRow[] rows, int luaIndex)
        where TRow : struct
    {
        int idx = luaIndex - 1;
        return (uint)idx < (uint)rows.Length ? rows[idx] : default;
    }

    public static int? GetMapIntOrNull(FrozenDictionary<int, int> map, int key) =>
        map.TryGetValue(key, out int v) ? v : null;

    public static string GetMapStrOrEmpty(FrozenDictionary<int, string> map, int key) =>
        map.TryGetValue(key, out string? v) ? v : string.Empty;

    public static int[] GetMapIntArrOrEmpty(FrozenDictionary<int, int[]> map, int key) =>
        map.TryGetValue(key, out int[]? v) ? v : [];

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

    public static bool TryGrantMappedItem(IScriptDialogContext context, int key, Func<int, string?> mapFn, int count = 1)
    {
        string? code = mapFn(key);
        return code is not null && context.TryGrantItemByCode(code, count);
    }

    public static bool TryGrantMappedEquip(IScriptDialogContext context, int key, Func<int, string?> mapFn, ElementSeries? seriesOverride = null)
    {
        string? code = mapFn(key);
        return code is not null && context.TryGrantEquipmentByCode(code, seriesOverride);
    }

    public static bool TryConsumeMappedItem(IScriptDialogContext context, int key, Func<int, string?> mapFn, int count = 1)
    {
        string? code = mapFn(key);
        return code is not null && context.TryConsumeItemByCode(code, count);
    }

    public static int GetMappedItemCount(IScriptDialogContext context, int key, Func<int, string?> mapFn)
    {
        string? code = mapFn(key);
        return code is not null ? context.GetBagItemCount(code) : 0;
    }

    public static int HasMappedItem(IScriptDialogContext context, int key, Func<int, string?> mapFn)
    {
        string? code = mapFn(key);
        return code is not null && context.HasBagItem(code) ? 1 : 0;
    }

    public static int GiveAward(IScriptDialogContext context, RuntimeAwardNode? node, int awardCount = 1)
    {
        if (node is null || awardCount <= 0)
        {
            return 0;
        }

        switch (node.Kind)
        {
            case RuntimeAwardKind.Sequential:
                if (node.Children is not null)
                {
                    for (int i = 0; i < node.Children.Length; i++)
                    {
                        _ = GiveAward(context, node.Children[i], awardCount);
                    }
                }

                return 1;

            case RuntimeAwardKind.RandomWeighted:
                if (node.Children is not null)
                {
                    const int rtotal = 10_000_000;
                    for (int k = 0; k < awardCount; k++)
                    {
                        int rcur = LuaRandom(1, rtotal);
                        int rstep = 0;
                        for (int i = 0; i < node.Children.Length; i++)
                        {
                            RuntimeAwardNode child = node.Children[i];
                            rstep += (int)Math.Floor(child.RatePercent * rtotal / 100.0);
                            if (rcur <= rstep)
                            {
                                _ = GiveAward(context, child, 1);
                                break;
                            }
                        }
                    }
                }

                return 1;

            case RuntimeAwardKind.GoldEquip:
            {
                int total = awardCount * node.Count;
                ItemBindState bind = node.BindStateRaw == -2 ? ItemBindState.BoundPermanent : ItemBindState.Unbound;
                for (int i = 0; i < total; i++)
                {
                    if (node.ConsumeRandomSeriesRoll)
                    {
                        _ = LuaRandom(0, 4);
                    }

                    if (string.Equals(node.CallbackKind, "BingoMessage", StringComparison.Ordinal))
                    {
                        string catName = string.IsNullOrEmpty(node.CatalogItemNameUtf8) ? node.ItemNameUtf8 : node.CatalogItemNameUtf8;
                        context.BroadcastGlobalNews(LuaFormat("<color=green>Chúc mừng cao thủ <color=yellow>%s<color=green> đã nhận được <color=yellow><%s><color=green> từ <color=yellow><%s><color>", context.RoleNameUtf8, catName, node.CallbackTitleUtf8 ?? string.Empty));
                    }

                    _ = context.TryGrantEquipmentByCode(node.ItemCode, bindState: bind);
                }

                if (string.Equals(node.CallbackKind, "RcnyGoldNews", StringComparison.Ordinal))
                {
                    string display = node.CallbackTitleUtf8 ?? node.ItemNameUtf8;
                    context.SendSystemMessage($"Nhận được {display}.");
                    context.BroadcastGlobalNews(LuaFormat("<color=green>Chúc mừng cao thủ <color=yellow>%s<color=green> mở bảo rương nhận được <color=yellow>%s<color=green>!<color>", context.RoleNameUtf8, display));
                }
                else
                {
                    context.SendSystemMessage(LuaFormat("Nhận được %d %s.", total, node.ItemNameUtf8));
                }

                return 1;
            }

            case RuntimeAwardKind.Item:
            {
                int total = awardCount * node.Count;
                ItemBindState bind = node.BindStateRaw == -2 ? ItemBindState.BoundPermanent : ItemBindState.Unbound;
                if (node.IsEquipment)
                {
                    for (int i = 0; i < total; i++)
                    {
                        int sVal = node.ConsumeRandomSeriesRoll ? LuaRandom(0, 4) : (node.FixedSeries ?? 0);
                        if (string.Equals(node.CallbackKind, "BingoMessage", StringComparison.Ordinal))
                        {
                            string catName = string.IsNullOrEmpty(node.CatalogItemNameUtf8) ? node.ItemNameUtf8 : node.CatalogItemNameUtf8;
                            context.BroadcastGlobalNews(LuaFormat("<color=green>Chúc mừng cao thủ <color=yellow>%s<color=green> đã nhận được <color=yellow><%s><color=green> từ <color=yellow><%s><color>", context.RoleNameUtf8, catName, node.CallbackTitleUtf8 ?? string.Empty));
                        }

                        _ = context.TryGrantEquipmentByCode(node.ItemCode, (ElementSeries)Math.Clamp(sVal, 0, 4), bindState: bind);
                    }
                }
                else if (node.MaxStackCount > 1)
                {
                    int rem = total;
                    while (rem > 0)
                    {
                        if (node.ConsumeRandomSeriesRoll)
                        {
                            _ = LuaRandom(0, 4);
                        }

                        int batch = Math.Min(rem, node.MaxStackCount);
                        if (string.Equals(node.CallbackKind, "BingoMessage", StringComparison.Ordinal))
                        {
                            string catName = string.IsNullOrEmpty(node.CatalogItemNameUtf8) ? node.ItemNameUtf8 : node.CatalogItemNameUtf8;
                            context.BroadcastGlobalNews(LuaFormat("<color=green>Chúc mừng cao thủ <color=yellow>%s<color=green> đã nhận được <color=yellow><%s><color=green> từ <color=yellow><%s><color>", context.RoleNameUtf8, catName, node.CallbackTitleUtf8 ?? string.Empty));
                        }

                        _ = context.TryGrantItemByCode(node.ItemCode, batch, bind);
                        rem -= batch;
                    }
                }
                else
                {
                    for (int i = 0; i < total; i++)
                    {
                        if (node.ConsumeRandomSeriesRoll)
                        {
                            _ = LuaRandom(0, 4);
                        }

                        if (string.Equals(node.CallbackKind, "BingoMessage", StringComparison.Ordinal))
                        {
                            string catName = string.IsNullOrEmpty(node.CatalogItemNameUtf8) ? node.ItemNameUtf8 : node.CatalogItemNameUtf8;
                            context.BroadcastGlobalNews(LuaFormat("<color=green>Chúc mừng cao thủ <color=yellow>%s<color=green> đã nhận được <color=yellow><%s><color=green> từ <color=yellow><%s><color>", context.RoleNameUtf8, catName, node.CallbackTitleUtf8 ?? string.Empty));
                        }

                        _ = context.TryGrantItemByCode(node.ItemCode, 1, bind);
                    }
                }

                context.SendSystemMessage(LuaFormat("Nhận được %d %s.", total, node.ItemNameUtf8));
                return 1;
            }

            case RuntimeAwardKind.ChestExpAward:
            {
                long totalExp = (long)awardCount * node.Count * node.NumericAmount;
                GrantVngChestExpAward(context, totalExp, node.CallbackTitleUtf8 ?? string.Empty);
                return 1;
            }

            case RuntimeAwardKind.BingoBoxExpAward:
            {
                long totalExp = (long)awardCount * node.Count * node.NumericAmount;
                GrantBingoBoxExpAward(context, totalExp, node.CallbackTitleUtf8 ?? string.Empty);
                return 1;
            }

            case RuntimeAwardKind.Exp:
            {
                long totalExp = (long)awardCount * node.Count * node.NumericAmount;
                context.GrantExperience(totalExp);
                context.SendSystemMessage($"Đạt được kinh nghiệm không thể cộng dồn {totalExp.ToString(CultureInfo.InvariantCulture)}");
                return 1;
            }

            case RuntimeAwardKind.ExpTl:
            {
                long totalExp = (long)awardCount * node.Count * node.NumericAmount;
                context.GrantExperience(totalExp);
                context.SendSystemMessage($"Nhận được điểm kinh nghiệm cộng dồn {totalExp.ToString(CultureInfo.InvariantCulture)}.");
                return 1;
            }

            case RuntimeAwardKind.Money:
            {
                int totalMoney = awardCount * node.Count * (int)node.NumericAmount;
                context.GrantMoney(totalMoney);
                context.SendSystemMessage($"Nhận được {totalMoney.ToString(CultureInfo.InvariantCulture)} lượng");
                return 1;
            }

            case RuntimeAwardKind.Repute:
            {
                int totalRep = awardCount * node.Count * (int)node.NumericAmount;
                context.AddRepute(totalRep);
                context.SendSystemMessage($"Điểm Danh Vọng tăng cao {totalRep.ToString(CultureInfo.InvariantCulture)}");
                return 1;
            }

            default:
                return 0;
        }
    }

    public static string FormatTaskSayColor(IScriptDialogContext context, string? rawText)
    {
        if (string.IsNullOrEmpty(rawText))
        {
            return string.Empty;
        }

        string strSex = context.Gender == CharacterGender.Male ? "Công tử " : "Nữ hiệp";
        uint npcId = context.SourceNpcId ?? 0u;
        string strNpc = npcId == 0u
            ? string.Empty
            : $"<#><link=image[0,8]:#npcspr:?NPCSID={(context.SourceNpcTemplateId ?? 0).ToString(CultureInfo.InvariantCulture)}?ACTION=0><<{context.SourceNpcNameUtf8 ?? string.Empty}>><link>: ";

        return rawText
            .Replace("{{", "<color=yellow>", StringComparison.Ordinal)
            .Replace("}}", "<color>", StringComparison.Ordinal)
            .Replace("<sex>", strSex, StringComparison.Ordinal)
            .Replace("<pan>", string.Empty, StringComparison.Ordinal)
            .Replace("<npc>", strNpc, StringComparison.Ordinal);
    }

    public static void DispatchTaskSay(
        IScriptDialogContext context,
        string? rawCaption,
        IReadOnlyList<string> rawOptions,
        Func<IScriptDialogContext, string, string[], ValueTask> invokeCallback)
    {
        string str = rawCaption ?? string.Empty;
        DialogOption[] opts = BuildDynamicDialogOptions(rawOptions, rawOptions.Count, invokeCallback);
        if (str.StartsWith("<dec>", StringComparison.Ordinal))
        {
            string body = FormatTaskSayColor(context, str[5..]);
            context.Describe(body, opts);
        }
        else
        {
            string body = FormatTaskSayColor(context, str);
            context.Say(body, opts);
        }
    }

    public static void DispatchCreateTaskSay(
        IScriptDialogContext context,
        IReadOnlyList<string> tableWithCaptionAndOptions,
        Func<IScriptDialogContext, string, string[], ValueTask> invokeCallback)
    {
        if (tableWithCaptionAndOptions.Count == 0)
        {
            return;
        }

        string caption = tableWithCaptionAndOptions[0];
        var opts = new string[Math.Max(0, tableWithCaptionAndOptions.Count - 1)];
        for (int i = 1; i < tableWithCaptionAndOptions.Count; i++)
        {
            opts[i - 1] = tableWithCaptionAndOptions[i];
        }

        DispatchTaskSay(context, caption, opts, invokeCallback);
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
            return unchecked((int)(long)Math.Truncate(dv));
        }

        return null;
    }

    public static double LuaToDouble(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0.0;
        }

        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double dv) ? dv : 0.0;
    }

    public static int LuaMod(int x, int y) => y == 0 ? 0 : x % y;

    public static int LuaMod(long x, long y) => y == 0 ? 0 : unchecked((int)(x % y));

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
        return FormatDateTime(now, format);
    }

    public static string FormatTime2String(string? format, double unixSeconds)
    {
        long sec = unchecked((long)Math.Truncate(unixSeconds));
        DateTime dt = DateTimeOffset.FromUnixTimeSeconds(sec).UtcDateTime;
        return FormatDateTime(dt, format);
    }

    private static string FormatDateTime(DateTime now, string? format)
    {
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

    public static PriseDialogOption[] BuildDynamicPriseOptions(
        IReadOnlyList<string> rawOptions,
        int declaredCount,
        Func<IScriptDialogContext, string, string[], ValueTask> invokeCallback)
    {
        if (declaredCount <= 0 || rawOptions.Count == 0)
        {
            return [];
        }

        int count = Math.Min(declaredCount, rawOptions.Count);
        var built = new PriseDialogOption[count];
        for (int i = 0; i < count; i++)
        {
            built[i] = ParseSinglePriseOption(rawOptions[i], invokeCallback);
        }

        return built;
    }

    public static PriseDialogOption ParseSinglePriseOption(
        string optionSpec,
        Func<IScriptDialogContext, string, string[], ValueTask> invokeCallback)
    {
        int s1 = optionSpec.IndexOf('/');
        if (s1 < 0)
        {
            return new PriseDialogOption(optionSpec, null);
        }

        int s2 = optionSpec.IndexOf('/', s1 + 1);
        int s3 = s2 >= 0 ? optionSpec.IndexOf('/', s2 + 1) : -1;
        if (s2 >= 0 && s3 > s2)
        {
            string display = optionSpec[..s2];
            string argSeg = optionSpec[(s2 + 1)..s3].Trim();
            string fnName = optionSpec[(s3 + 1)..].Trim();
            string[] cbArgs = argSeg.Length > 0 ? argSeg.Split(',', StringSplitOptions.TrimEntries) : [];
            return fnName.Length > 0
                ? new PriseDialogOption(display, ctx => invokeCallback(ctx, fnName, cbArgs))
                : new PriseDialogOption(display, null);
        }

        DialogOption simple = ParseSingleDialogOption(optionSpec, invokeCallback);
        return new PriseDialogOption(simple.TextUtf8, simple.Callback);
    }

    public static int GetBitTask(IScriptDialogContext context, int taskId, int bitPos, int bitCount)
    {
        if ((uint)taskId >= 6000u || bitPos < 0 || bitCount <= 0 || bitPos + bitCount > 32)
        {
            return 0;
        }

        uint raw = unchecked((uint)context.GetTask((ushort)taskId));
        if (raw == 0u)
        {
            return 0;
        }

        uint mask = bitCount == 32 ? 0xFFFFFFFFu : ((1u << bitCount) - 1u);
        return unchecked((int)((raw >> bitPos) & mask));
    }

    public static int SetBitTask(IScriptDialogContext context, int taskId, int bitPos, int bitCount, int value)
    {
        if ((uint)taskId >= 6000u || bitPos < 0 || bitCount <= 0 || bitPos + bitCount > 32)
        {
            return 0;
        }

        uint cur = unchecked((uint)context.GetTask((ushort)taskId));
        uint mask = bitCount == 32 ? 0xFFFFFFFFu : ((1u << bitCount) - 1u);
        uint updated = (cur & ~(mask << bitPos)) | ((unchecked((uint)value) & mask) << bitPos);
        context.SetTask((ushort)taskId, unchecked((int)updated));
        return 1;
    }

    private static readonly Dictionary<string, (ushort TaskId, int StartBit, int BitCount, int MaxVal)> VngChestExpBitTasks = new(StringComparer.Ordinal)
    {
        ["Tống Kim Bí Bảo"] = (2705, 1, 7, 50),
        ["Bảo rương vượt ải"] = (2705, 8, 7, 50),
        ["Bảo Rương Thủy Tặc"] = (2705, 15, 7, 50),
        ["Tín Sứ Bảo Rương"] = (2705, 22, 7, 50),
        ["Viêm Đế Bí Bảo"] = (2706, 1, 7, 50),
        ["Vệ Trụ Lễ Bao"] = (2706, 8, 7, 50),
        ["Công Thành Lễ Bao (mới)"] = (2706, 15, 7, 50),
        ["Thiên Trì Bí Bảo"] = (3080, 1, 7, 50),
        ["Bảo Rương Kiếm Gia"] = (3080, 9, 7, 50),
        ["Ngọc Bích Bảo Rương"] = (2706, 22, 7, 50),
    };

    public static void GrantVngChestExpAward(IScriptDialogContext context, long rawExpValue, string chestNameUtf8)
    {
        const int MaxExpPerDay = 50;
        const ushort TskDay = 2744;
        if (!VngChestExpBitTasks.TryGetValue(chestNameUtf8, out var taskSpec))
        {
            return;
        }

        int nowYmd = int.Parse(FormatLegacyDate("%Y%m%d"), CultureInfo.InvariantCulture);
        int curDay = context.GetTask(TskDay);
        if (nowYmd != curDay)
        {
            context.SetTask(TskDay, nowYmd);
            foreach (var spec in VngChestExpBitTasks.Values)
            {
                _ = SetBitTask(context, spec.TaskId, spec.StartBit, spec.BitCount, 0);
            }
        }
        else if (GetBitTask(context, taskSpec.TaskId, taskSpec.StartBit, taskSpec.BitCount) >= taskSpec.MaxVal)
        {
            context.SendSystemMessage(LuaFormat("Hôm nay đã nhận đủ <color=yellow>%d <color>triệu kinh nghiệm, không thể nhận thêm.", MaxExpPerDay));
            return;
        }

        int usedMillions = GetBitTask(context, taskSpec.TaskId, taskSpec.StartBit, taskSpec.BitCount);
        long value = rawExpValue;
        if (usedMillions + (value / 1_000_000.0) > MaxExpPerDay)
        {
            value = (long)((MaxExpPerDay - usedMillions) * 1_000_000L);
        }

        long expBefore = context.Experience;
        context.GrantExperience(value);
        long actualExp = Math.Max(0L, context.Experience - expBefore);
        int nextMillions = Math.Min(MaxExpPerDay, usedMillions + (int)Math.Ceiling(actualExp / 1_000_000.0));
        _ = SetBitTask(context, taskSpec.TaskId, taskSpec.StartBit, taskSpec.BitCount, nextMillions);
        if (actualExp < value)
        {
            context.SendSystemMessage(LuaFormat("Bạn đã nhận được %d điểm kinh nghiệm (kinh nghiệm không cộng dồn: %d, phần vượt quá cấp hiện tại bị mất)", actualExp, value));
        }
        else
        {
            context.SendSystemMessage(LuaFormat("Bạn đã nhận được %d điểm kinh nghiệm", actualExp));
        }
    }

    public static void GrantBingoBoxExpAward(IScriptDialogContext context, long amount, string boxNameUtf8)
    {
        context.GrantExperience(amount);
        context.SendSystemMessage($"Đạt được kinh nghiệm không thể cộng dồn {amount.ToString(CultureInfo.InvariantCulture)}");
        context.BroadcastGlobalNews(LuaFormat("<color=green>Chúc mừng cao thủ <color=yellow>%s<color=green> đã nhận được <color=yellow><%s><color=green> từ <color=yellow><%s><color>", context.RoleNameUtf8, $"{amount.ToString(CultureInfo.InvariantCulture)} điểm kinh nghiệm", boxNameUtf8));
    }

    public static int PlayerFunGetTaskDailyCount(IScriptDialogContext context, int taskId)
    {
        int val = context.GetTask((ushort)taskId);
        int curDate = int.Parse(FormatLegacyDate("%y%m%d"), CultureInfo.InvariantCulture);
        if (val / 256 != curDate)
        {
            val = curDate * 256;
            context.SetTask((ushort)taskId, val);
        }

        return val % 256;
    }

    public static int PlayerFunCheckTaskDaily(IScriptDialogContext context, int taskId, int targetValue, string? failMsg, string? op)
    {
        int count = PlayerFunGetTaskDailyCount(context, taskId);
        bool pass = (op?.Trim()) switch
        {
            "<" => count < targetValue,
            "<=" => count <= targetValue,
            ">" => count > targetValue,
            ">=" => count >= targetValue,
            "==" => count == targetValue,
            "~=" => count != targetValue,
            _ => false,
        };
        if (pass)
        {
            return 1;
        }

        ShowLibMessage(context, failMsg);
        return 0;
    }

    public static int PlayerFunAddTaskDaily(IScriptDialogContext context, int taskId, int delta)
    {
        int val = context.GetTask((ushort)taskId);
        int curDate = int.Parse(FormatLegacyDate("%y%m%d"), CultureInfo.InvariantCulture);
        if (val / 256 != curDate)
        {
            val = curDate * 256;
        }

        context.SetTask((ushort)taskId, val + delta);
        return 1;
    }

    public static int PlayerFunCheckFreeBagCellWH(IScriptDialogContext context, int width, int height, int count, string? failMsg)
    {
        if (context.CanFitBagItem((byte)Math.Clamp(width, 1, 10), (byte)Math.Clamp(height, 1, 10)))
        {
            return 1;
        }

        string resolved = string.Equals(failMsg, "default", StringComparison.Ordinal)
            ? LuaFormat("Để bảo đảm tài sản của đại hiệp, xin hãy để trống %d %dx%d hành trang", count, width, height)
            : failMsg ?? string.Empty;
        ShowLibMessage(context, resolved);
        return 0;
    }

    private static void ShowLibMessage(IScriptDialogContext context, string? msg)
    {
        if (string.IsNullOrEmpty(msg) || string.Equals(msg, "nomsg", StringComparison.Ordinal))
        {
            return;
        }

        if (msg.Contains("<msg>", StringComparison.Ordinal))
        {
            context.SendSystemMessage(msg.Replace("<msg>", string.Empty, StringComparison.Ordinal));
        }
        else
        {
            context.Talk([msg]);
        }
    }

    private sealed class ObjBufferSlot
    {
        public string LastMessage { get; set; } = string.Empty;
    }

    private static readonly System.Threading.AsyncLocal<Dictionary<int, ObjBufferSlot>?> CurrentObjBuffers = new();
    private static readonly System.Threading.AsyncLocal<int> NextObjBufferId = new();

    public static int CreateObjBuffer()
    {
        var map = CurrentObjBuffers.Value ??= new Dictionary<int, ObjBufferSlot>();
        int id = NextObjBufferId.Value + 1;
        NextObjBufferId.Value = id;
        map[id] = new ObjBufferSlot();
        return id;
    }

    public static void PushObjBufferObject(int handle, string? text)
    {
        if (CurrentObjBuffers.Value is { } map && map.TryGetValue(handle, out var slot))
        {
            slot.LastMessage = text ?? string.Empty;
        }
    }

    public static void RemoteExecuteBroadcast(IScriptDialogContext context, string? scriptPath, string? fnName, int handle)
    {
        _ = scriptPath;
        if (string.Equals(fnName, "broadcast", StringComparison.Ordinal) &&
            CurrentObjBuffers.Value is { } map &&
            map.TryGetValue(handle, out var slot))
        {
            context.BroadcastGlobalNews(slot.LastMessage);
        }
    }

    public static void ReleaseObjBuffer(int handle)
    {
        _ = CurrentObjBuffers.Value?.Remove(handle);
    }

    public static RuntimeAwardNode? GetAwardAt1Based(RuntimeAwardNode[]? arr, int index1Based) =>
        arr is not null && index1Based >= 1 && index1Based <= arr.Length ? arr[index1Based - 1] : null;

    public static RuntimeAwardNode? GetMapAwardAt(FrozenDictionary<int, RuntimeAwardNode>? map, int key) =>
        map is not null && map.TryGetValue(key, out RuntimeAwardNode? val) ? val : null;

    public static FrozenDictionary<string, int>? GetIntRecordAt(FrozenDictionary<int, FrozenDictionary<string, int>>? map, int key) =>
        map is not null && map.TryGetValue(key, out FrozenDictionary<string, int>? val) ? val : null;

    public static int GetFieldInt(FrozenDictionary<string, int>? rec, string fieldName) =>
        rec is not null && rec.TryGetValue(fieldName, out int val) ? val : 0;
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
            "System.Collections.Frozen",
            "System.Collections.Generic",
            "System.Threading",
            "System.Threading.Tasks",
            "JXReborn.Primitives.Characters",
            "JXReborn.Primitives.Combat",
            "JXReborn.Primitives.Items",
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
            "System.Collections.Frozen",
            "System.Collections.Generic",
            "System.Threading",
            "System.Threading.Tasks",
            "JXReborn.Primitives.Characters",
            "JXReborn.Primitives.Combat",
            "JXReborn.Primitives.Items",
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
            "System.Collections.Frozen",
            "System.Collections.Generic",
            "System.Threading.Tasks",
            "JXReborn.Primitives.Characters",
            "JXReborn.Primitives.Combat",
            "JXReborn.Primitives.Items",
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

            LuaValueType vt = script.TopLevelConstantTypes[name];
            if (vt == LuaValueType.ClosureHelper)
            {
                continue;
            }

            if (init is LuaTableLiteralExpr tbl && vt is LuaValueType.StringArray or LuaValueType.IntArray)
            {
                topLevelConstArrays[name] = tbl;
            }

            string csType = EmitTopLevelConstantType(name, vt, script, isMonsterScript);
            string initCode = EmitTopLevelConstantInitializer(name, vt, init, scriptIndex, script, constFieldMap, constExprMap, isMonsterScript);
            sb.AppendLine();
            sb.Append($"    private static readonly {csType} {fieldName} = {initCode};");
            sb.AppendLine();
        }

        var functionsByName = script.Program.Functions.ToDictionary(static f => f.Name, StringComparer.Ordinal);
        bool needsCallbackRouter = !isMonsterScript && script.Program.Functions.Count > 1;

        if (needsCallbackRouter)
        {
            EmitScriptCallbackRouter(sb, scriptIndex, script, ctxType);
        }

        foreach (LuaFunctionDecl fn in script.Program.Functions)
        {
            _currentFnLocalTupleTables = script.FunctionLocalTupleTables?.GetValueOrDefault(fn.Name);
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
                    paramDeclParts.Add($"{ToCSharpType(paramTypes[p], isMonsterScript)} {csParamName}");
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
                ? "(" + string.Join(", ", retTuple.Select(t => ToCSharpType(t, isMonsterScript))) + ")"
                : ToCSharpType(retType, isMonsterScript);

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

            if (isItemScript && isEntry && paramTypes.Count == 1 && fn.Parameters.Count >= 1)
            {
                string rawItemParam = fn.Parameters[0];
                string csItemParam = $"p_{SanitizeIdentifier(rawItemParam)}";
                localVarMap[rawItemParam] = csItemParam;
                paramTypeLookup[rawItemParam] = LuaValueType.Int;
                sb.AppendLine($"        int {csItemParam} = (int)context.RuntimeItemId;");
            }

            IReadOnlyDictionary<string, LuaValueType> locals = script.FunctionLocalTypes[fn.Name];
            foreach (KeyValuePair<string, LuaValueType> kv in locals.OrderBy(static k => k.Key, StringComparer.Ordinal))
            {
                if (localVarMap.ContainsKey(kv.Key))
                {
                    continue;
                }

                string csName = $"v_{SanitizeIdentifier(kv.Key)}";
                localVarMap[kv.Key] = csName;
                if (kv.Value == LuaValueType.ConstTupleRow &&
                    _currentFnLocalTupleTables is not null &&
                    _currentFnLocalTupleTables.TryGetValue(kv.Key, out string? rowTblName) &&
                    script.ConstTupleColumnTypes is not null &&
                    script.ConstTupleColumnTypes.TryGetValue(rowTblName, out IReadOnlyList<LuaValueType>? rowCols))
                {
                    string tupleCsType = "(" + string.Join(", ", rowCols.Select(c => ToCSharpType(c, isMonsterScript))) + ")";
                    sb.AppendLine($"        {tupleCsType} {csName} = default;");
                }
                else
                {
                    sb.AppendLine($"        {ToCSharpType(kv.Value, isMonsterScript)} {csName} = {DefaultValueExpr(kv.Value, isMonsterScript)};");
                }
            }

            var fnScopeTypes = new Dictionary<string, LuaValueType>(locals, StringComparer.Ordinal);
            foreach (KeyValuePair<string, LuaValueType> pkv in paramTypeLookup)
            {
                fnScopeTypes[pkv.Key] = pkv.Value;
            }

            var fnConstArrays = new Dictionary<string, LuaTableLiteralExpr>(topLevelConstArrays, StringComparer.Ordinal);
            if (script.FunctionPreloadedTaskGlobals is not null &&
                script.FunctionPreloadedTaskGlobals.TryGetValue(fn.Name, out IReadOnlyList<(string VarName, ushort TaskId)>? preloadedTasks))
            {
                foreach ((string varName, ushort taskId) in preloadedTasks)
                {
                    sb.AppendLine($"        {localVarMap[varName]} = context.GetTask({taskId});");
                }
            }

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
                        sb.AppendLine($"        return {DefaultReturnExpr(retType, retTuple, isMonsterScript)};");
                    }
                }
                else if (!isEntry && (retTuple is not null || retType != LuaValueType.Void))
                {
                    sb.AppendLine($"        return {DefaultReturnExpr(retType, retTuple, isMonsterScript)};");
                }
            }

            sb.AppendLine("    }");
        }

        _currentFnLocalTupleTables = null;
    }

    private string EmitTopLevelConstantType(string constName, LuaValueType vt, CompiledLuaScript script, bool isMonsterScript)
    {
        if (vt == LuaValueType.ConstTupleTable &&
            script.ConstTupleColumnTypes is not null &&
            script.ConstTupleColumnTypes.TryGetValue(constName, out IReadOnlyList<LuaValueType>? cols))
        {
            return "(" + string.Join(", ", cols.Select(c => ToCSharpType(c, isMonsterScript))) + ")[]";
        }

        return ToCSharpType(vt, isMonsterScript);
    }

    private string EmitTopLevelConstantInitializer(
        string constName,
        LuaValueType vt,
        LuaExpression init,
        int scriptIndex,
        CompiledLuaScript script,
        IReadOnlyDictionary<string, string> constFieldMap,
        IReadOnlyDictionary<string, LuaExpression> constExprMap,
        bool isMonsterScript)
    {
        if (vt == LuaValueType.AwardTemplate &&
            script.TopLevelAwardNodes is not null &&
            script.TopLevelAwardNodes.TryGetValue(constName, out CompiledAwardNode? awardNode))
        {
            return EmitAwardNodeExpr(awardNode);
        }

        if (vt == LuaValueType.ConstTupleTable &&
            init is LuaTableLiteralExpr outer &&
            script.ConstTupleColumnTypes is not null &&
            script.ConstTupleColumnTypes.TryGetValue(constName, out IReadOnlyList<LuaValueType>? cols))
        {
            var rowStrs = new List<string>(outer.Elements.Count);
            foreach (LuaExpression rowEl in outer.Elements)
            {
                var rowTbl = (LuaTableLiteralExpr)rowEl;
                var cellStrs = new List<string>(cols.Count);
                for (int c = 0; c < cols.Count; c++)
                {
                    if (c < rowTbl.Elements.Count)
                    {
                        cellStrs.Add(EmitAssignableExpression(rowTbl.Elements[c], cols[c], scriptIndex, script, null, constFieldMap, constExprMap, null, isMonsterScript));
                    }
                    else
                    {
                        cellStrs.Add(DefaultValueExpr(cols[c], isMonsterScript));
                    }
                }

                rowStrs.Add("(" + string.Join(", ", cellStrs) + ")");
            }

            return "[" + string.Join(", ", rowStrs) + "]";
        }

        if (vt == LuaValueType.IntKeyedAwardTemplateMap &&
            script.TopLevelAwardMaps is not null &&
            script.TopLevelAwardMaps.TryGetValue(constName, out IReadOnlyDictionary<int, CompiledAwardNode>? awardMap))
        {
            var pairs = new List<string>(awardMap.Count);
            foreach (KeyValuePair<int, CompiledAwardNode> kv in awardMap.OrderBy(static x => x.Key))
            {
                pairs.Add($"[{kv.Key.ToString(CultureInfo.InvariantCulture)}] = {EmitAwardNodeExpr(kv.Value)}");
            }

            return $"new Dictionary<int, GeneratedLuaRuntime.RuntimeAwardNode> {{ {string.Join(", ", pairs)} }}.ToFrozenDictionary()";
        }

        if (vt == LuaValueType.IntKeyedIntRecordMap &&
            script.TopLevelIntRecordMaps is not null &&
            script.TopLevelIntRecordMaps.TryGetValue(constName, out IReadOnlyDictionary<int, IReadOnlyDictionary<string, int>>? recMap))
        {
            var outerPairs = new List<string>(recMap.Count);
            foreach (KeyValuePair<int, IReadOnlyDictionary<string, int>> kv in recMap.OrderBy(static x => x.Key))
            {
                var innerPairs = kv.Value
                    .OrderBy(static f => f.Key, StringComparer.Ordinal)
                    .Select(f => $"[{ToEscapedStringLiteral(f.Key)}] = {f.Value.ToString(CultureInfo.InvariantCulture)}");
                outerPairs.Add($"[{kv.Key.ToString(CultureInfo.InvariantCulture)}] = new Dictionary<string, int>(StringComparer.Ordinal) {{ {string.Join(", ", innerPairs)} }}.ToFrozenDictionary(StringComparer.Ordinal)");
            }

            return $"new Dictionary<int, FrozenDictionary<string, int>> {{ {string.Join(", ", outerPairs)} }}.ToFrozenDictionary()";
        }

        if (vt is LuaValueType.IntKeyedIntMap or LuaValueType.IntKeyedStringMap or LuaValueType.IntKeyedIntArrayMap &&
            init is LuaKeyedTableLiteralExpr ktbl)
        {
            string valCsType = vt switch
            {
                LuaValueType.IntKeyedStringMap => "string",
                LuaValueType.IntKeyedIntArrayMap => "int[]",
                _ => "int",
            };
            var pairs = new List<string>(ktbl.Entries.Count);
            foreach (LuaTableFieldEntry fe in ktbl.Entries)
            {
                string kStr = EmitIntExpr(fe.BracketKey!, scriptIndex, script, null, constFieldMap, constExprMap, null, isMonsterScript);
                string vStr = EmitExpression(fe.Value, scriptIndex, script, null, constFieldMap, constExprMap, null, isMonsterScript);
                pairs.Add($"[{kStr}] = {vStr}");
            }

            return $"new Dictionary<int, {valCsType}> {{ {string.Join(", ", pairs)} }}.ToFrozenDictionary()";
        }

        return EmitExpression(init, scriptIndex, script, null, constFieldMap, constExprMap, null, isMonsterScript);
    }

    private static string EmitAwardNodeExpr(CompiledAwardNode node)
    {
        string kindName = node.Kind switch
        {
            CompiledAwardNodeKind.SequentialGroup => "GeneratedLuaRuntime.RuntimeAwardKind.Sequential",
            CompiledAwardNodeKind.RandomWeightedGroup => "GeneratedLuaRuntime.RuntimeAwardKind.RandomWeighted",
            CompiledAwardNodeKind.LeafItem => "GeneratedLuaRuntime.RuntimeAwardKind.Item",
            CompiledAwardNodeKind.LeafGoldEquip => "GeneratedLuaRuntime.RuntimeAwardKind.GoldEquip",
            CompiledAwardNodeKind.LeafExp => "GeneratedLuaRuntime.RuntimeAwardKind.Exp",
            CompiledAwardNodeKind.LeafExpTl => "GeneratedLuaRuntime.RuntimeAwardKind.ExpTl",
            CompiledAwardNodeKind.LeafMoney => "GeneratedLuaRuntime.RuntimeAwardKind.Money",
            CompiledAwardNodeKind.LeafRepute => "GeneratedLuaRuntime.RuntimeAwardKind.Repute",
            CompiledAwardNodeKind.LeafChestExpAward => "GeneratedLuaRuntime.RuntimeAwardKind.ChestExpAward",
            CompiledAwardNodeKind.LeafBingoBoxExpAward => "GeneratedLuaRuntime.RuntimeAwardKind.BingoBoxExpAward",
            _ => "GeneratedLuaRuntime.RuntimeAwardKind.Sequential",
        };

        if (node.Children is { Count: > 0 })
        {
            string childrenCode = "[" + string.Join(", ", node.Children.Select(EmitAwardNodeExpr)) + "]";
            return $"new GeneratedLuaRuntime.RuntimeAwardNode({kindName}, Children: {childrenCode})";
        }

        string rateLit = node.RatePercent.ToString("R", CultureInfo.InvariantCulture);
        string isEquipLit = node.NumericAmount == 1 && node.Kind == CompiledAwardNodeKind.LeafItem ? "true" : "false";
        string rngLit = node.ConsumeRandomSeriesRoll ? "true" : "false";
        string fixedSeriesLit = node.FixedSeries.HasValue ? node.FixedSeries.Value.ToString(CultureInfo.InvariantCulture) : "null";
        string catNameLit = ToEscapedStringLiteral(node.CatalogItemNameUtf8 ?? string.Empty);
        string cbKindLit = node.CallbackKind is not null ? ToEscapedStringLiteral(node.CallbackKind) : "null";
        string cbTitleLit = node.CallbackTitleUtf8 is not null ? ToEscapedStringLiteral(node.CallbackTitleUtf8) : "null";
        string specLit = node.SpecParams is { Count: > 0 }
            ? "[" + string.Join(", ", node.SpecParams.Select(static s => s.ToString(CultureInfo.InvariantCulture))) + "]"
            : "null";

        return $"new GeneratedLuaRuntime.RuntimeAwardNode({kindName}, RatePercent: {rateLit}, Count: {node.Count}, ItemCode: {ToEscapedStringLiteral(node.ItemCode)}, ItemNameUtf8: {ToEscapedStringLiteral(node.ItemNameUtf8)}, MaxStackCount: {node.MaxStackCount}, IsEquipment: {isEquipLit}, ConsumeRandomSeriesRoll: {rngLit}, FixedSeries: {fixedSeriesLit}, BindStateRaw: {node.BindStateRaw}, NumericAmount: {node.NumericAmount}L, CatalogItemNameUtf8: {catNameLit}, CallbackKind: {cbKindLit}, CallbackTitleUtf8: {cbTitleLit}, ExpiredTimeMinutes: {node.ExpiredTimeMinutes}, SpecParams: {specLit})";
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
            if (pTypes.Any(static t => t is not (LuaValueType.Int or LuaValueType.String)))
            {
                continue;
            }

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
            script.FunctionParamTypes.GetValueOrDefault(f.Name, []).All(static t => t is LuaValueType.Int or LuaValueType.String) &&
            script.FunctionIsAsync[f.Name] &&
            ((script.FunctionReturnTuples is not null && script.FunctionReturnTuples.ContainsKey(f.Name)) ||
             script.FunctionReturnTypes[f.Name] != LuaValueType.Void)))
        {
            sb.AppendLine();
            sb.AppendLine("    private static async ValueTask IgnoreResultAsync<T>(ValueTask<T> task) => _ = await task;");
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
                    if (lhsType == LuaValueType.DialogOptionList && ld.Initializer is LuaTableLiteralExpr { Elements.Count: > 0 } optListLit)
                    {
                        sb.AppendLine($"{indent}{lhs} = new List<DialogOption>();");
                        foreach (LuaExpression el in optListLit.Elements)
                        {
                            string optExpr = EmitDialogOptionTupleWithCaptures(sb, indent, (LuaTableLiteralExpr)el, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript, ref tempCounter);
                            sb.AppendLine($"{indent}{lhs}.Add({optExpr});");
                        }

                        constArrays.Remove(ld.Name);
                        break;
                    }

                    string rhs = ld.Initializer is LuaTableLiteralExpr { Elements.Count: 0 }
                        ? DefaultValueExpr(lhsType, isMonsterScript)
                        : EmitAssignableExpression(ld.Initializer, lhsType, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    sb.AppendLine($"{indent}{lhs} = {rhs};");
                    if (ld.Initializer is LuaTableLiteralExpr { Elements.Count: > 0 } tbl && lhsType is LuaValueType.StringArray or LuaValueType.IntArray)
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

                    if (asg.TargetName == "PlayerIndex")
                    {
                        string piVal = EmitIntExpr(asg.Value, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                        sb.AppendLine($"{indent}context.CurrentPlayerIndex = {piVal};");
                        break;
                    }

                    string lhs = localVarMap[asg.TargetName];
                    LuaValueType lhsType = fnScopeTypes[asg.TargetName];
                    if (lhsType == LuaValueType.DialogOptionList && asg.Value is LuaTableLiteralExpr { Elements.Count: > 0 } optListLit)
                    {
                        sb.AppendLine($"{indent}{lhs} = new List<DialogOption>();");
                        foreach (LuaExpression el in optListLit.Elements)
                        {
                            string optExpr = EmitDialogOptionTupleWithCaptures(sb, indent, (LuaTableLiteralExpr)el, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript, ref tempCounter);
                            sb.AppendLine($"{indent}{lhs}.Add({optExpr});");
                        }

                        constArrays.Remove(asg.TargetName);
                        break;
                    }

                    string rhs = asg.Value is LuaTableLiteralExpr { Elements.Count: 0 }
                        ? DefaultValueExpr(lhsType, isMonsterScript)
                        : EmitAssignableExpression(asg.Value, lhsType, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    sb.AppendLine($"{indent}{lhs} = {rhs};");
                    if (asg.Value is LuaTableLiteralExpr { Elements.Count: > 0 } tbl && lhsType is LuaValueType.StringArray or LuaValueType.IntArray)
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

                case LuaMemberAssignStmt mas:
                {
                    LuaValueType targetType = InferExpressionType(mas.Target, script, fnScopeTypes);
                    if (targetType == LuaValueType.DialogBuilder && mas.FieldName == "szTitleMsg")
                    {
                        string builderExpr = EmitExpression(mas.Target, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                        string valStr = EmitAsStringExpression(mas.Value, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                        sb.AppendLine($"{indent}{builderExpr}.TitleMsg = {valStr};");
                    }
                    else
                    {
                        // Metadata-only runtime assignment on AwardTemplate (`%tbItem.nExpiredTime = ...`): evaluate RHS cleanly
                        string valExpr = EmitExpression(mas.Value, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                        sb.AppendLine($"{indent}_ = {valExpr};");
                    }

                    break;
                }

                case LuaMemberCallStmt mcs:
                    EmitMemberCallStatement(sb, mcs.Call, indent, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript, ref tempCounter);
                    break;

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
                    EmitCallStatement(sb, cs.Call, indent, scriptIndex, script, isItemScript, isMonsterScript, hasRouter, functionsByName, fnScopeTypes, constFieldMap, constExprMap, localVarMap, constArrays, ref tempCounter);
                    break;

                case LuaReturnStmt ret:
                    EmitReturnStatement(sb, ret, indent, scriptIndex, script, isEntry, fnAsync, retType, retTuple, isItemScript, isMonsterScript, fnScopeTypes, constFieldMap, constExprMap, localVarMap);
                    return true;
            }
        }

        return false;
    }

    private void EmitMemberCallStatement(
        StringBuilder sb,
        LuaMemberCallExpr mcall,
        string indent,
        int scriptIndex,
        CompiledLuaScript script,
        IReadOnlyDictionary<string, LuaValueType> fnScopeTypes,
        IReadOnlyDictionary<string, string> constFieldMap,
        IReadOnlyDictionary<string, LuaExpression> constExprMap,
        IReadOnlyDictionary<string, string> localVarMap,
        bool isMonsterScript,
        ref int tempCounter)
    {
        string? recvName = mcall.Receiver switch
        {
            LuaIdentifierExpr rid => rid.Name,
            LuaUpvalueExpr uid => uid.Name,
            _ => null,
        };
        if (recvName is "tbAwardTemplet" or "PlayerFunLib" or "ObjBuffer" or "tbLog" or "tbVngTransLog" or "tbvng_ChestExpAward")
        {
            sb.AppendLine($"{indent}_ = {EmitMemberCallExpression(mcall, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript)};");
            return;
        }

        if (mcall.Receiver is LuaIdentifierExpr recvId && localVarMap.TryGetValue(recvId.Name, out string? csBuilder))
        {
            if (mcall.MemberName == "Show")
            {
                sb.AppendLine($"{indent}{csBuilder}.Show(context);");
                return;
            }

            if (mcall.MemberName == "AddOptEntry")
            {
                var syntheticTuple = new LuaTableLiteralExpr(mcall.Arguments, mcall.Line);
                string optExpr = EmitDialogOptionTupleWithCaptures(sb, indent, syntheticTuple, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript, ref tempCounter);
                sb.AppendLine($"{indent}{{ DialogOption __opt = {optExpr}; {csBuilder}.AddOptEntry(__opt.TextUtf8, __opt.Callback); }}");
                return;
            }
        }

        throw new InvalidOperationException($"Unsupported member call statement '{mcall.MemberName}'.");
    }

    private string EmitMemberCallExpression(
        LuaMemberCallExpr mcall,
        int scriptIndex,
        CompiledLuaScript script,
        IReadOnlyDictionary<string, LuaValueType>? fnScopeTypes,
        IReadOnlyDictionary<string, string> constFieldMap,
        IReadOnlyDictionary<string, LuaExpression> constExprMap,
        IReadOnlyDictionary<string, string>? localVarMap,
        bool isMonsterScript)
    {
        if (mcall.Receiver is LuaIdentifierExpr { Name: "DailogClass" } && mcall.MemberName == "new")
        {
            string argStr = mcall.Arguments.Count == 1
                ? EmitAsStringExpression(mcall.Arguments[0], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript)
                : "string.Empty";
            return $"new GeneratedLuaRuntime.DailogBuilder({argStr})";
        }

        string? recvName = mcall.Receiver switch
        {
            LuaIdentifierExpr rid2 => rid2.Name,
            LuaUpvalueExpr uid2 => uid2.Name,
            _ => null,
        };
        if (recvName == "tbAwardTemplet" && mcall.MemberName is "GiveAwardByList" or "Give")
        {
            string nodeExpr;
            if (mcall.Arguments[0] is LuaIdentifierExpr awardId && constFieldMap.TryGetValue(awardId.Name, out string? fieldRef))
            {
                nodeExpr = fieldRef;
            }
            else if (InferExpressionType(mcall.Arguments[0], script, fnScopeTypes) == LuaValueType.AwardTemplate)
            {
                nodeExpr = EmitExpression(mcall.Arguments[0], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
            }
            else
            {
                var tempValidator = new LuaScriptSemanticValidator(_eventItemCodeByDetailId, _itemCatalogIndex);
                _ = tempValidator.TryCompileAwardNode(mcall.Arguments[0], constExprMap, out CompiledAwardNode? compiledNode, out _);
                nodeExpr = EmitAwardNodeExpr(compiledNode!);
            }

            string countExpr = "1";
            if (mcall.MemberName == "GiveAwardByList" && mcall.Arguments.Count == 3)
            {
                countExpr = EmitIntExpr(mcall.Arguments[2], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
            }
            else if (mcall.MemberName == "Give" && mcall.Arguments.Count >= 2)
            {
                countExpr = EmitIntExpr(mcall.Arguments[1], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
            }

            return $"GeneratedLuaRuntime.GiveAward(context, {nodeExpr}, {countExpr})";
        }

        if (recvName == "PlayerFunLib")
        {
            string AInt(int idx) => EmitIntExpr(mcall.Arguments[idx], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
            string AStr(int idx) => EmitAsStringExpression(mcall.Arguments[idx], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
            return mcall.MemberName switch
            {
                "GetTaskDailyCount" => $"GeneratedLuaRuntime.PlayerFunGetTaskDailyCount(context, {AInt(0)})",
                "AddTaskDaily" => $"GeneratedLuaRuntime.PlayerFunAddTaskDaily(context, {AInt(0)}, {AInt(1)})",
                "CheckTaskDaily" => $"GeneratedLuaRuntime.PlayerFunCheckTaskDaily(context, {AInt(0)}, {AInt(1)}, {AStr(2)}, {AStr(3)})",
                "CheckFreeBagCellWH" => $"GeneratedLuaRuntime.PlayerFunCheckFreeBagCellWH(context, {AInt(0)}, {AInt(1)}, {AInt(2)}, {AStr(3)})",
                _ => "0",
            };
        }

        if (mcall.IsColonCall && recvName == "ObjBuffer" && mcall.MemberName == "PushObject")
        {
            string hExpr = EmitIntExpr(mcall.Arguments[0], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
            string sExpr = EmitAsStringExpression(mcall.Arguments[1], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
            return $"(GeneratedLuaRuntime.PushObjBufferObject({hExpr}, {sExpr}), 0).Item2";
        }

        if (mcall.IsColonCall && recvName is "tbLog" or "tbVngTransLog")
        {
            return "0";
        }

        if (mcall.IsColonCall && recvName == "tbvng_ChestExpAward" && mcall.MemberName == "ExpAward")
        {
            string vExpr = EmitDoubleExpr(mcall.Arguments[0], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
            string nExpr = EmitAsStringExpression(mcall.Arguments[1], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
            return $"(GeneratedLuaRuntime.GrantVngChestExpAward(context, (long)Math.Truncate({vExpr}), {nExpr}), 0).Item2";
        }

        throw new InvalidOperationException($"Unsupported member call expression '{mcall.MemberName}'.");
    }

    private string EmitDialogOptionTupleWithCaptures(
        StringBuilder sb,
        string indent,
        LuaTableLiteralExpr optTuple,
        int scriptIndex,
        CompiledLuaScript script,
        IReadOnlyDictionary<string, LuaValueType>? fnScopeTypes,
        IReadOnlyDictionary<string, string> constFieldMap,
        IReadOnlyDictionary<string, LuaExpression> constExprMap,
        IReadOnlyDictionary<string, string>? localVarMap,
        bool isMonsterScript,
        ref int tempCounter)
    {
        string labelExpr = EmitAsStringExpression(optTuple.Elements[0], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
        if (optTuple.Elements.Count == 1 || optTuple.Elements[1] is LuaNilExpr)
        {
            return $"new DialogOption({labelExpr}, null)";
        }

        string cbName = ((LuaIdentifierExpr)optTuple.Elements[1]).Name;
        if (optTuple.Elements.Count == 2)
        {
            return $"new DialogOption({labelExpr}, {BuildZeroArgCallbackDelegateExpr(scriptIndex, script, cbName)})";
        }

        var paramTbl = (LuaTableLiteralExpr)optTuple.Elements[2];
        if (paramTbl.Elements.Count == 0)
        {
            return $"new DialogOption({labelExpr}, {BuildZeroArgCallbackDelegateExpr(scriptIndex, script, cbName)})";
        }

        IReadOnlyList<LuaValueType> expectedParams = script.FunctionParamTypes.GetValueOrDefault(cbName, []);
        var capVars = new List<string>(paramTbl.Elements.Count);
        for (int p = 0; p < paramTbl.Elements.Count; p++)
        {
            LuaValueType pt = p < expectedParams.Count ? expectedParams[p] : LuaValueType.Int;
            string capName = $"__cap{tempCounter++}";
            string argVal = EmitAssignableExpression(paramTbl.Elements[p], pt, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
            sb.AppendLine($"{indent}{ToCSharpType(pt, isMonsterScript)} {capName} = {argVal};");
            capVars.Add(capName);
        }

        string methodName = MakeFunctionMethodName(scriptIndex, cbName);
        string iface = ResolveContextInterfaceName(script.Kind);
        string ctxArg = iface == "IScriptDialogContext" ? "ctx" : $"({iface})ctx";
        bool cbAsync = script.FunctionIsAsync[cbName];
        bool cbHasRet = (script.FunctionReturnTuples is not null && script.FunctionReturnTuples.ContainsKey(cbName)) ||
                        script.FunctionReturnTypes[cbName] != LuaValueType.Void;

        string callArgs = isMonsterScript
            ? $"{ctxArg}, {string.Join(", ", capVars)}"
            : $"{ctxArg}, {string.Join(", ", capVars)}, CancellationToken.None";

        string lambda;
        if (isMonsterScript)
        {
            lambda = cbHasRet
                ? $"ctx => {{ _ = {methodName}({callArgs}); return ValueTask.CompletedTask; }}"
                : $"ctx => {{ {methodName}({callArgs}); return ValueTask.CompletedTask; }}";
        }
        else if (!cbAsync && cbHasRet)
        {
            lambda = $"ctx => {{ _ = {methodName}({callArgs}); return ValueTask.CompletedTask; }}";
        }
        else if (cbAsync && cbHasRet)
        {
            lambda = $"async ctx => {{ _ = await {methodName}({callArgs}); }}";
        }
        else
        {
            lambda = $"ctx => {methodName}({callArgs})";
        }

        return $"new DialogOption({labelExpr}, {lambda})";
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
                string callExpr = EmitCallExpression(singleCall, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript, preserveTuple: true);
                sb.AppendLine($"{indent}var {tupVar} = {callExpr};");
                for (int i = 0; i < targetNames.Count && i < tupTypes.Count; i++)
                {
                    if (targetNames[i] == "_") continue;
                    sb.AppendLine($"{indent}{localVarMap[targetNames[i]]} = {tupVar}.Item{i + 1};");
                }

                return;
            }

            if (singleCall.CalleeName == "GetTong" && singleCall.Arguments.Count == 0)
            {
                string[] tExprs = ["string.Empty", "0"];
                for (int i = 0; i < targetNames.Count && i < tExprs.Length; i++)
                {
                    if (targetNames[i] == "_") continue;
                    sb.AppendLine($"{indent}{localVarMap[targetNames[i]]} = {tExprs[i]};");
                }

                return;
            }

            if (singleCall.CalleeName == "GetItemProp" && singleCall.Arguments.Count == 1)
            {
                string idxCode = EmitIntExpr(singleCall.Arguments[0], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                int tId = tempCounter++;
                string propVar = $"__prop{tId}";
                sb.AppendLine($"{indent}_ = {idxCode};");
                sb.AppendLine($"{indent}var {propVar} = (0, 0, context.ParticularType, context.ItemTemplateId > 0 ? 1 : 0, (int)context.Series, (int)context.Lucky);");
                string[] pExprs = [$"{propVar}.Item1", $"{propVar}.Item2", $"{propVar}.Item3", $"{propVar}.Item4", $"{propVar}.Item5", $"{propVar}.Item6"];
                for (int i = 0; i < targetNames.Count && i < pExprs.Length; i++)
                {
                    if (targetNames[i] == "_") continue;
                    sb.AppendLine($"{indent}{localVarMap[targetNames[i]]} = {pExprs[i]};");
                }

                return;
            }

            if (singleCall.CalleeName == "RevID2WXY")
            {
                int tId = tempCounter++;
                string revVar = $"__rev{tId}";
                if (singleCall.Arguments.Count == 1 && singleCall.Arguments[0] is LuaCallExpr { CalleeName: "GetPlayerRev" })
                {
                    sb.AppendLine($"{indent}var {revVar} = context.ResolveRevivalPosition(context.RevivalMapId, context.RevivalPointId);");
                }
                else
                {
                    string mExpr = EmitIntExpr(singleCall.Arguments[0], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    string rExpr = EmitIntExpr(singleCall.Arguments[1], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    sb.AppendLine($"{indent}var {revVar} = context.ResolveRevivalPosition((ushort)({mExpr}), {rExpr});");
                }

                string[] exprs = [$"(int){revVar}.MapId", $"{revVar}.MpsX", $"{revVar}.MpsY"];
                for (int i = 0; i < targetNames.Count && i < exprs.Length; i++)
                {
                    if (targetNames[i] == "_") continue;
                    sb.AppendLine($"{indent}{localVarMap[targetNames[i]]} = {exprs[i]};");
                }

                return;
            }
        }

        int baseId = tempCounter++;
        for (int i = 0; i < values.Count; i++)
        {
            if (i < targetNames.Count && targetNames[i] != "_")
            {
                LuaValueType targetType = fnScopeTypes[targetNames[i]];
                string rhs = EmitAssignableExpression(values[i], targetType, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                sb.AppendLine($"{indent}{ToCSharpType(targetType, isMonsterScript)} __rhs{baseId}_{i} = {rhs};");
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
                sb.AppendLine($"{indent}return {DefaultValueExpr(retType, isMonsterScript)};");
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
                : DefaultValueExpr(retType, isMonsterScript);
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
        IReadOnlyDictionary<string, LuaTableLiteralExpr> constArrays,
        ref int tempCounter)
    {
        string api = call.CalleeName;
        IReadOnlyList<LuaExpression> args = call.Arguments;

        string E(int idx) => EmitExpression(args[idx], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
        string EInt(int idx) => EmitIntExpr(args[idx], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
        string EStr(int idx) => EmitAsStringExpression(args[idx], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);

        // Local SyncCallback parameter call (`fnCallback()`)
        if (fnScopeTypes.TryGetValue(api, out LuaValueType localCalleeType) && localCalleeType == LuaValueType.SyncCallback &&
            localVarMap.TryGetValue(api, out string? csCallbackVar))
        {
            if (isMonsterScript)
            {
                sb.AppendLine($"{indent}{csCallbackVar}(context);");
            }
            else
            {
                sb.AppendLine($"{indent}await {csCallbackVar}(context, cancellationToken);");
            }

            return;
        }

        if (functionsByName.ContainsKey(api))
        {
            string invokeExpr = BuildHelperInvocationExpr(call, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
            if (isMonsterScript)
            {
                bool hasRet = (script.FunctionReturnTuples is not null && script.FunctionReturnTuples.ContainsKey(api)) ||
                              script.FunctionReturnTypes[api] != LuaValueType.Void;
                sb.AppendLine(hasRet ? $"{indent}_ = {invokeExpr};" : $"{indent}{invokeExpr};");
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
            case "LG_ApplyDoScript":
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
            case "Add120SkillExp":
                sb.AppendLine($"{indent}_ = GeneratedLuaRuntime.Add120SkillExp(context, {EInt(0)});");
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
            case "SetPKFlag":
                sb.AppendLine($"{indent}context.SetPkMode((PlayerPkMode)({EInt(0)}));");
                break;
            case "ForbidChangePK":
                sb.AppendLine($"{indent}context.IsPkSwitchForbidden = ({EInt(0)}) == 1;");
                break;
            case "SetPunish":
                sb.AppendLine($"{indent}context.IsDeathPunishEnabled = ({EInt(0)}) != 0;");
                break;
            case "SetLogoutRV":
                sb.AppendLine($"{indent}context.UseReviveIdOnLogin = ({EInt(0)}) != 0;");
                break;
            case "DisabledUseTownP":
                sb.AppendLine($"{indent}context.IsTownPortalDisabled = ({EInt(0)}) != 0;");
                break;
            case "SetTimer":
                sb.AppendLine($"{indent}context.SetScriptTimer({EInt(0)}, {EInt(1)});");
                break;
            case "StopTimer":
                sb.AppendLine($"{indent}context.StopScriptTimer();");
                break;
            case "SetTempRevPos":
                if (args.Count == 1)
                {
                    sb.AppendLine($"{indent}context.SetTemporaryDeathRevivalByPointId({EInt(0)});");
                }
                else
                {
                    sb.AppendLine($"{indent}context.SetTemporaryDeathRevivalPosition((ushort)({EInt(0)}), {EInt(1)}, {EInt(2)});");
                }

                break;
            case "SetFaction":
                sb.AppendLine($"{indent}_ = GeneratedLuaRuntime.SetPlayerFaction(context, {EStr(0)});");
                break;
            case "RemoveItemByIndex":
                sb.AppendLine($"{indent}_ = context.TryRemoveBagItemByRuntimeId((uint)({EInt(0)}));");
                break;
            case "DelMagic":
                sb.AppendLine($"{indent}_ = context.TryRemoveSkill({EInt(0)});");
                break;
            case "RollbackSkill":
                sb.AppendLine($"{indent}_ = context.RollbackSkills();");
                break;
            case "RestoreLife":
                sb.AppendLine($"{indent}context.RestoreLife();");
                break;
            case "RestoreMana":
                sb.AppendLine($"{indent}context.RestoreMana();");
                break;
            case "RestoreStamina":
                sb.AppendLine($"{indent}context.RestoreStamina();");
                break;
            case "ST_SyncMiniMapObj":
                sb.AppendLine($"{indent}context.SyncMiniMapMarker({EInt(0)}, {EInt(1)});");
                break;
            case "AddPlayerEvent":
                sb.AppendLine($"{indent}_ = context.RegisterPlayerEvent((ushort)({EInt(0)}));");
                break;
            case "tinsert":
            {
                LuaValueType lt = InferExpressionType(args[0], script, fnScopeTypes);
                LuaExpression valNode = args.Count == 2 ? args[1] : args[2];
                if (lt == LuaValueType.DialogOptionList)
                {
                    string optExpr = EmitDialogOptionTupleWithCaptures(sb, indent, (LuaTableLiteralExpr)valNode, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript, ref tempCounter);
                    if (args.Count == 2)
                    {
                        sb.AppendLine($"{indent}({E(0)}).Add({optExpr});");
                    }
                    else
                    {
                        sb.AppendLine($"{indent}GeneratedLuaRuntime.InsertAt1Based({E(0)}, {EInt(1)}, {optExpr});");
                    }

                    break;
                }

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
                if (TryResolveStaticEventItemCode(args[0], constExprMap, out string? code))
                {
                    sb.AppendLine($"{indent}_ = context.TryGrantItemByCode({ToEscapedStringLiteral(code!)});");
                }
                else
                {
                    string mapSwitch = BuildEventItemSwitchLambda(args[0], constExprMap);
                    sb.AppendLine($"{indent}_ = GeneratedLuaRuntime.TryGrantMappedItem(context, {EInt(0)}, {mapSwitch});");
                }

                break;
            }
            case "DelItem":
            {
                if (TryResolveStaticEventItemCode(args[0], constExprMap, out string? code))
                {
                    sb.AppendLine($"{indent}_ = context.TryConsumeItemByCode({ToEscapedStringLiteral(code!)});");
                }
                else
                {
                    string mapSwitch = BuildEventItemSwitchLambda(args[0], constExprMap);
                    sb.AppendLine($"{indent}_ = GeneratedLuaRuntime.TryConsumeMappedItem(context, {EInt(0)}, {mapSwitch});");
                }

                break;
            }
            case "AddGoldItem":
            case "AddItem":
            case "AddItemNoStack":
            case "AddStackItem":
            case "DelCommonItem":
            case "ConsumeEquiproomItem":
            case "ConsumeItem":
            case "SetBitTask":
            case "ITEM_SetExpiredTime":
            case "SetSpecItemParam":
                sb.AppendLine($"{indent}_ = {EmitCallExpression(call, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript)};");
                break;
            case "SyncItem":
                sb.AppendLine($"{indent}context.SyncBagItem((uint)({EInt(0)}));");
                break;
            case "OB_Release":
                sb.AppendLine($"{indent}GeneratedLuaRuntime.ReleaseObjBuffer({EInt(0)});");
                break;
            case "RemoteExecute":
                sb.AppendLine($"{indent}GeneratedLuaRuntime.RemoteExecuteBroadcast(context, {EStr(0)}, {EStr(1)}, {EInt(2)});");
                break;
            case "LeaveTeam":
                sb.AppendLine($"{indent}context.LeaveParty();");
                break;
            case "SetCreateTeam":
                sb.AppendLine($"{indent}context.IsCreateTeamAllowed = ({EInt(0)}) != 0;");
                break;
            case "SetDeathScript":
                sb.AppendLine($"{indent}context.CustomDeathScriptPath = {EStr(0)};");
                break;
            case "BT_LeaveBattle":
                sb.AppendLine($"{indent}// BT_LeaveBattle: ranking/battle UI cleared on exit");
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
                if (args.Count == 1 && args[0] is LuaCallExpr { CalleeName: "GetPlayerRev" })
                {
                    sb.AppendLine($"{indent}context.SetRevivalPoint(context.RevivalMapId, context.RevivalPointId);");
                }
                else if (args.Count == 1)
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
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(args[0], constExprMap, out int declaredLadderCount);
                int clampEnd = 1 + Math.Min(declaredLadderCount, args.Count - 1);
                var ids = new List<string>(clampEnd - 1);
                for (int i = 1; i < clampEnd; i++)
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
            case "AddStrg":
                sb.AppendLine($"{indent}context.AddBaseStrength({EInt(0)});");
                break;
            case "AddDex":
                sb.AppendLine($"{indent}context.AddBaseDexterity({EInt(0)});");
                break;
            case "AddVit":
                sb.AppendLine($"{indent}context.AddBaseVitality({EInt(0)});");
                break;
            case "AddEng":
                sb.AppendLine($"{indent}context.AddBaseEnergy({EInt(0)});");
                break;
            case "AskClientForNumber":
            {
                if (args[0] is LuaBinaryExpr { Operator: LuaBinaryOp.Concat, Left: LuaStringExpr askPrefix } askConcat)
                {
                    string pfx = askPrefix.DecodedUtf8;
                    List<(int Suffix, string FnName)> matches = [];
                    foreach (LuaFunctionDecl fDecl in script.Program.Functions)
                    {
                        if (fDecl.Name.Length > pfx.Length &&
                            fDecl.Name.StartsWith(pfx, StringComparison.Ordinal) &&
                            int.TryParse(fDecl.Name.AsSpan(pfx.Length), out int sfx))
                        {
                            matches.Add((sfx, fDecl.Name));
                        }
                    }

                    matches.Sort(static (a, b) => a.Suffix.CompareTo(b.Suffix));
                    int kId = tempCounter++;
                    string idxVar = $"__askIdx{kId}";
                    string castCtx = script.Kind == GeneratedScriptKind.Npc ? "ctx" : $"({ResolveContextInterfaceName(script.Kind)})ctx";
                    bool anyAsync = matches.Any(m => script.FunctionIsAsync[m.FnName]);

                    sb.AppendLine($"{indent}int {idxVar} = {EmitIntExpr(askConcat.Right, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript)};");
                    sb.AppendLine($"{indent}context.AskClientForNumber({EStr(3)}, {EInt(1)}, {EInt(2)}, {(anyAsync ? "async " : string.Empty)}(ctx, num) =>");
                    sb.AppendLine($"{indent}{{");
                    sb.AppendLine($"{indent}    switch ({idxVar})");
                    sb.AppendLine($"{indent}    {{");
                    foreach ((int sfx, string fnName) in matches)
                    {
                        string mName = MakeFunctionMethodName(scriptIndex, fnName);
                        bool cbAsync = script.FunctionIsAsync[fnName];
                        bool cbRet = script.FunctionReturnTypes[fnName] != LuaValueType.Void;
                        string callExpr = $"{mName}({castCtx}, num, CancellationToken.None)";
                        if (cbAsync)
                        {
                            sb.AppendLine(cbRet
                                ? $"{indent}        case {sfx}: _ = await {callExpr}; break;"
                                : $"{indent}        case {sfx}: await {callExpr}; break;");
                        }
                        else if (anyAsync)
                        {
                            sb.AppendLine(cbRet
                                ? $"{indent}        case {sfx}: _ = {callExpr}; break;"
                                : $"{indent}        case {sfx}: await {callExpr}; break;");
                        }
                        else
                        {
                            sb.AppendLine(cbRet
                                ? $"{indent}        case {sfx}: _ = {callExpr}; return ValueTask.CompletedTask;"
                                : $"{indent}        case {sfx}: return {callExpr};");
                        }
                    }

                    sb.AppendLine($"{indent}    }}");
                    if (!anyAsync)
                    {
                        sb.AppendLine($"{indent}    return ValueTask.CompletedTask;");
                    }

                    sb.AppendLine($"{indent}}});");
                    break;
                }

                string cbName = ((LuaStringExpr)args[0]).DecodedUtf8.Trim();
                string methodName = MakeFunctionMethodName(scriptIndex, cbName);
                string castCtxStd = script.Kind == GeneratedScriptKind.Npc ? "ctx" : $"({ResolveContextInterfaceName(script.Kind)})ctx";
                bool cbAsyncStd = script.FunctionIsAsync[cbName];
                bool cbHasRetStd = script.FunctionReturnTypes[cbName] != LuaValueType.Void;
                string lambda = !cbAsyncStd && cbHasRetStd
                    ? $"static (ctx, num) => {{ _ = {methodName}({castCtxStd}, num, CancellationToken.None); return ValueTask.CompletedTask; }}"
                    : cbAsyncStd && cbHasRetStd
                        ? $"static async (ctx, num) => {{ _ = await {methodName}({castCtxStd}, num, CancellationToken.None); }}"
                        : $"static (ctx, num) => {methodName}({castCtxStd}, num, CancellationToken.None)";
                sb.AppendLine($"{indent}context.AskClientForNumber({EStr(3)}, {EInt(1)}, {EInt(2)}, {lambda});");
                break;
            }
            case "NewWorld":
                if (args.Count == 1 &&
                    args[0] is LuaCallExpr worldTupleCall &&
                    script.FunctionReturnTuples is not null &&
                    script.FunctionReturnTuples.TryGetValue(worldTupleCall.CalleeName, out IReadOnlyList<LuaValueType>? worldTup) &&
                    worldTup.Count >= 3)
                {
                    int lId = tempCounter++;
                    string lv = $"__nw{lId}";
                    string tupleCallExpr = EmitCallExpression(worldTupleCall, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript, preserveTuple: true);
                    sb.AppendLine($"{indent}var {lv} = {tupleCallExpr};");
                    sb.AppendLine(isMonsterScript
                        ? $"{indent}_ = context.TeleportPlayerAsync((ushort)({lv}.Item1), ({lv}.Item2) * 32, ({lv}.Item3) * 32, context.CurrentFightMode, CancellationToken.None);"
                        : $"{indent}_ = await context.TeleportPlayerAsync((ushort)({lv}.Item1), ({lv}.Item2) * 32, ({lv}.Item3) * 32, context.CurrentFightMode, cancellationToken);");
                }
                else
                {
                    sb.AppendLine(isMonsterScript
                        ? $"{indent}_ = context.TeleportPlayerAsync((ushort)({EInt(0)}), ({EInt(1)}) * 32, ({EInt(2)}) * 32, context.CurrentFightMode, CancellationToken.None);"
                        : $"{indent}_ = await context.TeleportPlayerAsync((ushort)({EInt(0)}), ({EInt(1)}) * 32, ({EInt(2)}) * 32, context.CurrentFightMode, cancellationToken);");
                }

                break;
            case "SetPos":
                sb.AppendLine(isMonsterScript
                    ? $"{indent}_ = context.TeleportPlayerAsync(context.MapId, ({EInt(0)}) * 32, ({EInt(1)}) * 32, context.CurrentFightMode, CancellationToken.None);"
                    : $"{indent}_ = await context.TeleportPlayerAsync(context.MapId, ({EInt(0)}) * 32, ({EInt(1)}) * 32, context.CurrentFightMode, cancellationToken);");
                break;
            case "ReturnFromPortal":
                sb.AppendLine(isMonsterScript
                    ? $"{indent}_ = context.TryReturnFromPortalAsync(CancellationToken.None);"
                    : $"{indent}_ = await context.TryReturnFromPortalAsync(cancellationToken);");
                break;
            case "Talk":
            {
                if (args.Count < 3)
                {
                    for (int i = 0; i < args.Count; i++)
                    {
                        sb.AppendLine($"{indent}_ = {E(i)};");
                    }

                    break;
                }

                string cbDelegate = "null";
                if (args[1] is LuaStringExpr cbStr)
                {
                    string cbName = cbStr.DecodedUtf8.Trim();
                    if (cbName.Length > 0 &&
                        LuaScriptSemanticValidator.IsValidLuaIdentifier(cbName) &&
                        functionsByName.TryGetValue(cbName, out LuaFunctionDecl? cbFn) &&
                        cbFn.Body.Count > 0)
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
            case "CreateNewSayEx":
            {
                string titleExpr = EStr(0);
                if (args[1] is LuaTableLiteralExpr inlineOpts)
                {
                    var optExprs = new List<string>(inlineOpts.Elements.Count);
                    foreach (LuaExpression el in inlineOpts.Elements)
                    {
                        optExprs.Add(EmitDialogOptionTupleWithCaptures(sb, indent, (LuaTableLiteralExpr)el, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript, ref tempCounter));
                    }

                    sb.AppendLine($"{indent}context.Describe(GeneratedLuaRuntime.FormatTaskSayColor(context, {titleExpr}), [{string.Join(", ", optExprs)}]);");
                }
                else
                {
                    sb.AppendLine($"{indent}context.Describe(GeneratedLuaRuntime.FormatTaskSayColor(context, {titleExpr}), ({E(1)}).ToArray());");
                }

                break;
            }
            case "CreateTaskSay":
            {
                string routerRef = hasRouter ? $"S{scriptIndex}_InvokeCallback" : "static (_, _, _) => ValueTask.CompletedTask";
                sb.AppendLine($"{indent}GeneratedLuaRuntime.DispatchCreateTaskSay(context, {E(0)}, {routerRef});");
                break;
            }
            case "TaskSay":
            {
                string routerRef = hasRouter ? $"S{scriptIndex}_InvokeCallback" : "static (_, _, _) => ValueTask.CompletedTask";
                sb.AppendLine($"{indent}GeneratedLuaRuntime.DispatchTaskSay(context, {EStr(0)}, {E(1)}, {routerRef});");
                break;
            }
            case "Say":
            case "Describe":
            {
                LuaValueType promptType = InferExpressionType(args[0], script, fnScopeTypes);
                bool isSayByStringId = api == "Say" && promptType is LuaValueType.Int or LuaValueType.NullableInt;
                string hostMethod = isSayByStringId ? "SayByStringId" : (api == "Describe" ? "Describe" : "Say");
                string prompt = isSayByStringId ? EInt(0) : EStr(0);
                if (args.Count <= 2)
                {
                    sb.AppendLine($"{indent}context.{hostMethod}({prompt});");
                    break;
                }

                string routerRef = hasRouter ? $"S{scriptIndex}_InvokeCallback" : "static (_, _, _) => ValueTask.CompletedTask";

                if (args.Count == 3)
                {
                    LuaValueType tabType = InferExpressionType(args[2], script, fnScopeTypes);
                    if (tabType is LuaValueType.StringList or LuaValueType.StringArray)
                    {
                        if (args[2] is LuaIdentifierExpr tabIdent &&
                            constArrays.TryGetValue(tabIdent.Name, out LuaTableLiteralExpr? tabLit) &&
                            tabLit.Elements.All(static el => LuaScriptSemanticValidator.TryEvalConstantString(el, out _)))
                        {
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
            case "Prise":
            {
                if (args.Count < 2)
                {
                    break;
                }

                string prompt = InferExpressionType(args[0], script, fnScopeTypes) == LuaValueType.String ? EStr(0) : $"({EInt(0)}).ToString(CultureInfo.InvariantCulture)";
                string routerRef = hasRouter ? $"S{scriptIndex}_InvokeCallback" : "static (_, _, _) => ValueTask.CompletedTask";
                if (args.Count == 3)
                {
                    sb.AppendLine($"{indent}context.Prise({prompt}, GeneratedLuaRuntime.BuildDynamicPriseOptions({E(2)}, {EInt(1)}, {routerRef}));");
                    break;
                }

                var priseOpts = new List<string>(3);
                for (int i = 1; i < 4; i++)
                {
                    priseOpts.Add($"GeneratedLuaRuntime.ParseSinglePriseOption({EStr(i)}, {routerRef})");
                }

                sb.AppendLine($"{indent}context.Prise({prompt}, {string.Join(", ", priseOpts)});");
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

        if (script.Kind == GeneratedScriptKind.Monster)
        {
            return cbHasRet
                ? $"static ctx => {{ _ = {methodName}({ctxArg}); return ValueTask.CompletedTask; }}"
                : $"static ctx => {{ {methodName}({ctxArg}); return ValueTask.CompletedTask; }}";
        }

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

    private static string BuildSyncCallbackArgExpr(int scriptIndex, CompiledLuaScript script, string cbName, bool isMonsterScript)
    {
        string methodName = MakeFunctionMethodName(scriptIndex, cbName);
        bool cbHasRet = (script.FunctionReturnTuples is not null && script.FunctionReturnTuples.ContainsKey(cbName)) ||
                        script.FunctionReturnTypes[cbName] != LuaValueType.Void;

        if (isMonsterScript)
        {
            return cbHasRet
                ? $"static ctx => {{ _ = {methodName}(ctx); }}"
                : methodName;
        }

        string iface = ResolveContextInterfaceName(script.Kind);
        string ctxArg = iface == "IScriptDialogContext" ? "ctx" : $"({iface})ctx";
        bool cbAsync = script.FunctionIsAsync[cbName];
        if (!cbAsync && cbHasRet)
        {
            return $"static (ctx, ct) => {{ _ = {methodName}({ctxArg}, ct); return ValueTask.CompletedTask; }}";
        }

        if (cbAsync && cbHasRet)
        {
            return $"static async (ctx, ct) => {{ _ = await {methodName}({ctxArg}, ct); }}";
        }

        return $"static (ctx, ct) => {methodName}({ctxArg}, ct)";
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
        var parts = new List<string>(paramTypes.Count + 2) { "context" };
        int boundCount = Math.Min(call.Arguments.Count, paramTypes.Count);
        for (int i = 0; i < boundCount; i++)
        {
            parts.Add(EmitAssignableExpression(call.Arguments[i], paramTypes[i], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript));
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
        if (targetType == LuaValueType.SyncCallback && expr is LuaIdentifierExpr cbId &&
            (localVarMap is null || !localVarMap.ContainsKey(cbId.Name)) &&
            script.FunctionReturnTypes.ContainsKey(cbId.Name))
        {
            return BuildSyncCallbackArgExpr(scriptIndex, script, cbId.Name, isMonsterScript);
        }

        LuaValueType actualType = InferExpressionType(expr, script, fnScopeTypes);
        if (targetType == LuaValueType.Int && actualType == LuaValueType.NullableInt)
        {
            return EmitIntExpr(expr, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
        }

        if (targetType == LuaValueType.NullableInt && actualType == LuaValueType.Nil)
        {
            return "((int?)null)";
        }

        if (targetType == LuaValueType.NullableInt && actualType == LuaValueType.Int)
        {
            return $"((int?)({EmitIntExpr(expr, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript)}))";
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
        if (t == LuaValueType.Number)
        {
            return $"unchecked((int)(long)Math.Truncate({raw}))";
        }

        return t == LuaValueType.NullableInt ? $"(({raw}) ?? 0)" : raw;
    }

    private string EmitLongExpr(
        LuaExpression expr,
        int scriptIndex,
        CompiledLuaScript script,
        IReadOnlyDictionary<string, LuaValueType>? fnScopeTypes,
        IReadOnlyDictionary<string, string> constFieldMap,
        IReadOnlyDictionary<string, LuaExpression> constExprMap,
        IReadOnlyDictionary<string, string>? localVarMap,
        bool isMonsterScript)
    {
        if (expr is LuaBinaryExpr bin && bin.Operator is LuaBinaryOp.Add or LuaBinaryOp.Sub or LuaBinaryOp.Mul)
        {
            LuaValueType lt = InferExpressionType(bin.Left, script, fnScopeTypes);
            LuaValueType rt = InferExpressionType(bin.Right, script, fnScopeTypes);
            if (lt != LuaValueType.Number && rt != LuaValueType.Number)
            {
                string ll = EmitLongExpr(bin.Left, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                string rl = EmitLongExpr(bin.Right, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                string opStr = bin.Operator switch
                {
                    LuaBinaryOp.Sub => "-",
                    LuaBinaryOp.Mul => "*",
                    _ => "+",
                };
                return $"({ll} {opStr} {rl})";
            }
        }

        LuaValueType t = InferExpressionType(expr, script, fnScopeTypes);
        if (t == LuaValueType.Number)
        {
            string raw = EmitExpression(expr, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
            return $"(long)Math.Truncate((double)({raw}))";
        }

        return $"((long)({EmitIntExpr(expr, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript)}))";
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
            return $"((double)({EmitExpression(expr, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript)}))";
        }

        return $"((double)({EmitLongExpr(expr, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript)}))";
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

        if (t == LuaValueType.SyncCallback)
        {
            return "true";
        }

        string raw = EmitExpression(expr, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
        if (t == LuaValueType.NullableInt)
        {
            return $"({raw}).HasValue";
        }

        if (t is LuaValueType.AwardTemplate or LuaValueType.IntFieldRecord)
        {
            return $"({raw} is not null)";
        }

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

                if (id.Name == "PlayerIndex")
                {
                    return "context.CurrentPlayerIndex";
                }

                if (id.Name == "NpcName2Replace")
                {
                    return "true";
                }

                if (localVarMap is not null && localVarMap.TryGetValue(id.Name, out string? locName))
                {
                    return locName;
                }

                if (constFieldMap.TryGetValue(id.Name, out string? cfName))
                {
                    return cfName;
                }

                if (script.FunctionReturnTypes.ContainsKey(id.Name))
                {
                    return BuildSyncCallbackArgExpr(scriptIndex, script, id.Name, isMonsterScript);
                }

                throw new InvalidOperationException($"Unbound identifier '{id.Name}'.");

            case LuaTableLiteralExpr tbl:
                if (InferExpressionType(tbl, script, fnScopeTypes) == LuaValueType.LocalAwardArray)
                {
                    var tempValidator = new LuaScriptSemanticValidator(_eventItemCodeByDetailId, _itemCatalogIndex);
                    var nodeStrs = new List<string>(tbl.Elements.Count);
                    foreach (LuaExpression el in tbl.Elements)
                    {
                        _ = tempValidator.TryCompileAwardNode(el, constExprMap, out CompiledAwardNode? compiledNode, out _);
                        nodeStrs.Add(EmitAwardNodeExpr(compiledNode!));
                    }

                    return "[" + string.Join(", ", nodeStrs) + "]";
                }

                return "[" + string.Join(", ", tbl.Elements.Select(e => EmitExpression(e, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript))) + "]";

            case LuaIndexExpr idx:
            {
                if (idx.Target is LuaIdentifierExpr rowVarId &&
                    _currentFnLocalTupleTables is not null &&
                    _currentFnLocalTupleTables.TryGetValue(rowVarId.Name, out string? rowTblName) &&
                    script.ConstTupleColumnTypes is not null &&
                    script.ConstTupleColumnTypes.TryGetValue(rowTblName, out IReadOnlyList<LuaValueType>? rowCols) &&
                    LuaScriptSemanticValidator.TryEvalConstantInt(idx.Index, constExprMap, out int rowCol1Based) &&
                    rowCol1Based >= 1 &&
                    rowCol1Based <= rowCols.Count)
                {
                    string rowVarCs = localVarMap![rowVarId.Name];
                    LuaValueType rowColType = rowCols[rowCol1Based - 1];
                    string rowItemAccess = $"{rowVarCs}.Item{rowCol1Based}";
                    return rowColType == LuaValueType.String ? $"({rowItemAccess} ?? string.Empty)" : rowItemAccess;
                }

                if (idx.Target is LuaIndexExpr rowIdxExpr &&
                    rowIdxExpr.Target is LuaIdentifierExpr tupleTblId &&
                    script.ConstTupleColumnTypes is not null &&
                    script.ConstTupleColumnTypes.TryGetValue(tupleTblId.Name, out IReadOnlyList<LuaValueType>? cols) &&
                    LuaScriptSemanticValidator.TryEvalConstantInt(idx.Index, constExprMap, out int col1Based))
                {
                    string tblField = constFieldMap[tupleTblId.Name];
                    string rExpr = EmitIntExpr(rowIdxExpr.Index, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    LuaValueType colType = cols[col1Based - 1];
                    string itemAccess = $"GeneratedLuaRuntime.GetRowAt1Based({tblField}, {rExpr}).Item{col1Based}";
                    return colType == LuaValueType.String ? $"({itemAccess} ?? string.Empty)" : itemAccess;
                }

                if (idx.Target is LuaIdentifierExpr outerTblId &&
                    script.ConstTupleColumnTypes is not null &&
                    script.ConstTupleColumnTypes.ContainsKey(outerTblId.Name))
                {
                    string tblField = constFieldMap[outerTblId.Name];
                    string rExpr = EmitIntExpr(idx.Index, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    return $"GeneratedLuaRuntime.GetRowAt1Based({tblField}, {rExpr})";
                }

                LuaValueType targetType = InferExpressionType(idx.Target, script, fnScopeTypes);
                string targetCode = EmitExpression(idx.Target, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                string indexCode = EmitIntExpr(idx.Index, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                return targetType switch
                {
                    LuaValueType.IntKeyedIntMap => $"GeneratedLuaRuntime.GetMapIntOrNull({targetCode}, {indexCode})",
                    LuaValueType.IntKeyedStringMap => $"GeneratedLuaRuntime.GetMapStrOrEmpty({targetCode}, {indexCode})",
                    LuaValueType.IntKeyedIntArrayMap => $"GeneratedLuaRuntime.GetMapIntArrOrEmpty({targetCode}, {indexCode})",
                    LuaValueType.IntKeyedAwardTemplateMap => $"GeneratedLuaRuntime.GetMapAwardAt({targetCode}, {indexCode})",
                    LuaValueType.LocalAwardArray => $"GeneratedLuaRuntime.GetAwardAt1Based({targetCode}, {indexCode})",
                    LuaValueType.IntKeyedIntRecordMap => $"GeneratedLuaRuntime.GetIntRecordAt({targetCode}, {indexCode})",
                    _ => $"GeneratedLuaRuntime.GetAt1Based({targetCode}, {indexCode})",
                };
            }

            case LuaMemberAccessExpr macc:
            {
                string recvCode = EmitExpression(macc.Target, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                return $"GeneratedLuaRuntime.GetFieldInt({recvCode}, {ToEscapedStringLiteral(macc.FieldName)})";
            }

            case LuaMemberCallExpr mcall:
                return EmitMemberCallExpression(mcall, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);

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

                LuaValueType lt = InferExpressionType(bin.Left, script, fnScopeTypes);
                LuaValueType rt = InferExpressionType(bin.Right, script, fnScopeTypes);

                if (bin.Operator == LuaBinaryOp.Or && lt == LuaValueType.NullableInt && rt == LuaValueType.Int)
                {
                    string le = EmitExpression(bin.Left, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    string re = EmitIntExpr(bin.Right, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    return $"(({le}) ?? ({re}))";
                }

                if (bin.Operator == LuaBinaryOp.Or && lt == LuaValueType.String && rt == LuaValueType.String)
                {
                    string ls = EmitAsStringExpression(bin.Left, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    string rs = EmitAsStringExpression(bin.Right, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    return $"(({ls}) ?? ({rs}))";
                }

                if (bin.Operator is LuaBinaryOp.And or LuaBinaryOp.Or)
                {
                    string lb = EmitBoolCondition(bin.Left, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    string rb = EmitBoolCondition(bin.Right, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                    string boolOp = bin.Operator == LuaBinaryOp.Or ? "||" : "&&";
                    return $"({lb} {boolOp} {rb})";
                }

                if (bin.Operator is LuaBinaryOp.Eq or LuaBinaryOp.Neq)
                {
                    if (lt == LuaValueType.Bool && LuaScriptSemanticValidator.TryEvalConstantInt(bin.Right, constExprMap, out int rBit) && rBit is 0 or 1)
                    {
                        string lb = EmitBoolCondition(bin.Left, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                        return rBit == 1
                            ? (bin.Operator == LuaBinaryOp.Eq ? lb : $"(!({lb}))")
                            : (bin.Operator == LuaBinaryOp.Eq ? "false" : "true");
                    }

                    if (rt == LuaValueType.Bool && LuaScriptSemanticValidator.TryEvalConstantInt(bin.Left, constExprMap, out int lBit) && lBit is 0 or 1)
                    {
                        string rb = EmitBoolCondition(bin.Right, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
                        return lBit == 1
                            ? (bin.Operator == LuaBinaryOp.Eq ? rb : $"(!({rb}))")
                            : (bin.Operator == LuaBinaryOp.Eq ? "false" : "true");
                    }

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
        bool isMonsterScript,
        bool preserveTuple = false)
    {
        string E(int idx) => EmitExpression(call.Arguments[idx], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
        string EInt(int idx) => EmitIntExpr(call.Arguments[idx], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
        string EDbl(int idx) => EmitDoubleExpr(call.Arguments[idx], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
        string EStr(int idx) => EmitAsStringExpression(call.Arguments[idx], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);

        if (script.FunctionReturnTypes.ContainsKey(call.CalleeName))
        {
            string invoke = BuildHelperInvocationExpr(call, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript);
            if (!isMonsterScript && script.FunctionIsAsync[call.CalleeName])
            {
                invoke = $"(await {invoke})";
            }

            if (!preserveTuple &&
                script.FunctionReturnTuples is not null &&
                script.FunctionReturnTuples.ContainsKey(call.CalleeName))
            {
                return $"({invoke}).Item1";
            }

            return invoke;
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
            case "CountFreeRoomByWH":
            {
                string extraEval = call.Arguments.Count > 2
                    ? string.Join(", ", Enumerable.Range(2, call.Arguments.Count - 2).Select(i => $"_ = {EInt(i)}")) + ", "
                    : string.Empty;
                string check = $"(context.CanFitBagItem((byte)Math.Clamp({EInt(0)}, 1, 10), (byte)Math.Clamp({EInt(1)}, 1, 10)) ? 1 : 0)";
                return extraEval.Length == 0 ? check : $"({extraEval}{check}).Item{call.Arguments.Count - 1}";
            }
            case "GetCamp": return "(int)context.BaseCamp";
            case "GetCurCamp": return "(int)context.CurrentCamp";
            case "GetPK": return "(int)context.PkValue";
            case "GetPKFlag": return "(int)context.PkMode";
            case "GetForbidChangePK": return "(context.IsPkSwitchForbidden ? 1 : 0)";
            case "GetPunish": return "(context.IsDeathPunishEnabled ? 1 : 0)";
            case "GetRank": return "(int)context.RankId";
            case "GetLastFactionNumber": return "(context.EffectiveFactionId == 255 ? -1 : (int)context.EffectiveFactionId)";
            case "GetLastDiagNpc": return "(int)(context.SourceNpcId ?? 0u)";
            case "GetCurServerTime": return "GeneratedLuaRuntime.GetCurServerTime()";
            case "GetTimerId": return "context.ActiveScriptTimerId";
            case "SetTimer": return $"(context.SetScriptTimer({EInt(0)}, {EInt(1)}), 1).Item2";
            case "GetLogoutRV": return "(context.UseReviveIdOnLogin ? 1 : 0)";
            case "IsDisabledUseTownP": return "(context.IsTownPortalDisabled ? 1 : 0)";
            case "GetTeamSize": return "context.GetTeamSize()";
            case "GetTeamMember": return $"context.GetTeamMemberIndex({EInt(0)})";
            case "IsCaptain": return "(context.IsPartyCaptain ? 1 : 0)";
            case "GetExp": return "((int)Math.Min(int.MaxValue, context.Experience))";
            case "GetLucky": return "(int)context.Lucky";
            case "GetProp": return "(int)context.UnallocatedAttributePoints";
            case "GetMagicPoint": return "(int)context.UnallocatedSkillPoints";
            case "GetTripMode": return "0";
            case "ST_GetTransLifeCount": return "0";
            case "OB_Create": return "GeneratedLuaRuntime.CreateObjBuffer()";
            case "LeaveTeam": return "(context.LeaveParty(), 1).Item2";
            case "BT_LeaveBattle": return "0";
            case "SetFaction": return $"GeneratedLuaRuntime.SetPlayerFaction(context, {EStr(0)})";
            case "IsMyItem": return $"(context.IsOwnedBagItem((uint)({EInt(0)})) ? 1 : 0)";
            case "RemoveItemByIndex": return $"(context.TryRemoveBagItemByRuntimeId((uint)({EInt(0)})) ? 1 : 0)";
            case "GetItemName": return $"context.GetBagItemNameUtf8((uint)({EInt(0)}))";
            case "GetItemProp": return "context.ParticularType";
            case "ITEM_GetExpiredTime": return $"(int)(context.GetBagItemExpirationUnixSeconds((uint)({EInt(0)})) ?? 0u)";
            case "ITEM_SetExpiredTime":
            {
                string timeArg = call.Arguments.Count >= 3 ? EInt(2) : "0";
                return $"(context.TrySetBagItemExpirationDate((uint)({EInt(0)}), {EInt(1)}, {timeArg}) ? 1 : 0)";
            }
            case "FormatTime2String":
            {
                string tsExpr = call.Arguments.Count >= 2 ? EDbl(1) : "(double)GeneratedLuaRuntime.GetCurServerTime()";
                return $"GeneratedLuaRuntime.FormatTime2String({EStr(0)}, {tsExpr})";
            }
            case "GetItemParam": return $"context.GetBagItemSpecParam((uint)({EInt(0)}), {EInt(1)})";
            case "SetSpecItemParam": return $"(context.TrySetBagItemSpecParam((uint)({EInt(0)}), {EInt(1)}, {EInt(2)}) ? 1 : 0)";
            case "GetBitTask": return $"GeneratedLuaRuntime.GetBitTask(context, {EInt(0)}, {EInt(1)}, {EInt(2)})";
            case "SetBitTask": return $"GeneratedLuaRuntime.SetBitTask(context, {EInt(0)}, {EInt(1)}, {EInt(2)}, {EInt(3)})";
            case "GetWorldPos": return "(int)context.MapId";
            case "GetPlayerRev": return "(int)context.RevivalMapId";
            case "GetLeavePos": return "0";
            case "GetTong": return "string.Empty";
            case "RevID2WXY":
                return call.Arguments.Count == 1
                    ? "(int)context.ResolveRevivalPosition(context.RevivalMapId, context.RevivalPointId).MapId"
                    : $"(int)context.ResolveRevivalPosition((ushort)({EInt(0)}), {EInt(1)}).MapId";
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
            case "RollbackSkill": return "context.RollbackSkills()";
            case "AddPlayerEvent": return $"(context.RegisterPlayerEvent((ushort)({EInt(0)})) ? 1 : 0)";
            case "HaveCommonItem":
            {
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], constExprMap, out int g);
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[1], constExprMap, out int d);
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[2], constExprMap, out int p);
                return _itemCatalogIndex!.TryResolveByGdp(g, d, p, out PackItemEntry itemEntry)
                    ? $"(context.HasBagItem({ToEscapedStringLiteral(itemEntry.Code)}) ? 1 : 0)"
                    : "0";
            }
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
                return at is LuaValueType.StringList or LuaValueType.IntList or LuaValueType.DialogOptionList
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
                    : $"GeneratedLuaRuntime.LuaMod({EmitLongExpr(call.Arguments[0], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript)}, {EmitLongExpr(call.Arguments[1], scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript)})";
            case "min":
                return InferExpressionType(call, script, fnScopeTypes) == LuaValueType.Number
                    ? $"Math.Min({EDbl(0)}, {EDbl(1)})"
                    : $"Math.Min({EInt(0)}, {EInt(1)})";
            case "max":
                return InferExpressionType(call, script, fnScopeTypes) == LuaValueType.Number
                    ? $"Math.Max({EDbl(0)}, {EDbl(1)})"
                    : $"Math.Max({EInt(0)}, {EInt(1)})";
            case "tonumber":
                if (LuaScriptSemanticValidator.IsLongDateToNumberCall(call))
                {
                    return $"GeneratedLuaRuntime.LuaToDouble({E(0)})";
                }

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
            case "Add120SkillExp":
                return $"GeneratedLuaRuntime.Add120SkillExp(context, {EInt(0)})";
            case "HaveItem":
            {
                if (TryResolveStaticEventItemCode(call.Arguments[0], constExprMap, out string? code))
                {
                    return $"(context.HasBagItem({ToEscapedStringLiteral(code!)}) ? 1 : 0)";
                }

                string mapSwitch = BuildEventItemSwitchLambda(call.Arguments[0], constExprMap);
                return $"GeneratedLuaRuntime.HasMappedItem(context, {EInt(0)}, {mapSwitch})";
            }
            case "GetItemCount":
            case "GetItemCountEx":
            {
                if (TryResolveStaticEventItemCode(call.Arguments[0], constExprMap, out string? code))
                {
                    return $"context.GetBagItemCount({ToEscapedStringLiteral(code!)})";
                }

                string mapSwitch = BuildEventItemSwitchLambda(call.Arguments[0], constExprMap);
                return $"GeneratedLuaRuntime.GetMappedItemCount(context, {EInt(0)}, {mapSwitch})";
            }
            case "AddGoldItem":
            {
                if (LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[1], constExprMap, out int rowId))
                {
                    _ = _itemCatalogIndex!.TryResolveGoldEquipByRowId(rowId, out PackItemEntry goldEntry);
                    return $"(context.TryGrantEquipmentByCode({ToEscapedStringLiteral(goldEntry.Code)}) ? 1 : 0)";
                }

                string goldSwitch = BuildGoldItemSwitchLambda(call.Arguments[1], constExprMap);
                return $"(GeneratedLuaRuntime.TryGrantMappedEquip(context, {EInt(1)}, {goldSwitch}) ? 1 : 0)";
            }
            case "AddItem":
            case "AddItemNoStack":
            {
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[0], constExprMap, out int g);
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[1], constExprMap, out int d);
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[3], constExprMap, out int lv);
                if (LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[2], constExprMap, out int p))
                {
                    if (!_itemCatalogIndex!.TryResolveByGdpl(g, d, p, lv, out PackItemEntry itemEntry))
                    {
                        return "0";
                    }

                    string litCode = ToEscapedStringLiteral(itemEntry.Code);
                    if (g == 0)
                    {
                        string seriesArg = $"(ElementSeries)Math.Clamp({EInt(4)}, 0, 4)";
                        if (call.Arguments.Count >= 12)
                        {
                            string magArr = $"stackalloc int[] {{ {EInt(6)}, {EInt(7)}, {EInt(8)}, {EInt(9)}, {EInt(10)}, {EInt(11)} }}";
                            return $"(context.TryGrantEquipmentByCode({litCode}, {seriesArg}, {magArr}) ? 1 : 0)";
                        }

                        return $"(context.TryGrantEquipmentByCode({litCode}, {seriesArg}) ? 1 : 0)";
                    }

                    return $"(context.TryGrantItemByCode({litCode}, 1) ? 1 : 0)";
                }

                string gdplSwitch = BuildDynamicParticularSwitchLambda(g, d, lv, call.Arguments[2], constExprMap);
                if (g == 0)
                {
                    return $"(GeneratedLuaRuntime.TryGrantMappedEquip(context, {EInt(2)}, {gdplSwitch}, (ElementSeries)Math.Clamp({EInt(4)}, 0, 4)) ? 1 : 0)";
                }

                return $"(GeneratedLuaRuntime.TryGrantMappedItem(context, {EInt(2)}, {gdplSwitch}, 1) ? 1 : 0)";
            }
            case "AddStackItem":
            {
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[1], constExprMap, out int g);
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[2], constExprMap, out int d);
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[4], constExprMap, out int lv);
                if (LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[3], constExprMap, out int p))
                {
                    _ = _itemCatalogIndex!.TryResolveByGdpl(g, d, p, lv, out PackItemEntry itemEntry);
                    return $"(context.TryGrantItemByCode({ToEscapedStringLiteral(itemEntry.Code)}, Math.Max(1, {EInt(0)})) ? 1 : 0)";
                }

                string gdplSwitch = BuildDynamicParticularSwitchLambda(g, d, lv, call.Arguments[3], constExprMap);
                return $"(GeneratedLuaRuntime.TryGrantMappedItem(context, {EInt(3)}, {gdplSwitch}, Math.Max(1, {EInt(0)})) ? 1 : 0)";
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
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[3], constExprMap, out int lv);
                if (LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[2], constExprMap, out int p))
                {
                    _ = _itemCatalogIndex!.TryResolveByGdpl(g, d, p, lv, out PackItemEntry itemEntry);
                    return $"context.GetBagItemCount({ToEscapedStringLiteral(itemEntry.Code)})";
                }

                string gdplSwitch = BuildDynamicParticularSwitchLambda(g, d, lv, call.Arguments[2], constExprMap);
                return $"GeneratedLuaRuntime.GetMappedItemCount(context, {EInt(2)}, {gdplSwitch})";
            }
            case "ConsumeEquiproomItem":
            {
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[1], constExprMap, out int g);
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[2], constExprMap, out int d);
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[4], constExprMap, out int lv);
                if (LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[3], constExprMap, out int p))
                {
                    _ = _itemCatalogIndex!.TryResolveByGdpl(g, d, p, lv, out PackItemEntry itemEntry);
                    return $"(context.TryConsumeItemByCode({ToEscapedStringLiteral(itemEntry.Code)}, {EInt(0)}) ? 1 : 0)";
                }

                string gdplSwitch = BuildDynamicParticularSwitchLambda(g, d, lv, call.Arguments[3], constExprMap);
                return $"(GeneratedLuaRuntime.TryConsumeMappedItem(context, {EInt(3)}, {gdplSwitch}, {EInt(0)}) ? 1 : 0)";
            }
            case "CalcItemCount":
            {
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[1], constExprMap, out int g);
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[2], constExprMap, out int d);
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[4], constExprMap, out int lv);
                string countExpr;
                if (LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[3], constExprMap, out int p))
                {
                    _ = _itemCatalogIndex!.TryResolveByGdpl(g, d, p, lv, out PackItemEntry itemEntry);
                    countExpr = $"context.GetBagItemCount({ToEscapedStringLiteral(itemEntry.Code)})";
                }
                else
                {
                    string gdplSwitch = BuildDynamicParticularSwitchLambda(g, d, lv, call.Arguments[3], constExprMap);
                    countExpr = $"GeneratedLuaRuntime.GetMappedItemCount(context, {EInt(3)}, {gdplSwitch})";
                }

                return $"(({EInt(0)}) == 3 ? {countExpr} : 0)";
            }
            case "ConsumeItem":
            {
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[2], constExprMap, out int g);
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[3], constExprMap, out int d);
                _ = LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[5], constExprMap, out int lv);
                string consumeExpr;
                if (LuaScriptSemanticValidator.TryEvalConstantInt(call.Arguments[4], constExprMap, out int p))
                {
                    _ = _itemCatalogIndex!.TryResolveByGdpl(g, d, p, lv, out PackItemEntry itemEntry);
                    consumeExpr = $"(context.TryConsumeItemByCode({ToEscapedStringLiteral(itemEntry.Code)}, {EInt(1)}) ? 1 : -1)";
                }
                else
                {
                    string gdplSwitch = BuildDynamicParticularSwitchLambda(g, d, lv, call.Arguments[4], constExprMap);
                    consumeExpr = $"(GeneratedLuaRuntime.TryConsumeMappedItem(context, {EInt(4)}, {gdplSwitch}, {EInt(1)}) ? 1 : -1)";
                }

                return $"(({EInt(0)}) == 3 ? {consumeExpr} : -1)";
            }
            case "NewWorld":
                if (call.Arguments.Count == 1 &&
                    call.Arguments[0] is LuaCallExpr worldTupleCall &&
                    script.FunctionReturnTuples is not null &&
                    script.FunctionReturnTuples.TryGetValue(worldTupleCall.CalleeName, out IReadOnlyList<LuaValueType>? worldTup) &&
                    worldTup.Count >= 3)
                {
                    string tupleCallExpr = EmitCallExpression(worldTupleCall, scriptIndex, script, fnScopeTypes, constFieldMap, constExprMap, localVarMap, isMonsterScript, preserveTuple: true);
                    string tpCall = isMonsterScript
                        ? $"((_ = context.TeleportPlayerAsync((ushort)(__nw.Item1), (__nw.Item2) * 32, (__nw.Item3) * 32, context.CurrentFightMode, CancellationToken.None)), 1).Item2"
                        : $"((await context.TeleportPlayerAsync((ushort)(__nw.Item1), (__nw.Item2) * 32, (__nw.Item3) * 32, context.CurrentFightMode, cancellationToken)) ? 1 : 0)";
                    return $"(({tupleCallExpr}) is var __nw ? {tpCall} : 0)";
                }

                return isMonsterScript
                    ? $"((_ = context.TeleportPlayerAsync((ushort)({EInt(0)}), ({EInt(1)}) * 32, ({EInt(2)}) * 32, context.CurrentFightMode, CancellationToken.None)), 1).Item2"
                    : $"((await context.TeleportPlayerAsync((ushort)({EInt(0)}), ({EInt(1)}) * 32, ({EInt(2)}) * 32, context.CurrentFightMode, cancellationToken)) ? 1 : 0)";
            case "ReturnFromPortal":
                return isMonsterScript
                    ? "((_ = context.TryReturnFromPortalAsync(CancellationToken.None)), 1).Item2"
                    : "((await context.TryReturnFromPortalAsync(cancellationToken)) ? 1 : 0)";
            default:
                throw new InvalidOperationException($"Unsupported call expression '{call.CalleeName}'.");
        }
    }

    private bool TryResolveStaticEventItemCode(
        LuaExpression arg,
        IReadOnlyDictionary<string, LuaExpression> constExprMap,
        out string? code)
    {
        if (LuaScriptSemanticValidator.TryEvalConstantInt(arg, constExprMap, out int eid))
        {
            if (_itemCatalogIndex is not null && _itemCatalogIndex.TryResolveEventItem(eid, out string catalogCode))
            {
                code = catalogCode;
                return true;
            }

            code = _eventItemCodeByDetailId[eid];
            return true;
        }

        if (LuaScriptSemanticValidator.TryEvalConstantString(arg, out string? name) &&
            name is not null &&
            _itemCatalogIndex is not null &&
            _itemCatalogIndex.TryResolveEventItemByName(name, out _, out string byNameCode))
        {
            code = byNameCode;
            return true;
        }

        code = null;
        return false;
    }

    private string BuildEventItemSwitchLambda(LuaExpression arg, IReadOnlyDictionary<string, LuaExpression> constExprMap)
    {
        _ = LuaScriptSemanticValidator.TryExtractConstTableIntCandidates(arg, constExprMap, out List<int> cands);
        var arms = new List<string>();
        foreach (int id in cands.Distinct().OrderBy(static x => x))
        {
            string c = _itemCatalogIndex is not null && _itemCatalogIndex.TryResolveEventItem(id, out string cc)
                ? cc
                : _eventItemCodeByDetailId[id];
            arms.Add($"{id} => {ToEscapedStringLiteral(c)}");
        }

        arms.Add("_ => null");
        return $"static v => v switch {{ {string.Join(", ", arms)} }}";
    }

    private string BuildGoldItemSwitchLambda(LuaExpression arg, IReadOnlyDictionary<string, LuaExpression> constExprMap)
    {
        _ = LuaScriptSemanticValidator.TryExtractConstTableIntCandidates(arg, constExprMap, out List<int> cands);
        var arms = new List<string>();
        foreach (int rowId in cands.Distinct().OrderBy(static x => x))
        {
            if (_itemCatalogIndex!.TryResolveGoldEquipByRowId(rowId, out PackItemEntry entry) &&
                !string.IsNullOrEmpty(entry.Code))
            {
                arms.Add($"{rowId} => {ToEscapedStringLiteral(entry.Code)}");
            }
        }

        arms.Add("_ => null");
        return $"static v => v switch {{ {string.Join(", ", arms)} }}";
    }

    private string BuildDynamicParticularSwitchLambda(int g, int d, int lv, LuaExpression pExpr, IReadOnlyDictionary<string, LuaExpression> constExprMap)
    {
        _ = LuaScriptSemanticValidator.TryExtractConstTableIntCandidates(pExpr, constExprMap, out List<int> cands, _currentFnLocalTupleTables);
        var arms = new List<string>();
        foreach (int p in cands.Distinct().OrderBy(static x => x))
        {
            if (_itemCatalogIndex!.TryResolveByGdpl(g, d, p, lv, out PackItemEntry entry) &&
                !string.IsNullOrEmpty(entry.Code))
            {
                arms.Add($"{p} => {ToEscapedStringLiteral(entry.Code)}");
            }
        }

        arms.Add("_ => null");
        return $"static v => v switch {{ {string.Join(", ", arms)} }}";
    }

    private LuaValueType InferExpressionType(
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
                if (id.Name is "SubWorld" or "PlayerIndex")
                {
                    return LuaValueType.Int;
                }

                if (id.Name == "NpcName2Replace")
                {
                    return LuaValueType.Bool;
                }

                if (fnScopeTypes is not null && fnScopeTypes.TryGetValue(id.Name, out LuaValueType st))
                {
                    return st;
                }

                if (script.TopLevelConstantTypes.TryGetValue(id.Name, out LuaValueType tt))
                {
                    return tt;
                }

                if (script.FunctionReturnTypes.ContainsKey(id.Name))
                {
                    return LuaValueType.SyncCallback;
                }

                return LuaValueType.Int;
            case LuaIndexExpr idx:
                if (idx.Target is LuaIdentifierExpr rowVarId &&
                    _currentFnLocalTupleTables is not null &&
                    _currentFnLocalTupleTables.TryGetValue(rowVarId.Name, out string? rowTblName) &&
                    script.ConstTupleColumnTypes is not null &&
                    script.ConstTupleColumnTypes.TryGetValue(rowTblName, out IReadOnlyList<LuaValueType>? rowCols) &&
                    LuaScriptSemanticValidator.TryEvalConstantInt(idx.Index, out int rowCol1) &&
                    rowCol1 >= 1 &&
                    rowCol1 <= rowCols.Count)
                {
                    return rowCols[rowCol1 - 1];
                }

                if (idx.Target is LuaIndexExpr inner &&
                    inner.Target is LuaIdentifierExpr tupId &&
                    script.ConstTupleColumnTypes is not null &&
                    script.ConstTupleColumnTypes.TryGetValue(tupId.Name, out IReadOnlyList<LuaValueType>? cols) &&
                    LuaScriptSemanticValidator.TryEvalConstantInt(idx.Index, out int col1) &&
                    col1 >= 1 &&
                    col1 <= cols.Count)
                {
                    return cols[col1 - 1];
                }

                if (idx.Target is LuaIdentifierExpr outerTupId &&
                    script.ConstTupleColumnTypes is not null &&
                    script.ConstTupleColumnTypes.ContainsKey(outerTupId.Name))
                {
                    return LuaValueType.ConstTupleRow;
                }

                return InferExpressionType(idx.Target, script, fnScopeTypes) switch
                {
                    LuaValueType.StringArray or LuaValueType.StringList or LuaValueType.IntKeyedStringMap => LuaValueType.String,
                    LuaValueType.IntKeyedIntMap => LuaValueType.NullableInt,
                    LuaValueType.Int2DArray or LuaValueType.IntKeyedIntArrayMap => LuaValueType.IntArray,
                    LuaValueType.String2DArray => LuaValueType.StringArray,
                    LuaValueType.LocalAwardArray or LuaValueType.IntKeyedAwardTemplateMap => LuaValueType.AwardTemplate,
                    LuaValueType.IntKeyedIntRecordMap => LuaValueType.IntFieldRecord,
                    _ => LuaValueType.Int,
                };
            case LuaMemberAccessExpr:
                return LuaValueType.Int;
            case LuaTableLiteralExpr tbl when tbl.Elements.Count > 0 && tbl.Elements[0] is LuaKeyedTableLiteralExpr:
                return LuaValueType.LocalAwardArray;
            case LuaMemberCallExpr mcall:
                return mcall.Receiver is LuaIdentifierExpr { Name: "DailogClass" } && mcall.MemberName == "new"
                    ? LuaValueType.DialogBuilder
                    : LuaValueType.Int;
            case LuaBinaryExpr bin:
            {
                if (bin.Operator == LuaBinaryOp.Concat)
                {
                    return LuaValueType.String;
                }

                LuaValueType lt = InferExpressionType(bin.Left, script, fnScopeTypes);
                LuaValueType rt = InferExpressionType(bin.Right, script, fnScopeTypes);
                if (bin.Operator == LuaBinaryOp.Or && lt == LuaValueType.NullableInt && rt == LuaValueType.Int)
                {
                    return LuaValueType.Int;
                }

                if (bin.Operator == LuaBinaryOp.Or && lt == LuaValueType.String && rt == LuaValueType.String)
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

                if (call.CalleeName == "tonumber" && LuaScriptSemanticValidator.IsLongDateToNumberCall(call))
                {
                    return LuaValueType.Number;
                }

                return call.CalleeName switch
                {
                    "GetName" or "GetAccount" or "GetNpcName" or "NpcName2Replace" or "GetFaction" or "GetLastAddFaction" or "GetItemName" or "FormatTime2String" or "GetTong" or "tostring" or "strsub" or "strlower" or "strupper" or "format" or "GetLocalDate" or "date" => LuaValueType.String,
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
        sb.AppendLine("#pragma warning disable CS0162, CS0219, CS0414, CS8321");
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

    private static string ToCSharpType(LuaValueType t, bool isMonsterScript = false) =>
        t switch
        {
            LuaValueType.Int => "int",
            LuaValueType.NullableInt => "int?",
            LuaValueType.Number => "double",
            LuaValueType.String => "string",
            LuaValueType.Bool => "bool",
            LuaValueType.StringArray => "string[]",
            LuaValueType.IntArray => "int[]",
            LuaValueType.Int2DArray => "int[][]",
            LuaValueType.String2DArray => "string[][]",
            LuaValueType.IntKeyedIntMap => "FrozenDictionary<int, int>",
            LuaValueType.IntKeyedStringMap => "FrozenDictionary<int, string>",
            LuaValueType.IntKeyedIntArrayMap => "FrozenDictionary<int, int[]>",
            LuaValueType.IntKeyedAwardTemplateMap => "FrozenDictionary<int, GeneratedLuaRuntime.RuntimeAwardNode>",
            LuaValueType.IntKeyedIntRecordMap => "FrozenDictionary<int, FrozenDictionary<string, int>>",
            LuaValueType.IntFieldRecord => "FrozenDictionary<string, int>?",
            LuaValueType.StringList => "List<string>",
            LuaValueType.IntList => "List<int>",
            LuaValueType.LocalAwardArray => "GeneratedLuaRuntime.RuntimeAwardNode[]",
            LuaValueType.DialogOptionList => "List<DialogOption>",
            LuaValueType.DialogBuilder => "GeneratedLuaRuntime.DailogBuilder",
            LuaValueType.AwardTemplate => "GeneratedLuaRuntime.RuntimeAwardNode?",
            LuaValueType.SyncCallback => isMonsterScript ? "Action<IScriptCombatContext>" : "Func<IScriptDialogContext, CancellationToken, ValueTask>",
            _ => "void",
        };

    private static string DefaultValueExpr(LuaValueType t, bool isMonsterScript = false) =>
        t switch
        {
            LuaValueType.Int => "0",
            LuaValueType.NullableInt => "null",
            LuaValueType.Number => "0.0",
            LuaValueType.String => "string.Empty",
            LuaValueType.Bool => "false",
            LuaValueType.StringArray => "[]",
            LuaValueType.IntArray => "[]",
            LuaValueType.Int2DArray => "[]",
            LuaValueType.String2DArray => "[]",
            LuaValueType.IntKeyedIntMap => "FrozenDictionary<int, int>.Empty",
            LuaValueType.IntKeyedStringMap => "FrozenDictionary<int, string>.Empty",
            LuaValueType.IntKeyedIntArrayMap => "FrozenDictionary<int, int[]>.Empty",
            LuaValueType.IntKeyedAwardTemplateMap => "FrozenDictionary<int, GeneratedLuaRuntime.RuntimeAwardNode>.Empty",
            LuaValueType.IntKeyedIntRecordMap => "FrozenDictionary<int, FrozenDictionary<string, int>>.Empty",
            LuaValueType.IntFieldRecord => "null",
            LuaValueType.StringList => "new List<string>()",
            LuaValueType.IntList => "new List<int>()",
            LuaValueType.LocalAwardArray => "[]",
            LuaValueType.DialogOptionList => "new List<DialogOption>()",
            LuaValueType.DialogBuilder => "new GeneratedLuaRuntime.DailogBuilder()",
            LuaValueType.AwardTemplate => "null",
            LuaValueType.SyncCallback => isMonsterScript ? "static _ => { }" : "static (_, _) => ValueTask.CompletedTask",
            _ => "default",
        };

    private static string DefaultReturnExpr(LuaValueType retType, IReadOnlyList<LuaValueType>? retTuple, bool isMonsterScript)
    {
        if (retTuple is not null)
        {
            return "(" + string.Join(", ", retTuple.Select(t => DefaultValueExpr(t, isMonsterScript))) + ")";
        }

        return DefaultValueExpr(retType, isMonsterScript);
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
