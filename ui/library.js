/* ═══════════════════════════════════════════════════════════════════════
   MarwanOS — library.js
   The home rail's contents, from a real scan of what is installed.

   Everything on the rail used to be a fixture: a Steam tile claiming 342
   titles and 1.2 TB, an Arcade with 1,204 ROMs, a Remote Play host called
   TOWER. None of it existed. This module replaces that object with what
   the host actually found, and — the part that matters — says so plainly
   when the answer is nothing.

   Two things on the rail are ours and are therefore always real: Files
   (ui/files.js) and Browser (ui/browser.js). Everything else has to earn
   its place by being installed.

   ── Public surface ──────────────────────────────────────────────────
     MarwanLibrary.initial(hooks)   synchronous, no host call. The rail data
                                 to paint with before anything has loaded:
                                 Files, Browser, and a Play tab that says
                                 it is still looking.
     MarwanLibrary.load()           Promise<APPS>. Paints from the host's disk
                                 cache immediately if there is one, then
                                 refreshes in the background and fires
                                 onUpdate when the fresh scan lands.
     MarwanLibrary.refresh()        Promise<APPS>. Forces a scan now.
     MarwanLibrary.onUpdate         function(APPS) — assign it. Called every
                                 time a newer set of tiles is available.
     MarwanLibrary.state()          {scanning, error, scannedUtc, ageSeconds,
                                  counts, sources} — for a diagnostics panel.
     MarwanLibrary.launch(app)      launch a tile. Tiles carry this as `run`,
                                 so index.html needs to know nothing.
     MarwanLibrary.cover(app)       Promise<dataUri|null>. The large portrait
                                 art, fetched lazily.
     MarwanLibrary.art(app)         Promise<art> from ui/homeart.js — the
                                 cover, the backdrop wash and the colours
                                 the whole home screen takes its mood
                                 from. Every tile has one, including the
                                 ones with no artwork at all.

   `hooks` is {files: fn, browser: fn, toast: fn} — the two in-shell
   openers and the shell's own way of saying one sentence, passed in
   rather than reached for, so this file never has to know that ACT
   exists. `toast` is optional; without it the tiles that would speak
   stay silent rather than reaching for a global.

   ── The shape returned ──────────────────────────────────────────────
   Exactly what index.html's rail already consumes:
     { id, name, icon, art, eyebrow, title, desc, tags, facts, action, run }
   `icon` is a key into index.html's ICON map, `art` a key into G. Both are
   chosen from the existing sets — this module adds no new artwork.

   Beyond the rail this module also exposes `hero(id)` — Steam's real
   1920x620 landscape art for a backdrop, local and keyless, instead of a
   600x900 portrait stretched across a 4K screen — and it keeps the rail
   LIVE: the host watches for installs and this polls a cursor, so a game
   installed while the console is running appears without a restart.

   A third key, `apps`, carries the installed non-game software the scan
   found (Git, VS Code, Stremio…). The shell's third rail tab draws it.
   Like the other two it is never empty: index.html indexes [0] and takes
   a modulo of the length on every tab, so "nothing here" is a tile that
   says which sources were read, not a zero-length array.

   ── Transport ───────────────────────────────────────────────────────
   Same three-way detection as ui/files.js, one message type along:
     1. window.mosLibraryApi(json)
     2. chrome.webview.hostObjects.mosLibrary.Handle
     3. chrome.webview postMessage {type:"lib"} -> reply {type:"lib.reply"}
   With no bridge at all this module still works: it returns the honest
   empty state and reports why, which is what you get opening index.html
   in a plain browser.
   ══════════════════════════════════════════════════════════════════════ */

