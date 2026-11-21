using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using JXReborn.ClientDriver;
using JXReborn.DualParityServer.Configuration;

namespace JXReborn.DualParityServer.Standalone;

/// <summary>
/// Manages an isolated <c>JXReborn.Host</c> child process bound to dedicated ports and an ephemeral T=0 SQLite DB,
/// assigned to a <see cref="Win32JobObject"/> so it terminates cleanly on exit without touching dev DBs or ports.
/// </summary>
public sealed class RebornHostLauncher : IAsyncDisposable
{
    private readonly DualParityOptions _options;
    private readonly string _sessionId;
    private readonly RingLogBuffer _logs = new(300);

    private Win32JobObject? _jobObject;
    private Process? _process;
    private string? _tempWorkDir;
    private string _state = "Not started";
    private bool _isReady;

    public RebornHostLauncher(DualParityOptions options, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _options = options;
        _sessionId = sessionId;
    }

    public bool IsReady => _isReady;

    public int? ProcessId => _process?.Id;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _state = "Starting JXReborn.Host...";
        string hostProjectPath = ResolveHostProjectPath(_options.RebornHostProjectPath);
        string serverRootDir = ResolveServerRootDirectory(hostProjectPath);

        _tempWorkDir = Path.Combine(Path.GetTempPath(), "jx-dual-parity", _sessionId);
        Directory.CreateDirectory(_tempWorkDir);
        string ephemeralDbPath = Path.Combine(_tempWorkDir, "reborn-t0.db");

        string? resolvedPackDir = ResolvePackDirectory(_options.RebornPackDirectory, serverRootDir);
        ProcessStartInfo psi = BuildStartInfo(hostProjectPath, serverRootDir, resolvedPackDir);
        if (TryResolveKeyTablePath(serverRootDir) is { } keyTablePath)
        {
            psi.Environment["LEGACY_KEYTABLE_PATH"] = keyTablePath;
            psi.Environment["LegacyGateway__KeyTablePath"] = keyTablePath;
        }
        psi.Environment["Persistence__Provider"] = "Sqlite";
        psi.Environment["Persistence__ConnectionString"] = $"Data Source={ephemeralDbPath}";
        psi.Environment["Persistence__AutoInitializeDatabase"] = "true";
        psi.Environment["Application__AutoProvisionAccounts"] = "true";
        psi.Environment["Application__AdvertisedGameServerHost"] = "127.0.0.1";
        psi.Environment["Application__AdvertisedGameServerPort"] = _options.RebornGameServerPort.ToString(CultureInfo.InvariantCulture);
        psi.Environment["LegacyGateway__BindAddress"] = "127.0.0.1";
        psi.Environment["LegacyGateway__BishopPort"] = _options.RebornPort.ToString(CultureInfo.InvariantCulture);
        psi.Environment["LegacyGateway__GameServerPort"] = _options.RebornGameServerPort.ToString(CultureInfo.InvariantCulture);
        psi.Environment["ZoneEngine__EnableTestGmToken"] = "true";
        psi.Environment["ZoneEngine__AutoGrantGmTokenOnLogin"] = "true";
        psi.Environment["ZoneEngine__TestGmTokenItemCode"] = "cam-nang-tan-thu";
        psi.Environment["ZoneEngine__TestGmTokenScriptCode"] = @"\script\global\nobitaxd\item\camnang_gm.lua";
        if (TryResolveWorkbenchScriptsDirectory(serverRootDir) is { } workbenchScriptsDir)
        {
            psi.Environment["ScriptHost__ScriptDirectories__1"] = workbenchScriptsDir;
            psi.Environment["ScriptHost__ScriptLayers__1"] = "JXReborn.Workbench.Scripts.dll";
        }

        if (!string.IsNullOrWhiteSpace(resolvedPackDir))
        {
            psi.Environment["Pack__PackDirectory"] = resolvedPackDir;
        }

        _logs.Append($"Launching JXReborn.Host on 127.0.0.1:{_options.RebornPort} (GS {_options.RebornGameServerPort}), DB={ephemeralDbPath}");

