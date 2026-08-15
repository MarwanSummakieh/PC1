# PC1 — a console shell for this machine

A living-room operating system for one machine, called PC1. The layout is the
PS5 dashboard's: a status bar, two view tabs, a hero that answers "what is
this?" before you press anything, and a single row of large box art anchored to
the bottom of the screen. The *material* is the Mythos Playnite theme's: flat
near-black, one saturated blue, hairline rules, dense label/value tables and a
white selection border.

Neither is imitated as trade dress. There is no Sony mark or artwork here, and
nothing is copied out of Mythos — it is the same design language rebuilt at
ten-foot scale for a controller.

The identity is a single typographic lockup: **PC1** sitting on a rule that
fills with the machine's progress while it boots and then stays put as the
wordmark's underline. The loading bar *is* the logo, which is why the boot
screen and the shell read as the same object.

## Files

| File | What it is |
|---|---|
| `index.html` | The full shell — boot sequence resolving into the home screen. |
| `boot.html` | The boot splash on its own, for actually running at startup. |
| `ui/*.woff2`, `ui/OFL-Inter.txt` | Inter and Inter Tight, vendored. Deployed flat beside `index.html`. |
| `ui/osk.js`, `ui/osk.css` | The on-screen keyboard (`ArcOSK`). Deployed flat beside `index.html`. |
| `ui/files.js`, `ui/files.css` | The file explorer (`ArcFiles`). Same deal. |
| `ui/browser.js`, `ui/browser.css` | The browser's **chrome** (`ArcBrowser`) — tabs, address bar, start page, history, downloads. Not the web pages. |
| `ui/arcnav.js` | The navigation layer injected into **every web page** the browser loads. Never loaded by `index.html`; the host reads it off disk and injects it. |
| `spike/ShellHostWeb/ShellHostWeb.cs` | The WebView2 host. Owns the pad, the launch cycle, the bridges into `SystemApi` and `FileApi`, and the browser's content WebViews. |
| `spike/ShellHostWeb/SystemApi.cs` | The native system-control layer everything in Settings reads and writes through. |
| `spike/ShellHostWeb/FileApi.cs` | The native file-operations layer behind the explorer. |
| `README.md` | This spec. |

No build step, no CDN, no external images, no network of any kind — the fonts
ship in the same folder and are loaded same-origin. `index.html` loads
`osk.*`, `files.*`, `browser.*` and the woff2 files **by bare filename**,
because the host deploys everything flat into `C:\ArcOS\web\`; opening the
repo's `index.html` directly will not find them. `boot.html` will use Inter
Tight if it is a sibling and falls back to Segoe UI Variable Display if not,
so it still stands alone. Browsed web pages are exempt from all of this: they
are not the shell and not on the shell's origin. See below.

**The code still says `arc`.** `arcos.local`, `ArcOSK`, `ArcFiles`,
`ArcBrowser`, `.arcbw-*`, `__arcAdjust`, `arcnav.js` and the `arc.deviceName`
key are identifiers, not branding, and they are wired through the C# host and
the provisioning scripts. Renaming them is a separate job with real blast
radius; only user-visible text was rebranded.

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

**Why downloads are taken off Chromium.** WebView2 has its own download
experience and it cannot be used here: it is painted by the renderer *inside*
the content window, so the shell cannot restyle it, move it, or put a focus
ring on it; its buttons are mouse targets that `arcnav.js` cannot reach,
because they are not in the document; and it fades away after a few seconds
with no toolbar button to bring it back. So `DownloadStarting` sets
`e.Handled = true` — the documented way to suppress the flyout — and the host
reports the whole life of the download (name, destination, bytes, state, and
the reason it stopped) to the shell page as `{ev:"downloads", list:[…]}`. The
page draws a chip in the chrome row while anything is in flight and a
`Downloads` sheet with ordinary focusable rows: Cross pauses, resumes, shows a
finished file in the explorer or asks a failed one again; Square cancels or
forgets the row. Commands go back as `{cmd:"download", do:…, id:…}`.

The path Chromium chose is kept rather than overridden: it is already the
user's Downloads folder and already made unique against what is on disk, and
the SDK is explicit that supplying a path pointing at an existing file
*overwrites* it. Nothing is blocked and nothing is scanned — a download is
saved, listed, and left for the human to deal with in the file explorer; the
shell never launches it.

**How an extension gets onto a machine with no mouse.** Extensions are a
runtime call on the content profile, not a config file:
`AddBrowserExtensionAsync(folder)` loads one into every open document
immediately and the profile remembers it, and `EnableAsync` / `RemoveAsync`
are the same shape. So the browser's Extensions sheet is a real manager. The
host offers the two sources a television actually has — a `.zip` or `.crx`
this browser has **downloaded**, and anything on a plugged-in **USB stick** —
unpacks the chosen one into `C:\ArcOS\extensions\<name>` (a `.crx` is a zip
with a signature header; the header is parsed and skipped) and adds it. Cross
turns one on or off, Square removes it after a confirmation, and a folder that
is on disk but did not load is listed with its reason rather than being
silently absent. There is no Chrome Web Store install: the store serves `.crx`
to Chrome-branded browsers behind a signed request this host cannot honestly
fake, and a button that fails for reasons nobody in a living room can act on
is worse than no button.

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

The look is a flat, near-black interface with one saturated blue and a white
selection ring — the visual language of the Mythos Playnite theme, expressed in
a ten-foot console layout. Three rules hold it together:

* **The ground is neutral.** Not navy. The cover art is the only real chroma on
  screen and it needs a ground that competes with nothing.
* **Nothing is lit from within.** Depth comes from surface steps and hairlines.
  There is no glow, no bloom and no gradient fill anywhere in the shell.
* **Blue is the action, white is the cursor.** The accent fills the primary
  button and nothing else. Focus is always a hard white ring.

### Colour

| Token | Value | Role |
|---|---|---|
| `--void` | `#0b0b0d` | Ground. Lifted off pure black so hairlines survive. |
| `--deep` | `#141417` | Cards, panels, chips, the fact table. |
| `--haze` | `#1c1c21` | Raised surface — rows, hovered controls. |
| `--line` | `#2b2b32` | Hairline borders. |
| `--line-soft` | `#202027` | The rule *between* rows inside one card. |
| `--ice` | `#f4f5f7` | Primary text. |
| `--steel` | `#9c9da5` | Secondary — descriptions, status bar, icons. |
| `--dim` | `#6a6b73` | Tertiary — labels, inactive tabs, build strings. |
| `--beam` | `#1a86ff` | The action. Primary button, progress, active state. |
| `--beam-hot` | `#57a6ff` | Lighter blue, for small marks on dark. |
| `--beam-deep` | `#0b4fa8` | The dark end, for spinner tracks. |
| `--sel` | `#ffffff` | **Selection.** Every focus ring in the shell. |
| `--good` | `#3fd18b` | Semantic only — connected, playing, on. |
| `--warn` | `#ffb457` | Semantic only — attention, revert, danger. |
| `--bad` | `#ff5c68` | Semantic only — destructive. |

