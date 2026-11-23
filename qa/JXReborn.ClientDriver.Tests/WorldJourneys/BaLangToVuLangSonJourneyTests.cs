using System.Diagnostics;
using System.Text.Json;
using JXReborn.Adapter.Legacy.Packets;
using JXReborn.ClientDriver.Tests.Diagnostics;
using JXReborn.GameAssets.WorldMaps;
using JXReborn.Primitives.Characters;
using JXReborn.ZoneEngine.NpcEntities;
using JXReborn.ZoneEngine.PlayerSessions;
using JXReborn.ZoneEngine.SpatialGrid;
using JXReborn.ZoneEngine.WorldZones;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JXReborn.ClientDriver.Tests.WorldJourneys;

/// <summary>
/// Full headless <c>game.exe</c> E2E journey from Ba Lăng Huyện (<c>Map 53</c>) to Vũ Lăng Sơn (<c>Map 70</c>),
/// collecting live server and client diagnostics across town exit, 18-region wilderness travel, and cross-map portals.
/// </summary>
[Collection(HeadlessGameExeCollectionDefinition.Name)]
[Trait("Category", "Headless")]
public sealed class BaLangToVuLangSonJourneyTests
{
    private const ushort BaLangHuyenMapId = 53;
    private const ushort VuLangSonMapId = 70;
    private const ushort VuLangDongMapId = 199;
    private const uint SouthGateTrapId = 1968940038u;
    private const uint VuLangSonPortalTrapId = 4274748173u;

    private readonly ITestOutputHelper _output;

    public BaLangToVuLangSonJourneyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task RealGameExe_RunFromBaLangHuyenToVuLangSonPortal_ShouldTraverseSouthGateBounceAtLevelOneAndVerifyPortalTransitionBehavior()
    {
        var totalSw = Stopwatch.StartNew();
        await using var harness = await HeadlessTestServerHarness.StartAsync(excludedWorldSetMapIds: [VuLangSonMapId]);

        var mapCatalog = harness.Services.GetRequiredService<IMapCatalog>();
        var router = harness.Services.GetRequiredService<ZoneClusterRouter>();

        bool isMap53Loaded = router.IsMapLoaded(BaLangHuyenMapId);
        bool isMap54Loaded = router.IsMapLoaded(54);
        bool isMap70Loaded = router.IsMapLoaded(VuLangSonMapId);
        bool isMap74Loaded = router.IsMapLoaded(74);
        bool isMap199Loaded = router.IsMapLoaded(VuLangDongMapId);

        Assert.True(isMap53Loaded, "Map 53 (Ba Lăng Huyện) must be loaded.");
        Assert.True(router.TryGetWorker(BaLangHuyenMapId, out MapZoneWorker? map53Worker) && map53Worker is not null);

        WriteDiag($"[SERVER BOOT] BishopPort={harness.BishopPort}, GsPort={harness.GameServerPort}, ActiveMaps={mapCatalog.ActiveMaps.Count}, ActiveWorkers={router.Workers.Count}");
        WriteDiag($"[MAP AUDIT] Map53(BaLangHuyen)={isMap53Loaded}, Map54(NamNhacTran)={isMap54Loaded}, Map70(VuLangSon)={isMap70Loaded}, Map74(MieuLinh)={isMap74Loaded}, Map199(VuLangDong)={isMap199Loaded}");
        WriteDiag($"[MAP 53 ENTITIES] StaticNpcs={map53Worker.StaticNpcs.Count}");

        ClientPakObstacleReader? clientPakObstacles = ClientPakObstacleReader.TryOpenFromTemplate();
        Assert.NotNull(clientPakObstacles);
        (int auditedRegions, int mismatchedRegions, int mismatchedCells) = clientPakObstacles.AuditPassabilityAgainstServer(
            mapCatalog,
            BaLangHuyenMapId,
            leftRegionX: 82,
            topRegionY: 98,
            rightRegionX: 103,
            bottomRegionY: 105);
        WriteDiag(
            $"[CLIENT-SERVER MAP OBSTACLE AUDIT] CorridorRegions(82..103,98..105)={auditedRegions}, " +
            $"MismatchedRegions={mismatchedRegions}, MismatchedCells={mismatchedCells}");

        await using var client = await JxHeadlessClient.SpawnAsync(new JxHeadlessOptions
        {
            GatewayPort = harness.BishopPort
        });

        WorldDomSnapshot enteredSnap = await client.LoginAndEnterWorldAsync(
            "127.0.0.1",
            harness.BishopPort,
            account: "vls_journey1",
            password: "pass123",
            roleName: "HanhGiaVLS",
            gender: 0,
            series: 0,
            nativePlaceId: BaLangHuyenMapId);

        // Wait for initial AOI full-sync burst after c2s_syncend completes
        WorldDomSnapshot spawnSnap = await client.WaitUntilAsync(
            s => s.Npcs.Any(n => !n.IsSelf),
            TimeSpan.FromSeconds(5));

        Assert.True(spawnSnap.Player.InWorld);
        Assert.Equal(BaLangHuyenMapId, spawnSnap.Player.MapId);
        Assert.Equal(0, spawnSnap.Player.FightMode);

        PlayerZoneState? serverPlayer = map53Worker.PlayersByCharacterId.Values.FirstOrDefault(
            p => p.RuntimeNpcId == enteredSnap.Player.DwId);
        Assert.NotNull(serverPlayer);

        WriteDiag(
            $"[SPAWN] ClientPos=({spawnSnap.Player.MpsX},{spawnSnap.Player.MpsY}), ServerPos=({serverPlayer.CurrentMpsX},{serverPlayer.CurrentMpsY}), " +
            $"FightMode={spawnSnap.Player.FightMode}, Level={spawnSnap.Player.Level}, Life={spawnSnap.Player.Life}/{spawnSnap.Player.LifeMax}, " +
            $"Stamina(Client/Server)={spawnSnap.Player.Stamina}/{serverPlayer.CurrentStamina}, VisibleNpcs={spawnSnap.Npcs.Count(n => !n.IsSelf)}");

        string sampleTownNpcs = string.Join(", ", spawnSnap.Npcs.Where(n => !n.IsSelf).Take(10).Select(n => $"{n.Name}(kind={n.Kind},dw={n.DwId})"));
        WriteDiag($"[TOWN NPCS SAMPLE] {sampleTownNpcs}");

        // Telemetry accumulators
        uint maxSeenS2cSeq = spawnSnap.RecentS2c.Count > 0 ? spawnSnap.RecentS2c.Max(p => p.Seq) : 0u;
        int selfSnap0x4ECount = 0;
        int walkBroadcast0x50Count = 0;
        int runBroadcast0x51Count = 0;
        double maxClientServerDriftPx = 0.0;
        int minClientStamina = spawnSnap.Player.Stamina;
        int walkFallbackSegments = 0;
        int orphanedProcessAiRepairs = 0;
        int aoiDesyncZeroNpcEvents = 0;
        var distinctVisibleNpcs = new HashSet<uint>(spawnSnap.Npcs.Where(n => !n.IsSelf).Select(n => n.DwId));
        var distinctMonsterNames = new HashSet<string>(StringComparer.Ordinal);
        var distinctDialogNpcNames = new HashSet<string>(
            spawnSnap.Npcs.Where(n => !n.IsSelf && n.Kind == 3 && !string.IsNullOrWhiteSpace(n.Name)).Select(n => n.Name),
            StringComparer.Ordinal);

        // STAGE 1: Town Spawn -> Ba Lăng Huyện Nam Môn (South Gate trap 1968940038)
        IReadOnlyList<(int MpsX, int MpsY)> townToGateWaypoints = MapObstaclePathfinder.FindWaypointsToTrap(
            mapCatalog,
            BaLangHuyenMapId,
            spawnSnap.Player.MpsX,
            spawnSnap.Player.MpsY,
            SouthGateTrapId,
            clientPakObstacles);

        WriteDiag($"[STAGE 1: TOWN -> NAM MON] Planned {townToGateWaypoints.Count} waypoints from ({spawnSnap.Player.MpsX},{spawnSnap.Player.MpsY}) to trap {SouthGateTrapId} ({townToGateWaypoints[^1].MpsX},{townToGateWaypoints[^1].MpsY})");

        for (int i = 0; i < townToGateWaypoints.Count - 1; i++)
        {
            (int wx, int wy) = townToGateWaypoints[i];
            WorldDomSnapshot segSnap = await TraverseSegmentAsync(
                client,
                mapCatalog,
                clientPakObstacles,
                serverPlayer,
                _output,
                $"TownWP#{i + 1}",
                wx,
                wy,
                () => walkFallbackSegments++,
                () => orphanedProcessAiRepairs++);
            CollectTelemetry(
                segSnap,
                serverPlayer,
                map53Worker,
                ref maxSeenS2cSeq,
                ref selfSnap0x4ECount,
                ref walkBroadcast0x50Count,
                ref runBroadcast0x51Count,
                ref maxClientServerDriftPx,
                ref minClientStamina,
                ref aoiDesyncZeroNpcEvents,
                distinctVisibleNpcs,
                distinctMonsterNames,
                distinctDialogNpcNames);
        }

        // Final step onto South Gate trap (1968940038) — server may immediately teleport the client across the gate (FightMode = 1) via 0x4E
        (int gateTrapX, int gateTrapY) = townToGateWaypoints[^1];
        _ = await client.MoveToAsync(gateTrapX, gateTrapY, mode: 1);
        _ = await client.WaitUntilAsync(
            s => s.Player.FightMode == 1 || ComputeDistance(s.Player.MpsX, s.Player.MpsY, gateTrapX, gateTrapY) <= 40.0,
            TimeSpan.FromSeconds(8));

        bool serverToggledGate = SpinWait.SpinUntil(
            () => serverPlayer.FightMode == 1,
            TimeSpan.FromSeconds(5));
        Assert.True(serverToggledGate, $"Expected serverPlayer.FightMode == 1 after stepping onto South Gate ({gateTrapX},{gateTrapY}), actual ServerPos=({serverPlayer.CurrentMpsX},{serverPlayer.CurrentMpsY}), FightMode={serverPlayer.FightMode}.");

        WorldDomSnapshot afterGateSnap = await client.GetStateAsync();
        double gatePosDrift = ComputeDistance(
            afterGateSnap.Player.MpsX,
            afterGateSnap.Player.MpsY,
            serverPlayer.CurrentMpsX,
            serverPlayer.CurrentMpsY);

        CollectTelemetry(
            afterGateSnap,
            serverPlayer,
            map53Worker,
            ref maxSeenS2cSeq,
            ref selfSnap0x4ECount,
            ref walkBroadcast0x50Count,
            ref runBroadcast0x51Count,
            ref maxClientServerDriftPx,
            ref minClientStamina,
            ref aoiDesyncZeroNpcEvents,
            distinctVisibleNpcs,
            distinctMonsterNames,
            distinctDialogNpcNames);

        WriteDiag(
            $"[NAM MON CROSSED] ClientPos=({afterGateSnap.Player.MpsX},{afterGateSnap.Player.MpsY}) ClientFightMode={afterGateSnap.Player.FightMode} vs " +
            $"ServerPos=({serverPlayer.CurrentMpsX},{serverPlayer.CurrentMpsY}) ServerFightMode={serverPlayer.FightMode} (GateDrift={gatePosDrift:F1}px), " +
            $"Stamina(Client/Server)={afterGateSnap.Player.Stamina}/{serverPlayer.CurrentStamina}, 0x4ESnapsSoFar={selfSnap0x4ECount}");

        // Grant high HP pool in memory (never call OutboundSink from the xUnit thread, as _frameProcessor's KSG_EncodeBuf key is single-threaded!)
        // while preserving Level=1 so our unprovoked runner survives passive-mob AI aggro bugs along the 18-region road.
        const int journeyLifeCap = 30_000;
        serverPlayer.MaxLife = journeyLifeCap;
        serverPlayer.CurrentLife = journeyLifeCap;

        int lifeLossesObserved = 0;
        int totalUnprovokedDamageTaken = 0;
        void SustainRunnerLife()
        {
            int cur = serverPlayer.CurrentLife;
            if (cur < journeyLifeCap)
            {
                totalUnprovokedDamageTaken += journeyLifeCap - cur;
                lifeLossesObserved++;
                serverPlayer.CurrentLife = journeyLifeCap;
            }
        }

        // STAGE 2: Wilderness Road from South Gate -> Vũ Lăng Sơn Portal (trap 4274748173)
        IReadOnlyList<(int MpsX, int MpsY)> wildernessWaypoints = MapObstaclePathfinder.FindWaypointsToTrap(
            mapCatalog,
            BaLangHuyenMapId,
            afterGateSnap.Player.MpsX,
            afterGateSnap.Player.MpsY,
            VuLangSonPortalTrapId,
            clientPakObstacles,
            allowedPassThroughTrapId: SouthGateTrapId);

        WriteDiag($"[STAGE 2: NAM MON -> VU LANG SON PORTAL] Planned {wildernessWaypoints.Count} waypoints from ({afterGateSnap.Player.MpsX},{afterGateSnap.Player.MpsY}) to trap {VuLangSonPortalTrapId} ({wildernessWaypoints[^1].MpsX},{wildernessWaypoints[^1].MpsY})");

        int replanCount = 0;
        for (int i = 0; i < wildernessWaypoints.Count - 1; i++)
        {
            (int wx, int wy) = wildernessWaypoints[i];
            WorldDomSnapshot segSnap = await TraverseSegmentAsync(
                client,
                mapCatalog,
                clientPakObstacles,
                serverPlayer,
                _output,
                $"WildWP#{i + 1}",
                wx,
                wy,
                () => walkFallbackSegments++,
                () => orphanedProcessAiRepairs++,
                SustainRunnerLife);
            SustainRunnerLife();

            CollectTelemetry(
                segSnap,
                serverPlayer,
                map53Worker,
                ref maxSeenS2cSeq,
                ref selfSnap0x4ECount,
                ref walkBroadcast0x50Count,
                ref runBroadcast0x51Count,
                ref maxClientServerDriftPx,
                ref minClientStamina,
                ref aoiDesyncZeroNpcEvents,
                distinctVisibleNpcs,
                distinctMonsterNames,
                distinctDialogNpcNames);

            double wpDrift = ComputeDistance(segSnap.Player.MpsX, segSnap.Player.MpsY, serverPlayer.CurrentMpsX, serverPlayer.CurrentMpsY);
            if ((i + 1) % 8 == 0 || i == wildernessWaypoints.Count - 2)
            {
                int liveStam = Math.Min(segSnap.Player.Stamina, segSnap.Player.StaminaMax);
                WriteDiag(
                    $"  [Wilderness WP {i + 1}/{wildernessWaypoints.Count - 1}] Client=({segSnap.Player.MpsX},{segSnap.Player.MpsY}) Reg=({segSnap.Player.MpsX / 512},{segSnap.Player.MpsY / 1024}), " +
                    $"Server=({serverPlayer.CurrentMpsX},{serverPlayer.CurrentMpsY}) Drift={wpDrift:F1}px, Stamina=({segSnap.Player.Stamina},{segSnap.Player.StaminaMax}|live={liveStam})/{serverPlayer.CurrentStamina}, " +
                    $"VisibleNpcs={segSnap.Npcs.Count(n => !n.IsSelf)}, 0x4E={selfSnap0x4ECount}");
            }

            if (replanCount < 6 &&
                i >= 1 &&
                i < wildernessWaypoints.Count - 2 &&
                (wpDrift > 128.0 || ComputeDistance(serverPlayer.CurrentMpsX, serverPlayer.CurrentMpsY, wx, wy) > 96.0))
            {
                replanCount++;
                wildernessWaypoints = MapObstaclePathfinder.FindWaypointsToTrap(
                    mapCatalog,
                    BaLangHuyenMapId,
                    serverPlayer.CurrentMpsX,
                    serverPlayer.CurrentMpsY,
                    VuLangSonPortalTrapId,
                    clientPakObstacles,
                    allowedPassThroughTrapId: SouthGateTrapId);
                WriteDiag(
                    $"  [STAGE 2 REPLAN #{replanCount}] Re-anchored {wildernessWaypoints.Count} waypoints from authoritative server=({serverPlayer.CurrentMpsX},{serverPlayer.CurrentMpsY})");
                i = -1;
            }
        }

        // STAGE 3: Step onto Vũ Lăng Sơn Portal at Level 1 -> Expect server bounce-back + denied message (minLevel = 5)
        WorldDomSnapshot prePortalSnap = await client.GetStateAsync();
        int sysMsgCountBefore = prePortalSnap.SystemMessages.Count;
        (int portalTrapX, int portalTrapY) = wildernessWaypoints[^1];

        await IssueMoveAndTrackOrphanedAiAsync(client, portalTrapX, portalTrapY, mode: 1, () => orphanedProcessAiRepairs++);
        WorldDomSnapshot bouncedSnap = await client.WaitUntilAsync(
            s =>
            {
                SustainRunnerLife();
                return s.SystemMessages.Count > sysMsgCountBefore;
            },
            TimeSpan.FromSeconds(10));

        string bounceMessage = bouncedSnap.SystemMessages[^1].Text;
        double clientDistFromBounce = ComputeDistance(bouncedSnap.Player.MpsX, bouncedSnap.Player.MpsY, 42688, 105792);
        double serverDistFromBounce = ComputeDistance(serverPlayer.CurrentMpsX, serverPlayer.CurrentMpsY, 42688, 105792);
        double postBounceClientServerDrift = ComputeDistance(
            bouncedSnap.Player.MpsX,
            bouncedSnap.Player.MpsY,
            serverPlayer.CurrentMpsX,
            serverPlayer.CurrentMpsY);

        WriteDiag(
            $"[STAGE 3: LEVEL 1 PORTAL CHECK] SystemMsg='{bounceMessage}', " +
            $"ClientPos=({bouncedSnap.Player.MpsX},{bouncedSnap.Player.MpsY}) (distToBounce={clientDistFromBounce:F1}px) vs " +
            $"ServerPos=({serverPlayer.CurrentMpsX},{serverPlayer.CurrentMpsY}) (distToBounce={serverDistFromBounce:F1}px), " +
            $"ClientServerDrift={postBounceClientServerDrift:F1}px");

        Assert.Contains("Phía trước nguy hiểm", bounceMessage, StringComparison.Ordinal);
        Assert.InRange(serverDistFromBounce, 0.0, 16.0);
        Assert.Equal(BaLangHuyenMapId, bouncedSnap.Player.MapId);

        // STAGE 4: Promote character to Level 5, step off onto the non-trap approach cell, then step back onto Vũ Lăng Sơn Portal (Map 70)
        serverPlayer.Level = 5;
        (int approachBeforePortalX, int approachBeforePortalY) = wildernessWaypoints[^2];
        _ = await TraverseSegmentAsync(
            client,
            mapCatalog,
            clientPakObstacles,
            serverPlayer,
            _output,
            "ApproachBeforePortal",
            approachBeforePortalX,
            approachBeforePortalY,
            () => walkFallbackSegments++,
            () => orphanedProcessAiRepairs++,
            SustainRunnerLife);

        _ = SpinWait.SpinUntil(() => serverPlayer.LastTriggeredTrapId == 0u, TimeSpan.FromSeconds(3));

        await IssueMoveAndTrackOrphanedAiAsync(client, portalTrapX, portalTrapY, mode: 1, () => orphanedProcessAiRepairs++);
        _ = await client.WaitUntilAsync(
            s =>
            {
                SustainRunnerLife();
                return ComputeDistance(s.Player.MpsX, s.Player.MpsY, portalTrapX, portalTrapY) <= 48.0;
            },
            TimeSpan.FromSeconds(8));

        bool trapEvaluatedAtLevel5 = SpinWait.SpinUntil(
            () => serverPlayer.LastTriggeredTrapId == VuLangSonPortalTrapId || serverPlayer.MapId == VuLangSonMapId,
            TimeSpan.FromSeconds(5));
        Assert.True(trapEvaluatedAtLevel5, "Server should evaluate portal-to-map-70 trap after Level 5 player steps onto trigger cell.");

        WorldDomSnapshot afterLevel5PortalSnap = await client.GetStateAsync();
        IReadOnlyList<ServerLogEntry> logsAfterMap70Attempt = harness.LogCollector.SnapshotEntries();
        int warnOrErrorLogs = logsAfterMap70Attempt.Count(e => e.Level >= LogLevel.Warning);

        WriteDiag(
            $"[STAGE 4: LEVEL 5 PORTAL TO MAP 70] IsMap70Loaded={isMap70Loaded}, ClientMapId={afterLevel5PortalSnap.Player.MapId}, " +
            $"ServerMapId={serverPlayer.MapId}, LastTriggeredTrapId={serverPlayer.LastTriggeredTrapId}, " +
            $"ClientPos=({afterLevel5PortalSnap.Player.MpsX},{afterLevel5PortalSnap.Player.MpsY}), ServerWarnOrErrorLogs={warnOrErrorLogs}");

        Assert.False(isMap70Loaded);
        Assert.Equal(BaLangHuyenMapId, afterLevel5PortalSnap.Player.MapId);
        Assert.Equal(VuLangSonPortalTrapId, serverPlayer.LastTriggeredTrapId);

        // STAGE 5: Verify same-GS cross-map transition to a LOADED map (Map 199 - Vũ Lăng Động, also linked from Map 53)
        // Dispatch strictly onto map53Worker's single-reader command channel so _regionGrid and _frameProcessor are never mutated off-thread.
        var teleportTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool enqueuedTeleport = map53Worker.TryEnqueueCommand(new WorkerSafeTeleportZoneCommand(
            serverPlayer,
            TargetMapId: VuLangDongMapId,
            TargetMpsX: 52608,
            TargetMpsY: 92992,
            TargetFightMode: 1,
            Completion: teleportTcs));
        Assert.True(enqueuedTeleport, "Should enqueue WorkerSafeTeleportZoneCommand onto Map 53 worker channel.");
        bool transferredToLoadedMap199 = await teleportTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(transferredToLoadedMap199, "Teleport to loaded Map 199 (Vũ Lăng Động) should succeed.");

        WorldDomSnapshot map199Snap = await client.WaitUntilAsync(
            s =>
            {
                SustainRunnerLife();
                return s.Player.MapId == VuLangDongMapId && s.Player.RegionLoaded && s.Player.MpsX > 0;
            },
            TimeSpan.FromSeconds(10));

        WriteDiag(
            $"[STAGE 5: LOADED CROSS-MAP CHECK (MAP 199 VU LANG DONG)] ClientMapId={map199Snap.Player.MapId}, InWorld={map199Snap.Player.InWorld}, " +
            $"SyncReady={map199Snap.Player.SyncReady}, RegionLoaded={map199Snap.Player.RegionLoaded}, " +
            $"ClientPos=({map199Snap.Player.MpsX},{map199Snap.Player.MpsY}), ClientFightMode={map199Snap.Player.FightMode}, VisibleNpcs={map199Snap.Npcs.Count(n => !n.IsSelf)}");

        Assert.Equal(VuLangDongMapId, map199Snap.Player.MapId);
        Assert.True(map199Snap.Player.InWorld);

        IReadOnlyList<ServerLogEntry> allLogs = harness.LogCollector.SnapshotEntries();
        WriteDiag(
            $"[SUMMARY TELEMETRY] Elapsed={totalSw.Elapsed.TotalSeconds:F1}s, DistinctNpcsSeen={distinctVisibleNpcs.Count}, " +
            $"DialogNpcs=[{string.Join(", ", distinctDialogNpcNames.Take(12))}], " +
            $"MonsterNames=[{string.Join(", ", distinctMonsterNames.Take(12))}], " +
            $"MinClientStamina={minClientStamina}, WalkFallbacks={walkFallbackSegments}, OrphanedProcessAiRepairs={orphanedProcessAiRepairs}, " +
            $"Total0x4ESelfSnaps={selfSnap0x4ECount}, Total0x50Walk={walkBroadcast0x50Count}, Total0x51Run={runBroadcast0x51Count}, MaxClientServerDriftPx={maxClientServerDriftPx:F1}, " +
            $"AoiZeroNpcEvents={aoiDesyncZeroNpcEvents}, UnprovokedPassiveMobHits={lifeLossesObserved} (TotalDmg={totalUnprovokedDamageTaken}), " +
            $"TotalServerLogs={allLogs.Count} (Warn+Error={allLogs.Count(l => l.Level >= LogLevel.Warning)})");

        foreach (ServerLogEntry log in allLogs.Where(l => l.Level >= LogLevel.Warning).Take(10))
        {
            WriteDiag($"  [SERVER {log.Level}] {log.Category}: {log.Message}");
        }
    }

