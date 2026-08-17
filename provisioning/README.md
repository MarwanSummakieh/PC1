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

Everything runs against the account `marwanshell`: a standard, non-administrative, local,
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
| 2 | `02-create-test-account.ps1` | Creates the local user `marwanshell`. Prints its password **once**. | **Yes** |
| 3 | *(build `spike\ShellHost.exe`)* | Step 3 refuses to configure a shell path that does not exist, unless you pass `-AllowMissingShell`. | — |
| 4 | `03-apply-shell-launcher.ps1` | **The consequential one.** Sets the default shell to `explorer.exe`, sets `ShellHost.exe` as `marwanshell`'s shell, enables enforcement. Takes effect at the next sign-in. | **Yes — and read `RECOVERY.md` first** |
| 5 | `Ctrl`+`Alt`+`Del` → **Switch user** → sign into `marwanshell` | Keep the `brain` session alive. Do **not** sign out. | — |
| 6 | `04-install-broker.ps1` | Registers the SYSTEM scheduled task `\MarwanOS\marwan-install-broker` and creates `C:\ProgramData\MarwanOS`. **Independent of steps 1–5** — apply or remove it on its own, in either order. | **Yes** |

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

Run `94` **before** `92` if you are tearing the whole thing down: `92` deletes `marwanshell`,
and the broker's ACLs and task DACL reference that SID.

`91` refuses to run while enforcement is still on, unless you pass `-Force`. That
ordering matters: removing the optional feature first would take away the WMI provider
you need in order to clear the configuration.

---

## Script → undo map

| Apply | Undo | Reverses |
| --- | --- | --- |
| `01-enable-shell-launcher.ps1` | `91-disable-shell-launcher.ps1` | `Enable-WindowsOptionalFeature` → `Disable-WindowsOptionalFeature -FeatureName Client-EmbeddedShellLauncher -NoRestart`. Leaves `Client-DeviceLockdown` alone unless `-AlsoDisableDeviceLockdown`. |
| `02-create-test-account.ps1` | `92-remove-test-account.ps1` | `New-LocalUser` + `Add-LocalGroupMember` → `Remove-CimInstance` on `Win32_UserProfile` (deletes `C:\Users\marwanshell`) then `Remove-LocalUser`. **Destructive.** Prompts for typed confirmation. |
| `03-apply-shell-launcher.ps1` | `93-remove-shell-launcher-config.ps1` | `SetDefaultShell` + `SetCustomShell` + `SetEnabled($true)` → `SetDefaultShell("explorer.exe",0)` + `RemoveCustomShell(<sid>)` + `SetEnabled($false)`. Use `-All` to clear every per-SID entry when the state is unknown. |
| `04-install-broker.ps1` | `94-remove-install-broker.ps1` | `RegisterTaskDefinition` + directory ACLs → `DeleteTask` + delete the `\MarwanOS` task folder. `C:\ProgramData\MarwanOS` is kept (it holds the install log) unless `-RemoveData`. |

All eight scripts are **idempotent**: re-running one converges on the same state rather
than erroring or double-applying.

### The de-Microsofting scripts (20–22)

Numbered apart from the shell-launcher sequence because they are independent of it and of
each other, and undone by a script numbered **down** from 90 rather than up from it —
`20↔90`, `21↔89`, `22↔88` — since `91`–`94` were already spoken for. All of them are dry
runs unless given `-Apply`.

| Apply | Undo | Reverses |
| --- | --- | --- |
| `20-disable-defender.ps1` | `90-restore-defender.ps1` | Defender + SmartScreen off → back on. Needs Tamper Protection cleared by hand first. |
| `21-set-default-browser.ps1` | `89-restore-default-browser.ps1` | Registers MarwanOS as a browser and enforces it as the default for http/https via the `DefaultAssociationsConfiguration` policy → removes the registration and restores the previous policy value. |
| `22-remove-edge-browser.ps1` | `88-restore-edge-browser.ps1` | De-registers, hides and blocks Edge (optionally uninstalls it) → restores the registration, shortcuts and launchability from `C:\MarwanOS\backup`. An `-Uninstall` run is not reversible from the backup. |

**Order matters between 21 and 22.** Run 21 first. A machine where Edge has been
de-registered and nothing else claims `http` is a machine where a clicked link goes
nowhere at all.

