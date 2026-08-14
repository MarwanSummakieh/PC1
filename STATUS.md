# ARC OS — state of the build

Last updated 2026-08-14, after the M1→M6 session.

## Where it runs

| | |
|---|---|
| **Test bench** | `DESKTOP-6BCSJ3P`, <bench-ip> — Windows 11 IoT Enterprise LTSC 2024 **Evaluation** (90-day), build 26100.9168, i7-6700K, 8 GB, Odyssey G85SD 3440×1440 @120 Hz, wired Ethernet, DualSense on USB. **No Wi-Fi, no Bluetooth hardware.** This is where everything is verified. |
| **Laptop** | `DESKTOP-AGB796T` — the dev machine. Has Wi-Fi and Bluetooth, so radio-dependent code is verified here. Its shell is untouched: `brain` still gets a normal Windows desktop. |

Live shell on the bench: `C:\ArcOS\web\ArcShellHostWeb-v5.exe`, bound to the `arcshell` SID via
Shell Launcher, enforcement on. Reached over SSH with an elevated session — sshd does not depend on the
interactive shell, so **a broken shell is still remotely recoverable**. That is the primary safety net.

## What actually happens when you power it on

Firmware → no Windows logo → no logon screen → `arcshell` signed in automatically → ARC OS boot sequence
→ home screen. No explorer, no taskbar, no Start menu, no desktop. Roughly 30 s cold to usable.

## What is built and observed working

| Area | State |
|---|---|
| Shell replacement | Real. `winlogon` starts our process as the shell; no explorer/Start/Search in the session. `sihost` *does* run — the session is not as barren as "no explorer" suggests. |
| Window handoff | Launch → hide → job-object tracking of the whole child tree → return with **verified** foreground. Survives the launcher-spawns-child-then-exits case that defeats polling. |
| Return-code policy | exit 0 → shell restarts (79–94 ms); exit 2 → device restarts (~26 s back to shell). exit 3 → shutdown **still unverified**. |
| DualSense | Raw HID, offsets verified against the device's own report descriptor. USB and Bluetooth layouts. Battery decoded. Reconnects cleanly. Chromium's Gamepad API also sees it. |
| UI | The ARC OS design running as the shell in WebView2, served from a virtual host. Boot sequence resolves into the home screen. |
| Navigation | Focus-scope stack (Circle always pops one level), 2D movement with per-row column memory, contextual hint bar with 16 inline PS5 glyphs, keyboard/pad glyph swapping. |
| Scaling | Root-font-size driven; verified at 1280×720, 1920×1080, 2560×1440, 3440×1440, 3840×2160 and portrait. Overscan safe area. |
| On-screen keyboard | 5 modes, hold-to-repeat, 6,862 headless grid assertions + 30 behavioural. |
| Settings | Nine panels on real state: network, display, sound, controllers, power, storage, system/update, date & time, about. |
| File explorer | Real file management with recycle/permanent delete, copy/move as cancellable jobs with progress, 10,000-entry folders at 46 ms first paint. |
| Browser | Tabs, address bar, TLS chip, history, per-site zoom, pinned apps, media keys, spatial + cursor navigation. Crash-isolated from the shell. |

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
* **`hostinfo` race** (pre-existing): the host's capability reply can beat the page's listener, leaving
  `Host.caps` all false and the About panel misreporting. Intermittent; deserves its own pass.
* The bench is an **evaluation** licence with a 90-day clock.

## Things learned that changed the design

1. **With no explorer, there is no foreground window when a child exits** (`hwnd=0`). Reclaiming focus is
   *easier* without explorer, not harder — the opposite of the pre-spike assumption.
2. **Shell Launcher resolves the shell path at sign-in.** Repointing the config does not change the running
   shell; a new binary needs a sign-out or reboot. This dictates how updates (M7) must work.
3. **Only one operation in the whole system API needs elevation** — `updates.install`. Everything else runs
   as a standard user, partly because the token carries `SeShutdownPrivilege` and `SeTimeZonePrivilege`
   disabled-but-present.
4. **Session 0 cannot query displays.** Display code must be tested as `arcshell` in session 1; no amount of
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
