using System.Globalization;
using System.Text.RegularExpressions;
using JXReborn.ClientDriver;

namespace JXReborn.DualParityServer.Diffing;

/// <summary>
/// Semantic normalizer and diff engine comparing <see cref="WorldDomSnapshot"/> from <c>legacy-linux</c>
/// against <c>reborn-server</c> while filtering ephemeral IDs, tick counters, and regeneration noise.
/// </summary>
public sealed partial class SemanticDiffEngine
{
    private readonly DiffOptions _options;
    private int _nextFindingSequence;

    public SemanticDiffEngine(DiffOptions? options = null)
    {
        _options = options ?? new DiffOptions();
    }

    public StepComparisonResult Compare(
        int stepIndex,
        string stepName,
        string actionSummary,
        WorldDomSnapshot legacyAfter,
        WorldDomSnapshot rebornAfter,
        WorldDomSnapshot? legacyBefore = null,
        WorldDomSnapshot? rebornBefore = null,
        string? npcName = null,
        IReadOnlyList<string>? actions = null,
        FindingArtifacts? artifacts = null)
    {
        ArgumentNullException.ThrowIfNull(legacyAfter);
        ArgumentNullException.ThrowIfNull(rebornAfter);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var findings = new List<ParityFinding>();

        var repro = new FindingReproContext(
            MapId: legacyAfter.Player.MapId,
            PlayerPos: [legacyAfter.Player.MpsX, legacyAfter.Player.MpsY],
            NpcName: npcName,
            Actions: actions ?? [actionSummary]);

        var stateDelta = new FindingStateDelta(
            LegacyMoneyDelta: legacyBefore is null ? 0 : legacyAfter.Player.Money - legacyBefore.Player.Money,
            RebornMoneyDelta: rebornBefore is null ? 0 : rebornAfter.Player.Money - rebornBefore.Player.Money,
            LegacyExpDelta: legacyBefore is null ? 0 : legacyAfter.Player.Exp - legacyBefore.Player.Exp,
            RebornExpDelta: rebornBefore is null ? 0 : rebornAfter.Player.Exp - rebornBefore.Player.Exp,
            LegacyLevel: legacyAfter.Player.Level,
            RebornLevel: rebornAfter.Player.Level,
            LegacyMapId: legacyAfter.Player.MapId,
            RebornMapId: rebornAfter.Player.MapId);

        IReadOnlyList<string> legacyS2c = ExtractRecentS2cOpcodes(legacyAfter, legacyBefore);
        IReadOnlyList<string> rebornS2c = ExtractRecentS2cOpcodes(rebornAfter, rebornBefore);
        FindingArtifacts resolvedArtifacts = artifacts ?? new FindingArtifacts();

        void AddFinding(
            FindingSeverity severity,
            FindingCategory category,
            string fieldPath,
            string legacyValue,
            string rebornValue)
        {
            string scopeTag = string.IsNullOrWhiteSpace(npcName) ? $"Map{legacyAfter.Player.MapId}" : $"Map{legacyAfter.Player.MapId}::{npcName}";
            string clusterKey = $"{category}::{scopeTag}::{fieldPath}";
            if (_options.KnownDeviationClusterKeys.Contains(clusterKey))
            {
                return;
            }

            int seq = Interlocked.Increment(ref _nextFindingSequence);
            string findingId = $"F-{now:yyyyMMdd}-{seq:D4}";
            findings.Add(new ParityFinding(
                FindingId: findingId,
                ClusterKey: clusterKey,
                Severity: severity,
                Category: category,
                StepIndex: stepIndex,
                StepName: stepName,
                FieldPath: fieldPath,
                LegacyValue: legacyValue,
                RebornValue: rebornValue,
                Repro: repro,
                StateDelta: stateDelta,
                LegacyRecentS2cOpcodes: legacyS2c,
                RebornRecentS2cOpcodes: rebornS2c,
                Artifacts: resolvedArtifacts,
                TimestampUtc: now));
        }

        ComparePlayerState(legacyAfter.Player, rebornAfter.Player, AddFinding);
        ComparePosition(legacyAfter.Player, rebornAfter.Player, AddFinding);
        CompareDialog(legacyAfter.Dialog, rebornAfter.Dialog, AddFinding);

        if (_options.CompareInventory)
        {
            CompareInventory(legacyAfter.Items, rebornAfter.Items, AddFinding);
        }

        if (_options.CompareSystemMessages)
        {
            CompareSystemMessages(legacyAfter.SystemMessages, rebornAfter.SystemMessages, legacyBefore?.SystemMessages, rebornBefore?.SystemMessages, AddFinding);
        }

        if (_options.CompareNearbyStaticNpcs)
        {
            CompareNearbyStaticNpcs(legacyAfter.Npcs, rebornAfter.Npcs, AddFinding);
        }

        int criticalCount = findings.Count(f => f.Severity == FindingSeverity.Critical);
        int warningCount = findings.Count(f => f.Severity == FindingSeverity.Warning);

        return new StepComparisonResult(
            StepIndex: stepIndex,
            StepName: stepName,
            ActionSummary: actionSummary,
            TimestampUtc: now,
            Passed: criticalCount == 0,
            CriticalCount: criticalCount,
            WarningCount: warningCount,
            Findings: findings);
    }

