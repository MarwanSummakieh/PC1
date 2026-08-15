<#
.SYNOPSIS
    Unprivileged client for the ARC install broker. Queues a package install and
    waits for the result. Produces no UAC prompt.

.DESCRIPTION
    This is the half that ShellHost.exe calls. It runs as whatever account the
    shell runs as (arcshell), holds no elevated token, and can do nothing on its
    own: it writes a request file and asks the scheduled task to run.

    All authority lives in the scheduled task and the manifest. If the package is
    not in the manifest, this returns REJECTED and nothing happens.

.PARAMETER PackageId
    Manifest id, e.g. steam. Must be present in C:\ProgramData\ARC\packages.json
    or the request is rejected. This is a manifest slug, not a winget id.

.PARAMETER TimeoutSeconds
    How long to wait for the worker to report a result. Default 900 (15 min);
    large game clients over a slow link can take longer than you expect.

.PARAMETER Root
    Broker state directory. Must match 04-install-broker.ps1.

.OUTPUTS
    Writes human-readable progress to the host. Exit code:
        0   installed (or already installed)
        2   rejected (not allowlisted / malformed)
        3   winget failed - see detail
        4   broker not installed or not reachable
        5   timed out waiting for a result

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File arc-install.ps1 -PackageId steam
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageId,

    [int]$TimeoutSeconds = 900,

    [string]$Root = 'C:\ProgramData\ARC'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$TaskPath     = '\ARC\arc-install-broker'
$QueueDir     = Join-Path $Root 'queue'
$ProcessedDir = Join-Path $Root 'processed'

if (-not (Test-Path $QueueDir)) {
    Write-Host "Install broker is not installed (no $QueueDir)." -ForegroundColor Red
    Write-Host "Run provisioning\04-install-broker.ps1 from an elevated prompt first."
    exit 4
}

# Ticket name is constrained to what the worker will accept: [A-Za-z0-9-]{1,64}
$ticket   = [guid]::NewGuid().ToString()
$reqPath  = Join-Path $QueueDir "$ticket.req"
$resPath  = Join-Path $ProcessedDir "$ticket.result"

Write-Host "Requesting install: $PackageId" -ForegroundColor Cyan
Write-Host "  ticket: $ticket" -ForegroundColor DarkGray

# Write the request. The file contains the package id and nothing else - the
# worker deliberately ignores everything after the first line.
Set-Content -Path $reqPath -Value $PackageId -Encoding UTF8

# Poke the broker. The task's MultipleInstances policy is Queue, so a run that
# arrives while another is in flight is queued rather than dropped.
$run = & schtasks.exe /run /tn $TaskPath 2>&1
$runCode = $LASTEXITCODE
if ($runCode -ne 0) {
    # "already running" is benign: the in-flight worker drains the whole queue,
    # and the queued run will pick up anything it missed.
    $text = ($run | Out-String).Trim()
    if ($text -notmatch '(?i)already running') {
        Write-Host "Could not start the broker task ($runCode): $text" -ForegroundColor Red
        Remove-Item -Path $reqPath -Force -ErrorAction SilentlyContinue
        exit 4
    }
    Write-Host "  broker already running; request queued." -ForegroundColor DarkGray
}

# Wait for the result file.
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$spin     = 0
while (-not (Test-Path $resPath)) {
    if ((Get-Date) -gt $deadline) {
        Write-Host ""
        Write-Host "Timed out after $TimeoutSeconds s waiting for the broker." -ForegroundColor Red
        Write-Host "The install may still be running. Check $Root\logs\install-broker.log"
        exit 5
    }
    Start-Sleep -Seconds 2
    $spin++
    if ($spin % 15 -eq 0) {
        Write-Host ("  still working... {0}s" -f ($spin * 2)) -ForegroundColor DarkGray
    }
}

# Parse key=value result.
$result = @{}
foreach ($line in (Get-Content -Path $resPath -Encoding UTF8)) {
    $kv = $line -split '=', 2
    if ($kv.Count -eq 2) { $result[$kv[0]] = $kv[1] }
}

$status   = if ($result.ContainsKey('status'))   { $result['status'] }   else { 'UNKNOWN' }
$detail   = if ($result.ContainsKey('detail'))   { $result['detail'] }   else { '' }
$exitCode = if ($result.ContainsKey('exitcode')) { $result['exitcode'] } else { '?' }

switch ($status) {
    'OK' {
        $msg = "$PackageId installed."
        if ($detail) { $msg = "$PackageId - $detail." }
        Write-Host $msg -ForegroundColor Green
        exit 0
    }
    'REJECTED' {
        Write-Host "Rejected: $detail" -ForegroundColor Yellow
        Write-Host "Add an entry for '$PackageId' to $Root\packages.json (elevated) to permit it."
        exit 2
    }
    'FAILED' {
        Write-Host "Install failed (installer exit $exitCode): $detail" -ForegroundColor Red
        Write-Host "See $Root\logs\install-broker.log for the winget output."
        exit 3
    }
    default {
        Write-Host "Broker returned an unrecognised status '$status'." -ForegroundColor Red
        exit 3
    }
}
