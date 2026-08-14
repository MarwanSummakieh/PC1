# PLAN.md — ARC OS engineering decision record

**Machine:** Acer Predator PT314-51s, Windows 11 IoT Enterprise LTSC 2024, build 26100.9168, x64.
**Date:** 2026-08-13. **Status of the machine:** unmodified. No provisioning script has been run;
Shell Launcher is not enabled; the `arcshell` account does not exist.

Evidence base, all in this repo:
[`README.md`](README.md) (design spec) ·
[`notes/research-handoff-and-priors.md`](notes/research-handoff-and-priors.md) (sourced research) ·
[`spike/SPIKE-RESULTS.md`](spike/SPIKE-RESULTS.md) and [`spike/README.md`](spike/README.md) (window-handoff spike, observed on this machine) ·
[`provisioning/README.md`](provisioning/README.md) · [`provisioning/RECOVERY.md`](provisioning/RECOVERY.md) · [`provisioning/MACHINE-CHANGES.md`](provisioning/MACHINE-CHANGES.md) ·
[`usb/README.md`](usb/README.md).

---

## 1. Verdict — is the no-explorer approach viable?

**Yes, viable, with one documented escape lever held in reserve.** Shell Launcher is a
first-class, supported feature on this exact SKU — the edition table on Microsoft Learn
names IoT Enterprise LTSC explicitly, and `C:\Windows\System32\CustomShellHost.exe`
(10.0.26100.8972) already ships in this image
([`notes/research-handoff-and-priors.md` §1.1](notes/research-handoff-and-priors.md)) — so we are
not fighting the platform, we are using a shipped lockdown feature for its stated purpose. The
two constraints that actually shape the design are both known and both already satisfiable:
Shell Launcher will not tolerate a shell that spawns a child and exits (§1.3 — "Shell Launcher
doesn't support a custom shell with an application that launches a different process and exits"),
and the shell must run non-elevated (§1.5.6). Our spike host is exactly that shape — a long-lived
process that owns the game's lifetime through a job object, verified 8/8 on this machine
([`spike/SPIKE-RESULTS.md` §2–§6](spike/SPIKE-RESULTS.md)). The real risk is not "can we replace
explorer" but "what breaks once explorer is gone": the notification area is the hardest loss
(`Shell_NotifyIcon` is a `FindWindow("Shell_TrayWnd")` + `WM_COPYDATA` switchboard — no tray
window, no tray icon, §3.1), toast *rendering* is expected to silently no-op while delivery keeps
working (§3.4), and every community Steam-as-shell setup reports the same failure modes: Big
Picture's "Exit to Desktop" is a no-op, its shutdown/log-off actions do nothing to Windows, and a
boot race where Steam comes up before the network can deadlock a machine with no shell to fall
back to (§3.3). Those are all recoverable by owning the surfaces ourselves — our own volume UI,
our own notifications, our own power actions through the Shell Launcher exit-code policy — which
the ARC OS design already assumes, since the control centre is a first-class screen rather than a
shim over Windows' own ([`README.md` — Interaction](README.md)). **The pragmatic posture is
therefore: replace the shell, own every surface we need, and treat "start a hidden background
explorer after our first frame" as a documented, pre-written fallback lever rather than part of
the design** — a lever we pull only if bench test #1 from
[`notes/research-handoff-and-priors.md` §6](notes/research-handoff-and-priors.md) says it works,
because the sources on that point flatly contradict each other (§3.2: a Microsoft Q&A advisor says
explorer launched under a custom shell gives only a File Explorer window and no shell services;
GamesDows, LaunchBox and Motion-Shell all report the taskbar genuinely appearing). **What is still
completely unproven on this machine must be stated plainly: nothing has ever run under a replaced
shell here, no real game has been launched by the host, the shell-replacement scripts in
`provisioning/` have never been executed, and the foreground-return result — the single most
load-bearing spike result — was measured against `notepad.exe`, not against a fullscreen-exclusive
D3D title.** The verdict is that the approach is sound enough to spend the next milestones on, not
that it is de-risked.

Supporting notes on the verdict:

- **V2 vs the WMI provider.** "Shell Launcher V2" strictly means the 2019 XML schema applied via
  the Assigned Access CSP, which buys UWP-app shells and `AllAppsFullScreen`. Our shell is a Win32
  exe, so the `WESL_UserSetting` WMI provider is sufficient and is far easier to unwind from a
  PowerShell prompt ([`provisioning/README.md` — "v1 vs v2"](provisioning/README.md)). The V2 host
  binary being present matters mainly as evidence the feature family is real in this image. Move to
  the XML path only if we ever need a UWP shell.
- **Startup latency is an unknown, not a blocker.** The one community measurement claims ~55 s of
  extra delay on Windows 11 before the custom shell appears (§1.5.2), from a non-Microsoft
  responder, single report. `boot.html` exists precisely to cover a black gap
  ([`README.md` — "Running the boot screen at startup"](README.md)), but it cannot cover the gap
  *before our process starts*. Measure it (M1); if it is genuinely tens of seconds, the fallback is
  the raw `Winlogon\Shell` value plus our own crash-restart supervisor (§1.5b), at the cost of
  losing the return-code state machine.
- **Recovery is the precondition, not a milestone-9 concern.** A single enabled admin account
  (`brain`) is the whole safety margin. [`provisioning/RECOVERY.md`](provisioning/RECOVERY.md)
  lists five escalating paths, of which (b) Ctrl+Alt+Del → Switch user is winlogon-owned and
  shell-independent, and (d) offline DISM is documented. Two of the assumptions behind them —
  Task Manager reachability under Shell Launcher on 24H2, and Safe Mode bypassing Shell Launcher —
  are explicitly marked UNVERIFIED in that document. They get verified before, not after, the
  first shell replacement.

---

## 2. Stack recommendation

**Build a native Win32/C# host process that owns window management, process lifecycle, input and
Shell Launcher integration, and render the existing ARC OS HTML design inside it in WebView2.**
The decision is made on window-handoff evidence, and that evidence already exists as a working
binary on this machine: `spike/ShellHost/bin/ArcShellHost.exe` was compiled with nothing but the
inbox `csc.exe` (4.8.9232.0, C# 5 language level — there is no .NET SDK, no MSVC and no Windows
SDK on this box) and it demonstrably does the three hard things. Job-object process-tree tracking
survives the launcher-spawns-game-then-exits case that every polling implementation fails,
including Playnite's — the spike's `--no-job` run restored the host 0.85 s after launch while
"the game" was still on screen, which is the most important negative result in
[`spike/SPIKE-RESULTS.md` §4](spike/SPIKE-RESULTS.md), and the job path handled the same case
correctly with the grandchild PID tracked 4.57 s after its parent died (§3). Foreground return
after a child exits is 8/8 via plain `SW_RESTORE` + `BringWindowToTop` + `SetForegroundWindow` in
~15 ms, verified programmatically and by an external observer, with the foreground genuinely stolen
from a third process rather than handed back (§2, §6, §7). Cold-start activation needed the
synthetic-ALT `keybd_event` workaround 11/11 (§7) — so that workaround is load-bearing, not
defensive dead code. Exit codes 0/2/3/99 come out distinct and correct end-to-end, which is exactly
the contract a Shell Launcher return-code policy consumes (§5). Discarding that in favour of a
different runtime means re-proving all of it. On the UI side the argument is just as concrete: the
ARC OS design is *already* HTML/CSS and self-contained with no build step and no fetched fonts
([`README.md` — Files](README.md)); the WebView2 Evergreen runtime 151.0.4129.78 is already
installed on this machine; and WebView2's one serious weakness for a couch UI — input latency and
the browser event pipeline — is mitigated because the host owns the controller and feeds discrete,
already-debounced navigation events into the page over `PostWebMessage`, rather than the page
polling anything itself. **The recommendation is therefore a two-layer host: C#/Win32 for
everything that touches the OS, WebView2 for everything that touches the eye.**

Honest weighing of the alternatives:

| Option | For | Against | Verdict |
|---|---|---|---|
| **Win32/C# host + WebView2** (recommended) | All handoff mechanics already proven here; design already HTML; runtime already installed; buildable today with zero installs | WebView2 interop assemblies are not inbox — needs vendoring or an SDK; +150–250 MB RSS; cold-start of the browser process adds to first frame; WebView2's child HWNDs must not break the proven foreground paths (untested) | **Adopt** |
| **Pure Win32/GDI+ host** (extend the spike as-is) | Zero new dependencies; fastest first frame; already compiles | The design is blur, layered gradients, variable-weight type, staggered motion — reimplementing it in GDI+ is months, and `backdrop-filter`-style blur has no cheap equivalent | Fallback only, with a visibly reduced UI |
| **Godot 4** | Self-contained export, no SDK needed; SDL-based controller stack for free; low input latency | Entire ARC OS design would be rebuilt from scratch in a different layout model; the proven job-object/foreground/exit-code code becomes GDExtension or an out-of-process helper; new toolchain to install | Reject unless WebView2 fails under a replaced shell |
| **WPF / Avalonia** | WPF's `PresentationFramework` is inbox on .NET FX 4.8, so `csc.exe` can build code-only WPF (no XAML compile step without MSBuild); rich enough for the design | Code-only WPF is a hostile authoring model for this much visual design; Avalonia needs NuGet + SDK; still a full redesign of an HTML artefact | Reject |
| **WinUI 3 / UWP shell** | The one path Shell Launcher V2 is explicitly built for | Squarely in the KB5072911 blast radius — XAML-dependent shell components failing to start after cumulative updates, Microsoft-confirmed, fixed only from 2026-06-23 ([research §1.5.4](notes/research-handoff-and-priors.md)) — and needs an SDK we do not have | Reject |

On the toolchain constraint, concretely: WebView2 from `csc.exe` needs three files that are not
inbox — `Microsoft.Web.WebView2.Core.dll`, `Microsoft.Web.WebView2.WinForms.dll` and the native
`WebView2Loader.dll`. The NuGet package ships `net45`-targeted assemblies, so **vendoring those
three files into the repo and referencing them with `csc /reference:` is a repo change, not a
machine change**, and keeps the "builds on a machine with no SDK" property that the spike proved.
The alternative — installing the .NET 8 SDK — is a real machine change and should be a deliberate,
separately-approved decision, not something we slide in; the WindowsDesktop 8.0.30 *runtime* is
already present, so that door stays open later at zero cost today.

**Evidence that would reverse this recommendation:** (a) WebView2 fails to create or render under
a replaced shell — plausible, since nothing under a custom shell has been tested and the browser
process makes assumptions about the session; (b) WebView2's HWND hierarchy defeats the
foreground-return paths that work today for a plain WinForms window, i.e. the M2 real-game handoff
regresses when the host is WebView2-backed rather than blank; (c) first-frame latency under Shell
Launcher plus WebView2 cold start is so bad that the shell is visibly worse than the alternatives
even with `boot.html` covering it; (d) controller-driven navigation through `PostWebMessage`
measures worse than ~1 frame of added latency. Any of (a) or (b) drops us to the pure-Win32
fallback with a reduced UI; (c) alone is survivable by keeping the WebView2 warm across game
launches instead of tearing it down.

---

## 3. Controller decision

The hardware inverts the usual advice. Device enumeration on this machine found only
`VID_054C / PID_0CE6` — two Sony DualSense pads, each with a USB and a Bluetooth enumeration — and
**no device instance path containing `IG_`**, which is precisely the marker XInput uses to identify
XInput-capable devices ([research §4.0](notes/research-handoff-and-priors.md)). The spike confirmed
the consequence empirically: all four XInput slots returned `ERROR_DEVICE_NOT_CONNECTED` (1167) on
every poll ([`spike/SPIKE-RESULTS.md` §8](spike/SPIKE-RESULTS.md)). Under Windows.Gaming.Input a
DualSense is reachable only as `RawGameController`; `Gamepad.FromGameController()` returns null for
it, and WGI is focus-gated ("the axes are all zero when the application loses focus", per SDL's own
WGI backend). GameInput is the only officially supported background-input path, but it needs the
`Microsoft.GameInput` NuGet plus a `GameInputRedist.msi` bundle, and GDK issue #104 — reported on
build 26100 — has background reading callbacks not firing for gamepads.

**Recommendation: read the pads over raw HID, via SDL3 as the input layer, inside the host
process.** SDL already carries the PS5 hidapi driver (USB report `0x01`; Bluetooth report `0x31`,
78 bytes, CRC32 seeded `0xA1`, with the BT stream only starting after a `GET_REPORT 0x20` feature
request), handles hot-plug, and is what Playnite Fullscreen itself uses. It gets us the PS button
as an ordinary HID bit — no undocumented `XInputGetStateEx` ordinal, no GDK redist — plus touchpad,
gyro and adaptive triggers, none of which survive an XInput translation. Keep the already-working
`XInputGetStateEx` ordinal-100 path in the host (it resolves reliably on this build,
`spike/SPIKE-RESULTS.md` §8) as a zero-cost fallback for the day an Xbox pad appears, but do not
design around it. `RawGameController` is the acceptable pure-managed second choice if we want to
avoid shipping a native SDL DLL, at the cost of focus gating and no PS-button reporting.

Steam Input, in practice: on Windows the Steam Overlay **hooks** XInput/DirectInput/RawInput/WGI
inside the game process and injects an emulated Xbox pad — a per-process hook, not a system device
(Steamworks, §4.3). Our shell is launched outside Steam, so it sees the physical DualSense and
Steam's virtual pad never reaches us; double-input is a game-side problem, not a shell-side one.
The inverse is the real risk: raw HID reads are global-focus, so **our polling fires at the same
time as the foreground game** and the shell will act on inputs meant for Palworld unless input
handling is gated on our own window focus — the DirectXTK `Suspend()`/`Resume()` pattern. The one
external hazard to detect and surface is HidHide (a kernel HID filter with a per-exe allowlist,
the recommended companion to DS4Windows and therefore common in exactly the DualSense population):
if a user installs it, the shell sees *zero* controllers and must say so rather than showing "no
controller found".

**Controller wake is not achievable on this hardware. State it as a fact, not a limitation to work
around.** Nothing HID here is wake-armed: the USB stack only sets `DEVICE_REMOTE_WAKEUP` on a
transition to D1/D2, Microsoft's enumerated remote-wake classes are mice, keyboards, hubs, modems
and NICs — gamepads appear nowhere — the Modern Standby wake-source specification (an exhaustive
OEM requirements document) likewise omits gamepads entirely, and 24H2 additionally disables most
wake sources when it detects excessive battery drain. Both our pads are Bluetooth, where the Power
Management tab is typically absent outright. **Waking this machine will be the chassis power button
or the keyboard, permanently.** Design consequence: the shell needs a "reconnecting controller…"
state after resume, because Bluetooth pads generally need a button press to re-associate, and no
feature may assume a pad can start a session.

---

## 4. Milestones to a first living-room-usable session

Nine milestones. Each definition of done is a behaviour observed on this machine — not a file that
exists. **[ELEVATED]** marks milestones blocked on user-present, elevated actions; they cannot be
started by an unattended session.

### M0 — Recovery floor **[ELEVATED, blocks everything]**
**Scope:** Write the bootable USB with `usb/make-boot-usb.ps1`. Record the BitLocker state and
recovery key. Run `provisioning/01`, then `02`, then `03` in order, each with `-WhatIfOnly` first,
recording every row in `provisioning/MACHINE-CHANGES.md`. Discover and write down the real registry
path of the Shell Launcher config while the machine is healthy (RECOVERY.md path (e) says to do
this and it is not documented by Microsoft). Verify the recovery paths **on `arcshell` before any
shell replacement is trusted** — per the standing guardrail that `brain` is never reconfigured.
**Done when:** the machine boots from the USB stick to the Windows Setup screen and `Shift`+`F10`
opens a command prompt; `Get-BitLockerVolume` output and the recovery key are recorded outside this
machine; `WESL_UserSetting.IsEnabled()` returns true; `arcshell` exists as a non-admin local user;
and, signed into `arcshell` with the custom shell active, `Ctrl`+`Shift`+`Esc` opens Task Manager
**and** `Ctrl`+`Alt`+`Del` → Switch user returns to a live `brain` desktop — both confirmed by
doing them, closing the two UNVERIFIED items in `RECOVERY.md` and rows in `MACHINE-CHANGES.md`.

### M1 — The spike, for real, under a replaced shell **[ELEVATED]**
**Scope:** Run the existing four-test kickoff suite (`spike/run-tests.ps1`) with `ArcShellHost.exe`
as `arcshell`'s actual Shell Launcher shell rather than as an app under explorer. Observe the
return-code policy end to end. Time the black gap from credentials-entered to first host frame.
Run bench tests #1 (background explorer), #3 (toasts) and #4 (startup latency) from
[research §6](notes/research-handoff-and-priors.md).
**Done when:** signing into `arcshell` shows the host's window with no taskbar and no desktop;
pressing X powers the machine off and pressing "2" reboots it, matching the
`0→RestartShell, 2→RestartDevice, 3→ShutdownDevice` map; the black gap is measured with a number
written into `MACHINE-CHANGES.md`; and we can state from observation whether `explorer.exe` started
from under the shell gives a real desktop or only a file browser window.

### M2 — Real-game handoff
**Scope:** Launch `Palworld-Win64-Shipping.exe` directly first (job-object tracking, no launcher in
the way), then `steam.exe -silent "steam://rungameid/<id>"` with Steam already running — the case
where the game is a child of the *pre-existing* Steam process and job tracking cannot see it, so
the install-directory watch from Playnite's Steam controller is required (research §2(d)). Watch
the fullscreen-exclusive focus edge specifically: whether `path0` still wins or whether paths 2–5,
never exercised in the spike, finally fire.
**Done when:** from the host, a real game starts, plays, and on quitting returns the host to the
foreground within ~2 s with no black screen, no stuck fullscreen surface and no orphan process —
observed for both the direct-exe and the `steam://` route, and repeated three times each. Which
foreground path won is in the log.

### M3 — WebView2 shell UI
**Scope:** Host `index.html` inside `ArcShellHost.exe` via WebView2 (vendored interop assemblies,
no SDK install). Wire the host's input layer to the page over `PostWebMessage` so the rail, tabs
and control centre respond to the DualSense; keep keyboard working. Boot sequence handled by
`boot.html?next=index.html` or by the same page's boot phase.
**Done when:** signing into `arcshell` produces the ARC OS boot sequence resolving into the home
screen at full screen, and both DualSense pads move the rail, open the control centre and launch
the focused tile — and M2's handoff behaviour is re-observed unchanged with the WebView2-backed
window, since that is the regression this milestone risks.

### M4 — Library aggregation from the sources that actually exist here
**Scope:** Replace the hand-written `APPS` object in `index.html` ([README — Extending it](README.md))
with a real scan: Steam `libraryfolders.vdf` + `appmanifest_*.acf` parsing for Palworld, CS2 and
Dota 2; Riot's install metadata for League and 2XKO; and a loose-exe/registry entry for the
standalone TEKKEN 8. No Epic/GOG/Battle.net/Xbox providers — nothing here uses them, and building
speculative providers is how this project acquires untested code. Enumerate packaged apps, if ever
needed, with `PackageManager.FindPackagesForUser` rather than `shell:AppsFolder`, which is reported
to go stale under a custom shell (research §5.3).
**Done when:** the rail on a cold boot shows exactly the six installed titles with correct names
and install sizes, discovered by scanning — verified by uninstalling or moving one title and seeing
it disappear on the next scan without any code being edited.

### M5 — Artwork with an offline-first cache
**Scope:** SteamGridDB fetch for hero/tile art, written to a local cache directory; the cache is
the source of truth at render time and the network is only ever a background refresh. The design's
current fallback is the `G` gradient table — two stops per key, used for both tile face and hero
wash — so a missing image degrades to a coloured gradient box, never to a broken image.
**Done when:** the shell renders real artwork for all six titles from cache; with Wi-Fi turned off
and the cache intact, a cold boot looks identical; with Wi-Fi off and the cache deleted, every tile
falls back to its gradient and the shell still boots and launches games with no error dialog and no
stall.

### M6 — Settings and power, wired to the exit-code policy
**Scope:** Control centre becomes functional: volume via the Core Audio APIs (there is no tray
mixer to fall back on, §3.1), display/audio device state, controller battery from the HID report,
and the three power actions. Sleep is an in-process `SetSuspendState`, **not** an exit. Restart and
shutdown exit with codes 2 and 3 and let Shell Launcher act, keeping us inside the ≤4 custom-code
budget and leaving exit 1 free as a "drop me to a repair state" `DoNothing` code (research §1.3).
**Done when:** from the control centre on `arcshell`, Sleep suspends the machine and the power
button resumes it back into the shell with the pads reconnecting; Restart reboots into the shell;
Shut down powers the machine off; and the volume slider changes system volume with no explorer
running.

### M7 — Recovery, watchdog and update-in-place hardening
**Scope:** In-shell escape hatch (a deliberate, non-obvious input that exits with the repair code
and lands the user somewhere recoverable). Watchdog behaviour promoted from a test flag to a real
supervisor: if the WebView2 process dies, the host survives and re-creates it rather than exiting.
An `AutoRestartShell` analog: confirm that a hard crash of the host results in Shell Launcher
restarting it rather than a black screen, and that a *repeated* crash cannot become an infinite
exit/restart loop (Microsoft's own warning, research §1.3). Update-in-place strategy: the shell
binary cannot be overwritten while it is the running shell, so updates stage to a side directory
and swap on next sign-in.
**Done when:** killing `ArcShellHost.exe` from a `brain` session over Switch-user brings the shell
straight back on `arcshell`; killing the WebView2 process leaves the host alive and the UI
returning within a few seconds; a deliberately crash-looping build is escapable without WinRE; and
a staged binary update applies on the next sign-in without any elevated step.

### M8 — First living-room session (acceptance)
**Scope:** No new features. One sitting, from a cold machine, on the television, using only a
DualSense and the power button.
**Done when:** power on → boot sequence → home screen → launch a game with the pad → play → quit →
back at the home screen → launch a second game → quit → sleep from the control centre → power
button → back at the home screen, with no keyboard touched, no mouse touched, no error dialog and
no drop to a Windows surface at any point.

---

## 5. Open risks, ranked

| # | Risk | Why it ranks here | Evidence | Mitigation / first test |
|---|---|---|---|---|
| 1 | **Foreground return against a real fullscreen-exclusive game is untested.** | It is the single mechanic the entire product rests on, and the only evidence is against `notepad.exe`. `path0` won 8/8 but paths 2–5 have never fired even once, so four of our six recovery paths are compiled, unproven code. A game that leaves the display in an exclusive mode on exit could leave a black screen with no shell behind it. | [`spike/SPIKE-RESULTS.md` §7, §10](spike/SPIKE-RESULTS.md) — "should not be assumed working against a real fullscreen exclusive game" | M2. Also test hide-vs-minimize for the host, which §10 flags as unmeasured, and keep the watchdog armed during the first runs. |
| 2 | **Riot Vanguard under a replaced shell.** | Kernel-mode anti-cheat present on this machine, loaded at boot, with its own opinions about process environments. No research in this repo covers it and no community source is known to have tested Vanguard with no explorer. If it refuses to start League/2XKO under Shell Launcher, that is two of six titles gone with no in-shell workaround. | Not covered in [`notes/research-handoff-and-priors.md`](notes/research-handoff-and-priors.md) — an acknowledged gap, not a resolved question | Test in M2 as its own case, before the library work. If it fails, the background-explorer lever (risk 4) becomes the mitigation rather than a nicety. |
| 3 | **Store-client tray dependence — Steam only, for now.** | Steam is the launcher for three of six titles. Its tray icon is rendered through `steamwebhelper` and breaks even *with* explorer; with no `Shell_TrayWnd` it cannot be created at all, so Steam runs with no visible affordance. Big Picture's Exit/shutdown actions are reported no-ops without explorer, and there is a documented boot race where Steam starts before the network and prompts offline/online with no shell to fall back to. | [research §3.1, §3.3](notes/research-handoff-and-priors.md) — the §3.1 tray inference is marked UNCERTAIN, the §3.3 failure modes are COMMUNITY but consistent across several projects | Never rely on Steam UI for anything; always launch with `-silent` and a `steam://rungameid` URI; own the "Steam is starting" state in our UI; bench test #7. |
| 4 | **Unresolved contradiction: does a background explorer work under Shell Launcher?** | This decides whether tray icons, toast rendering and `shell:AppsFolder` come back for free, and it is the mitigation of last resort for risks 2 and 3. Sources directly contradict each other and the reconciliation hypothesis — that Shell Launcher actively suppresses the standard shell while a raw `Winlogon\Shell` swap does not — is stated in the research as inference with no source. | [research §3.2](notes/research-handoff-and-priors.md), flagged "Test #1 for us" | Bench test #1, in M1. Cheap to run, high leverage. Whatever the answer, write it into the research doc as PRIMARY-on-this-machine. |
| 5 | **Unresolved contradiction: "UWP requires explorer.exe".** | Lower rank only because nothing installed here is a packaged app — no Xbox app, no Game Pass titles. It becomes a live risk the moment anyone wants Settings, the touch keyboard, or a Store title from the shell. The structural evidence is good (this machine's registry resolves `CLSID_ApplicationActivationManager` to `InProcServer32 = twinui.appcore.dll`, so the activation manager loads in-process rather than being explorer-hosted) but nobody has published a test. | [research §3.4, §5.1, §5.4](notes/research-handoff-and-priors.md) | Deferred. If it becomes relevant, bench test #2: a 30-line `IApplicationActivationManager` harness under the custom shell. Prefer `ActivateApplication` over `explorer.exe shell:AppsFolder\…`, which is a trap here for exactly the reason in risk 4. |
| 6 | **KB-race regressions on future cumulative updates.** | Microsoft has already shipped one CU-triggered regression that breaks XAML-dependent shell components on first logon after update (KB5072911, affecting `explorer.exe`, `shellhost.exe`, Start, Settings, Taskbar). This build (26100.9168, Aug 2026) is past the KB5095093 fix, but the class of bug recurs, and a machine whose *only* shell is ours has no fallback UI when a CU lands badly. | [research §1.5.4](notes/research-handoff-and-priors.md) — PRIMARY, Microsoft KB | Chose a non-XAML UI stack partly for this reason (§2). Keep the USB from M0 permanently near the machine. Consider deferring feature updates and reading the KB before applying CUs, rather than letting the living-room box update unattended. |
| 7 | **Single-admin-account recovery posture.** | Exactly one enabled account exists (`brain`, admin, UAC-filtered token). Every in-session recovery path in `RECOVERY.md` terminates at "sign into `brain`". If that profile is damaged, or its password is lost, the only routes left are WinRE and the USB — both of which will demand the BitLocker recovery key if the volume is protected. | [`provisioning/RECOVERY.md`](provisioning/RECOVERY.md) — paths (c), (d), (e); the BitLocker precondition is called out at the top | M0 is written to close this: record the BitLocker key off-machine, keep the `brain` session alive via Switch user rather than Sign out during every test, and never give `brain` a Shell Launcher config — which `03-apply-shell-launcher.ps1` enforces by aborting on any admin or non-local SID. A second local admin account is worth considering, as a user decision. |

---

## Standing constraints (do not violate without a new decision here)

1. `brain` is never given a Shell Launcher configuration, never modified, never deleted.
2. No machine-state change without an explicit per-script confirmation, recorded in
   `provisioning/MACHINE-CHANGES.md` with its undo command written down first.
3. The shell runs **non-elevated** — required by Shell Launcher with UAC on, and required for
   packaged-app activation to work at all.
4. The shell process never spawns a child and exits; it owns the game's lifetime through the job
   object for as long as the game runs.
5. Distinct exit codes stay within a ≤4-mapping budget, with `DefaultAction` always set explicitly.
