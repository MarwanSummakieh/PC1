# RECOVERY.md — getting back to a working desktop

**Read this before you sign into `arcshell` for the first time.**

This document lists every way back, in order of escalation. Start at (a) and go down.
Nothing here should ever be needed for the `brain` account: `brain` is never given a
Shell Launcher configuration, and the default shell for unconfigured users is set to
`explorer.exe` by `03-apply-shell-launcher.ps1` before anything else is written.

---

## Before you start: the two things that make this safe

1. **Use `Switch user`, not `Sign out`.**
   From the `brain` desktop press `Ctrl` + `Alt` + `Del` → **Switch user** → sign into
   `arcshell`. Your elevated `brain` session with the provisioning scripts stays alive
   in the background. Getting back is then just `Ctrl` + `Alt` + `Del` → **Switch user**
   → `brain`. This is recovery path (c) and it costs nothing to set up.

2. **Have a second admin path.** `brain` must be in the local Administrators group.
   Verify from an elevated prompt before you begin:

   ```powershell
   Get-LocalGroupMember -Group Administrators
   ```

3. **Know your BitLocker recovery key.** Paths (d) and (e) boot outside Windows. If the
   system drive is BitLocker-protected you will be asked for the 48-digit recovery key
   and you cannot proceed without it. Check now:

   ```powershell
   Get-BitLockerVolume -MountPoint C:
   # If ProtectionStatus is On, retrieve and write down the key:
   (Get-BitLockerVolume -MountPoint C:).KeyProtector |
       Where-Object KeyProtectorType -eq 'RecoveryPassword' |
       Select-Object KeyProtectorId, RecoveryPassword
   ```

---

## (a) Task Manager → run `explorer.exe`

**Keystrokes:** `Ctrl` + `Shift` + `Esc`

If Task Manager opens:

1. Click **Run new task** (top-right of the Windows 11 Task Manager window).
   Keyboard route: `Alt` to focus the command bar, then arrow to **Run new task**, `Enter`.
   Older-style route: **File** → **Run new task**.
2. Type `explorer.exe`
3. Tick **Create this task with administrative privileges** if you intend to run the
   undo scripts from this session.
4. **OK**

You now have a normal desktop *for this session only*. This does not change the Shell
Launcher configuration — the custom shell comes back at the next sign-in. Use this
session to open PowerShell and run the undo scripts:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Users\brain\Documents\repos\PC1\provisioning\93-remove-shell-launcher-config.ps1"
```

### The AllowTaskManager question — answered

**Shell Launcher has no `AllowTaskManager` setting.** `AllowTaskManager` is an attribute
of the *Assigned Access* multi-app kiosk XML schema
(`AssignedAccess` CSP → `KioskModeApp` / multi-app configuration), a different feature.
The Shell Launcher XSD and the `WESL_UserSetting` WMI class have no equivalent, and
nothing in `03-apply-shell-launcher.ps1` sets the `DisableTaskMgr` policy.

So Task Manager **is expected to be reachable** under Shell Launcher.

> **UNVERIFIED on Windows 11 IoT Enterprise LTSC 2024.** This is a reasoned conclusion
> from the documentation, not something confirmed on this machine. Treat path (a) as
> *probably* available and never as your only plan. Paths (b) and (c) do not depend on it.

If `Ctrl` + `Shift` + `Esc` does nothing, check for a leftover policy from a *previous*
lockdown attempt (run as `brain`, elevated):

```powershell
# Machine-wide policy
Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name DisableTaskMgr -ErrorAction SilentlyContinue
# Per-user policy for arcshell (needs the account's SID; hive must be loaded, i.e. signed in)
$sid = (Get-LocalUser arcshell).SID.Value
Get-ItemProperty "Registry::HKEY_USERS\$sid\Software\Microsoft\Windows\CurrentVersion\Policies\System" -Name DisableTaskMgr -ErrorAction SilentlyContinue

