using System.Buffers.Binary;
using JXReborn.Adapter.Legacy.Packets;
using JXReborn.Primitives.Items;
using JXReborn.Primitives.WorldZones;
using JXReborn.TestBase.WireCaptures;

namespace JXReborn.WireParityProxy.Translation;

/// <summary>
/// Translates dynamic entity IDs and bag coordinates inside outbound C2S packets before forwarding
/// them to the Shadow backend, re-stamping <c>dwCheckValue</c> whenever a protected packet is rewritten.
/// </summary>
public sealed class C2sPacketRewriter
{
    private readonly EntityIdTranslator _translator;

    public C2sPacketRewriter(EntityIdTranslator translator)
    {
        ArgumentNullException.ThrowIfNull(translator);
        _translator = translator;
    }

    public C2sRewriteResult RewriteForShadow(
        ReadOnlySpan<byte> primaryC2sPayload,
        uint clientToken1,
        uint clientToken2)
    {
        byte[] copy = primaryC2sPayload.ToArray();
        if (copy.Length == 0)
        {
            return new C2sRewriteResult(copy, WasModified: false, TranslationMissReason: null);
        }

        byte opcode = copy[0];
        switch (opcode)
        {
            case ProtocolOpcodes.c2s_requestnpc when copy.Length >= RequestNpcC2SPacket.PayloadSize:
                return RewriteTargetNpc(
                    copy,
                    npcIdOffset: 1,
                    checkValueOffset: -1,
                    checkKey: 0,
                    clientToken1,
                    clientToken2,
                    label: "c2s_requestnpc");

            case ProtocolOpcodes.c2s_npcstaterequest when copy.Length >= NpcStateRequestC2SPacket.PayloadSize:
                return RewriteTargetNpc(
                    copy,
                    npcIdOffset: 1,
                    checkValueOffset: -1,
                    checkKey: 0,
                    clientToken1,
                    clientToken2,
                    label: "c2s_npcstaterequest");

            case ProtocolOpcodes.c2s_requestnpcfeature when copy.Length >= 5:
                return RewriteTargetNpc(
                    copy,
                    npcIdOffset: 1,
                    checkValueOffset: -1,
                    checkKey: 0,
                    clientToken1,
                    clientToken2,
                    label: "c2s_requestnpcfeature");

            case ProtocolOpcodes.c2s_viewequip when copy.Length >= ViewEquipC2SPacket.PayloadSize:
                return RewriteTargetNpc(
                    copy,
                    npcIdOffset: 1,
                    checkValueOffset: -1,
                    checkKey: 0,
                    clientToken1,
                    clientToken2,
                    label: "c2s_viewequip");

            case ProtocolOpcodes.c2s_requestobj when copy.Length >= RequestObjC2SPacket.PayloadSize:
                return RewriteTargetObj(copy, objIdOffset: 1, isMouseClick: false, label: "c2s_requestobj");

            case ProtocolOpcodes.c2s_objmouseclick when copy.Length >= ObjMouseClickC2SPacket.PayloadSize:
                return RewriteTargetObj(copy, objIdOffset: 5, isMouseClick: true, label: "c2s_objmouseclick");

            case ProtocolOpcodes.c2s_dialognpc when copy.Length >= 5:
                return RewriteTargetNpc(
                    copy,
                    npcIdOffset: 1,
                    checkValueOffset: copy.Length >= 9 ? 5 : -1,
                    checkKey: MovementAntiCheatConsts.DialogNpcCheckKey,
                    clientToken1,
                    clientToken2,
                    label: "c2s_dialognpc");

            case ProtocolOpcodes.c2s_npcskill when copy.Length >= NpcSkillC2SPacket.PayloadSize:
                {
                    int targetMpsX = BinaryPrimitives.ReadInt32LittleEndian(copy.AsSpan(9, 4));
                    if (targetMpsX != -1)
                    {
                        return new C2sRewriteResult(copy, WasModified: false, TranslationMissReason: null);
                    }

                    return RewriteTargetNpc(
                        copy,
                        npcIdOffset: 1,
                        checkValueOffset: 13,
                        checkKey: MovementAntiCheatConsts.SkillCheckKey,
                        clientToken1,
                        clientToken2,
                        label: "c2s_npcskill");
                }

            case ProtocolOpcodes.c2s_playereatitem when copy.Length == PlayerEatItemC2SPacket.PayloadSize:
                return RewriteEatItem(copy);

            case ProtocolOpcodes.c2s_playermoveitem when copy.Length == PlayerMoveItemC2SPacket.PayloadSize:
                return RewriteMoveItem(copy);

            case ProtocolOpcodes.c2s_giveitemui when GiveItemUiC2SPacket.TryParse(copy, out GiveItemUiC2SPacket giveUi):
                return RewriteGiveItemUi(copy, giveUi);

            case ProtocolOpcodes.c2s_tradeapplystart when copy.Length >= 5:
                return RewriteTargetNpc(
                    copy,
                    npcIdOffset: 1,
                    checkValueOffset: copy.Length >= 9 ? 5 : -1,
                    checkKey: MovementAntiCheatConsts.TradeStartCheckKey,
                    clientToken1,
                    clientToken2,
                    label: "c2s_tradeapplystart");

            default:
                return new C2sRewriteResult(copy, WasModified: false, TranslationMissReason: null);
        }
    }

