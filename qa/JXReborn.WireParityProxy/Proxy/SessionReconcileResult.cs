namespace JXReborn.WireParityProxy.Proxy;

/// <summary>
/// Outcome of a manual 2-server session reconciliation triggered by the user from the WireParityProxy dashboard.
/// </summary>
public sealed record SessionReconcileResult(
    bool Succeeded,
    string TierUsed,
    bool UsedBaLangFallback,
    int TargetMapId,
    int TargetMpsX,
    int TargetMpsY,
    int PrimaryMapId,
    int ShadowMapId,
    string Message);
