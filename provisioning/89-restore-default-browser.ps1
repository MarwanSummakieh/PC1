<#
.SYNOPSIS
    Undo of 21-set-default-browser.ps1. Removes the MarwanOS browser registration and
    the enforced default-association policy.

.DESCRIPTION
    Reverses, in the order that leaves the machine usable at every intermediate step:
      1. The DefaultAssociationsConfiguration policy -- restored to whatever it was
         before (recorded at HKLM\SOFTWARE\MarwanOS\Browser\PreviousPolicy), or removed
         if there was none. Doing this FIRST means that if the run stops halfway, the
         machine is merely un-enforced rather than enforced onto a ProgId that no longer
         exists.
      2. The registration keys: RegisteredApplications, StartMenuInternet\MarwanOSBrowser,
         Classes\MarwanOSHTML.
      3. HKLM\SOFTWARE\MarwanOS\Browser, including the recorded fallback command.
      4. The association XML file (kept with -KeepXml).

    WHAT THIS CANNOT DO, AND WHY YOU MAY STILL HAVE TO PICK A BROWSER BY HAND.
    Removing an enforced policy does not restore a previous per-user choice, because the
    policy never overwrote one -- it took precedence over it. In the normal case each
    account simply goes back to the UserChoice it already had. But if an account was
    switched to MarwanOS by hand in Settings (rather than by the policy), its UserChoice
    key now names a ProgId that step 2 deletes, and Windows will ask which application to
    use the next time a link is opened. That is a two-click fix in Settings > Apps >
    Default apps and cannot be scripted -- the UserChoice hash is exactly the thing
    Windows refuses to let a script write.

.NOTES
    Run in an ELEVATED PowerShell. Dry run by default; -Apply to write.
#>

[CmdletBinding()]
param(
    [switch] $Apply,
    [switch] $KeepXml,
    [string] $AssocXml = "C:\ProgramData\MarwanOS\default-associations.xml",
    [string] $LogPath  = "C:\MarwanOS\default-browser.log"
)

$ErrorActionPreference = "Stop"

$ProgId    = "MarwanOSHTML"
$ClientKey = "MarwanOSBrowser"

function Line($m) {
    $stamp = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    Write-Host $m
    try { Add-Content -LiteralPath $LogPath -Value ("{0}  {1}" -f $stamp, $m) -ErrorAction SilentlyContinue } catch {}
}

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$pr = New-Object Security.Principal.WindowsPrincipal($id)
if (-not $pr.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    Write-Host "ERROR: run this in an ELEVATED PowerShell (Administrator). Nothing was changed." -ForegroundColor Red
    exit 2
}

try { New-Item -ItemType Directory -Force (Split-Path $LogPath) -ErrorAction SilentlyContinue | Out-Null } catch {}
Line "=== 89-restore-default-browser.ps1  (Apply=$Apply) ==="

$previousPolicy = $null
try {
    $previousPolicy = (Get-ItemProperty -Path "HKLM:\SOFTWARE\MarwanOS\Browser" -Name PreviousPolicy -ErrorAction Stop).PreviousPolicy
} catch {}
Line ("policy to restore: " + $(if ([string]::IsNullOrEmpty($previousPolicy)) { "(none -- the value will be removed)" } else { $previousPolicy }))

$targets = @(
    "HKLM:\SOFTWARE\Clients\StartMenuInternet\$ClientKey",
    "HKLM:\SOFTWARE\Classes\$ProgId",
    "HKLM:\SOFTWARE\MarwanOS\Browser"
)
foreach ($t in $targets) { Line ("present: {0}  {1}" -f $(if (Test-Path $t) { "YES" } else { "no " }), $t) }

if (-not $Apply) {
    Write-Host ""
    Write-Host "DRY RUN. Re-run with -Apply. Nothing was changed." -ForegroundColor Cyan
    exit 0
}

# 1. Policy first.
try {
    if ([string]::IsNullOrEmpty($previousPolicy)) {
        Remove-ItemProperty -Path "HKLM:\SOFTWARE\Policies\Microsoft\Windows\System" `
            -Name DefaultAssociationsConfiguration -ErrorAction SilentlyContinue
        Line "OK  removed the DefaultAssociationsConfiguration policy value"
    } else {
        New-ItemProperty -Path "HKLM:\SOFTWARE\Policies\Microsoft\Windows\System" `
            -Name DefaultAssociationsConfiguration -Value $previousPolicy -PropertyType String -Force | Out-Null
        Line "OK  restored DefaultAssociationsConfiguration = $previousPolicy"
    }
} catch { Line "FAIL policy  --  $($_.Exception.Message)" }

# 2. Registration.
try {
    Remove-ItemProperty -Path "HKLM:\SOFTWARE\RegisteredApplications" -Name $ClientKey -ErrorAction SilentlyContinue
    Line "OK  removed RegisteredApplications\$ClientKey"
} catch { Line "FAIL RegisteredApplications  --  $($_.Exception.Message)" }

foreach ($t in $targets) {
    try {
        if (Test-Path $t) { Remove-Item -Path $t -Recurse -Force; Line "OK  removed $t" }
        else              { Line "--  not present: $t" }
    } catch { Line "FAIL removing $t  --  $($_.Exception.Message)" }
}

# 4. The file.
if (-not $KeepXml) {
    try {
        if (Test-Path -LiteralPath $AssocXml) { Remove-Item -LiteralPath $AssocXml -Force; Line "OK  removed $AssocXml" }
    } catch { Line "FAIL removing $AssocXml  --  $($_.Exception.Message)" }
}

Line "=== done ==="
Write-Host ""
Write-Host "MarwanOS is no longer registered as a browser." -ForegroundColor Green
Write-Host "Each account returns to its own default at the next sign-in. If an account is asked"
Write-Host "to choose a browser, pick one in Settings > Apps > Default apps -- see the note in"
Write-Host "this script's header for why that step cannot be scripted."
exit 0
