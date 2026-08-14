<#
.SYNOPSIS
    Creates the throwaway local test account 'arcshell' (standard user, NOT an admin).

.DESCRIPTION
    Step 2 of 3 in the PC1 shell-replacement provisioning sequence.

    This account is the ONLY account that will ever have its shell replaced.
    The primary account 'brain' is never touched by anything in provisioning\.

    What this script does:
      * Pre-checks for an existing local user with the same name; exits if found.
      * Generates a random 20-character password using a cryptographic RNG.
      * Creates the user with New-LocalUser -PasswordNeverExpires.
      * Adds the user to the local 'Users' group ONLY. Never Administrators.
      * Prints the password ONCE to the console for the operator to record.

    THE PASSWORD IS PRINTED ONCE AND NEVER STORED. Write it down before closing
    the window. If lost, delete the account with 92-remove-test-account.ps1 and
    re-run this script.

.DOCS
    New-LocalUser        : https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.localaccounts/new-localuser
    Add-LocalGroupMember : https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.localaccounts/add-localgroupmember
    Shell Launcher requires the local account to exist BEFORE it is configured:
      https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/configuration-file

.UNDO
    provisioning\92-remove-test-account.ps1

.NOTES
    UNVERIFIED ON THIS MACHINE (Windows 11 IoT Enterprise LTSC 2024):
      * Whether a local password-complexity / minimum-length policy is in force.
        The generated password is 20 chars with all four character classes, which
        satisfies the Windows default complexity rule, but a custom local security
        policy could still reject it. If New-LocalUser fails with a password error,
        that is why.
      * Whether the machine has a policy hiding local accounts from the sign-in
        screen (some IoT/kiosk images do). If 'arcshell' does not appear on the
        logon screen, use "Other user" and type .\arcshell.
      * The user profile is NOT created until the first interactive sign-in.
        92-remove-test-account.ps1 handles both the profile-exists and
        profile-never-created cases.

    RUN AS: elevated Windows PowerShell 5.1.
    THIS SCRIPT CHANGES MACHINE STATE. Do not run without the user's explicit go-ahead.
#>

[CmdletBinding()]
param(
    # Name of the throwaway account. Do NOT point this at a real account.
    [ValidateNotNullOrEmpty()]
    [string]$UserName = 'arcshell',

    [string]$FullName = 'PC1 Shell Launcher test account',

    # New-LocalUser rejects descriptions longer than 48 characters.
    [string]$Description = 'THROWAWAY PC1 shell test account. Safe to delete',

    [switch]$WhatIfOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# --- Hard guard: never operate on the primary account -----------------------
# This is a belt-and-braces check. Even though -UserName defaults to arcshell,
# a typo must not be able to clobber the operator's real account.
$ProtectedNames = @('brain', 'Administrator', 'DefaultAccount', 'Guest', 'WDAGUtilityAccount')
if ($ProtectedNames -contains $UserName) {
    Write-Error "REFUSING: '$UserName' is a protected account name. This script only creates disposable test accounts."
    exit 1
}

$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script must be run from an ELEVATED PowerShell prompt (Run as administrator)."
    exit 1
}

Write-Host "=== 02-create-test-account.ps1 ===" -ForegroundColor Cyan
Write-Host "Target account : $UserName"
Write-Host ""

# --- Pre-check: does the account already exist? ------------------------------
$existing = Get-LocalUser -Name $UserName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Account '$UserName' ALREADY EXISTS. Nothing to do." -ForegroundColor Green
    Write-Host "  SID                 : $($existing.SID.Value)"
    Write-Host "  Enabled             : $($existing.Enabled)"
    Write-Host "  PasswordNeverExpires: $($existing.PasswordNeverExpires)"

    # Report group membership so the operator can confirm it is not an admin.
    $adminMembers = @()
    try {
        $adminMembers = Get-LocalGroupMember -Group 'Administrators' -ErrorAction Stop |
                        ForEach-Object { $_.SID.Value }
    } catch {
        Write-Warning "Could not enumerate Administrators via Get-LocalGroupMember: $($_.Exception.Message)"
    }
    if ($adminMembers -contains $existing.SID.Value) {
        Write-Warning "!! '$UserName' IS A MEMBER OF Administrators. That is NOT what this project wants."
        Write-Warning "   Remove it before step 03:  Remove-LocalGroupMember -Group Administrators -Member $UserName"
    } else {
        Write-Host "  Administrators      : NOT a member (correct)"
    }

    Write-Host ""
    Write-Host "If you need a fresh password, run 92-remove-test-account.ps1 and then re-run this script."
    Write-Host "Next step: provisioning\03-apply-shell-launcher.ps1"
    exit 0
}

if ($WhatIfOnly) {
    Write-Host "-WhatIfOnly specified. Would create standard user '$UserName' in group 'Users'. No changes made." -ForegroundColor Yellow
    exit 0
}

