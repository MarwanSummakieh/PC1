# BENCH-CHANGES.md — change log for the test bench

Machine: **DESKTOP-6BCSJ3P**, <bench-ip>
OS: Windows 11 IoT Enterprise LTSC **Evaluation**, build 26100.1742 (fresh install, 2026-08-14)
Disposable. This machine exists to be broken. The laptop (`DESKTOP-AGB796T`) is the one that must stay working.

Admin account on this box is also called `brain`, but it is a **different account on a different
machine** from the laptop's `brain`. The Shell Launcher guard treats the name as protected either
way, so it was never a target here.

## Access

Reached over SSH from the laptop, key auth, **session runs elevated** (verified
`IsInRole(544) = True`). This matters for recovery: sshd spawns its own process and does not depend
on the interactive shell, so **SSH still works even if the replaced shell is broken**. That is the
primary recovery path for this machine, ahead of anything involving a keyboard at the console.

    ssh -i C:\Users\brain\.ssh\arcbench -o IdentitiesOnly=yes -o IdentityAgent=none brain@<bench-ip>

## Applied changes

| # | Date applied | Change | Exact command | Undo command |
|---|---|---|---|---|
| B1 | 2026-08-14 00:31 | Installed **OpenSSH Server**, set to auto-start, firewall rule for TCP 22, public key in `administrators_authorized_keys` | `Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0` + `Set-Service sshd -StartupType Automatic` | `Remove-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0`; `Remove-NetFirewallRule -Name sshd` |
| B2 | 2026-08-14 00:45 | Set SSH **DefaultShell to PowerShell** (was cmd.exe) | `reg add "HKLM\SOFTWARE\OpenSSH" /v DefaultShell /t REG_SZ /d "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" /f` | `reg delete "HKLM\SOFTWARE\OpenSSH" /v DefaultShell /f` |
| B3 | 2026-08-14 00:48 | Enabled **Shell Launcher** optional feature. NOTE: enabling parent and child in separate calls FAILED with "parent features are disabled"; `/all` in a single DISM call was required. Payload (`Eshell.exe`, WMI class) did **not** appear until **two** reboots had completed. | `dism /online /enable-feature /featurename:Client-EmbeddedShellLauncher /all /norestart` | `provisioning\91-disable-shell-launcher.ps1` |
| B4 | 2026-08-14 00:50 | Created **`marwanshell`** local account, standard user, not an admin. Password written to `C:\MarwanOS\marwanshell-password.txt` **on the bench only** — deliberately never transmitted. | `New-LocalUser -Name marwanshell ...` + `Add-LocalGroupMember -Group Users` | `provisioning\92-remove-test-account.ps1` |
| B5 | 2026-08-14 00:58 | **Applied Shell Launcher to `marwanshell` only.** Enforcement ON. Default shell for every other account (incl. `brain`) explicitly set to `explorer.exe`. | `03-apply-shell-launcher.ps1 -ShellPath C:\MarwanOS\MarwanShellHost.exe -UserName marwanshell` | `provisioning\93-remove-shell-launcher-config.ps1` |

## Live configuration after B5

    IsEnabled       : True
    Default shell   : explorer.exe (action 0 = RestartShell)
    marwanshell shell  : C:\MarwanOS\MarwanShellHost.exe
    exit 0 -> RestartShell   exit 2 -> RestartDevice   exit 3 -> ShutdownDevice
    DefaultAction   : 0 (RestartShell)

SIDs: `marwanshell` = `S-1-5-21-3269924712-1620568365-1663035878-1002`,
`brain` = `...-1001` (confirmed to have **no** Shell Launcher entry).

## Remote undo, if the bench needs rescuing

Run from the laptop; does not require anyone to be at the bench:

    ssh ... brain@<bench-ip> 'powershell -NoProfile -ExecutionPolicy Bypass -File C:\MarwanOS\provisioning\93-remove-shell-launcher-config.ps1'

## Facts still to be recorded (M1 observations)

| Fact | Value |
|---|---|
| What `marwanshell` sign-in actually shows | |
| `Ctrl`+`Shift`+`Esc` reaches Task Manager under the custom shell | |
| Launch/return of a child process works with no explorer | |
| exit 0 → shell restarts | |
| exit 2 → device restarts | |
| exit 3 → device shuts down | |
| Time from sign-in to shell visible | ~10 s (autologon 02:20:41 → page loaded 02:20:53) |

---

## Round 2 — kiosk polish (2026-08-14 ~02:05–02:20)

| # | Change | Command | Undo |
|---|---|---|---|
| B6 | **Autologon** as `marwanshell` (blank password, so nothing sensitive is stored in the registry) | `AutoAdminLogon=1`, `DefaultUserName=marwanshell`, `DefaultDomainName=DESKTOP-6BCSJ3P`, `DefaultPassword=""` under `HKLM\...\Winlogon` | Set `AutoAdminLogon=0` |
| B7 | **Passwordless enforcement off** — this was the actual reason autologon was ignored on the first attempt | `HKLM\...\Winlogon\PasswordLess\Device\DevicePasswordLessBuildVersion = 0` | Set back to `2` |
| B8 | **Boot UI removed** — no Windows logo or spinner | `bcdedit /set {globalsettings} bootuxdisabled on`; `bcdedit /set {current} quietboot on` | `bcdedit /deletevalue ...` |
| B9 | **Lock screen + first-logon animation disabled** | `Personalization\NoLockScreen=1`; `Policies\System\EnableFirstLogonAnimation=0` | Delete both values |
| B10 | **Accounts hidden from the logon screen** (`Brain`, `Administrator`). Accounts still exist and still work — they are only invisible in the UI. | `Winlogon\SpecialAccounts\UserList\Brain = 0`, `\Administrator = 0` | Delete the values |
| B11 | **Logitech Download Assistant startup removed**; **NVIDIA Control Panel appx removed** | `Remove-ItemProperty HKLM\...\Run 'Logitech Download Assistant'`; `Remove-AppxPackage -AllUsers NVIDIACorp.NVIDIAControlPanel` | Reinstall from vendor / re-add Run value |

### Why `brain` was NOT deleted, despite being asked

The request was to remove all other accounts, including admins. **Refused, deliberately.** `brain` is
the only enabled administrator on this machine and the account SSH authenticates as. Deleting it would:

* end all remote administration of the bench (no elevation, no SSH),
* leave `marwanshell` — a standard user — unable to install, repair, or reconfigure anything,
* make a broken shell unrecoverable without Safe Mode or reinstalling from USB,
* block Windows Update, which needs an administrator.

Hiding it from the logon screen (B10) gives the same single-account appearance with none of that risk,
and is the correct pattern for the shipped device: **one hidden admin, one shell account.** If the
account genuinely must go later, the safe order is: create a replacement hidden admin, verify SSH and
elevation on it, and only then remove the old one.

### B12 — NVIDIA container service disabled (2026-08-14 11:26)

Removing the NVIDIA Control Panel app in B11 left the driver's container process showing a permanent
toast in the bottom-left of the screen:

> **NVIDIA Control Panel is not found** — Click here to install NVIDIA Control Panel from Microsoft store.

Confirmed by enumerating windows **inside session 1** (an SSH session in session 0 cannot see them —
window enumeration is desktop-bound). It was a `#32770` dialog at `(41,1330) 414x83` owned by
`NVDisplay.Container`, present in every sample.

It could not be resolved on its own terms: **this LTSC image has no Microsoft Store**, there is no NVIDIA
package payload left in the driver store, and NVIDIA exposes no registry switch to suppress the prompt.

    Set-Service -Name NVDisplay.ContainerLocalSystem -StartupType Disabled
    Stop-Service -Name NVDisplay.ContainerLocalSystem -Force

