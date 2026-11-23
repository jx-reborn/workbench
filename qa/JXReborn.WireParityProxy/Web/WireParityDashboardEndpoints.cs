using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using JXReborn.DualParityServer.Diffing;
using JXReborn.DualParityServer.Standalone;
using JXReborn.WireParityProxy.Configuration;
using JXReborn.WireParityProxy.Mcp;
using JXReborn.WireParityProxy.Proxy;
using JXReborn.WireParityProxy.Reporting;

namespace JXReborn.WireParityProxy.Web;

/// <summary>
/// Minimal API endpoints serving the live dashboard, findings, action timeline, report export,
/// and one-click <c>*.capture.json</c> (L3A+) export.
/// </summary>
public static class WireParityDashboardEndpoints
{
    public static void Map(
        WebApplication app,
        WireParityProxyOptions options,
        BishopShadowProxy bishopProxy,
        GameServerShadowProxy gameServerProxy,
        ConcurrentQueue<ParityFinding> globalFindings,
        StandaloneStackOrchestrator? standalone = null,
        WireParityMcpToolDispatcher? mcpDispatcher = null)
    {
        app.MapGet("/", () => Results.Content(WireParityDashboardHtml.Page, "text/html; charset=utf-8"));

        app.MapGet("/api/status", () =>
        {
            WireParitySession? session = gameServerProxy.LatestSession;
            int totalFindings = globalFindings.Count + (session?.DiffCoordinator.GetFindings().Count ?? 0);
            (ManagedServerStatus Legacy, ManagedServerStatus Reborn)? managed = standalone?.GetStatuses();
            return Results.Json(new
            {
                standaloneMode = standalone is not null,
                standaloneReady = standalone?.IsReady ?? false,
                legacyServerStatus = managed?.Legacy.State,
                rebornServerStatus = managed?.Reborn.State,
                bishopPort = bishopProxy.BoundPort,
                bishopActive = bishopProxy.ActiveConnections,
                bishopHandoffs = bishopProxy.HandoffsCompleted,
                gameServerPort = gameServerProxy.BoundPort,
                gameServerActive = gameServerProxy.ActiveConnections,
                primaryBackend = options.PrimaryBackendName,
                shadowBackend = options.ShadowBackendName,
                sessionId = session?.SessionId,
                roleName = session?.RoleName,
                c2sFrames = session?.C2sFrames ?? 0,
                primaryS2cFrames = session?.PrimaryS2cFrames ?? 0,
                shadowS2cFrames = session?.ShadowS2cFrames ?? 0,
                findingsCount = totalFindings,
                protocolViolations = session?.ProtocolGate.ViolationCount ?? 0,
                isDrifted = session?.DiffCoordinator.LatestDrift.IsDrifted ?? false,
                driftSummary = session?.DiffCoordinator.LatestDrift.Summary ?? "Idle"
            });
        });

        app.MapGet("/api/findings", () =>
        {
            var all = new List<ParityFinding>(globalFindings);
            WireParitySession? session = gameServerProxy.LatestSession;
            if (session is not null)
            {
                all.AddRange(session.DiffCoordinator.GetFindings());
            }

            return Results.Json(all);
        });

        app.MapGet("/api/steps", () =>
        {
            WireParitySession? session = gameServerProxy.LatestSession;
            return Results.Json(session?.DiffCoordinator.GetSteps() ?? []);
        });

        app.MapPost("/api/drift/reset", () =>
        {
            gameServerProxy.LatestSession?.DiffCoordinator.ResetDrift();
            return Results.Json(new { ok = true });
        });

        app.MapPost("/api/report/export", async (CancellationToken ct) =>
        {
            WireParitySession? session = gameServerProxy.LatestSession;
            if (session is null)
            {
                return Results.BadRequest(new { error = "No active GameServer session yet." });
            }

            string dir = WireParityReportWriter.WriteAll(session, options.ReportOutputDir, globalFindings.ToArray());
            if (standalone is not null)
            {
                await standalone.WriteServerLogsAsync(dir, ct).ConfigureAwait(false);
            }

            return Results.Json(new { path = dir });
        });

        app.MapPost("/api/capture/export/{stepIndex:int}", (int stepIndex) =>
        {
            WireParitySession? session = gameServerProxy.LatestSession;
            if (session is null)
            {
                return Results.BadRequest(new { error = "No active GameServer session yet." });
            }

            string captureId = $"WPP_{session.SessionId.Replace("-", "_", StringComparison.Ordinal)}_S{stepIndex:D3}";
            if (captureId.Length > 79)
            {
                captureId = captureId[..79];
            }

            string outDir = Path.Combine(options.ReportOutputDir, "captures");
            string filePath = L3aCaptureExporter.ExportActionWindow(session, stepIndex, captureId, outDir);
            return Results.Json(new { path = filePath, captureId });
        });

        if (mcpDispatcher is not null)
        {
            app.MapGet("/api/world-state", async (CancellationToken ct) =>
            {
                var req = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = 1,
                    ["method"] = "tools/call",
                    ["params"] = new JsonObject
                    {
                        ["name"] = "get_world_state",
                        ["arguments"] = new JsonObject()
                    }
                };
                JsonObject? resp = await mcpDispatcher.HandleRequestAsync(req, ct).ConfigureAwait(false);
                return Results.Content(resp?.ToJsonString() ?? "{}", "application/json; charset=utf-8");
            });

            app.MapPost("/api/action", async (HttpRequest httpRequest, CancellationToken ct) =>
            {
                JsonObject? args = await JsonNode.ParseAsync(httpRequest.Body, cancellationToken: ct) as JsonObject ?? new JsonObject();
                var req = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = 1,
                    ["method"] = "tools/call",
                    ["params"] = new JsonObject
                    {
                        ["name"] = "perform_action",
                        ["arguments"] = args
                    }
                };
                JsonObject? resp = await mcpDispatcher.HandleRequestAsync(req, ct).ConfigureAwait(false);
                return Results.Content(resp?.ToJsonString() ?? "{}", "application/json; charset=utf-8");
            });

