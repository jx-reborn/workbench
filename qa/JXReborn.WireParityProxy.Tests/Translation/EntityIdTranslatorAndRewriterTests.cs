using System.Buffers.Binary;
using JXReborn.Adapter.Legacy.Packets;
using JXReborn.Primitives.Characters;
using JXReborn.Primitives.WorldZones;
using JXReborn.WireParityProxy.ShadowStates;
using JXReborn.WireParityProxy.Translation;
using Xunit;

namespace JXReborn.WireParityProxy.Tests.Translation;

public sealed class EntityIdTranslatorAndRewriterTests
{
    [Fact]
    public void RewriteDialogNpc_ShouldTranslateNpcIdAndRestampValidAntiCheatCheckValue()
    {
        var primary = new ProxyShadowClientState("legacy-linux");
        var shadow = new ProxyShadowClientState("reborn-server");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        primary.ApplyS2c(BuildSyncNpcPayload(npcId: 1042, templateId: 413, mpsX: 51200, mpsY: 102400, name: "CocGo"), now);
        shadow.ApplyS2c(BuildSyncNpcPayload(npcId: 2088, templateId: 413, mpsX: 51210, mpsY: 102405, name: "CocGo"), now);

        var translator = new EntityIdTranslator(primary, shadow);
        var rewriter = new C2sPacketRewriter(translator);

        const uint token1 = 0x13572468u;
        const uint token2 = 0x24681357u;
        byte[] dialogC2s = new byte[9];
        dialogC2s[0] = ProtocolOpcodes.c2s_dialognpc;
        BinaryPrimitives.WriteUInt32LittleEndian(dialogC2s.AsSpan(1, 4), 1042u);
        BinaryPrimitives.WriteUInt32LittleEndian(dialogC2s.AsSpan(5, 4), 0xDEADBEEFu);

        C2sRewriteResult result = rewriter.RewriteForShadow(dialogC2s, token1, token2);

        Assert.True(result.WasModified);
        Assert.Null(result.TranslationMissReason);
        Assert.Equal(2088u, BinaryPrimitives.ReadUInt32LittleEndian(result.ShadowPayload.AsSpan(1, 4)));

        uint stampedCheck = BinaryPrimitives.ReadUInt32LittleEndian(result.ShadowPayload.AsSpan(5, 4));
        Assert.True(MovementAntiCheatConsts.IsValidCheckValue(
            stampedCheck,
            token1,
            token2,
            MovementAntiCheatConsts.DialogNpcCheckKey));
    }

    [Fact]
    public void RewriteDialogNpc_WhenShadowMissingNpc_ShouldReportTranslationMiss()
    {
        var primary = new ProxyShadowClientState("legacy-linux");
        var shadow = new ProxyShadowClientState("reborn-server");
        primary.ApplyS2c(BuildSyncNpcPayload(npcId: 1042, templateId: 413, mpsX: 51200, mpsY: 102400, name: "CocGo"), DateTimeOffset.UtcNow);

        var rewriter = new C2sPacketRewriter(new EntityIdTranslator(primary, shadow));
        byte[] dialogC2s = new byte[9];
        dialogC2s[0] = ProtocolOpcodes.c2s_dialognpc;
        BinaryPrimitives.WriteUInt32LittleEndian(dialogC2s.AsSpan(1, 4), 1042u);

        C2sRewriteResult result = rewriter.RewriteForShadow(dialogC2s, 1u, 2u);

        Assert.False(result.WasModified);
        Assert.NotNull(result.TranslationMissReason);
        Assert.Contains("1042", result.TranslationMissReason, StringComparison.Ordinal);
    }

    [Fact]
    public void RewriteRequestNpc_WhenOnlyCompact0x4DObserved_ShouldTranslateByCampAndProximity()
    {
        var primary = new ProxyShadowClientState("legacy-linux");
        var shadow = new ProxyShadowClientState("reborn-server");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        primary.ApplyS2c(BuildSyncNpcMinPayload(npcId: 17354, mpsX: 52335, mpsY: 102036, camp: 6), now);
        shadow.ApplyS2c(BuildSyncNpcMinPayload(npcId: 40012, mpsX: 52335, mpsY: 102036, camp: 6), now);

        var rewriter = new C2sPacketRewriter(new EntityIdTranslator(primary, shadow));
        byte[] reqNpcC2s = new byte[RequestNpcC2SPacket.PayloadSize];
        new RequestNpcC2SPacket(17354u).WritePayload(reqNpcC2s);

        C2sRewriteResult result = rewriter.RewriteForShadow(reqNpcC2s, 1u, 2u);

        Assert.True(result.WasModified);
        Assert.Null(result.TranslationMissReason);
        Assert.Equal(40012u, BinaryPrimitives.ReadUInt32LittleEndian(result.ShadowPayload.AsSpan(1, 4)));
    }