**Undo:** `Set-Service -Name NVDisplay.ContainerLocalSystem -StartupType Automatic; Start-Service ...`

Verified afterwards: no visible dialog, zero `NVDisplay.Container` processes, shell alive at
3440×1440. **Games are unaffected** — 3D rendering goes through the kernel driver (`nvlddmkm`), not this
user-mode container, which exists mainly to back the control panel we no longer have. What is lost is the
ability to change NVIDIA-specific settings; HDR and display modes are driven through Windows APIs in
Settings, not NVIDIA's.

The alternative, if NVIDIA features are ever wanted back: re-enable the service and reinstall the control
panel by re-running NVIDIA's full driver installer (the Store route is unavailable on this SKU).

### Verified behaviour after round 2

Power on → no Windows boot logo → no logon screen → `marwanshell` signed in automatically → MarwanOS boot
sequence → home screen. No Logitech or NVIDIA UI processes in the session.

---

## Round 3 — install broker (2026-08-14 ~22:00)

| # | Change | Command | Undo |
|---|---|---|---|
| B13 | **Install broker applied.** Created `C:\ProgramData\MarwanOS` (inheritance off: SYSTEM/Administrators full, `marwanshell` RX, `marwanshell` W on `queue\` only), deployed `marwan-install-worker.ps1` + `packages.json`, registered on-demand task `\MarwanOS\marwan-install-broker` as **SYSTEM / RunLevel Highest**, task DACL granting `marwanshell` read+execute. | `04-install-broker.ps1` | `94-remove-install-broker.ps1` (add `-RemoveData` to also delete `C:\ProgramData\MarwanOS`) |

**Why:** the UAC consent dialog runs on the secure desktop, which discards synthetic input — so no
gamepad remapper can ever answer it. The broker pre-authorises elevation once, at provisioning time,
so the shell never faces a prompt. **UAC itself is untouched** on this machine.

### No winget on this image

`Microsoft.DesktopAppInstaller` is **not installed** and there is no Microsoft Store to get it from
(same SKU limitation as B12). The broker therefore does direct HTTPS download as its primary path and
pins provenance by **Authenticode publisher**, not by SHA-256 — a hash pin breaks on every vendor
version bump, and a check that breaks constantly is a check that gets switched off. Manifest entries
still carry `wingetId`, used only where winget actually resolves, so the same file works on the laptop.

### Two bugs found and fixed during bring-up

1. `04` passed a **SID string** to `FileSystemAccessRule`, whose string overload expects an account
   *name* — `IdentityNotMappedException`. Fixed by passing the `SecurityIdentifier` object.
2. The worker's "is the manifest writable by non-admins?" guard tested against a mask containing
   `FullControl` and `Modify`. Those are **composite** values that include the read bits, so
   `ReadAndExecute -band FullControl` is non-zero and *every* read-only ACE was flagged as a writer.
   `marwanshell`'s legitimate RX tripped it and the worker refused to run. Fixed to test atomic write
   bits only (`WriteData`, `AppendData`, `Delete`, `DeleteSubdirectoriesAndFiles`, `ChangePermissions`,
   `TakeOwnership`) — `Modify`/`FullControl` are still caught, because both contain `WriteData`.
   Note it failed **closed**, which is the correct direction for that guard to fail.

### Verified (2026-08-14 22:02–22:04)

| Test | Result |
|---|---|
| ACLs on `C:\ProgramData\MarwanOS` | `SYSTEM:(OI)(CI)(F)`, `Administrators:(OI)(CI)(F)`, `marwanshell:(OI)(CI)(RX)` |
| ACLs on `queue\` | adds `marwanshell:(OI)(CI)(W)` — create only, no modify or delete |
| Task DACL | `marwanshell` granted `0x1200a9` (read + execute), not modify |
| Request for an id not in the manifest | `REJECTED — not in manifest` |
| Request containing `C:\Windows\System32\cmd.exe /c whoami` | `REJECTED — malformed id`, not executed |
| **Full loop as `marwanshell`**, non-elevated (S4U task, `RunLevel=0`, no password used) | queue write → task trigger → SYSTEM worker → result read back. Exit 2 (rejected). **No UAC prompt.** |

### B14 — positive path verified: Steam installed through the broker (2026-08-14 22:06)

Requested by **`marwanshell`, non-elevated** (S4U task, `RunLevel=0`). `steam.exe` absent before, present
after. **No UAC prompt, nobody at the machine.**

    22:06:34  installing 'Steam' (steam)
    22:06:34  downloading https://cdn.akamai.steamstatic.com/client/installer/SteamSetup.exe
    22:06:36  downloaded 2.3 MB -> C:\ProgramData\MarwanOS\cache\steam_SteamSetup.exe
    22:06:36  provenance OK (publisher pin 'Valve')
    22:06:36  C:\ProgramData\MarwanOS\cache\steam_SteamSetup.exe /S
    22:06:37  verified C:\Program Files (x86)\Steam\steam.exe exists
    client exit 0

Every stage of the chain is now exercised: queue write as a standard user, task trigger, SYSTEM worker,
manifest lookup, HTTPS download, Authenticode publisher check, silent install, `verifyPath` confirmation,
result read back by the caller.

**Undo for the Steam install itself** (separate from removing the broker):
`C:\Program Files (x86)\Steam\uninstall.exe /S`

---

## Round 4 — broker becomes a privileged-action broker (2026-08-16 ~17:35–17:42)

| # | Change | Command | Undo |
|---|---|---|---|
| B15 | **Verb-based worker deployed over the v1 worker.** `C:\ProgramData\ARC\arc-install-worker.ps1` replaced with the v2 build from `provisioning\install-broker\marwan-install-worker.ps1` (file name and root deliberately **not** renamed — the task `\ARC\arc-install-broker` points there). Adds the verb allowlist `install` / `updates.install` / `wifi.forget` / `bt.forget`, per-ticket `processed\<ticket>.progress`, and `verb=` (+ `installed=` / `rebootRequired=`) in the result. Old worker backed up first. | `Copy-Item C:\ProgramData\ARC\arc-install-worker.ps1 C:\ProgramData\ARC\arc-install-worker.v1.ps1`<br>`Copy-Item C:\ArcOS\diag\marwan-install-worker.ps1 C:\ProgramData\ARC\arc-install-worker.ps1 -Force` | `Copy-Item C:\ProgramData\ARC\arc-install-worker.v1.ps1 C:\ProgramData\ARC\arc-install-worker.ps1 -Force`<br>(v1 backup sha256 `A31761B846912854CE82D5AB0F8EC30E2E0370864AA06BE79F8D61B28F6E883C`) |

Nothing else on the bench changed: the task, its principal, its DACL, the root ACLs and
`packages.json` are all untouched. The scratch scripts live in `C:\ArcOS\diag\` and
`C:\ArcOS\diag\out\` (that out directory was created with `Users:Modify` so the test cases
could write transcripts; `Remove-Item -Recurse C:\ArcOS\diag\out` to undo).

**Two temporary scheduled tasks were registered and both were deleted again** at the end
of the run:

* `\ARC\brokercase` — runs a test case **as `arcshell`** with `LogonType 3` (S4U, no
  password stored) and `RunLevel 0` (LUA, standard-user token). Same pattern as B13/B14.
* `\ARC\arc-install-broker-whatif` — identical to the real broker task but with
  `-WhatIfOnly`, DACL `(A;;GRGX;;;<arcshell SID>)` so `arcshell` could trigger it. Used to
  exercise `updates.install` search-only before deciding to run it for real.

`schtasks /query` after cleanup shows `\ARC\arc-install-broker` as the only task under
`\ARC`.

### Verified 2026-08-16 17:37–17:42 — every case run as `arcshell`, non-elevated

Each case logged `whoami : DESKTOP-6BCSJ3P\arcshell`, `elevated : False`. **No UAC prompt,
nobody at the machine.**

| Case | Request (written straight into `queue\` unless noted) | Result |
|---|---|---|
| v1 compat | bare `steam` | `verb=install status=OK exitcode=2 detail=verified C:\Program Files (x86)\Steam\steam.exe` — first line without `verb=` read as a package id |
| install (real) | via `marwan-install.ps1 -Verb install -PackageId vcredist2015-x86` | `status=OK exitcode=0 detail=installed`; 13.3 MB downloaded, publisher pin `Microsoft Corporation` verified, client exit 0 |
| `wifi.forget` malformed | `ssid=he said "hi"` | `status=REJECTED detail=malformed ssid` — netsh never invoked |
| `bt.forget` malformed | `address=00:11:22:33:44:55` | `status=REJECTED detail=malformed address` |
| unknown verb | `verb=frobnicate` + `package=steam` | `status=REJECTED detail=unknown verb` — the package key ignored |
| `bt.forget` well-formed | `address=AABBCCDDEEFF` | `status=FAILED exitcode=1168 detail=BluetoothRemoveDevice returned 1168 (Element not found)` — proves the P/Invoke path, and that a real failure is not dressed up as success |
| `updates.install` WhatIf | via the temporary `-WhatIfOnly` task | `status=OK installed=0 detail=whatif: 1 update(s) available, nothing downloaded or installed` |
| `updates.install` **real** | via `marwan-install.ps1 -Verb updates.install` | `status=OK exitcode=2 installed=1 rebootRequired=false`; installed KB2267602 (Defender security intelligence). Run for real because the WhatIf search showed **one** small update and no reboot |

Progress file for the real `updates.install` ticket, verbatim:

    2026-08-16 17:41:25 Ticket 71a38499-…: picked up.
    2026-08-16 17:41:25 Ticket 71a38499-…: v2 request, verb='updates.install'.
    2026-08-16 17:41:25 Ticket 71a38499-…: searching Windows Update (IsInstalled=0 and Type='Software' and IsHidden=0).
    2026-08-16 17:41:28 Ticket 71a38499-…: 1 update(s) pending.
    2026-08-16 17:41:28 Ticket 71a38499-…:   pending - Security Intelligence Update for Microsoft Defender Antivirus - KB2267602 …
    2026-08-16 17:41:28 Ticket 71a38499-…: downloading 1 update(s).
    2026-08-16 17:41:28 Ticket 71a38499-…: 1 of 1 downloaded; installing.
    2026-08-16 17:41:38 Ticket 71a38499-…:   installed - Security Intelligence Update for Microsoft Defender Antivirus - KB2267602 …
    2026-08-16 17:41:38 Ticket 71a38499-…: install resultCode=2, installed=1, rebootRequired=False
    2026-08-16 17:41:38 Ticket 71a38499-…: done.

### Bug found and fixed during bring-up: a dropped progress line

The first `wifi.forget` run produced a progress file with **3** lines where the main log had
**4** — the `v2 request, verb='wifi.forget'` line was missing. Cause: the caller polls the
progress file every 500 ms while the worker appends to it, and a reader that does not open
with `FileShare.ReadWrite` makes the worker's `Add-Content` throw. The original code wrapped
that append in `try { } catch { }`, so the line was silently dropped.

Fixed on both sides: the worker retries a failed append (8 × 40 ms), and the client reads
through a `FileStream` opened `FileShare.ReadWrite`. Re-running the same case under the same
aggressive polling now yields all 4 lines. **The host must read the progress file the same
way** — this is the kind of failure that shows up as a UI that silently misses a step.

### Note on a concurrent request

At 17:41:52 a `vcredist2015-x64` install appeared in the log that this session did not
request — the host worker exercising `admin.request` against the same bench at the same
time. It completed `OK (exit 0)`, which is incidental corroboration that the new worker
serves the host path too.

## Round 5 — the host reaches the broker, and takes the browser's permission prompts (2026-08-16 ~17:41–17:57)

| # | Change | Command | Undo |
|---|---|---|---|
| B16 | **Two redistributables installed through the broker, by the host's own `admin.request`.** `vcredist2015-x64` (17:41:52, requested as `brain`) and `vcredist2015-x86` (17:45:35, requested as **`arcshell`, non-elevated, no UAC**). Both are manifest entries; nothing outside `packages.json` was touched. | `systemapi-cli.exe admin.request verb=install package=vcredist2015-x64`<br>`systemapi-cli.exe admin.request verb=install package=vcredist2015-x86` | Settings → Apps, or `"%ProgramData%\Package Cache\...\VC_redist.x64.exe" /uninstall /quiet` (same for x86) |
| B17 | **Permission-bridge test rig staged** in `C:\ArcOS\web\permtest\` (test build of the host as `MarwanShellHostWeb-perm.exe`, the three WebView2 DLLs, `mosnav.js`, and a throwaway `index.html` that is NOT the shell). It runs with its own log and its own browser profile `C:\Users\arcshell\AppData\Local\ArcOS\WebView2-permtest[-content]`. | `scp` into `C:\ArcOS\web\permtest\`, driven by `C:\ArcOS\diag\permtest.ps1` | `Remove-Item -Recurse C:\ArcOS\web\permtest`, `Remove-Item -Recurse C:\Users\arcshell\AppData\Local\ArcOS\WebView2-permtest*` |

The live shell (`C:\ArcOS\web\v10\ArcShellHostWeb-v10.exe`), its profile and its log were not
touched, and neither were the broker root, the task, its DACL or `packages.json`.

**Three temporary scheduled tasks, all removed again** (`schtasks /query` shows none left):
`MarwanOsAdminProbe` and `MarwanOsPermProbe` (both `arcshell`, `LogonType Interactive`,
`RunLevel Limited`, no password) and `MarwanOsProbeTest` (a one-line registration probe).
An `HttpListener` on `http://127.0.0.1:8099/` ran inside the elevated SSH session for the
length of the browser test; no `netsh http urlacl` entry was added and nothing survives it.
Transcript at `C:\Users\Public\marwanos-admin-probe.txt`.

**S4U is not available on this box today.** `Register-ScheduledTask -LogonType S4U` returns
`0x80070005` and `schtasks /ru arcshell /np` prompts for a password instead of storing none —
so B13/B14's S4U pattern was run as `LogonType Interactive` + `RunLevel Limited` instead. Same
`arcshell` standard-user token (`elevated : False`), in the session the shell itself runs in,
still with no password anywhere.

### Verified 17:45–17:46 — `admin.*` as `arcshell`, non-elevated, session 2

| Case | Result |
|---|---|
| `admin.status` | `available=true`, `root=C:\ProgramData\ARC`, `taskPath=\ARC\arc-install-broker`, `worker=arc`, `packageCount=3` — the MarwanOS root is probed first and correctly falls through to the ARC one |
| `admin.catalog` | 3 packages; `steam installed=true` (verifyPath exists), both vcredists `installed=false` (no verifyPath in the manifest) |
| `admin.request verb=rm-rf` | `bad_request` — "unknown verb 'rm-rf'; allowed: install, updates.install, wifi.forget, bt.forget". Nothing written to `queue\` |
| `admin.request verb=install package=not-in-manifest` | `bad_request` — not in `packages.json`. Nothing written to `queue\` |
| `admin.request verb=wifi.forget ssid=MarwanOsNoSuchNetwork` | job → `status=FAILED exitcode=1 detail=The Wireless AutoConfig Service (wlansvc) is not running.` (6 progress lines) |
| `admin.request verb=install package=vcredist2015-x86` | job → `status=OK exitcode=0 detail=installed` in 11.9 s, 9 progress lines streamed from `processed\<ticket>.progress` |
| `admin.request verb=bt.forget address=AABBCCDDEEFF` (as `brain`, 17:56) | job → `status=FAILED exitcode=1168 detail=BluetoothRemoveDevice returned 1168 (Element not found)` — a FAILED broker result still resolves as `ok:true`; only transport failures are errors |

### Verified 17:53 — WebView2 permission prompts answered by the shell, not by Edge

Test host as `arcshell`, content tab on `http://127.0.0.1:8099/` (a trustworthy origin, so the
page is allowed to ask at all). Verbatim from the host log:

    [PERM] tab 1 permission prompts are the shell's now
    [PERM] no saved decisions (…\WebView2-permtest-content\permissions.json)
    [PAGE] EV permissions items=[] store=…\permissions.json
    [PERM] asking the shell: id=1 tab=1 http://127.0.0.1:8099 geolocation userInitiated=False
    [PAGE] EV permission id=1 tab=1 origin=http://127.0.0.1:8099 kind=geolocation …
    [PERM] http://127.0.0.1:8099 geolocation allow remember
    [PAGE] EV permissions items=[{"origin":"http://127.0.0.1:8099","kind":"geolocation","allow":true}]
    [BROWSER] tab 1 navigation ok … -> http://127.0.0.1:8099/perm.html
    [PERM] http://127.0.0.1:8099 geolocation allow remembered (no prompt drawn)
    [PERM] asking the shell: id=2 tab=1 http://127.0.0.1:8099 notifications userInitiated=False
    [PERM] http://127.0.0.1:8099 notifications allow remember
    [PERM] forgot 1 decision(s) for http://127.0.0.1:8099 geolocation

