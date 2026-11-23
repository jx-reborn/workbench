using JXReborn.Adapter.Legacy.Packets;
using JXReborn.ClientDriver;
using JXReborn.GameAssets.WorldMaps;
using JXReborn.WireParityProxy.Configuration;
using JXReborn.WireParityProxy.Driver;
using JXReborn.WireParityProxy.Navigation;
using JXReborn.WireParityProxy.Proxy;

namespace JXReborn.WireParityProxy.Scenarios;

/// <summary>
/// Crawls nearby dialog NPCs (<c>Kind == 3</c>) through the single <c>WireParityProxy</c> <c>game.exe</c> driver,
/// walking safely around terrain obstacles, exercising root/leaf dialog branches, and resetting dialogs cleanly.
/// </summary>
public sealed class WireNpcDialogCrawler
{
    private static readonly string[] SafeCloseKeywords =
    [
        "đóng",
        "kết thúc",
        "không giao dịch",
        "không ngồi",
        "không mua",
        "không cần",
        "rời khỏi",
        "hủy",
        "ta chỉ",
        "để sau",
        "để ta",
        "nhân tiện",
        "ghé qua",
        "thoát",
        "không"
    ];

    private static readonly string[] TeleportRiskKeywords =
    [
        "tới ",
        "đến ",
        "đi ",
        "truyền tống",
        "ngồi xe",
        "ngồi thuyền",
        "đảo",
        "trấn",
        "môn phái",
        "chiến trường",
        "tống kim",
        "luận kiếm",
        "lôi đài"
    ];

    private readonly PrimaryClientDriverBridge _driverBridge;
    private readonly GameServerShadowProxy _gameServerProxy;
    private readonly WireParityProxyOptions _options;
    private readonly IMapCatalog? _mapCatalog;
    private readonly ProxyClientPakObstacleReader? _clientPakObstacles;

    public WireNpcDialogCrawler(
        PrimaryClientDriverBridge driverBridge,
        GameServerShadowProxy gameServerProxy,
        WireParityProxyOptions options,
        IMapCatalog? mapCatalog = null,
        ProxyClientPakObstacleReader? clientPakObstacles = null)
    {
        ArgumentNullException.ThrowIfNull(driverBridge);
        ArgumentNullException.ThrowIfNull(gameServerProxy);
        ArgumentNullException.ThrowIfNull(options);
        _driverBridge = driverBridge;
        _gameServerProxy = gameServerProxy;
        _options = options;
        _mapCatalog = mapCatalog ?? ProxyMapCatalogLoader.TryGetDefault();
        _clientPakObstacles = clientPakObstacles;
    }

