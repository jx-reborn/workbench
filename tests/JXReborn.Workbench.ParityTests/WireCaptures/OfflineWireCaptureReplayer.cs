using JXReborn.Adapter.Legacy.Bishop;
using JXReborn.Adapter.Legacy.GameServer;
using JXReborn.Workbench.ParityTests.Fixtures;
using JXReborn.TestBase.WireCaptures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JXReborn.Integration.Tests.WireCaptures;

/// <summary>
/// Executes a verified <see cref="WireCaptureDocument"/> offline against <c>JXReborn</c> over real encrypted TCP sockets
/// (<c>Bishop</c> + <c>GameServer</c>) driven by a deterministic 18-Hz steppable clock (0 <c>game.exe</c>, 0 Docker, 0 <c>Task.Delay</c>).
/// </summary>
public static class OfflineWireCaptureReplayer
{
    public static async Task ReplayAndAssertAsync(
        string captureId,
        ITestOutputHelper? output = null,
        string? baselinesDirectoryOverride = null,
        CancellationToken cancellationToken = default)
    {
        WireCaptureDocument doc = WireCaptureStore.LoadAndVerify(captureId, baselinesDirectoryOverride);
        output?.WriteLine(
            $"[WireReplay] Loaded '{doc.Metadata.CaptureId}' ({doc.Metadata.Title}) | profile={doc.Metadata.OracleProfile} | sha256={doc.Metadata.ContentSha256[..12]}...");

        await using var harness = await WorkbenchIntegrationServerHarness.StartAsync(
            manualZoneTicks: true,
            enableTestGmToken: true);

        var evictionNotifier = harness.Services.GetRequiredService<LegacySessionEvictionNotifier>();
        var knownSessionIds = new HashSet<string>(StringComparer.Ordinal);
        var actors = new Dictionary<string, VirtualJxClient>(StringComparer.Ordinal);

        try
        {
            Assert.NotEmpty(doc.Actors);
            foreach (WireCaptureActorDefinition actorDef in doc.Actors)
            {
                GameServerClientSession? ResolveNewSession(Guid? _)
                {
                    foreach (GameServerClientSession s in evictionNotifier.GetActiveGameServerSessionsSnapshot())
                    {
                        if (!knownSessionIds.Contains(s.ConnectionId) && s.IsAttachedToWorld)
                        {
                            knownSessionIds.Add(s.ConnectionId);
                            return s;
                        }
                    }

                    return null;
                }

                VirtualJxClient vClient = await VirtualJxClient.LoginAndEnterWorldSteppableAsync(
                    actorDef,
                    harness.BishopPort,
                    harness.GameServerPort,
                    ResolveNewSession,
                    harness.StepWorldTicksAsync,
                    cancellationToken);

                actors[actorDef.ActorId] = vClient;
                await vClient.ExecuteGmSetupAsync(actorDef, harness.StepWorldTicksAsync, cancellationToken);
            }

            WireCaptureAssertionEngine.ResolveSymbols(
                doc.Symbols,
                actors,
                out Dictionary<string, uint> resolvedDwIds,
                out Dictionary<string, VirtualJxItemSlot> resolvedItems);

            var preFailures = new List<string>();
            foreach (WireCaptureActorDefinition actorDef in doc.Actors)
            {
                if (actorDef.ExpectedPreState is not null)
                {
                    WireCaptureAssertionEngine.VerifyActorState(
                        $"PreState({actorDef.ActorId})",
                        actors[actorDef.ActorId],
                        actorDef.ExpectedPreState,
                        resolvedDwIds,
                        preFailures);
                }
            }

            Assert.True(
                preFailures.Count == 0,
                $"Symmetric GM setup pre-state mismatch before replaying '{captureId}':{Environment.NewLine}{string.Join(Environment.NewLine, preFailures)}");

            var allStepFailures = new List<string>();
            Assert.NotEmpty(doc.Steps);
            foreach (WireCaptureStep step in doc.Steps)
            {
                Assert.NotEmpty(step.C2sFrames);
                if (step.UserActionDescription.Contains("0x8B", StringComparison.OrdinalIgnoreCase) ||
                    step.UserActionDescription.Contains("0x6A", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.NotEmpty(step.ExpectedS2cContract);
                }
                VirtualJxClient stepClient = actors[step.ActorId];
                RefreshResolvedItemLocations(doc.Symbols, actors, resolvedDwIds, resolvedItems);

                var stepObservedByActor = actors.Keys.ToDictionary(
                    static k => k,
                    static _ => new List<ObservedS2cFrame>(),
                    StringComparer.Ordinal);
                int currentTick = 0;

                foreach (IGrouping<int, WireCaptureC2sFrame> group in step.C2sFrames
                             .OrderBy(static f => f.TickOffsetFrames)
                             .GroupBy(static f => f.TickOffsetFrames))
                {
                    int targetOffset = Math.Max(currentTick, group.Key);
                    if (targetOffset > currentTick)
                    {
                        await StepAndDrainAllActorsAsync(
                            targetOffset - currentTick,
                            currentTick,
                            actors,
                            stepObservedByActor,
                            harness.StepWorldTicksAsync,
                            cancellationToken);
                        currentTick = targetOffset;
                    }

                    RefreshResolvedItemLocations(doc.Symbols, actors, resolvedDwIds, resolvedItems);
                    foreach (WireCaptureC2sFrame c2s in group)
                    {
                        byte[] payload = stepClient.BuildPatchedC2sPayload(c2s, resolvedDwIds, resolvedItems);
                        await stepClient.SendC2sAndAwaitEnqueuedAsync(payload, cancellationToken);
                    }
                }

                int remainingTicks = Math.Max(1, step.ObservationTicks - currentTick);
                await StepAndDrainAllActorsAsync(
                    remainingTicks,
                    currentTick,
                    actors,
                    stepObservedByActor,
                    harness.StepWorldTicksAsync,
                    cancellationToken);

                int totalObserved = stepObservedByActor.Values.Sum(static l => l.Count);
                output?.WriteLine(
                    $"[WireReplay:{step.StepId}] Sent {step.C2sFrames.Count} C2S frame(s), stepped {currentTick + remainingTicks} tick(s), observed {totalObserved} S2C frame(s) across {actors.Count} actor(s).");

                IReadOnlyDictionary<string, IReadOnlyList<ObservedS2cFrame>> readOnlyObserved =
                    stepObservedByActor.ToDictionary(
                        static kvp => kvp.Key,
                        static kvp => (IReadOnlyList<ObservedS2cFrame>)kvp.Value,
                        StringComparer.Ordinal);

                IReadOnlyList<string> stepFailures = WireCaptureAssertionEngine.VerifyStep(
                    step,
                    readOnlyObserved,
                    actors,
                    resolvedDwIds);
                allStepFailures.AddRange(stepFailures);
            }

            Assert.True(
                allStepFailures.Count == 0,
                $"Wire capture replay failed for '{captureId}' ({allStepFailures.Count} discrepancy/discrepancies):{Environment.NewLine}{string.Join(Environment.NewLine, allStepFailures)}");
        }
        finally
        {
            foreach (VirtualJxClient client in actors.Values)
            {
                await client.DisposeAsync();
            }
        }
    }

    private static async Task StepAndDrainAllActorsAsync(
        int tickCount,
        int baseTickOffset,
        IReadOnlyDictionary<string, VirtualJxClient> actors,
        IReadOnlyDictionary<string, List<ObservedS2cFrame>> sinksByActor,
        Func<int, CancellationToken, Task> stepWorldTicksAsync,
        CancellationToken cancellationToken)
    {
        for (int t = 0; t < tickCount; t++)
        {
            int tickOffset = baseTickOffset + t + 1;
            await stepWorldTicksAsync(1, cancellationToken);
            foreach ((string actorId, VirtualJxClient client) in actors)
            {
                await client.DrainAvailableS2cAsync(tickOffset, sinksByActor[actorId], cancellationToken);
            }
        }
    }

    private static void RefreshResolvedItemLocations(
        IReadOnlyDictionary<string, WireCaptureSymbolDefinition> symbolDefs,
        IReadOnlyDictionary<string, VirtualJxClient> actors,
        IReadOnlyDictionary<string, uint> resolvedDwIds,
        Dictionary<string, VirtualJxItemSlot> resolvedItems)
    {
        foreach ((string sym, WireCaptureSymbolDefinition def) in symbolDefs)
        {
            if (def.Kind == WireCaptureSymbolKind.Item &&
                resolvedDwIds.TryGetValue(sym, out uint id) &&
                actors.TryGetValue(def.ActorId, out VirtualJxClient? client) &&
                client.ItemsById.TryGetValue(id, out VirtualJxItemSlot? latest))
            {
                resolvedItems[sym] = latest;
            }
        }
    }
}
