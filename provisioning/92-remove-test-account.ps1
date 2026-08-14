<#
.SYNOPSIS
    UNDO for 02-create-test-account.ps1 -- deletes the 'arcshell' local account and its profile.

.DESCRIPTION
    Removes:
      1. The Win32_UserProfile for the account's SID (deletes C:\Users\arcshell
         and the registry ProfileList entry) via Remove-CimInstance.
      2. The local user account itself via Remove-LocalUser.

    Profile is removed FIRST, because the profile lookup is keyed on the SID and
    the SID is easiest to resolve while the account still exists.

    THIS IS DESTRUCTIVE AND IRREVERSIBLE for anything stored in C:\Users\arcshell.
    That directory is expected to hold nothing of value -- it is a throwaway test
    profile. Confirm that before running.

    Idempotent: if the account does not exist, the script reports that and exits 0.
    It will still offer to clean up an orphaned profile directory if one is found.

.DOCS
    Remove-LocalUser   : https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.localaccounts/remove-localuser
    Win32_UserProfile  : https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-userprofile
    Remove-CimInstance : https://learn.microsoft.com/en-us/powershell/module/cimcmdlets/remove-ciminstance

.NOTES
    ORDER MATTERS. Run 93-remove-shell-launcher-config.ps1 BEFORE this script.
    If a Shell Launcher custom-shell entry still points at this account's SID,
    deleting the account leaves an orphaned SID entry in the Shell Launcher
    configuration. That is not known to be harmful, but it is untidy and the
    stale entry could in principle be re-matched if the SID were ever reissued
    (SIDs are not reused in practice, but do not rely on that).

    The account must not be signed in. If it is, sign it out first
    (Ctrl+Alt+Del > Sign out from that session, or use `query user` / `logoff`).
    Remove-CimInstance on a loaded profile will fail.

    UNVERIFIED: on Windows 11 IoT Enterprise LTSC 2024, whether Remove-CimInstance
    on Win32_UserProfile reliably removes the directory when the profile has never
    been loaded (i.e. the account was created but never signed in). In that case
    there is usually no Win32_UserProfile instance at all and nothing to remove.

    RUN AS: elevated Windows PowerShell 5.1, signed in as 'brain' (or any admin
    that is NOT the account being deleted).
#>

[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string]$UserName = 'arcshell',

    # Skip the interactive confirmation prompt (for scripted teardown).
    [switch]$Confirm,

    [switch]$WhatIfOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# --- Hard guard: never delete the primary account or a built-in -------------
$ProtectedNames = @('brain', 'Administrator', 'DefaultAccount', 'Guest', 'WDAGUtilityAccount', 'SYSTEM')
if ($ProtectedNames -contains $UserName) {
    Write-Error "REFUSING: '$UserName' is a protected account name. This script only deletes disposable test accounts."
    exit 1
}

$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script must be run from an ELEVATED PowerShell prompt."
    exit 1
}

# --- Guard: do not delete the account you are currently signed in as --------
$currentSid = $identity.User.Value
Write-Host "=== 92-remove-test-account.ps1 (UNDO of step 02) ===" -ForegroundColor Cyan
Write-Host "Running as   : $($identity.Name)  [$currentSid]"
Write-Host "Target       : $UserName"
Write-Host ""

# --- Resolve the account -----------------------------------------------------
$user = Get-LocalUser -Name $UserName -ErrorAction SilentlyContinue
$targetSid = $null

if ($user) {
    $targetSid = $user.SID.Value
    Write-Host "Found account '$UserName' with SID $targetSid"
    if ($targetSid -eq $currentSid) {
        Write-Error "REFUSING: you are currently signed in as '$UserName'. Sign in as 'brain' and run this again."
        exit 2
    }
} else {
    Write-Host "Account '$UserName' does not exist (already removed, or never created)."
}

# --- Locate the profile ------------------------------------------------------
$profileInstance = $null
if ($targetSid) {
    $profileInstance = Get-CimInstance -ClassName Win32_UserProfile -Filter "SID = '$targetSid'" -ErrorAction SilentlyContinue
}
if (-not $profileInstance) {
    # Fall back to matching by path, which catches an orphaned profile left over
    # after the account was deleted by some other means.
    $profileInstance = Get-CimInstance -ClassName Win32_UserProfile -ErrorAction SilentlyContinue |
                       Where-Object { $_.LocalPath -and (Split-Path $_.LocalPath -Leaf) -ieq $UserName -and -not $_.Special }
}

if ($profileInstance) {
    foreach ($p in @($profileInstance)) {
        Write-Host "Found profile: $($p.LocalPath)  [SID $($p.SID)]  Loaded=$($p.Loaded)"
        if ($p.Special) {
            Write-Error "REFUSING: profile $($p.LocalPath) is flagged Special (a system profile). Aborting."
            exit 3
        }
        if ($p.SID -eq $currentSid) {
            Write-Error "REFUSING: that profile belongs to the currently signed-in user. Aborting."
            exit 3
        }
        if ($p.Loaded) {
            Write-Error "Profile is currently LOADED -- the account is signed in somewhere. Sign it out first (Ctrl+Alt+Del > Sign out in that session), then re-run."
            exit 4
        }
    }
} else {
    Write-Host "No Win32_UserProfile found for '$UserName' (account may never have signed in)."
}

if (-not $user -and -not $profileInstance) {
    Write-Host "Nothing to do. Already in the desired state." -ForegroundColor Green
    exit 0
}

if ($WhatIfOnly) {
    Write-Host "-WhatIfOnly specified. No changes made." -ForegroundColor Yellow
    exit 0
}

# --- Confirmation ------------------------------------------------------------
if (-not $Confirm) {
    Write-Host ""
    Write-Warning "This will PERMANENTLY delete the account '$UserName' and everything in its profile directory."
    $answer = Read-Host "Type the account name '$UserName' to confirm"
    if ($answer -cne $UserName) {
        Write-Host "Confirmation did not match. Aborted. No changes made." -ForegroundColor Yellow
        exit 0
    }
}

# --- 1. Remove the profile ---------------------------------------------------
if ($profileInstance) {
    foreach ($p in @($profileInstance)) {
        Write-Host "Removing profile $($p.LocalPath) ..."
        Remove-CimInstance -InputObject $p
        Write-Host "  Removed." -ForegroundColor Green
    }
}

# --- 2. Remove the account ---------------------------------------------------
if ($user) {
    Write-Host "Removing local user '$UserName' ..."
    Remove-LocalUser -Name $UserName
    Write-Host "  Removed." -ForegroundColor Green
}

# --- Post-check --------------------------------------------------------------
$still = Get-LocalUser -Name $UserName -ErrorAction SilentlyContinue
if ($still) {
    Write-Error "Account still present after Remove-LocalUser. Investigate."
    exit 5
}
$leftoverDir = Join-Path $env:SystemDrive "Users\$UserName"
if (Test-Path -LiteralPath $leftoverDir) {
    Write-Warning "Directory $leftoverDir still exists (files may have been in use)."
    Write-Warning "Remove it manually after a reboot:  Remove-Item -LiteralPath '$leftoverDir' -Recurse -Force"
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "Record this in provisioning\MACHINE-CHANGES.md" -ForegroundColor DarkGray
exit 0
