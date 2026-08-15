/* ═══════════════════════════════════════════════════════════════════════
   PC1 — file explorer  (ui/files.js)

   A controller-driven file manager. No dependencies, no build step, no module
   system: this file defines one global, `ArcFiles`, in the same plain-JS style
   as index.html and ui/osk.js.

   Public API
   ──────────
     ArcFiles.open({ path, transport, onExit, title })
     ArcFiles.close()
     ArcFiles.isOpen()
     ArcFiles.handleAction(action)   // the pad channel — returns true if consumed
     ArcFiles.beginRepeat(action)    // hold-to-repeat: press
     ArcFiles.endRepeat()            // hold-to-repeat: release
     ArcFiles.setTransport(fn)       // fn(commandObject) -> Promise(envelope)
     ArcFiles.probe()                // which transport was chosen, and why
     ArcFiles.debugState()
     ArcFiles._model                 // pure helpers, for the headless test

   Integration with the shell
   ──────────────────────────
   The explorer is modal in exactly the way the on-screen keyboard is: while it
   is up it takes every action, and nothing behind it moves. In index.html's
   Nav.action(), after the ArcOSK line:

       if (window.ArcFiles && ArcFiles.isOpen() && ArcFiles.handleAction(button || action)) return "files";

   It renders its own hint bar from its own scope stack, using the same PS5
   glyph vocabulary as the shell, so it needs nothing from the shell's Hints
   or Focus modules.

   Talking to the native layer
   ──────────────────────────
   Everything goes through one transport function that takes a command object
   and resolves to FileApi's envelope, {ok:true,data} or {ok:false,error,detail}.
   Four are auto-detected, in order:

     1. window.arcFileApi(json)                    — a host-injected function
     2. chrome.webview.hostObjects.arcFiles.Handle — a host object
     3. chrome.webview postMessage {type:"fs"}     — reply {type:"fs.reply"}
     4. nothing                                    — the explorer says so on
                                                     screen instead of pretending

   Crash isolation
   ───────────────
   This runs inside the Windows shell process. An escaped exception blanks a
   television, so every entry point — public method, event listener, timer,
   promise handler — is wrapped by guard(). Failures are logged and, when the
   ShellHostWeb shim is present, forwarded to the host log via window.arcPost.

   Styling lives in ui/files.css. It is a separate file rather than an injected
   <style> block because a strict CSP (`style-src 'self'`) blocks inline style
   elements but allows a same-origin stylesheet. If the sheet is not already
   linked when open() is first called, this file adds a <link> pointing at
   files.css next to itself — a resource fetch, which CSP permits.
   ═══════════════════════════════════════════════════════════════════════ */

