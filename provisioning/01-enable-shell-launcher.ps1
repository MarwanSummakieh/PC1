<#
.SYNOPSIS
    Enables the Windows "Shell Launcher" optional feature (Client-EmbeddedShellLauncher).

.DESCRIPTION
    Step 1 of 3 in the PC1 shell-replacement provisioning sequence.

    This ONLY installs the optional component. It does not configure any shell,
    does not touch any user account, and does not enable Shell Launcher
    enforcement. Installing the feature alone changes nothing about how any
    account logs in. Enforcement is turned on later by 03-apply-shell-launcher.ps1
    (via WESL_UserSetting.SetEnabled).

    Idempotent: if the feature is already Enabled, the script prints the state and
    exits 0 without calling Enable-WindowsOptionalFeature.

.DOCS
    Enable Shell Launcher (exact command + feature names):
      https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/configure
    Shell Launcher overview / edition requirements:
      https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/

    Microsoft Learn ("Configure Shell Launcher" > Enable Shell Launcher > PowerShell tab)
    documents the enable command as:
        Enable-WindowsOptionalFeature -FeatureName Client-DeviceLockdown,Client-EmbeddedShellLauncher -Online
    i.e. BOTH Client-DeviceLockdown (the parent Device Lockdown component) and
    Client-EmbeddedShellLauncher. This script handles both, because enabling only
    Client-EmbeddedShellLauncher on a machine where Client-DeviceLockdown is
    disabled may fail or silently leave the WMI provider unregistered.

.UNDO
    provisioning\91-disable-shell-launcher.ps1
    Equivalent one-liner:
        Disable-WindowsOptionalFeature -Online -FeatureName Client-EmbeddedShellLauncher -NoRestart

.NOTES
    UNVERIFIED ON THIS MACHINE (Windows 11 IoT Enterprise LTSC 2024 / 24H2 base):
      * Whether Client-DeviceLockdown is already enabled by default on IoT
        Enterprise LTSC 2024 images. On some IoT SKUs the Device Lockdown group is
        preinstalled. The pre-check below prints the real state -- read it.
      * Whether -NoRestart actually avoids a reboot on this build. DISM may still
        report RestartNeeded. If RestartNeeded is True, the WMI class
        WESL_UserSetting may not be resolvable until after a reboot.
      * The Shell Launcher *license* check (SLGetWindowsInformationDWORD with
        "EmbeddedFeature-ShellLauncher-Enabled") from the Learn WMI sample is a
        separate gate from the optional feature. 03-apply-shell-launcher.ps1
        performs that check before writing anything.

    RUN AS: elevated Windows PowerShell 5.1 (powershell.exe, "Run as administrator").
    THIS SCRIPT CHANGES MACHINE STATE. Do not run without the user's explicit go-ahead.
#>

[CmdletBinding()]
param(
    # Print what would happen and exit without changing anything.
    [switch]$WhatIfOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# --- Feature names -----------------------------------------------------------
# Client-DeviceLockdown        : parent "Device Lockdown" optional component
# Client-EmbeddedShellLauncher : "Shell Launcher" itself (registers WESL_UserSetting)
$FeatureNames = @('Client-DeviceLockdown', 'Client-EmbeddedShellLauncher')

# --- Guard: must be elevated -------------------------------------------------
$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script must be run from an ELEVATED PowerShell prompt (Run as administrator)."
    exit 1
}

Write-Host "=== 01-enable-shell-launcher.ps1 ===" -ForegroundColor Cyan
Write-Host "OS: $((Get-CimInstance Win32_OperatingSystem).Caption) build $([Environment]::OSVersion.Version)"
Write-Host ""

# --- Pre-check: report current state of every relevant feature ---------------
$states = @{}
foreach ($f in $FeatureNames) {
    try {
        $feat = Get-WindowsOptionalFeature -Online -FeatureName $f
        $states[$f] = $feat.State
        Write-Host ("  {0,-32} : {1}" -f $f, $feat.State)
    }
    catch {
        # Feature name not present on this SKU at all.
        $states[$f] = 'NOTPRESENT'
        Write-Warning ("  {0,-32} : NOT PRESENT on this image ({1})" -f $f, $_.Exception.Message)
    }
}
Write-Host ""

# --- Idempotency: nothing to do if everything is already Enabled -------------
$toEnable = @($FeatureNames | Where-Object { $states[$_] -ne 'Enabled' -and $states[$_] -ne 'NOTPRESENT' })

if ($toEnable.Count -eq 0) {
    if ($states['Client-EmbeddedShellLauncher'] -eq 'Enabled') {
        Write-Host "Shell Launcher is ALREADY ENABLED. Nothing to do." -ForegroundColor Green
        Write-Host "Next step: provisioning\02-create-test-account.ps1"
        exit 0
    }
    Write-Error "Client-EmbeddedShellLauncher is not present on this image. Shell Launcher is not available on this SKU/build. STOP -- do not continue to steps 02/03."
    exit 2
}

Write-Host "Will enable: $($toEnable -join ', ')" -ForegroundColor Yellow

if ($WhatIfOnly) {
    Write-Host "-WhatIfOnly specified. No changes made." -ForegroundColor Yellow
    exit 0
}

# --- The actual change -------------------------------------------------------
# -NoRestart: do not auto-reboot. We report RestartNeeded and let the human decide.
# -All is deliberately NOT used here so that no unexpected parent features are
# pulled in silently; the parent is named explicitly in $FeatureNames instead.
$result = Enable-WindowsOptionalFeature -Online -FeatureName $toEnable -NoRestart

Write-Host ""
Write-Host "Enable-WindowsOptionalFeature completed." -ForegroundColor Green
Write-Host "  RestartNeeded : $($result.RestartNeeded)"

# --- Post-check --------------------------------------------------------------
foreach ($f in $FeatureNames) {
    if ($states[$f] -eq 'NOTPRESENT') { continue }
    $after = Get-WindowsOptionalFeature -Online -FeatureName $f
    Write-Host ("  {0,-32} : {1}" -f $f, $after.State)
}

Write-Host ""
if ($result.RestartNeeded) {
    Write-Host "A RESTART IS REQUIRED before the WESL_UserSetting WMI class is reliably available." -ForegroundColor Yellow
    Write-Host "Restart, then run: provisioning\02-create-test-account.ps1"
} else {
    Write-Host "Next step: provisioning\02-create-test-account.ps1"
}

Write-Host ""
Write-Host "UNDO for this step: provisioning\91-disable-shell-launcher.ps1" -ForegroundColor DarkGray
Write-Host "Record this change in provisioning\MACHINE-CHANGES.md" -ForegroundColor DarkGray
exit 0