    private static void ComparePlayerState(
        PlayerSnapshot legacy,
        PlayerSnapshot reborn,
        Action<FindingSeverity, FindingCategory, string, string, string> addFinding)
    {
        CompareExact("Player.InWorld", legacy.InWorld, reborn.InWorld, FindingSeverity.Critical, FindingCategory.PlayerState, addFinding);
        CompareExact("Player.MapId", legacy.MapId, reborn.MapId, FindingSeverity.Critical, FindingCategory.PlayerState, addFinding);
        CompareExact("Player.Level", legacy.Level, reborn.Level, FindingSeverity.Critical, FindingCategory.PlayerState, addFinding);
        CompareExact("Player.Exp", legacy.Exp, reborn.Exp, FindingSeverity.Critical, FindingCategory.PlayerState, addFinding);
        CompareExact("Player.Money", legacy.Money, reborn.Money, FindingSeverity.Critical, FindingCategory.PlayerState, addFinding);
        CompareExact("Player.SaveMoney", legacy.SaveMoney, reborn.SaveMoney, FindingSeverity.Critical, FindingCategory.PlayerState, addFinding);
        CompareExact("Player.Series", legacy.Series, reborn.Series, FindingSeverity.Critical, FindingCategory.PlayerState, addFinding);
        CompareExact("Player.Camp", legacy.Camp, reborn.Camp, FindingSeverity.Critical, FindingCategory.PlayerState, addFinding);
        CompareExact("Player.CurrentCamp", legacy.CurrentCamp, reborn.CurrentCamp, FindingSeverity.Critical, FindingCategory.PlayerState, addFinding);
        CompareExact("Player.FightMode", legacy.FightMode, reborn.FightMode, FindingSeverity.Critical, FindingCategory.PlayerState, addFinding);
        CompareExact("Player.PkFlag", legacy.PkFlag, reborn.PkFlag, FindingSeverity.Critical, FindingCategory.PlayerState, addFinding);
        CompareExact("Player.PkValue", legacy.PkValue, reborn.PkValue, FindingSeverity.Critical, FindingCategory.PlayerState, addFinding);
        CompareExact("Player.CurFaction", legacy.CurFaction, reborn.CurFaction, FindingSeverity.Critical, FindingCategory.PlayerState, addFinding);
        CompareExact("Player.LifeMax", legacy.LifeMax, reborn.LifeMax, FindingSeverity.Critical, FindingCategory.PlayerState, addFinding);
        CompareExact("Player.ManaMax", legacy.ManaMax, reborn.ManaMax, FindingSeverity.Critical, FindingCategory.PlayerState, addFinding);
        CompareExact("Player.StaminaMax", legacy.StaminaMax, reborn.StaminaMax, FindingSeverity.Warning, FindingCategory.PlayerState, addFinding);
        CompareExact("Player.AttrPoints", legacy.AttrPoints, reborn.AttrPoints, FindingSeverity.Critical, FindingCategory.PlayerState, addFinding);
        CompareExact("Player.SkillPoints", legacy.SkillPoints, reborn.SkillPoints, FindingSeverity.Critical, FindingCategory.PlayerState, addFinding);
        CompareExact("Player.ActiveAuraSkillId", legacy.ActiveAuraSkillId, reborn.ActiveAuraSkillId, FindingSeverity.Warning, FindingCategory.PlayerState, addFinding);
    }