        _jobObject = new Win32JobObject();
        _process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start JXReborn.Host from '{hostProjectPath}'.");
        _jobObject.AssignProcess(_process);

        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) _logs.Append(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) _logs.Append($"[ERR] {e.Data}"); };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await WaitForTcpPortAsync("127.0.0.1", _options.RebornPort, TimeSpan.FromSeconds(60), cancellationToken);
        _isReady = true;
        _state = "Running";
    }

    public string GetAllLogs() => _logs.GetAllText();

    public ManagedServerStatus GetStatus() => new(
        Name: "reborn-server",
        Enabled: true,
        IsReady: _isReady,
        State: _process is { HasExited: true } ? $"Exited ({_process.ExitCode})" : _state,
        Endpoint: $"127.0.0.1:{_options.RebornPort} (GS {_options.RebornGameServerPort})",
        Identifier: _process is not null ? $"PID {_process.Id}" : null,
        RecentLogs: _logs.GetLines(60));

    private ProcessStartInfo BuildStartInfo(string hostProjectPath, string serverRootDir, string? resolvedPackDir)
    {
        string projectDir = Path.GetDirectoryName(hostProjectPath)!;
        string prebuiltDll = Path.Combine(projectDir, "bin", "Debug", "net10.0", "JXReborn.Host.dll");
        string releaseDll = Path.Combine(projectDir, "bin", "Release", "net10.0", "JXReborn.Host.dll");

        string? dllToRun = File.Exists(prebuiltDll) ? prebuiltDll : (File.Exists(releaseDll) ? releaseDll : null);

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = serverRootDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (dllToRun is not null)
        {
            _logs.Append($"Using prebuilt host binary: {dllToRun}");
            psi.ArgumentList.Add(dllToRun);
        }
        else
        {
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add("--project");
            psi.ArgumentList.Add(hostProjectPath);
            psi.ArgumentList.Add("--");
        }

        psi.ArgumentList.Add("--contentRoot");
        psi.ArgumentList.Add(serverRootDir);
        if (!string.IsNullOrWhiteSpace(resolvedPackDir))
        {
            psi.ArgumentList.Add("--pack");
            psi.ArgumentList.Add(resolvedPackDir);
        }

        return psi;
    }

    private async Task WaitForTcpPortAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_process is { HasExited: true })
            {
                _state = $"Crashed (exit {_process.ExitCode})";
                string tail = string.Join(Environment.NewLine, _logs.GetLines(30));
                throw new InvalidOperationException(
                    $"JXReborn.Host exited prematurely with code {_process.ExitCode}. Recent logs:{Environment.NewLine}{tail}");
            }

            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(host, port, cancellationToken);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(250, cancellationToken);
            }
        }

        _state = "Startup timed out";
        string recent = string.Join(Environment.NewLine, _logs.GetLines(25));
        throw new TimeoutException(
            $"Timed out after {timeout.TotalSeconds:F0}s waiting for JXReborn.Host on {host}:{port}. Recent logs:{Environment.NewLine}{recent}");
    }

    public static string ResolveHostProjectPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string full = Path.GetFullPath(configuredPath);
            if (File.Exists(full))
            {
                return full;
            }

            throw new FileNotFoundException($"Configured JXReborn.Host.csproj not found at '{full}'.", full);
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? dir = new(start);
            while (dir is not null)
            {
                if (visited.Add(dir.FullName))
                {
                    string candidate = Path.Combine(dir.FullName, "server", "src", "JXReborn.Host", "JXReborn.Host.csproj");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }

                    string sibling = Path.Combine(dir.FullName, "src", "JXReborn.Host", "JXReborn.Host.csproj");
                    if (File.Exists(sibling))
                    {
                        return sibling;
                    }
                }

                dir = dir.Parent;
            }
        }

        throw new FileNotFoundException("Could not locate server/src/JXReborn.Host/JXReborn.Host.csproj. Specify --reborn-project <path>.");
    }

    private static string ResolveServerRootDirectory(string hostProjectPath)
    {
        DirectoryInfo? dir = new(Path.GetDirectoryName(hostProjectPath)!);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "JXReborn.slnx")) ||
                Directory.Exists(Path.Combine(dir.FullName, "artifacts")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(hostProjectPath)!, "..", "..", ".."));
    }

    private static string? ResolvePackDirectory(string? configuredPackDir, string serverRootDir)
    {
        if (!string.IsNullOrWhiteSpace(configuredPackDir) && Directory.Exists(configuredPackDir))
        {
            return Path.GetFullPath(configuredPackDir);
        }

        string workspaceRoot = Directory.GetParent(serverRootDir)?.FullName ?? serverRootDir;
        string[] candidates =
        [
            Path.Combine(workspaceRoot, "pack-tieudaotu"),
            Path.Combine(serverRootDir, "packs", "synthetic")
        ];

        return candidates.FirstOrDefault(dir => File.Exists(Path.Combine(dir, "pack.json")));
    }

    private static string? TryResolveKeyTablePath(string serverRootDir)
    {
        string? envPath = Environment.GetEnvironmentVariable("LEGACY_KEYTABLE_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            return envPath;
        }

        string workspaceRoot = Directory.GetParent(serverRootDir)?.FullName ?? serverRootDir;
        string[] candidates =
        [
            Path.Combine(serverRootDir, "artifacts", "keys", "legacy-public-keys.bin"),
            Path.Combine(workspaceRoot, "workbench", "artifacts", "keys", "legacy-public-keys.bin")
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? TryResolveWorkbenchScriptsDirectory(string serverRootDir)
    {
        string workspaceRoot = Directory.GetParent(serverRootDir)?.FullName ?? serverRootDir;
        string[] candidates =
        [
            Path.Combine(workspaceRoot, "workbench", "scripts"),
            Path.Combine(AppContext.BaseDirectory, "scripts")
        ];

        return candidates.FirstOrDefault(dir => File.Exists(Path.Combine(dir, "JXReborn.Workbench.Scripts.dll")));
    }

    public async ValueTask DisposeAsync()
    {
        _state = "Stopping...";
        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync();
                }
            }
            catch (Exception)
            {
                // Best-effort process cleanup.
            }

            _process.Dispose();
            _process = null;
        }

        _jobObject?.Dispose();
        _jobObject = null;
        _isReady = false;
        _state = "Stopped";

        if (!string.IsNullOrWhiteSpace(_tempWorkDir) && Directory.Exists(_tempWorkDir))
        {
            try
            {
                Directory.Delete(_tempWorkDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp DB cleanup.
            }
        }
    }
}