    public async Task<IReadOnlyList<string>> CrawlNearbyNpcsAsync(
        int radiusTiles = 20,
        int maxBranches = 0,
        IReadOnlyList<string>? excludeNpcNames = null,
        CancellationToken cancellationToken = default)
    {
        JxHeadlessClient client = _driverBridge.Client
            ?? throw new InvalidOperationException("No live game.exe driver attached. Call login first.");

        await CloseDialogSafelyAsync(client, cancellationToken).ConfigureAwait(false);
        WorldDomSnapshot initialSnap = await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
        int originX = initialSnap.Player.MpsX;
        int originY = initialSnap.Player.MpsY;
        ushort mapId = (ushort)Math.Clamp(initialSnap.Player.MapId, 0, ushort.MaxValue);
        double maxDistMps = Math.Max(4, radiusTiles) * 32.0;

        var excluded = new HashSet<string>(
            (excludeNpcNames ?? []).Select(static n => n.Trim()),
            StringComparer.OrdinalIgnoreCase);

        List<NpcSnapshot> targets = initialSnap.Npcs
            .Where(n => !n.IsSelf && n.Kind == 3 && !string.IsNullOrWhiteSpace(n.Name))
            .Where(n => !excluded.Contains(n.Name.Trim()))
            .Where(n => Distance(n.MpsX, n.MpsY, originX, originY) <= maxDistMps)
            .OrderBy(n => Distance(n.MpsX, n.MpsY, originX, originY))
            .ThenBy(n => n.DwId)
            .ToList();

        var crawled = new List<string>(targets.Count);
        var visitedDwIds = new HashSet<uint>();

        foreach (NpcSnapshot candidate in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visitedDwIds.Add(candidate.DwId))
            {
                continue;
            }

            await CloseDialogSafelyAsync(client, cancellationToken).ConfigureAwait(false);
            WorldDomSnapshot snap = await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
            if (snap.Player.MapId != mapId)
            {
                break;
            }

            NpcSnapshot? liveNpc = snap.Npcs.FirstOrDefault(n => n.DwId == candidate.DwId)
                ?? snap.Npcs.FirstOrDefault(n => !n.IsSelf && string.Equals(n.Name.Trim(), candidate.Name.Trim(), StringComparison.OrdinalIgnoreCase) && Distance(n.MpsX, n.MpsY, candidate.MpsX, candidate.MpsY) <= 128);
            if (liveNpc is null)
            {
                continue;
            }

            if (Distance(snap.Player.MpsX, snap.Player.MpsY, liveNpc.MpsX, liveNpc.MpsY) > 96.0)
            {
                try
                {
                    await WalkNearPositionAsync(client, mapId, liveNpc.MpsX, liveNpc.MpsY, _mapCatalog, _clientPakObstacles, stopRadiusCells: 2, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // Fall back to direct approach move if static obstacle grid blocks exact cell.
                    _ = await client.MoveToAsync(liveNpc.MpsX, liveNpc.MpsY, mode: 2, cancellationToken: cancellationToken).ConfigureAwait(false);
                    await WaitTicksAsync(client, 10, cancellationToken).ConfigureAwait(false);
                }
            }

            snap = await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
            uint seqBefore = MaxS2cSeq(snap);
            _ = await client.InteractNpcByDwIdAsync(liveNpc.DwId, cancellationToken).ConfigureAwait(false);
            snap = await WaitForInteractionResponseAsync(client, seqBefore, cancellationToken).ConfigureAwait(false);

            if (maxBranches > 0 && snap.Dialog.IsOpen && !snap.Dialog.IsTalk && snap.Dialog.Options.Count > 1)
            {
                int testedBranches = 0;
                for (int optIdx = 0; optIdx < snap.Dialog.Options.Count && testedBranches < maxBranches; optIdx++)
                {
                    string optText = snap.Dialog.Options[optIdx].Trim();
                    if (SafeCloseKeywords.Any(k => optText.Contains(k, StringComparison.OrdinalIgnoreCase)) ||
                        TeleportRiskKeywords.Any(k => optText.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    uint optSeqBefore = MaxS2cSeq(snap);
                    _ = await client.SelectDialogAsync(optIdx, cancellationToken).ConfigureAwait(false);
                    snap = await WaitForInteractionResponseAsync(client, optSeqBefore, cancellationToken).ConfigureAwait(false);
                    testedBranches++;
                    await CloseDialogSafelyAsync(client, cancellationToken).ConfigureAwait(false);

                    if (snap.Player.MapId != mapId)
                    {
                        break;
                    }

                    if (optIdx + 1 < snap.Dialog.Options.Count && testedBranches < maxBranches)
                    {
                        uint reopenSeq = MaxS2cSeq(await client.GetStateAsync(cancellationToken).ConfigureAwait(false));
                        _ = await client.InteractNpcByDwIdAsync(liveNpc.DwId, cancellationToken).ConfigureAwait(false);
                        snap = await WaitForInteractionResponseAsync(client, reopenSeq, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            await CloseDialogSafelyAsync(client, cancellationToken).ConfigureAwait(false);
            await WaitWindowFlushAsync(client, _options.ActionWindowMs, cancellationToken).ConfigureAwait(false);
            crawled.Add($"{liveNpc.Name.Trim()}@{liveNpc.MpsX / 32},{liveNpc.MpsY / 32}");
        }

        return crawled;
    }

    internal static async Task CloseDialogSafelyAsync(JxHeadlessClient client, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            WorldDomSnapshot snap = await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
            if (!snap.Dialog.IsOpen)
            {
                return;
            }

            uint seqBefore = MaxS2cSeq(snap);
            if (snap.Dialog.IsTalk || snap.Dialog.Options.Count == 0)
            {
                _ = await client.ConfirmTalkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                int closeIdx = PickSafeCloseOptionIndex(snap.Dialog.Options);
                _ = await client.SelectDialogAsync(closeIdx, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                _ = await client.WaitUntilAsync(
                    s => !s.Dialog.IsOpen || MaxS2cSeq(s) > seqBefore,
                    TimeSpan.FromSeconds(2),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return;
            }
        }
    }

    internal static async Task WalkNearPositionAsync(
        JxHeadlessClient client,
        ushort expectedMapId,
        int targetMpsX,
        int targetMpsY,
        IMapCatalog? mapCatalog,
        ProxyClientPakObstacleReader? pakObstacles,
        int stopRadiusCells,
        CancellationToken cancellationToken)
    {
        WorldDomSnapshot start = await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
        if (start.Player.MapId != expectedMapId || Distance(start.Player.MpsX, start.Player.MpsY, targetMpsX, targetMpsY) <= (stopRadiusCells * 32) + 24)
        {
            return;
        }

        IReadOnlyList<(int MpsX, int MpsY)> waypoints = mapCatalog is not null
            ? ProxyMapPathfinder.FindWaypointsNearPosition(
                mapCatalog,
                expectedMapId,
                start.Player.MpsX,
                start.Player.MpsY,
                targetMpsX,
                targetMpsY,
                stopRadiusCells: stopRadiusCells,
                clientPakObstacles: pakObstacles,
                maxFallbackRadiusCells: Math.Max(stopRadiusCells, 4))
            : [(targetMpsX, targetMpsY)];

        foreach ((int wx, int wy) in waypoints)
        {
            WorldDomSnapshot current = await MoveSingleSegmentAsync(client, expectedMapId, wx, wy, mode: 2, cancellationToken).ConfigureAwait(false);
            if (current.Player.MapId != expectedMapId)
            {
                break;
            }
        }
    }

    internal static async Task<WorldDomSnapshot> MoveSingleSegmentAsync(
        JxHeadlessClient client,
        ushort expectedMapId,
        int targetMpsX,
        int targetMpsY,
        int mode,
        CancellationToken cancellationToken,
        double arriveRadiusMps = 40.0,
        bool stopOnDialogOpen = false)
    {
        WorldDomSnapshot before = await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
        if (before.Player.MapId != expectedMapId || Distance(before.Player.MpsX, before.Player.MpsY, targetMpsX, targetMpsY) <= Math.Min(12.0, arriveRadiusMps))
        {
            return before;
        }

        _ = await client.MoveToAsync(targetMpsX, targetMpsY, mode: mode, cancellationToken: cancellationToken).ConfigureAwait(false);
        int lastX = before.Player.MpsX;
        int lastY = before.Player.MpsY;
        uint lastMovedTick = before.TickCount;

        try
        {
            return await client.WaitUntilAsync(
                s =>
                {
                    if (s.Player.MapId != expectedMapId ||
                        (stopOnDialogOpen && s.Dialog.IsOpen) ||
                        Distance(s.Player.MpsX, s.Player.MpsY, targetMpsX, targetMpsY) <= arriveRadiusMps)
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

                    return s.TickCount > lastMovedTick && (s.TickCount - lastMovedTick) >= 24u;
                },
                TimeSpan.FromSeconds(10),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task<WorldDomSnapshot> WaitForInteractionResponseAsync(
        JxHeadlessClient client,
        uint seqBefore,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.WaitUntilAsync(
                s => s.Dialog.IsOpen ||
                     s.RecentS2c.Any(p => p.Seq > seqBefore &&
                         (p.Opcode == ProtocolOpcodes.s2c_scriptaction ||
                          p.Opcode == ProtocolOpcodes.s2c_opensalebox ||
                          p.Opcode == ProtocolOpcodes.s2c_showladder ||
                          p.Opcode == ProtocolOpcodes.s2c_openstorebox ||
                          p.Opcode == ProtocolOpcodes.s2c_citywarextend)),
                TimeSpan.FromSeconds(3),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static Task<WorldDomSnapshot> WaitWindowFlushAsync(
        JxHeadlessClient client,
        int actionWindowMs,
        CancellationToken cancellationToken)
    {
        int ticksToWait = Math.Max(8, (actionWindowMs / 55) + 4);
        return WaitTicksAsync(client, ticksToWait, cancellationToken);
    }

    internal static async Task<WorldDomSnapshot> WaitTicksAsync(
        JxHeadlessClient client,
        int deltaTicks,
        CancellationToken cancellationToken)
    {
        WorldDomSnapshot snap = await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
        uint targetTick = snap.TickCount + (uint)Math.Max(1, deltaTicks);
        try
        {
            return await client.WaitUntilAsync(
                s => s.TickCount >= targetTick,
                TimeSpan.FromSeconds(Math.Max(5, (deltaTicks / 10) + 3)),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static uint MaxS2cSeq(WorldDomSnapshot snap) =>
        snap.RecentS2c.Count == 0 ? 0u : snap.RecentS2c.Max(static p => p.Seq);

    internal static double Distance(int x1, int y1, int x2, int y2) =>
        Math.Sqrt(((double)(x2 - x1) * (x2 - x1)) + ((double)(y2 - y1) * (y2 - y1)));

    private static int PickSafeCloseOptionIndex(IReadOnlyList<string> options)
    {
        for (int i = options.Count - 1; i >= 0; i--)
        {
            string lower = options[i].Trim();
            if (SafeCloseKeywords.Any(k => lower.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                return i;
            }
        }

        for (int i = options.Count - 1; i >= 0; i--)
        {
            if (options[i].Contains("Quay lại", StringComparison.OrdinalIgnoreCase) ||
                options[i].Contains("Trở lại", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return options.Count - 1;
    }
}
