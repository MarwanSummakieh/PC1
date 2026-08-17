<#
.SYNOPSIS
    Unprivileged client for the MarwanOS broker. Queues one allowlisted privileged
    action, tails its progress, and reports the result. Produces no UAC prompt.

.DESCRIPTION
    This is the half the shell (or a human at a standard-user prompt) calls. It runs
    as whatever account the shell runs as (marwanshell), holds no elevated token, and
    can do nothing on its own: it writes a request file and asks the scheduled task
    to run.

    All authority lives in the scheduled task, the verb allowlist in the worker, and
    packages.json. If the verb is not allowlisted, or its argument does not match the
    grammar, the worker returns REJECTED and nothing happens.

    Verbs:
        install          -PackageId <manifest slug>     (default)
        updates.install  (no arguments)
        wifi.forget      -Ssid "<network name>"
        bt.forget        -Address AABBCCDDEEFF

    The request file it writes is v2 format - key=value lines, `verb=` first:

        verb=install
        package=steam

.PARAMETER Verb
    One of install | updates.install | wifi.forget | bt.forget. Default: install.

.PARAMETER PackageId
    Manifest id for -Verb install, e.g. steam. Must be present in
    <Root>\packages.json or the request is rejected. A manifest slug, not a winget id.

.PARAMETER Ssid
    Network name for -Verb wifi.forget. 1-32 characters, no control characters, no
    double quote.

.PARAMETER Address
    Bluetooth device address for -Verb bt.forget: exactly 12 hex digits, no
    separators (AABBCCDDEEFF).

.PARAMETER TimeoutSeconds
    How long to wait for the worker to report a result. Default 900 (15 min), or
    3600 for updates.install - Windows Update is slow and a large game client over a
    slow link takes longer than you expect.

.PARAMETER Root
    Broker state directory. When omitted, the known roots are probed in order
    (C:\ProgramData\MarwanOS, then C:\ProgramData\ARC on the old-named bench) and the
    first with a queue\ directory wins.

.PARAMETER TaskPath
    Scheduled task to poke. Defaults to the task that matches the chosen root.

.OUTPUTS
    Progress lines from the worker as they are written. Exit code:
        0   done (OK)
        2   rejected (verb not allowlisted / argument malformed / not in manifest)
        3   failed - see detail
        4   broker not installed or not reachable
        5   timed out waiting for a result

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File marwan-install.ps1 -PackageId steam

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File marwan-install.ps1 -Verb updates.install

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File marwan-install.ps1 -Verb wifi.forget -Ssid "Living Room"
#>
[CmdletBinding()]
param(
    [ValidateSet('install', 'updates.install', 'wifi.forget', 'bt.forget')]
    [string]$Verb = 'install',

    [string]$PackageId,
    [string]$Ssid,
    [string]$Address,

    [int]$TimeoutSeconds = 0,

    [string]$Root,
    [string]$TaskPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Known roots, newest naming first. The bench (DESKTOP-6BCSJ3P) is still on the
# old ARC names and must not be renamed.
$KnownRoots = @(
    [pscustomobject]@{ Root = 'C:\ProgramData\MarwanOS'; Task = '\MarwanOS\marwan-install-broker' },
    [pscustomobject]@{ Root = 'C:\ProgramData\ARC';      Task = '\ARC\arc-install-broker' }
)

if (-not $Root) {
    $hit = $KnownRoots | Where-Object { Test-Path (Join-Path $_.Root 'queue') } | Select-Object -First 1
    if (-not $hit) {
        Write-Host "Broker is not installed (no queue\ under $(($KnownRoots.Root) -join ' or '))." -ForegroundColor Red
        Write-Host "Run provisioning\04-install-broker.ps1 from an elevated prompt first."
        exit 4
    }
    $Root = $hit.Root
    if (-not $TaskPath) { $TaskPath = $hit.Task }
}
if (-not $TaskPath) {
    $match = $KnownRoots | Where-Object { $_.Root -eq $Root } | Select-Object -First 1
    $TaskPath = if ($match) { $match.Task } else { '\MarwanOS\marwan-install-broker' }
}

$QueueDir     = Join-Path $Root 'queue'
$ProcessedDir = Join-Path $Root 'processed'

if (-not (Test-Path $QueueDir)) {
    Write-Host "Broker is not installed (no $QueueDir)." -ForegroundColor Red
    Write-Host "Run provisioning\04-install-broker.ps1 from an elevated prompt first."
    exit 4
}

# --- Validate exactly what the worker validates ------------------------------
# Failing here saves a round trip; it is NOT the control. The worker re-validates
# everything, because it is the only side that is trusted.
$body = @("verb=$Verb")
$label = $Verb
switch ($Verb) {
    'install' {
        if ($PackageId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') {
            Write-Host "-PackageId '$PackageId' is not a valid manifest id." -ForegroundColor Yellow
            exit 2
        }
        $body += "package=$PackageId"
        $label = "install $PackageId"
    }
    'updates.install' {
        $label = 'install pending Windows updates'
    }
    'wifi.forget' {
        if ($Ssid -notmatch '^[^\x00-\x1F\x7F"]{1,32}$') {
            Write-Host "-Ssid must be 1-32 characters, no control characters and no double quote." -ForegroundColor Yellow
            exit 2
        }
        $body += "ssid=$Ssid"
        $label = "forget network '$Ssid'"
    }
    'bt.forget' {
        if ($Address -notmatch '^[0-9A-Fa-f]{12}$') {
            Write-Host "-Address must be exactly 12 hex digits with no separators, e.g. AABBCCDDEEFF." -ForegroundColor Yellow
            exit 2
        }
        $body += "address=$Address"
        $label = "unpair Bluetooth device $Address"
    }
}

if ($TimeoutSeconds -le 0) {
    $TimeoutSeconds = if ($Verb -eq 'updates.install') { 3600 } else { 900 }
}

# Ticket name is constrained to what the worker will accept: [A-Za-z0-9-]{1,64}
$ticket   = [guid]::NewGuid().ToString()
$reqPath  = Join-Path $QueueDir "$ticket.req"
$resPath  = Join-Path $ProcessedDir "$ticket.result"
$progPath = Join-Path $ProcessedDir "$ticket.progress"

Write-Host "Requesting: $label" -ForegroundColor Cyan
Write-Host "  root  : $Root" -ForegroundColor DarkGray
Write-Host "  task  : $TaskPath" -ForegroundColor DarkGray
Write-Host "  ticket: $ticket" -ForegroundColor DarkGray

# The request carries a verb and a validated identifier. It never carries a URL,
# a path, an argument list or a command - all of those come from the worker and
# from packages.json, which standard users cannot write.
Set-Content -Path $reqPath -Value ($body -join "`r`n") -Encoding UTF8

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

# Wait for the result file, echoing the worker's progress file as it grows.
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$shown    = 0
$spin     = 0

# Read a file the worker is still appending to. FileShare.ReadWrite matters:
# a reader that does not share write access makes the worker's own append throw,
# which would drop progress lines on the floor. The host must do the same.
function Read-Shared {
    param([string]$Path)
    try {
        $fs = New-Object IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
            $sr   = New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8)
            $text = $sr.ReadToEnd()
            $sr.Dispose()
        }
        finally { $fs.Dispose() }
        return @(($text -split "`r?`n") | Where-Object { $_ -ne '' })
    }
    catch { return @() }
}

