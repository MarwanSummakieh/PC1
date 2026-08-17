<#
.SYNOPSIS
    Applies a Shell Launcher custom-shell configuration to ONE account ('marwanshell') only.

.DESCRIPTION
    Step 3 of 3 in the PC1 shell-replacement provisioning sequence. This is the
    step that actually changes what a user sees after sign-in.

    What it does, in order:
      1. Verifies the Shell Launcher license and that the WESL_UserSetting WMI
         class is reachable in root\standardcimv2\embedded.
      2. Resolves -UserName to a SID.
      3. Runs a block of GUARDS (see "SAFETY GUARDS" below) and ABORTS on any hit.
      4. Sets the DEFAULT shell for all unconfigured users to plain explorer.exe
         with action 0 (restart the shell). This is the safety net: any account
         that is not explicitly configured -- including 'brain' -- keeps a normal
         Windows desktop.
      5. Calls WESL_UserSetting.SetCustomShell for the target SID ONLY.
      6. Calls WESL_UserSetting.SetEnabled($true) to turn on enforcement.
      7. Reads the configuration back and prints it.

    Changes do not take effect until the affected user signs in
    (per https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/configure
    -- "Any changes don't take effect until a user signs in").

    Idempotent: SetCustomShell overwrites any existing entry for that SID, and
    SetDefaultShell / SetEnabled are set-to-desired-state calls. Re-running the
    script with the same arguments converges on the same configuration.

.SAFETY GUARDS
    The script REFUSES to write anything if any of these are true:
      * -UserName is in the protected list (brain, Administrator, Guest, ...).
      * The resolved SID equals the SID of the local account 'brain'.
      * The resolved SID equals the SID of the account currently running the script.
      * The resolved SID is a member of the local Administrators group.
        (Per the project rule: never configure a SID that belongs to an
        Administrators member other than marwanshell. If marwanshell itself is somehow
        in Administrators, that is a provisioning error and the script also
        aborts -- fix the group membership first.)
      * The resolved SID is not a machine-local user SID (must be S-1-5-21-<domain>-<RID>
        with RID >= 1000). This blocks group SIDs such as S-1-5-32-544 and
        well-known SIDs such as S-1-1-0 (Everyone).
      * The resolved principal is not a local user returned by Get-LocalUser.

.PARAMETER ShellPath
    Full path to the executable Shell Launcher starts instead of explorer.exe.
    Defaults to the not-yet-built spike host. The script refuses to apply a
    missing path unless -AllowMissingShell is given, because a Shell Launcher
    entry pointing at a nonexistent binary gives the account a black screen.

.PARAMETER UserName
    Local account to configure. Defaults to 'marwanshell'.

