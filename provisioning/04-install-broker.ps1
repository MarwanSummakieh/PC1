<#
.SYNOPSIS
    Installs the MarwanOS broker: a SYSTEM scheduled task that performs a small
    allowlist of privileged VERBS on demand, with no UAC prompt.

.DESCRIPTION
    Step 4 of the PC1 provisioning sequence. Independent of steps 1-3 (Shell
    Launcher); it can be applied and removed on its own.

    THE PROBLEM
    Installing a machine-scope application requires an administrator token.
    Windows raises that request as a UAC consent dialog on the secure desktop,
    which by design discards synthetic input - so no gamepad remapper, Steam
    Input profile, or button layout can ever answer it. On a controller-only
    living-room machine the prompt is unanswerable, not merely inconvenient.

    THE APPROACH
    Grant the consent ONCE, here, at provisioning time, with a keyboard present.
    A scheduled task registered by an administrator with RunLevel Highest already
    carries an elevated token. A standard user who triggers that task gets no
    consent dialog, because consent was given at registration. UAC stays fully
    enabled machine-wide; nothing about the security posture of any other
    application changes.

    WHAT THIS DELIBERATELY DOES NOT DO
    The task does not run a command supplied by the caller. It runs a fixed
    worker script, which accepts one of exactly four verbs:

        install          package=<id>      id must be in packages.json
        updates.install  (no arguments)
        wifi.forget      ssid=<1..32 ch>
        bt.forget        address=<12 hex>

    NO VERB TAKES A PATH, A URL, A COMMAND LINE OR AN ARGUMENT LIST. A broker
    that ran caller-supplied paths would be a permanent SYSTEM backdoor for every
    process running as marwanshell - which is precisely the standard,
    non-administrative account the shell runs as.

    LAYOUT CREATED
        C:\ProgramData\MarwanOS\                 SYSTEM+Admins full, runners read
          packages.json                     the only authority; admin-writable only
          marwan-install-worker.ps1            the privileged half
          queue\                            runners may create request files here
          processed\                        results + <ticket>.progress, readable by runners
          cache\                            downloaded installers
          logs\install-broker.log           every download, check and decision

    Idempotent: re-running repairs ACLs, refreshes the worker, and updates the
    task in place. An existing packages.json is preserved unless -ReplaceManifest.

.PARAMETER AllowRunAs
    Accounts permitted to trigger the task. Default: marwanshell. These accounts get
    read access to the broker directory and write access to queue\ only.

.PARAMETER ReplaceManifest
    Overwrite a deployed packages.json with the copy from this repo. Off by
    default so that re-running never silently drops packages you added by hand.

.PARAMETER WhatIfOnly
    Run every check and print the exact changes, without applying any of them.

.UNDO
    provisioning\94-remove-install-broker.ps1

.NOTES
    RUN AS: elevated PowerShell (Run as administrator). Windows PowerShell 5.1
    or PowerShell 7 both work - this script uses the Schedule.Service COM API,
    not the [wmiclass] accelerator that scripts 03/93 depend on.

    THIS SCRIPT CHANGES MACHINE STATE. Do not run without explicit go-ahead.
    Record the run in provisioning\MACHINE-CHANGES.md.
#>
[CmdletBinding()]
param(
    [string[]]$AllowRunAs = @('marwanshell'),
    [string]$Root = 'C:\ProgramData\MarwanOS',
    [switch]$ReplaceManifest,
    [switch]$WhatIfOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$TaskFolder = 'MarwanOS'
$TaskName   = 'marwan-install-broker'
$TaskFull   = "\$TaskFolder\$TaskName"

$SourceDir  = Join-Path $PSScriptRoot 'install-broker'
$SrcWorker  = Join-Path $SourceDir 'marwan-install-worker.ps1'
$SrcAllow   = Join-Path $SourceDir 'packages.json'

$QueueDir     = Join-Path $Root 'queue'
$ProcessedDir = Join-Path $Root 'processed'
$LogDir       = Join-Path $Root 'logs'
$CacheDir     = Join-Path $Root 'cache'
$DstWorker    = Join-Path $Root 'marwan-install-worker.ps1'
$DstAllow     = Join-Path $Root 'packages.json'

Write-Host "=== 04-install-broker.ps1 ===" -ForegroundColor Cyan
Write-Host "OS: $((Get-CimInstance Win32_OperatingSystem).Caption) build $([Environment]::OSVersion.Version)"
Write-Host ""

# --- Guard: must be elevated -------------------------------------------------
$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script must be run from an ELEVATED PowerShell prompt (Run as administrator)."
    exit 1
}

# --- Guard: source files present --------------------------------------------
foreach ($f in @($SrcWorker, $SrcAllow)) {
    if (-not (Test-Path $f)) {
        Write-Error "Missing source file: $f"
        exit 2
    }
}

