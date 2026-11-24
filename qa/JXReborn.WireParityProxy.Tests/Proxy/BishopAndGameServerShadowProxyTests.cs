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

    [Fact]
    public async Task BishopShadowProxy_WhenPrimaryReturnsPermitZeroWithRoleNameDuringColdBoot_ShouldRetrySelectUntilPrimaryPermits()
    {
        // Ground truth: mui2-final cold-boot where bishop_y responds s2c_notifyplayerlogin (0x34) with Permit=0 AND RoleName='gm_bl_01_r1'
        // while jx_linux_y is still registering Map 53; proxy must retry c2s_dbplayerselect instead of forwarding Permit=0 (loginResult=14) to game.exe.
        LegacyKeyTable keyTable = LegacyKeyTable.CreateSynthetic();
        Guid primaryGuid = Guid.NewGuid();
        Guid shadowGuid = Guid.NewGuid();

        using var primaryBishopListener = new TcpListener(IPAddress.Loopback, 0);
        using var shadowBishopListener = new TcpListener(IPAddress.Loopback, 0);
        primaryBishopListener.Start();
        shadowBishopListener.Start();

        var options = new WireParityProxyOptions
        {
            BindHost = "127.0.0.1",
            BishopListenPort = 0,
            GameServerListenPort = 0,
            AdvertisedGameServerHost = "127.0.0.1",
            LegacyBishopHost = "127.0.0.1",
            LegacyBishopPort = ((IPEndPoint)primaryBishopListener.LocalEndpoint).Port,
            RebornBishopHost = "127.0.0.1",
            RebornBishopPort = ((IPEndPoint)shadowBishopListener.LocalEndpoint).Port,
            AllowSyntheticKeyTableFallback = true
        };

        var findings = new List<JXReborn.DualParityServer.Diffing.ParityFinding>();
        var handoffMap = new HandoffTicketMap();
        await using var bishopProxy = new BishopShadowProxy(options, keyTable, handoffMap, static () => 16668, findings.Add);
        bishopProxy.Start();

        Task primaryTask = Task.Run(async () =>
        {
            TcpClient tcp = await primaryBishopListener.AcceptTcpClientAsync();
            await using JxWireConnection conn = await JxWireConnection.AcceptClientAsync("primary-bishop", tcp, keyTable, CancellationToken.None);

            JxWireFrame? attempt1 = await conn.ReadDecryptedFrameAsync(CancellationToken.None);
            Assert.NotNull(attempt1);
            Assert.Equal(ProtocolOpcodes.c2s_dbplayerselect, attempt1.Opcode);

            NotifyPlayerLoginS2CPacket deny = NotifyPlayerLoginS2CPacket.Create(Guid.Empty, "gm_bl_01_r1", permitted: false, "127.0.0.1", 16666);
            byte[] denyBytes = new byte[NotifyPlayerLoginS2CPacket.Size];
            MemoryMarshal.Write(denyBytes, in deny);
            await conn.WriteEncryptedFrameAsync(denyBytes, CancellationToken.None);

            JxWireFrame? attempt2 = await conn.ReadDecryptedFrameAsync(CancellationToken.None);
            Assert.NotNull(attempt2);
            Assert.Equal(ProtocolOpcodes.c2s_dbplayerselect, attempt2.Opcode);

            NotifyPlayerLoginS2CPacket allow = NotifyPlayerLoginS2CPacket.Create(primaryGuid, "gm_bl_01_r1", permitted: true, "127.0.0.1", 16666);
            byte[] allowBytes = new byte[NotifyPlayerLoginS2CPacket.Size];
            MemoryMarshal.Write(allowBytes, in allow);
            await conn.WriteEncryptedFrameAsync(allowBytes, CancellationToken.None);
        });

        Task shadowTask = Task.Run(async () =>
        {
            TcpClient tcp = await shadowBishopListener.AcceptTcpClientAsync();
            await using JxWireConnection conn = await JxWireConnection.AcceptClientAsync("shadow-bishop", tcp, keyTable, CancellationToken.None);
            JxWireFrame? selectFrame = await conn.ReadDecryptedFrameAsync(CancellationToken.None);
            Assert.NotNull(selectFrame);

            NotifyPlayerLoginS2CPacket allow = NotifyPlayerLoginS2CPacket.Create(shadowGuid, "gm_bl_01_r1", permitted: true, "127.0.0.1", 16667);
            byte[] allowBytes = new byte[NotifyPlayerLoginS2CPacket.Size];
            MemoryMarshal.Write(allowBytes, in allow);
            await conn.WriteEncryptedFrameAsync(allowBytes, CancellationToken.None);
        });

        NotifyPlayerLoginS2CPacket receivedNotify;
        await using (JxWireConnection clientConn = await JxWireConnection.ConnectUpstreamAsync(
            "client-bishop",
            "127.0.0.1",
            bishopProxy.BoundPort,
            keyTable,
            CancellationToken.None))
        {
            byte[] selectPayload = new byte[41];
            selectPayload[0] = ProtocolOpcodes.c2s_dbplayerselect;
            await clientConn.WriteEncryptedFrameAsync(selectPayload, CancellationToken.None);

            JxWireFrame? notifyFrame = await clientConn.ReadDecryptedFrameAsync(CancellationToken.None);
            Assert.NotNull(notifyFrame);
            receivedNotify = MemoryMarshal.Read<NotifyPlayerLoginS2CPacket>(notifyFrame.DecryptedPayload);
        }

        await Task.WhenAll(primaryTask, shadowTask).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEqual((byte)0, receivedNotify.Permit);
        Assert.Equal(primaryGuid, receivedNotify.GetTicketGuid());
        Assert.Empty(findings);
    }

    [Fact]
    public async Task GameServerShadowProxy_WhenPassiveProbeArrivesMidDialogWindowWithUnhydratedPrimaryNpc_ShouldCoalesceWindowAndSuppressCacheMiss()
    {
        // Ground truth: Map 53 mui2-final Step 247/248 & Step 66:
        // 1) game.exe sends c2s_dialognpc (0x6E), then 5ms later sends passive c2s_requestnpc (0x49) for unhydrated NpcId=112896;
        // 2) Primary replies 0x63 at +10ms while Shadow replies 0x63 at +55ms (next 18-Hz tick).
        // Proxy must NOT chop the 0x6E window on passive 0x49 and must NOT emit C2S.EntityTranslation for unhydrated Primary cache-miss probe.
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
            RoleName: "WindowCoalesceHero",
            CreatedAtUtc: DateTimeOffset.UtcNow));

        var options = new WireParityProxyOptions
        {
            BindHost = "127.0.0.1",
            BishopListenPort = 0,
            GameServerListenPort = 0,
            AllowSyntheticKeyTableFallback = true,
            ActionWindowMs = 140
        };

        await using var gsProxy = new GameServerShadowProxy(options, keyTable, handoffMap);
        gsProxy.Start();

        byte[] dialogReply = ScriptActionS2CPacket.CreateTalkPayload(
            ["Bên ngoài loạn lạc, xem ngươi trói gà không chặt! Ra ngoài thôn ta e không giữ được mạng đâu!"],
            expectCompletionCallback: false);

        static async Task SendValidWorldBootstrapAsync(JxWireConnection conn, uint selfNpcId, uint boatmanId)
        {
            byte[] syncWorld = new byte[34];
            syncWorld[0] = ProtocolOpcodes.s2c_syncworld;
            BinaryPrimitives.WriteInt32LittleEndian(syncWorld.AsSpan(1, 4), 53);
            byte[] selfNpc = new SyncNpcFullS2CPacket(0, ElementSeries.Metal, 128, 93543, 1, 0, 49389, 0, 0, EntityKind.Player, selfNpcId, -1, 0xFFFF, 1, 0, "WindowCoalesceHero").ToPayloadBytes();
            byte[] curPlayer = new byte[Marshal.SizeOf<SyncCurPlayerS2CPacket>()];
            var curPkt = new SyncCurPlayerS2CPacket { ProtocolType = ProtocolOpcodes.s2c_synccurplayer, Id = selfNpcId, Level = 1, LifeMax = 204, StaminaMax = 50, Kind = (byte)EntityKind.Player, ManaMax = 16 };
            MemoryMarshal.Write(curPlayer, in curPkt);
            byte[] npcFull = new SyncNpcFullS2CPacket(6, ElementSeries.Metal, 128, 93543, 1, 0, 49389, 0, 0, EntityKind.DialogNpc, boatmanId, -1, 396, 1, 0, "Thuyền Phu").ToPayloadBytes();
            byte[] syncEnd = [ProtocolOpcodes.s2c_syncend];
            await conn.WriteEncryptedFrameAsync(syncWorld, CancellationToken.None);
            await conn.WriteEncryptedFrameAsync(selfNpc, CancellationToken.None);
            await conn.WriteEncryptedFrameAsync(curPlayer, CancellationToken.None);
            await conn.WriteEncryptedFrameAsync(npcFull, CancellationToken.None);
            await conn.WriteEncryptedFrameAsync(syncEnd, CancellationToken.None);
        }

        Task primaryTask = Task.Run(async () =>
        {
            TcpClient tcp = await primaryGsListener.AcceptTcpClientAsync();
            await using JxWireConnection conn = await JxWireConnection.AcceptClientAsync("primary-gs", tcp, keyTable, CancellationToken.None);
            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None); // logiclogin
            await SendValidWorldBootstrapAsync(conn, 1001u, 16986u);

            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None); // c2s_syncend
            JxWireFrame? dlgC2s = await conn.ReadDecryptedFrameAsync(CancellationToken.None); // 0x6E
            Assert.NotNull(dlgC2s);
            Assert.Equal(ProtocolOpcodes.c2s_dialognpc, dlgC2s.Opcode);
            await conn.WriteEncryptedFrameAsync(dialogReply, CancellationToken.None);

            JxWireFrame? probeC2s = await conn.ReadDecryptedFrameAsync(CancellationToken.None); // 0x49 cache-miss probe still reaches Primary
            Assert.NotNull(probeC2s);
            Assert.Equal(ProtocolOpcodes.c2s_requestnpc, probeC2s.Opcode);

            while (await conn.ReadDecryptedFrameAsync(CancellationToken.None) is not null) { }
        });

        Task shadowTask = Task.Run(async () =>
        {
            TcpClient tcp = await shadowGsListener.AcceptTcpClientAsync();
            await using JxWireConnection conn = await JxWireConnection.AcceptClientAsync("shadow-gs", tcp, keyTable, CancellationToken.None);
            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None); // logiclogin
            await SendValidWorldBootstrapAsync(conn, 2002u, 42u);

            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None); // c2s_syncend
            JxWireFrame? dlgC2s = await conn.ReadDecryptedFrameAsync(CancellationToken.None); // 0x6E rewritten to 42u
            Assert.NotNull(dlgC2s);
            Assert.Equal(ProtocolOpcodes.c2s_dialognpc, dlgC2s.Opcode);
            Assert.Equal(42u, BinaryPrimitives.ReadUInt32LittleEndian(dlgC2s.DecryptedPayload.AsSpan(1, 4)));

            await Task.Delay(55); // Simulate next 18-Hz tick on Shadow
            await conn.WriteEncryptedFrameAsync(dialogReply, CancellationToken.None);

            while (await conn.ReadDecryptedFrameAsync(CancellationToken.None) is not null) { }
        });

        await using (JxWireConnection clientGs = await JxWireConnection.ConnectUpstreamAsync("client-gs", "127.0.0.1", gsProxy.BoundPort, keyTable, CancellationToken.None))
        {
            byte[] logicLogin = new byte[17];
            logicLogin[0] = ProtocolOpcodes.c2s_logiclogin;
            primaryGuid.TryWriteBytes(logicLogin.AsSpan(1, 16));
            await clientGs.WriteEncryptedFrameAsync(logicLogin, CancellationToken.None);

            for (int i = 0; i < 5; i++)
            {
                _ = await clientGs.ReadDecryptedFrameAsync(CancellationToken.None);
            }

            byte[] c2sSyncEnd = new byte[9];
            c2sSyncEnd[0] = ProtocolOpcodes.c2s_syncend;
            await clientGs.WriteEncryptedFrameAsync(c2sSyncEnd, CancellationToken.None);
            await Task.Delay(160); // Let syncend window finish cleanly

            byte[] clickBoatman = new byte[9];
            clickBoatman[0] = ProtocolOpcodes.c2s_dialognpc;
            BinaryPrimitives.WriteUInt32LittleEndian(clickBoatman.AsSpan(1, 4), 16986u);
            await clientGs.WriteEncryptedFrameAsync(clickBoatman, CancellationToken.None);

            await Task.Delay(5); // Immediate background cache-miss probe from game.exe for unhydrated NpcId=112896!
            byte[] probeNpc = new byte[5];
            probeNpc[0] = ProtocolOpcodes.c2s_requestnpc;
            BinaryPrimitives.WriteUInt32LittleEndian(probeNpc.AsSpan(1, 4), 112896u);
            await clientGs.WriteEncryptedFrameAsync(probeNpc, CancellationToken.None);

            _ = await clientGs.ReadDecryptedFrameAsync(CancellationToken.None); // 0x63 dialogReply
            await Task.Delay(180); // Wait for 0x6E coalesced window to complete
        }

        await Task.WhenAll(primaryTask, shadowTask).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(gsProxy.LatestSession);
        IReadOnlyList<JXReborn.DualParityServer.Diffing.ParityFinding> allFindings = gsProxy.LatestSession.DiffCoordinator.GetFindings();
        Assert.DoesNotContain(allFindings, static f => f.Severity == JXReborn.DualParityServer.Diffing.FindingSeverity.Critical);
    }

    [Fact]
    public async Task GameServerShadowProxy_OnDuplicate0x72ClickAnd0x82InputSubmit_ShouldCoalesceClickAndOpenDedicated0x82Window()
    {
        // Ground truth: Map 53 mui3-zero-crit Step 15 (duplicate 0x72 72650062006D040000 10ms apart)
        // and Step 110 (0x82 c2s_citywarextend AskClientForNumber input submit must open its own window).
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
            RoleName: "DupClickAnd0x82Hero",
            CreatedAtUtc: DateTimeOffset.UtcNow));

        var options = new WireParityProxyOptions
        {
            BindHost = "127.0.0.1",
            BishopListenPort = 0,
            GameServerListenPort = 0,
            AllowSyntheticKeyTableFallback = true,
            ActionWindowMs = 150
        };

        await using var gsProxy = new GameServerShadowProxy(options, keyTable, handoffMap);
        gsProxy.Start();

        byte[] signboardReply = ScriptActionS2CPacket.CreateSayPayload("Nha môn thông cáo", []);

        static async Task SendBootstrapWithNoticeBoardAsync(JxWireConnection conn, uint selfNpcId, int objId)
        {
            SyncWorldS2CPacket sw = SyncWorldS2CPacket.Create(53, 52003, 101307, 100, "Ba Lang Huyen");
            byte[] syncWorld = new byte[SyncWorldS2CPacket.Size];
            MemoryMarshal.Write(syncWorld, in sw);
            byte[] selfNpc = new SyncNpcFullS2CPacket(0, ElementSeries.Metal, 128, 101307, 1, 0, 52003, 0, 0, EntityKind.Player, selfNpcId, -1, 0xFFFF, 1, 0, "DupClickAnd0x82Hero").ToPayloadBytes();
            byte[] curPlayer = new byte[Marshal.SizeOf<SyncCurPlayerS2CPacket>()];
            var curPkt = new SyncCurPlayerS2CPacket { ProtocolType = ProtocolOpcodes.s2c_synccurplayer, Id = selfNpcId, Level = 1, LifeMax = 204, StaminaMax = 50, Kind = (byte)EntityKind.Player, ManaMax = 16 };
            MemoryMarshal.Write(curPlayer, in curPkt);
            byte[] objAdd = ObjAddS2CPacket.CreatePayloadBytes(1, objId, 4, 52083, 101248, 0, 0, 0, 0, 0, 0, 0, 0, string.Empty);
            byte[] syncEnd = [ProtocolOpcodes.s2c_syncend];
            await conn.WriteEncryptedFrameAsync(syncWorld, CancellationToken.None);
            await conn.WriteEncryptedFrameAsync(selfNpc, CancellationToken.None);
            await conn.WriteEncryptedFrameAsync(curPlayer, CancellationToken.None);
            await conn.WriteEncryptedFrameAsync(objAdd, CancellationToken.None);
            await conn.WriteEncryptedFrameAsync(syncEnd, CancellationToken.None);
        }

        Task primaryTask = Task.Run(async () =>
        {
            TcpClient tcp = await primaryGsListener.AcceptTcpClientAsync();
            await using JxWireConnection conn = await JxWireConnection.AcceptClientAsync("primary-gs", tcp, keyTable, CancellationToken.None);
            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None);
            await SendBootstrapWithNoticeBoardAsync(conn, 1001u, 1133);
            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None); // 0x43
            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None); // 0xA7 post-syncend
            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None); // 0x72 #1
            await conn.WriteEncryptedFrameAsync(signboardReply, CancellationToken.None); // immediate Primary reply!
            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None); // 0x72 #2 (10ms later)
            await conn.WriteEncryptedFrameAsync(signboardReply, CancellationToken.None);
            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None); // 0x5F menu
            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None); // 0x82 input number
            await Task.Delay(60); // Primary teleports 60ms after 0x82!
            SyncNpcPosS2CPacket pos = SyncNpcPosS2CPacket.Create(1001u, 53376, 95424);
            byte[] posBytes = new byte[SyncNpcPosS2CPacket.Size];
            MemoryMarshal.Write(posBytes, in pos);
            await conn.WriteEncryptedFrameAsync(posBytes, CancellationToken.None);
            while (await conn.ReadDecryptedFrameAsync(CancellationToken.None) is not null) { }
        });

        Task shadowTask = Task.Run(async () =>
        {
            TcpClient tcp = await shadowGsListener.AcceptTcpClientAsync();
            await using JxWireConnection conn = await JxWireConnection.AcceptClientAsync("shadow-gs", tcp, keyTable, CancellationToken.None);
            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None);
            await SendBootstrapWithNoticeBoardAsync(conn, 2002u, 6);
            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None); // 0x43
            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None); // 0xA7 post-syncend
            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None); // 0x72 #1
            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None); // 0x72 #2 arrives before 18-Hz tick!
            await Task.Delay(45);
            await conn.WriteEncryptedFrameAsync(signboardReply, CancellationToken.None);
            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None); // 0x5F menu
            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None); // 0x82 input number
            await Task.Delay(10); // Shadow teleports in 10ms!
            SyncNpcPosS2CPacket pos = SyncNpcPosS2CPacket.Create(2002u, 53376, 95424);
            byte[] posBytes = new byte[SyncNpcPosS2CPacket.Size];
            MemoryMarshal.Write(posBytes, in pos);
            await conn.WriteEncryptedFrameAsync(posBytes, CancellationToken.None);
            while (await conn.ReadDecryptedFrameAsync(CancellationToken.None) is not null) { }
        });

        await using (JxWireConnection clientGs = await JxWireConnection.ConnectUpstreamAsync("client-gs", "127.0.0.1", gsProxy.BoundPort, keyTable, CancellationToken.None))
        {
            byte[] logicLogin = new byte[17];
            logicLogin[0] = ProtocolOpcodes.c2s_logiclogin;
            primaryGuid.TryWriteBytes(logicLogin.AsSpan(1, 16));
            await clientGs.WriteEncryptedFrameAsync(logicLogin, CancellationToken.None);
            for (int i = 0; i < 5; i++)
            {
                _ = await clientGs.ReadDecryptedFrameAsync(CancellationToken.None);
            }

            await clientGs.WriteEncryptedFrameAsync(new byte[9] { ProtocolOpcodes.c2s_syncend, 0, 0, 0, 0, 0, 0, 0, 0 }, CancellationToken.None);
            await Task.Delay(5);
            await clientGs.WriteEncryptedFrameAsync(new byte[] { 0xA7, 0, 0, 0, 0 }, CancellationToken.None);
            await Task.Delay(170);

            byte[] clickBoard = Convert.FromHexString("72650062006D040000");
            await clientGs.WriteEncryptedFrameAsync(clickBoard, CancellationToken.None);
            await Task.Delay(10); // Duplicate 0x72 from game.exe AI frame before Shadow's 18-Hz tick!
            await clientGs.WriteEncryptedFrameAsync(clickBoard, CancellationToken.None);
            _ = await clientGs.ReadDecryptedFrameAsync(CancellationToken.None);
            _ = await clientGs.ReadDecryptedFrameAsync(CancellationToken.None);
            await Task.Delay(180);

            // Simulate 0x5F followed 125ms later by 0x82 input submit (previously 0x82 failed to open its own window and split Primary vs Shadow teleport!)
            byte[] select5F = new byte[17];
            select5F[0] = ProtocolOpcodes.c2s_playerselui;
            await clientGs.WriteEncryptedFrameAsync(select5F, CancellationToken.None);
            await Task.Delay(125);
            byte[] submit82 = new byte[12];
            submit82[0] = ProtocolOpcodes.c2s_citywarextend;
            await clientGs.WriteEncryptedFrameAsync(submit82, CancellationToken.None);
            _ = await clientGs.ReadDecryptedFrameAsync(CancellationToken.None);
            await Task.Delay(180);
        }

        await Task.WhenAll(primaryTask, shadowTask).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(gsProxy.LatestSession);
        IReadOnlyList<JXReborn.DualParityServer.Diffing.ParityFinding> findings = gsProxy.LatestSession.DiffCoordinator.GetFindings();
        Assert.DoesNotContain(findings, static f => f.Severity == JXReborn.DualParityServer.Diffing.FindingSeverity.Critical);
    }

    [Fact]
    public async Task GameServerShadowProxy_WhenClientEmits0xFAExtendDuring0x5FMapTransition_ShouldKeep0x5FWindowOpenUntilShadowSyncWorldArrives()
    {
        // Ground truth: live playtest wpp-20261124-152238-49b20b Step 76..81:
        // On 0x5F faction join teleport (Map 53 -> Map 13), Primary emits 0x49 (s2c_syncworld) immediately,
        // causing game.exe to emit 0xFA (c2s_extend channel query) ~10ms later before Shadow's 18-Hz tick finishes.
        // 0xFA must not slice the active 0x5F transition window into a transient Player.MapId Critical diff.
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
            RoleName: "WaterSpark",
            CreatedAtUtc: DateTimeOffset.UtcNow));

        var options = new WireParityProxyOptions
        {
            BindHost = "127.0.0.1",
            BishopListenPort = 0,
            GameServerListenPort = 0,
            AllowSyntheticKeyTableFallback = true,
            ActionWindowMs = 150
        };

        await using var gsProxy = new GameServerShadowProxy(options, keyTable, handoffMap);
        gsProxy.Start();

        static byte[] CreateSyncWorldBytes(ushort mapId, int mpsX, int mpsY, string mapName)
        {
            SyncWorldS2CPacket sw = SyncWorldS2CPacket.Create(mapId, mpsX, mpsY, 100, mapName);
            byte[] buf = new byte[SyncWorldS2CPacket.Size];
            MemoryMarshal.Write(buf, in sw);
            return buf;
        }

        static async Task SendBootstrapAsync(JxWireConnection conn, uint selfNpcId)
        {
            byte[] syncWorld = CreateSyncWorldBytes(53, 52003, 101307, "Ba Lang Huyen");
            byte[] selfNpc = new SyncNpcFullS2CPacket(0, ElementSeries.Water, 128, 101307, 1, 0, 52003, 0, 0, EntityKind.Player, selfNpcId, -1, 0xFFFF, 1, 0, "WaterSpark").ToPayloadBytes();
            byte[] curPlayer = new byte[Marshal.SizeOf<SyncCurPlayerS2CPacket>()];
            var curPkt = new SyncCurPlayerS2CPacket { ProtocolType = ProtocolOpcodes.s2c_synccurplayer, Id = selfNpcId, Level = 10, LifeMax = 204, StaminaMax = 50, Kind = (byte)EntityKind.Player, ManaMax = 16 };
            MemoryMarshal.Write(curPlayer, in curPkt);
            byte[] syncEnd = [ProtocolOpcodes.s2c_syncend];
            await conn.WriteEncryptedFrameAsync(syncWorld, CancellationToken.None);
            await conn.WriteEncryptedFrameAsync(selfNpc, CancellationToken.None);
            await conn.WriteEncryptedFrameAsync(curPlayer, CancellationToken.None);
            await conn.WriteEncryptedFrameAsync(syncEnd, CancellationToken.None);
        }

        byte[] map13SyncWorld = CreateSyncWorldBytes(13, 60736, 159296, "Nga Mi phai");

        Task primaryTask = Task.Run(async () =>
        {
            TcpClient tcp = await primaryGsListener.AcceptTcpClientAsync();
            await using JxWireConnection conn = await JxWireConnection.AcceptClientAsync("primary-gs", tcp, keyTable, CancellationToken.None);
            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None); // logiclogin
            await SendBootstrapAsync(conn, 1001u);
            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None); // 0x43
            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None); // 0x5F
            await conn.WriteEncryptedFrameAsync(map13SyncWorld, CancellationToken.None); // Immediate Map 13 transition!
            while (await conn.ReadDecryptedFrameAsync(CancellationToken.None) is not null) { }
        });

        Task shadowTask = Task.Run(async () =>
        {
            TcpClient tcp = await shadowGsListener.AcceptTcpClientAsync();
            await using JxWireConnection conn = await JxWireConnection.AcceptClientAsync("shadow-gs", tcp, keyTable, CancellationToken.None);
            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None); // logiclogin
            await SendBootstrapAsync(conn, 2002u);
            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None); // 0x43
            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None); // 0x5F
            await Task.Delay(55); // Shadow 18-Hz tick completes after client's 0xFA burst!
            await conn.WriteEncryptedFrameAsync(map13SyncWorld, CancellationToken.None);
            while (await conn.ReadDecryptedFrameAsync(CancellationToken.None) is not null) { }
        });

        await using (JxWireConnection clientGs = await JxWireConnection.ConnectUpstreamAsync("client-gs", "127.0.0.1", gsProxy.BoundPort, keyTable, CancellationToken.None))
        {
            byte[] logicLogin = new byte[17];
            logicLogin[0] = ProtocolOpcodes.c2s_logiclogin;
            primaryGuid.TryWriteBytes(logicLogin.AsSpan(1, 16));
            await clientGs.WriteEncryptedFrameAsync(logicLogin, CancellationToken.None);
            for (int i = 0; i < 4; i++)
            {
                _ = await clientGs.ReadDecryptedFrameAsync(CancellationToken.None);
            }

            await clientGs.WriteEncryptedFrameAsync(new byte[9] { ProtocolOpcodes.c2s_syncend, 0, 0, 0, 0, 0, 0, 0, 0 }, CancellationToken.None);
            await Task.Delay(180);

            byte[] select5F = new byte[17];
            select5F[0] = ProtocolOpcodes.c2s_playerselui;
            await clientGs.WriteEncryptedFrameAsync(select5F, CancellationToken.None);
            _ = await clientGs.ReadDecryptedFrameAsync(CancellationToken.None); // Primary's 0x49 arrives at client

            // Client immediately fires 0xFA c2s_extend channel queries upon entering Map 13!
            byte[] extendFa = new byte[12];
            extendFa[0] = ProtocolOpcodes.c2s_extend;
            await clientGs.WriteEncryptedFrameAsync(extendFa, CancellationToken.None);
            await Task.Delay(200);
        }

        await Task.WhenAll(primaryTask, shadowTask).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(gsProxy.LatestSession);
        IReadOnlyList<JXReborn.DualParityServer.Diffing.ParityFinding> transitionFindings = gsProxy.LatestSession.DiffCoordinator.GetFindings();
        Assert.DoesNotContain(transitionFindings, static f => f.Severity == JXReborn.DualParityServer.Diffing.FindingSeverity.Critical);
    }
}
