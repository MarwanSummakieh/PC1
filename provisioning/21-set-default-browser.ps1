<#
.SYNOPSIS
    Registers MarwanOS as a browser on this machine and makes it the default handler
    for http, https, .htm and .html.

.DESCRIPTION
    Step in the PC1 de-Microsofting sequence, and the half that ADDS something. The
    matching removal of Edge is 22-remove-edge-browser.ps1; the two are deliberately
    separate scripts so either can be undone without the other.

    WHAT WINDOWS NEEDS BEFORE AN APPLICATION CAN BE A BROWSER
      1. A ProgId  -- HKLM\SOFTWARE\Classes\MarwanOSHTML -- whose shell\open\command is
         what actually runs. Here that is MarwanOpenUrl.exe, NOT the shell binary. See
         the header of spike\ShellHostWeb\OpenUrl.cs: registering the shell itself would
         make every clicked link start a SECOND full shell, with a second WebView2
         environment on the same user-data folder and a second reader on the same pad.
      2. A registration under HKLM\SOFTWARE\Clients\StartMenuInternet, with a
         Capabilities subkey claiming the http/https URL associations. This is what makes
         the application appear in Windows' own "Default apps" list as a browser.
      3. An entry in HKLM\SOFTWARE\RegisteredApplications pointing at those Capabilities.

    MAKING IT THE DEFAULT IS A SEPARATE PROBLEM, AND THE HONEST ANSWER IS POLICY.
    Windows 10/11 protect the per-user choice (HKCU\...\UrlAssociations\http\UserChoice)
    with a hash over the SID, the ProgId and a timestamp. Writing that key by script is
    not supported and Windows discards a bad hash. The supported route on this SKU
    (Windows 11 IoT Enterprise LTSC) is the group policy "Set a default associations
    configuration file":
        HKLM\SOFTWARE\Policies\Microsoft\Windows\System\DefaultAssociationsConfiguration
    pointed at an XML file. It is read at EVERY USER LOGON and it ENFORCES the mapping --
    the user cannot change it back in Settings, which for a console is the point.

    THE CONSEQUENCE NOBODY SHOULD DISCOVER LATER: that policy is MACHINE-WIDE. There is
    no supported per-user form of it. So it also applies to 'brain'. To keep the primary
    account's desktop working exactly as it did, this script records the CURRENT default
    browser's open command at HKLM\SOFTWARE\MarwanOS\Browser\FallbackCommand before
    switching anything, and MarwanOpenUrl.exe uses it whenever it is started in a session
    that has no MarwanOS shell running. brain clicks a link, brain's usual browser opens;
    the console account clicks a link, the console's browser opens. If you then remove
    that fallback browser from the machine (22-remove-edge-browser.ps1 -Uninstall), links
    in brain's session queue instead of opening -- set FallbackCommand to a replacement
    browser at that point, or accept it.

    TAKES EFFECT AT THE NEXT SIGN-IN of each account, because that is when the policy is
    evaluated. Nothing about the running session changes.

.CONTEXT
    Asked for by the user 2026-08-16: "remove edge as a browser and make the one we made
    the default browser so we can open stuff in it." NOT YET APPLIED.

    HARD PREREQUISITE: a build of MarwanOpenUrl.exe must be deployed next to the shell.
    Build it with spike\ShellHostWeb\build-openurl.cmd. The receiving end -- the shell's
    WM_COPYDATA handler -- landed in the same change, so a shell binary older than that
    will not answer, and every link will fall through to the queue file.

.UNDO
    provisioning\89-restore-default-browser.ps1

.NOTES
    Run in an ELEVATED PowerShell. Exits non-zero and changes nothing if not elevated.
    Dry run by default; -Apply to write.
#>

[CmdletBinding()]
param(
    # Set to actually apply. Without it this reports what it would do and what the
    # current associations are, and writes nothing.
    [switch] $Apply,

    # The deployed shell directory on the bench. MarwanOpenUrl.exe is expected here,
    # and the shell exe here supplies the icon.
    [string] $ShellDir = "C:\ArcOS\web\v16",

    # The registered handler. Never the shell binary -- see the .DESCRIPTION.
    [string] $OpenerName = "MarwanOpenUrl.exe",

    # Where the enforced-associations XML is written. Under ProgramData because the
    # policy reads it at every logon as each user, so every account must be able to
    # read it and no standard account may write it.
    [string] $AssocXml = "C:\ProgramData\MarwanOS\default-associations.xml",

    # Register and record the fallback, but do NOT set the machine-wide policy. Use
    # this to make MarwanOS *available* as a browser and pick it by hand first.
    [switch] $NoPolicy,

    # Proceed even if MarwanOpenUrl.exe is not on disk yet (registration now, deploy
    # later). Links will do nothing until the binary exists.
    [switch] $AllowMissingOpener,

    [string] $LogPath = "C:\MarwanOS\default-browser.log"
)

