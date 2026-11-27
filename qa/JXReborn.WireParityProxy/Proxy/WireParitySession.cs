using JXReborn.DualParityServer.Diffing;
using JXReborn.WireParityProxy.Configuration;
using JXReborn.WireParityProxy.Diffing;
using JXReborn.WireParityProxy.ShadowStates;
using JXReborn.WireParityProxy.Translation;

namespace JXReborn.WireParityProxy.Proxy;

/// <summary>
/// Holds all per-character live state for one GameServer dual-backend session
/// (Primary &amp; Shadow states, ID translator, C2S rewriter, Protocol Gate, and Diff Coordinator).
/// </summary>
public sealed class WireParitySession
{
    private long _c2sFrames;
    private long _primaryS2cFrames;
    private long _shadowS2cFrames;
    private Func<bool, CancellationToken, Task<SessionReconcileResult>>? _reconcileHandler;

    public WireParitySession(string roleName, WireParityProxyOptions options, SemanticDiffEngine? sharedSemanticDiff = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        SessionId = $"wpp-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        RoleName = roleName;
        StartedAtUtc = DateTimeOffset.UtcNow;
        PrimaryState = new ProxyShadowClientState(options.PrimaryBackendName);
        ShadowState = new ProxyShadowClientState(options.ShadowBackendName);
        Translator = new EntityIdTranslator(PrimaryState, ShadowState);
        Rewriter = new C2sPacketRewriter(Translator);
        ProtocolGate = new ProtocolSafetyGate();
        DiffCoordinator = new WireParityDiffCoordinator(options, sharedSemanticDiff);
    }

    public string SessionId { get; }

    public string RoleName { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public ProxyShadowClientState PrimaryState { get; }

    public ProxyShadowClientState ShadowState { get; }

    public EntityIdTranslator Translator { get; }

    public C2sPacketRewriter Rewriter { get; }

    public ProtocolSafetyGate ProtocolGate { get; }

    public WireParityDiffCoordinator DiffCoordinator { get; }

    public uint ClientToken1 { get; private set; }

    public uint ClientToken2 { get; private set; }

    public long C2sFrames => Interlocked.Read(ref _c2sFrames);

    public long PrimaryS2cFrames => Interlocked.Read(ref _primaryS2cFrames);

    public long ShadowS2cFrames => Interlocked.Read(ref _shadowS2cFrames);

    public void SetClientTokens(uint token1, uint token2)
    {
        ClientToken1 = token1;
        ClientToken2 = token2;
    }

    public void IncrementC2s() => Interlocked.Increment(ref _c2sFrames);

    public void IncrementPrimaryS2c() => Interlocked.Increment(ref _primaryS2cFrames);

    public void IncrementShadowS2c() => Interlocked.Increment(ref _shadowS2cFrames);

    internal void AttachReconcileHandler(Func<bool, CancellationToken, Task<SessionReconcileResult>>? handler) =>
        Volatile.Write(ref _reconcileHandler, handler);

    internal Func<bool, CancellationToken, Task<SessionReconcileResult>>? GetReconcileHandler() =>
        Volatile.Read(ref _reconcileHandler);
}