**What makes a link open in the console's browser.** The registered handler is
`MarwanOpenUrl.exe` (built by `spike\ShellHostWeb\build-openurl.cmd`), not the shell
binary — registering the shell would make every clicked link start a *second* full shell
against the same WebView2 user-data folder and the same pad. The opener finds the shell
already running in the caller's session, hands it the URL over `WM_COPYDATA`, and exits;
the shell opens it as a browser tab through the same page path the pad uses. Both halves
have to be deployed together: a shell binary without the receiver will never answer, and
every link will silently land in the queue file instead.

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
marwanshell (no elevation)              SYSTEM (elevated, pre-authorised)
──────────────────────────────       ─────────────────────────────────────
shell / marwan-install.ps1
  writes queue\<ticket>.req    ──►
    verb=install
    package=steam
  schtasks /run \MarwanOS\...  ──►     marwan-install-worker.ps1
                                         reads the verb + its one argument
                                         validates it against the grammar
                                         ┌ install         → packages.json entry:
                                         │                   pinned https url, Authenticode
                                         │                   signer, pinned args, verifyPath
                                         ├ updates.install → WUA COM search/download/install
                                         ├ wifi.forget     → netsh wlan delete profile
                                         └ bt.forget       → BluetoothRemoveDevice
  tails processed\…progress   ◄──      appends one line per event
  reads processed\…result     ◄──      writes the result (verb, status, exitcode, detail)
```

### Verbs

The broker performs **exactly four verbs**, and nothing else. Anything else is `REJECTED`
without being executed.

| Verb | Argument | Grammar | What runs |
|---|---|---|---|
| `install` | `package` | `^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$` **and** present in `packages.json` | the manifest entry — pinned url, publisher check, pinned args, `verifyPath` |
| `updates.install` | *(none)* | — | Windows Update Agent COM: search `IsInstalled=0 and Type='Software' and IsHidden=0`, download, install. Result adds `installed=<n>` and `rebootRequired=` |
| `wifi.forget` | `ssid` | 1–32 chars, no control characters, no `"` | `netsh wlan delete profile name=<ssid>`, passed as an argument **array** |
| `bt.forget` | `address` | exactly 12 hex digits, no separators | `BluetoothRemoveDevice` (`bthprops.cpl`) |

The request file is key=value, `verb=` first. A request whose first line is not `verb=` is
read as a v1 request: the line is a package id and the verb is `install`. Unknown keys are
ignored.

### The boundary, and how to not destroy it

**No verb takes a path, a URL, a command line, an argument list or a hash.** `install`
names a manifest slug; everything downloaded or executed comes from `packages.json`, which
lives in a directory standard users cannot write to, and the worker **refuses to run** if
it detects that has stopped being true. `wifi.forget` and `bt.forget` take one identifier,
validated against a strict pattern and handed to a *fixed* program as an argument array —
never concatenated into a command string, so there is no shell to escape.
`updates.install` takes nothing at all.

That constraint is the entire security model. A broker that accepted a caller-supplied URL
or path — or a "skip verification" flag, or a "just this once" escape hatch — would be a
permanent SYSTEM backdoor for every process running as `marwanshell`. Adding an entry to
`packages.json` is a deliberate decision to let the shell install that software with nobody
at the machine. Adding a general-purpose verb is a decision to hand `marwanshell` the box.

### The progress file

The worker mirrors every log line for the ticket it is working on into
`processed\<ticket>.progress`, append-only, one line per event:

```
2026-08-16 17:41:25 Ticket 71a38499-…: picked up.
2026-08-16 17:41:25 Ticket 71a38499-…: v2 request, verb='updates.install'.
2026-08-16 17:41:28 Ticket 71a38499-…: 1 update(s) pending.
2026-08-16 17:41:38 Ticket 71a38499-…: install resultCode=2, installed=1, rebootRequired=False
```

It exists from the moment the ticket is picked up, so a caller can show something long
before the result file appears — a manifest download or a Windows Update run is minutes of
otherwise-silent waiting. The shell tails it for the on-screen progress.

**Readers must open it with `FileShare.ReadWrite`.** A reader that does not share write
access makes the worker's own append fail; during bring-up that silently swallowed a
progress line. The worker now retries a failed append, and `marwan-install.ps1` shows the
right way to read a file that is still being written.

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

One consequence reaches the screen. `admin.catalog` returns a `source` per package, and the
consent sheet prints it: the **host of the entry's `url`** (`cdn.akamai.steamstatic.com`,
`aka.ms`), falling back to `winget · <id>` only where an entry has no url. It printed the
`wingetId` until 2026-08-16, which named a package manager this machine does not have on
the one screen whose whole job is to say truthfully where the bytes are about to come from.