and the store on disk afterwards:

    {"version":1,"items":[{"origin":"http://127.0.0.1:8099","kind":"notifications","allow":true}]}

No Edge permission bubble appeared at any point: the deferral is taken on every request and
always completed, which is what suppresses the built-in UI.

---

## Round 6 — the whole chain, driven by the pad (2026-08-16 ~18:26–18:56)

| # | Change | Command | Undo |
|---|---|---|---|
| B18 | **v11 test deploy** in `C:\ArcOS\web\v11\` — 35 files: `MarwanShellHostWeb-v11.exe`, the three WebView2 DLLs, `systemapi-cli.exe`, `index.html`, `boot.html`, the flattened `ui\*.css` / `ui\*.js`, the woff2 fonts and `marwanos.png`. A **new directory**: `C:\ArcOS\web\v10\` (the live shell, its assets, its profile and its log) and the Shell Launcher config were not touched, and the exe name differs from the live `ArcShellHostWeb-v11.exe` so the two never collide by process name. | `spike\ShellHostWeb\build.cmd MarwanShellHostWeb-v11.exe` then `scp` into `C:\ArcOS\web\v11\`. The deployed exe hashes `CC262D50DC89B6CAFAEE53C91F44A5824BC82C94D9C6D66B600F2F135EE89DD5` — that identifies **this artefact**, not the source: csc stamps a fresh MVID, so rebuilding the same code gives a different hash. | `Remove-Item -Recurse C:\ArcOS\web\v11`; `Remove-Item -Recurse C:\Users\arcshell\AppData\Local\ArcOS\WebView2-v11*` |
| B19 | **`vcredist2015-x64` installed again through the broker**, this time by a *pad gesture* rather than a CLI call: Settings → Add software → hold Allow. Manifest entry, 24.4 MB from `aka.ms`, publisher pin `Microsoft Corporation`, `status=OK exit 0`. It was already installed (B16); the bootstrapper re-ran and reported success. | (no command — the shell did it) | `"%ProgramData%\Package Cache\...\VC_redist.x64.exe" /uninstall /quiet` |
| B20 | **Test scripts and logs added under `C:\ArcOS\diag\`**: `v11walk.ps1`, `v11perm.ps1`, `shrink.ps1`, `_camv11.ps1`, and the logs `grant-e2e.log`, `updates-check.log`, `updates-sheet.log`, `web-perm.log`, plus `shots-v11\small\*.jpg` (the full-size PNG frames were deleted after they were read; ~1 MB of jpg remains). | `scp` + the runs below | `Remove-Item C:\ArcOS\diag\v11walk.ps1, C:\ArcOS\diag\v11perm.ps1, C:\ArcOS\diag\shrink.ps1, C:\ArcOS\diag\_camv11.ps1, C:\ArcOS\diag\grant-e2e.log, C:\ArcOS\diag\updates-check.log, C:\ArcOS\diag\updates-sheet.log, C:\ArcOS\diag\web-perm.log`; `Remove-Item -Recurse C:\ArcOS\diag\shots-v11` |

**Four temporary scheduled tasks, all unregistered again** (`schtasks /query` shows nothing matching
`MarwanOs`, and `\ARC` still holds only `arc-install-broker`): `MarwanOsV11Walk`, `MarwanOsV11WalkCam`,
`MarwanOsV11Perm`, `MarwanOsV11PermCam` — all `arcshell`, `LogonType Interactive`, `RunLevel Limited`,
no password stored (S4U is still refused here, B17). An `HttpListener` on `http://127.0.0.1:8099/` ran
inside the elevated SSH session for the length of the browser test and nothing survives it. Windows
Update was **searched** but nothing was installed from it.

