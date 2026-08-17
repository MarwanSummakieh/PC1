# MarwanOS — state of the build

Last updated 2026-08-16, after the privileged-actions session (broker verbs, `admin.*`, the grant sheet,
the web permission bridge) and its end-to-end bench pass.

## Where it runs

| | |
|---|---|
| **Test bench** | `DESKTOP-6BCSJ3P` (LAN IP kept out of the repo) — Windows 11 IoT Enterprise LTSC 2024 **Evaluation** (90-day), build 26100.9168, i7-6700K, 8 GB, Odyssey G85SD 3440×1440 @120 Hz, wired Ethernet, DualSense on USB. **No Wi-Fi, no Bluetooth hardware.** This is where everything is verified. |
| **Laptop** | `DESKTOP-AGB796T` — the dev machine. Has Wi-Fi and Bluetooth, so radio-dependent code is verified here. Its shell is untouched: `brain` still gets a normal Windows desktop. |

Live shell on the bench, as actually observed on 2026-08-16: `C:\ArcOS\web\v10\ArcShellHostWeb-v11.exe`,
bound to the `arcshell` SID via Shell Launcher, enforcement on. (The ARC names are the bench's; see the
naming gap below. Test builds go in a sibling `C:\ArcOS\web\vNN\` and never touch the live one.) Reached over SSH with an elevated session — sshd does not depend on the
interactive shell, so **a broken shell is still remotely recoverable**. That is the primary safety net.

## What actually happens when you power it on

Firmware → no Windows logo → no logon screen → `marwanshell` signed in automatically → MarwanOS boot sequence
→ home screen. No explorer, no taskbar, no Start menu, no desktop. Roughly 30 s cold to usable.

## What is built and observed working

| Area | State |
|---|---|
| Shell replacement | Real. `winlogon` starts our process as the shell; no explorer/Start/Search in the session. `sihost` *does* run — the session is not as barren as "no explorer" suggests. |
| Window handoff | Launch → hide → job-object tracking of the whole child tree → return with **verified** foreground. Survives the launcher-spawns-child-then-exits case that defeats polling. |
| Return-code policy | exit 0 → shell restarts (79–94 ms); exit 2 → device restarts (~26 s back to shell). exit 3 → shutdown **still unverified**. |
| DualSense | Raw HID, offsets verified against the device's own report descriptor. USB and Bluetooth layouts. Battery decoded. Reconnects cleanly. Chromium's Gamepad API also sees it. |
| UI | The MarwanOS design running as the shell in WebView2, served from a virtual host. Boot sequence resolves into the home screen. |
| Navigation | Focus-scope stack (Circle always pops one level), 2D movement with per-row column memory, contextual hint bar with 16 inline PS5 glyphs, keyboard/pad glyph swapping. |
| Scaling | Root-font-size driven; verified at 1280×720, 1920×1080, 2560×1440, 3440×1440, 3840×2160 and portrait. Overscan safe area. |
| On-screen keyboard | 5 modes, hold-to-repeat, 6,862 headless grid assertions + 30 behavioural. |
| Settings | Ten panels on real state: profile, network, display, sound, controllers, power, storage, **add software**, system/update, date & time. |
| File explorer | Real file management with recycle/permanent delete, copy/move as cancellable jobs with progress, 10,000-entry folders at 46 ms first paint. |
| Browser | Tabs, address bar, TLS chip, history, per-site zoom, pinned apps, media keys, spatial + cursor navigation. Crash-isolated from the shell. |
| Extensions | Installable from the sofa: the sheet lists `.zip`/`.crx` files the browser has downloaded and unpacked folders on a USB stick, unpacks the chosen one into `C:\MarwanOS\extensions` and adds it to the profile live. On/off and remove-with-confirmation too. Bench-verified by pad presses only — install, running, remove, files deleted. |
| Downloads | Taken off Chromium at `DownloadStarting` (its own flyout is suppressed) and drawn by the shell: progress chip in the chrome row, a focusable Downloads sheet with pause/resume/cancel/forget, show-in-Files, and a stated reason for every failure. Bench-verified end to end against a local server, 4 MB undeclared-length and 24 MB declared-length, both landing in `%USERPROFILE%\Downloads`. |
| **Pointer mode for foreign windows** | The pad drives the **real Windows cursor** (SendInput, absolute over the virtual screen) whenever the front window is not the shell's and is not a game: touchpad surface and left stick move it, the d-pad steps 8 px, Cross and the touchpad button are the left button (down/up, so dragging works), Square is the right button, two fingers or the right stick are the wheel, a touchpad tap is a click, L1/R1 page, Options is Enter, Circle is Escape, **Triangle brings the shell forward with the on-screen keyboard and types what you enter into the window it remembered**, L3 toggles. Bench-verified 2026-08-16 against a real foreign window (`ptrtarget.exe`, its own process, its own message loop, logging what actually arrived): stick moves of 225/−210/+135 px, five 8-px d-pad steps, `mouse LEFT DOWN`/`UP`, `WM_CONTEXTMENU`, 9 wheel notches up then 9 down, `WM_KEYDOWN` PageUp/PageDown/Enter/Escape, and a touchpad drag/tap that moved the cursor and clicked. Typing: `[PTR] typed 4 chars into 1228 (enter=yes)` and the target logged `t`,`e`,`s`,`t`,Enter — the text is never logged. Engagement is by rule, and the rule fired unaided on the real installer window: `[PTR] on reason=the front window belongs to a process this shell cannot open (elevated or SYSTEM…)`. See the gap below for what it then could not do. |
| **Privileged actions / permission grants** | The shell holds no administrator token and asks for none. A verb allowlist (`install`, `updates.install`, `wifi.forget`, `bt.forget`) is handed to the SYSTEM broker task authorised once at provisioning; the host exposes it as `admin.status` / `admin.catalog` / `admin.request` (all `elevation:"no"`), and the shell asks with **one** consent sheet — `Grant.ask` — whose Allow must be **held ~800 ms**. Deny holds the focus; a tap does nothing. Bench-verified end to end on 2026-08-16 by pad tokens only, as `arcshell`, non-elevated, **no UAC prompt and nobody at the machine**: Settings → Add software → Visual C++ 2015-2022 (x64) → sheet → tap ignored → hold → real broker job (24.4 MB fetched, Authenticode publisher pin, `status=OK exit 0`) in 17 s. Settings → System's "Install pending updates" row is unreachable until a check finds something, then opens the same sheet ("Install 10 Windows updates?"). |
| **Placeholder sweep (2026-08-16)** | Every focusable control in the shell was audited end-to-end (page → host verb) and the ones that only pretended were made real: `powerFail` from the host is now heard (Rest/Sign out say "Windows refused…" instead of a success toast); the notepad "placeholder child" launch fallback is gone (a tile with nothing to open says so); a third rail tab **Apps** shows the non-game entries the scan already found; the scanning tile's Status fact is live and its press reports progress; read-only rows (All adapters, API catalogue) open real detail panels instead of chiming and doing nothing; "Test the vibration" reads the host's `hapticAck` before claiming a pulse; browser tile facts read the persisted engine and tab cap. Files: Cancel is honest on Recycle-Bin jobs, Open/Properties act on the marked item, Find works at the drive picker, Delete says up front when a drive has no bin. Browser: **PS opens the control centre from a web page** (browser steps aside via `shellHold`, comes back on Circle), Forward with nothing ahead stays put, Square hint only on removable pins, **Site permissions** sheet (list/forget — the host verbs already existed), pointer-mode row cannot invert, Stop-loading and Play/pause rows, failed-extension row opens its folder in Files. mosnav: D-pad steps the pointer in cursor mode, Square refuses full screen when there is no player, R3 says "already in view" when it is. Verified locally by pad tokens (`.stage/shots/ps*.png`, `bfix-*.png`, `files-audit-*.png`, `home-*-apps*.png`). **Deployed to `C:\ArcOS\web\v10\` (backup in `_bak-20260816-2020\`) but the live shell was NOT restarted — it was in use with a Riot installer running under it; the new pages load on the next shell restart.** Not touched: boot.html's invented boot-step names (a design call, not a button). |
| **League of Legends through the broker (2026-08-16 evening)** | brain hit the wall for real: two copies of `Install League of Legends na.exe` sat on an unanswerable UAC prompt on the bench. Fixed at the root, not the symptom: League (NA + EUW) is in the broker manifest, publisher-pinned to `Riot Games`, and marked **`interactive`** — the worker verifies the installer, then starts it as SYSTEM **inside the console session** (`psexec -s -i` shape) so Riot's own setup/sign-in UI runs elevated on the console, which is the only way Riot's *elevated agent* exists to put Vanguard in without a UAC prompt. Bench-observed: `'League of Legends Installer'` visible over the shell, SYSTEM-owned, session 1, no prompt (`C:\ArcOS\diag\s1\frame.jpg`); the Riot Client itself installed OK by the silent path first. Worker also fixed to wait on the installer process only (not the tree — Riot's leaves its client running) with a SYSTEM-only `postKill`, plus a `path` capability for setups other programs download; host `admin.request` wait is activity-aware (task Running ⇒ keep waiting, 4 h cap); `admin.catalog` returns `ready`/`interactive`/`source`; LibraryApi has a **Riot source** (machine-wide `RiotClientInstalls.json` + `Metadata\*.product_settings.yaml`) so a broker-installed Riot Client shows on the rail with no shortcut. v14 staged at `C:\ArcOS\web\v14\`, live shell not restarted. |
| **Web permissions** | WebView2's `PermissionRequested` is deferred on the content tab and answered by the same grant sheet, so no Edge bubble is ever drawn — Chromium's own prompt lives in a child HWND a controller cannot reach. Decisions optionally persist to `permissions.json` beside the browser profile. Bench-verified 2026-08-16 against `http://127.0.0.1:8099`: geolocation → sheet → "Remember for this site" on → hold → allowed and written to the store; notifications → queued behind it → Circle → denied once. The content view is hidden while a sheet is up and shown again after. |

## Known gaps — read before trusting anything above

* **Exit code 3 (shutdown) has never been observed.** Reported as working, but the host log records no
  such exit and uptime contradicts it. Press `3` (or Triangle in the bare host) to close this out.
* **No real game has ever been launched by the shell.** Every handoff test used `notepad.exe`. Fullscreen
  exclusive D3D — the actual hard case — is untested. This is the largest remaining risk and the whole
  reason the project exists.
* **Nothing has been driven by hand with the pad.** Routing is proven end to end; the decode→action edge,
  stick feel, repeat cadence, and whether Cross/Circle are the right way round all need a human.
* **No eyes on the screen.** Everything visual is verified numerically. Nobody has judged whether it looks
  good at 3440×1440 from three metres.
* **Wi-Fi and Bluetooth panels** cannot be finished on the bench — no radios. Needs a USB adapter.
* **USB removable-drive detection and eject** have never run against hardware. Needs a stick plugged in.
* ~~**`hostinfo` race**~~ — **fixed 2026-08-14.** The host answered the shim's document-created `ready`
  before index.html had attached its own listener, and WebView2 drops a message with no listener, so
  `Host.caps` stayed all false on a host that had everything. The page now re-sends `ready` itself the
  moment its listener exists and reads the reply it can actually hear; `ready` rather than a new verb so
  the already-deployed binaries are repaired too. Verified 10/10 consecutive bench runs on
  `MarwanShellHostWeb-v5.exe`, logs under `C:\MarwanOS\web\hostinfo-runs\`. Run 5 caught the old race live —
  one `hostinfo` line instead of two, i.e. the unsolicited reply was lost and only the requested one
  arrived.
* **The bench rebooted uncleanly twice on 2026-08-14** (00:50 and 11:17), Kernel-Power 41 with no
  bugcheck, no minidump, no `MEMORY.DMP` — so a hard hang or power loss, not a BSOD. The 11:17 one
  killed a verification sweep mid-run. Windows logged `RADAR_PRE_LEAK_64` against `msedgewebview2.exe`
  four minutes earlier, so memory pressure on the 8 GB box is a suspect but not proven. Untriaged, and
  it undermines every long unattended run on the machine everything is verified on.
* **The live shell restarted once mid-sweep** (12:09:25, during run 4) with no reboot and no session
  change — Shell Launcher silently restarting it on a clean exit. No WER report, no Application or
  System event, so it did not crash; why it exited is unknown. It cannot be known: the Shell Launcher
  shell runs with **no `--log`**, so every restart it does is invisible. Worth noting that the harness
  starts the test host *into the live marwanshell session*, where it shares the DualSense HID and the
  foreground with the running shell. Untriaged, and unrelated to the `hostinfo` fix.
* **The pad is a mouse for foreign windows now, and the broker hands it a window it can reach.** Pointer
  mode drives an ordinary vendor window fine. Its wall (B29) was that the install broker ran `interactive`
  installers as **SYSTEM**, and `SendInput` is subject to UIPI — a Medium process may inject only into
  applications at its own integrity or lower — so every move and click on the real `League of Legends
  Installer` was discarded, cursor unchanged at (1720,718). **Fixed at the token (B30):** the worker now
  starts interactive installers under the **console user's own linked admin token**, and where UAC
  elevates admins silently (this bench, since B28) lowers that token to **Medium** integrity, so the
  window sits *at* the shell's level instead of above it; a new host rule (8b) engages the pointer on a
  window that opens but carries an elevated token. Verified on the bench that the worker launches the
  installer as `arcshell IL=0x2000 elevated=1 session=1`, that a Medium shell process can open it and
  read `elevated=1` (so the pointer engages by rule), and that the leftover/postKill logic tells the
  broker's installer apart from the player's own Riot Client (10 of brain's processes survived a
  targeted stop while the ticket still resolved `OK`). **Not yet shown:** the actual `[PTR]` engage +
  cursor-move over the installer — the session-1 run aborted itself (correctly) because the bench was
  occupied by a live Riot sign-in with Vanguard installing (console idle 0 ms); that shot is deferred to
  an unattended bench, not blocked in code. Where the console user is a standard user the worker still
  falls back to the SYSTEM launch (a mouse can drive it; the pad cannot). Vanguard demands a restart when
  it goes in.
* **The touchpad decode has never had a real finger on it.** The 12-bit two-finger block is read at the
  same payload anchor the verified sticks/buttons/battery use (+32), and every gesture above it — drag,
  tap, two-finger scroll — was exercised through the synthetic `touch:`/`tap:`/`touch2:` tokens, which
  enter at the same `PointerTouchSample` a real finger does. But the bench's live shell owns the
  DualSense over raw HID, so the test host runs `--no-pad` and no report from the actual glass has been
  through `DecodeFinger`. The first real touch logs its raw bytes and decoded position once, which is
  what will confirm or refute the offset. The touchpad *button* and the two-finger scroll gesture are
  likewise code-verified, not bench-observed: the bench filled up with Steam Big Picture and a game
  before those two tokens ran, and injecting synthetic clicks into somebody's game session is not a
  test worth running.
* **The hold gesture has never been performed by a human thumb.** Every allow in the run above was a
  synthetic press/release pair from `--walk`. The 800 ms is a number chosen on paper: whether it feels
  deliberate rather than annoying, and whether the fill reads at three metres, needs a person with a pad.
* **The bench is still ARC-named and was deliberately not migrated.** Account `arcshell`, shell dir
  `C:\ArcOS\web\`, broker root `C:\ProgramData\ARC`, task `\ARC\arc-install-broker`, worker file
  `arc-install-worker.ps1`. The repo says MarwanOS everywhere. The host copes by probing MarwanOS first
  and falling through to ARC, and the docs' paths therefore do **not** literally match the box. Renaming
  it is a re-provision (new account, new Shell Launcher binding), not a find-and-replace.
* **S4U is refused on the bench.** `Register-ScheduledTask -LogonType S4U` returns `0x80070005` and
  `schtasks /ru arcshell /np` prompts for a password instead of storing none, so every "run it as the
  shell account" harness now uses `-LogonType Interactive -RunLevel Limited` instead (B17). Same
  standard-user token, same session, still no password stored — but the B13/B14 S4U pattern in the older
  rows cannot be reproduced today.
* **The broker is not installed on the laptop and the laptop's system state was not touched.**
  `admin.status` there answers `available:false` with a reason, and Settings → Add software says so
  rather than offering buttons that would fail. `provisioning/MACHINE-CHANGES.md` rows 3/4/5 remain
  **NOT YET APPLIED** — Claude runs unelevated there and nothing privileged can be applied.
* **Nothing has answered a permission prompt on a page that is not `127.0.0.1`.** The bridge is proven,
  but only against a local `HttpListener`; a real site asking for a camera on real hardware (the bench has
  no camera or microphone) is untested.
* **The live bench shell restarts silently and often.** Eight `[HOST] ShellHostWeb started` lines between
  17:32 and 18:31 on 2026-08-16 with **no** matching `[EXIT]` line, so it is being killed rather than
  exiting — cause unknown, and the Shell-Launcher-started shell runs with no `--log` of its own beyond
  the default. It was stable across all four test runs of this session, so the windowed test instances
  are not the trigger; it predates them.
* The bench is an **evaluation** licence with a 90-day clock.

## Things learned that changed the design

1. **With no explorer, there is no foreground window when a child exits** (`hwnd=0`). Reclaiming focus is
   *easier* without explorer, not harder — the opposite of the pre-spike assumption.
2. **Shell Launcher resolves the shell path at sign-in.** Repointing the config does not change the running
   shell; a new binary needs a sign-out or reboot. This dictates how updates (M7) must work.
3. **Only one operation in the whole system API needs elevation** — `updates.install`. Everything else runs
   as a standard user, partly because the token carries `SeShutdownPrivilege` and `SeTimeZonePrivilege`
   disabled-but-present.
4. **Session 0 cannot query displays.** Display code must be tested as `marwanshell` in session 1; no amount of
   elevation substitutes for a window station.
5. **`chrome.webview.hostObjects` detection is unfalsifiable** — it is a JS Proxy that manufactures any
   property, so a probe "succeeds" against a host that registered nothing, then fails at call time. Always
   supply the transport explicitly.
6. **Synthetic clicks need `ExecuteScriptAsync`, not `postMessage`.** Without user activation Chromium
   blocks fullscreen and autoplay and marks history entries skippable — which silently made browser Back
   permanently unavailable.
7. **A WebView2 controller is a child HWND, not a layer.** Nothing the shell draws can overlap web content;
   full-screen surfaces must hide it.
8. **The CSP blocks injected `<style>` elements.** Components must ship real same-origin stylesheets.
9. **Windows' recycle API offers neither progress nor cancellation** — so the UI says so instead of faking.
10. **Spatial navigation is only as good as a site's semantics.** Good on Wikipedia article bodies; poor on
    Hacker News (walks the upvote arrows, not the headlines) and anything iframed. **Pinned apps should be
    the primary path; the full browser is the escape hatch.**

## Recommended next steps

1. **M2, properly: launch a real game.** Palworld direct-exe, then `steam://rungameid`, watching the
   fullscreen-exclusive focus edge. This is the one milestone that can still invalidate the architecture.
2. Hands-on pad pass — feel, mapping, and the visual judgement nothing automated can supply.
3. Close the small gaps: exit code 3, USB stick, `hostinfo` race.
4. M7: watchdog and update-in-place, shaped by finding #2 above.
5. Library aggregation (M4 in the original plan) — Steam ACF parsing, Riot, loose exes — replacing the
   demo tiles with a real scan.
