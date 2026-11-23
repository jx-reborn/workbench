using JXReborn.Adapter.Legacy;
using JXReborn.Adapter.Legacy.Bishop;
using JXReborn.Adapter.Legacy.Ciphers;
using JXReborn.Adapter.Legacy.GameServer;
using JXReborn.Application;
using JXReborn.Application.Configuration;
using JXReborn.GameAssets;
using JXReborn.GameAssets.Configuration;
using JXReborn.Infrastructure;
using JXReborn.Scripts.Hosting;
using JXReborn.TestBase.Fixtures;
using JXReborn.Workbench.ParityTests.Configuration;
using JXReborn.Workbench.Scripts.Items;
using JXReborn.Packs.TieuDaoTu.Hosting;
using JXReborn.ZoneEngine;
using JXReborn.ZoneEngine.WorldZones;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace JXReborn.Workbench.ParityTests.Fixtures;

public sealed class WorkbenchIntegrationServerHarness : IAsyncDisposable
{
    private readonly string _sqliteDbPath;
    private readonly IHost _host;
    private readonly FakeTimeProvider? _fakeTimeProvider;
    private readonly ZoneClusterRouter _clusterRouter;
    private readonly bool _manualZoneTicks;

    public int BishopPort { get; private set; }

    public int GameServerPort { get; private set; }

    public IServiceProvider Services => _host.Services;

    public bool IsManualZoneTickMode => _manualZoneTicks;

    private WorkbenchIntegrationServerHarness(
        string sqliteDbPath,
        IHost host,
        FakeTimeProvider? fakeTimeProvider,
        ZoneClusterRouter clusterRouter,
        bool manualZoneTicks)
    {
        _sqliteDbPath = sqliteDbPath;
        _host = host;
        _fakeTimeProvider = fakeTimeProvider;
        _clusterRouter = clusterRouter;
        _manualZoneTicks = manualZoneTicks;
    }

