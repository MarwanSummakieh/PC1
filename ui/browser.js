/* ═══════════════════════════════════════════════════════════════════════
   PC1 — browser chrome  (ui/browser.js)

   This is the browser everything EXCEPT the web page. The tab strip, the
   address bar, the TLS indicator, the start page of pinned tiles, the
   history list, the menu, the hint bar and the whole controller model live
   here, in the shell's own WebView, as ordinary DOM.

   The page itself is drawn by a second WebView2 that the host owns and
   positions over the rectangle this module reserves (.arcbw-stage). Read
   the BrowserHost comment in ShellHostWeb.cs for why it has to be that way;
   the consequence for this file is one rule, applied everywhere:

       NOTHING THIS FILE DRAWS CAN APPEAR ON TOP OF THE PAGE.

   A content WebView is a child window, not a layer. So every full-screen
   surface here — start page, history, menu, tab switcher, the keyboard —
   asks the host to hide the content view first and show it again after. The
   helper is stage(); if you add a surface and forget to call it, your
   surface will be invisible behind a web page and the reason will not be
   obvious.

   Public API
   ──────────
     ArcBrowser.attach(bridge)          once, from index.html
     ArcBrowser.open({ url, onExit })
     ArcBrowser.close()
     ArcBrowser.isOpen()
     ArcBrowser.handleAction(a, phase)  the pad channel; true if consumed
     ArcBrowser.hostMessage(m)          {type:"browser", ev:…} from the host
     ArcBrowser.debugState()

   The bridge index.html supplies
   ──────────────────────────────
     post(o)        send a message to the host
     osk(cfg)       open the on-screen keyboard (same cfg as ArcOSK.open)
     oskIsOpen()
     log(text)      host log
     toast(text)    the shell's own toast, for things the browser is not up
     files(folder, path)   optional — show a finished download in the
                    console's file explorer. Return false if it could not,
                    and the browser falls back to naming the path.
   ═══════════════════════════════════════════════════════════════════════ */

