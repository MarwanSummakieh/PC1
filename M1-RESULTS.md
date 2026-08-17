# M1 results — the spike, run for real under Shell Launcher

**Machine:** DESKTOP-6BCSJ3P (test bench), Windows 11 IoT Enterprise LTSC Evaluation, build **26100.9168**
**Date:** 2026-08-14, ~01:00–01:10
**Shell:** `C:\MarwanOS\MarwanShellHost.exe` bound to the `marwanshell` SID via `WESL_UserSetting`, enforcement ON
**Display:** 3440×1440
**Controller:** none attached — all input was keyboard

Every result below was observed on the machine. Timings come from `C:\MarwanOS\handoff-log.txt`,
written by the host itself; session/process facts come from live queries over SSH from the laptop.

---

## The four kickoff-brief steps

| # | Step | Result |
|---|---|---|
| 1 | Window running as the Shell Launcher target, solid colour, no explorer | **PASS** |
| 2 | Launch a real installed program by path, from a button press | **PARTIAL** — launched by keyboard, not gamepad; child was `notepad.exe`, not a game |
| 3 | Return from it, with focus, no mouse | **PASS**, verified programmatically |
| 4 | Exit cleanly; Shell Launcher return-code policy behaves as documented | **2 of 3 verified** (see below) |

## 1. Shell replacement — PASS

Signing into `marwanshell` lands directly in the host window. No taskbar, no Start, no desktop icons.

Process evidence while the session was live (`marwanshell` = session 3, `brain` = session 1, disconnected):

    MarwanShellHost   pid 7656   session 3      <- our shell
    explorer       pid 7208   session 1      <- brain's session only
    SearchHost     pid 7400   session 1
    StartMenuExperienceHost  pid 2268  session 1
    sihost         pid 2692   session 1
    sihost         pid 10984  session 3      <- NOTE: present in the shell-launcher session

**Finding worth carrying forward:** `sihost` (Shell Infrastructure Host) **does** run in a Shell
Launcher session, while explorer, Start and Search do not. The session is therefore not as barren as
"no explorer" suggests. Relevant when we get to notifications, volume UI and anything that expects
shell infrastructure — some of it is still there.

## 2. Task Manager is reachable — PASS

`Ctrl`+`Shift`+`Esc` opened Task Manager under the replaced shell.

This was previously an **expectation, not a fact** — the provisioning notes flagged it as unverified,
reasoning that Shell Launcher has no `AllowTaskManager` setting (that belongs to Assigned Access
multi-app kiosk XML) and does not set `DisableTaskMgr`. That reasoning is now confirmed on 26100.9168.
The documented recovery path is real.

## 3. Launch and return with no explorer — PASS

    01:04:53.036  [LAUNCH] CreateProcess(suspended) ok: 'notepad.exe' pid=10280
    01:04:53.036  [TRACK]  AssignProcessToJobObject ok - tracking mode = JOB OBJECT
    01:04:53.036  [HANDOFF] host minimized (SW_MINIMIZE)
    01:05:10.637  [TRACK]  job: ACTIVE_PROCESS_ZERO - child tree is empty
    01:05:10.641  [RETURN] foreground before restore: hwnd=0
    01:05:10.641  [RETURN] forced-foreground path0  (0 ms)
    01:05:10.641  [VERIFY] PASS GetForegroundWindow()==host hwnd (0x50078)

**The most interesting line is `foreground before restore: hwnd=0`.** With no explorer running, when
the child process closes there is **no foreground window at all** — there is no desktop to fall back
to. Returning to the foreground is therefore *easier* without explorer than with it: nothing competes.
This is evidence **in favour of** the no-explorer architecture, and it is the opposite of the
pre-spike assumption that removing the shell would make focus harder to reclaim.

Caveat: `notepad.exe` is not a game. Nothing here yet exercises fullscreen-exclusive D3D, which is
where focus return is genuinely hard. That is M2.

## 4. Return-code policy — 2 of 3 verified

| Shell exit code | Configured action | Observed |
|---|---|---|
| 0 (`Esc`) | RestartShell | **PASS**, twice. Restart took **94 ms** and **79 ms** — the shell is back before a person perceives it as gone. |
| 2 (`2` key) | RestartDevice | **PASS**. Exit at 01:09:13 → OS boot 01:09:30 → shell on screen 01:09:39. **~26 s from keypress to usable shell.** |
| 3 (`3` key) | ShutdownDevice | **NOT VERIFIED** — see below. |

### Honest note on exit code 3

The operator reported all three worked. The host's own log does **not** support that for exit 3: it
records `ESC`/`ESC`/`X` and no `Y` action, no `exiting with code 3`, and the machine's boot time
(01:09:30) matches the *restart* from exit code 2, not a shutdown followed by a power-on. The bench
was still reachable over SSH afterwards.

Most likely the shutdown was simply not triggered. Recording it as unverified rather than assuming.
**Still to test:** press `3` and confirm the machine powers off.

## Unplanned finding: reboot returns straight to the shell, unattended

After the exit-code-2 device restart, the machine came back up **into the ARC shell with no logon
interaction at all** — `marwanshell` was signed in again at 01:09 with no password typed. This is
Windows' automatic sign-in of the last interactive user after a restart, working because the account
has a blank password.

Cold restart to a usable shell in **~26 seconds, hands-off**. That is effectively the M8 living-room
experience appearing early, by accident. It should be made deliberate (explicit autologon) rather than
relied on as a side effect, since ARSO behaviour is not guaranteed across updates.

## Corrections to earlier assumptions

* **Keyboard bindings are `Enter` / `Esc` / `2` / `3`.** `B`/`X`/`Y` are *gamepad* buttons and do
  nothing on a keyboard. Reported as "nothing happens" during testing; the shell was behaving
  correctly. Any future test script should state the keyboard keys, not the pad buttons.
* **Enabling the optional feature needs `/all` in one DISM call.** Enabling `Client-DeviceLockdown`
  and `Client-EmbeddedShellLauncher` in separate calls fails with "parent features are disabled",
  even after the parent reports `Enabled`. The laptop's `01-enable-shell-launcher.ps1` should be
  changed to match.
* **The payload needs two reboots.** `Eshell.exe` and the `WESL_UserSetting` WMI class were still
  absent after the first restart and only appeared after servicing completed on the second.

## What M1 does not answer

* Real-game handoff, especially fullscreen exclusive (M2).
* Anything gamepad-driven — no controller was attached to the bench.
* Whether a background explorer can be started under Shell Launcher (research bench test #1) — not
  attempted yet; the session had no explorer at all, which is the clean baseline for trying it.
* Store clients under a replaced shell — none are installed on the bench.