    public static async Task<WorkbenchIntegrationServerHarness> StartAsync(
        bool autoProvisionAccounts = true,
        bool fallbackUnloadedStarterVillage = true,
        ushort[]? excludedWorldSetMapIds = null,
        bool manualZoneTicks = false,
        bool enableTestGmToken = false)
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"jxreborn_workbench_{Guid.NewGuid():N}.db");

        var configDict = new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "Sqlite",
            ["Persistence:ConnectionString"] = $"Data Source={dbPath};Default Timeout=30;Pooling=False;",
            ["Persistence:AutoInitializeDatabase"] = "true",
            ["GameAssets:DataDirectory"] = WorkbenchPaths.PackDataDirectory,
            ["GameAssets:ArtifactsDirectory"] = WorkbenchPaths.PackServerDistDirectory,
            ["GameAssets:DataSnapshotFile"] = WorkbenchPaths.PackSnapshotFileName,
            ["GameAssets:DefaultFallbackStarterVillageMapId"] = "53",
            ["GameAssets:FallbackUnloadedStarterVillage"] = fallbackUnloadedStarterVillage.ToString(),
            ["Application:AutoProvisionAccounts"] = autoProvisionAccounts.ToString(),
            ["Application:AdvertisedGameServerHost"] = "127.0.0.1",
            ["Application:AdvertisedGameServerPort"] = "0",
            ["ZoneEngine:TicksPerSecond"] = "18",
            ["ZoneEngine:HeartbeatIntervalSeconds"] = manualZoneTicks ? "3600" : "5",
            ["ZoneEngine:HeartbeatTimeoutSeconds"] = manualZoneTicks ? "7200" : "15",
            ["ZoneEngine:EnableTestGmToken"] = enableTestGmToken.ToString(),
            ["ZoneEngine:AutoGrantGmTokenOnLogin"] = enableTestGmToken.ToString(),
            ["ZoneEngine:TestGmTokenItemCode"] = TestGmTokenItemScript.GmTokenItemCode,
            ["ZoneEngine:TestGmTokenScriptCode"] = TestGmTokenItemScript.GmTokenScriptPath,
            ["LegacyGateway:BindAddress"] = "127.0.0.1",
            ["LegacyGateway:BishopPort"] = "0",
            ["LegacyGateway:GameServerPort"] = "0",
            ["LegacyGateway:AllowSyntheticKeyTableFallback"] = "true"
        };

        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(configDict);

        FakeTimeProvider? fakeTime = null;
        if (manualZoneTicks)
        {
            fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
            builder.Services.AddSingleton<TimeProvider>(fakeTime);
        }

        builder.Services.AddJXRebornInfrastructure(builder.Configuration);

        bool canUseSharedMapCatalog = fallbackUnloadedStarterVillage && (excludedWorldSetMapIds is null or { Length: 0 });
        if (canUseSharedMapCatalog)
        {
            builder.Services
                .AddOptions<GameAssetsOptions>()
                .Bind(builder.Configuration.GetSection(GameAssetsOptions.SectionName));
            SharedGameAssetsFixture.Default.RegisterSharedCatalogsInto(builder.Services);
        }
        else
        {
            builder.Services.AddJXRebornGameAssets(builder.Configuration, opts =>
            {
                if (excludedWorldSetMapIds is { Length: > 0 })
                {
                    opts.ExcludedWorldSetMapIds = excludedWorldSetMapIds;
                }
            });
        }

        builder.Services
            .AddJXRebornApplication(builder.Configuration)
            .AddJXRebornScripts(
                builder.Configuration,
                preloadedAssemblies: [typeof(TieuDaoTuScriptCodeResolver).Assembly, typeof(TestGmTokenItemScript).Assembly])
            .AddJXRebornZoneEngine(builder.Configuration)
            .AddSingleton(LegacyKeyTable.CreateSynthetic())
            .AddJXRebornLegacyAdapter(builder.Configuration);

        if (manualZoneTicks)
        {
            ServiceDescriptor? zoneHosted = builder.Services.FirstOrDefault(
                static d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(ZoneEngineHostedService));
            if (zoneHosted is not null)
            {
                builder.Services.Remove(zoneHosted);
            }
        }

        IHost host = builder.Build();
        await host.StartAsync();

        var bishopListener = host.Services.GetRequiredService<BishopTcpListenerHostedService>();
        var gsListener = host.Services.GetRequiredService<GameServerTcpListenerHostedService>();
        var clusterRouter = host.Services.GetRequiredService<ZoneClusterRouter>();

        ApplicationOptions appOptions = host.Services.GetRequiredService<IOptions<ApplicationOptions>>().Value;
        appOptions.AdvertisedGameServerPort = (ushort)gsListener.BoundPort;

        return new WorkbenchIntegrationServerHarness(dbPath, host, fakeTime, clusterRouter, manualZoneTicks)
        {
            BishopPort = bishopListener.BoundPort,
            GameServerPort = gsListener.BoundPort
        };
    }

    public async Task StepWorldTicksAsync(int tickFrames = 1, CancellationToken cancellationToken = default)
    {
        if (!_manualZoneTicks)
        {
            throw new InvalidOperationException("StepWorldTicksAsync requires WorkbenchIntegrationServerHarness started with manualZoneTicks: true.");
        }

        TimeSpan singleTickDuration = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 18);
        for (int t = 0; t < tickFrames; t++)
        {
            _fakeTimeProvider?.Advance(singleTickDuration);
            foreach (MapZoneWorker worker in _clusterRouter.Workers)
            {
                await worker.StepTickIfActiveAsync(cancellationToken);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_manualZoneTicks)
            {
                var evictionNotifier = _host.Services.GetService<LegacySessionEvictionNotifier>();
                if (evictionNotifier is not null)
                {
                    foreach (GameServerClientSession session in evictionNotifier.GetActiveGameServerSessionsSnapshot())
                    {
                        session.MarkCleanLogoutCompleted();
                    }
                }
            }

            await _host.StopAsync(TimeSpan.FromSeconds(3));
        }
        catch
        {
        }

        _host.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (string candidate in new[] { _sqliteDbPath, _sqliteDbPath + "-wal", _sqliteDbPath + "-shm" })
        {
            try
            {
                if (File.Exists(candidate))
                {
                    File.Delete(candidate);
                }
            }
            catch
            {
            }
        }
    }
}
