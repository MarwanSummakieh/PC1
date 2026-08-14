# ShellHost spike — observed results

**Machine:** Windows 11 IoT Enterprise LTSC 2024, 10.0.26100, x64. Primary screen 1280×720.
**Date of run:** 2026-08-13.
**All results below were observed by actually running the binary on this machine.** Nothing
is simulated or inferred.

No system setting, registry value, service or account was changed. Nothing was installed.
Only `notepad.exe` and `cmd.exe` were used as child processes.

---

## 1. Toolchain

| Checked | Result |
|---|---|
| `dotnet --list-sdks` | **empty** — "No SDKs were found" |
| `dotnet --info` | runtimes only: `Microsoft.NETCore.App 8.0.30`, `Microsoft.WindowsDesktop.App 8.0.30` |
| `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` | **present**, version **4.8.9232.0**, "for C# 5" |

**Built with:** the inbox .NET Framework compiler. Single source file
`spike\ShellHost\ShellHost.cs` (1065 lines) → `spike\ShellHost\bin\ArcShellHost.exe` via
`spike\ShellHost\build.cmd`:

```
csc.exe /target:winexe /platform:x64 /optimize+ /warn:4 /out:bin\ArcShellHost.exe
        /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll
        ShellHost.cs
```

Compiles clean (no warnings at `/warn:4`). The C# 5 language level is a real constraint —
no string interpolation, `nameof`, or expression-bodied members.

**Naming finding:** Windows 11 ships `C:\Windows\System32\ShellHost.exe` (a live system
process, seen running as PID 18516 during testing). The spike binary was renamed to
`ArcShellHost.exe` so process-level checks are unambiguous. Worth keeping for the real host.

---

## 2. Test A — direct child, return to foreground

`--child=notepad.exe --auto-launch=1500 --auto-kill=4000 --auto-exit=2500 --exit-as=ESC`

**PASS.** Exit code 0.

