<#
.SYNOPSIS
    Starts the local operator Hub and opens it in the browser.

.DESCRIPTION
    Publishes the Hub into .artifacts on first use (UI bundle + hub-api, and the hub CLI),
    starts hub-api from the repository root so it finds hub.config.yaml there, and opens
    http://127.0.0.1:5177. Running it again while the Hub is up only opens the browser.

    The Hub runs in its own minimized window; closing that window, or -Stop, stops it. A run
    in flight when the Hub stops is marked Interrupted by the reconciler on the next start.

.PARAMETER Rebuild
    Republish before starting - after the Hub's own code changed.

.PARAMETER Stop
    Stop the running Hub.
#>
[CmdletBinding()]
param(
    [switch] $Rebuild,
    [switch] $Stop
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$hubOut = Join-Path $repo '.artifacts\hub'
$cliOut = Join-Path $repo '.artifacts\cli'
$exe = Join-Path $hubOut 'hub-api.exe'
$url = 'http://127.0.0.1:5177'

function Test-HubListening {
    $client = New-Object System.Net.Sockets.TcpClient
    try { return $client.ConnectAsync('127.0.0.1', 5177).Wait(500) }
    catch { return $false }
    finally { $client.Dispose() }
}

function Get-HubProcess {
    Get-Process -Name 'hub-api' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and ([IO.Path]::GetFullPath($_.Path) -eq [IO.Path]::GetFullPath($exe)) }
}

function Invoke-Step([string] $what, [scriptblock] $step) {
    Write-Host "  $what"
    & $step
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)." }
}

if ($Stop) {
    $running = @(Get-HubProcess)
    if ($running.Count -eq 0) { Write-Host 'The Hub is not running.'; return }
    $running | Stop-Process
    Write-Host 'The Hub stopped.'
    return
}

$up = Test-HubListening

if ($Rebuild -and $up) {
    throw 'The Hub is running; stop it first (corexhub -Stop), then rebuild.'
}

if ($Rebuild -or -not (Test-Path $exe) -or -not (Test-Path (Join-Path $cliOut 'hub.exe'))) {
    Write-Host 'Publishing the Hub...'
    Push-Location (Join-Path $repo 'src\CoreX.Hub.Web')
    try {
        Invoke-Step 'UI: npm ci' { npm ci --no-fund --no-audit | Out-Null }
        Invoke-Step 'UI: npm run build' { npm run build | Out-Null }
    }
    finally { Pop-Location }
    Invoke-Step 'hub-api: dotnet publish' {
        dotnet publish (Join-Path $repo 'src\CoreX.Hub.Api') -c Release -o $hubOut --nologo -v q | Out-Null
    }
    Invoke-Step 'hub CLI: dotnet publish' {
        dotnet publish (Join-Path $repo 'src\CoreX.Hub.Cli') -c Release -o $cliOut --nologo -v q | Out-Null
    }
}

if (-not $up) {
    Write-Host 'Starting the Hub...'
    # The working directory is the point: hub-api looks for hub.config.yaml beside the data
    # directory first and then upward from here.
    Start-Process -FilePath $exe -WorkingDirectory $repo -WindowStyle Minimized | Out-Null

    $deadline = (Get-Date).AddSeconds(30)
    while (-not (Test-HubListening)) {
        if ((Get-Date) -gt $deadline) { throw "The Hub did not start listening on $url within 30 seconds." }
        Start-Sleep -Milliseconds 300
    }
}

Write-Host "The Hub is up: $url"
Start-Process $url
