using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using JXReborn.Adapter.Legacy.Ciphers;
using JXReborn.Adapter.Legacy.Packets;
using JXReborn.Primitives.Characters;
using JXReborn.WireParityProxy.Configuration;
using JXReborn.WireParityProxy.Proxy;
using JXReborn.WireParityProxy.Wire;
using Xunit;

namespace JXReborn.WireParityProxy.Tests.Proxy;

public sealed class BishopAndGameServerShadowProxyTests
{
    [Fact]
    public async Task FullBishopToGameServerDualFlow_ShouldRewrite0x34MapGuidsAndIsolateShadowS2c()
    {
        LegacyKeyTable keyTable = LegacyKeyTable.CreateSynthetic();
        Guid primaryGuid = Guid.NewGuid();
        Guid shadowGuid = Guid.NewGuid();

        using var primaryGsListener = new TcpListener(IPAddress.Loopback, 0);
        using var shadowGsListener = new TcpListener(IPAddress.Loopback, 0);
        primaryGsListener.Start();
        shadowGsListener.Start();
        ushort primaryGsPort = (ushort)((IPEndPoint)primaryGsListener.LocalEndpoint).Port;
        ushort shadowGsPort = (ushort)((IPEndPoint)shadowGsListener.LocalEndpoint).Port;

        using var primaryBishopListener = new TcpListener(IPAddress.Loopback, 0);
        using var shadowBishopListener = new TcpListener(IPAddress.Loopback, 0);
        primaryBishopListener.Start();
        shadowBishopListener.Start();
        int primaryBishopPort = ((IPEndPoint)primaryBishopListener.LocalEndpoint).Port;
        int shadowBishopPort = ((IPEndPoint)shadowBishopListener.LocalEndpoint).Port;

        var options = new WireParityProxyOptions
        {
            BindHost = "127.0.0.1",
            BishopListenPort = 0,
            GameServerListenPort = 0,
            AdvertisedGameServerHost = "127.0.0.1",
            LegacyBishopHost = "127.0.0.1",
            LegacyBishopPort = primaryBishopPort,
            RebornBishopHost = "127.0.0.1",
            RebornBishopPort = shadowBishopPort,
            AllowSyntheticKeyTableFallback = true,
            ActionWindowMs = 60
        };

        var handoffMap = new HandoffTicketMap();
        await using var gsProxy = new GameServerShadowProxy(options, keyTable, handoffMap);
        await using var bishopProxy = new BishopShadowProxy(options, keyTable, handoffMap, () => gsProxy.BoundPort);

        gsProxy.Start();
        bishopProxy.Start();

        Task RunFakeBishopAsync(TcpListener listener, Guid guid, ushort gsPort) => Task.Run(async () =>
        {
            TcpClient tcp = await listener.AcceptTcpClientAsync();
            await using JxWireConnection conn = await JxWireConnection.AcceptClientAsync("fake-bishop", tcp, keyTable, CancellationToken.None);
            JxWireFrame? selectFrame = await conn.ReadDecryptedFrameAsync(CancellationToken.None);
            Assert.NotNull(selectFrame);
            Assert.Equal(ProtocolOpcodes.c2s_dbplayerselect, selectFrame.Opcode);

            NotifyPlayerLoginS2CPacket notify = NotifyPlayerLoginS2CPacket.Create(
                guid,
                "HeroProxy01",
                permitted: true,
                gameServerHost: "127.0.0.1",
                gameServerPort: gsPort);
            byte[] notifyBytes = new byte[NotifyPlayerLoginS2CPacket.Size];
            MemoryMarshal.Write(notifyBytes, in notify);
            await conn.WriteEncryptedFrameAsync(notifyBytes, CancellationToken.None);
        });

        Task primaryBishopTask = RunFakeBishopAsync(primaryBishopListener, primaryGuid, primaryGsPort);
        Task shadowBishopTask = RunFakeBishopAsync(shadowBishopListener, shadowGuid, shadowGsPort);

        // 1. Single client connects to BishopShadowProxy and sends c2s_dbplayerselect (0x64)
        NotifyPlayerLoginS2CPacket receivedNotify;
        await using (JxWireConnection clientBishop = await JxWireConnection.ConnectUpstreamAsync(
            "client-bishop",
            "127.0.0.1",
            bishopProxy.BoundPort,
            keyTable,
            CancellationToken.None))
        {
            byte[] selectPayload = new byte[41];
            selectPayload[0] = ProtocolOpcodes.c2s_dbplayerselect;
            await clientBishop.WriteEncryptedFrameAsync(selectPayload, CancellationToken.None);

            JxWireFrame? notifyFrame = await clientBishop.ReadDecryptedFrameAsync(CancellationToken.None);
            Assert.NotNull(notifyFrame);
            Assert.Equal(ProtocolOpcodes.s2c_notifyplayerlogin, notifyFrame.Opcode);
            receivedNotify = MemoryMarshal.Read<NotifyPlayerLoginS2CPacket>(notifyFrame.DecryptedPayload);
        }

        await Task.WhenAll(primaryBishopTask, shadowBishopTask);

        // Verify 0x34 was rewritten to GameServerShadowProxy.BoundPort while preserving PrimaryGuid
        Assert.Equal(primaryGuid, receivedNotify.GetTicketGuid());
        Assert.Equal((ushort)gsProxy.BoundPort, receivedNotify.Port);
        Assert.Equal(1, handoffMap.PendingCount);

        // 2. Fake Primary & Shadow GameServers verify each receives its own GUID and identical SyncEnd tokens
        var primaryReceivedGuidTcs = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shadowReceivedGuidTcs = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var primaryTokensTcs = new TaskCompletionSource<(uint, uint)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shadowTokensTcs = new TaskCompletionSource<(uint, uint)>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task RunFakeGsAsync(
            TcpListener listener,
            TaskCompletionSource<Guid> guidTcs,
            TaskCompletionSource<(uint, uint)> tokenTcs,
            int moneyToSend) => Task.Run(async () =>
        {
            TcpClient tcp = await listener.AcceptTcpClientAsync();
            await using JxWireConnection conn = await JxWireConnection.AcceptClientAsync("fake-gs", tcp, keyTable, CancellationToken.None);

            JxWireFrame? login = await conn.ReadDecryptedFrameAsync(CancellationToken.None);
            Assert.NotNull(login);
            Assert.Equal(ProtocolOpcodes.c2s_logiclogin, login.Opcode);
            guidTcs.TrySetResult(new Guid(login.DecryptedPayload.AsSpan(1, 16)));

            JxWireFrame? syncEnd = await conn.ReadDecryptedFrameAsync(CancellationToken.None);
            Assert.NotNull(syncEnd);
            Assert.Equal(ProtocolOpcodes.c2s_syncend, syncEnd.Opcode);
            uint t1 = BinaryPrimitives.ReadUInt32LittleEndian(syncEnd.DecryptedPayload.AsSpan(1, 4));
            uint t2 = BinaryPrimitives.ReadUInt32LittleEndian(syncEnd.DecryptedPayload.AsSpan(5, 4));
            tokenTcs.TrySetResult((t1, t2));

            byte[] moneyPkt = new byte[5];
            moneyPkt[0] = ProtocolOpcodes.s2c_syncmoney;
            BinaryPrimitives.WriteInt32LittleEndian(moneyPkt.AsSpan(1, 4), moneyToSend);
            await conn.WriteEncryptedFrameAsync(moneyPkt, CancellationToken.None);
        });

        Task primaryGsTask = RunFakeGsAsync(primaryGsListener, primaryReceivedGuidTcs, primaryTokensTcs, moneyToSend: 5000);
        Task shadowGsTask = RunFakeGsAsync(shadowGsListener, shadowReceivedGuidTcs, shadowTokensTcs, moneyToSend: 9999);

        await using (JxWireConnection clientGs = await JxWireConnection.ConnectUpstreamAsync(
            "client-gs",
            "127.0.0.1",
            receivedNotify.Port,
            keyTable,
            CancellationToken.None))
        {
            byte[] logicLogin = new byte[17];
            logicLogin[0] = ProtocolOpcodes.c2s_logiclogin;
            receivedNotify.GetTicketGuid().TryWriteBytes(logicLogin.AsSpan(1, 16));
            await clientGs.WriteEncryptedFrameAsync(logicLogin, CancellationToken.None);

            byte[] syncEnd = new byte[9];
            syncEnd[0] = ProtocolOpcodes.c2s_syncend;
            BinaryPrimitives.WriteUInt32LittleEndian(syncEnd.AsSpan(1, 4), 0x11223344u);
            BinaryPrimitives.WriteUInt32LittleEndian(syncEnd.AsSpan(5, 4), 0x55667788u);
            await clientGs.WriteEncryptedFrameAsync(syncEnd, CancellationToken.None);

            JxWireFrame? clientS2c = await clientGs.ReadDecryptedFrameAsync(CancellationToken.None);
            Assert.NotNull(clientS2c);
            Assert.Equal(ProtocolOpcodes.s2c_syncmoney, clientS2c.Opcode);
            // Client MUST receive Primary's value (5000), never Shadow's value (9999)
            Assert.Equal(5000, BinaryPrimitives.ReadInt32LittleEndian(clientS2c.DecryptedPayload.AsSpan(1, 4)));
        }

        await Task.WhenAll(primaryGsTask, shadowGsTask);

        Assert.Equal(primaryGuid, await primaryReceivedGuidTcs.Task);
        Assert.Equal(shadowGuid, await shadowReceivedGuidTcs.Task);
        Assert.Equal((0x11223344u, 0x55667788u), await primaryTokensTcs.Task);
        Assert.Equal((0x11223344u, 0x55667788u), await shadowTokensTcs.Task);
        Assert.NotNull(gsProxy.LatestSession);
        Assert.Equal(5000, gsProxy.LatestSession.PrimaryState.Money);
        Assert.Equal(9999, gsProxy.LatestSession.ShadowState.Money);
    }

