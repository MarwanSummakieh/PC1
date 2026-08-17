<#
.SYNOPSIS
    Elevated action worker for the MarwanOS broker. Drains the request queue,
    performing one allowlisted VERB per request. Exits when the queue is empty.

.DESCRIPTION
    This script is the PRIVILEGED half of the broker. It is launched only by the
    scheduled task \MarwanOS\marwan-install-broker, which runs as SYSTEM. It is never
    launched directly by the shell.

    THE SECURITY BOUNDARY

        A standard user (marwanshell) can cause this script to perform ONE of a
        FIXED, SMALL SET OF VERBS, with arguments drawn from a constrained
        grammar, and CANNOT cause it to do anything else.

    The verb allowlist is exactly:

        install          package=<id>      id must be present in packages.json
        updates.install  (no arguments)    Windows Update: search, download, install
        wifi.forget      ssid=<1..32 ch>   netsh wlan delete profile
        bt.forget        address=<12 hex>  BluetoothRemoveDevice

    Anything else is REJECTED without being executed.

    NO VERB TAKES A PATH, A URL, A COMMAND LINE, A HASH, OR AN ARGUMENT LIST.
    That is the property that makes the broker safe to expose to a standard user.
    `install` names a manifest slug; everything actually downloaded or executed
    comes from the matching packages.json entry, which lives in a directory
    standard users cannot write to (and the worker refuses to run if that stops
    being true). `wifi.forget` and `bt.forget` take an identifier that is
    validated against a strict pattern and passed to a FIXED program as an
    argument ARRAY - never concatenated into a command string. `updates.install`
    takes nothing at all.

    Adding a verb that accepts a caller-supplied path, url or command line would
    turn this into a permanent SYSTEM backdoor for every process running as
    marwanshell. Don't.

    Provenance for `install` is pinned by AUTHENTICODE PUBLISHER, not by content
    hash. A hash pin breaks the moment a vendor ships a new build, which in
    practice means it gets disabled and the pin becomes decoration. A publisher
    pin survives version bumps and still proves the bytes came from who the
    manifest says. A manifest entry may additionally pin sha256 where the
    installer genuinely never changes; both checks are enforced when both are
    present.

    NO WINGET DEPENDENCY. This image (Windows 11 IoT Enterprise LTSC Evaluation)
    ships neither winget nor the Microsoft Store. Direct download is the primary
    path; winget is used only when the manifest names a wingetId AND winget
    actually resolves, so the same manifest works on machines that have it.

.PARAMETER Root
    Broker state directory. Standard users must not be able to write to it, except
    the queue\ subdirectory. Created and ACL'd by provisioning\04-install-broker.ps1.

.PARAMETER WhatIfOnly
    Do everything except the irreversible step:
      install          still downloads and still verifies the signature, does not run it
      updates.install  searches only, reports the count, downloads nothing
      wifi.forget      validates only, does not call netsh
      bt.forget        validates only, does not call BluetoothRemoveDevice

.NOTES
    RUN AS:      SYSTEM, via the scheduled task. Refuses to run unelevated.
    LOG:         <Root>\logs\install-broker.log
    PROGRESS:    <Root>\processed\<ticket>.progress   (one line per event, tailed by the host)
    RESULT:      <Root>\processed\<ticket>.result
    POWERSHELL:  Windows PowerShell 5.1