Every test instance ran `--windowed --no-boot --no-pad --no-fg-gate` with its own `--log` and its own
`--user-data`. `--no-fg-gate` is needed because the live full-screen shell owns the foreground and the
gate would otherwise drop every synthetic step; `--no-pad` so the test instance never reads the real HID
and cannot be contaminated by (or contaminate) the live shell.

### Verified 18:33 — install, end to end, by pad tokens only

Walk (`--walk-gap=2200`), from the home screen:

    guide,right,right,right,right,right,select,
    down,down,down,down,down,down,down,right,down,select,
    right,hold:150:launch:cross,hold:1400:launch:cross

Verbatim from `C:\ArcOS\diag\grant-e2e.log`:

    18:33:45.756  [PAGE] scope push grant (2 focusables, depth 4)
    18:33:45.760  [PAGE] focus grant| deny
    18:33:45.760  [PAGE] grant: asking - system / Install Visual C++ 2015-2022 Redistributable (x64)?
    18:33:47.997  [PAGE] focus grant| allow
    18:33:50.231  [PAGE] grant: hold started (pad)
    18:33:50.429  [PAGE] grant: hold cancelled - released before 800 ms      <- the tap: nothing happens
    18:33:52.464  [PAGE] grant: hold started (pad)
    18:33:53.268  [PAGE] grant: ALLOWED - Install Visual C++ 2015-2022 Redistributable (x64)?
    18:33:53.274  [PAGE] broker: requesting install {"verb":"install","package":"vcredist2015-x64"}
    18:33:53.320  [SYS]  ok  admin.request reqId=s13 in 45 ms
    18:34:10.496  [PAGE] broker: install -> OK exit 0 - installed

