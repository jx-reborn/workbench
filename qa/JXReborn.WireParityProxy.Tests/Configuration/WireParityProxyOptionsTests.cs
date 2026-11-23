using JXReborn.DualParityServer.Configuration;
using JXReborn.WireParityProxy.Configuration;
using Xunit;

namespace JXReborn.WireParityProxy.Tests.Configuration;

public sealed class WireParityProxyOptionsTests
{
    [Fact]
    public void ParseArgs_WithStandaloneFlags_ShouldConfigureAllInOneOptionsAndMapToDualParityOptions()
    {
        string[] args =
        [
            "--standalone",
            "--reuse-legacy",
            "--keep-containers",
            "--legacy-profile", "v2",
            "--no-spawn",
            "--headless",
            "--no-browser",
            "--swap-primary"
        ];

        WireParityProxyOptions options = WireParityProxyOptions.ParseArgs(args);

        Assert.True(options.StandaloneMode);
        Assert.True(options.ReuseLegacyContainer);
        Assert.True(options.KeepContainers);
        Assert.Equal("v2", options.LegacyProfile);
        Assert.False(options.SpawnClient);
        Assert.False(options.InteractiveWindow);
        Assert.False(options.OpenBrowserOnStandalone);
        Assert.True(options.SwapPrimary);
        Assert.Equal("reborn-server", options.PrimaryBackendName);
        Assert.Equal("legacy-linux", options.ShadowBackendName);

        DualParityOptions mapped = options.ToStandaloneDualParityOptions();
        Assert.True(mapped.Standalone);
        Assert.True(mapped.ReuseLegacyContainer);
        Assert.True(mapped.KeepContainers);
        Assert.Equal("v2", mapped.LegacyProfile);
        Assert.Equal(options.LegacyBishopPort, mapped.LegacyPort);
        Assert.Equal(options.RebornBishopPort, mapped.RebornPort);
        Assert.Equal(options.LegacyGameServerPort, mapped.LegacyGameServerPort);
        Assert.Equal(options.RebornGameServerPort, mapped.RebornGameServerPort);
    }

    [Fact]
    public void ParseArgs_WithStandaloneOnly_ShouldDefaultToAutoSpawnInteractiveSingleClient()
    {
        WireParityProxyOptions options = WireParityProxyOptions.ParseArgs(["--standalone"]);

        Assert.True(options.StandaloneMode);
        Assert.True(options.SpawnClient);
        Assert.True(options.InteractiveWindow);
        Assert.True(options.OpenBrowserOnStandalone);
    }

    [Fact]
    public void ParseArgs_WithScenarioAndStandalone_ShouldSetScenarioPathAndHeadlessAutomationDefaults()
    {
        WireParityProxyOptions options = WireParityProxyOptions.ParseArgs(
            ["--standalone", "--scenario", @"Scenarios\Maps\078_tuong-duong.scenario.json", "--keep-alive"]);

        Assert.True(options.StandaloneMode);
        Assert.Equal(@"Scenarios\Maps\078_tuong-duong.scenario.json", options.ScenarioPath);
        Assert.True(options.KeepAliveAfterScenario);
        Assert.True(options.SpawnClient);
        Assert.False(options.InteractiveWindow);
        Assert.False(options.OpenBrowserOnStandalone);
    }
}
