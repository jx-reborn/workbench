using System.Buffers.Binary;
using JXReborn.Adapter.Legacy.Packets;
using JXReborn.WireParityProxy.ShadowStates;
using Xunit;

namespace JXReborn.WireParityProxy.Tests.ShadowStates;

public sealed class ProxyShadowClientStateMovementTests
{
    [Fact]
    public void ApplyS2c_With13ByteSyncNpcMinPlayer_ShouldUpdatePlayerCoordinates()
    {
        var state = new ProxyShadowClientState("reborn-server");
        byte[] payload = new byte[SyncNpcMinPlayerS2CPacket.Size];
        payload[0] = ProtocolOpcodes.s2c_syncnpcminplayer;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1, 2), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(3, 2), 32);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(5, 4), 101562);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(9, 4), 51902);

        state.ApplyS2c(payload, DateTimeOffset.UtcNow);

        Assert.Equal(51902, state.MpsX);
        Assert.Equal(101562, state.MpsY);
    }
}