# To clear the machine-wide one:
Remove-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name DisableTaskMgr -ErrorAction SilentlyContinue
```

---

## (b) `Ctrl` + `Alt` + `Del` — the secure attention sequence

**Keystrokes:** `Ctrl` + `Alt` + `Del`

This is handled by **winlogon**, not by the shell. It works no matter what the shell is,
even if the shell has crashed and the screen is black. This is the most reliable
in-session escape hatch there is.

The screen offers:

| Option | What it gets you |
| --- | --- |
| **Lock** | Not useful here. |
| **Switch user** | **Use this.** Takes you to the logon screen with the `arcshell` session left running; sign into `brain`, which always gets Explorer. |
| **Sign out** | Ends the `arcshell` session cleanly. Returns to the logon screen. |
| **Change a password** | Not useful here. |
| **Task Manager** | Second route into path (a), independent of `Ctrl` + `Shift` + `Esc`. |

There is also a **power button** in the bottom-right of that screen: `Restart`,
`Shut down`. Holding `Shift` while clicking **Restart** there is exactly how you get to
path (d).

> A shell configured with `DefaultAction = 2` (ShutdownDevice) or `1` (RestartDevice)
> that exits immediately will power the machine off/reboot it before you can react.
> `03-apply-shell-launcher.ps1` defaults to `DefaultAction = 0` (RestartShell) for this
> reason. If you hit a crash loop, re-run it with `-DefaultAction 3` (DoNothing): you get
> a blank desktop, but `Ctrl` + `Alt` + `Del` still works, and that is enough.

---

## (c) Sign into `brain` and run the undo scripts

`brain` is never configured by anything in `provisioning\`, and step 03 explicitly sets
the default shell for all unconfigured accounts to `explorer.exe`. `brain` gets a normal
desktop.

1. `Ctrl` + `Alt` + `Del` → **Switch user** (or **Sign out**).
2. Sign into `brain`.
3. Open **Windows PowerShell (Admin)**: press `Win` + `X`, then `A`. Approve the UAC prompt.
   (`Win` + `X` needs Explorer running — it will be, on `brain`.)
4. Run the undo, most-targeted first:

```powershell
cd C:\Users\brain\Documents\repos\PC1\provisioning

# Undo step 03 — removes arcshell's custom shell, turns enforcement off.
# THIS IS THE ONE THAT FIXES A BROKEN SHELL.
.\93-remove-shell-launcher-config.ps1

# If the config is in an unknown state, nuke every per-SID entry:
.\93-remove-shell-launcher-config.ps1 -All

# Undo step 02 — delete the test account and its profile (optional).
.\92-remove-test-account.ps1

# Undo step 01 — remove the optional feature (optional; run 93 first).
.\91-disable-shell-launcher.ps1
```

5. Sign the `arcshell` session out so it re-reads the configuration:

```powershell
# Find the session id, then log it off.
query user
logoff <ID>
```

### Manual one-liners, if the scripts themselves are the problem

```powershell
# Everything below must run in Windows PowerShell 5.1 (powershell.exe), elevated.
$SL = [wmiclass]"\\localhost\root\standardcimv2\embedded:WESL_UserSetting"

# See what is configured.
$SL.IsEnabled()
$SL.GetDefaultShell()
Get-WmiObject -Namespace root\standardcimv2\embedded -Class WESL_UserSetting | Select Sid, Shell, DefaultAction

# The two-command emergency fix:
$SL.SetEnabled($false)
$SL.SetDefaultShell("explorer.exe", 0)

# Remove a specific SID's entry:
$SL.RemoveCustomShell((Get-LocalUser arcshell).SID.Value)
```

### Just disable the account and stop the bleeding

```powershell
Disable-LocalUser -Name arcshell
```

Nobody can sign into a broken shell if the account cannot sign in.

---

## (d) Safe Mode / WinRE — disable the feature from outside a normal boot

Use this if you cannot sign into `brain` at all.

### Getting to WinRE

Any one of:

* **From the logon screen:** click the **power** icon (bottom-right), hold `Shift`, and
  click **Restart**. Keep `Shift` held until the blue *Choose an option* screen appears.
* **From a working session:** `shutdown /r /o /t 0`
* **Three failed boots:** force power-off with the physical power button (hold ~5 s)
  during the Windows logo, three times in a row. The fourth boot enters
  *Automatic Repair* → **Advanced options**.

You may be prompted for the BitLocker recovery key here. See the top of this document.

### Safe Mode

*Choose an option* → **Troubleshoot** → **Advanced options** → **Startup Settings** →
**Restart** → on the numbered menu press **`4`** (or `F4`) for *Safe Mode*, or **`5`**
(`F5`) for *Safe Mode with Networking*.

Sign in, open an elevated PowerShell, and run path (c)'s commands.

> **UNVERIFIED on Windows 11 IoT Enterprise LTSC 2024:** whether Shell Launcher is
> bypassed in Safe Mode. Safe Mode normally loads `explorer.exe` as the shell and skips
> optional shell components, so this is expected to work — but do not rely on it as the
> only plan.

### WinRE command prompt — disable the feature offline

*Choose an option* → **Troubleshoot** → **Advanced options** → **Command Prompt**.

The Windows volume is usually **not** `C:` inside WinRE. Find it first:

```
diskpart
list volume
exit
```

Look for the volume containing `\Windows` — assume it is `D:` in the commands below, and
substitute what you actually find.

```
REM Confirm you have the right volume
dir D:\Windows\System32\config

REM Check the feature state offline
dism /image:D:\ /get-featureinfo /featurename:Client-EmbeddedShellLauncher

REM Disable Shell Launcher offline. THIS IS THE OFFLINE FIX.
dism /image:D:\ /disable-feature /featurename:Client-EmbeddedShellLauncher