function Show-NewProgress {
    if (-not (Test-Path $progPath)) { return }
    $lines = @(Read-Shared -Path $progPath)
    while ($script:shown -lt $lines.Count) {
        Write-Host ("  {0}" -f $lines[$script:shown]) -ForegroundColor DarkGray
        $script:shown++
    }
}

while (-not (Test-Path $resPath)) {
    if ((Get-Date) -gt $deadline) {
        Show-NewProgress
        Write-Host ""
        Write-Host "Timed out after $TimeoutSeconds s waiting for the broker." -ForegroundColor Red
        Write-Host "The action may still be running. Check $Root\logs\install-broker.log"
        exit 5
    }
    Show-NewProgress
    Start-Sleep -Milliseconds 500
    $spin++
    if ($spin % 60 -eq 0 -and $shown -eq 0) {
        Write-Host ("  still queued... {0}s" -f ($spin / 2)) -ForegroundColor DarkGray
    }
}
Show-NewProgress

# Parse key=value result.
$result = @{}
foreach ($line in @(Get-Content -Path $resPath -Encoding UTF8)) {
    $kv = ([string]$line) -split '=', 2
    if ($kv.Count -eq 2) { $result[$kv[0]] = $kv[1] }
}
function Field {
    param([string]$k, [string]$d = '')
    if ($result.ContainsKey($k)) { $result[$k] } else { $d }
}

$status   = Field 'status' 'UNKNOWN'
$detail   = Field 'detail'
$exitCode = Field 'exitcode' '?'
$rverb    = Field 'verb'

Write-Host ""
Write-Host "  verb    : $rverb"
Write-Host "  status  : $status"
Write-Host "  exitcode: $exitCode"
if ($result.ContainsKey('package') -and $result['package']) { Write-Host "  package : $($result['package'])" }
if ($result.ContainsKey('installed'))      { Write-Host "  installed      : $($result['installed'])" }
if ($result.ContainsKey('rebootRequired')) { Write-Host "  rebootRequired : $($result['rebootRequired'])" }
Write-Host "  detail  : $detail"
Write-Host ""

switch ($status) {
    'OK' {
        Write-Host "Done: $label." -ForegroundColor Green
        if ((Field 'rebootRequired') -eq 'true') {
            Write-Host "A restart is needed to finish." -ForegroundColor Yellow
        }
        exit 0
    }
    'REJECTED' {
        Write-Host "Rejected: $detail" -ForegroundColor Yellow
        if ($detail -eq 'not in manifest') {
            Write-Host "Add an entry for '$PackageId' to $Root\packages.json (elevated) to permit it."
        }
        elseif ($detail -eq 'unknown verb') {
            Write-Host "The broker only performs: install, updates.install, wifi.forget, bt.forget."
        }
        exit 2
    }
    'FAILED' {
        Write-Host "Failed (exit $exitCode): $detail" -ForegroundColor Red
        Write-Host "See $Root\logs\install-broker.log for the full output."
        exit 3
    }
    default {
        Write-Host "Broker returned an unrecognised status '$status'." -ForegroundColor Red
        exit 3
    }
}