    [Fact]
    public void RewriteRequestNpc_WhenOnlyCompact0x4DObservedAndCampDiffersAtExactPos_ShouldTranslateByProximityFallback()
    {
        var primary = new ProxyShadowClientState("legacy-linux");
        var shadow = new ProxyShadowClientState("reborn-server");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        primary.ApplyS2c(BuildSyncNpcMinPayload(npcId: 92256, mpsX: 52224, mpsY: 101536, camp: 4), now);
        shadow.ApplyS2c(BuildSyncNpcMinPayload(npcId: 10512, mpsX: 52224, mpsY: 101536, camp: 6), now);

        var rewriter = new C2sPacketRewriter(new EntityIdTranslator(primary, shadow));
        byte[] reqNpcC2s = new byte[RequestNpcC2SPacket.PayloadSize];
        new RequestNpcC2SPacket(92256u).WritePayload(reqNpcC2s);

        C2sRewriteResult result = rewriter.RewriteForShadow(reqNpcC2s, 1u, 2u);

        Assert.True(result.WasModified);
        Assert.Null(result.TranslationMissReason);
        Assert.Equal(10512u, BinaryPrimitives.ReadUInt32LittleEndian(result.ShadowPayload.AsSpan(1, 4)));
    }

    [Fact]
    public void RewriteRequestNpc_WhenPrimaryNameHasTrailingSpaces_ShouldTrimAndTranslate()
    {
        var primary = new ProxyShadowClientState("legacy-linux");
        var shadow = new ProxyShadowClientState("reborn-server");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        primary.ApplyS2c(BuildSyncNpcPayload(npcId: 92256, templateId: 9999, mpsX: 52224, mpsY: 101536, name: "Tiêu Dao Tử  "), now);
        shadow.ApplyS2c(BuildSyncNpcPayload(npcId: 10512, templateId: 2537, mpsX: 52224, mpsY: 101536, name: "Tiêu Dao Tử"), now);

        var rewriter = new C2sPacketRewriter(new EntityIdTranslator(primary, shadow));
        byte[] reqNpcC2s = new byte[RequestNpcC2SPacket.PayloadSize];
        new RequestNpcC2SPacket(92256u).WritePayload(reqNpcC2s);

        C2sRewriteResult result = rewriter.RewriteForShadow(reqNpcC2s, 1u, 2u);

        Assert.True(result.WasModified);
        Assert.Null(result.TranslationMissReason);
        Assert.Equal(10512u, BinaryPrimitives.ReadUInt32LittleEndian(result.ShadowPayload.AsSpan(1, 4)));
    }

    [Fact]
    public void RewriteRequestNpc_WhenPrimaryHydrated0x4CAndShadowOnlyCompact0x4D_ShouldTranslateByProximity()
    {
        var primary = new ProxyShadowClientState("legacy-linux");
        var shadow = new ProxyShadowClientState("reborn-server");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        primary.ApplyS2c(BuildSyncNpcPayload(npcId: 92256, templateId: 2537, mpsX: 52224, mpsY: 101536, name: "Tiêu Dao Tử  "), now);
        shadow.ApplyS2c(BuildSyncNpcMinPayload(npcId: 10512, mpsX: 52224, mpsY: 101536, camp: 6), now);

        var rewriter = new C2sPacketRewriter(new EntityIdTranslator(primary, shadow));
        byte[] reqNpcC2s = new byte[RequestNpcC2SPacket.PayloadSize];
        new RequestNpcC2SPacket(92256u).WritePayload(reqNpcC2s);

        C2sRewriteResult result = rewriter.RewriteForShadow(reqNpcC2s, 1u, 2u);

        Assert.True(result.WasModified);
        Assert.Null(result.TranslationMissReason);
        Assert.Equal(10512u, BinaryPrimitives.ReadUInt32LittleEndian(result.ShadowPayload.AsSpan(1, 4)));
    }