    [Fact]
    public async Task RealGameExe_CrossMapTransitionToVuLangDong_ShouldTransferToLoadedCaveMapCleanly()
    {
        await using var harness = await HeadlessTestServerHarness.StartAsync();
        var router = harness.Services.GetRequiredService<ZoneClusterRouter>();
        Assert.True(router.TryGetWorker(BaLangHuyenMapId, out MapZoneWorker? map53Worker) && map53Worker is not null);
        Assert.True(router.TryGetWorker(VuLangDongMapId, out MapZoneWorker? map199Worker) && map199Worker is not null);

        await using var client = await JxHeadlessClient.SpawnAsync(new JxHeadlessOptions
        {
            GatewayPort = harness.BishopPort
        });

        WorldDomSnapshot entered = await client.LoginAndEnterWorldAsync(
            "127.0.0.1",
            harness.BishopPort,
            account: "vls_cave1",
            password: "pass123",
            roleName: "ThamDongVLS",
            gender: 0,
            series: 0,
            nativePlaceId: BaLangHuyenMapId);

        PlayerZoneState? serverPlayer = map53Worker.PlayersByCharacterId.Values.FirstOrDefault(
            p => p.RuntimeNpcId == entered.Player.DwId);
        Assert.NotNull(serverPlayer);

        int oldRegionX = serverPlayer.RegionX;
        int oldRegionY = serverPlayer.RegionY;
        serverPlayer.MaxLife = 30_000;
        serverPlayer.CurrentLife = 30_000;

        var teleportTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(map53Worker.TryEnqueueCommand(new WorkerSafeTeleportZoneCommand(
            serverPlayer,
            TargetMapId: VuLangDongMapId,
            TargetMpsX: 52608,
            TargetMpsY: 92992,
            TargetFightMode: 1,
            Completion: teleportTcs)));

        bool transferred = await teleportTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(transferred);

        WorldDomSnapshot caveSnap = await client.WaitUntilAsync(
            s => s.Player.MapId == VuLangDongMapId && s.Player.RegionLoaded,
            TimeSpan.FromSeconds(10));

        bool ghostLeftInSourceRegion = map53Worker.RegionGrid.TryGetRegion(oldRegionX, oldRegionY, out SpatialRegionSlot? oldSlot)
            && oldSlot is not null
            && oldSlot.Players.Contains(serverPlayer);

        WriteDiag(
            $"[CAVE TRANSITION CHECK] ClientMapId={caveSnap.Player.MapId}, ClientPos=({caveSnap.Player.MpsX},{caveSnap.Player.MpsY}), " +
            $"FightMode={caveSnap.Player.FightMode}, VisibleCaveNpcs={caveSnap.Npcs.Count(n => !n.IsSelf)}, " +
            $"Map199Attached={map199Worker.PlayersByCharacterId.ContainsKey(serverPlayer.CharacterId)}, " +
            $"GhostResidualInMap53Region={ghostLeftInSourceRegion}");

        Assert.Equal(VuLangDongMapId, caveSnap.Player.MapId);
        Assert.True(caveSnap.Player.InWorld);
        Assert.False(ghostLeftInSourceRegion, "Source Map 53 region still retains ghost PlayerZoneState.");
        Assert.True(map199Worker.PlayersByCharacterId.ContainsKey(serverPlayer.CharacterId), "Player must be attached to Map 199 worker.");
    }

