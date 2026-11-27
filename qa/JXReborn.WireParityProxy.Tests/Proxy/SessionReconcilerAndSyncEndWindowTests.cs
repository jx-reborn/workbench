using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using JXReborn.Adapter.Legacy.Ciphers;
using JXReborn.Adapter.Legacy.Packets;
using JXReborn.DualParityServer.Diffing;
using JXReborn.Primitives.Characters;
using JXReborn.Primitives.Items;
using JXReborn.WireParityProxy.Configuration;
using JXReborn.WireParityProxy.Proxy;
using JXReborn.WireParityProxy.Web;
using JXReborn.WireParityProxy.Wire;
using Xunit;

namespace JXReborn.WireParityProxy.Tests.Proxy;

/// <summary>
/// Ground-truth tests anchored on live session <c>wpp-20261127-002318-4ccba6</c> (<c>Coolant</c> Lv120):
/// 1) <c>c2s_syncend</c> (<c>435D150000501F0000</c>) followed 90ms later by <c>game.exe</c>'s automatic
///    <c>c2s_pkapplychangenormalflag</c> (<c>7600</c>) must coalesce into Step 1 so <c>LoginHint</c> <c>0x63</c>
///    is never falsely split across Step 1 (<c>Missing[0x63]</c>) and Step 2 (<c>Extra[0x63]</c>).
/// 2) Manual <c>ReconcileLatestSessionAsync</c> never auto-fires on login, syncs Shadow -&gt; Primary first (Tier 1),
///    and falls back to pulling BOTH Primary and Shadow to Ba Lăng Huyện <c>Map 53 (51200, 102400)</c> (Tier 2).
/// </summary>
public sealed class SessionReconcilerAndSyncEndWindowTests
{
    [Fact]
    public async Task SyncEndWindow_WhenClientEmitsPkNormalFlag0x76Within90Ms_ShouldCoalesceIntoStep1AndAvoidFalse0x63Split()
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
            RoleName: "Coolant",
            CreatedAtUtc: DateTimeOffset.UtcNow));

        var options = new WireParityProxyOptions
        {
            BindHost = "127.0.0.1",
            BishopListenPort = 0,
            GameServerListenPort = 0,
            AllowSyntheticKeyTableFallback = true,
            ActionWindowMs = 200
        };

        await using var gsProxy = new GameServerShadowProxy(options, keyTable, handoffMap);
        gsProxy.Start();

        byte[] loginHint0x63 = ScriptActionS2CPacket.CreateDescribePayload(
            "<link=image[0,1]:\\spr\\npcres\\enemy\\enemy060\\enemy060_st.spr>Hệ Thống thông báo hoạt động có thể làm : <link>",
            ["Đêm huy hoàng, Xem Chi Tiết", "Hôm nay không cần nhắc nhở ta", "Kết thúc đối thoại!"]);

        Task RunBackendAsync(TcpListener listener, int delayBeforeHintMs) => Task.Run(async () =>
        {
            TcpClient tcp = await listener.AcceptTcpClientAsync();
            await using JxWireConnection conn = await JxWireConnection.AcceptClientAsync("fake-gs", tcp, keyTable, CancellationToken.None);

            JxWireFrame? login = await conn.ReadDecryptedFrameAsync(CancellationToken.None);
            Assert.NotNull(login);

            await WriteInitialSyncBurstAsync(conn, mapId: 199, mpsX: 52331, mpsY: 93322, npcId: 1001u, level: 120, life: 2400);

            JxWireFrame? syncEnd = await conn.ReadDecryptedFrameAsync(CancellationToken.None);
            Assert.NotNull(syncEnd);
            Assert.Equal(ProtocolOpcodes.c2s_syncend, syncEnd.Opcode);

            if (delayBeforeHintMs <= 30)
            {
                await Task.Delay(delayBeforeHintMs);
                await conn.WriteEncryptedFrameAsync(loginHint0x63, CancellationToken.None);
            }

            JxWireFrame? pkNormal = await conn.ReadDecryptedFrameAsync(CancellationToken.None);
            Assert.NotNull(pkNormal);
            Assert.Equal(ProtocolOpcodes.c2s_pkapplychangenormalflag, pkNormal.Opcode);

            if (delayBeforeHintMs > 30)
            {
                await Task.Delay(30);
                await conn.WriteEncryptedFrameAsync(loginHint0x63, CancellationToken.None);
            }

            byte[] pkAck = [ProtocolOpcodes.s2c_pksyncnormalflag, 0x00];
            await conn.WriteEncryptedFrameAsync(pkAck, CancellationToken.None);

            while (await conn.ReadDecryptedFrameAsync(CancellationToken.None) is not null)
            {
            }
        });

        Task primaryTask = RunBackendAsync(primaryGsListener, delayBeforeHintMs: 15);
        Task shadowTask = RunBackendAsync(shadowGsListener, delayBeforeHintMs: 120);

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

            // Drain initial S2C sync burst (4 frames: world, npc, curplayer, syncend)
            for (int i = 0; i < 4; i++)
            {
                _ = await clientGs.ReadDecryptedFrameAsync(CancellationToken.None);
            }

            // Exact Step 1 & Step 2 C2S bytes from wpp-20261127-002318-4ccba6: 435D150000501F0000 then 7600 60ms later
            byte[] c2sSyncEnd = Convert.FromHexString("435D150000501F0000");
            byte[] c2sPkFlag = Convert.FromHexString("7600");
            await clientGs.WriteEncryptedFrameAsync(c2sSyncEnd, CancellationToken.None);
            await Task.Delay(60);
            await clientGs.WriteEncryptedFrameAsync(c2sPkFlag, CancellationToken.None);

            await Task.Delay(380);
        }

        await Task.WhenAll(primaryTask, shadowTask);

        WireParitySession? session = gsProxy.LatestSession;
        Assert.NotNull(session);
        IReadOnlyList<ParityFinding> criticalFindings = session!.DiffCoordinator.GetFindings()
            .Where(static f => f.Severity == FindingSeverity.Critical)
            .ToArray();
        Assert.Empty(criticalFindings);
        Assert.Single(session.DiffCoordinator.GetSteps());
    }

    [Fact]
    public async Task ReconcileLatestSessionAsync_WhenShadowIsDeadOnMap199AndPrimaryAliveOnMap53_ShouldRevivePushTasksAndUseGmTokenOnShadowOnly()
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
            RoleName: "Coolant",
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

        var primaryPostSyncFrames = new ConcurrentQueue<JxWireFrame>();
        var shadowPostSyncFrames = new ConcurrentQueue<JxWireFrame>();
        var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task primaryTask = Task.Run(async () =>
        {
            TcpClient tcp = await primaryGsListener.AcceptTcpClientAsync();
            await using JxWireConnection conn = await JxWireConnection.AcceptClientAsync("primary-gs", tcp, keyTable, CancellationToken.None);
            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None); // logiclogin

            await WriteInitialSyncBurstAsync(conn, mapId: 53, mpsX: 51200, mpsY: 102400, npcId: 1001u, level: 120, life: 2400, money: 250_000);
            await WriteGmTokenSyncAsync(conn, runtimeItemId: 501u, gridX: 0, gridY: 0);

            byte[] taskPkt = new byte[9];
            taskPkt[0] = ProtocolOpcodes.s2c_synctaskvalue;
            BinaryPrimitives.WriteInt32LittleEndian(taskPkt.AsSpan(1, 4), 1014);
            BinaryPrimitives.WriteInt32LittleEndian(taskPkt.AsSpan(5, 4), 2);
            await conn.WriteEncryptedFrameAsync(taskPkt, CancellationToken.None);

            byte[] skillPkt = new byte[9];
            skillPkt[0] = ProtocolOpcodes.s2c_playerskilllevel;
            BinaryPrimitives.WriteInt32LittleEndian(skillPkt.AsSpan(1, 4), 37);
            BinaryPrimitives.WriteInt32LittleEndian(skillPkt.AsSpan(5, 4), 15);
            await conn.WriteEncryptedFrameAsync(skillPkt, CancellationToken.None);

            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None); // c2s_syncend
            while (await conn.ReadDecryptedFrameAsync(CancellationToken.None) is { } extra)
            {
                primaryPostSyncFrames.Enqueue(extra);
            }
        });

        Task shadowTask = Task.Run(async () =>
        {
            TcpClient tcp = await shadowGsListener.AcceptTcpClientAsync();
            await using JxWireConnection conn = await JxWireConnection.AcceptClientAsync("shadow-gs", tcp, keyTable, CancellationToken.None);
            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None); // logiclogin

            // Shadow starts drifted on Map 199 (Vũ Lăng Động) and dead (s2c_npcdeath 0x57) with GM Token at (2, 3) id=777
            await WriteInitialSyncBurstAsync(conn, mapId: 199, mpsX: 52331, mpsY: 93322, npcId: 9001u, level: 10, life: 0, money: 0);
            await WriteGmTokenSyncAsync(conn, runtimeItemId: 777u, gridX: 2, gridY: 3);

            byte[] deathPkt = new byte[5];
            deathPkt[0] = ProtocolOpcodes.s2c_npcdeath;
            BinaryPrimitives.WriteUInt32LittleEndian(deathPkt.AsSpan(1, 4), 9001u);
            await conn.WriteEncryptedFrameAsync(deathPkt, CancellationToken.None);

            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None); // c2s_syncend
            readyTcs.TrySetResult();

            while (await conn.ReadDecryptedFrameAsync(CancellationToken.None) is { } cmd)
            {
                shadowPostSyncFrames.Enqueue(cmd);
                if (cmd.Opcode == ProtocolOpcodes.c2s_npcrevive)
                {
                    byte[] revPkt = new byte[NpcReviveS2CPacket.PayloadSize];
                    new NpcReviveS2CPacket(9001u, 0).WritePayload(revPkt);
                    await conn.WriteEncryptedFrameAsync(revPkt, CancellationToken.None);
                    await WriteNormalVitalsAsync(conn, life: 200, mana: 100, stamina: 100);
                }
                else if (cmd.Opcode == ProtocolOpcodes.c2s_playereatitem)
                {
                    // Simulate TestGmTokenItemScript applying scratch tasks -> teleporting Shadow to Map 53 (51200, 102400)
                    await conn.WriteEncryptedFrameAsync(StructBytes(SyncWorldS2CPacket.Create(53, 51200, 102400, 100, "Ba Lang Huyen")), CancellationToken.None);
                    byte[] minPos = StructBytes(new SyncNpcMinPlayerS2CPacket
                    {
                        ProtocolType = ProtocolOpcodes.s2c_syncnpcminplayer,
                        MapMpsX = 51200,
                        MapMpsY = 102400,
                    });
                    await conn.WriteEncryptedFrameAsync(minPos, CancellationToken.None);
                    byte[] moneySync = new byte[SyncMoneyS2CPacket.PayloadSize];
                    new SyncMoneyS2CPacket(250_000, 0).WritePayload(moneySync);
                    await conn.WriteEncryptedFrameAsync(moneySync, CancellationToken.None);
                    await WriteNormalVitalsAsync(conn, life: 2400, mana: 800, stamina: 300);
                }
            }
        });

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

            for (int i = 0; i < 4; i++)
            {
                _ = await clientGs.ReadDecryptedFrameAsync(CancellationToken.None);
            }

            await clientGs.WriteEncryptedFrameAsync(Convert.FromHexString("435D150000501F0000"), CancellationToken.None);
            await readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(120);

            // MUST NOT auto-heal silently on login before the user clicks the sync button!
            Assert.Empty(shadowPostSyncFrames);

            SessionReconcileResult res = await gsProxy.ReconcileLatestSessionAsync(forceBaLangFallback: false);

            Assert.True(res.Succeeded, res.Message);
            Assert.False(res.UsedBaLangFallback);
            Assert.Equal(53, res.PrimaryMapId);
            Assert.Equal(53, res.ShadowMapId);
            Assert.False(gsProxy.LatestSession!.DiffCoordinator.LatestDrift.IsDrifted);

            // Primary MUST NOT receive any injected packets in Tier 1!
            Assert.Empty(primaryPostSyncFrames);

            // Shadow MUST receive: 0x74 (revive), >=1 0xAB (0xFF01 task sync), and 0x59 (GM Token at GridX=2, GridY=3, Id=777)
            JxWireFrame[] shadowCmds = shadowPostSyncFrames.ToArray();
            Assert.NotEmpty(shadowCmds);
            Assert.Contains(shadowCmds, static f => f.Opcode == ProtocolOpcodes.c2s_npcrevive);
            Assert.Contains(shadowCmds, static f =>
                f.Opcode == ProtocolOpcodes.c2s_scriptprotocol &&
                ScriptProtocolC2SPacket.TryParse(f.DecryptedPayload, out var sp) &&
                sp.Param == ScriptProtocolC2SPacket.TestSyncTaskValuesProtocolId);
            Assert.Contains(shadowCmds, static f =>
                f.Opcode == ProtocolOpcodes.c2s_playereatitem &&
                PlayerEatItemC2SPacket.TryParse(f.DecryptedPayload, out var eat) &&
                eat.GridX == 2 &&
                eat.GridY == 3 &&
                eat.RuntimeItemId == 777u &&
                eat.ContainerPlace == ItemContainerPlace.EquipRoom);
        }

        await Task.WhenAll(primaryTask, shadowTask);
    }

    [Fact]
    public async Task ReconcileLatestSessionAsync_WhenForceBaLangRequestedOrPrimaryDead_ShouldPullBothBackendsToBaLang53()
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
            RoleName: "Coolant",
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

        var primaryCmds = new ConcurrentQueue<JxWireFrame>();
        var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task primaryTask = Task.Run(async () =>
        {
            TcpClient tcp = await primaryGsListener.AcceptTcpClientAsync();
            await using JxWireConnection conn = await JxWireConnection.AcceptClientAsync("primary-gs", tcp, keyTable, CancellationToken.None);
            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None);

            // Primary starts on Map 199 dead -> Tier 1 cannot sync to a dead corpse -> must fall back to Tier 2 (pull BOTH to Ba Lăng 53)!
            await WriteInitialSyncBurstAsync(conn, mapId: 199, mpsX: 52331, mpsY: 93322, npcId: 1001u, level: 120, life: 0, money: 100_000);
            await WriteGmTokenSyncAsync(conn, runtimeItemId: 501u, gridX: 1, gridY: 0);
            byte[] deathPkt = new byte[5];
            deathPkt[0] = ProtocolOpcodes.s2c_npcdeath;
            BinaryPrimitives.WriteUInt32LittleEndian(deathPkt.AsSpan(1, 4), 1001u);
            await conn.WriteEncryptedFrameAsync(deathPkt, CancellationToken.None);

            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None); // c2s_syncend
            int menuDepth = 0;
            while (await conn.ReadDecryptedFrameAsync(CancellationToken.None) is { } cmd)
            {
                primaryCmds.Enqueue(cmd);
                if (cmd.Opcode == ProtocolOpcodes.c2s_npcrevive)
                {
                    // Revive on Primary (e.g. to Map 78 revival point first)
                    byte[] revPkt = new byte[NpcReviveS2CPacket.PayloadSize];
                    new NpcReviveS2CPacket(1001u, 0).WritePayload(revPkt);
                    await conn.WriteEncryptedFrameAsync(revPkt, CancellationToken.None);
                    await conn.WriteEncryptedFrameAsync(StructBytes(SyncWorldS2CPacket.Create(78, 50176, 103424, 100, "Tuong Duong")), CancellationToken.None);
                    await WriteNormalVitalsAsync(conn, life: 2400, mana: 800, stamina: 300);
                }
                else if (cmd.Opcode == ProtocolOpcodes.c2s_playereatitem)
                {
                    menuDepth = 1;
                    await conn.WriteEncryptedFrameAsync(
                        ScriptActionS2CPacket.CreateSayPayload("GM Root", ["Char", "Faction", "Eco", "Items", "Combat", "Teleport", "World", "Tasks", "Close"]),
                        CancellationToken.None);
                }
                else if (cmd.Opcode == ProtocolOpcodes.c2s_playerselui && PlayerSelectUiC2SPacket.TryParse(cmd.DecryptedPayload, out var sel))
                {
                    if (menuDepth == 1 && sel.SelectIndex == 5)
                    {
                        menuDepth = 2;
                        await conn.WriteEncryptedFrameAsync(
                            ScriptActionS2CPacket.CreateSayPayload("Teleport", ["Ba Lang (53)", "Back"]),
                            CancellationToken.None);
                    }
                    else if (menuDepth == 2 && sel.SelectIndex == 0)
                    {
                        menuDepth = 0;
                        await conn.WriteEncryptedFrameAsync(StructBytes(SyncWorldS2CPacket.Create(53, 51200, 102400, 100, "Ba Lang Huyen")), CancellationToken.None);
                        await conn.WriteEncryptedFrameAsync(StructBytes(new SyncNpcMinPlayerS2CPacket
                        {
                            ProtocolType = ProtocolOpcodes.s2c_syncnpcminplayer,
                            MapMpsX = 51200,
                            MapMpsY = 102400,
                        }), CancellationToken.None);
                    }
                }
            }
        });

        Task shadowTask = Task.Run(async () =>
        {
            TcpClient tcp = await shadowGsListener.AcceptTcpClientAsync();
            await using JxWireConnection conn = await JxWireConnection.AcceptClientAsync("shadow-gs", tcp, keyTable, CancellationToken.None);
            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None);

            await WriteInitialSyncBurstAsync(conn, mapId: 199, mpsX: 52331, mpsY: 93322, npcId: 9001u, level: 120, life: 2400, money: 100_000);
            await WriteGmTokenSyncAsync(conn, runtimeItemId: 777u, gridX: 0, gridY: 0);

            _ = await conn.ReadDecryptedFrameAsync(CancellationToken.None); // c2s_syncend
            readyTcs.TrySetResult();

            while (await conn.ReadDecryptedFrameAsync(CancellationToken.None) is { } cmd)
            {
                if (cmd.Opcode == ProtocolOpcodes.c2s_playereatitem)
                {
                    await conn.WriteEncryptedFrameAsync(StructBytes(SyncWorldS2CPacket.Create(53, 51200, 102400, 100, "Ba Lang Huyen")), CancellationToken.None);
                    await conn.WriteEncryptedFrameAsync(StructBytes(new SyncNpcMinPlayerS2CPacket
                    {
                        ProtocolType = ProtocolOpcodes.s2c_syncnpcminplayer,
                        MapMpsX = 51200,
                        MapMpsY = 102400,
                    }), CancellationToken.None);
                }
            }
        });

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

            _ = Task.Run(async () =>
            {
                try
                {
                    while (await clientGs.ReadDecryptedFrameAsync(CancellationToken.None) is not null)
                    {
                    }
                }
                catch
                {
                }
            });

            await Task.Delay(80);
            await clientGs.WriteEncryptedFrameAsync(Convert.FromHexString("435D150000501F0000"), CancellationToken.None);
            await readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(80);

            // Call with forceBaLangFallback: false -> because Primary is dead on Map 199, Tier 1 must automatically fall back to Tier 2!
            SessionReconcileResult res = await gsProxy.ReconcileLatestSessionAsync(forceBaLangFallback: false);

            Assert.True(res.Succeeded, res.Message);
            Assert.True(res.UsedBaLangFallback);
            Assert.Equal(53, res.PrimaryMapId);
            Assert.Equal(53, res.ShadowMapId);
            Assert.NotEmpty(primaryCmds);
            Assert.Contains(primaryCmds, static f => f.Opcode == ProtocolOpcodes.c2s_npcrevive);
            Assert.Contains(primaryCmds, static f => f.Opcode == ProtocolOpcodes.c2s_playereatitem);
        }

        await Task.WhenAll(primaryTask, shadowTask);
    }

    [Fact]
    public void DashboardHtml_ToolbarMarkup_ShouldExposeManualReconcileAndBaLangFallbackButtons()
    {
        Assert.Contains("/api/reconcile", WireParityDashboardHtml.Page, StringComparison.Ordinal);
        Assert.Contains("Ba Lăng", WireParityDashboardHtml.Page, StringComparison.Ordinal);
    }

    private static async Task WriteInitialSyncBurstAsync(
        JxWireConnection conn,
        ushort mapId,
        int mpsX,
        int mpsY,
        uint npcId,
        byte level,
        ushort life,
        int money = 0)
    {
        await conn.WriteEncryptedFrameAsync(StructBytes(SyncWorldS2CPacket.Create(mapId, mpsX, mpsY, 100, "Map")), CancellationToken.None);
        byte[] syncSelfNpc = new SyncNpcFullS2CPacket(
            Camp: 1,
            Series: ElementSeries.Water,
            LifePercent: (byte)(life > 0 ? 128 : 0),
            MapMpsY: mpsY,
            DoingState: (byte)(life > 0 ? 1 : 13),
            StateInfo: 0,
            MapMpsX: mpsX,
            MenuState: 0,
            GoldType: 0,
            Kind: EntityKind.Player,
            NpcId: npcId,
            MasterId: -1,
            TemplateId: 0xFFFF,
            Level: level,
            RankFfId: 0,
            NameUnicode: "Coolant").ToPayloadBytes();
        await conn.WriteEncryptedFrameAsync(syncSelfNpc, CancellationToken.None);
        await conn.WriteEncryptedFrameAsync(StructBytes(new SyncCurPlayerS2CPacket
        {
            ProtocolType = ProtocolOpcodes.s2c_synccurplayer,
            Id = npcId,
            Level = level,
            LifeMax = 2400,
            ManaMax = 800,
            StaminaMax = 300,
            Money1 = money,
            Kind = (byte)EntityKind.Player,
        }), CancellationToken.None);
        await WriteNormalVitalsAsync(conn, life, 800, 300);
        await conn.WriteEncryptedFrameAsync(StructBytes(SyncEndS2CPacket.Create()), CancellationToken.None);
    }

    private static Task WriteNormalVitalsAsync(JxWireConnection conn, ushort life, ushort mana, ushort stamina) =>
        conn.WriteEncryptedFrameAsync(StructBytes(new SyncCurPlayerNormalS2CPacket
        {
            ProtocolType = ProtocolOpcodes.s2c_synccurplayernormal,
            Life = life,
            Mana = mana,
            Stamina = stamina,
        }), CancellationToken.None);

    private static Task WriteGmTokenSyncAsync(JxWireConnection conn, uint runtimeItemId, byte gridX, byte gridY)
    {
        var pkt = new SyncItemS2CPacket(
            RuntimeItemId: runtimeItemId,
            Genre: ItemGenreKind.MagicScript,
            Quality: ItemQualityKind.Normal,
            DetailType: 1,
            ParticularType: 4258,
            Series: ElementSeries.Metal,
            Level: 1,
            Luck: 0,
            MagicLevel0: 0,
            MagicLevel1: 0,
            MagicLevel2: 0,
            MagicLevel3: 0,
            MagicLevel4: 0,
            MagicLevel5: 0,
            Version: 1,
            RandomSeed: 0,
            StackCount: 1,
            MaxDurability: 1,
            CurrentDurability: 1,
            ContainerPlace: ItemContainerPlace.EquipRoom,
            GridY: gridY,
            GridX: gridX,
            WidthCells: 1,
            HeightCells: 1);
        byte[] buf = new byte[SyncItemS2CPacket.PayloadSize];
        pkt.WritePayload(buf);
        return conn.WriteEncryptedFrameAsync(buf, CancellationToken.None);
    }

    private static byte[] StructBytes<T>(T value) where T : struct
    {
        byte[] bytes = new byte[Marshal.SizeOf<T>()];
        MemoryMarshal.Write(bytes, in value);
        return bytes;
    }
}