.DOCS  (verified 2026-08-13 against Microsoft Learn)
    WESL_UserSetting class + full MOF + reference PowerShell sample:
      https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/wesl-usersetting
    SetCustomShell signature and the action enum table:
      https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/wesl-usersettingsetcustomshell
    SetDefaultShell:
      https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/wesl-usersettingsetdefaultshell
    SetEnabled:
      https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/wesl-usersettingsetenabled
    Configure Shell Launcher with the WMI provider (license-check sample):
      https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/configure-wmi
    Exit-code-to-action mapping explained:
      https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/configure

    Verified method signatures (MOF, from the WESL_UserSetting page):

        [Static] uint32 SetCustomShell(
            [In, Required] string Sid,
            [In, Required] string Shell,
            [In]           sint32 CustomReturnCodes[],
            [In]           sint32 CustomReturnCodesAction[],
            [In]           sint32 DefaultAction
        );
        [Static] uint32 GetCustomShell(
            [In, Required] string Sid,
            [Out, Required] string Shell,
            [Out, Required] sint32 CustomReturnCodes[],
            [Out, Required] sint32 CustomReturnCodesAction[],
            [Out, Required] sint32 DefaultAction
        );
        [Static] uint32 RemoveCustomShell([In, Required] string Sid);
        [Static] uint32 GetDefaultShell([Out, Required] string Shell,
                                        [Out, Required] sint32 DefaultAction);
        [Static] uint32 SetDefaultShell([In, Required] string Shell,
                                        [In, Required] sint32 DefaultAction);
        [Static] uint32 IsEnabled([Out, Required] boolean Enabled);
        [Static] uint32 SetEnabled([In, Required] boolean Enabled);

    VERIFIED WESL ACTION ENUM (this is the *action* value, NOT the exit code):

        0 = Restart the shell
        1 = Restart the device
        2 = Shut down the device
        3 = Do nothing

    !! IMPORTANT CORRECTION TO THE ORIGINAL BRIEF !!
    The brief described the map as "exit 0 = restart shell, 2 = restart device,
    3 = shutdown". Those are SHELL EXIT CODES, not action values. Microsoft's
    action enum for "restart device" is 1 and for "shut down" is 2 -- there is no
    action value 3 meaning shutdown (3 means "do nothing"). This script therefore
    implements the intent of the brief as an exit-code -> action MAPPING:

        shell exit code  0  ->  action 0  (RestartShell)
        shell exit code  2  ->  action 1  (RestartDevice)
        shell exit code  3  ->  action 2  (ShutdownDevice)

    which is expressed as two parallel arrays:
        CustomReturnCodes       = @(0, 2, 3)
        CustomReturnCodesAction = @(0, 1, 2)
    See -ReturnCodes / -ReturnCodeActions to override.

