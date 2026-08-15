/* ═══════════════════════════════════════════════════════════════════════
   ARC OS — library.js
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
     ArcLibrary.initial(hooks)   synchronous, no host call. The rail data
                                 to paint with before anything has loaded:
                                 Files, Browser, and a Play tab that says
                                 it is still looking.
     ArcLibrary.load()           Promise<APPS>. Paints from the host's disk
                                 cache immediately if there is one, then
                                 refreshes in the background and fires
                                 onUpdate when the fresh scan lands.
     ArcLibrary.refresh()        Promise<APPS>. Forces a scan now.
     ArcLibrary.onUpdate         function(APPS) — assign it. Called every
                                 time a newer set of tiles is available.
     ArcLibrary.state()          {scanning, error, scannedUtc, ageSeconds,
                                  counts, sources} — for a diagnostics panel.
     ArcLibrary.launch(app)      launch a tile. Tiles carry this as `run`,
                                 so index.html needs to know nothing.
     ArcLibrary.cover(app)       Promise<dataUri|null>. The large portrait
                                 art, fetched lazily.
     ArcLibrary.art(app)         Promise<art> from ui/homeart.js — the
                                 cover, the backdrop wash and the colours
                                 the whole home screen takes its mood
                                 from. Every tile has one, including the
                                 ones with no artwork at all.

   `hooks` is {files: fn, browser: fn} — the two in-shell openers, passed
   in rather than reached for, so this file never has to know that ACT
   exists.

   ── The shape returned ──────────────────────────────────────────────
   Exactly what index.html's rail already consumes:
     { id, name, icon, art, eyebrow, title, desc, tags, facts, action, run }
   `icon` is a key into index.html's ICON map, `art` a key into G. Both are
   chosen from the existing sets — this module adds no new artwork.

   A third key, `apps`, carries the installed non-game software the scan
   found (Git, VS Code, Stremio…). Nothing renders it yet: the shell has
   two tabs and neither is an app list. It is there so that adding one is
   a change to index.html and not another scan.

   ── Transport ───────────────────────────────────────────────────────
   Same three-way detection as ui/files.js, one message type along:
     1. window.arcLibraryApi(json)
     2. chrome.webview.hostObjects.arcLibrary.Handle
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

    if (typeof root.arcLibraryApi === "function") {
      tx.name = "arcLibraryApi";
      tx.why = "window.arcLibraryApi(json) is present";
      tx.fn = function (cmd) {
        return new Promise(function (resolve, reject) {
          var r;
          try { r = root.arcLibraryApi(JSON.stringify(cmd)); }
          catch (e) { reject(e); return; }
          if (r && typeof r.then === "function") r.then(function (v) { resolve(parseEnv(v)); }, reject);
          else resolve(parseEnv(r));
        });
      };
      return tx.fn;
    }

    try {
      if (root.chrome && root.chrome.webview && root.chrome.webview.hostObjects &&
          root.chrome.webview.hostObjects.arcLibrary) {
        tx.name = "hostObject";
        tx.why = "chrome.webview.hostObjects.arcLibrary is present";
        tx.fn = function (cmd) {
          return Promise.resolve(root.chrome.webview.hostObjects.arcLibrary.Handle(JSON.stringify(cmd)))
            .then(function (v) { return parseEnv(v); });
        };
        return tx.fn;
      }
    } catch (e) {}

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

  var entriesById = {};      /* id -> raw entry from the host */
  var tilesById = {};        /* id -> the tile object last handed to the shell */
  var hooks = { files: null, browser: null };
  var covers = {};           /* id -> dataUri | null (null = asked, none there) */

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
      run: function () { return ArcLibrary.launch(e.id); }
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

  function browserTile() {
    return {
      id: "web", name: "Browser", icon: "web", art: "web", hue: 188,
      eyebrow: "Internet", title: "Browser",
      desc: "A full browser sized for a television. Pinned apps open full screen; anything else is " +
            "one address away. Links are reached with the D-pad, and the left stick becomes a " +
            "pointer on sites that will not co-operate.",
      tags: ["Spatial navigation", "Stick pointer", "Full screen"],
      facts: [["Source", "This shell"], ["Tabs", "4 max"], ["Search", "DuckDuckGo"],
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

  function emptyTile() {
    var checked = state.sources ? state.sources.length : 7;
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
      desc: "Nothing playable is installed. All seven sources were checked and came back empty. " +
            "Install a client or add a Start Menu shortcut, and it will be here after the next scan.",
      tags: ["Scanned locally", "Nothing invented"],
      facts: facts,
      action: "Scan again",
      run: function () { ArcLibrary.refresh(); }
    };
  }

  function scanningTile() {
    return {
      id: "lib:scanning", name: "Scanning", icon: "update", art: "disc", mono: true,
      eyebrow: "Play", title: "Looking for games",
      desc: "Reading Steam's manifests, the Epic and GOG installs, the packaged titles and the " +
            "Start Menu. Whatever is genuinely installed will land on this rail; nothing that is " +
            "not will.",
      tags: ["Local scan", "No network"],
      facts: [["Status", state.phase || "starting"], ["Sources", "7"], ["Runs in", "This shell"]],
      live: "Status",
      action: "Wait",
      run: function () { }
    };
  }

  function noBridgeTile(why) {
    return {
      id: "lib:nobridge", name: "No host", icon: "info", art: "disc", mono: true,
      eyebrow: "Play", title: "The library cannot be read",
      desc: "The shell host has not exposed LibraryApi to this page, so there is no way to find out " +
            "what is installed. Rather than show a rail of things that may not exist, the shell is " +
            "showing you this. " + (why || ""),
      tags: ["No bridge", "Nothing invented"],
      facts: [["Transport", tx.name], ["Status", "Not connected"], ["Runs in", "This shell"]],
      live: "Status",
      action: "Retry",
      run: function () { tx.fn = null; ArcLibrary.refresh(); }
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

    return {
      play: play,
      media: [filesTile(), browserTile()],
      apps: apps
    };
  }

  function placeholder(tile) {
    return { play: [tile], media: [filesTile(), browserTile()], apps: [] };
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
    covers = {};
    remember(apps);
    if (typeof ArcLibrary.onUpdate === "function") {
      try { ArcLibrary.onUpdate(apps); }
      catch (e) { log("onUpdate threw: " + e); }
    }
    decorate();
    return apps;
  }

  function log(msg) {
    try { if (typeof root.arcPost === "function") root.arcPost({ type: "log", target: "library: " + msg }); } catch (e) {}
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

     ArcArt is loaded before this file, but it is not required: with the
     module absent every tile silently keeps the gradient face index.html
     gave it, which is the pre-artwork shell exactly as it was. */

  function artFor(app) {
    var id = app && app.id ? app.id : String(app);
    var t = tilesById[id] || (typeof app === "object" ? app : null);
    if (!root.ArcArt) return Promise.resolve(null);

    var e = entriesById[id];
    if (!e) {
      return root.ArcArt.build(id, {
        seed: (t && t.mono) ? { mono: true } : { h: (t && t.hue) || 214 }
      });
    }
    return coverFor(id).then(function (uri) {
      return root.ArcArt.build(id, {
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

  var ArcLibrary = {
    onUpdate: null,

    initial: function (h) {
      if (h) { hooks.files = h.files || null; hooks.browser = h.browser || null; }
      var apps = placeholder(scanningTile());
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
        return Promise.resolve(placeholder(noBridgeTile(tx.why)));
      }
      return call({ cmd: "lib.list" }).then(function (doc) {
        var apps;
        if (doc && !doc.never && doc.entries && doc.entries.length) {
          apps = absorb(doc);
          announce(apps);
          log("painted " + doc.entries.length + " cached entries (" + state.ageSeconds + "s old)");
        } else {
          apps = placeholder(scanningTile());
        }
        /* Refresh regardless: the cache is a head start, never the answer. */
        ArcLibrary.refresh();
        return apps;
      }, function (err) {
        log("lib.list failed: " + err.message);
        return ArcLibrary.refresh();
      });
    },

    refresh: function () {
      if (!detectTransport()) {
        state.error = "no_bridge";
        return Promise.resolve(announce(placeholder(noBridgeTile(tx.why))));
      }
      if (state.scanning) return Promise.resolve(null);
      state.scanning = true;
      state.error = null;
      return callJob({ cmd: "lib.scan", icons: true, iconSize: 96 }, 180000).then(function (doc) {
        state.scanning = false;
        var apps = absorb(doc);
        log("scan complete: " + (doc.counts ? doc.counts.games : "?") + " games, " +
            (doc.counts ? doc.counts.apps : "?") + " apps in " + doc.elapsedMs + " ms");
        return announce(apps);
      }, function (err) {
        state.scanning = false;
        state.error = err.message;
        log("scan failed: " + err.message);
        var t = noBridgeTile("The scan failed: " + err.message);
        t.title = "The library scan failed";
        t.desc = "The host could not finish reading what is installed, so the rail is showing you " +
                 "this instead of a guess. " + err.message;
        return announce(placeholder(t));
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

  root.ArcLibrary = ArcLibrary;
})(typeof window !== "undefined" ? window : this);
