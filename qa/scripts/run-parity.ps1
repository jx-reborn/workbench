<#
.SYNOPSIS
    Boots an isolated legacy reference runtime container (`--tmpfs /var/lib/mysql`, Zero-Sim package)
    via `wslc` or `docker` and runs the JXReborn Differential Parity test suite.

.PARAMETER Profile
    Which Zero-Sim package profile to mount (`v1`). Defaults to `v1`.

.PARAMETER Mode
    Parity execution mode:
    - `Live` (default): Boots the isolated `T=0` legacy oracle container and runs Oracle vs. JXReborn side-by-side.
    - `Record`: Runs `Live` dual-run and writes verified `*.capture.json` golden baselines after all assertions pass.
    - `Replay`: Offline mode (0 Docker/container); replays JXReborn against recorded `Baselines/*.capture.json` files.

.PARAMETER Filter
    Optional xUnit test filter expression (e.g. `J04`, `DifferentialParityWorldTests`, `DualRun_LoginAndEnterWorld`).
    Defaults to `Category=DifferentialParity`.

.PARAMETER Repack
    Rebuilds the `.tar.gz` package profile before starting the container and passes `FORCE_EXTRACT=1`.

.PARAMETER ReuseRunning
    Reuses an already-running oracle container of the same profile instead of recreating a fresh `tmpfs` DB.

.PARAMETER StopAfter
    Removes the oracle container after the test run finishes.

.PARAMETER StartOnly
    Starts and verifies the oracle container without running `dotnet test`.
#>
[CmdletBinding()]
param(
    [ValidateSet("v1")]
    [string]$Profile = "v1",

    [ValidateSet("Live", "Record", "Replay")]
    [string]$Mode = "Live",

    [string]$Filter = "",

    [switch]$Repack,

    [switch]$ReuseRunning,

    [switch]$StopAfter,

    [switch]$StartOnly,

    [switch]$NoBuild,

    [int]$BishopPort = 15622,

    [int]$GameServerPort = 16666,

    [string]$ContainerName = "legacy-oracle-test",

    [string]$Image = ""
)

$ErrorActionPreference = "Stop"
$QaRoot = Split-Path -Parent $PSScriptRoot
$RepoRoot = Split-Path -Parent $QaRoot

if ([string]::IsNullOrWhiteSpace($Image)) {
    $Image = if (-not [string]::IsNullOrWhiteSpace($env:LEGACY_ORACLE_IMAGE)) {
        $env:LEGACY_ORACLE_IMAGE
    } else {
        "ghcr.io/jx-reborn/legacy-server-standalone:latest"
    }
}

$testProj = Join-Path $QaRoot "JXReborn.ClientDriver.Tests\JXReborn.ClientDriver.Tests.csproj"
$resolvedFilter = if ([string]::IsNullOrWhiteSpace($Filter)) {
    "Category=DifferentialParity"
} elseif ($Filter -match "[=~|&]") {
    $Filter
} else {
    "Category=DifferentialParity&FullyQualifiedName~$Filter"
}

# Fast offline Replay branch: 0 Docker / 0 Oracle container startup
if ($Mode -eq "Replay") {
    Write-Host "[PARITY] Mode: Replay (Offline Golden Baseline — no container required) | Profile: $Profile" -ForegroundColor Cyan

    $prevHost   = $env:LEGACY_ORACLE_HOST
    $prevPort   = $env:LEGACY_ORACLE_PORT
    $prevProf   = $env:LEGACY_ORACLE_PROFILE
    $prevCont   = $env:LEGACY_ORACLE_CONTAINER
    $prevMode   = $env:LEGACY_PARITY_MODE
    $prevPkgSha = $env:LEGACY_ORACLE_PACKAGE_SHA256

    try {
        Remove-Item Env:LEGACY_ORACLE_HOST -ErrorAction SilentlyContinue
        Remove-Item Env:LEGACY_ORACLE_PORT -ErrorAction SilentlyContinue
        Remove-Item Env:LEGACY_ORACLE_CONTAINER -ErrorAction SilentlyContinue
        Remove-Item Env:LEGACY_ORACLE_PACKAGE_SHA256 -ErrorAction SilentlyContinue
        $env:LEGACY_ORACLE_PROFILE = $Profile
        $env:LEGACY_PARITY_MODE    = "replay"

        $dotnetArgs = @("test", $testProj, "--filter", $resolvedFilter, "--logger", "console;verbosity=normal")
        if ($NoBuild) {
            $dotnetArgs += "--no-build"
        }

        Write-Host "[PARITY] Executing: dotnet $($dotnetArgs -join ' ')" -ForegroundColor Cyan
        & dotnet @dotnetArgs
        $testExit = $LASTEXITCODE
        if ($testExit -ne 0) {
            throw "Differential Parity Replay test run failed with exit code $testExit."
        }
    } finally {
        $env:LEGACY_ORACLE_HOST           = $prevHost
        $env:LEGACY_ORACLE_PORT           = $prevPort
        $env:LEGACY_ORACLE_PROFILE        = $prevProf
        $env:LEGACY_ORACLE_CONTAINER      = $prevCont
        $env:LEGACY_PARITY_MODE           = $prevMode
        $env:LEGACY_ORACLE_PACKAGE_SHA256 = $prevPkgSha
    }

    return
}

