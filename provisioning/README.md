# PC1 provisioning

Scripts that put a custom console shell in front of **one throwaway local account**, and
the matching scripts that take it all back off.

> **Nothing in this folder has been run.** Every script here changes machine state and is
> written to be reviewed first. They print what they are going to do, guard against the
> dangerous cases, and stop rather than guess.

---

## The guardrail

**The primary account `brain` is never configured, never modified, and never deleted by
anything in this folder.**

This is enforced, not just intended:

* `03-apply-shell-launcher.ps1` resolves the target SID and **aborts before writing
  anything** if that SID belongs to `brain`, to the account running the script, to any
  member of the local `Administrators` group, or to anything that is not a machine-local
  user SID (`S-1-5-21-…-RID` with RID ≥ 1000). Group SIDs such as `S-1-5-32-544` are
  rejected outright.
* The Administrators check **fails closed**: if the group cannot be enumerated, the
  script aborts rather than proceed on an unverified assumption.
* Step 03 sets the *default* shell for every unconfigured account to `explorer.exe`
  **before** it writes the custom shell entry. `brain` therefore gets a normal Windows
  desktop by design, and would still get one even if the rest of step 03 failed halfway.
* `02-` and `92-` refuse to act on the names `brain`, `Administrator`, `Guest`,
  `DefaultAccount`, `WDAGUtilityAccount`, `SYSTEM`.
* `92-remove-test-account.ps1` refuses to delete the account you are currently signed in
  as, and refuses to touch a profile flagged `Special`.

Everything runs against the account `arcshell`: a standard, non-administrative, local,
disposable test account.

---

## Order of operations

Run in this order, from an **elevated Windows PowerShell 5.1 prompt** (`powershell.exe`,
Run as administrator). **Not PowerShell 7** — scripts 03 and 93 depend on the
`[wmiclass]` type accelerator, which `pwsh` removed. They check for this and abort.

```powershell
cd C:\Users\brain\Documents\repos\PC1\provisioning
```

| Step | Script | What it changes | Requires confirmation |
| --- | --- | --- | --- |
| 0 | — | Read **`RECOVERY.md`** end to end. Record your BitLocker recovery key. | — |
| 1 | `01-enable-shell-launcher.ps1` | Installs the `Client-DeviceLockdown` + `Client-EmbeddedShellLauncher` optional features. **May require a reboot.** No account is affected. | **Yes** |
| — | *(reboot if step 1 reported `RestartNeeded`)* | | |
| 2 | `02-create-test-account.ps1` | Creates the local user `arcshell`. Prints its password **once**. | **Yes** |
| 3 | *(build `spike\ShellHost.exe`)* | Step 3 refuses to configure a shell path that does not exist, unless you pass `-AllowMissingShell`. | — |
| 4 | `03-apply-shell-launcher.ps1` | **The consequential one.** Sets the default shell to `explorer.exe`, sets `ShellHost.exe` as `arcshell`'s shell, enables enforcement. Takes effect at the next sign-in. | **Yes — and read `RECOVERY.md` first** |
| 5 | `Ctrl`+`Alt`+`Del` → **Switch user** → sign into `arcshell` | Keep the `brain` session alive. Do **not** sign out. | — |

Every script accepts `-WhatIfOnly`. Use it on the first pass — it runs all the
pre-checks and guards and prints the exact calls it would make, without writing anything:

```powershell
.\03-apply-shell-launcher.ps1 -WhatIfOnly
```

### Undo, in reverse order

```powershell
.\93-remove-shell-launcher-config.ps1   # first — while the WMI provider still exists
.\92-remove-test-account.ps1            # then the account
.\91-disable-shell-launcher.ps1         # last — removes the feature
```

`91` refuses to run while enforcement is still on, unless you pass `-Force`. That
ordering matters: removing the optional feature first would take away the WMI provider
you need in order to clear the configuration.

---

## Script → undo map

| Apply | Undo | Reverses |
| --- | --- | --- |
| `01-enable-shell-launcher.ps1` | `91-disable-shell-launcher.ps1` | `Enable-WindowsOptionalFeature` → `Disable-WindowsOptionalFeature -FeatureName Client-EmbeddedShellLauncher -NoRestart`. Leaves `Client-DeviceLockdown` alone unless `-AlsoDisableDeviceLockdown`. |
| `02-create-test-account.ps1` | `92-remove-test-account.ps1` | `New-LocalUser` + `Add-LocalGroupMember` → `Remove-CimInstance` on `Win32_UserProfile` (deletes `C:\Users\arcshell`) then `Remove-LocalUser`. **Destructive.** Prompts for typed confirmation. |
| `03-apply-shell-launcher.ps1` | `93-remove-shell-launcher-config.ps1` | `SetDefaultShell` + `SetCustomShell` + `SetEnabled($true)` → `SetDefaultShell("explorer.exe",0)` + `RemoveCustomShell(<sid>)` + `SetEnabled($false)`. Use `-All` to clear every per-SID entry when the state is unknown. |

All six scripts are **idempotent**: re-running one converges on the same state rather
than erroring or double-applying.

---

## What requires confirmation

Scripts 01, 02, 03, 91, 92 and 93 all change machine state. **None of them should be run
without the user explicitly saying yes to that specific script.** The scripts themselves
do not ask for a general go-ahead — that conversation happens outside them — with one
exception: `92-remove-test-account.ps1` requires you to type the account name, because it
permanently deletes a profile directory.

