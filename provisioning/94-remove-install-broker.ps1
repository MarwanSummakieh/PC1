<#
.SYNOPSIS
    Removes the ARC install broker. Undo for provisioning\04-install-broker.ps1.

.DESCRIPTION
    Unregisters the \ARC\arc-install-broker scheduled task and removes the \ARC
    task folder if it is left empty. This is the part that matters: with the task
    gone, the elevated path is gone, and the console shell can no longer cause an
    install without a human at the machine.

    The state directory C:\ProgramData\ARC is LEFT IN PLACE by default, because
    it holds the install log and the manifest. Pass -RemoveData to delete it too.

    Idempotent: removing a broker that is not installed reports that and exits 0.

.PARAMETER RemoveData
    Also delete the broker state directory, including logs\install-broker.log,
    cached installers, and any packages.json edits made since provisioning.
    Destructive; asks for typed confirmation unless -Force.

.PARAMETER Force
    Skip the typed confirmation for -RemoveData.

.PARAMETER WhatIfOnly
    Print what would be removed, change nothing.

.NOTES
    RUN AS: elevated PowerShell (Run as administrator).
    Record the run in provisioning\MACHINE-CHANGES.md.
#>
[CmdletBinding()]
param(
    [string]$Root = 'C:\ProgramData\ARC',
    [switch]$RemoveData,
    [switch]$Force,
    [switch]$WhatIfOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$TaskFolder = 'ARC'
$TaskName   = 'arc-install-broker'
$TaskFull   = "\$TaskFolder\$TaskName"

Write-Host "=== 94-remove-install-broker.ps1 ===" -ForegroundColor Cyan
Write-Host ""

# --- Guard: must be elevated -------------------------------------------------
$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script must be run from an ELEVATED PowerShell prompt (Run as administrator)."
    exit 1
}

$svc = New-Object -ComObject Schedule.Service
$svc.Connect()

# --- Pre-check ---------------------------------------------------------------
$folder = $null
$task   = $null
try {
    $folder = $svc.GetFolder("\$TaskFolder")
    try { $task = $folder.GetTask($TaskName) } catch { $task = $null }
}
catch {
    $folder = $null
}

Write-Host ("  task $TaskFull : {0}" -f $(if ($task) { 'present' } else { 'not present' }))
Write-Host ("  state dir $Root : {0}" -f $(if (Test-Path $Root) { 'present' } else { 'not present' }))
Write-Host ""

if (-not $task -and -not (Test-Path $Root)) {
    Write-Host "Broker is not installed. Nothing to do." -ForegroundColor Green
    exit 0
}

Write-Host "Will remove:" -ForegroundColor Yellow
if ($task) { Write-Host "  1. Scheduled task $TaskFull" }
if ($folder) { Write-Host "  2. Task folder \$TaskFolder (only if empty afterwards)" }
if ($RemoveData -and (Test-Path $Root)) {
    Write-Host "  3. State directory $Root  <-- INCLUDING LOGS AND ALLOWLIST" -ForegroundColor Red
}
elseif (Test-Path $Root) {
    Write-Host "  -  State directory $Root will be LEFT IN PLACE (pass -RemoveData to delete)" -ForegroundColor DarkGray
}
Write-Host ""

if ($WhatIfOnly) {
    Write-Host "-WhatIfOnly specified. No changes made." -ForegroundColor Yellow
    exit 0
}

# --- 1. Stop and unregister the task ----------------------------------------
if ($task) {
    try {
        if ($task.State -eq 4) {   # TASK_STATE_RUNNING
            Write-Host "  task is running; stopping it first."
            $task.Stop(0)
            Start-Sleep -Seconds 2
        }
    }
    catch {
        Write-Warning "Could not query/stop the running task: $($_.Exception.Message)"
    }

    $folder.DeleteTask($TaskName, 0)
    Write-Host "  unregistered $TaskFull" -ForegroundColor Green
}

# --- 2. Remove the task folder if empty -------------------------------------
if ($folder) {
    try {
        $remainingTasks   = @($folder.GetTasks(1))     # 1 = include hidden
        $remainingFolders = @($folder.GetFolders(0))
        if ($remainingTasks.Count -eq 0 -and $remainingFolders.Count -eq 0) {
            $svc.GetFolder('\').DeleteFolder($TaskFolder, 0)
            Write-Host "  removed empty task folder \$TaskFolder" -ForegroundColor Green
        }
        else {
            Write-Host "  task folder \$TaskFolder still holds $($remainingTasks.Count) task(s); left in place." -ForegroundColor DarkGray
        }
    }
    catch {
        Write-Warning "Could not tidy the task folder: $($_.Exception.Message)"
    }
}

# --- 3. Optionally remove state ---------------------------------------------
if ($RemoveData -and (Test-Path $Root)) {
    if (-not $Force) {
        Write-Host ""
        Write-Host "This permanently deletes $Root, including install-broker.log" -ForegroundColor Red
        $typed = Read-Host "Type the word DELETE to confirm"
        if ($typed -ne 'DELETE') {
            Write-Host "Not confirmed. State directory left in place." -ForegroundColor Yellow
            Write-Host ""
            Write-Host "Task removal above still applies. Done." -ForegroundColor Green
            exit 0
        }
    }
    Remove-Item -Path $Root -Recurse -Force
    Write-Host "  removed $Root" -ForegroundColor Green
}

Write-Host ""
Write-Host "Done. The elevated install path no longer exists." -ForegroundColor Green
Write-Host "Record this in provisioning\MACHINE-CHANGES.md" -ForegroundColor DarkGray
exit 0