# 1. Resolve container CLI (`wslc` or `docker`)
$Cli = $null
foreach ($candidate in @("wslc", "docker")) {
    $cmd = Get-Command $candidate -ErrorAction SilentlyContinue
    if ($null -ne $cmd) {
        $Cli = $candidate
        break
    }
}
if ($null -eq $Cli) {
    throw "Neither 'wslc' nor 'docker' is installed or on PATH."
}
Write-Host "[PARITY] Container engine: $Cli | Profile: $Profile | Ports: Bishop=$BishopPort, GS=$GameServerPort" -ForegroundColor Cyan

# 2. Ensure target Zero-Sim `.tar.gz` package exists
$PackageFileName = switch ($Profile) {
    "v1" { "jxser-v1.tar.gz" }
}
$PackagePath = Join-Path $RepoRoot "artifacts\packages\$PackageFileName"
$ForceExtract = "0"

if ($Repack -or -not (Test-Path -LiteralPath $PackagePath)) {
    Write-Host "[PARITY] Packing profile '$Profile' -> $PackagePath..." -ForegroundColor Yellow
    $packScript = Join-Path $PSScriptRoot "pack-legacy-profiles.ps1"
    & powershell -NoProfile -ExecutionPolicy Bypass -File $packScript -Profile $Profile
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to build package profile '$Profile'."
    }
    $ForceExtract = "1"
}

function Invoke-CliCapture([string[]]$Arguments) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Cli
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $quoted = foreach ($a in $Arguments) {
        if ($a -match '[\s"]') { '"' + ($a -replace '"', '\"') + '"' } else { $a }
    }
    $psi.Arguments = ($quoted -join " ")
    $proc = [System.Diagnostics.Process]::Start($psi)
    $stdout = $proc.StandardOutput.ReadToEnd()
    $stderr = $proc.StandardError.ReadToEnd()
    $proc.WaitForExit()
    $combined = ($stdout + "`n" + $stderr).Trim()
    return [pscustomobject]@{ ExitCode = $proc.ExitCode; Output = $combined }
}

function Get-ContainerLine([string]$PsTable, [string]$Name) {
    $escaped = [regex]::Escape($Name)
    foreach ($line in ($PsTable -split "`r?`n")) {
        if ($line -match "(?:^|\s)$escaped(?:\s|$)") {
            return $line
        }
    }
    return $null
}

$VolumeName = "legacy-oracle-test-$Profile"
$null = Invoke-CliCapture @("volume", "create", $VolumeName)

# 3. Start or recreate oracle container (`--tmpfs /var/lib/mysql` = fresh DB every start)
$psOut = (Invoke-CliCapture @("ps", "-a")).Output
$existingLine = Get-ContainerLine $psOut $ContainerName
$isAlreadyUp = ($null -ne $existingLine) -and ($existingLine -match "\bUp\b" -or $existingLine -match "running")

if (-not ($ReuseRunning -and $isAlreadyUp -and $ForceExtract -eq "0")) {
    # Always extract the mounted profile archive when recreating a fresh T=0 container,
    # because the base container image may already ship a pre-populated /home/jxser directory.
    $ForceExtract = "1"
    if ($null -ne $existingLine) {
        Write-Host "[PARITY] Removing previous container '$ContainerName' for clean T=0 tmpfs database..." -ForegroundColor DarkGray
        if ($Cli -eq "wslc") {
            $null = Invoke-CliCapture @("remove", "-f", $ContainerName)
        } else {
            $null = Invoke-CliCapture @("rm", "-f", $ContainerName)
        }
    }

    Write-Host "[PARITY] Launching '$ContainerName' ($Image) with --tmpfs /var/lib/mysql..." -ForegroundColor Cyan
    $runArgs = @(
        "run", "-d",
        "--name", $ContainerName,
        "-p", "${BishopPort}:${BishopPort}",
        "-p", "${GameServerPort}:${GameServerPort}",
        "-v", "${PackagePath}:/opt/jxser/jxserv.tar.gz:ro",
        "-v", "${VolumeName}:/home/jxser",
        "--tmpfs", "/var/lib/mysql",
        "-e", "SERVER_IP=127.0.0.1",
        "-e", "DISABLE_SIMS=1",
        "-e", "BISHOP_PORT=$BishopPort",
        "-e", "GS_PORT=$GameServerPort",
        "-e", "FORCE_EXTRACT=$ForceExtract",
        "--ulimit", "nofile=65535:65535",
        "--stop-timeout", "35",
        $Image
    )
    $runRes = Invoke-CliCapture $runArgs
    if ($runRes.ExitCode -ne 0) {
        throw "Failed to start container '$ContainerName':`n$($runRes.Output)"
    }
    Write-Host "[PARITY] Started container $($runRes.Output.Trim())" -ForegroundColor DarkGray
} else {
    Write-Host "[PARITY] Reusing running container '$ContainerName'." -ForegroundColor Green
}

