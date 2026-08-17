# MACHINE-CHANGES.md — change log for PC1 provisioning

Every change this project makes to the machine gets a row here. **Fill the row in at the
time you run the script, not afterwards.** If a row is not in this table, the change is
not authorised.

Status vocabulary, used in the **Date applied** column:

* `NOT YET APPLIED` — script exists, nothing has been run.
* An ISO date (`2026-08-13 14:32`) — applied at that local time.
* `REVERTED <date>` — applied and then undone.

Rules:

1. Nothing is run without the user's explicit confirmation, recorded in
   **Confirmed by user**. "Confirmed by user" means the human said yes *for that
   specific script, in that specific session* — not a blanket approval.
2. The undo command is written down **before** the change is applied.
3. `brain` is never a target. Any row whose command names `brain` as the account to
   configure, delete, or re-shell is invalid by definition.
4. Read `RECOVERY.md` before applying row 3.

---

## Applied changes

| # | Date applied | Change | Exact command | Undo command | Applied by | Confirmed by user |
|---|---|---|---|---|---|---|
| 1 | `2026-08-13 23:49` | Enable the **Shell Launcher optional feature** (`Client-DeviceLockdown`, `Client-EmbeddedShellLauncher`). Installs the component and registers the `WESL_UserSetting` WMI provider. Does **not** enable enforcement and does **not** change any account's shell. May require a reboot. | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Users\brain\Documents\repos\PC1\provisioning\01-enable-shell-launcher.ps1"`<br><br>Underlying: `Enable-WindowsOptionalFeature -Online -FeatureName Client-DeviceLockdown,Client-EmbeddedShellLauncher -NoRestart` | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Users\brain\Documents\repos\PC1\provisioning\91-disable-shell-launcher.ps1"`<br><br>Underlying: `Disable-WindowsOptionalFeature -Online -FeatureName Client-EmbeddedShellLauncher -NoRestart` | Claude (elevated window, UAC consented by user) | Yes — "I approve M0", 2026-08-13 |
| 2 | `2026-08-13 23:58` | Create the **throwaway local account `marwanshell`** — standard user, member of `Users` only, never `Administrators`, random 20-char password printed once, password expiry disabled. | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Users\brain\Documents\repos\PC1\provisioning\02-create-test-account.ps1"`<br><br>Underlying: `New-LocalUser -Name marwanshell -Password <random> -PasswordNeverExpires -AccountNeverExpires` then `Add-LocalGroupMember -Group Users -Member marwanshell` | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Users\brain\Documents\repos\PC1\provisioning\92-remove-test-account.ps1"`<br><br>Underlying: `Get-CimInstance Win32_UserProfile -Filter "SID='<marwanshell SID>'" \| Remove-CimInstance` then `Remove-LocalUser -Name marwanshell` | Claude (elevated window, UAC consented by user) | Yes — "I approve M0", 2026-08-13 |
| 3 | `NOT YET APPLIED` | Apply the **Shell Launcher custom shell to `marwanshell` only**. Sets the default shell for all unconfigured users (including `brain`) to `explorer.exe`, sets `ShellHost.exe` as the shell for the `marwanshell` SID with the exit-code map `0→RestartShell, 2→RestartDevice, 3→ShutdownDevice`, then enables enforcement. **Takes effect at the next sign-in.** | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Users\brain\Documents\repos\PC1\provisioning\03-apply-shell-launcher.ps1"`<br><br>Underlying (5.1 only): `$SL=[wmiclass]"\\localhost\root\standardcimv2\embedded:WESL_UserSetting"`; `$SL.SetDefaultShell("explorer.exe",0)`; `$SL.SetCustomShell("<marwanshell SID>","C:\Users\brain\Documents\repos\PC1\spike\ShellHost.exe",@(0,2,3),@(0,1,2),0)`; `$SL.SetEnabled($true)` | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Users\brain\Documents\repos\PC1\provisioning\93-remove-shell-launcher-config.ps1"`<br><br>Underlying: `$SL.SetDefaultShell("explorer.exe",0)`; `$SL.RemoveCustomShell("<marwanshell SID>")`; `$SL.SetEnabled($false)` | | |
| 5 | `NOT YET APPLIED` | **Disable Microsoft Defender + SmartScreen** (turn off, not uninstall — the platform is not removable on Win11 and self-heals). Real-time/behaviour/IOAV/script/archive scanning off, cloud+sample submission off, policy keys `DisableAntiSpyware=1`/`DisableAntiVirus=1` + Real-Time Protection subkey, SmartScreen policy off, the four Defender scheduled tasks disabled, and `WinDefend`/`WdNisSvc`/`Sense` service `Start=4`. **Hard prerequisite: Tamper Protection OFF** (manual toggle in Windows Security; the script refuses and changes nothing while it is on). **Leaves the machine with no antivirus** until MarwanOS ships its own. Does not touch UAC, firewall, or any account. | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Users\brain\Documents\repos\PC1\provisioning\20-disable-defender.ps1" -Apply`<br><br>(Run without `-Apply` first — dry run that only reports state.) | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Users\brain\Documents\repos\PC1\provisioning\90-restore-defender.ps1" -Apply`<br><br>Re-enabling **Tamper Protection** afterwards is a manual toggle. | | Yes — "script a full disable", 2026-08-16 (authored; **not** approved to *apply*) |
| 4 | `NOT YET APPLIED` | Install the **MarwanOS privileged-action broker** (formerly "install broker"): creates `C:\ProgramData\MarwanOS` with inheritance disabled (SYSTEM/Administrators full, `marwanshell` read, `marwanshell` write to `queue\` only), deploys `marwan-install-worker.ps1` + `packages.json`, and registers the on-demand scheduled task `\MarwanOS\marwan-install-broker` running as **SYSTEM, RunLevel Highest**, with a task DACL granting `marwanshell` read+execute. Lets the controller-only shell perform a **fixed allowlist of four privileged verbs** with **no UAC prompt**: `install` (manifest-named, Authenticode-publisher-pinned packages), `updates.install` (Windows Update search/download/install), `wifi.forget` (`netsh wlan delete profile`), `bt.forget` (`BluetoothRemoveDevice`). **No verb takes a path, URL, command line or argument list from the caller.** Does not change UAC settings; `EnableLUA=1` and `PromptOnSecureDesktop=1` are left as they are. | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Users\brain\Documents\repos\PC1\provisioning\04-install-broker.ps1"`<br><br>Underlying: `Schedule.Service` COM `RegisterTaskDefinition('marwan-install-broker', <def>, 6, 'SYSTEM', $null, 5)` with principal `LogonType=5, RunLevel=1`, action `powershell.exe -File C:\ProgramData\MarwanOS\marwan-install-worker.ps1`; then `SetSecurityDescriptor("D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;<marwanshell SID>)", 0)` | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Users\brain\Documents\repos\PC1\provisioning\94-remove-install-broker.ps1"`<br><br>Underlying: `$svc.GetFolder('\MarwanOS').DeleteTask('marwan-install-broker',0)` then delete the `\MarwanOS` task folder if empty. Add `-RemoveData` to also `Remove-Item -Recurse C:\ProgramData\MarwanOS`. | | |

