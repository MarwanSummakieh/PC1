<#
.SYNOPSIS
    Elevated install worker for the ARC install broker. Drains the request queue,
    installing packages named in packages.json. Exits when the queue is empty.

.DESCRIPTION
    This script is the PRIVILEGED half of the broker. It is launched only by the
    scheduled task \ARC\arc-install-broker, which runs as SYSTEM. It is never
    launched directly by the shell.

    The security property this script must preserve:

        A standard user (arcshell) can cause this script to install a package
        named in packages.json, and CANNOT cause it to do anything else.

    That is why a request file carries an id and nothing else. It never carries a
    URL, a path, an argument, or a hash. Every one of those comes from
    packages.json, which lives in a directory standard users cannot write to.

    Provenance is pinned by AUTHENTICODE PUBLISHER, not by content hash. A hash
    pin breaks the moment a vendor ships a new build, which in practice means it
    gets disabled and the pin becomes decoration. A publisher pin survives version
    bumps and still proves the bytes came from who the manifest says. A manifest
    entry may additionally pin sha256 where the installer genuinely never changes;
    both checks are enforced when both are present.

    NO WINGET DEPENDENCY. This image (Windows 11 IoT Enterprise LTSC Evaluation)
    ships neither winget nor the Microsoft Store. Direct download is the primary
    path; winget is used only when the manifest names a wingetId AND winget
    actually resolves, so the same manifest works on machines that have it.

.PARAMETER Root
    Broker state directory. Standard users must not be able to write to it, except
    the queue\ subdirectory. Created and ACL'd by provisioning\04-install-broker.ps1.

.PARAMETER WhatIfOnly
    Do everything except run the installer: still downloads and still verifies, so
    it genuinely exercises the manifest, the network path and the signature check.

.NOTES
    RUN AS: SYSTEM, via the scheduled task. Refuses to run unelevated.
    LOG:    <Root>\logs\install-broker.log
#>
[CmdletBinding()]
param(
    [string]$Root = 'C:\ProgramData\ARC',
    [switch]$WhatIfOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$QueueDir     = Join-Path $Root 'queue'
$ProcessedDir = Join-Path $Root 'processed'
$LogDir       = Join-Path $Root 'logs'
$CacheDir     = Join-Path $Root 'cache'
$ManifestPath = Join-Path $Root 'packages.json'
$LogPath      = Join-Path $LogDir 'install-broker.log'

foreach ($d in @($QueueDir, $ProcessedDir, $LogDir, $CacheDir)) {
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
}

function Write-Log {
    param([string]$Message, [string]$Level = 'INFO')
    $line = '{0} [{1,-5}] {2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Level, $Message
    try { Add-Content -Path $LogPath -Value $line -Encoding UTF8 } catch { }
    Write-Host $line
}

# --- Guard: must be elevated -------------------------------------------------
$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$wp        = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $wp.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Log "Refusing to run: not elevated (running as $($identity.Name))." 'ERROR'
    exit 1
}

Write-Log "Worker start (identity=$($identity.Name), root=$Root)."

