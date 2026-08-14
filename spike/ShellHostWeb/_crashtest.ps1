<#
    Crash isolation for the browser's content WebView.

    The claim under test: a web page's process can die, be killed, or hang, and the
    ARC shell - which IS the Windows shell on this machine - keeps running and keeps
    drawing. The browser puts web content in a second CoreWebView2Environment with its
    own user-data folder, which means its own browser process and its own renderers,
    so the two are different process trees rather than two views onto one.

    The test does not simulate anything. It finds a real renderer process belonging to
    the content environment, kills it from outside with no warning, and then asks
    whether the shell process is still there and still pumping messages.

    Run it while a ShellHostWeb run with --browse is up.
#>
param(
  [string]$ShellProcess   = 'ArcShellHostWeb-v5',
  [string]$ContentMarker  = 'WebView2-v5-content',
  [int]$WaitSeconds       = 60
)

$ErrorActionPreference = 'Stop'

function WebViewProcs {
  Get-CimInstance -ClassName Win32_Process -Filter 'Name = "msedgewebview2.exe"'
}

Write-Output "waiting for a content renderer (marker: $ContentMarker)..."
$deadline = (Get-Date).AddSeconds($WaitSeconds)
$victim = $null
while ((Get-Date) -lt $deadline -and -not $victim) {
  $victim = WebViewProcs |
            Where-Object { $_.CommandLine -like "*$ContentMarker*" -and $_.CommandLine -like '*--type=renderer*' } |
            Select-Object -First 1
  if (-not $victim) { Start-Sleep -Milliseconds 600 }
}
if (-not $victim) {
  Write-Output 'NO CONTENT RENDERER FOUND - is a --browse run actually up?'
  WebViewProcs | ForEach-Object { Write-Output ("  pid {0}  {1}" -f $_.ProcessId, ($_.CommandLine -replace '^.*?(--type=\S*).*$', '$1')) }
  exit 1
}

$shellBefore = Get-Process -Name $ShellProcess -ErrorAction SilentlyContinue
$all = WebViewProcs
$contentAll = @($all | Where-Object { $_.CommandLine -like "*$ContentMarker*" })
$shellAll   = @($all | Where-Object { $_.CommandLine -notlike "*$ContentMarker*" })

Write-Output ''
Write-Output '--- BEFORE ---'
Write-Output ("shell process           : pid {0}  responding={1}" -f $shellBefore.Id, $shellBefore.Responding)
Write-Output ("content-tree processes  : {0}" -f $contentAll.Count)
Write-Output ("shell-webview processes : {0}" -f $shellAll.Count)
Write-Output ''
Write-Output ("the two trees share no process: {0}" -f (($contentAll.ProcessId | Where-Object { $shellAll.ProcessId -contains $_ }).Count -eq 0))

Write-Output ''
Write-Output ("KILLING content renderer pid {0} (SIGKILL equivalent, no notice given)" -f $victim.ProcessId)
Stop-Process -Id $victim.ProcessId -Force
Start-Sleep -Seconds 5

Write-Output ''
Write-Output '--- AFTER ---'
$shellAfter = Get-Process -Name $ShellProcess -ErrorAction SilentlyContinue
if (-not $shellAfter) {
  Write-Output 'RESULT: FAIL - the shell process is gone.'
  exit 2
}
# Responding is the real question: a process that exists but has stopped pumping is a
# black television just the same.
Write-Output ("shell process           : pid {0}  responding={1}" -f $shellAfter.Id, $shellAfter.Responding)
$after = WebViewProcs
Write-Output ("content-tree processes  : {0}" -f @($after | Where-Object { $_.CommandLine -like "*$ContentMarker*" }).Count)
Write-Output ("shell-webview processes : {0}" -f @($after | Where-Object { $_.CommandLine -notlike "*$ContentMarker*" }).Count)
Write-Output ("dead renderer still present: {0}" -f [bool](Get-Process -Id $victim.ProcessId -ErrorAction SilentlyContinue))

if ($shellAfter.Id -ne $shellBefore.Id) { Write-Output 'RESULT: FAIL - the shell restarted.'; exit 3 }
if (-not $shellAfter.Responding)        { Write-Output 'RESULT: FAIL - the shell is not pumping messages.'; exit 4 }
Write-Output 'RESULT: PASS - same shell pid, still responding, after a content renderer was killed outright.'
exit 0
