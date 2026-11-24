using System.Diagnostics;
using JXReborn.Adapter.Legacy.Packets;
using JXReborn.ClientDriver;
using JXReborn.DualParityServer.Diffing;
using JXReborn.GameAssets.WorldMaps;
using JXReborn.WireParityProxy.Configuration;
using JXReborn.WireParityProxy.Diffing;
using JXReborn.WireParityProxy.Driver;
using JXReborn.WireParityProxy.Navigation;
using JXReborn.WireParityProxy.Proxy;
using JXReborn.WireParityProxy.Reporting;

namespace JXReborn.WireParityProxy.Scenarios;

/// <summary>
/// Orchestrates end-to-end execution of a <see cref="WireParityScenario"/> through <c>WireParityProxy</c>
/// using 1 headless <c>game.exe</c> client, obstacle pathfinding, GM Token atomic setup, drift recovery,
/// and automatic <c>*.capture.json</c> export for steps that trigger Critical findings.
/// </summary>
public sealed class WireScenarioRunner
{
    private const int GmTokenGenre = 6;
    private const int GmTokenDetailType = 1;
    private const int GmTokenParticularType = 4258;
    private const int PlaceEquipRoom = 3;

    private readonly WireParityProxyOptions _options;
    private readonly PrimaryClientDriverBridge _driverBridge;
    private readonly BishopShadowProxy _bishopProxy;
    private readonly GameServerShadowProxy _gameServerProxy;
    private readonly IMapCatalog? _mapCatalog;
    private readonly ProxyClientPakObstacleReader? _clientPakObstacles;
    private readonly TextWriter _logOut;

    public WireScenarioRunner(
        WireParityProxyOptions options,
        PrimaryClientDriverBridge driverBridge,
        BishopShadowProxy bishopProxy,
        GameServerShadowProxy gameServerProxy,
        IMapCatalog? mapCatalog = null,
        ProxyClientPakObstacleReader? clientPakObstacles = null,
        TextWriter? logOut = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(driverBridge);
        ArgumentNullException.ThrowIfNull(bishopProxy);
        ArgumentNullException.ThrowIfNull(gameServerProxy);
        _options = options;
        _driverBridge = driverBridge;
        _bishopProxy = bishopProxy;
        _gameServerProxy = gameServerProxy;
        _mapCatalog = mapCatalog ?? ProxyMapCatalogLoader.TryGetDefault();
        _clientPakObstacles = clientPakObstacles ?? ProxyClientPakObstacleReader.TryOpenFromTemplate();
        _logOut = logOut ?? Console.Out;
    }