# 4. Wait until stack reports UP and both Bishop + GameServer ports accept TCP on 127.0.0.1
function Test-TcpPort([int]$Port) {
    $tcp = New-Object System.Net.Sockets.TcpClient
    try {
        $iar = $tcp.BeginConnect("127.0.0.1", $Port, $null, $null)
        if (-not $iar.AsyncWaitHandle.WaitOne(500, $false)) { return $false }
        $tcp.EndConnect($iar)
        return $true
    } catch {
        return $false
    } finally {
        $tcp.Dispose()
    }
}

Write-Host "[PARITY] Waiting for Legacy stack in '$ContainerName' to become ready..." -NoNewline
$ready = $false
for ($i = 0; $i -lt 90; $i++) {
    $logs = (Invoke-CliCapture @("logs", $ContainerName)).Output
    if ($logs -match "=== JX-Linux Docker stack is UP ===" -and (Test-TcpPort $BishopPort) -and (Test-TcpPort $GameServerPort)) {
        $ready = $true
        break
    }
    $stateCheck = (Invoke-CliCapture @("ps", "-a")).Output
    $curLine = Get-ContainerLine $stateCheck $ContainerName
    if ($null -ne $curLine -and $curLine -match "Exited") {
        Write-Host ""
        Write-Host $logs -ForegroundColor Red
        throw "Container '$ContainerName' exited unexpectedly during startup."
    }
    Write-Host "." -NoNewline
    Start-Sleep -Seconds 1
}
Write-Host ""

if (-not $ready) {
    $logs = (Invoke-CliCapture @("logs", $ContainerName)).Output
    Write-Host $logs -ForegroundColor Red
    throw "Timed out waiting for '$ContainerName' to become ready on ports $BishopPort / $GameServerPort."
}
Write-Host "[PARITY] Oracle '$ContainerName' ($Profile) is READY on 127.0.0.1:$BishopPort (GS $GameServerPort)." -ForegroundColor Green

if ($StartOnly) {
    return
}

# 5. Run Differential Parity xUnit tests
$PackageSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $PackagePath).Hash.ToLowerInvariant()

$prevHost   = $env:LEGACY_ORACLE_HOST
$prevPort   = $env:LEGACY_ORACLE_PORT
$prevProf   = $env:LEGACY_ORACLE_PROFILE
$prevCont   = $env:LEGACY_ORACLE_CONTAINER
$prevCli    = $env:LEGACY_CONTAINER_CLI
$prevMode   = $env:LEGACY_PARITY_MODE
$prevPkgSha = $env:LEGACY_ORACLE_PACKAGE_SHA256

try {
    $env:LEGACY_ORACLE_HOST           = "127.0.0.1"
    $env:LEGACY_ORACLE_PORT           = "$BishopPort"
    $env:LEGACY_ORACLE_PROFILE        = $Profile
    $env:LEGACY_ORACLE_CONTAINER      = $ContainerName
    $env:LEGACY_CONTAINER_CLI         = $Cli
    $env:LEGACY_PARITY_MODE           = $Mode.ToLowerInvariant()
    $env:LEGACY_ORACLE_PACKAGE_SHA256 = $PackageSha256

    $dotnetArgs = @("test", $testProj, "--filter", $resolvedFilter, "--logger", "console;verbosity=normal")
    if ($NoBuild) {
        $dotnetArgs += "--no-build"
    }

    Write-Host "[PARITY] Mode: $Mode | Executing: dotnet $($dotnetArgs -join ' ')" -ForegroundColor Cyan
    & dotnet @dotnetArgs
    $testExit = $LASTEXITCODE
    if ($testExit -ne 0) {
        throw "Differential Parity test run failed with exit code $testExit."
    }
} finally {
    $env:LEGACY_ORACLE_HOST           = $prevHost
    $env:LEGACY_ORACLE_PORT           = $prevPort
    $env:LEGACY_ORACLE_PROFILE        = $prevProf
    $env:LEGACY_ORACLE_CONTAINER      = $prevCont
    $env:LEGACY_CONTAINER_CLI         = $prevCli
    $env:LEGACY_PARITY_MODE           = $prevMode
    $env:LEGACY_ORACLE_PACKAGE_SHA256 = $prevPkgSha

    if ($StopAfter) {
        Write-Host "[PARITY] Stopping and removing '$ContainerName'..." -ForegroundColor DarkGray
        if ($Cli -eq "wslc") {
            $null = Invoke-CliCapture @("remove", "-f", $ContainerName)
        } else {
            $null = Invoke-CliCapture @("rm", "-f", $ContainerName)
        }
    }
}
