using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using JXReborn.Adapter.Legacy.Ciphers;
using JXReborn.DualParityServer.Diffing;
using JXReborn.DualParityServer.Standalone;
using JXReborn.WireParityProxy.Configuration;
using JXReborn.WireParityProxy.Driver;
using JXReborn.WireParityProxy.Mcp;
using JXReborn.WireParityProxy.Proxy;
using JXReborn.WireParityProxy.Reporting;
using JXReborn.WireParityProxy.Scenarios;
using JXReborn.WireParityProxy.Web;
using JXReborn.WireParityProxy.Wire;

WireParityProxyOptions options = WireParityProxyOptions.ParseArgs(args);
if (options.McpMode && !string.IsNullOrWhiteSpace(options.McpProxyUrl))
{
    var bridge = new WireParityHttpMcpBridge(options.McpProxyUrl);
    await bridge.RunAsync(Console.In, Console.Out);
    return;
}

LegacyKeyTable keyTable = WireKeyTableLoader.Load(options);
TextWriter logOut = options.McpMode ? Console.Error : Console.Out;

string runId = $"wpp-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
var globalFindings = new ConcurrentQueue<ParityFinding>();
var handoffMap = new HandoffTicketMap(TimeSpan.FromSeconds(Math.Max(5, options.HandoffTicketTtlSeconds)));

StandaloneStackOrchestrator? standalone = options.StandaloneMode
    ? new StandaloneStackOrchestrator(options.ToStandaloneDualParityOptions(), runId)
    : null;

await using var driverBridge = new PrimaryClientDriverBridge();
await using var gameServerProxy = new GameServerShadowProxy(options, keyTable, handoffMap);
await using var bishopProxy = new BishopShadowProxy(
    options,
    keyTable,
    handoffMap,
    () => gameServerProxy.BoundPort,
    finding => globalFindings.Enqueue(finding));