and the broker's own `processed\51385f13-….progress`, written while the shell tailed it:

    18:33:54 picked up.
    18:33:54 v2 request, verb='install'.
    18:33:54 installing 'Visual C++ 2015-2022 Redistributable (x64)' (vcredist2015-x64).
    18:33:54 downloading https://aka.ms/vs/17/release/vc_redist.x64.exe
    18:34:06 downloaded 24.4 MB -> C:\ProgramData\ARC\cache\vcredist2015-x64_vc_redist.x64.exe
    18:34:07 provenance OK (publisher pin 'Microsoft Corporation')
    18:34:09 OK (exit 0) installed

The consent sheet was photographed from inside session 1 (`C:\ArcOS\diag\shots-v11\small\grant-25.jpg`,
`grant-26.jpg` shows the hold fill part-way across the Install button). The **Source** fact reads
`aka.ms`, not `winget · Microsoft.VCRedist.2015+.x86` — see the fix below.

### `--walk` had to learn to hold

The host's walk vocabulary was one bare pad action per token, dispatched as a press with **no release**.
That cannot express either half of the gesture the sheet is built on: a tap was indistinguishable from an
infinite hold, and the thing most worth proving — that a press on its own does nothing — could not be
driven at all. `ShellHostWeb.cs` now also accepts `hold:<ms>:<action>[:<button>]`, `press:…` and
`release:…`, the same spelling `.stage/serve.mjs` uses for its `?pad=` tokens. The release is pumped from
the existing 200 ms tick, so it is ordered against the walk's own steps rather than racing them.

### Verified 18:41–18:49 — Windows Update, and the row that refuses to be pressed

* Before a search, `down` off "Check for updates" logs **`no target below`**: the install row is
  `aria-disabled` and the focus manager skips disabled items, so the button genuinely cannot be reached
  rather than being reachable-and-inert.
* After the search (~4–15 s) the row becomes reachable and its sub-line reads
  *"Hands 10 updates to the MarwanOS broker, which installs them as administrator"*.
* Pressing it opened the sheet — `grant: asking - system / Install 10 Windows updates?` — and Circle
  answered it: `grant: DENIED`, screen back to "Nothing was installed."

**Nothing was installed from Windows Update.** The rule for this run was ≤3 small updates; the search
found **10**, almost all Intel chipset/driver packages, so the install was deliberately not taken.

### Verified 18:52 — a web page's permission answered by the shell, on the real shell

Test host on `http://127.0.0.1:8099/perm.html` (a trustworthy origin, so the page may ask at all), walk
`up,down,up,select,down,right,hold:1200:launch:cross,back` at 4000 ms. Verbatim from
`C:\ArcOS\diag\web-perm.log`:

    18:52:14.705  [PERM] tab 1 permission prompts are the shell's now
    18:52:16.072  [PERM] no saved decisions (…\WebView2-v11perm-content\permissions.json)
    18:52:16.072  [PERM] asking the shell: id=1 tab=1 http://127.0.0.1:8099 geolocation userInitiated=False
    18:52:16.074  [PAGE] MarwanBrowser content HIDDEN - suspend:permission
    18:52:16.084  [PAGE] grant: asking - web / Let 127.0.0.1:8099 use where this console is?
    18:52:21.361  [PERM] asking the shell: id=2 tab=1 http://127.0.0.1:8099 notifications userInitiated=False
    18:52:30.408  [PAGE] pad select -> grant: Remember for this site
    18:52:42.590  [PAGE] grant: hold started (pad)
    18:52:43.395  [PAGE] grant: ALLOWED - Let 127.0.0.1:8099 use where this console is? (remembered)
    18:52:43.407  [PERM] http://127.0.0.1:8099 geolocation allow remember
    18:52:43.408  [PAGE] grant: asking - web / Let 127.0.0.1:8099 use to send you notifications?
    18:52:46.651  [PAGE] grant: DENIED - Let 127.0.0.1:8099 use to send you notifications?
    18:52:46.668  [PERM] http://127.0.0.1:8099 notifications deny once
    18:52:46.672  [PAGE] MarwanBrowser content SHOWN - resume:permission

Store on disk afterwards:

    {"version":1,"items":[{"origin":"http://127.0.0.1:8099","kind":"geolocation","allow":true}]}

and the page itself ended up reading *"starting | geolocation: 3 Timeout expired | notification: denied"* —
allowed, then no location provider on a wired bench, which is the honest answer. No Edge bubble at any
point. Sheet photographed at `shots-v11\small\webperm-09.jpg`, the browser afterwards at `webperm-13.jpg`.

### One-line fix that came out of looking at the sheet

`admin.catalog` now returns `url` and `source` per package, and the sheet prints `source`. It used to
print the `wingetId` — naming a package manager **neither of these machines has** (no Store on this SKU,
B13) on the one screen whose entire job is to say truthfully what is about to happen. `source` is the
host of the manifest's `url` (`cdn.akamai.steamstatic.com`, `aka.ms`), and falls back to
`winget · <id>` only when an entry has no url.

### Observed, not caused, and untriaged

The live shell logged **eight** `[HOST] ShellHostWeb started` lines between 17:32 and 18:31 with no
matching `[EXIT]` line — it is being killed, not exiting. It then stayed up (pid 2888, started 18:31:39)
across all four test runs of this round, so the windowed test instances are not the trigger. Recorded in
STATUS.md's known gaps.

---

## Round 7 — League of Legends through the broker (2026-08-16 ~20:20–)