    [Fact]
    public async Task RealGameExe_WalkPastBaLangSignboardToVuLangSon_ShouldTransferToMap70()
    {
        await using var harness = await HeadlessTestServerHarness.StartAsync();
        var router = harness.Services.GetRequiredService<ZoneClusterRouter>();
        Assert.True(router.TryGetWorker(BaLangHuyenMapId, out MapZoneWorker? map53Worker) && map53Worker is not null);
        Assert.True(router.TryGetWorker(VuLangSonMapId, out MapZoneWorker? map70Worker) && map70Worker is not null);

        await using var client = await JxHeadlessClient.SpawnAsync(new JxHeadlessOptions
        {
            GatewayPort = harness.BishopPort
        });

        WorldDomSnapshot entered = await client.LoginAndEnterWorldAsync(
            "127.0.0.1",
            harness.BishopPort,
            account: "vls_walk1",
            password: "pass123",
            roleName: "WalkToVLS",
            gender: 0,
            series: 0,
            nativePlaceId: BaLangHuyenMapId);

        PlayerZoneState? serverPlayer = map53Worker.PlayersByCharacterId.Values.FirstOrDefault(
            p => p.RuntimeNpcId == entered.Player.DwId);
        Assert.NotNull(serverPlayer);

        serverPlayer.Level = 10;
        serverPlayer.MaxLife = 30_000;
        serverPlayer.CurrentLife = 30_000;

        // Teleport onto the road right beside the "Ba Lăng huyện<---->Vũ Lăng sơn" signboard (43188, 105587)
        var teleportTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(map53Worker.TryEnqueueCommand(new WorkerSafeTeleportZoneCommand(
            serverPlayer,
            TargetMapId: BaLangHuyenMapId,
            TargetMpsX: 43100,
            TargetMpsY: 105792,
            TargetFightMode: 1,
            Completion: teleportTcs)));
        Assert.True(await teleportTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        _ = await client.WaitUntilAsync(
            s => ComputeDistance(s.Player.MpsX, s.Player.MpsY, 43100, 105792) <= 64.0,
            TimeSpan.FromSeconds(5));

        // Walk/run west across region (83, 103) portal-to-vu-lang-son (X=[42528..42720], Y=[105536..106240])
        _ = await client.MoveToAsync(42400, 105792, mode: 1);

        WorldDomSnapshot vlsSnap = await client.WaitUntilAsync(
            s => s.Player.MapId == VuLangSonMapId && s.Player.RegionLoaded && s.Player.MpsX > 0,
            TimeSpan.FromSeconds(10));

        WriteDiag(
            $"[VLS WALK CHECK] ClientMapId={vlsSnap.Player.MapId}, ClientPos=({vlsSnap.Player.MpsX},{vlsSnap.Player.MpsY}), " +
            $"ServerMapId={serverPlayer.MapId}, ServerPos=({serverPlayer.CurrentMpsX},{serverPlayer.CurrentMpsY}), " +
            $"FightMode={vlsSnap.Player.FightMode}");

        Assert.Equal(VuLangSonMapId, vlsSnap.Player.MapId);
        Assert.True(map70Worker.PlayersByCharacterId.ContainsKey(serverPlayer.CharacterId));
    }

    [Fact]
    public async Task RealGameExe_BaLangOutsideMonsters_ShouldRetainFullLifeAcross0x4DSync_AndInterpolateSmoothWalkVia0x50()
    {
        await using var harness = await HeadlessTestServerHarness.StartAsync();
        var mapCatalog = harness.Services.GetRequiredService<IMapCatalog>();
        var router = harness.Services.GetRequiredService<ZoneClusterRouter>();
        Assert.True(router.TryGetWorker(BaLangHuyenMapId, out MapZoneWorker? map53Worker) && map53Worker is not null);

        // Select a walkable outside-town monster surrounded by multiple patrol-capable monsters in Map 53
        List<NpcZoneState> patrolMonsters = map53Worker.StaticNpcs.Values
            .Where(n => n.Kind == EntityKind.Monster && n.CanPatrol && mapCatalog.GetCellObstacle(BaLangHuyenMapId, n.SpawnMpsX, n.SpawnMpsY) == 0)
            .ToList();
        Assert.NotEmpty(patrolMonsters);

        NpcZoneState anchorMonster = patrolMonsters
            .OrderByDescending(anchor => patrolMonsters.Count(other =>
                ComputeDistance(anchor.SpawnMpsX, anchor.SpawnMpsY, other.SpawnMpsX, other.SpawnMpsY) <= 450.0))
            .First();

        (int landingX, int landingY) = FindNearbyWalkablePoint(
            mapCatalog,
            BaLangHuyenMapId,
            anchorMonster.SpawnMpsX,
            anchorMonster.SpawnMpsY);

        await using var client = await JxHeadlessClient.SpawnAsync(new JxHeadlessOptions
        {
            GatewayPort = harness.BishopPort
        });

        WorldDomSnapshot enteredSnap = await client.LoginAndEnterWorldAsync(
            "127.0.0.1",
            harness.BishopPort,
            account: "bl_mon_sync1",
            password: "pass123",
            roleName: "KiemTraQuai",
            gender: 0,
            series: 0,
            nativePlaceId: BaLangHuyenMapId);

        PlayerZoneState? serverPlayer = map53Worker.PlayersByCharacterId.Values.FirstOrDefault(
            p => p.RuntimeNpcId == enteredSnap.Player.DwId);
        Assert.NotNull(serverPlayer);

        var teleportTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(map53Worker.TryEnqueueCommand(new WorkerSafeTeleportZoneCommand(
            serverPlayer,
            TargetMapId: BaLangHuyenMapId,
            TargetMpsX: landingX,
            TargetMpsY: landingY,
            TargetFightMode: 0,
            Completion: teleportTcs)));
        Assert.True(await teleportTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        WorldDomSnapshot readySnap = await client.WaitUntilAsync(
            s => s.Player.MapId == BaLangHuyenMapId &&
                 s.Player.RegionLoaded &&
                 ComputeDistance(s.Player.MpsX, s.Player.MpsY, landingX, landingY) <= 64.0 &&
                 s.Npcs.Any(n => !n.IsSelf && n.Kind == 0 && n.MpsX > 0 && n.MpsY > 0),
            TimeSpan.FromSeconds(10));

        uint maxSeenSeq = readySnap.RecentS2c.Count > 0 ? readySnap.RecentS2c.Max(p => p.Seq) : 0u;
        int count0x4D = 0;
        int count0x50 = 0;
        int hpViolations = 0;
        string? firstHpViolationDetail = null;
        int largeSingleStepJumps = 0;
        double maxObservedStepJumpPx = 0.0;

        var lastPosByMonster = new Dictionary<uint, (int X, int Y, int Doing)>();
        var walkPositionsByMonster = new Dictionary<uint, List<(int X, int Y)>>();
        var monstersSeenWalking = new HashSet<uint>();

        var pollSw = Stopwatch.StartNew();
        while (pollSw.Elapsed < TimeSpan.FromSeconds(8))
        {
            WorldDomSnapshot snap = await client.GetStateAsync();

            foreach (S2cPacketSnapshot pkt in snap.RecentS2c)
            {
                if (pkt.Seq <= maxSeenSeq)
                {
                    continue;
                }

                maxSeenSeq = pkt.Seq;
                if (pkt.Opcode == ProtocolOpcodes.s2c_syncnpcmin)
                {
                    count0x4D++;
                }
                else if (pkt.Opcode == ProtocolOpcodes.s2c_npcwalk)
                {
                    count0x50++;
                }
            }

            foreach (NpcSnapshot npc in snap.Npcs)
            {
                if (npc.IsSelf || npc.Kind != 0)
                {
                    continue;
                }

                // Skip transient (0,0) coordinates when game.exe has RegionIndex < 0 (not yet attached or exited 9-region AOI).
                if (npc.MpsX <= 0 || npc.MpsY <= 0)
                {
                    lastPosByMonster.Remove(npc.DwId);
                    continue;
                }

                if (npc.LifeMax > 0 && npc.Life != npc.LifeMax)
                {
                    hpViolations++;
                    firstHpViolationDetail ??= $"NpcDwId={npc.DwId} ({npc.Name}) Life={npc.Life}/{npc.LifeMax}, Doing={npc.Doing} at ({npc.MpsX},{npc.MpsY})";
                }

                if (npc.Doing == 2)
                {
                    monstersSeenWalking.Add(npc.DwId);
                }

                if (lastPosByMonster.TryGetValue(npc.DwId, out (int X, int Y, int Doing) prev))
                {
                    double stepPx = ComputeDistance(prev.X, prev.Y, npc.MpsX, npc.MpsY);
                    if (stepPx > maxObservedStepJumpPx)
                    {
                        maxObservedStepJumpPx = stepPx;
                    }

                    if (stepPx > 96.0)
                    {
                        largeSingleStepJumps++;
                    }

                    if (stepPx > 0.5 && npc.Doing == 2)
                    {
                        if (!walkPositionsByMonster.TryGetValue(npc.DwId, out List<(int X, int Y)>? path))
                        {
                            path = [(prev.X, prev.Y)];
                            walkPositionsByMonster[npc.DwId] = path;
                        }

                        path.Add((npc.MpsX, npc.MpsY));
                    }
                }

                lastPosByMonster[npc.DwId] = (npc.MpsX, npc.MpsY, npc.Doing);
            }

            uint prevTick = snap.TickCount;
            _ = await client.WaitUntilAsync(s => s.TickCount >= prevTick + 2u, TimeSpan.FromSeconds(3));
        }

        int maxWalkFramesForSingleMonster = walkPositionsByMonster.Count > 0
            ? walkPositionsByMonster.Values.Max(static p => p.Count)
            : 0;

        WriteDiag(
            $"[OUTSIDE MONSTER QA] Anchor={anchorMonster.Name}({anchorMonster.NpcId}) Landing=({landingX},{landingY}), " +
            $"VisibleMonsters={lastPosByMonster.Count}, Seen0x4D={count0x4D}, Seen0x50={count0x50}, " +
            $"HpViolations={hpViolations}, WalkingMonsters={monstersSeenWalking.Count}, " +
            $"MaxWalkSamples={maxWalkFramesForSingleMonster}, MaxStepJumpPx={maxObservedStepJumpPx:F1}, LargeJumps={largeSingleStepJumps}");

        Assert.True(count0x4D > 0, $"Expected periodic 0x4D (s2c_syncnpcmin) syncs within 8s outside Ba Lăng Huyện, got {count0x4D}.");
        Assert.True(count0x50 > 0, $"Expected 0x50 (s2c_npcwalk) patrol broadcasts within 8s outside Ba Lăng Huyện, got {count0x50}.");
        Assert.True(hpViolations == 0, $"Monster full-HP mismatch after 0x4D sync: {firstHpViolationDetail}");
        Assert.True(monstersSeenWalking.Count > 0, "Expected at least one monster in game.exe memory to enter Doing=Walk (2) upon receiving 0x50.");
        Assert.True(maxWalkFramesForSingleMonster >= 3, $"Expected game.exe to interpolate smooth intermediate walk positions (>=3 samples), got {maxWalkFramesForSingleMonster}.");
        Assert.Equal(0, largeSingleStepJumps);
    }

    private static (int X, int Y) FindNearbyWalkablePoint(IMapCatalog mapCatalog, ushort mapId, int centerX, int centerY)
    {
        (int Dx, int Dy)[] offsets = [(0, 96), (96, 0), (0, -96), (-96, 0), (64, 64), (-64, 64), (0, 0)];
        foreach ((int dx, int dy) in offsets)
        {
            int x = centerX + dx;
            int y = centerY + dy;
            if (x > 0 && y > 0 && mapCatalog.GetCellObstacle(mapId, x, y) == 0)
            {
                return (x, y);
            }
        }

        return (centerX, centerY);
    }

    private void WriteDiag(string message)
    {
        _output.WriteLine(message);
    }

    private static async Task IssueMoveAndTrackOrphanedAiAsync(
        JxHeadlessClient client,
        int targetMpsX,
        int targetMpsY,
        int mode,
        Action onOrphanedAiRepaired)
    {
        JsonElement res = await client.MoveToAsync(targetMpsX, targetMpsY, mode);
        if (res.ValueKind == JsonValueKind.Object &&
            res.TryGetProperty("repaired_orphaned_process_ai", out JsonElement repairedElem) &&
            repairedElem.ValueKind == JsonValueKind.True)
        {
            onOrphanedAiRepaired();
        }
    }

    private static async Task<(WorldDomSnapshot Snapshot, bool UsedWalkFallback)> MoveSingleWaypointAsync(
        JxHeadlessClient client,
        IMapCatalog mapCatalog,
        ClientPakObstacleReader clientPakObstacles,
        PlayerZoneState serverPlayer,
        ITestOutputHelper output,
        string segmentLabel,
        int targetMpsX,
        int targetMpsY,
        Action onOrphanedAiRepaired,
        Action? sustainLife)
    {
        WorldDomSnapshot before = await client.GetStateAsync();
        double initialDist = ComputeDistance(before.Player.MpsX, before.Player.MpsY, targetMpsX, targetMpsY);
        double initialServerDist = ComputeDistance(serverPlayer.CurrentMpsX, serverPlayer.CurrentMpsY, targetMpsX, targetMpsY);
        if (initialDist <= 44.0 && initialServerDist <= 44.0)
        {
            return (before, false);
        }

        int liveClientStamina = Math.Min(before.Player.Stamina, before.Player.StaminaMax);
        int preferredMode = liveClientStamina > 8 ? 2 : 1;
        bool usedFallback = preferredMode == 1;
        await IssueMoveAndTrackOrphanedAiAsync(client, targetMpsX, targetMpsY, preferredMode, onOrphanedAiRepaired);

        if (initialDist <= 56.0 && initialServerDist > 44.0)
        {
            bool seenServerMove = false;
            _ = SpinWait.SpinUntil(
                () =>
                {
                    sustainLife?.Invoke();
                    if (serverPlayer.Doing != JXReborn.Primitives.WorldZones.EntityDoingPosture.Stand)
                    {
                        seenServerMove = true;
                    }

                    return ComputeDistance(serverPlayer.CurrentMpsX, serverPlayer.CurrentMpsY, targetMpsX, targetMpsY) <= 44.0 ||
                           (seenServerMove && serverPlayer.Doing == JXReborn.Primitives.WorldZones.EntityDoingPosture.Stand);
                },
                TimeSpan.FromSeconds(3));
            WorldDomSnapshot synced = await client.GetStateAsync();
            return (synced, usedFallback);
        }

        WorldDomSnapshot firstOutcome = await WaitMoveOrStallAsync(
            client,
            before,
            targetMpsX,
            targetMpsY,
            tolerancePx: 60.0,
            stallTicks: 18u,
            timeoutSec: 6,
            sustainLife);
        if (ComputeDistance(firstOutcome.Player.MpsX, firstOutcome.Player.MpsY, targetMpsX, targetMpsY) <= 60.0)
        {
            _ = SpinWait.SpinUntil(
                () =>
                {
                    sustainLife?.Invoke();
                    return ComputeDistance(serverPlayer.CurrentMpsX, serverPlayer.CurrentMpsY, targetMpsX, targetMpsY) <= 48.0 ||
                           serverPlayer.Doing == JXReborn.Primitives.WorldZones.EntityDoingPosture.Stand;
                },
                TimeSpan.FromSeconds(2));
            return (firstOutcome, usedFallback);
        }

        // If a monster hit (s2c_npchurt 0x56 -> do_hurt 9 in game.exe) interrupted movement or s2c_syncpos/0x4E orphaned m_ProcessAI == 0,
        // wait out any active hurt frames and re-issue MoveTo before attempting lateral obstacle detours.
        for (int retry = 0;
             retry < 5 &&
             (firstOutcome.Player.DesX != targetMpsX ||
              firstOutcome.Player.DesY != targetMpsY ||
              firstOutcome.Player.Doing == 1 ||
              firstOutcome.Player.Doing == 4 ||
              firstOutcome.Player.Doing == 9 ||
              firstOutcome.Player.ProcessAi == 0);
             retry++)
        {
            sustainLife?.Invoke();
            if (firstOutcome.Player.Doing is 4 or 9)
            {
                firstOutcome = await client.WaitUntilAsync(
                    s =>
                    {
                        sustainLife?.Invoke();
                        return s.Player.Doing is not (4 or 9) || s.TickCount >= firstOutcome.TickCount + 12u;
                    },
                    TimeSpan.FromSeconds(3));
            }

            int jitterX = targetMpsX + ((retry % 2 == 0) ? 1 : -1);
            int retryMode = Math.Min(firstOutcome.Player.Stamina, firstOutcome.Player.StaminaMax) > 8 ? 2 : 1;
            await IssueMoveAndTrackOrphanedAiAsync(client, jitterX, targetMpsY, retryMode, onOrphanedAiRepaired);
            firstOutcome = await WaitMoveOrStallAsync(
                client,
                firstOutcome,
                jitterX,
                targetMpsY,
                tolerancePx: 60.0,
                stallTicks: 18u,
                timeoutSec: 6,
                sustainLife);
            if (ComputeDistance(firstOutcome.Player.MpsX, firstOutcome.Player.MpsY, targetMpsX, targetMpsY) <= 64.0)
            {
                return (firstOutcome, usedFallback);
            }
        }

        // If the authoritative server has already cleanly reached this waypoint while client stopped within ~120px with lock == 0
        // (sub-step arrival or KNpcFindPath lookahead across a region seam), never drag the server backwards off the road with a lateral detour;
        // nudging 1px resets KNpcFindPath::GetDir (0x005F589C) and lets the next road waypoint pull both forward cleanly.
        if (firstOutcome.Player.LockMove == 0 &&
            ComputeDistance(serverPlayer.CurrentMpsX, serverPlayer.CurrentMpsY, targetMpsX, targetMpsY) <= 48.0 &&
            ComputeDistance(firstOutcome.Player.MpsX, firstOutcome.Player.MpsY, targetMpsX, targetMpsY) <= 128.0)
        {
            int nudgeMode = Math.Min(firstOutcome.Player.Stamina, firstOutcome.Player.StaminaMax) > 8 ? 2 : 1;
            await IssueMoveAndTrackOrphanedAiAsync(client, targetMpsX + 1, targetMpsY, nudgeMode, onOrphanedAiRepaired);
            firstOutcome = await WaitMoveOrStallAsync(
                client,
                firstOutcome,
                targetMpsX + 1,
                targetMpsY,
                tolerancePx: 64.0,
                stallTicks: 14u,
                timeoutSec: 4,
                sustainLife);
            if (ComputeDistance(firstOutcome.Player.MpsX, firstOutcome.Player.MpsY, targetMpsX, targetMpsY) <= 128.0 &&
                ComputeDistance(serverPlayer.CurrentMpsX, serverPlayer.CurrentMpsY, targetMpsX, targetMpsY) <= 56.0)
            {
                return (firstOutcome, usedFallback);
            }
        }

        usedFallback = true;
        NpcSnapshot? blockingNpc = firstOutcome.Npcs
            .Where(n => !n.IsSelf)
            .OrderBy(n => ComputeDistance(firstOutcome.Player.MpsX, firstOutcome.Player.MpsY, n.MpsX, n.MpsY))
            .FirstOrDefault();
        string nearestNpcDesc = blockingNpc is null
            ? "none"
            : $"{blockingNpc.Name}(kind={blockingNpc.Kind},pos={blockingNpc.MpsX},{blockingNpc.MpsY})";

        output.WriteLine(
            $"  [MOVEMENT STALL] {segmentLabel}: target=({targetMpsX},{targetMpsY}), " +
            $"client=({firstOutcome.Player.MpsX},{firstOutcome.Player.MpsY}) des=({firstOutcome.Player.DesX},{firstOutcome.Player.DesY}) doing={firstOutcome.Player.Doing} procAi={firstOutcome.Player.ProcessAi} lock={firstOutcome.Player.LockMove} pause={firstOutcome.Player.PauseSend} nodeRef={firstOutcome.Player.NpcNodeRef} noMove={firstOutcome.Player.NoMoveByte} freeze={firstOutcome.Player.FreezeTime} " +
            $"regIdx={firstOutcome.Player.RegionIndex}(id=0x{firstOutcome.Player.RegionDwId:X8},conL={firstOutcome.Player.RegionConnectLeft},swCenter={firstOutcome.Player.SubWorldCenterSlot}) regLoaded={firstOutcome.Player.RegionLoaded} " +
            $"sceneFocus=({firstOutcome.Player.SceneFocusRx},{firstOutcome.Player.SceneFocusRy}) loadQ={firstOutcome.Player.SceneFirstToLoad} procMask=0x{firstOutcome.Player.SceneProcMask:X3} stam=({firstOutcome.Player.Stamina},{firstOutcome.Player.StaminaMax}), " +
            $"server=({serverPlayer.CurrentMpsX},{serverPlayer.CurrentMpsY}), nearestNpc={nearestNpcDesc}");

        // Anchor multi-scale box-bypass detours relative to firstOutcome so dynamic NPC blockers or client-only props are cleanly skirted.
        double dx = targetMpsX - firstOutcome.Player.MpsX;
        double dy = targetMpsY - firstOutcome.Player.MpsY;
        double len = Math.Max(1.0, Math.Sqrt((dx * dx) + (dy * dy)));
        double ux = dx / len;
        double uy = dy / len;

        double[] lateralRadii = [48.0, -48.0, 96.0, -96.0, 144.0, -144.0, 192.0, -192.0];
        WorldDomSnapshot current = firstOutcome;

        foreach (double radius in lateralRadii)
        {
            sustainLife?.Invoke();
            double ox = -uy * radius;
            double oy = ux * radius;
            int lateralX = (int)Math.Round(firstOutcome.Player.MpsX - (ux * 64.0) + ox);
            int lateralY = (int)Math.Round(firstOutcome.Player.MpsY - (uy * 64.0) + oy);
            int forwardBypassX = (int)Math.Round(targetMpsX + ox);
            int forwardBypassY = (int)Math.Round(targetMpsY + oy);

            bool lateralDestOpen = IsDetourPointPassable(mapCatalog, clientPakObstacles, BaLangHuyenMapId, lateralX, lateralY);
            bool forwardDestOpen = IsDetourPointPassable(mapCatalog, clientPakObstacles, BaLangHuyenMapId, forwardBypassX, forwardBypassY);
            bool canStepLateral = lateralDestOpen &&
                                  forwardDestOpen &&
                                  IsServerChordStrictlyPassable(mapCatalog, BaLangHuyenMapId, serverPlayer.CurrentMpsX, serverPlayer.CurrentMpsY, lateralX, lateralY) &&
                                  IsServerChordStrictlyPassable(mapCatalog, BaLangHuyenMapId, lateralX, lateralY, forwardBypassX, forwardBypassY) &&
                                  IsJointSegmentPassableFromInterior(mapCatalog, clientPakObstacles, BaLangHuyenMapId, lateralX, lateralY, forwardBypassX, forwardBypassY);
            bool canStepDiagonalForward = !canStepLateral &&
                                          forwardDestOpen &&
                                          IsServerChordStrictlyPassable(mapCatalog, BaLangHuyenMapId, serverPlayer.CurrentMpsX, serverPlayer.CurrentMpsY, forwardBypassX, forwardBypassY) &&
                                          IsJointSegmentPassableFromInterior(mapCatalog, clientPakObstacles, BaLangHuyenMapId, current.Player.MpsX, current.Player.MpsY, forwardBypassX, forwardBypassY);
            if (!canStepLateral && !canStepDiagonalForward)
            {
                continue;
            }

            int moveMode;
            if (canStepLateral)
            {
                moveMode = Math.Min(current.Player.Stamina, current.Player.StaminaMax) > 8 ? 2 : 1;
                await IssueMoveAndTrackOrphanedAiAsync(client, lateralX, lateralY, moveMode, onOrphanedAiRepaired);
                current = await WaitMoveOrStallAsync(client, current, lateralX, lateralY, tolerancePx: 48.0, stallTicks: 14u, timeoutSec: 5, sustainLife);
            }

            if (forwardDestOpen)
            {
                moveMode = Math.Min(current.Player.Stamina, current.Player.StaminaMax) > 8 ? 2 : 1;
                await IssueMoveAndTrackOrphanedAiAsync(client, forwardBypassX, forwardBypassY, moveMode, onOrphanedAiRepaired);
                current = await WaitMoveOrStallAsync(client, current, forwardBypassX, forwardBypassY, tolerancePx: 52.0, stallTicks: 18u, timeoutSec: 6, sustainLife);
            }

            bool bypassedForward = ComputeDistance(current.Player.MpsX, current.Player.MpsY, forwardBypassX, forwardBypassY) <= 64.0;

            moveMode = Math.Min(current.Player.Stamina, current.Player.StaminaMax) > 8 ? 2 : 1;
            await IssueMoveAndTrackOrphanedAiAsync(client, targetMpsX, targetMpsY, moveMode, onOrphanedAiRepaired);
            WorldDomSnapshot targetAttempt = await WaitMoveOrStallAsync(client, current, targetMpsX, targetMpsY, tolerancePx: 56.0, stallTicks: 14u, timeoutSec: 5, sustainLife);
            if (ComputeDistance(targetAttempt.Player.MpsX, targetAttempt.Player.MpsY, targetMpsX, targetMpsY) <= 56.0)
            {
                return (targetAttempt, true);
            }

            current = targetAttempt;

            if (bypassedForward)
            {
                if (ComputeDistance(current.Player.MpsX, current.Player.MpsY, forwardBypassX, forwardBypassY) > 64.0)
                {
                    moveMode = Math.Min(current.Player.Stamina, current.Player.StaminaMax) > 8 ? 2 : 1;
                    await IssueMoveAndTrackOrphanedAiAsync(client, forwardBypassX, forwardBypassY, moveMode, onOrphanedAiRepaired);
                    current = await WaitMoveOrStallAsync(client, current, forwardBypassX, forwardBypassY, tolerancePx: 56.0, stallTicks: 14u, timeoutSec: 5, sustainLife);
                }

                output.WriteLine(
                    $"    [BYPASS ACCEPTED] {segmentLabel}: target ({targetMpsX},{targetMpsY}) blocked dynamically/locally; continuing from ({current.Player.MpsX},{current.Player.MpsY})");
                return (current, true);
            }
        }

        int finalMode = Math.Min(current.Player.Stamina, current.Player.StaminaMax) > 8 ? 2 : 1;
        await IssueMoveAndTrackOrphanedAiAsync(client, targetMpsX, targetMpsY, finalMode, onOrphanedAiRepaired);
        WorldDomSnapshot finalWalk = await client.WaitUntilAsync(
            s =>
            {
                sustainLife?.Invoke();
                return ComputeDistance(s.Player.MpsX, s.Player.MpsY, targetMpsX, targetMpsY) <= 80.0;
            },
            TimeSpan.FromSeconds(8));
        return (finalWalk, true);
    }

    private static bool IsDetourPointPassable(
        IMapCatalog mapCatalog,
        ClientPakObstacleReader clientPakObstacles,
        ushort mapId,
        int x,
        int y) =>
        x >= 0 &&
        y >= 0 &&
        (mapCatalog.GetCellTrapId(mapId, x, y) != 0 || mapCatalog.GetCellObstacle(mapId, x, y) == 0) &&
        clientPakObstacles.GetCellObstacle(mapId, x, y) == 0;

    private static bool IsServerChordStrictlyPassable(
        IMapCatalog mapCatalog,
        ushort mapId,
        int x0,
        int y0,
        int x1,
        int y1)
    {
        double dist = ComputeDistance(x0, y0, x1, y1);
        int steps = Math.Max(2, (int)Math.Ceiling(dist / 8.0));
        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            int x = (int)Math.Round(x0 + ((x1 - x0) * t));
            int y = (int)Math.Round(y0 + ((y1 - y0) * t));
            if (mapCatalog.GetCellTrapId(mapId, x, y) == 0 && mapCatalog.GetCellObstacle(mapId, x, y) != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsJointSegmentPassableFromInterior(
        IMapCatalog mapCatalog,
        ClientPakObstacleReader clientPakObstacles,
        ushort mapId,
        int x0,
        int y0,
        int x1,
        int y1)
    {
        double dist = ComputeDistance(x0, y0, x1, y1);
        int steps = Math.Max(2, (int)Math.Ceiling(dist / 16.0));
        int startStep = Math.Min(steps, 3);
        for (int i = startStep; i <= steps; i++)
        {
            double t = (double)i / steps;
            int x = (int)Math.Round(x0 + ((x1 - x0) * t));
            int y = (int)Math.Round(y0 + ((y1 - y0) * t));
            if (!IsDetourPointPassable(mapCatalog, clientPakObstacles, mapId, x, y))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<WorldDomSnapshot> WaitMoveOrStallAsync(
        JxHeadlessClient client,
        WorldDomSnapshot baseline,
        int targetMpsX,
        int targetMpsY,
        double tolerancePx,
        uint stallTicks,
        int timeoutSec,
        Action? sustainLife)
    {
        int lastX = baseline.Player.MpsX;
        int lastY = baseline.Player.MpsY;
        uint lastMovedTick = baseline.TickCount;

        return await client.WaitUntilAsync(
            s =>
            {
                sustainLife?.Invoke();
                if (ComputeDistance(s.Player.MpsX, s.Player.MpsY, targetMpsX, targetMpsY) <= tolerancePx)
                {
                    return true;
                }

                if (s.Player.MpsX != lastX || s.Player.MpsY != lastY)
                {
                    lastX = s.Player.MpsX;
                    lastY = s.Player.MpsY;
                    lastMovedTick = s.TickCount;
                    return false;
                }

                return s.TickCount > lastMovedTick && (s.TickCount - lastMovedTick) >= stallTicks;
            },
            TimeSpan.FromSeconds(timeoutSec));
    }

    private static async Task<WorldDomSnapshot> TraverseSegmentAsync(
        JxHeadlessClient client,
        IMapCatalog mapCatalog,
        ClientPakObstacleReader clientPakObstacles,
        PlayerZoneState serverPlayer,
        ITestOutputHelper output,
        string segmentLabel,
        int targetMpsX,
        int targetMpsY,
        Action onWalkFallback,
        Action onOrphanedAiRepaired,
        Action? sustainLife = null)
    {
        (WorldDomSnapshot snap, bool usedFallback) = await MoveSingleWaypointAsync(
            client,
            mapCatalog,
            clientPakObstacles,
            serverPlayer,
            output,
            segmentLabel,
            targetMpsX,
            targetMpsY,
            onOrphanedAiRepaired,
            sustainLife);
        if (usedFallback)
        {
            onWalkFallback();
        }

        return snap;
    }

    private static void CollectTelemetry(
        WorldDomSnapshot snap,
        PlayerZoneState serverPlayer,
        MapZoneWorker worker,
        ref uint maxSeenS2cSeq,
        ref int selfSnap0x4ECount,
        ref int walkBroadcast0x50Count,
        ref int runBroadcast0x51Count,
        ref double maxClientServerDriftPx,
        ref int minClientStamina,
        ref int aoiDesyncZeroNpcEvents,
        HashSet<uint> distinctVisibleNpcs,
        HashSet<string> distinctMonsterNames,
        HashSet<string> distinctDialogNpcNames)
    {
        foreach (S2cPacketSnapshot pkt in snap.RecentS2c)
        {
            if (pkt.Seq > maxSeenS2cSeq)
            {
                maxSeenS2cSeq = pkt.Seq;
                if (pkt.Opcode == ProtocolOpcodes.s2c_syncnpcminplayer)
                {
                    selfSnap0x4ECount++;
                }
                else if (pkt.Opcode == ProtocolOpcodes.s2c_npcwalk)
                {
                    walkBroadcast0x50Count++;
                }
                else if (pkt.Opcode == ProtocolOpcodes.s2c_npcrun)
                {
                    runBroadcast0x51Count++;
                }
            }
        }

        double drift = ComputeDistance(
            snap.Player.MpsX,
            snap.Player.MpsY,
            serverPlayer.CurrentMpsX,
            serverPlayer.CurrentMpsY);
        if (drift > maxClientServerDriftPx)
        {
            maxClientServerDriftPx = drift;
        }

        int liveStam = Math.Min(snap.Player.Stamina, snap.Player.StaminaMax);
        if (liveStam < minClientStamina)
        {
            minClientStamina = liveStam;
        }

        int visibleOthers = 0;
        foreach (NpcSnapshot npc in snap.Npcs)
        {
            if (npc.IsSelf)
            {
                continue;
            }

            visibleOthers++;
            distinctVisibleNpcs.Add(npc.DwId);
            if (npc.Kind == 0 && !string.IsNullOrWhiteSpace(npc.Name))
            {
                distinctMonsterNames.Add(npc.Name);
            }
            else if (npc.Kind == 3 && !string.IsNullOrWhiteSpace(npc.Name))
            {
                distinctDialogNpcNames.Add(npc.Name);
            }
        }

        if (visibleOthers == 0)
        {
            SpatialRegionSlot[] slots = new SpatialRegionSlot[9];
            int count = worker.RegionGrid.GetSurroundingRegions(serverPlayer.RegionX, serverPlayer.RegionY, slots);
            int serverNearbyNpcs = 0;
            for (int i = 0; i < count; i++)
            {
                serverNearbyNpcs += slots[i].Npcs.Count;
            }

            if (serverNearbyNpcs > 0)
            {
                aoiDesyncZeroNpcEvents++;
            }
        }
    }

    private static double ComputeDistance(int x0, int y0, int x1, int y1)
    {
        double dx = x1 - x0;
        double dy = y1 - y0;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
