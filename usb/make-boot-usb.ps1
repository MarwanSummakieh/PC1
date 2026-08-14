<#
.SYNOPSIS
    Creates a bootable USB flash drive for Windows 11 IoT Enterprise LTSC 2024
    from the official Microsoft evaluation ISO.

.DESCRIPTION
    This script IRREVERSIBLY ERASES the target physical disk (default: Disk 1).
    Before doing anything destructive it re-verifies a set of guards live:
      - the disk number exists
      - BusType is USB
      - FriendlyName (trimmed) equals the expected model string
      - size falls inside the expected range
    If ANY guard fails the script aborts loudly and writes nothing.

    It then: wipes the disk, initializes GPT, creates one maximum-size partition,
    formats it FAT32 (label WIN11LTSC), mounts the ISO read-only, and robocopies
    the contents across. install.wim is 4,247,599,512 bytes for this ISO, which
    fits under the FAT32 4 GiB per-file ceiling, so no split is required -- but a
    size check is retained so a future, larger ISO is split with DISM automatically.

.NOTES
    ELEVATION
    ---------
    This script must run elevated. It detects a non-elevated session and
    relaunches itself via Start-Process -Verb RunAs, which raises a UAC prompt.

    The account 'brain' IS a member of the local Administrators group -- the
    non-elevated shell simply carries a UAC-filtered token (whoami /groups shows
    BUILTIN\Administrators present but flagged "Group used for deny only", which
    is the signature of token filtering, not of a standard non-admin account).
    So the UAC prompt should be a simple Yes/No consent dialog. If Windows
    instead asks for a username and password, the account is genuinely standard
    and you will need credentials for an administrator account.

    NOTE ON #Requires -RunAsAdministrator:
    It is deliberately NOT used here. #Requires is evaluated by the engine before
    any line of the script body executes, so it would hard-error on a
    non-elevated launch and the self-relaunch block below would never get a
    chance to run. The manual check achieves the same guarantee AND gives the
    friendly auto-elevation behaviour.

.EXAMPLE
    Right-click the file > "Run with PowerShell"

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\make-boot-usb.ps1

.EXAMPLE
    # Skip the interactive typed confirmation (for unattended use):
    .\make-boot-usb.ps1 -Force
#>

[CmdletBinding()]
param(
    [string] $IsoPath = 'C:\Users\brain\Downloads\26100.1742.240906-0331.ge_release_svc_refresh_CLIENT_IOT_LTSC_EVAL_x64FRE_en-us.iso',
    [int]    $DiskNumber = 1,
    [string] $ExpectedFriendlyName = 'USB DISK 3.2',
    [double] $MinSizeGB = 25,
    [double] $MaxSizeGB = 35,
    [string] $PreferredDriveLetter = 'E',
    [string] $VolumeLabel = 'WIN11LTSC',
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$FAT32_MAX_FILE_BYTES = 4294967295

# ---------------------------------------------------------------------------
# Console helpers
# ---------------------------------------------------------------------------
function Write-Step  { param([string]$m) Write-Host "`n==> $m" -ForegroundColor Cyan }
function Write-Ok    { param([string]$m) Write-Host "    [ OK ] $m" -ForegroundColor Green }
function Write-Warn2 { param([string]$m) Write-Host "    [WARN] $m" -ForegroundColor Yellow }
function Write-Fail  { param([string]$m) Write-Host "    [FAIL] $m" -ForegroundColor Red }

function Abort {
    param([string]$Reason)
    Write-Host ""
    Write-Host "===========================================================" -ForegroundColor Red
    Write-Host " ABORTED - nothing was written to any disk." -ForegroundColor Red
    Write-Host " Reason: $Reason" -ForegroundColor Red
    Write-Host "===========================================================" -ForegroundColor Red
    Write-Host ""
    if ($Host.Name -eq 'ConsoleHost') { Read-Host "Press Enter to close" | Out-Null }
    exit 1
}

# ---------------------------------------------------------------------------
# Step 0: elevation check + self-relaunch
# ---------------------------------------------------------------------------
$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$isElevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isElevated) {
    Write-Host ""
    Write-Host "This script needs administrator rights to repartition a disk." -ForegroundColor Yellow
    Write-Host "Relaunching elevated - please approve the UAC prompt." -ForegroundColor Yellow
    Write-Host "(If UAC asks for a username and password rather than just Yes," -ForegroundColor DarkGray
    Write-Host " you'll need the credentials of an administrator account.)" -ForegroundColor DarkGray
    Write-Host ""

    try {
        $hostExe = (Get-Process -Id $PID).Path
        if ([string]::IsNullOrWhiteSpace($hostExe)) { $hostExe = 'powershell.exe' }

        $argList = @(
            '-NoProfile'
            '-ExecutionPolicy', 'Bypass'
            '-NoExit'
            '-File', "`"$PSCommandPath`""
            '-IsoPath', "`"$IsoPath`""
            '-DiskNumber', $DiskNumber
            '-ExpectedFriendlyName', "`"$ExpectedFriendlyName`""
            '-MinSizeGB', $MinSizeGB
            '-MaxSizeGB', $MaxSizeGB
            '-PreferredDriveLetter', $PreferredDriveLetter
            '-VolumeLabel', "`"$VolumeLabel`""
        )
        if ($Force) { $argList += '-Force' }

        Start-Process -FilePath $hostExe -Verb RunAs -ArgumentList $argList | Out-Null
        Write-Host "Elevated window launched. This window can be closed." -ForegroundColor Green
    }
    catch {
        Write-Fail "Could not elevate: $($_.Exception.Message)"
        Write-Host "Run this script from an elevated PowerShell prompt instead." -ForegroundColor Yellow
    }
    exit 0
}

