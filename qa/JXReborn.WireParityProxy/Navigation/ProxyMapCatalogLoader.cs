using JXReborn.GameAssets;
using JXReborn.GameAssets.Configuration;
using JXReborn.GameAssets.WorldMaps;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JXReborn.WireParityProxy.Navigation;

/// <summary>
/// Resolves and caches the compiled <see cref="IMapCatalog"/> from <c>pack-tieudaotu/dist/server/server.pack</c>
/// for deterministic obstacle-aware pathfinding without modifying process-wide environment variables.
/// </summary>
public static class ProxyMapCatalogLoader
{
    private static readonly Lazy<IMapCatalog?> CachedDefault = new(LoadDefaultCore, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IMapCatalog? TryGetDefault() => CachedDefault.Value;

    public static string? ResolvePackRootDirectory(string? configuredPackDir = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPackDir) && Directory.Exists(configuredPackDir))
        {
            return Path.GetFullPath(configuredPackDir);
        }

        foreach (string seed in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            DirectoryInfo? dir = new(seed);
            for (int depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
            {
                string directPack = Path.Combine(dir.FullName, "pack-tieudaotu");
                if (File.Exists(Path.Combine(directPack, "pack.json")))
                {
                    return directPack;
                }

                string siblingPack = Path.Combine(dir.FullName, "dist", "server", "server.pack");
                if (File.Exists(siblingPack) && File.Exists(Path.Combine(dir.FullName, "pack.json")))
                {
                    return dir.FullName;
                }
            }
        }

        return null;
    }

    private static IMapCatalog? LoadDefaultCore()
    {
        string? packRoot = ResolvePackRootDirectory();
        if (string.IsNullOrWhiteSpace(packRoot))
        {
            return null;
        }

        string dataDir = Path.Combine(packRoot, "data");
        string distServerDir = Path.Combine(packRoot, "dist", "server");
        if (!File.Exists(Path.Combine(distServerDir, "server.pack")))
        {
            return null;
        }

        var configDict = new Dictionary<string, string?>
        {
            [$"{GameAssetsOptions.SectionName}:{nameof(GameAssetsOptions.DataDirectory)}"] = dataDir,
            [$"{GameAssetsOptions.SectionName}:{nameof(GameAssetsOptions.ArtifactsDirectory)}"] = distServerDir,
            [$"{GameAssetsOptions.SectionName}:{nameof(GameAssetsOptions.DataSnapshotFile)}"] = "server.pack",
            [$"{GameAssetsOptions.SectionName}:{nameof(GameAssetsOptions.DefaultFallbackStarterVillageMapId)}"] = "53",
            [$"{GameAssetsOptions.SectionName}:{nameof(GameAssetsOptions.FallbackUnloadedStarterVillage)}"] = "true"
        };

        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddJXRebornGameAssets(config);
        ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IMapCatalog>();
    }
}