Semantic colour is deliberately separate from the accent. A green value means
*live*; it never means *selected*. The one exception is the power confirmation,
where the ring itself carries meaning — amber round the destructive button,
white round the safe one — because there the difference is the whole point.

**Why the ring is white.** It used to be a blue halo, which had two problems on
a television: it washed out against bright artwork, and its blur made the exact
boundary of the focused thing ambiguous from across a room. White at full
opacity survives every cover in the catalogue. On covers it is drawn *inset*, so
selection never changes the box's size and the rail's arithmetic stays exact.

### Type

Inter and Inter Tight, vendored as woff2 beside the shell — same origin, no
network, no CDN. Variable across 100–900, so one file per subset covers every
weight. Licence in `ui/OFL-Inter.txt`.

| Role | Family | Treatment |
|---|---|---|
| Display | Inter Tight | 600–700, `-.03em`. Wordmark, hero and panel titles, tabs. |
| UI | Inter | 400–500. Body, chips, rows, hints. |
| Data | Inter, `tabular-nums` | Clock, percentages, every value in a fact table. |

`font-display:block`, not `swap`: a console shell that flashes a fallback face
on the way up looks broken, and the file is on local disk so the block is
milliseconds. Each family declares a latin and a latin-ext subset; latin-ext
stays unloaded until something on screen needs it.

**Weight replaced tracking.** The old shell built its voice from hairline
weights and very wide letter-spacing — `.42em` on the boot sub-line, `.34em` on
eyebrows, `.26em` on tab labels. That vocabulary is gone. Titles are now heavy
and tight, labels are sentence case in the tertiary grey, and the only
uppercase left in the shell is on keyboard modifier caps, which really are
uppercase. Running text stays near 56ch.

### Scale

The shell is resolution-independent: one root scale factor drives everything,
and every other length is `rem`.

```css
:root{ font-size: clamp(12px, min(1.62vh, 2.05vw), 40px); }
```

Height-led, because a console is read from a fixed distance and it is the
panel's height that changes. The `vw` term only bites on absurdly
wide-and-short viewports, where it shrinks rather than overflows.

| Viewport | Root | Cover | Focused cover | Hero title |
|---|---|---|---|---|
| 1920×1080 | 17.50px | 149 × 198px | 219 × 292px | 56px |
| 2560×1440 | 23.33px | 198 × 264px | 292 × 389px | 76px |
| 3440×1440 | 23.33px | 198 × 264px | 292 × 389px | 76px |
| 3840×2160 | 35.00px | 298 × 397px | 438 × 583px | 114px |