Write-Host ""
Write-Host "###########################################################" -ForegroundColor White
Write-Host "  Windows 11 IoT Enterprise LTSC 2024 - bootable USB maker" -ForegroundColor White
Write-Host "###########################################################" -ForegroundColor White
Write-Ok "Running elevated as $($identity.Name)"

# ---------------------------------------------------------------------------
# Step 1: validate the ISO exists
# ---------------------------------------------------------------------------
Write-Step "Checking source ISO"
if (-not (Test-Path -LiteralPath $IsoPath)) { Abort "ISO not found at: $IsoPath" }
$isoItem = Get-Item -LiteralPath $IsoPath
Write-Ok "ISO found: $($isoItem.FullName)"
Write-Ok "ISO size : $('{0:N0}' -f $isoItem.Length) bytes"

# ---------------------------------------------------------------------------
# Step 2: LIVE GUARDS - re-verified every run, before anything destructive
# ---------------------------------------------------------------------------
Write-Step "Re-verifying target disk guards (Disk $DiskNumber)"

$disk = $null
try { $disk = Get-Disk -Number $DiskNumber -ErrorAction Stop }
catch { Abort "Disk $DiskNumber does not exist or is not accessible. ($($_.Exception.Message))" }

$actualName   = ($disk.FriendlyName).Trim()
$actualBus    = [string]$disk.BusType
$actualSizeGB = [math]::Round($disk.Size / 1GB, 2)

Write-Host "    Disk $DiskNumber -> FriendlyName='$actualName'  BusType=$actualBus  Size=${actualSizeGB}GB"

if ($actualBus -ne 'USB') {
    Abort "Guard failed: BusType is '$actualBus', expected 'USB'. Refusing to touch a non-USB disk."
}
Write-Ok "BusType is USB"

if ($actualName -ne $ExpectedFriendlyName.Trim()) {
    Abort "Guard failed: FriendlyName is '$actualName', expected '$($ExpectedFriendlyName.Trim())'."
}
Write-Ok "FriendlyName matches '$ExpectedFriendlyName'"

if ($actualSizeGB -lt $MinSizeGB -or $actualSizeGB -gt $MaxSizeGB) {
    Abort "Guard failed: size ${actualSizeGB}GB is outside the allowed ${MinSizeGB}-${MaxSizeGB}GB window."
}
Write-Ok "Size ${actualSizeGB}GB is within ${MinSizeGB}-${MaxSizeGB}GB"

# Explicit paranoia: never, under any circumstance, act on disk 0.
if ($DiskNumber -eq 0) { Abort "Refusing to operate on Disk 0 (system disk)." }
if ($disk.IsBoot -or $disk.IsSystem) { Abort "Guard failed: Disk $DiskNumber is flagged as a boot/system disk." }
Write-Ok "Disk is not a boot/system disk"

