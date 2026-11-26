using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using JXReborn.Adapter.Legacy.Ciphers;
using JXReborn.DualParityServer.Configuration;
using JXReborn.WireParityProxy.Configuration;
using JXReborn.WireParityProxy.Proxy;
using JXReborn.WireParityProxy.Scenarios;
using Xunit;

namespace JXReborn.WireParityProxy.Tests.Scenarios;

public sealed class ScenarioHeadlessPortCollisionTests
{
    [Fact]
    public void ParseArgs_WithMap53HeadlessScenarioWithoutExplicitPortFlags_ShouldDefaultDashboardAndProxyPortsToEphemeralZero()
    {
        string map53ScenarioPath = ResolveMap53ScenarioPath();
        WireParityScenario? map53Scenario = JsonSerializer.Deserialize<WireParityScenario>(File.ReadAllText(map53ScenarioPath));
        Assert.NotNull(map53Scenario);
        Assert.Equal((ushort)53, map53Scenario.MapId);
        Assert.NotNull(map53Scenario.Steps);
        Assert.NotEmpty(map53Scenario.Steps);

        string reportDir = Path.Combine(Path.GetTempPath(), $"wpp-parse-map53-{Guid.NewGuid():N}");
        WireParityProxyOptions implicitPortOptions = WireParityProxyOptions.ParseArgs(
        [
            "--standalone",
            "--headless",
            "--no-browser",
            "--report-dir", reportDir,
            "--scenario", map53ScenarioPath
        ]);

        Assert.True(implicitPortOptions.StandaloneMode);
        Assert.False(implicitPortOptions.KeepAliveAfterScenario);
        Assert.Equal(0, implicitPortOptions.DashboardPort);
        Assert.Equal(0, implicitPortOptions.BishopListenPort);
        Assert.Equal(0, implicitPortOptions.GameServerListenPort);

        DualParityOptions mappedDualOptions = implicitPortOptions.ToStandaloneDualParityOptions();
        Assert.Equal(0, mappedDualOptions.DashboardPort);

        WireParityProxyOptions explicitPortOptions = WireParityProxyOptions.ParseArgs(
        [
            "--standalone",
            "--headless",
            "--no-browser",
            "--dashboard-port", "5189",
            "--bishop-port", "16632",
            "--gs-port", "16678",
            "--report-dir", reportDir,
            "--scenario", map53ScenarioPath
        ]);

        Assert.Equal(5189, explicitPortOptions.DashboardPort);
        Assert.Equal(16632, explicitPortOptions.BishopListenPort);
        Assert.Equal(16678, explicitPortOptions.GameServerListenPort);
    }

