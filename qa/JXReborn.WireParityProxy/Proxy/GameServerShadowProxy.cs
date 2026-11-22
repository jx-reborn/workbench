using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using JXReborn.Adapter.Legacy.Ciphers;
using JXReborn.Adapter.Legacy.Packets;
using JXReborn.DualParityServer.Diffing;
using JXReborn.WireParityProxy.Configuration;
using JXReborn.WireParityProxy.Diffing;
using JXReborn.WireParityProxy.Translation;
using JXReborn.WireParityProxy.Wire;

namespace JXReborn.WireParityProxy.Proxy;

/// <summary>
/// L7 dual-backend GameServer proxy. Accepts 1 <c>game.exe</c> connection after Bishop <c>0x34</c> handoff,
/// redeems the mapped <c>(PrimaryGuid, ShadowGuid)</c> against both GameServers, translates dynamic IDs on C2S,
/// runs <see cref="ProtocolSafetyGate"/> on Shadow S2C, and feeds <see cref="WireParityDiffCoordinator"/>.
/// </summary>
public sealed class GameServerShadowProxy : IAsyncDisposable
{
    private static readonly TimeSpan InitialSyncBarrierTimeout = TimeSpan.FromSeconds(5);

    private readonly WireParityProxyOptions _options;
    private readonly LegacyKeyTable _keyTable;
    private readonly HandoffTicketMap _handoffMap;
    private readonly Action<WireParitySession>? _onSessionCreated;
    private readonly ConcurrentQueue<WireParitySession> _sessions = new();
    private readonly CancellationTokenSource _cts = new();

    private TcpListener? _listener;
    private Task? _acceptLoop;
    private int _boundPort;
    private int _activeConnections;
    private long _totalConnections;
    private WireParitySession? _latestSession;

    public GameServerShadowProxy(
        WireParityProxyOptions options,
        LegacyKeyTable keyTable,
        HandoffTicketMap handoffMap,
        Action<WireParitySession>? onSessionCreated = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(keyTable);
        ArgumentNullException.ThrowIfNull(handoffMap);
        _options = options;
        _keyTable = keyTable;
        _handoffMap = handoffMap;
        _onSessionCreated = onSessionCreated;
    }

    public int BoundPort => _boundPort;

    public bool IsListening => _listener is not null;

    public int ActiveConnections => Volatile.Read(ref _activeConnections);

    public long TotalConnections => Interlocked.Read(ref _totalConnections);

    public WireParitySession? LatestSession => Volatile.Read(ref _latestSession);

    public IReadOnlyList<WireParitySession> GetSessions() => _sessions.ToArray();

