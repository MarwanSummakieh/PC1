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
| 2 | `2026-08-13 23:58` | Create the **throwaway local account `arcshell`** — standard user, member of `Users` only, never `Administrators`, random 20-char password printed once, password expiry disabled. | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Users\brain\Documents\repos\PC1\provisioning\02-create-test-account.ps1"`<br><br>Underlying: `New-LocalUser -Name arcshell -Password <random> -PasswordNeverExpires -AccountNeverExpires` then `Add-LocalGroupMember -Group Users -Member arcshell` | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Users\brain\Documents\repos\PC1\provisioning\92-remove-test-account.ps1"`<br><br>Underlying: `Get-CimInstance Win32_UserProfile -Filter "SID='<arcshell SID>'" \| Remove-CimInstance` then `Remove-LocalUser -Name arcshell` | Claude (elevated window, UAC consented by user) | Yes — "I approve M0", 2026-08-13 |
| 3 | `NOT YET APPLIED` | Apply the **Shell Launcher custom shell to `arcshell` only**. Sets the default shell for all unconfigured users (including `brain`) to `explorer.exe`, sets `ShellHost.exe` as the shell for the `arcshell` SID with the exit-code map `0→RestartShell, 2→RestartDevice, 3→ShutdownDevice`, then enables enforcement. **Takes effect at the next sign-in.** | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Users\brain\Documents\repos\PC1\provisioning\03-apply-shell-launcher.ps1"`<br><br>Underlying (5.1 only): `$SL=[wmiclass]"\\localhost\root\standardcimv2\embedded:WESL_UserSetting"`; `$SL.SetDefaultShell("explorer.exe",0)`; `$SL.SetCustomShell("<arcshell SID>","C:\Users\brain\Documents\repos\PC1\spike\ShellHost.exe",@(0,2,3),@(0,1,2),0)`; `$SL.SetEnabled($true)` | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Users\brain\Documents\repos\PC1\provisioning\93-remove-shell-launcher-config.ps1"`<br><br>Underlying: `$SL.SetDefaultShell("explorer.exe",0)`; `$SL.RemoveCustomShell("<arcshell SID>")`; `$SL.SetEnabled($false)` | | |
| 4 | `NOT YET APPLIED` | Install the **ARC install broker**: creates `C:\ProgramData\ARC` with inheritance disabled (SYSTEM/Administrators full, `arcshell` read, `arcshell` write to `queue\` only), deploys `arc-install-worker.ps1` + `packages.json`, and registers the on-demand scheduled task `\ARC\arc-install-broker` running as **SYSTEM, RunLevel Highest**, with a task DACL granting `arcshell` read+execute. Lets the controller-only shell install manifest-named, Authenticode-publisher-pinned packages with **no UAC prompt**. Does not change UAC settings; `EnableLUA=1` and `PromptOnSecureDesktop=1` are left as they are. | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Users\brain\Documents\repos\PC1\provisioning\04-install-broker.ps1"`<br><br>Underlying: `Schedule.Service` COM `RegisterTaskDefinition('arc-install-broker', <def>, 6, 'SYSTEM', $null, 5)` with principal `LogonType=5, RunLevel=1`, action `powershell.exe -File C:\ProgramData\ARC\arc-install-worker.ps1`; then `SetSecurityDescriptor("D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;<arcshell SID>)", 0)` | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Users\brain\Documents\repos\PC1\provisioning\94-remove-install-broker.ps1"`<br><br>Underlying: `$svc.GetFolder('\ARC').DeleteTask('arc-install-broker',0)` then delete the `\ARC` task folder if empty. Add `-RemoveData` to also `Remove-Item -Recurse C:\ProgramData\ARC`. | | |

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
| `arcshell` SID | `S-1-5-21-22379415-3387693327-1370978032-1003` | after step 02, 2026-08-13 |
| `brain` SID | `S-1-5-21-22379415-3387693327-1370978032-1001` | 2026-08-13 |
| BitLocker on C: (On/Off) | Off (`FullyDecrypted`, protection Off) | 2026-08-13, step 01 log |
| BitLocker recovery key stored where | n/a — BitLocker not enabled | 2026-08-13 |
| Step 01 reported `RestartNeeded` | `False`; `WESL_UserSetting` resolvable immediately | at step 01 |
| Reboot performed after step 01 | Not needed | 2026-08-13 |
| `Ctrl`+`Shift`+`Esc` reaches Task Manager under the custom shell (Yes/No) | | first `arcshell` sign-in |
| Real registry path of the Shell Launcher config (see RECOVERY.md path (e)) | | after step 03 |