(function (root) {
  "use strict";

  /* ═══ Transport ════════════════════════════════════════════════════ */

  var tx = { fn: null, name: "none", why: "no bridge detected", seq: 0, pending: {} };

  function detectTransport() {
    if (tx.fn) return tx.fn;

    if (typeof root.mosLibraryApi === "function") {
      tx.name = "mosLibraryApi";
      tx.why = "window.mosLibraryApi(json) is present";
      tx.fn = function (cmd) {
        return new Promise(function (resolve, reject) {
          var r;
          try { r = root.mosLibraryApi(JSON.stringify(cmd)); }
          catch (e) { reject(e); return; }
          if (r && typeof r.then === "function") r.then(function (v) { resolve(parseEnv(v)); }, reject);
          else resolve(parseEnv(r));
        });
      };
      return tx.fn;
    }

    /* postMessage is tried BEFORE hostObjects, and the order is the fix for a
       real failure rather than a preference.

       chrome.webview.hostObjects is a JS Proxy: reading .mosLibrary off it
       returns a truthy stand-in whether or not the host ever registered that
       name. The old order therefore selected a transport that cannot work on
       every host build — no host in this tree registers mosLibrary — and the
       first call came back empty, which parseEnv reports as "the library
       bridge returned nothing". The rail then showed the honest-empty state
       on a host that had a perfectly good library, and nothing on screen said
       why. Bench-observed 2026-08-16 on a page that does not inject its own
       transport: "transport: hostObject", zero requests reaching the host's
       LibraryApi worker.

       index.html was never affected because it injects window.mosLibraryApi
       above, which wins before either of these is reached. That is exactly
       what hid the bug.

       postMessage is the transport the host actually implements for lib.*,
       so it goes first; the host-object branch stays underneath it for a host
       that really does register one, where its truthiness is not a lie. */
    try {
      if (root.chrome && root.chrome.webview && typeof root.chrome.webview.postMessage === "function") {
        tx.name = "postMessage";
        tx.why = "chrome.webview.postMessage is present; expects {type:\"lib.reply\",reqId,...}";
        root.chrome.webview.addEventListener("message", function (ev) {
          var d = ev ? ev.data : null;
          if (typeof d === "string") { try { d = JSON.parse(d); } catch (e) { return; } }
          if (!d || d.type !== "lib.reply" || !d.reqId) return;
          var p = tx.pending[d.reqId];
          if (!p) return;
          delete tx.pending[d.reqId];
          clearTimeout(p.timer);
          p.resolve(d);
        });
        tx.fn = function (cmd) {
          return new Promise(function (resolve, reject) {
            tx.seq++;
            var id = "lx-" + tx.seq;
            cmd.reqId = id;
            var timer = setTimeout(function () {
              delete tx.pending[id];
              reject(new Error("the host did not answer '" + cmd.cmd + "' within 60 seconds"));
            }, 60000);
            tx.pending[id] = { resolve: resolve, timer: timer };
            try { root.chrome.webview.postMessage(JSON.stringify({ type: "lib", reqId: id, command: cmd })); }
            catch (e) { clearTimeout(timer); delete tx.pending[id]; reject(e); }
          });
        };
        return tx.fn;
      }
    } catch (e) {}

    try {
      if (root.chrome && root.chrome.webview && root.chrome.webview.hostObjects &&
          root.chrome.webview.hostObjects.mosLibrary) {
        tx.name = "hostObject";
        tx.why = "chrome.webview.hostObjects.mosLibrary is present";
        tx.fn = function (cmd) {
          return Promise.resolve(root.chrome.webview.hostObjects.mosLibrary.Handle(JSON.stringify(cmd)))
            .then(function (v) { return parseEnv(v); });
        };
        return tx.fn;
      }
    } catch (e) {}

    return null;
  }

  /* ═══ The "is it already running?" channel ═════════════════════════
     Separate from the lib.* transport above on purpose: this asks the
     HOST about process state, not LibraryApi about what is installed.
     LibraryApi cannot answer it — it starts processes and forgets them.

     One round trip, before every launch. If the host says the app is
     already up it has ALSO already brought it to the front, and this
     module must not launch anything. That is the whole fix for the four
     Steam clients: nothing here decides, the host does, because the host
     is the only thing that knows what it adopted and what is still alive.

     With no host bridge (index.html opened in a plain browser) the answer
     is "not running" and the old behaviour stands. */
  var runq = { seq: 0, pending: {}, wired: false };

  function hostPost(o) {
    try {
      if (root.chrome && root.chrome.webview && root.chrome.webview.postMessage) {
        root.chrome.webview.postMessage(JSON.stringify(o));
        return true;
      }
    } catch (e) {}
    return false;
  }

  function wireRunq() {
    if (runq.wired) return true;
    try {
      if (!(root.chrome && root.chrome.webview && root.chrome.webview.addEventListener)) return false;
      root.chrome.webview.addEventListener("message", function (ev) {
        var d = ev ? ev.data : null;
        if (typeof d === "string") { try { d = JSON.parse(d); } catch (e) { return; } }
        if (!d || d.type !== "lib.run.reply" || !d.reqId) return;
        var p = runq.pending[d.reqId];
        if (!p) return;
        delete runq.pending[d.reqId];
        clearTimeout(p.timer);
        p.resolve(d);
      });
      runq.wired = true;
      return true;
    } catch (e) { return false; }
  }

  function askHostRunning(e) {
    if (!wireRunq()) return Promise.resolve({ running: false, detail: "no host to ask" });
    return new Promise(function (resolve) {
      runq.seq++;
      var id = "run-" + runq.seq;
      /* A short timeout, and it resolves rather than rejects: a host that
         does not answer must not stop the human launching something. The
         old behaviour — launch and hope — is the safe fallback. */
      var timer = setTimeout(function () {
        delete runq.pending[id];
        resolve({ running: false, detail: "the host did not answer in 1.5 s" });
      }, 1500);
      runq.pending[id] = { resolve: resolve, timer: timer };
      var sent = hostPost({
        type: "lib.run.activate", reqId: id, id: e.id, title: e.title,
        launchKind: e.launchKind, launchTarget: e.launchTarget
      });
      if (!sent) {
        clearTimeout(timer);
        delete runq.pending[id];
        resolve({ running: false, detail: "no host to ask" });
      }
    });
  }

  function parseEnv(v) {
    if (v === null || v === undefined) throw new Error("the library bridge returned nothing");
    if (typeof v === "string") {
      try { return JSON.parse(v); }
      catch (e) { throw new Error("the library bridge returned text that is not JSON: " + String(v).slice(0, 160)); }
    }
    return v;
  }

  function call(cmd) {
    var fn = tx.fn || detectTransport();
    if (!fn) return Promise.reject(new Error("no_bridge: the shell host has not exposed LibraryApi to the page."));
    return fn(cmd).then(function (env) {
      if (!env || typeof env !== "object") throw new Error("the library bridge sent something that is not a reply");
      if (env.ok) return env.data || {};
      var err = new Error(env.detail || env.error || "the library command failed");
      err.code = env.error || "error";
      throw err;
    });
  }

  /* A job, polled to completion. lib.scan is the only slow command. */
  function callJob(cmd, timeoutMs) {
    var deadline = Date.now() + (timeoutMs || 120000);
    return call(cmd).then(function (data) {
      if (!data || !data.jobId) return data;          /* host ran it synchronously */
      return poll(data.jobId);
    });

    function poll(jobId) {
      return call({ cmd: "job.status", jobId: jobId }).then(function (j) {
        if (j.state === "done") return j.result || {};
        if (j.state === "error") throw new Error(j.detail || "the scan failed");
        if (j.state === "cancelled") throw new Error("the scan was cancelled");
        if (Date.now() > deadline) throw new Error("the scan did not finish in time");
        state.phase = j.phase || "";
        state.percent = typeof j.percent === "number" ? j.percent : -1;
        tickScan();
        return new Promise(function (r) { setTimeout(r, 350); }).then(function () { return poll(jobId); });
      });
    }
  }

  /* ═══ State ════════════════════════════════════════════════════════ */

  var state = {
    scanning: false,
    error: null,
    scannedUtc: null,
    ageSeconds: null,
    counts: null,
    sources: null,
    phase: "",
    percent: -1,
    transport: function () { return tx.name + " — " + tx.why; }
  };

  /* Whether the rail on screen IS the scanning card, and what its Status
     line last read. Both exist for one reason: the Status fact is marked
     `live`, which index.html paints in the semantic green — a promise that
     the value moves. It never did, because the tile was built once and the
     job's phase was only ever written into `state`. */
  var showingScan = false;
  var lastScanLine = "";

  function isScanRail(apps) {
    var first = apps && apps.play && apps.play.length === 1 ? apps.play[0] : null;
    return !!(first && first.id && first.id.indexOf("lib:scanning") === 0);
  }

  /* Rebuild the scanning card, and only when the line it shows would read
     differently — a scan that sits on one phase for ten seconds must not
     rebuild the rail thirty times.

     Guarded on the rail actually being that card: refresh() also runs
     behind a rail already painted from the disk cache, and replacing a
     screen full of real games with a progress card would be a regression
     dressed up as a feature. */
  function tickScan() {
    if (!showingScan) return;
    var line = scanLine();
    if (line === lastScanLine) return;
    lastScanLine = line;
    announce(placeholder(scanningTile));
  }

  var entriesById = {};      /* id -> raw entry from the host */
  var tilesById = {};        /* id -> the tile object last handed to the shell */
  var hooks = { files: null, browser: null, toast: null };
  var covers = {};           /* id -> dataUri | null (null = asked, none there) */
  var heroes = {};           /* id -> promise of the landscape hero dataUri | null */

  /* ═══ Formatting ═══════════════════════════════════════════════════ */

  function human(bytes) {
    if (typeof bytes !== "number" || bytes < 0) return null;
    var u = ["B", "KB", "MB", "GB", "TB"], v = bytes, i = 0;
    while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
    return (v >= 100 || i === 0 ? Math.round(v) : v.toFixed(1)) + " " + u[i];
  }

  function ago(iso) {
    if (!iso) return null;
    var t = Date.parse(iso);
    if (isNaN(t)) return null;
    var s = Math.max(0, (Date.now() - t) / 1000);
    if (s < 90) return "Just now";
    if (s < 3600) return Math.round(s / 60) + " min ago";
    if (s < 86400) { var h = Math.round(s / 3600); return h + (h === 1 ? " hour ago" : " hours ago"); }
    var d = Math.round(s / 86400);
    if (d < 14) return d + (d === 1 ? " day ago" : " days ago");
    if (d < 60) return Math.round(d / 7) + " weeks ago";
    if (d < 365) return Math.round(d / 30) + " months ago";
    return Math.round(d / 365) + (Math.round(d / 365) === 1 ? " year ago" : " years ago");
  }

  function clock(iso) {
    var t = Date.parse(iso);
    if (isNaN(t)) return "unknown";
    try { return new Date(t).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" }); }
    catch (e) { return new Date(t).toISOString().slice(11, 16); }
  }

  /* index.html builds the tile with innerHTML and interpolates `name`
     directly. A title is data from a manifest on disk, so it does not get
     to open a tag — the two characters that could are simply not carried
     through. `&` is left alone: it renders as itself and titles have it. */
  function safeName(s) {
    return String(s == null ? "" : s).replace(/[<>]/g, "");
  }

  /* Middle-truncated so both the drive and the leaf stay readable. */
  function shortPath(p, max) {
    if (!p) return null;
    max = max || 46;
    if (p.length <= max) return p;
    var head = p.slice(0, 12), tail = p.slice(-(max - 15));
    return head + "…" + tail;
  }

  /* ═══ Entry → tile ═════════════════════════════════════════════════ */

  /* Both maps below are keys that already exist in index.html. Nothing
     here introduces a glyph or a gradient the page does not have. */
  var ICON_FOR = {
    steam: "steam", epic: "library", gog: "library", battlenet: "library",
    xbox: "pad", shortcut: "disk", folder: "disk"
  };
  var ART_FOR = {
    steam: "play", epic: "disc", gog: "arcade", battlenet: "stream",
    xbox: "play", shortcut: "disc", folder: "arcade"
  };

  /* One sentence, and it says the thing you actually want to know
     standing in front of a television: how this title is going to start.

     It used to be a paragraph — four lines of installation-path prose,
     set at reading size, under a heading, on a screen looked at from
     three metres. On a hero that is not information, it is texture, and
     it was a large part of why the home screen read as a document rather
     than as a console. The full detail was never lost: the install path,
     the size and the launch route are all in the spec strip and the
     details panel, where somebody who wants them is already looking. */
  function describe(e) {
    switch (e.source) {
      case "steam":
        return "Starts through Steam, the same route the client's own shortcuts take — so the "
             + "overlay and your controller configuration come with it.";
      case "epic":
        return "Starts through the Epic Games Launcher's own protocol, which is what keeps "
             + "entitlements and updates working.";
      case "gog":
        return "DRM-free, so the shell runs the game's own executable directly. Galaxy does not "
             + "have to be running at all.";
      case "battlenet":
        return "Starts through Battle.net, which hands the request to the client.";
      case "xbox":
        return "A packaged title, activated by its application id rather than through a Windows "
             + "shell — this machine deliberately does not have one.";
      case "folder":
        return "Found on disk with nothing registering it: no launcher, no shortcut, no uninstall "
             + "entry. The shell runs its executable itself and tracks the process.";
      case "riot":
        if (e.kind === "launcher")
          return "Riot's own client, read from its machine-wide install record — so it is here even "
               + "when nothing made a shortcut. Sign in there to install or update a game.";
        return "Starts through the Riot Client's product launch, the same route its own shortcut "
             + "takes; Vanguard is checked by the client, not the shell.";
      default:
        if (e.kind === "launcher")
          return "Hands the display over to the client's own interface. Anything it installs shows "
               + "up here after the next scan.";
        return "A Start Menu shortcut. The shell runs its command line directly and tracks the "
             + "process it starts.";
    }
  }

  /* The source is already the eyebrow above the title, so it is not
     also a chip under it. Two identical words a centimetre apart is
     what a screen assembled from parts looks like. */
  function tagsFor(e) {
    var t = [e.launchKind === "uri" ? "Started by the client"
           : e.launchKind === "aumid" ? "Packaged app"
           : "Runs directly"];
    if (e.note) t.push(e.note);
    return t;
  }

  /* The spec strip is one line across the bottom of the reading column,
     so a fact costs horizontal space rather than vertical, and a value
     that is 60 characters of install path costs all of it. Four short
     values; the drive letter is the part of a path anybody reads at this
     distance, and the rest is a leaf name. */
  function factsFor(e) {
    var f = [["Source", e.sourceLabel]];
    var size = human(e.sizeBytes);
    f.push(["Size on disk", size || "Not measured"]);
    f.push(["Last played", ago(e.lastPlayedUtc) || "Not recorded"]);
    f.push(["Installed on", where(e.installDir)]);
    return f;
  }

  /* "C:\Program Files (x86)\Steam\steamapps\common\Palworld" → "C: · Palworld" */
  function where(dir) {
    if (!dir) return "Unknown";
    var s = String(dir).replace(/[\\/]+$/, "");
    var drive = /^([A-Za-z]:)/.exec(s);
    var leaf = s.split(/[\\/]/).pop() || s;
    if (leaf.length > 22) leaf = leaf.slice(0, 21) + "…";
    return drive ? drive[1] + " · " + leaf : leaf;
  }

  function toTile(e) {
    entriesById[e.id] = e;
    return {
      id: e.id,
      name: safeName(e.title),
      icon: ICON_FOR[e.source] || "library",
      art: ART_FOR[e.source] || "disc",
      eyebrow: e.sourceLabel,
      title: e.title,
      desc: describe(e),
      tags: tagsFor(e),
      facts: factsFor(e),
      action: e.kind === "game" ? "Play" : "Open",
      run: function () { return MarwanLibrary.launch(e.id); }
    };
  }

  /* ═══ The two tiles this shell provides itself ═════════════════════ */

  function filesTile() {
    return {
      id: "files", name: "Files", icon: "folder", art: "files", hue: 212,
      eyebrow: "This machine", title: "Files",
      desc: "Every drive Windows has mounted, browsable from the couch. Copy, move, rename and " +
            "delete run natively in the host; long jobs report progress and can be cancelled.",
      tags: ["Controller navigation", "Native operations"],
      facts: [["Source", "This shell"], ["Operations", "Native"], ["Runs in", "This shell"]],
      action: "Open",
      run: function () { if (hooks.files) hooks.files(); }
    };
  }

  /* Which search engine the browser will actually use, by its own name.
     It used to be the string "DuckDuckGo", which stopped being true the
     moment somebody changed it in the browser's menu — a fact on a hero
     that quietly contradicts the screen it describes. ui/browser.js
     persists the choice under its own key and exposes its ENGINES table;
     both fallbacks here are that file's own default. */
  function searchEngineName() {
    var key = "duckduckgo";
    try {
      var raw = root.localStorage ? root.localStorage.getItem("arc.browser.engine") : null;
      if (raw) { var v = JSON.parse(raw); if (typeof v === "string" && v) key = v; }
    } catch (e) {}
    var table = (root.MarwanBrowser && root.MarwanBrowser.engines) || null;
    if (table) {
      if (table[key] && table[key].name) return table[key].name;
      if (table.duckduckgo && table.duckduckgo.name) return table.duckduckgo.name;
    }
    return key === "duckduckgo" ? "DuckDuckGo" : key;
  }

  /* The tab ceiling is the browser's, and the host can lower it — so it is
     read rather than repeated. Four is what browser.js starts at. */
  function tabLimit() {
    try {
      if (root.MarwanBrowser && typeof root.MarwanBrowser.debugState === "function") {
        var d = root.MarwanBrowser.debugState();
        if (d && typeof d.max === "number" && d.max > 0) return d.max;
      }
    } catch (e) {}
    return 4;
  }

  function browserTile() {
    return {
      id: "web", name: "Browser", icon: "web", art: "web", hue: 188,
      eyebrow: "Internet", title: "Browser",
      desc: "A full browser sized for a television. Pinned apps open full screen; anything else is " +
            "one address away. Links are reached with the D-pad, and the left stick becomes a " +
            "pointer on sites that will not co-operate.",
      tags: ["Spatial navigation", "Stick pointer", "Full screen"],
      facts: [["Source", "This shell"], ["Tabs", tabLimit() + " max"], ["Search", searchEngineName()],
              ["Isolation", "Separate process"]],
      action: "Open",
      run: function () { if (hooks.browser) hooks.browser(); }
    };
  }

  /* ═══ The honest empty state ═══════════════════════════════════════
     A zero-length rail would break index.html in four places (APPS[tab][0],
     centre(), selectTile's modulo, paintHero on boot), so "nothing found"
     is a tile rather than an absence. It is also the better answer: an
     empty screen tells you nothing, and this one tells you where it looked
     and what to do about it. */

  /* How many sources the last scan actually read. Seven is what the host
     ships with, and it is the fallback rather than the claim: the prose and
     the number beside it now come from the same place, so a host that reads
     eight cannot leave the sentence saying seven. */
  function sourceCount() {
    return state.sources ? state.sources.length : 7;
  }

  function emptyTile() {
    var checked = sourceCount();
    var facts = [
      ["Sources checked", String(checked)],
      ["Games found", "0"],
      ["Last scan", state.scannedUtc ? clock(state.scannedUtc) : "not yet"],
      ["Runs in", "This shell"]
    ];
    return {
      id: "lib:none", name: "No games", icon: "library", art: "disc", mono: true,
      eyebrow: "Play", title: "No games found",
      /* Short enough to be read whole. The hero clamps at four lines, and
         a sentence that ends in an ellipsis on the one screen whose only
         job is to explain itself is worse than a shorter sentence. */
      desc: "Nothing playable is installed. All " + checked + " sources were checked and came back " +
            "empty. Install a client or add a Start Menu shortcut, and it will be here after the " +
            "next scan.",
      tags: ["Scanned locally", "Nothing invented"],
      facts: facts,
      action: "Scan again",
      run: function () { MarwanLibrary.refresh(); }
    };
  }

  /* The Apps tab's own honest empty state. Same argument as emptyTile and
     the same reason it exists at all: index.html reads APPS[tab][0] and
     takes a modulo of the length, so the tab cannot be handed [].  */
  function noAppsTile() {
    var checked = sourceCount();
    return {
      id: "lib:noapps", name: "No apps", icon: "library", art: "disc", mono: true,
      eyebrow: "Apps", title: "No applications found",
      desc: "The scan found nothing installed that is not a game or a launcher. Anything with a " +
            "Start Menu shortcut or an uninstall entry belongs here, so this usually means the " +
            "scan has not run since the last install.",
      tags: ["Scanned locally", "Nothing invented"],
      facts: [
        ["Sources checked", String(checked)],
        ["Apps found", "0"],
        ["Last scan", state.scannedUtc ? clock(state.scannedUtc) : "not yet"],
        ["Runs in", "This shell"]
      ],
      action: "Scan again",
      run: function () { MarwanLibrary.refresh(); }
    };
  }

  /* The scan's progress in one line, which is what the Status fact shows
     and what pressing the tile says out loud. */
  function scanLine() {
    var pct = (typeof state.percent === "number" && state.percent >= 0)
      ? Math.round(state.percent) + "%" : "";
    var phase = state.phase || "";
    if (phase && pct) return phase + " · " + pct;
    return phase || pct || "starting";
  }

  function say(msg) {
    if (!hooks.toast) return false;
    try { hooks.toast(msg); return true; } catch (e) { return false; }
  }

  /* `which` is the tab this copy of the tile is for. Both tabs are looking
     at the same job, but a card on the Apps tab that says "Looking for
     games" is a card describing the wrong screen — and the two need
     different ids, or the artwork cache hands one tile the other's face. */
  function scanningTile(which) {
    var isApps = which === "apps";
    return {
      id: isApps ? "lib:scanning:apps" : "lib:scanning",
      name: "Scanning", icon: "update", art: "disc", mono: true,
      eyebrow: isApps ? "Apps" : "Play",
      title: isApps ? "Looking for applications" : "Looking for games",
      desc: "Reading Steam's manifests, the Epic and GOG installs, the packaged titles and the " +
            "Start Menu. Whatever is genuinely installed will land on this rail; nothing that is " +
            "not will.",
      tags: ["Local scan", "No network"],
      facts: [["Status", scanLine()], ["Sources", String(sourceCount())], ["Runs in", "This shell"]],
      live: "Status",
      /* It used to be an empty function under the word "Wait": a tile the
         cursor could land on that answered nothing at all. The scan already
         reports its phase and its percentage — this says them. */
      action: "Progress",
      run: function () {
        var line = scanLine();
        if (!say(state.scanning || line !== "starting"
              ? "Scanning: " + line + "."
              : "The scan has not started yet."))
          log("scan progress: " + line);
      }
    };
  }

  function noBridgeTile(why, which) {
    var isApps = which === "apps";
    return {
      id: isApps ? "lib:nobridge:apps" : "lib:nobridge",
      name: "No host", icon: "info", art: "disc", mono: true,
      eyebrow: isApps ? "Apps" : "Play", title: "The library cannot be read",
      desc: "The shell host has not exposed LibraryApi to this page, so there is no way to find out " +
            "what is installed. Rather than show a rail of things that may not exist, the shell is " +
            "showing you this. " + (why || ""),
      tags: ["No bridge", "Nothing invented"],
      facts: [["Transport", tx.name], ["Status", "Not connected"], ["Runs in", "This shell"]],
      live: "Status",
      action: "Retry",
      run: function () { tx.fn = null; MarwanLibrary.refresh(); }
    };
  }

  /* ═══ Assembling the rail ══════════════════════════════════════════ */

  function assemble(doc) {
    var games = [], launchers = [], apps = [];
    var list = (doc && doc.entries) || [];
    for (var i = 0; i < list.length; i++) {
      var e = list[i];
      if (!e || !e.id) continue;
      var tile = toTile(e);
      if (e.kind === "game") games.push(tile);
      else if (e.kind === "launcher") launchers.push(tile);
      else apps.push(tile);
    }

    /* Launchers sit after the games: they are real and installed, but the
       thing you came to the Play tab for is a game. */
    var play = games.concat(launchers);
    if (!play.length) play = [emptyTile()];
    if (!apps.length) apps = [noAppsTile()];

    return {
      play: play,
      media: [filesTile(), browserTile()],
      apps: apps
    };
  }

  /* `make` is a factory rather than a tile, because the same state has to
     be said twice: once on the Play tab and once on the Apps tab, in that
     tab's words. Media is real either way — Files and the Browser are ours
     and do not depend on a scan. */
  function placeholder(make) {
    return { play: [make("play")], media: [filesTile(), browserTile()], apps: [make("apps")] };
  }

  /* The rail is rebuilt from scratch on every update, so a tile element
     can only tell you its id. This keeps the object that id belongs to,
     which is where the artwork seed for a tile with no scan entry lives. */
  function remember(apps) {
    var keys = ["play", "media", "apps"], i, j, list;
    for (i = 0; i < keys.length; i++) {
      list = apps[keys[i]] || [];
      for (j = 0; j < list.length; j++) if (list[j] && list[j].id) tilesById[list[j].id] = list[j];
    }
  }

  function absorb(doc) {
    state.scannedUtc = doc.scannedUtc || null;
    state.ageSeconds = typeof doc.ageSeconds === "number" ? doc.ageSeconds : 0;
    state.counts = doc.counts || null;
    state.sources = doc.sources || null;
    return assemble(doc);
  }

  function announce(apps) {
    showingScan = isScanRail(apps);
    covers = {};
    heroes = {};
    remember(apps);
    if (typeof MarwanLibrary.onUpdate === "function") {
      try { MarwanLibrary.onUpdate(apps); }
      catch (e) { log("onUpdate threw: " + e); }
    }
    decorate();
    return apps;
  }

  function log(msg) {
    try { if (typeof root.mosPost === "function") root.mosPost({ type: "log", target: "library: " + msg }); } catch (e) {}
    try { if (root.console && root.console.log) root.console.log("[library] " + msg); } catch (e) {}
  }

  /* ═══ Cover art ════════════════════════════════════════════════════
     The scan already carries a small icon per entry; the portrait cover is
     bigger and only worth fetching for tiles that are actually on screen.
     A MutationObserver on #rail means index.html never has to call this. */

  /* The PROMISE is cached, not the answer.

     Caching the answer looked equivalent and was not: the rail and the
     backdrop both ask for the focused title's cover in the same tick, so
     the second caller reached a slot that had been claimed but not yet
     filled, read `null`, and concluded the title had no cover. Because
     ui/homeart.js then caches the art record it built from that answer,
     the focused title — and only the focused title, because it is the
     only one asked for twice — permanently wore a generated portrait
     while every other Steam game on the same rail wore its real one.
     A cached promise makes the second caller wait for the first. */
  function coverFor(id) {
    if (Object.prototype.hasOwnProperty.call(covers, id)) return covers[id];
    var e = entriesById[id];
    if (!e || !e.hasArt) { covers[id] = Promise.resolve(null); return covers[id]; }
    covers[id] = call({ cmd: "lib.icon", id: id, art: true, size: 512 }).then(function (d) {
      return d && d.dataUri ? d.dataUri : null;
    }, function () { return null; });
    return covers[id];
  }

  /* ── Hero art ──────────────────────────────────────────────────────
     The portrait a tile wears is the wrong picture for a backdrop. Steam
     caches a real landscape hero next to the cover — 1920x620 — and the
     old scanner never looked for it, so the shell filled a 3840-wide
     screen by stretching a 600x900 portrait. Worse, Steam frequently
     stores the AUTO-DOWNSCALED capsule: library_600x900.jpg is often
     300x450 on disk despite its name, which is a 12.8x upscale in the
     wrong aspect. A 1920x620 hero is a 2x upscale in the right one.

     Local first and never blocking: meta.art answers from Steam's own
     cache with no network at all. `upgradable` on the reply means a
     native 3840x1240 exists on Valve's keyless CDN; fetching it is
     prefetchArt()'s job, in the background, not this call's.

     meta.art returns a PATH, and a page served from marwanos.local
     cannot open C:\...\library_hero.jpg. lib.icon turns the path into a
     data URI — passing it through untouched when the file is small
     enough, which every 1920x620 hero is.

     Resolves to null for anything that is not a Steam title, which is
     the honest answer rather than a generated stand-in. */
  function heroFor(id) {
    if (Object.prototype.hasOwnProperty.call(heroes, id)) return heroes[id];
    if (!id || id.indexOf("steam:") !== 0) { heroes[id] = Promise.resolve(null); return heroes[id]; }
    heroes[id] = call({ cmd: "meta.art", id: id, kind: "hero" }).then(function (d) {
      if (!d || !d.path) return null;
      return call({ cmd: "lib.icon", path: d.path, art: true, size: 512 }).then(function (i) {
        return i && i.dataUri ? i.dataUri : null;
      }, function () { return null; });
    }, function () { return null; });
    return heroes[id];
  }

  /* Fire-and-forget upgrade to the native 3840x1240 heroes. It is a job on
     the host and this deliberately does not poll it: the next time a tile
     asks for its hero the cached upgrade is simply there. Silent on
     failure, because a console with no network must not report an error
     for artwork nobody asked for. */
  function prefetchArt(appIds, kind) {
    if (!appIds || !appIds.length) return Promise.resolve(null);
    return call({ cmd: "meta.prefetch", appIds: appIds, kind: kind || "hero" })
      .then(function (d) { log("meta.prefetch started for " + appIds.length + " titles"); return d; },
            function (err) { log("meta.prefetch not started: " + err.message); return null; });
  }

  /* ── Live install detection ────────────────────────────────────────
     The shell used to scan once at boot and paint from cache, so a game
     installed while it was running stayed invisible until somebody
     restarted the console. The host now watches the folders that
     actually change during an install and debounces the storm — Steam
     rewrites an appmanifest dozens of times over one download — into a
     single sequence number.

     This is a CURSOR, not a subscription: lib.changed asks "what has
     happened since N?" and gets back everything at once, so a poll that
     is late loses nothing and a poll that overlaps a rescan cannot
     double-count. Same shape as ui/files.js's fs.events, on purpose.

     Started once, after the first successful scan, and never stopped:
     the rail is live for as long as the shell is. A host that does not
     understand lib.watch (an older binary) fails the first call and the
     loop simply never starts, which is the pre-watch behaviour exactly. */
  var watch = { on: false, since: 0, timer: null, ms: 1500 };

  function startWatch() {
    if (watch.on) return Promise.resolve(null);
    watch.on = true;
    return call({ cmd: "lib.watch" }).then(function () {
      log("watching for installs and removals (poll every " + watch.ms + " ms)");
      watch.timer = setInterval(pollChanged, watch.ms);
      return true;
    }, function (err) {
      watch.on = false;
      log("live install detection unavailable: " + err.message);
      return false;
    });
  }

  function pollChanged() {
    if (state.scanning) return;                 /* a rescan is already in flight */
    call({ cmd: "lib.changed", since: watch.since }).then(function (d) {
      if (!d) return;
      if (typeof d.seq === "number") watch.since = d.seq;
      var evs = d.events || [], i, e, changed = false;
      for (i = 0; i < evs.length; i++) {
        e = evs[i];
        if (!e || e.kind !== "lib.changed" || !e.data) continue;
        if (e.data.addedCount || e.data.removedCount) {
          changed = true;
          log("library changed: +" + (e.data.addedCount || 0) + " -" + (e.data.removedCount || 0));
        }
      }
      if (!changed) return;
      /* The host has ALREADY rescanned — that is what produced the event —
         and written the result to its cache. lib.list reads that back in a
         few ms; calling refresh() here would make it scan the whole disk a
         second time to learn what it just learned. refresh() stays as the
         fallback for a cache that came back empty. */
      call({ cmd: "lib.list" }).then(function (doc) {
        if (doc && !doc.never && doc.entries && doc.entries.length) announce(absorb(doc));
        else MarwanLibrary.refresh();
      }, function () { MarwanLibrary.refresh(); });
    }, function () { /* a missed poll is not an error; the cursor catches up */ });
  }

  /* Every Steam appid in the current scan, for prefetchArt. */
  function steamAppIds() {
    var out = [], id;
    for (id in entriesById) {
      if (!Object.prototype.hasOwnProperty.call(entriesById, id)) continue;
      if (id.indexOf("steam:") === 0) out.push(id.slice(6));
    }
    return out;
  }

  /* ── The art record ────────────────────────────────────────────────
     One promise per tile, resolved once and shared by the rail and the
     backdrop, so pointing at a title twice costs nothing the second time.
     Three routes in, and every tile takes one of them:

       cover  a real portrait from the source (Steam's librarycache).
       icon   no cover, but the scan extracted the executable's icon —
              ui/homeart.js builds a portrait out of it. On this laptop
              that is 2XKO, League of Legends, Teamfight Tactics and
              TEKKEN 8: four of the seven games, so it is the ordinary
              case rather than the fallback.
       seed   the shell's own tiles and the honest-empty ones, which have
              no artwork by nature and should not pretend otherwise.

     MarwanArt is loaded before this file, but it is not required: with the
     module absent every tile silently keeps the gradient face index.html
     gave it, which is the pre-artwork shell exactly as it was. */

  function artFor(app) {
    var id = app && app.id ? app.id : String(app);
    var t = tilesById[id] || (typeof app === "object" ? app : null);
    if (!root.MarwanArt) return Promise.resolve(null);

    var e = entriesById[id];
    if (!e) {
      return root.MarwanArt.build(id, {
        seed: (t && t.mono) ? { mono: true } : { h: (t && t.hue) || 214 }
      });
    }
    return coverFor(id).then(function (uri) {
      return root.MarwanArt.build(id, {
        cover: uri || null,
        icon:  uri ? null : (e.icon || null),
        seed:  { h: 214 }
      });
    });
  }

  var observer = null;

  function decorate() {
    var rail = document.getElementById("rail");
    if (!rail) return;
    var tiles = rail.querySelectorAll(".tile");
    for (var i = 0; i < tiles.length; i++) paintTile(tiles[i]);
    if (!observer && root.MutationObserver) {
      observer = new MutationObserver(function () { decorate(); });
      observer.observe(rail, { childList: true });
    }
  }

  /* Painting a tile means choosing which of three FACES it wears, and the
     choice is a class rather than an inline background. That is not a
     style preference: index.html used to set `background:` inline on
     .tile-art, and an inline `background` shorthand resets background-size
     and background-repeat to their initial values — which beat every rule
     in library.css, because inline always does. The visible result was
     that every real cover was shown as an unscaled crop of its own top
     left corner and every extracted icon was TILED across the box. It was
     the single biggest reason the rail looked generated rather than
     designed, and it was invisible to every numeric check ever run
     against this screen. Faces are classes now, and .tile-art carries no
     inline style at all. */
  function paintTile(tile) {
    var label = tile.getAttribute("data-label") || "";
    if (label.indexOf("tile:") !== 0) return;
    var id = label.slice(5);
    var face = tile.querySelector(".tile-art");
    if (!face || face.getAttribute("data-face") === id) return;

    var e = entriesById[id];
    /* The extracted icon is already in hand from the scan, so a tile is
       never a bare field while the cover is still being fetched. */
    if (e && e.icon && !face.getAttribute("data-face")) {
      var mark = tile.querySelector(".tile-mark");
      if (mark) { mark.style.backgroundImage = "url(\"" + e.icon + "\")"; tile.classList.add("has-mark"); }
    }

    artFor({ id: id }).then(function (art) {
      if (!art) return;
      face.setAttribute("data-face", id);
      tile.style.setProperty("--k-accent", art.accent);
      tile.style.setProperty("--k-deep", art.deep);
      tile.style.setProperty("--k-ink", art.ink);
      if (art.cover || art.face) {
        face.style.backgroundImage = "url(\"" + (art.cover || art.face) + "\")";
        tile.classList.add("face-art");
        tile.classList.toggle("face-made", !art.cover);
        tile.classList.remove("has-mark");
      } else {
        tile.classList.add("face-field");
      }
    });
  }

  /* ═══ Public ═══════════════════════════════════════════════════════ */

  var MarwanLibrary = {
    onUpdate: null,

    initial: function (h) {
      if (h) {
        hooks.files = h.files || null;
        hooks.browser = h.browser || null;
        hooks.toast = h.toast || null;
      }
      var apps = placeholder(scanningTile);
      showingScan = true;
      remember(apps);
      return apps;
    },

    /* Cache first so the rail paints at boot speed, then a real scan in the
       background. Both stages go through onUpdate, so the caller wires the
       repaint once and never thinks about it again. */
    load: function () {
      if (!detectTransport()) {
        log("no bridge: " + tx.why);
        state.error = "no_bridge";
        showingScan = false;
        return Promise.resolve(placeholder(function (which) { return noBridgeTile(tx.why, which); }));
      }
      return call({ cmd: "lib.list" }).then(function (doc) {
        var apps;
        if (doc && !doc.never && doc.entries && doc.entries.length) {
          apps = absorb(doc);
          announce(apps);
          log("painted " + doc.entries.length + " cached entries (" + state.ageSeconds + "s old)");
        } else {
          apps = placeholder(scanningTile);
          showingScan = true;              /* the rail IS the scanning card; keep it live */
        }
        /* Refresh regardless: the cache is a head start, never the answer. */
        MarwanLibrary.refresh();
        return apps;
      }, function (err) {
        log("lib.list failed: " + err.message);
        return MarwanLibrary.refresh();
      });
    },

    refresh: function () {
      if (!detectTransport()) {
        state.error = "no_bridge";
        return Promise.resolve(announce(placeholder(function (which) { return noBridgeTile(tx.why, which); })));
      }
      if (state.scanning) return Promise.resolve(null);
      state.scanning = true;
      state.error = null;
      return callJob({ cmd: "lib.scan", icons: true, iconSize: 96 }, 180000).then(function (doc) {
        state.scanning = false;
        var apps = absorb(doc);
        log("scan complete: " + (doc.counts ? doc.counts.games : "?") + " games, " +
            (doc.counts ? doc.counts.apps : "?") + " apps in " + doc.elapsedMs + " ms");
        if (doc.notInstalled && doc.notInstalled.length)
          log(doc.notInstalled.length + " entr" + (doc.notInstalled.length === 1 ? "y was" : "ies were") +
              " dropped as not actually installed");
        var out = announce(apps);
        /* Both of these are deliberately AFTER announce: the rail is painted
           from what the scan already found, and neither of them may delay it. */
        startWatch();
        prefetchArt(steamAppIds(), "hero");
        return out;
      }, function (err) {
        state.scanning = false;
        state.error = err.message;
        log("scan failed: " + err.message);
        var why = err.message;
        return announce(placeholder(function (which) {
          var t = noBridgeTile("The scan failed: " + why, which);
          t.title = "The library scan failed";
          t.desc = "The host could not finish reading what is installed, so the rail is showing you " +
                   "this instead of a guess. " + why;
          return t;
        }));
      });
    },

    launch: function (id) {
      var e = entriesById[id];
      if (!e) return Promise.reject(new Error("no such library entry: " + id));
      log("launch " + id + " (" + e.launchKind + " " + e.launchTarget + ")");

      /* Ask first. An app that is already up is raised, not started again —
         a second Steam client is not a second launch, it is a bug. */
      return askHostRunning(e).then(function (r) {
        if (r && r.running) {
          log("not launching " + id + ": " + (r.detail || "already running") +
              " — the host brought it to the front instead");
          return { raised: true, alreadyRunning: true, detail: r.detail || "already running" };
        }
        return call({ cmd: "lib.launch", id: id }).then(function (d) {
          log("launched " + id + ": pid=" + (d.pid || "none") +
              " trackable=" + (d.trackable ? "yes" : "no") +
              " pidIsTarget=" + (d.pidIsTarget ? "yes" : "no"));
          /* The host owns tracking. Tell it what came back, including whether
             the pid is the target or a launcher stub that will exit in a
             second and must NOT be read as the game quitting. The host adopts
             it from here: hides the shell, tracks the tree, and forces the
             foreground back when it is empty. */
          hostPost({
            type: "launched", id: id, title: e.title,
            /* What this tile IS, as opposed to how it was started. The host's
               pointer mode is the consumer: a launcher is a mouse UI and gets a
               cursor when it takes the screen, a game reads the pad itself and
               must never have one pushed at it. */
            entryKind: e.kind || "",
            pid: d.pid || 0, launchKind: d.launchKind,
            launchTarget: d.launchTarget || e.launchTarget,
            trackable: !!d.trackable, pidIsTarget: !!d.pidIsTarget
          });
          return d;
        }, function (err) {
          log("launch failed for " + id + ": " + err.message);
          throw err;
        });
      });
    },

    cover: function (app) { return coverFor(app && app.id ? app.id : app); },

    /* The landscape hero for a backdrop, as a data URI, or null. Local and
       instant; see heroFor. index.html's paintHero is the intended caller:
         MarwanLibrary.hero(app.id).then(uri => { if (uri) setHero(uri); });
       Null is a real answer — non-Steam titles have no hero and the shell
       should keep the generated backdrop rather than invent one. */
    hero: function (app) { return heroFor(app && app.id ? app.id : app); },

    /* Fire-and-forget upgrade of a list of Steam appids to native 3840x1240
       heroes. Called automatically after every scan; exposed so a settings
       screen can offer it on demand. */
    prefetchArt: prefetchArt,

    art: artFor,

    entry: function (id) { return entriesById[id] || null; },

    state: function () {
      return {
        scanning: state.scanning, error: state.error, phase: state.phase,
        percent: state.percent, scannedUtc: state.scannedUtc,
        ageSeconds: state.ageSeconds, counts: state.counts, sources: state.sources,
        transport: state.transport()
      };
    },

    decorate: decorate
  };

  root.MarwanLibrary = MarwanLibrary;
})(typeof window !== "undefined" ? window : this);
