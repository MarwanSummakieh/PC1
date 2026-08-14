<#
.SYNOPSIS
    UNDO for 01-enable-shell-launcher.ps1 -- disables the Shell Launcher optional feature.

.DESCRIPTION
    Removes the Client-EmbeddedShellLauncher optional component.

    ORDER MATTERS. Run 93-remove-shell-launcher-config.ps1 FIRST. That script
    clears the per-SID custom shell and calls WESL_UserSetting.SetEnabled($false).
    Disabling the optional feature while enforcement is still on is not a tested
    path and could leave the arcshell account without a working shell with no WMI
    provider left to fix it with. This script refuses to run if it can still see
    an enabled Shell Launcher configuration, unless -Force is given.

    By default this script leaves Client-DeviceLockdown alone (it is a shared
    parent component that other lockdown features depend on). Pass
    -AlsoDisableDeviceLockdown only if you are sure nothing else needs it.

.DOCS
    https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/configure

.NOTES
    UNVERIFIED: on Windows 11 IoT Enterprise LTSC 2024, whether disabling this
    feature requires a reboot to fully unregister root\standardcimv2\embedded.
    Assume yes and reboot before drawing conclusions.

    RUN AS: elevated Windows PowerShell 5.1.
#>

[CmdletBinding()]
param(
    [switch]$AlsoDisableDeviceLockdown,
    # Skip the "is enforcement still on?" safety check.
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script must be run from an ELEVATED PowerShell prompt."
    exit 1
}

Write-Host "=== 91-disable-shell-launcher.ps1 (UNDO of step 01) ===" -ForegroundColor Cyan

# --- Safety check: is Shell Launcher enforcement still switched on? ----------
if (-not $Force) {
    try {
        $slClass = [wmiclass]"\\localhost\root\standardcimv2\embedded:WESL_UserSetting"
        $enabled = $slClass.IsEnabled().Enabled
        if ($enabled) {
            Write-Error @"
Shell Launcher enforcement is STILL ENABLED (WESL_UserSetting.IsEnabled() = True).
Run provisioning\93-remove-shell-launcher-config.ps1 first, then re-run this script.
(Override with -Force only if you know what you are doing.)
"@
            exit 2
        }
        Write-Host "  Pre-check: Shell Launcher enforcement is already disabled. Good."
    }
    catch {
        # Class missing usually means the feature is already gone -- that is fine.
        Write-Host "  Pre-check: WESL_UserSetting not reachable (feature likely already removed)."
    }
}

$FeatureNames = @('Client-EmbeddedShellLauncher')
if ($AlsoDisableDeviceLockdown) { $FeatureNames += 'Client-DeviceLockdown' }

# --- Idempotency -------------------------------------------------------------
$toDisable = @()
foreach ($f in $FeatureNames) {
    try {
        $state = (Get-WindowsOptionalFeature -Online -FeatureName $f).State
        Write-Host ("  {0,-32} : {1}" -f $f, $state)
        if ($state -eq 'Enabled') { $toDisable += $f }
    } catch {
        Write-Host ("  {0,-32} : NOT PRESENT" -f $f)
    }
}

if ($toDisable.Count -eq 0) {
    Write-Host "Nothing to disable. Already in the desired state." -ForegroundColor Green
    exit 0
}

# --- The actual change -------------------------------------------------------
$result = Disable-WindowsOptionalFeature -Online -FeatureName $toDisable -NoRestart

Write-Host ""
Write-Host "Disable-WindowsOptionalFeature completed." -ForegroundColor Green
Write-Host "  RestartNeeded : $($result.RestartNeeded)"
if ($result.RestartNeeded) {
    Write-Host "Restart the machine to complete removal." -ForegroundColor Yellow
}
Write-Host "Record this in provisioning\MACHINE-CHANGES.md" -ForegroundColor DarkGray
exit 0
