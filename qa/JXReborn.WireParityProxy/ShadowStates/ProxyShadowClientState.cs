using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using JXReborn.Adapter.Legacy.Packets;
using JXReborn.ClientDriver;
using JXReborn.Primitives.Characters;
using JXReborn.Primitives.Items;
using JXReborn.Primitives.WorldZones;
using JXReborn.TestBase.WireCaptures;

namespace JXReborn.WireParityProxy.ShadowStates;

/// <summary>
/// Thread-safe live shadow state reconstructed purely from decrypted GameServer S2C packets
/// for either <c>legacy-linux</c> or <c>reborn-server</c>.
/// </summary>
public sealed class ProxyShadowClientState
{
    private const int RecentPacketLimit = 128;

    private readonly Lock _sync = new();
    private readonly Dictionary<uint, VirtualJxItemSlot> _itemsById = [];
    private readonly Dictionary<uint, VirtualJxNpcEntity> _npcsById = [];
    private readonly Dictionary<int, ShadowGroundObject> _groundObjects = [];
    private readonly Dictionary<int, ushort> _learnedSkills = [];
    private readonly Queue<S2cPacketSnapshot> _recentS2c = new();
    private readonly Queue<C2sPacketSnapshot> _recentC2s = new();
    private readonly List<SystemMessageSnapshot> _systemMessages = [];

    private uint _handItemId;
    private uint _s2cSeq;
    private uint _c2sSeq;
    private uint _dialogSeq;
    private int _groundObjSeq;
    private ShadowDialogState _dialog = ShadowDialogState.Closed;

    public ProxyShadowClientState(string backendLabel)
    {
        BackendLabel = backendLabel;
    }

    public string BackendLabel { get; }

    public uint SelfNpcId { get; private set; }

    public int MapId { get; private set; }

    public int MpsX { get; private set; }

    public int MpsY { get; private set; }

    public int Level { get; private set; }

    public long Exp { get; private set; }

    public int Life { get; private set; }

    public int MaxLife { get; private set; }

    public int Mana { get; private set; }

    public int MaxMana { get; private set; }

    public int Stamina { get; private set; }

    public int MaxStamina { get; private set; }

    public int Money { get; private set; }

    public int SaveMoney { get; private set; }

    public int PkFlag { get; private set; }

    public int PkValue { get; private set; }

    public int FightMode { get; private set; }

    public int Series { get; private set; }

    public int Camp { get; private set; }

    public int AttrPoints { get; private set; }

    public int SkillPoints { get; private set; }

    public bool IsSyncCompleted { get; private set; }

    public ShadowDialogState Dialog
    {
        get
        {
            lock (_sync)
            {
                return _dialog;
            }
        }
    }

    public void RecordOutboundC2s(ReadOnlySpan<byte> decryptedPayload, DateTimeOffset timestampUtc)
    {
        if (decryptedPayload.IsEmpty)
        {
            return;
        }

        lock (_sync)
        {
            uint seq = ++_c2sSeq;
            byte opcode = decryptedPayload[0];
            string hexPreview = Convert.ToHexString(decryptedPayload[..Math.Min(decryptedPayload.Length, 32)]);
            _recentC2s.Enqueue(new C2sPacketSnapshot(
                Seq: seq,
                Opcode: opcode,
                Length: decryptedPayload.Length,
                TickCount: seq,
                TimestampMs: (ulong)Math.Max(0, timestampUtc.ToUnixTimeMilliseconds()),
                HexPreview: hexPreview));
            while (_recentC2s.Count > RecentPacketLimit)
            {
                _recentC2s.Dequeue();
            }

            if (opcode == ProtocolOpcodes.c2s_playerselui)
            {
                _dialog = ShadowDialogState.Closed;
            }
        }
    }

    public void ApplyS2c(ReadOnlySpan<byte> decryptedPayload, DateTimeOffset timestampUtc)
    {
        if (decryptedPayload.IsEmpty)
        {
            return;
        }

        lock (_sync)
        {
            uint seq = ++_s2cSeq;
            byte opcode = decryptedPayload[0];
            string hexPreview = Convert.ToHexString(decryptedPayload[..Math.Min(decryptedPayload.Length, 32)]);
            _recentS2c.Enqueue(new S2cPacketSnapshot(
                Seq: seq,
                Opcode: opcode,
                Length: decryptedPayload.Length,
                TickCount: seq,
                TimestampMs: (ulong)Math.Max(0, timestampUtc.ToUnixTimeMilliseconds()),
                HexPreview: hexPreview));
            while (_recentS2c.Count > RecentPacketLimit)
            {
                _recentS2c.Dequeue();
            }

            ApplyS2cCore(opcode, decryptedPayload);
        }
    }