# --- Guard: resolve every runner account, and refuse administrators ----------
# Granting an administrator "permission to trigger the broker" is meaningless -
# they can already elevate. Granting one by mistake, on the other hand, hides the
# fact that the allowlist is doing no work. Fail closed on anything unresolvable.
$adminMembers = @()
try {
    $adminMembers = @(Get-LocalGroupMember -Group 'Administrators' | ForEach-Object { $_.Name })
}
catch {
    Write-Error "Could not enumerate the local Administrators group: $($_.Exception.Message). Failing closed."
    exit 3
}

$runners = @()
foreach ($name in $AllowRunAs) {
    try {
        $sidObj = (New-Object Security.Principal.NTAccount($name)).Translate([Security.Principal.SecurityIdentifier])
        $sid    = $sidObj.Value
    }
    catch {
        Write-Error "Cannot resolve account '$name' to a SID: $($_.Exception.Message)"
        exit 3
    }
    $qualified = "$env:COMPUTERNAME\$name"
    if ($adminMembers -contains $qualified -or $adminMembers -contains $name) {
        Write-Error "'$name' is a member of Administrators. Refusing to configure the broker for an administrator account - it gains nothing and obscures the allowlist boundary."
        exit 3
    }
    # SidObj matters: FileSystemAccessRule's string overload expects an account
    # NAME, so handing it a SID string fails to translate. Pass the object.
    $runners += [pscustomobject]@{ Name = $name; Qualified = $qualified; Sid = $sid; SidObj = $sidObj }
    Write-Host ("  runner: {0,-24} {1}" -f $qualified, $sid)
}
Write-Host ""

# --- Report the plan ---------------------------------------------------------
Write-Host "Will apply:" -ForegroundColor Yellow
Write-Host "  1. Create $Root (+ queue, processed, cache, logs)"
Write-Host "  2. Set explicit ACLs: SYSTEM/Administrators full; runners read; runners write to queue\ only"
Write-Host "  3. Copy marwan-install-worker.ps1 -> $DstWorker"
Write-Host ("  4. {0} packages.json -> {1}" -f $(if ((Test-Path $DstAllow) -and -not $ReplaceManifest) { 'PRESERVE existing' } else { 'Copy' }), $DstAllow)
Write-Host "  5. Register scheduled task $TaskFull as SYSTEM, RunLevel Highest, no trigger (on-demand)"
Write-Host "  6. Grant runners read+execute on the task itself, so they can trigger it"
Write-Host ""

if ($WhatIfOnly) {
    Write-Host "-WhatIfOnly specified. No changes made." -ForegroundColor Yellow
    exit 0
}

# --- 1. Directories ----------------------------------------------------------
foreach ($d in @($Root, $QueueDir, $ProcessedDir, $CacheDir, $LogDir)) {
    if (-not (Test-Path $d)) {
        New-Item -ItemType Directory -Path $d -Force | Out-Null
        Write-Host "  created $d"
    }
}

# --- 2. ACLs -----------------------------------------------------------------
# ProgramData's default inherited ACL lets Users create files anywhere beneath
# it. That is exactly what must not be true of the directory holding the
# allowlist, so inheritance is switched off and the ACL rebuilt explicitly.
$CI_OI = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
$NONE  = [Security.AccessControl.PropagationFlags]::None

$acl = Get-Acl -Path $Root
$acl.SetAccessRuleProtection($true, $false)   # protected, drop inherited rules
foreach ($rule in @($acl.Access)) { [void]$acl.RemoveAccessRule($rule) }

$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
    'NT AUTHORITY\SYSTEM', 'FullControl', $CI_OI, $NONE, 'Allow')))
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
    'BUILTIN\Administrators', 'FullControl', $CI_OI, $NONE, 'Allow')))
foreach ($r in $runners) {
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        $r.SidObj, 'ReadAndExecute', $CI_OI, $NONE, 'Allow')))
}
Set-Acl -Path $Root -AclObject $acl
Write-Host "  ACL set on $Root (inheritance disabled)"

# queue\ is the one place a runner may write. Write only - not Modify, so a
# runner cannot delete or overwrite another request, and not Execute.
$qacl = Get-Acl -Path $QueueDir
foreach ($r in $runners) {
    $qacl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        $r.SidObj, 'Write', $CI_OI, $NONE, 'Allow')))
}
Set-Acl -Path $QueueDir -AclObject $qacl
Write-Host "  ACL set on $QueueDir (runners may create request files)"

# --- 3/4. Payload ------------------------------------------------------------
Copy-Item -Path $SrcWorker -Destination $DstWorker -Force
Write-Host "  copied worker -> $DstWorker"

if ((Test-Path $DstAllow) -and -not $ReplaceManifest) {
    Write-Host "  manifest already present; left untouched (use -ReplaceManifest to overwrite)" -ForegroundColor DarkGray
}
else {
    Copy-Item -Path $SrcAllow -Destination $DstAllow -Force
    Write-Host "  copied manifest -> $DstAllow"
}

