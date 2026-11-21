using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using JXReborn.Adapter.Legacy.Ciphers;
using JXReborn.Adapter.Legacy.Packets;
using JXReborn.DualParityServer.Diffing;
using JXReborn.Primitives.Characters;
using JXReborn.WireParityProxy.Configuration;
using JXReborn.WireParityProxy.Wire;

namespace JXReborn.WireParityProxy.Proxy;

/// <summary>
/// L7 dual-backend Bishop proxy. Accepts 1 <c>game.exe</c> connection, mirrors decrypted Bishop C2S packets
/// to both Primary and Shadow Bishop servers, maps <c>0x34</c> (<c>s2c_notifyplayerlogin</c>) handoff GUIDs,
/// and rewrites the GameServer IP:Port in the Primary <c>0x34</c> response to point back to <see cref="GameServerShadowProxy"/>.
/// </summary>
public sealed class BishopShadowProxy : IAsyncDisposable
{
    private readonly WireParityProxyOptions _options;
    private readonly LegacyKeyTable _keyTable;
    private readonly HandoffTicketMap _handoffMap;
    private readonly Func<int> _getGameServerProxyPort;
    private readonly Action<ParityFinding>? _onFinding;
    private readonly CancellationTokenSource _cts = new();

    private TcpListener? _listener;
    private Task? _acceptLoop;
    private int _boundPort;
    private int _activeConnections;
    private long _totalConnections;
    private long _handoffsCompleted;
    private int _findingSeq;

    public BishopShadowProxy(
        WireParityProxyOptions options,
        LegacyKeyTable keyTable,
        HandoffTicketMap handoffMap,
        Func<int> getGameServerProxyPort,
        Action<ParityFinding>? onFinding = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(keyTable);
        ArgumentNullException.ThrowIfNull(handoffMap);
        ArgumentNullException.ThrowIfNull(getGameServerProxyPort);
        _options = options;
        _keyTable = keyTable;
        _handoffMap = handoffMap;
        _getGameServerProxyPort = getGameServerProxyPort;
        _onFinding = onFinding;
    }

    public int BoundPort => _boundPort;

    public bool IsListening => _listener is not null;

    public int ActiveConnections => Volatile.Read(ref _activeConnections);

    public long TotalConnections => Interlocked.Read(ref _totalConnections);

    public long HandoffsCompleted => Interlocked.Read(ref _handoffsCompleted);