Record every run in **`MACHINE-CHANGES.md`** with the date, the exact command, the undo
command, who ran it, and who confirmed it. The three apply scripts are pre-filled there
as `NOT YET APPLIED`.

---

## Shell Launcher: the shape of the thing

Shell Launcher replaces `explorer.exe` for a chosen user or group with an executable of
your choice. It is an optional Windows component, available on Enterprise / Enterprise
LTSC / Education / IoT Enterprise / IoT Enterprise LTSC — which this machine (Windows 11
IoT Enterprise LTSC 2024) qualifies for.

Configuration lives in the WMI class **`WESL_UserSetting`**, namespace
**`root\standardcimv2\embedded`**. Only one instance of the class exists per device; the
methods are static.

### The action enum, verified against Microsoft Learn

When the custom shell exits, Shell Launcher takes one of four actions:

| Value | Action |
| --- | --- |
| `0` | Restart the shell |
| `1` | Restart the device |
| `2` | Shut down the device |
| `3` | Do nothing |

**These are action values, not exit codes.** The project brief described the mapping as
"exit 0 = restart shell, 2 = restart device, 3 = shutdown" — those are the *shell's exit
codes*. They are wired to actions through two parallel arrays:

```
CustomReturnCodes       = @(0, 2, 3)   # exit codes ShellHost.exe may return
CustomReturnCodesAction = @(0, 1, 2)   # RestartShell, RestartDevice, ShutdownDevice
DefaultAction           =   0          # RestartShell, for any other exit code
```

Note that the intended "shutdown" behaviour is action `2`, not `3`. Action `3` means *do
nothing*. Getting this backwards would give you a device that shuts down when you meant
it to idle, or idles when you meant it to power off.

### v1 vs v2

The brief calls for Shell Launcher V2. Microsoft's own docs split these:

* **The `WESL_UserSetting` WMI provider** (what these scripts use) configures a Win32
  executable as the shell, per SID. Documented, supported, and directly reversible from
  a PowerShell prompt.
* **"Shell Launcher V2"** specifically means the XML schema at
  `http://schemas.microsoft.com/ShellLauncher/2019/Configuration`, applied through the
  Assigned Access CSP. It adds UWP-app shells (`V2:AppType="UWP"`) and
  `V2:AllAppsFullScreen`, and replaces `explorer.exe` with `CustomShellHost.exe` rather
  than `Eshell.exe`.

Since `ShellHost.exe` is a Win32 console host, the WMI path is sufficient and is the
easier one to unwind. If UWP-shell hosting or `AllAppsFullScreen` is ever needed, the
configuration has to move to the V2 XML via Assigned Access CSP — a different mechanism
with a different undo, which would need new scripts here.

---

## Known unknowns

Things these scripts assume but which have **not** been verified on this machine. Check
them empirically before trusting the setup:

1. **Console-subsystem shell.** Shell Launcher's documented scenarios are GUI apps. Whether
   a console `ShellHost.exe` behaves sanely as a shell — window sizing, focus, whether a
   console host window appears at all — is untested.
2. **Task Manager reachability.** Shell Launcher has no `AllowTaskManager` setting (that
   belongs to Assigned Access multi-app kiosk XML), and these scripts do not set
   `DisableTaskMgr`, so `Ctrl`+`Shift`+`Esc` *should* work. Not confirmed on 24H2.
   Recovery paths (b) through (e) in `RECOVERY.md` do not depend on it.
3. **`Client-DeviceLockdown` default state** on IoT Enterprise LTSC 2024 images.
4. **Reboot requirement** after enabling the feature with `-NoRestart`.
5. **Registry storage layout** of the Shell Launcher configuration — needed for the
   offline-USB recovery path. Not documented by Microsoft. `RECOVERY.md` path (e)
   includes commands to discover it while the machine is healthy; do that.
6. **Safe Mode bypass.** Expected to load `explorer.exe` regardless of Shell Launcher, but
   unconfirmed.

---

## Files

```
provisioning/
├── README.md                            this file
├── RECOVERY.md                          READ BEFORE SIGNING INTO arcshell
├── MACHINE-CHANGES.md                   change log — fill in as you go
├── 01-enable-shell-launcher.ps1         apply:  optional feature
├── 02-create-test-account.ps1           apply:  arcshell local account
├── 03-apply-shell-launcher.ps1          apply:  custom shell for arcshell only
├── 91-disable-shell-launcher.ps1        undo 01
├── 92-remove-test-account.ps1           undo 02
└── 93-remove-shell-launcher-config.ps1  undo 03  ← the recovery script
```

## Reference

* Shell Launcher overview — <https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/>
* Configure Shell Launcher (feature enable + exit-code actions) — <https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/configure>
* Configure with the WMI provider — <https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/configure-wmi>
* `WESL_UserSetting` — <https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/wesl-usersetting>
* `WESL_UserSetting.SetCustomShell` — <https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/wesl-usersettingsetcustomshell>
* `WESL_UserSetting.SetDefaultShell` — <https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/wesl-usersettingsetdefaultshell>
* `WESL_UserSetting.SetEnabled` — <https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/wesl-usersettingsetenabled>
* Shell Launcher configuration file / V2 XML schema — <https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/configuration-file>
