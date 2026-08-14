# ARC OS — a console shell for PC1

A living-room operating-system shell in the spirit of the PS5 dashboard: one
dark, lit, atmospheric surface; a single row of large tiles; a hero that
answers "what is this?" before you press anything; and a boot sequence that
resolves into the home screen instead of cutting to it.

Nothing here imitates Sony's marks or artwork. The identity is its own — the
loading arc **is** the logo, which is why the boot screen and the shell feel
like the same object.

## Files

| File | What it is |
|---|---|
| `index.html` | The full shell — boot sequence resolving into the home screen. |
| `boot.html` | The boot splash on its own, for actually running at startup. Self-contained. |
| `ui/osk.js`, `ui/osk.css` | The on-screen keyboard (`ArcOSK`). Deployed flat beside `index.html`. |
| `ui/files.js`, `ui/files.css` | The file explorer (`ArcFiles`). Same deal. |
| `ui/browser.js`, `ui/browser.css` | The browser's **chrome** (`ArcBrowser`) — tabs, address bar, start page, history. Not the web pages. |
| `ui/arcnav.js` | The navigation layer injected into **every web page** the browser loads. Never loaded by `index.html`; the host reads it off disk and injects it. |
| `spike/ShellHostWeb/ShellHostWeb.cs` | The WebView2 host. Owns the pad, the launch cycle, the bridges into `SystemApi` and `FileApi`, and the browser's content WebViews. |
| `spike/ShellHostWeb/SystemApi.cs` | The native system-control layer everything in Settings reads and writes through. |
| `spike/ShellHostWeb/FileApi.cs` | The native file-operations layer behind the explorer. |
| `README.md` | This spec. |

