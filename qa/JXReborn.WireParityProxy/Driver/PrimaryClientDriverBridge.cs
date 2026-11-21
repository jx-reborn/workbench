using System.Diagnostics;
using JXReborn.ClientDriver;
using JXReborn.WireParityProxy.Configuration;
using JXReborn.WireParityProxy.Proxy;

namespace JXReborn.WireParityProxy.Driver;

/// <summary>
/// Optional Named-Pipe driver bridge attaching to the single <c>game.exe</c> instance
/// (either spawned headless or live-attached) to read client RAM ground-truth snapshots
/// and cross-check <see cref="WireParitySession.PrimaryState"/>.
/// </summary>
public sealed class PrimaryClientDriverBridge : IAsyncDisposable
{
    private readonly Lock _sync = new();
    private JxHeadlessClient? _client;

    public bool IsConnected
    {
        get
        {
            JxHeadlessClient? current;
            lock (_sync)
            {
                current = _client;
            }

            return IsClientProcessAlive(current);
        }
    }

    public JxHeadlessClient? Client
    {
        get
        {
            JxHeadlessClient? current;
            lock (_sync)
            {
                current = _client;
            }

            return IsClientProcessAlive(current) ? current : null;
        }
    }

    public async Task ConnectLiveProcessAsync(int pid, CancellationToken cancellationToken = default)
    {
        JxHeadlessClient attached = await JxHeadlessClient.AttachToProcessAsync(pid, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await ReplaceClientAsync(attached).ConfigureAwait(false);
    }

    public async Task<JxHeadlessClient> SpawnAgainstProxyAsync(
        WireParityProxyOptions options,
        int boundBishopPort,
        bool interactiveWindow = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        JxHeadlessClient spawned = await JxHeadlessClient.SpawnAsync(
            new JxHeadlessOptions
            {
                ServerIp = options.BindHost,
                GatewayPort = boundBishopPort,
                InteractiveWindow = interactiveWindow
            },
            cancellationToken).ConfigureAwait(false);

        await ReplaceClientAsync(spawned).ConfigureAwait(false);
        return spawned;
    }

    public async Task<JxHeadlessClient> EnsureAliveClientAsync(
        WireParityProxyOptions options,
        int boundBishopPort,
        CancellationToken cancellationToken = default)
    {
        JxHeadlessClient? current = Client;
        if (current is not null)
        {
            return current;
        }

        await InvalidateBrokenClientAsync().ConfigureAwait(false);
        return await SpawnAgainstProxyAsync(options, boundBishopPort, options.InteractiveWindow, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<WorldDomSnapshot?> GetClientRamSnapshotAsync(CancellationToken cancellationToken = default)
    {
        JxHeadlessClient? current = Client;
        if (current is null)
        {
            return null;
        }

        try
        {
            return await current.GetStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            await InvalidateBrokenClientAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                $"game.exe driver pipe disconnected ({ex.Message}). Call perform_action(action: 'login') to respawn game.exe.",
                ex);
        }
    }

    public async Task InvalidateBrokenClientAsync()
    {
        await ReplaceClientAsync(null).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await ReplaceClientAsync(null).ConfigureAwait(false);
    }

    private async Task ReplaceClientAsync(JxHeadlessClient? next)
    {
        JxHeadlessClient? previous;
        lock (_sync)
        {
            previous = _client;
            _client = next;
        }

        if (previous is not null && !ReferenceEquals(previous, next))
        {
            try
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort cleanup of dead pipe/process.
            }
        }
    }

    private static bool IsClientProcessAlive(JxHeadlessClient? client)
    {
        if (client is null)
        {
            return false;
        }

        if (client.ProcessId is not int pid)
        {
            return true;
        }

        try
        {
            using Process proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