$ErrorActionPreference = "Stop"

$ProgId      = "MarwanOSHTML"
$ClientKey   = "MarwanOSBrowser"
$AppName     = "MarwanOS Browser"
$AppDesc     = "The console's own browser. Links open as a tab inside the MarwanOS shell."

function Line($m) {
    $stamp = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    Write-Host $m
    try { Add-Content -LiteralPath $LogPath -Value ("{0}  {1}" -f $stamp, $m) -ErrorAction SilentlyContinue } catch {}
}

# ---- Elevation ------------------------------------------------------------
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$pr = New-Object Security.Principal.WindowsPrincipal($id)
if (-not $pr.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    Write-Host "ERROR: run this in an ELEVATED PowerShell (Administrator). Nothing was changed." -ForegroundColor Red
    exit 2
}

try { New-Item -ItemType Directory -Force (Split-Path $LogPath) -ErrorAction SilentlyContinue | Out-Null } catch {}
Line "=== 21-set-default-browser.ps1  (Apply=$Apply NoPolicy=$NoPolicy) ==="

# ---- The binary that will be registered -----------------------------------
$opener   = Join-Path $ShellDir $OpenerName
$shellExe = Get-ChildItem -LiteralPath $ShellDir -Filter "MarwanShellHostWeb*.exe" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not (Test-Path -LiteralPath $opener)) {
    if (-not $AllowMissingOpener) {
        Write-Host ""
        Write-Host "STOP: $opener is not on disk." -ForegroundColor Yellow
        Write-Host "Build it with spike\ShellHostWeb\build-openurl.cmd and deploy it next to the shell,"
        Write-Host "or re-run with -AllowMissingOpener to register the association ahead of the binary."
        Line "REFUSED: opener missing at $opener; no changes made."
        exit 3
    }
    Line "WARN opener missing at $opener (-AllowMissingOpener); links will do nothing until it is deployed."
}
$iconSource = if ($shellExe) { $shellExe.FullName } else { $opener }
Line "opener      = $opener"
Line "icon source = $iconSource"

# ---- What is the default browser right now? -------------------------------
# Read for two reasons: to print it, and to keep it as the fallback for sessions
# that never run the shell. Read from the user running the script -- an elevated
# admin session -- which is brain's own choice, which is exactly the one to preserve.
function Get-CurrentHttpCommand {
    try {
        $uc = Get-ItemProperty -Path "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell\Associations\UrlAssociations\http\UserChoice" -ErrorAction Stop
        # Not $pid -- that is an automatic read-only variable and assigning to it throws.
        $progId = $uc.ProgId
        if (-not $progId) { return $null }
        $cmd = (Get-ItemProperty -Path "Registry::HKEY_CLASSES_ROOT\$progId\shell\open\command" -ErrorAction Stop).'(default)'
        return [pscustomobject]@{ ProgId = $progId; Command = $cmd }
    } catch { return $null }
}

$current = Get-CurrentHttpCommand
if ($current) { Line "current http handler: ProgId=$($current.ProgId)  command=$($current.Command)" }
else          { Line "current http handler: could not be read (no UserChoice for this account)" }

$existingPolicy = $null
try {
    $existingPolicy = (Get-ItemProperty -Path "HKLM:\SOFTWARE\Policies\Microsoft\Windows\System" -Name DefaultAssociationsConfiguration -ErrorAction Stop).DefaultAssociationsConfiguration
    Line "existing DefaultAssociationsConfiguration policy: $existingPolicy"
} catch { Line "existing DefaultAssociationsConfiguration policy: none" }

if (-not $Apply) {
    Write-Host ""
    Write-Host "DRY RUN. Would register '$AppName' (ProgId $ProgId) with:" -ForegroundColor Cyan
    Write-Host "    HKLM\SOFTWARE\Classes\$ProgId\shell\open\command = `"$opener`" `"%1`""
    Write-Host "    HKLM\SOFTWARE\Clients\StartMenuInternet\$ClientKey\Capabilities  (http, https, .htm, .html)"
    Write-Host "    HKLM\SOFTWARE\RegisteredApplications\$ClientKey"
    if ($current) {
    Write-Host "    HKLM\SOFTWARE\MarwanOS\Browser\FallbackCommand = $($current.Command)"
    }
    if (-not $NoPolicy) {
    Write-Host "    $AssocXml  + the DefaultAssociationsConfiguration policy pointing at it"
    }
    Write-Host "Re-run with -Apply. Nothing was changed." -ForegroundColor Cyan
    Line "dry run only; no changes made."
    exit 0
}

# ---- Write ----------------------------------------------------------------
function SetReg($path, $name, $value, $kind = "String") {
    try {
        if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
        New-ItemProperty -Path $path -Name $name -Value $value -PropertyType $kind -Force | Out-Null
        Line "OK  reg $path\$name = $value"
    } catch { Line "FAIL reg $path\$name  --  $($_.Exception.Message)"; throw }
}