    public void Start()
    {
        if (_listener is not null)
        {
            return;
        }

        IPAddress bindIp = IPAddress.TryParse(_options.BindHost, out IPAddress? parsed)
            ? parsed
            : IPAddress.Loopback;

        _listener = new TcpListener(bindIp, _options.BishopListenPort);
        _listener.Start();
        _boundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is not null)
        {
            TcpClient accepted;
            try
            {
                accepted = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }

            _ = Task.Run(() => HandleClientAsync(accepted, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient acceptedClient, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _activeConnections);
        Interlocked.Increment(ref _totalConnections);

        using CancellationTokenSource linkCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        (string primaryHost, int primaryPort) = _options.GetPrimaryBishopEndpoint();
        (string shadowHost, int shadowPort) = _options.GetShadowBishopEndpoint();

        JxWireConnection? clientConn = null;
        JxWireConnection? primaryConn = null;
        JxWireConnection? shadowConn = null;

        try
        {
            Task<JxWireConnection> primaryConnectTask = JxWireConnection.ConnectUpstreamAsync(
                $"bishop-primary:{_options.PrimaryBackendName}",
                primaryHost,
                primaryPort,
                _keyTable,
                linkCts.Token);
            Task<JxWireConnection> shadowConnectTask = JxWireConnection.ConnectUpstreamAsync(
                $"bishop-shadow:{_options.ShadowBackendName}",
                shadowHost,
                shadowPort,
                _keyTable,
                linkCts.Token);

            await Task.WhenAll(primaryConnectTask, shadowConnectTask).ConfigureAwait(false);
            primaryConn = await primaryConnectTask.ConfigureAwait(false);
            shadowConn = await shadowConnectTask.ConfigureAwait(false);

            clientConn = await JxWireConnection.AcceptClientAsync(
                "bishop-client",
                acceptedClient,
                _keyTable,
                linkCts.Token).ConfigureAwait(false);

            var shadowNotifySlot = new ShadowNotifySlot();

            Task c2sTask = PumpClientToBackendsAsync(clientConn, primaryConn, shadowConn, linkCts.Token);
            Task shadowS2cTask = PumpShadowS2cAsync(shadowConn, shadowNotifySlot, linkCts.Token);
            Task primaryS2cTask = PumpPrimaryS2cAsync(primaryConn, clientConn, shadowConn, shadowNotifySlot, linkCts.Token);

            await Task.WhenAny(c2sTask, primaryS2cTask).ConfigureAwait(false);
            await linkCts.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(
                IgnoreCancellationAsync(c2sTask),
                IgnoreCancellationAsync(primaryS2cTask),
                IgnoreCancellationAsync(shadowS2cTask)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on disconnect/shutdown.
        }
        catch (Exception ex)
        {
            EmitBishopFinding(
                FindingSeverity.Critical,
                FindingCategory.RuntimeError,
                "Bishop.Connection",
                "Connected",
                ex.Message);
        }
        finally
        {
            if (clientConn is not null)
            {
                await clientConn.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                acceptedClient.Dispose();
            }

            if (primaryConn is not null)
            {
                await primaryConn.DisposeAsync().ConfigureAwait(false);
            }

            if (shadowConn is not null)
            {
                await shadowConn.DisposeAsync().ConfigureAwait(false);
            }

            Interlocked.Decrement(ref _activeConnections);
        }
    }

    private static async Task PumpClientToBackendsAsync(
        JxWireConnection client,
        JxWireConnection primary,
        JxWireConnection shadow,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            JxWireFrame? frame = await client.ReadDecryptedFrameAsync(cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                break;
            }

            Task primaryWrite = primary.WriteEncryptedFrameAsync(frame.DecryptedPayload, cancellationToken);
            Task shadowWrite = shadow.WriteEncryptedFrameAsync(frame.DecryptedPayload, cancellationToken);
            await Task.WhenAll(primaryWrite, shadowWrite).ConfigureAwait(false);
        }
    }

    private static async Task PumpShadowS2cAsync(
        JxWireConnection shadow,
        ShadowNotifySlot shadowNotifySlot,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            JxWireFrame? frame = await shadow.ReadDecryptedFrameAsync(cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                break;
            }

            if (frame.Opcode == ProtocolOpcodes.s2c_notifyplayerlogin &&
                frame.DecryptedPayload.Length >= NotifyPlayerLoginS2CPacket.Size)
            {
                NotifyPlayerLoginS2CPacket pkt = MemoryMarshal.Read<NotifyPlayerLoginS2CPacket>(frame.DecryptedPayload);
                shadowNotifySlot.Publish(pkt);
            }
        }
    }

