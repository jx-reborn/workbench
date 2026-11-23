using JXReborn.DualParityServer.Configuration;

namespace JXReborn.WireParityProxy.Configuration;

/// <summary>
/// Runtime configuration for <c>JXReborn.WireParityProxy</c> (single-client, dual-backend L7 shadow proxy).
/// </summary>
public sealed class WireParityProxyOptions
{
    public string BindHost { get; set; } = "127.0.0.1";

    public int BishopListenPort { get; set; } = 16622;

    public int GameServerListenPort { get; set; } = 16668;

    public string AdvertisedGameServerHost { get; set; } = "127.0.0.1";

    public int DashboardPort { get; set; } = 5100;

    public string LegacyBishopHost { get; set; } = "127.0.0.1";

    public int LegacyBishopPort { get; set; } = 15622;

    public string RebornBishopHost { get; set; } = "127.0.0.1";

    public int RebornBishopPort { get; set; } = 15623;

    /// <summary>
    /// When <c>false</c> (default), <c>legacy-linux</c> is the Primary Oracle returning S2C to <c>game.exe</c>
    /// and <c>reborn-server</c> is the Shadow SUT. Set <c>true</c> (<c>--swap-primary</c>) to invert roles.
    /// </summary>
    public bool SwapPrimary { get; set; }

    public string? KeyTablePath { get; set; }

    public bool AllowSyntheticKeyTableFallback { get; set; }

    public int HandoffMatchTimeoutMs { get; set; } = 5000;

    public int HandoffTicketTtlSeconds { get; set; } = 60;

    public int ActionWindowMs { get; set; } = 350;

    public int PositionToleranceMps { get; set; } = 64;

    public int CriticalPositionDriftMps { get; set; } = 256;

    public string ReportOutputDir { get; set; } = Path.Combine(
        AppContext.BaseDirectory,
        "wire-parity-reports");

    public bool StandaloneMode { get; set; }

    public bool McpMode { get; set; }

    public string? McpProxyUrl { get; set; }

    public bool ReuseLegacyContainer { get; set; }

    public bool KeepContainers { get; set; }

    public string LegacyProfile { get; set; } = "v1";

    public int LegacyGameServerPort { get; set; } = 16666;

    public int RebornGameServerPort { get; set; } = 16667;

    public bool SpawnClient { get; set; }

    public bool InteractiveWindow { get; set; } = true;

    public bool OpenBrowserOnStandalone { get; set; } = true;

    public string? ScenarioPath { get; set; }

    public bool KeepAliveAfterScenario { get; set; }

    public string PrimaryBackendName => SwapPrimary ? "reborn-server" : "legacy-linux";

    public string ShadowBackendName => SwapPrimary ? "legacy-linux" : "reborn-server";

    public DualParityOptions ToStandaloneDualParityOptions() => new()
    {
        Standalone = true,
        LegacyHost = LegacyBishopHost,
        LegacyPort = LegacyBishopPort,
        LegacyGameServerPort = LegacyGameServerPort,
        LegacyProfile = LegacyProfile,
        ReuseLegacyContainer = ReuseLegacyContainer,
        KeepContainers = KeepContainers,
        RebornHost = RebornBishopHost,
        RebornPort = RebornBishopPort,
        RebornGameServerPort = RebornGameServerPort,
        DashboardPort = DashboardPort,
        ReportOutputDir = ReportOutputDir,
        InteractiveWindows = InteractiveWindow,
        OpenBrowserOnStandalone = OpenBrowserOnStandalone,
        PositionToleranceMps = PositionToleranceMps,
        CriticalPositionDriftMps = CriticalPositionDriftMps
    };

    public (string Host, int Port) GetPrimaryBishopEndpoint() =>
        SwapPrimary ? (RebornBishopHost, RebornBishopPort) : (LegacyBishopHost, LegacyBishopPort);

    public (string Host, int Port) GetShadowBishopEndpoint() =>
        SwapPrimary ? (LegacyBishopHost, LegacyBishopPort) : (RebornBishopHost, RebornBishopPort);

    public static WireParityProxyOptions ParseArgs(string[] args)
    {
        var options = new WireParityProxyOptions();
        bool explicitNoSpawn = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;

            switch (arg)
            {
                case "--bind-host":
                    if (Next() is { } bindHost)
                    {
                        options.BindHost = bindHost;
                    }

                    break;
                case "--bishop-port":
                    if (int.TryParse(Next(), out int bishopPort))
                    {
                        options.BishopListenPort = bishopPort;
                    }

                    break;
                case "--gs-port":
                    if (int.TryParse(Next(), out int gsPort))
                    {
                        options.GameServerListenPort = gsPort;
                    }

                    break;
                case "--dashboard-port":
                    if (int.TryParse(Next(), out int dashPort))
                    {
                        options.DashboardPort = dashPort;
                    }

                    break;
                case "--legacy-host":
                    if (Next() is { } legacyHost)
                    {
                        options.LegacyBishopHost = legacyHost;
                    }

                    break;
                case "--legacy-port":
                    if (int.TryParse(Next(), out int legacyPort))
                    {
                        options.LegacyBishopPort = legacyPort;
                    }

                    break;
                case "--reborn-host":
                    if (Next() is { } rebornHost)
                    {
                        options.RebornBishopHost = rebornHost;
                    }

                    break;
                case "--reborn-port":
                    if (int.TryParse(Next(), out int rebornPort))
                    {
                        options.RebornBishopPort = rebornPort;
                    }

                    break;
                case "--keytable":
                    options.KeyTablePath = Next();
                    break;
                case "--allow-synthetic-keys":
                    options.AllowSyntheticKeyTableFallback = true;
                    break;
                case "--swap-primary":
                    options.SwapPrimary = true;
                    break;
                case "--action-window-ms":
                    if (int.TryParse(Next(), out int winMs) && winMs > 0)
                    {
                        options.ActionWindowMs = winMs;
                    }

                    break;
                case "--report-dir":
                    if (Next() is { } reportDir)
                    {
                        options.ReportOutputDir = reportDir;
                    }

                    break;
                case "--standalone":
                    options.StandaloneMode = true;
                    options.SpawnClient = true;
                    break;
                case "--reuse-legacy":
                    options.ReuseLegacyContainer = true;
                    break;
                case "--keep-containers":
                    options.KeepContainers = true;
                    break;
                case "--legacy-profile":
                    if (Next() is { } profile)
                    {
                        options.LegacyProfile = profile;
                    }

                    break;
                case "--spawn":
                    options.SpawnClient = true;
                    break;
                case "--no-spawn":
                    options.SpawnClient = false;
                    explicitNoSpawn = true;
                    break;
                case "--headless":
                    options.InteractiveWindow = false;
                    break;
                case "--no-browser":
                    options.OpenBrowserOnStandalone = false;
                    break;
                case "--scenario":
                    if (Next() is { } scenarioPath)
                    {
                        options.ScenarioPath = scenarioPath;
                    }

                    break;
                case "--keep-alive":
                    options.KeepAliveAfterScenario = true;
                    break;
                case "--mcp":
                    options.McpMode = true;
                    options.OpenBrowserOnStandalone = false;
                    break;
                case "--proxy-url":
                    if (Next() is { } proxyUrl)
                    {
                        options.McpProxyUrl = proxyUrl;
                    }

                    break;
            }
        }

        if (!string.IsNullOrWhiteSpace(options.ScenarioPath))
        {
            if (!explicitNoSpawn)
            {
                options.SpawnClient = true;
            }

            options.InteractiveWindow = false;
            options.OpenBrowserOnStandalone = false;
        }

        return options;
    }
}