.SHELL LAUNCHER V1 vs V2 -- READ THIS
    The brief specifies "Shell Launcher V2". Microsoft Learn splits these:
      * The WESL_UserSetting WMI provider (used here) is the original per-SID
        configuration surface. It sets a Win32 executable as the shell. It is
        still documented and supported on current builds.
      * "Shell Launcher V2" specifically refers to the XML configuration schema
        (namespace http://schemas.microsoft.com/ShellLauncher/2019/Configuration)
        applied via the Assigned Access CSP, which adds UWP-app shells
        (V2:AppType="UWP") and V2:AllAppsFullScreen. V2 replaces explorer.exe with
        CustomShellHost.exe rather than Eshell.exe.
    Because PC1's shell is a Win32 console host (ShellHost.exe), the WMI provider
    is sufficient and is the simpler, more easily-reversible path. If UWP-shell or
    AllAppsFullScreen behaviour is ever needed, the configuration must move to the
    V2 XML via Assigned Access CSP instead. See:
      https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/configuration-file

.UNDO
    provisioning\93-remove-shell-launcher-config.ps1

.NOTES
    UNVERIFIED ON THIS MACHINE (Windows 11 IoT Enterprise LTSC 2024 / 24H2):
      * Whether the WMI provider path still fully governs shell selection on 24H2,
        or whether an Assigned Access CSP configuration would take precedence.
        Untested here. Verify empirically on the marwanshell account before trusting.
      * Whether a *console* application (ShellHost.exe) works as a Shell Launcher
        shell without a visible console window issue. Console-subsystem shells are
        not the documented scenario. Test on marwanshell first.
      * Whether Ctrl+Shift+Esc reaches Task Manager under Shell Launcher on this
        build. Shell Launcher has NO AllowTaskManager setting (that attribute
        belongs to Assigned Access multi-app kiosk XML, not Shell Launcher), and
        Shell Launcher does not itself set the DisableTaskMgr policy -- so Task
        Manager is expected to be reachable. THIS IS AN EXPECTATION, NOT A
        VERIFIED FACT. See provisioning\RECOVERY.md before you sign into marwanshell.
      * Whether -NoRestart from step 01 left the provider fully registered.

    RUN AS: elevated *Windows PowerShell 5.1* (powershell.exe). PowerShell 7 has
    removed the [wmiclass] type accelerator and the *-WmiObject cmdlets, and the
    Learn-documented invocation pattern used here depends on [wmiclass]. The
    script checks for this and aborts on PowerShell 7.

    THIS SCRIPT CHANGES MACHINE STATE. Do not run without the user's explicit go-ahead.
#>

[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string]$ShellPath = 'C:\Users\brain\Documents\repos\PC1\spike\ShellHost.exe',

    [ValidateNotNullOrEmpty()]
    [string]$UserName = 'marwanshell',

    # Shell exit codes to map. Parallel to -ReturnCodeActions.
    [int[]]$ReturnCodes = @(0, 2, 3),

    # WESL action for each exit code above. 0=RestartShell 1=RestartDevice 2=ShutdownDevice 3=DoNothing
    [int[]]$ReturnCodeActions = @(0, 1, 2),

    # Action for any exit code not in the map above.
    # 0 (RestartShell) keeps a shell on screen. If ShellHost.exe ever crash-loops,
    # re-run with -DefaultAction 3 (DoNothing) -- you then get a blank desktop but
    # Ctrl+Alt+Del still works, because that is handled by winlogon, not the shell.
    [ValidateSet(0, 1, 2, 3)]
    [int]$DefaultAction = 0,

    # Shell handed to every user that has no explicit configuration (incl. 'brain').
    [string]$FallbackShell = 'explorer.exe',

    # Allow configuring a ShellPath that does not exist yet (e.g. before the build).
    [switch]$AllowMissingShell,

    # Do everything except write. Prints exactly what would be sent to WMI.
    [switch]$WhatIfOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ============================================================================
# Constants
# ============================================================================
$NAMESPACE      = 'root\standardcimv2\embedded'
$COMPUTER       = 'localhost'
$PRIMARY_ACCOUNT = 'brain'      # the account that must NEVER be configured
$ProtectedNames = @('brain', 'Administrator', 'DefaultAccount', 'Guest', 'WDAGUtilityAccount', 'SYSTEM')

# WESL action enum -- verified against Microsoft Learn (see .DOCS above).
$ACTION_RESTART_SHELL   = 0
$ACTION_RESTART_DEVICE  = 1
$ACTION_SHUTDOWN_DEVICE = 2
$ACTION_DO_NOTHING      = 3
$ACTION_NAMES = @{
    0 = 'RestartShell'
    1 = 'RestartDevice'
    2 = 'ShutdownDevice'
    3 = 'DoNothing'
}

Write-Host "=== 03-apply-shell-launcher.ps1 ===" -ForegroundColor Cyan

# ============================================================================
# Environment guards
# ============================================================================
if ($PSVersionTable.PSEdition -ne 'Desktop') {
    Write-Error @"
This script requires Windows PowerShell 5.1 (powershell.exe), not PowerShell 7 (pwsh).
PowerShell 7 removed the [wmiclass] type accelerator that the Microsoft-documented
Shell Launcher invocation pattern relies on.
Re-run with:  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "<path to this script>"
"@
    exit 1
}

$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script must be run from an ELEVATED PowerShell prompt (Run as administrator)."
    exit 1
}
$currentSid = $identity.User.Value

# ============================================================================
# Shell Launcher license check
# Verbatim approach from the Learn WMI sample:
#   https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/configure-wmi
# This is a SEPARATE gate from the optional feature -- an unlicensed edition can
# have the feature installed and still refuse to enforce.
# ============================================================================
function Test-ShellLauncherLicenseEnabled {
    $source = @'
using System;
using System.Runtime.InteropServices;

static class CheckShellLauncherLicense
{
    const int S_OK = 0;

    public static bool IsShellLauncherLicenseEnabled()
    {
        int enabled = 0;
        if (NativeMethods.SLGetWindowsInformationDWORD("EmbeddedFeature-ShellLauncher-Enabled", out enabled) != S_OK) {
            enabled = 0;
        }
        return (enabled != 0);
    }

    static class NativeMethods
    {
        [DllImport("Slc.dll")]
        internal static extern int SLGetWindowsInformationDWORD([MarshalAs(UnmanagedType.LPWStr)]string valueName, out int value);
    }
}
'@
    $type = Add-Type -TypeDefinition $source -PassThru
    return $type[0]::IsShellLauncherLicenseEnabled()
}