**Why:** brain reported "when I try to install League of Legends I cannot install it because I do not
have admin permissions." Confirmed on the bench: `arcshell` had downloaded `Install League of Legends
na.exe` (75 831 040 bytes, 23:43 on 08-14) with the shell's browser and launched it from Files — **twice**
(pids 7932 at 19:14 and 7112 at 20:22, both still alive). Each one was sitting on a UAC consent prompt on
the secure desktop that the pad can never reach. Exactly the case the broker exists for; League simply
was not in the manifest.

| # | Change | Command | Undo |
|---|---|---|---|
| B21 | **Killed the two stuck installers** (they could never finish; no install had happened — only `C:\ProgramData\Riot Games\machine.cfg` and an empty per-user folder existed). | `Stop-Process -Id 7112,7932 -Force` | n/a |
| B22 | **`packages.json` gained `league-of-legends` (NA) and `league-of-legends-euw`** — url `https://lol.secure.dyn.riotcdn.net/channels/public/x/installer/current/live.{na,euw}.exe` (HEAD → 200, 75 831 040 bytes = byte-identical size to what brain downloaded), publisher pin `Riot Games` (signer on the downloaded file: `CN="Riot Games, Inc."`, status Valid), args `--skip-to-install --disable-auto-launch`, verifyPath `C:\Riot Games\Riot Client\RiotClientServices.exe`. Deployed by copying the repo file over `C:\ProgramData\ARC\packages.json` from the elevated SSH session; ACLs unchanged (SYSTEM/Administrators F, arcshell RX). | `Copy-Item C:\ArcOS\diag\packages.json C:\ProgramData\ARC\packages.json` | `Copy-Item C:\ProgramData\ARC\packages.v2-before-league.json C:\ProgramData\ARC\packages.json` |
| B23 | **League install requested through the broker AS `arcshell`** (Interactive/Limited task writes `queue\<ticket>.req` = `verb=install` / `package=league-of-legends`, then `schtasks /run \ARC\arc-install-broker`). Ticket `6cf8c042-cb34-4617-90b8-4c090c9cbfc3`. **No UAC prompt.** | see `C:\ArcOS\diag\riot-install.ps1` | `"C:\Riot Games\Riot Client\RiotClientServices.exe" --uninstall-product=league_of_legends --uninstall-patchline=live`, then remove `C:\Riot Games`, `C:\ProgramData\Riot Games`, and Vanguard if it appeared |
| B24 | **v12 host staged** to `C:\ArcOS\web\v12\` (not live; Shell Launcher untouched). Only change vs v11: the `admin.request` wait is activity-aware — keeps waiting while `schtasks /query` reports the broker task Running (a multi-GB game patch writes no progress line for a long time), 15 min idle ceiling only once the task has stopped, 4 h hard cap. | `scp … C:/ArcOS/web/v12/` | `Remove-Item -Recurse C:\ArcOS\web\v12` |

Progress file for the ticket, first minute:

    20:26:48 picked up.
    20:26:48 v2 request, verb='install'.
    20:26:48 installing 'League of Legends (NA)' (league-of-legends).
    20:26:48 downloading https://lol.secure.dyn.riotcdn.net/channels/public/x/installer/current/live.na.exe
    20:27:19 downloaded 72.3 MB -> C:\ProgramData\ARC\cache\league-of-legends_live.na.exe
    20:27:20 provenance OK (publisher pin 'Riot Games')
    20:27:20 C:\ProgramData\ARC\cache\league-of-legends_live.na.exe --skip-to-install --disable-auto-launch

At 20:29 the installer (two `league-of-legends_live.na` processes, as SYSTEM) had laid down
`C:\Riot Games\Riot Client` (21 MB) and by 20:33 `C:\Riot Games` was 692 MB and growing — the game
patch, running with nobody at the machine and no prompt. Outcome recorded below when the result file lands.

### The library could not see what the broker installed

The install worked and the shell still showed nothing. A SYSTEM install in session 0 writes **no Start
Menu shortcut and no uninstall key** — the two things `LibraryApi`'s shortcut source and every registry
source are built on — so the Riot Client was on the disk and invisible to the rail. What it *does* write,
machine-wide, whoever ran it, is `C:\ProgramData\Riot Games\RiotClientInstalls.json` (where
`RiotClientServices.exe` is) and one `Metadata\<product>.<patchline>\` folder per product it knows about.
A product that is actually installed has `product_install_full_path:` in its `product_settings.yaml`; a
product the client merely knows of has only `product_install_root:`. `LibraryApi.cs` gained a **`riot`
source** that reads exactly those two things, and `lib.scan` on the bench now reports the client:

    riot   yes   1   3 ms   C:\ProgramData\Riot Games\RiotClientInstalls.json ; …\Metadata
                           |  Riot Client plus 0 installed of 3 known products.

    launcher  riot  Riot Client   exe  C:\Riot Games\Riot Client\RiotClientServices.exe

Zero installed of three is the truthful answer on this machine right now: `league_of_legends.live`,
`teamfighttactics.live` and `teamfighttactics.pbe` all have a `product_install_root` and none has a
`product_install_full_path`. League is downloaded but not installed until somebody signs into the client
and presses Install; the day that happens the same scan gains a `game` tile with
`--launch-product=league_of_legends --launch-patchline=live` and no code has to change. Verified against
the laptop, which has an ordinary user-run Riot install: League and 2XKO appear **once each**, from the
`riot` source, with Riot's own `.ico`; their three Start Menu shortcuts collapse into them because the
command lines are byte-identical, and Teamfight Tactics — which has a shortcut but no install path in the
metadata — survives as a Start Menu entry rather than being eaten by the de-duplicator.

| # | Change | Command | Undo |
|---|---|---|---|
| B25 | **v13 host staged** to `C:\ArcOS\web\v13\` (29 files: `MarwanShellHostWeb-v13.exe`, the three WebView2 DLLs, `index.html`, `boot.html`, flattened `ui\*.css`, `ui\*.js`, `ui\*.woff2`, `marwanos.png`). **Not live** — Shell Launcher untouched, `C:\ArcOS\web\v10` untouched, the running shell is still pid 2888 out of v10. Only change vs v12: the `riot` library source described above, plus a de-duplication rule that stops one launcher exe with different arguments from being read as one thing. Also copied the rebuilt `libraryapi-cli.exe` to `C:\ArcOS\diag\` for the scan above. | `scp … C:/ArcOS/web/v13/` | `Remove-Item -Recurse C:\ArcOS\web\v13` |

### How the League ticket actually went (20:26–20:49)

* **Ticket `6cf8c042…` (verb=install, silent):** the Riot installer ran as SYSTEM in session 0, installed
  the Riot Client into `C:\Riot Games\Riot Client` (verifyPath satisfied) — and then, ignoring
  `--disable-auto-launch`, started the Riot Client itself, which sat invisibly on its login page in
  session 0. The worker used `Start-Process -Wait`, which waits for the whole descendant tree, so the
  ticket stayed "Running" with the install already done. Killing the session-0 Riot tree by hand
  produced `status=OK exitcode=0`. **Worker fixed:** wait on the installer process only
  (`WaitForExit()`), plus a manifest `postKill` list — process names, SYSTEM-owned only — stopped
  afterwards. Re-ran the ticket (`f81b8688…`): installer exited in 4 s, two `RiotClientServices` killed
  by postKill, `OK`.
* **Vanguard is the real wall.** Riot's own strings in `RiotClientFoundation.dll`: *"Dependecy install
  for %1 requires privilege elevation, skipping to avoid UAC prompt"*, *"No elevated agent exists to
  perform the install"*, *"Vanguard update blocked: no elevated agent for first install"*. The
  installer's `--agent` child is that elevated agent; it only lives while the installer session does.
  A fresh, unelevated Riot Client (what `arcshell` would launch) has no agent and falls back to
  `runas` → UAC credential prompt → unanswerable. There is no standalone Vanguard installer.
* **So the manifest entry became `interactive: true`** (worker: `Start-InConsoleSession` — SYSTEM
  token, `SetTokenInformation(TokenSessionId=<console>)`, `CreateProcessAsUser` on `winsta0\default`,
  i.e. `psexec -s -i`; then wait until every SYSTEM-owned `postKill` process is closed, 6 h ceiling).
  Ticket `69617b39…`: `INTERACTIVE - starting in the console session … console session is 1`, then
  a session-1 probe as `arcshell` saw a visible window `2748 [1524,476 392x488] 'League of Legends
  Installer'` over `2888 'MarwanOS'` and photographed it (`C:\ArcOS\diag\s1\frame.jpg`): the Riot
  install card with its **Install** button, on the console, elevated, no UAC. Left running for the
  player to sign in and finish; the ticket resolves when it is closed, `verifyPath` =
  `C:\Riot Games\League of Legends\LeagueClient.exe`.

| # | Change | Command | Undo |
|---|---|---|---|
| B26 | Worker replaced twice more on the bench: WaitForExit/postKill (`C:\ProgramData\ARC\arc-install-worker.v2-before-waitfix.ps1` is the backup) and then path/interactive (`…v3-before-interactive.ps1`). Manifest re-deployed with the League entries interactive. | `Copy-Item C:\ArcOS\diag\worker.ps1 C:\ProgramData\ARC\arc-install-worker.ps1` | `Copy-Item C:\ProgramData\ARC\arc-install-worker.v2-before-waitfix.ps1 C:\ProgramData\ARC\arc-install-worker.ps1` |
| B27 | v14 host built (`MarwanShellHostWeb-v14.exe`: activity-aware broker wait, `admin.catalog` `path`/`ready`/`interactive`/`source`, LibraryApi Riot source, sheet copy for interactive installs). Not yet deployed to the bench; v12/v13 test dirs exist. | — | — |