    private void ComparePosition(
        PlayerSnapshot legacy,
        PlayerSnapshot reborn,
        Action<FindingSeverity, FindingCategory, string, string, string> addFinding)
    {
        if (legacy.MapId != reborn.MapId)
        {
            return;
        }

        int dx = Math.Abs(legacy.MpsX - reborn.MpsX);
        int dy = Math.Abs(legacy.MpsY - reborn.MpsY);
        int maxDrift = Math.Max(dx, dy);

        if (maxDrift > _options.CriticalPositionDriftMps)
        {
            addFinding(
                FindingSeverity.Critical,
                FindingCategory.Position,
                "Player.Mps",
                $"({legacy.MpsX}, {legacy.MpsY})",
                $"({reborn.MpsX}, {reborn.MpsY}) [drift={maxDrift}]");
        }
        else if (maxDrift > _options.PositionToleranceMps)
        {
            addFinding(
                FindingSeverity.Warning,
                FindingCategory.Position,
                "Player.Mps",
                $"({legacy.MpsX}, {legacy.MpsY})",
                $"({reborn.MpsX}, {reborn.MpsY}) [drift={maxDrift}]");
        }
    }

    private static void CompareDialog(
        DialogSnapshot legacy,
        DialogSnapshot reborn,
        Action<FindingSeverity, FindingCategory, string, string, string> addFinding)
    {
        if (legacy.IsOpen != reborn.IsOpen)
        {
            addFinding(
                FindingSeverity.Critical,
                FindingCategory.NpcDialog,
                "Dialog.IsOpen",
                legacy.IsOpen.ToString(),
                reborn.IsOpen.ToString());
            return;
        }

        if (!legacy.IsOpen)
        {
            return;
        }

        CompareExact("Dialog.IsTalk", legacy.IsTalk, reborn.IsTalk, FindingSeverity.Warning, FindingCategory.NpcDialog, addFinding);

        string legacyPrompt = NormalizeDialogPrompt(legacy.Prompt);
        string rebornPrompt = NormalizeDialogPrompt(reborn.Prompt);
        if (!string.Equals(legacyPrompt, rebornPrompt, StringComparison.Ordinal))
        {
            addFinding(
                FindingSeverity.Critical,
                FindingCategory.NpcDialog,
                "Dialog.Prompt",
                legacyPrompt,
                rebornPrompt);
        }

        IReadOnlyList<string> legacyOptions = legacy.Options;
        IReadOnlyList<string> rebornOptions = reborn.Options;
        if (legacyOptions.Count != rebornOptions.Count)
        {
            addFinding(
                FindingSeverity.Critical,
                FindingCategory.NpcDialog,
                "Dialog.Options.Count",
                $"{legacyOptions.Count} [{string.Join(" | ", legacyOptions.Select(NormalizeText))}]",
                $"{rebornOptions.Count} [{string.Join(" | ", rebornOptions.Select(NormalizeText))}]");
            return;
        }

        for (int i = 0; i < legacyOptions.Count; i++)
        {
            string lOpt = NormalizeText(legacyOptions[i]);
            string rOpt = NormalizeText(rebornOptions[i]);
            if (!string.Equals(lOpt, rOpt, StringComparison.Ordinal))
            {
                addFinding(
                    FindingSeverity.Critical,
                    FindingCategory.NpcDialog,
                    $"Dialog.Options[{i}]",
                    lOpt,
                    rOpt);
            }
        }
    }