# --- Generate a random password ---------------------------------------------
# Uses RNGCryptoServiceProvider (available in Windows PowerShell 5.1) rather than
# Get-Random, which is not cryptographically secure.
# Character set deliberately excludes ambiguous glyphs (0/O, 1/l/I) so the
# operator can transcribe it by hand without errors, and excludes quote/backtick
# characters so it can be pasted into a shell safely.
function New-RandomPassword {
    param([int]$Length = 20)

    $lower  = 'abcdefghijkmnopqrstuvwxyz'      # no 'l'
    $upper  = 'ABCDEFGHJKLMNPQRSTUVWXYZ'       # no 'I', no 'O'
    $digit  = '23456789'                        # no '0', no '1'
    $symbol = '!#%*+-=?@_'                      # shell-safe subset
    $all    = $lower + $upper + $digit + $symbol

    $rng   = [System.Security.Cryptography.RNGCryptoServiceProvider]::new()
    $bytes = [byte[]]::new(4)

    function Get-SecureIndex([int]$max) {
        # Rejection sampling to avoid modulo bias.
        do {
            $rng.GetBytes($bytes)
            $value = [BitConverter]::ToUInt32($bytes, 0)
        } while ($value -ge ([uint32]::MaxValue - ([uint32]::MaxValue % $max)))
        return [int]($value % $max)
    }

    # Guarantee at least one character from each class (Windows complexity rule).
    $chars = New-Object System.Collections.Generic.List[char]
    $chars.Add($lower[(Get-SecureIndex $lower.Length)])
    $chars.Add($upper[(Get-SecureIndex $upper.Length)])
    $chars.Add($digit[(Get-SecureIndex $digit.Length)])
    $chars.Add($symbol[(Get-SecureIndex $symbol.Length)])
    while ($chars.Count -lt $Length) {
        $chars.Add($all[(Get-SecureIndex $all.Length)])
    }

    # Fisher-Yates shuffle so the guaranteed classes are not always in positions 0-3.
    for ($i = $chars.Count - 1; $i -gt 0; $i--) {
        $j = Get-SecureIndex ($i + 1)
        $tmp = $chars[$i]; $chars[$i] = $chars[$j]; $chars[$j] = $tmp
    }

    $rng.Dispose()
    return -join $chars
}

$plainPassword  = New-RandomPassword -Length 20
$securePassword = ConvertTo-SecureString -String $plainPassword -AsPlainText -Force

# --- Create the account ------------------------------------------------------
# -PasswordNeverExpires : disables password expiry, as required.
# NOTE: New-LocalUser does NOT add the account to any group. Interactive logon
#       comes from membership in the local 'Users' group, which we add below.
#       No other group is added. Never Administrators.
$newUser = New-LocalUser -Name $UserName `
                         -Password $securePassword `
                         -FullName $FullName `
                         -Description $Description `
                         -PasswordNeverExpires `
                         -AccountNeverExpires

Write-Host "Created local user '$UserName'." -ForegroundColor Green
Write-Host "  SID : $($newUser.SID.Value)"

# --- Group membership: Users only -------------------------------------------
try {
    Add-LocalGroupMember -Group 'Users' -Member $UserName -ErrorAction Stop
    Write-Host "  Added to local group 'Users'." -ForegroundColor Green
}
catch {
    if ($_.Exception.Message -match 'already a member') {
        Write-Host "  Already a member of 'Users'."
    } else {
        throw
    }
}

# --- Verification: prove it is NOT an administrator --------------------------
$isAdmin = $false
try {
    $isAdmin = (Get-LocalGroupMember -Group 'Administrators' -ErrorAction Stop |
                ForEach-Object { $_.SID.Value }) -contains $newUser.SID.Value
} catch {
    Write-Warning "Could not verify Administrators membership: $($_.Exception.Message)"
}
if ($isAdmin) {
    Write-Error "UNEXPECTED: '$UserName' ended up in Administrators. Investigate before running step 03."
    exit 3
}
Write-Host "  Verified: NOT a member of Administrators." -ForegroundColor Green

# --- Print the password ONCE -------------------------------------------------
Write-Host ""
Write-Host "=====================================================================" -ForegroundColor Yellow
Write-Host " RECORD THIS PASSWORD NOW. IT IS SHOWN ONCE AND IS NOT STORED." -ForegroundColor Yellow
Write-Host "=====================================================================" -ForegroundColor Yellow
Write-Host ""
Write-Host "   User     : .\$UserName"
Write-Host "   Password : $plainPassword"
Write-Host ""
Write-Host "=====================================================================" -ForegroundColor Yellow
Write-Host ""
Write-Host "Sign in with 'Other user' on the logon screen and type: .\$UserName"

# Best-effort scrub of the plaintext from the variable. This does not scrub the
# console scrollback or the PSReadLine history of this session -- close the
# window when you are done.
$plainPassword = $null
[GC]::Collect()

Write-Host ""
Write-Host "Next step: provisioning\03-apply-shell-launcher.ps1" -ForegroundColor Cyan
Write-Host "UNDO for this step: provisioning\92-remove-test-account.ps1" -ForegroundColor DarkGray
Write-Host "Record this change in provisioning\MACHINE-CHANGES.md" -ForegroundColor DarkGray
exit 0
