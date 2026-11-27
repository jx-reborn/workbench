using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using JXReborn.Adapter.Legacy.Ciphers;
using JXReborn.Adapter.Legacy.Packets;
using JXReborn.DualParityServer.Diffing;
using JXReborn.TestBase.WireCaptures;
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
    private readonly ConcurrentDictionary<string, SemanticDiffEngine> _roleDiffEngines = new(StringComparer.OrdinalIgnoreCase);
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

    public async Task<SessionReconcileResult> ReconcileLatestSessionAsync(
        bool forceBaLangFallback = false,
        CancellationToken cancellationToken = default)
    {
        WireParitySession? session = LatestSession;
        if (session is null)
        {
            return new SessionReconcileResult(
                Succeeded: false,
                TierUsed: "None",
                UsedBaLangFallback: false,
                TargetMapId: 0,
                TargetMpsX: 0,
                TargetMpsY: 0,
                PrimaryMapId: 0,
                ShadowMapId: 0,
                Message: "Chưa có phiên GameServer nào đang kết nối để đồng bộ.");
        }

        Func<bool, CancellationToken, Task<SessionReconcileResult>>? handler = session.GetReconcileHandler();
        if (handler is null)
        {
            return new SessionReconcileResult(
                Succeeded: false,
                TierUsed: "None",
                UsedBaLangFallback: false,
                TargetMapId: 0,
                TargetMpsX: 0,
                TargetMpsY: 0,
                PrimaryMapId: session.PrimaryState.MapId,
                ShadowMapId: session.ShadowState.MapId,
                Message: "Phiên GameServer đã ngắt kết nối.");
        }

        return await handler(forceBaLangFallback, cancellationToken).ConfigureAwait(false);
    }

    public void Start()
    {
        if (_listener is not null)
        {
            return;
        }

        IPAddress bindIp = IPAddress.TryParse(_options.BindHost, out IPAddress? parsed)
            ? parsed
            : IPAddress.Loopback;

        var candidate = new TcpListener(bindIp, _options.GameServerListenPort);
        try
        {
            candidate.Start();
            _listener = candidate;
        }
        catch (SocketException) when (_options.GameServerListenPort > 0 && !_options.HasExplicitGameServerPort)
        {
            candidate.Stop();
            var fallback = new TcpListener(bindIp, 0);
            fallback.Start();
            _listener = fallback;
        }

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

            SemanticDiffEngine roleDiffEngine = _roleDiffEngines.GetOrAdd(
                ticket.RoleName,
                _ => WireParityDiffCoordinator.CreateSemanticDiffEngine(_options));
            var session = new WireParitySession(ticket.RoleName, _options, roleDiffEngine);
            _sessions.Enqueue(session);
            Volatile.Write(ref _latestSession, session);
            _onSessionCreated?.Invoke(session);

            // Forward c2s_logiclogin with each backend's own handoff GUID
            byte[] shadowLoginPayload = loginFrame.DecryptedPayload.ToArray();
            ticket.ShadowGuid.TryWriteBytes(shadowLoginPayload.AsSpan(1, 16));

            using var primaryWriteGate = new SemaphoreSlim(1, 1);
            using var shadowWriteGate = new SemaphoreSlim(1, 1);
            using var clientWriteGate = new SemaphoreSlim(1, 1);
            using var reconcileGate = new SemaphoreSlim(1, 1);
            int reconcileInProgress = 0;

            async Task SendPrimaryFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
            {
                await primaryWriteGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await primaryConn.WriteEncryptedFrameAsync(payload, ct).ConfigureAwait(false);
                }
                finally
                {
                    primaryWriteGate.Release();
                }
            }

            async Task SendShadowFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
            {
                await shadowWriteGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await shadowConn.WriteEncryptedFrameAsync(payload, ct).ConfigureAwait(false);
                }
                finally
                {
                    shadowWriteGate.Release();
                }
            }

            async Task SendClientFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
            {
                await clientWriteGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await clientConn.WriteEncryptedFrameAsync(payload, ct).ConfigureAwait(false);
                }
                finally
                {
                    clientWriteGate.Release();
                }
            }

            await Task.WhenAll(
                SendPrimaryFrameAsync(loginFrame.DecryptedPayload, linkCts.Token),
                SendShadowFrameAsync(shadowLoginPayload, linkCts.Token)).ConfigureAwait(false);

            var windowLock = new Lock();
            var primarySyncEndTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var shadowSyncEndTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int gatedOnInitialSync = 0;
            WireActionWindow? activeWindow = null;
            DateTimeOffset activeWindowOpenedUtc = default;
            CancellationTokenSource? windowTimerCts = null;
            int actionSeq = 0;

            async Task<SessionReconcileResult> ExecuteManualReconcileAsync(bool forceBaLang, CancellationToken externalCt)
            {
                using var opCts = CancellationTokenSource.CreateLinkedTokenSource(linkCts.Token, externalCt);
                CancellationToken ct = opCts.Token;

                await reconcileGate.WaitAsync(ct).ConfigureAwait(false);
                Interlocked.Increment(ref reconcileInProgress);
                try
                {
                    lock (windowLock)
                    {
                        if (activeWindow is not null)
                        {
                            activeWindow = null;
                            windowTimerCts?.Cancel();
                            windowTimerCts?.Dispose();
                            windowTimerCts = null;
                        }
                    }

                    // Tier 1: Sync Shadow -> Primary without disturbing the player's live Primary position/state.
                    if (!forceBaLang &&
                        session.PrimaryState.IsSyncCompleted &&
                        !session.PrimaryState.IsDead &&
                        session.PrimaryState.Life > 0 &&
                        session.PrimaryState.MapId > 0)
                    {
                        bool tier1Aligned = await TrySyncShadowToTargetAsync(
                            session,
                            SendShadowFrameAsync,
                            targetMapId: session.PrimaryState.MapId,
                            targetMpsX: session.PrimaryState.MpsX,
                            targetMpsY: session.PrimaryState.MpsY,
                            explicitFightMode: -1,
                            restoreFullPrimaryVitals: false,
                            ct).ConfigureAwait(false);

                        if (tier1Aligned)
                        {
                            session.DiffCoordinator.ResetDrift();
                            RecordManualReconcileNote(
                                session,
                                actionSeq,
                                "Tier1_ShadowToPrimary",
                                $"Map {session.PrimaryState.MapId} ({session.PrimaryState.MpsX / 32},{session.PrimaryState.MpsY / 32})");
                            return new SessionReconcileResult(
                                Succeeded: true,
                                TierUsed: "Tier1_ShadowToPrimary",
                                UsedBaLangFallback: false,
                                TargetMapId: session.PrimaryState.MapId,
                                TargetMpsX: session.PrimaryState.MpsX,
                                TargetMpsY: session.PrimaryState.MpsY,
                                PrimaryMapId: session.PrimaryState.MapId,
                                ShadowMapId: session.ShadowState.MapId,
                                Message: $"Đã đồng bộ Shadow → Primary tại Map {session.PrimaryState.MapId} ({session.PrimaryState.MpsX / 32}, {session.PrimaryState.MpsY / 32}).");
                        }
                    }

                    // Tier 2 (Fallback or explicit button): Pull BOTH Primary and Shadow to Ba Lăng Huyện Map 53 (1600, 3200).
                    const int baLangMapId = 53;
                    const int baLangMpsX = 1600 * 32;
                    const int baLangMpsY = 3200 * 32;

                    bool primaryAtBaLang = await TryPullPrimaryToBaLangAsync(
                        session,
                        SendPrimaryFrameAsync,
                        ct).ConfigureAwait(false);

                    bool shadowAtBaLang = await TrySyncShadowToTargetAsync(
                        session,
                        SendShadowFrameAsync,
                        targetMapId: baLangMapId,
                        targetMpsX: baLangMpsX,
                        targetMpsY: baLangMpsY,
                        explicitFightMode: 0,
                        restoreFullPrimaryVitals: true,
                        ct).ConfigureAwait(false);

                    bool bothAligned = primaryAtBaLang && shadowAtBaLang &&
                                       session.PrimaryState.MapId == baLangMapId &&
                                       session.ShadowState.MapId == baLangMapId;
                    if (bothAligned)
                    {
                        session.DiffCoordinator.ResetDrift();
                        RecordManualReconcileNote(
                            session,
                            actionSeq,
                            "Tier2_BothToBaLang53",
                            "Map 53 (1600,3200)");
                        return new SessionReconcileResult(
                            Succeeded: true,
                            TierUsed: "Tier2_BothToBaLang53",
                            UsedBaLangFallback: true,
                            TargetMapId: baLangMapId,
                            TargetMpsX: baLangMpsX,
                            TargetMpsY: baLangMpsY,
                            PrimaryMapId: session.PrimaryState.MapId,
                            ShadowMapId: session.ShadowState.MapId,
                            Message: "Đã đưa cả Primary và Shadow về Ba Lăng Huyện (Map 53 - 1600, 3200) và đồng bộ trạng thái.");
                    }

                    return new SessionReconcileResult(
                        Succeeded: false,
                        TierUsed: "Tier2_BothToBaLang53",
                        UsedBaLangFallback: true,
                        TargetMapId: baLangMapId,
                        TargetMpsX: baLangMpsX,
                        TargetMpsY: baLangMpsY,
                        PrimaryMapId: session.PrimaryState.MapId,
                        ShadowMapId: session.ShadowState.MapId,
                        Message: $"Đồng bộ Ba Lăng chưa hội tụ hoàn toàn (Primary Map={session.PrimaryState.MapId}, Shadow Map={session.ShadowState.MapId}).");
                }
                finally
                {
                    Interlocked.Decrement(ref reconcileInProgress);
                    reconcileGate.Release();
                }
            }

            session.AttachReconcileHandler(ExecuteManualReconcileAsync);

            void ScheduleWindowCloseTimerLocked(WireActionWindow window)
            {
                windowTimerCts?.Cancel();
                windowTimerCts?.Dispose();
                var timerCts = CancellationTokenSource.CreateLinkedTokenSource(linkCts.Token);
                windowTimerCts = timerCts;
                int baseDelayMs = Math.Max(20, _options.ActionWindowMs);
                int delayMs = window.PrimaryC2s.Opcode == ProtocolOpcodes.c2s_npcskill && baseDelayMs >= 300
                    ? Math.Max(baseDelayMs, 960)
                    : baseDelayMs;
                int graceDelayMs = Math.Min(560, Math.Max(60, baseDelayMs + 80));
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(delayMs, timerCts.Token).ConfigureAwait(false);
                        bool needInFlightGrace = false;
                        lock (windowLock)
                        {
                            if (ReferenceEquals(activeWindow, window))
                            {
                                needInFlightGrace = HasInFlightTransitionSkew(session.PrimaryState, session.ShadowState);
                                if (!needInFlightGrace)
                                {
                                    CloseActiveWindowLocked();
                                    return;
                                }
                            }
                        }

                        if (needInFlightGrace)
                        {
                            await Task.Delay(graceDelayMs, timerCts.Token).ConfigureAwait(false);
                            lock (windowLock)
                            {
                                if (ReferenceEquals(activeWindow, window))
                                {
                                    CloseActiveWindowLocked();
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Closed early or extended by coalesced continuation frame.
                    }
                }, CancellationToken.None);
            }

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
                    activeWindowOpenedUtc = DateTimeOffset.UtcNow;
                    ScheduleWindowCloseTimerLocked(window);
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

                    bool suppressUnhydratedShadowProbe =
                        rewrite.TranslationMissReason is not null &&
                        IsPassiveCacheMissQueryC2s(frame.Opcode);

                    session.PrimaryState.RecordOutboundC2s(frame.DecryptedPayload, frame.TimestampUtc);
                    if (!suppressUnhydratedShadowProbe)
                    {
                        session.ShadowState.RecordOutboundC2s(rewrite.ShadowPayload, frame.TimestampUtc);
                    }

                    if (frame.Opcode != ProtocolOpcodes.c2s_ping &&
                        !suppressUnhydratedShadowProbe &&
                        Volatile.Read(ref reconcileInProgress) == 0)
                    {
                        bool canOpenNewWindow = true;
                        lock (windowLock)
                        {
                            if (activeWindow is not null)
                            {
                                if (IsPassiveProbeOrKeepaliveC2s(frame.Opcode) &&
                                    !IsPassiveProbeOrKeepaliveC2s(activeWindow.PrimaryC2s.Opcode))
                                {
                                    canOpenNewWindow = false;
                                }
                                else if ((DateTimeOffset.UtcNow - activeWindowOpenedUtc) < TimeSpan.FromMilliseconds(5000) &&
                                         ShouldCoalesceContinuationBurstC2s(activeWindow.PrimaryC2s, frame))
                                {
                                    canOpenNewWindow = false;
                                    ScheduleWindowCloseTimerLocked(activeWindow);
                                }
                            }
                        }

                        if (canOpenNewWindow)
                        {
                            OpenActionWindow(frame, rewrite);
                        }
                    }

                    if (suppressUnhydratedShadowProbe)
                    {
                        await SendPrimaryFrameAsync(frame.DecryptedPayload, linkCts.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        await Task.WhenAll(
                            SendPrimaryFrameAsync(frame.DecryptedPayload, linkCts.Token),
                            SendShadowFrameAsync(rewrite.ShadowPayload, linkCts.Token)).ConfigureAwait(false);
                    }
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

                    if (frame.Opcode == ProtocolOpcodes.s2c_synctaskvalue &&
                        frame.DecryptedPayload.Length >= 9)
                    {
                        int syncTaskId = BinaryPrimitives.ReadInt32LittleEndian(frame.DecryptedPayload.AsSpan(1, 4));
                        int syncTaskVal = BinaryPrimitives.ReadInt32LittleEndian(frame.DecryptedPayload.AsSpan(5, 4));
                        if (syncTaskId is >= 1020 and <= 1046)
                        {
                            byte[] syncPair = new byte[8];
                            BinaryPrimitives.WriteInt32LittleEndian(syncPair.AsSpan(0, 4), syncTaskId);
                            BinaryPrimitives.WriteInt32LittleEndian(syncPair.AsSpan(4, 4), syncTaskVal);
                            byte[] qaSyncPayload = ScriptProtocolC2SPacket.CreateCanonicalPayload(
                                ScriptProtocolC2SPacket.TestSyncTaskValuesProtocolId,
                                syncPair);
                            await SendShadowFrameAsync(qaSyncPayload, linkCts.Token).ConfigureAwait(false);
                        }
                    }

                    lock (windowLock)
                    {
                        activeWindow?.AddPrimaryS2c(frame);
                    }

                    await SendClientFrameAsync(frame.DecryptedPayload, linkCts.Token).ConfigureAwait(false);
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
            Volatile.Read(ref _latestSession)?.AttachReconcileHandler(null);
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

    private static bool IsPassiveCacheMissQueryC2s(byte opcode) =>
        opcode is ProtocolOpcodes.c2s_requestnpc
            or ProtocolOpcodes.c2s_requestobj
            or ProtocolOpcodes.c2s_npcstaterequest
            or ProtocolOpcodes.c2s_requestnpcfeature;

    private static bool IsPassiveProbeOrKeepaliveC2s(byte opcode) =>
        IsPassiveCacheMissQueryC2s(opcode) ||
        opcode is ProtocolOpcodes.c2s_ping
            or 0xA7
            or ProtocolOpcodes.c2s_ongettagsrequest
            or ProtocolOpcodes.c2s_extend
            or ProtocolOpcodes.c2s_extendfriend
            or ProtocolOpcodes.c2s_changeauraskill;

    private static bool ShouldCoalesceContinuationBurstC2s(JxWireFrame activeC2s, JxWireFrame incomingC2s)
    {
        // 0. game.exe automatically emits c2s_pkapplychangenormalflag (0x76) ~60-90ms after c2s_syncend (0x43);
        //    keep Step001_C2S_0x43 open so LoginHintScript (0x63) never splits into Missing[0x63]/Extra[0x63].
        if (activeC2s.Opcode == ProtocolOpcodes.c2s_syncend &&
            incomingC2s.Opcode == ProtocolOpcodes.c2s_pkapplychangenormalflag)
        {
            return true;
        }

        // 1. Exact duplicate C2S retransmit (e.g. game.exe emitting two identical 0x72 c2s_objmouseclick frames ~10ms apart)
        if (incomingC2s.Opcode != ProtocolOpcodes.c2s_playerselui &&
            incomingC2s.DecryptedPayload.AsSpan().SequenceEqual(activeC2s.DecryptedPayload))
        {
            return true;
        }

        // 2. Continuous movement steps (0x4B/0x4C) or rapid auto-attack skill repeats (0x4D) straddling 18-Hz ticks
        if ((activeC2s.Opcode is ProtocolOpcodes.c2s_npcwalk or ProtocolOpcodes.c2s_npcrun) &&
            (incomingC2s.Opcode is ProtocolOpcodes.c2s_npcwalk or ProtocolOpcodes.c2s_npcrun))
        {
            return true;
        }

        return activeC2s.Opcode == ProtocolOpcodes.c2s_npcskill &&
               incomingC2s.Opcode == ProtocolOpcodes.c2s_npcskill;
    }

    private static async Task<bool> TrySyncShadowToTargetAsync(
        WireParitySession session,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendShadowAsync,
        int targetMapId,
        int targetMpsX,
        int targetMpsY,
        int explicitFightMode,
        bool restoreFullPrimaryVitals,
        CancellationToken ct)
    {
        // 1. If Shadow is currently dead, send c2s_npcrevive (0x74) first so UseInventoryItemZoneCommand won't drop 0x59.
        if (session.ShadowState.IsDead || session.ShadowState.Life <= 0)
        {
            byte[] reviveBuf = new byte[PlayerReviveC2SPacket.PayloadSize];
            new PlayerReviveC2SPacket(0).WritePayload(reviveBuf);
            session.ShadowState.RecordOutboundC2s(reviveBuf, DateTimeOffset.UtcNow);
            await sendShadowAsync(reviveBuf, ct).ConfigureAwait(false);

            bool revived = await WaitUntilAsync(
                () => !session.ShadowState.IsDead && session.ShadowState.Life > 0,
                TimeSpan.FromMilliseconds(1500),
                ct).ConfigureAwait(false);
            if (!revived)
            {
                return false;
            }
        }

        // 2. Close any open Shadow dialog cleanly before invoking GM Token.
        if (session.ShadowState.Dialog.IsOpen)
        {
            int closeIdx = Math.Max(0, session.ShadowState.Dialog.OptionsTcvn3Hex.Count - 1);
            byte[] closeBuf = new byte[PlayerSelectUiC2SPacket.PayloadSize];
            new PlayerSelectUiC2SPacket(closeIdx).WritePayload(closeBuf);
            session.ShadowState.RecordOutboundC2s(closeBuf, DateTimeOffset.UtcNow);
            await sendShadowAsync(closeBuf, ct).ConfigureAwait(false);
            await Task.Delay(30, ct).ConfigureAwait(false);
        }

        if (!session.ShadowState.TryFindGmTokenSlot(out VirtualJxItemSlot? shadowToken) || shadowToken is null)
        {
            return false;
        }

        // 3. Build task sync list: Primary quest/event tasks (< 6000) + scratch reconcile block (65400..).
        var pairs = new List<(ushort TaskId, int Value)>();
        foreach ((ushort taskId, int val) in session.PrimaryState.SnapshotTaskValues())
        {
            if (taskId < 6000)
            {
                pairs.Add((taskId, val));
            }
        }

        long targetExp = Math.Max(0L, session.PrimaryState.Exp);
        int targetLife = restoreFullPrimaryVitals
            ? Math.Max(1, Math.Max(session.PrimaryState.MaxLife, session.PrimaryState.Life))
            : Math.Max(1, session.PrimaryState.Life);
        int targetMana = restoreFullPrimaryVitals
            ? Math.Max(session.PrimaryState.MaxMana, session.PrimaryState.Mana)
            : Math.Max(0, session.PrimaryState.Mana);
        int targetStamina = restoreFullPrimaryVitals
            ? Math.Max(session.PrimaryState.MaxStamina, session.PrimaryState.Stamina)
            : Math.Max(0, session.PrimaryState.Stamina);

        pairs.Add((65400, 0x52454331));
        pairs.Add((65401, targetMapId));
        pairs.Add((65402, targetMpsX));
        pairs.Add((65403, targetMpsY));
        pairs.Add((65404, explicitFightMode));
        pairs.Add((65405, Math.Max(1, session.PrimaryState.Level)));
        pairs.Add((65406, unchecked((int)(targetExp & 0xFFFFFFFFL))));
        pairs.Add((65407, unchecked((int)((ulong)targetExp >> 32))));
        pairs.Add((65408, targetLife));
        pairs.Add((65409, targetMana));
        pairs.Add((65410, targetStamina));
        pairs.Add((65411, Math.Max(0, session.PrimaryState.Money)));
        pairs.Add((65412, Math.Max(0, session.PrimaryState.SaveMoney)));
        pairs.Add((65413, session.PrimaryState.PkFlag));
        pairs.Add((65414, session.PrimaryState.PkValue));
        pairs.Add((65415, session.PrimaryState.Camp));
        pairs.Add((65416, session.PrimaryState.CurrentCamp));
        pairs.Add((65417, Math.Max(0, session.PrimaryState.AttrPoints)));
        pairs.Add((65418, Math.Max(0, session.PrimaryState.SkillPoints)));

        KeyValuePair<int, ushort>[] learnedSkills = session.PrimaryState.SnapshotLearnedSkills()
            .Where(static kv => kv.Key > 0 && kv.Value > 0)
            .OrderBy(static kv => kv.Key)
            .Take(38)
            .ToArray();
        pairs.Add((65420, learnedSkills.Length));
        for (int i = 0; i < learnedSkills.Length; i++)
        {
            pairs.Add(((ushort)(65421 + (i * 2)), learnedSkills[i].Key));
            pairs.Add(((ushort)(65422 + (i * 2)), learnedSkills[i].Value));
        }

        const int maxPairsPerChunk = 60;
        for (int offset = 0; offset < pairs.Count; offset += maxPairsPerChunk)
        {
            int count = Math.Min(maxPairsPerChunk, pairs.Count - offset);
            byte[] buf = new byte[count * 8];
            for (int i = 0; i < count; i++)
            {
                (ushort tid, int tval) = pairs[offset + i];
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(i * 8, 4), tid);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan((i * 8) + 4, 4), tval);
            }

            byte[] scriptPkt = ScriptProtocolC2SPacket.CreateCanonicalPayload(
                ScriptProtocolC2SPacket.TestSyncTaskValuesProtocolId,
                buf);
            await sendShadowAsync(scriptPkt, ct).ConfigureAwait(false);
        }

        // Allow 1 zone tick so SyncTestTaskValuesZoneCommand is applied before UseInventoryItemZoneCommand runs.
        await Task.Delay(35, ct).ConfigureAwait(false);

        if (!session.ShadowState.TryFindGmTokenSlot(out shadowToken) || shadowToken is null)
        {
            return false;
        }

        byte[] eatBuf = new byte[PlayerEatItemC2SPacket.PayloadSize];
        new PlayerEatItemC2SPacket(
            GridX: shadowToken.GridX,
            RuntimeItemId: shadowToken.RuntimeItemId,
            GridY: shadowToken.GridY,
            ContainerPlace: shadowToken.ContainerPlace).WritePayload(eatBuf);
        session.ShadowState.RecordOutboundC2s(eatBuf, DateTimeOffset.UtcNow);
        await sendShadowAsync(eatBuf, ct).ConfigureAwait(false);

        return await WaitUntilAsync(
            () => !session.ShadowState.IsDead &&
                  session.ShadowState.MapId == targetMapId &&
                  Math.Abs(session.ShadowState.MpsX - targetMpsX) <= 128 &&
                  Math.Abs(session.ShadowState.MpsY - targetMpsY) <= 128,
            TimeSpan.FromMilliseconds(2200),
            ct).ConfigureAwait(false);
    }

    private static async Task<bool> TryPullPrimaryToBaLangAsync(
        WireParitySession session,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendPrimaryAsync,
        CancellationToken ct)
    {
        const int baLangMapId = 53;
        const int baLangMpsX = 1600 * 32;
        const int baLangMpsY = 3200 * 32;

        if (session.PrimaryState.IsDead || session.PrimaryState.Life <= 0)
        {
            byte[] reviveBuf = new byte[PlayerReviveC2SPacket.PayloadSize];
            new PlayerReviveC2SPacket(0).WritePayload(reviveBuf);
            session.PrimaryState.RecordOutboundC2s(reviveBuf, DateTimeOffset.UtcNow);
            await sendPrimaryAsync(reviveBuf, ct).ConfigureAwait(false);

            bool revived = await WaitUntilAsync(
                () => !session.PrimaryState.IsDead && session.PrimaryState.Life > 0,
                TimeSpan.FromMilliseconds(1500),
                ct).ConfigureAwait(false);
            if (!revived)
            {
                return false;
            }
        }

        if (session.PrimaryState.Dialog.IsOpen)
        {
            int closeIdx = Math.Max(0, session.PrimaryState.Dialog.OptionsTcvn3Hex.Count - 1);
            byte[] closeBuf = new byte[PlayerSelectUiC2SPacket.PayloadSize];
            new PlayerSelectUiC2SPacket(closeIdx).WritePayload(closeBuf);
            session.PrimaryState.RecordOutboundC2s(closeBuf, DateTimeOffset.UtcNow);
            await sendPrimaryAsync(closeBuf, ct).ConfigureAwait(false);
            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        if (session.PrimaryState.MapId == baLangMapId &&
            Math.Abs(session.PrimaryState.MpsX - baLangMpsX) <= 128 &&
            Math.Abs(session.PrimaryState.MpsY - baLangMpsY) <= 128)
        {
            return true;
        }

        if (!session.PrimaryState.TryFindGmTokenSlot(out VirtualJxItemSlot? primaryToken) || primaryToken is null)
        {
            return session.PrimaryState.MapId == baLangMapId;
        }

        uint prevSeq = session.PrimaryState.Dialog.Sequence;
        byte[] eatBuf = new byte[PlayerEatItemC2SPacket.PayloadSize];
        new PlayerEatItemC2SPacket(
            GridX: primaryToken.GridX,
            RuntimeItemId: primaryToken.RuntimeItemId,
            GridY: primaryToken.GridY,
            ContainerPlace: primaryToken.ContainerPlace).WritePayload(eatBuf);
        session.PrimaryState.RecordOutboundC2s(eatBuf, DateTimeOffset.UtcNow);
        await sendPrimaryAsync(eatBuf, ct).ConfigureAwait(false);

        bool rootOpened = await WaitUntilAsync(
            () => session.PrimaryState.Dialog.IsOpen && session.PrimaryState.Dialog.Sequence != prevSeq,
            TimeSpan.FromMilliseconds(1000),
            ct).ConfigureAwait(false);
        if (!rootOpened)
        {
            return false;
        }

        uint rootSeq = session.PrimaryState.Dialog.Sequence;
        byte[] selTeleportMenu = new byte[PlayerSelectUiC2SPacket.PayloadSize];
        new PlayerSelectUiC2SPacket(5).WritePayload(selTeleportMenu); // [5] Bản đồ & Di chuyển
        session.PrimaryState.RecordOutboundC2s(selTeleportMenu, DateTimeOffset.UtcNow);
        await sendPrimaryAsync(selTeleportMenu, ct).ConfigureAwait(false);

        bool tpOpened = await WaitUntilAsync(
            () => session.PrimaryState.Dialog.IsOpen && session.PrimaryState.Dialog.Sequence != rootSeq,
            TimeSpan.FromMilliseconds(1000),
            ct).ConfigureAwait(false);
        if (!tpOpened)
        {
            return false;
        }

        byte[] selBaLang = new byte[PlayerSelectUiC2SPacket.PayloadSize];
        new PlayerSelectUiC2SPacket(0).WritePayload(selBaLang); // [0] Ba Lăng Huyện - Trung tâm (53)
        session.PrimaryState.RecordOutboundC2s(selBaLang, DateTimeOffset.UtcNow);
        await sendPrimaryAsync(selBaLang, ct).ConfigureAwait(false);

        return await WaitUntilAsync(
            () => session.PrimaryState.MapId == baLangMapId &&
                  Math.Abs(session.PrimaryState.MpsX - baLangMpsX) <= 128 &&
                  Math.Abs(session.PrimaryState.MpsY - baLangMpsY) <= 128,
            TimeSpan.FromMilliseconds(1800),
            ct).ConfigureAwait(false);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, CancellationToken ct)
    {
        if (predicate())
        {
            return true;
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(20, ct).ConfigureAwait(false);
            if (predicate())
            {
                return true;
            }
        }

        return predicate();
    }

    private static void RecordManualReconcileNote(
        WireParitySession session,
        int stepIndex,
        string tierLabel,
        string targetSummary)
    {
        session.DiffCoordinator.RecordImmediateFinding(new ParityFinding(
            FindingId: $"SYNC-{DateTimeOffset.UtcNow:HHmmss}",
            ClusterKey: $"SessionReconcile::{tierLabel}",
            Severity: FindingSeverity.Warning,
            Category: FindingCategory.Position,
            StepIndex: stepIndex,
            StepName: "ManualDashboardReconcile",
            FieldPath: "Session.ManualReconcile",
            LegacyValue: targetSummary,
            RebornValue: targetSummary,
            Repro: new FindingReproContext(
                MapId: session.PrimaryState.MapId,
                PlayerPos: [session.PrimaryState.MpsX, session.PrimaryState.MpsY],
                NpcName: string.Empty,
                Actions: [tierLabel]),
            StateDelta: new FindingStateDelta(
                LegacyMoneyDelta: 0,
                RebornMoneyDelta: 0,
                LegacyExpDelta: 0,
                RebornExpDelta: 0,
                LegacyLevel: session.PrimaryState.Level,
                RebornLevel: session.ShadowState.Level,
                LegacyMapId: session.PrimaryState.MapId,
                RebornMapId: session.ShadowState.MapId),
            LegacyRecentS2cOpcodes: [],
            RebornRecentS2cOpcodes: [],
            Artifacts: new FindingArtifacts(),
            TimestampUtc: DateTimeOffset.UtcNow));
    }

    private static bool HasInFlightTransitionSkew(
        ShadowStates.ProxyShadowClientState primary,
        ShadowStates.ProxyShadowClientState shadow)
    {
        if (primary.MapId != shadow.MapId ||
            primary.Dialog.IsOpen != shadow.Dialog.IsOpen ||
            primary.Exp != shadow.Exp)
        {
            return true;
        }

        int dx = Math.Abs(primary.MpsX - shadow.MpsX);
        int dy = Math.Abs(primary.MpsY - shadow.MpsY);
        return Math.Max(dx, dy) > 256;
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