    public void Start()
    {
        if (_listener is not null)
        {
            return;
        }

        IPAddress bindIp = IPAddress.TryParse(_options.BindHost, out IPAddress? parsed)
            ? parsed
            : IPAddress.Loopback;

        _listener = new TcpListener(bindIp, _options.GameServerListenPort);
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
        JxWireConnection? clientConn = null;
        JxWireConnection? primaryConn = null;
        JxWireConnection? shadowConn = null;

        try
        {
            clientConn = await JxWireConnection.AcceptClientAsync(
                "gs-client",
                acceptedClient,
                _keyTable,
                linkCts.Token).ConfigureAwait(false);

            JxWireFrame? loginFrame = await clientConn.ReadDecryptedFrameAsync(linkCts.Token).ConfigureAwait(false);
            if (loginFrame is null ||
                loginFrame.Opcode != ProtocolOpcodes.c2s_logiclogin ||
                loginFrame.DecryptedPayload.Length < 17)
            {
                return;
            }

            var primaryGuid = new Guid(loginFrame.DecryptedPayload.AsSpan(1, 16));
            if (!_handoffMap.TryTake(primaryGuid, out HandoffTicketEntry? ticket) || ticket is null)
            {
                return;
            }

            Task<JxWireConnection> primaryConnectTask = JxWireConnection.ConnectUpstreamAsync(
                $"gs-primary:{_options.PrimaryBackendName}",
                ticket.PrimaryGsHost,
                ticket.PrimaryGsPort,
                _keyTable,
                linkCts.Token);
            Task<JxWireConnection> shadowConnectTask = JxWireConnection.ConnectUpstreamAsync(
                $"gs-shadow:{_options.ShadowBackendName}",
                ticket.ShadowGsHost,
                ticket.ShadowGsPort,
                _keyTable,
                linkCts.Token);

            await Task.WhenAll(primaryConnectTask, shadowConnectTask).ConfigureAwait(false);
            primaryConn = await primaryConnectTask.ConfigureAwait(false);
            shadowConn = await shadowConnectTask.ConfigureAwait(false);

            var session = new WireParitySession(ticket.RoleName, _options);
            _sessions.Enqueue(session);
            Volatile.Write(ref _latestSession, session);
            _onSessionCreated?.Invoke(session);

            // Forward c2s_logiclogin with each backend's own handoff GUID
            byte[] shadowLoginPayload = loginFrame.DecryptedPayload.ToArray();
            ticket.ShadowGuid.TryWriteBytes(shadowLoginPayload.AsSpan(1, 16));

            await Task.WhenAll(
                primaryConn.WriteEncryptedFrameAsync(loginFrame.DecryptedPayload, linkCts.Token),
                shadowConn.WriteEncryptedFrameAsync(shadowLoginPayload, linkCts.Token)).ConfigureAwait(false);

            var windowLock = new Lock();
            var primarySyncEndTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var shadowSyncEndTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int gatedOnInitialSync = 0;
            WireActionWindow? activeWindow = null;
            CancellationTokenSource? windowTimerCts = null;
            int actionSeq = 0;

            void CloseActiveWindowLocked()
            {
                if (activeWindow is null)
                {
                    return;
                }

                WireActionWindow closing = activeWindow;
                activeWindow = null;
                windowTimerCts?.Cancel();
                windowTimerCts?.Dispose();
                windowTimerCts = null;

                _ = session.DiffCoordinator.CompleteActionWindow(closing, session.PrimaryState, session.ShadowState);
            }

            void OpenActionWindow(JxWireFrame c2sFrame, C2sRewriteResult rewrite)
            {
                lock (windowLock)
                {
                    CloseActiveWindowLocked();

                    int seq = ++actionSeq;
                    string label = $"Step{seq:D3}_C2S_0x{c2sFrame.Opcode:X2}";
                    var window = new WireActionWindow(
                        actionSequence: seq,
                        actionLabel: label,
                        primaryC2s: c2sFrame,
                        shadowC2sPayload: rewrite.ShadowPayload,
                        c2sWasRewritten: rewrite.WasModified,
                        translationMissReason: rewrite.TranslationMissReason);
                    activeWindow = window;

                    var timerCts = CancellationTokenSource.CreateLinkedTokenSource(linkCts.Token);
                    windowTimerCts = timerCts;
                    int delayMs = Math.Max(20, _options.ActionWindowMs);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(delayMs, timerCts.Token).ConfigureAwait(false);
                            lock (windowLock)
                            {
                                if (ReferenceEquals(activeWindow, window))
                                {
                                    CloseActiveWindowLocked();
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            // Closed early by next action or disconnect.
                        }
                    }, CancellationToken.None);
                }
            }

            Task c2sTask = Task.Run(async () =>
            {
                while (!linkCts.Token.IsCancellationRequested)
                {
                    JxWireFrame? frame = await clientConn.ReadDecryptedFrameAsync(linkCts.Token).ConfigureAwait(false);
                    if (frame is null)
                    {
                        break;
                    }

                    session.IncrementC2s();
                    if (frame.Opcode == ProtocolOpcodes.c2s_syncend)
                    {
                        if (frame.DecryptedPayload.Length >= 9)
                        {
                            uint t1 = BinaryPrimitives.ReadUInt32LittleEndian(frame.DecryptedPayload.AsSpan(1, 4));
                            uint t2 = BinaryPrimitives.ReadUInt32LittleEndian(frame.DecryptedPayload.AsSpan(5, 4));
                            session.SetClientTokens(t1, t2);
                        }

                        // Wait for Shadow's initial world sync burst (s2c_syncend) before opening Step 1 and forwarding
                        // c2s_syncend so slower Shadow cold-start never cascades false-positive InWorld drift across Steps 1..N.
                        if (Interlocked.Exchange(ref gatedOnInitialSync, 1) == 0 &&
                            primarySyncEndTcs.Task.IsCompleted &&
                            !shadowSyncEndTcs.Task.IsCompleted)
                        {
                            try
                            {
                                await shadowSyncEndTcs.Task
                                    .WaitAsync(InitialSyncBarrierTimeout, linkCts.Token)
                                    .ConfigureAwait(false);
                            }
                            catch (TimeoutException)
                            {
                                // Bounded safety timeout so live play cannot hang if Shadow stalls on login.
                            }
                        }
                    }

                    C2sRewriteResult rewrite = session.Rewriter.RewriteForShadow(
                        frame.DecryptedPayload,
                        session.ClientToken1,
                        session.ClientToken2);

                    session.PrimaryState.RecordOutboundC2s(frame.DecryptedPayload, frame.TimestampUtc);
                    session.ShadowState.RecordOutboundC2s(rewrite.ShadowPayload, frame.TimestampUtc);

                    if (frame.Opcode != ProtocolOpcodes.c2s_ping)
                    {
                        OpenActionWindow(frame, rewrite);
                    }

                    await Task.WhenAll(
                        primaryConn.WriteEncryptedFrameAsync(frame.DecryptedPayload, linkCts.Token),
                        shadowConn.WriteEncryptedFrameAsync(rewrite.ShadowPayload, linkCts.Token)).ConfigureAwait(false);
                }
            }, linkCts.Token);

            Task primaryS2cTask = Task.Run(async () =>
            {
                while (!linkCts.Token.IsCancellationRequested)
                {
                    JxWireFrame? frame = await primaryConn.ReadDecryptedFrameAsync(linkCts.Token).ConfigureAwait(false);
                    if (frame is null)
                    {
                        break;
                    }

                    session.IncrementPrimaryS2c();
                    session.PrimaryState.ApplyS2c(frame.DecryptedPayload, frame.TimestampUtc);
                    if (frame.Opcode == ProtocolOpcodes.s2c_syncend)
                    {
                        primarySyncEndTcs.TrySetResult();
                    }

                    lock (windowLock)
                    {
                        activeWindow?.AddPrimaryS2c(frame);
                    }

                    await clientConn.WriteEncryptedFrameAsync(frame.DecryptedPayload, linkCts.Token).ConfigureAwait(false);
                }
            }, linkCts.Token);

            Task shadowS2cTask = Task.Run(async () =>
            {
                while (!linkCts.Token.IsCancellationRequested)
                {
                    JxWireFrame? frame = await shadowConn.ReadDecryptedFrameAsync(linkCts.Token).ConfigureAwait(false);
                    if (frame is null)
                    {
                        break;
                    }

                    session.IncrementShadowS2c();

                    int currentStep;
                    string currentLabel;
                    lock (windowLock)
                    {
                        currentStep = activeWindow?.ActionSequence ?? 0;
                        currentLabel = activeWindow?.ActionLabel ?? "InitialSync";
                        activeWindow?.AddShadowS2c(frame);
                    }

                    ParityFinding? protoFinding = session.ProtocolGate.ObserveShadowS2c(
                        frame.DecryptedPayload,
                        currentStep,
                        session.PrimaryState.MapId,
                        session.PrimaryState.MpsX,
                        session.PrimaryState.MpsY,
                        currentLabel);
                    if (protoFinding is not null)
                    {
                        session.DiffCoordinator.RecordImmediateFinding(protoFinding);
                    }

                    session.ShadowState.ApplyS2c(frame.DecryptedPayload, frame.TimestampUtc);
                    if (frame.Opcode == ProtocolOpcodes.s2c_syncend)
                    {
                        shadowSyncEndTcs.TrySetResult();
                    }
                }
            }, linkCts.Token);

            await Task.WhenAny(c2sTask, primaryS2cTask).ConfigureAwait(false);
            await linkCts.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(
                IgnoreCancellationAsync(c2sTask),
                IgnoreCancellationAsync(primaryS2cTask),
                IgnoreCancellationAsync(shadowS2cTask)).ConfigureAwait(false);

            lock (windowLock)
            {
                CloseActiveWindowLocked();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
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
