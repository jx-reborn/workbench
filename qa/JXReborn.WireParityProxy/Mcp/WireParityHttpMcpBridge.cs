using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace JXReborn.WireParityProxy.Mcp;

/// <summary>
/// Thin, stateless stdio MCP bridge that forwards <c>tools/list</c> and <c>tools/call</c>
/// over HTTP to a running <c>JXReborn.WireParityProxy</c> daemon (<c>/api/mcp</c>),
/// keeping the AI Agent's MCP connection alive across proxy or server restarts.
/// </summary>
public sealed class WireParityHttpMcpBridge
{
    private readonly HttpClient _httpClient;
    private readonly string _mcpEndpointUrl;

    public WireParityHttpMcpBridge(string proxyBaseUrl, HttpMessageHandler? handler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proxyBaseUrl);
        string normalized = proxyBaseUrl.TrimEnd('/');
        _mcpEndpointUrl = $"{normalized}/api/mcp";
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonObject? request;
            try
            {
                request = JsonNode.Parse(line) as JsonObject;
            }
            catch
            {
                continue;
            }

            if (request is null)
            {
                continue;
            }

            JsonObject? response = await HandleRequestAsync(request, cancellationToken).ConfigureAwait(false);
            if (response is not null)
            {
                await output.WriteLineAsync(response.ToJsonString()).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<JsonObject?> HandleRequestAsync(JsonObject request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? method = request["method"]?.GetValue<string>();
        JsonNode? idNode = request["id"]?.DeepClone();

        if (idNode is null)
        {
            return null;
        }

        // Answer initialize & ping locally so MCP handshake never stalls if the daemon is restarting
        if (string.Equals(method, "initialize", StringComparison.Ordinal))
        {
            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = idNode,
                ["result"] = new JsonObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject(),
                        ["resources"] = new JsonObject()
                    },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "jxreborn-wire-parity-bridge",
                        ["version"] = "1.0.0"
                    }
                }
            };
        }

        if (string.Equals(method, "ping", StringComparison.Ordinal))
        {
            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = idNode,
                ["result"] = new JsonObject()
            };
        }

        try
        {
            using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
            using HttpResponseMessage httpResp = await _httpClient.PostAsync(_mcpEndpointUrl, content, cancellationToken)
                .ConfigureAwait(false);
            string body = await httpResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (JsonNode.Parse(body) is JsonObject jsonResp)
            {
                return jsonResp;
            }

            return CreateBridgeError(idNode, method, $"Unexpected response from {_mcpEndpointUrl} (HTTP {(int)httpResp.StatusCode}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return CreateBridgeError(
                idNode,
                method,
                $"WireParityProxy daemon is unreachable at {_mcpEndpointUrl}: {ex.Message}. Start or restart the proxy daemon and retry.");
        }
    }

    private static JsonObject CreateBridgeError(JsonNode idNode, string? method, string message)
    {
        if (string.Equals(method, "tools/call", StringComparison.Ordinal))
        {
            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = idNode,
                ["result"] = new JsonObject
                {
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = message
                        }
                    },
                    ["isError"] = true
                }
            };
        }

        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = idNode,
            ["error"] = new JsonObject
            {
                ["code"] = -32000,
                ["message"] = message
            }
        };
    }
}