#>
[CmdletBinding()]
param(
    [string]$Root = 'C:\ProgramData\MarwanOS',
    [switch]$WhatIfOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$QueueDir     = Join-Path $Root 'queue'
$ProcessedDir = Join-Path $Root 'processed'
$LogDir       = Join-Path $Root 'logs'
$CacheDir     = Join-Path $Root 'cache'
$ManifestPath = Join-Path $Root 'packages.json'
$LogPath      = Join-Path $LogDir 'install-broker.log'

foreach ($d in @($QueueDir, $ProcessedDir, $LogDir, $CacheDir)) {
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
}

# Ticket currently being processed. While this is set, every log line is also
# mirrored into processed\<ticket>.progress, which the host tails for its UI.
$script:CurrentTicket = $null

# Append one line, retrying briefly on a sharing violation.
# The host (and the CLI client) TAIL the progress file while the worker is
# writing it. A reader that opens without FileShare.Write makes a single
# Add-Content throw, and a progress line that is silently dropped is a progress
# line the UI never shows - observed during bring-up. Retry instead of swallowing.
function Add-LineSafe {
    param([string]$Path, [string]$Line)
    for ($i = 0; $i -lt 8; $i++) {
        try { Add-Content -Path $Path -Value $Line -Encoding UTF8; return }
        catch { Start-Sleep -Milliseconds 40 }
    }
}

function Write-Log {
    param([string]$Message, [string]$Level = 'INFO')
    $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    $line  = '{0} [{1,-5}] {2}' -f $stamp, $Level, $Message
    Add-LineSafe -Path $LogPath -Line $line
    if ($script:CurrentTicket) {
        # Append-only, one line per event: "<yyyy-MM-dd HH:mm:ss> <text>".
        Add-LineSafe -Path (Join-Path $ProcessedDir "$($script:CurrentTicket).progress") `
                     -Line ('{0} {1}' -f $stamp, ($Message -replace '[\r\n]+', ' '))
    }
    Write-Host $line
}

# --- Guard: must be elevated -------------------------------------------------
$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$wp        = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $wp.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Log "Refusing to run: not elevated (running as $($identity.Name))." 'ERROR'
    exit 1
}

Write-Log "Worker start (identity=$($identity.Name), root=$Root, whatIfOnly=$([bool]$WhatIfOnly))."

# --- Guard: the manifest must not be writable by non-administrators ----------
# If a standard user can edit packages.json, the manifest is not a control at
# all - they could point an entry at their own binary. Fail closed.
function Get-UntrustedWriter {
    param([string]$Path)
    $trusted = @(
        'NT AUTHORITY\SYSTEM',
        'BUILTIN\Administrators',
        'NT SERVICE\TrustedInstaller',
        'CREATOR OWNER'
    )
    # Test ATOMIC write bits only. Do not put Write/Modify/FullControl in this
    # mask: they are composite values that also contain the read bits, so
    # `ReadAndExecute -band FullControl` is non-zero and every read-only ACE
    # would look like a write ACE. Modify and FullControl are still caught here,
    # because both contain WriteData.
    $writeRights = [Security.AccessControl.FileSystemRights]::WriteData -bor              # 2
                   [Security.AccessControl.FileSystemRights]::AppendData -bor             # 4
                   [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
                   [Security.AccessControl.FileSystemRights]::Delete -bor
                   [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
                   [Security.AccessControl.FileSystemRights]::TakeOwnership
    foreach ($ace in (Get-Acl -Path $Path).Access) {
        if ($ace.AccessControlType -ne 'Allow') { continue }
        if (($ace.FileSystemRights -band $writeRights) -eq 0) { continue }
        if ($trusted -notcontains $ace.IdentityReference.Value) {
            return $ace.IdentityReference.Value
        }
    }
    return $null
}

if (-not (Test-Path $ManifestPath)) {
    Write-Log "Manifest not found at $ManifestPath. Refusing to run." 'ERROR'
    exit 2
}

$offender = Get-UntrustedWriter -Path $ManifestPath
if ($offender) {
    Write-Log "REFUSING TO RUN: '$offender' can write $ManifestPath. The manifest is not trustworthy. Re-run provisioning\04-install-broker.ps1 to repair ACLs." 'ERROR'
    exit 3
}

# --- Load and validate the manifest -----------------------------------------
try {
    $manifest = Get-Content -Path $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
}
catch {
    Write-Log "Manifest is not valid JSON: $($_.Exception.Message)" 'ERROR'
    exit 2
}

$Packages = @{}
foreach ($p in @($manifest.packages)) {
    if (-not $p.id) { Write-Log "Manifest entry with no id; skipped." 'WARN'; continue }

    $hasUrl    = [bool]($p.PSObject.Properties.Name -contains 'url' -and $p.url)
    $hasWinget = [bool]($p.PSObject.Properties.Name -contains 'wingetId' -and $p.wingetId)
    $hasPath   = [bool]($p.PSObject.Properties.Name -contains 'path' -and $p.path)

    if (-not $hasUrl -and -not $hasWinget -and -not $hasPath) {
        Write-Log "Manifest entry '$($p.id)' has none of url / path / wingetId; skipped." 'WARN'
        continue
    }
    # `path` = an installer that is ALREADY on this machine, put there by something else
    # (the case: Riot Client downloads the Vanguard setup into a ProgramData folder and
    # then wants to run it elevated). Absolute path, may end in a *.exe glob (newest match
    # wins). Publisher pin is mandatory - the folder is typically user-writable, so the
    # signature is the only thing separating "the vendor's setup" from "a file somebody
    # dropped there". Args still come from this manifest, never from the caller.
    if ($hasPath) {
        if ($p.path -notmatch '^[A-Za-z]:\\' -or $p.path -match '\.\.') {
            Write-Log "Manifest entry '$($p.id)' path must be absolute and free of '..'; skipped." 'WARN'
            continue
        }
        if (-not ($p.PSObject.Properties.Name -contains 'publisher' -and $p.publisher)) {
            Write-Log "Manifest entry '$($p.id)' has a path but no publisher pin; skipped." 'WARN'
            continue
        }
    }
    # An https url without a publisher pin is an unverified download. Refuse the
    # entry rather than quietly install whatever answers that hostname today.
    if ($hasUrl) {
        if ($p.url -notmatch '^https://') {
            Write-Log "Manifest entry '$($p.id)' has a non-https url; skipped." 'WARN'
            continue
        }
        if (-not ($p.PSObject.Properties.Name -contains 'publisher' -and $p.publisher)) {
            Write-Log "Manifest entry '$($p.id)' has a url but no publisher pin; skipped." 'WARN'
            continue
        }
    }
    $Packages[$p.id.ToLowerInvariant()] = $p
}
Write-Log "Manifest loaded: $($Packages.Count) usable package(s) [$(($Packages.Values | ForEach-Object { $_.id }) -join ', ')]."

# An empty manifest is no longer fatal: it only means every `install` request
# will be REJECTED as 'not in manifest'. The other verbs do not consult it.
if ($Packages.Count -eq 0) {
    Write-Log "No usable manifest entries. 'install' requests will be rejected; other verbs are unaffected." 'WARN'
}

# --- Optional winget backend -------------------------------------------------
function Resolve-Winget {
    $pkg = Get-ChildItem -Path 'C:\Program Files\WindowsApps' `
                         -Filter 'Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe' `
                         -Directory -ErrorAction SilentlyContinue |
           Sort-Object Name -Descending | Select-Object -First 1
    if ($pkg) {
        $exe = Join-Path $pkg.FullName 'winget.exe'
        if (Test-Path $exe) { return $exe }
    }
    $cmd = Get-Command winget.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}
$Winget = Resolve-Winget
Write-Log ("winget backend : {0}" -f $(if ($Winget) { $Winget } else { 'not present (direct download only)' }))

# TLS 1.2+ - older defaults still bite on LTSC images.
try {
    [Net.ServicePointManager]::SecurityProtocol =
        [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls11
}
catch { Write-Log "Could not raise TLS version: $($_.Exception.Message)" 'WARN' }

# --- Helpers -----------------------------------------------------------------
function Write-Result {
    param(
        [string]$Ticket,
        [string]$Verb = '',
        [string]$Status,           # OK | REJECTED | FAILED
        [int]$ExitCode = -1,
        [string]$PackageId = '',
        [string]$Detail = '',
        $Installed = $null,        # updates.install only
        $RebootRequired = $null    # updates.install only
    )
    $body = @(
        "ticket=$Ticket"
        "verb=$Verb"
        "status=$Status"
        "exitcode=$ExitCode"
        "package=$PackageId"
        "detail=$($Detail -replace '[\r\n]+', ' ')"
    )
    if ($null -ne $Installed)      { $body += "installed=$Installed" }
    if ($null -ne $RebootRequired) { $body += ("rebootRequired={0}" -f $(if ($RebootRequired) { 'true' } else { 'false' })) }
    $body += "completed=$(Get-Date -Format 'o')"
    Set-Content -Path (Join-Path $ProcessedDir "$Ticket.result") -Value ($body -join "`r`n") -Encoding UTF8
}

# Printable-only, length-capped rendering of an attacker-supplied string, for logs.
function Format-Safe {
    param([string]$Text, [int]$Max = 80)
    if ($null -eq $Text) { return '' }
    $clean = ($Text -replace '[^\x20-\x7E]', '?')
    if ($clean.Length -gt $Max) { $clean = $clean.Substring(0, $Max) + '...' }
    return $clean
}

# Returns $null on success, or a string describing why the file is not acceptable.
function Test-Provenance {
    param([string]$File, $Entry)

    $sig = Get-AuthenticodeSignature -FilePath $File
    if ($sig.Status -ne 'Valid') {
        return "Authenticode status is '$($sig.Status)', expected Valid"
    }
    if (-not $sig.SignerCertificate) {
        return "signature carries no signer certificate"
    }
    $subject = $sig.SignerCertificate.Subject
    if ($subject -notlike "*$($Entry.publisher)*") {
        return "signer '$subject' does not match pinned publisher '$($Entry.publisher)'"
    }

    if ($Entry.PSObject.Properties.Name -contains 'sha256' -and $Entry.sha256) {
        $actual = (Get-FileHash -Path $File -Algorithm SHA256).Hash
        if ($actual -ne $Entry.sha256.ToUpperInvariant()) {
            return "sha256 mismatch: got $actual, manifest pins $($Entry.sha256)"
        }
    }
    return $null
}

# Start a process in the active console session, on the interactive desktop, under one of two
# tokens - used only for manifest entries marked interactive:
#
#   "user-admin"  the CONSOLE USER's own linked administrator token (WTSQueryUserToken for the
#                 console session, then TokenLinkedToken - the full token UAC keeps behind a
#                 signed-in admin's filtered one). Optionally with its integrity label lowered
#                 from High to Medium. Preferred whenever the console user has one.
#   "system"      THIS process's token (SYSTEM) moved into the console session - what
#                 `psexec -s -i` does. The fallback when the console user is a standard user.
#
# Why the user's token, and why Medium: the shell's pointer mode drives foreign windows with
# SendInput, and SendInput is subject to UIPI - it only reaches windows at the caller's
# integrity level or lower. The shell runs at Medium (a signed-in admin's desktop is the
# FILTERED token, Medium; ConsentPromptBehaviorAdmin=0 does not change that). A SYSTEM-owned
# installer (System integrity) swallows every injected move and click - measured on the
# bench 2026-08-16, BENCH-CHANGES B29 - and so would a High one. An admin token whose label
# has been lowered to Medium keeps everything an installer needs (Administrators enabled,
# TokenElevation=1, SeLoadDriver/SeDebug/... present, CreateProcess of a requireAdministrator
# image succeeds - all measured on the bench, B30) and sits at the integrity the pad can reach.
#
# Lowering to Medium is done ONLY where the box already elevates this user silently
# (ConsentPromptBehaviorAdmin=0): there, Medium->High is not a boundary for the console user
# any more, so a Medium-reachable admin window gives a Medium process nothing it could not
# already take. Where UAC still prompts, the token is left at High - the pointer cannot drive
# it, but nothing has been weakened either. The SYSTEM fallback keeps System integrity for the
# same reason: a standard-user shell must not be able to click through a SYSTEM UI.
#
# Every interactive process goes into a JOB OBJECT (no limits, breakaway allowed so nothing the
# installer spawns is broken by it). Membership is how postKill later tells "left behind by
# this installer" from "the player's own copy of the same program" now that both run as the
# same user - see Install-FromUrl. Needs SeTcbPrivilege for the session move and for a
# PRIMARY linked token; SYSTEM holds it.
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.ComponentModel;
namespace MarwanBroker {
  public class LaunchInfo { public int Pid; public IntPtr Job; public string Mode; public string User; public string Shape; }
  public static class Console1 {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO { public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
      public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2;
      public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_PRIVILEGES { public int PrivilegeCount; public LUID Luid; public int Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    public struct SID_AND_ATTRIBUTES { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_BASIC_LIMIT_INFORMATION { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION { public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint ResumeThread(IntPtr hThread);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attrs, string name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int cls, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int len);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
    [DllImport("wtsapi32.dll", SetLastError = true)] static extern bool WTSQueryUserToken(uint sid, out IntPtr tok);
    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool WTSQuerySessionInformation(IntPtr server, uint sid, int cls, out IntPtr buf, out int len);
    [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr buf);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr tok);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr tok, uint access, IntPtr attrs, int impersonationLevel, int tokenType, out IntPtr newTok);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr tok, int cls, IntPtr info, int len, out int ret);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetTokenInformation(IntPtr tok, int cls, ref uint info, int len);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetTokenInformation(IntPtr tok, int cls, IntPtr info, int len);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool LookupPrivilegeValue(string sys, string name, out LUID luid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr tok, bool disableAll, ref TOKEN_PRIVILEGES np, int len, IntPtr prev, IntPtr ret);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool ConvertStringSidToSid(string s, out IntPtr sid);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool CreateProcessAsUser(IntPtr tok, string app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("userenv.dll", SetLastError = true)] static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr tok, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] static extern bool DestroyEnvironmentBlock(IntPtr env);
    [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthority(IntPtr sid, int i);
    [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr h);

    const int TokenSessionId = 12, TokenElevationType = 18, TokenLinkedToken = 19, TokenElevation = 20, TokenIntegrityLevel = 25;
    const uint TOKEN_ALL_ACCESS = 0x000F01FF, TOKEN_ADJUST_PRIVILEGES = 0x0020, TOKEN_QUERY = 0x0008;

    public static uint ConsoleSession() { return WTSGetActiveConsoleSessionId(); }

    public static string ConsoleUser(uint sid) {
      IntPtr buf; int len; string name = "";
      if (WTSQuerySessionInformation(IntPtr.Zero, sid, 5 /*WTSUserName*/, out buf, out len)) { name = Marshal.PtrToStringUni(buf); WTSFreeMemory(buf); }
      return name ?? "";
    }

    static int Dword(IntPtr tok, int cls) { IntPtr b = Marshal.AllocHGlobal(4); int n; int v = -1; if (GetTokenInformation(tok, cls, b, 4, out n)) v = Marshal.ReadInt32(b); Marshal.FreeHGlobal(b); return v; }

    // "IL=0x2000 elevated=1 elevType=2 session=1" - what a token IS, for the log.
    public static string Shape(IntPtr tok) {
      int n; GetTokenInformation(tok, TokenIntegrityLevel, IntPtr.Zero, 0, out n);
      IntPtr b = Marshal.AllocHGlobal(n); string il = "?";
      if (GetTokenInformation(tok, TokenIntegrityLevel, b, n, out n)) { IntPtr sid = Marshal.ReadIntPtr(b); int c = Marshal.ReadByte(GetSidSubAuthorityCount(sid)); il = "0x" + Marshal.ReadInt32(GetSidSubAuthority(sid, c - 1)).ToString("x"); }
      Marshal.FreeHGlobal(b);
      return "IL=" + il + " elevated=" + Dword(tok, TokenElevation) + " elevType=" + Dword(tok, TokenElevationType) + " session=" + Dword(tok, TokenSessionId);
    }

    static void EnableTcb() {
      IntPtr tok; if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out tok)) return;
      LUID luid; TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
      if (LookupPrivilegeValue(null, "SeTcbPrivilege", out luid)) { tp.PrivilegeCount = 1; tp.Luid = luid; tp.Attributes = 2; AdjustTokenPrivileges(tok, false, ref tp, Marshal.SizeOf(tp), IntPtr.Zero, IntPtr.Zero); }
      CloseHandle(tok);
    }

    // The console user's ADMIN token as a primary token, or IntPtr.Zero when they have none
    // (a standard user, or nobody signed in). `why` says which.
    public static IntPtr UserAdminToken(uint sid, out string why) {
      why = ""; IntPtr ut;
      if (!WTSQueryUserToken(sid, out ut)) { why = "WTSQueryUserToken failed (err " + Marshal.GetLastWin32Error() + ": nobody signed in on the console?)"; return IntPtr.Zero; }
      IntPtr full = IntPtr.Zero;
      int et = Dword(ut, TokenElevationType);
      if (et == 3 /*Limited*/) {
        // Filtered admin: the full token is linked. With SeTcbPrivilege this comes back as a
        // PRIMARY token (identification-level otherwise), which is what CreateProcessAsUser wants.
        IntPtr b = Marshal.AllocHGlobal(IntPtr.Size); int n;
        if (GetTokenInformation(ut, TokenLinkedToken, b, IntPtr.Size, out n)) full = Marshal.ReadIntPtr(b);
        else why = "TokenLinkedToken failed (err " + Marshal.GetLastWin32Error() + ")";
        Marshal.FreeHGlobal(b);
      }
      else if (Dword(ut, TokenElevation) == 1) { full = ut; ut = IntPtr.Zero; why = "the session token itself is elevated (elevType " + et + ")"; }
      else why = "console user is not an administrator (elevType " + et + ", elevated 0)";
      if (ut != IntPtr.Zero) CloseHandle(ut);
      if (full == IntPtr.Zero) return IntPtr.Zero;
      IntPtr dup;
      if (!DuplicateTokenEx(full, TOKEN_ALL_ACCESS, IntPtr.Zero, 2, 1, out dup)) { why = "DuplicateTokenEx failed (err " + Marshal.GetLastWin32Error() + ")"; CloseHandle(full); return IntPtr.Zero; }
      CloseHandle(full);
      return dup;
    }

    static IntPtr SystemToken() {
      IntPtr tok, dup;
      if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ALL_ACCESS, out tok)) throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken");
      if (!DuplicateTokenEx(tok, TOKEN_ALL_ACCESS, IntPtr.Zero, 2, 1, out dup)) { int e = Marshal.GetLastWin32Error(); CloseHandle(tok); throw new Win32Exception(e, "DuplicateTokenEx"); }
      CloseHandle(tok);
      return dup;
    }

    static void SetIntegrity(IntPtr tok, string levelSid) {
      IntPtr sid; if (!ConvertStringSidToSid(levelSid, out sid)) throw new Win32Exception(Marshal.GetLastWin32Error(), "ConvertStringSidToSid");
      SID_AND_ATTRIBUTES sa = new SID_AND_ATTRIBUTES(); sa.Sid = sid; sa.Attributes = 0x20 /*SE_GROUP_INTEGRITY*/;
      IntPtr b = Marshal.AllocHGlobal(Marshal.SizeOf(sa)); Marshal.StructureToPtr(sa, b, false);
      bool ok = SetTokenInformation(tok, TokenIntegrityLevel, b, Marshal.SizeOf(sa)); int err = Marshal.GetLastWin32Error();
      Marshal.FreeHGlobal(b); LocalFree(sid);
      if (!ok) throw new Win32Exception(err, "SetTokenInformation(TokenIntegrityLevel)");
    }

    // mode: "user-admin" (console user's admin token) or "system". mediumIntegrity applies to
    // user-admin only. Throws if the requested mode cannot be honoured - the caller decides
    // whether to fall back, so that the log says which token actually ran the installer.
    public static LaunchInfo Launch(string exe, string args, string dir, string mode, bool mediumIntegrity) {
      uint sid = WTSGetActiveConsoleSessionId();
      if (sid == 0xFFFFFFFF) throw new InvalidOperationException("no active console session");
      EnableTcb();   // session move + primary linked token both need it
      LaunchInfo info = new LaunchInfo(); info.Mode = mode; info.User = ConsoleUser(sid);
      IntPtr tok;
      if (mode == "user-admin") {
        string why; tok = UserAdminToken(sid, out why);
        if (tok == IntPtr.Zero) throw new InvalidOperationException("no admin token for the console user: " + why);
        if (mediumIntegrity) SetIntegrity(tok, "S-1-16-8192");
      }
      else if (mode == "system") { tok = SystemToken(); info.User = "SYSTEM"; }
      else throw new ArgumentException("mode must be user-admin or system");
      if (!SetTokenInformation(tok, TokenSessionId, ref sid, 4)) { int e = Marshal.GetLastWin32Error(); CloseHandle(tok); throw new Win32Exception(e, "SetTokenInformation(TokenSessionId=" + sid + ")"); }
      info.Shape = Shape(tok);

      IntPtr job = CreateJobObject(IntPtr.Zero, null);
      if (job != IntPtr.Zero) {
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION li = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        // BREAKAWAY_OK only: a child that asks to leave the job may (nothing is broken by the
        // job's existence); nothing leaves it silently, and no limit is imposed. NOT
        // KILL_ON_JOB_CLOSE - the worker exiting must never take the player's installer down.
        li.BasicLimitInformation.LimitFlags = 0x00000800;
        SetInformationJobObject(job, 9 /*ExtendedLimitInformation*/, ref li, Marshal.SizeOf(li));
      }

      IntPtr env; if (!CreateEnvironmentBlock(out env, tok, false)) env = IntPtr.Zero;
      STARTUPINFO si = new STARTUPINFO(); si.cb = Marshal.SizeOf(si); si.lpDesktop = "winsta0\\default";
      PROCESS_INFORMATION pi;
      string cmd = "\"" + exe + "\"" + (string.IsNullOrEmpty(args) ? "" : " " + args);
      // CREATE_SUSPENDED so the job is joined before the first instruction runs.
      bool ok = CreateProcessAsUser(tok, exe, cmd, IntPtr.Zero, IntPtr.Zero, false, 0x00000400 | 0x00000010 | 0x00000004, env, dir, ref si, out pi);
      int err = Marshal.GetLastWin32Error();
      if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
      CloseHandle(tok);
      if (!ok) { if (job != IntPtr.Zero) CloseHandle(job); throw new Win32Exception(err, "CreateProcessAsUser(" + mode + ") in session " + sid); }
      if (job != IntPtr.Zero && !AssignProcessToJobObject(job, pi.hProcess)) { CloseHandle(job); job = IntPtr.Zero; }
      ResumeThread(pi.hThread);
      CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
      info.Pid = pi.dwProcessId; info.Job = job;
      return info;
    }

    // Was that pid started by the installer we launched (it or any descendant)? Nested jobs
    // count as membership; a process that broke away does not.
    public static bool InJob(IntPtr job, int pid) {
      if (job == IntPtr.Zero) return false;
      IntPtr h = OpenProcess(0x1000 /*QUERY_LIMITED_INFORMATION*/, false, pid);
      if (h == IntPtr.Zero) return false;
      bool r; bool ok = IsProcessInJob(h, job, out r);
      CloseHandle(h);
      return ok && r;
    }
    public static void CloseJob(IntPtr job) { if (job != IntPtr.Zero) CloseHandle(job); }
  }
}
"@ -ErrorAction Stop

# Job handle of the interactive installer being waited on, for the postKill logic below.
$script:InteractiveJob = [IntPtr]::Zero

# Does this box elevate administrators without a prompt? That is the condition under which the
# installer's admin token may be lowered to Medium (see the comment above Console1).
function Test-SilentAdminElevation {
    try {
        $v = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name ConsentPromptBehaviorAdmin -ErrorAction Stop
        return ([int]$v.ConsentPromptBehaviorAdmin -eq 0)
    } catch { return $false }
}

function Start-InConsoleSession {
    param([string]$Exe, [string[]]$Args)
    $quoted = @()
    foreach ($a in @($Args)) { if ($a -match '[\s"]') { $quoted += ('"' + ($a -replace '"', '\"') + '"') } else { $quoted += $a } }
    $argLine = $quoted -join ' '
    $dir = [IO.Path]::GetDirectoryName($Exe)
    $sid = [MarwanBroker.Console1]::ConsoleSession()
    $user = [MarwanBroker.Console1]::ConsoleUser($sid)
    Write-Log "  console session is $sid (user '$user')"

    $silent = Test-SilentAdminElevation
    try {
        $info = [MarwanBroker.Console1]::Launch($Exe, $argLine, $dir, 'user-admin', $silent)
        Write-Log ("  started under the console user's ADMIN token: {0} [{1}]{2}" -f $info.User, $info.Shape,
            $(if ($silent) { ' - integrity lowered to Medium (UAC elevates admins silently here), reachable by the shell pointer' }
              else         { ' - left at High (UAC still prompts on this box), the shell pointer cannot drive it' }))
    }
    catch {
        Write-Log "  console user has no usable admin token ($($_.Exception.Message)) - falling back to SYSTEM in the console session"
        $info = [MarwanBroker.Console1]::Launch($Exe, $argLine, $dir, 'system', $false)
        Write-Log "  started as SYSTEM in session $sid [$($info.Shape)] - a mouse can drive it, the shell pointer cannot (UIPI)"
    }
    $script:InteractiveJob = $info.Job
    if ($info.Job -eq [IntPtr]::Zero) { Write-Log "  (no job object for this launch - leftovers will be matched by owner only)" 'WARN' }
    return $info.Pid
}

# Is a running process a leftover of the interactive installer we started? Either it is in the
# installer's job (the user-token launch, or a SYSTEM launch), or - the shape before jobs
# existed, and the shape after a worker restart lost the handle - it is SYSTEM-owned. The
# player's own copy of the same program (their filtered token, not in our job) is neither.
function Test-InstallerLeftover {
    param($CimProcess)
    if ([MarwanBroker.Console1]::InJob($script:InteractiveJob, [int]$CimProcess.ProcessId)) { return $true }
    $o = $null; try { $o = Invoke-CimMethod -InputObject $CimProcess -MethodName GetOwner -ErrorAction Stop } catch { }
    return [bool]($o -and $o.User -eq 'SYSTEM')
}

function Install-FromUrl {
    param($Entry, [string]$Ticket)

    $hasPath = [bool]($Entry.PSObject.Properties.Name -contains 'path' -and $Entry.path)
    if ($hasPath) {
        # Local installer. Resolve a glob to the newest match; refuse anything that is not a
        # plain file. Copy it into our cache first so the bytes we verify are the bytes we run
        # (the source folder is user-writable; a swap between check and run must not be possible).
        $leafPat = [IO.Path]::GetFileName($Entry.path)
        $dir     = [IO.Path]::GetDirectoryName($Entry.path)
        $cands   = @(Get-ChildItem -Path $dir -Filter $leafPat -File -ErrorAction SilentlyContinue |
                     Where-Object { $_.Extension -in '.exe', '.msi' } | Sort-Object LastWriteTimeUtc -Descending)
        if ($cands.Count -eq 0) { throw "no installer matches $($Entry.path) - it has not been downloaded yet" }
        $src  = $cands[0].FullName
        $file = Join-Path $CacheDir ("{0}_{1}" -f $Entry.id, $cands[0].Name)
        Write-Log "Ticket ${Ticket}: local installer $src ($([math]::Round($cands[0].Length/1MB,1)) MB)"
        Copy-Item -Path $src -Destination $file -Force
    }
    else {
        $leaf = [IO.Path]::GetFileName(([Uri]$Entry.url).AbsolutePath)
        if (-not $leaf) { $leaf = "$($Entry.id).installer" }
        $file = Join-Path $CacheDir ("{0}_{1}" -f $Entry.id, $leaf)

        Write-Log "Ticket ${Ticket}: downloading $($Entry.url)"
        if (Test-Path $file) { Remove-Item -Path $file -Force -ErrorAction SilentlyContinue }
        Invoke-WebRequest -Uri $Entry.url -OutFile $file -UseBasicParsing -TimeoutSec 600

        $size = (Get-Item $file).Length
        Write-Log "Ticket ${Ticket}: downloaded $([math]::Round($size/1MB,1)) MB -> $file"
    }

    $bad = Test-Provenance -File $file -Entry $Entry
    if ($bad) {
        Remove-Item -Path $file -Force -ErrorAction SilentlyContinue
        throw "provenance check failed: $bad"
    }
    Write-Log "Ticket ${Ticket}: provenance OK (publisher pin '$($Entry.publisher)')"

    if ($WhatIfOnly) {
        Write-Log "Ticket ${Ticket}: -WhatIfOnly, not executing the installer."
        return 0
    }

    $type = if ($Entry.PSObject.Properties.Name -contains 'type' -and $Entry.type) { $Entry.type } else { 'exe' }
    $argv = @()
    if ($Entry.PSObject.Properties.Name -contains 'args' -and $Entry.args) { $argv = @($Entry.args) }

    # Wait for the INSTALLER PROCESS ONLY - never `Start-Process -Wait`, which waits for the
    # whole descendant tree. The Riot installer honours --disable-auto-launch about as well
    # as its name suggests: it exits, but leaves the Riot Client running (as SYSTEM, in
    # session 0, on an invisible login page), and -Wait then never returned. The ticket sat
    # "Running" for ever with the install already done. WaitForExit() on the process object
    # returns when the installer itself exits, which is the only thing its exit code speaks for.
    $interactive = [bool]($Entry.PSObject.Properties.Name -contains 'interactive' -and $Entry.interactive)
    if ($interactive -and $type -ne 'msi') {
        # The installer needs a human AND an elevated token at the same time - Riot's does:
        # its UI logs the player in and installs the game, and its "elevated agent" (which
        # only lives while the installer runs elevated) is what installs Vanguard without a
        # UAC prompt. So the verified file is started INSIDE the console session, on the
        # interactive desktop, where the player can see it and use it - under the console
        # user's own admin token when they have one (at the desktop's integrity, so the
        # shell's pad-pointer can drive it), as SYSTEM otherwise. It is what a UAC "Yes"
        # would have produced, minus the dialog nobody can answer. See Start-InConsoleSession.
        Write-Log "Ticket ${Ticket}: INTERACTIVE - starting in the console session: $file $($argv -join ' ')"
        $ipid = Start-InConsoleSession -Exe $file -Args $argv
        Write-Log "Ticket ${Ticket}: interactive installer running as pid $ipid - waiting for the player to finish with it"
        $proc = Get-Process -Id $ipid -ErrorAction Stop
    }
    elseif ($type -eq 'msi') {
        $msiArgs = @('/i', "`"$file`"", '/qn', '/norestart') + $argv
        Write-Log "Ticket ${Ticket}: msiexec $($msiArgs -join ' ')"
        $proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList $msiArgs -PassThru
    }
    else {
        Write-Log "Ticket ${Ticket}: $file $($argv -join ' ')"
        if ($argv.Count -gt 0) {
            $proc = Start-Process -FilePath $file -ArgumentList $argv -PassThru
        }
        else {
            $proc = Start-Process -FilePath $file -PassThru
        }
    }
    $proc.WaitForExit()
    $code = $proc.ExitCode
    Write-Log "Ticket ${Ticket}: installer process exited $code"

    # Interactive installers hand off to a UI the player is still using (Riot's installer
    # exits within seconds and leaves its client on the login page). Do not pull the rug:
    # wait until every postKill process that came out of OUR launch is gone - i.e. the
    # player closed it - before the ticket is judged. Ceiling 6 h, then leftovers are killed
    # like any other run. "Came out of our launch" = in the installer's job object, or
    # SYSTEM-owned (Test-InstallerLeftover); the player's own copy of the same program,
    # started from the shell under their filtered token, is neither and is left alone.
    if ($interactive -and $Entry.PSObject.Properties.Name -contains 'postKill' -and $Entry.postKill) {
        $names = @($Entry.postKill | Where-Object { $_ -match '^[A-Za-z0-9 ._-]{1,64}$' })
        $ceiling = (Get-Date).AddHours(6)
        $lastNote = Get-Date
        Write-Log "Ticket ${Ticket}: interactive - waiting for the player to finish and close [$($names -join ', ')]"
        while ((Get-Date) -lt $ceiling) {
            $alive = 0
            foreach ($pn in $names) {
                foreach ($p in @(Get-CimInstance Win32_Process -Filter "Name = '$pn.exe'" -ErrorAction SilentlyContinue)) {
                    if (Test-InstallerLeftover -CimProcess $p) { $alive++ }
                }
            }
            if ($alive -eq 0) { Write-Log "Ticket ${Ticket}: interactive session finished (nothing left running)."; break }
            if (((Get-Date) - $lastNote).TotalSeconds -ge 60) { $lastNote = Get-Date; Write-Log "Ticket ${Ticket}: still open in the console session ($alive process(es)) - waiting" }
            Start-Sleep -Seconds 5
        }
    }

    # Some installers leave a launcher/agent running under the token we gave them - as
    # SYSTEM in session 0 it is invisible and useless, in the console session it is a game
    # client with an admin token sitting in the player's desktop, and either way it keeps
    # files locked. The MANIFEST may name process names to stop afterwards (`postKill`).
    # Names only, matched exactly, and ONLY processes that came out of our launch are touched
    # (Test-InstallerLeftover) - the player's own copy of the same program is never killed.
    if ($Entry.PSObject.Properties.Name -contains 'postKill' -and $Entry.postKill) {
        foreach ($pn in @($Entry.postKill)) {
            if ($pn -notmatch '^[A-Za-z0-9 ._-]{1,64}$') { Write-Log "Ticket ${Ticket}: postKill name '$pn' refused (bad chars)." 'WARN'; continue }
            $victims = @(Get-CimInstance Win32_Process -Filter "Name = '$pn.exe'" -ErrorAction SilentlyContinue | Where-Object { Test-InstallerLeftover -CimProcess $_ })
            foreach ($v in $victims) {
                Write-Log "Ticket ${Ticket}: postKill '$($v.Name)' pid $($v.ProcessId) session $($v.SessionId) (left by the installer)"
                try { Stop-Process -Id $v.ProcessId -Force -ErrorAction Stop } catch { Write-Log "Ticket ${Ticket}: postKill pid $($v.ProcessId) failed: $($_.Exception.Message)" 'WARN' }
            }
        }
    }
    if ($script:InteractiveJob -ne [IntPtr]::Zero) { [MarwanBroker.Console1]::CloseJob($script:InteractiveJob); $script:InteractiveJob = [IntPtr]::Zero }
    return $code
}

function Install-FromWinget {
    param($Entry, [string]$Ticket)

    $wargs = @(
        'install', '--id', $Entry.wingetId, '--exact', '--silent',
        '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity'
    )
    Write-Log "Ticket ${Ticket}: winget $($wargs -join ' ')"
    if ($WhatIfOnly) {
        Write-Log "Ticket ${Ticket}: -WhatIfOnly, not invoking winget."
        return 0
    }
    $out = & $Winget @wargs 2>&1
    foreach ($l in $out) { Write-Log "  | $l" }
    return $LASTEXITCODE
}

# --- Verb: install -----------------------------------------------------------
function Invoke-VerbInstall {
    param([string]$Ticket, [hashtable]$Req)

    $requested = ''
    if ($Req.ContainsKey('package')) { $requested = $Req['package'] }

    if ($requested -notmatch $IdPattern) {
        Write-Log "Ticket ${Ticket}: REJECTED - malformed id '$(Format-Safe $requested)'." 'WARN'
        Write-Result -Ticket $Ticket -Verb 'install' -Status 'REJECTED' -Detail 'malformed id'
        return
    }

    $key = $requested.ToLowerInvariant()
    if (-not $Packages.ContainsKey($key)) {
        Write-Log "Ticket ${Ticket}: REJECTED - '$requested' is not in the manifest." 'WARN'
        Write-Result -Ticket $Ticket -Verb 'install' -Status 'REJECTED' -PackageId $requested -Detail 'not in manifest'
        return
    }

    # From here on use the MANIFEST's entry, never the requester's string.
    $entry = $Packages[$key]
    Write-Log "Ticket ${Ticket}: installing '$($entry.name)' ($($entry.id))."

    $code   = -1
    $failed = $null
    try {
        $useWinget = $Winget -and ($entry.PSObject.Properties.Name -contains 'wingetId') -and $entry.wingetId
        $hasLocal  = ($entry.PSObject.Properties.Name -contains 'path') -and $entry.path
        $isInter   = ($entry.PSObject.Properties.Name -contains 'interactive') -and $entry.interactive
        if ($useWinget -and -not $hasLocal -and -not $isInter) { $code = Install-FromWinget -Entry $entry -Ticket $Ticket }
        else                                                   { $code = Install-FromUrl    -Entry $entry -Ticket $Ticket }
    }
    catch {
        $failed = $_.Exception.Message
        Write-Log "Ticket ${Ticket}: $failed" 'ERROR'
    }

    if ($failed) {
        Write-Result -Ticket $Ticket -Verb 'install' -Status 'FAILED' -ExitCode $code -PackageId $entry.id -Detail $failed
        return
    }

    # An installer's exit code is a claim, not evidence. When the manifest names
    # a verifyPath, that file existing is what decides the outcome.
    if ($entry.PSObject.Properties.Name -contains 'verifyPath' -and $entry.verifyPath) {
        if (Test-Path $entry.verifyPath) {
            Write-Log "Ticket ${Ticket}: verified $($entry.verifyPath) exists."
            Write-Result -Ticket $Ticket -Verb 'install' -Status 'OK' -ExitCode $code -PackageId $entry.id -Detail "verified $($entry.verifyPath)"
            return
        }
        if ($WhatIfOnly) {
            Write-Result -Ticket $Ticket -Verb 'install' -Status 'OK' -ExitCode 0 -PackageId $entry.id -Detail 'whatif: downloaded and verified, not installed'
            return
        }
        Write-Log "Ticket ${Ticket}: FAILED - installer exit $code but $($entry.verifyPath) is missing." 'ERROR'
        Write-Result -Ticket $Ticket -Verb 'install' -Status 'FAILED' -ExitCode $code -PackageId $entry.id -Detail "verifyPath missing after install (exit $code)"
        return
    }

    # winget: -1978335189 = already installed. MSI/exe: 3010 = success, reboot required.
    if ($code -eq 0 -or $code -eq 3010 -or $code -eq -1978335189) {
        $detail = switch ($code) {
            3010          { 'installed; reboot required' }
            -1978335189   { 'already installed' }
            default       { if ($WhatIfOnly) { 'whatif: downloaded and verified, not installed' } else { 'installed' } }
        }
        Write-Log "Ticket ${Ticket}: OK (exit $code) $detail"
        Write-Result -Ticket $Ticket -Verb 'install' -Status 'OK' -ExitCode $code -PackageId $entry.id -Detail $detail
    }
    else {
        Write-Log "Ticket ${Ticket}: FAILED - installer exit $code." 'ERROR'
        Write-Result -Ticket $Ticket -Verb 'install' -Status 'FAILED' -ExitCode $code -PackageId $entry.id -Detail "installer exit $code"
    }
}

# --- Verb: updates.install ---------------------------------------------------
# Takes no arguments at all. Everything comes from the Windows Update agent.
function Invoke-VerbUpdatesInstall {
    param([string]$Ticket)

    try {
        Write-Log "Ticket ${Ticket}: searching Windows Update (IsInstalled=0 and Type='Software' and IsHidden=0)."
        $session  = New-Object -ComObject Microsoft.Update.Session
        $searcher = $session.CreateUpdateSearcher()
        $found    = $searcher.Search("IsInstalled=0 and Type='Software' and IsHidden=0")
        $pending  = @($found.Updates)
        Write-Log "Ticket ${Ticket}: $($pending.Count) update(s) pending."
        foreach ($u in $pending) { Write-Log "Ticket ${Ticket}:   pending - $(Format-Safe $u.Title 120)" }

        if ($pending.Count -eq 0) {
            Write-Result -Ticket $Ticket -Verb 'updates.install' -Status 'OK' -ExitCode 0 `
                         -Detail 'no updates pending' -Installed 0 -RebootRequired $false
            return
        }

        if ($WhatIfOnly) {
            Write-Log "Ticket ${Ticket}: -WhatIfOnly, search only - nothing downloaded or installed."
            Write-Result -Ticket $Ticket -Verb 'updates.install' -Status 'OK' -ExitCode 0 `
                         -Detail "whatif: $($pending.Count) update(s) available, nothing downloaded or installed" `
                         -Installed 0 -RebootRequired $false
            return
        }

        $toDownload = New-Object -ComObject Microsoft.Update.UpdateColl
        foreach ($u in $pending) {
            if (-not $u.EulaAccepted) { $u.AcceptEula() }
            [void]$toDownload.Add($u)
        }
        Write-Log "Ticket ${Ticket}: downloading $($toDownload.Count) update(s)."
        $downloader = $session.CreateUpdateDownloader()
        $downloader.Updates = $toDownload
        [void]$downloader.Download()

        $toInstall = New-Object -ComObject Microsoft.Update.UpdateColl
        foreach ($u in $pending) { if ($u.IsDownloaded) { [void]$toInstall.Add($u) } }
        Write-Log "Ticket ${Ticket}: $($toInstall.Count) of $($pending.Count) downloaded; installing."

        if ($toInstall.Count -eq 0) {
            Write-Result -Ticket $Ticket -Verb 'updates.install' -Status 'FAILED' -ExitCode 1 `
                         -Detail 'no update downloaded successfully' -Installed 0 -RebootRequired $false
            return
        }

        $installer = $session.CreateUpdateInstaller()
        $installer.Updates = $toInstall
        $res = $installer.Install()

        # OperationResultCode: 2 = succeeded, 3 = succeeded with errors, 4 = failed, 5 = aborted
        $ok      = ($res.ResultCode -eq 2 -or $res.ResultCode -eq 3)
        $reboot  = [bool]$res.RebootRequired
        $done    = 0
        for ($i = 0; $i -lt $toInstall.Count; $i++) {
            $ir = $res.GetUpdateResult($i)
            $title = Format-Safe $toInstall.Item($i).Title 120
            if ($ir.ResultCode -eq 2 -or $ir.ResultCode -eq 3) { $done++; Write-Log "Ticket ${Ticket}:   installed - $title" }
            else { Write-Log "Ticket ${Ticket}:   FAILED ($($ir.ResultCode), hr=0x$('{0:X8}' -f $ir.HResult)) - $title" 'WARN' }
        }

        Write-Log "Ticket ${Ticket}: install resultCode=$($res.ResultCode), installed=$done, rebootRequired=$reboot"
        Write-Result -Ticket $Ticket -Verb 'updates.install' `
                     -Status $(if ($ok) { 'OK' } else { 'FAILED' }) -ExitCode $res.ResultCode `
                     -Detail "$done of $($toInstall.Count) update(s) installed (resultCode $($res.ResultCode))" `
                     -Installed $done -RebootRequired $reboot
    }
    catch {
        Write-Log "Ticket ${Ticket}: updates.install failed: $($_.Exception.Message)" 'ERROR'
        Write-Result -Ticket $Ticket -Verb 'updates.install' -Status 'FAILED' -ExitCode 1 `
                     -Detail $_.Exception.Message -Installed 0 -RebootRequired $false
    }
}

# --- Verb: wifi.forget -------------------------------------------------------
# The SSID is validated, then passed to a FIXED program as an argument ARRAY.
# It is never concatenated into a command string, so there is no shell to escape.
function Invoke-VerbWifiForget {
    param([string]$Ticket, [hashtable]$Req)

    $ssid = ''
    if ($Req.ContainsKey('ssid')) { $ssid = $Req['ssid'] }

    if ($ssid -notmatch $SsidPattern) {
        Write-Log "Ticket ${Ticket}: REJECTED - malformed ssid '$(Format-Safe $ssid)'." 'WARN'
        Write-Result -Ticket $Ticket -Verb 'wifi.forget' -Status 'REJECTED' -Detail 'malformed ssid'
        return
    }

    if ($WhatIfOnly) {
        Write-Log "Ticket ${Ticket}: -WhatIfOnly, not calling netsh."
        Write-Result -Ticket $Ticket -Verb 'wifi.forget' -Status 'OK' -ExitCode 0 -Detail 'whatif: validated, netsh not called'
        return
    }

    $netsh = Join-Path $env:SystemRoot 'System32\netsh.exe'
    $nargs = @('wlan', 'delete', 'profile', "name=$ssid")
    Write-Log "Ticket ${Ticket}: netsh wlan delete profile name=<ssid>"
    try {
        $out  = & $netsh @nargs 2>&1
        $code = $LASTEXITCODE
        foreach ($l in $out) { Write-Log "Ticket ${Ticket}:   | $(Format-Safe ([string]$l) 200)" }
        $text = (($out | Out-String) -replace '[\r\n]+', ' ').Trim()

        if ($code -eq 0 -and $text -match '(?i)delet') {
            Write-Log "Ticket ${Ticket}: OK - profile deleted."
            Write-Result -Ticket $Ticket -Verb 'wifi.forget' -Status 'OK' -ExitCode 0 -Detail (Format-Safe $text 200)
        }
        else {
            Write-Log "Ticket ${Ticket}: FAILED - netsh exit $code." 'ERROR'
            Write-Result -Ticket $Ticket -Verb 'wifi.forget' -Status 'FAILED' -ExitCode $code -Detail (Format-Safe $text 200)
        }
    }
    catch {
        Write-Log "Ticket ${Ticket}: FAILED - $($_.Exception.Message)" 'ERROR'
        Write-Result -Ticket $Ticket -Verb 'wifi.forget' -Status 'FAILED' -ExitCode 1 -Detail $_.Exception.Message
    }
}

# --- Verb: bt.forget ---------------------------------------------------------
function Invoke-VerbBtForget {
    param([string]$Ticket, [hashtable]$Req)

    $addr = ''
    if ($Req.ContainsKey('address')) { $addr = $Req['address'] }

    if ($addr -notmatch $BtPattern) {
        Write-Log "Ticket ${Ticket}: REJECTED - malformed address '$(Format-Safe $addr)'." 'WARN'
        Write-Result -Ticket $Ticket -Verb 'bt.forget' -Status 'REJECTED' -Detail 'malformed address'
        return
    }
    $addr = $addr.ToUpperInvariant()

    if ($WhatIfOnly) {
        Write-Log "Ticket ${Ticket}: -WhatIfOnly, not calling BluetoothRemoveDevice."
        Write-Result -Ticket $Ticket -Verb 'bt.forget' -Status 'OK' -ExitCode 0 -Detail 'whatif: validated, BluetoothRemoveDevice not called'
        return
    }

    try {
        if (-not ('MarwanOS.Bt' -as [type])) {
            Add-Type -Namespace 'MarwanOS' -Name 'Bt' -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("bthprops.cpl", SetLastError = true)]
public static extern uint BluetoothRemoveDevice(ref ulong pAddress);
'@
        }
        # BLUETOOTH_ADDRESS is a union over a ULONGLONG; 12 hex chars fit in the
        # low 6 bytes with the top two zero.
        $value = [uint64]::Parse($addr, [Globalization.NumberStyles]::HexNumber)
        Write-Log "Ticket ${Ticket}: BluetoothRemoveDevice($addr)"
        $rc = [MarwanOS.Bt]::BluetoothRemoveDevice([ref]$value)

        if ($rc -eq 0) {
            Write-Log "Ticket ${Ticket}: OK - device removed."
            Write-Result -Ticket $Ticket -Verb 'bt.forget' -Status 'OK' -ExitCode 0 -Detail "removed $addr"
        }
        else {
            $msg = (New-Object ComponentModel.Win32Exception([int]$rc)).Message
            Write-Log "Ticket ${Ticket}: FAILED - BluetoothRemoveDevice returned $rc ($msg)." 'ERROR'
            Write-Result -Ticket $Ticket -Verb 'bt.forget' -Status 'FAILED' -ExitCode ([int]$rc) -Detail "BluetoothRemoveDevice returned $rc ($msg)"
        }
    }
    catch {
        Write-Log "Ticket ${Ticket}: FAILED - $($_.Exception.Message)" 'ERROR'
        Write-Result -Ticket $Ticket -Verb 'bt.forget' -Status 'FAILED' -ExitCode 1 -Detail $_.Exception.Message
    }
}