    private static void CompareInventory(
        IReadOnlyList<InventoryItemSnapshot> legacyItems,
        IReadOnlyList<InventoryItemSnapshot> rebornItems,
        Action<FindingSeverity, FindingCategory, string, string, string> addFinding)
    {
        Dictionary<string, int> legacyCounts = AggregateInventory(legacyItems);
        Dictionary<string, int> rebornCounts = AggregateInventory(rebornItems);

        var allKeys = new SortedSet<string>(legacyCounts.Keys, StringComparer.Ordinal);
        allKeys.UnionWith(rebornCounts.Keys);

        foreach (string key in allKeys)
        {
            legacyCounts.TryGetValue(key, out int legacyQty);
            rebornCounts.TryGetValue(key, out int rebornQty);
            if (legacyQty != rebornQty)
            {
                addFinding(
                    FindingSeverity.Critical,
                    FindingCategory.Inventory,
                    $"Inventory[{key}]",
                    legacyQty.ToString(CultureInfo.InvariantCulture),
                    rebornQty.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    private void CompareSystemMessages(
        IReadOnlyList<SystemMessageSnapshot> legacyAfter,
        IReadOnlyList<SystemMessageSnapshot> rebornAfter,
        IReadOnlyList<SystemMessageSnapshot>? legacyBefore,
        IReadOnlyList<SystemMessageSnapshot>? rebornBefore,
        Action<FindingSeverity, FindingCategory, string, string, string> addFinding)
    {
        uint legacySince = legacyBefore is { Count: > 0 } ? legacyBefore[^1].Seq : 0;
        uint rebornSince = rebornBefore is { Count: > 0 } ? rebornBefore[^1].Seq : 0;

        List<string> legacyNew = legacyAfter
            .Where(m => m.Seq > legacySince)
            .Select(m => NormalizeText(m.Text))
            .Where(t => !string.IsNullOrWhiteSpace(t) && !IsIgnoredSystemMessage(t))
            .ToList();

        List<string> rebornNew = rebornAfter
            .Where(m => m.Seq > rebornSince)
            .Select(m => NormalizeText(m.Text))
            .Where(t => !string.IsNullOrWhiteSpace(t) && !IsIgnoredSystemMessage(t))
            .ToList();

        if (legacyNew.Count == 0 && rebornNew.Count == 0)
        {
            return;
        }

        if (!legacyNew.SequenceEqual(rebornNew, StringComparer.Ordinal))
        {
            addFinding(
                FindingSeverity.Warning,
                FindingCategory.SystemMessage,
                "SystemMessages.Delta",
                string.Join(" | ", legacyNew),
                string.Join(" | ", rebornNew));
        }
    }

    private static void CompareNearbyStaticNpcs(
        IReadOnlyList<NpcSnapshot> legacyNpcs,
        IReadOnlyList<NpcSnapshot> rebornNpcs,
        Action<FindingSeverity, FindingCategory, string, string, string> addFinding)
    {
        // Kind == 3 in JX Online represents dialog/town NPCs (non-combat static NPCs).
        HashSet<string> legacyNames = legacyNpcs
            .Where(n => !n.IsSelf && n.Kind == 3)
            .Select(n => NormalizeText(n.Name))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.Ordinal);

        HashSet<string> rebornNames = rebornNpcs
            .Where(n => !n.IsSelf && n.Kind == 3)
            .Select(n => NormalizeText(n.Name))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.Ordinal);

        List<string> missingOnReborn = legacyNames.Except(rebornNames).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (missingOnReborn.Count > 0)
        {
            addFinding(
                FindingSeverity.Warning,
                FindingCategory.NpcVisibility,
                "Npcs.DialogMissingOnReborn",
                string.Join(", ", missingOnReborn),
                "(missing)");
        }
    }

    private bool IsIgnoredSystemMessage(string message)
    {
        foreach (string sub in _options.IgnoredSystemMessageSubstrings)
        {
            if (message.Contains(sub, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, int> AggregateInventory(IReadOnlyList<InventoryItemSnapshot> items)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (InventoryItemSnapshot item in items)
        {
            int qty = Math.Max(1, item.StackCount);
            string key = $"Place{item.Place}:({item.Genre},{item.DetailType},{item.ParticularType},Lv{item.Level},S{item.Series})";
            map[key] = map.TryGetValue(key, out int cur) ? cur + qty : qty;
        }

        return map;
    }

    private static IReadOnlyList<string> ExtractRecentS2cOpcodes(WorldDomSnapshot after, WorldDomSnapshot? before)
    {
        uint sinceSeq = before?.RecentS2c is { Count: > 0 } ? before.RecentS2c[^1].Seq : 0;
        return after.RecentS2c
            .Where(p => p.Seq > sinceSeq)
            .TakeLast(16)
            .Select(p => $"0x{p.Opcode:X2}({p.Length}B)")
            .ToArray();
    }

    private static void CompareExact<T>(
        string fieldPath,
        T legacyValue,
        T rebornValue,
        FindingSeverity severity,
        FindingCategory category,
        Action<FindingSeverity, FindingCategory, string, string, string> addFinding)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(legacyValue, rebornValue))
        {
            addFinding(
                severity,
                category,
                fieldPath,
                legacyValue.ToString() ?? string.Empty,
                rebornValue.ToString() ?? string.Empty);
        }
    }

    private static string NormalizeDialogPrompt(string? value)
    {
        string normalized = NormalizeText(value);
        return string.IsNullOrEmpty(normalized)
            ? normalized
            : NpcSprDisplayNameRegex().Replace(normalized, "<<NPC>>");
    }

    [GeneratedRegex(@"(?<=#npcspr:\?NPCSID=\d+\?ACTION=\d+>)<<.*?>>(?=<link>:)", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex NpcSprDisplayNameRegex();

    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }
}