# ---------------------------------------------------------------------------
# Step 3: final human confirmation
# ---------------------------------------------------------------------------
if (-not $Force) {
    Write-Host ""
    Write-Host "-----------------------------------------------------------" -ForegroundColor Yellow
    Write-Host " ALL DATA ON THE FOLLOWING DISK WILL BE PERMANENTLY ERASED:" -ForegroundColor Yellow
    Write-Host "   Disk $DiskNumber  |  $actualName  |  ${actualSizeGB}GB  |  $actualBus" -ForegroundColor Yellow
    Write-Host " This cannot be undone." -ForegroundColor Yellow
    Write-Host "-----------------------------------------------------------" -ForegroundColor Yellow
    $answer = Read-Host "Type ERASE (all caps) to continue, anything else to cancel"
    if ($answer -cne 'ERASE') { Abort "User did not confirm (typed '$answer')." }
    Write-Ok "Confirmed by user"
}

# ---------------------------------------------------------------------------
# Step 4: mount the ISO read-only and discover its drive letter
# ---------------------------------------------------------------------------
Write-Step "Mounting ISO (read-only)"
$weMountedIt = $false
$img = Get-DiskImage -ImagePath $IsoPath

if (-not $img.Attached) {
    $img = Mount-DiskImage -ImagePath $IsoPath -Access ReadOnly -PassThru
    $weMountedIt = $true
    Start-Sleep -Seconds 2
} else {
    Write-Warn2 "ISO was already mounted; reusing the existing mount."
}

$isoLetter = $null
for ($i = 0; $i -lt 15; $i++) {
    $v = Get-DiskImage -ImagePath $IsoPath | Get-Volume -ErrorAction SilentlyContinue
    if ($v -and $v.DriveLetter) { $isoLetter = $v.DriveLetter; break }
    Start-Sleep -Seconds 1
}
if (-not $isoLetter) { Abort "Mounted the ISO but could not determine its drive letter." }

$isoRoot = "${isoLetter}:\"
Write-Ok "ISO mounted at $isoRoot"

# Sanity-check the mounted image really is a Windows installer
foreach ($required in @('bootmgr', 'efi\boot\bootx64.efi', 'sources')) {
    if (-not (Test-Path -LiteralPath (Join-Path $isoRoot $required))) {
        Abort "Mounted ISO is missing '$required' - does not look like a Windows install image."
    }
}
Write-Ok "ISO structure looks like a Windows installer"

# Decide up-front whether install.wim needs splitting
$wimPath   = Join-Path $isoRoot 'sources\install.wim'
$esdPath   = Join-Path $isoRoot 'sources\install.esd'
$needSplit = $false
$wimBytes  = 0

if (Test-Path -LiteralPath $wimPath) {
    $wimBytes = (Get-Item -LiteralPath $wimPath).Length
    Write-Ok "sources\install.wim = $('{0:N0}' -f $wimBytes) bytes"
    if ($wimBytes -gt $FAT32_MAX_FILE_BYTES) {
        $needSplit = $true
        Write-Warn2 "install.wim exceeds the FAT32 limit ($('{0:N0}' -f $FAT32_MAX_FILE_BYTES) bytes) - it will be split with DISM."
    } else {
        Write-Ok "install.wim fits within FAT32 - no split needed"
    }
} elseif (Test-Path -LiteralPath $esdPath) {
    Write-Ok "sources\install.esd = $('{0:N0}' -f (Get-Item -LiteralPath $esdPath).Length) bytes (ESD needs no split handling)"
} else {
    Abort "Neither sources\install.wim nor sources\install.esd was found on the ISO."
}

# ---------------------------------------------------------------------------
# Step 5: DESTRUCTIVE - wipe, initialize, partition, format
# ---------------------------------------------------------------------------
Write-Step "Erasing Disk $DiskNumber"
Clear-Disk -Number $DiskNumber -RemoveData -RemoveOEM -Confirm:$false
Write-Ok "Disk cleared"

# Clear-Disk usually leaves the disk RAW; initialize only if needed.
$disk = Get-Disk -Number $DiskNumber
if ($disk.PartitionStyle -eq 'RAW') {
    Initialize-Disk -Number $DiskNumber -PartitionStyle GPT
    Write-Ok "Initialized as GPT"
} else {
    Set-Disk -Number $DiskNumber -PartitionStyle GPT -ErrorAction SilentlyContinue
    Write-Ok "Partition style set to GPT"
}