$openCmd = '"{0}" "%1"' -f $opener

# 1. The ProgId: what actually runs.
SetReg "HKLM:\SOFTWARE\Classes\$ProgId" "(default)" "MarwanOS Browser Document"
SetReg "HKLM:\SOFTWARE\Classes\$ProgId\DefaultIcon" "(default)" ("{0},0" -f $iconSource)
SetReg "HKLM:\SOFTWARE\Classes\$ProgId\shell\open\command" "(default)" $openCmd

# 2. The browser registration Windows' own Default Apps UI reads.
$client = "HKLM:\SOFTWARE\Clients\StartMenuInternet\$ClientKey"
SetReg $client "(default)" $AppName
SetReg "$client\DefaultIcon" "(default)" ("{0},0" -f $iconSource)
SetReg "$client\shell\open\command" "(default)" ('"{0}"' -f $opener)
SetReg "$client\Capabilities" "ApplicationName" $AppName
SetReg "$client\Capabilities" "ApplicationIcon" ("{0},0" -f $iconSource)
SetReg "$client\Capabilities" "ApplicationDescription" $AppDesc
SetReg "$client\Capabilities\StartMenu" "StartMenuInternet" $ClientKey
foreach ($p in @("http", "https"))            { SetReg "$client\Capabilities\URLAssociations"  $p $ProgId }
foreach ($f in @(".htm", ".html", ".xhtml"))  { SetReg "$client\Capabilities\FileAssociations" $f $ProgId }

# 3. The pointer that makes the registration visible.
SetReg "HKLM:\SOFTWARE\RegisteredApplications" $ClientKey "SOFTWARE\Clients\StartMenuInternet\$ClientKey\Capabilities"

# 4. The fallback for every session that is not the console. Written BEFORE the
#    policy, so that if the policy write fails the fallback is still on record.
if ($current -and $current.Command) {
    SetReg "HKLM:\SOFTWARE\MarwanOS\Browser" "FallbackCommand" $current.Command
    SetReg "HKLM:\SOFTWARE\MarwanOS\Browser" "FallbackProgId"  $current.ProgId
} else {
    Line "WARN no previous http handler could be read; no fallback recorded. Links clicked in a"
    Line "     session with no MarwanOS shell will be queued rather than opened."
}
SetReg "HKLM:\SOFTWARE\MarwanOS\Browser" "PreviousPolicy" ([string]$existingPolicy)

# 5. The enforced association.
if (-not $NoPolicy) {
    $xml = @"
<?xml version="1.0" encoding="UTF-8"?>
<!-- Written by provisioning\21-set-default-browser.ps1. Read by Windows at every user
     logon via the DefaultAssociationsConfiguration policy. Editing this file changes the
     machine's default browser for every account at their next sign-in. -->
<DefaultAssociations>
  <Association Identifier="http"   ProgId="$ProgId" ApplicationName="$AppName" />
  <Association Identifier="https"  ProgId="$ProgId" ApplicationName="$AppName" />
  <Association Identifier=".htm"   ProgId="$ProgId" ApplicationName="$AppName" />
  <Association Identifier=".html"  ProgId="$ProgId" ApplicationName="$AppName" />
</DefaultAssociations>
"@
    try {
        New-Item -ItemType Directory -Force (Split-Path $AssocXml) | Out-Null
        # UTF8 without BOM: the policy parser is content with either, but the file is
        # declared UTF-8 and a BOM in a file that says encoding="UTF-8" is noise.
        [IO.File]::WriteAllText($AssocXml, $xml, (New-Object Text.UTF8Encoding($false)))
        Line "OK  wrote $AssocXml"
    } catch { Line "FAIL writing $AssocXml  --  $($_.Exception.Message)"; throw }

    SetReg "HKLM:\SOFTWARE\Policies\Microsoft\Windows\System" "DefaultAssociationsConfiguration" $AssocXml
}

Line "=== done ==="
Write-Host ""
Write-Host "MarwanOS is registered as a browser." -ForegroundColor Green
if ($NoPolicy) {
    Write-Host "-NoPolicy was set, so nothing is enforced: pick it by hand in Settings > Apps >"
    Write-Host "Default apps, or re-run without -NoPolicy."
} else {
    Write-Host "It becomes the default for http/https AT THE NEXT SIGN-IN of each account -" -ForegroundColor Yellow
    Write-Host "the policy is evaluated at logon. Sign the console account out and back in to test." -ForegroundColor Yellow
}
Write-Host ""
Write-Host "Verify from the console account, after signing back in:"
Write-Host '    MarwanOpenUrl.exe writes %LOCALAPPDATA%\ArcOS\openurl\openurl.log on every call'
Write-Host "    the shell logs [OPENURL] lines for anything it accepts"
exit 0
