using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JXReborn.Adapter.Legacy.Ciphers;
using JXReborn.Adapter.Legacy.Packets;
using JXReborn.DualParityServer.Diffing;
using JXReborn.WireParityProxy.Configuration;
using JXReborn.WireParityProxy.Driver;
using JXReborn.WireParityProxy.Mcp;
using JXReborn.WireParityProxy.Proxy;
using Xunit;

namespace JXReborn.WireParityProxy.Tests.Mcp;

public sealed class WireParityMcpToolDispatcherTests
{
    [Fact]
    public async Task InitializeListAndCallStatus_ShouldFollowMcpJsonRpcContract()
    {
        LegacyKeyTable keyTable = LegacyKeyTable.CreateSynthetic();
        var options = new WireParityProxyOptions
        {
            BishopListenPort = 0,
            GameServerListenPort = 0,
            AllowSyntheticKeyTableFallback = true
        };
        var handoffMap = new HandoffTicketMap();
        var globalFindings = new ConcurrentQueue<ParityFinding>();

        await using var driverBridge = new PrimaryClientDriverBridge();
        await using var gsProxy = new GameServerShadowProxy(options, keyTable, handoffMap);
        await using var bishopProxy = new BishopShadowProxy(options, keyTable, handoffMap, () => gsProxy.BoundPort);

        var dispatcher = new WireParityMcpToolDispatcher(
            options,
            bishopProxy,
            gsProxy,
            driverBridge,
            globalFindings);

        JsonObject? initResp = await dispatcher.HandleRequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize"
        });
        Assert.NotNull(initResp);
        Assert.Equal("jxreborn-wire-parity-proxy", initResp["result"]?["serverInfo"]?["name"]?.GetValue<string>());

        JsonObject? listResp = await dispatcher.HandleRequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 2,
            ["method"] = "tools/list"
        });
        Assert.NotNull(listResp);
        JsonArray? tools = listResp["result"]?["tools"] as JsonArray;
        Assert.NotNull(tools);
        Assert.Equal(9, tools.Count);

        JsonObject? resListResp = await dispatcher.HandleRequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 20,
            ["method"] = "resources/list"
        });
        Assert.NotNull(resListResp);
        Assert.Equal(WireParityMcpAppHtml.ResourceUri, resListResp["result"]?["resources"]?[0]?["uri"]?.GetValue<string>());

        JsonObject? resReadResp = await dispatcher.HandleRequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 21,
            ["method"] = "resources/read",
            ["params"] = new JsonObject { ["uri"] = WireParityMcpAppHtml.ResourceUri }
        });
        Assert.NotNull(resReadResp);
        Assert.Equal(WireParityMcpAppHtml.MimeType, resReadResp["result"]?["contents"]?[0]?["mimeType"]?.GetValue<string>());

        JsonObject? callResp = await dispatcher.HandleRequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 3,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "get_proxy_status",
                ["arguments"] = new JsonObject()
            }
        });
        Assert.NotNull(callResp);
        Assert.False(callResp["result"]?["isError"]?.GetValue<bool>());
        string statusJson = callResp["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? string.Empty;
        using JsonDocument doc = JsonDocument.Parse(statusJson);
        Assert.Equal("legacy-linux", doc.RootElement.GetProperty("primaryBackend").GetString());
        Assert.Equal("reborn-server", doc.RootElement.GetProperty("shadowBackend").GetString());
    }

    [Fact]
    public async Task StdioHost_ShouldProcessLineDelimitedJsonRpcAndIgnoreNotifications()
    {
        LegacyKeyTable keyTable = LegacyKeyTable.CreateSynthetic();
        var options = new WireParityProxyOptions { AllowSyntheticKeyTableFallback = true };
        var handoffMap = new HandoffTicketMap();

        await using var driverBridge = new PrimaryClientDriverBridge();
        await using var gsProxy = new GameServerShadowProxy(options, keyTable, handoffMap);
        await using var bishopProxy = new BishopShadowProxy(options, keyTable, handoffMap, () => gsProxy.BoundPort);

        var dispatcher = new WireParityMcpToolDispatcher(
            options,
            bishopProxy,
            gsProxy,
            driverBridge,
            new ConcurrentQueue<ParityFinding>());
        var host = new WireParityMcpStdioHost(dispatcher);

        string inputLines =
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":10,\"method\":\"ping\"}\n";
        using var reader = new StringReader(inputLines);
        using var writer = new StringWriter();

        await host.RunAsync(reader, writer);

        string output = writer.ToString().Trim();
        Assert.Single(output.Split('\n'));
        using JsonDocument doc = JsonDocument.Parse(output);
        Assert.Equal(10, doc.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task HttpMcpBridge_ShouldHandshakeLocallyAndForwardToolCallsToDaemon()
    {
        var handler = new StubHttpMessageHandler(_ =>
        {
            var daemonResp = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 2,
                ["result"] = new JsonObject
                {
                    ["tools"] = new JsonArray { new JsonObject { ["name"] = "get_proxy_status" } }
                }
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(daemonResp.ToJsonString(), Encoding.UTF8, "application/json")
            };
        });

        var bridge = new WireParityHttpMcpBridge("http://127.0.0.1:5100", handler);

        // 1. initialize responds immediately without touching HTTP daemon
        JsonObject? initResp = await bridge.HandleRequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize"
        });
        Assert.NotNull(initResp);
        Assert.Equal("jxreborn-wire-parity-bridge", initResp["result"]?["serverInfo"]?["name"]?.GetValue<string>());
        Assert.Equal(0, handler.CallCount);

        // 2. tools/list forwards to daemon /api/mcp
        JsonObject? listResp = await bridge.HandleRequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 2,
            ["method"] = "tools/list"
        });
        Assert.NotNull(listResp);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal("get_proxy_status", listResp["result"]?["tools"]?[0]?["name"]?.GetValue<string>());

        // 3. When daemon is unreachable, tools/call returns graceful isError=true instead of throwing
        var offlineBridge = new WireParityHttpMcpBridge(
            "http://127.0.0.1:5100",
            new StubHttpMessageHandler(_ => throw new HttpRequestException("Connection refused")));

        JsonObject? offlineResp = await offlineBridge.HandleRequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 3,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "get_proxy_status" }
        });
        Assert.NotNull(offlineResp);
        Assert.True(offlineResp["result"]?["isError"]?.GetValue<bool>());
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(responder(request));
        }
    }
}