# --- 5. Register the task ----------------------------------------------------
$svc = New-Object -ComObject Schedule.Service
$svc.Connect()

$rootFolder = $svc.GetFolder('\')
try {
    $folder = $svc.GetFolder("\$TaskFolder")
}
catch {
    $folder = $rootFolder.CreateFolder($TaskFolder)
    Write-Host "  created task folder \$TaskFolder"
}

$def = $svc.NewTask(0)
$def.RegistrationInfo.Author      = 'PC1 provisioning\04-install-broker.ps1'
$def.RegistrationInfo.Description = 'MarwanOS broker. Performs an allowlisted privileged verb on demand (install / updates.install / wifi.forget / bt.forget), so the controller-only shell never faces a UAC prompt. Manifest: ' + $DstAllow

$def.Principal.UserId    = 'SYSTEM'
$def.Principal.LogonType = 5    # TASK_LOGON_SERVICE_ACCOUNT
$def.Principal.RunLevel  = 1    # TASK_RUNLEVEL_HIGHEST

$def.Settings.Enabled                    = $true
$def.Settings.Hidden                     = $false
$def.Settings.AllowDemandStart           = $true
$def.Settings.StartWhenAvailable         = $false
$def.Settings.DisallowStartIfOnBatteries = $false
$def.Settings.StopIfGoingOnBatteries     = $false
$def.Settings.MultipleInstances          = 1     # TASK_INSTANCES_QUEUE
$def.Settings.ExecutionTimeLimit         = 'PT2H'

$action = $def.Actions.Create(0)  # TASK_ACTION_EXEC
$action.Path      = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
$action.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$DstWorker`" -Root `"$Root`""

# 6 = TASK_CREATE_OR_UPDATE, logon type 5 = service account
$registered = $folder.RegisterTaskDefinition($TaskName, $def, 6, 'SYSTEM', $null, 5)
Write-Host "  registered $TaskFull (SYSTEM, RunLevel Highest, on-demand only)"

# --- 6. Let the runners trigger it ------------------------------------------
# Without this the task exists but marwanshell cannot start it: by default only
# administrators and SYSTEM hold execute rights on a SYSTEM-registered task.
#   GA = full, GR = read, GX = execute (i.e. "may run this task")
$aces = ($runners | ForEach-Object { "(A;;GRGX;;;$($_.Sid))" }) -join ''
$sddl = "D:P(A;;GA;;;SY)(A;;GA;;;BA)$aces"
try {
    $registered.SetSecurityDescriptor($sddl, 0)
    Write-Host "  task ACL set: runners may read and run, not modify"
}
catch {
    Write-Warning "Could not set the task security descriptor: $($_.Exception.Message)"
    Write-Warning "The task is registered, but $($runners.Name -join ', ') may not be able to trigger it. Verify with the post-check below."
}

# --- Post-check --------------------------------------------------------------
Write-Host ""
Write-Host "Post-check:" -ForegroundColor Cyan
$check = $folder.GetTask($TaskName)
Write-Host ("  task            : {0}" -f $check.Path)
Write-Host ("  enabled         : {0}" -f $check.Enabled)
Write-Host ("  principal       : {0} (runlevel {1})" -f $check.Definition.Principal.UserId, $check.Definition.Principal.RunLevel)
Write-Host ("  action          : {0} {1}" -f $check.Definition.Actions.Item(1).Path, $check.Definition.Actions.Item(1).Arguments)
Write-Host ("  worker deployed : {0}" -f (Test-Path $DstWorker))
Write-Host ("  manifest        : {0}" -f $DstAllow)
try {
    $mf = Get-Content $DstAllow -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($p in @($mf.packages)) {
        Write-Host ("                    {0,-18} {1}" -f $p.id, $p.name)
    }
}
catch {
    Write-Warning "Deployed manifest is not valid JSON: $($_.Exception.Message)"
}
Write-Host ("  task SD         : {0}" -f $check.GetSecurityDescriptor(4))   # DACL_SECURITY_INFORMATION

Write-Host ""
Write-Host "Verify end-to-end as the runner account (NOT elevated):" -ForegroundColor Green
Write-Host "  powershell -NoProfile -ExecutionPolicy Bypass -File `"$SourceDir\marwan-install.ps1`" -PackageId steam"
Write-Host ""
Write-Host "Nothing is proven until that command has been run from an marwanshell session and observed." -ForegroundColor Yellow
Write-Host ""
Write-Host "UNDO for this step: provisioning\94-remove-install-broker.ps1" -ForegroundColor DarkGray
Write-Host "Record this change in provisioning\MACHINE-CHANGES.md" -ForegroundColor DarkGray
exit 0
