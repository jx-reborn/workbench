using JXReborn.DualParityServer.Configuration;
using JXReborn.DualParityServer.Diffing;
using JXReborn.DualParityServer.Reporting;
using JXReborn.DualParityServer.Session;
using JXReborn.DualParityServer.Standalone;
using Xunit;

namespace JXReborn.DualParityServer.Tests.Parity;

public sealed class StandaloneStackTests
{
    [Fact]
    public void ParseArgs_WhenStandaloneEnabled_ShouldEnableSpawnAndIsolateRebornPort()
    {
        DualParityOptions opts = DualParityOptions.ParseArgs(["--standalone", "--reuse-legacy", "--keep-containers"]);

        Assert.True(opts.Standalone);
        Assert.True(opts.SpawnClients);
        Assert.True(opts.ReuseLegacyContainer);
        Assert.True(opts.KeepContainers);
        Assert.Equal(15623, opts.RebornPort);
        Assert.Equal(16667, opts.RebornGameServerPort);
        Assert.Equal(15622, opts.LegacyPort);
        Assert.Equal(16666, opts.LegacyGameServerPort);
    }

    [Fact]
    public void ParseArgs_WhenExplicitRebornPortGiven_ShouldRespectExplicitRebornPort()
    {
        DualParityOptions opts = DualParityOptions.ParseArgs(["--standalone", "--reborn-port", "17777"]);

        Assert.True(opts.Standalone);
        Assert.Equal(17777, opts.RebornPort);
    }

    [Fact]
    public void RingLogBuffer_WhenExceedingCapacity_ShouldKeepMostRecentLines()
    {
        var buffer = new RingLogBuffer(capacity: 3);
        buffer.Append("line-1");
        buffer.Append("line-2");
        buffer.Append("line-3");
        buffer.Append("line-4");

        IReadOnlyList<string> lines = buffer.GetLines(10);
        Assert.Equal(3, lines.Count);
        Assert.Contains("line-2", lines[0]);
        Assert.Contains("line-4", lines[2]);
    }

    [Fact]
    public async Task ExportAsync_WithStandaloneOrchestrator_ShouldWriteServerLogFiles()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"jx-dual-standalone-test-{Guid.NewGuid():N}");
        try
        {
            var options = new DualParityOptions
            {
                Standalone = true,
                ReportOutputDir = tempDir
            };

            await using var session = new DualSessionManager(options, new SemanticDiffEngine());
            await using var standalone = new StandaloneStackOrchestrator(options, session.SessionId);
            var writer = new ParityReportWriter();

            ExportedReportPaths paths = await writer.ExportAsync(session, standalone: standalone);

            Assert.True(File.Exists(Path.Combine(paths.OutputDirectory, "logs", "legacy-server.log")));
            Assert.True(File.Exists(Path.Combine(paths.OutputDirectory, "logs", "reborn-server.log")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