Write-Host "Checking Shell Launcher license ..."
if (-not (Test-ShellLauncherLicenseEnabled)) {
    Write-Error "This device does not have the required license to use Shell Launcher (EmbeddedFeature-ShellLauncher-Enabled = 0). STOP."
    exit 2
}
Write-Host "  License OK." -ForegroundColor Green

# ============================================================================
# Bind to the WMI class
# ============================================================================
try {
    $ShellLauncherClass = [wmiclass]"\\$COMPUTER\${NAMESPACE}:WESL_UserSetting"
}
catch {
    Write-Error @"
Could not bind to WESL_UserSetting in $NAMESPACE.
$($_.Exception.Message)

Run provisioning\01-enable-shell-launcher.ps1 first, and reboot if it reported
RestartNeeded = True.
"@
    exit 2
}
Write-Host "  WESL_UserSetting bound in $NAMESPACE." -ForegroundColor Green

# Helper: WMI static methods return a __PARAMETERS object with a ReturnValue
# HRESULT. Anything non-zero is a failure; surface it loudly rather than
# continuing with a half-applied configuration.
function Assert-WmiOk {
    param($Result, [string]$What)
    $rv = $null
    if ($Result -and ($Result.PSObject.Properties.Name -contains 'ReturnValue')) {
        $rv = $Result.ReturnValue
    }
    if ($null -ne $rv -and $rv -ne 0) {
        throw ("{0} failed. HRESULT/ReturnValue = 0x{1:X8} ({1})" -f $What, $rv)
    }
    Write-Host "  $What -> OK" -ForegroundColor Green
}

# ============================================================================
# Resolve the target SID
# ============================================================================
Write-Host ""
Write-Host "Resolving target account ..."

if ($ProtectedNames -contains $UserName) {
    Write-Error "GUARD TRIPPED: '$UserName' is a protected account name. This project only ever configures a disposable test account. ABORTED -- nothing was written."
    exit 3
}

$localUser = Get-LocalUser -Name $UserName -ErrorAction SilentlyContinue
if (-not $localUser) {
    Write-Error "Local account '$UserName' does not exist. Run provisioning\02-create-test-account.ps1 first. ABORTED."
    exit 3
}
$targetSid = $localUser.SID.Value
Write-Host "  $UserName -> $targetSid"

# ============================================================================
# SAFETY GUARDS -- every one of these aborts before anything is written
# ============================================================================
Write-Host ""
Write-Host "Running safety guards ..." -ForegroundColor Yellow

# --- Guard 1: the SID must not belong to 'brain' ----------------------------
$brainSid = $null
$brainUser = Get-LocalUser -Name $PRIMARY_ACCOUNT -ErrorAction SilentlyContinue
if ($brainUser) {
    $brainSid = $brainUser.SID.Value
    Write-Host "  Primary account '$PRIMARY_ACCOUNT' SID: $brainSid"
} else {
    # Fall back to a name translation in case 'brain' is a domain/Entra principal.
    try {
        $brainSid = (New-Object System.Security.Principal.NTAccount($PRIMARY_ACCOUNT)).Translate(
                        [System.Security.Principal.SecurityIdentifier]).Value
        Write-Host "  Primary account '$PRIMARY_ACCOUNT' SID (via translate): $brainSid"
    } catch {
        Write-Warning "  Could not resolve '$PRIMARY_ACCOUNT' to a SID. Continuing with the remaining guards."
    }
}
if ($brainSid -and $targetSid -eq $brainSid) {
    Write-Error "GUARD TRIPPED: target SID $targetSid IS the SID of the primary account '$PRIMARY_ACCOUNT'. ABORTED -- nothing was written."
    exit 4
}
Write-Host "  [PASS] target SID is not '$PRIMARY_ACCOUNT'."