    [Fact]
    public async Task DelayedShadowInitialSync_ShouldWaitForShadowSyncEndBeforeStep1AndProduceZeroFindings()
    {
        LegacyKeyTable keyTable = LegacyKeyTable.CreateSynthetic();
        Guid primaryGuid = Guid.NewGuid();
        Guid shadowGuid = Guid.NewGuid();

        using var primaryGsListener = new TcpListener(IPAddress.Loopback, 0);
        using var shadowGsListener = new TcpListener(IPAddress.Loopback, 0);
        primaryGsListener.Start();
        shadowGsListener.Start();

        var handoffMap = new HandoffTicketMap();
        handoffMap.Register(new HandoffTicketEntry(
            PrimaryGuid: primaryGuid,
            PrimaryGsHost: "127.0.0.1",
            PrimaryGsPort: (ushort)((IPEndPoint)primaryGsListener.LocalEndpoint).Port,
            ShadowGuid: shadowGuid,
            ShadowGsHost: "127.0.0.1",
            ShadowGsPort: (ushort)((IPEndPoint)shadowGsListener.LocalEndpoint).Port,
            RoleName: "DelayedHero",
            CreatedAtUtc: DateTimeOffset.UtcNow));

        var options = new WireParityProxyOptions
        {
            BindHost = "127.0.0.1",
            BishopListenPort = 0,
            GameServerListenPort = 0,
            AllowSyntheticKeyTableFallback = true,
            ActionWindowMs = 60
        };

        await using var gsProxy = new GameServerShadowProxy(options, keyTable, handoffMap);
        gsProxy.Start();

        long shadowSyncEndSentTick = 0;
        long shadowC2sSyncEndReceivedTick = 0;
        var shadowBurstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        static byte[] StructBytes<T>(T value) where T : struct
        {
            byte[] bytes = new byte[Marshal.SizeOf<T>()];
            MemoryMarshal.Write(bytes, in value);
            return bytes;
        }

        Task RunServerAsync(TcpListener listener, Task? releaseGate, bool recordTicks) => Task.Run(async () =>
        {
            TcpClient tcp = await listener.AcceptTcpClientAsync();
            await using JxWireConnection conn = await JxWireConnection.AcceptClientAsync("fake-gs", tcp, keyTable, CancellationToken.None);

            JxWireFrame? login = await conn.ReadDecryptedFrameAsync(CancellationToken.None);
            Assert.NotNull(login);
            Assert.Equal(ProtocolOpcodes.c2s_logiclogin, login.Opcode);

            if (releaseGate is not null)
            {
                await releaseGate;
            }

            byte[] syncWorld = StructBytes(SyncWorldS2CPacket.Create(53, 52032, 101696, 100, "Ba Lang Huyen"));
            byte[] syncSelfNpc = new SyncNpcFullS2CPacket(
                Camp: 0,
                Series: ElementSeries.Metal,
                LifePercent: 128,
                MapMpsY: 101696,
                DoingState: 1,
                StateInfo: 0,
                MapMpsX: 52032,
                MenuState: 0,
                GoldType: 0,
                Kind: EntityKind.Player,
                NpcId: 1001u,
                MasterId: -1,
                TemplateId: 0xFFFF,
                Level: 1,
                RankFfId: 0,
                NameUnicode: "DelayedHero").ToPayloadBytes();
            byte[] syncCurPlayer = StructBytes(new SyncCurPlayerS2CPacket
            {
                ProtocolType = ProtocolOpcodes.s2c_synccurplayer,
                Id = 1001u,
                Level = 1,
                LifeMax = 204,
                StaminaMax = 50,
                Kind = (byte)EntityKind.Player,
                ManaMax = 16
            });
            byte[] syncEnd = StructBytes(SyncEndS2CPacket.Create());
            await conn.WriteEncryptedFrameAsync(syncWorld, CancellationToken.None);
            await conn.WriteEncryptedFrameAsync(syncSelfNpc, CancellationToken.None);
            await conn.WriteEncryptedFrameAsync(syncCurPlayer, CancellationToken.None);
            await conn.WriteEncryptedFrameAsync(syncEnd, CancellationToken.None);
            if (recordTicks)
            {
                Volatile.Write(ref shadowSyncEndSentTick, Stopwatch.GetTimestamp());
            }

            JxWireFrame? clientSyncEnd = await conn.ReadDecryptedFrameAsync(CancellationToken.None);
            Assert.NotNull(clientSyncEnd);
            Assert.Equal(ProtocolOpcodes.c2s_syncend, clientSyncEnd.Opcode);
            if (recordTicks)
            {
                Volatile.Write(ref shadowC2sSyncEndReceivedTick, Stopwatch.GetTimestamp());
            }

            byte[] moneyPkt = new byte[SyncMoneyS2CPacket.PayloadSize];
            new SyncMoneyS2CPacket(1234, 0).WritePayload(moneyPkt);
            await conn.WriteEncryptedFrameAsync(moneyPkt, CancellationToken.None);

            // Keep server socket open until client closes so Primary EOF never cancels Shadow S2C read prematurely.
            while (await conn.ReadDecryptedFrameAsync(CancellationToken.None) is not null)
            {
            }
        });

        Task primaryTask = RunServerAsync(primaryGsListener, releaseGate: null, recordTicks: false);
        Task shadowTask = RunServerAsync(shadowGsListener, releaseGate: shadowBurstGate.Task, recordTicks: true);

        await using (JxWireConnection clientGs = await JxWireConnection.ConnectUpstreamAsync(
            "client-gs",
            "127.0.0.1",
            gsProxy.BoundPort,
            keyTable,
            CancellationToken.None))
        {
            byte[] logicLogin = new byte[17];
            logicLogin[0] = ProtocolOpcodes.c2s_logiclogin;
            primaryGuid.TryWriteBytes(logicLogin.AsSpan(1, 16));
            await clientGs.WriteEncryptedFrameAsync(logicLogin, CancellationToken.None);

            JxWireFrame? f1 = await clientGs.ReadDecryptedFrameAsync(CancellationToken.None);
            JxWireFrame? f2 = await clientGs.ReadDecryptedFrameAsync(CancellationToken.None);
            JxWireFrame? f3 = await clientGs.ReadDecryptedFrameAsync(CancellationToken.None);
            JxWireFrame? f4 = await clientGs.ReadDecryptedFrameAsync(CancellationToken.None);
            Assert.NotNull(f1);
            Assert.NotNull(f2);
            Assert.NotNull(f3);
            Assert.NotNull(f4);
            Assert.Equal(ProtocolOpcodes.s2c_syncworld, f1.Opcode);
            Assert.Equal(ProtocolOpcodes.s2c_syncnpc, f2.Opcode);
            Assert.Equal(ProtocolOpcodes.s2c_synccurplayer, f3.Opcode);
            Assert.Equal(ProtocolOpcodes.s2c_syncend, f4.Opcode);

            byte[] c2sSyncEnd = new byte[9];
            c2sSyncEnd[0] = ProtocolOpcodes.c2s_syncend;
            BinaryPrimitives.WriteUInt32LittleEndian(c2sSyncEnd.AsSpan(1, 4), 1u);
            BinaryPrimitives.WriteUInt32LittleEndian(c2sSyncEnd.AsSpan(5, 4), 2u);
            await clientGs.WriteEncryptedFrameAsync(c2sSyncEnd, CancellationToken.None);

            // Release Shadow after Primary finished s2c_syncend and client sent c2s_syncend,
            // proving the proxy holds Shadow's c2s_syncend until Shadow emits its own s2c_syncend.
            shadowBurstGate.TrySetResult();

            JxWireFrame? moneyFrame = await clientGs.ReadDecryptedFrameAsync(CancellationToken.None);
            Assert.NotNull(moneyFrame);
            Assert.Equal(ProtocolOpcodes.s2c_syncmoney, moneyFrame.Opcode);

            using var stepWaitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while ((gsProxy.LatestSession?.DiffCoordinator.GetSteps().Count ?? 0) < 1 && !stepWaitCts.IsCancellationRequested)
            {
                await Task.Yield();
            }
        }

        await Task.WhenAll(primaryTask, shadowTask);

        Assert.True(shadowC2sSyncEndReceivedTick >= shadowSyncEndSentTick,
            "Shadow must finish its s2c_syncend burst before receiving c2s_syncend and opening Step 1.");
        Assert.NotNull(gsProxy.LatestSession);
        Assert.True(gsProxy.LatestSession.PrimaryState.IsSyncCompleted);
        Assert.True(gsProxy.LatestSession.ShadowState.IsSyncCompleted);
        Assert.Equal(53, gsProxy.LatestSession.ShadowState.MapId);
        Assert.Empty(gsProxy.LatestSession.DiffCoordinator.GetFindings());
        Assert.False(gsProxy.LatestSession.DiffCoordinator.LatestDrift.IsDrifted);
        Assert.Single(gsProxy.LatestSession.DiffCoordinator.GetSteps());
        Assert.Equal(new[] { "0x61" }, gsProxy.LatestSession.DiffCoordinator.GetSteps()[0].ShadowS2cOpcodes);
    }
}