    private async Task PumpPrimaryS2cAsync(
        JxWireConnection primary,
        JxWireConnection client,
        JxWireConnection shadow,
        ShadowNotifySlot shadowNotifySlot,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            JxWireFrame? frame = await primary.ReadDecryptedFrameAsync(cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                break;
            }

            if (frame.Opcode == ProtocolOpcodes.s2c_notifyplayerlogin &&
                frame.DecryptedPayload.Length >= NotifyPlayerLoginS2CPacket.Size)
            {
                byte[] rewritten = await HandleNotifyPlayerLoginAsync(
                    frame.DecryptedPayload,
                    shadow,
                    shadowNotifySlot,
                    cancellationToken).ConfigureAwait(false);
                await client.WriteEncryptedFrameAsync(rewritten, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await client.WriteEncryptedFrameAsync(frame.DecryptedPayload, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<byte[]> HandleNotifyPlayerLoginAsync(
        byte[] primaryPayload,
        JxWireConnection shadowConn,
        ShadowNotifySlot shadowNotifySlot,
        CancellationToken cancellationToken)
    {
        NotifyPlayerLoginS2CPacket primaryPkt = MemoryMarshal.Read<NotifyPlayerLoginS2CPacket>(primaryPayload);
        string roleName = primaryPkt.GetRoleName();

        NotifyPlayerLoginS2CPacket? shadowPkt = await WaitShadowNotifyAsync(shadowNotifySlot, cancellationToken).ConfigureAwait(false);
        if (shadowPkt is null)
        {
            EmitBishopFinding(
                FindingSeverity.Critical,
                FindingCategory.WireProtocol,
                "Bishop.NotifyPlayerLogin",
                $"Permit={primaryPkt.Permit}, Role='{roleName}'",
                $"Timed out waiting {_options.HandoffMatchTimeoutMs}ms for Shadow 0x34 handoff");
        }
        else if (primaryPkt.Permit != 0 && shadowPkt.Value.Permit == 0 && !string.IsNullOrWhiteSpace(roleName))
        {
            EmitBishopFinding(
                FindingSeverity.Warning,
                FindingCategory.WireProtocol,
                "Bishop.NotifyPlayerLogin.Permit",
                $"Permit={primaryPkt.Permit}",
                $"Permit={shadowPkt.Value.Permit} (auto-provisioning '{roleName}' on Shadow)");

            shadowNotifySlot.Reset();
            byte[] createPayload = new CreateCharacterC2SPacket(
                Identity: primaryPkt.Identity,
                Gender: CharacterGender.Male,
                Series: ElementSeries.Metal,
                NativePlaceId: 53,
                RoleNameUnicode: roleName).ToPayloadBytes();
            await shadowConn.WriteEncryptedFrameAsync(createPayload, cancellationToken).ConfigureAwait(false);
            shadowPkt = await WaitShadowNotifyAsync(shadowNotifySlot, cancellationToken).ConfigureAwait(false);
        }
        else if (primaryPkt.Permit != shadowPkt.Value.Permit)
        {
            EmitBishopFinding(
                FindingSeverity.Critical,
                FindingCategory.WireProtocol,
                "Bishop.NotifyPlayerLogin.Permit",
                $"Permit={primaryPkt.Permit}",
                $"Permit={shadowPkt.Value.Permit}");
        }

        if (shadowPkt is { } shadow && primaryPkt.Permit != 0 && shadow.Permit != 0)
        {
            var entry = new HandoffTicketEntry(
                PrimaryGuid: primaryPkt.GetTicketGuid(),
                PrimaryGsHost: primaryPkt.GetIpAddress().ToString(),
                PrimaryGsPort: primaryPkt.Port,
                ShadowGuid: shadow.GetTicketGuid(),
                ShadowGsHost: shadow.GetIpAddress().ToString(),
                ShadowGsPort: shadow.Port,
                RoleName: roleName,
                CreatedAtUtc: DateTimeOffset.UtcNow);
            _handoffMap.Register(entry);
            Interlocked.Increment(ref _handoffsCompleted);
        }

        byte[] rewritten = primaryPayload.ToArray();
        if (primaryPkt.Permit != 0)
        {
            IPAddress advertisedIp = IPAddress.TryParse(_options.AdvertisedGameServerHost, out IPAddress? parsed) &&
                                     parsed.AddressFamily == AddressFamily.InterNetwork
                ? parsed
                : IPAddress.Loopback;

            Span<byte> ipBytes = stackalloc byte[4];
            advertisedIp.TryWriteBytes(ipBytes, out _);
            ipBytes.CopyTo(rewritten.AsSpan(0x36, 4));
            BinaryPrimitives.WriteUInt16LittleEndian(rewritten.AsSpan(0x3A, 2), (ushort)_getGameServerProxyPort());
        }

        return rewritten;
    }

    private async Task<NotifyPlayerLoginS2CPacket?> WaitShadowNotifyAsync(
        ShadowNotifySlot slot,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_options.HandoffMatchTimeoutMs);
            return await slot.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private sealed class ShadowNotifySlot
    {
        private readonly Lock _lock = new();
        private TaskCompletionSource<NotifyPlayerLoginS2CPacket> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Publish(NotifyPlayerLoginS2CPacket packet)
        {
            TaskCompletionSource<NotifyPlayerLoginS2CPacket> target;
            lock (_lock)
            {
                target = _tcs;
            }

            target.TrySetResult(packet);
        }

        public void Reset()
        {
            lock (_lock)
            {
                _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public Task<NotifyPlayerLoginS2CPacket> WaitAsync(CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                return _tcs.Task.WaitAsync(cancellationToken);
            }
        }
    }

    private void EmitBishopFinding(
        FindingSeverity severity,
        FindingCategory category,
        string fieldPath,
        string primaryVal,
        string shadowVal)
    {
        if (_onFinding is null)
        {
            return;
        }

        int seq = Interlocked.Increment(ref _findingSeq);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        _onFinding(new ParityFinding(
            FindingId: $"BISHOP-{now:yyyyMMdd}-{seq:D4}",
            ClusterKey: $"{category}::Bishop::{fieldPath}",
            Severity: severity,
            Category: category,
            StepIndex: 0,
            StepName: "BishopHandoff",
            FieldPath: fieldPath,
            LegacyValue: primaryVal,
            RebornValue: shadowVal,
            Repro: new FindingReproContext(0, [0, 0], null, ["BishopHandoff"]),
            StateDelta: new FindingStateDelta(0, 0, 0, 0, 0, 0, 0, 0),
            LegacyRecentS2cOpcodes: ["0x34"],
            RebornRecentS2cOpcodes: ["0x34"],
            Artifacts: new FindingArtifacts(),
            TimestampUtc: now));
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (IOException)
        {
            // Expected when peer closes socket.
        }
        catch (SocketException)
        {
            // Expected when peer resets socket.
        }
    }
}
