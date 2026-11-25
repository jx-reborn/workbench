using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JXReborn.Primitives.Characters;
using JXReborn.Primitives.Combat;
using JXReborn.Primitives.Items;
using JXReborn.Primitives.WorldZones;
using JXReborn.ScriptMigrator.Generators;
using JXReborn.ZoneEngine.Factions;
using JXReborn.ZoneEngine.Scripting;
using JXReborn.ZoneEngine.WorldZones;

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

    public RecordingScriptContext(string scriptPath, ScriptStateVector vector, PackItemCatalogIndex? itemCatalogIndex = null)
    {
        ArgumentNullException.ThrowIfNull(vector);
        _vector = vector;
        Trace = ScriptEffectTrace.CreateFromVector(vector);

        SourceNpcScriptPath = scriptPath;
        TrapScriptPath = scriptPath;
        ObjectScriptPath = scriptPath;
        ItemScriptPath = scriptPath;
        SlainNpcScriptPath = scriptPath;
        Experience = vector.InitialExperience;
        ParticularType = vector.ParticularType;

        if (vector.ItemExpirationUnixSeconds > 0u)
        {
            _bagItemExpirations[40001u] = vector.ItemExpirationUnixSeconds;
        }

        if (vector.ItemSpecParams is { Count: > 0 } specParams)
        {
            int[] arr = new int[6];
            for (int i = 0; i < arr.Length && i < specParams.Count; i++)
            {
                arr[i] = specParams[i];
            }

            _bagItemSpecParams[40001u] = arr;
        }

        if (itemCatalogIndex is not null &&
            vector.ParticularType > 0 &&
            itemCatalogIndex.TryResolveByGdp(6, 1, vector.ParticularType, out PackItemEntry ownedItem))
        {
            _bagItemNames[40001u] = ownedItem.NameUtf8;
        }

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
    public long Experience { get; private set; }
    public ushort Lucky { get; private set; }
    public ushort UnallocatedAttributePoints { get; private set; }
    public ushort UnallocatedSkillPoints { get; private set; }
    public bool IsCreateTeamAllowed { get; set; } = true;
    public string? CustomDeathScriptPath { get; set; }

    private readonly Dictionary<uint, string> _bagItemNames = new() { [1u] = "ScriptItem", [40001u] = string.Empty };
    private readonly Dictionary<uint, uint> _bagItemExpirations = new();
    private readonly Dictionary<uint, int[]> _bagItemSpecParams = new();

    public void AddBaseStrength(int delta) => Trace.Events.Add($"ADD_BASE_STR({delta})");
    public void AddBaseDexterity(int delta) => Trace.Events.Add($"ADD_BASE_DEX({delta})");
    public void AddBaseVitality(int delta) => Trace.Events.Add($"ADD_BASE_VIT({delta})");
    public void AddBaseEnergy(int delta) => Trace.Events.Add($"ADD_BASE_ENG({delta})");
    public ElementSeries Series => (ElementSeries)_vector.Series;
    public byte CurrentFactionId => Trace.FinalCurrentFactionId;
    public byte FirstFactionId => _vector.EffectiveFactionId;
    public byte EffectiveFactionId => Trace.FinalCurrentFactionId != 255 ? Trace.FinalCurrentFactionId : _vector.EffectiveFactionId;
    public ushort FactionJoinCount => _vector.EffectiveFactionId == 255 ? (ushort)0 : (ushort)1;
    public ushort MapId => Trace.FinalMapId;
    public int CurrentMpsX => Trace.FinalMpsX;
    public int CurrentMpsY => Trace.FinalMpsY;
    public int InventoryMoney => Trace.FinalMoney;
    public int FreeBagCellCount => _vector.FreeBagCells;
    public uint? SourceNpcId => 20001u;
    public int? SourceNpcTemplateId => 0;
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
    public int ParticularType { get; }
    public string ItemCode => "oracle-item";
    public string? ItemScriptPath { get; }
    public int CurrentStackCount => 1;
    public uint TargetNpcId => 0u;

    public int SlainNpcTemplateId => 1;
    public short SlainNpcLevel => 60;
    public bool IsSlainNpcSpecial => false;
    public int SlainNpcParam1 => 0;
    public int SlainNpcMpsX => Trace.FinalMpsX;
    public int SlainNpcMpsY => Trace.FinalMpsY;
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

    public void SayByStringId(int stringId, params ReadOnlySpan<DialogOption> options) =>
        Say($"STR_ID:{stringId}", options);

    public void Describe(string richHeaderAndPromptUtf8, params ReadOnlySpan<DialogOption> options) =>
        Say(richHeaderAndPromptUtf8, options);

    public void Prise(string promptUtf8, params ReadOnlySpan<PriseDialogOption> options)
    {
        PriseDialogOption[] copied = options.ToArray();
        DialogOption[] mapped = copied.Select(static o => new DialogOption(o.TextWithIconUtf8, o.Callback)).ToArray();
        Say(promptUtf8, mapped);
    }

    public void SendTaskTrace() => Trace.Events.Add("TASK_TRACE");

    public void Talk(IReadOnlyList<string> pagesUtf8, Func<IScriptDialogContext, ValueTask>? onCompleted = null)
    {
        Trace.Events.Add(Trace.PrefixForActivePlayer(ScriptEffectTrace.FormatTalkEvent(pagesUtf8)));
        _pendingTalkContinuation = onCompleted;
    }

    public void TalkByStringIds(IReadOnlyList<int> stringIds, Func<IScriptDialogContext, ValueTask>? onCompleted = null) =>
        Talk(stringIds.Select(static id => $"STR_ID:{id}").ToArray(), onCompleted);

    public void SendSystemMessage(string messageUtf8) =>
        Trace.Events.Add(Trace.PrefixForActivePlayer($"MSG(\"{ScriptEffectTrace.EscapeEventText(messageUtf8)}\")"));

    public void AddNote(string noteUtf8) =>
        Trace.Events.Add(Trace.PrefixForActivePlayer($"NOTE(\"{ScriptEffectTrace.EscapeEventText(noteUtf8)}\")"));

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

    public void AddSkillState(int skillId, int level, int stateType, int durationFrames, bool noClearOnDeath)
    {
        if (skillId > 0)
        {
            Trace.SkillStates.Add((skillId, level, stateType, durationFrames));
            if (noClearOnDeath)
            {
                Trace.Events.Add($"SKILL_STATE_NOCLEAR({skillId},{level},{stateType},{durationFrames})");
            }
        }
    }

    public void RemoveSkillState(int skillId) =>
        Trace.Events.Add($"REMOVE_SKILL_STATE({skillId})");

    public void AddDirectSkillExp(int skillId, int expDelta)
    {
        if (skillId > 0 && expDelta != 0)
        {
            Trace.Events.Add($"SKILL_EXP({skillId},{expDelta})");
        }
    }

    public bool TrySpawnDynamicNpcOnMap(
        ushort targetMapId,
        int templateId,
        short level,
        int mpsX,
        int mpsY,
        string? customNameUtf8 = null,
        bool isBoss = false,
        string? scriptPath = null,
        string? scriptCode = null,
        int scriptParam1 = 0,
        int expireFrames = 0)
    {
        Trace.Events.Add($"SPAWN_NPC({targetMapId},{templateId},{level},{mpsX},{mpsY},{isBoss},{scriptParam1},{expireFrames})");
        return true;
    }

    public bool TryDropItemAtSlainNpc(string itemCode, int stackCount = 1)
    {
        Trace.Events.Add($"DROP_AT_SLAIN({itemCode},{stackCount})");
        return true;
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
            Experience += amountExp;
        }

        Trace.AddExperienceForActivePlayer(amountExp);
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

    public bool TryGrantEquipmentByCode(
        string itemCode,
        ElementSeries series,
        ReadOnlySpan<int> magicLevels,
        ItemBindState bindState = ItemBindState.Unbound,
        ushort? luckOverride = null,
        ushort? currentDurabilityOverride = null)
    {
        _ = luckOverride;
        _ = currentDurabilityOverride;
        _bagItems[itemCode] = GetBagItemCount(itemCode) + 1;
        string levels = string.Join(",", magicLevels.ToArray());
        Trace.Events.Add($"GRANT_EQUIP_EX({itemCode},{(int)series},[{levels}],{(int)bindState})");
        return true;
    }

    public bool IsOwnedBagItem(uint runtimeItemId) => runtimeItemId != 0 && _bagItemNames.ContainsKey(runtimeItemId);

    public string GetBagItemNameUtf8(uint runtimeItemId) =>
        _bagItemNames.TryGetValue(runtimeItemId, out string? name) ? name : string.Empty;

    public uint? GetBagItemExpirationUnixSeconds(uint runtimeItemId) =>
        _bagItemExpirations.TryGetValue(runtimeItemId, out uint exp) ? exp : null;

    public bool TrySetBagItemExpirationDate(uint runtimeItemId, int dateOrMinutes, int hhmmss = 0)
    {
        if (runtimeItemId == 0)
        {
            return false;
        }

        uint unixSeconds;
        if (dateOrMinutes <= 20_000_000)
        {
            if (dateOrMinutes <= 0)
            {
                return false;
            }

            DateTime nowDt = _vector.SimulatedLocalTime ?? new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
            DateTime utc = nowDt.Kind == DateTimeKind.Utc ? nowDt : DateTime.SpecifyKind(nowDt, DateTimeKind.Utc);
            unixSeconds = (uint)new DateTimeOffset(utc).AddMinutes(dateOrMinutes).ToUnixTimeSeconds();
        }
        else
        {
            int year = dateOrMinutes / 10000;
            int month = (dateOrMinutes / 100) % 100;
            int day = dateOrMinutes % 100;
            int hour = hhmmss / 10000;
            int minute = (hhmmss / 100) % 100;
            int second = hhmmss % 100;
            var dt = new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero);
            unixSeconds = (uint)dt.ToUnixTimeSeconds();
        }

        _bagItemExpirations[runtimeItemId] = unixSeconds;
        Trace.Events.Add($"SET_BAG_EXPIRATION({runtimeItemId},{dateOrMinutes},{hhmmss})");
        return true;
    }

    public int GetBagItemSpecParam(uint runtimeItemId, int oneBasedParamIndex)
    {
        if (oneBasedParamIndex is < 1 or > 6)
        {
            return 0;
        }

        return _bagItemSpecParams.TryGetValue(runtimeItemId, out int[]? arr) ? arr[oneBasedParamIndex - 1] : 0;
    }

    public bool TrySetBagItemSpecParam(uint runtimeItemId, int oneBasedParamIndex, int value)
    {
        if (runtimeItemId == 0 || oneBasedParamIndex is < 1 or > 6)
        {
            return false;
        }

        if (!_bagItemSpecParams.TryGetValue(runtimeItemId, out int[]? arr))
        {
            arr = new int[6];
            _bagItemSpecParams[runtimeItemId] = arr;
        }

        arr[oneBasedParamIndex - 1] = value;
        Trace.Events.Add($"SET_BAG_SPEC_PARAM({runtimeItemId},{oneBasedParamIndex},{value})");
        return true;
    }

    public void SyncBagItem(uint runtimeItemId)
    {
        Trace.Events.Add($"SYNC_BAG_ITEM({runtimeItemId})");
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

    public int RollbackSkills()
    {
        int total = 0;
        foreach (int lvl in _learnedSkills.Values)
        {
            total += lvl;
        }

        _learnedSkills.Clear();
        Trace.Events.Add($"ROLLBACK_SKILLS({total})");
        return total;
    }

    public void RestoreLife() => Trace.Events.Add("RESTORE_LIFE");

    public void RestoreMana() => Trace.Events.Add("RESTORE_MANA");

    public void RestoreStamina() => Trace.Events.Add("RESTORE_STAMINA");

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

    public bool IsPkSwitchForbidden
    {
        get => Trace.IsPkSwitchForbidden;
        set => Trace.IsPkSwitchForbidden = value;
    }

    public bool IsDeathPunishEnabled
    {
        get => !Trace.IsDeathPunishDisabled;
        set => Trace.IsDeathPunishDisabled = !value;
    }

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
            Trace.TempDeathRevival = null;
        }
    }

    public void SetTemporaryDeathRevivalPosition(ushort mapId, int mpsX, int mpsY) =>
        Trace.TempDeathRevival = (mapId, mpsX, mpsY);

    public void SetTemporaryDeathRevivalByPointId(int revivePointId)
    {
        if (RevivalPositionCatalog.TryGetCoordinates(Trace.FinalMapId, revivePointId, out int rx, out int ry))
        {
            Trace.TempDeathRevival = (Trace.FinalMapId, rx, ry);
        }
        else
        {
            Trace.TempDeathRevival = (57, 50976, 102208);
        }
    }

    public (ushort MapId, int MpsX, int MpsY) ResolveRevivalPosition(ushort mapId, int revivePointId) =>
        RevivalPositionCatalog.TryGetCoordinates(mapId, revivePointId, out int rx, out int ry)
            ? (mapId, rx, ry)
            : (mapId, 0, 0);

    public bool UseReviveIdOnLogin
    {
        get => Trace.UseReviveIdOnLogin;
        set => Trace.UseReviveIdOnLogin = value;
    }

    public bool IsTownPortalDisabled
    {
        get => Trace.IsTownPortalDisabled;
        set => Trace.IsTownPortalDisabled = value;
    }

    public int ActiveScriptTimerId => Trace.ActiveScriptTimerId;

    public void SetScriptTimer(int durationFrames, int timerId)
    {
        if (durationFrames <= 0 || timerId <= 0)
        {
            StopScriptTimer();
            return;
        }

        Trace.ActiveScriptTimerId = timerId;
        Trace.Events.Add($"SET_TIMER({durationFrames},{timerId})");
    }

    public void StopScriptTimer()
    {
        Trace.ActiveScriptTimerId = 0;
        Trace.Events.Add("STOP_TIMER");
    }

    public int CurrentPlayerIndex
    {
        get => Trace.CurrentPlayerIndex;
        set => Trace.SwitchActivePlayer(value);
    }

    private int? _teamSizeOverride;

    public bool IsPartyCaptain => _teamSizeOverride == 0 ? false : Trace.IsCurrentPlayerCaptain;

    public int GetTeamSize() => _teamSizeOverride ?? Trace.TeamSize;

    public void LeaveParty()
    {
        _teamSizeOverride = 0;
        Trace.Events.Add("LEAVE_PARTY");
    }

    public int GetTeamMemberIndex(int oneBasedMemberOrdinal) =>
        Trace.TeamSize > 0 && (uint)(oneBasedMemberOrdinal - 1) < (uint)Trace.TeamSize
            ? oneBasedMemberOrdinal
            : 0;

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

    public FactionProgressionResult TryJoinFaction(byte factionId)
    {
        Trace.FinalCurrentFactionId = factionId;
        return FactionProgressionResult.Success;
    }

    public FactionProgressionResult TryGraduateFaction()
    {
        Trace.FinalCurrentFactionId = 255;
        return FactionProgressionResult.Success;
    }

    public void OpenStoreBox() => Trace.OpenedStoreBox = true;

    public void SyncMiniMapMarker(int mpsX, int mpsY) => Trace.Events.Add($"MINIMAP_OBJ({mpsX},{mpsY})");

    public bool RegisterPlayerEvent(ushort eventId)
    {
        Trace.Events.Add($"ADD_PLAYER_EVENT({eventId})");
        return true;
    }
}
