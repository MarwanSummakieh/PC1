<#
.SYNOPSIS
    UNDO for 03-apply-shell-launcher.ps1 -- removes the custom shell for 'marwanshell'
    and turns Shell Launcher enforcement off.

.DESCRIPTION
    THIS IS THE PRIMARY RECOVERY SCRIPT. If the marwanshell session is unusable, sign
    into 'brain' (which is never configured and always gets Explorer) and run this.

    What it does, in order:
      1. Resets the default shell for unconfigured users to explorer.exe.
         (Done first so that even if a later step fails, no account is left
         pointing at a broken custom shell.)
      2. Calls WESL_UserSetting.RemoveCustomShell for the target SID.
      3. Calls WESL_UserSetting.SetEnabled($false) to disable enforcement globally.
      4. Reads the configuration back and prints every remaining per-SID entry.

    With -All it removes EVERY per-SID entry it finds, not just the target's. Use
    that if the configuration is in an unknown state.

    Idempotent. Safe to run repeatedly. Safe to run when nothing is configured.

    Changes take effect at the next sign-in of the affected account
    (https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/configure).
    An marwanshell session that is currently running its custom shell keeps running it
    until sign-out.

.DOCS
    RemoveCustomShell : https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/wesl-usersettingremovecustomshell
    SetEnabled        : https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/wesl-usersettingsetenabled
    SetDefaultShell   : https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/wesl-usersettingsetdefaultshell
    WESL_UserSetting  : https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/wesl-usersetting

.NOTES
    This script never removes a configuration for the primary account 'brain'
    unless you explicitly pass -All, and even then it announces it loudly. Normally
    'brain' has no entry at all.

    UNVERIFIED on Windows 11 IoT Enterprise LTSC 2024: whether SetEnabled($false)
    takes effect for an already-signed-in Shell Launcher session, or only at the
    next sign-in. Assume "next sign-in" and sign the marwanshell session out.

    RUN AS: elevated *Windows PowerShell 5.1* (powershell.exe), signed in as 'brain'.
#>

[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string]$UserName = 'marwanshell',

    # Remove every per-SID custom shell entry, not just the target's.
    # Use when the configuration state is unknown or the account was already deleted.
    [switch]$All,

    # Leave enforcement enabled (only clear the per-SID entry). Rarely what you want.
    [switch]$KeepEnabled,

    [string]$FallbackShell = 'explorer.exe',

    [switch]$WhatIfOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$NAMESPACE       = 'root\standardcimv2\embedded'
$COMPUTER        = 'localhost'
$PRIMARY_ACCOUNT = 'brain'
$ACTION_RESTART_SHELL = 0
$ACTION_NAMES = @{ 0 = 'RestartShell'; 1 = 'RestartDevice'; 2 = 'ShutdownDevice'; 3 = 'DoNothing' }

Write-Host "=== 93-remove-shell-launcher-config.ps1 (UNDO of step 03) ===" -ForegroundColor Cyan

if ($PSVersionTable.PSEdition -ne 'Desktop') {
    Write-Error "Run this under Windows PowerShell 5.1 (powershell.exe), not pwsh. [wmiclass] is unavailable in PowerShell 7."
    exit 1
}

$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script must be run from an ELEVATED PowerShell prompt."
    exit 1
}

# --- Bind to the WMI class ---------------------------------------------------
try {
    $ShellLauncherClass = [wmiclass]"\\$COMPUTER\${NAMESPACE}:WESL_UserSetting"
}
catch {
    Write-Host "WESL_UserSetting is not reachable in $NAMESPACE." -ForegroundColor Yellow
    Write-Host "The Shell Launcher feature is not installed, so there is no configuration to remove."
    Write-Host "Nothing to do." -ForegroundColor Green
    exit 0
}

function Assert-WmiOk {
    param($Result, [string]$What)
    $rv = $null
    if ($Result -and ($Result.PSObject.Properties.Name -contains 'ReturnValue')) { $rv = $Result.ReturnValue }
    if ($null -ne $rv -and $rv -ne 0) {
        throw ("{0} failed. HRESULT/ReturnValue = 0x{1:X8} ({1})" -f $What, $rv)
    }
    Write-Host "  $What -> OK" -ForegroundColor Green
}

# --- Show the current state --------------------------------------------------
Write-Host ""
Write-Host "Current state:" -ForegroundColor Cyan
try {
    Write-Host "  IsEnabled : $($ShellLauncherClass.IsEnabled().Enabled)"
    $def = $ShellLauncherClass.GetDefaultShell()
    Write-Host "  Default   : $($def.Shell) (action $($def.DefaultAction) = $($ACTION_NAMES[[int]$def.DefaultAction]))"
} catch {
    Write-Warning "  Could not read current state: $($_.Exception.Message)"
}

$existingEntries = @(Get-WmiObject -Namespace $NAMESPACE -Computer $COMPUTER -Class WESL_UserSetting -ErrorAction SilentlyContinue)
if ($existingEntries.Count -gt 0) {
    Write-Host "  Per-SID entries:"
    $existingEntries | Select-Object Sid, Shell, DefaultAction | Format-Table -AutoSize
} else {
    Write-Host "  Per-SID entries: none"
}