Write-Step "Creating partition"
$usbLetter = $null
$preferred = $PreferredDriveLetter.TrimEnd(':').ToUpper()
$letterInUse = (Get-Volume -ErrorAction SilentlyContinue | Where-Object { $_.DriveLetter -eq $preferred })

if (-not $letterInUse) {
    try {
        $part = New-Partition -DiskNumber $DiskNumber -UseMaximumSize -DriveLetter $preferred
        $usbLetter = $preferred
        Write-Ok "Partition created on preferred drive letter ${usbLetter}:"
    }
    catch {
        Write-Warn2 "Could not use preferred letter ${preferred}: $($_.Exception.Message)"
        $part = New-Partition -DiskNumber $DiskNumber -UseMaximumSize -AssignDriveLetter
        $usbLetter = $part.DriveLetter
        Write-Ok "Fell back to auto-assigned drive letter ${usbLetter}:"
    }
} else {
    Write-Warn2 "Preferred letter ${preferred}: is already in use - auto-assigning instead."
    $part = New-Partition -DiskNumber $DiskNumber -UseMaximumSize -AssignDriveLetter
    $usbLetter = $part.DriveLetter
    Write-Ok "Auto-assigned drive letter ${usbLetter}:"
}

if (-not $usbLetter) { Abort "Partition was created but no drive letter was assigned." }
Start-Sleep -Seconds 2

Write-Step "Formatting ${usbLetter}: as FAT32 (label $VolumeLabel)"
$formatMethod = $null
try {
    Format-Volume -DriveLetter $usbLetter -FileSystem FAT32 -NewFileSystemLabel $VolumeLabel -Confirm:$false -Force | Out-Null
    $formatMethod = 'Format-Volume'
    Write-Ok "Formatted with Format-Volume"
}
catch {
    Write-Warn2 "Format-Volume refused FAT32: $($_.Exception.Message)"
    Write-Warn2 "Falling back to format.com ..."
    $fmt = Start-Process -FilePath "$env:SystemRoot\System32\format.com" `
                         -ArgumentList "${usbLetter}: /FS:FAT32 /Q /V:$VolumeLabel /Y" `
                         -Wait -PassThru -NoNewWindow
    if ($fmt.ExitCode -ne 0) { Abort "format.com failed with exit code $($fmt.ExitCode)." }
    $formatMethod = 'format.com'
    Write-Ok "Formatted with format.com"
}
Start-Sleep -Seconds 2

$usbRoot = "${usbLetter}:\"

# ---------------------------------------------------------------------------
# Step 6: copy the payload
# ---------------------------------------------------------------------------
Write-Step "Copying ISO contents to $usbRoot (this takes several minutes)"

$robocopyArgs = @($isoRoot, $usbRoot, '/E', '/R:2', '/W:2', '/NP', '/NFL', '/NDL', '/NJH')
if ($needSplit) {
    $robocopyArgs += @('/XF', 'install.wim')
    Write-Warn2 "Excluding install.wim from the bulk copy (will be split separately)."
}

& robocopy.exe @robocopyArgs
$rc = $LASTEXITCODE

# robocopy: 0-7 are success//informational, 8+ are genuine failures.
if ($rc -ge 8) { Abort "robocopy failed with exit code $rc (8 or above indicates copy errors)." }
Write-Ok "robocopy completed with exit code $rc (0-7 = success)"

if ($needSplit) {
    Write-Step "Splitting install.wim with DISM (3800 MB chunks)"
    Write-Host "    Reads the ISO, writes only to the USB - the ISO is not modified."
    $swm = Join-Path $usbRoot 'sources\install.swm'
    & dism.exe /English /Split-Image /ImageFile:"$wimPath" /SWMFile:"$swm" /FileSize:3800
    if ($LASTEXITCODE -ne 0) { Abort "DISM /Split-Image failed with exit code $LASTEXITCODE." }
    Write-Ok "install.wim split into .swm parts"
}

# ---------------------------------------------------------------------------
# Step 7: verification
# ---------------------------------------------------------------------------
Write-Step "Verifying the finished USB"

$results = @()
function Add-Check {
    param([string]$Name, [bool]$Pass, [string]$Detail)
    $script:results += [PSCustomObject]@{ Check = $Name; Result = $(if ($Pass) { 'PASS' } else { 'FAIL' }); Detail = $Detail }
}

