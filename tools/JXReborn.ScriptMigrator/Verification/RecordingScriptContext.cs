using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JXReborn.Primitives.Characters;
using JXReborn.Primitives.Combat;
using JXReborn.Primitives.Items;
using JXReborn.Primitives.WorldZones;
using JXReborn.ZoneEngine.Factions;
using JXReborn.ZoneEngine.Scripting;

namespace JXReborn.ScriptMigrator.Verification;

/// <summary>
/// Deterministic in-memory implementation of all <c>IScript*Context</c> contracts recording every
/// side effect into <see cref="ScriptEffectTrace"/> and driving multi-step <c>Say</c>/<c>Talk</c> callbacks.
/// </summary>
public sealed class RecordingScriptContext :
    IScriptDialogContext,
    IScriptTrapContext,
    IScriptObjectContext,
    IScriptItemContext,
    IScriptCombatContext
{
    private readonly ScriptStateVector _vector;
    private readonly Dictionary<string, int> _bagItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, int> _learnedSkills = [];
    private DialogOption[]? _pendingOptions;
    private Func<IScriptDialogContext, ValueTask>? _pendingTalkContinuation;
    private (int Min, int Max, Func<IScriptDialogContext, int, ValueTask> Callback)? _pendingNumberInput;

    public ScriptEffectTrace Trace { get; }

    public RecordingScriptContext(string scriptPath, ScriptStateVector vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        _vector = vector;
        Trace = ScriptEffectTrace.CreateFromVector(vector);

        SourceNpcScriptPath = scriptPath;
        TrapScriptPath = scriptPath;
        ObjectScriptPath = scriptPath;
        ItemScriptPath = scriptPath;
        SlainNpcScriptPath = scriptPath;

        if (vector.OwnedItemCodes is not null)
        {
            foreach (string code in vector.OwnedItemCodes)
            {
                _bagItems[code] = 1;
            }
        }

        if (vector.OwnedItemCounts is not null)
        {
            foreach (KeyValuePair<string, int> kv in vector.OwnedItemCounts)
            {
                if (kv.Value > 0)
                {
                    _bagItems[kv.Key] = kv.Value;
                }
            }
        }

        if (vector.LearnedSkills is not null)
        {
            foreach (KeyValuePair<int, int> kv in vector.LearnedSkills)
            {
                if (kv.Value > 0)
                {
                    _learnedSkills[kv.Key] = kv.Value;
                }
            }
        }
    }

    public Guid CharacterId => Guid.Empty;
    public uint PlayerRuntimeNpcId => 10001u;
    public string RoleNameUtf8 => _vector.RoleName;
    public byte Level => _vector.Level;
    public CharacterGender Gender => (CharacterGender)_vector.Gender;
    public ElementSeries Series => (ElementSeries)_vector.Series;
    public byte CurrentFactionId => _vector.CurrentFactionId;
    public byte FirstFactionId => _vector.EffectiveFactionId;
    public byte EffectiveFactionId => _vector.EffectiveFactionId;
    public ushort FactionJoinCount => _vector.EffectiveFactionId == 255 ? (ushort)0 : (ushort)1;
    public ushort MapId => Trace.FinalMapId;
    public int CurrentMpsX => Trace.FinalMpsX;
    public int CurrentMpsY => Trace.FinalMpsY;
    public int InventoryMoney => Trace.FinalMoney;
    public int FreeBagCellCount => _vector.FreeBagCells;
    public uint? SourceNpcId => 20001u;
    public string? SourceNpcNameUtf8 => "NPC";
    public string? SourceNpcScriptPath { get; }
    public byte CurrentFightMode => Trace.FinalFightMode;

    public uint TrapId => 1u;
    public string TrapScriptCode => "oracle-trap";
    public string? TrapScriptPath { get; }

    public uint ObjectRuntimeId => 30001u;
    public int ObjectDataId => 1;
    public string ObjectNameUtf8 => "Object";
    public string? ObjectScriptPath { get; }
    public byte ObjectState => Trace.ObjectPropState ?? 0;

    public void SetPropState(byte state = 1) => Trace.ObjectPropState = (byte)(state != 0 ? 1 : 0);

    public uint RuntimeItemId => 40001u;
    public int ItemTemplateId => 1;
    public int ParticularType => 0;
    public string ItemCode => "oracle-item";
    public string? ItemScriptPath { get; }
    public int CurrentStackCount => 1;
    public uint TargetNpcId => 0u;

    public int SlainNpcTemplateId => 1;
    public string? SlainNpcScriptPath { get; }

    public int Repute => Trace.FinalRepute;

    public async ValueTask DrainInteractiveContinuationsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<int> choices = _vector.DialogChoices ?? [];
        IReadOnlyList<int> numInputs = _vector.NumberInputs ?? [];
        int choiceIdx = 0;
        int numIdx = 0;
        int guard = 0;

        while (guard++ < 32)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_pendingTalkContinuation is { } talkCb)
            {
                _pendingTalkContinuation = null;
                await talkCb(this);
                continue;
            }

            if (_pendingNumberInput is { } numReq)
            {
                _pendingNumberInput = null;
                if (numIdx >= numInputs.Count)
                {
                    break;
                }

                int val = Math.Clamp(numInputs[numIdx++], numReq.Min, Math.Max(numReq.Min, numReq.Max));
                await numReq.Callback(this, val);
                continue;
            }

            if (_pendingOptions is { Length: > 0 } opts)
            {
                _pendingOptions = null;
                if (choiceIdx >= choices.Count)
                {
                    break;
                }

                int pick = choices[choiceIdx++];
                if ((uint)pick < (uint)opts.Length && opts[pick].Callback is { } optCb)
                {
                    await optCb(this);
                    continue;
                }

                break;
            }

            break;
        }
    }

    public void SetFightMode(byte fightMode) => Trace.FinalFightMode = (byte)(fightMode != 0 ? 1 : 0);

    public int GetTask(ushort taskId) => Trace.GetTaskValue(taskId);

    public void SetTask(ushort taskId, int value) => Trace.SetTaskValue(taskId, value);

    public int GetTempTask(ushort tempTaskId) => Trace.GetTempTaskValue(tempTaskId);

    public void SetTempTask(ushort tempTaskId, int value) => Trace.SetTempTaskValue(tempTaskId, value);

    public byte GetTaskByte(ushort taskId, int byteIndex)
    {
        if (byteIndex is < 1 or > 4)
        {
            return 0;
        }

        int shift = (byteIndex - 1) * 8;
        return (byte)((unchecked((uint)GetTask(taskId)) >> shift) & 0xFFu);
    }

    public void SetTaskByte(ushort taskId, int byteIndex, byte value)
    {
        if (byteIndex is < 1 or > 4)
        {
            return;
        }

        int shift = (byteIndex - 1) * 8;
        uint current = unchecked((uint)GetTask(taskId));
        uint mask = ~(0xFFu << shift);
        SetTask(taskId, unchecked((int)((current & mask) | ((uint)value << shift))));
    }

    public bool GetTaskBit(ushort taskId, int bitIndex)
    {
        if (bitIndex is < 1 or > 32)
        {
            return false;
        }

        return ((unchecked((uint)GetTask(taskId)) >> (bitIndex - 1)) & 1u) != 0;
    }

    public void SetTaskBit(ushort taskId, int bitIndex, bool value)
    {
        if (bitIndex is < 1 or > 32)
        {
            return;
        }

        uint current = unchecked((uint)GetTask(taskId));
        uint mask = 1u << (bitIndex - 1);
        SetTask(taskId, unchecked((int)(value ? (current | mask) : (current & ~mask))));
    }

    public int GetGlobalValue(int globalValueId) => Trace.GetGlobalVal(globalValueId);

    public void SetGlobalValue(int globalValueId, int value) => Trace.SetGlobalVal(globalValueId, value);

    public void Say(string promptUtf8, params ReadOnlySpan<DialogOption> options)
    {
        DialogOption[] copied = options.ToArray();
        string[] labels = copied.Select(static o => o.TextUtf8).ToArray();
        Trace.Events.Add(ScriptEffectTrace.FormatSayEvent(promptUtf8, labels));
        _pendingOptions = copied.Length > 0 ? copied : null;
    }

    public void Describe(string richHeaderAndPromptUtf8, params ReadOnlySpan<DialogOption> options) =>
        Say(richHeaderAndPromptUtf8, options);

    public void Talk(IReadOnlyList<string> pagesUtf8, Func<IScriptDialogContext, ValueTask>? onCompleted = null)
    {
        Trace.Events.Add(ScriptEffectTrace.FormatTalkEvent(pagesUtf8));
        _pendingTalkContinuation = onCompleted;
    }

    public void SendSystemMessage(string messageUtf8) =>
        Trace.Events.Add($"MSG(\"{ScriptEffectTrace.EscapeEventText(messageUtf8)}\")");

    public void AddNote(string noteUtf8) =>
        Trace.Events.Add($"NOTE(\"{ScriptEffectTrace.EscapeEventText(noteUtf8)}\")");

    public void BroadcastToMap(string messageUtf8) =>
        Trace.Events.Add($"MAP_MSG(\"{ScriptEffectTrace.EscapeEventText(messageUtf8)}\")");

    public void BroadcastToTeam(string messageUtf8) =>
        Trace.Events.Add($"TEAM_MSG(\"{ScriptEffectTrace.EscapeEventText(messageUtf8)}\")");

    public void BroadcastToFaction(byte factionId, string messageUtf8) =>
        Trace.Events.Add($"FAC_MSG({factionId},\"{ScriptEffectTrace.EscapeEventText(messageUtf8)}\")");

    public void BroadcastGlobalNews(string messageUtf8) =>
        Trace.Events.Add($"NEWS(\"{ScriptEffectTrace.EscapeEventText(messageUtf8)}\")");

    public void AskClientForNumber(string titleUtf8, int minValue, int maxValue, Func<IScriptDialogContext, int, ValueTask> onSubmitted)
    {
        Trace.Events.Add($"ASK_NUM(\"{ScriptEffectTrace.EscapeEventText(titleUtf8)}\",{minValue},{maxValue})");
        _pendingNumberInput = (minValue, maxValue, onSubmitted);
    }

    public void AskClientForString(string titleUtf8, int minLength, int maxLength, Func<IScriptDialogContext, string, ValueTask> onSubmitted) =>
        Trace.Events.Add($"ASK_STR(\"{ScriptEffectTrace.EscapeEventText(titleUtf8)}\",{minLength},{maxLength})");

    public void GiveItemUI(string titleUtf8, string descriptionUtf8, Func<IScriptDialogContext, IReadOnlyList<SubmittedUiItem>, ValueTask> onSubmitted) =>
        Trace.Events.Add($"GIVE_UI(\"{ScriptEffectTrace.EscapeEventText(titleUtf8)}\")");

    public void SetProtectTime(int durationFrames)
    {
        if (durationFrames > 0)
        {
            Trace.ProtectFrames = durationFrames;
        }
    }

    public void AddSkillState(int skillId, int level, int stateType, int durationFrames)
    {
        if (skillId > 0)
        {
            Trace.SkillStates.Add((skillId, level, stateType, durationFrames));
        }
    }

    public void OpenDiceGame(int betMoney) => Trace.Events.Add($"DICE({betMoney})");

    public void ShowLadder(ReadOnlySpan<int> ladderIds) =>
        Trace.Events.Add($"LADDER([{string.Join(",", ladderIds.ToArray())}])");

    public bool TryPayMoney(int amountGold)
    {
        if (amountGold < 0 || Trace.FinalMoney < amountGold)
        {
            return false;
        }

        Trace.FinalMoney -= amountGold;
        return true;
    }

    public void GrantMoney(int amountGold)
    {
        if (amountGold > 0)
        {
            Trace.FinalMoney += amountGold;
        }
    }

    public void GrantExperience(long amountExp)
    {
        if (amountExp > 0)
        {
            Trace.ExpGranted += amountExp;
        }
    }

    public bool CanFitBagItem(byte gridWidthCells, byte gridHeightCells) => _vector.FreeBagCells > 0;

    public int GetBagItemCount(string itemCode) => _bagItems.GetValueOrDefault(itemCode, 0);

    public bool HasBagItem(string itemCode, int count = 1) => GetBagItemCount(itemCode) >= count;

    public bool TryGrantItemByCode(string itemCode, int stackCount = 1, ItemBindState bindState = ItemBindState.Unbound)
    {
        _bagItems[itemCode] = GetBagItemCount(itemCode) + stackCount;
        Trace.Events.Add($"GRANT_ITEM({itemCode},{stackCount})");
        return true;
    }

    public bool TryGrantEquipmentByCode(
        string itemCode,
        ElementSeries? seriesOverride = null,
        int maxRandomAffixes = 0,
        int sequentialAffixChancePercent = 0,
        ItemBindState bindState = ItemBindState.Unbound)
    {
        _bagItems[itemCode] = GetBagItemCount(itemCode) + 1;
        Trace.Events.Add($"GRANT_EQUIP({itemCode})");
        return true;
    }

    public bool TryConsumeItemByCode(string itemCode, int count = 1)
    {
        int cur = GetBagItemCount(itemCode);
        if (cur < count)
        {
            Trace.Events.Add($"CONSUME_ITEM_FAIL({itemCode},{count})");
            return false;
        }

        if (cur == count)
        {
            _bagItems.Remove(itemCode);
        }
        else
        {
            _bagItems[itemCode] = cur - count;
        }

        Trace.Events.Add($"CONSUME_ITEM({itemCode},{count})");
        return true;
    }

    public bool TryRemoveBagItemByRuntimeId(uint runtimeItemId) => true;

    public bool HasLearnedSkill(int skillId) => _learnedSkills.TryGetValue(skillId, out int lv) && lv > 0;

    public int GetLearnedSkillLevel(int skillId) => _learnedSkills.GetValueOrDefault(skillId, -1);

    public bool TryLearnSkill(int skillId, ushort baseLevel = 1)
    {
        _learnedSkills[skillId] = baseLevel;
        Trace.Events.Add($"LEARN_SKILL({skillId},{baseLevel})");
        return true;
    }

    public bool TryRemoveSkill(int skillId)
    {
        bool removed = _learnedSkills.Remove(skillId);
        Trace.Events.Add($"DEL_SKILL({skillId})");
        return removed;
    }

    public bool TryGrantBookSkill(int skillId, ushort baseLevel = 1) => TryLearnSkill(skillId, baseLevel);

    public void AddBonusSkillPoints(int points) => Trace.Events.Add($"ADD_SKILL_PTS({points})");

    public void AddBonusAttributePoints(int points) => Trace.Events.Add($"ADD_ATTR_PTS({points})");

    public void AddRepute(int delta) => Trace.FinalRepute += delta;

    public int FuYuan => Trace.FinalFuYuan;

    public void AddFuYuan(int delta) => Trace.FinalFuYuan = Math.Max(0, Trace.FinalFuYuan + delta);

    public EntityCamp BaseCamp => (EntityCamp)Trace.FinalBaseCamp;

    public EntityCamp CurrentCamp => (EntityCamp)Trace.FinalCurrentCamp;

    public void SetBaseCamp(EntityCamp camp) => Trace.FinalBaseCamp = (byte)camp;

    public void SetCurrentCamp(EntityCamp camp) => Trace.FinalCurrentCamp = (byte)camp;

    public void RestoreCurrentCamp() => Trace.FinalCurrentCamp = Trace.FinalBaseCamp;

    public ushort RevivalMapId => Trace.CurrentRevivalMapId;

    public int RevivalPointId => Trace.CurrentRevivalPointId;

    public PlayerPkMode PkMode => (PlayerPkMode)Trace.FinalPkMode;

    public void SetPkMode(PlayerPkMode mode) => Trace.FinalPkMode = (byte)mode;

    public sbyte PkValue => Trace.FinalPkValue;

    public void SetPkValue(sbyte pkValue) => Trace.FinalPkValue = (sbyte)Math.Clamp((int)pkValue, 0, 10);

    public byte RankId => Trace.FinalRankId;

    public void SetRank(byte rankId) => Trace.FinalRankId = rankId;

    public void SetRevivalPoint(ushort revivalMapId, int revivalPointId)
    {
        if (revivalPointId > 0)
        {
            Trace.CurrentRevivalMapId = revivalMapId;
            Trace.CurrentRevivalPointId = revivalPointId;
            Trace.RevivalPoint = (revivalMapId, revivalPointId);
        }
    }

    public void UnlockStation(ushort stationId)
    {
        if (stationId > 0)
        {
            Trace.UnlockedStations.Add(stationId);
        }
    }

    public IReadOnlyList<ushort> GetUnlockedStations() => Trace.UnlockedStations.ToArray();

    public void RecordWayPoint(int wayPointId)
    {
        if (wayPointId > 0)
        {
            Trace.RecordedWayPoints.Add(wayPointId);
        }
    }

    public IReadOnlyList<int> GetRecordedWayPoints() => Trace.RecordedWayPoints.ToArray();

    public bool IsExpandableBoxUnlocked(byte boxIndex = 1) => false;

    public void UnlockExpandableBox(byte boxIndex = 1) => Trace.Events.Add($"UNLOCK_BOX({boxIndex})");

    public ValueTask<bool> TeleportPlayerAsync(
        ushort targetMapId,
        int targetMpsX,
        int targetMpsY,
        byte targetFightMode = 0,
        CancellationToken cancellationToken = default)
    {
        Trace.DidTeleport = true;
        Trace.FinalMapId = targetMapId;
        Trace.FinalMpsX = targetMpsX;
        Trace.FinalMpsY = targetMpsY;
        Trace.FinalFightMode = (byte)(targetFightMode != 0 ? 1 : 0);
        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> TryReturnFromPortalAsync(CancellationToken cancellationToken = default)
    {
        Trace.Events.Add("RETURN_PORTAL");
        return ValueTask.FromResult(true);
    }

    public void OpenShop(int shopId, byte currencyType = 0, int scalePercent = 100) =>
        Trace.Events.Add($"OPEN_SHOP({shopId},{currencyType},{scalePercent})");

    public void OpenStoreBox() => Trace.OpenedStoreBox = true;
}