    [Fact]
    public async Task ProgramMain_WhenDefaultPorts5100And16622And16668AreOccupiedDuringScenarioRun_ShouldBindEphemeralPortsAndWriteScenarioReportJson()
    {
        string map53ScenarioPath = ResolveMap53ScenarioPath();
        WireParityScenario? map53Scenario = JsonSerializer.Deserialize<WireParityScenario>(File.ReadAllText(map53ScenarioPath));
        Assert.NotNull(map53Scenario);
        Assert.Equal((ushort)53, map53Scenario.MapId);
        Assert.NotNull(map53Scenario.Steps);
        Assert.NotEmpty(map53Scenario.Steps);

        string tempRoot = Path.Combine(Path.GetTempPath(), $"wpp-port-collision-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        List<TcpListener> occupiedGuards = OccupyLoopbackPortsIfAvailable([5100, 16622, 16668]);
        try
        {
            string tempScenarioPath = Path.Combine(tempRoot, "053_ba-lang-huyen.zero-step.json");
            var smokeScenario = new WireParityScenario(
                Name: map53Scenario.Name,
                MapId: map53Scenario.MapId,
                Description: map53Scenario.Description,
                Account: map53Scenario.Account,
                Password: map53Scenario.Password,
                RoleName: map53Scenario.RoleName,
                NativePlaceId: map53Scenario.NativePlaceId,
                AutoExportFailingCaptures: false,
                Steps: []);
            await File.WriteAllTextAsync(tempScenarioPath, JsonSerializer.Serialize(smokeScenario));

            string reportDir = Path.Combine(tempRoot, "wf-iter-smoke");
            (int exitCode, string stdout, string stderr) = await RunProxyProcessAsync(
                [
                    "--no-spawn",
                    "--headless",
                    "--no-browser",
                    "--allow-synthetic-keys",
                    "--report-dir", reportDir,
                    "--scenario", tempScenarioPath
                ],
                closeStdinImmediately: false);

            string reportPath = Path.Combine(reportDir, "scenario-report.json");
            Assert.True(
                exitCode == 0,
                $"Expected WireParityProxy scenario run to exit with code 0 when default ports 5100/16622/16668 are occupied, got {exitCode}.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            Assert.True(
                File.Exists(reportPath),
                $"Expected '{reportPath}' to be written.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");

            WireScenarioRunReport? report = JsonSerializer.Deserialize<WireScenarioRunReport>(await File.ReadAllTextAsync(reportPath));
            Assert.NotNull(report);
            Assert.Equal(map53Scenario.Name, report.ScenarioName);
            Assert.Equal((ushort)53, report.TargetMapId);
            Assert.Equal(0, report.StepFailures);
            Assert.Equal(0, report.ProtocolViolations);
            Assert.Equal(0, report.CriticalFindings);
        }
        finally
        {
            DisposeListeners(occupiedGuards);
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task ProgramMain_WhenDefaultPorts5100And16622And16668AreOccupiedInMcpMode_ShouldFallbackToEphemeralPortsAndExitCleanlyOnStdinEof()
    {
        List<TcpListener> occupiedGuards = OccupyLoopbackPortsIfAvailable([5100, 16622, 16668]);
        try
        {
            (int exitCode, string stdout, string stderr) = await RunProxyProcessAsync(
                [
                    "--mcp",
                    "--no-spawn",
                    "--allow-synthetic-keys"
                ],
                closeStdinImmediately: true);

            Assert.True(
                exitCode == 0,
                $"Expected WireParityProxy --mcp to fall back when default ports 5100/16622/16668 are occupied, got {exitCode}.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            Assert.Contains("[WireParityProxy] MCP stdio server ready.", stderr, StringComparison.Ordinal);
        }
        finally
        {
            DisposeListeners(occupiedGuards);
        }
    }

    [Fact]
    public async Task ProgramMain_WhenDashboardPort5100IsOccupied_ShouldFallbackWhenImplicitAndFailWhenExplicitlyRequestedViaCli()
    {
        List<TcpListener> occupied5100Guard = OccupyLoopbackPortsIfAvailable([5100]);
        using TcpListener explicitBusyDashboardGuard = CreateExclusiveLoopbackListener(0);
        int explicitBusyDashboardPort = ((IPEndPoint)explicitBusyDashboardGuard.LocalEndpoint).Port;

        try
        {
            (int implicitExitCode, string implicitStdout, string implicitStderr) = await RunProxyProcessAsync(
                [
                    "--mcp",
                    "--no-spawn",
                    "--allow-synthetic-keys",
                    "--bishop-port", "0",
                    "--gs-port", "0"
                ],
                closeStdinImmediately: true);

            Assert.True(
                implicitExitCode == 0,
                $"Expected implicit DashboardPort 5100 collision to fall back to ephemeral Kestrel port, got {implicitExitCode}.\nSTDOUT:\n{implicitStdout}\nSTDERR:\n{implicitStderr}");
            Assert.Contains("[WireParityProxy] MCP stdio server ready.", implicitStderr, StringComparison.Ordinal);

            (int explicitExitCode, _, _) = await RunProxyProcessAsync(
                [
                    "--mcp",
                    "--no-spawn",
                    "--allow-synthetic-keys",
                    "--bishop-port", "0",
                    "--gs-port", "0",
                    "--dashboard-port", explicitBusyDashboardPort.ToString(CultureInfo.InvariantCulture)
                ],
                closeStdinImmediately: true);

            Assert.NotEqual(0, explicitExitCode);
        }
        finally
        {
            DisposeListeners(occupied5100Guard);
        }
    }

    [Fact]
    public async Task ShadowProxies_WhenConfiguredPortIsOccupied_ShouldFallbackWhenImplicitAndThrowWhenExplicitlyPassedViaCli()
    {
        LegacyKeyTable keyTable = LegacyKeyTable.CreateSynthetic();
        using TcpListener occupiedBishopGuard = CreateExclusiveLoopbackListener(0);
        using TcpListener occupiedGsGuard = CreateExclusiveLoopbackListener(0);
        int occupiedBishopPort = ((IPEndPoint)occupiedBishopGuard.LocalEndpoint).Port;
        int occupiedGsPort = ((IPEndPoint)occupiedGsGuard.LocalEndpoint).Port;

        var implicitOptions = new WireParityProxyOptions
        {
            BindHost = "127.0.0.1",
            BishopListenPort = occupiedBishopPort,
            GameServerListenPort = occupiedGsPort,
            AllowSyntheticKeyTableFallback = true
        };

        var handoffMap = new HandoffTicketMap();
        await using (var gsProxy = new GameServerShadowProxy(implicitOptions, keyTable, handoffMap))
        await using (var bishopProxy = new BishopShadowProxy(implicitOptions, keyTable, handoffMap, () => gsProxy.BoundPort))
        {
            gsProxy.Start();
            bishopProxy.Start();

            Assert.True(gsProxy.BoundPort > 0);
            Assert.NotEqual(occupiedGsPort, gsProxy.BoundPort);
            Assert.True(bishopProxy.BoundPort > 0);
            Assert.NotEqual(occupiedBishopPort, bishopProxy.BoundPort);
        }

        WireParityProxyOptions explicitOptions = WireParityProxyOptions.ParseArgs(
        [
            "--bind-host", "127.0.0.1",
            "--bishop-port", occupiedBishopPort.ToString(CultureInfo.InvariantCulture),
            "--gs-port", occupiedGsPort.ToString(CultureInfo.InvariantCulture),
            "--allow-synthetic-keys"
        ]);

        await using var explicitGsProxy = new GameServerShadowProxy(explicitOptions, keyTable, handoffMap);
        await using var explicitBishopProxy = new BishopShadowProxy(explicitOptions, keyTable, handoffMap, () => explicitGsProxy.BoundPort);

        Assert.ThrowsAny<SocketException>(() => explicitGsProxy.Start());
        Assert.ThrowsAny<SocketException>(() => explicitBishopProxy.Start());
    }

    private static string ResolveMap53ScenarioPath()
    {
        string[] seeds = [AppContext.BaseDirectory, Directory.GetCurrentDirectory()];
        Assert.NotEmpty(seeds);
        foreach (string seed in seeds)
        {
            DirectoryInfo? dir = new(seed);
            for (int depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
            {
                string direct = Path.Combine(dir.FullName, "qa", "JXReborn.WireParityProxy", "Scenarios", "Maps", "053_ba-lang-huyen.scenario.json");
                if (File.Exists(direct))
                {
                    return direct;
                }

                string nested = Path.Combine(dir.FullName, "workbench", "qa", "JXReborn.WireParityProxy", "Scenarios", "Maps", "053_ba-lang-huyen.scenario.json");
                if (File.Exists(nested))
                {
                    return nested;
                }
            }
        }

        throw new FileNotFoundException("Could not locate workbench/qa/JXReborn.WireParityProxy/Scenarios/Maps/053_ba-lang-huyen.scenario.json.");
    }

    private static TcpListener CreateExclusiveLoopbackListener(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Server.ExclusiveAddressUse = true;
        listener.Start();
        return listener;
    }

    private static List<TcpListener> OccupyLoopbackPortsIfAvailable(int[] ports)
    {
        Assert.NotEmpty(ports);
        var bound = new List<TcpListener>(ports.Length);
        foreach (int port in ports)
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            try
            {
                listener.Server.ExclusiveAddressUse = true;
                listener.Start();
                bound.Add(listener);
            }
            catch (SocketException)
            {
                listener.Stop();
                // Port is already occupied by another process on the host (e.g. 5100), which satisfies the collision precondition.
            }
        }

        return bound;
    }

    private static void DisposeListeners(List<TcpListener> listeners)
    {
        for (int i = 0; i < listeners.Count; i++)
        {
            try
            {
                listeners[i].Stop();
            }
            catch
            {
                // Best-effort listener cleanup.
            }
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProxyProcessAsync(
        IReadOnlyList<string> proxyArgs,
        bool closeStdinImmediately)
    {
        Assert.NotEmpty(proxyArgs);
        string proxyDllPath = typeof(WireParityProxyOptions).Assembly.Location;
        Assert.True(File.Exists(proxyDllPath), $"WireParityProxy assembly not found at '{proxyDllPath}'.");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(proxyDllPath)!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(proxyDllPath);
        for (int i = 0; i < proxyArgs.Count; i++)
        {
            psi.ArgumentList.Add(proxyArgs[i]);
        }

        using Process proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet process for JXReborn.WireParityProxy.");

        if (closeStdinImmediately)
        {
            proc.StandardInput.Close();
        }

        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort kill on hang.
            }

            throw new TimeoutException("JXReborn.WireParityProxy timed out after 20 seconds.");
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        return (proc.ExitCode, stdout, stderr);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp directory cleanup.
        }
    }
}