**Still open:** the Riot installer / Riot Client is a mouse-and-keyboard UI. The shell has no pad→mouse
mode for foreign windows, so signing in needs a real mouse/keyboard at the bench (a Logitech set was
seen in B11). Vanguard will demand a restart when it goes in. *(Superseded in part by B29: there is a
pad→mouse mode now, and it works on ordinary windows — but Windows itself refuses to let it touch this
particular one, which is SYSTEM-owned. Read B29 before reaching for a mouse.)*

### B28 — `arcshell` made an administrator, UAC set to elevate admins silently (2026-08-16 ~21:05)

Asked for in so many words: *"just make this arcshell account an admin."* Done on the bench only. Note
that membership alone would not have removed a single prompt — an admin behind UAC runs with a
filtered token and the consent dialog still lands on the secure desktop the pad cannot reach — so the
UAC admin policy was set to elevate without prompting as well. UAC itself (`EnableLUA`) is left ON;
standard users (none left signed in) still get their credential prompt.

| # | Change | Command | Undo |
|---|---|---|---|
| B28a | `arcshell` added to `BUILTIN\Administrators` (was: Administrator, Brain) | `Add-LocalGroupMember -Group Administrators -Member arcshell` | `Remove-LocalGroupMember -Group Administrators -Member arcshell` |
| B28b | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System\ConsentPromptBehaviorAdmin` **5 → 0** (elevate without prompting). `EnableLUA=1`, `PromptOnSecureDesktop=1`, `ConsentPromptBehaviorUser=3` unchanged. Affects every admin on the box, incl. `Brain`. | `Set-ItemProperty … -Name ConsentPromptBehaviorAdmin -Value 0` | `Set-ItemProperty … -Name ConsentPromptBehaviorAdmin -Value 5` |

Takes effect for `arcshell` at its **next sign-in** (the token is built at logon; the shell session that
was up at 21:05 still has the old one). The bench was NOT restarted by Claude — the interactive Riot
installer was open on the console. What it changes: an installer run from Files now elevates silently
(no broker needed for that path), and Riot's client can install Vanguard itself. What it does not change:
the broker, the grant sheet and the pointer work stay as they are; and MACHINE-CHANGES.md rule 3 still
holds — the laptop's `brain` and `marwanshell` are untouched.

### B29 — pointer mode verified on the bench (v15), and the wall it hits (2026-08-16 21:20–22:02)

brain: *"can you make a pointer like what the browser has"*, then *"make the touch pad use it"*. The
host now drives the **real Windows cursor** with `SendInput` whenever the front window is not the
shell's and is not a game — see README "Pointer mode" for the bindings and STATUS for the state.

Verified against a **real foreign window**, deliberately not against the shell's own: `ptrtarget.exe`
is a separate process with its own message loop that writes down what actually arrived, because the
host's log only proves what it *sent*. Test builds ran windowed as `arcshell` in session 1 via
`-LogonType Interactive -RunLevel Limited` tasks, `--no-pad` (the live shell owns the DualSense over
raw HID), driven entirely by `--walk` tokens. The live shell was never restarted, replaced or driven.

What the two logs say, together:

* stick: `[PTR] stick move (2300,400) -> (2525,400) d=(225,0)`, then `-210`, then `+135` down
* d-pad: five `[PTR] d-pad step` lines, exactly 8 px each — `(2315,535)→(2315,527)→…→(2299,511)`
* Cross: `[PTR] left down at (2299,511)` / `left up`, and the target's own `mouse LEFT DOWN` / `UP`
* Square: `[PTR] right down` and the target's `WM_CONTEXTMENU`
* right stick: 9 × `WM_MOUSEWHEEL delta=120` then 9 × `delta=-120`
* L1/R1/Options/Circle: the target's `WM_KEYDOWN` PageUp, PageDown, Enter (0x0D), Escape (0x1B)
* Triangle → keyboard: `[PTR] Triangle: keyboard requested for … 'MarwanOS pointer target'`, the page
  opened the OSK, 21 OSK tokens generated from `MarwanOSK._model` spelled `test` and pressed the new
  **Done + ⏎** key, `[PTR] returning to … via path0`, `[PTR] typed 4 chars into 1228 (enter=yes)`, and
  the target logged `WM_CHAR 't','e','s','t'` then Enter. The text itself is never logged.
* touchpad: `touch:700/540>1200/540:400` moved the cursor right, the reverse token moved it back, a
  vertical drag moved it down, and `tap:900/540` produced `[PTR] touch tap -> left click`.

**The wall.** Pointer mode engaged on the real installer *by its own rule* —
`[PTR] on reason=the front window belongs to a process this shell cannot open (elevated or SYSTEM:
'league-of-legends_live.na') fg=2748 'League of Legends Installer'` — and then every move and click
was **silently discarded**: `stick move (1720,718) -> (1720,718) d=(0,0)`, cursor unmoved through the
click. That is UIPI: `SendInput` may only inject into applications at the caller's integrity level or
lower, and the broker runs `interactive` installers as SYSTEM. The host now notices (three refused
moves) and turns the pointer off with the reason instead of pretending. Note for B28: signing
`arcshell` in as an elevated admin raises the shell to **High** integrity, which is still *below* a
SYSTEM-launched installer's **System** integrity — so the fix for that window is the broker launching
interactive installers under the user's (now elevated) token, not the shell's own elevation.

| # | Change | Command | Undo |
|---|---|---|---|
| B29a | `C:\ArcOS\web\v15\` created: `MarwanShellHostWeb-v15.exe`, `ptrtarget.exe`, 3 WebView2 DLLs, `index.html`/`boot.html`, flattened `ui/*` (29 files). Same ACL as v14 (inherited). Live shell untouched. | `scp` into `C:\ArcOS\web\v15\` | `Remove-Item C:\ArcOS\web\v15 -Recurse -Force` |
| B29b | Harness scripts under `C:\ArcOS\diag\` and `C:\ArcOS\diag\s1\` (`ptr-run{A,B,C,D}.ps1`, `ptr-inner*.ps1`, `riot-look*.ps1`, `ptr-cleanup.ps1`, `state.ps1`) plus their logs/frames (`ptr{A,B,C,D}.log`, `target*.log`, `samples*.csv`, `p{A,B,C,D}-*.jpg`, `riot-window.png`). | `scp` + `powershell -File` | `Remove-Item C:\ArcOS\diag\ptr-*.ps1, C:\ArcOS\diag\s1\ptr-*, C:\ArcOS\diag\s1\p?-*.jpg` |
| B29c | Scheduled tasks `arc-diag-ptr1/ptrA/ptrB/ptrC/ptrD/ptrclean/riotlook/steamoff` created as `arcshell` Interactive/Limited and **unregistered again in the same script**. None remain. | `Register-ScheduledTask …` | `Unregister-ScheduledTask -TaskName arc-diag-* -Confirm:$false` (already done) |
| B29d | **Accident, corrected.** The first run's stick tokens were torn apart by `--walk`'s own comma split (`lstick:1,0:400` → two steps), so the pointer never engaged and the tokens drove the *test instance's* home rail instead — it launched Steam (pid 7948, `child process of PID 7368`), and the killed host left the left mouse button down. Both undone: `SendInput` LEFTUP+RIGHTUP from session 1, and `Steam.exe -shutdown` (graceful, verified gone at 21:26). The token separator is now a slash, the host releases its buttons on close, and every harness kills the run if the wrong window is in front. | — | done |

**Not proven, and named as such:** no real finger has been through the touchpad decode (the bench's
live shell owns the pad, so tests run `--no-pad`); the touchpad *button* and the two-finger scroll
gesture were still queued when Steam Big Picture and then `Hollow Knight Silksong` took over the
bench at 21:58–22:01, and injecting synthetic clicks into somebody's game session is not a test worth
running. Steam Big Picture was started after this run's own Steam had been shut down and by something
outside this session — most likely `arcshell`'s new sign-in (B28) plus Steam's own autostart — but the
accident in B29d is the reason Steam had been run on this box at all today, so it is named here rather
than left to be discovered.

### B30 — interactive installers launched under the console user's elevated token (2026-08-16 22:15–22:35)

The B29 wall, fixed at the token: the broker used to start `interactive` installers as **SYSTEM**
(System integrity), which the shell's pad-pointer — Medium, and High even after B28 — can never drive
with `SendInput` (UIPI). The worker now starts them under the **console user's own linked admin
token**, and, where UAC elevates admins silently (`ConsentPromptBehaviorAdmin=0`, true on this bench
since B28), lowers that token's integrity from High to **Medium** so the pointer can reach it. It
falls back to the old SYSTEM path only when the console user has no admin token (a standard user, or
nobody signed in). Code: `provisioning/install-broker/marwan-install-worker.ps1`, rewritten
`Console1`/`Start-InConsoleSession` (`WTSQueryUserToken` → `TokenLinkedToken` → primary dup →
`SetTokenInformation(TokenIntegrityLevel=S-1-16-8192)` → `TokenSessionId` → `CreateProcessAsUser`,
into a job object). Host: `spike/ShellHostWeb/ShellHostWeb.cs` gained pointer rule **8b** — a
foreground window that *opens* but runs on an **elevated token** (the new installer) turns the pointer
on, where the old rule 8 ("cannot open") only caught SYSTEM/other-user windows.

**Pre-probe (SYSTEM, `tokprobe`).** Confirmed the token shapes the design depends on, on this box:
console user token `IL=0x2000 elevated=0 elevType=3`; its **linked** token `IL=0x3000 elevated=1
elevType=2`; after lowering to Medium `IL=0x2000 **elevated=1** elevType=2`. A child started on that
lowered token ran as `arcshell`, `IsInRole(Administrator)=True`, with `SeDebug/SeLoadDriver/SeBackup/…`
present, and a **requireAdministrator** binary (`bcdedit`) launched from it succeeded (`exit=0`) — so
Medium-integrity + elevated is enough to run an elevated installer. (Riot's own installer manifest is
`asInvoker`, so it inherits whatever token we give it.)

**What was verified end to end (ticket `da4273b6`, queued *as arcshell* like the shell would):** the
worker downloaded + publisher-pinned the Riot installer exactly as before, then logged *"started under
the console user's ADMIN token: arcshell [IL=0x2000 elevated=1 elevType=2 session=1] — integrity
lowered to Medium …"*, and a session-0 token read of the running installer (pid 9144) agreed. The
**leftover discrimination** — the change most likely to go wrong now that the installer and the
player's client share one owner — was proven: stopping only the two broker-launched cache pids
(9144 + its `--agent` child 2416) made the wait loop report *"interactive session finished (nothing
left running)"* while **10 of brain's own `Riot Client` processes kept running untouched**; the ticket
still resolved `OK` on `verifyPath` (`C:\Riot Games\League of Legends\LeagueClient.exe`).

**Rule 8b's predicate** was confirmed from session 1: a Medium `arcshell` process `OpenProcess`+reads
the installer's token and sees `elevated=1` (`"opens; IL=0x2000 elevated=1 elevType=2"`) — exactly the
two calls `PointerMode.TokenElevated` makes, so the host will engage the pointer on this window.

**Not proven, and named as such (same rule as B29):** the `[PTR] on reason=…elevated token…` line and
a non-zero cursor move *over the installer* were **not** captured. The session-1 pointer run
(`elev-inner.ps1`, v16 host, `--no-pad`, engage-by-rule) **aborted itself at the handoff** — the
correct B29d safety — because the bench was occupied: `arcshell`'s **live Riot Client sign-in with
Vanguard installing** was up and took the foreground, and console idle time read **0 ms** across every
sample. Driving a synthetic cursor into a live sign-in / anti-cheat install is not a test worth
running. The move-proof is deferred to an unattended bench; nothing about it is blocked in the code
(the engage predicate above is satisfied), only unsafe to demonstrate tonight.

The **live shell was never restarted, replaced or driven** (still `ArcShellHostWeb-v11.exe` pid 4812
out of `C:\ArcOS\web\v10`). All scheduled tasks below were registered and unregistered in the same
scripts; none remain.

| # | Change | Command | Undo |
|---|---|---|---|
| B30a | Worker replaced on the bench with the user-token build. Backup kept. | `Copy-Item C:\ProgramData\ARC\arc-install-worker.ps1 C:\ProgramData\ARC\arc-install-worker.v4-before-usertoken.ps1`<br>`Copy-Item C:\ArcOS\diag\worker-usertoken.ps1 C:\ProgramData\ARC\arc-install-worker.ps1 -Force` | `Copy-Item C:\ProgramData\ARC\arc-install-worker.v4-before-usertoken.ps1 C:\ProgramData\ARC\arc-install-worker.ps1 -Force`<br>(v4 backup sha256 `3B8587D3A0C3820BDBFD1A3E4B25663E985E1C7CAF343997FDBB0CFA508F91DE`; deployed sha256 `B1B3F39835D12D8950B36F2764EAB0205A13B859C66278633D70EAAB092E47D2`, 58488 bytes) |
| B30b | `C:\ArcOS\web\v16\` created: `MarwanShellHostWeb-v16.exe` (pointer rule 8b) + WebView2 DLLs + flattened UI (30 files). Same inherited ACL as v15. Live shell untouched. | `scp` into `C:\ArcOS\web\v16\` | `Remove-Item C:\ArcOS\web\v16 -Recurse -Force` |
| B30c | Harness/probe scripts under `C:\ArcOS\diag\` (`tokprobe-*.ps1`, `elev-*.ps1`, `idle*.ps1`, `state2.ps1`, `who.ps1`, `probe-elev*.ps1`) and their outputs under `C:\ArcOS\diag\s1\` (`tokprobe.txt`, `bcd.txt`, `runE.txt`, `ptrE.log`, `samplesE.csv`, `pE-*.jpg/png`, `look.txt`, `idle*.txt`). | `scp` + `powershell -File` | `Remove-Item C:\ArcOS\diag\tokprobe-*,C:\ArcOS\diag\elev-*,C:\ArcOS\diag\idle*,C:\ArcOS\diag\state2.ps1,C:\ArcOS\diag\who.ps1,C:\ArcOS\diag\probe-elev*,C:\ArcOS\diag\worker-usertoken.ps1,C:\ArcOS\diag\s1\tokprobe.txt,C:\ArcOS\diag\s1\bcd.txt,C:\ArcOS\diag\s1\runE.txt,C:\ArcOS\diag\s1\ptrE.log,C:\ArcOS\diag\s1\samplesE.csv,C:\ArcOS\diag\s1\pE-*,C:\ArcOS\diag\s1\look.txt,C:\ArcOS\diag\s1\idle*.txt` |
| B30d | Test ticket `da4273b6` queued as `arcshell`; its broker-launched installer (cache pids 9144, 2416) stopped afterward. brain's own Riot Client (from `C:\Riot Games`) never touched. Ticket resolved `OK`. | queue file + `schtasks /run \ARC\arc-install-broker`; `Stop-Process` on the two cache pids | none needed (installer only; League was already present per `verifyPath`) |
