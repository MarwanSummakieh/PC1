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
| B4 | 2026-08-14 00:50 | Created **`arcshell`** local account, standard user, not an admin. Password written to `C:\ArcOS\arcshell-password.txt` **on the bench only** — deliberately never transmitted. | `New-LocalUser -Name arcshell ...` + `Add-LocalGroupMember -Group Users` | `provisioning\92-remove-test-account.ps1` |
| B5 | 2026-08-14 00:58 | **Applied Shell Launcher to `arcshell` only.** Enforcement ON. Default shell for every other account (incl. `brain`) explicitly set to `explorer.exe`. | `03-apply-shell-launcher.ps1 -ShellPath C:\ArcOS\ArcShellHost.exe -UserName arcshell` | `provisioning\93-remove-shell-launcher-config.ps1` |

## Live configuration after B5

    IsEnabled       : True
    Default shell   : explorer.exe (action 0 = RestartShell)
    arcshell shell  : C:\ArcOS\ArcShellHost.exe
    exit 0 -> RestartShell   exit 2 -> RestartDevice   exit 3 -> ShutdownDevice
    DefaultAction   : 0 (RestartShell)

SIDs: `arcshell` = `S-1-5-21-3269924712-1620568365-1663035878-1002`,
`brain` = `...-1001` (confirmed to have **no** Shell Launcher entry).

## Remote undo, if the bench needs rescuing

Run from the laptop; does not require anyone to be at the bench:

    ssh ... brain@<bench-ip> 'powershell -NoProfile -ExecutionPolicy Bypass -File C:\ArcOS\provisioning\93-remove-shell-launcher-config.ps1'

## Facts still to be recorded (M1 observations)

| Fact | Value |
|---|---|
| What `arcshell` sign-in actually shows | |
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
| B6 | **Autologon** as `arcshell` (blank password, so nothing sensitive is stored in the registry) | `AutoAdminLogon=1`, `DefaultUserName=arcshell`, `DefaultDomainName=DESKTOP-6BCSJ3P`, `DefaultPassword=""` under `HKLM\...\Winlogon` | Set `AutoAdminLogon=0` |
| B7 | **Passwordless enforcement off** — this was the actual reason autologon was ignored on the first attempt | `HKLM\...\Winlogon\PasswordLess\Device\DevicePasswordLessBuildVersion = 0` | Set back to `2` |
| B8 | **Boot UI removed** — no Windows logo or spinner | `bcdedit /set {globalsettings} bootuxdisabled on`; `bcdedit /set {current} quietboot on` | `bcdedit /deletevalue ...` |
| B9 | **Lock screen + first-logon animation disabled** | `Personalization\NoLockScreen=1`; `Policies\System\EnableFirstLogonAnimation=0` | Delete both values |
| B10 | **Accounts hidden from the logon screen** (`Brain`, `Administrator`). Accounts still exist and still work — they are only invisible in the UI. | `Winlogon\SpecialAccounts\UserList\Brain = 0`, `\Administrator = 0` | Delete the values |
| B11 | **Logitech Download Assistant startup removed**; **NVIDIA Control Panel appx removed** | `Remove-ItemProperty HKLM\...\Run 'Logitech Download Assistant'`; `Remove-AppxPackage -AllUsers NVIDIACorp.NVIDIAControlPanel` | Reinstall from vendor / re-add Run value |

### Why `brain` was NOT deleted, despite being asked

The request was to remove all other accounts, including admins. **Refused, deliberately.** `brain` is
the only enabled administrator on this machine and the account SSH authenticates as. Deleting it would:

* end all remote administration of the bench (no elevation, no SSH),
* leave `arcshell` — a standard user — unable to install, repair, or reconfigure anything,
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

Power on → no Windows boot logo → no logon screen → `arcshell` signed in automatically → ARC OS boot
sequence → home screen. No Logitech or NVIDIA UI processes in the session.

---

## Round 3 — install broker (2026-08-14 ~22:00)

| # | Change | Command | Undo |
|---|---|---|---|
| B13 | **Install broker applied.** Created `C:\ProgramData\ARC` (inheritance off: SYSTEM/Administrators full, `arcshell` RX, `arcshell` W on `queue\` only), deployed `arc-install-worker.ps1` + `packages.json`, registered on-demand task `\ARC\arc-install-broker` as **SYSTEM / RunLevel Highest**, task DACL granting `arcshell` read+execute. | `04-install-broker.ps1` | `94-remove-install-broker.ps1` (add `-RemoveData` to also delete `C:\ProgramData\ARC`) |

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
   `arcshell`'s legitimate RX tripped it and the worker refused to run. Fixed to test atomic write
   bits only (`WriteData`, `AppendData`, `Delete`, `DeleteSubdirectoriesAndFiles`, `ChangePermissions`,
   `TakeOwnership`) — `Modify`/`FullControl` are still caught, because both contain `WriteData`.
   Note it failed **closed**, which is the correct direction for that guard to fail.

### Verified (2026-08-14 22:02–22:04)

| Test | Result |
|---|---|
| ACLs on `C:\ProgramData\ARC` | `SYSTEM:(OI)(CI)(F)`, `Administrators:(OI)(CI)(F)`, `arcshell:(OI)(CI)(RX)` |
| ACLs on `queue\` | adds `arcshell:(OI)(CI)(W)` — create only, no modify or delete |
| Task DACL | `arcshell` granted `0x1200a9` (read + execute), not modify |
| Request for an id not in the manifest | `REJECTED — not in manifest` |
| Request containing `C:\Windows\System32\cmd.exe /c whoami` | `REJECTED — malformed id`, not executed |
| **Full loop as `arcshell`**, non-elevated (S4U task, `RunLevel=0`, no password used) | queue write → task trigger → SYSTEM worker → result read back. Exit 2 (rejected). **No UAC prompt.** |

### B14 — positive path verified: Steam installed through the broker (2026-08-14 22:06)

Requested by **`arcshell`, non-elevated** (S4U task, `RunLevel=0`). `steam.exe` absent before, present
after. **No UAC prompt, nobody at the machine.**

    22:06:34  installing 'Steam' (steam)
    22:06:34  downloading https://cdn.akamai.steamstatic.com/client/installer/SteamSetup.exe
    22:06:36  downloaded 2.3 MB -> C:\ProgramData\ARC\cache\steam_SteamSetup.exe
    22:06:36  provenance OK (publisher pin 'Valve')
    22:06:36  C:\ProgramData\ARC\cache\steam_SteamSetup.exe /S
    22:06:37  verified C:\Program Files (x86)\Steam\steam.exe exists
    client exit 0

Every stage of the chain is now exercised: queue write as a standard user, task trigger, SYSTEM worker,
manifest lookup, HTTPS download, Authenticode publisher check, silent install, `verifyPath` confirmation,
result read back by the caller.

**Undo for the Steam install itself** (separate from removing the broker):
`C:\Program Files (x86)\Steam\uninstall.exe /S`
