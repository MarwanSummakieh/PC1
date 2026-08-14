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

### Verified behaviour after round 2

Power on → no Windows boot logo → no logon screen → `arcshell` signed in automatically → ARC OS boot
sequence → home screen. No Logitech or NVIDIA UI processes in the session.