# --- Request parsing ---------------------------------------------------------
$IdPattern   = '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$'
$SsidPattern = '^[^\x00-\x1F\x7F"]{1,32}$'
$BtPattern   = '^[0-9A-Fa-f]{12}$'
$VerbPattern = '^[a-z][a-z.]{0,31}$'

# v2 = key=value lines, first line 'verb='. v1 = a bare package id on line 1.
# Unknown keys are ignored; so is anything after the keys this worker knows.
function Read-Request {
    param([string]$Path)

    $lines = @()
    try { $lines = @(Get-Content -Path $Path -Encoding UTF8 -ErrorAction Stop) }
    catch { return @{ error = "unreadable request file: $($_.Exception.Message)" } }

    $first = ''
    if ($lines.Count -gt 0) { $first = ([string]$lines[0]).Trim() }

    if ($first -notmatch '^verb=') {
        # v1 compat: the whole request is one package id.
        return @{ verb = 'install'; package = $first; version = 'v1' }
    }

    $req = @{ version = 'v2' }
    foreach ($raw in $lines) {
        $line = ([string]$raw).Trim()
        if (-not $line) { continue }
        $kv = $line -split '=', 2
        if ($kv.Count -ne 2) { continue }
        $key = $kv[0].Trim().ToLowerInvariant()
        if (-not $key) { continue }
        if ($req.ContainsKey($key)) { continue }   # first occurrence wins
        $req[$key] = $kv[1].Trim()
    }
    if (-not $req.ContainsKey('verb')) { $req['verb'] = '' }
    return $req
}