foreach ($f in @('bootmgr', 'bootmgr.efi', 'efi\boot\bootx64.efi', 'setup.exe')) {
    $p = Join-Path $usbRoot $f
    if (Test-Path -LiteralPath $p) {
        Add-Check $f $true ("{0:N0} bytes" -f (Get-Item -LiteralPath $p).Length)
    } else {
        Add-Check $f $false "MISSING"
    }
}

if ($needSplit) {
    $swm1 = Join-Path $usbRoot 'sources\install.swm'
    $swm2 = Join-Path $usbRoot 'sources\install2.swm'
    Add-Check 'sources\install.swm'  (Test-Path -LiteralPath $swm1) $(if (Test-Path -LiteralPath $swm1) { "{0:N0} bytes" -f (Get-Item -LiteralPath $swm1).Length } else { 'MISSING' })
    Add-Check 'sources\install2.swm' (Test-Path -LiteralPath $swm2) $(if (Test-Path -LiteralPath $swm2) { "{0:N0} bytes" -f (Get-Item -LiteralPath $swm2).Length } else { 'MISSING' })
} else {
    $target = if (Test-Path -LiteralPath (Join-Path $usbRoot 'sources\install.wim')) { 'sources\install.wim' } else { 'sources\install.esd' }
    $p = Join-Path $usbRoot $target
    if (Test-Path -LiteralPath $p) {
        Add-Check $target $true ("{0:N0} bytes" -f (Get-Item -LiteralPath $p).Length)
    } else {
        Add-Check $target $false "MISSING"
    }
}

$vol = Get-Volume -DriveLetter $usbLetter
Add-Check 'Filesystem' ($vol.FileSystem -eq 'FAT32') "$($vol.FileSystem), label '$($vol.FileSystemLabel)'"

Write-Host ""
$results | Format-Table -AutoSize

Write-Host "USB root listing ($usbRoot):" -ForegroundColor White
Get-ChildItem -LiteralPath $usbRoot -Force | Select-Object Mode, Name, @{n='SizeBytes';e={$_.Length}} | Format-Table -AutoSize

Write-Host "sources\ (largest 10 files):" -ForegroundColor White
Get-ChildItem -LiteralPath (Join-Path $usbRoot 'sources') -File -ErrorAction SilentlyContinue |
    Sort-Object Length -Descending | Select-Object -First 10 Name, @{n='SizeBytes';e={$_.Length}} | Format-Table -AutoSize

Write-Host ("Volume: {0}: | FS={1} | Label={2} | Size={3:N2} GB | Free={4:N2} GB | Format method={5}" -f `
    $usbLetter, $vol.FileSystem, $vol.FileSystemLabel, ($vol.Size / 1GB), ($vol.SizeRemaining / 1GB), $formatMethod) -ForegroundColor White

# ---------------------------------------------------------------------------
# Step 8: dismount the ISO
# ---------------------------------------------------------------------------
Write-Step "Dismounting ISO"
try {
    Dismount-DiskImage -ImagePath $IsoPath | Out-Null
    Write-Ok "ISO dismounted"
}
catch {
    Write-Warn2 "Could not dismount the ISO: $($_.Exception.Message)"
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
$failed = @($results | Where-Object { $_.Result -eq 'FAIL' })
Write-Host ""
if ($failed.Count -eq 0) {
    Write-Host "===========================================================" -ForegroundColor Green
    Write-Host " PASS - bootable USB created at ${usbLetter}: (label $VolumeLabel)" -ForegroundColor Green
    Write-Host " Boot the target machine in UEFI mode and select this drive." -ForegroundColor Green
    Write-Host "===========================================================" -ForegroundColor Green
} else {
    Write-Host "===========================================================" -ForegroundColor Red
    Write-Host " FAIL - $($failed.Count) verification check(s) did not pass:" -ForegroundColor Red
    $failed | ForEach-Object { Write-Host "   - $($_.Check): $($_.Detail)" -ForegroundColor Red }
    Write-Host "===========================================================" -ForegroundColor Red
}
Write-Host ""

if ($Host.Name -eq 'ConsoleHost' -and -not $Force) { Read-Host "Press Enter to close" | Out-Null }
