using System.Text.Json;
using JXReborn.GameAssets.WorldMaps;
using JXReborn.WireParityProxy.Navigation;
using JXReborn.WireParityProxy.Scenarios;
using Xunit;

namespace JXReborn.WireParityProxy.Tests.Scenarios;

public sealed class WireParityScenarioAndPathfinderTests
{
    [Fact]
    public void WireParityScenario_JsonRoundTrip_ShouldPreserveGroundTruthStepFields()
    {
        var scenario = new WireParityScenario(
            Name: "TuongDuongNhaMonSmoke",
            MapId: 78,
            Description: "Khu A Nha Mon smoke anchored at legacy-server Ground Truth",
            Account: "gm_td_01",
            Password: "123456",
            RoleName: "SurveyHero78",
            NativePlaceId: 78,
            AutoExportFailingCaptures: true,
            Steps:
            [
                new WireScenarioStep(
                    Name: "InteractGuard1",
                    Type: "interact_npc",
                    Region: "Khu A",
                    MapId: 78,
                    TileX: 1593,
                    TileY: 3206,
                    NpcName: "Vệ binh Nha môn 1",
                    TemplateId: 51,
                    MaxRadiusTiles: 6),
                new WireScenarioStep(
                    Name: "StepMap79Portal",
                    Type: "step_trap",
                    Region: "Khu F",
                    MapId: 78,
                    TrapId: 3721490225u,
                    ExpectedMapId: 79,
                    ExpectedFightMode: 1)
            ]);

        string json = JsonSerializer.Serialize(scenario);
        WireParityScenario? roundTripped = JsonSerializer.Deserialize<WireParityScenario>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal((ushort)78, roundTripped.MapId);
        Assert.NotNull(roundTripped.Steps);
        Assert.Equal(2, roundTripped.Steps.Count);
        Assert.Equal(3721490225u, roundTripped.Steps[1].TrapId);
        Assert.Equal(1593, roundTripped.Steps[0].TileX);
    }

    [Fact]
    public void ProxyMapPathfinder_OnRealMaps53And78_ShouldFindObstacleAndTrapFreeWaypoints()
    {
        IMapCatalog? mapCatalog = ProxyMapCatalogLoader.TryGetDefault();
        Assert.NotNull(mapCatalog);
        using ProxyClientPakObstacleReader? pakObstacles = ProxyClientPakObstacleReader.TryOpenFromTemplate();
        Assert.NotNull(pakObstacles);

        // Map 53: Town Center (1626, 3178) -> Ngo Than Y (51150, 102713) & North Gate trap (2476324320)
        IReadOnlyList<(int MpsX, int MpsY)> balangWaypoints = ProxyMapPathfinder.FindWaypointsNearPosition(
            mapCatalog,
            mapId: 53,
            startMpsX: 1626 * 32,
            startMpsY: 3178 * 32,
            targetMpsX: 51150,
            targetMpsY: 102713,
            stopRadiusCells: 2,
            clientPakObstacles: pakObstacles);
        Assert.NotEmpty(balangWaypoints);
        foreach ((int wx, int wy) in balangWaypoints)
        {
            Assert.Equal(0, mapCatalog.GetCellObstacle(53, wx, wy) & 0x0F);
            Assert.Equal(0, pakObstacles.GetCellObstacle(53, wx, wy));
            Assert.Equal(0u, mapCatalog.GetCellTrapId(53, wx, wy));
        }

        IReadOnlyList<(int MpsX, int MpsY)> balangNorthTrap = ProxyMapPathfinder.FindWaypointsToTrap(
            mapCatalog,
            mapId: 53,
            startMpsX: 1626 * 32,
            startMpsY: 3178 * 32,
            targetTrapId: 2476324320u,
            clientPakObstacles: pakObstacles);
        Assert.True(balangNorthTrap.Count >= 2);
        Assert.Equal(2476324320u, mapCatalog.GetCellTrapId(53, balangNorthTrap[^1].MpsX, balangNorthTrap[^1].MpsY));

        // Map 78: Nha Mon RevPos 29 (1577, 3238) -> Guard 1 (1593, 3206) & Secret Tunnel portal (3721490225)
        IReadOnlyList<(int MpsX, int MpsY)> tuongDuongWaypoints = ProxyMapPathfinder.FindWaypointsNearPosition(
            mapCatalog,
            mapId: 78,
            startMpsX: 1577 * 32,
            startMpsY: 3238 * 32,
            targetMpsX: 1593 * 32,
            targetMpsY: 3206 * 32,
            stopRadiusCells: 2,
            clientPakObstacles: pakObstacles);
        Assert.NotEmpty(tuongDuongWaypoints);
        foreach ((int wx, int wy) in tuongDuongWaypoints)
        {
            Assert.Equal(0, mapCatalog.GetCellObstacle(78, wx, wy) & 0x0F);
            Assert.Equal(0, pakObstacles.GetCellObstacle(78, wx, wy));
            Assert.Equal(0u, mapCatalog.GetCellTrapId(78, wx, wy));
        }

        IReadOnlyList<(int MpsX, int MpsY)> secretTunnelWaypoints = ProxyMapPathfinder.FindWaypointsToTrap(
            mapCatalog,
            mapId: 78,
            startMpsX: 1593 * 32,
            startMpsY: 3206 * 32,
            targetTrapId: 3721490225u,
            clientPakObstacles: pakObstacles);
        Assert.True(secretTunnelWaypoints.Count >= 2);
        Assert.Equal(3721490225u, mapCatalog.GetCellTrapId(78, secretTunnelWaypoints[^1].MpsX, secretTunnelWaypoints[^1].MpsY));
    }