    public IReadOnlyDictionary<uint, VirtualJxNpcEntity> SnapshotNpcs()
    {
        lock (_sync)
        {
            return new Dictionary<uint, VirtualJxNpcEntity>(_npcsById);
        }
    }

    public IReadOnlyDictionary<uint, VirtualJxItemSlot> SnapshotItems()
    {
        lock (_sync)
        {
            return new Dictionary<uint, VirtualJxItemSlot>(_itemsById);
        }
    }

    public IReadOnlyDictionary<int, ShadowGroundObject> SnapshotGroundObjects()
    {
        lock (_sync)
        {
            return new Dictionary<int, ShadowGroundObject>(_groundObjects);
        }
    }

    public WorldDomSnapshot ToWorldDomSnapshot()
    {
        lock (_sync)
        {
            return ShadowStateSnapshotAdapter.Build(
                this,
                _dialog,
                _npcsById.Values.ToArray(),
                _itemsById.Values.ToArray(),
                _learnedSkills.ToArray(),
                _systemMessages.ToArray(),
                _recentS2c.ToArray(),
                _recentC2s.ToArray());
        }
    }

    private void ApplyS2cCore(byte opcode, ReadOnlySpan<byte> payload)
    {
        switch (opcode)
        {
            case ProtocolOpcodes.s2c_syncend:
                IsSyncCompleted = true;
                break;

            case ProtocolOpcodes.s2c_syncworld:
                if (payload.Length >= 5)
                {
                    int newMapId = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(1, 4));
                    if (newMapId != MapId)
                    {
                        _npcsById.Clear();
                        _groundObjects.Clear();
                    }

                    MapId = newMapId;
                }

                break;

            case ProtocolOpcodes.s2c_synccurplayer:
                if (payload.Length >= Marshal.SizeOf<SyncCurPlayerS2CPacket>())
                {
                    SyncCurPlayerS2CPacket pkt = MemoryMarshal.Read<SyncCurPlayerS2CPacket>(payload);
                    SelfNpcId = pkt.Id;
                    Level = pkt.Level;
                    Series = pkt.Series;
                    MaxLife = pkt.LifeMax;
                    MaxMana = pkt.ManaMax;
                    MaxStamina = pkt.StaminaMax;
                    Money = pkt.Money1;
                    SaveMoney = pkt.Money2;
                    AttrPoints = pkt.AttributePoint;
                    SkillPoints = pkt.SkillPoint;
                    Exp = (pkt.ExpHigh * CharacterConsts.ExpHighDivisor) + pkt.ExpRemainder;
                }

                break;

            case ProtocolOpcodes.s2c_synccurplayernormal:
                if (payload.Length >= Marshal.SizeOf<SyncCurPlayerNormalS2CPacket>())
                {
                    SyncCurPlayerNormalS2CPacket pkt = MemoryMarshal.Read<SyncCurPlayerNormalS2CPacket>(payload);
                    Stamina = pkt.Stamina;
                    Mana = pkt.Mana;
                    Life = pkt.Life;
                }

                break;

            case ProtocolOpcodes.s2c_playerlevelup:
                if (PlayerLevelUpS2CPacket.TryParse(payload, out PlayerLevelUpS2CPacket lv))
                {
                    Level = lv.NewLevel;
                    MaxLife = lv.BaseLifeMax;
                    MaxStamina = lv.BaseStaminaMax;
                    MaxMana = lv.BaseManaMax;
                }

                break;

            case ProtocolOpcodes.s2c_playerskilllevel:
                if (payload.Length >= 9)
                {
                    int skillId = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(1, 4));
                    int skillLevel = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(5, 4));
                    if (skillId > 0)
                    {
                        _learnedSkills[skillId] = (ushort)Math.Clamp(skillLevel, 0, ushort.MaxValue);
                    }
                }

                break;

            case ProtocolOpcodes.s2c_syncmoney:
                if (payload.Length >= 5)
                {
                    Money = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(1, 4));
                }

                break;

            case ProtocolOpcodes.s2c_pksyncnormalflag:
                if (payload.Length >= 2)
                {
                    PkFlag = payload[1];
                }

                break;

            case ProtocolOpcodes.s2c_pksyncpkvalue:
                if (payload.Length >= 2)
                {
                    PkValue = (sbyte)payload[1];
                }

                break;

            case ProtocolOpcodes.s2c_syncitem:
                if (SyncItemS2CPacket.TryParse(payload, out SyncItemS2CPacket item))
                {
                    _itemsById[item.RuntimeItemId] = new VirtualJxItemSlot(
                        item.RuntimeItemId,
                        item.Genre,
                        item.Quality,
                        item.DetailType,
                        item.ParticularType,
                        item.Series,
                        item.Level,
                        item.StackCount,
                        item.MaxDurability,
                        item.CurrentDurability,
                        item.ContainerPlace,
                        item.GridX,
                        item.GridY,
                        Math.Max(1, item.WidthCells),
                        Math.Max(1, item.HeightCells));
                    if (item.ContainerPlace == ItemContainerPlace.Hand)
                    {
                        _handItemId = item.RuntimeItemId;
                    }
                }

                break;

            case ProtocolOpcodes.s2c_removeitem:
                if (RemoveItemS2CPacket.TryParse(payload, out RemoveItemS2CPacket rem))
                {
                    _itemsById.Remove(rem.RuntimeItemId);
                    if (_handItemId == rem.RuntimeItemId)
                    {
                        _handItemId = 0;
                    }
                }

                break;

            case ProtocolOpcodes.s2c_playermoveitem:
                if (PlayerMoveItemS2CPacket.TryParse(payload, out PlayerMoveItemS2CPacket mv))
                {
                    ApplyMoveItem(mv);
                }

                break;

            case ProtocolOpcodes.s2c_syncnpc:
                if (SyncNpcFullS2CPacket.TryParse(payload, out SyncNpcFullS2CPacket npc))
                {
                    if (SelfNpcId == 0 && npc.Kind == EntityKind.Player)
                    {
                        SelfNpcId = npc.NpcId;
                    }

                    if (npc.NpcId == SelfNpcId)
                    {
                        MpsX = npc.MapMpsX;
                        MpsY = npc.MapMpsY;
                        Camp = (int)npc.Camp;
                        Series = (int)npc.Series;
                    }
                    else
                    {
                        _npcsById[npc.NpcId] = new VirtualJxNpcEntity(
                            npc.NpcId,
                            npc.TemplateId,
                            npc.Level,
                            npc.Kind,
                            npc.Camp,
                            npc.Series,
                            npc.MapMpsX,
                            npc.MapMpsY,
                            npc.DoingState,
                            npc.LifePercent,
                            npc.NameUnicode);
                    }
                }

                break;

            case ProtocolOpcodes.s2c_syncnpcmin:
                if (SyncNpcMinS2CPacket.TryParse(payload, out SyncNpcMinS2CPacket minNpc))
                {
                    if (minNpc.NpcId == SelfNpcId)
                    {
                        MpsX = minNpc.MpsX;
                        MpsY = minNpc.MpsY;
                    }
                    else if (_npcsById.TryGetValue(minNpc.NpcId, out VirtualJxNpcEntity? existing))
                    {
                        _npcsById[minNpc.NpcId] = existing with
                        {
                            MpsX = minNpc.MpsX,
                            MpsY = minNpc.MpsY,
                            DoingState = minNpc.Doing,
                            LifePercent128 = minNpc.LifePer,
                            Camp = minNpc.Camp
                        };
                    }
                    else
                    {
                        _npcsById[minNpc.NpcId] = new VirtualJxNpcEntity(
                            minNpc.NpcId,
                            TemplateId: 0,
                            Level: 0,
                            Kind: minNpc.Camp == 6 ? EntityKind.DialogNpc : EntityKind.Monster,
                            Camp: minNpc.Camp,
                            Series: ElementSeries.Metal,
                            MpsX: minNpc.MpsX,
                            MpsY: minNpc.MpsY,
                            DoingState: minNpc.Doing,
                            LifePercent128: minNpc.LifePer,
                            NameUnicode: string.Empty);
                    }
                }

                break;

            case ProtocolOpcodes.s2c_syncnpcminplayer:
                if (payload.Length >= SyncNpcMinPlayerS2CPacket.Size)
                {
                    SyncNpcMinPlayerS2CPacket posPkt = MemoryMarshal.Read<SyncNpcMinPlayerS2CPacket>(payload);
                    MpsX = posPkt.MapMpsX;
                    MpsY = posPkt.MapMpsY;
                }

                break;

            case ProtocolOpcodes.s2c_npcremove:
                if (payload.Length >= 5)
                {
                    uint npcId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(1, 4));
                    _npcsById.Remove(npcId);
                }

                break;

            case ProtocolOpcodes.s2c_npcdeath:
                if (payload.Length >= 5)
                {
                    uint npcId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(1, 4));
                    if (_npcsById.TryGetValue(npcId, out VirtualJxNpcEntity? deadNpc))
                    {
                        _npcsById[npcId] = deadNpc with { LifePercent128 = 0 };
                    }
                }

                break;

            case ProtocolOpcodes.s2c_objsyncpos:
                if (ObjSyncStateS2CPacket.TryParse(payload, out ObjSyncStateS2CPacket objState))
                {
                    if (_groundObjects.TryGetValue(objState.ObjId, out ShadowGroundObject existingObj))
                    {
                        _groundObjects[objState.ObjId] = existingObj with { State = objState.State };
                    }
                    else
                    {
                        _groundObjects[objState.ObjId] = new ShadowGroundObject(
                            objState.ObjId,
                            objState.State,
                            MpsX: 0,
                            MpsY: 0,
                            HasCoords: false,
                            FirstSeenOrder: ++_groundObjSeq);
                    }
                }

                break;

            case ProtocolOpcodes.s2c_objadd:
                if (ObjAddS2CPacket.TryParse(payload, out ObjAddS2CPacket objAdd))
                {
                    int seenOrder = _groundObjects.TryGetValue(objAdd.ObjId, out ShadowGroundObject existingObj)
                        ? existingObj.FirstSeenOrder
                        : ++_groundObjSeq;
                    _groundObjects[objAdd.ObjId] = new ShadowGroundObject(
                        objAdd.ObjId,
                        objAdd.State,
                        objAdd.MpsX,
                        objAdd.MpsY,
                        HasCoords: true,
                        FirstSeenOrder: seenOrder);
                }

                break;

            case ProtocolOpcodes.s2c_objremove:
                if (ObjRemoveS2CPacket.TryParse(payload, out ObjRemoveS2CPacket objRem))
                {
                    _groundObjects.Remove(objRem.ObjId);
                }

                break;

            case ProtocolOpcodes.s2c_scriptaction:
                ApplyScriptAction(payload);
                break;
        }
    }

    private void ApplyScriptAction(ReadOnlySpan<byte> payload)
    {
        if (!ScriptActionS2CPacket.TryParse(payload, out ScriptActionS2CPacket action))
        {
            return;
        }

        ReadOnlySpan<byte> content = action.Content;
        switch (action.UiId)
        {
            case ScriptActionS2CPacket.UiSelectDialog:
            {
                if (action.Param1 != 0)
                {
                    SetDialogState(Convert.ToHexString(content), [], isTalk: false);
                    return;
                }

                List<string> segments = SplitOnDelimiter(content, [0x00], maxSegments: 1 + action.OptionOrPageCount);
                string promptHex = segments.Count > 0 ? segments[0] : string.Empty;
                List<string> options = TakeOptions(segments, startIndex: 1, action.OptionOrPageCount);
                SetDialogState(promptHex, options, isTalk: false);
                break;
            }

            case ScriptActionS2CPacket.UiDescribeDialog:
            {
                ReadOnlySpan<byte> delimiter = [(byte)'|', (byte)' ', (byte)'|'];
                List<string> segments = SplitOnDelimiter(content, delimiter, maxSegments: 1 + action.OptionOrPageCount);
                string promptHex = segments.Count > 0 ? segments[0] : string.Empty;
                List<string> options = TakeOptions(segments, startIndex: 1, action.OptionOrPageCount);
                SetDialogState(promptHex, options, isTalk: false);
                break;
            }

            case ScriptActionS2CPacket.UiTalkDialog:
            {
                ReadOnlySpan<byte> delimiter = [(byte)'|', (byte)' ', (byte)'|'];
                List<string> segments = SplitOnDelimiter(content, delimiter, maxSegments: Math.Max(1, (int)action.OptionOrPageCount) + 1);
                string promptHex = string.Concat(segments.Where(static s => s.Length > 0));
                SetDialogState(promptHex, [], isTalk: true);
                break;
            }
        }
    }

    private void SetDialogState(string promptTcvn3Hex, IReadOnlyList<string> optionsTcvn3Hex, bool isTalk)
    {
        uint seq = ++_dialogSeq;
        _dialog = new ShadowDialogState(
            IsOpen: true,
            Sequence: seq,
            PromptTcvn3Hex: promptTcvn3Hex,
            OptionsTcvn3Hex: optionsTcvn3Hex,
            IsTalk: isTalk);
    }

    private static List<string> SplitOnDelimiter(ReadOnlySpan<byte> content, ReadOnlySpan<byte> delimiter, int maxSegments)
    {
        var segments = new List<string>();
        ReadOnlySpan<byte> remaining = content;
        while (segments.Count < maxSegments)
        {
            int idx = remaining.IndexOf(delimiter);
            if (idx < 0)
            {
                segments.Add(Convert.ToHexString(remaining));
                break;
            }

            segments.Add(Convert.ToHexString(remaining[..idx]));
            remaining = remaining[(idx + delimiter.Length)..];
            if (remaining.IsEmpty)
            {
                break;
            }
        }

        return segments;
    }

    private static List<string> TakeOptions(List<string> segments, int startIndex, int maxCount)
    {
        var options = new List<string>(Math.Min(maxCount, Math.Max(0, segments.Count - startIndex)));
        for (int i = startIndex; i < segments.Count && options.Count < maxCount; i++)
        {
            options.Add(segments[i]);
        }

        return options;
    }

    private void ApplyMoveItem(PlayerMoveItemS2CPacket mv)
    {
        if (_handItemId == 0)
        {
            VirtualJxItemSlot? downItem = FindItemCoveringCell(mv.DownPos, mv.DownX, mv.DownY);
            if (downItem is null)
            {
                return;
            }

            if (mv.UpPos == ItemContainerPlace.Hand ||
                (mv.DownPos == mv.UpPos && mv.DownX == mv.UpX && mv.DownY == mv.UpY))
            {
                _itemsById[downItem.RuntimeItemId] = downItem with
                {
                    ContainerPlace = ItemContainerPlace.Hand,
                    GridX = 0,
                    GridY = 0
                };
                _handItemId = downItem.RuntimeItemId;
            }
            else
            {
                VirtualJxItemSlot? displaced = FindItemCoveringCell(mv.UpPos, mv.UpX, mv.UpY);
                _itemsById[downItem.RuntimeItemId] = downItem with
                {
                    ContainerPlace = mv.UpPos,
                    GridX = mv.UpX,
                    GridY = mv.UpY
                };
                if (displaced is not null && displaced.RuntimeItemId != downItem.RuntimeItemId)
                {
                    _itemsById[displaced.RuntimeItemId] = displaced with
                    {
                        ContainerPlace = ItemContainerPlace.Hand,
                        GridX = 0,
                        GridY = 0
                    };
                    _handItemId = displaced.RuntimeItemId;
                }
            }
        }
        else if (_itemsById.TryGetValue(_handItemId, out VirtualJxItemSlot? handSlot))
        {
            VirtualJxItemSlot? displaced = FindItemCoveringCell(mv.UpPos, mv.UpX, mv.UpY);
            _itemsById[handSlot.RuntimeItemId] = handSlot with
            {
                ContainerPlace = mv.UpPos,
                GridX = mv.UpX,
                GridY = mv.UpY
            };
            if (displaced is not null && displaced.RuntimeItemId != handSlot.RuntimeItemId)
            {
                _itemsById[displaced.RuntimeItemId] = displaced with
                {
                    ContainerPlace = ItemContainerPlace.Hand,
                    GridX = 0,
                    GridY = 0
                };
                _handItemId = displaced.RuntimeItemId;
            }
            else
            {
                _handItemId = 0;
            }
        }
    }

    private VirtualJxItemSlot? FindItemCoveringCell(ItemContainerPlace place, byte x, byte y)
    {
        foreach (VirtualJxItemSlot slot in _itemsById.Values)
        {
            if (slot.ContainerPlace != place)
            {
                continue;
            }

            if (place == ItemContainerPlace.Equip)
            {
                if (slot.GridX == x)
                {
                    return slot;
                }

                continue;
            }

            if (x >= slot.GridX && x < slot.GridX + Math.Max(1, slot.WidthCells) &&
                y >= slot.GridY && y < slot.GridY + Math.Max(1, slot.HeightCells))
            {
                return slot;
            }
        }

        return null;
    }
}