External observer timeline (foreground process / is-host / host-minimized / #notepad):

```
   585 | ArcShellHost | True  | False | 0     <- host has foreground
  1642 | msedge       | False | True  | 1     <- launched, host minimized itself
  1914 | notepad      | False | True  | 1     <- child owns the screen
  6017 | ArcShellHost | True  | False | 0     <- child killed, host is back
  8410 | msedge       | False | False | 0     <- host exited
```

Host log:

```
23:17:26.435 [LAUNCH] CreateProcess(suspended) ok: 'notepad.exe' pid=4936
23:17:26.437 [TRACK]  AssignProcessToJobObject ok - tracking mode = JOB OBJECT
23:17:26.443 [HANDOFF] host minimized (SW_MINIMIZE)
23:17:30.620 [TRACK]  job: ACTIVE_PROCESS_ZERO - child tree is empty
23:17:30.647 [RETURN] foreground before restore: pid=22868 proc=msedge
23:17:30.654 [RETURN] forced-foreground path: path0:SW_RESTORE+BringWindowToTop+SetForegroundWindow (0 ms)
23:17:30.654 [VERIFY] PASS GetForegroundWindow()==host hwnd (0x1E039E)
```

Programmatic verification (`GetForegroundWindow() == host hwnd`) **passed**, and was
independently confirmed by the external observer. Return latency from child death to
verified foreground: **~15 ms**.

Note the foreground window immediately before the restore was **msedge**, not the child —
so this was a genuine steal, not a trivial hand-back.

## 3. Test B — launcher spawns child then exits (job-object tracking)

`--child="cmd.exe /c start notepad.exe" --auto-launch=1500 --auto-kill=5000`

**PASS.** Exit code 0. This is the case the job object exists for.

```
23:17:34.916 [LAUNCH] CreateProcess(suspended) ok: 'cmd.exe /c start notepad.exe' pid=5560
23:17:34.916 [TRACK]  AssignProcessToJobObject ok - tracking mode = JOB OBJECT
23:17:34.918 [TRACK]  job: NEW_PROCESS pid=5560     <- cmd
23:17:34.920 [TRACK]  job: NEW_PROCESS pid=12380    <- conhost
23:17:34.922 [HANDOFF] host minimized
23:17:35.502 [TRACK]  job: NEW_PROCESS pid=3832     <- notepad, spawned by cmd
23:17:35.511 [TRACK]  job: EXIT_PROCESS pid=5560    <- cmd exits ~0.6 s after launch
23:17:35.519 [TRACK]  job: EXIT_PROCESS pid=12380
23:17:39.935 [AUTO]   auto-kill: tracked pids = [3832]
23:17:40.085 [TRACK]  job: ACTIVE_PROCESS_ZERO - child tree is empty
23:17:40.113 [RETURN] forced-foreground path: path0:... (1 ms)
23:17:40.114 [VERIFY] PASS GetForegroundWindow()==host hwnd (0x430574)
```

The launcher (`cmd`) exited **4.57 seconds before** the host came back. The external
observer confirms the host stayed minimized across that whole window, with notepad in the
foreground. Job PID enumeration correctly reported `[3832]` — the grandchild — after cmd
was gone, which is also what the auto-kill used.

Creating the child **suspended** and assigning to the job *before* `ResumeThread` is what
makes this airtight; a plain `Process.Start()` followed by assignment can lose the race.

## 4. Test D — the same case with `--no-job` (fallback path) — **FAILS, as expected**

`--child="cmd.exe /c start notepad.exe" --no-job --auto-launch=1500 --auto-kill=5000`

**The fallback does not survive this case, and this is the most important negative result
in the spike.**

```
23:17:44.566 [LAUNCH] CreateProcess(suspended) ok: 'cmd.exe /c start notepad.exe' pid=2516
23:17:44.566 [TRACK]  tracking mode = TOOLHELP POLL (--no-job specified)
23:17:44.573 [HANDOFF] host minimized
23:17:45.415 [TRACK]  poll: no live descendants of pid 2516 - child tree is empty
23:17:45.426 [RETURN] foreground before restore: pid=7976 proc=notepad title='Untitled - Notepad'
23:17:45.431 [RETURN] forced-foreground path: path0:... (0 ms)
```

The host restored itself **0.85 s after launch, while notepad was still running and owned
the screen** — it stole the foreground back from the "game". External observer confirms it:

```
  2384 | notepad      | False | True  | 1
  2651 | ArcShellHost | True  | False | 1     <- host in foreground, notepad STILL running
```

Cause: `start` hands the spawn off, `cmd` exits, and the descendant walk loses the chain
because the surviving process is no longer parented to anything the host is tracking. The
auto-kill then found no tracked PIDs, so notepad was left running and the harness had to
clean it up.

**Conclusion: the toolhelp fallback is not a real safety net for launcher-style titles.**
Treat job-object assignment as a hard requirement; if `AssignProcessToJobObject` ever
fails, that should be surfaced as a degraded mode, not silently accepted. (Note also that
the *reverse* failure — restoring too late — never occurred; the fallback's failure mode is
restoring too early, which is the more visible one for a user.)

## 5. Test C — exit codes

Verified two independent ways: `Process.ExitCode` from the PowerShell harness, and the
literal `ERRORLEVEL` a Shell Launcher policy would observe.

```
cmd /v:on /c "start /wait ArcShellHost.exe --auto-exit=1000 --exit-as=<K> & echo !ERRORLEVEL!"

ESC -> ERRORLEVEL=0     B -> ERRORLEVEL=0     X -> ERRORLEVEL=2     Y -> ERRORLEVEL=3
```

| Trigger | Expected | Observed (`Process.ExitCode`) | Observed (`ERRORLEVEL`) |
|---|---|---|---|
| `Esc` | 0 | 0 | 0 |
| B | 0 | 0 | 0 |
| X | 2 | 2 | 2 |
| Y | 3 | 3 | 3 |
| watchdog | 99 | 99 (observed during the failed run in §7) | — |

**PASS.** All four paths produce distinct, correct codes.

> Gotcha for future test scripts: `cmd /c "start /wait app.exe & echo %ERRORLEVEL%"` always
> prints 0, because `%ERRORLEVEL%` is expanded at parse time. `/v:on` with `!ERRORLEVEL!` is
> required. The first run of this test produced a false 0/0/0/0 for exactly this reason.

## 6. Return reliability — 5 consecutive cycles

`--child="cmd.exe /c start notepad.exe" --cycles=5 --auto-launch=1200 --auto-kill=2000 --exit-as=Y`

```
[SUMMARY] cycles launched=5 returned=5 foregroundPASS=5 foregroundFAIL=0
          paths=[c1:path0 | c2:path0 | c3:path0 | c4:path0 | c5:path0]
```

**5/5 returns verified**, every one via `path0`, each in 0–1 ms. Process exited with code 3
as requested. No leftover processes.

## 7. Which foreground code path actually wins

This is the headline answer to "does forced foreground work, and how".

| Situation | Path that succeeded |
|---|---|
| **Return after a child exits** (all 8 observed returns) | **`path0`** — plain `SW_RESTORE` + `BringWindowToTop` + `SetForegroundWindow`, 0–1 ms |
| **Cold start**, when msedge owned the foreground (all 11 observed starts) | **`path1`** — `path0` failed every time; the synthetic **ALT down/up via `keybd_event`** made it succeed, ~10 ms |

```
23:17:25.334 [FOREGROUND] path0 failed (fg=... proc=msedge ...), trying ALT-key workaround
23:17:25.344 [FOREGROUND] startup activation via path1:keybd_event(ALT down/up)+SetForegroundWindow
```

So **the ALT workaround is genuinely required** — not defensive dead code. It is exercised
on every cold start on this machine. The reason the return case is easier is that the host
process retains foreground-activation rights as the parent of the process that just owned
the foreground; a cold-started host has no such right and is blocked by the foreground lock.

`path2`–`path5` (AttachThreadInput, combined, transient topmost, minimize/restore cycle)
were **never needed** in any observed run, so they are implemented and compiled but remain
**unproven in practice**. They should not be assumed working against a real fullscreen
exclusive game.

`SPI_SETFOREGROUNDLOCKTIMEOUT` was deliberately not used — it is a system setting.

## 8. Gamepad / XInput

**No controller is physically connected to this machine.** All four slots returned
`ERROR_DEVICE_NOT_CONNECTED` (1167) on every poll, at startup and on the post-return
re-poll:

```
[XINPUT] re-poll after return: no controller in slots 0-3 (all return ERROR_DEVICE_NOT_CONNECTED)
```

The only HID devices present are an "Acer Airplane Mode Controller" and a "HID-compliant
system controller" — neither is a gamepad.

**Therefore: button mapping, thumbstick/trigger readings, the Guide button, and
"re-poll works after return" are NOT verified against real hardware.** They must be
re-tested when a controller is available. Nothing about live input is claimed here.

What *was* verified without hardware:

| Item | Result |
|---|---|
| `xinput1_4.dll` loads | **yes** |
| `XInputGetState` resolves by name | **yes** |
| **`XInputGetStateEx` via `GetProcAddress` ordinal #100** | **RESOLVED** — non-null, at `0x7FFA00B29BD0`, consistently across every run |
| Calling the ordinal-100 function | **works** — the ~60 Hz poll loop calls it as its primary path for many thousands of calls with no crash, no stack corruption and a correct `1167` return, which is good evidence the `stdcall` signature binding is right |
| Guide bit (`0x0400`) actually reported | **unverified — needs hardware** |

So the answer to "does ordinal #100 resolve on this OS build": **yes, reliably.**

---

## 9. Bugs found and fixed during the spike

1. **`--child` clobbered by positional args.** PowerShell's `Start-Process -ArgumentList`
   joins without quoting, so `--child=cmd.exe /c start notepad.exe` arrived as four argv
   entries; the parser then let the positional tail overwrite the explicit `--child`,
   producing `CreateProcess FAILED ... err=2 (ERROR_FILE_NOT_FOUND)`. Fixed on both sides
   (parser now ignores positionals when `--child` is explicit; harness quotes the value).
   **The watchdog caught this cleanly** — both affected runs exited 99 instead of hanging,
   which is exactly what it is for.
2. **`%ERRORLEVEL%` parse-time expansion** producing false-pass exit codes (see §5).
3. **Binary name collision** with the Windows 11 system `ShellHost.exe` (see §1).

## 10. What this spike does and does not de-risk

**De-risked:**
- Building and shipping a native host with zero installed toolchain.
- Job-object tracking of a launcher → game → launcher-exits chain. Solid.
- Deterministic, distinct exit codes for a future Shell Launcher policy.
- Return-to-foreground for ordinary windowed children — 8/8, fast, and programmatically
  verified rather than eyeballed.

**Still open:**
- Foreground return against a **real fullscreen exclusive game** is untested; paths 2–5
  have never fired. This is the remaining sharp edge.
- All gamepad behaviour beyond DLL/ordinal resolution — needs a physical controller.
- Multi-monitor, DPI scaling, and what happens if the child never creates a window.
- The host currently minimizes; whether minimize or hide behaves better against
  fullscreen-exclusive titles is unmeasured.

## 11. Cleanup

Every window opened during testing was closed and every child process terminated. Final
check after the suite: `notepad = 0`, `cmd = 0`, `ArcShellHost = 0`. No stray processes
left behind.