    [Fact]
    public void RewriteRequestObj_WhenOnly0x65StateSyncObserved_ShouldTranslateByOrderAndState()
    {
        var primary = new ProxyShadowClientState("legacy-linux");
        var shadow = new ProxyShadowClientState("reborn-server");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        primary.ApplyS2c(BuildObjSyncStatePayload(state: 0, objId: 1132), now);
        primary.ApplyS2c(BuildObjSyncStatePayload(state: 0, objId: 1133), now);
        shadow.ApplyS2c(BuildObjSyncStatePayload(state: 0, objId: 1000), now);
        shadow.ApplyS2c(BuildObjSyncStatePayload(state: 0, objId: 1001), now);

        var rewriter = new C2sPacketRewriter(new EntityIdTranslator(primary, shadow));
        byte[] reqObjC2s = new byte[RequestObjC2SPacket.PayloadSize];
        new RequestObjC2SPacket(1133).WritePayload(reqObjC2s);

        C2sRewriteResult result = rewriter.RewriteForShadow(reqObjC2s, 1u, 2u);

        Assert.True(result.WasModified);
        Assert.Null(result.TranslationMissReason);
        Assert.Equal(1001, BinaryPrimitives.ReadInt32LittleEndian(result.ShadowPayload.AsSpan(1, 4)));
    }

    [Fact]
    public void RewriteForShadow_OnObjMouseClick_ShouldTranslateObjIdAtOffset5AndPreserveRegionId()
    {
        var primary = new ProxyShadowClientState("legacy-linux");
        var shadow = new ProxyShadowClientState("reborn-server");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        primary.ApplyS2c(BuildObjSyncStatePayload(state: 0, objId: 1132), now);
        shadow.ApplyS2c(BuildObjSyncStatePayload(state: 0, objId: 4007), now);

        var rewriter = new C2sPacketRewriter(new EntityIdTranslator(primary, shadow));
        byte[] mouseClickC2s = new byte[ObjMouseClickC2SPacket.PayloadSize];
        new ObjMouseClickC2SPacket(RegionId: 0x00620064u, ObjId: 1132).WritePayload(mouseClickC2s);

        C2sRewriteResult result = rewriter.RewriteForShadow(mouseClickC2s, 1u, 2u);

        Assert.True(result.WasModified);
        Assert.Null(result.TranslationMissReason);
        Assert.Equal(0x00620064u, BinaryPrimitives.ReadUInt32LittleEndian(result.ShadowPayload.AsSpan(1, 4)));
        Assert.Equal(4007, BinaryPrimitives.ReadInt32LittleEndian(result.ShadowPayload.AsSpan(5, 4)));
    }