# --- Drain the queue ---------------------------------------------------------
$requests = @(Get-ChildItem -Path $QueueDir -Filter '*.req' -File -ErrorAction SilentlyContinue |
              Sort-Object CreationTimeUtc)

if ($requests.Count -eq 0) {
    Write-Log "Queue empty. Nothing to do."
    exit 0
}
Write-Log "Queue depth: $($requests.Count)."

foreach ($req in $requests) {
    $ticket = [IO.Path]::GetFileNameWithoutExtension($req.Name)

    # The ticket name is attacker-controlled; make sure it cannot escape the
    # processed directory when the .result / .progress paths are built from it.
    if ($ticket -notmatch '^[A-Za-z0-9-]{1,64}$') {
        Write-Log "Rejecting request with malformed ticket name '$(Format-Safe $req.Name)'." 'WARN'
        Remove-Item -Path $req.FullName -Force -ErrorAction SilentlyContinue
        continue
    }

    # From here on every log line is mirrored into processed\<ticket>.progress.
    $script:CurrentTicket = $ticket
    Write-Log "Ticket ${ticket}: picked up."

    $parsed = Read-Request -Path $req.FullName
    Remove-Item -Path $req.FullName -Force -ErrorAction SilentlyContinue

    if ($parsed.ContainsKey('error')) {
        Write-Log "Ticket ${ticket}: REJECTED - $($parsed['error'])" 'WARN'
        Write-Result -Ticket $ticket -Status 'REJECTED' -Detail 'unreadable request'
        $script:CurrentTicket = $null
        continue
    }

    $verb = [string]$parsed['verb']
    Write-Log "Ticket ${ticket}: $($parsed['version']) request, verb='$(Format-Safe $verb 40)'."

    if ($verb -notmatch $VerbPattern) {
        Write-Log "Ticket ${ticket}: REJECTED - unknown verb." 'WARN'
        Write-Result -Ticket $ticket -Status 'REJECTED' -Detail 'unknown verb'
        $script:CurrentTicket = $null
        continue
    }

    switch ($verb) {
        'install'         { Invoke-VerbInstall        -Ticket $ticket -Req $parsed }
        'updates.install' { Invoke-VerbUpdatesInstall -Ticket $ticket }
        'wifi.forget'     { Invoke-VerbWifiForget     -Ticket $ticket -Req $parsed }
        'bt.forget'       { Invoke-VerbBtForget       -Ticket $ticket -Req $parsed }
        default {
            Write-Log "Ticket ${ticket}: REJECTED - unknown verb '$(Format-Safe $verb 40)'." 'WARN'
            Write-Result -Ticket $ticket -Verb $verb -Status 'REJECTED' -Detail 'unknown verb'
        }
    }

    Write-Log "Ticket ${ticket}: done."
    $script:CurrentTicket = $null
}

# --- Retention ---------------------------------------------------------------
$cut = (Get-Date).ToUniversalTime().AddDays(-7)
foreach ($dir in @($ProcessedDir, $CacheDir)) {
    $stale = @(Get-ChildItem -Path $dir -File -ErrorAction SilentlyContinue |
               Where-Object { $_.CreationTimeUtc -lt $cut })
    if ($stale.Count -gt 0) {
        Write-Log "Pruning $($stale.Count) file(s) older than 7 days from $dir."
        $stale | Remove-Item -Force -ErrorAction SilentlyContinue
    }
}

Write-Log "Worker done."
exit 0