# --- Guard 2: not the account running this script ---------------------------
if ($targetSid -eq $currentSid) {
    Write-Error "GUARD TRIPPED: target SID is the SID of the account currently running this script ($($identity.Name)). ABORTED -- nothing was written."
    exit 4
}
Write-Host "  [PASS] target SID is not the current user."

# --- Guard 3: must be a machine-local user SID ------------------------------
# Local user accounts are S-1-5-21-<3 sub-authorities>-<RID>, RID >= 1000.
# This rejects group SIDs (S-1-5-32-544 Administrators), Everyone (S-1-1-0),
# Authenticated Users (S-1-5-11), built-in RIDs < 1000, etc.
if ($targetSid -notmatch '^S-1-5-21-\d+-\d+-\d+-(\d+)$') {
    Write-Error "GUARD TRIPPED: '$targetSid' is not a machine-local user SID (expected S-1-5-21-x-y-z-RID). Refusing to configure a group or well-known SID. ABORTED."
    exit 4
}
$rid = [int]$Matches[1]
if ($rid -lt 1000) {
    Write-Error "GUARD TRIPPED: SID RID $rid is below 1000, which means a built-in account (e.g. 500 = Administrator). ABORTED."
    exit 4
}
Write-Host "  [PASS] target SID is a local user SID with RID $rid."