**TV overscan** is handled by a safe-area inset rather than a fixed edge:
`--edge: max(2.5rem, 2.6vw)` and `--safe-y: max(1.6rem, 2.2vh)`. Nothing
critical reaches the physical edge of the panel.

### Rail geometry

Covers are 3:4 portrait — box art, not icons.

| Token | Value | In px at 1080p |
|---|---|---|
| `--cover-u` | `8.5` | `149px` wide |
| `--cover-active-u` | `12.5` | `219px` wide |
| `--gap-u` | `.875` | `15px` |
| `--edge` | `max(2.5rem, 2.6vw)` | `50px` |

Only the **widths** are tokens. Every height is `width × 4/3` computed at the
point of use, so the aspect ratio cannot drift out of sync with itself.

The rail tokens are **unitless multipliers of 1rem**, and the multiplication
happens at the point of use (`width: calc(var(--cover-u) * 1rem)`), not in the
token. Both details matter:

* JS can compute the scroll offset exactly at any scale —
  `idx × (cover-u + gap-u) × rem`, never measured from the DOM. Every cover
  ahead of the focused one sits at its collapsed width, so the arithmetic is
  exact; measuring `offsetLeft` mid-transition reads the previous layout and the
  rail drifts.
* A custom property declared as `calc(… * 1rem)` on `:root` is resolved once
  against the root font size and is **not** re-resolved when a viewport-driven
  root font size changes. Put the calc in the token and the rail silently keeps
  whatever scale it had at first layout.

## Motion

| Moment | Timing | Curve |
|---|---|---|
| Wordmark letters | 0.55s, staggered 0.09s from 0.18s | `(.16,.84,.24,1)` |
| Progress step | 0.5s per step, weighted by real cost | `(.16,.84,.24,1)` |
| Boot hold at 100% | 0.42s before anything leaves | — |
| Boot exit | 0.45s opacity + scale 1→1.015 | `(.6,0,.9,.4)` |
| Shell entrance | 0.5–0.6s, scale 1.012→1 | `(.16,.84,.24,1)` |
| Cover focus | 0.42s width/height | `(.16,.84,.24,1)` |
| Hero crossfade | 0.7s art, 0.28s text (text leads out, art follows) | `(.16,.84,.24,1)` |

One easing curve throughout, one exit curve (`(.6,0,.9,.4)`) for things
leaving. Progress steps are weighted, not evenly spaced — a real boot spends
its time on drivers and services, and a bar that admits that reads as honest.

Three things the shell deliberately does **not** do any more:

* **No bloom wipe out of boot.** The splash fades; it does not flash.
* **No blur on the shell entrance.** A full-screen `filter: blur()` is a real
  per-frame compositing cost, and it read as the old lit design's signature.
* **No ambient canvas.** There used to be a particle field running a
  `requestAnimationFrame` loop forever, on a machine whose whole job is to give
  its frames to something else. The background is now the focused cover thrown
  out of focus (`artCSS()`), so it moves with the cursor rather than on a timer
  and costs nothing when nobody is touching the pad.

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
animation. If you want the PC1 wordmark during the actual boot phase, that's the
BGRT logo slot in firmware, which takes a static BMP and is set by the OEM or
by a signed firmware update; it is not something this page can reach. What this
splash covers well is the gap between logon and the shell being ready, which on
a machine like this is most of the visible wait anyway.

## Extending it

Apps live in the `APPS` object in `index.html` — one entry per cover:

```js
{ id:"steam", name:"Steam", icon:"steam", art:"play",
  eyebrow:"Big Picture", title:"Steam",
  desc:"…",
  tags:["Controller", "Big Picture", "Cloud saves"],   // chips, left column
  facts:[["Titles","342"], ["Installed","1.2 TB"]],    // table, right column
  live:"Friends online",  // the fact whose value is a running state
  badge:"2",              // corner count, e.g. pending downloads
  action:"Launch" }       // primary button label
```

**`tags` and `facts` are two different vocabularies and the split is the
point.** A tag is what the app *can do* — short, static, no numbers. A fact is
what is *true about it right now* — a label and a value. Tags become chips
under the description; facts become the table on the right. Putting a number in
a tag, or a capability in a fact, is how that column stops being scannable.

`live` names a fact **by label, not by index**, so inserting a fact above it
cannot silently move the green highlight onto the wrong row.

`art` keys into the `G` gradient table (two stops per key, used for the cover
face and the backdrop wash), and `icon` keys into `ICON`, a table of inline
24×24 stroke paths. Adding an app means adding a gradient pair, an icon path,
and the entry — no other code changes.

### Where the cascade will bite you

The panels own `.facts` / `.fact` for their read-only lists; the home screen's
version is `.hero-facts` / `.hero-fact`. They are deliberately identical to
look at and deliberately different in name. Sharing the names once gave panel
rows the hero card's border **and** their own zebra striping at the same time —
the rules did not conflict loudly, they both just applied.
