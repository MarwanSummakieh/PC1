# ARC OS ShellHost spike - unattended test runner.
# Observes the host from OUTSIDE the process (foreground window, minimized state,
# child process liveness) so the results are not just the host's own self-report.

$ErrorActionPreference = 'Stop'
$exe = "C:\Users\brain\Documents\repos\PC1\spike\ShellHost\bin\ArcShellHost.exe"
$log = "C:\Users\brain\Documents\repos\PC1\spike\handoff-log.txt"

Add-Type -Namespace Spy -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
'@

function Get-ForegroundInfo {
    $h = [Spy.Win]::GetForegroundWindow()
    $pid2 = 0
    [void][Spy.Win]::GetWindowThreadProcessId($h, [ref]$pid2)
    $name = '?'
    try { $name = (Get-Process -Id $pid2 -ErrorAction Stop).ProcessName } catch {}
    [pscustomobject]@{ Hwnd = $h; Pid = $pid2; Name = $name }
}

function Invoke-HostTest {
    param([string]$Name, [string[]]$HostArgs, [int]$TimeoutSec = 60)

    Write-Host "`n=== $Name ===" -ForegroundColor Cyan
    Write-Host "args: $($HostArgs -join ' ')"

    $before = (Get-Process notepad -ErrorAction SilentlyContinue | Measure-Object).Count
    if ($before -gt 0) { Write-Host "WARNING: $before notepad process(es) already running" -ForegroundColor Yellow }

    $p = Start-Process -FilePath $exe -ArgumentList $HostArgs -PassThru
    $samples = New-Object System.Collections.Generic.List[object]
    $t0 = Get-Date
    $hostHwnd = [IntPtr]::Zero

    while (-not $p.HasExited -and ((Get-Date) - $t0).TotalSeconds -lt $TimeoutSec) {
        Start-Sleep -Milliseconds 250
        try { $p.Refresh() } catch {}
        if (-not $p.HasExited -and $p.MainWindowHandle -ne 0) { $hostHwnd = $p.MainWindowHandle }
        $fg = Get-ForegroundInfo
        $np = @(Get-Process notepad -ErrorAction SilentlyContinue)
        $cm = @(Get-Process cmd -ErrorAction SilentlyContinue)
        $iconic = $null
        if ($hostHwnd -ne [IntPtr]::Zero) { $iconic = [Spy.Win]::IsIconic($hostHwnd) }
        $samples.Add([pscustomobject]@{
            T        = [math]::Round(((Get-Date) - $t0).TotalMilliseconds)
            Fg       = $fg.Name
            FgIsHost = ($fg.Hwnd -eq $hostHwnd -and $hostHwnd -ne [IntPtr]::Zero)
            HostMin  = $iconic
            Notepads = $np.Count
            Cmds     = $cm.Count
        })
    }

    if (-not $p.HasExited) {
        Write-Host "TIMEOUT - killing host" -ForegroundColor Red
        try { $p.Kill() } catch {}
    }
    $p.WaitForExit()
    $code = $p.ExitCode

    Write-Host "exit code: $code"
    Write-Host ("timeline, state transitions only (T ms | foreground | fg==host | host minimized | #notepad | #cmd):")
    $prev = $null
    foreach ($s in $samples) {
        $key = "$($s.Fg)|$($s.FgIsHost)|$($s.HostMin)|$($s.Notepads)|$($s.Cmds)"
        if ($key -ne $prev) {
            Write-Host ("  {0,6} | {1,-12} | {2,-5} | {3,-5} | {4} | {5}" -f $s.T, $s.Fg, $s.FgIsHost, $s.HostMin, $s.Notepads, $s.Cmds)
            $prev = $key
        }
    }

    $after = @(Get-Process notepad -ErrorAction SilentlyContinue)
    if ($after.Count -gt 0) {
        Write-Host "CLEANUP: killing $($after.Count) leftover notepad(s)" -ForegroundColor Yellow
        $after | ForEach-Object { taskkill /PID $_.Id /T /F | Out-Null }
    }

    [pscustomobject]@{ Name = $Name; ExitCode = $code; Samples = $samples }
}

# Fresh log per run of the suite
if (Test-Path $log) { Remove-Item $log -Force }

$results = New-Object System.Collections.Generic.List[object]

$results.Add((Invoke-HostTest -Name 'TEST A - direct child (notepad), job tracking' -HostArgs @(
    '--child=notepad.exe','--auto-launch=1500','--auto-kill=4000','--auto-exit=2500','--exit-as=ESC','--watchdog=45000')))

# NOTE: the child command line contains spaces, so it must be passed as ONE argv entry.
# PowerShell joins -ArgumentList with spaces without quoting, hence the embedded quotes.
$launcherChild = '--child="cmd.exe /c start notepad.exe"'

$results.Add((Invoke-HostTest -Name 'TEST B - launcher spawns child (cmd /c start notepad), job tracking' -HostArgs @(
    $launcherChild,'--auto-launch=1500','--auto-kill=5000','--auto-exit=2500','--exit-as=ESC','--watchdog=45000')))

$results.Add((Invoke-HostTest -Name 'TEST D - launcher spawns child WITH --no-job (fallback path)' -HostArgs @(
    $launcherChild,'--no-job','--auto-launch=1500','--auto-kill=5000','--auto-exit=2500','--exit-as=ESC','--watchdog=30000')))

foreach ($pair in @(@('ESC',0), @('B',0), @('X',2), @('Y',3))) {
    $r = Invoke-HostTest -Name "TEST C - exit path $($pair[0]) expects code $($pair[1])" -HostArgs @(
        "--auto-exit=1500", "--exit-as=$($pair[0])", '--watchdog=20000') -TimeoutSec 25
    $r | Add-Member -NotePropertyName Expected -NotePropertyValue $pair[1]
    $results.Add($r)
}

Write-Host "`n===== SUMMARY =====" -ForegroundColor Green
foreach ($r in $results) {
    $exp = if ($r.PSObject.Properties['Expected']) { " (expected $($r.Expected))" } else { "" }
    Write-Host ("{0,-70} exit={1}{2}" -f $r.Name, $r.ExitCode, $exp)
}

Write-Host "`nStray processes check:"
@('notepad','cmd','ArcShellHost') | ForEach-Object {
    $c = @(Get-Process $_ -ErrorAction SilentlyContinue).Count
    Write-Host "  $_ = $c"
}