No build step, no fonts to fetch, no CDN, no external images — the CSP on the
hosted page blocks all of it. `boot.html` is entirely standalone;
`index.html` additionally loads `osk.*`, `files.*` and `browser.*` from its
own origin (they must be siblings of it, as they are in `C:\ArcOS\web\`).
Browsed web pages are obviously exempt from all of that: they are not the
shell and they are not on the shell's origin. See below.

## The browser: two WebViews, not one

This is the one piece of architecture worth knowing before reading any of the
browser code, because everything else follows from it.

There are **two entirely separate WebView2 worlds** in the shell process.

| | Shell WebView | Content WebViews |
|---|---|---|
| What it draws | `index.html` — home, Settings, keyboard, explorer, **and the browser's chrome** | the actual web |
| How many | one | one per tab, capped at four |
| Environment | the shell's | a *second* one, own user-data folder, own browser process |
| Hardening | full kiosk lockdown, unchanged | ordinary browser: script, storage, cookies, media all on |
| Origin | `https://arcos.local/` only | anywhere |

**Why not an `<iframe>`.** `index.html` cannot script into a cross-origin
document, so an iframe could never have a focus ring drawn in it, a link list
built from it, or a link clicked in it. Spatial navigation *is* the product,
and it requires code running inside the page. Only a WebView the host owns
can get that, via `AddScriptToExecuteOnDocumentCreatedAsync` — which is what
injects `ui/arcnav.js` at the top of every document on every origin.

**Why a second environment.** An environment is a browser process tree.
Sharing the shell's would put the shell's renderer, its GPU process and its
network service in the same failure domain as whatever the human just browsed
to. A separate one costs about one extra browser process and buys the
property this machine exists to have: a page cannot take the television down.
Verified by killing a content renderer outright — see `_crashtest.ps1`.

**Why the page cannot be drawn over.** A `CoreWebView2Controller` is a real
child window, not a composited layer. Nothing `index.html` draws can appear on
top of it. So the content view is a *rectangle* the shell reserves in its own
layout (`.arcbw-stage`), and any shell surface needing the whole screen — the
keyboard, the start page, history, the menu — asks the host to hide the
content view first. `browser.js` calls this `stage(false)`; forget it and your
new panel will be invisible behind a web page.

**Why discrete pad actions go through `ExecuteScriptAsync`.** A click
synthesised inside a `chrome.webview` message handler carries no user
activation, and Chromium withholds three things without one:
`requestFullscreen()` rejects, autoplay is blocked, and — the expensive one —
the history entry the navigation creates is flagged skippable, so `CanGoBack`
stays false however many links you follow and Circle can never take you back a
page. Script delivered through `ExecuteScriptAsync` is treated as
user-initiated and all three work. The 30 Hz analog-stick stream stays on the
cheap message channel, where activation does not matter.

## Design tokens

### Colour

The neutrals are blue-biased rather than grey — on a dark screen a true grey
reads as dead pixels next to a blue accent.

| Token | Value | Role |
|---|---|---|
| `--void` | `#04060b` | Ground. The darkest thing on screen. |
| `--deep` | `#0c1422` | Panels, chips, control-centre buttons. |
| `--haze` | `#17253c` | Atmospheric mid-tone in the ambient field. |
| `--line` | `#24344f` | Hairline borders and key caps. |
| `--ice` | `#e6f0ff` | Primary text. |
| `--steel` | `#7f96b8` | Secondary text, status bar, icons. |
| `--dim` | `#4e6183` | Tertiary — inactive tabs, hint bar, build strings. |
| `--beam` | `#4c8dff` | The accent. Focus, progress, glow. |
| `--beam-hot` | `#a6d4ff` | The hot core of the accent — highlights and eyebrows. |
| `--beam-deep` | `#1b3f8a` | The accent's dark end, for gradient tails. |
| `--good` | `#56d6a0` | Semantic only — connected, playing, on. |
| `--warn` | `#ffb457` | Semantic only — attention. |

Semantic colour is deliberately separate from the accent. A green chip means
*live*; it never means *selected*.

### Type

No webfonts — the CSP on a hosted page blocks font CDNs, and a silent fallback
would wreck the identity. Character comes from weight and tracking instead.

| Role | Stack | Treatment |
|---|---|---|
| Display | `Segoe UI Variable Display` → `system-ui` | 200–300 weight. Hero titles, wordmark, clock. |
| UI | `Segoe UI Variable Text` → `system-ui` | 400. Body, chips, hints. |
| Data | same, `tabular-nums` | Clock, percentages — digits must not jitter. |

Tracking carries the console feel: `.42em` on the boot sub-line, `.30em` on the
wordmark, `.34em` on hero eyebrows, `.26em` on tab labels. Running text stays
near 52ch.

### Scale

The shell is resolution-independent: one root scale factor drives everything,
and every other length is `rem`.

```css
:root{ font-size: clamp(11px, min(1.4815vh, 2.3vw), 34px); }
```

Height-led, because a console is read from a fixed distance and it is the
panel's height that changes. `1.4815vh` is 16px at 1080p, so the design's
original pixel values are exactly what 1080p still gets; 1440p (including the
3440×1440 bench) gets 21.33px and 2160p gets 32px. The `vw` term only bites on
absurdly wide-and-short viewports, where it shrinks rather than overflows.

| Viewport | Root | Tile | Active tile | Hero title |
|---|---|---|---|---|
| 1920×1080 | 16.00px | 118px | 176px | 64px |
| 2560×1440 | 21.33px | 157px | 235px | 85px |
| 3440×1440 | 21.33px | 157px | 235px | 85px |
| 3840×2160 | 32.00px | 236px | 352px | 128px |

**TV overscan** is handled by a safe-area inset rather than a fixed edge:
`--edge: max(2.5rem, 2.6vw)` and `--safe-y: max(1.6rem, 2.2vh)`. Nothing
critical reaches the physical edge of the panel.

### Rail geometry

| Token | Value | In px at 1080p |
|---|---|---|
| `--tile-u` | `7.375` | `118px` |
| `--tile-active-u` | `11` | `176px` |
| `--gap-u` | `1` | `16px` |
| `--edge` | `max(2.5rem, 2.6vw)` | `50px` |

The rail tokens are **unitless multipliers of 1rem**, and the multiplication
happens at the point of use (`width: calc(var(--tile-u) * 1rem)`), not in the
token. Both details matter:

* JS can compute the scroll offset exactly at any scale —
  `idx × (tile-u + gap-u) × rem`, never measured from the DOM. Every tile ahead
  of the focused one sits at its collapsed width, so the arithmetic is exact;
  measuring `offsetLeft` mid-transition reads the previous layout and the rail
  drifts.
* A custom property declared as `calc(… * 1rem)` on `:root` is resolved once
  against the root font size and is **not** re-resolved when a viewport-driven
  root font size changes. Put the calc in the token and the rail silently keeps
  whatever scale it had at first layout.

## Motion

| Moment | Timing | Curve |
|---|---|---|
| Wordmark letters | 0.8s, staggered 0.11s from 0.42s | `(.16,.84,.24,1)` |
| Arc spin | 1.5s linear, infinite | linear |
| Arc settle | 0.9s to 486° — 1⅓ turns into rest | `(.16,.84,.24,1)` |
| Progress step | 0.5s per step, weighted by real cost | `(.16,.84,.24,1)` |
| Bloom wipe | 0.85s, peak opacity at 22% | `(.16,.84,.24,1)` |
| Shell entrance | 0.7–0.9s, scale 1.05→1 with 14px blur clearing | `(.16,.84,.24,1)` |
| Tile focus | 0.42s width/height | `(.16,.84,.24,1)` |
| Hero crossfade | 0.7s art, 0.28s text (text leads out, art follows) | `(.16,.84,.24,1)` |

One easing curve throughout, one exit curve (`(.6,0,.9,.4)`) for things
leaving. Progress steps are weighted, not evenly spaced — a real boot spends
its time on drivers and services, and a bar that admits that reads as honest.

`prefers-reduced-motion` collapses every animation to ~0 and shortens
transitions to 120ms; the boot sequence still runs, at quarter length.

## Interaction

The target device is a DualSense with no keyboard and no mouse. The pad is the
interface; the keyboard is a debugging convenience that happens to still work.

| DualSense | Keyboard | Action |
|---|---|---|
| D-pad / left stick | `←` `→` `↑` `↓` | Move focus within the current scope |
| Cross | `↵` | Activate what is focused |
| Circle | `Esc` | Leave the current scope |
| Square | `X` | Details / app options |
| Triangle | `O` | Options for the focused app |
| L1 / R1 | `Tab` | Switch between Play and Media |
| Options / touchpad | `C` | Control centre |
| PS | `G` | Guide (currently opens the control centre) |
| — | `B` | Replay the boot sequence |

### Focus scopes

Focus lives in a **stack of scopes**: home rail → control centre → a panel → a
confirmation. Circle always means "leave this scope", so back is never
ambiguous and never has to know where it came from.

Within a scope, navigation is 2D over focusables carrying explicit grid
metadata (`data-nav="row,col"`). Geometric position is only consulted for
elements that opt in with `data-nav="auto"`, so a list never depends on
guessing where its rows landed. Three rules do all the work:

* **Horizontal** stays on its row. Wrapping is opt-in per row — the rail wraps,
  a two-item row like Play/Media does not, or "left" visibly moves right.
* **Vertical** takes the nearest row, then the column *remembered* for that
  row. That memory is why leaving the rail for the Options button and coming
  back lands on the tile you left.
* **Two-pane scopes** (`crossColumns`) treat each column as its own vertical
  list: up/down stays inside a pane, left/right crosses between them and lands
  on the row remembered for that pane. Settings is built this way.

Every focusable gets the same visible treatment — the `--beam` accent on the
`(.16,.84,.24,1)` curve — and scopes further down the stack keep a dimmed ring,
because on a television you must always be able to see where backing out will
put you. Focus is never invisible and never ambiguous.

### Hint bar

The hint bar is the tutorial surface: it renders from the current scope, so it
is right by construction rather than by maintenance. It shows **DualSense
glyphs** when the pad was the last thing touched and **key caps** when the
keyboard was, switching automatically. Synthetic key events (the host shim
relaying pad input) count as pad, not keyboard.

The glyphs are inline SVG in the same 24×24 stroke language as every other icon
here — cross, circle, square, triangle, D-pad (plus left/right and up/down
variants), L1/R1/L2/R2, Options, Create, PS, stick, touchpad. They are generic
geometric shapes, not anyone's trade dress.

**Control centre** overlays rather than navigates — it blurs what's behind it
and slides up from the bottom, so whatever you were doing is visibly still
there. Each item opens a panel; Power puts Restart, Shut down and Sign out
behind an explicit confirmation whose *Cancel* is the button already focused,
because a single stray press must never take a console down mid-game.

## Settings

Settings is the one screen that changes the machine rather than describing
it. Nine categories on the left, a content pane on the right, `crossColumns`
between them; every pane reads the running system through one channel and
writes back through the same one.

### The system channel

```
page                          host                       SystemApi
Sys.call("audio.setVolume",  {"type":"sys",…}  ──►  SysWorker (STA thread)
        {level:.4})                                       │
   ◄── Promise<data> ──  {"type":"sysres",…}  ◄────  Handle(json)
```

`SystemApi.Handle()` is not called on the UI thread. It runs on **one
dedicated STA background thread with a serial queue**, which reproduces
exactly the serialisation and apartment the API's contract assumes while
leaving the message pump — and therefore the pad — running throughout. A
command that takes two seconds delays the next command, never a frame.

Anything genuinely slow is a **job**: `wifi.scan`, `bt.scan`, `bt.pair`,
`wifi.connect`, `updates.check` return `{jobId}` immediately and
`Sys.job(cmd, args, {onProgress})` polls `job.status` until it settles. The
panel shows a spinner with real elapsed time rather than an indeterminate
one, and abandoning the pane cancels the poll.

Every request carries a deadline and every rejection carries `.code` and
`.detail`. `Sys.say(err)` turns the pair into one English sentence — a bare
HRESULT is never the only thing on screen, and no failure is ever a silent
no-op.

### What each category does

| Category | Reads | Writes |
|---|---|---|
| Network | `net.status`, `wifi.status`, `wifi.list` | `wifi.connect` (password via the OSK in `password` mode), `wifi.disconnect`, `wifi.forget`; `wifi.scan` is a job with a live-updating list |
| Display | `display.list`, `display.modes` | `display.setMode` behind confirm-or-revert, `display.setHdr` |
| Sound | `audio.devices` | `audio.setDefault`, `audio.setVolume`, `audio.setMute` |
| Controllers | host HID snapshot (incl. battery), `bt.status`, `bt.devices` | `bt.setRadio`, `bt.scan`, `bt.pair`, `bt.unpair` |
| Power | `power.status` | the same confirmed rest/restart/shut down/sign out the Power panel owns |
| Storage | `sys.storage` | — |
| System | `sys.info`, `updates.check`, `updates.list` | — **deliberately no install button** |
| Date & time | `sys.time`, `sys.timezones`, `sys.locale`, `sys.privileges` | `sys.setTimezone` |
| About | `sys.info`, `accounts.list`, `api.commands` | the shell-local device name, via the OSK |

Three of those absences are the design, not a gap:

* **No update install.** `updates.install` needs administrator rights the
  shell does not have and should not have. The panel reports "N updates
  pending" honestly and says Windows installs them on its own schedule.
  A progress bar that could only ever fail would be a lie with a spinner on it.
* **No clock setting.** The account holds `SeTimeZonePrivilege` but not
  `SeSystemtimePrivilege`, so the zone is settable and the clock is not. The
  panel says which privilege is missing rather than offering a control that
  fails.
* **No account editing.** `accounts.list` is read-only by design.

Hardware that is not there is a first-class state. On a wired machine with no
radio, Network says *"No Wi-Fi adapter found on this machine"* and hides the
scan and connect controls entirely; Controllers does the same for Bluetooth.
Nothing is populated with plausible-looking data to make a panel look busy.

### Confirm-or-revert

On a television a display mode the panel cannot show is a black screen, and
you cannot see the pad to press your way out of it. So a mode change is four
steps and the default answer is always *put it back*:

1. **Dry-run** the mode (`test:true`, which SystemApi runs as `CDS_TEST`).
   A mode the driver refuses is reported here, with nothing changed.
2. **Draw the confirmation first**, focused on *Revert now*, and only then
   apply — a display that cannot show the new mode must already have the
   confirmation on it.
3. **Count down out loud** for 15 seconds.
4. **Revert** on zero, on Circle, or on any error. *Keep* is the only path
   that makes the change permanent.

Nothing in that depends on the human being able to see the screen: doing
nothing gets the old mode back. A failed revert is the one outcome that
cannot be silent, and it says exactly what to do instead.

### Sliders on a pad

A volume slider is a focusable row that claims the horizontal axis through
`element.__arcAdjust(dx, dy)` — the focus manager stays generic and knows
nothing about sliders. Left and right adjust in 5% steps, debounced before
the write; Cross toggles mute. At either end of the track the press is *not*
consumed, so in the two-pane layout pushing left at 0% crosses back to the
category list rather than doing nothing.

### Verifying it

The host can drive the whole screen with no human present:

```bash
ArcShellHostWeb-v4.exe --sys-selftest      # read-only sweep + walk all nine categories
ArcShellHostWeb-v4.exe --display-selftest  # apply a mode, then press NOTHING and prove the revert
ArcShellHostWeb-v4.exe --walk=cc,right,select,... --walk-gap=2200   # any flow at all
```

`--sys-selftest` fires every read-only command straight at the worker (each
one logs `ok`/`ERR` and its duration), then walks the whole category list and
works the volume slider with two rights and two lefts that net to zero.

`--display-selftest` is the interesting one: it walks to a mode, selects it,
and then deliberately stops, so the countdown has to revert on its own. Never
run it on a machine you cannot see.

`--walk` is the general form — a comma-separated list of pad actions over the
real host→page channel. Anything reachable with the pad is verifiable with
nobody in the room, which is the only kind of verification a television shell
can have.

`--browse=<url>` opens the browser on a real site before the walk starts, so
a walk can begin from inside a live web page. It exists because the pad
vocabulary cannot type an address and driving the on-screen keyboard one key
at a time to reach one would be forty walk steps of nothing useful. It goes
through the same `ACT.openBrowser()` a human's Cross press does.

`_crashtest.ps1` is the isolation proof. Run it beside a `--browse` run: it
finds a real renderer process belonging to the *content* environment, kills it
with no warning, and then checks that the shell has the same pid and is still
pumping messages.

Browser-flow walks worth keeping:

```bash
# spatial navigation on a dense page, with every focus stop in the log
--browse=https://news.ycombinator.com --walk=down,down,down,right,down,l3,l3

# the Circle chain: page history, then the start page, then out of the browser
--browse=https://example.com --walk=cross,back,back,back

# video: play/pause, seek, volume, full screen
--browse=<a direct video url> --walk=cross,right,up,down,cross,square
```

## Running the boot screen at startup

`boot.html` takes three query parameters:

| Parameter | Default | Effect |
|---|---|---|
| `duration` | `6400` | Total sequence length in ms. |
| `next` | — | Navigate here when the sequence ends. |
| `hold` | — | `hold=1` loops the sequence, for waits of unknown length. |

To show the splash and hand off to the shell:

```bash
msedge --kiosk --edge-kiosk-type=fullscreen "file:///C:/Users/brain/Documents/repos/PC1/boot.html?duration=7000&next=index.html"
```

Put that in a shortcut under `shell:startup`, or in a Task Scheduler task
triggered "At log on", and the machine comes up into the shell.

**One honest limitation:** anything a browser draws appears *after* Windows has
booted and logged in — it cannot cover firmware POST or the Windows boot
animation. If you want the arc mark during the actual boot phase, that's the
BGRT logo slot in firmware, which takes a static BMP and is set by the OEM or
by a signed firmware update; it is not something this page can reach. What this
splash covers well is the gap between logon and the shell being ready, which on
a machine like this is most of the visible wait anyway.

## Extending it

Apps live in the `APPS` object in `index.html` — one entry per tile:

```js
{ id:"steam", name:"Steam", icon:"steam", art:"play",
  eyebrow:"Big Picture", title:"Steam",
  desc:"…", meta:["342 titles","1.2 TB installed"],
  live:2,            // index into meta[] to render as a green "live" chip
  badge:"2",         // corner count, e.g. pending downloads
  action:"Launch" }  // primary button label
```

`art` keys into the `G` gradient table (two stops per key, used for both the
tile face and the hero wash), and `icon` keys into `ICON`, a table of inline
24×24 stroke paths. Adding an app means adding a gradient pair, an icon path,
and the entry — no other code changes.