    private C2sRewriteResult RewriteTargetNpc(
        byte[] copy,
        int npcIdOffset,
        int checkValueOffset,
        uint checkKey,
        uint token1,
        uint token2,
        string label)
    {
        uint primaryNpcId = BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(npcIdOffset, 4));
        if (primaryNpcId == 0)
        {
            return new C2sRewriteResult(copy, WasModified: false, TranslationMissReason: null);
        }

        if (!_translator.TryTranslateNpcId(primaryNpcId, out uint shadowNpcId))
        {
            return new C2sRewriteResult(
                copy,
                WasModified: false,
                TranslationMissReason: $"{label}: could not translate Primary NpcId={primaryNpcId} to Shadow.");
        }

        bool modified = shadowNpcId != primaryNpcId;
        if (modified)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(npcIdOffset, 4), shadowNpcId);
            if (checkValueOffset >= 0 && copy.Length >= checkValueOffset + 4 && checkKey != 0)
            {
                uint checkValue = MovementAntiCheatConsts.CreateCheckValue(checkKey, token1, token2);
                BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(checkValueOffset, 4), checkValue);
            }
        }

        return new C2sRewriteResult(copy, WasModified: modified, TranslationMissReason: null);
    }

    private C2sRewriteResult RewriteTargetObj(byte[] copy, int objIdOffset, bool isMouseClick, string label)
    {
        int primaryObjId = BinaryPrimitives.ReadInt32LittleEndian(copy.AsSpan(objIdOffset, 4));
        if (primaryObjId <= 0)
        {
            return new C2sRewriteResult(copy, WasModified: false, TranslationMissReason: null);
        }

        if (!_translator.TryTranslateObjId(primaryObjId, out int shadowObjId, isMouseClick))
        {
            return new C2sRewriteResult(
                copy,
                WasModified: false,
                TranslationMissReason: $"{label}: could not translate Primary ObjId={primaryObjId} to Shadow.");
        }

        bool modified = shadowObjId != primaryObjId;
        if (modified)
        {
            BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(objIdOffset, 4), shadowObjId);
        }

        return new C2sRewriteResult(copy, WasModified: modified, TranslationMissReason: null);
    }

    private C2sRewriteResult RewriteEatItem(byte[] copy)
    {
        byte gridX = copy[1];
        uint itemId = BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(2, 4));
        byte gridY = copy[6];
        var place = (ItemContainerPlace)copy[7];

        if (!_translator.TryTranslateItemByIdOrCell(itemId, place, gridX, gridY, out VirtualJxItemSlot? shadowItem) ||
            shadowItem is null)
        {
            return new C2sRewriteResult(
                copy,
                WasModified: false,
                TranslationMissReason: $"c2s_playereatitem: could not translate Primary item (id={itemId}, place={place}, x={gridX}, y={gridY}).");
        }

        bool modified = shadowItem.GridX != gridX ||
                        shadowItem.GridY != gridY ||
                        shadowItem.ContainerPlace != place ||
                        shadowItem.RuntimeItemId != itemId;

        if (modified)
        {
            copy[1] = shadowItem.GridX;
            BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(2, 4), shadowItem.RuntimeItemId);
            copy[6] = shadowItem.GridY;
            copy[7] = (byte)shadowItem.ContainerPlace;
        }

        return new C2sRewriteResult(copy, WasModified: modified, TranslationMissReason: null);
    }

    private C2sRewriteResult RewriteMoveItem(byte[] copy)
    {
        byte downY = copy[1];
        var downPos = (ItemContainerPlace)copy[3];
        byte downX = copy[4];

        if (downPos != ItemContainerPlace.EquipRoom)
        {
            return new C2sRewriteResult(copy, WasModified: false, TranslationMissReason: null);
        }

        if (!_translator.TryTranslateItemByIdOrCell(0, downPos, downX, downY, out VirtualJxItemSlot? shadowItem) ||
            shadowItem is null)
        {
            return new C2sRewriteResult(copy, WasModified: false, TranslationMissReason: null);
        }

        bool modified = shadowItem.GridX != downX || shadowItem.GridY != downY;
        if (modified)
        {
            copy[1] = shadowItem.GridY;
            copy[4] = shadowItem.GridX;
        }

        return new C2sRewriteResult(copy, WasModified: modified, TranslationMissReason: null);
    }

    private C2sRewriteResult RewriteGiveItemUi(byte[] copy, GiveItemUiC2SPacket giveUi)
    {
        bool modified = false;
        for (int i = 0; i < giveUi.Entries.Length; i++)
        {
            GiveItemUiEntry entry = giveUi.Entries[i];
            int entryOffset = GiveItemUiC2SPacket.HeaderSize + (i * GiveItemUiC2SPacket.EntrySize);
            if (!_translator.TryTranslateItemByIdOrCell(
                    0,
                    entry.Place,
                    entry.GridX,
                    entry.GridY,
                    out VirtualJxItemSlot? shadowItem) ||
                shadowItem is null)
            {
                return new C2sRewriteResult(
                    copy,
                    WasModified: modified,
                    TranslationMissReason: $"c2s_giveitemui: could not translate Primary item (place={entry.Place}, x={entry.GridX}, y={entry.GridY}).");
            }

            if (shadowItem.ContainerPlace != entry.Place ||
                shadowItem.GridX != entry.GridX ||
                shadowItem.GridY != entry.GridY)
            {
                copy[entryOffset] = (byte)shadowItem.ContainerPlace;
                copy[entryOffset + 1] = shadowItem.GridX;
                copy[entryOffset + 2] = shadowItem.GridY;
                modified = true;
            }
        }

        return new C2sRewriteResult(copy, WasModified: modified, TranslationMissReason: null);
    }
}
