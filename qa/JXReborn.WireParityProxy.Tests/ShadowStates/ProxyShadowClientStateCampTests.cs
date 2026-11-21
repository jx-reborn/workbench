using JXReborn.Adapter.Legacy.Packets;
using JXReborn.ClientDriver;
using JXReborn.Primitives.Characters;
using JXReborn.Primitives.WorldZones;
using JXReborn.WireParityProxy.ShadowStates;
using Xunit;

namespace JXReborn.WireParityProxy.Tests.ShadowStates;

public sealed class ProxyShadowClientStateCampTests
{
    [Fact]
    public void ApplyS2c_WithNpcChangeCampAndCurCampForSelf_ShouldUpdateCampAndCurrentCampInSnapshot()
    {
        var state = new ProxyShadowClientState("reborn-server");
        byte[] selfSyncPayload = new SyncNpcFullS2CPacket(
            Camp: (byte)EntityCamp.Begin,
            Series: ElementSeries.Water,
            LifePercent: 128,
            MapMpsY: 101696,
            DoingState: 1,
            StateInfo: 0,
            MapMpsX: 52032,
            MenuState: 0,
            GoldType: 0,
            Kind: EntityKind.Player,
            NpcId: 1001,
            MasterId: -1,
            TemplateId: 0xFFFE,
            Level: 120,
            RankFfId: 0,
            NameUnicode: "Fac05ThuyYen").ToPayloadBytes();

        state.ApplyS2c(selfSyncPayload, DateTimeOffset.UtcNow);

        Assert.Equal(1001u, state.SelfNpcId);
        Assert.Equal((int)EntityCamp.Begin, state.Camp);

        byte[] changeCampPayload = new byte[NpcChangeCampS2CPacket.PayloadSize];
        new NpcChangeCampS2CPacket(1001, EntityCamp.Balance).WritePayload(changeCampPayload);
        state.ApplyS2c(changeCampPayload, DateTimeOffset.UtcNow);

        byte[] changeCurCampPayload = new byte[NpcChangeCurCampS2CPacket.PayloadSize];
        new NpcChangeCurCampS2CPacket(1001, EntityCamp.Balance).WritePayload(changeCurCampPayload);
        state.ApplyS2c(changeCurCampPayload, DateTimeOffset.UtcNow);

        Assert.Equal((int)EntityCamp.Balance, state.Camp);
        Assert.Equal((int)EntityCamp.Balance, state.CurrentCamp);
        WorldDomSnapshot balanceSnapshot = state.ToWorldDomSnapshot();
        Assert.Equal((int)EntityCamp.Balance, balanceSnapshot.Player.Camp);
        Assert.Equal((int)EntityCamp.Balance, balanceSnapshot.Player.CurrentCamp);

        byte[] evilCurCampPayload = new byte[NpcChangeCurCampS2CPacket.PayloadSize];
        new NpcChangeCurCampS2CPacket(1001, EntityCamp.Evil).WritePayload(evilCurCampPayload);
        state.ApplyS2c(evilCurCampPayload, DateTimeOffset.UtcNow);

        Assert.Equal((int)EntityCamp.Balance, state.Camp);
        Assert.Equal((int)EntityCamp.Evil, state.CurrentCamp);
        WorldDomSnapshot evilSnapshot = state.ToWorldDomSnapshot();
        Assert.Equal((int)EntityCamp.Balance, evilSnapshot.Player.Camp);
        Assert.Equal((int)EntityCamp.Evil, evilSnapshot.Player.CurrentCamp);
    }

    [Fact]
    public void ApplyS2c_WithNpcChangeCampAndCurCampForTrackedNpc_ShouldUpdateNpcCamp()
    {
        var state = new ProxyShadowClientState("reborn-server");
        byte[] selfSyncPayload = new SyncNpcFullS2CPacket(
            Camp: (byte)EntityCamp.Begin,
            Series: ElementSeries.Water,
            LifePercent: 128,
            MapMpsY: 101696,
            DoingState: 1,
            StateInfo: 0,
            MapMpsX: 52032,
            MenuState: 0,
            GoldType: 0,
            Kind: EntityKind.Player,
            NpcId: 1001,
            MasterId: -1,
            TemplateId: 0xFFFE,
            Level: 120,
            RankFfId: 0,
            NameUnicode: "SelfPlayer").ToPayloadBytes();
        byte[] otherNpcPayload = new SyncNpcFullS2CPacket(
            Camp: (byte)EntityCamp.Justice,
            Series: ElementSeries.Metal,
            LifePercent: 128,
            MapMpsY: 101760,
            DoingState: 1,
            StateInfo: 0,
            MapMpsX: 52096,
            MenuState: 0,
            GoldType: 0,
            Kind: EntityKind.Player,
            NpcId: 2002,
            MasterId: -1,
            TemplateId: 0xFFFF,
            Level: 80,
            RankFfId: 0,
            NameUnicode: "OtherPlayer").ToPayloadBytes();

        state.ApplyS2c(selfSyncPayload, DateTimeOffset.UtcNow);
        state.ApplyS2c(otherNpcPayload, DateTimeOffset.UtcNow);

        byte[] changeCampPayload = new byte[NpcChangeCampS2CPacket.PayloadSize];
        new NpcChangeCampS2CPacket(2002, EntityCamp.Balance).WritePayload(changeCampPayload);
        state.ApplyS2c(changeCampPayload, DateTimeOffset.UtcNow);
        Assert.Equal((byte)EntityCamp.Balance, state.SnapshotNpcs()[2002].Camp);

        byte[] changeCurCampPayload = new byte[NpcChangeCurCampS2CPacket.PayloadSize];
        new NpcChangeCurCampS2CPacket(2002, EntityCamp.Killer).WritePayload(changeCurCampPayload);
        state.ApplyS2c(changeCurCampPayload, DateTimeOffset.UtcNow);
        Assert.Equal((byte)EntityCamp.Killer, state.SnapshotNpcs()[2002].Camp);
    }
}
