using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using JXReborn.ClientDriver;
using JXReborn.DualParityServer.Diffing;
using JXReborn.DualParityServer.Standalone;
using JXReborn.WireParityProxy.Configuration;
using JXReborn.WireParityProxy.Driver;
using JXReborn.WireParityProxy.Proxy;
using JXReborn.WireParityProxy.Reporting;

namespace JXReborn.WireParityProxy.Mcp;

/// <summary>
/// Implements Model Context Protocol (MCP) JSON-RPC 2.0 tool discovery and execution
/// for AI Agents driving <see cref="GameServerShadowProxy"/> and <see cref="PrimaryClientDriverBridge"/>.
/// </summary>
public sealed class WireParityMcpToolDispatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly WireParityProxyOptions _options;
    private readonly BishopShadowProxy _bishopProxy;
    private readonly GameServerShadowProxy _gameServerProxy;
    private readonly PrimaryClientDriverBridge _driverBridge;
    private readonly ConcurrentQueue<ParityFinding> _globalFindings;
    private readonly StandaloneStackOrchestrator? _standalone;

    public WireParityMcpToolDispatcher(
        WireParityProxyOptions options,
        BishopShadowProxy bishopProxy,
        GameServerShadowProxy gameServerProxy,
        PrimaryClientDriverBridge driverBridge,
        ConcurrentQueue<ParityFinding> globalFindings,
        StandaloneStackOrchestrator? standalone = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(bishopProxy);
        ArgumentNullException.ThrowIfNull(gameServerProxy);
        ArgumentNullException.ThrowIfNull(driverBridge);
        ArgumentNullException.ThrowIfNull(globalFindings);
        _options = options;
        _bishopProxy = bishopProxy;
        _gameServerProxy = gameServerProxy;
        _driverBridge = driverBridge;
        _globalFindings = globalFindings;
        _standalone = standalone;
    }

    public async Task<JsonObject?> HandleRequestAsync(JsonObject request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string? method = request["method"]?.GetValue<string>();
        JsonNode? idNode = request["id"]?.DeepClone();

        if (string.IsNullOrWhiteSpace(method))
        {
            return CreateErrorResponse(idNode, -32600, "Invalid Request: missing method.");
        }

        // Notifications (no id) do not expect a response
        if (idNode is null)
        {
            return null;
        }

        try
        {
            return method switch
            {
                "initialize" => CreateSuccessResponse(idNode, new JsonObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject(),
                        ["resources"] = new JsonObject()
                    },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "jxreborn-wire-parity-proxy",
                        ["version"] = "1.0.0"
                    }
                }),
                "ping" => CreateSuccessResponse(idNode, new JsonObject()),
                "resources/list" => CreateSuccessResponse(idNode, new JsonObject
                {
                    ["resources"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["uri"] = WireParityMcpAppHtml.ResourceUri,
                            ["name"] = "WireParityProxy Dashboard",
                            ["mimeType"] = WireParityMcpAppHtml.MimeType,
                            ["_meta"] = new JsonObject
                            {
                                ["ui"] = new JsonObject { ["prefersBorder"] = false }
                            }
                        }
                    }
                }),
                "resources/read" => HandleResourceRead(idNode, request["params"] as JsonObject),
                "tools/list" => CreateSuccessResponse(idNode, new JsonObject
                {
                    ["tools"] = BuildToolsList()
                }),
                "tools/call" => await HandleToolCallAsync(idNode, request["params"] as JsonObject, cancellationToken)
                    .ConfigureAwait(false),
                _ => CreateErrorResponse(idNode, -32601, $"Method not found: '{method}'.")
            };
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(idNode, -32603, ex.Message);
        }
    }

    private async Task<JsonObject> HandleToolCallAsync(
        JsonNode idNode,
        JsonObject? @params,
        CancellationToken cancellationToken)
    {
        string? toolName = @params?["name"]?.GetValue<string>();
        JsonObject args = @params?["arguments"] as JsonObject ?? new JsonObject();

        if (string.IsNullOrWhiteSpace(toolName))
        {
            return CreateErrorResponse(idNode, -32602, "Missing tool name.");
        }

        try
        {
            object payload = toolName switch
            {
                "open_parity_dashboard" => OpenParityDashboard(),
                "get_proxy_status" => GetProxyStatus(),
                "get_world_state" => await GetWorldStateAsync(cancellationToken).ConfigureAwait(false),
                "perform_action" => await PerformActionAsync(args, cancellationToken).ConfigureAwait(false),
                "get_proxy_findings" => GetProxyFindings(args),
                "get_action_steps" => GetActionSteps(),
                "export_l3a_capture" => ExportL3aCapture(args),
                "export_report" => await ExportReportAsync(cancellationToken).ConfigureAwait(false),
                "reset_drift" => ResetDrift(),
                _ => throw new InvalidOperationException($"Unknown tool '{toolName}'.")
            };

            string jsonText = JsonSerializer.Serialize(payload, JsonOptions);
            var resultObj = new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = jsonText
                    }
                },
                ["isError"] = false
            };
            if (string.Equals(toolName, "open_parity_dashboard", StringComparison.Ordinal))
            {
                resultObj["structuredContent"] = JsonNode.Parse(jsonText);
            }

            return CreateSuccessResponse(idNode, resultObj);
        }
        catch (Exception ex)
        {
            return CreateSuccessResponse(idNode, new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = ex.Message
                    }
                },
                ["isError"] = true
            });
        }
    }

    private object GetProxyStatus()
    {
        WireParitySession? session = _gameServerProxy.LatestSession;
        int totalFindings = _globalFindings.Count + (session?.DiffCoordinator.GetFindings().Count ?? 0);
        return new
        {
            standaloneMode = _standalone is not null,
            standaloneReady = _standalone?.IsReady ?? false,
            driverConnected = _driverBridge.IsConnected,
            bishopPort = _bishopProxy.BoundPort,
            gameServerPort = _gameServerProxy.BoundPort,
            primaryBackend = _options.PrimaryBackendName,
            shadowBackend = _options.ShadowBackendName,
            sessionId = session?.SessionId,
            roleName = session?.RoleName,
            c2sFrames = session?.C2sFrames ?? 0,
            primaryS2cFrames = session?.PrimaryS2cFrames ?? 0,
            shadowS2cFrames = session?.ShadowS2cFrames ?? 0,
            findingsCount = totalFindings,
            protocolViolations = session?.ProtocolGate.ViolationCount ?? 0,
            isDrifted = session?.DiffCoordinator.LatestDrift.IsDrifted ?? false,
            driftSummary = session?.DiffCoordinator.LatestDrift.Summary ?? "Idle"
        };
    }

    private async Task<object> GetWorldStateAsync(CancellationToken cancellationToken)
    {
        WorldDomSnapshot? ramSnapshot = await _driverBridge.GetClientRamSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (ramSnapshot is not null)
        {
            return new { source = "client_ram", snapshot = ramSnapshot };
        }

        WireParitySession? session = _gameServerProxy.LatestSession
            ?? throw new InvalidOperationException("No client driver attached and no active GameServer session yet.");

        return new { source = "proxy_wire_state", snapshot = session.PrimaryState.ToWorldDomSnapshot() };
    }

    private async Task<object> PerformActionAsync(JsonObject args, CancellationToken cancellationToken)
    {
        string action = args["action"]?.GetValue<string>()
            ?? throw new ArgumentException("Missing required argument 'action'.");

        if (string.Equals(action, "respawn", StringComparison.OrdinalIgnoreCase))
        {
            await _driverBridge.InvalidateBrokenClientAsync().ConfigureAwait(false);
            JxHeadlessClient respawned = await _driverBridge.SpawnAgainstProxyAsync(
                _options,
                _bishopProxy.BoundPort,
                _options.InteractiveWindow,
                cancellationToken).ConfigureAwait(false);
            return new { ok = true, action = "respawn", pid = respawned.ProcessId };
        }

        JxHeadlessClient client = string.Equals(action, "login", StringComparison.OrdinalIgnoreCase)
            ? await _driverBridge.EnsureAliveClientAsync(_options, _bishopProxy.BoundPort, cancellationToken).ConfigureAwait(false)
            : _driverBridge.Client
                ?? throw new InvalidOperationException("No live game.exe driver attached. Call perform_action(action: 'login') to spawn and login.");

        try
        {
            switch (action.ToLowerInvariant())
            {
                case "move_to":
                    int x = args["mps_x"]?.GetValue<int>() ?? throw new ArgumentException("Missing 'mps_x'.");
                    int y = args["mps_y"]?.GetValue<int>() ?? throw new ArgumentException("Missing 'mps_y'.");
                    bool run = args["run"]?.GetValue<bool>() ?? true;
                    await client.MoveToAsync(x, y, mode: run ? 2 : 1, cancellationToken: cancellationToken).ConfigureAwait(false);
                    return new { ok = true, action, x, y, run };

                case "interact_npc":
                    uint npcDwId = args["npc_dw_id"]?.GetValue<uint>() ?? throw new ArgumentException("Missing 'npc_dw_id'.");
                    await client.InteractNpcByDwIdAsync(npcDwId, cancellationToken).ConfigureAwait(false);
                    return new { ok = true, action, npcDwId };

                case "select_dialog":
                    int optionIndex = args["option_index"]?.GetValue<int>() ?? throw new ArgumentException("Missing 'option_index'.");
                    await client.SelectDialogAsync(optionIndex, cancellationToken).ConfigureAwait(false);
                    return new { ok = true, action, optionIndex };

                case "confirm_talk":
                    await client.ConfirmTalkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    return new { ok = true, action };

                case "use_item":
                    int arrayIndex = args["array_index"]?.GetValue<int>() ?? throw new ArgumentException("Missing 'array_index'.");
                    int place = args["place"]?.GetValue<int>() ?? throw new ArgumentException("Missing 'place'.");
                    int gridX = args["grid_x"]?.GetValue<int>() ?? throw new ArgumentException("Missing 'grid_x'.");
                    int gridY = args["grid_y"]?.GetValue<int>() ?? throw new ArgumentException("Missing 'grid_y'.");
                    await client.UseItemAsync(arrayIndex, place, gridX, gridY, cancellationToken).ConfigureAwait(false);
                    return new { ok = true, action, arrayIndex, place, gridX, gridY };

                case "move_item":
                    int srcPlace = args["src_place"]?.GetValue<int>() ?? throw new ArgumentException("Missing 'src_place'.");
                    int srcX = args["src_x"]?.GetValue<int>() ?? throw new ArgumentException("Missing 'src_x'.");
                    int srcY = args["src_y"]?.GetValue<int>() ?? throw new ArgumentException("Missing 'src_y'.");
                    int dstPlace = args["dst_place"]?.GetValue<int>() ?? throw new ArgumentException("Missing 'dst_place'.");
                    int dstX = args["dst_x"]?.GetValue<int>() ?? throw new ArgumentException("Missing 'dst_x'.");
                    int dstY = args["dst_y"]?.GetValue<int>() ?? throw new ArgumentException("Missing 'dst_y'.");
                    await client.MoveItemAsync(srcPlace, srcX, srcY, dstPlace, dstX, dstY, cancellationToken).ConfigureAwait(false);
                    return new { ok = true, action, srcPlace, srcX, srcY, dstPlace, dstX, dstY };

                case "cast_skill_target":
                    int skillIdTarget = args["skill_id"]?.GetValue<int>() ?? throw new ArgumentException("Missing 'skill_id'.");
                    uint targetNpcDwId = args["npc_dw_id"]?.GetValue<uint>() ?? throw new ArgumentException("Missing 'npc_dw_id'.");
                    await client.UseSkillOnTargetAsync(skillIdTarget, targetNpcDwId, cancellationToken).ConfigureAwait(false);
                    return new { ok = true, action, skillId = skillIdTarget, npcDwId = targetNpcDwId };

                case "cast_skill_pos":
                    int skillIdPos = args["skill_id"]?.GetValue<int>() ?? throw new ArgumentException("Missing 'skill_id'.");
                    int skillMpsX = args["mps_x"]?.GetValue<int>() ?? throw new ArgumentException("Missing 'mps_x'.");
                    int skillMpsY = args["mps_y"]?.GetValue<int>() ?? throw new ArgumentException("Missing 'mps_y'.");
                    await client.UseSkillAtPositionAsync(skillIdPos, skillMpsX, skillMpsY, cancellationToken).ConfigureAwait(false);
                    return new { ok = true, action, skillId = skillIdPos, mpsX = skillMpsX, mpsY = skillMpsY };

                case "change_aura_skill":
                    int auraSkillId = args["skill_id"]?.GetValue<int>() ?? throw new ArgumentException("Missing 'skill_id'.");
                    await client.ChangeAuraSkillAsync(auraSkillId, cancellationToken).ConfigureAwait(false);
                    return new { ok = true, action, skillId = auraSkillId };

                case "add_skill_point":
                    int addSkillId = args["skill_id"]?.GetValue<int>() ?? throw new ArgumentException("Missing 'skill_id'.");
                    int skillPoints = args["points"]?.GetValue<int>() ?? 1;
                    await client.AddSkillPointAsync(addSkillId, skillPoints, cancellationToken).ConfigureAwait(false);
                    return new { ok = true, action, skillId = addSkillId, points = skillPoints };

                case "add_attribute":
                    int attribute = args["attribute"]?.GetValue<int>() ?? throw new ArgumentException("Missing 'attribute'.");
                    int attrPoints = args["points"]?.GetValue<int>() ?? 1;
                    await client.AddBaseAttributeAsync(attribute, attrPoints, cancellationToken).ConfigureAwait(false);
                    return new { ok = true, action, attribute, points = attrPoints };

                case "pickup_obj":
                    if (args["object_id"] is { } objIdNode)
                    {
                        int objectId = objIdNode.GetValue<int>();
                        await client.PickupObjectByIdAsync(objectId, cancellationToken).ConfigureAwait(false);
                        return new { ok = true, action, objectId };
                    }

                    if (args["array_index"] is { } objArrNode)
                    {
                        int objArrayIdx = objArrNode.GetValue<int>();
                        await client.PickupObjectByArrayIndexAsync(objArrayIdx, cancellationToken).ConfigureAwait(false);
                        return new { ok = true, action, arrayIndex = objArrayIdx };
                    }

                    throw new ArgumentException("Missing 'object_id' or 'array_index'.");

                case "dismiss_login_hint":
                    await client.DismissSystemLoginHintIfOpenAsync(cancellationToken).ConfigureAwait(false);
                    return new { ok = true, action };

                case "switch_pk":
                    int pkMode = args["pk_mode"]?.GetValue<int>() ?? throw new ArgumentException("Missing 'pk_mode'.");
                    await client.SwitchPkAsync(pkMode, cancellationToken).ConfigureAwait(false);
                    return new { ok = true, action, pkMode };

                case "send_chat":
                    int channel = args["channel"]?.GetValue<int>() ?? 0;
                    string message = args["message"]?.GetValue<string>() ?? throw new ArgumentException("Missing 'message'.");
                    await client.SendChatAsync(channel, message, cancellationToken: cancellationToken).ConfigureAwait(false);
                    return new { ok = true, action, channel, message };

                case "login":
                    string account = args["account"]?.GetValue<string>() ?? "gm001";
                    string password = args["password"]?.GetValue<string>() ?? "123456";
                    string roleName = args["role_name"]?.GetValue<string>() ?? "ParityHero01";
                    int series = args["series"]?.GetValue<int>() ?? 0;
                    int gender = args["gender"]?.GetValue<int>() ?? 0;
                    int nativePlaceId = args["native_place_id"]?.GetValue<int>() ?? 53;
                    WorldDomSnapshot entered = await client.LoginAndEnterWorldAsync(
                        _options.BindHost,
                        _bishopProxy.BoundPort,
                        account,
                        password,
                        roleName,
                        gender: gender,
                        series: series,
                        nativePlaceId: nativePlaceId,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    return new { ok = true, action, mapId = entered.Player.MapId, mpsX = entered.Player.MpsX, mpsY = entered.Player.MpsY };

                default:
                    throw new ArgumentException($"Unsupported action '{action}'.");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            await _driverBridge.InvalidateBrokenClientAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Driver pipe disconnected during '{action}': {ex.Message}. Retry with action: 'login' or 'respawn'.",
                ex);
        }
    }

    private object GetProxyFindings(JsonObject args)
    {
        string? severityFilter = args["severity"]?.GetValue<string>();
        int minStep = args["since_step"]?.GetValue<int>() ?? 0;

        var all = new List<ParityFinding>(_globalFindings);
        WireParitySession? session = _gameServerProxy.LatestSession;
        if (session is not null)
        {
            all.AddRange(session.DiffCoordinator.GetFindings());
        }

        IEnumerable<ParityFinding> filtered = all.Where(f => f.StepIndex >= minStep);
        if (!string.IsNullOrWhiteSpace(severityFilter) &&
            Enum.TryParse<FindingSeverity>(severityFilter, ignoreCase: true, out FindingSeverity sev))
        {
            filtered = filtered.Where(f => f.Severity == sev);
        }

        return filtered.ToArray();
    }

    private object GetActionSteps() =>
        _gameServerProxy.LatestSession?.DiffCoordinator.GetSteps() ?? [];

    private object ExportL3aCapture(JsonObject args)
    {
        WireParitySession session = _gameServerProxy.LatestSession
            ?? throw new InvalidOperationException("No active GameServer session yet.");

        int stepIndex = args["step_index"]?.GetValue<int>()
            ?? throw new ArgumentException("Missing 'step_index'.");

        string defaultId = $"WPP_{session.SessionId.Replace("-", "_", StringComparison.Ordinal)}_S{stepIndex:D3}";
        if (defaultId.Length > 79)
        {
            defaultId = defaultId[..79];
        }

        string captureId = args["capture_id"]?.GetValue<string>() ?? defaultId;
        string outDir = Path.Combine(_options.ReportOutputDir, "captures");
        string path = L3aCaptureExporter.ExportActionWindow(session, stepIndex, captureId, outDir);
        return new { ok = true, captureId, path };
    }

    private async Task<object> ExportReportAsync(CancellationToken cancellationToken)
    {
        WireParitySession session = _gameServerProxy.LatestSession
            ?? throw new InvalidOperationException("No active GameServer session yet.");

        string dir = WireParityReportWriter.WriteAll(session, _options.ReportOutputDir, _globalFindings.ToArray());
        if (_standalone is not null)
        {
            await _standalone.WriteServerLogsAsync(dir, cancellationToken).ConfigureAwait(false);
        }

        return new { ok = true, path = dir };
    }

    private object OpenParityDashboard() => new
    {
        status = GetProxyStatus(),
        findings = GetProxyFindings(new JsonObject()),
        steps = GetActionSteps()
    };

    private static JsonObject HandleResourceRead(JsonNode idNode, JsonObject? @params)
    {
        string? uri = @params?["uri"]?.GetValue<string>();
        if (!string.Equals(uri, WireParityMcpAppHtml.ResourceUri, StringComparison.Ordinal))
        {
            return CreateErrorResponse(idNode, -32602, $"Unknown resource URI '{uri}'.");
        }

        return CreateSuccessResponse(idNode, new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["uri"] = WireParityMcpAppHtml.ResourceUri,
                    ["mimeType"] = WireParityMcpAppHtml.MimeType,
                    ["text"] = WireParityMcpAppHtml.Page
                }
            }
        });
    }

    private object ResetDrift()
    {
        _gameServerProxy.LatestSession?.DiffCoordinator.ResetDrift();
        return new { ok = true };
    }

    private static JsonArray BuildToolsList() =>
    [
        new JsonObject
        {
            ["name"] = "open_parity_dashboard",
            ["description"] = "Open the interactive WireParityProxy live differential dashboard inside the Antigravity pane.",
            ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            ["_meta"] = new JsonObject
            {
                ["ui"] = new JsonObject { ["resourceUri"] = WireParityMcpAppHtml.ResourceUri },
                ["com.google.antigravity/ui"] = new JsonObject
                {
                    ["entrypoints"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "aux_pane", ["title"] = "Wire Parity" },
                        new JsonObject { ["type"] = "global", ["title"] = "Wire Parity" }
                    }
                }
            }
        },
        CreateToolSchema(
            "get_proxy_status",
            "Get live status of Bishop/GameServer shadow proxies, traffic counters, protocol violations, and state drift.",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),
        CreateToolSchema(
            "get_world_state",
            "Get structured WorldDomSnapshot (player, NPCs, dialog, inventory, skills) from client RAM or proxy wire state.",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),
        CreateToolSchema(
            "perform_action",
            "Drive the single game.exe client via driver RPC (login, respawn, move_to, interact_npc, select_dialog, confirm_talk, use_item, move_item, cast_skill_target, cast_skill_pos, change_aura_skill, add_skill_point, add_attribute, pickup_obj, switch_pk, send_chat, dismiss_login_hint).",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["action"] = new JsonObject { ["type"] = "string", ["description"] = "Action name: login | respawn | move_to | interact_npc | select_dialog | confirm_talk | use_item | move_item | cast_skill_target | cast_skill_pos | change_aura_skill | add_skill_point | add_attribute | pickup_obj | switch_pk | send_chat | dismiss_login_hint" },
                    ["mps_x"] = new JsonObject { ["type"] = "integer" },
                    ["mps_y"] = new JsonObject { ["type"] = "integer" },
                    ["run"] = new JsonObject { ["type"] = "boolean" },
                    ["npc_dw_id"] = new JsonObject { ["type"] = "integer" },
                    ["option_index"] = new JsonObject { ["type"] = "integer" },
                    ["array_index"] = new JsonObject { ["type"] = "integer" },
                    ["place"] = new JsonObject { ["type"] = "integer" },
                    ["grid_x"] = new JsonObject { ["type"] = "integer" },
                    ["grid_y"] = new JsonObject { ["type"] = "integer" },
                    ["src_place"] = new JsonObject { ["type"] = "integer" },
                    ["src_x"] = new JsonObject { ["type"] = "integer" },
                    ["src_y"] = new JsonObject { ["type"] = "integer" },
                    ["dst_place"] = new JsonObject { ["type"] = "integer" },
                    ["dst_x"] = new JsonObject { ["type"] = "integer" },
                    ["dst_y"] = new JsonObject { ["type"] = "integer" },
                    ["skill_id"] = new JsonObject { ["type"] = "integer" },
                    ["attribute"] = new JsonObject { ["type"] = "integer" },
                    ["points"] = new JsonObject { ["type"] = "integer" },
                    ["object_id"] = new JsonObject { ["type"] = "integer" },
                    ["pk_mode"] = new JsonObject { ["type"] = "integer" },
                    ["channel"] = new JsonObject { ["type"] = "integer" },
                    ["message"] = new JsonObject { ["type"] = "string" },
                    ["account"] = new JsonObject { ["type"] = "string" },
                    ["password"] = new JsonObject { ["type"] = "string" },
                    ["role_name"] = new JsonObject { ["type"] = "string" },
                    ["series"] = new JsonObject { ["type"] = "integer" },
                    ["gender"] = new JsonObject { ["type"] = "integer" },
                    ["native_place_id"] = new JsonObject { ["type"] = "integer" }
                },
                ["required"] = new JsonArray { "action" }
            }),
        CreateToolSchema(
            "get_proxy_findings",
            "Get 3-layer parity findings (Protocol Gate, Action-Window Wire Diff, Semantic State Diff).",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["severity"] = new JsonObject { ["type"] = "string", ["description"] = "Optional: Info | Warning | Critical" },
                    ["since_step"] = new JsonObject { ["type"] = "integer", ["description"] = "Optional minimum stepIndex" }
                }
            }),
        CreateToolSchema(
            "get_action_steps",
            "Get timeline of completed C2S action windows with Primary vs Shadow S2C opcodes and drift status.",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),
        CreateToolSchema(
            "export_l3a_capture",
            "Export a specific action step into a SHA-256 signed *.capture.json file for L3A+ offline CI replay.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["step_index"] = new JsonObject { ["type"] = "integer" },
                    ["capture_id"] = new JsonObject { ["type"] = "string" }
                },
                ["required"] = new JsonArray { "step_index" }
            }),
        CreateToolSchema(
            "export_report",
            "Write findings.jsonl, report.md, report.html (and server logs in standalone mode) to disk.",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),
        CreateToolSchema(
            "reset_drift",
            "Reset the cumulative StateDriftDetector flag after resyncing the character.",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() })
    ];

    private static JsonObject CreateToolSchema(string name, string description, JsonObject inputSchema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = inputSchema
    };

    private static JsonObject CreateSuccessResponse(JsonNode? id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result
    };

    private static JsonObject CreateErrorResponse(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message
        }
    };
}