# --- Guard 4: must NOT be a member of the local Administrators group --------
# Get-LocalGroupMember can throw on machines with unresolvable (deleted/Entra)
# member SIDs, so fall back to the WinNT ADSI provider, which returns raw SIDs.
function Get-AdministratorsMemberSid {
    $sids = New-Object System.Collections.Generic.List[string]
    $ok = $false
    try {
        Get-LocalGroupMember -Group 'Administrators' -ErrorAction Stop |
            ForEach-Object { $sids.Add($_.SID.Value) }
        $ok = $true
    }
    catch {
        Write-Warning "  Get-LocalGroupMember failed ($($_.Exception.Message)); falling back to ADSI."
    }
    if (-not $ok) {
        try {
            $adminGroupSid = New-Object System.Security.Principal.SecurityIdentifier(
                                 [System.Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)
            $adminGroupName = $adminGroupSid.Translate([System.Security.Principal.NTAccount]).Value.Split('\')[-1]
            $group = [ADSI]"WinNT://./$adminGroupName,group"
            foreach ($m in @($group.Invoke('Members'))) {
                $path = $m.GetType().InvokeMember('AdsPath', 'GetProperty', $null, $m, $null)
                $name = $m.GetType().InvokeMember('Name',    'GetProperty', $null, $m, $null)
                try {
                    $sids.Add((New-Object System.Security.Principal.NTAccount($name)).Translate(
                                  [System.Security.Principal.SecurityIdentifier]).Value)
                } catch {
                    Write-Warning "  Could not translate Administrators member '$path'."
                }
            }
            $ok = $true
        }
        catch {
            Write-Warning "  ADSI fallback also failed: $($_.Exception.Message)"
        }
    }
    if (-not $ok) {
        # Fail CLOSED. If we cannot prove the target is not an admin, we do not proceed.
        throw "GUARD TRIPPED: could not enumerate the local Administrators group, so the 'target must not be an administrator' guard cannot be evaluated. Failing closed. ABORTED -- nothing was written."
    }
    return $sids
}

$adminSids = Get-AdministratorsMemberSid
Write-Host "  Administrators members: $($adminSids.Count) SID(s)."
foreach ($a in $adminSids) { Write-Host "    - $a" -ForegroundColor DarkGray }

if ($adminSids -contains $targetSid) {
    Write-Error @"
GUARD TRIPPED: target SID $targetSid ('$UserName') is a member of the local
Administrators group. This project only configures a NON-administrative
throwaway account. ABORTED -- nothing was written.

Fix with:
    Remove-LocalGroupMember -Group Administrators -Member $UserName
then re-run this script.
"@
    exit 4
}
Write-Host "  [PASS] target SID is not in Administrators."

if ($brainSid -and -not ($adminSids -contains $brainSid)) {
    Write-Warning "  Note: '$PRIMARY_ACCOUNT' is not in the local Administrators group. Confirm you still have an admin account you can sign into before continuing."
}

Write-Host "All guards passed." -ForegroundColor Green

# ============================================================================
# Validate the shell path and the return-code map
# ============================================================================
Write-Host ""
if (-not (Test-Path -LiteralPath $ShellPath -PathType Leaf)) {
    if ($AllowMissingShell) {
        Write-Warning "ShellPath '$ShellPath' does not exist. Continuing because -AllowMissingShell was given."
        Write-Warning "Signing into '$UserName' before that binary exists will give a shell-less session. Read RECOVERY.md first."
    } else {
        Write-Error "ShellPath '$ShellPath' does not exist. Build it first, or pass -AllowMissingShell if you accept a shell-less session. ABORTED -- nothing was written."
        exit 5
    }
} else {
    Write-Host "ShellPath exists: $ShellPath" -ForegroundColor Green
}

if ($ReturnCodes.Count -ne $ReturnCodeActions.Count) {
    Write-Error "-ReturnCodes ($($ReturnCodes.Count) items) and -ReturnCodeActions ($($ReturnCodeActions.Count) items) must be the same length. ABORTED."
    exit 5
}
foreach ($a in $ReturnCodeActions) {
    if ($a -notin 0, 1, 2, 3) {
        Write-Error "Invalid WESL action '$a'. Valid: 0=RestartShell 1=RestartDevice 2=ShutdownDevice 3=DoNothing. ABORTED."
        exit 5
    }
}

Write-Host ""
Write-Host "Configuration to apply:" -ForegroundColor Cyan
Write-Host "  Account            : $UserName"
Write-Host "  SID                : $targetSid"
Write-Host "  Shell              : $ShellPath"
Write-Host "  Exit-code map:"
for ($i = 0; $i -lt $ReturnCodes.Count; $i++) {
    Write-Host ("    exit {0,-5} -> action {1} ({2})" -f $ReturnCodes[$i], $ReturnCodeActions[$i], $ACTION_NAMES[$ReturnCodeActions[$i]])
}
Write-Host ("  DefaultAction      : {0} ({1})" -f $DefaultAction, $ACTION_NAMES[$DefaultAction])
Write-Host "  Shell for everyone else (incl. '$PRIMARY_ACCOUNT') : $FallbackShell (action $ACTION_RESTART_SHELL / $($ACTION_NAMES[$ACTION_RESTART_SHELL]))"
Write-Host ""

if ($WhatIfOnly) {
    Write-Host "-WhatIfOnly specified. No changes made." -ForegroundColor Yellow
    exit 0
}

# ============================================================================
# Apply
# ============================================================================

# --- 1. Default shell for unconfigured users --------------------------------
# Deliberately FIRST, so that if anything below fails, every account that is not
# explicitly configured still resolves to a normal Explorer desktop.
# Signature: SetDefaultShell(string Shell, sint32 DefaultAction)
Write-Host "Setting default shell for unconfigured users to '$FallbackShell' ..."
$r = $ShellLauncherClass.SetDefaultShell($FallbackShell, $ACTION_RESTART_SHELL)
Assert-WmiOk -Result $r -What "SetDefaultShell('$FallbackShell', $ACTION_RESTART_SHELL)"

# --- 2. Custom shell for the target SID only --------------------------------
# Signature: SetCustomShell(string Sid, string Shell,
#                           sint32 CustomReturnCodes[], sint32 CustomReturnCodesAction[],
#                           sint32 DefaultAction)
# The arrays must be sint32[]; cast explicitly so PowerShell does not hand WMI
# an Object[] that the provider rejects.
Write-Host "Setting custom shell for $UserName ($targetSid) ..."
[int[]]$codes   = $ReturnCodes
[int[]]$actions = $ReturnCodeActions
$r = $ShellLauncherClass.SetCustomShell($targetSid, $ShellPath, $codes, $actions, $DefaultAction)
Assert-WmiOk -Result $r -What "SetCustomShell($targetSid, ...)"

# --- 3. Turn on enforcement --------------------------------------------------
# Signature: SetEnabled(boolean Enabled)
# LAST, so the configuration is fully in place before Shell Launcher starts
# acting on it.
Write-Host "Enabling Shell Launcher enforcement ..."
$r = $ShellLauncherClass.SetEnabled($true)
Assert-WmiOk -Result $r -What 'SetEnabled($true)'

# ============================================================================
# Read back and verify
# ============================================================================
Write-Host ""
Write-Host "Verifying ..." -ForegroundColor Cyan

$isEnabled = $ShellLauncherClass.IsEnabled()
Write-Host "  IsEnabled            : $($isEnabled.Enabled)"

$def = $ShellLauncherClass.GetDefaultShell()
Write-Host "  Default shell        : $($def.Shell)  (action $($def.DefaultAction) = $($ACTION_NAMES[[int]$def.DefaultAction]))"

$custom = $ShellLauncherClass.GetCustomShell($targetSid)
Write-Host "  $UserName shell      : $($custom.Shell)"
Write-Host "  $UserName codes      : $($custom.CustomReturnCodes -join ', ')"
Write-Host "  $UserName actions    : $($custom.CustomReturnCodesAction -join ', ')"
Write-Host "  $UserName default    : $($custom.DefaultAction) ($($ACTION_NAMES[[int]$custom.DefaultAction]))"

Write-Host ""
Write-Host "All per-SID configurations currently defined:" -ForegroundColor Cyan
Get-WmiObject -Namespace $NAMESPACE -Computer $COMPUTER -Class WESL_UserSetting |
    Select-Object Sid, Shell, DefaultAction | Format-Table -AutoSize

# --- Final safety assertion: brain must have NO custom entry ----------------
if ($brainSid) {
    $brainEntry = Get-WmiObject -Namespace $NAMESPACE -Computer $COMPUTER -Class WESL_UserSetting |
                  Where-Object { $_.Sid -eq $brainSid }
    if ($brainEntry) {
        Write-Warning "!!! '$PRIMARY_ACCOUNT' ($brainSid) HAS a Shell Launcher entry: $($brainEntry.Shell)"
        Write-Warning "!!! This script did not create it, but it must be removed before you sign out."
        Write-Warning "!!! Remove it now:  ([wmiclass]'\\localhost\$NAMESPACE`:WESL_UserSetting').RemoveCustomShell('$brainSid')"
    } else {
        Write-Host "Confirmed: '$PRIMARY_ACCOUNT' has NO Shell Launcher entry and will get '$FallbackShell'." -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "=====================================================================" -ForegroundColor Yellow
Write-Host " BEFORE YOU SIGN INTO '$UserName': read provisioning\RECOVERY.md." -ForegroundColor Yellow
Write-Host " Stay signed in as '$PRIMARY_ACCOUNT' in this session. Use" -ForegroundColor Yellow
Write-Host " 'Switch user', not 'Sign out', so you keep a working session to" -ForegroundColor Yellow
Write-Host " run the undo scripts from." -ForegroundColor Yellow
Write-Host "=====================================================================" -ForegroundColor Yellow
Write-Host ""
Write-Host "UNDO for this step: provisioning\93-remove-shell-launcher-config.ps1" -ForegroundColor DarkGray
Write-Host "Record this change in provisioning\MACHINE-CHANGES.md" -ForegroundColor DarkGray
exit 0