### Installers that need a person as well as a token (`interactive`), and installers already on disk (`path`)

Two manifest fields were forced by League of Legends (2026-08-16, BENCH-CHANGES Round 7):

* **`interactive: true`** — the worker still downloads and verifies the file, but then starts
  it **as SYSTEM inside the console session**, on the interactive desktop
  (`SetTokenInformation(TokenSessionId)` + `CreateProcessAsUser`, i.e. what `psexec -s -i`
  does), and waits until every SYSTEM-owned `postKill` process has been closed. The person
  sees the vendor's own setup window on the console and finishes there. Why: Riot's client
  installs Vanguard through an *elevated agent* that only exists while its installer runs
  elevated; a fresh unelevated Riot Client has none and falls back to a UAC prompt nobody
  can answer. Running the vendor's UI elevated is exactly what a UAC "Yes" would have
  produced — minus the dialog. It does mean a SYSTEM-owned window on the player's screen for
  the duration, which is why the consent sheet spells it out ("its own setup window opens on
  this screen") and why the entry has to be marked deliberately in the manifest.
* **`postKill`** — process *names* (never paths); the worker stops SYSTEM-owned processes with
  those names after the installer exits. Riot's installer ignores `--disable-auto-launch` and
  leaves its client running under the broker's token; before this the ticket never finished
  (`Start-Process -Wait` waits for the whole tree — the worker now waits for the installer
  process only). Only SYSTEM-owned processes are ever touched: the player's own copy of the
  same program is never killed.
* **`path`** instead of `url` — an installer some other program has already put on disk
  (absolute; may end in a `*.exe` glob, newest match wins). Publisher pin still mandatory,
  and the file is **copied into the broker cache before it is verified and run**, so a swap
  in the (typically user-writable) source folder between check and run is impossible.
  `admin.catalog` reports such entries with `ready:false` + `readyNote` until the file
  exists, and the shell shows the row disabled rather than letting a hold end in FAILED.

### Known limits

* The worker runs as SYSTEM, which has no interactive user profile. **Machine-scope
  installs work; per-user installs do not** reliably, and when they do they land outside
  `marwanshell`'s profile. Install user-scope-only software from an interactive session.
* Unsigned installers cannot be brokered. That is deliberate: there is no provenance to
  pin, so there is nothing separating "the vendor's installer" from "whatever answered
  that hostname today".
* The installer's exit code is treated as a claim, not evidence. Where an entry names a
  `verifyPath`, that file existing is what decides OK versus FAILED.
* `wifi.forget` and `bt.forget` depend on hardware and services that may not be there.
  On a box with no WLAN service `netsh` fails and the result is `FAILED` carrying netsh's
  own text; `bt.forget` for an unknown device returns `FAILED` with the Win32 code (1168,
  "Element not found"). Neither is treated as success.
* `updates.install` runs the Windows Update Agent as SYSTEM. It can take a long time and
  may report `rebootRequired=true`; restarting is left to the shell and the user.

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
├── RECOVERY.md                          READ BEFORE SIGNING INTO marwanshell
├── MACHINE-CHANGES.md                   change log — fill in as you go
├── 01-enable-shell-launcher.ps1         apply:  optional feature
├── 02-create-test-account.ps1           apply:  marwanshell local account
├── 03-apply-shell-launcher.ps1          apply:  custom shell for marwanshell only
├── 04-install-broker.ps1                apply:  SYSTEM broker (no-UAC privileged verbs)
├── 20-disable-defender.ps1              apply:  Defender + SmartScreen off
├── 21-set-default-browser.ps1           apply:  MarwanOS registered + default for http/https
├── 22-remove-edge-browser.ps1           apply:  Edge de-registered, hidden, blocked
├── 88-restore-edge-browser.ps1          undo 22
├── 89-restore-default-browser.ps1       undo 21
├── 90-restore-defender.ps1              undo 20
├── 91-disable-shell-launcher.ps1        undo 01
├── 92-remove-test-account.ps1           undo 02
├── 93-remove-shell-launcher-config.ps1  undo 03  ← the recovery script
├── 94-remove-install-broker.ps1         undo 04
└── install-broker/
    ├── marwan-install-worker.ps1           privileged half — runs as SYSTEM
    ├── marwan-install.ps1                  unprivileged half — what ShellHost calls
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