(function (root) {
"use strict";

var VERSION = "1.0.0";

/* ═══ Crash isolation ══════════════════════════════════════════════════ */

function emit(level, where, what) {
  var msg = "ArcFiles[" + where + "] " + (what && what.stack ? what.stack : what);
  try { if (root.console && console[level]) console[level](msg); } catch (e) {}
  try { if (typeof root.arcPost === "function") root.arcPost({ type: "log", target: msg }); } catch (e) {}
}
function report(where, err) { emit("error", where, err); }
function note(where, what) { emit("info", where, what); }

function guard(where, fn) {
  return function () {
    try { return fn.apply(this, arguments); }
    catch (err) { report(where, err); return undefined; }
  };
}

/* ═══ Formatting ═══════════════════════════════════════════════════════
   Sizes are binary because that is what Windows reports and what the free
   space in the drive picker has to agree with. A television is read from
   three metres, so three significant figures is the ceiling of useful. */

var UNITS = ["bytes", "KB", "MB", "GB", "TB", "PB"];

function bytes(n) {
  if (n === null || n === undefined || n < 0 || isNaN(n)) return "—";
  n = Number(n);
  if (n < 1024) return n + (n === 1 ? " byte" : " bytes");
  var i = 0, d = n;
  while (d >= 1024 && i < UNITS.length - 1) { d /= 1024; i++; }
  return (d < 10 ? d.toFixed(2) : d < 100 ? d.toFixed(1) : Math.round(d)) + " " + UNITS[i];
}

function rate(bps) {
  if (!bps || bps <= 0) return "—";
  return bytes(bps) + "/s";
}

function eta(sec) {
  if (sec === null || sec === undefined || !isFinite(sec) || sec < 0) return "—";
  sec = Math.round(sec);
  if (sec < 60) return sec + " s";
  var m = Math.floor(sec / 60), s = sec % 60;
  if (m < 60) return m + " min " + (s < 10 ? "0" : "") + s + " s";
  var h = Math.floor(m / 60);
  return h + " h " + ((m % 60) < 10 ? "0" : "") + (m % 60) + " min";
}

var MONTHS = ["Jan","Feb","Mar","Apr","May","Jun","Jul","Aug","Sep","Oct","Nov","Dec"];

function when(iso) {
  if (!iso) return "—";
  var d = new Date(iso);
  if (isNaN(d.getTime())) return "—";
  var now = new Date();
  var same = d.getFullYear() === now.getFullYear();
  var hh = String(d.getHours()).padStart(2, "0");
  var mm = String(d.getMinutes()).padStart(2, "0");
  return d.getDate() + " " + MONTHS[d.getMonth()] + (same ? "" : " " + d.getFullYear()) + "  " + hh + ":" + mm;
}

/* File kinds drive the icon and the details line. Deliberately short: a
   television does not need forty file-type names, it needs to tell a photo
   from a video from a thing it cannot open. */
var KIND = {
  txt:"doc", md:"doc", log:"doc", ini:"doc", cfg:"doc", csv:"doc", rtf:"doc",
  pdf:"doc", doc:"doc", docx:"doc", xls:"doc", xlsx:"doc", ppt:"doc", pptx:"doc",
  jpg:"image", jpeg:"image", png:"image", gif:"image", bmp:"image", webp:"image",
  tif:"image", tiff:"image", heic:"image", avif:"image", svg:"image",
  mp3:"audio", flac:"audio", wav:"audio", m4a:"audio", ogg:"audio", opus:"audio", aac:"audio", wma:"audio",
  mp4:"video", mkv:"video", avi:"video", mov:"video", webm:"video", m2ts:"video", wmv:"video",
  zip:"archive", rar:"archive", "7z":"archive", tar:"archive", gz:"archive", iso:"archive", cab:"archive",
  exe:"app", msi:"app", bat:"app", cmd:"app", ps1:"app", lnk:"app", com:"app",
  js:"code", ts:"code", json:"code", html:"code", htm:"code", css:"code", xml:"code",
  cs:"code", py:"code", c:"code", h:"code", cpp:"code", java:"code", sh:"code", yml:"code", yaml:"code"
};

var KIND_NAME = {
  doc:"Document", image:"Image", audio:"Audio", video:"Video",
  archive:"Archive", app:"Program", code:"Text", file:"File", dir:"Folder"
};

function kindOf(entry) {
  if (entry.isDirectory) return "dir";
  var k = KIND[(entry.ext || "").toLowerCase()];
  return k || "file";
}

function typeLabel(entry) {
  if (entry.isDirectory) return "Folder";
  var e = (entry.ext || "").toUpperCase();
  var k = KIND_NAME[kindOf(entry)] || "File";
  return e ? (e + " " + k.toLowerCase()) : k;
}

/* ═══ Inline SVG ═══════════════════════════════════════════════════════
   Same 24×24 stroke language as every other icon in PC1. */

var ICON = {
  folder:  '<path d="M3.2 6.6a1.6 1.6 0 0 1 1.6-1.6h4l2 2.4h7.4a1.6 1.6 0 0 1 1.6 1.6v8.4a1.6 1.6 0 0 1-1.6 1.6H4.8a1.6 1.6 0 0 1-1.6-1.6z"/>',
  file:    '<path d="M6.4 3.6h7l4.2 4.2v12.6H6.4z"/><path d="M13.4 3.6v4.2h4.2"/>',
  doc:     '<path d="M6.4 3.6h7l4.2 4.2v12.6H6.4z"/><path d="M13.4 3.6v4.2h4.2"/><path d="M9 12h6M9 15.2h6M9 8.8h2.6"/>',
  image:   '<rect x="3.2" y="5" width="17.6" height="14" rx="2.2"/><path d="m3.9 16.4 4.3-4.5 3.2 3.2 3-3 5.7 5.3"/><circle cx="8.5" cy="9.5" r="1.4"/>',
  audio:   '<path d="M9.4 17.4V6.3l9-1.9v11.2"/><circle cx="7.1" cy="17.5" r="2.3"/><circle cx="16.2" cy="15.6" r="2.3"/>',
  video:   '<rect x="2.6" y="5.6" width="18.8" height="12.8" rx="2.4"/><path d="M10 9.7v4.7l4.4-2.4z"/>',
  archive: '<rect x="3.4" y="4.6" width="17.2" height="14.8" rx="2"/><path d="M12 4.6v14.8" stroke-dasharray="2 1.8"/><rect x="10.4" y="10.6" width="3.2" height="3.4" rx=".8"/>',
  app:     '<rect x="3.4" y="4.6" width="17.2" height="14.8" rx="2.2"/><path d="M8 9.6 11 12l-3 2.4M12.8 14.6h3.4"/>',
  code:    '<path d="m8.6 8.4-4 3.6 4 3.6M15.4 8.4l4 3.6-4 3.6M13.6 5.6l-3.2 12.8"/>',
  disk:    '<rect x="2.8" y="6.4" width="18.4" height="11.2" rx="2.4"/><circle cx="17.4" cy="12" r="1.2"/><path d="M6 9.6h6M6 14.4h4"/>',
  usb:     '<path d="M12 20.4V6.2"/><path d="m9.2 8.8 2.8-2.6 2.8 2.6"/><path d="M8.2 12.6h2.2v3.2M15.8 14.2h-2.2v-3.4"/><circle cx="8.2" cy="11.4" r="1.2"/><rect x="14.6" y="13" width="2.4" height="2.4" rx=".5"/>',
  optical: '<circle cx="12" cy="12" r="8.4"/><circle cx="12" cy="12" r="2.4"/>',
  network: '<path d="M2 8.4a15 15 0 0 1 20 0"/><path d="M5.4 12a10 10 0 0 1 13.2 0"/><path d="M8.9 15.4a5 5 0 0 1 6.2 0"/><circle cx="12" cy="18.8" r="1"/>',
  home:    '<path d="M4 10.6 12 4l8 6.6"/><path d="M6.2 9.6v9.8h11.6V9.6"/><path d="M10.2 19.4v-5h3.6v5"/>',
  desktop: '<rect x="2.6" y="4.6" width="18.8" height="12.4" rx="2"/><path d="M8.6 20.4h6.8M12 17v3.4"/>',
  dl:      '<path d="M12 4v10.4"/><path d="m7.8 10.5 4.2 4.2 4.2-4.2"/><path d="M4.6 18.6h14.8"/>',
  photo:   '<rect x="3" y="5" width="18" height="14" rx="2.4"/><path d="m3.8 16.4 4.4-4.6 3.3 3.3 3-3.1 5.7 5.4"/><circle cx="8.4" cy="9.4" r="1.5"/>',
  music:   '<path d="M9.5 17.5V6.2l9-1.9v11.3"/><circle cx="7.2" cy="17.6" r="2.3"/><circle cx="16.3" cy="15.6" r="2.3"/>',
  up:      '<path d="M12 19.4V5"/><path d="m6.4 10.6 5.6-5.6 5.6 5.6"/>',
  copy:    '<rect x="8.4" y="3.6" width="12" height="12" rx="2"/><path d="M15.6 18.4a2 2 0 0 1-2 2H5.6a2 2 0 0 1-2-2V9.6a2 2 0 0 1 2-2"/>',
  move:    '<path d="M3.6 6.4a1.6 1.6 0 0 1 1.6-1.6h3.6l2 2.4h7a1.6 1.6 0 0 1 1.6 1.6v8a1.6 1.6 0 0 1-1.6 1.6H5.2a1.6 1.6 0 0 1-1.6-1.6z"/><path d="M9.4 12.6h6.2M13.2 10.2l2.6 2.4-2.6 2.4"/>',
  paste:   '<path d="M8.6 4.6H6.8a1.8 1.8 0 0 0-1.8 1.8v12.2a1.8 1.8 0 0 0 1.8 1.8h10.4a1.8 1.8 0 0 0 1.8-1.8V6.4a1.8 1.8 0 0 0-1.8-1.8h-1.8"/><rect x="9" y="2.8" width="6" height="3.4" rx="1.1"/><path d="M8.6 12.8h6.8M8.6 16h4.4"/>',
  rename:  '<path d="M4.4 19.6h4l10-10a2.1 2.1 0 0 0-3-3l-10 10z"/><path d="M14.2 7.4l2.4 2.4"/>',
  trash:   '<path d="M4.6 6.8h14.8"/><path d="M9.4 6.8V4.6h5.2v2.2"/><path d="M6.6 6.8 7.6 20h8.8l1-13.2"/><path d="M10.4 10.4v6M13.6 10.4v6"/>',
  nuke:    '<path d="M4.6 6.8h14.8"/><path d="M9.4 6.8V4.6h5.2v2.2"/><path d="M6.6 6.8 7.6 20h8.8l1-13.2"/><path d="m9.8 10.6 4.4 5.2M14.2 10.6l-4.4 5.2"/>',
  newdir:  '<path d="M3.2 6.6a1.6 1.6 0 0 1 1.6-1.6h4l2 2.4h7.4a1.6 1.6 0 0 1 1.6 1.6v8.4a1.6 1.6 0 0 1-1.6 1.6H4.8a1.6 1.6 0 0 1-1.6-1.6z"/><path d="M12 10.6v5.2M9.4 13.2h5.2"/>',
  info:    '<circle cx="12" cy="12" r="8.6"/><path d="M12 11v5.2"/><circle cx="12" cy="7.9" r="1"/>',
  eject:   '<path d="m12 4.6 7 8.2H5z"/><rect x="5" y="16" width="14" height="2.8" rx="1"/>',
  search:  '<circle cx="10.6" cy="10.6" r="6.2"/><path d="m15.2 15.2 4.4 4.4"/>',
  eye:     '<path d="M2.6 12s3.6-6.2 9.4-6.2S21.4 12 21.4 12s-3.6 6.2-9.4 6.2S2.6 12 2.6 12z"/><circle cx="12" cy="12" r="2.6"/>',
  sort:    '<path d="M6.6 5.4v13.2M3.6 15.6l3 3 3-3"/><path d="M13 6.6h7.4M13 11.4h5.4M13 16.2h3.4"/>',
  refresh: '<path d="M20 12a8 8 0 1 1-2.6-5.9"/><path d="M20.2 4.2v4.4h-4.4"/>',
  open:    '<path d="M13.6 4.6h5.8v5.8"/><path d="m19.4 4.6-7.6 7.6"/><path d="M18 14.2v4.2a1.8 1.8 0 0 1-1.8 1.8H5.6a1.8 1.8 0 0 1-1.8-1.8V7.8A1.8 1.8 0 0 1 5.6 6h4.2"/>',
  warn:    '<path d="M12 4.4 21 19.6H3z"/><path d="M12 10v4.4"/><circle cx="12" cy="17.2" r=".9"/>',
  empty:   '<path d="M3.2 6.6a1.6 1.6 0 0 1 1.6-1.6h4l2 2.4h7.4a1.6 1.6 0 0 1 1.6 1.6v8.4a1.6 1.6 0 0 1-1.6 1.6H4.8a1.6 1.6 0 0 1-1.6-1.6z"/><path d="M8.6 13.4h6.8"/>',
  plug:    '<path d="M8.4 3.6v5M15.6 3.6v5"/><path d="M5.6 8.6h12.8v3.2a6.4 6.4 0 0 1-12.8 0z"/><path d="M12 18.2v2.4"/>'
};

function svg(name, cls) {
  var d = ICON[name] || ICON.file;
  return '<svg viewBox="0 0 24 24"' + (cls ? ' class="' + cls + '"' : "") + ' aria-hidden="true">' + d + "</svg>";
}

/* ── DualSense glyphs ─────────────────────────────────────────────────
   Lifted verbatim from index.html's vocabulary so the two hint bars are the
   same object — read the long note beside PAD_GLYPH there for where each of
   these shapes comes from and why the PS button is not among them. Keep the
   two tables in step: a file explorer whose Cross is a different Cross from
   the home screen's is worse than either drawing on its own. */
var PAD_GLYPH = {
  cross:    '<path class="face" d="M5.7 5.7 18.3 18.3M18.3 5.7 5.7 18.3"/>',
  circle:   '<circle class="face" cx="12" cy="12" r="6.15"/>',
  square:   '<rect class="face" x="6.45" y="6.45" width="11.1" height="11.1"/>',
  triangle: '<path class="face tri" d="M12 3.7 20 17.9H4z"/>',
  dpad:     '<path class="solid" d="M7.2.6H16.8V6L12 10.5 7.2 6Z"/>'
          + '<path class="solid" d="M7.2 23.4H16.8V18L12 13.5 7.2 18Z"/>'
          + '<path class="solid" d="M.6 7.2V16.8H6L10.5 12 6 7.2Z"/>'
          + '<path class="solid" d="M23.4 7.2V16.8H18L13.5 12 18 7.2Z"/>',
  dpadLR:   '<path class="solid" d="M7.2.6H16.8V6L12 10.5 7.2 6Z" opacity=".3"/>'
          + '<path class="solid" d="M7.2 23.4H16.8V18L12 13.5 7.2 18Z" opacity=".3"/>'
          + '<path class="solid" d="M.6 7.2V16.8H6L10.5 12 6 7.2Z"/>'
          + '<path class="solid" d="M23.4 7.2V16.8H18L13.5 12 18 7.2Z"/>',
  dpadUD:   '<path class="solid" d="M7.2.6H16.8V6L12 10.5 7.2 6Z"/>'
          + '<path class="solid" d="M7.2 23.4H16.8V18L12 13.5 7.2 18Z"/>'
          + '<path class="solid" d="M.6 7.2V16.8H6L10.5 12 6 7.2Z" opacity=".3"/>'
          + '<path class="solid" d="M23.4 7.2V16.8H18L13.5 12 18 7.2Z" opacity=".3"/>',
  l1:       '<rect x="1.2" y="6.8" width="21.6" height="10.4" rx="2.6"/>'
          + '<text class="lbl" x="12" y="15.1" text-anchor="middle">L1</text>',
  r1:       '<rect x="1.2" y="6.8" width="21.6" height="10.4" rx="2.6"/>'
          + '<text class="lbl" x="12" y="15.1" text-anchor="middle">R1</text>',
  options:  '<path class="solid" d="M3 4.76h18v2H3zM3 11h18v2H3zM3 17.24h18v2H3z"/>'
};

function glyphSVG(name) {
  var d = PAD_GLYPH[name];
  if (!d) return "";
  return '<svg class="glyph" viewBox="0 0 24 24" aria-hidden="true">' + d + "</svg>";
}

var HINTS = {
  navUD:   { pad:["dpadUD"],   key:["↑","↓"],  label:"Move" },
  navLR:   { pad:["dpadLR"],   key:["←","→"],  label:"Choose" },
  open:    { pad:["cross"],    key:["↵"],      label:"Open" },
  select:  { pad:["cross"],    key:["↵"],      label:"Select" },
  confirm: { pad:["cross"],    key:["↵"],      label:"Confirm" },
  up:      { pad:["circle"],   key:["Esc"],    label:"Up" },
  back:    { pad:["circle"],   key:["Esc"],    label:"Back" },
  close:   { pad:["circle"],   key:["Esc"],    label:"Close" },
  cancel:  { pad:["circle"],   key:["Esc"],    label:"Cancel" },
  page:    { pad:["l1","r1"],  key:["PgUp","PgDn"], label:"Page" },
  mark:    { pad:["square"],   key:["X"],      label:"Mark" },
  options: { pad:["triangle"], key:["O"],      label:"Options" },
  stop:    { pad:["cross"],    key:["↵"],      label:"Stop" }
};

/* ═══ Transport ════════════════════════════════════════════════════════ */

var tx = {
  fn: null,
  name: "none",
  why: "no bridge detected",
  seq: 0,
  pending: {}
};

function detectTransport() {
  if (tx.fn) return tx.fn;

  /* 1. a plain injected function. The host may return a string, an object, or
        a promise of either; all three are normalised here. */
  if (typeof root.arcFileApi === "function") {
    tx.name = "arcFileApi";
    tx.why = "window.arcFileApi(json) is present";
    tx.fn = function (cmd) {
      return new Promise(function (resolve, reject) {
        var r;
        try { r = root.arcFileApi(JSON.stringify(cmd)); }
        catch (e) { reject(e); return; }
        if (r && typeof r.then === "function") r.then(function (v) { resolve(parseEnv(v)); }, reject);
        else resolve(parseEnv(r));
      });
    };
    return tx.fn;
  }

  /* 2. a WebView2 host object. AddHostObjectToScript proxies are async. */
  try {
    if (root.chrome && root.chrome.webview && root.chrome.webview.hostObjects &&
        root.chrome.webview.hostObjects.arcFiles) {
      tx.name = "hostObject";
      tx.why = "chrome.webview.hostObjects.arcFiles is present";
      tx.fn = function (cmd) {
        return Promise.resolve(root.chrome.webview.hostObjects.arcFiles.Handle(JSON.stringify(cmd)))
          .then(function (v) { return parseEnv(v); });
      };
      return tx.fn;
    }
  } catch (e) {}

  /* 3. postMessage with a correlation id. The host is expected to reply with
        {type:"fs.reply", reqId, ...envelope}. reqId, not id: FileApi commands
        take arguments of their own and the two must not collide. */
  try {
    if (root.chrome && root.chrome.webview && typeof root.chrome.webview.postMessage === "function") {
      tx.name = "postMessage";
      tx.why = "chrome.webview.postMessage is present; expects {type:\"fs.reply\",reqId,...}";
      root.chrome.webview.addEventListener("message", guard("fs.reply", function (ev) {
        var d = ev ? ev.data : null;
        if (typeof d === "string") { try { d = JSON.parse(d); } catch (e) { return; } }
        if (!d || d.type !== "fs.reply" || !d.reqId) return;
        var p = tx.pending[d.reqId];
        if (!p) return;
        delete tx.pending[d.reqId];
        clearTimeout(p.timer);
        p.resolve(d);
      }));
      tx.fn = function (cmd) {
        return new Promise(function (resolve, reject) {
          tx.seq++;
          var id = "fx-" + tx.seq;
          cmd.reqId = id;
          var timer = setTimeout(function () {
            delete tx.pending[id];
            reject(new Error("the host did not answer '" + cmd.cmd + "' within 40 seconds"));
          }, 40000);
          tx.pending[id] = { resolve: resolve, timer: timer };
          try { root.chrome.webview.postMessage(JSON.stringify({ type: "fs", reqId: id, command: cmd })); }
          catch (e) { clearTimeout(timer); delete tx.pending[id]; reject(e); }
        });
      };
      return tx.fn;
    }
  } catch (e) {}

  return null;
}

function parseEnv(v) {
  if (v === null || v === undefined) throw new Error("the file bridge returned nothing");
  if (typeof v === "string") {
    try { return JSON.parse(v); }
    catch (e) { throw new Error("the file bridge returned text that is not JSON: " + String(v).slice(0, 160)); }
  }
  return v;
}

/* One call. Resolves with data on ok, rejects with a readable Error otherwise —
   the whole point being that no caller ever has to look at an error code. */
function call(cmd) {
  var fn = tx.fn || detectTransport();
  if (!fn) {
    return Promise.reject(errorWith("no_bridge",
      "The file system bridge is not connected. The shell host has to expose FileApi to the page " +
      "before the explorer can read anything."));
  }
  return fn(cmd).then(function (env) {
    if (!env || typeof env !== "object") throw errorWith("bad_reply", "The file bridge sent something that is not a reply.");
    if (env.ok) return env.data || {};
    throw errorWith(env.error || "error", env.detail || describeCode(env.error), env.data);
  });
}

function errorWith(code, text, data) {
  var e = new Error(text || code);
  e.code = code;
  e.data = data;
  return e;
}

/* A last-resort translation, for the day a host answers with a code and no
   prose. FileApi always sends prose; this is belt and braces. */
var CODE_TEXT = {
  access_denied: "Access denied.",
  not_found: "That item no longer exists.",
  in_use: "The file is in use by another program.",
  disk_full: "There is not enough space on the destination drive.",
  exists: "Something with that name is already there.",
  write_protected: "The drive is write-protected.",
  not_ready: "The drive is not ready.",
  path_too_long: "The full path is too long for Windows to handle.",
  bad_name: "Windows will not accept that name.",
  not_empty: "The folder is not empty.",
  cancelled: "Cancelled.",
  refused_system: "That is part of Windows itself and the shell will not touch it.",
  refused_root: "A whole drive cannot be changed that way.",
  no_bridge: "The file system bridge is not connected."
};
function describeCode(c) { return CODE_TEXT[c] || ("Something went wrong (" + (c || "unknown") + ")."); }

/* ═══ State ════════════════════════════════════════════════════════════ */

var st = {
  open: false,
  el: null,
  cssChecked: false,
  view: "drives",          // drives | dir
  path: null,
  title: "Files",

  rows: [],                // the display model — see buildRows()
  tops: [],                // cumulative pixel offset per row
  totalH: 0,
  cursor: 0,
  scroll: 0,
  live: {},                // model index -> element, for the rendered window

  entries: [],
  listing: null,
  drives: null,
  places: null,

  sel: {},                 // path -> true
  sort: "name",
  desc: false,
  showHidden: false,

  clip: null,              // { mode:"copy"|"move", paths:[], label }
  find: "",

  scopes: [],
  job: null,
  jobTimer: 0,
  watchSeq: 0,
  watchTimer: 0,
  memo: {},                // path -> cursor index, so backing out lands where you left
  busy: 0,
  inFlight: 0,             // navigations the human asked for, still landing
  err: null,
  onExit: null,
  repeatTimer: 0,
  repeatAction: null,
  rowH: 54,
  navToken: 0
};

function remPx() {
  try {
    var v = parseFloat(getComputedStyle(document.documentElement).fontSize);
    return isFinite(v) && v > 0 ? v : 16;
  } catch (e) { return 16; }
}

/* Row heights, in rem, resolved against the live root scale. The virtual
   scroller never assumes them: it reads them here and builds a cumulative
   offset table, so a row type of a different height costs nothing. */
var ROW_REM = { header: 2.5, entry: 3.4, place: 3.4, drive: 7.6, note: 3.0 };

function rowHeight(kind) {
  return Math.round((ROW_REM[kind] || ROW_REM.entry) * remPx());
}

function reducedMotion() {
  try { return !!(root.matchMedia && root.matchMedia("(prefers-reduced-motion: reduce)").matches); }
  catch (e) { return false; }
}

/* ═══ Stylesheet ═══════════════════════════════════════════════════════ */

function ensureCSS(doc) {
  if (st.cssChecked) return;
  st.cssChecked = true;
  var probe = doc.createElement("div");
  probe.className = "arc-files-probe";
  doc.body.appendChild(probe);
  var loaded = false;
  try { loaded = getComputedStyle(probe).getPropertyValue("--fx-loaded").indexOf("1") >= 0; }
  catch (e) {}
  doc.body.removeChild(probe);
  if (loaded) return;

  var href = "files.css";
  try {
    var me = doc.currentScript;
    if (!me) {
      var all = doc.getElementsByTagName("script");
      for (var n = all.length - 1; n >= 0; n--) {
        if (all[n].src && all[n].src.indexOf("files.js") >= 0) { me = all[n]; break; }
      }
    }
    if (me && me.src) href = me.src.replace(/files\.js(\?.*)?$/, "files.css");
  } catch (e) {}

  var link = doc.createElement("link");
  link.rel = "stylesheet";
  link.href = href;
  link.setAttribute("data-arc-files", "auto");
  doc.head.appendChild(link);
  note("css", "files.css was not linked; injected <link href=\"" + href + "\">");
}

/* ═══ DOM ══════════════════════════════════════════════════════════════ */

function el(tag, cls, html) {
  var e = document.createElement(tag);
  if (cls) e.className = cls;
  if (html !== undefined && html !== null) e.innerHTML = html;
  return e;
}

function buildDOM() {
  var doc = document;
  ensureCSS(doc);

  var wrap = el("div", "arc-files");
  wrap.setAttribute("role", "dialog");
  wrap.setAttribute("aria-modal", "true");
  wrap.setAttribute("aria-label", "Files");
  wrap.setAttribute("aria-hidden", "true");

  var busy = el("div", "fx-busy");

  var head = el("div", "fx-head");
  var headMain = el("div", "fx-head-main");
  var eyebrow = el("div", "fx-eyebrow", "Files");
  var crumbs = el("div", "fx-crumbs");
  headMain.appendChild(eyebrow);
  headMain.appendChild(crumbs);
  var side = el("div", "fx-head-side");
  head.appendChild(headMain);
  head.appendChild(side);

  var body = el("div", "fx-body");
  var view = el("div", "fx-view");
  var canvas = el("div", "fx-canvas");
  view.appendChild(canvas);
  body.appendChild(view);

  var details = el("div", "fx-details");
  var detIco = el("div", "fx-det-ico", svg("info"));
  var detMain = el("div", "fx-det-main");
  var detName = el("div", "fx-det-name", "");
  var detSub = el("div", "fx-det-sub", "");
  detMain.appendChild(detName);
  detMain.appendChild(detSub);
  var detFacts = el("div", "fx-det-facts");
  details.appendChild(detIco);
  details.appendChild(detMain);
  details.appendChild(detFacts);

  var hints = el("div", "fx-hints");
  var scrim = el("div", "fx-scrim");
  var panel = el("div", "fx-panel");
  scrim.appendChild(panel);
  var toast = el("div", "fx-toast");

  wrap.appendChild(busy);
  wrap.appendChild(head);
  wrap.appendChild(body);
  wrap.appendChild(details);
  wrap.appendChild(hints);
  wrap.appendChild(scrim);
  wrap.appendChild(toast);
  doc.body.appendChild(wrap);

  st.el = {
    wrap: wrap, busy: busy, head: head, crumbs: crumbs, eyebrow: eyebrow, side: side,
    body: body, view: view, canvas: canvas,
    details: details, detIco: detIco, detName: detName, detSub: detSub, detFacts: detFacts,
    hints: hints, scrim: scrim, panel: panel, toast: toast
  };

  root.addEventListener("resize", guard("resize", function () {
    if (!st.open) return;
    measure();
    layout();
    render();
  }));
}

function measure() {
  st.rowH = rowHeight("entry");
}

/* ═══ Toast ════════════════════════════════════════════════════════════ */

var toastTimer = 0;
function toast(msg, bad) {
  try {
    var t = st.el.toast;
    t.textContent = msg;
    t.classList.toggle("is-bad", !!bad);
    t.classList.add("is-show");
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { t.classList.remove("is-show"); }, bad ? 7000 : 4200);
  } catch (e) {}
  note("toast", msg);
}

function busy(on) {
  st.busy += on ? 1 : -1;
  if (st.busy < 0) st.busy = 0;
  try { st.el.busy.classList.toggle("is-on", st.busy > 0); } catch (e) {}
}

/* ═══ The display model ════════════════════════════════════════════════
   One flat array of rows drives both views. Each row carries its own kind,
   which decides its height and its renderer; non-focusable rows (section
   headers) are skipped by movement. Keeping the two views in one model is
   what lets the virtual scroller, the cursor, the hint bar and the details
   strip all be written once. */

function buildRows() {
  var rows = [];

  if (st.view === "drives") {
    if (st.places && st.places.length) {
      rows.push({ kind: "header", label: "Places" });
      for (var i = 0; i < st.places.length; i++) {
        var p = st.places[i];
        rows.push({ kind: "place", focusable: true, label: p.label, path: p.path, icon: p.icon || "folder" });
      }
    }
    if (st.drives && st.drives.length) {
      var internal = [], removable = [], mounted = 0;
      for (var d = 0; d < st.drives.length; d++) {
        if (st.drives[d].removable) { removable.push(st.drives[d]); if (st.drives[d].ready) mounted++; }
        else internal.push(st.drives[d]);
      }

      if (removable.length) {
        rows.push({ kind: "header", label: "Removable" });
        for (var r = 0; r < removable.length; r++)
          rows.push({ kind: "drive", focusable: true, drive: removable[r] });
      }
      if (internal.length) {
        rows.push({ kind: "header", label: "This machine" });
        for (var n = 0; n < internal.length; n++)
          rows.push({ kind: "drive", focusable: true, drive: internal[n] });
      }
      /* An empty optical bay is not a removable drive you can use, so the
         note keys off what is actually mounted rather than off what has a
         drive letter. */
      if (!mounted) {
        rows.push({ kind: "note", label: "No removable drive is connected. Plug in a USB stick and this list refreshes on its own." });
      }
    }
  } else {
    for (var e = 0; e < st.entries.length; e++)
      rows.push({ kind: "entry", focusable: true, entry: st.entries[e] });
    if (!st.entries.length && !st.err)
      rows.push({ kind: "note", label: "This folder is empty." });
  }

  st.rows = rows;
  layout();
}

function layout() {
  var tops = new Array(st.rows.length);
  var y = 0;
  for (var i = 0; i < st.rows.length; i++) {
    tops[i] = y;
    y += rowHeight(st.rows[i].kind);
  }
  st.tops = tops;
  st.totalH = y;
  try { st.el.canvas.style.height = y + "px"; } catch (e) {}
}

function rowAtTop(i) { return st.tops[i] || 0; }
function rowBottom(i) { return rowAtTop(i) + rowHeight(st.rows[i].kind); }

function firstFocusable(from, dir) {
  var i = from;
  while (i >= 0 && i < st.rows.length) {
    if (st.rows[i].focusable) return i;
    i += dir;
  }
  return -1;
}

/* ═══ Virtual list ═════════════════════════════════════════════════════
   Only the rows inside the viewport (plus a small overscan) exist in the DOM.
   The canvas is the full height of everything and is translated rather than
   scrolled, so there is no scroll event to chase and no layout thrash: one
   transform per frame and a handful of absolutely-positioned children.

   Finding the window is a binary search over the cumulative offset table, so
   a folder with ten thousand entries costs the same as one with ten. */

var OVERSCAN = 4;

function findRowAt(y) {
  var lo = 0, hi = st.rows.length - 1, best = 0;
  while (lo <= hi) {
    var mid = (lo + hi) >> 1;
    if (st.tops[mid] <= y) { best = mid; lo = mid + 1; }
    else hi = mid - 1;
  }
  return best;
}

function viewportH() {
  try {
    var r = st.el.view.getBoundingClientRect();
    var pad = 2 * 0.5 * remPx();
    return Math.max(80, r.height - pad);
  } catch (e) { return 600; }
}

function clampScroll(v) {
  var max = Math.max(0, st.totalH - viewportH());
  return Math.max(0, Math.min(max, v));
}

/* Keep the cursor inside the viewport, with a row of lead-in above and below
   so the list never feels like it is scrolling only when you hit the edge. */
function ensureVisible() {
  if (!st.rows.length) return;
  var i = Math.max(0, Math.min(st.cursor, st.rows.length - 1));
  var top = rowAtTop(i), bot = rowBottom(i);
  var h = viewportH();
  var lead = Math.round(st.rowH * 0.9);
  if (top - lead < st.scroll) st.scroll = clampScroll(top - lead);
  else if (bot + lead > st.scroll + h) st.scroll = clampScroll(bot + lead - h);
}

function render() {
  if (!st.open || !st.el) return;
  var canvas = st.el.canvas;
  canvas.style.transform = "translateY(" + (-st.scroll) + "px)";

  var h = viewportH();
  var first = Math.max(0, findRowAt(st.scroll) - OVERSCAN);
  var last = Math.min(st.rows.length - 1, findRowAt(st.scroll + h) + OVERSCAN);
  if (!st.rows.length) { dropAll(); paintEmpty(); return; }
  clearEmpty();

  // Drop anything that fell out of the window.
  var keep = {};
  for (var i = first; i <= last; i++) keep[i] = true;
  for (var k in st.live) {
    if (!keep[k]) {
      try { st.live[k].remove(); } catch (e) {}
      delete st.live[k];
    }
  }

  // Create or refresh what is in it.
  for (var j = first; j <= last; j++) {
    var node = st.live[j];
    if (!node) {
      node = renderRow(st.rows[j], j);
      node.style.top = rowAtTop(j) + "px";
      canvas.appendChild(node);
      st.live[j] = node;
    }
    paintRowState(node, st.rows[j], j);
  }
}

function dropAll() {
  for (var k in st.live) {
    try { st.live[k].remove(); } catch (e) {}
  }
  st.live = {};
}

function paintRowState(node, row, i) {
  var focused = (i === st.cursor) && topScope().id === "list";
  var stale = (i === st.cursor) && topScope().id !== "list";
  if (focused) node.setAttribute("data-focus", "on");
  else if (stale) node.setAttribute("data-focus", "stale");
  else node.removeAttribute("data-focus");
  if (row.kind === "entry")
    node.classList.toggle("is-sel", !!st.sel[row.entry.path]);
}

function renderRow(row, i) {
  if (row.kind === "header") {
    var h = el("div", "fx-hdr-row");
    h.style.height = rowHeight("header") + "px";
    h.textContent = row.label;
    return h;
  }

  if (row.kind === "note") {
    var n = el("div", "fx-row fx-note-row");
    n.style.height = rowHeight("note") + "px";
    n.style.gridTemplateColumns = "2.6rem 1fr";
    n.innerHTML = '<div class="fx-ico">' + svg("info") + '</div><div class="fx-name"></div>';
    n.querySelector(".fx-name").textContent = row.label;
    return n;
  }

  if (row.kind === "place") {
    var p = el("div", "fx-row is-dir");
    p.style.height = rowHeight("place") + "px";
    p.innerHTML =
      '<span class="fx-tick"></span>' +
      '<div class="fx-ico">' + svg(row.icon) + "</div>" +
      '<div class="fx-name"></div>' +
      '<div class="fx-size"></div>' +
      '<div class="fx-when"></div>';
    p.querySelector(".fx-name").textContent = row.label;
    p.querySelector(".fx-when").textContent = row.path;
    p.querySelector(".fx-when").style.direction = "rtl";
    p.querySelector(".fx-when").style.overflow = "hidden";
    p.querySelector(".fx-when").style.textOverflow = "ellipsis";
    bindClick(p, i);
    return p;
  }

  if (row.kind === "drive") {
    var d = row.drive;
    var c = el("div", "fx-row fx-card-row");
    c.className = "fx-card" + (d.removable ? " is-removable" : "");
    c.style.position = "absolute";
    c.style.left = "0"; c.style.right = "0";
    c.style.height = (rowHeight("drive") - Math.round(0.55 * remPx())) + "px";
    var icon = d.driveType === "optical" ? "optical"
             : d.driveType === "network" ? "network"
             : d.removable ? "usb" : "disk";
    var pct = d.total > 0 ? Math.max(0, Math.min(100, d.percentUsed || 0)) : 0;
    var tight = pct >= 90 ? " is-full" : (pct >= 78 ? " is-tight" : "");
    c.innerHTML =
      '<div class="fx-card-top">' + svg(icon) +
        '<span class="fx-card-name"></span>' +
        (d.removable ? '<span class="fx-badge">Removable</span>' : "") +
      "</div>" +
      '<div class="fx-card-sub"></div>' +
      '<div class="fx-bar"><div class="fx-bar-fill' + tight + '"></div></div>' +
      '<div class="fx-card-free"><span class="fx-l"></span><span class="fx-r"></span></div>';
    c.querySelector(".fx-card-name").textContent = (d.display || d.letter) + "  (" + d.letter + ":)";
    c.querySelector(".fx-card-sub").textContent = d.ready
      ? [d.fileSystem || "unknown file system", d.busType, d.driveType].filter(Boolean).join(" · ")
      : (d.note || "Not ready");
    c.querySelector(".fx-bar-fill").style.width = (d.ready ? pct : 0) + "%";
    c.querySelector(".fx-l").textContent = d.ready ? (bytes(d.free) + " free") : "—";
    c.querySelector(".fx-r").textContent = d.ready ? ("of " + bytes(d.total)) : (d.note ? "" : "unavailable");
    bindClick(c, i);
    return c;
  }

  // ---- an ordinary directory entry ----
  var e = row.entry;
  var n2 = el("div", "fx-row" + (e.isDirectory ? " is-dir" : "") +
                    (e.attributes && (e.attributes.hidden || e.attributes.system) ? " is-hidden" : ""));
  n2.innerHTML =
    '<span class="fx-tick"></span>' +
    '<div class="fx-ico">' + svg(e.isDirectory ? "folder" : kindOf(e)) + "</div>" +
    '<div class="fx-name"></div>' +
    '<div class="fx-size"></div>' +
    '<div class="fx-when"></div>';
  n2.querySelector(".fx-name").textContent = e.name;
  n2.querySelector(".fx-size").textContent = e.isDirectory ? "" : bytes(e.size);
  n2.querySelector(".fx-when").textContent = when(e.modified);
  bindClick(n2, i);
  return n2;
}

/* A mouse is a debugging convenience, exactly as the keyboard is. Synthetic
   clicks (detail 0) are the pad path and must not double-fire. */
function bindClick(node, i) {
  node.addEventListener("click", guard("row click", function (ev) {
    if (ev.detail === 0) return;
    if (topScope().id !== "list") return;
    st.cursor = i;
    ensureVisible();
    render();
    paintDetails();
    activate();
  }));
}

/* Rebuilt every time rather than reused: an empty state that is already on
   screen when a SECOND folder fails would otherwise keep the first folder's
   message, which is the most misleading thing an error card can do. */
function paintEmpty() {
  clearEmpty();
  var loading = !st.err && st.inFlight > 0;
  var e = el("div", "fx-empty" + (st.err ? " is-error" : ""));
  e.innerHTML = svg(st.err ? "warn" : "empty") + "<b></b><span></span>";
  e.querySelector("b").textContent = st.err ? "This folder could not be opened"
                                   : loading ? "Reading…" : "Nothing here";
  e.querySelector("span").textContent = st.err ? st.err
                                      : loading ? "" : "The folder is empty.";
  st.el.view.appendChild(e);
}
function clearEmpty() {
  var e = st.el.view.querySelector(".fx-empty");
  if (e) e.remove();
}

/* ═══ Head, details strip, hint bar ════════════════════════════════════ */

function paintHead() {
  var c = st.el.crumbs;
  c.innerHTML = "";
  st.el.eyebrow.textContent = st.title;

  if (st.view === "drives") {
    var only = el("span", "fx-crumb is-last");
    only.textContent = "This machine";
    c.appendChild(only);
  } else {
    var parts = crumbParts(st.path);
    for (var i = 0; i < parts.length; i++) {
      if (i) {
        var sep = el("span", "fx-crumb-sep");
        sep.textContent = "›";
        c.appendChild(sep);
      }
      var s = el("span", "fx-crumb" + (i === parts.length - 1 ? " is-last" : ""));
      s.textContent = parts[i].label;
      c.appendChild(s);
    }
  }

  var side = st.el.side;
  side.innerHTML = "";

  if (st.clip) {
    var clip = el("div", "fx-chip is-warn");
    clip.innerHTML = svg(st.clip.mode === "move" ? "move" : "copy") + "<b></b>";
    clip.querySelector("b").textContent =
      st.clip.paths.length + (st.clip.paths.length === 1 ? " item " : " items ") +
      "ready to " + st.clip.mode;
    side.appendChild(clip);
  }

  var selN = selectedPaths().length;
  if (selN) {
    var sc = el("div", "fx-chip is-live");
    sc.innerHTML = "<b></b>";
    sc.querySelector("b").textContent = selN + " selected";
    side.appendChild(sc);
  }

  if (st.view === "dir" && st.listing) {
    var count = el("div", "fx-chip");
    count.innerHTML = "<b></b>";
    var d = st.listing.dirCount || 0, f = st.listing.fileCount || 0;
    count.querySelector("b").textContent =
      d + (d === 1 ? " folder" : " folders") + ", " + f + (f === 1 ? " file" : " files");
    side.appendChild(count);

    if (st.listing.hiddenPresent && !st.showHidden) {
      var hid = el("div", "fx-chip");
      hid.innerHTML = svg("eye") + "<b></b>";
      hid.querySelector("b").textContent = st.listing.hiddenPresent + " hidden";
      side.appendChild(hid);
    }
  }
}

function crumbParts(path) {
  if (!path) return [{ label: "", path: "" }];
  var norm = path.replace(/\\+$/, "");
  var bits = norm.split("\\");
  var out = [], acc = "";
  for (var i = 0; i < bits.length; i++) {
    if (!bits[i]) continue;
    /* The drive root keeps its trailing separator ("C:\" is a path, "C:" is a
       drive-relative reference and means something else entirely); every
       segment after it must not double it up. */
    if (!acc) acc = bits[i] + "\\";
    else acc = acc.replace(/\\+$/, "") + "\\" + bits[i];
    out.push({ label: bits[i], path: acc });
  }
  if (!out.length) out.push({ label: norm, path: norm });
  return out;
}

function currentRow() {
  if (st.cursor < 0 || st.cursor >= st.rows.length) return null;
  return st.rows[st.cursor];
}
function currentEntry() {
  var r = currentRow();
  return r && r.kind === "entry" ? r.entry : null;
}

function paintDetails() {
  var e = st.el;
  var row = currentRow();

  if (st.err) {
    e.detIco.innerHTML = svg("warn");
    e.detIco.style.color = "var(--fx-warn)";
    e.detName.textContent = "Could not read this folder";
    e.detSub.textContent = st.err;
    e.detSub.classList.add("is-error");
    e.detFacts.innerHTML = "";
    return;
  }
  e.detIco.style.color = "";
  e.detSub.classList.remove("is-error");

  if (!row) {
    e.detIco.innerHTML = svg("info");
    e.detName.textContent = "—";
    e.detSub.textContent = "";
    e.detFacts.innerHTML = "";
    return;
  }

  if (row.kind === "drive") {
    var d = row.drive;
    e.detIco.innerHTML = svg(d.removable ? "usb" : "disk");
    e.detName.textContent = (d.display || "") + "  (" + d.letter + ":)";
    e.detSub.textContent = d.ready
      ? [d.fileSystem, d.busType + " bus", d.driveType, d.recycleBin ? "has a Recycle Bin" : "no Recycle Bin"].filter(Boolean).join(" · ")
      : (d.note || "The drive is not ready.");
    e.detFacts.innerHTML = "";
    if (d.ready) {
      facts(e.detFacts, [["Free", bytes(d.free)], ["Total", bytes(d.total)], ["Used", (d.percentUsed || 0) + "%"]]);
    }
    return;
  }

  if (row.kind === "place") {
    e.detIco.innerHTML = svg(row.icon);
    e.detName.textContent = row.label;
    e.detSub.textContent = row.path;
    e.detFacts.innerHTML = "";
    return;
  }

  if (row.kind === "entry") {
    var en = row.entry;
    e.detIco.innerHTML = svg(en.isDirectory ? "folder" : kindOf(en));
    e.detName.textContent = en.name;
    var a = en.attributes || {};
    var tags = [];
    if (a.hidden) tags.push("hidden");
    if (a.system) tags.push("system");
    if (a.readonly) tags.push("read-only");
    if (a.reparse) tags.push("link");
    if (a.compressed) tags.push("compressed");
    if (a.encrypted) tags.push("encrypted");
    e.detSub.textContent = typeLabel(en) + (tags.length ? "  ·  " + tags.join(", ") : "") + "  ·  " + en.path;
    e.detFacts.innerHTML = "";
    facts(e.detFacts, [
      ["Size", en.isDirectory ? "—" : bytes(en.size)],
      ["Modified", when(en.modified)]
    ]);
    return;
  }

  e.detIco.innerHTML = svg("info");
  e.detName.textContent = row.label || "";
  e.detSub.textContent = "";
  e.detFacts.innerHTML = "";
}

function facts(host, pairs) {
  for (var i = 0; i < pairs.length; i++) {
    var f = el("div", "fx-fact");
    var s = el("span"); s.textContent = pairs[i][0];
    var b = el("b"); b.textContent = pairs[i][1];
    f.appendChild(s); f.appendChild(b);
    host.appendChild(f);
  }
}

/* The hint bar renders from the top scope, so it is right by construction
   rather than by maintenance — the same rule index.html follows. */
var inputDevice = "pad";
function setDevice(d) {
  if (d === inputDevice) return;
  inputDevice = d;
  paintHints();
}

function paintHints() {
  try {
    var sc = topScope();
    var ids = (sc && sc.hints) || [];
    var out = "";
    for (var i = 0; i < ids.length; i++) {
      var h = HINTS[ids[i]];
      if (!h) continue;
      var marks = "";
      if (inputDevice === "pad") {
        for (var g = 0; g < h.pad.length; g++) marks += glyphSVG(h.pad[g]);
      } else {
        for (var k = 0; k < h.key.length; k++) marks += '<span class="fx-key">' + h.key[k] + "</span>";
      }
      if (!marks) continue;
      out += '<span class="fx-hint"><span class="fx-hint-glyphs">' + marks + "</span>" + h.label + "</span>";
    }
    st.el.hints.innerHTML = out;
  } catch (err) { report("hints", err); }
}

/* ═══ Focus scopes ═════════════════════════════════════════════════════
   A stack, exactly like the shell's. The base scope is the list, which is
   virtual and therefore indexes a model rather than DOM nodes. Everything
   above it is an ordinary grid of [data-nav] elements inside a panel, so
   panels can be written as plain markup. */

/* The base scope's hints are computed rather than fixed, because what the
   buttons do genuinely differs between the picker and a folder: Circle leaves
   the explorer at the root and goes up a level anywhere else, and there is
   nothing to mark or page through in a list of four drives. */
function baseScope() {
  return {
    id: "list",
    kind: "list",
    get hints() {
      if (st.view === "drives") return ["navUD", "open", "options", "close"];
      return ["navUD", "open", "up", "page", "mark", "options"];
    }
  };
}

function topScope() {
  return st.scopes.length ? st.scopes[st.scopes.length - 1] : { id: "none", kind: "list", hints: [] };
}

function pushScope(sc) {
  sc.items = sc.items || [];
  sc.index = sc.index || 0;
  st.scopes.push(sc);
  note("scope", "push " + sc.id + " (depth " + st.scopes.length + ")");
  repaintScope();
}

function popScope() {
  if (st.scopes.length <= 1) return false;
  var sc = st.scopes.pop();
  try { if (sc.onPop) sc.onPop(); } catch (e) { report("scope " + sc.id + " onPop", e); }
  note("scope", "pop " + sc.id + " -> " + topScope().id);
  repaintScope();
  return true;
}

function popTo(id) {
  var g = 0;
  while (st.scopes.length > 1 && topScope().id !== id && g++ < 12) popScope();
}

function collect(rootEl) {
  var out = [];
  var list = rootEl.querySelectorAll("[data-nav]");
  for (var i = 0; i < list.length; i++) {
    var e = list[i];
    var raw = (e.getAttribute("data-nav") || "").trim();
    var r = NaN, c = NaN;
    if (raw && raw !== "auto") {
      var p = raw.split(",");
      r = parseFloat(p[0]); c = parseFloat(p[1]);
    }
    if (!isFinite(r) || !isFinite(c)) {
      var b = e.getBoundingClientRect();
      r = Math.round(b.top / Math.max(8, b.height || 8));
      c = Math.round(b.left / 8);
    }
    out.push({ el: e, r: r, c: c });
  }
  out.sort(function (a, b) { return (a.r - b.r) || (a.c - b.c); });
  return out;
}

function scopeMove(sc, dx, dy) {
  var l = [];
  for (var i = 0; i < sc.items.length; i++)
    if (sc.items[i].el.getAttribute("aria-disabled") !== "true") l.push(sc.items[i]);
  if (!l.length) return false;
  var cur = sc.items[sc.index] || l[0];
  var best = null, bestD = Infinity, it, d;

  if (dx) {
    for (var a = 0; a < l.length; a++) {
      it = l[a];
      if (it === cur || it.r !== cur.r) continue;
      d = it.c - cur.c;
      if ((d > 0) !== (dx > 0)) continue;
      if (Math.abs(d) < bestD) { bestD = Math.abs(d); best = it; }
    }
  } else {
    var row = null;
    for (var b = 0; b < l.length; b++) {
      it = l[b];
      d = it.r - cur.r;
      if (d === 0 || (d > 0) !== (dy > 0)) continue;
      if (row === null || Math.abs(d) < Math.abs(row - cur.r)) row = it.r;
    }
    if (row !== null) {
      for (var c2 = 0; c2 < l.length; c2++) {
        it = l[c2];
        if (it.r !== row) continue;
        d = Math.abs(it.c - cur.c);
        if (d < bestD) { bestD = d; best = it; }
      }
    }
  }
  if (!best) return false;
  for (var k = 0; k < sc.items.length; k++) if (sc.items[k] === best) sc.index = k;
  repaintScope();
  return true;
}

function repaintScope() {
  try {
    var sc = topScope();
    for (var s = 0; s < st.scopes.length; s++) {
      var scope = st.scopes[s];
      if (scope.kind !== "grid") continue;
      for (var i = 0; i < scope.items.length; i++) {
        var e = scope.items[i].el;
        if (i === scope.index) e.setAttribute("data-focus", s === st.scopes.length - 1 ? "on" : "stale");
        else e.removeAttribute("data-focus");
      }
    }
    if (sc.kind === "grid") {
      var cur = sc.items[sc.index];
      if (cur) {
        try { cur.el.focus({ preventScroll: true }); } catch (e2) {}
        try { cur.el.scrollIntoView({ block: "nearest" }); } catch (e3) {}
      }
      if (sc.onFocus) sc.onFocus(cur);
    }
    render();
    paintHints();
  } catch (err) { report("repaintScope", err); }
}

/* ═══ Panels ═══════════════════════════════════════════════════════════ */

function openPanel(opts) {
  var p = st.el.panel;
  p.innerHTML = "";
  st.el.scrim.classList.toggle("is-menu", !!opts.menu);

  if (opts.eyebrow) {
    var eb = el("div", "fx-panel-eyebrow");
    eb.textContent = opts.eyebrow;
    p.appendChild(eb);
  }
  if (opts.title) {
    var t = el("h2", "fx-panel-title");
    t.textContent = opts.title;
    p.appendChild(t);
  }
  if (opts.sub) {
    var s = el("p", "fx-panel-sub" + (opts.danger ? " is-danger" : ""));
    s.textContent = opts.sub;
    p.appendChild(s);
  }
  var bodyEl = el("div", "fx-panel-body");
  p.appendChild(bodyEl);
  if (opts.build) opts.build(bodyEl, p);

  st.el.scrim.classList.add("is-open");
  st.el.scrim.setAttribute("aria-hidden", "false");

  var items = collect(p);
  /* Land on the first thing that can actually be pressed. A menu whose top
     row is greyed out must not open with the cursor sitting on it. */
  var start = opts.index || 0;
  while (start < items.length && items[start].el.getAttribute("aria-disabled") === "true") start++;
  if (start >= items.length) start = 0;

  var sc = {
    id: opts.id,
    kind: "grid",
    items: items,
    index: start,
    hints: opts.hints || ["navUD", "select", "close"],
    panel: p,
    onFocus: opts.onFocus,
    onBack: opts.onBack,
    onPop: function () {
      if (!hasPanelScope()) {
        st.el.scrim.classList.remove("is-open");
        st.el.scrim.setAttribute("aria-hidden", "true");
      }
      if (opts.onClose) opts.onClose();
    }
  };
  pushScope(sc);
  return sc;
}

function hasPanelScope() {
  for (var i = 0; i < st.scopes.length; i++) if (st.scopes[i].kind === "grid") return true;
  return false;
}

function menuItem(o, r, c) {
  var b = el("button", "fx-item" + (o.tone ? " is-" + o.tone : ""));
  b.setAttribute("data-nav", r + "," + (c || 0));
  b.dataset.act = o.act || "noop";
  if (o.arg) b.dataset.arg = o.arg;
  if (o.disabled) b.setAttribute("aria-disabled", "true");
  b.innerHTML =
    "<span>" + svg(o.icon || "info") + "</span>" +
    "<span><b></b>" + (o.sub ? "<small></small>" : "") + "</span>" +
    (o.tail ? '<span class="fx-item-tail"></span>' : "<span></span>");
  b.querySelector("b").textContent = o.label;
  if (o.sub) b.querySelector("small").textContent = o.sub;
  if (o.tail) b.querySelector(".fx-item-tail").textContent = o.tail;
  b.addEventListener("click", guard("menu click", function (ev) {
    if (ev.detail === 0) return;
    setDevice("key");
    var sc = topScope();
    for (var i = 0; i < sc.items.length; i++) if (sc.items[i].el === b) sc.index = i;
    repaintScope();
    activate();
  }));
  return b;
}

function btn(label, o, r, c) {
  var b = el("button", "fx-btn" + (o.tone ? " is-" + o.tone : ""));
  b.textContent = label;
  b.setAttribute("data-nav", r + "," + c);
  b.dataset.act = o.act || "noop";
  if (o.arg) b.dataset.arg = o.arg;
  b.addEventListener("click", guard("btn click", function (ev) {
    if (ev.detail === 0) return;
    setDevice("key");
    var sc = topScope();
    for (var i = 0; i < sc.items.length; i++) if (sc.items[i].el === b) sc.index = i;
    repaintScope();
    activate();
  }));
  return b;
}

/* ═══ Navigation ═══════════════════════════════════════════════════════ */

function selectedPaths() {
  var out = [];
  for (var k in st.sel) if (st.sel[k]) out.push(k);
  return out;
}

/* What an operation acts on: the marked set if there is one, otherwise the
   row under the cursor. Making that a single function is what keeps the
   context menu, the confirmations and the jobs from disagreeing. */
function targets() {
  var sel = selectedPaths();
  if (sel.length) {
    var rowsOut = [];
    for (var i = 0; i < st.entries.length; i++)
      if (st.sel[st.entries[i].path]) rowsOut.push(st.entries[i]);
    return rowsOut.length ? rowsOut : [];
  }
  var e = currentEntry();
  return e ? [e] : [];
}

function targetSummary(list) {
  var files = 0, dirs = 0, size = 0, unknown = false;
  for (var i = 0; i < list.length; i++) {
    if (list[i].isDirectory) { dirs++; unknown = true; }
    else { files++; size += (list[i].size || 0); }
  }
  var what = [];
  if (dirs) what.push(dirs + (dirs === 1 ? " folder" : " folders"));
  if (files) what.push(files + (files === 1 ? " file" : " files"));
  return {
    count: list.length,
    text: what.join(" and "),
    size: size,
    sizeText: unknown
      ? (files ? bytes(size) + " of files, plus whatever is inside the folder" + (dirs === 1 ? "" : "s")
               : "an unknown amount — the folder contents have not been measured")
      : bytes(size)
  };
}

function goTo(path, remember) {
  if (remember && st.view === "dir" && st.path) st.memo[st.path] = st.cursor;
  st.err = null;
  st.sel = {};
  st.find = "";
  busy(true);
  st.inFlight++;
  var tok = ++st.navToken;
  return call({ cmd: "fs.list", path: path, sort: st.sort, desc: st.desc, showHidden: st.showHidden, refresh: true })
    .then(guard("goTo ok", function (data) {
      if (tok !== st.navToken) return;                 // a later navigation won
      st.view = "dir";
      st.path = data.path;
      st.listing = data;
      st.entries = data.entries || [];
      buildRows();
      var memo = st.memo[st.path];
      st.cursor = (typeof memo === "number" && memo < st.rows.length) ? memo : firstFocusable(0, 1);
      if (st.cursor < 0) st.cursor = 0;
      st.scroll = 0;
      ensureVisible();
      dropAll();
      paintAll();
      watchHere();
    }))
    .catch(guard("goTo fail", function (err) {
      if (tok !== st.navToken) return;
      st.err = err && err.message ? err.message : String(err);
      st.view = "dir";
      st.path = path;
      st.listing = null;
      st.entries = [];
      buildRows();
      dropAll();
      paintAll();
      toast(st.err, true);
    }))
    .then(function () { busy(false); st.inFlight--; });
}

/* The view does not change until the data lands, exactly as goTo() works.
   Flipping st.view first would leave the state machine claiming to be at the
   drive picker while the previous folder's rows are still on screen, and
   anything that asked "are we at the picker yet?" would get a yes too early. */
function goDrives() {
  st.err = null;
  st.sel = {};
  busy(true);
  st.inFlight++;
  var tok = ++st.navToken;
  return Promise.all([
    call({ cmd: "fs.drives" }).catch(function (e) { return { drives: [], _err: e }; }),
    call({ cmd: "fs.home" }).catch(function () { return { places: [] }; })
  ]).then(guard("drives ok", function (r) {
    if (tok !== st.navToken) return;
    st.view = "drives";
    st.path = null;
    st.listing = null;
    st.drives = r[0].drives || [];
    st.places = r[1].places || [];
    if (r[0]._err) st.err = r[0]._err.message;
    buildRows();
    st.cursor = firstFocusable(0, 1);
    if (st.cursor < 0) st.cursor = 0;
    st.scroll = 0;
    dropAll();
    paintAll();
    watchDrives();
  })).catch(guard("drives fail", function (err) {
    st.err = err && err.message ? err.message : String(err);
    st.view = "drives";
    st.path = null;
    st.listing = null;
    st.drives = []; st.places = [];
    buildRows();
    dropAll();
    paintAll();
  })).then(function () { busy(false); st.inFlight--; });
}

function paintAll() {
  paintHead();
  render();
  paintDetails();
  paintHints();
}

function up() {
  if (st.view === "drives") return false;
  var parent = st.listing ? st.listing.parent : null;
  if (!parent && st.path) {
    var parts = crumbParts(st.path);
    parent = parts.length > 1 ? parts[parts.length - 2].path : null;
  }
  if (parent) { goTo(parent, true); return true; }
  goDrives();
  return true;
}

function activate() {
  var sc = topScope();
  if (sc.kind === "grid") {
    var it = sc.items[sc.index];
    if (!it) return;
    if (it.el.getAttribute("aria-disabled") === "true") return;
    var act = it.el.dataset.act;
    if (act && ACT[act]) ACT[act](it.el);
    return;
  }

  var row = currentRow();
  if (!row) return;
  if (row.kind === "place") { goTo(row.path, true); return; }
  if (row.kind === "drive") {
    if (!row.drive.ready) {
      toast("Drive " + row.drive.letter + ": is not ready. " + (row.drive.note || ""), true);
      return;
    }
    goTo(row.drive.path, true);
    return;
  }
  if (row.kind === "entry") {
    var e = row.entry;
    if (e.isDirectory) { goTo(e.path, true); return; }
    openFile(e);
  }
}

function openFile(e) {
  busy(true);
  call({ cmd: "fs.open", path: e.path })
    .then(guard("open ok", function (d) {
      if (d.pid) toast("Opened " + e.name + " — " + (d.process || "process") + " (pid " + d.pid + ")");
      else toast("Opened " + e.name + ". " + (d.note || ""));
    }))
    .catch(guard("open fail", function (err) { toast(err.message, true); }))
    .then(function () { busy(false); });
}

function moveCursor(delta) {
  if (!st.rows.length) return;
  var i = st.cursor;
  var dir = delta > 0 ? 1 : -1;
  var steps = Math.abs(delta);
  while (steps-- > 0) {
    var j = i + dir;
    while (j >= 0 && j < st.rows.length && !st.rows[j].focusable) j += dir;
    if (j < 0 || j >= st.rows.length) break;
    i = j;
  }
  if (i === st.cursor) return;
  st.cursor = i;
  ensureVisible();
  render();
  paintDetails();
}

function pageBy(dir) {
  var rowsPerPage = Math.max(1, Math.floor(viewportH() / st.rowH) - 1);
  moveCursor(dir * rowsPerPage);
}

function toggleMark() {
  var e = currentEntry();
  if (!e) return;
  if (st.sel[e.path]) delete st.sel[e.path];
  else st.sel[e.path] = true;
  render();
  paintHead();
  paintDetails();
}

/* ═══ Context menu ═════════════════════════════════════════════════════ */

function openMenu() {
  var sel = targets();
  var one = sel.length === 1 ? sel[0] : null;
  var row = currentRow();
  var drive = row && row.kind === "drive" ? row.drive : null;
  var inDir = st.view === "dir";
  var sum = targetSummary(sel);

  openPanel({
    id: "menu",
    menu: true,
    eyebrow: "Options",
    title: drive ? (drive.display + " (" + drive.letter + ":)")
         : sel.length > 1 ? (sel.length + " items")
         : one ? one.name
         : (st.view === "dir" ? "This folder" : "Files"),
    sub: sel.length > 1 ? (sum.text + " · " + sum.sizeText) : null,
    hints: ["navUD", "select", "close"],
    build: function (bodyEl) {
      var r = 0, items = [];

      if (drive) {
        items.push({ label: "Open", sub: drive.ready ? drive.path : "Not ready", icon: "open", act: "menuOpen", disabled: !drive.ready });
        items.push({
          label: "Eject", icon: "eject", tone: "danger", act: "menuEject", arg: drive.letter,
          sub: drive.removable ? "Make it safe to unplug" : "Only removable drives can be ejected",
          disabled: !drive.removable
        });
        items.push({ label: "Refresh drives", icon: "refresh", act: "menuRefresh" });
      } else if (inDir) {
        if (one) items.push({ label: one.isDirectory ? "Open folder" : "Open", icon: "open", act: "menuOpen" });
        items.push({
          label: "Copy", icon: "copy", act: "menuClip", arg: "copy", disabled: !sel.length,
          sub: sel.length ? ("Mark " + sum.text + " to paste elsewhere") : "Nothing is selected"
        });
        items.push({
          label: "Move", icon: "move", act: "menuClip", arg: "move", disabled: !sel.length,
          sub: sel.length ? ("Mark " + sum.text + " to move elsewhere") : "Nothing is selected"
        });
        if (st.clip) {
          items.push({
            label: "Paste here", icon: "paste", act: "menuPaste",
            sub: st.clip.paths.length + " item" + (st.clip.paths.length === 1 ? "" : "s") +
                 " to " + st.clip.mode + " into this folder"
          });
        }
        items.push({ label: "Rename", icon: "rename", act: "menuRename", disabled: !one, sub: one ? one.name : "Select exactly one item" });
        items.push({
          label: "Delete", icon: "trash", tone: "danger", act: "menuDelete", arg: "recycle",
          disabled: !sel.length, sub: "Move to the Recycle Bin"
        });
        items.push({
          label: "Delete permanently", icon: "nuke", tone: "nuke", act: "menuDelete", arg: "permanent",
          disabled: !sel.length, sub: "Cannot be undone"
        });
        items.push({ label: "New folder", icon: "newdir", act: "menuNewFolder" });
        items.push({ label: "Find in this folder", icon: "search", act: "menuFind", tail: st.find || "" });
        items.push({ label: "Sort by", icon: "sort", act: "menuSort", tail: sortLabel() });
        items.push({ label: st.showHidden ? "Hide hidden items" : "Show hidden items", icon: "eye", act: "menuHidden" });
        items.push({ label: "Select all", icon: "info", act: "menuSelectAll", disabled: !st.entries.length });
        if (sel.length) items.push({ label: "Clear selection", icon: "info", act: "menuClearSel" });
        items.push({ label: "Properties", icon: "info", act: "menuProps", disabled: !one });
        items.push({ label: "Refresh", icon: "refresh", act: "menuRefresh" });
      } else {
        items.push({ label: "Refresh drives", icon: "refresh", act: "menuRefresh" });
      }

      for (var i = 0; i < items.length; i++) bodyEl.appendChild(menuItem(items[i], r++, 0));
    }
  });
}

function sortLabel() {
  var n = { name: "Name", size: "Size", modified: "Date", type: "Type", created: "Created" }[st.sort] || st.sort;
  return n + (st.desc ? " ↓" : " ↑");
}

/* ═══ Confirmation ═════════════════════════════════════════════════════
   Cancel is always column 0 and always focused: the safe answer is the one
   already selected, and confirming is a deliberate move sideways. A permanent
   delete puts a third button between Cancel and the destructive one, so the
   journey to it is two moves and passes an offer to recycle instead. */

function confirmDelete(mode) {
  var list = targets();
  if (!list.length) { toast("Nothing is selected."); return; }
  var sum = targetSummary(list);
  var permanent = mode === "permanent";
  var names = [];
  for (var i = 0; i < Math.min(list.length, 6); i++) names.push(list[i].name);
  if (list.length > 6) names.push("and " + (list.length - 6) + " more");

  openPanel({
    id: "confirm",
    eyebrow: permanent ? "Permanent delete" : "Delete",
    title: permanent
      ? ("Permanently delete " + sum.count + (sum.count === 1 ? " item?" : " items?"))
      : ("Move " + sum.count + (sum.count === 1 ? " item" : " items") + " to the Recycle Bin?"),
    sub: sum.text + " · " + sum.sizeText + ".  " +
         (permanent
           ? "This does NOT go to the Recycle Bin. Once it is gone there is no way to get it back."
           : "You can put them back from the Recycle Bin afterwards."),
    danger: permanent,
    hints: ["navLR", "confirm", "cancel"],
    build: function (bodyEl) {
      var f = el("div", "fx-facts");
      for (var i = 0; i < names.length; i++) {
        var d = el("div", "fx-f");
        var s = el("span"); s.textContent = i < list.length ? typeLabel(list[i]) : "";
        var b = el("b"); b.textContent = names[i];
        d.appendChild(s); d.appendChild(b);
        f.appendChild(d);
      }
      bodyEl.appendChild(f);

      var acts = el("div", "fx-actions");
      acts.appendChild(btn("Cancel", { act: "back" }, 0, 0));
      if (permanent) {
        acts.appendChild(btn("Use the Recycle Bin instead", { act: "doDelete", arg: "recycle", tone: "danger" }, 0, 1));
        acts.appendChild(btn("Delete permanently", { act: "doDelete", arg: "permanent", tone: "nuke" }, 0, 2));
      } else {
        acts.appendChild(btn("Move to Recycle Bin", { act: "doDelete", arg: "recycle", tone: "danger" }, 0, 1));
      }
      bodyEl.appendChild(acts);
    },
    index: 0
  });
}

/* ═══ Progress ═════════════════════════════════════════════════════════ */

function watchJob(jobId, title, onDone) {
  st.job = { id: jobId, title: title, done: false };

  var fill, pctEl, doneEl, rateEl, etaEl, fileEl, probEl, cancelBtn;

  openPanel({
    id: "progress",
    eyebrow: "Working",
    title: title,
    sub: "Cancelling stops it where it is; nothing already finished is undone.",
    hints: ["stop"],
    build: function (bodyEl) {
      var p = el("div", "fx-prog");
      p.innerHTML =
        '<div class="fx-prog-bar"><div class="fx-prog-fill is-indet"></div></div>' +
        '<div class="fx-prog-line"><span class="fx-pl"></span><span class="fx-pr"></span></div>' +
        '<div class="fx-prog-file"></div>';
      bodyEl.appendChild(p);
      fill = p.querySelector(".fx-prog-fill");
      doneEl = p.querySelector(".fx-pl");
      rateEl = p.querySelector(".fx-pr");
      fileEl = p.querySelector(".fx-prog-file");
      probEl = el("div", "fx-problems");
      probEl.style.display = "none";
      bodyEl.appendChild(probEl);

      var acts = el("div", "fx-actions");
      cancelBtn = btn("Cancel", { act: "cancelJob", tone: "danger" }, 0, 0);
      acts.appendChild(cancelBtn);
      bodyEl.appendChild(acts);
    },
    onBack: function () {
      // Circle on a running job means cancel, not "walk away and leave it".
      ACT.cancelJob();
      return false;
    },
    onClose: function () {
      clearTimeout(st.jobTimer);
      st.jobTimer = 0;
      st.job = null;
    }
  });

  function tick() {
    call({ cmd: "job.status", jobId: jobId })
      .then(guard("job tick", function (j) {
        if (!st.job || st.job.id !== jobId) return;

        var indet = j.percent < 0;
        fill.classList.toggle("is-indet", indet);
        if (!indet) fill.style.width = j.percent + "%";
        else fill.style.width = "";

        doneEl.textContent = indet
          ? (j.phase || "working") + "…"
          : (bytes(j.bytesDone) + " of " + bytes(j.bytesTotal) + "   ·   " + j.percent + "%");
        rateEl.textContent = j.bytesPerSec
          ? (rate(j.bytesPerSec) + "   ·   " + eta(j.etaSec) + " left")
          : (j.itemsTotal ? (j.itemsDone + " / " + j.itemsTotal + " items") : "");
        fileEl.textContent = j.current || "";

        if (j.problems && j.problems.length) {
          probEl.style.display = "";
          probEl.innerHTML = "";
          for (var i = 0; i < j.problems.length; i++) {
            var d = el("div");
            d.textContent = j.problems[i];
            probEl.appendChild(d);
          }
        }

        if (j.state === "running") {
          st.jobTimer = setTimeout(tick, 250);
          return;
        }

        // finished, one way or another
        popTo("list");
        if (j.state === "done") {
          var r = j.result || {};
          var extra = r.failed ? ("  " + r.failed + " item(s) could not be done — see the log.") : "";
          toast(title + " finished." + extra, !!r.failed);
        } else if (j.state === "cancelled") {
          toast(title + " cancelled after " + bytes(j.bytesDone) + ".");
        } else {
          toast(title + " failed: " + (j.detail || j.error || "unknown"), true);
        }
        if (onDone) onDone(j);
      }))
      .catch(guard("job tick fail", function (err) {
        popTo("list");
        toast("Lost track of the job: " + err.message, true);
      }));
  }
  tick();
}

/* ═══ Properties ═══════════════════════════════════════════════════════ */

function openProps() {
  var e = currentEntry();
  if (!e) { toast("Nothing is selected."); return; }
  busy(true);
  call({ cmd: "fs.properties", path: e.path, folderSize: !!e.isDirectory })
    .then(guard("props ok", function (d) {
      var sizeRowB = null;
      openPanel({
        id: "props",
        eyebrow: "Properties",
        title: d.name,
        sub: d.path,
        hints: ["back"],
        build: function (bodyEl) {
          var f = el("div", "fx-facts");
          var pairs = [
            ["Type", d.isDirectory ? "Folder" : typeLabel({ isDirectory: false, ext: d.ext })],
            ["Location", d.parent || "—"],
            ["Size", d.isDirectory ? "measuring…" : bytes(d.size)],
            ["Size on disk", d.sizeOnDisk === null || d.sizeOnDisk === undefined ? "—" : bytes(d.sizeOnDisk)],
            ["Created", when(d.created)],
            ["Modified", when(d.modified)],
            ["Accessed", when(d.accessed)],
            ["Attributes", attrText(d.attributes)],
            ["Drive", d.driveRoot || "—"],
            ["Protected by Windows", d.protected ? "Yes" : "No"]
          ];
          for (var i = 0; i < pairs.length; i++) {
            var row = el("div", "fx-f");
            var s = el("span"); s.textContent = pairs[i][0];
            var b = el("b"); b.textContent = pairs[i][1];
            row.appendChild(s); row.appendChild(b);
            f.appendChild(row);
            if (pairs[i][0] === "Size") sizeRowB = b;
          }
          bodyEl.appendChild(f);
          var acts = el("div", "fx-actions");
          acts.appendChild(btn("Close", { act: "back" }, 0, 0));
          bodyEl.appendChild(acts);
        }
      });

      // The folder walk is a job, so the card opens instantly and the number
      // lands when it lands rather than the card waiting for it.
      if (d.sizeJob && d.sizeJob.jobId) pollFolderSize(d.sizeJob.jobId, sizeRowB);
    }))
    .catch(guard("props fail", function (err) { toast(err.message, true); }))
    .then(function () { busy(false); });
}

function pollFolderSize(jobId, target) {
  function tick() {
    call({ cmd: "job.status", jobId: jobId }).then(guard("size tick", function (j) {
      if (!target || !target.isConnected) return;             // the card closed
      if (j.state === "running") {
        target.textContent = "measuring… " + (j.itemsDone || 0) + " items";
        setTimeout(tick, 400);
        return;
      }
      if (j.state === "done" && j.result) {
        target.textContent = bytes(j.result.bytes) + "  (" + j.result.files + " files, " + j.result.folders + " folders)";
      } else {
        target.textContent = "could not be measured";
      }
    })).catch(function () { if (target) target.textContent = "could not be measured"; });
  }
  tick();
}

function attrText(a) {
  if (!a) return "—";
  var out = [];
  if (a.hidden) out.push("hidden");
  if (a.system) out.push("system");
  if (a.readonly) out.push("read-only");
  if (a.archive) out.push("archive");
  if (a.reparse) out.push("link");
  if (a.compressed) out.push("compressed");
  if (a.encrypted) out.push("encrypted");
  if (a.sparse) out.push("sparse");
  return out.length ? out.join(", ") : "none";
}

/* ═══ Watching ═════════════════════════════════════════════════════════
   One watcher on the folder in view, plus a permanent one on the drive list
   so a USB stick appearing refreshes the picker without anyone pressing
   anything. Events are polled rather than pushed because the page cannot
   receive a callback from a FileSystemWatcher — the poll is one tiny command
   a second, and it stops the moment the explorer closes. */

/* Chained, not fired in parallel: an unwatch that landed after its watch would
   silently switch the watcher off and the view would stop refreshing. */
function rewatch(cmds) {
  var chain = call({ cmd: "fs.unwatch", all: true }).catch(function () {});
  for (var i = 0; i < cmds.length; i++) {
    (function (c) {
      chain = chain.then(function () { return call(c).catch(function () {}); });
    })(cmds[i]);
  }
  chain.then(function () { startEventPoll(); });
}

function watchHere() {
  rewatch([{ cmd: "fs.watch", path: st.path }, { cmd: "fs.watch", drives: true }]);
}

function watchDrives() {
  rewatch([{ cmd: "fs.watch", drives: true }]);
}

function startEventPoll() {
  if (st.watchTimer) return;
  var tick = guard("events", function () {
    if (!st.open) { st.watchTimer = 0; return; }
    call({ cmd: "fs.events", since: st.watchSeq })
      .then(guard("events ok", function (d) {
        st.watchSeq = d.seq || st.watchSeq;
        if (!d.count) return;
        var driveChange = false, dirChange = false;
        for (var i = 0; i < d.events.length; i++) {
          var k = d.events[i].kind;
          if (k === "drive.added" || k === "drive.removed") driveChange = true;
          else dirChange = true;
        }
        /* A background refresh must never win a race against a navigation the
           human started. Somebody pressing Cross while a USB stick is being
           enumerated should land in the folder they chose, not be thrown back
           to the drive picker a moment later. */
        if (st.inFlight > 0) return;
        if (driveChange) {
          if (st.view === "drives") { softRefresh(); toast("Drive list changed."); }
          else toast("A drive was plugged in or removed.");
        }
        if (dirChange && st.view === "dir" && !st.job && topScope().id === "list") softRefresh();
      }))
      .catch(function () { /* the bridge may be gone; the next tick finds out */ })
      .then(function () {
        if (st.open) st.watchTimer = setTimeout(tick, 1200);
        else st.watchTimer = 0;
      });
  });
  st.watchTimer = setTimeout(tick, 1200);
}

/* Refresh without losing the cursor or the selection — a background refresh
   that jumped the list would be worse than no refresh at all. */
function softRefresh() {
  if (st.view === "drives") { goDrives(); return; }
  var keepPath = currentEntry() ? currentEntry().path : null;
  var keepSel = st.sel;
  /* A background refresh does NOT take a navigation token of its own: it
     remembers the one that was current when it started and throws its answer
     away if the human has navigated in the meantime. Taking a token would
     make it win that race, and being bounced back to the folder you just
     left is the worst thing an auto-refresh can do. */
  var tok = st.navToken;
  var wantPath = st.path;
  st.inFlight++;
  call({ cmd: "fs.list", path: st.path, sort: st.sort, desc: st.desc, showHidden: st.showHidden, refresh: true })
    .then(guard("soft ok", function (data) {
      if (tok !== st.navToken || st.view !== "dir" || st.path !== wantPath) return;
      st.listing = data;
      st.entries = data.entries || [];
      st.sel = {};
      for (var i = 0; i < st.entries.length; i++)
        if (keepSel[st.entries[i].path]) st.sel[st.entries[i].path] = true;
      buildRows();
      if (keepPath) {
        for (var j = 0; j < st.rows.length; j++)
          if (st.rows[j].entry && st.rows[j].entry.path === keepPath) { st.cursor = j; break; }
      }
      if (st.cursor >= st.rows.length) st.cursor = Math.max(0, st.rows.length - 1);
      ensureVisible();
      dropAll();
      paintAll();
    }))
    .catch(function () {})
    .then(function () { st.inFlight--; });
}

/* ═══ OSK ══════════════════════════════════════════════════════════════ */

function osk(cfg) {
  if (!root.ArcOSK) {
    toast("The on-screen keyboard is not loaded — osk.js must sit next to files.js.", true);
    return false;
  }
  return root.ArcOSK.open(cfg);
}

/* ═══ Action table ═════════════════════════════════════════════════════ */

var ACT = {
  noop: function () {},
  back: function () { popScope(); },

  menuOpen: function () { popTo("list"); activate(); },

  menuRefresh: function () { popTo("list"); if (st.view === "drives") goDrives(); else goTo(st.path, false); },

  menuHidden: function () {
    st.showHidden = !st.showHidden;
    popTo("list");
    if (st.view === "dir") goTo(st.path, false);
    toast(st.showHidden ? "Showing hidden and system items." : "Hidden items are back out of sight.");
  },

  menuSort: function () {
    popTo("list");
    openPanel({
      id: "sort",
      eyebrow: "Sort",
      title: "Sort this folder by",
      hints: ["navUD", "select", "close"],
      build: function (bodyEl) {
        var keys = [["name", "Name"], ["size", "Size"], ["modified", "Date modified"], ["type", "Type"], ["created", "Date created"]];
        var r = 0;
        for (var i = 0; i < keys.length; i++) {
          bodyEl.appendChild(menuItem({
            label: keys[i][1], icon: "sort", act: "doSort", arg: keys[i][0],
            tail: st.sort === keys[i][0] ? (st.desc ? "descending" : "ascending") : ""
          }, r++, 0));
        }
        bodyEl.appendChild(menuItem({
          label: st.desc ? "Switch to ascending" : "Switch to descending",
          icon: "sort", act: "doSortDir"
        }, r++, 0));
      }
    });
  },

  doSort: function (elm) {
    var k = elm.dataset.arg;
    if (st.sort === k) st.desc = !st.desc;
    else { st.sort = k; st.desc = (k === "size" || k === "modified" || k === "created"); }
    popTo("list");
    goTo(st.path, false);
  },
  doSortDir: function () { st.desc = !st.desc; popTo("list"); goTo(st.path, false); },

  menuSelectAll: function () {
    for (var i = 0; i < st.entries.length; i++) st.sel[st.entries[i].path] = true;
    popTo("list");
    paintAll();
  },
  menuClearSel: function () { st.sel = {}; popTo("list"); paintAll(); },

  menuClip: function (elm) {
    var mode = elm.dataset.arg;
    var list = targets();
    if (!list.length) { toast("Nothing is selected."); return; }
    var paths = [];
    for (var i = 0; i < list.length; i++) paths.push(list[i].path);
    st.clip = { mode: mode, paths: paths };
    st.sel = {};
    popTo("list");
    paintAll();
    toast(paths.length + " item" + (paths.length === 1 ? "" : "s") + " ready to " + mode +
          ". Go to the destination folder and choose Paste here.");
  },

  menuPaste: function () {
    if (!st.clip) return;
    var mode = st.clip.mode, paths = st.clip.paths.slice(0);
    popTo("list");
    busy(true);
    call({ cmd: mode === "move" ? "fs.move" : "fs.copy", paths: paths, to: st.path })
      .then(guard("paste ok", function (d) {
        busy(false);
        st.clip = null;
        paintHead();
        watchJob(d.jobId, (mode === "move" ? "Moving " : "Copying ") + paths.length +
                          " item" + (paths.length === 1 ? "" : "s"),
          function () { softRefresh(); });
      }))
      .catch(guard("paste fail", function (err) {
        busy(false);
        toast(err.message, true);
      }));
  },

  menuRename: function () {
    var list = targets();
    if (list.length !== 1) { toast("Select exactly one item to rename."); return; }
    var item = list[0];
    popTo("list");
    osk({
      title: "Rename",
      value: item.name,
      mode: "text",
      maxLength: 255,
      placeholder: item.isDirectory ? "Folder name" : "File name",
      commitLabel: "Rename",
      onCommit: guard("rename commit", function (v) {
        if (!v || v === item.name) return;
        busy(true);
        call({ cmd: "fs.rename", path: item.path, name: v })
          .then(guard("rename ok", function (d) {
            toast("Renamed to " + d.name + ".");
            st.sel = {};
            softRefresh();
          }))
          .catch(guard("rename fail", function (err) { toast(err.message, true); }))
          .then(function () { busy(false); });
      }),
      onCancel: guard("rename cancel", function () { note("osk", "rename cancelled"); })
    });
  },

  menuNewFolder: function () {
    popTo("list");
    var here = st.path;
    osk({
      title: "New folder",
      value: "",
      mode: "text",
      maxLength: 255,
      placeholder: "Folder name",
      commitLabel: "Create",
      onCommit: guard("mkdir commit", function (v) {
        if (!v) return;
        busy(true);
        call({ cmd: "fs.mkdir", path: here, name: v })
          .then(guard("mkdir ok", function (d) {
            toast("Created " + d.name + ".");
            softRefresh();
          }))
          .catch(guard("mkdir fail", function (err) { toast(err.message, true); }))
          .then(function () { busy(false); });
      }),
      onCancel: function () {}
    });
  },

  menuFind: function () {
    popTo("list");
    osk({
      title: "Find in this folder",
      value: st.find || "",
      mode: "search",
      maxLength: 60,
      placeholder: "Part of a name",
      commitLabel: "Find",
      onCommit: guard("find commit", function (v) {
        st.find = (v || "").trim();
        if (!st.find) { toast("Search cleared."); return; }
        if (!jumpToMatch(st.find, st.cursor + 1) && !jumpToMatch(st.find, 0))
          toast("Nothing here matches “" + st.find + "”.", true);
      }),
      onCancel: function () {}
    });
  },

  menuDelete: function (elm) { popTo("list"); confirmDelete(elm.dataset.arg); },

  doDelete: function (elm) {
    var permanent = elm.dataset.arg === "permanent";
    var list = targets();
    var paths = [];
    for (var i = 0; i < list.length; i++) paths.push(list[i].path);
    popTo("list");
    if (!paths.length) return;
    busy(true);
    call({ cmd: "fs.delete", paths: paths, recycle: !permanent })
      .then(guard("delete ok", function (d) {
        busy(false);
        st.sel = {};
        watchJob(d.jobId,
          permanent ? ("Permanently deleting " + paths.length + " item" + (paths.length === 1 ? "" : "s"))
                    : ("Moving " + paths.length + " item" + (paths.length === 1 ? "" : "s") + " to the Recycle Bin"),
          function () { softRefresh(); });
      }))
      .catch(guard("delete fail", function (err) {
        busy(false);
        toast(err.message, true);
      }));
  },

  menuProps: function () { popTo("list"); openProps(); },

  menuEject: function (elm) {
    var letter = elm.dataset.arg;
    popTo("list");
    busy(true);
    call({ cmd: "fs.eject", drive: letter })
      .then(guard("eject ok", function (d) {
        toast(d.detail || ("Drive " + letter + ": is safe to unplug."));
        goDrives();
      }))
      .catch(guard("eject fail", function (err) { toast(err.message, true); }))
      .then(function () { busy(false); });
  },

  cancelJob: function () {
    if (!st.job) return;
    call({ cmd: "job.cancel", jobId: st.job.id })
      .then(guard("cancel ok", function () { toast("Stopping…"); }))
      .catch(guard("cancel fail", function (err) { toast("Could not cancel: " + err.message, true); }));
  }
};

function jumpToMatch(term, from) {
  var t = term.toLowerCase();
  for (var i = from; i < st.rows.length; i++) {
    var r = st.rows[i];
    if (!r.focusable) continue;
    var name = r.entry ? r.entry.name : (r.label || "");
    if (name.toLowerCase().indexOf(t) >= 0) {
      st.cursor = i;
      ensureVisible();
      render();
      paintDetails();
      return true;
    }
  }
  return false;
}

/* ═══ The pad channel ══════════════════════════════════════════════════
   One table, whatever the source. Both the semantic names the shell sends
   and the literal button names ArcOSK uses are accepted, because the two
   disagree on purpose — L1/R1 switch views in the shell and page a list
   here, and a host that only knows one spelling still works. */

var ALIAS = {
  cross: "activate", select: "activate", launch: "activate", confirm: "activate", enter: "activate",
  circle: "back", back: "back", cancel: "back", esc: "back",
  square: "mark", details: "mark",
  triangle: "menu", options: "menu", appopt: "menu",
  l1: "pageup", pageup: "pageup", tabplay: "pageup", lb: "pageup",
  r1: "pagedown", pagedown: "pagedown", tabmedia: "pagedown", rb: "pagedown",
  up: "up", down: "down", left: "left", right: "right",
  search: "search", find: "search",
  home: "home", end: "end"
};

function doAction(a) {
  if (!st.open) return false;
  if (!a) return false;

  /* The keyboard is modal and comes first, in case a host wired this up
     without the ArcOSK line of its own. */
  try {
    if (root.ArcOSK && root.ArcOSK.isOpen()) {
      root.ArcOSK.handleAction(a);
      return true;
    }
  } catch (e) { report("osk route", e); }

  var name = ALIAS[String(a).toLowerCase()] || String(a).toLowerCase();
  var sc = topScope();

  switch (name) {
    case "up":
      if (sc.kind === "grid") { scopeMove(sc, 0, -1); return true; }
      moveCursor(-1); return true;
    case "down":
      if (sc.kind === "grid") { scopeMove(sc, 0, 1); return true; }
      moveCursor(1); return true;
    case "left":
      if (sc.kind === "grid") { scopeMove(sc, -1, 0); return true; }
      up(); return true;
    case "right":
      if (sc.kind === "grid") { scopeMove(sc, 1, 0); return true; }
      activate(); return true;
    case "pageup":
      if (sc.kind === "grid") { scopeMove(sc, 0, -1); return true; }
      pageBy(-1); return true;
    case "pagedown":
      if (sc.kind === "grid") { scopeMove(sc, 0, 1); return true; }
      pageBy(1); return true;
    case "activate":
      activate(); return true;
    case "back":
      if (sc.kind === "grid") {
        if (sc.onBack && sc.onBack() === false) return true;
        popScope();
        return true;
      }
      /* At the top of the tree, Circle leaves the explorer entirely — the
         shell's rule that back always means "leave this scope", carried one
         level further out. */
      if (st.view === "drives") { close(); return true; }
      up(); return true;
    case "mark":
      if (sc.kind === "grid") return true;
      toggleMark(); return true;
    case "menu":
      if (sc.kind === "grid") return true;
      openMenu(); return true;
    case "search":
      if (sc.kind === "grid") return true;
      ACT.menuFind(); return true;
    case "home":
      if (sc.kind === "list") { st.cursor = firstFocusable(0, 1); ensureVisible(); render(); paintDetails(); }
      return true;
    case "end":
      if (sc.kind === "list") { st.cursor = firstFocusable(st.rows.length - 1, -1); ensureVisible(); render(); paintDetails(); }
      return true;
  }

  note("action", "unhandled '" + a + "' (consumed anyway: the explorer is modal)");
  return true;
}

/* Hold-to-repeat. Directions want it; face buttons must not have it, or one
   long press of Cross opens a folder and then whatever is under the cursor
   inside it. The host normally owns repeat for directions and sends a stream
   of "repeat" phases; this is for hosts that do not. */
var REPEATABLE = { up: 1, down: 1, pageup: 1, pagedown: 1 };
var FIRST_MS = 380, NEXT_MS = 70;

function beginRepeat(a) {
  endRepeat();
  var name = ALIAS[String(a).toLowerCase()] || String(a).toLowerCase();
  doAction(a);
  if (!REPEATABLE[name]) return;
  st.repeatAction = a;
  st.repeatTimer = setTimeout(function again() {
    if (!st.repeatAction) return;
    doAction(st.repeatAction);
    st.repeatTimer = setTimeout(again, NEXT_MS);
  }, FIRST_MS);
}

function endRepeat() {
  clearTimeout(st.repeatTimer);
  st.repeatTimer = 0;
  st.repeatAction = null;
}

/* ═══ Keyboard ═════════════════════════════════════════════════════════
   A debugging convenience that happens to still work, exactly as in the
   shell. ArcOSK installs its own capture-phase handler; the keys it does not
   use must still not reach the explorer. */

var KEYMAP = {
  ArrowUp: "up", ArrowDown: "down", ArrowLeft: "left", ArrowRight: "right",
  Enter: "activate", " ": "activate", Escape: "back",
  PageUp: "pageup", PageDown: "pagedown", Home: "home", End: "end",
  x: "mark", X: "mark", o: "menu", O: "menu", f: "search", F: "search", "/": "search"
};

function keyHandler(ev) {
  if (!st.open) return;
  if (ev.isTrusted !== false) setDevice("key"); else setDevice("pad");
  try { if (root.ArcOSK && root.ArcOSK.isOpen()) return; } catch (e) {}
  var a = KEYMAP[ev.key];
  if (!a) return;
  ev.preventDefault();
  ev.stopPropagation();
  doAction(a);
}

/* ═══ Open / close ═════════════════════════════════════════════════════ */

function open(cfg) {
  cfg = cfg || {};
  if (st.open) return true;

  if (!st.el) buildDOM();
  if (cfg.transport) { tx.fn = cfg.transport; tx.name = "supplied"; tx.why = "open({transport}) was given one"; }
  detectTransport();

  st.title = cfg.title || "Files";
  st.onExit = cfg.onExit || null;
  st.scopes = [baseScope()];
  st.sel = {};
  st.clip = null;
  st.err = null;
  st.find = "";
  st.watchSeq = 0;

  /* Start from nothing rather than from whatever the last session left in the
     model: a reopened explorer that briefly shows the previous folder is both
     confusing and a lie about where the cursor is. */
  st.rows = []; st.tops = []; st.totalH = 0;
  st.cursor = 0; st.scroll = 0;
  st.entries = []; st.listing = null; st.drives = null; st.places = null;
  dropAll();

  st.open = true;
  measure();
  st.el.wrap.classList.add("is-open");
  st.el.wrap.classList.toggle("is-still", reducedMotion());
  st.el.wrap.setAttribute("aria-hidden", "false");
  document.addEventListener("keydown", keyHandler, true);

  note("open", "transport=" + tx.name + " (" + tx.why + ")");

  if (cfg.path) goTo(cfg.path, false);
  else goDrives();

  return true;
}

function close() {
  if (!st.open) return;
  st.open = false;
  endRepeat();
  clearTimeout(st.jobTimer);
  clearTimeout(st.watchTimer);
  st.watchTimer = 0;
  st.job = null;
  st.inFlight = 0;
  call({ cmd: "fs.unwatch", all: true }).catch(function () {});
  document.removeEventListener("keydown", keyHandler, true);
  try {
    st.el.wrap.classList.remove("is-open");
    st.el.wrap.setAttribute("aria-hidden", "true");
    st.el.scrim.classList.remove("is-open");
  } catch (e) {}
  st.scopes = [];
  note("close", "explorer closed");
  try { if (st.onExit) st.onExit(); } catch (e) { report("onExit", e); }
}

/* ═══ Public surface ═══════════════════════════════════════════════════ */

var API = {
  version: VERSION,
  open: guard("open", open),
  close: guard("close", close),
  isOpen: guard("isOpen", function () { return !!st.open; }),
  handleAction: guard("handleAction", function (a) { return doAction(a); }),
  beginRepeat: guard("beginRepeat", beginRepeat),
  endRepeat: guard("endRepeat", endRepeat),
  setTransport: guard("setTransport", function (fn) {
    tx.fn = fn; tx.name = "supplied"; tx.why = "setTransport() was called";
  }),
  probe: guard("probe", function () {
    detectTransport();
    return { transport: tx.name, why: tx.why, connected: !!tx.fn };
  }),
  refresh: guard("refresh", function () { if (st.view === "drives") goDrives(); else goTo(st.path, false); }),
  goTo: guard("goTo", function (p) { return goTo(p, true); }),
  debugState: guard("debugState", function () {
    var row = currentRow();
    return {
      open: st.open, view: st.view, path: st.path,
      rows: st.rows.length, cursor: st.cursor, scroll: st.scroll, totalH: st.totalH,
      rendered: Object.keys(st.live).length,
      focused: row ? (row.entry ? row.entry.name : (row.drive ? row.drive.letter : row.label)) : null,
      scope: topScope().id, depth: st.scopes.length,
      selected: selectedPaths().length,
      clip: st.clip ? (st.clip.mode + ":" + st.clip.paths.length) : null,
      sort: st.sort, desc: st.desc, showHidden: st.showHidden,
      transport: tx.name, job: st.job ? st.job.id : null
    };
  }),

  /* Pure helpers — the headless test drives exactly these. */
  _model: {
    bytes: bytes, when: when, eta: eta, rate: rate,
    kindOf: kindOf, typeLabel: typeLabel, crumbParts: crumbParts,
    ALIAS: ALIAS, HINTS: HINTS, ICON: ICON, PAD_GLYPH: PAD_GLYPH,
    /* The virtual-window arithmetic, extracted so it can be asserted without
       a DOM: given row heights and a scroll offset, which rows exist? */
    windowFor: function (heights, scroll, viewH, overscan) {
      var tops = [], y = 0, i;
      for (i = 0; i < heights.length; i++) { tops.push(y); y += heights[i]; }
      function at(v) {
        var lo = 0, hi = tops.length - 1, best = 0;
        while (lo <= hi) {
          var mid = (lo + hi) >> 1;
          if (tops[mid] <= v) { best = mid; lo = mid + 1; }
          else hi = mid - 1;
        }
        return best;
      }
      if (!heights.length) return { first: 0, last: -1, total: 0, tops: tops };
      var os = overscan === undefined ? OVERSCAN : overscan;
      return {
        first: Math.max(0, at(scroll) - os),
        last: Math.min(heights.length - 1, at(scroll + viewH) + os),
        total: y,
        tops: tops
      };
    }
  }
};

root.ArcFiles = API;
if (typeof module !== "undefined" && module.exports) module.exports = API;

})(typeof window !== "undefined" ? window
   : (typeof globalThis !== "undefined" ? globalThis : this));