REM Reboot
wpeutil reboot
```

This is the same lever as `91-disable-shell-launcher.ps1`, applied offline.

---

## (e) Offline registry edit from a Windows install USB

Last resort. Use when WinRE on the machine itself is unavailable or damaged.

1. Boot the machine from a Windows 11 installation USB (mash the vendor boot-menu key at
   power-on — commonly `F12`, `F11`, `Esc`, or `Del`; check your hardware).
2. At the *Windows Setup* language screen press `Shift` + `F10` to open a command prompt.
   (Alternative: **Repair your computer** → **Troubleshoot** → **Command Prompt**.)
3. Identify the Windows volume with `diskpart` → `list volume` → `exit`, as above.
4. **Preferred: use DISM**, exactly as in (d):

   ```
   dism /image:D:\ /disable-feature /featurename:Client-EmbeddedShellLauncher
   ```

5. **If DISM will not run**, load the offline `SOFTWARE` hive and edit it directly:

   ```
   REM Back the hive up first. Do not skip this.
   copy D:\Windows\System32\config\SOFTWARE D:\Windows\System32\config\SOFTWARE.bak

   REM Mount the offline SOFTWARE hive under a temporary key name
   reg load HKLM\OFFLINE_SW D:\Windows\System32\config\SOFTWARE

   REM Inspect the Shell Launcher configuration
   reg query "HKLM\OFFLINE_SW\Microsoft\Windows Embedded\Shell Launcher" /s

   REM Turn enforcement off (value name and type per the key you find above)
   reg add "HKLM\OFFLINE_SW\Microsoft\Windows Embedded\Shell Launcher" /v Enabled /t REG_DWORD /d 0 /f

   REM ALWAYS unload the hive, or the change is not committed
   reg unload HKLM\OFFLINE_SW
   ```

   Then also sanity-check that the normal Winlogon shell is intact — Shell Launcher does
   not normally touch this, but a stray edit here is the classic cause of a black desktop:

   ```
   reg load HKLM\OFFLINE_SW D:\Windows\System32\config\SOFTWARE
   reg query "HKLM\OFFLINE_SW\Microsoft\Windows NT\CurrentVersion\Winlogon" /v Shell
   REM Expected value: explorer.exe
   reg add "HKLM\OFFLINE_SW\Microsoft\Windows NT\CurrentVersion\Winlogon" /v Shell /t REG_SZ /d explorer.exe /f
   reg unload HKLM\OFFLINE_SW
   ```

6. Remove the USB and reboot.

> **UNVERIFIED — READ THIS BEFORE TRUSTING STEP 5.**
> The registry path `HKLM\SOFTWARE\Microsoft\Windows Embedded\Shell Launcher` and the
> `Enabled` value name are **not documented on Microsoft Learn**. Microsoft documents
> only the WMI provider (`WESL_UserSetting`) and the Assigned Access CSP as the supported
> configuration surfaces; the on-disk storage layout is an implementation detail and may
> differ on Windows 11 IoT Enterprise LTSC 2024.
>
> **Before you rely on this path, verify the layout while the machine is healthy.** Right
> after running `03-apply-shell-launcher.ps1`, from an elevated `brain` session, run:
>
> ```powershell
> reg query "HKLM\SOFTWARE\Microsoft\Windows Embedded\Shell Launcher" /s
> # If that returns nothing, find where the SID actually landed:
> $sid = (Get-LocalUser arcshell).SID.Value
> reg query HKLM\SOFTWARE /f "$sid" /s /k /e
> reg query HKLM\SOFTWARE /f "ShellHost.exe" /s /d
> ```
>
> Write the real key path into this document at that point. The DISM route in step 4 is
> documented and does not depend on any of this — prefer it.

---

## Quick reference card

Print this, or keep it on a phone.

| Situation | Do this |
| --- | --- |
| Custom shell running, want a desktop now | `Ctrl`+`Shift`+`Esc` → Run new task → `explorer.exe` |
| Black screen, nothing responds | `Ctrl`+`Alt`+`Del` → Switch user → sign in as `brain` |
| Want the config gone for good | As `brain`, elevated: `.\93-remove-shell-launcher-config.ps1` |
| Config in unknown state | `.\93-remove-shell-launcher-config.ps1 -All` |
| Stop anyone signing into the broken account | `Disable-LocalUser -Name arcshell` |
| Cannot sign into `brain` | Shift+Restart → Troubleshoot → Startup Settings → `4` (Safe Mode) |
| Cannot reach a desktop at all | WinRE Command Prompt → `dism /image:D:\ /disable-feature /featurename:Client-EmbeddedShellLauncher` |
| WinRE broken | Install USB → `Shift`+`F10` → same DISM command |

## Reference

* Shell Launcher overview — <https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/>
* Configure Shell Launcher (feature enable, exit-code actions) — <https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/configure>
* Configure with the WMI provider — <https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/configure-wmi>
* `WESL_UserSetting` class reference — <https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/wesl-usersetting>
* Shell Launcher configuration file / XSD (V2 schema) — <https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/configuration-file>