(function (root) {
"use strict";

var VERSION = "1.0.0";

/* ═══ Crash isolation ══════════════════════════════════════════════════
   Identical discipline to ui/files.js, for the identical reason: this runs
   inside the Windows shell process and an escaped exception is a black
   television. */

var bridge = {
  post: function () { return false; },
  osk: function () { return false; },
  oskIsOpen: function () { return false; },
  log: function () {},
  toast: function () {},
  /* Optional. The shell supplies it so a finished download can be shown where
     it landed, in the console's own file explorer. Without it the browser says
     the path in a toast rather than pretending the file is unreachable. */
  files: null
};

function say(what) { try { bridge.log("ArcBrowser " + what); } catch (e) {} }
function report(where, err) {
  var m = "ArcBrowser[" + where + "] " + (err && err.stack ? err.stack : err);
  try { if (root.console && console.error) console.error(m); } catch (e) {}
  try { bridge.log(m); } catch (e) {}
}
function guard(where, fn) {
  return function () {
    try { return fn.apply(this, arguments); }
    catch (err) { report(where, err); return undefined; }
  };
}

/* ═══ Preferences ══════════════════════════════════════════════════════
   localStorage, same as the rest of the shell's preferences. Everything
   here survives a restart; nothing here leaves the machine. */

var PREF = {
  pins:   "arc.browser.pins",
  hist:   "arc.browser.history",
  zoom:   "arc.browser.zoom",
  engine: "arc.browser.engine"
};

function load(key, fallback) {
  try {
    var raw = localStorage.getItem(key);
    if (!raw) return fallback;
    var v = JSON.parse(raw);
    return (v === null || v === undefined) ? fallback : v;
  } catch (e) { return fallback; }
}
function save(key, value) {
  try { localStorage.setItem(key, JSON.stringify(value)); }
  catch (e) { report("save " + key, e); }
}

/* Search engines. DuckDuckGo by default because it is the one that does not
   need an account, does not personalise, and does not put a consent wall in
   front of a television. */
var ENGINES = {
  duckduckgo: { name: "DuckDuckGo", url: "https://duckduckgo.com/?q=" },
  google:     { name: "Google",     url: "https://www.google.com/search?q=" },
  bing:       { name: "Bing",       url: "https://www.bing.com/search?q=" },
  wikipedia:  { name: "Wikipedia",  url: "https://en.wikipedia.org/w/index.php?search=" }
};

var DEFAULT_PINS = [
  { name: "YouTube", url: "https://www.youtube.com/tv",   colour: "#c8262e" },
  { name: "Netflix", url: "https://www.netflix.com",      colour: "#b1060f" },
  { name: "Twitch",  url: "https://www.twitch.tv",        colour: "#7b3fe4" },
  { name: "iPlayer", url: "https://www.bbc.co.uk/iplayer", colour: "#f54997" }
];

/* ═══ State ════════════════════════════════════════════════════════════ */

var st = {
  open: false,
  el: null,
  onExit: null,

  tabs: [],
  active: 0,
  max: 4,

  /* Where the pad is pointing. One of:
       "content" — the web page has it; actions are relayed to the host
       "chrome"  — the tab strip and address row
       "start"   — the pinned-app start page
       "history" — the history list
       "menu"    — the browser menu
     Only "content" leaves the content WebView visible. */
  scope: "start",
  chromeIdx: 0,
  sheetIdx: 0,
  sheetRows: [],
  sheetKind: null,        // which sheet is up, when the scope alone cannot say

  /* Downloads, as the host reports them. The list is authoritative and
     arrives whole on every change; nothing here is inferred from the last
     one except the transfer rate, which the host does not send because it
     is a property of two samples rather than of the download. */
  downloads: [],
  downActive: 0,
  rates: {},              // id -> { got, at, bps }
  downSeen: {},           // id -> last state a toast was raised for

  navMode: "spatial",     // what arcnav.js reports it is doing
  focusKind: null,        // kind of the element the page's ring is on
  media: null,
  pendingEdit: null,      // a text field inside the page is waiting for the OSK
  pendingSelect: null,

  history: [],
  session: [],
  pins: [],
  zoom: {},
  engine: "duckduckgo",

  boundsTimer: 0,
  lastBounds: "",
  toastTimer: 0,
  unavailable: null
};

/* ═══ Glyphs ═══════════════════════════════════════════════════════════ */

var G = {
  lock:   '<path d="M6.5 10.5V8a5.5 5.5 0 0 1 11 0v2.5"/><rect x="4.2" y="10.5" width="15.6" height="9.5" rx="2"/>',
  open:   '<path d="M8.4 10.5V7.6a5.5 5.5 0 0 1 10.6-2"/><rect x="4.2" y="10.5" width="12.6" height="9.5" rx="2"/>',
  zoom:   '<circle cx="10.6" cy="10.6" r="6.4"/><path d="M15.3 15.3 20.5 20.5"/><path d="M7.8 10.6h5.6"/>',
  menu:   '<path d="M4.5 7.5h15M4.5 12h15M4.5 16.5h15"/>',
  clock:  '<circle cx="12" cy="12" r="8.5"/><path d="M12 7.2V12l3.2 2"/>',
  plus:   '<path d="M12 5.5v13M5.5 12h13"/>',
  /* The pad glyphs, kept identical to index.html's PAD_GLYPH — read the
     long note beside it for where each shape comes from. Three files now
     carry this vocabulary (here, ui/files.js and index.html); they have to
     stay in step, because a browser whose Cross is a different Cross from
     the home screen's is worse than either drawing on its own. */
  cross:  '<path class="face" d="M5.7 5.7 18.3 18.3M18.3 5.7 5.7 18.3"/>',
  circle: '<circle class="face" cx="12" cy="12" r="6.15"/>',
  square: '<rect class="face" x="6.45" y="6.45" width="11.1" height="11.1"/>',
  tri:    '<path class="face tri" d="M12 3.7 20 17.9H4z"/>',
  dpad:   '<path class="solid" d="M7.2.6H16.8V6L12 10.5 7.2 6Z"/>'
        + '<path class="solid" d="M7.2 23.4H16.8V18L12 13.5 7.2 18Z"/>'
        + '<path class="solid" d="M.6 7.2V16.8H6L10.5 12 6 7.2Z"/>'
        + '<path class="solid" d="M23.4 7.2V16.8H18L13.5 12 18 7.2Z"/>',
  stick:  '<circle cx="12" cy="12" r="6.55"/><circle class="solid" cx="12" cy="12" r="4.7"/>',
  opts:   '<path class="solid" d="M3 4.76h18v2H3zM3 11h18v2H3zM3 17.24h18v2H3z"/>',
  down:   '<path d="M12 4v10.5"/><path d="M7.6 10.6 12 15l4.4-4.4"/><path d="M4.6 18.6h14.8"/>'
};

function svg(path) { return '<svg viewBox="0 0 24 24" aria-hidden="true">' + path + "</svg>"; }

/* ═══ URL handling ═════════════════════════════════════════════════════
   One field takes both an address and a search, because asking a human with
   a controller to choose between two boxes is a joke. The test is
   deliberately conservative: something is only treated as a URL when it
   really looks like one, and everything else is a search — mistaking a
   search for a hostname produces a DNS error page, which is much worse than
   searching for something that happened to be a domain. */

var SCHEME = /^[a-z][a-z0-9+.-]*:\/\//i;
var HOSTISH = /^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+(:\d+)?(\/.*)?$/i;
var LOCALISH = /^localhost(:\d+)?(\/.*)?$/i;
var IPISH = /^\d{1,3}(\.\d{1,3}){3}(:\d+)?(\/.*)?$/;

function resolveInput(text) {
  var t = String(text || "").trim();
  if (!t) return null;
  if (SCHEME.test(t)) return t;
  if (/^(about|chrome|edge|view-source|data|javascript):/i.test(t)) {
    /* Refused, not silently rewritten. about:blank is harmless but
       javascript: and data: URLs typed into a TV address bar are a class of
       thing this shell has no reason to be able to do. */
    return { refuse: "That kind of address cannot be opened here." };
  }
  if (t.indexOf(" ") < 0 && (HOSTISH.test(t) || LOCALISH.test(t) || IPISH.test(t)))
    return "https://" + t;
  var eng = ENGINES[st.engine] || ENGINES.duckduckgo;
  return eng.url + encodeURIComponent(t);
}

function hostOf(url) {
  try {
    var m = String(url).match(/^[a-z][a-z0-9+.-]*:\/\/([^\/?#]+)/i);
    return m ? m[1].replace(/^www\./i, "") : "";
  } catch (e) { return ""; }
}

function splitUrl(url) {
  var m = String(url || "").match(/^([a-z][a-z0-9+.-]*:\/\/)([^\/?#]*)(.*)$/i);
  if (!m) return { scheme: "", host: url || "", rest: "" };
  return { scheme: m[1], host: m[2], rest: m[3] };
}

/* ═══ DOM ══════════════════════════════════════════════════════════════ */

function el(tag, cls, html) {
  var e = document.createElement(tag);
  if (cls) e.className = cls;
  if (html !== undefined) e.innerHTML = html;
  return e;
}

function buildDOM() {
  var wrap = el("div", "arcbw");
  wrap.setAttribute("aria-hidden", "true");

  var chrome = el("div", "arcbw-chrome");
  var tabs = el("div", "arcbw-tabs");
  var omni = el("div", "arcbw-omni");

  var addr = el("div", "arcbw-addr");
  addr.setAttribute("role", "button");
  var tls = el("span", "arcbw-tls is-none");
  var url = el("span", "arcbw-url is-placeholder");
  url.textContent = "Search or enter an address";
  addr.appendChild(tls); addr.appendChild(url);

  var zoomChip = el("div", "arcbw-chip", svg(G.zoom) + "<span>100%</span>");
  /* The downloads chip is in the chrome and not in the menu because a download
     is the one thing in this browser that happens WHILE the human is doing
     something else. Buried two presses deep it would be invisible exactly when
     it matters — a 400 MB file that failed at 90% behind a menu nobody opened.
     It is only in the row while there is something to say; see renderDown(). */
  var dlChip = el("div", "arcbw-chip is-dl is-off",
    svg(G.down) + "<span>Downloads</span><i class=\"arcbw-chip-bar\"></i>");
  var menuChip = el("div", "arcbw-chip", svg(G.menu) + "<span>Menu</span>");

  omni.appendChild(addr); omni.appendChild(zoomChip);
  omni.appendChild(dlChip); omni.appendChild(menuChip);
  chrome.appendChild(tabs); chrome.appendChild(omni);

  var stage = el("div", "arcbw-stage");
  var msg = el("div", "arcbw-stage-msg", "<h3></h3><p></p>");
  stage.appendChild(msg);

  var sheet = el("div", "arcbw-sheet");
  stage.appendChild(sheet);

  var hints = el("div", "arcbw-hints");

  wrap.appendChild(chrome);
  wrap.appendChild(stage);
  wrap.appendChild(hints);

  /* Last, and a sibling of the stage rather than a child of it: a toast
     inside the stage would be behind the content window. See the note in
     browser.css. */
  var toast = el("div", "arcbw-toast");
  wrap.appendChild(toast);
  document.body.appendChild(wrap);

  st.el = {
    wrap: wrap, chrome: chrome, tabs: tabs, omni: omni,
    addr: addr, tls: tls, url: url, zoom: zoomChip, dl: dlChip, menu: menuChip,
    stage: stage, msg: msg, sheet: sheet, toast: toast, hints: hints
  };
}

/* ═══ Telling the host where the page goes ═════════════════════════════
   The stage's rectangle in device pixels. Reported on every layout change
   and re-checked on a slow timer, because a font-size change (the root
   scale is viewport-derived) moves it without firing resize on the element.
   The host compares before moving anything, so re-sending the same numbers
   costs nothing. */

function pushBounds() {
  if (!st.open || !st.el) return;
  var r = st.el.stage.getBoundingClientRect();
  var dpr = root.devicePixelRatio || 1;
  var b = {
    x: Math.round(r.left * dpr), y: Math.round(r.top * dpr),
    w: Math.round(r.width * dpr), h: Math.round(r.height * dpr)
  };
  var key = b.x + "," + b.y + "," + b.w + "," + b.h;
  if (key === st.lastBounds) return;
  st.lastBounds = key;
  bridge.post({ type: "browser", cmd: "bounds", x: b.x, y: b.y, w: b.w, h: b.h });
}

/* ═══ Suspending the content view ══════════════════════════════════════
   A WebView2 content controller is a child HWND, not a layer. Nothing this
   file draws can appear on top of it. So every shell-drawn surface that has
   to be seen — the start page, the menu, the history list, the extensions
   list, a crash sheet, and above all the on-screen keyboard — must take the
   content view off the screen for as long as it is up, and put it back
   after.

   This used to be a bare boolean, stage(true) / stage(false), called from a
   dozen places. Two problems with that, both of which bit:

     - it is a TOGGLE, so two overlapping surfaces fight. The keyboard opens
       over the start page, the keyboard closes, and whoever restores first
       wins: either the page comes back underneath a start page that is
       still up, or it stays hidden with nothing left holding it down.
     - it is easy to forget. showZoom() and showEngines() never called it at
       all, so choosing a zoom level from the chrome drew the sheet behind a
       live web page.

   So visibility is DERIVED, never toggled. Two inputs:

     - the set of reasons currently holding the content view down. Each is a
       named string, taken by suspendContent(reason) and given back by
       resumeContent(reason). Names, not a counter, so a double-suspend is
       idempotent and a leaked resume cannot push the count negative.
     - the scope. Only "content" and "chrome" want the page visible at all;
       the chrome is a thin strip at the top, and hiding the page behind it
       would make walking to the address bar look like closing the tab.

   apply() recomputes from those two and posts only on a real change. That
   makes the state impossible to strand: whatever happens, the next scope
   change or resume recomputes the truth from scratch rather than trying to
   undo a toggle. Every transition is one line in the log with its reason
   and the list of remaining holds, because "the keyboard is up and nothing
   is on the screen" is exactly the failure this has to make readable. */

var holds = {};            /* reason -> true */
var shownNow = null;        /* last "show" posted; null until the first apply */
var focusNow = null;        /* last "focus" posted */

function holdList() {
  var out = [], k;
  for (k in holds) if (Object.prototype.hasOwnProperty.call(holds, k)) out.push(k);
  return out.length ? out.join("+") : "none";
}

function wantContent() {
  for (var k in holds) if (Object.prototype.hasOwnProperty.call(holds, k)) return false;
  return st.scope === "content" || st.scope === "chrome";
}

function applyContent(why) {
  if (!st.open) return;
  var show = wantContent();
  /* Keyboard focus only ever goes to the content window when the page
     itself is what the pad is driving. It must come back to the shell
     WebView for anything else, because that is what keeps Esc/F2/F3
     reaching the host's accelerator hook. */
  var focus = show && st.scope === "content";
  if (show === shownNow && focus === focusNow) return;
  shownNow = show; focusNow = focus;
  bridge.post({ type: "browser", cmd: "show", on: show ? 1 : 0 });
  bridge.post({ type: "browser", cmd: "focus", content: focus ? 1 : 0 });
  say("content " + (show ? "SHOWN" : "HIDDEN") + " — " + why +
      " (scope " + st.scope + ", holds: " + holdList() + ")");
}

function suspendContent(reason) {
  if (!reason || holds[reason]) return;
  holds[reason] = true;
  applyContent("suspend:" + reason);
}

function resumeContent(reason) {
  if (!reason || !holds[reason]) return;
  delete holds[reason];
  applyContent("resume:" + reason);
}

/* Scope changed; recompute. Named so the call sites read as what they mean. */
function syncContent(why) { applyContent(why || "scope"); }

/* The one thing a derived model still needs: a way back from an impossible
   state. Nothing should ever leak a hold, but if a surface throws between
   suspending and resuming, the browser must not be left showing a blank
   rectangle for the rest of the session. Closing and reopening clears them. */
function clearHolds(why) {
  var had = holdList();
  holds = {};
  if (had !== "none") say("cleared content holds (" + had + ") — " + why);
}

/* ═══ Rendering ════════════════════════════════════════════════════════ */

function activeTab() {
  for (var i = 0; i < st.tabs.length; i++) if (st.tabs[i].id === st.active) return st.tabs[i];
  return null;
}

function renderTabs() {
  var e = st.el, i;
  e.tabs.innerHTML = "";
  for (i = 0; i < st.tabs.length; i++) {
    var t = st.tabs[i];
    var node = el("button", "arcbw-tab" + (t.id === st.active ? " is-active" : "") +
                            (t.loading ? " is-loading" : "") + (t.crashed ? " is-crashed" : ""));
    var fav = el("span", "arcbw-tab-fav");
    /* Only ever a data: URI. The host fetches the icon inside the content
       WebView and hands the bytes over, so this page never issues a request
       to a site it is not itself served from. Anything else is ignored
       rather than loaded — a remote URL here would be the shell reaching out
       to a third party, which is the one thing it must not do. */
    if (t.favicon && !t.loading && t.favicon.indexOf("data:image/") === 0) {
      var img = document.createElement("img");
      img.className = "arcbw-tab-fav";
      img.src = t.favicon;
      img.alt = "";
      fav = img;
    }
    var title = el("span", "arcbw-tab-title");
    title.textContent = t.crashed ? "Page stopped responding"
                      : (t.title || hostOf(t.url) || "New tab");
    node.appendChild(fav); node.appendChild(title);
    node.__arcTab = t.id;
    e.tabs.appendChild(node);
  }
  var add = el("button", "arcbw-newtab", "+");
  if (st.tabs.length >= st.max) add.setAttribute("aria-disabled", "true");
  add.__arcNew = true;
  e.tabs.appendChild(add);
}

function renderAddress() {
  var e = st.el, t = activeTab();
  var url = t ? t.url : "";
  if (!url || url === "about:blank") {
    e.url.className = "arcbw-url is-placeholder";
    e.url.textContent = "Search or enter an address";
    e.tls.className = "arcbw-tls is-none";
    e.tls.innerHTML = "";
  } else {
    var p = splitUrl(url);
    e.url.className = "arcbw-url";
    e.url.innerHTML = "";
    var hostSpan = el("span", "arcbw-host"); hostSpan.textContent = p.host;
    var restSpan = el("span", "arcbw-rest"); restSpan.textContent = p.rest;
    e.url.appendChild(hostSpan); e.url.appendChild(restSpan);
    /* The indicator says what the transport is, and nothing more. It is not
       a padlock meaning "safe": WebView2 refuses to load a page whose
       certificate does not verify, so https here means the connection was
       encrypted and authenticated, and http means it was neither. */
    if (t.secure) { e.tls.className = "arcbw-tls is-secure"; e.tls.innerHTML = svg(G.lock) + "<span>https</span>"; }
    else { e.tls.className = "arcbw-tls is-plain"; e.tls.innerHTML = svg(G.open) + "<span>not encrypted</span>"; }
  }
  var z = t ? Math.round((t.zoom || 1) * 100) : 100;
  e.zoom.innerHTML = svg(G.zoom) + "<span>" + z + "%</span>";
}

function renderStage() {
  var e = st.el, t = activeTab();
  var blank = !t || t.crashed || !t.url || t.url === "about:blank";
  e.stage.classList.toggle("is-blank", !!blank && st.scope !== "start");
  if (t && t.crashed) {
    e.msg.querySelector("h3").textContent = "This page stopped responding";
    e.msg.querySelector("p").textContent =
      "Its process was closed. The rest of the console was not affected — the browser runs web " +
      "pages in their own processes, separate from the shell. Press Triangle for the menu and " +
      "choose Reload, or close the tab.";
  } else if (blank) {
    e.msg.querySelector("h3").textContent = "Nothing loaded yet";
    e.msg.querySelector("p").textContent = "Press Options to reach the address bar, or Circle for your pinned apps.";
  }
}

/* ═══ Hints ════════════════════════════════════════════════════════════ */

function hint(glyph, label) {
  return '<span class="arcbw-hint">' + svg(glyph) + "<span>" + label + "</span></span>";
}
function keyHint(cap, label) {
  return '<span class="arcbw-hint"><b>' + cap + "</b><span>" + label + "</span></span>";
}

function renderHints() {
  var h = [];
  if (st.scope === "content") {
    if (st.navMode === "cursor") {
      h.push(hint(G.stick, "Move the pointer"));
      h.push(hint(G.cross, "Click"));
      h.push(keyHint("L3", "Back to link mode"));
    } else {
      h.push(hint(G.dpad, "Move between links"));
      h.push(hint(G.cross, "Open"));
      h.push(keyHint("L3", "Pointer mode"));
    }
    h.push(keyHint("R stick", "Scroll"));
    h.push(keyHint("L1 / R1", "Page up / down"));
    h.push(hint(G.circle, "Back"));
    h.push(hint(G.opts, "Address bar"));
    /* The label has to follow what the button actually does right now, or
       the one press that gets a dismissed keyboard back is undiscoverable. */
    h.push(hint(G.tri, st.focusKind === "text" ? "Keyboard" : "Menu"));
    if (st.media) h.push(hint(G.square, "Full screen"));
  } else if (st.scope === "chrome") {
    h.push(hint(G.dpad, "Move"));
    h.push(hint(G.cross, "Select"));
    h.push(hint(G.opts, "Address bar"));
    h.push(hint(G.circle, "Back to the page"));
    h.push(keyHint("L1 / R1", "Switch tab"));
  } else if (st.scope === "start") {
    h.push(hint(G.dpad, "Move"));
    h.push(hint(G.cross, "Open"));
    /* Options is listed on EVERY scope, first among the shortcuts. It is
       the one button that reaches the address bar from anywhere in the
       browser, and a shortcut nobody is told about is a shortcut nobody
       uses — the whole browser was unusable for exactly that reason. */
    h.push(hint(G.opts, "Address bar"));
    h.push(hint(G.square, "Remove pin"));
    h.push(hint(G.circle, "Leave the browser"));
    h.push(hint(G.tri, "Menu"));
  } else if (st.sheetKind === "extensions") {
    var erow = st.sheetRows[st.sheetIdx];
    var etail = erow ? (erow.querySelector(".arcbw-row-tail") || {}).textContent : "";
    h.push(hint(G.dpad, "Move"));
    h.push(hint(G.cross, etail === "On" ? "Turn off" : etail === "Off" ? "Turn on"
                       : etail === "Install" ? "Install it" : "Select"));
    if (erow && typeof erow.__arcRemove === "function") h.push(hint(G.square, "Remove"));
    h.push(hint(G.circle, "Back"));
  } else if (st.sheetKind === "downloads") {
    /* The two buttons do different things on different rows here, so the
       hint bar has to follow the row the cursor is actually on rather than
       state one meaning for the whole screen. */
    var row = st.sheetRows[st.sheetIdx];
    var d = (row && typeof row.__dl === "number") ? findDown(row.__dl) : null;
    h.push(hint(G.dpad, "Move"));
    if (d) {
      h.push(hint(G.cross, d.state === "running" ? "Pause"
                         : d.state === "paused" ? "Carry on"
                         : d.state === "done" ? "Show in Files" : "Ask again"));
      h.push(hint(G.square, (d.state === "running" || d.state === "paused") ? "Cancel" : "Remove from the list"));
    } else {
      h.push(hint(G.cross, "Select"));
    }
    h.push(hint(G.opts, "Address bar"));
    h.push(hint(G.circle, "Back"));
  } else {
    h.push(hint(G.dpad, "Move"));
    h.push(hint(G.cross, "Select"));
    h.push(hint(G.opts, "Address bar"));
    h.push(hint(G.circle, "Back"));
  }
  st.el.hints.innerHTML = h.join("");
}

/* ═══ Focus painting ═══════════════════════════════════════════════════
   The browser keeps its own cursor rather than borrowing the shell's Focus
   manager: its scopes are not a stack (content and chrome swap sideways,
   they do not nest) and its rows are rebuilt from host messages several
   times a second. One attribute, one function, no bookkeeping to go stale. */

/* The chrome is TWO rows on the screen and used to be one list to the
   D-pad: tabs, then the + button, then the address bar, then zoom, then
   Menu, all walked with left and right, with up and down both meaning
   "leave". That is why the address bar could not be reached. Standing on a
   tab — which is where entering the chrome puts you when a page is loading —
   the obvious press for the address bar directly below it is Down, and Down
   dropped straight back into the page. Nothing said the row had to be
   crossed sideways through the + button.

   So the rows are modelled as rows. Left and right stay inside a row, up and
   down cross between them, and only a press that leaves the last row leaves
   the chrome. */

function chromeRows() {
  var top = [], i;
  var kids = st.el.tabs.children;      // the tabs and the + button
  for (i = 0; i < kids.length; i++) top.push(kids[i]);
  /* The downloads chip is only walkable while it is on the screen. A hidden
     element in this list is a cursor position that looks like a dead press. */
  var bottom = [st.el.addr, st.el.zoom];
  if (!st.el.dl.classList.contains("is-off")) bottom.push(st.el.dl);
  bottom.push(st.el.menu);
  return [top, bottom];
}

/* Flat order, still the thing paintFocus() and st.chromeIdx speak. */
function chromeItems() {
  var r = chromeRows();
  return r[0].concat(r[1]);
}

function chromeAt() {
  var rows = chromeRows(), top = rows[0].length;
  var i = Math.max(0, Math.min(top + rows[1].length - 1, st.chromeIdx));
  return (i < top) ? { row: 0, col: i, rows: rows }
                   : { row: 1, col: i - top, rows: rows };
}

function chromeGo(row, col) {
  var rows = chromeRows();
  var r = rows[row] || [];
  if (!r.length) { row = row ? 0 : 1; r = rows[row] || []; }
  if (!r.length) return;
  col = Math.max(0, Math.min(r.length - 1, col));
  st.chromeIdx = (row === 0) ? col : rows[0].length + col;
}

var lastFocusKey = "";

function paintFocus() {
  var all = st.el.wrap.querySelectorAll("[data-focus]");
  for (var i = 0; i < all.length; i++) all[i].removeAttribute("data-focus");

  var node = null;
  if (st.scope === "chrome") {
    var items = chromeItems();
    if (!items.length) return;
    st.chromeIdx = Math.max(0, Math.min(items.length - 1, st.chromeIdx));
    node = items[st.chromeIdx];
  } else if (st.sheetRows.length && (st.scope === "start" || st.scope === "history" || st.scope === "menu")) {
    st.sheetIdx = Math.max(0, Math.min(st.sheetRows.length - 1, st.sheetIdx));
    node = st.sheetRows[st.sheetIdx];
  }

  if (node) {
    node.setAttribute("data-focus", "on");
    scrollIntoRow(node);
    /* The same discipline index.html follows: every cursor move is one line in
       the host log. It is the only way an unattended --walk over the browser's
       own chrome can be read back afterwards and believed. */
    var label = (node.textContent || "").replace(/\s+/g, " ").trim().slice(0, 70) ||
                (node.__arcNew ? "+ new tab" : "(unnamed)");
    var key = st.scope + "|" + label;
    if (key !== lastFocusKey) { lastFocusKey = key; say("focus " + st.scope + " | " + label); }
  }
  renderHints();
}

/* ═══ Keeping the ring on the screen ═══════════════════════════════════
   The shell's own focus manager clears focus DOCUMENT-WIDE before
   repainting its stack:

       document.querySelectorAll("[data-focus]").forEach(e => e.removeAttribute(...))

   The browser's chrome lives in the same document, so any repaint the shell
   does while the browser is up — and several things trigger one — erases the
   browser's ring as a side effect. The browser then looks like it has no
   cursor at all until the next pad press repaints it, which reads exactly
   like "the address bar cannot be reached": the human is on it, and nothing
   on the screen says so.

   The proper fix is one line in index.html, scoping that clear to the focus
   manager's own elements, and it is not this file's to make. Until then this
   notices the ring has been taken off something that should have it and puts
   it back. It runs on the bounds tick that is already going round, costs one
   querySelector, and does nothing at all in the common case. */
function focusStillPainted() {
  if (!st.open || !st.el) return true;
  if (st.scope === "content") return true;          // the ring is the page's own
  return !!st.el.wrap.querySelector('[data-focus="on"]');
}

function keepFocusPainted() {
  if (focusStillPainted()) return;
  lastFocusKey = "";                                 // so the restore is logged once
  paintFocus();
  say("focus ring was cleared by a shell repaint; restored it");
}

function scrollIntoRow(node) {
  try { node.scrollIntoView({ block: "nearest", inline: "nearest", behavior: "smooth" }); }
  catch (e) { try { node.scrollIntoView(); } catch (e2) {} }
}

/* ═══ Sheets ═══════════════════════════════════════════════════════════ */

function closeSheet() {
  st.el.sheet.classList.remove("is-open");
  st.el.sheet.innerHTML = "";
  st.sheetRows = [];
  /* The downloads sheet keeps live references into its own DOM so it can
     write new byte counts into rows that are already on the screen. Those
     references are dead the moment the sheet's markup is thrown away, and a
     host message arriving one frame later would otherwise write into
     detached nodes for the rest of the session. */
  st.sheetKind = null;
  st.dlList = null;
  st.dlKey = null;
}

function sheet(title, sub) {
  closeSheet();
  var s = st.el.sheet;
  var h = el("h2"); h.textContent = title; s.appendChild(h);
  if (sub) { var p = el("p", "arcbw-sub"); p.textContent = sub; s.appendChild(p); }
  s.classList.add("is-open");
  return s;
}

function rowEl(o) {
  var r = el("button", "arcbw-row" + (o.danger ? " is-danger" : ""));
  var main = el("div", "arcbw-row-main");
  var lab = el("div", "arcbw-row-label"); lab.textContent = o.label;
  main.appendChild(lab);
  if (o.sub) { var s = el("div", "arcbw-row-sub"); s.textContent = o.sub; main.appendChild(s); }
  r.appendChild(main);
  if (o.tail) { var t = el("div", "arcbw-row-tail"); t.textContent = o.tail; r.appendChild(t); }
  r.__arcRun = o.run;
  return r;
}

/* ── Start page: the pinned apps ─────────────────────────────────────── */

function showStart() {
  /* Arriving from anywhere else starts at the first tile. Keeping a row index
     from the menu or the history list and applying it to a grid of pinned
     apps lands the cursor somewhere arbitrary — most often on "+ Add a site",
     which is the one thing nobody wants pre-selected. */
  if (st.scope !== "start") st.sheetIdx = 0;
  st.scope = "start";
  syncContent("start page");
  var s = sheet("Browser", "Pinned apps open full screen. Everything else is one address away.");
  var grid = el("div", "arcbw-pins");
  st.sheetRows = [];

  /* ── The first tile is always the address ────────────────────────────
     This is where the browser failed hardest, and it failed silently. On a
     console with no pins yet the start page was ONE tile, "+ Add a site",
     and the hint bar never mentioned Options — so the address bar had no
     route to it that anybody could find by pressing things. The bench log
     of a real attempt is four unbroken minutes of a human pressing every
     direction on a page with a single item, then leaving:

         pad down  -> browser: down      (nothing moved: one row)
         pad right -> browser: right
         pad up    -> browser: up
         ... Circle, gone

     Not one Options press in the whole session, because nothing on the
     screen suggested one. So the thing everybody actually came for is now
     the first tile, it is where the cursor starts, and Cross on it opens
     the keyboard straight into the address field. Discoverability is not a
     nicety here — with no pointer and no keyboard, a control you cannot
     find is a control that does not exist. */
  var go = el("button", "arcbw-pin is-go");
  go.appendChild(el("div", "arcbw-pin-glyph", svg(G.zoom)));
  go.appendChild(el("div", "arcbw-pin-name", "Search or enter an address"));
  go.appendChild(el("div", "arcbw-pin-host", "The keyboard opens on Cross"));
  go.__arcRun = editAddress;
  grid.appendChild(go);
  st.sheetRows.push(go);

  for (var i = 0; i < st.pins.length; i++) {
    (function (pin) {
      var tile = el("button", "arcbw-pin");
      tile.style.background = "linear-gradient(150deg, " + (pin.colour || "#2b5fd0") + " 0%, #06101f 78%)";
      var glyph = el("div", "arcbw-pin-glyph");
      glyph.textContent = (pin.name || "?").slice(0, 1).toUpperCase();
      var name = el("div", "arcbw-pin-name"); name.textContent = pin.name;
      var host = el("div", "arcbw-pin-host"); host.textContent = hostOf(pin.url);
      tile.appendChild(glyph); tile.appendChild(name); tile.appendChild(host);
      tile.__arcRun = function () { openPin(pin); };
      tile.__arcRemove = function () { removePin(pin); };
      grid.appendChild(tile);
      st.sheetRows.push(tile);
    })(st.pins[i]);
  }

  var add = el("button", "arcbw-pin is-add");
  add.appendChild(el("div", "arcbw-pin-name", "+ Add a site"));
  add.__arcRun = addPin;
  grid.appendChild(add);
  st.sheetRows.push(add);

  s.appendChild(grid);
  st.sheetIdx = Math.max(0, Math.min(st.sheetIdx, st.sheetRows.length - 1));
  paintFocus();
}

/* Pinned apps are the reason this whole thing is worth having on a
   television, so they get the full screen: chrome hidden, straight into the
   page. Options brings the chrome back. */
function openPin(pin) {
  say("pin -> " + pin.url);
  closeSheet();
  goToContent();
  immersive(true);
  bridge.post({ type: "browser", cmd: "navigate", url: pin.url });
  remember(pin.url, pin.name);
}

function addPin() {
  /* Before the keyboard, not after: suspending second leaves one frame in
     which the keyboard is up and the page is still covering it. */
  suspendContent("keyboard");
  bridge.osk({
    title: "Add a site",
    mode: "url",
    value: "",
    placeholder: "example.com",
    commitLabel: "Add",
    onCommit: guard("addPin commit", function (v) {
      var r = resolveInput(v);
      if (!r || r.refuse) { toast(r && r.refuse ? r.refuse : "Nothing to add."); afterOsk(); return; }
      st.pins.push({ name: hostOf(r) || v, url: r, colour: colourFor(r) });
      save(PREF.pins, st.pins);
      afterOsk();
      showStart();
      toast("Pinned " + (hostOf(r) || v));
    }),
    onCancel: guard("addPin cancel", afterOsk)
  });
}

function removePin(pin) {
  var i = st.pins.indexOf(pin);
  if (i < 0) return;
  st.pins.splice(i, 1);
  save(PREF.pins, st.pins);
  showStart();
  toast("Removed " + pin.name);
}

/* A stable colour per host, so a pinned site keeps the same tile between
   restarts without anybody having to pick one. */
function colourFor(url) {
  var h = hostOf(url), n = 0;
  for (var i = 0; i < h.length; i++) n = (n * 31 + h.charCodeAt(i)) >>> 0;
  return "hsl(" + (n % 360) + " 58% 38%)";
}

/* ── History ─────────────────────────────────────────────────────────── */

function remember(url, title) {
  if (!url || url === "about:blank") return;
  var now = Date.now();
  st.session.push({ url: url, title: title || "", at: now });
  /* Collapse a repeat visit rather than stacking it: a history list that is
     forty copies of one video page is not a history list. */
  for (var i = 0; i < st.history.length; i++) {
    if (st.history[i].url === url) { st.history.splice(i, 1); break; }
  }
  st.history.unshift({ url: url, title: title || "", at: now });
  if (st.history.length > 300) st.history.length = 300;
  save(PREF.hist, st.history);
}

function showHistory() {
  st.scope = "history";
  syncContent("history");
  var s = sheet("History", st.session.length + " this session, " + st.history.length + " kept on this machine.");
  var list = el("div", "arcbw-list");
  st.sheetRows = [];

  if (!st.history.length) {
    list.appendChild(el("div", "arcbw-empty", "Nothing here yet."));
  } else {
    for (var i = 0; i < st.history.length && i < 120; i++) {
      (function (h) {
        var r = rowEl({
          label: h.title || hostOf(h.url) || h.url,
          sub: h.url,
          tail: ago(h.at),
          run: function () { closeSheet(); goToContent(); bridge.post({ type: "browser", cmd: "navigate", url: h.url }); }
        });
        list.appendChild(r);
        st.sheetRows.push(r);
      })(st.history[i]);
    }
    var clear = rowEl({
      label: "Clear history", sub: "Removes everything kept on this machine", danger: true,
      run: function () { st.history = []; save(PREF.hist, st.history); showHistory(); toast("History cleared."); }
    });
    list.appendChild(clear);
    st.sheetRows.push(clear);
  }
  s.appendChild(list);
  st.sheetIdx = 0;
  paintFocus();
}

function ago(t) {
  var s = Math.max(0, Math.round((Date.now() - t) / 1000));
  if (s < 60) return "just now";
  var m = Math.round(s / 60); if (m < 60) return m + " min ago";
  var h = Math.round(m / 60); if (h < 24) return h + " h ago";
  return Math.round(h / 24) + " d ago";
}

/* ═══ Downloads ════════════════════════════════════════════════════════
   The host takes every download off Chromium before its own flyout can be
   drawn (see the Downloads region in ShellHostWeb.cs for why that flyout is
   unusable here) and reports the whole list on every change. This is the
   half the human actually touches:

     - a chip in the chrome row, present only while there is something to
       say, carrying the percentage and a progress bar. A download is the
       one thing in this browser that happens while the human is doing
       something else, so it gets a permanent place on the screen rather
       than a line in a menu nobody has a reason to open;
     - a sheet listing every download of the session with a focus ring, one
       action on Cross (pause, resume, show the finished file, ask again for
       a failed one) and one on Square (cancel, or forget the row);
     - toasts for the three transitions worth interrupting for: it started,
       it landed, it failed.

   THE LIST IS THE HOST'S. Nothing here invents a state or remembers one
   across a message; the only thing measured on this side is the transfer
   rate, which is a property of two samples rather than of the download and
   so cannot be sent. Everything else is drawn from what just arrived.

   The one gap, stated rather than hidden: in full screen there is no chrome
   band and no toast (browser.css explains why), so a download that starts
   during a video is silent until the human leaves full screen — where the
   chip is waiting for them. */

function bytes(n) {
  if (typeof n !== "number" || !isFinite(n) || n < 0) return "";
  if (n < 1024) return n + " B";
  var units = ["KB", "MB", "GB", "TB"], i = -1, v = n;
  do { v /= 1024; i++; } while (v >= 1024 && i < units.length - 1);
  return (v < 10 ? v.toFixed(1) : Math.round(v)) + " " + units[i];
}

/* -1 when the server never declared a length. That is a real case (chunked
   responses, most streamed archives) and it has to read as "no percentage",
   never as 0% — a bar stuck at zero on a download that is working is the
   single most alarming thing this screen could show. */
function pctOf(d) {
  if (!d || !d.total || d.total <= 0) return -1;
  return Math.max(0, Math.min(100, Math.round((d.got / d.total) * 100)));
}

function shortFolder(p) {
  var s = String(p || "");
  var m = s.match(/([^\\\/]+)[\\\/]*$/);
  return m ? m[1] : s;
}

/* Smoothed, deliberately. A raw sample over three quarters of a second of a
   television's Wi-Fi swings by a factor of three, and a number that cannot be
   read is not a measurement. */
function sampleRate(d) {
  var now = Date.now();
  var r = st.rates[d.id];
  if (!r) { st.rates[d.id] = { got: d.got, at: now, bps: 0 }; return 0; }
  var dt = now - r.at;
  if (dt >= 700) {
    var inst = (d.got - r.got) * 1000 / dt;
    if (!(inst > 0)) inst = 0;
    r.bps = r.bps ? (r.bps * 0.6 + inst * 0.4) : inst;
    r.got = d.got;
    r.at = now;
  }
  return r.bps;
}

function findDown(id) {
  for (var i = 0; i < st.downloads.length; i++) if (st.downloads[i].id === id) return st.downloads[i];
  return null;
}

function applyDownloads(m) {
  st.downloads = m.list || [];
  st.downActive = m.active || 0;

  var live = {}, i, k;
  for (i = 0; i < st.downloads.length; i++) {
    var d = st.downloads[i];
    live[d.id] = true;
    if (d.state === "running") sampleRate(d);
    else if (st.rates[d.id]) st.rates[d.id].bps = 0;
  }
  /* A forgotten or trimmed download takes its bookkeeping with it, or the
     rate table grows for the life of the session. */
  for (k in st.rates) if (Object.prototype.hasOwnProperty.call(st.rates, k) && !live[k]) delete st.rates[k];
  for (k in st.downSeen) if (Object.prototype.hasOwnProperty.call(st.downSeen, k) && !live[k]) delete st.downSeen[k];

  announceDownload(m);
  renderDown();
  if (st.sheetKind === "downloads") renderDownloadRows();
}

/* One toast per state, and only for the download the message is about. The
   host re-sends the whole list several times a second while bytes arrive;
   without the seen-state check a 40-second download would raise forty
   identical toasts. */
function announceDownload(m) {
  var d = findDown(m.id);
  if (!d) return;
  if (st.downSeen[d.id] === d.state) return;
  var first = !(d.id in st.downSeen);
  st.downSeen[d.id] = d.state;

  if (d.state === "running") {
    if (first) toast("Downloading " + d.name + " to " + shortFolder(d.folder));
    return;
  }
  if (d.state === "done") { toast("Saved " + d.name + " in " + shortFolder(d.folder)); return; }
  if (d.state === "failed") { toast(d.name + " did not finish — " + (d.why || "it was interrupted")); return; }
  /* Pause and cancel are things the human just did on this screen; saying
     them back is noise. */
}

function renderDown() {
  if (!st.el) return;
  var chip = st.el.dl;
  var running = 0, failed = 0, i, d, lead = null;

  for (i = 0; i < st.downloads.length; i++) {
    d = st.downloads[i];
    if (d.state === "running" || d.state === "paused") { running++; if (!lead) lead = d; }
    else if (d.state === "failed") failed++;
  }

  var off = st.downloads.length === 0;
  var was = chip.classList.contains("is-off");
  /* The cursor is an INDEX into the chrome row, and this chip joins and leaves
     that row on its own schedule — a download can start while the human is
     standing on Menu. Holding the index would slide the ring sideways under
     their thumb, so the ELEMENT is remembered and its new index looked up
     afterwards. */
  var stood = (was !== off && st.scope === "chrome") ? chromeItems()[st.chromeIdx] : null;
  chip.classList.toggle("is-off", off);
  if (stood) {
    var after = chromeItems();
    for (var s = 0; s < after.length; s++) if (after[s] === stood) { st.chromeIdx = s; break; }
  }
  chip.classList.toggle("is-bad", !off && !running && failed > 0);

  if (!off) {
    var label;
    var pct = lead ? pctOf(lead) : -1;
    if (lead && lead.state === "paused") label = running > 1 ? running + " paused/running" : "Paused";
    /* "0 B" is what a size-less download reads as in its first second, and it
       looks like a download that is not working. It says Starting instead. */
    else if (lead) label = (running > 1 ? running + " · " : "") +
      (pct >= 0 ? pct + "%" : (lead.got > 0 ? bytes(lead.got) : "Starting"));
    else if (failed) label = failed > 1 ? failed + " failed" : "Failed";
    else label = "Downloads";

    var span = chip.querySelector("span");
    if (span) span.textContent = label;
    var bar = chip.querySelector(".arcbw-chip-bar");
    if (bar) {
      bar.style.width = (lead && pct >= 0) ? pct + "%" : (lead ? "100%" : "0%");
      chip.classList.toggle("is-unknown", !!lead && pct < 0);
    }
  }

  /* The chip appearing or leaving changes what the chrome row contains, and
     the cursor is an index into that row. */
  if (was !== off && st.scope === "chrome") paintFocus();
}

function downCmd(act, id) {
  say("download " + act + " " + id);
  bridge.post({ type: "browser", cmd: "download", "do": act, id: id });
}

function revealDownload(d) {
  if (!d.folder) { toast("The console does not know where that file went."); return; }
  if (typeof bridge.files !== "function") { toast("Saved at " + d.path); return; }
  say("showing " + d.path + " in the file explorer");
  var ok;
  try { ok = bridge.files(d.folder, d.path); }
  catch (err) { report("show in files", err); ok = false; }
  if (ok === false) toast("Saved at " + d.path);
}

function retryDownload(d) {
  if (!d.url) { toast("There is no address to ask for again."); return; }
  closeSheet();
  goToContent();
  bridge.post({ type: "browser", cmd: "navigate", url: d.url });
  toast("Asking for " + d.name + " again");
}

function downLine(d) {
  var host = hostOf(d.url);
  var rate = st.rates[d.id] ? st.rates[d.id].bps : 0;
  switch (d.state) {
    case "running":
      return (d.total > 0 ? bytes(d.got) + " of " + bytes(d.total) : bytes(d.got) + " so far") +
             (rate > 0 ? " · " + bytes(Math.round(rate)) + "/s" : "") +
             (host ? " · " + host : "");
    case "paused":
      return "Paused at " + bytes(d.got) + (d.total > 0 ? " of " + bytes(d.total) : "") +
             (d.canResume ? " · Cross to carry on" : " · this one cannot be resumed, only started again");
    case "done":
      return "Saved in " + (d.folder || "the downloads folder") +
             (d.total > 0 ? " · " + bytes(d.total) : (d.got > 0 ? " · " + bytes(d.got) : ""));
    case "cancelled":
      return "Cancelled" + (host ? " · " + host : "");
    default:
      return d.why || "It did not finish";
  }
}

function downTailText(d) {
  if (d.state === "running") { var p = pctOf(d); return p >= 0 ? p + "%" : bytes(d.got); }
  if (d.state === "paused") return "Paused";
  if (d.state === "done") return "Saved";
  if (d.state === "cancelled") return "Cancelled";
  return "Failed";
}

/* Cross does the obvious thing for the state the row is in, and Square is
   always the destructive one. Both are rebound whenever the state changes,
   which is why the state is part of the rebuild key below. */
function bindDownRow(r, d) {
  if (d.state === "running") {
    r.__arcRun = function () { downCmd("pause", d.id); };
    r.__arcRemove = function () { downCmd("cancel", d.id); };
  } else if (d.state === "paused") {
    r.__arcRun = function () { downCmd("resume", d.id); };
    r.__arcRemove = function () { downCmd("cancel", d.id); };
  } else if (d.state === "done") {
    r.__arcRun = function () { revealDownload(d); };
    r.__arcRemove = function () { downCmd("forget", d.id); };
  } else {
    r.__arcRun = function () { retryDownload(d); };
    r.__arcRemove = function () { downCmd("forget", d.id); };
  }
}

function fillDownRow(r, d) {
  var pct = pctOf(d);
  r.__sub.textContent = downLine(d);
  r.__tail.textContent = downTailText(d);
  r.__bar.style.width = (pct >= 0 ? pct : (d.state === "done" ? 100 : 0)) + "%";
}

function downRow(d) {
  var r = el("button", "arcbw-row arcbw-drow is-" + d.state + (d.state === "failed" ? " is-danger" : ""));
  var main = el("div", "arcbw-row-main");
  var lab = el("div", "arcbw-row-label");
  lab.textContent = d.name;
  var sub = el("div", "arcbw-row-sub");
  var bar = el("div", "arcbw-bar", "<i></i>");
  main.appendChild(lab); main.appendChild(sub); main.appendChild(bar);
  var tail = el("div", "arcbw-row-tail");
  r.appendChild(main); r.appendChild(tail);
  r.__dl = d.id; r.__sub = sub; r.__tail = tail; r.__bar = bar.firstChild;
  bindDownRow(r, d);
  fillDownRow(r, d);
  return r;
}

function downMenuTail() {
  if (st.downActive) return st.downActive + " running";
  if (!st.downloads.length) return "None yet";
  return st.downloads.length + (st.downloads.length === 1 ? " file" : " files");
}

function downSub() {
  var running = 0, done = 0, bad = 0, folder = "", i;
  for (i = 0; i < st.downloads.length; i++) {
    var d = st.downloads[i];
    if (d.state === "running" || d.state === "paused") running++;
    else if (d.state === "done") done++;
    else bad++;
    if (!folder && d.folder) folder = d.folder;
  }
  if (!st.downloads.length)
    return "Files a site sends are saved straight to this console's downloads folder and listed here.";
  var bits = [];
  if (running) bits.push(running + (running === 1 ? " in progress" : " in progress"));
  if (done) bits.push(done + " finished");
  if (bad) bits.push(bad + " stopped");
  return bits.join(" · ") + (folder ? " · saved to " + folder : "");
}

/* Rebuilt only when the set of downloads or any of their states changes;
   otherwise the numbers are written into the rows that are already there.
   A full rebuild several times a second would throw the focus ring and the
   list's scroll position away between two presses of the D-pad. */
function renderDownloadRows() {
  if (st.sheetKind !== "downloads" || !st.dlList) return;
  var i, key = [];
  for (i = 0; i < st.downloads.length; i++) key.push(st.downloads[i].id + ":" + st.downloads[i].state);
  var k = key.join(",");
  if (k === st.dlKey) {
    for (i = 0; i < st.sheetRows.length; i++) {
      var row = st.sheetRows[i];
      /* typeof, not truthiness: the footer rows have no id at all, and an id
         of 0 is a perfectly good download. */
      if (typeof row.__dl !== "number") continue;
      var d = findDown(row.__dl);
      if (d) fillDownRow(row, d);
    }
    /* The sub-line counts bytes too, and it is the line the human reads when
       the list is long enough that their row has scrolled off. */
    var sub = st.el.sheet.querySelector(".arcbw-sub");
    if (sub) sub.textContent = downSub();
    return;
  }
  st.dlKey = k;

  var list = st.dlList;
  list.innerHTML = "";
  st.sheetRows = [];

  if (!st.downloads.length) {
    list.appendChild(el("div", "arcbw-empty",
      "Nothing has been downloaded yet. When a site sends a file it is saved automatically " +
      "and appears here, with the console's own progress rather than the browser's."));
  }

  var folder = "";
  for (i = 0; i < st.downloads.length; i++) {
    var one = st.downloads[i];
    if (!folder && one.folder) folder = one.folder;
    var r = downRow(one);
    list.appendChild(r);
    st.sheetRows.push(r);
  }

  var finished = 0;
  for (i = 0; i < st.downloads.length; i++)
    if (st.downloads[i].state !== "running" && st.downloads[i].state !== "paused") finished++;

  if (folder && typeof bridge.files === "function") {
    var open = rowEl({
      label: "Open the downloads folder",
      sub: folder + " — opens in Files; the browser stays as it is behind it",
      run: function () { revealDownload({ folder: folder, path: folder }); }
    });
    list.appendChild(open); st.sheetRows.push(open);
  }
  if (finished) {
    var clear = rowEl({
      label: "Clear the finished ones",
      sub: "Empties this list only. Nothing is deleted from the disk.",
      danger: true,
      run: function () { downCmd("clear", 0); }
    });
    list.appendChild(clear); st.sheetRows.push(clear);
  }

  st.sheetIdx = Math.max(0, Math.min(st.sheetIdx, st.sheetRows.length - 1));
  paintFocus();
}

function showDownloads() {
  if (st.sheetKind !== "downloads") st.sheetIdx = 0;
  st.scope = "menu";
  syncContent("downloads");
  var s = sheet("Downloads", downSub());
  st.sheetKind = "downloads";
  st.dlKey = null;
  st.dlList = el("div", "arcbw-list");
  s.appendChild(st.dlList);
  renderDownloadRows();
  /* Ask for a fresh list rather than drawing the last one and hoping: this
     sheet can be opened long after the browser was last looked at. */
  downCmd("list", 0);
}

/* ── Menu ────────────────────────────────────────────────────────────── */

function showMenu() {
  st.scope = "menu";
  syncContent("menu");
  var t = activeTab();
  var s = sheet("Browser menu", t && t.url ? t.url : "No page loaded");
  var list = el("div", "arcbw-list");
  st.sheetRows = [];

  function add(o) { var r = rowEl(o); list.appendChild(r); st.sheetRows.push(r); }
  function sec(text) { list.appendChild(el("div", "arcbw-sec", text)); }

  sec("This page");
  add({ label: "Reload", sub: t && t.crashed ? "Restart the page process" : "Fetch it again",
        run: function () { closeSheet(); goToContent(); bridge.post({ type: "browser", cmd: "reload" }); } });
  add({ label: "Forward", sub: t && t.canForward ? "" : "Nothing ahead in this tab",
        run: function () { closeSheet(); goToContent(); bridge.post({ type: "browser", cmd: "forward" }); } });
  add({ label: "Zoom", tail: t ? Math.round((t.zoom || 1) * 100) + "%" : "100%",
        sub: "Remembered for this site", run: showZoom });
  add({ label: "Rescan the page for links",
        sub: "Use this when a site has redrawn itself and the ring cannot find anything",
        run: function () { closeSheet(); goToContent(); bridge.post({ type: "browser", cmd: "scan" }); } });
  add({ label: st.navMode === "cursor" ? "Use link navigation" : "Use the pointer",
        sub: st.navMode === "cursor"
          ? "Move between the page's links and buttons with the D-pad"
          : "A virtual mouse driven by the left stick — for sites the ring cannot read",
        tail: "L3",
        run: function () { closeSheet(); goToContent(); bridge.post({ type: "browser", cmd: "mode", mode: "toggle" }); } });

  sec("Browser");
  add({ label: "Pinned apps", sub: st.pins.length + " pinned", run: showStart });
  add({ label: "Pin this page", sub: t && t.url ? hostOf(t.url) : "Nothing to pin",
        run: function () {
          if (!t || !t.url) { toast("No page to pin."); return; }
          st.pins.push({ name: t.title || hostOf(t.url), url: t.url, colour: colourFor(t.url) });
          save(PREF.pins, st.pins);
          toast("Pinned " + hostOf(t.url));
          showMenu();
        } });
  add({ label: "History", sub: st.history.length + " entries", run: showHistory });
  add({ label: "Downloads", tail: downMenuTail(),
        sub: "Files sites have sent, where they were saved, and what stopped the ones that stopped",
        run: showDownloads });
  add({ label: "New tab", sub: st.tabs.length + " of " + st.max + " open",
        run: function () { closeSheet(); goToContent(); bridge.post({ type: "browser", cmd: "newtab", url: "" }); showStart(); } });
  if (t) add({ label: "Close this tab", sub: t.title || hostOf(t.url) || "New tab", danger: true,
        run: function () { bridge.post({ type: "browser", cmd: "closetab", tab: t.id }); showMenu(); } });
  add({ label: "Search engine", tail: (ENGINES[st.engine] || ENGINES.duckduckgo).name, run: showEngines });
  add({ label: "Extensions", tail: extensionTail(),
        sub: "Ad blocking and anything else — install one from a download or a USB stick",
        run: showExtensions });

  sec("Leave");
  add({ label: "Close the browser",
        sub: st.downActive
          ? "Tabs stay loaded, and so do the " + st.downActive + " download" +
            (st.downActive === 1 ? "" : "s") + " still running"
          : "Tabs stay loaded and come back where you left them",
        run: function () { close(); } });
  /* The warning is not decoration. Downloads belong to the content WebViews,
     so tearing every tab down stops them, and a human who has been waiting
     ten minutes for a file deserves to be told that before they press it
     rather than after. */
  add({ label: "Close the browser and every tab",
        sub: st.downActive
          ? "Stops " + st.downActive + " download" + (st.downActive === 1 ? "" : "s") +
            " that " + (st.downActive === 1 ? "is" : "are") + " still running"
          : "Frees the memory the pages are using",
        danger: true,
        run: function () { bridge.post({ type: "browser", cmd: "quit" }); close(); } });

  s.appendChild(list);
  st.sheetIdx = 0;
  paintFocus();
}

/* ═══ Extensions ═══════════════════════════════════════════════════════
   Installed from the sofa, with a controller, on a machine with no mouse,
   no file dialog and no web store.

   WHERE AN EXTENSION COMES FROM. Two places, and the host looks in both:
   a .zip or .crx this browser has DOWNLOADED (most extensions worth having
   publish exactly that on their releases page), and anything on a plugged-in
   USB stick. Both end as rows on this sheet; Cross installs one. The host
   unpacks it into the console's own extensions folder and hands the folder to
   the profile, which loads it into every open page immediately and remembers
   it for next boot — see the Installing extensions region in ShellHostWeb.cs.

   WHY NOT THE CHROME WEB STORE. It serves .crx to Chrome-branded browsers
   behind a signed request this host cannot honestly fake, so a store button
   here would be a button that fails for reasons nobody in a living room can
   do anything about. A .crx already on the disk installs fine.

   A FAILED EXTENSION IS A ROW, NOT A LOG LINE. An ad blocker that silently
   did not load leaves a television showing adverts, which from the sofa is
   indistinguishable from one that loaded and found nothing to block. So
   anything on disk that did not load is listed here with the reason. */

var exts = { ready: false, folder: "", list: [], candidates: [] };

function extensionTail() {
  var xs = exts.list;
  if (!xs.length) return "None";
  var bad = 0, off = 0, i;
  for (i = 0; i < xs.length; i++) { if (!xs[i].ok) bad++; else if (!xs[i].enabled) off++; }
  if (bad) return bad + " failed";
  if (off) return (xs.length - off) + " of " + xs.length + " on";
  return xs.length === 1 ? "1 installed" : xs.length + " installed";
}

function extCmd(act, o) {
  o = o || {};
  say("extension " + act + " " + (o.id || o.path || ""));
  bridge.post({ type: "browser", cmd: "extension", "do": act, id: o.id || "", path: o.path || "" });
}

function extSub() {
  if (!exts.ready) return "The browser has not started a page yet, so nothing can be installed into it.";
  var on = 0, i;
  for (i = 0; i < exts.list.length; i++) if (exts.list[i].ok && exts.list[i].enabled) on++;
  if (!exts.list.length)
    return "Nothing is installed. Extensions run inside every page this browser loads — an ad " +
           "blocker is the one most people want on a television.";
  return on + " running · installed into " + exts.folder;
}

/* Opening the sheet and drawing it are two things, and they have to stay two
   things. The host answers a "list" with an {ev:"extensions"} message, and the
   page redraws when one arrives — so a draw that also asked for a list was a
   loop that ran as fast as the message channel could carry it. The bench found
   it in one run: twenty-four list pushes inside a fifth of a second, and a walk
   that never got another press in edgeways. ASK ON THE WAY IN, ONLY HERE. */
function showExtensions() {
  if (st.sheetKind !== "extensions") st.sheetIdx = 0;
  renderExtensions();
  /* Rescan on the way in: a .zip downloaded two minutes ago has to be on this
     screen without anybody being told to press "look again" first — the whole
     route into this sheet is "I just downloaded an extension". */
  extCmd("list");
}

function renderExtensions() {
  st.scope = "menu";
  syncContent("extensions");
  var s = sheet("Extensions", extSub());
  st.sheetKind = "extensions";
  var list = el("div", "arcbw-list");
  st.sheetRows = [];
  var i;

  function add(o) { var r = rowEl(o); list.appendChild(r); st.sheetRows.push(r); return r; }
  function sec(text) { list.appendChild(el("div", "arcbw-sec", text)); }

  if (exts.list.length) {
    sec("Installed");
    for (i = 0; i < exts.list.length; i++) {
      (function (x) {
        if (!x.ok) {
          /* No id, so nothing can be switched: this one is on disk and did not
             load. The reason is the whole content of the row. */
          add({ label: x.name, sub: x.detail || "It did not load", tail: "Failed", danger: true,
                run: function () { toast(x.name + " — " + (x.detail || "it did not load")); } });
          return;
        }
        var r = add({
          label: x.name,
          sub: x.enabled ? "Running in every page this browser opens"
                         : "Installed but switched off — nothing is loading it into pages",
          tail: x.enabled ? "On" : "Off",
          run: function () { extCmd(x.enabled ? "disable" : "enable", { id: x.id }); }
        });
        r.__arcRemove = function () { confirmRemoveExtension(x); };
      })(exts.list[i]);
    }
  }

  sec(exts.list.length ? "Add another" : "Add one");
  if (exts.candidates.length) {
    for (i = 0; i < exts.candidates.length; i++) {
      (function (c) {
        add({
          label: c.name,
          sub: c.where + (c.kind === "folder" ? " · an unpacked extension folder"
                                              : " · " + c.kind.toUpperCase() + (c.size ? " · " + bytes(c.size) : "")),
          tail: "Install",
          run: function () {
            toast("Installing " + c.name + "…");
            extCmd("install", { path: c.path });
          }
        });
      })(exts.candidates[i]);
    }
  } else {
    list.appendChild(el("div", "arcbw-empty",
      "Nothing to install was found. Download an extension's .zip in this browser — most publish " +
      "one on their releases page — or plug in a USB stick with the unpacked folder on it, then " +
      "look again."));
  }
  add({ label: "Look again", sub: "Re-checks the downloads folder and any plugged-in drive",
        run: function () { extCmd("list"); toast("Looking…"); } });

  s.appendChild(list);
  st.sheetIdx = Math.max(0, Math.min(st.sheetIdx, st.sheetRows.length - 1));
  paintFocus();
}

/* Removing deletes files off the disk, so it is asked rather than done. The
   confirmation is its own sheet because a modal dialog would have to be drawn
   over the page, and nothing here can be. */
function confirmRemoveExtension(x) {
  st.scope = "menu";
  st.sheetKind = "extconfirm";
  syncContent("remove extension");
  var s = sheet("Remove " + x.name + "?",
    "It stops running everywhere immediately, and the copy in the console's extensions folder is " +
    "deleted. Anything you installed it from — a download, a USB stick — is left alone.");
  var list = el("div", "arcbw-list");
  st.sheetRows = [];
  var keep = rowEl({ label: "Keep it", sub: "Nothing changes", run: showExtensions });
  var go = rowEl({ label: "Remove it", sub: x.name, danger: true,
    run: function () { extCmd("remove", { id: x.id }); showExtensions(); } });
  list.appendChild(keep); st.sheetRows.push(keep);
  list.appendChild(go); st.sheetRows.push(go);
  s.appendChild(list);
  st.sheetIdx = 0;
  paintFocus();
}

function showCrash(m) {
  st.scope = "menu";
  st.el.wrap.classList.remove("is-immersive");
  syncContent("crash sheet");
  var t = activeTab();
  var s = sheet("This page stopped responding",
    "Its process ended (" + (m.reason || "unknown") + "). Nothing else was affected: web pages run in " +
    "their own processes, in a separate browser from the one drawing this interface, so the console " +
    "and everything else on it carried on.");
  var list = el("div", "arcbw-list");
  st.sheetRows = [];
  function add(o) { var r = rowEl(o); list.appendChild(r); st.sheetRows.push(r); }

  add({ label: "Reload the page", sub: t ? t.url : "",
        run: function () { closeSheet(); goToContent(); bridge.post({ type: "browser", cmd: "reload" }); } });
  add({ label: "Close this tab", danger: true,
        run: function () {
          if (t) bridge.post({ type: "browser", cmd: "closetab", tab: t.id });
          showStart();
        } });
  add({ label: "Pinned apps", run: showStart });

  s.appendChild(list);
  st.sheetIdx = 0;
  paintFocus();
}

var ZOOMS = [75, 90, 100, 110, 125, 150, 175, 200, 250];

function showZoom() {
  st.scope = "menu";
  /* This sheet is reachable straight from the chrome's zoom chip, with a
     page still on the screen. Without this it drew behind the content
     window and the human saw nothing happen. */
  syncContent("zoom");
  var t = activeTab();
  var cur = t ? Math.round((t.zoom || 1) * 100) : 100;
  var s = sheet("Zoom", "Television viewing distance is about three metres; 125% or 150% is usually right. " +
                        "Whatever you choose is remembered for this site.");
  var list = el("div", "arcbw-list");
  st.sheetRows = [];
  for (var i = 0; i < ZOOMS.length; i++) {
    (function (pct) {
      var r = rowEl({
        label: pct + "%", tail: pct === cur ? "current" : "",
        run: function () { setZoom(pct); showZoom(); }
      });
      list.appendChild(r); st.sheetRows.push(r);
      if (pct === cur) st.sheetIdx = st.sheetRows.length - 1;
    })(ZOOMS[i]);
  }
  s.appendChild(list);
  paintFocus();
}

function setZoom(pct) {
  var t = activeTab();
  bridge.post({ type: "browser", cmd: "zoom", pct: pct });
  if (t && t.url) { st.zoom[hostOf(t.url)] = pct; save(PREF.zoom, st.zoom); }
  toast("Zoom " + pct + "%");
}

function showEngines() {
  st.scope = "menu";
  syncContent("search engine");
  var s = sheet("Search engine", "What a typed phrase is sent to when it is not an address.");
  var list = el("div", "arcbw-list");
  st.sheetRows = [];
  for (var k in ENGINES) {
    if (!Object.prototype.hasOwnProperty.call(ENGINES, k)) continue;
    (function (key) {
      var r = rowEl({
        label: ENGINES[key].name, sub: ENGINES[key].url.replace(/\?.*$/, ""),
        tail: key === st.engine ? "current" : "",
        run: function () { st.engine = key; save(PREF.engine, key); showEngines(); toast("Searching with " + ENGINES[key].name); }
      });
      list.appendChild(r); st.sheetRows.push(r);
    })(k);
  }
  s.appendChild(list);
  st.sheetIdx = 0;
  paintFocus();
}

/* ── Tab switcher lives on the chrome row, not a sheet ───────────────── */

function goToContent() {
  st.scope = "content";
  closeSheet();
  syncContent("into the page");
  pushBounds();
  renderStage();
  paintFocus();
  /* Worth one line in the log every time, because it is the thing that keeps
     the native escape hatch working. Keyboard focus must stay with the SHELL
     WebView while a page is up: that is what routes Esc/F2/F3 to the host's
     accelerator hook. Nothing here moves focus to the content window, and if
     something ever does, this line is where it will show up. */
  var mine = false;
  try { mine = document.hasFocus(); } catch (e) {}
  say("content pane active; the shell WebView still holds keyboard focus = " + mine);
}

/* Chrome off, page fills the display. The stage rectangle changes as a
   result, so the host has to be told again — and not on the same frame, or
   it is measured before the chrome has finished leaving the layout. */
function immersive(on) {
  st.el.wrap.classList.toggle("is-immersive", !!on);
  st.lastBounds = "";
  setTimeout(guard("immersive bounds", pushBounds), 40);
  setTimeout(guard("immersive bounds", pushBounds), 220);
  say("full screen " + (on ? "on" : "off") + " (shell-level: chrome hidden, content fills the display)");
  if (!on) toast("Left full screen");
}

function goToChrome() {
  st.scope = "chrome";
  closeSheet();
  st.el.wrap.classList.remove("is-immersive");
  /* The chrome bar is a thin strip; there is no need to hide the page for
     it, and not hiding it means the human can see what they are navigating
     away from. syncContent() also drops keyboard focus back to the shell,
     because the scope is no longer "content". */
  syncContent("into the chrome");
  /* ALWAYS the address bar, not "the address bar unless the index happened
     to survive from last time". Reaching for Options means "I want to go
     somewhere else" every time, and a cursor that lands on whichever tab was
     last touched is the reason this was hard to reach at all. The tab strip
     is one press of Up from here. */
  chromeGo(1, 0);
  paintFocus();
  say("chrome active, on the address bar (Up for the tabs, Cross to type)");
}

/* ═══ Address bar ══════════════════════════════════════════════════════ */

function editAddress() {
  var t = activeTab();
  suspendContent("keyboard");
  bridge.osk({
    title: "Address or search",
    mode: "url",
    value: (t && t.url && t.url !== "about:blank") ? t.url : "",
    placeholder: "Search " + (ENGINES[st.engine] || ENGINES.duckduckgo).name + ", or type an address",
    commitLabel: "Go",
    onCommit: guard("address commit", function (v) {
      var r = resolveInput(v);
      afterOsk();
      if (!r) { return; }
      if (r.refuse) { toast(r.refuse); return; }
      goToContent();
      bridge.post({ type: "browser", cmd: "navigate", url: r });
    }),
    onCancel: guard("address cancel", afterOsk)
  });
}

/* The keyboard covered the page; put it back exactly as it was. The chrome
   bar counts as "page visible" — it is a strip at the top, not a surface,
   and hiding the page behind it would make going to the address bar look
   like closing the tab. */
function afterOsk() {
  if (!st.open) return;
  resumeContent("keyboard");
}

/* ═══ Text fields inside the page ══════════════════════════════════════
   arcnav.js posts what the field currently holds; the OSK edits it; the
   value goes back down and is written through the native setter so that a
   framework-controlled input keeps it. */

/* NOTHING IN HERE MAY LOG m.value.
   This is the one message in the whole browser that carries what a human
   typed, and one of the fields it carries is a password. The value goes from
   the page, through the host (which relays it to this page and does not write
   it to the log), into the keyboard, and back the same way. It is never a
   toast, never a say(), never part of an error string. The only things
   written to the log here are the field's NAME and whether it was secure. */
function editPageField(m) {
  var secure = !!m.secure || m.inputType === "password";
  var title = m.label || "Enter text";

  /* arcnav sends the mapped mode; inputType is kept as the fallback for a
     page injected by an older build that predates it. */
  var mode = m.mode ||
    (m.inputType === "password" ? "password"
     : m.inputType === "number" ? "number"
     : m.inputType === "url"    ? "url" : "text");

  st.pendingEdit = true;
  suspendContent("keyboard");
  say("keyboard up for " + (secure ? "a password field" : "field \"" + title + "\"") +
      " (" + mode + (m.why ? ", " + m.why : "") + ")");

  bridge.osk({
    title: title,
    mode: mode,
    value: String(m.value === undefined ? "" : m.value),
    maxLength: (typeof m.maxLength === "number" && m.maxLength > 0) ? m.maxLength : null,
    /* A search box still says "Search" on its commit key even though it uses
       the plain text layout — the label is what the human reads, the mode is
       what the keys do. */
    commitLabel: m.inputType === "search" ? "Search" : (mode === "url" ? "Go" : "Done"),
    onCommit: guard("page field commit", function (v) {
      st.pendingEdit = null;
      bridge.post({ type: "browser", cmd: "text", payload: { value: String(v) } });
      say("keyboard committed into " + (secure ? "the password field" : "\"" + title + "\""));
      afterOsk();
    }),
    onCancel: guard("page field cancel", function () {
      st.pendingEdit = null;
      bridge.post({ type: "browser", cmd: "cancel" });
      say("keyboard cancelled; the field was left as it was");
      afterOsk();
    })
  });
}

function choosePageOption(m) {
  st.pendingSelect = true;
  st.scope = "menu";
  syncContent("page dropdown");
  var s = sheet(m.label || "Choose", "From the page.");
  var list = el("div", "arcbw-list");
  st.sheetRows = [];
  var opts = m.options || [];
  for (var i = 0; i < opts.length; i++) {
    (function (o, idx) {
      var r = rowEl({
        label: o.label || o.value || "(blank)",
        tail: idx === m.index ? "current" : "",
        run: function () {
          st.pendingSelect = null;
          bridge.post({ type: "browser", cmd: "option", index: idx });
          goToContent();
        }
      });
      list.appendChild(r); st.sheetRows.push(r);
      if (idx === m.index) st.sheetIdx = idx;
    })(opts[i], i);
  }
  s.appendChild(list);
  paintFocus();
}

/* ═══ Toast ════════════════════════════════════════════════════════════ */

function toast(text) {
  if (!st.el) return;
  /* Always logged as well as shown. A toast is the browser telling the human
     something it decided — "four tabs is the limit", "that page stopped
     responding" — and those decisions have to be readable afterwards in an
     unattended run, where nobody was there to see the toast. */
  say("toast: " + text);
  /* In full screen the chrome band is gone and there is nowhere a toast could
     be drawn that the page would not cover; see browser.css. */
  if (st.el.wrap.classList.contains("is-immersive")) return;
  st.el.toast.textContent = text;
  st.el.toast.classList.add("is-up");
  clearTimeout(st.toastTimer);
  st.toastTimer = setTimeout(function () {
    try { st.el.toast.classList.remove("is-up"); } catch (e) {}
  }, 2600);
}

/* ═══ Messages from the host ═══════════════════════════════════════════ */

function hostMessage(m) {
  if (!m || m.type !== "browser") return false;

  switch (m.ev) {
    case "tabs":
      st.tabs = m.tabs || [];
      st.active = m.active || 0;
      st.max = m.max || 4;
      if (st.open) { renderTabs(); renderAddress(); renderStage(); paintFocus(); }
      return true;

    case "opened":
      st.unavailable = null;
      return true;

    /* An extension that did not load must be VISIBLE. The failure mode this
       guards against is specific: an ad blocker that silently did not load
       leaves a browser showing adverts on a television, and from the sofa
       that is indistinguishable from one that loaded and found nothing to
       block. So a failure is a toast and a menu row, not a log line. */
    /* The whole extension picture: what is installed, what failed, and what
       is sitting on disk waiting to be installed. */
    case "extensions":
      exts = {
        ready: !!m.ready,
        folder: m.folder || "",
        list: m.list || [],
        candidates: m.candidates || []
      };
      say("extensions: " + exts.list.length + " known, " + exts.candidates.length + " installable found ("
          + (m.reason || "") + ")");
      /* renderExtensions, NOT showExtensions: the latter asks for a list, and
         this is the answer to one. */
      if (st.sheetKind === "extensions") renderExtensions();
      return true;

    /* The answer to an install, enable, disable or remove. Always said out
       loud: these are the presses whose effect is invisible until a page is
       reloaded, so silence would read as nothing having happened. */
    case "extresult":
      say("extension result ok=" + m.ok + ": " + m.detail);
      toast(m.detail || (m.ok ? "Done" : "That did not work"));
      return true;

    case "extension":
      if (!st.extensions) st.extensions = [];
      {
        /* Keyed by name, not appended. The host loads extensions once per
           process, but the browser can be closed and reopened many times
           inside that process, and a list that grew on every report would
           read "2 failed" when one extension failed twice as many times as
           it existed. Replacing also means a later report about the same
           extension is the one that counts. */
        var rec = { name: m.name, ok: !!m.ok, detail: m.detail || "" }, seen = false;
        for (var xi = 0; xi < st.extensions.length; xi++) {
          if (st.extensions[xi].name === rec.name) { st.extensions[xi] = rec; seen = true; break; }
        }
        if (!seen) st.extensions.push(rec);
      }
      if (m.ok) {
        say("extension loaded: " + m.name);
      } else {
        say("EXTENSION FAILED: " + m.name + " - " + m.detail);
        toast(m.name + " did not load — " + m.detail);
      }
      return true;

    case "closed":
    case "empty":
      return true;

    case "unavailable":
      st.unavailable = m.detail || "The browser is not available in this host build.";
      say("unavailable: " + st.unavailable);
      if (st.open) { toast(st.unavailable); }
      return true;

    case "navfail":
      toast("Could not open that address.");
      return true;

    /* The whole list, on every change. See the Downloads section above. */
    case "downloads":
      applyDownloads(m);
      return true;

    /* Pause, resume or cancel threw on the operation itself. Rare, and worth
       saying out loud: the row would otherwise sit there looking as though
       the press had done something. */
    case "downfail":
      say("download " + m.id + " could not be " + m.act + ": " + m.detail);
      toast("That download could not be " + (m.act || "changed") + " — " + (m.detail || "the browser refused"));
      return true;

    case "backresult":
      /* The host answered whether the page had anywhere to go back to. If it
         did not, Circle means what it means everywhere else in this shell:
         leave the thing you are in. */
      if (!m.went) leaveContent();
      return true;

    case "crashed":
      say("tab " + m.tab + " crashed: " + m.kind + " / " + m.reason);
      renderStage();
      /* A dead renderer leaves its window on screen showing nothing, and
         because that window is a child HWND the shell cannot draw an
         explanation over it. So the content view is hidden and a real sheet
         takes its place — otherwise the human is looking at a grey
         rectangle with no way to find out what happened or what to do. */
      if (m.tab === st.active) showCrash(m);
      return true;

    case "fullscreen":
      st.el.wrap.classList.toggle("is-immersive", !!m.on);
      /* The chrome disappearing changes the stage rectangle, and the host
         needs the new one before the video fills it. */
      setTimeout(pushBounds, 30);
      return true;

    case "arcnav":
      return fromPage(m.msg || {});
  }
  return false;
}

/* Messages from the injected navigation layer, relayed by the host. */
function fromPage(m) {
  switch (m.ev) {
    case "ready":
      say("page ready: " + m.title + " (" + m.items + " targets) " + m.url);
      remember(m.url, m.title);
      applyRememberedZoom(m.url);
      return true;
    case "mode":
      st.navMode = m.mode;
      renderHints();
      toast(m.mode === "cursor" ? "Pointer mode — left stick moves, Cross clicks"
                                : "Link mode — D-pad moves between links");
      return true;
    case "focus":
      /* Logged, not just stored. Where the ring actually lands is the only
         way to tell a site spatial navigation reads well from one where it
         is technically working and practically useless, and that judgement
         has to be makeable from a log after an unattended run. */
      st.lastFocus = m.label;
      /* Kept because it decides what Triangle means and what the hint bar
         says. m.label is the field's NAME, never its contents — arcnav.js
         guarantees that; see describe() there. */
      st.focusKind = m.kind;
      say("focus [" + m.kind + "] " + m.label + (m.href ? "  -> " + m.href : ""));
      renderHints();
      return true;
    case "nofocus":
      /* The honest message, not a spinner. This is the case cursor mode
         exists for and the human is told so rather than left pressing a
         D-pad that does nothing. */
      toast("Nothing on this page can be reached with the D-pad. Press L3 for the pointer.");
      return true;
    case "edit":
      editPageField(m);
      return true;
    case "select":
      choosePageOption(m);
      return true;
    case "media":
      st.media = m;
      renderHints();
      return true;
    case "log":
      say("page: " + m.text);
      return true;
  }
  return false;
}

function applyRememberedZoom(url) {
  var h = hostOf(url);
  if (!h) return;
  var pct = st.zoom[h];
  var t = activeTab();
  var cur = t ? Math.round((t.zoom || 1) * 100) : 100;
  if (pct && pct !== cur) {
    bridge.post({ type: "browser", cmd: "zoom", pct: pct });
    say("restored zoom " + pct + "% for " + h);
  }
}

/* ═══ The pad ══════════════════════════════════════════════════════════
   One entry point, exactly like ArcOSK and ArcFiles. While the browser is
   open it consumes everything: it is modal over the shell, and an action it
   does not recognise must not fall through to the home rail underneath. */

var ALIAS = {
  cross: "activate", launch: "activate", select: "activate", enter: "activate",
  circle: "back", back: "back", esc: "back",
  square: "square", triangle: "menu", appopt: "menu",
  options: "chrome", cc: "chrome", touchpad: "chrome",
  l1: "prev", tabplay: "prev", lb: "prev",
  r1: "next", tabmedia: "next", rb: "next",
  up: "up", down: "down", left: "left", right: "right",
  l3: "l3", r3: "r3", create: "history", guide: "guide"
};

/* Everything the content pane wants sent straight down, under its literal
   button name — arcnav.js has its own vocabulary and the shell should not
   be translating between two of them. */
var TO_PAGE = {
  up: "up", down: "down", left: "left", right: "right",
  activate: "cross", square: "square", prev: "l1", next: "r1", l3: "l3", r3: "r3"
};

function handleAction(a, phase) {
  if (!st.open) return false;

  /* The keyboard is modal over the browser, as it is over everything. */
  try { if (bridge.oskIsOpen()) return false; } catch (e) {}

  if (phase === "release" || phase === "repeat") {
    /* Repeats matter for the page (holding down to scroll a long article)
       and nowhere else: sheet rows move one at a time by design. */
    if (phase === "repeat" && st.scope === "content") return relay(a, phase);
    return true;
  }

  var name = ALIAS[String(a).toLowerCase()] || String(a).toLowerCase();

  if (name === "guide") return true;        // the PS button is the shell's, not ours

  if (st.scope === "content") return contentAction(name, a);
  if (st.scope === "chrome") return chromeAction(name);
  return sheetAction(name);
}

function relay(a, phase) {
  var name = ALIAS[String(a).toLowerCase()] || String(a).toLowerCase();
  var button = TO_PAGE[name];
  if (!button) return true;
  bridge.post({ type: "browser", cmd: "pad", action: button, phase: phase || "press" });
  return true;
}

function contentAction(name, raw) {
  switch (name) {
    case "square":
      /* Full screen is done twice over, on purpose.
         The shell's half - chrome hidden, content window given the whole
         display - always works and needs nobody's permission. The page's
         half is asked for in parallel by arcnav, and now succeeds too,
         because discrete actions reach the page through ExecuteScriptAsync
         and therefore carry a user activation. Where a site has its own
         fullscreen UI that is much better than a bare stretched video, and
         where it has none the shell's half alone is still a full screen. */
      immersive(!st.el.wrap.classList.contains("is-immersive"));
      relay(raw, "press");
      return true;
    case "back":
      /* Circle is a chain, and this is the order the human expects:
         first the page's own history, then out of the browser. The host
         answers whether it could go back; see "backresult". */
      bridge.post({ type: "browser", cmd: "back" });
      return true;
    case "chrome":  goToChrome(); return true;

    /* Triangle is the keyboard when there is a field to type into, and the
       menu the rest of the time. The keyboard has to be reopenable: it is
       dismissible with Circle, and a keyboard you can dismiss and not get
       back is a field you cannot correct. arcnav.js reopens it on whatever
       is focused, so this only has to get the press down there. */
    case "menu":
      if (st.focusKind === "text") {
        bridge.post({ type: "browser", cmd: "pad", action: "triangle", phase: "press" });
        return true;
      }
      showMenu();
      return true;

    case "history": showHistory(); return true;
    default:
      return relay(raw, "press");
  }
}

function leaveContent() {
  /* Nowhere left to go back to in the page. Show the start page rather
     than dropping straight out of the browser — one more Circle from there
     leaves entirely, which makes the exit deliberate instead of accidental
     halfway through a video. */
  say("no page history left; showing the start page");
  showStart();
}

function chromeAction(name) {
  var items = chromeItems();
  var at = chromeAt();
  switch (name) {
    case "left":  chromeGo(at.row, at.col - 1); paintFocus(); return true;
    case "right": chromeGo(at.row, at.col + 1); paintFocus(); return true;

    /* Up from the address row goes to the tab strip; up from the tab strip
       is the only Up that leaves. Down is the mirror of it. */
    case "up":
      if (at.row === 1) { chromeGo(0, Math.min(at.col, at.rows[0].length - 1)); paintFocus(); }
      else goToContent();
      return true;
    case "down":
      if (at.row === 0) { chromeGo(1, 0); paintFocus(); }
      else goToContent();
      return true;

    case "back":  goToContent(); return true;

    /* Options again, already in the chrome: go straight to the address bar.
       It is the dedicated shortcut — one button from anywhere in the
       browser to the thing people actually want, and pressing it twice from
       a page is never worse than pressing it once. */
    case "chrome": chromeGo(1, 0); paintFocus(); return true;

    case "menu":  showMenu(); return true;
    case "prev":  cycleTab(-1); return true;
    case "next":  cycleTab(1); return true;
    case "square":
      /* Close the focused tab from the strip. */
      {
        var node = items[st.chromeIdx];
        if (node && node.__arcTab) { bridge.post({ type: "browser", cmd: "closetab", tab: node.__arcTab }); return true; }
      }
      return true;
    case "activate":
      {
        var n = items[st.chromeIdx];
        if (!n) return true;
        if (n.__arcTab) { bridge.post({ type: "browser", cmd: "activate", tab: n.__arcTab }); goToContent(); return true; }
        if (n.__arcNew) {
          if (st.tabs.length >= st.max) { toast("Four tabs is the limit — each one is its own process."); return true; }
          bridge.post({ type: "browser", cmd: "newtab", url: "" });
          showStart();
          return true;
        }
        if (n === st.el.addr) { editAddress(); return true; }
        if (n === st.el.zoom) { showZoom(); return true; }
        if (n === st.el.dl)   { showDownloads(); return true; }
        if (n === st.el.menu) { showMenu(); return true; }
      }
      return true;
  }
  return true;
}

function cycleTab(d) {
  if (!st.tabs.length) return;
  var i = 0;
  for (var k = 0; k < st.tabs.length; k++) if (st.tabs[k].id === st.active) i = k;
  i = (i + d + st.tabs.length) % st.tabs.length;
  bridge.post({ type: "browser", cmd: "activate", tab: st.tabs[i].id });
}

function sheetAction(name) {
  var rows = st.sheetRows;
  var isGrid = st.scope === "start";

  switch (name) {
    case "up":
      /* Up off the top row of the start page goes to the chrome, because
         that is where the chrome IS — the tab strip and the address bar are
         drawn directly above this grid. Clamping here instead was the other
         half of the unreachable address bar: the one gesture that matches
         what is on the screen did nothing at all. */
      if (isGrid && st.sheetIdx < gridCols()) { goToChrome(); return true; }
      st.sheetIdx = Math.max(0, st.sheetIdx - (isGrid ? gridCols() : 1)); paintFocus(); return true;
    case "down":
      st.sheetIdx = Math.min(rows.length - 1, st.sheetIdx + (isGrid ? gridCols() : 1)); paintFocus(); return true;
    case "left":
      if (isGrid) { st.sheetIdx = Math.max(0, st.sheetIdx - 1); paintFocus(); }
      return true;
    case "right":
      if (isGrid) { st.sheetIdx = Math.min(rows.length - 1, st.sheetIdx + 1); paintFocus(); }
      return true;
    case "prev": st.sheetIdx = Math.max(0, st.sheetIdx - 6); paintFocus(); return true;
    case "next": st.sheetIdx = Math.min(rows.length - 1, st.sheetIdx + 6); paintFocus(); return true;

    case "activate":
      {
        var n = rows[st.sheetIdx];
        if (n && typeof n.__arcRun === "function") {
          try { n.__arcRun(); } catch (err) { report("sheet row", err); }
        }
      }
      return true;

    case "square":
      {
        var r = rows[st.sheetIdx];
        if (r && typeof r.__arcRemove === "function") { try { r.__arcRemove(); } catch (err) { report("remove", err); } }
      }
      return true;

    case "menu":
      if (st.scope === "menu") { goBackFromSheet(); return true; }
      showMenu(); return true;

    case "back":
      if (st.pendingSelect) { st.pendingSelect = null; bridge.post({ type: "browser", cmd: "cancel" }); }
      goBackFromSheet();
      return true;

    case "chrome":
      goToChrome(); return true;
  }
  return true;
}

function gridCols() {
  /* Read the real column count off the grid rather than assuming one: the
     tile size is a min-max and the count changes with the viewport. */
  try {
    var grid = st.el.sheet.querySelector(".arcbw-pins");
    if (!grid) return 1;
    var cs = getComputedStyle(grid).gridTemplateColumns;
    var n = cs ? cs.split(" ").filter(function (s) { return s && s !== "0px"; }).length : 1;
    return Math.max(1, n);
  } catch (e) { return 4; }
}

function goBackFromSheet() {
  if (st.scope === "start") {
    /* Circle on the start page is the way out of the browser, and it is the
       only way out that does not need the menu. */
    close();
    return;
  }
  var t = activeTab();
  if (t && t.url && t.url !== "about:blank") goToContent();
  else showStart();
}

/* ═══ Open / close ═════════════════════════════════════════════════════ */

function open(cfg) {
  cfg = cfg || {};
  if (st.open) return true;
  if (!st.el) buildDOM();

  st.pins = load(PREF.pins, null) || DEFAULT_PINS.slice();
  st.history = load(PREF.hist, []) || [];
  st.zoom = load(PREF.zoom, {}) || {};
  st.engine = load(PREF.engine, "duckduckgo");
  if (!ENGINES[st.engine]) st.engine = "duckduckgo";
  st.session = [];
  st.onExit = cfg.onExit || null;
  st.navMode = "spatial";
  st.media = null;
  st.lastBounds = "";

  /* A fresh session starts holding nothing, and with no memory of what was
     last posted — the host has torn its tabs down and back up, so the first
     apply() must actually send rather than decide it is already correct. */
  clearHolds("browser opening");
  shownNow = null; focusNow = null;

  st.open = true;
  st.el.wrap.classList.add("is-open");
  st.el.wrap.setAttribute("aria-hidden", "false");
  st.el.wrap.classList.remove("is-immersive");

  renderTabs(); renderAddress(); renderStage(); renderDown();

  bridge.post({ type: "browser", cmd: "open", url: cfg.url || "" });
  /* So the menu's Extensions row can say how many are on before anybody opens
     it. Cheap, and it is the only thing that would otherwise read "None" on a
     console that has an ad blocker running. */
  bridge.post({ type: "browser", cmd: "extension", "do": "list" });

  /* The layout has to exist before its rectangle means anything, and the
     root font-size is viewport-derived so the first frame is not final. */
  setTimeout(guard("bounds", pushBounds), 0);
  setTimeout(guard("bounds", pushBounds), 120);
  clearInterval(st.boundsTimer);
  st.boundsTimer = setInterval(guard("bounds tick", function () {
    pushBounds();
    keepFocusPainted();
    /* The one hold that is not ours to end. The keyboard can be closed by
       something other than its own commit/cancel — the shell tearing it
       down, or a re-open with a new config that supersedes it — and a
       "keyboard" hold left behind after it has gone means a blank rectangle
       where the web page should be, with nothing on the screen explaining
       it. Cheap to check, and it is the difference between a stuck browser
       and a browser that fixes itself within a second. */
    if (holds.keyboard) {
      var up = false;
      try { up = !!bridge.oskIsOpen(); } catch (e) {}
      if (!up) resumeContent("keyboard");
    }
  }), 700);
  addEventListener("resize", onResize);

  if (cfg.url) { goToContent(); }
  else { showStart(); }

  say("open (" + st.pins.length + " pins, " + st.history.length + " history entries, engine " + st.engine + ")");
  return true;
}

var onResize = guard("resize", function () { st.lastBounds = ""; pushBounds(); });

function close() {
  if (!st.open) return;
  st.open = false;
  clearInterval(st.boundsTimer);
  st.boundsTimer = 0;
  removeEventListener("resize", onResize);
  closeSheet();
  clearHolds("browser closed");
  shownNow = null; focusNow = null;
  bridge.post({ type: "browser", cmd: "close" });
  try {
    st.el.wrap.classList.remove("is-open", "is-immersive");
    st.el.wrap.setAttribute("aria-hidden", "true");
  } catch (e) {}
  say("closed");
  try { if (st.onExit) st.onExit(); } catch (err) { report("onExit", err); }
}

/* ═══ Public surface ═══════════════════════════════════════════════════ */

root.ArcBrowser = {
  version: VERSION,
  attach: guard("attach", function (b) {
    if (!b) return;
    if (b.post) bridge.post = b.post;
    if (b.osk) bridge.osk = b.osk;
    if (b.oskIsOpen) bridge.oskIsOpen = b.oskIsOpen;
    if (b.log) bridge.log = b.log;
    if (b.toast) bridge.toast = b.toast;
    if (b.files) bridge.files = b.files;
  }),
  open: guard("open", open),
  close: guard("close", close),
  isOpen: guard("isOpen", function () { return !!st.open; }),
  handleAction: guard("handleAction", handleAction),
  hostMessage: guard("hostMessage", hostMessage),
  engines: ENGINES,
  debugState: guard("debugState", function () {
    return {
      open: st.open, scope: st.scope, navMode: st.navMode,
      tabs: st.tabs.length, active: st.active, max: st.max,
      chromeIdx: st.chromeIdx, sheetIdx: st.sheetIdx, sheetRows: st.sheetRows.length,
      sheetKind: st.sheetKind,
      downloads: st.downloads.length, downloadsActive: st.downActive,
      contentShown: shownNow, contentFocused: focusNow, holds: holdList(),
      pins: st.pins.length, history: st.history.length, engine: st.engine,
      bounds: st.lastBounds, url: (activeTab() || {}).url || null,
      unavailable: st.unavailable
    };
  })
};

})(typeof window !== "undefined" ? window : this);