| 6 | `NOT YET APPLIED` | **Register MarwanOS as a browser and make it the default** for `http`, `https`, `.htm`, `.html`. Writes the ProgId `MarwanOSHTML` (whose open command is `MarwanOpenUrl.exe`, **not** the shell binary), the `StartMenuInternet\MarwanOSBrowser` capabilities, the `RegisteredApplications` pointer, and — because Windows hash-protects the per-user `UserChoice` key and has no supported per-user override — the machine-wide policy `DefaultAssociationsConfiguration` pointing at `C:\ProgramData\MarwanOS\default-associations.xml`. **That policy applies to every account including `brain`**, so the script first records the current default browser's open command at `HKLM\SOFTWARE\MarwanOS\Browser\FallbackCommand`; `MarwanOpenUrl.exe` uses it in any session with no MarwanOS shell running, which is what keeps `brain`'s desktop behaving as it did. Takes effect **at the next sign-in**. Requires a shell binary containing the `WM_COPYDATA` receiver (v17 or later) deployed alongside `MarwanOpenUrl.exe`. | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Users\brain\Documents\repos\PC1\provisioning\21-set-default-browser.ps1" -ShellDir "C:\ArcOS\web\v17" -Apply`<br><br>(Run without `-Apply` first — dry run that only reports the current associations. `-NoPolicy` registers without enforcing.) | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Users\brain\Documents\repos\PC1\provisioning\89-restore-default-browser.ps1" -Apply` | | Yes — "make the one we made the default browser", 2026-08-16 (authored; **not** approved to *apply*) |
| 7 | `NOT YET APPLIED` | **Remove Edge as a browser.** De-registers it (`RegisteredApplications\Microsoft Edge`, `Clients\StartMenuInternet\Microsoft Edge`, both hives), moves the machine-wide desktop and Start Menu shortcuts into `C:\MarwanOS\backup`, blocks `msedge.exe` from starting via an IFEO `Debugger` entry, and sets `EdgeUpdate` `InstallDefault=0` + `Install{56EB18F8-…}=0` so the updater cannot put the browser back. **Every step is keyed to `msedge.exe` or Edge's own install directory — never to `msedgewebview2.exe`** — and the script refuses to do anything unless it can first confirm the WebView2 runtime is installed independently under `EdgeWebView\Application`, because that runtime is what MarwanOS renders in. With `-Uninstall` it also runs Edge's own `setup.exe --uninstall --system-level --force-uninstall` and then reports whether `msedge.exe` is actually gone rather than trusting the exit code. Does **not** disable the Edge updater service or its tasks (that would freeze the WebView2 runtime too). Apply **after** row 6 — a machine with no registered browser has nothing to open a link with. | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Users\brain\Documents\repos\PC1\provisioning\22-remove-edge-browser.ps1" -Apply`<br><br>(Add `-Uninstall` to also run Edge's own uninstaller. Run without `-Apply` first.) | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Users\brain\Documents\repos\PC1\provisioning\88-restore-edge-browser.ps1" -Apply`<br><br>A run with `-Uninstall` cannot be undone from the backup — Edge has to be reinstalled from Microsoft. | | Yes — "remove edge as a browser", 2026-08-16 (authored; **not** approved to *apply*) |

---

## Blank row template

Copy this for any additional change:

| # | Date applied | Change | Exact command | Undo command | Applied by | Confirmed by user |
|---|---|---|---|---|---|---|
| n | `NOT YET APPLIED` |  |  |  |  |  |

---

## Facts worth recording alongside the rows above

Fill these in as you go. They are what recovery depends on.

| Fact | Value | Recorded when |
|---|---|---|
| `marwanshell` SID | `S-1-5-21-22379415-3387693327-1370978032-1003` | after step 02, 2026-08-13 |
| `brain` SID | `S-1-5-21-22379415-3387693327-1370978032-1001` | 2026-08-13 |
| BitLocker on C: (On/Off) | Off (`FullyDecrypted`, protection Off) | 2026-08-13, step 01 log |
| BitLocker recovery key stored where | n/a — BitLocker not enabled | 2026-08-13 |
| Step 01 reported `RestartNeeded` | `False`; `WESL_UserSetting` resolvable immediately | at step 01 |
| Reboot performed after step 01 | Not needed | 2026-08-13 |
| `Ctrl`+`Shift`+`Esc` reaches Task Manager under the custom shell (Yes/No) | | first `marwanshell` sign-in |
| Real registry path of the Shell Launcher config (see RECOVERY.md path (e)) | | after step 03 |