# --- Guard: the manifest must not be writable by non-administrators ----------
# If a standard user can edit packages.json, the manifest is not a control at
# all - they could point an entry at their own binary. Fail closed.
function Get-UntrustedWriter {
    param([string]$Path)
    $trusted = @(
        'NT AUTHORITY\SYSTEM',
        'BUILTIN\Administrators',
        'NT SERVICE\TrustedInstaller',
        'CREATOR OWNER'
    )
    # Test ATOMIC write bits only. Do not put Write/Modify/FullControl in this
    # mask: they are composite values that also contain the read bits, so
    # `ReadAndExecute -band FullControl` is non-zero and every read-only ACE
    # would look like a write ACE. Modify and FullControl are still caught here,
    # because both contain WriteData.
    $writeRights = [Security.AccessControl.FileSystemRights]::WriteData -bor              # 2
                   [Security.AccessControl.FileSystemRights]::AppendData -bor             # 4
                   [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
                   [Security.AccessControl.FileSystemRights]::Delete -bor
                   [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
                   [Security.AccessControl.FileSystemRights]::TakeOwnership
    foreach ($ace in (Get-Acl -Path $Path).Access) {
        if ($ace.AccessControlType -ne 'Allow') { continue }
        if (($ace.FileSystemRights -band $writeRights) -eq 0) { continue }
        if ($trusted -notcontains $ace.IdentityReference.Value) {
            return $ace.IdentityReference.Value
        }
    }
    return $null
}

if (-not (Test-Path $ManifestPath)) {
    Write-Log "Manifest not found at $ManifestPath. Nothing can be installed." 'ERROR'
    exit 2
}

$offender = Get-UntrustedWriter -Path $ManifestPath
if ($offender) {
    Write-Log "REFUSING TO RUN: '$offender' can write $ManifestPath. The manifest is not trustworthy. Re-run provisioning\04-install-broker.ps1 to repair ACLs." 'ERROR'
    exit 3
}

# --- Load and validate the manifest -----------------------------------------
try {
    $manifest = Get-Content -Path $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
}
catch {
    Write-Log "Manifest is not valid JSON: $($_.Exception.Message)" 'ERROR'
    exit 2
}

$Packages = @{}
foreach ($p in @($manifest.packages)) {
    if (-not $p.id) { Write-Log "Manifest entry with no id; skipped." 'WARN'; continue }

    $hasUrl    = [bool]($p.PSObject.Properties.Name -contains 'url' -and $p.url)
    $hasWinget = [bool]($p.PSObject.Properties.Name -contains 'wingetId' -and $p.wingetId)

    if (-not $hasUrl -and -not $hasWinget) {
        Write-Log "Manifest entry '$($p.id)' has neither url nor wingetId; skipped." 'WARN'
        continue
    }
    # An https url without a publisher pin is an unverified download. Refuse the
    # entry rather than quietly install whatever answers that hostname today.
    if ($hasUrl) {
        if ($p.url -notmatch '^https://') {
            Write-Log "Manifest entry '$($p.id)' has a non-https url; skipped." 'WARN'
            continue
        }
        if (-not ($p.PSObject.Properties.Name -contains 'publisher' -and $p.publisher)) {
            Write-Log "Manifest entry '$($p.id)' has a url but no publisher pin; skipped." 'WARN'
            continue
        }
    }
    $Packages[$p.id.ToLowerInvariant()] = $p
}
Write-Log "Manifest loaded: $($Packages.Count) usable package(s) [$(($Packages.Values | ForEach-Object { $_.id }) -join ', ')]."

if ($Packages.Count -eq 0) {
    Write-Log "No usable manifest entries. Nothing can be installed." 'ERROR'
    exit 2
}

# --- Optional winget backend -------------------------------------------------
function Resolve-Winget {
    $pkg = Get-ChildItem -Path 'C:\Program Files\WindowsApps' `
                         -Filter 'Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe' `
                         -Directory -ErrorAction SilentlyContinue |
           Sort-Object Name -Descending | Select-Object -First 1
    if ($pkg) {
        $exe = Join-Path $pkg.FullName 'winget.exe'
        if (Test-Path $exe) { return $exe }
    }
    $cmd = Get-Command winget.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}
$Winget = Resolve-Winget
Write-Log ("winget backend : {0}" -f $(if ($Winget) { $Winget } else { 'not present (direct download only)' }))

# TLS 1.2+ - older defaults still bite on LTSC images.
try {
    [Net.ServicePointManager]::SecurityProtocol =
        [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls11
}
catch { Write-Log "Could not raise TLS version: $($_.Exception.Message)" 'WARN' }

# --- Helpers -----------------------------------------------------------------
function Write-Result {
    param(
        [string]$Ticket,
        [string]$Status,           # OK | REJECTED | FAILED
        [int]$ExitCode = -1,
        [string]$PackageId = '',
        [string]$Detail = ''
    )
    $body = @(
        "ticket=$Ticket"
        "status=$Status"
        "exitcode=$ExitCode"
        "package=$PackageId"
        "detail=$($Detail -replace '[\r\n]+', ' ')"
        "completed=$(Get-Date -Format 'o')"
    ) -join "`r`n"
    Set-Content -Path (Join-Path $ProcessedDir "$Ticket.result") -Value $body -Encoding UTF8
}

# Returns $null on success, or a string describing why the file is not acceptable.
function Test-Provenance {
    param([string]$File, $Entry)

    $sig = Get-AuthenticodeSignature -FilePath $File
    if ($sig.Status -ne 'Valid') {
        return "Authenticode status is '$($sig.Status)', expected Valid"
    }
    if (-not $sig.SignerCertificate) {
        return "signature carries no signer certificate"
    }
    $subject = $sig.SignerCertificate.Subject
    if ($subject -notlike "*$($Entry.publisher)*") {
        return "signer '$subject' does not match pinned publisher '$($Entry.publisher)'"
    }

    if ($Entry.PSObject.Properties.Name -contains 'sha256' -and $Entry.sha256) {
        $actual = (Get-FileHash -Path $File -Algorithm SHA256).Hash
        if ($actual -ne $Entry.sha256.ToUpperInvariant()) {
            return "sha256 mismatch: got $actual, manifest pins $($Entry.sha256)"
        }
    }
    return $null
}

function Install-FromUrl {
    param($Entry, [string]$Ticket)

    $leaf = [IO.Path]::GetFileName(([Uri]$Entry.url).AbsolutePath)
    if (-not $leaf) { $leaf = "$($Entry.id).installer" }
    $file = Join-Path $CacheDir ("{0}_{1}" -f $Entry.id, $leaf)

    Write-Log "Ticket ${Ticket}: downloading $($Entry.url)"
    if (Test-Path $file) { Remove-Item -Path $file -Force -ErrorAction SilentlyContinue }
    Invoke-WebRequest -Uri $Entry.url -OutFile $file -UseBasicParsing -TimeoutSec 600

    $size = (Get-Item $file).Length
    Write-Log "Ticket ${Ticket}: downloaded $([math]::Round($size/1MB,1)) MB -> $file"

    $bad = Test-Provenance -File $file -Entry $Entry
    if ($bad) {
        Remove-Item -Path $file -Force -ErrorAction SilentlyContinue
        throw "provenance check failed: $bad"
    }
    Write-Log "Ticket ${Ticket}: provenance OK (publisher pin '$($Entry.publisher)')"

    if ($WhatIfOnly) {
        Write-Log "Ticket ${Ticket}: -WhatIfOnly, not executing the installer."
        return 0
    }

    $type = if ($Entry.PSObject.Properties.Name -contains 'type' -and $Entry.type) { $Entry.type } else { 'exe' }
    $argv = @()
    if ($Entry.PSObject.Properties.Name -contains 'args' -and $Entry.args) { $argv = @($Entry.args) }

    if ($type -eq 'msi') {
        $msiArgs = @('/i', "`"$file`"", '/qn', '/norestart') + $argv
        Write-Log "Ticket ${Ticket}: msiexec $($msiArgs -join ' ')"
        $proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList $msiArgs -Wait -PassThru
    }
    else {
        Write-Log "Ticket ${Ticket}: $file $($argv -join ' ')"
        if ($argv.Count -gt 0) {
            $proc = Start-Process -FilePath $file -ArgumentList $argv -Wait -PassThru
        }
        else {
            $proc = Start-Process -FilePath $file -Wait -PassThru
        }
    }
    return $proc.ExitCode
}

function Install-FromWinget {
    param($Entry, [string]$Ticket)

    $wargs = @(
        'install', '--id', $Entry.wingetId, '--exact', '--silent',
        '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity'
    )
    Write-Log "Ticket ${Ticket}: winget $($wargs -join ' ')"
    if ($WhatIfOnly) {
        Write-Log "Ticket ${Ticket}: -WhatIfOnly, not invoking winget."
        return 0
    }
    $out = & $Winget @wargs 2>&1
    foreach ($l in $out) { Write-Log "  | $l" }
    return $LASTEXITCODE
}

# --- Drain the queue ---------------------------------------------------------
$IdPattern = '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$'

$requests = @(Get-ChildItem -Path $QueueDir -Filter '*.req' -File -ErrorAction SilentlyContinue |
              Sort-Object CreationTimeUtc)

if ($requests.Count -eq 0) {
    Write-Log "Queue empty. Nothing to do."
    exit 0
}
Write-Log "Queue depth: $($requests.Count)."

foreach ($req in $requests) {
    $ticket = [IO.Path]::GetFileNameWithoutExtension($req.Name)

    # The ticket name is attacker-controlled; make sure it cannot escape the
    # processed directory when the .result path is built from it.
    if ($ticket -notmatch '^[A-Za-z0-9-]{1,64}$') {
        Write-Log "Rejecting request with malformed ticket name '$($req.Name)'." 'WARN'
        Remove-Item -Path $req.FullName -Force -ErrorAction SilentlyContinue
        continue
    }

    $requested = ''
    try {
        $first = @(Get-Content -Path $req.FullName -TotalCount 1 -Encoding UTF8)
        if ($first.Count -gt 0) { $requested = $first[0].Trim() }
    }
    catch {
        Write-Log "Ticket ${ticket}: unreadable request file: $($_.Exception.Message)" 'WARN'
    }
    Remove-Item -Path $req.FullName -Force -ErrorAction SilentlyContinue

    if ($requested -notmatch $IdPattern) {
        Write-Log "Ticket ${ticket}: REJECTED - malformed id." 'WARN'
        Write-Result -Ticket $ticket -Status 'REJECTED' -Detail 'malformed id'
        continue
    }

    $key = $requested.ToLowerInvariant()
    if (-not $Packages.ContainsKey($key)) {
        Write-Log "Ticket ${ticket}: REJECTED - '$requested' is not in the manifest." 'WARN'
        Write-Result -Ticket $ticket -Status 'REJECTED' -PackageId $requested -Detail 'not in manifest'
        continue
    }

    # From here on use the MANIFEST's entry, never the requester's string.
    $entry = $Packages[$key]
    Write-Log "Ticket ${ticket}: installing '$($entry.name)' ($($entry.id))."

    $code   = -1
    $failed = $null
    try {
        $useWinget = $Winget -and ($entry.PSObject.Properties.Name -contains 'wingetId') -and $entry.wingetId
        if ($useWinget) { $code = Install-FromWinget -Entry $entry -Ticket $ticket }
        else            { $code = Install-FromUrl    -Entry $entry -Ticket $ticket }
    }
    catch {
        $failed = $_.Exception.Message
        Write-Log "Ticket ${ticket}: $failed" 'ERROR'
    }

    if ($failed) {
        Write-Result -Ticket $ticket -Status 'FAILED' -ExitCode $code -PackageId $entry.id -Detail $failed
        continue
    }

    # An installer's exit code is a claim, not evidence. When the manifest names
    # a verifyPath, that file existing is what decides the outcome.
    if ($entry.PSObject.Properties.Name -contains 'verifyPath' -and $entry.verifyPath) {
        if (Test-Path $entry.verifyPath) {
            Write-Log "Ticket ${ticket}: verified $($entry.verifyPath) exists."
            Write-Result -Ticket $ticket -Status 'OK' -ExitCode $code -PackageId $entry.id -Detail "verified $($entry.verifyPath)"
            continue
        }
        if ($WhatIfOnly) {
            Write-Result -Ticket $ticket -Status 'OK' -ExitCode 0 -PackageId $entry.id -Detail 'whatif: downloaded and verified, not installed'
            continue
        }
        Write-Log "Ticket ${ticket}: FAILED - installer exit $code but $($entry.verifyPath) is missing." 'ERROR'
        Write-Result -Ticket $ticket -Status 'FAILED' -ExitCode $code -PackageId $entry.id -Detail "verifyPath missing after install (exit $code)"
        continue
    }

    # winget: -1978335189 = already installed. MSI/exe: 3010 = success, reboot required.
    if ($code -eq 0 -or $code -eq 3010 -or $code -eq -1978335189) {
        $detail = switch ($code) {
            3010          { 'installed; reboot required' }
            -1978335189   { 'already installed' }
            default       { '' }
        }
        Write-Log "Ticket ${ticket}: OK (exit $code) $detail"
        Write-Result -Ticket $ticket -Status 'OK' -ExitCode $code -PackageId $entry.id -Detail $detail
    }
    else {
        Write-Log "Ticket ${ticket}: FAILED - installer exit $code." 'ERROR'
        Write-Result -Ticket $ticket -Status 'FAILED' -ExitCode $code -PackageId $entry.id -Detail "installer exit $code"
    }
}

# --- Retention ---------------------------------------------------------------
$cut = (Get-Date).ToUniversalTime().AddDays(-7)
foreach ($dir in @($ProcessedDir, $CacheDir)) {
    $stale = @(Get-ChildItem -Path $dir -File -ErrorAction SilentlyContinue |
               Where-Object { $_.CreationTimeUtc -lt $cut })
    if ($stale.Count -gt 0) {
        Write-Log "Pruning $($stale.Count) file(s) older than 7 days from $dir."
        $stale | Remove-Item -Force -ErrorAction SilentlyContinue
    }
}

Write-Log "Worker done."
exit 0