try
{
    logOut.WriteLine($"[WireParityProxy] Run ID     : {runId}");
    logOut.WriteLine($"[WireParityProxy] Mode       : {(options.StandaloneMode ? "STANDALONE (All-in-One)" : "Attach to running servers")}{(options.McpMode ? " + MCP" : "")}");
    logOut.WriteLine($"[WireParityProxy] Primary    : {options.PrimaryBackendName}");
    logOut.WriteLine($"[WireParityProxy] Shadow     : {options.ShadowBackendName}");

    if (standalone is not null)
    {
        logOut.WriteLine("[WireParityProxy] Booting standalone stack (legacy-linux container T=0 + isolated JXReborn.Host T=0)...");
        await standalone.StartAsync();
        logOut.WriteLine("[WireParityProxy] Both backend servers are ready.");
    }

    gameServerProxy.Start();
    bishopProxy.Start();

    logOut.WriteLine($"[WireParityProxy] Bishop L7  : {options.BindHost}:{bishopProxy.BoundPort}");
    logOut.WriteLine($"[WireParityProxy] GS L7      : {options.BindHost}:{gameServerProxy.BoundPort}");

    if (options.SpawnClient)
    {
        logOut.WriteLine($"[WireParityProxy] Spawning single game.exe pointing to {options.BindHost}:{bishopProxy.BoundPort}...");
        await driverBridge.SpawnAgainstProxyAsync(options, bishopProxy.BoundPort, options.InteractiveWindow);
        logOut.WriteLine("[WireParityProxy] Single game.exe + driver attached.");
    }

    WebApplication BuildWebApp(int dashboardPort)
    {
        WebApplicationBuilder webBuilder = WebApplication.CreateBuilder(args);
        webBuilder.WebHost.UseUrls($"http://{options.BindHost}:{dashboardPort}");
        webBuilder.Logging.SetMinimumLevel(LogLevel.Warning);

        var dispatcher = new WireParityMcpToolDispatcher(
            options,
            bishopProxy,
            gameServerProxy,
            driverBridge,
            globalFindings,
            standalone);

        WebApplication builtApp = webBuilder.Build();
        WireParityDashboardEndpoints.Map(builtApp, options, bishopProxy, gameServerProxy, globalFindings, standalone, dispatcher);
        return builtApp;
    }

    async Task<(WebApplication App, WireParityMcpToolDispatcher Dispatcher, string Url)> StartDashboardAsync()
    {
        int targetPort = options.DashboardPort;
        WebApplication candidateApp = BuildWebApp(targetPort);
        try
        {
            await candidateApp.StartAsync();
        }
        catch (IOException) when (targetPort > 0 && !options.HasExplicitDashboardPort)
        {
            await candidateApp.DisposeAsync();
            candidateApp = BuildWebApp(0);
            await candidateApp.StartAsync();
        }

        string resolvedUrl = candidateApp.Urls.FirstOrDefault() ?? $"http://{options.BindHost}:{targetPort}";
        var resolvedDispatcher = new WireParityMcpToolDispatcher(
            options,
            bishopProxy,
            gameServerProxy,
            driverBridge,
            globalFindings,
            standalone);
        return (candidateApp, resolvedDispatcher, resolvedUrl);
    }

    if (options.McpMode)
    {
        var (app, mcpDispatcher, dashboardUrl) = await StartDashboardAsync();
        logOut.WriteLine($"[WireParityProxy] Dashboard  : {dashboardUrl}");
        var mcpHost = new WireParityMcpStdioHost(mcpDispatcher);
        logOut.WriteLine("[WireParityProxy] MCP stdio server ready.");
        await mcpHost.RunAsync(Console.In, Console.Out);
        await app.StopAsync();
        await app.DisposeAsync();
    }
    else if (!string.IsNullOrWhiteSpace(options.ScenarioPath))
    {
        var (app, _, dashboardUrl) = await StartDashboardAsync();
        logOut.WriteLine($"[WireParityProxy] Dashboard  : {dashboardUrl}");
        string fullScenarioPath = Path.GetFullPath(options.ScenarioPath);
        string scenarioJson = await File.ReadAllTextAsync(fullScenarioPath);
        WireParityScenario scenario = JsonSerializer.Deserialize<WireParityScenario>(scenarioJson)
            ?? throw new InvalidDataException($"Failed to deserialize scenario '{fullScenarioPath}'.");

        var runner = new WireScenarioRunner(options, driverBridge, bishopProxy, gameServerProxy, logOut: logOut);
        WireScenarioRunReport runReport = await runner.RunAsync(scenario);

        Directory.CreateDirectory(options.ReportOutputDir);
        string scenarioReportPath = Path.Combine(options.ReportOutputDir, "scenario-report.json");
        await File.WriteAllTextAsync(
            scenarioReportPath,
            JsonSerializer.Serialize(runReport, new JsonSerializerOptions { WriteIndented = true }));
        logOut.WriteLine(
            $"[WireParityProxy] Scenario '{runReport.ScenarioName}' finished: {runReport.PassedSteps}/{runReport.TotalSteps} steps OK, " +
            $"{runReport.StepFailures} step failures, {runReport.TotalFindings} parity findings ({runReport.CriticalFindings} crit).");
        logOut.WriteLine($"[WireParityProxy] Scenario summary saved to: {scenarioReportPath}");

        if (options.KeepAliveAfterScenario)
        {
            logOut.WriteLine("[WireParityProxy] --keep-alive specified; waiting until Ctrl+C...");
            await app.WaitForShutdownAsync();
        }
        else
        {
            await app.StopAsync();
            await app.DisposeAsync();
            if (runReport.StepFailures > 0)
            {
                Environment.ExitCode = 1;
            }
        }
    }
    else
    {
        var (app, _, dashboardUrl) = await StartDashboardAsync();
        logOut.WriteLine($"[WireParityProxy] Dashboard  : {dashboardUrl}");
        logOut.WriteLine("[WireParityProxy] Press Ctrl+C to stop, export reports, and clean up.");
        if (options.StandaloneMode && options.OpenBrowserOnStandalone && options.InteractiveWindow)
        {
            TryOpenBrowser(dashboardUrl);
        }

        await app.WaitForShutdownAsync();
        await app.DisposeAsync();
    }
}
finally
{
    WireParitySession? latest = gameServerProxy.LatestSession;
    ParityFinding[] globalList = globalFindings.ToArray();
    if (globalList.Length > 0)
    {
        logOut.WriteLine($"[WireParityProxy] Global/Bishop findings ({globalList.Length}):");
        foreach (ParityFinding gf in globalList)
        {
            logOut.WriteLine($"  - [{gf.Severity}] {gf.FindingId} {gf.FieldPath}: Legacy='{gf.LegacyValue}' vs Reborn='{gf.RebornValue}'");
        }
    }

    if (standalone is not null)
    {
        try
        {
            await standalone.WriteServerLogsAsync(options.ReportOutputDir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WireParityProxy] Failed to write standalone logs: {ex.Message}");
        }
    }

    if (latest is not null)
    {
        try
        {
            string reportDir = WireParityReportWriter.WriteAll(latest, options.ReportOutputDir, globalList);
            logOut.WriteLine($"[WireParityProxy] Reports exported to: {reportDir}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WireParityProxy] Failed to export final report: {ex.Message}");
        }
    }

    if (standalone is not null)
    {
        logOut.WriteLine("[WireParityProxy] Shutting down standalone servers...");
        await standalone.DisposeAsync();
    }
}

static void TryOpenBrowser(string url)
{
    try
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    catch
    {
        // Best-effort browser open.
    }
}
