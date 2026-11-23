using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using JXReborn.ClientDriver;
using JXReborn.DualParityServer.Automation;
using JXReborn.DualParityServer.Configuration;
using JXReborn.DualParityServer.Diffing;
using JXReborn.DualParityServer.Proxy;
using JXReborn.DualParityServer.Reporting;
using JXReborn.DualParityServer.Session;
using JXReborn.DualParityServer.Tests.Fixtures;
using Xunit;

namespace JXReborn.DualParityServer.Tests.Parity;

public sealed class AutomationAndReportingTests
{
    [Fact]
    public async Task NpcDialogCrawlerAndReportWriter_WhenAuditingMixedNpcs_ShouldScoreMatchingDivergingAndMissingNpcs()
    {
        NpcSnapshot legacyNpcA = TestSnapshotFactory.CreateDialogNpc(101, "Xa Phu", 51488, 101856);
        NpcSnapshot legacyNpcB = TestSnapshotFactory.CreateDialogNpc(102, "Duoc Diem", 51150, 102713);
        NpcSnapshot legacyNpcC = TestSnapshotFactory.CreateDialogNpc(103, "Da Tau", 51184, 102000);

        NpcSnapshot rebornNpcA = TestSnapshotFactory.CreateDialogNpc(901, "Xa Phu", 51488, 101856);
        NpcSnapshot rebornNpcB = TestSnapshotFactory.CreateDialogNpc(902, "Duoc Diem", 51150, 102713);

        var legacyClient = new FakeDualTargetClient(
            "legacy",
            TestSnapshotFactory.Create(npcs: [legacyNpcA, legacyNpcB, legacyNpcC]));
        legacyClient.SetNpcDialog(101, TestSnapshotFactory.CreateDialog("Ngua cua ta chay nhanh nhat Ba Lang, nguoi muon di dau?", "Ket thuc doi thoai"));
        legacyClient.SetNpcDialog(102, TestSnapshotFactory.CreateDialog("Bon tiem deu la than duoc! Co benh se khoi benh.", "Giao dich", "Khong giao dich"));
        legacyClient.SetNpcDialog(103, TestSnapshotFactory.CreateDialog("Nhiem vu Da Tau", "Thoat"));

        var rebornClient = new FakeDualTargetClient(
            "reborn",
            TestSnapshotFactory.Create(npcs: [rebornNpcA, rebornNpcB]));
        rebornClient.SetNpcDialog(901, TestSnapshotFactory.CreateDialog("Ngua cua ta chay nhanh nhat Ba Lang, nguoi muon di dau?", "Ket thuc doi thoai"));
        rebornClient.SetNpcDialog(902, TestSnapshotFactory.CreateDialog("Tiem thuoc sai text", "Giao dich", "Khong giao dich"));

        string tempDir = Path.Combine(Path.GetTempPath(), $"jx-dual-parity-test-{Guid.NewGuid():N}");
        try
        {
            var options = new DualParityOptions
            {
                SettleDelayMs = 0,
                ReportOutputDir = tempDir
            };

            await using var session = new DualSessionManager(options, new SemanticDiffEngine(), legacyClient, rebornClient);
            var crawler = new NpcDialogCrawler(session);

            IReadOnlyList<NpcParityScoreEntry> scores = await crawler.CrawlNearbyNpcsAsync(maxBranchesPerNpc: 0);

            Assert.Equal(3, scores.Count);
            Assert.True(scores.Single(s => s.NpcName == "Xa Phu").Matched);
            Assert.False(scores.Single(s => s.NpcName == "Duoc Diem").Matched);
            Assert.False(scores.Single(s => s.NpcName == "Da Tau").Matched);

            var writer = new ParityReportWriter();
            ExportedReportPaths paths = await writer.ExportAsync(session);

            Assert.True(File.Exists(paths.FindingsJsonlPath));
            Assert.True(File.Exists(paths.MarkdownReportPath));
            Assert.True(File.Exists(paths.HtmlReportPath));

            string[] jsonLines = await File.ReadAllLinesAsync(paths.FindingsJsonlPath);
            Assert.NotEmpty(jsonLines);
            foreach (string line in jsonLines)
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                Assert.True(doc.RootElement.TryGetProperty("clusterKey", out _));
                Assert.True(doc.RootElement.TryGetProperty("repro", out _));
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DualTcpProxyRelay_WhenRelayingEchoPayload_ShouldForwardBytesBidirectionallyAndTrackStats()
    {
        using var echoListener = new TcpListener(IPAddress.Loopback, 0);
        echoListener.Start();
        int upstreamPort = ((IPEndPoint)echoListener.LocalEndpoint).Port;

        Task echoTask = Task.Run(async () =>
        {
            using TcpClient serverSide = await echoListener.AcceptTcpClientAsync();
            NetworkStream stream = serverSide.GetStream();
            byte[] buf = new byte[64];
            int n = await stream.ReadAsync(buf);
            if (n > 0)
            {
                await stream.WriteAsync(buf.AsMemory(0, n));
                await stream.FlushAsync();
            }
        });

        await using var proxy = new DualTcpProxyRelay("test-echo", listenPort: 0, "127.0.0.1", upstreamPort);
        proxy.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.BoundPort);
        NetworkStream clientStream = client.GetStream();

        byte[] payload = Encoding.ASCII.GetBytes("JX_DUAL_PROXY_PING");
        await clientStream.WriteAsync(payload);
        await clientStream.FlushAsync();

        byte[] reply = new byte[payload.Length];
        int totalRead = 0;
        while (totalRead < reply.Length)
        {
            int r = await clientStream.ReadAsync(reply.AsMemory(totalRead));
            if (r == 0)
            {
                break;
            }

            totalRead += r;
        }

        await echoTask;

        Assert.Equal("JX_DUAL_PROXY_PING", Encoding.ASCII.GetString(reply, 0, totalRead));
        TcpProxyStats stats = proxy.GetStats();
        Assert.Equal(payload.Length, stats.BytesClientToServer);
        Assert.Equal(payload.Length, stats.BytesServerToClient);
        Assert.True(stats.TotalConnections >= 1);
    }
}