            app.MapGet("/api/scenarios", () =>
            {
                string scenarioDir = ResolveScenariosMapsDirectory();
                if (!Directory.Exists(scenarioDir))
                {
                    return Results.Json(Array.Empty<object>());
                }

                var files = Directory.GetFiles(scenarioDir, "*.scenario.json")
                    .OrderBy(static f => f, StringComparer.OrdinalIgnoreCase)
                    .Select(static f => new { file = Path.GetFileName(f), path = f })
                    .ToArray();
                return Results.Json(files);
            });

            app.MapPost("/api/scenarios/run", async (HttpRequest httpRequest, CancellationToken ct) =>
            {
                JsonObject? args = await JsonNode.ParseAsync(httpRequest.Body, cancellationToken: ct) as JsonObject ?? new JsonObject();
                var req = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = 1,
                    ["method"] = "tools/call",
                    ["params"] = new JsonObject
                    {
                        ["name"] = "run_scenario",
                        ["arguments"] = args
                    }
                };
                JsonObject? resp = await mcpDispatcher.HandleRequestAsync(req, ct).ConfigureAwait(false);
                return Results.Content(resp?.ToJsonString() ?? "{}", "application/json; charset=utf-8");
            });

            app.MapPost("/api/mcp", async (HttpRequest httpRequest, CancellationToken ct) =>
            {
                JsonObject? req = await JsonNode.ParseAsync(httpRequest.Body, cancellationToken: ct) as JsonObject;
                if (req is null)
                {
                    return Results.BadRequest(new { error = "Invalid JSON-RPC request body." });
                }

                JsonObject? resp = await mcpDispatcher.HandleRequestAsync(req, ct).ConfigureAwait(false);
                return resp is null
                    ? Results.NoContent()
                    : Results.Content(resp.ToJsonString(), "application/json; charset=utf-8");
            });
        }
    }

    private static string ResolveScenariosMapsDirectory()
    {
        foreach (string seed in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            DirectoryInfo? dir = new(seed);
            for (int depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
            {
                string direct = Path.Combine(dir.FullName, "Scenarios", "Maps");
                if (Directory.Exists(direct))
                {
                    return direct;
                }

                string nested = Path.Combine(dir.FullName, "qa", "JXReborn.WireParityProxy", "Scenarios", "Maps");
                if (Directory.Exists(nested))
                {
                    return nested;
                }
            }
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "Scenarios", "Maps");
    }
}
