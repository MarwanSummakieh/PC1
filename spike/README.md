# MarwanOS — ShellHost spike

Minimal native host that proves the two highest-risk mechanics for a Windows console-shell
project:

1. **Window handoff** — launch a child process, get out of the way, and reliably come back
   to the foreground when the child's whole process tree exits.
2. **Gamepad input** — XInput polling via P/Invoke, including the undocumented
   `XInputGetStateEx` (ordinal #100) needed for the Guide button.

The window is currently just a solid `#04060B` rectangle with a status readout. It is the
future native host for the MarwanOS HTML shell (`index.html` / `boot.html`); no HTML is
loaded yet.

## Build

No SDK required. The project builds with the **inbox .NET Framework compiler** that ships
with Windows, so nothing has to be installed.

```
spike\ShellHost\build.cmd
```

Produces `spike\ShellHost\bin\MarwanShellHost.exe` (x64, WinForms, .NET Framework 4.x).

Compiler: `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` (version 4.8, **C# 5
language level** — no string interpolation, `nameof`, expression-bodied members, etc.).

> The binary is deliberately **not** named `ShellHost.exe`: Windows 11 ships its own
> `C:\Windows\System32\ShellHost.exe`, and the name collision makes process checks
> ambiguous.

## Run

```
MarwanShellHost.exe                                   # default child: notepad.exe
MarwanShellHost.exe --child="cmd.exe /c start notepad.exe"
MarwanShellHost.exe notepad.exe                       # positional form also works
```

The window is borderless, covers the full primary screen, and is **never topmost** (it must
not fight fullscreen games).

### Controls

| Input | Gamepad bit | Action |
|---|---|---|
| **A** | `0x1000` | Launch the configured child process, then yield the screen |
| **B** | `0x2000` | Exit with code **0** |
| **X** | `0x4000` | Exit with code **2** |
| **Y** | `0x8000` | Exit with code **3** |
| Guide | `0x0400` | Logged only (requires `XInputGetStateEx`) |
| `Enter` | — | Same as A (launch) |
| `Esc` | — | Same as B (exit 0) |
| `2` | — | Same as X (exit 2) |
| `3` | — | Same as Y (exit 3) |

All four XInput slots (0–3) are polled at ~60 Hz; buttons are edge-triggered.

### Exit-code table

Distinct codes so a future Shell Launcher return-code policy can be tested.

| Code | Meaning | Trigger |
|---|---|---|
| `0` | Normal exit | B button, `Esc` |
| `2` | Alternate exit A | X button, `2` |
| `3` | Alternate exit B | Y button, `3` |
| `99` | Watchdog tripped — automated test did not complete | `--watchdog=<ms>` elapsed |
| `100` | Unhandled exception in the message loop | fatal |

### Command-line flags

| Flag | Meaning |
|---|---|
| `--child=<cmdline>` | Child command line (default `notepad.exe`). Quote it if it contains spaces. |
| `--no-job` | Force the toolhelp descendant-polling fallback instead of the job object |
| `--windowed` | 1000×620 window instead of fullscreen (development aid) |
| `--log=<path>` | Log file path (default: `handoff-log.txt` in the `spike` folder) |
| `--auto-launch=<ms>` | Unattended: auto-trigger launch this long after start / previous return |
| `--auto-kill=<ms>` | Unattended: `taskkill /T /F` the tracked tree this long after launch |
| `--auto-exit=<ms>` | Unattended: exit this long after the last return (or after start if no launch) |
| `--cycles=<n>` | Repeat the launch/return cycle n times (return-reliability stress) |
| `--exit-as=<ESC\|B\|X\|Y>` | Which exit path `--auto-exit` should take |
| `--launch-as=<ENTER\|A>` | Which launch path `--auto-launch` should take |
| `--watchdog=<ms>` | Hard timeout; exits with code 99 so a hung test can never wedge the machine |

## How the handoff works

**Launch.** The child is created with `CreateProcess(CREATE_SUSPENDED)`, assigned to a
Win32 **job object**, and only then resumed. Creating it suspended is what makes the job
airtight: no grandchild can be spawned before the assignment lands. The host then
`SW_MINIMIZE`s itself.

**Tracking.** The job is bound to an IO completion port. A background thread waits for
`JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO`, which fires when the *entire tree* is gone — this is
what makes the "launcher starts the game and exits immediately" case work. If job setup or
assignment fails (or `--no-job` is passed), it falls back to polling descendants via
`CreateToolhelp32Snapshot` — see SPIKE-RESULTS.md for the measured, and significant,
weakness of that fallback.

The job intentionally does **not** set `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, so closing the
host never kills a running game.

**Return.** On tree-empty the host restores and escalates through foreground paths, logging
which one won:

| Path | Technique |
|---|---|
| `path0` | `SW_RESTORE` + `BringWindowToTop` + `SetForegroundWindow` |
| `path1` | Synthetic ALT down/up via `keybd_event`, then `SetForegroundWindow` |
| `path2` | `AttachThreadInput` to the foreground thread, then `SetForegroundWindow` |
| `path3` | `AttachThreadInput` + ALT combined |
| `path4` | Transient `HWND_TOPMOST` toggle, immediately reverted to `HWND_NOTOPMOST` |
| `path5` | `SW_MINIMIZE` / `SW_RESTORE` cycle |

Each attempt is verified with `GetForegroundWindow() == hwnd` before being declared a
success. `SPI_SETFOREGROUNDLOCKTIMEOUT` is deliberately **not** used — that would be a
system-settings change.

## Logs and tests

* `spike\handoff-log.txt` — timestamped log of every launch, tracking event, foreground
  path and verification result.
* `spike\run-tests.ps1` — unattended test suite. It observes the host from *outside* the
  process (foreground window, minimized state, child liveness) so results are not just the
  host's self-report, and it cleans up any leftover notepad processes. It expects the
  binary `build.cmd` produces (`ShellHost\bin\MarwanShellHost.exe`; `-Exe <path>` overrides)
  and refuses to start if it is missing — `bin\` is gitignored, so build first. **It takes
  over the interactive desktop** (full-screen host, notepad spawns, foreground changes):
  run it on the bench/test account, never on a desktop someone is using.
* `spike\SPIKE-RESULTS.md` — observed results on the target machine.
