# PC1 provisioning

Scripts that put a custom console shell in front of **one throwaway local account**, and
the matching scripts that take it all back off.

> **Check `MACHINE-CHANGES.md` for what has actually been applied** — as of 2026-08-14
> that is steps 01 and 02 only. Every script here changes machine state and is written to
> be reviewed first. They print what they are going to do, guard against the dangerous
> cases, and stop rather than guess.

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
| 6 | `04-install-broker.ps1` | Registers the SYSTEM scheduled task `\ARC\arc-install-broker` and creates `C:\ProgramData\ARC`. **Independent of steps 1–5** — apply or remove it on its own, in either order. | **Yes** |

Every script accepts `-WhatIfOnly`. Use it on the first pass — it runs all the
pre-checks and guards and prints the exact calls it would make, without writing anything:

```powershell
.\03-apply-shell-launcher.ps1 -WhatIfOnly
```

### Undo, in reverse order

```powershell
.\94-remove-install-broker.ps1          # any time — independent of the rest
.\93-remove-shell-launcher-config.ps1   # first — while the WMI provider still exists
.\92-remove-test-account.ps1            # then the account
.\91-disable-shell-launcher.ps1         # last — removes the feature
```

Run `94` **before** `92` if you are tearing the whole thing down: `92` deletes `arcshell`,
and the broker's ACLs and task DACL reference that SID.

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
| `04-install-broker.ps1` | `94-remove-install-broker.ps1` | `RegisterTaskDefinition` + directory ACLs → `DeleteTask` + delete the `\ARC` task folder. `C:\ProgramData\ARC` is kept (it holds the install log) unless `-RemoveData`. |

All eight scripts are **idempotent**: re-running one converges on the same state rather
than erroring or double-applying.

---

## What requires confirmation

Scripts 01, 02, 03, 04, 91, 92, 93 and 94 all change machine state. **None of them should
be run without the user explicitly saying yes to that specific script.** The scripts themselves
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

## The install broker: why a controller can't answer UAC

Installing a machine-scope application needs an administrator token. Windows asks for it
with a UAC consent dialog, and that dialog runs on the **secure desktop**, which discards
synthetic input as a matter of design. That is the whole point of it: it is what stops a
background process from clicking "Yes" on your behalf.

The consequence for this project is absolute, and worth stating plainly so nobody spends
an evening on it:

> **No gamepad remapper can ever answer a UAC prompt.** Steam Input, JoyToKey, reWASD,
> Controller Companion and everything like them work through `SendInput`. The secure
> desktop drops it. There is no button layout, mapping profile or accessibility setting
> that changes this.

There are only three real ways out, and the broker is the third:

1. **Turn off the secure desktop** (`PromptOnSecureDesktop=0`). The prompt then appears on
   the normal desktop where injected input reaches it — and so does input from anything
   else running on the machine. This trades away the exact protection the prompt exists to
   provide. **Not done here.**
2. **A hardware HID bridge** — e.g. a Pi Pico in USB gadget mode presenting as a real
   keyboard, driven by the controller. Real HID input does reach the secure desktop, and
   nothing is weakened. Viable, but it is a second device to build and keep alive.
3. **Pre-authorise the elevation, so no prompt is ever raised.** A scheduled task
   registered by an administrator with `RunLevel Highest` already holds an elevated token.
   A standard user triggering it sees no consent dialog, because consent was given once,
   at registration, with a keyboard present.

`04-install-broker.ps1` does (3). **UAC is left completely alone** — `EnableLUA=1` and
`PromptOnSecureDesktop=1` stay as Windows shipped them. Every other application on the
machine still prompts exactly as before.

### The shape of it

```
arcshell (no elevation)          SYSTEM (elevated, pre-authorised)
─────────────────────────        ─────────────────────────────────
arc-install.ps1
  writes queue\<id>.req    ──►
  schtasks /run \ARC\...   ──►   arc-install-worker.ps1
                                   reads the id
                                   looks it up in packages.json
                                   downloads the pinned https url
                                   checks the Authenticode signer
                                   runs it with the pinned args
                                   confirms verifyPath exists
  polls processed\…result  ◄──    writes the result
```

### The boundary, and how to not destroy it

The request file carries **an id and nothing else** — no URL, no path, no arguments, no
hash. All of those come from `packages.json`, which lives in a directory standard users
cannot write to, and the worker **refuses to run** if it detects that has stopped being
true.

That constraint is the entire security model. A broker that accepted a caller-supplied URL
or path — or a "skip verification" flag, or a "just this once" escape hatch — would be a
permanent SYSTEM backdoor for every process running as `arcshell`. Adding an entry to
`packages.json` is a deliberate decision to let the shell install that software with nobody
at the machine. Adding a general-purpose escape is a decision to hand `arcshell` the box.

### Provenance is pinned by publisher, not by hash

Each entry with a `url` must also name a `publisher`. After download, the file's
Authenticode signature must be **Valid** and its signer subject must contain that string,
or the file is deleted unused.

A SHA-256 pin was the obvious alternative and is the wrong default: it breaks the moment a
vendor ships a new build, and a check that breaks on every update is a check that gets
switched off. A publisher pin survives version bumps while still proving the bytes came
from who the manifest says they did. `sha256` remains available per entry, enforced *in
addition* to the publisher check, for installers that genuinely never change.

### No winget dependency

The bench image (Windows 11 IoT Enterprise LTSC Evaluation) ships **neither winget nor the
Microsoft Store** — confirmed on `DESKTOP-6BCSJ3P`, where `Microsoft.DesktopAppInstaller`
is not installed at all. Direct download is therefore the primary path. An entry may also
carry `wingetId`, which is preferred *only* when winget actually resolves, so one manifest
serves both the bench and the laptop.

### Known limits

* The worker runs as SYSTEM, which has no interactive user profile. **Machine-scope
  installs work; per-user installs do not** reliably, and when they do they land outside
  `arcshell`'s profile. Install user-scope-only software from an interactive session.
* Unsigned installers cannot be brokered. That is deliberate: there is no provenance to
  pin, so there is nothing separating "the vendor's installer" from "whatever answered
  that hostname today".
* The installer's exit code is treated as a claim, not evidence. Where an entry names a
  `verifyPath`, that file existing is what decides OK versus FAILED.

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
├── 04-install-broker.ps1                apply:  SYSTEM install broker (no-UAC installs)
├── 91-disable-shell-launcher.ps1        undo 01
├── 92-remove-test-account.ps1           undo 02
├── 93-remove-shell-launcher-config.ps1  undo 03  ← the recovery script
├── 94-remove-install-broker.ps1         undo 04
└── install-broker/
    ├── arc-install-worker.ps1           privileged half — runs as SYSTEM
    ├── arc-install.ps1                  unprivileged half — what ShellHost calls
    └── packages.json                    the only thing the broker will install
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