    public async Task<WireScenarioRunReport> RunAsync(
        WireParityScenario scenario,
        bool stopOnStepFailure = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        IReadOnlyList<WireScenarioStep> steps = scenario.Steps ?? [];
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        var results = new List<WireScenarioStepResult>(steps.Count);

        _logOut.WriteLine($"[WireScenario] Running '{scenario.Name}' (Map {scenario.MapId}, {steps.Count} steps)...");

        for (int i = 0; i < steps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WireScenarioStep step = steps[i];
            int stepNumber = i + 1;
            var sw = Stopwatch.StartNew();

            WireParitySession? sessionBefore = _gameServerProxy.LatestSession;
            int findingsBefore = sessionBefore?.DiffCoordinator.GetFindings().Count ?? 0;
            int stepsBefore = sessionBefore?.DiffCoordinator.GetSteps().Count ?? 0;

            bool ok = true;
            string status = "OK";
            string? errorMessage = null;

            try
            {
                await ExecuteStepAsync(scenario, step, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ok = false;
                status = "FAIL";
                errorMessage = ex.Message;
            }

            if (_driverBridge.Client is { } liveClient)
            {
                await WireNpcDialogCrawler.WaitWindowFlushAsync(liveClient, _options.ActionWindowMs, cancellationToken)
                    .ConfigureAwait(false);
            }

            WorldDomSnapshot? snapAfter = null;
            if (_driverBridge.Client is { } clientAfter)
            {
                try
                {
                    snapAfter = await clientAfter.GetStateAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort post-step snapshot.
                }
            }

            WireParitySession? sessionAfter = _gameServerProxy.LatestSession;
            IReadOnlyList<ParityFinding> allFindings = sessionAfter?.DiffCoordinator.GetFindings() ?? [];
            IReadOnlyList<ParityFinding> newFindings = allFindings.Skip(findingsBefore).ToArray();
            int critCount = newFindings.Count(f => f.Severity == FindingSeverity.Critical);

            List<string> exportedCaptures = [];
            if (scenario.AutoExportFailingCaptures && sessionAfter is not null)
            {
                IReadOnlyList<WireStepReport> windowSteps = sessionAfter.DiffCoordinator.GetSteps();
                string captureDir = Path.Combine(_options.ReportOutputDir, "captures");
                foreach (WireStepReport ws in windowSteps.Skip(stepsBefore))
                {
                    if (ws.Findings.Any(f => f.Severity == FindingSeverity.Critical))
                    {
                        try
                        {
                            string safeName = SanitizeFilePart($"{scenario.MapId}_{stepNumber:D3}_{step.Name}_S{ws.StepIndex:D3}");
                            string path = L3aCaptureExporter.ExportActionWindow(
                                sessionAfter,
                                ws.StepIndex,
                                safeName,
                                captureDir,
                                _options.LegacyProfile);
                            exportedCaptures.Add(path);
                        }
                        catch
                        {
                            // Best-effort capture export.
                        }
                    }
                }
            }

            sw.Stop();
            int mapAfter = snapAfter?.Player.MapId ?? sessionAfter?.PrimaryState.MapId ?? 0;
            int txAfter = (snapAfter?.Player.MpsX ?? sessionAfter?.PrimaryState.MpsX ?? 0) / 32;
            int tyAfter = (snapAfter?.Player.MpsY ?? sessionAfter?.PrimaryState.MpsY ?? 0) / 32;

            var stepResult = new WireScenarioStepResult(
                StepNumber: stepNumber,
                Name: step.Name,
                Region: step.Region,
                Type: step.Type,
                Executed: ok,
                Status: status,
                ErrorMessage: errorMessage,
                MapIdAfter: mapAfter,
                TileXAfter: txAfter,
                TileYAfter: tyAfter,
                NewFindingsCount: newFindings.Count,
                CriticalFindingsCount: critCount,
                ExportedCaptures: exportedCaptures,
                ElapsedMs: sw.ElapsedMilliseconds);
            results.Add(stepResult);

            _logOut.WriteLine(
                $"[WireScenario] [{stepNumber}/{steps.Count}] {status} | {step.Region ?? "-"} | {step.Type} '{step.Name}' -> Map {mapAfter} ({txAfter},{tyAfter}), +{newFindings.Count} findings ({critCount} crit), {sw.ElapsedMilliseconds}ms" +
                (errorMessage is null ? string.Empty : $" | ERR: {errorMessage}"));

            if (!ok && (stopOnStepFailure || string.Equals(step.Type, "login", StringComparison.OrdinalIgnoreCase)))
            {
                break;
            }
        }

        WireParitySession? finalSession = _gameServerProxy.LatestSession;
        IReadOnlyList<ParityFinding> finalFindings = finalSession?.DiffCoordinator.GetFindings() ?? [];
        int totalCrit = finalFindings.Count(f => f.Severity == FindingSeverity.Critical);
        int totalWarn = finalFindings.Count(f => f.Severity == FindingSeverity.Warning);

        return new WireScenarioRunReport(
            ScenarioName: scenario.Name,
            TargetMapId: scenario.MapId,
            StartedAtUtc: startedAt,
            CompletedAtUtc: DateTimeOffset.UtcNow,
            TotalSteps: results.Count,
            PassedSteps: results.Count(r => r.Executed),
            StepFailures: results.Count(r => !r.Executed),
            TotalFindings: finalFindings.Count,
            CriticalFindings: totalCrit,
            WarningFindings: totalWarn,
            ProtocolViolations: finalSession?.ProtocolGate.ViolationCount ?? 0,
            StepResults: results,
            ReportDirectory: _options.ReportOutputDir);
    }

    private async Task ExecuteStepAsync(
        WireParityScenario scenario,
        WireScenarioStep step,
        CancellationToken cancellationToken)
    {
        string type = step.Type.Trim().ToLowerInvariant();
        if (type == "login")
        {
            const int maxLoginAttempts = 3;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    JxHeadlessClient loginClient = await _driverBridge.EnsureAliveClientAsync(
                        _options,
                        _bishopProxy.BoundPort,
                        cancellationToken).ConfigureAwait(false);
                    _ = await loginClient.LoginAndEnterWorldAsync(
                        _options.BindHost,
                        _bishopProxy.BoundPort,
                        scenario.Account,
                        scenario.Password,
                        scenario.RoleName,
                        gender: 0,
                        series: 0,
                        nativePlaceId: scenario.NativePlaceId,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    await WireNpcDialogCrawler.CloseDialogSafelyAsync(loginClient, cancellationToken).ConfigureAwait(false);
                    await WireNpcDialogCrawler.WaitTicksAsync(loginClient, 10, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (attempt < maxLoginAttempts && (ex is TimeoutException or InvalidOperationException))
                {
                    _logOut.WriteLine(
                        $"[WireScenario] Login attempt {attempt}/{maxLoginAttempts} not ready ({ex.Message}); retrying in 4s after respawning client...");
                    await _driverBridge.InvalidateBrokenClientAsync().ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        JxHeadlessClient client = _driverBridge.Client
            ?? throw new InvalidOperationException($"Step '{step.Name}' requires active client driver. Include a 'login' step first.");

        switch (type)
        {
            case "gm_teleport":
                {
                    int targetMap = step.MapId ?? scenario.MapId;
                    int tx = step.TileX ?? 1577;
                    int ty = step.TileY ?? 3238;
                    await WireNpcDialogCrawler.CloseDialogSafelyAsync(client, cancellationToken).ConfigureAwait(false);
                    await GmTeleportToCoordsAsync(client, targetMap, tx, ty, cancellationToken).ConfigureAwait(false);
                    if (step.ResetDrift ?? true)
                    {
                        _gameServerProxy.LatestSession?.DiffCoordinator.ResetDrift();
                    }
                }

                break;

            case "resync_if_drifted":
                {
                    WorldDomSnapshot snap = await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
                    WireParitySession? session = _gameServerProxy.LatestSession;
                    int targetMap = step.MapId ?? scenario.MapId;
                    bool backendMapDiff = session is not null && session.PrimaryState.MapId != session.ShadowState.MapId;
                    bool clientMapDiff = snap.Player.MapId != targetMap;
                    bool isDrifted = session?.DiffCoordinator.LatestDrift.IsDrifted == true;

                    if (backendMapDiff || clientMapDiff || isDrifted || step.ResetDrift == true)
                    {
                        int tx = step.TileX ?? 1577;
                        int ty = step.TileY ?? 3238;
                        await WireNpcDialogCrawler.CloseDialogSafelyAsync(client, cancellationToken).ConfigureAwait(false);
                        await GmTeleportToCoordsAsync(client, targetMap, tx, ty, cancellationToken).ConfigureAwait(false);
                        session?.DiffCoordinator.ResetDrift();
                    }
                }

                break;

            case "gm_set_level":
                {
                    int targetLevel = step.Level ?? step.Value
                        ?? throw new ArgumentException($"Step '{step.Name}' requires 'level' or 'value'.");
                    WorldDomSnapshot snap = await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
                    if (snap.Player.Level < targetLevel)
                    {
                        await WireNpcDialogCrawler.CloseDialogSafelyAsync(client, cancellationToken).ConfigureAwait(false);
                        await InvokeGmNumberSequenceAsync(client, [0, 0, 7], [targetLevel], expectMapChange: false, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                break;

            case "gm_set_money":
                {
                    int amount = step.Value ?? 0;
                    await WireNpcDialogCrawler.CloseDialogSafelyAsync(client, cancellationToken).ConfigureAwait(false);
                    if (amount <= 0)
                    {
                        await InvokeGmMenuPathAsync(client, [2, 5], cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await InvokeGmMenuPathAsync(client, [2, 5], cancellationToken).ConfigureAwait(false);
                        await InvokeGmNumberSequenceAsync(client, [2, 4], [amount], expectMapChange: false, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                break;

            case "gm_set_camp":
                {
                    int campOpt = step.OptionIndex ?? step.Value ?? 0;
                    await WireNpcDialogCrawler.CloseDialogSafelyAsync(client, cancellationToken).ConfigureAwait(false);
                    await InvokeGmMenuPathAsync(client, [1, 0, campOpt], cancellationToken).ConfigureAwait(false);
                }

                break;

            case "gm_learn_skill":
                {
                    int skillId = step.Value ?? 14;
                    int skillLv = Math.Clamp(step.Level ?? 5, 1, 63);
                    await WireNpcDialogCrawler.CloseDialogSafelyAsync(client, cancellationToken).ConfigureAwait(false);
                    if (skillId == 14 && skillLv == 5)
                    {
                        await InvokeGmMenuPathAsync(client, [1, 2, 0], cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await InvokeGmNumberSequenceAsync(client, [1, 2, 2], [skillId, skillLv], expectMapChange: false, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    await InvokeGmMenuPathAsync(client, [0, 2, 0], cancellationToken).ConfigureAwait(false);
                }

                break;

            case "gm_join_faction":
                {
                    int factionOpt = Math.Clamp(step.OptionIndex ?? step.Value ?? 0, 0, 11);
                    await WireNpcDialogCrawler.CloseDialogSafelyAsync(client, cancellationToken).ConfigureAwait(false);
                    await InvokeGmMenuPathAsync(client, [1, 5, factionOpt], cancellationToken).ConfigureAwait(false);
                }

                break;

            case "gm_set_fight_mode":
                {
                    int fightMode = (step.Value ?? 1) != 0 ? 0 : 1;
                    await WireNpcDialogCrawler.CloseDialogSafelyAsync(client, cancellationToken).ConfigureAwait(false);
                    await InvokeGmMenuPathAsync(client, [4, fightMode], cancellationToken).ConfigureAwait(false);
                }

                break;

            case "gm_set_task":
                {
                    int taskId = step.TaskId ?? throw new ArgumentException($"Step '{step.Name}' requires 'taskId'.");
                    int val = step.Value ?? throw new ArgumentException($"Step '{step.Name}' requires 'value'.");
                    await WireNpcDialogCrawler.CloseDialogSafelyAsync(client, cancellationToken).ConfigureAwait(false);
                    await InvokeGmNumberSequenceAsync(client, [7, 4], [taskId, val], expectMapChange: false, cancellationToken)
                        .ConfigureAwait(false);
                }

                break;

            case "gm_grant_item":
                {
                    int genre = step.Genre ?? throw new ArgumentException($"Step '{step.Name}' requires 'genre'.");
                    int detail = step.DetailType ?? 0;
                    int particular = step.ParticularType ?? 0;
                    int lv = step.Level ?? 1;
                    await WireNpcDialogCrawler.CloseDialogSafelyAsync(client, cancellationToken).ConfigureAwait(false);
                    await InvokeGmNumberSequenceAsync(client, [3, 5], [genre, detail, particular, lv], expectMapChange: false, cancellationToken)
                        .ConfigureAwait(false);
                }

                break;

            case "gm_clear_bag":
                await WireNpcDialogCrawler.CloseDialogSafelyAsync(client, cancellationToken).ConfigureAwait(false);
                await InvokeGmMenuPathAsync(client, [3, 4, 0], cancellationToken).ConfigureAwait(false);
                break;

            case "walk_to":
                {
                    WorldDomSnapshot snap = await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
                    ushort mapId = (ushort)(step.MapId ?? snap.Player.MapId);
                    int targetX = step.MpsX ?? ((step.TileX ?? throw new ArgumentException($"Step '{step.Name}' requires 'tileX'/'mpsX'.")) * 32);
                    int targetY = step.MpsY ?? ((step.TileY ?? throw new ArgumentException($"Step '{step.Name}' requires 'tileY'/'mpsY'.")) * 32);
                    await WireNpcDialogCrawler.WalkNearPositionAsync(
                        client,
                        mapId,
                        targetX,
                        targetY,
                        _mapCatalog,
                        _clientPakObstacles,
                        stopRadiusCells: 1,
                        cancellationToken).ConfigureAwait(false);
                }

                break;

            case "step_trap":
                {
                    await WireNpcDialogCrawler.CloseDialogSafelyAsync(client, cancellationToken).ConfigureAwait(false);
                    WorldDomSnapshot snap = await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
                    ushort currentMap = (ushort)snap.Player.MapId;
                    int? prevFightMode = snap.Player.FightMode;

                    if (step.TrapId.HasValue && _mapCatalog is not null)
                    {
                        IReadOnlyList<(int MpsX, int MpsY)> wps = ProxyMapPathfinder.FindWaypointsToTrap(
                            _mapCatalog,
                            currentMap,
                            snap.Player.MpsX,
                            snap.Player.MpsY,
                            step.TrapId.Value,
                            _clientPakObstacles,
                            step.AllowedPassThroughTrapId ?? 0);

                        for (int idx = 0; idx < wps.Count; idx++)
                        {
                            bool isFinalTrapCell = idx == wps.Count - 1;
                            int moveMode = isFinalTrapCell ? 1 : 2;
                            snap = await WireNpcDialogCrawler.MoveSingleSegmentAsync(
                                client,
                                currentMap,
                                wps[idx].MpsX,
                                wps[idx].MpsY,
                                moveMode,
                                cancellationToken,
                                arriveRadiusMps: isFinalTrapCell ? 10.0 : 40.0,
                                stopOnDialogOpen: isFinalTrapCell).ConfigureAwait(false);
                            if (snap.Player.MapId != currentMap || snap.Dialog.IsOpen)
                            {
                                break;
                            }
                        }
                    }
                    else if (step.TileX.HasValue && step.TileY.HasValue)
                    {
                        int tx = (step.TileX.Value * 32) + 16;
                        int ty = (step.TileY.Value * 32) + 16;
                        await WireNpcDialogCrawler.WalkNearPositionAsync(
                            client,
                            currentMap,
                            tx,
                            ty,
                            _mapCatalog,
                            _clientPakObstacles,
                            stopRadiusCells: 2,
                            cancellationToken).ConfigureAwait(false);
                        _ = await WireNpcDialogCrawler.MoveSingleSegmentAsync(
                            client,
                            currentMap,
                            tx,
                            ty,
                            mode: 1,
                            cancellationToken,
                            arriveRadiusMps: 10.0,
                            stopOnDialogOpen: true).ConfigureAwait(false);
                    }
                    else
                    {
                        throw new ArgumentException($"Step '{step.Name}' requires 'trapId' or '(tileX, tileY)'.");
                    }

                    if (step.ExpectedMapId.HasValue && step.ExpectedMapId.Value != currentMap)
                    {
                        _ = await client.WaitUntilAsync(
                            s => (s.Player.MapId == step.ExpectedMapId.Value && s.Player.SyncReady) || s.Dialog.IsOpen,
                            TimeSpan.FromSeconds(5),
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    else if (step.ExpectedMapId.HasValue && step.ExpectedMapId.Value == currentMap)
                    {
                        try
                        {
                            _ = await client.WaitUntilAsync(
                                s => s.Dialog.IsOpen || s.Player.MapId != currentMap,
                                TimeSpan.FromMilliseconds(1500),
                                cancellationToken: cancellationToken).ConfigureAwait(false);
                        }
                        catch (TimeoutException)
                        {
                            // Gate block may have used SetPos pushback without an open dialog frame.
                        }
                    }
                    else if (step.ExpectedFightMode.HasValue && prevFightMode != step.ExpectedFightMode.Value)
                    {
                        _ = await client.WaitUntilAsync(
                            s => s.Player.FightMode == step.ExpectedFightMode.Value || s.Dialog.IsOpen,
                            TimeSpan.FromSeconds(4),
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        _ = await WireNpcDialogCrawler.WaitTicksAsync(client, 10, cancellationToken).ConfigureAwait(false);
                    }

                    await WireNpcDialogCrawler.CloseDialogSafelyAsync(client, cancellationToken).ConfigureAwait(false);
                }

                break;

            case "interact_npc":
                {
                    await WireNpcDialogCrawler.CloseDialogSafelyAsync(client, cancellationToken).ConfigureAwait(false);
                    string targetName = step.NpcName ?? throw new ArgumentException($"Step '{step.Name}' requires 'npcName'.");
                    WorldDomSnapshot snap = await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
                    ushort currentMap = (ushort)snap.Player.MapId;

                    NpcSnapshot? found = FindBestMatchingNpc(snap.Npcs, targetName, step.TileX, step.TileY, step.MaxRadiusTiles, snap.Player.MpsX, snap.Player.MpsY);
                    if (found is null && step.TileX.HasValue && step.TileY.HasValue)
                    {
                        await WireNpcDialogCrawler.WalkNearPositionAsync(
                            client,
                            currentMap,
                            step.TileX.Value * 32,
                            step.TileY.Value * 32,
                            _mapCatalog,
                            _clientPakObstacles,
                            stopRadiusCells: 2,
                            cancellationToken).ConfigureAwait(false);
                        snap = await WireNpcDialogCrawler.WaitTicksAsync(client, 6, cancellationToken).ConfigureAwait(false);
                        found = FindBestMatchingNpc(snap.Npcs, targetName, step.TileX, step.TileY, step.MaxRadiusTiles, snap.Player.MpsX, snap.Player.MpsY);
                    }

                    if (found is null)
                    {
                        try
                        {
                            snap = await client.WaitUntilAsync(
                                s => FindBestMatchingNpc(s.Npcs, targetName, step.TileX, step.TileY, step.MaxRadiusTiles, s.Player.MpsX, s.Player.MpsY) is not null,
                                TimeSpan.FromSeconds(4),
                                cancellationToken: cancellationToken).ConfigureAwait(false);
                            found = FindBestMatchingNpc(snap.Npcs, targetName, step.TileX, step.TileY, step.MaxRadiusTiles, snap.Player.MpsX, snap.Player.MpsY);
                        }
                        catch (TimeoutException)
                        {
                            snap = await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
                            found = FindBestMatchingNpc(snap.Npcs, targetName, step.TileX, step.TileY, step.MaxRadiusTiles, snap.Player.MpsX, snap.Player.MpsY);
                        }
                    }

                    if (found is null)
                    {
                        throw new InvalidOperationException($"NPC '{targetName}' not found on Map {currentMap} near ({step.TileX},{step.TileY}).");
                    }

                    if (WireNpcDialogCrawler.Distance(snap.Player.MpsX, snap.Player.MpsY, found.MpsX, found.MpsY) > 64.0)
                    {
                        await WireNpcDialogCrawler.WalkNearPositionAsync(
                            client,
                            currentMap,
                            found.MpsX,
                            found.MpsY,
                            _mapCatalog,
                            _clientPakObstacles,
                            stopRadiusCells: 1,
                            cancellationToken).ConfigureAwait(false);
                        _ = await WireNpcDialogCrawler.WaitTicksAsync(client, 6, cancellationToken).ConfigureAwait(false);
                    }

                    snap = await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
                    uint seqBefore = WireNpcDialogCrawler.MaxS2cSeq(snap);
                    _ = await client.InteractNpcByDwIdAsync(found.DwId, cancellationToken).ConfigureAwait(false);
                    _ = await WireNpcDialogCrawler.WaitForInteractionResponseAsync(client, seqBefore, cancellationToken).ConfigureAwait(false);
                }

                break;

            case "attack_npc":
                {
                    await WireNpcDialogCrawler.CloseDialogSafelyAsync(client, cancellationToken).ConfigureAwait(false);
                    string targetName = step.NpcName ?? throw new ArgumentException($"Step '{step.Name}' requires 'npcName'.");
                    int skillId = step.Value ?? 14;
                    bool hitOnly = (step.OptionIndex ?? 0) != 0;

                    WorldDomSnapshot snap = await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
                    ushort currentMap = (ushort)snap.Player.MapId;
                    if (snap.Player.FightMode == 0)
                    {
                        await InvokeGmMenuPathAsync(client, [4, 0], cancellationToken).ConfigureAwait(false);
                        snap = await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
                    }

                    if (snap.Player.Mana < Math.Max(20, snap.Player.ManaMax / 2))
                    {
                        await InvokeGmMenuPathAsync(client, [0, 2, 0], cancellationToken).ConfigureAwait(false);
                        snap = await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
                    }

                    NpcSnapshot? target = FindBestMatchingCombatNpc(snap.Npcs, targetName, step.TileX, step.TileY, step.MaxRadiusTiles, snap.Player.MpsX, snap.Player.MpsY);
                    if (target is null && step.TileX.HasValue && step.TileY.HasValue)
                    {
                        await WireNpcDialogCrawler.WalkNearPositionAsync(
                            client,
                            currentMap,
                            step.TileX.Value * 32,
                            step.TileY.Value * 32,
                            _mapCatalog,
                            _clientPakObstacles,
                            stopRadiusCells: 2,
                            cancellationToken).ConfigureAwait(false);
                        snap = await WireNpcDialogCrawler.WaitTicksAsync(client, 8, cancellationToken).ConfigureAwait(false);
                        target = FindBestMatchingCombatNpc(snap.Npcs, targetName, step.TileX, step.TileY, step.MaxRadiusTiles, snap.Player.MpsX, snap.Player.MpsY);
                    }

                    if (target is null)
                    {
                        snap = await client.WaitUntilAsync(
                            s => FindBestMatchingCombatNpc(s.Npcs, targetName, step.TileX, step.TileY, step.MaxRadiusTiles, s.Player.MpsX, s.Player.MpsY) is not null,
                            TimeSpan.FromSeconds(5),
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                        target = FindBestMatchingCombatNpc(snap.Npcs, targetName, step.TileX, step.TileY, step.MaxRadiusTiles, snap.Player.MpsX, snap.Player.MpsY);
                    }

                    if (target is null)
                    {
                        throw new InvalidOperationException($"Combat target '{targetName}' not found on Map {currentMap} near ({step.TileX},{step.TileY}).");
                    }

                    uint targetDwId = target.DwId;
                    int initialLife = target.Life;
                    if (WireNpcDialogCrawler.Distance(snap.Player.MpsX, snap.Player.MpsY, target.MpsX, target.MpsY) > 64.0)
                    {
                        await WireNpcDialogCrawler.WalkNearPositionAsync(
                            client,
                            currentMap,
                            target.MpsX,
                            target.MpsY,
                            _mapCatalog,
                            _clientPakObstacles,
                            stopRadiusCells: 0,
                            cancellationToken).ConfigureAwait(false);
                        snap = await WireNpcDialogCrawler.WaitTicksAsync(client, 6, cancellationToken).ConfigureAwait(false);
                    }

                    long primaryExpBefore = _gameServerProxy.LatestSession?.PrimaryState.Exp ?? 0;
                    long shadowExpBefore = _gameServerProxy.LatestSession?.ShadowState.Exp ?? 0;
                    bool resolved = false;
                    for (int attempt = 0; attempt < 12; attempt++)
                    {
                        _ = await client.UseSkillOnTargetAsync(skillId, targetDwId, cancellationToken).ConfigureAwait(false);
                        snap = await WireNpcDialogCrawler.WaitTicksAsync(client, 14, cancellationToken).ConfigureAwait(false);
                        NpcSnapshot? after = snap.Npcs.FirstOrDefault(n => n.DwId == targetDwId);
                        if (after is null || after.Life <= 0 || after.Doing == 14 || (hitOnly && after.Life < initialLife))
                        {
                            resolved = true;
                            break;
                        }
                    }

                    if (!resolved)
                    {
                        throw new InvalidOperationException($"Combat target '{targetName}' (dwId={targetDwId}) was not defeated after 12 skill casts (skillId={skillId}).");
                    }

                    _ = await WireNpcDialogCrawler.WaitTicksAsync(client, 10, cancellationToken).ConfigureAwait(false);
                    for (int settle = 0; settle < 6; settle++)
                    {
                        WireParitySession? liveSession = _gameServerProxy.LatestSession;
                        if (liveSession is null ||
                            liveSession.PrimaryState.Exp <= primaryExpBefore ||
                            liveSession.ShadowState.Exp > shadowExpBefore)
                        {
                            break;
                        }

                        _ = await WireNpcDialogCrawler.WaitTicksAsync(client, 6, cancellationToken).ConfigureAwait(false);
                    }
                }

                break;

            case "click_object":
                {
                    await WireNpcDialogCrawler.CloseDialogSafelyAsync(client, cancellationToken).ConfigureAwait(false);
                    WorldDomSnapshot snap = await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
                    ushort currentMap = (ushort)snap.Player.MapId;

                    GroundObjectSnapshot? found = FindBestMatchingObject(snap.GroundObjects, step.ObjectName, step.DataId, step.TileX, step.TileY, step.MaxRadiusTiles, snap.Player.MpsX, snap.Player.MpsY);
                    if (found is null && step.TileX.HasValue && step.TileY.HasValue)
                    {
                        await WireNpcDialogCrawler.WalkNearPositionAsync(
                            client,
                            currentMap,
                            step.TileX.Value * 32,
                            step.TileY.Value * 32,
                            _mapCatalog,
                            _clientPakObstacles,
                            stopRadiusCells: 2,
                            cancellationToken).ConfigureAwait(false);
                        snap = await WireNpcDialogCrawler.WaitTicksAsync(client, 6, cancellationToken).ConfigureAwait(false);
                        found = FindBestMatchingObject(snap.GroundObjects, step.ObjectName, step.DataId, step.TileX, step.TileY, step.MaxRadiusTiles, snap.Player.MpsX, snap.Player.MpsY);
                    }

                    if (found is null)
                    {
                        try
                        {
                            snap = await client.WaitUntilAsync(
                                s => FindBestMatchingObject(s.GroundObjects, step.ObjectName, step.DataId, step.TileX, step.TileY, step.MaxRadiusTiles, s.Player.MpsX, s.Player.MpsY) is not null,
                                TimeSpan.FromSeconds(3),
                                cancellationToken: cancellationToken).ConfigureAwait(false);
                            found = FindBestMatchingObject(snap.GroundObjects, step.ObjectName, step.DataId, step.TileX, step.TileY, step.MaxRadiusTiles, snap.Player.MpsX, snap.Player.MpsY);
                        }
                        catch (TimeoutException)
                        {
                            snap = await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
                            found = FindBestMatchingObject(snap.GroundObjects, step.ObjectName, step.DataId, step.TileX, step.TileY, step.MaxRadiusTiles, snap.Player.MpsX, snap.Player.MpsY);
                        }
                    }

                    if (found is null)
                    {
                        throw new InvalidOperationException($"Ground object '{step.ObjectName ?? step.DataId?.ToString()}' not found on Map {currentMap} near ({step.TileX},{step.TileY}).");
                    }

                    if (WireNpcDialogCrawler.Distance(snap.Player.MpsX, snap.Player.MpsY, found.MpsX, found.MpsY) > 64.0)
                    {
                        await WireNpcDialogCrawler.WalkNearPositionAsync(
                            client,
                            currentMap,
                            found.MpsX,
                            found.MpsY,
                            _mapCatalog,
                            _clientPakObstacles,
                            stopRadiusCells: 1,
                            cancellationToken).ConfigureAwait(false);
                        _ = await WireNpcDialogCrawler.WaitTicksAsync(client, 6, cancellationToken).ConfigureAwait(false);
                    }

                    snap = await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
                    uint seqBefore = WireNpcDialogCrawler.MaxS2cSeq(snap);
                    _ = await client.PickupObjectByArrayIndexAsync(found.ArrayIndex, cancellationToken).ConfigureAwait(false);
                    _ = await WireNpcDialogCrawler.WaitForInteractionResponseAsync(client, seqBefore, cancellationToken).ConfigureAwait(false);
                }

                break;

            case "select_dialog":
                {
                    int optIdx = step.OptionIndex ?? throw new ArgumentException($"Step '{step.Name}' requires 'optionIndex'.");
                    WorldDomSnapshot snap = await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
                    uint seqBefore = WireNpcDialogCrawler.MaxS2cSeq(snap);
                    _ = await client.SelectDialogAsync(optIdx, cancellationToken).ConfigureAwait(false);
                    _ = await WireNpcDialogCrawler.WaitForInteractionResponseAsync(client, seqBefore, cancellationToken).ConfigureAwait(false);
                }

                break;

            case "confirm_talk":
                {
                    WorldDomSnapshot snap = await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
                    uint seqBefore = WireNpcDialogCrawler.MaxS2cSeq(snap);
                    _ = await client.ConfirmTalkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    _ = await WireNpcDialogCrawler.WaitForInteractionResponseAsync(client, seqBefore, cancellationToken).ConfigureAwait(false);
                }

                break;

            case "close_dialog":
                await WireNpcDialogCrawler.CloseDialogSafelyAsync(client, cancellationToken).ConfigureAwait(false);
                break;

            case "input_number":
                {
                    int val = step.Value ?? throw new ArgumentException($"Step '{step.Name}' requires 'value'.");
                    WorldDomSnapshot snap = await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
                    uint seqBefore = WireNpcDialogCrawler.MaxS2cSeq(snap);
                    _ = await client.SubmitInputNumberAsync(val, cancellationToken).ConfigureAwait(false);
                    _ = await WireNpcDialogCrawler.WaitForInteractionResponseAsync(client, seqBefore, cancellationToken).ConfigureAwait(false);
                }

                break;

            case "buy_shop_item":
                {
                    WorldDomSnapshot snap = await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
                    int buyIdx = step.Value ?? step.OptionIndex ?? 0;
                    (int freeX, int freeY) = ResolveShopBuyGridCell(snap.Items);
                    _ = await client.BuyShopItemAsync(buyIdx, PlaceEquipRoom, freeX, freeY, cancellationToken).ConfigureAwait(false);
                    _ = await WireNpcDialogCrawler.WaitTicksAsync(client, 10, cancellationToken).ConfigureAwait(false);
                }

                break;

            case "repair_item":
                {
                    uint itemId = (uint)(step.Value ?? throw new ArgumentException($"Step '{step.Name}' requires 'value' (itemId)."));
                    _ = await client.RepairItemAsync(itemId, cancellationToken).ConfigureAwait(false);
                    _ = await WireNpcDialogCrawler.WaitTicksAsync(client, 10, cancellationToken).ConfigureAwait(false);
                }

                break;

            case "store_money":
                {
                    byte dir = checked((byte)(step.OptionIndex ?? 0));
                    int amount = step.Value ?? 100;
                    _ = await client.StoreMoneyAsync(dir, amount, cancellationToken).ConfigureAwait(false);
                    _ = await WireNpcDialogCrawler.WaitTicksAsync(client, 10, cancellationToken).ConfigureAwait(false);
                }

                break;

            case "box_operate":
                {
                    byte op = checked((byte)(step.OptionIndex ?? step.Value ?? 0));
                    _ = await client.BoxOperateAsync(op, cancellationToken: cancellationToken).ConfigureAwait(false);
                }

                break;

            case "crawl_region_npcs":
                {
                    var crawler = new WireNpcDialogCrawler(_driverBridge, _gameServerProxy, _options, _mapCatalog, _clientPakObstacles);
                    IReadOnlyList<string> crawled = await crawler.CrawlNearbyNpcsAsync(
                        radiusTiles: step.MaxRadiusTiles ?? 22,
                        maxBranches: step.MaxBranches ?? 0,
                        excludeNpcNames: step.ExcludeNpcNames,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    _logOut.WriteLine($"[WireScenario]   crawled {crawled.Count} NPCs: {string.Join(", ", crawled.Take(10))}{(crawled.Count > 10 ? "..." : string.Empty)}");
                }

                break;

            default:
                throw new ArgumentException($"Unsupported WireScenario step type '{step.Type}'.");
        }
    }

    private static async Task InvokeGmMenuPathAsync(
        JxHeadlessClient client,
        IReadOnlyList<int> optionPath,
        CancellationToken cancellationToken)
    {
        WorldDomSnapshot snap = await OpenGmTokenMenuAsync(client, cancellationToken).ConfigureAwait(false);
        for (int idx = 0; idx < optionPath.Count; idx++)
        {
            bool isLast = idx == optionPath.Count - 1;
            uint seqBefore = WireNpcDialogCrawler.MaxS2cSeq(snap);
            _ = await client.SelectDialogAsync(optionPath[idx], cancellationToken).ConfigureAwait(false);
            if (!isLast)
            {
                snap = await client.WaitUntilAsync(
                    s => s.Dialog.IsOpen && s.RecentS2c.Any(p => p.Seq > seqBefore && p.Opcode == ProtocolOpcodes.s2c_scriptaction),
                    TimeSpan.FromSeconds(6),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        _ = await WireNpcDialogCrawler.WaitTicksAsync(client, 20, cancellationToken).ConfigureAwait(false);
    }

    private static async Task InvokeGmNumberSequenceAsync(
        JxHeadlessClient client,
        IReadOnlyList<int> optionPath,
        IReadOnlyList<int> numbers,
        bool expectMapChange,
        CancellationToken cancellationToken)
    {
        WorldDomSnapshot snap = await OpenGmTokenMenuAsync(client, cancellationToken).ConfigureAwait(false);
        for (int idx = 0; idx < optionPath.Count; idx++)
        {
            bool isLastOption = idx == optionPath.Count - 1;
            uint seqBefore = WireNpcDialogCrawler.MaxS2cSeq(snap);
            _ = await client.SelectDialogAsync(optionPath[idx], cancellationToken).ConfigureAwait(false);
            if (!isLastOption)
            {
                snap = await client.WaitUntilAsync(
                    s => s.Dialog.IsOpen && s.RecentS2c.Any(p => p.Seq > seqBefore && p.Opcode == ProtocolOpcodes.s2c_scriptaction),
                    TimeSpan.FromSeconds(6),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else if (numbers.Count > 0)
            {
                snap = await client.WaitUntilAsync(
                    s => s.RecentS2c.Any(p => p.Seq > seqBefore && p.Opcode == ProtocolOpcodes.s2c_citywarextend),
                    TimeSpan.FromSeconds(6),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        for (int nIdx = 0; nIdx < numbers.Count; nIdx++)
        {
            bool isLastInput = nIdx == numbers.Count - 1;
            uint seqBefore = WireNpcDialogCrawler.MaxS2cSeq(snap);
            _ = await client.SubmitInputNumberAsync(numbers[nIdx], cancellationToken).ConfigureAwait(false);
            if (!isLastInput)
            {
                snap = await client.WaitUntilAsync(
                    s => s.RecentS2c.Any(p => p.Seq > seqBefore && p.Opcode == ProtocolOpcodes.s2c_citywarextend),
                    TimeSpan.FromSeconds(6),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        if (!expectMapChange)
        {
            _ = await WireNpcDialogCrawler.WaitTicksAsync(client, 20, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task GmTeleportToCoordsAsync(
        JxHeadlessClient client,
        int mapId,
        int tileX,
        int tileY,
        CancellationToken cancellationToken)
    {
        WorldDomSnapshot preSnap = await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
        if (preSnap.Player.MapId == mapId &&
            WireNpcDialogCrawler.Distance(preSnap.Player.MpsX, preSnap.Player.MpsY, tileX * 32, tileY * 32) <= 60 * 32)
        {
            try
            {
                await WireNpcDialogCrawler.WalkNearPositionAsync(
                    client,
                    (ushort)mapId,
                    (tileX * 32) + 16,
                    (tileY * 32) + 16,
                    _mapCatalog,
                    _clientPakObstacles,
                    stopRadiusCells: 1,
                    cancellationToken).ConfigureAwait(false);
                WorldDomSnapshot afterWalk = await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
                if (afterWalk.Player.MapId == mapId &&
                    WireNpcDialogCrawler.Distance(afterWalk.Player.MpsX, afterWalk.Player.MpsY, tileX * 32, tileY * 32) <= 128.0)
                {
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                // Fall back to GM coordinate teleport if short local walk is blocked by closed gates or terrain.
            }

            preSnap = await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
        }

        if (preSnap.Player.Doing != 0)
        {
            _ = await client.MoveToAsync(preSnap.Player.MpsX, preSnap.Player.MpsY, mode: 1, cancellationToken: cancellationToken).ConfigureAwait(false);
            _ = await WireNpcDialogCrawler.WaitTicksAsync(client, 8, cancellationToken).ConfigureAwait(false);
        }

        await InvokeGmNumberSequenceAsync(client, [5, 8], [mapId, tileX, tileY], expectMapChange: true, cancellationToken)
            .ConfigureAwait(false);

        _ = await client.WaitUntilAsync(
            s => s.Player.InWorld &&
                 s.Player.SyncReady &&
                 s.Player.MapId == mapId &&
                 Math.Abs(s.Player.MpsX - (tileX * 32)) <= 96 &&
                 Math.Abs(s.Player.MpsY - (tileY * 32)) <= 96,
            TimeSpan.FromSeconds(10),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await WireNpcDialogCrawler.WaitTicksAsync(client, 20, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<WorldDomSnapshot> OpenGmTokenMenuAsync(
        JxHeadlessClient client,
        CancellationToken cancellationToken)
    {
        WorldDomSnapshot snap = await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
        if (snap.Dialog.IsOpen)
        {
            await WireNpcDialogCrawler.CloseDialogSafelyAsync(client, cancellationToken).ConfigureAwait(false);
            snap = await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
        }

        InventoryItemSnapshot token = snap.Items.FirstOrDefault(static i =>
            i.Place == PlaceEquipRoom &&
            i.Genre == GmTokenGenre &&
            i.DetailType == GmTokenDetailType &&
            i.ParticularType == GmTokenParticularType)
            ?? throw new InvalidOperationException("GM Token (6,1,4258) not found in player EquipRoom.");

        uint seqBefore = WireNpcDialogCrawler.MaxS2cSeq(snap);
        _ = await client.UseItemAsync(token.ArrayIndex, token.Place, token.GridX, token.GridY, cancellationToken)
            .ConfigureAwait(false);

        return await client.WaitUntilAsync(
            s => s.Dialog.IsOpen && s.RecentS2c.Any(p => p.Seq > seqBefore && p.Opcode == ProtocolOpcodes.s2c_scriptaction),
            TimeSpan.FromSeconds(6),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static NpcSnapshot? FindBestMatchingNpc(
        IReadOnlyList<NpcSnapshot> npcs,
        string name,
        int? anchorTileX,
        int? anchorTileY,
        int? maxRadiusTiles,
        int playerMpsX,
        int playerMpsY)
    {
        string target = name.Trim();
        int ax = anchorTileX.HasValue ? anchorTileX.Value * 32 : playerMpsX;
        int ay = anchorTileY.HasValue ? anchorTileY.Value * 32 : playerMpsY;
        double maxDist = (maxRadiusTiles ?? 80) * 32.0;

        List<NpcSnapshot> candidates = npcs
            .Where(n => !n.IsSelf && string.Equals(n.Name.Trim(), target, StringComparison.OrdinalIgnoreCase))
            .Where(n => WireNpcDialogCrawler.Distance(n.MpsX, n.MpsY, ax, ay) <= maxDist)
            .ToList();

        if (candidates.Count == 0)
        {
            candidates = npcs
                .Where(n => !n.IsSelf && n.Name.Contains(target, StringComparison.OrdinalIgnoreCase))
                .Where(n => WireNpcDialogCrawler.Distance(n.MpsX, n.MpsY, ax, ay) <= maxDist)
                .ToList();
        }

        return candidates
            .OrderBy(n => WireNpcDialogCrawler.Distance(n.MpsX, n.MpsY, ax, ay))
            .FirstOrDefault();
    }

    private static GroundObjectSnapshot? FindBestMatchingObject(
        IReadOnlyList<GroundObjectSnapshot> objs,
        string? objectName,
        int? dataId,
        int? anchorTileX,
        int? anchorTileY,
        int? maxRadiusTiles,
        int playerMpsX,
        int playerMpsY)
    {
        int ax = anchorTileX.HasValue ? anchorTileX.Value * 32 : playerMpsX;
        int ay = anchorTileY.HasValue ? anchorTileY.Value * 32 : playerMpsY;
        double maxDist = (maxRadiusTiles ?? 25) * 32.0;

        IEnumerable<GroundObjectSnapshot> query = objs
            .Where(o => WireNpcDialogCrawler.Distance(o.MpsX, o.MpsY, ax, ay) <= maxDist);

        if (dataId.HasValue)
        {
            GroundObjectSnapshot? byDataId = query
                .Where(o => o.DataId == dataId.Value)
                .OrderBy(o => WireNpcDialogCrawler.Distance(o.MpsX, o.MpsY, ax, ay))
                .FirstOrDefault();
            if (byDataId is not null)
            {
                return byDataId;
            }
        }

        if (!string.IsNullOrWhiteSpace(objectName))
        {
            string target = objectName.Trim();
            GroundObjectSnapshot? exact = query
                .Where(o => string.Equals(o.Name.Trim(), target, StringComparison.OrdinalIgnoreCase))
                .OrderBy(o => WireNpcDialogCrawler.Distance(o.MpsX, o.MpsY, ax, ay))
                .FirstOrDefault();
            if (exact is not null)
            {
                return exact;
            }

            GroundObjectSnapshot? contains = query
                .Where(o => o.Name.Contains(target, StringComparison.OrdinalIgnoreCase))
                .OrderBy(o => WireNpcDialogCrawler.Distance(o.MpsX, o.MpsY, ax, ay))
                .FirstOrDefault();
            if (contains is not null)
            {
                return contains;
            }
        }

        return query
            .OrderBy(o => WireNpcDialogCrawler.Distance(o.MpsX, o.MpsY, ax, ay))
            .FirstOrDefault();
    }

    private static NpcSnapshot? FindBestMatchingCombatNpc(
        IReadOnlyList<NpcSnapshot> npcs,
        string name,
        int? anchorTileX,
        int? anchorTileY,
        int? maxRadiusTiles,
        int playerMpsX,
        int playerMpsY)
    {
        string target = name.Trim();
        int ax = anchorTileX.HasValue ? anchorTileX.Value * 32 : playerMpsX;
        int ay = anchorTileY.HasValue ? anchorTileY.Value * 32 : playerMpsY;
        double maxDist = (maxRadiusTiles ?? 30) * 32.0;

        List<NpcSnapshot> liveNpcs = npcs
            .Where(n => !n.IsSelf && n.Kind != 3 && n.Life > 0 && n.Doing != 14)
            .Where(n => WireNpcDialogCrawler.Distance(n.MpsX, n.MpsY, ax, ay) <= maxDist)
            .ToList();

        List<NpcSnapshot> exact = liveNpcs
            .Where(n => string.Equals(n.Name.Trim(), target, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exact.Count == 0)
        {
            exact = liveNpcs
                .Where(n => n.Name.Contains(target, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return exact
            .OrderBy(n => WireNpcDialogCrawler.Distance(n.MpsX, n.MpsY, ax, ay))
            .FirstOrDefault();
    }

    public static (int GridX, int GridY) ResolveShopBuyGridCell(
        IReadOnlyList<InventoryItemSnapshot> items,
        int requiredWidth = 2,
        int requiredHeight = 3)
    {
        const int BagCols = 6;
        const int BagRows = 6;

        for (int w = Math.Clamp(requiredWidth, 1, BagCols); w >= 1; w--)
        {
            int h = w == requiredWidth ? Math.Clamp(requiredHeight, 1, BagRows) : 1;
            for (int x = BagCols - w; x >= 0; x--)
            {
                for (int y = BagRows - h; y >= 0; y--)
                {
                    if (x == 0 && y == 0)
                    {
                        continue;
                    }

                    bool collides = false;
                    foreach (InventoryItemSnapshot item in items)
                    {
                        if (item.Place != PlaceEquipRoom)
                        {
                            continue;
                        }

                        int iw = Math.Max(1, item.Width);
                        int ih = Math.Max(1, item.Height);
                        if (x < item.GridX + iw && x + w > item.GridX &&
                            y < item.GridY + ih && y + h > item.GridY)
                        {
                            collides = true;
                            break;
                        }
                    }

                    if (!collides)
                    {
                        return (x, y);
                    }
                }
            }
        }

        return (5, 5);
    }

    private static string SanitizeFilePart(string raw)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var chars = raw.Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray();
        string cleaned = new(chars);
        return cleaned.Length > 79 ? cleaned[..79] : cleaned;
    }
}