    [Theory]
    [InlineData(ProtocolOpcodes.c2s_npcstaterequest)]
    [InlineData(ProtocolOpcodes.c2s_requestnpcfeature)]
    [InlineData(ProtocolOpcodes.c2s_viewequip)]
    public void RewriteForShadow_OnNpcStateRequestFeatureAndViewEquip_ShouldTranslateTargetNpcIdAtOffset1(byte opcode)
    {
        // Ground truth: Map 53 Step038_C2S_0xA6 (NpcStateRequestC2SPacket 5B), 0x91 (5B), 0x78 (ViewEquipC2SPacket 5B)
        var primary = new ProxyShadowClientState("legacy-linux");
        var shadow = new ProxyShadowClientState("reborn-server");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        primary.ApplyS2c(BuildSyncNpcPayload(npcId: 17353, templateId: 219, mpsX: 51208, mpsY: 101458, name: "TapHoa"), now);
        shadow.ApplyS2c(BuildSyncNpcPayload(npcId: 42, templateId: 219, mpsX: 51208, mpsY: 101458, name: "TapHoa"), now);

        var rewriter = new C2sPacketRewriter(new EntityIdTranslator(primary, shadow));
        byte[] payload = new byte[5];
        payload[0] = opcode;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1, 4), 17353u);

        C2sRewriteResult result = rewriter.RewriteForShadow(payload, 1u, 2u);

        Assert.True(result.WasModified);
        Assert.Null(result.TranslationMissReason);
        Assert.Equal(42u, BinaryPrimitives.ReadUInt32LittleEndian(result.ShadowPayload.AsSpan(1, 4)));
    }

    [Fact]
    public void RewriteForShadow_OnStaticObjMouseClickWhenPrimaryHasOnly0x65AndShadowHas0x64Coords_ShouldSelectShadowObjectNearPlayerInsteadOfWrongFirstSeenOrder()
    {
        // Ground truth: Map 53 Step 43 Noticeboard 1 at (51824, 100780) vs StorageBox 1 at (52112, 101648).
        // Primary only emits 0x65 (HasCoords=false), while Shadow emits 0x64+0x65 (HasCoords=true) in different AOI region order.
        var primary = new ProxyShadowClientState("legacy-linux");
        var shadow = new ProxyShadowClientState("reborn-server");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Hydrate player self position standing next to Noticeboard 1 at (51830, 100790)
        primary.ApplyS2c(BuildSyncSelfPlayerNpcPayload(npcId: 100u, mpsX: 51830, mpsY: 100790), now);
        shadow.ApplyS2c(BuildSyncSelfPlayerNpcPayload(npcId: 200u, mpsX: 51840, mpsY: 100795), now);

        // Primary saw StorageBox (1132) first, then Noticeboard 1 (1133) via 0x65 (no coords)
        primary.ApplyS2c(BuildObjSyncStatePayload(state: 0, objId: 1132), now);
        primary.ApplyS2c(BuildObjSyncStatePayload(state: 0, objId: 1133), now);

        // Shadow saw Noticeboard 1 (objId=1 at 51824, 100780) first, then StorageBox (objId=2 at 52112, 101648) via 0x64
        shadow.ApplyS2c(ObjAddS2CPacket.CreatePayloadBytes(dir: 0, objId: 1, dataId: 4, mpsX: 51824, mpsY: 100780, moneyNum: 0, itemId: 0, state: 0, itemWidth: 0, genre: 0, itemHeight: 0, colorId: 0, flag: 0, nameUtf8: string.Empty), now);
        shadow.ApplyS2c(BuildObjSyncStatePayload(state: 0, objId: 1), now);
        shadow.ApplyS2c(ObjAddS2CPacket.CreatePayloadBytes(dir: 0, objId: 2, dataId: 1, mpsX: 52112, mpsY: 101648, moneyNum: 0, itemId: 0, state: 0, itemWidth: 0, genre: 0, itemHeight: 0, colorId: 0, flag: 0, nameUtf8: string.Empty), now);
        shadow.ApplyS2c(BuildObjSyncStatePayload(state: 0, objId: 2), now);

        var rewriter = new C2sPacketRewriter(new EntityIdTranslator(primary, shadow));
        byte[] mouseClickC2s = new byte[ObjMouseClickC2SPacket.PayloadSize];
        new ObjMouseClickC2SPacket(RegionId: 0x00620065u, ObjId: 1133).WritePayload(mouseClickC2s);

        C2sRewriteResult result = rewriter.RewriteForShadow(mouseClickC2s, 1u, 2u);

        Assert.True(result.WasModified);
        Assert.Null(result.TranslationMissReason);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(result.ShadowPayload.AsSpan(5, 4)));
    }

    [Fact]
    public void RewriteForShadow_OnWanderingMonsterBeyond256Mps_ShouldTranslateWithinMonsterPatrolRadius()
    {
        // Ground truth: Map 53 Step 1193 wandering monster (NpcId=17040, Camp=5) patrolling > 256 mps apart on Primary vs Shadow.
        var primary = new ProxyShadowClientState("legacy-linux");
        var shadow = new ProxyShadowClientState("reborn-server");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        primary.ApplyS2c(BuildSyncMonsterPayload(npcId: 17040u, templateId: 408, mpsX: 48600, mpsY: 94700, name: "HuouDom"), now);
        shadow.ApplyS2c(BuildSyncMonsterPayload(npcId: 5020u, templateId: 408, mpsX: 49020, mpsY: 94880, name: "HuouDom"), now);

        var rewriter = new C2sPacketRewriter(new EntityIdTranslator(primary, shadow));
        byte[] stateReq = new byte[5];
        stateReq[0] = ProtocolOpcodes.c2s_npcstaterequest;
        BinaryPrimitives.WriteUInt32LittleEndian(stateReq.AsSpan(1, 4), 17040u);

        C2sRewriteResult result = rewriter.RewriteForShadow(stateReq, 1u, 2u);

        Assert.True(result.WasModified);
        Assert.Null(result.TranslationMissReason);
        Assert.Equal(5020u, BinaryPrimitives.ReadUInt32LittleEndian(result.ShadowPayload.AsSpan(1, 4)));
    }

    [Fact]
    public void RewriteForShadow_OnStaticObjMouseClickWhenPrimarySentZero0x65Packets_ShouldStillSelectShadowObjectNearPlayer()
    {
        // Ground truth: Map 53 mui2-verify Step031_C2S_0x72 (StorageBox 1 at 52112, 101648) & Step042_C2S_0x72 (Noticeboard 1 at 51824, 100780).
        // game.exe loads default State==0 static objects from client Region_C.dat at map load, so Primary (legacy-linux) emits 0x00 0x65 packets
        // before the player clicks StorageBox 1 / Noticeboard 1, while Shadow (reborn-server) already emitted 0x64 (HasCoords=true) at Step 5.
        var primary = new ProxyShadowClientState("legacy-linux");
        var shadow = new ProxyShadowClientState("reborn-server");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        primary.ApplyS2c(BuildSyncSelfPlayerNpcPayload(npcId: 100u, mpsX: 52100, mpsY: 101640), now);
        shadow.ApplyS2c(BuildSyncSelfPlayerNpcPayload(npcId: 200u, mpsX: 52100, mpsY: 101640), now);

        shadow.ApplyS2c(ObjAddS2CPacket.CreatePayloadBytes(dir: 0, objId: 1, dataId: 4, mpsX: 51824, mpsY: 100780, moneyNum: 0, itemId: 0, state: 0, itemWidth: 0, genre: 0, itemHeight: 0, colorId: 0, flag: 0, nameUtf8: string.Empty), now);
        shadow.ApplyS2c(ObjAddS2CPacket.CreatePayloadBytes(dir: 0, objId: 2, dataId: 1, mpsX: 52112, mpsY: 101648, moneyNum: 0, itemId: 0, state: 0, itemWidth: 0, genre: 0, itemHeight: 0, colorId: 0, flag: 0, nameUtf8: string.Empty), now);

        var rewriter = new C2sPacketRewriter(new EntityIdTranslator(primary, shadow));
        byte[] mouseClickC2s = new byte[ObjMouseClickC2SPacket.PayloadSize];
        new ObjMouseClickC2SPacket(RegionId: 0x00630065u, ObjId: 1132).WritePayload(mouseClickC2s);

        C2sRewriteResult result = rewriter.RewriteForShadow(mouseClickC2s, 1u, 2u);

        Assert.True(result.WasModified);
        Assert.Null(result.TranslationMissReason);
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(result.ShadowPayload.AsSpan(5, 4)));
    }

    private static byte[] BuildSyncSelfPlayerNpcPayload(uint npcId, int mpsX, int mpsY)
    {
        var pkt = new SyncNpcFullS2CPacket(
            Camp: 0,
            Series: ElementSeries.Metal,
            LifePercent: 128,
            MapMpsY: mpsY,
            DoingState: 1,
            StateInfo: 0,
            MapMpsX: mpsX,
            MenuState: 0,
            GoldType: 0,
            Kind: EntityKind.Player,
            NpcId: npcId,
            MasterId: -1,
            TemplateId: 0,
            Level: 20,
            RankFfId: 0,
            NameUnicode: "Tester");
        return pkt.ToPayloadBytes();
    }

    private static byte[] BuildSyncMonsterPayload(uint npcId, ushort templateId, int mpsX, int mpsY, string name)
    {
        var pkt = new SyncNpcFullS2CPacket(
            Camp: 5,
            Series: ElementSeries.Wood,
            LifePercent: 128,
            MapMpsY: mpsY,
            DoingState: 1,
            StateInfo: 0,
            MapMpsX: mpsX,
            MenuState: 0,
            GoldType: 0,
            Kind: EntityKind.Monster,
            NpcId: npcId,
            MasterId: -1,
            TemplateId: templateId,
            Level: 2,
            RankFfId: 0,
            NameUnicode: name);
        return pkt.ToPayloadBytes();
    }

    private static byte[] BuildSyncNpcPayload(uint npcId, ushort templateId, int mpsX, int mpsY, string name)
    {
        var pkt = new SyncNpcFullS2CPacket(
            Camp: 0,
            Series: ElementSeries.Metal,
            LifePercent: 128,
            MapMpsY: mpsY,
            DoingState: 1,
            StateInfo: 0,
            MapMpsX: mpsX,
            MenuState: 0,
            GoldType: 0,
            Kind: EntityKind.DialogNpc,
            NpcId: npcId,
            MasterId: -1,
            TemplateId: templateId,
            Level: 10,
            RankFfId: 0,
            NameUnicode: name);
        return pkt.ToPayloadBytes();
    }

    private static byte[] BuildSyncNpcMinPayload(uint npcId, int mpsX, int mpsY, byte camp)
    {
        var pkt = new SyncNpcMinS2CPacket(
            NpcId: npcId,
            MpsX: mpsX,
            MpsY: mpsY,
            Doing: 1,
            LifePer: 128,
            State: 0,
            Camp: camp);
        byte[] payload = new byte[SyncNpcMinS2CPacket.PayloadSize];
        pkt.WritePayload(payload);
        return payload;
    }

    private static byte[] BuildObjSyncStatePayload(byte state, int objId)
    {
        var pkt = new ObjSyncStateS2CPacket(State: state, ObjId: objId);
        byte[] payload = new byte[ObjSyncStateS2CPacket.PayloadSize];
        pkt.WritePayload(payload);
        return payload;
    }
}