# --- Work out which SIDs to remove ------------------------------------------
$sidsToRemove = New-Object System.Collections.Generic.List[string]

if ($All) {
    foreach ($e in $existingEntries) { $sidsToRemove.Add($e.Sid) }
    Write-Warning "-All specified: every per-SID entry above will be removed."
}
else {
    # Resolve the target account. It may already be gone (if 92 ran first), in
    # which case fall back to matching any entry whose SID is not brain's.
    $localUser = Get-LocalUser -Name $UserName -ErrorAction SilentlyContinue
    if ($localUser) {
        $sidsToRemove.Add($localUser.SID.Value)
        Write-Host ""
        Write-Host "Target: $UserName -> $($localUser.SID.Value)"
    }
    else {
        Write-Warning "Local account '$UserName' no longer exists; cannot resolve its SID by name."
        Write-Warning "Re-run with -All to clear every per-SID entry, or remove the stale SID manually."
    }
}

# --- Guard: never remove brain's entry unless -All was explicitly given ------
$brainSid = $null
$brainUser = Get-LocalUser -Name $PRIMARY_ACCOUNT -ErrorAction SilentlyContinue
if ($brainUser) { $brainSid = $brainUser.SID.Value }

if ($brainSid -and ($sidsToRemove -contains $brainSid)) {
    if ($All) {
        Write-Warning "NOTE: '$PRIMARY_ACCOUNT' ($brainSid) has a Shell Launcher entry and -All will remove it."
        Write-Warning "      That is a RESTORATIVE action -- removing it gives '$PRIMARY_ACCOUNT' the default shell ($FallbackShell)."
    } else {
        Write-Error "GUARD TRIPPED: refusing to act on the primary account '$PRIMARY_ACCOUNT'. ABORTED."
        exit 3
    }
}

if ($WhatIfOnly) {
    Write-Host ""
    Write-Host "-WhatIfOnly specified. Would:" -ForegroundColor Yellow
    Write-Host "  SetDefaultShell('$FallbackShell', $ACTION_RESTART_SHELL)"
    foreach ($s in $sidsToRemove) { Write-Host "  RemoveCustomShell('$s')" }
    if (-not $KeepEnabled) { Write-Host "  SetEnabled(`$false)" }
    Write-Host "No changes made." -ForegroundColor Yellow
    exit 0
}

# --- 1. Reset the default shell ---------------------------------------------
Write-Host ""
Write-Host "Resetting default shell to '$FallbackShell' ..."
$r = $ShellLauncherClass.SetDefaultShell($FallbackShell, $ACTION_RESTART_SHELL)
Assert-WmiOk -Result $r -What "SetDefaultShell('$FallbackShell', $ACTION_RESTART_SHELL)"

# --- 2. Remove the per-SID entries ------------------------------------------
if ($sidsToRemove.Count -eq 0) {
    Write-Host "No per-SID entries to remove."
} else {
    foreach ($sid in $sidsToRemove) {
        Write-Host "Removing custom shell for $sid ..."
        try {
            $r = $ShellLauncherClass.RemoveCustomShell($sid)
            Assert-WmiOk -Result $r -What "RemoveCustomShell('$sid')"
        }
        catch {
            # A "not found" here is benign: the entry was already gone.
            Write-Warning "  RemoveCustomShell('$sid') reported: $($_.Exception.Message)"
            Write-Warning "  If the entry no longer appears in the read-back below, this is fine."
        }
    }
}

# --- 3. Disable enforcement --------------------------------------------------
if ($KeepEnabled) {
    Write-Warning "-KeepEnabled specified: Shell Launcher enforcement left ON."
} else {
    Write-Host "Disabling Shell Launcher enforcement ..."
    $r = $ShellLauncherClass.SetEnabled($false)
    Assert-WmiOk -Result $r -What 'SetEnabled($false)'
}

# --- Read back ---------------------------------------------------------------
Write-Host ""
Write-Host "State after undo:" -ForegroundColor Cyan
Write-Host "  IsEnabled : $($ShellLauncherClass.IsEnabled().Enabled)"
$def = $ShellLauncherClass.GetDefaultShell()
Write-Host "  Default   : $($def.Shell) (action $($def.DefaultAction) = $($ACTION_NAMES[[int]$def.DefaultAction]))"
$remaining = @(Get-WmiObject -Namespace $NAMESPACE -Computer $COMPUTER -Class WESL_UserSetting -ErrorAction SilentlyContinue)
if ($remaining.Count -gt 0) {
    Write-Host "  Remaining per-SID entries:"
    $remaining | Select-Object Sid, Shell, DefaultAction | Format-Table -AutoSize
    Write-Warning "  Entries remain. Re-run with -All if you want them all gone."
} else {
    Write-Host "  Remaining per-SID entries: none" -ForegroundColor Green
}

Write-Host ""
Write-Host "Done. The affected account gets its normal shell at the NEXT SIGN-IN." -ForegroundColor Green
Write-Host "If an marwanshell session is still open, sign it out (Ctrl+Alt+Del > Sign out)."
Write-Host "Record this in provisioning\MACHINE-CHANGES.md" -ForegroundColor DarkGray
exit 0