    [Fact]
    public void BuiltInMapSurveyScenarios_ForMaps53And78_ShouldExistAndMatchGroundTruthBounds()
    {
        IMapCatalog? mapCatalog = ProxyMapCatalogLoader.TryGetDefault();
        Assert.NotNull(mapCatalog);
        using ProxyClientPakObstacleReader? pakObstacles = ProxyClientPakObstacleReader.TryOpenFromTemplate();
        Assert.NotNull(pakObstacles);

        string mapsScenarioDir = FindScenarioMapsDirectory();
        var supportedStepTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "login",
            "gm_teleport",
            "resync_if_drifted",
            "gm_set_level",
            "gm_set_money",
            "gm_set_camp",
            "gm_set_fight_mode",
            "gm_set_task",
            "gm_grant_item",
            "gm_clear_bag",
            "walk_to",
            "step_trap",
            "interact_npc",
            "click_object",
            "select_dialog",
            "confirm_talk",
            "close_dialog",
            "input_number",
            "buy_shop_item",
            "repair_item",
            "box_operate",
            "crawl_region_npcs"
        };

        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        foreach ((string fileName, ushort expectedMapId, int minSteps) in new (string, ushort, int)[]
        {
            ("053_ba-lang-huyen.scenario.json", 53, 25),
            ("078_tuong-duong.scenario.json", 78, 40)
        })
        {
            string fullPath = Path.Combine(mapsScenarioDir, fileName);
            Assert.True(File.Exists(fullPath), $"Missing built-in Ground-Truth scenario file '{fullPath}'.");

            WireParityScenario? scenario = JsonSerializer.Deserialize<WireParityScenario>(
                File.ReadAllBytes(fullPath),
                jsonOptions);
            Assert.NotNull(scenario);
            Assert.Equal(expectedMapId, scenario.MapId);
            Assert.NotNull(scenario.Steps);
            Assert.True(
                scenario.Steps.Count >= minSteps,
                $"Expected at least {minSteps} steps in '{fileName}', got {scenario.Steps.Count}.");
            Assert.Equal("login", scenario.Steps[0].Type);

            foreach (WireScenarioStep step in scenario.Steps)
            {
                Assert.False(string.IsNullOrWhiteSpace(step.Name));
                Assert.Contains(step.Type, supportedStepTypes);

                ushort stepMapId = checked((ushort)(step.MapId ?? scenario.MapId));
                Assert.True(
                    mapCatalog.TryGetMapDescriptor(stepMapId, out MapDescriptor? descriptor) && descriptor is not null,
                    $"Step '{step.Name}' references unloaded map {stepMapId}.");

                if (step.TileX is { } tx && step.TileY is { } ty)
                {
                    int rx = tx / 16;
                    int ry = ty / 32;
                    Assert.True(
                        mapCatalog.TryGetRegionObstacleGrid(stepMapId, rx, ry, out _),
                        $"Step '{step.Name}' coord ({tx},{ty}) region ({rx},{ry}) is outside loaded regions for map {stepMapId}.");

                    if (string.Equals(step.Type, "gm_teleport", StringComparison.OrdinalIgnoreCase))
                    {
                        int mpsX = (tx * 32) + 16;
                        int mpsY = (ty * 32) + 16;
                        Assert.Equal(0, mapCatalog.GetCellObstacle(stepMapId, mpsX, mpsY) & 0x0F);
                        Assert.Equal(0, pakObstacles.GetCellObstacle(stepMapId, mpsX, mpsY));
                        Assert.Equal(0u, mapCatalog.GetCellTrapId(stepMapId, mpsX, mpsY));
                    }
                }

                if (step.MpsX is { } mx && step.MpsY is { } my)
                {
                    int rx = (mx / 32) / 16;
                    int ry = (my / 32) / 32;
                    Assert.True(
                        mapCatalog.TryGetRegionObstacleGrid(stepMapId, rx, ry, out _),
                        $"Step '{step.Name}' mps ({mx},{my}) region ({rx},{ry}) is outside loaded regions for map {stepMapId}.");
                }

                if (step.TrapId is { } trapId && trapId != 0)
                {
                    Assert.True(
                        MapContainsTrap(mapCatalog, stepMapId, descriptor, trapId),
                        $"Step '{step.Name}' trapId {trapId} was not found in real map {stepMapId} RegionTrapGrids.");
                }
            }
        }
    }

    private static string FindScenarioMapsDirectory()
    {
        foreach (string seed in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            DirectoryInfo? dir = new(seed);
            for (int depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "qa", "JXReborn.WireParityProxy", "Scenarios", "Maps");
                if (Directory.Exists(candidate) || File.Exists(Path.Combine(dir.FullName, "qa", "JXReborn.WireParityProxy", "JXReborn.WireParityProxy.csproj")))
                {
                    return candidate;
                }

                string nested = Path.Combine(dir.FullName, "workbench", "qa", "JXReborn.WireParityProxy", "Scenarios", "Maps");
                if (File.Exists(Path.Combine(dir.FullName, "workbench", "qa", "JXReborn.WireParityProxy", "JXReborn.WireParityProxy.csproj")))
                {
                    return nested;
                }
            }
        }

        throw new DirectoryNotFoundException("Could not locate workbench/qa/JXReborn.WireParityProxy/Scenarios/Maps.");
    }

    private static bool MapContainsTrap(IMapCatalog mapCatalog, ushort mapId, MapDescriptor descriptor, uint trapId)
    {
        for (int rx = descriptor.RegionBeginX; rx < descriptor.RegionBeginX + descriptor.RegionWidth; rx++)
        {
            for (int ry = descriptor.RegionBeginY; ry < descriptor.RegionBeginY + descriptor.RegionHeight; ry++)
            {
                if (!mapCatalog.TryGetRegionTrapGrid(mapId, rx, ry, out RegionTrapGrid? trapGrid) || trapGrid is null)
                {
                    continue;
                }

                for (int cx = 0; cx < 16; cx++)
                {
                    for (int cy = 0; cy < 32; cy++)
                    {
                        if (trapGrid.GetTrapId(cx, cy) == trapId)
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    [Fact]
    public void FindWaypointsNearPosition_Map53TieuNguWaterEdgeWithRadiusFallback_ShouldReachWalkableShoreWithinDialogRange()
    {
        IMapCatalog? mapCatalog = ProxyMapCatalogLoader.TryGetDefault();
        Assert.NotNull(mapCatalog);
        using ProxyClientPakObstacleReader? pakObstacles = ProxyClientPakObstacleReader.TryOpenFromTemplate();
        Assert.NotNull(pakObstacles);

        // Ground Truth: NPC 'Tieu Ngu' (id=362) sits at water edge (52464, 102834) tile (1639,3213)
        // where r=2 is enclosed by water, while nearest shore (1636,3216) is walkable within DialogRadiusCells=4.
        IReadOnlyList<(int MpsX, int MpsY)> waypoints = ProxyMapPathfinder.FindWaypointsNearPosition(
            mapCatalog,
            mapId: 53,
            startMpsX: 1628 * 32,
            startMpsY: 3213 * 32,
            targetMpsX: 52464,
            targetMpsY: 102834,
            stopRadiusCells: 2,
            clientPakObstacles: pakObstacles,
            maxFallbackRadiusCells: 4);

        Assert.NotEmpty(waypoints);
        (int endX, int endY) = waypoints[^1];
        Assert.Equal(0, mapCatalog.GetCellObstacle(53, endX, endY) & 0x0F);
        Assert.Equal(0, pakObstacles.GetCellObstacle(53, endX, endY));

        double distMps = Math.Sqrt(Math.Pow(endX - 52464, 2) + Math.Pow(endY - 102834, 2));
        Assert.True(distMps <= 160.0, $"Expected shoreline stop within DialogNpc server range (<=160 Mps), got {distMps:F1} Mps at ({endX / 32},{endY / 32}).");
    }
}
