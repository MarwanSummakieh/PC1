/* ═══════════════════════════════════════════════════════════════════════
   ARC OS — browser chrome  (ui/browser.js)

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
  toast: function () {}
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

  navMode: "spatial",     // what arcnav.js reports it is doing
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
  cross:  '<circle cx="12" cy="12" r="9.2"/><path d="M8.6 8.6 15.4 15.4M15.4 8.6 8.6 15.4"/>',
  circle: '<circle cx="12" cy="12" r="9.2"/><circle cx="12" cy="12" r="4"/>',
  square: '<circle cx="12" cy="12" r="9.2"/><rect x="8.2" y="8.2" width="7.6" height="7.6" rx="1.1"/>',
  tri:    '<circle cx="12" cy="12" r="9.2"/><path d="M12 7.3 16.2 15.1H7.8z"/>',
  dpad:   '<path d="M9.6 3.7h4.8v5.9h5.9v4.8h-5.9v5.9H9.6v-5.9H3.7V9.6h5.9z"/>',
  stick:  '<circle cx="12" cy="12" r="8.4"/><circle cx="12" cy="12" r="3.3"/>',
  opts:   '<rect x="7.2" y="4.4" width="9.6" height="15.2" rx="2.6"/><path d="M9.9 9.2h4.2M9.9 12h4.2"/>'
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
  var menuChip = el("div", "arcbw-chip", svg(G.menu) + "<span>Menu</span>");

  omni.appendChild(addr); omni.appendChild(zoomChip); omni.appendChild(menuChip);
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
    addr: addr, tls: tls, url: url, zoom: zoomChip, menu: menuChip,
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

/* Show or hide the content window. Every full-screen surface in this file
   goes through here — see the rule at the top. */
function stage(showContent) {
  bridge.post({ type: "browser", cmd: "show", on: showContent ? 1 : 0 });
  bridge.post({ type: "browser", cmd: "focus", content: (showContent && st.scope === "content") ? 1 : 0 });
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
    h.push(hint(G.tri, "Menu"));
    if (st.media) h.push(hint(G.square, "Full screen"));
  } else if (st.scope === "chrome") {
    h.push(hint(G.dpad, "Move"));
    h.push(hint(G.cross, "Select"));
    h.push(hint(G.circle, "Back to the page"));
    h.push(keyHint("L1 / R1", "Switch tab"));
  } else if (st.scope === "start") {
    h.push(hint(G.dpad, "Move"));
    h.push(hint(G.cross, "Open"));
    h.push(hint(G.square, "Remove pin"));
    h.push(hint(G.circle, "Leave the browser"));
    h.push(hint(G.tri, "Menu"));
  } else {
    h.push(hint(G.dpad, "Move"));
    h.push(hint(G.cross, "Select"));
    h.push(hint(G.circle, "Back"));
  }
  st.el.hints.innerHTML = h.join("");
}

/* ═══ Focus painting ═══════════════════════════════════════════════════
   The browser keeps its own cursor rather than borrowing the shell's Focus
   manager: its scopes are not a stack (content and chrome swap sideways,
   they do not nest) and its rows are rebuilt from host messages several
   times a second. One attribute, one function, no bookkeeping to go stale. */

function chromeItems() {
  var out = [], i;
  var kids = st.el.tabs.children;
  for (i = 0; i < kids.length; i++) out.push(kids[i]);
  out.push(st.el.addr);
  out.push(st.el.zoom);
  out.push(st.el.menu);
  return out;
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

function scrollIntoRow(node) {
  try { node.scrollIntoView({ block: "nearest", inline: "nearest", behavior: "smooth" }); }
  catch (e) { try { node.scrollIntoView(); } catch (e2) {} }
}

/* ═══ Sheets ═══════════════════════════════════════════════════════════ */

function closeSheet() {
  st.el.sheet.classList.remove("is-open");
  st.el.sheet.innerHTML = "";
  st.sheetRows = [];
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
  stage(false);
  var s = sheet("Browser", "Pinned apps open full screen. Everything else is one address away.");
  var grid = el("div", "arcbw-pins");
  st.sheetRows = [];

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
  stage(false);
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
  stage(false);
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

/* ── Menu ────────────────────────────────────────────────────────────── */

function showMenu() {
  st.scope = "menu";
  stage(false);
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
  add({ label: "New tab", sub: st.tabs.length + " of " + st.max + " open",
        run: function () { closeSheet(); goToContent(); bridge.post({ type: "browser", cmd: "newtab", url: "" }); showStart(); } });
  if (t) add({ label: "Close this tab", sub: t.title || hostOf(t.url) || "New tab", danger: true,
        run: function () { bridge.post({ type: "browser", cmd: "closetab", tab: t.id }); showMenu(); } });
  add({ label: "Search engine", tail: (ENGINES[st.engine] || ENGINES.duckduckgo).name, run: showEngines });

  sec("Leave");
  add({ label: "Close the browser", sub: "Tabs stay loaded and come back where you left them",
        run: function () { close(); } });
  add({ label: "Close the browser and every tab", sub: "Frees the memory the pages are using", danger: true,
        run: function () { bridge.post({ type: "browser", cmd: "quit" }); close(); } });

  s.appendChild(list);
  st.sheetIdx = 0;
  paintFocus();
}

function showCrash(m) {
  st.scope = "menu";
  st.el.wrap.classList.remove("is-immersive");
  stage(false);
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
  stage(true);
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
     away from. */
  stage(true);
  bridge.post({ type: "browser", cmd: "focus", content: 0 });
  var items = chromeItems();
  st.chromeIdx = Math.min(st.chromeIdx, items.length - 1);
  /* Land on the address bar rather than on tab one: reaching for Options
     almost always means "I want to go somewhere else". */
  if (st.chromeIdx < st.tabs.length) st.chromeIdx = items.indexOf(st.el.addr);
  paintFocus();
}

/* ═══ Address bar ══════════════════════════════════════════════════════ */

function editAddress() {
  var t = activeTab();
  stage(false);
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
  stage(st.scope === "content" || st.scope === "chrome");
}

/* ═══ Text fields inside the page ══════════════════════════════════════
   arcnav.js posts what the field currently holds; the OSK edits it; the
   value goes back down and is written through the native setter so that a
   framework-controlled input keeps it. */

function editPageField(m) {
  st.pendingEdit = true;
  stage(false);
  bridge.osk({
    title: m.label || "Enter text",
    mode: m.inputType === "password" ? "password"
        : m.inputType === "search" ? "search"
        : m.inputType === "number" ? "number"
        : m.inputType === "url" ? "url" : "text",
    value: String(m.value === undefined ? "" : m.value),
    commitLabel: m.inputType === "search" ? "Search" : "Done",
    onCommit: guard("page field commit", function (v) {
      st.pendingEdit = null;
      bridge.post({ type: "browser", cmd: "text", payload: { value: String(v) } });
      afterOsk();
    }),
    onCancel: guard("page field cancel", function () {
      st.pendingEdit = null;
      bridge.post({ type: "browser", cmd: "cancel" });
      afterOsk();
    })
  });
}

function choosePageOption(m) {
  st.pendingSelect = true;
  st.scope = "menu";
  stage(false);
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
      say("focus [" + m.kind + "] " + m.label + (m.href ? "  -> " + m.href : ""));
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
    case "menu":    showMenu(); return true;
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
  switch (name) {
    case "left":  st.chromeIdx = Math.max(0, st.chromeIdx - 1); paintFocus(); return true;
    case "right": st.chromeIdx = Math.min(items.length - 1, st.chromeIdx + 1); paintFocus(); return true;
    case "up":    goToContent(); return true;
    case "down":  goToContent(); return true;
    case "back":  goToContent(); return true;
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

  st.open = true;
  st.el.wrap.classList.add("is-open");
  st.el.wrap.setAttribute("aria-hidden", "false");
  st.el.wrap.classList.remove("is-immersive");

  renderTabs(); renderAddress(); renderStage();

  bridge.post({ type: "browser", cmd: "open", url: cfg.url || "" });

  /* The layout has to exist before its rectangle means anything, and the
     root font-size is viewport-derived so the first frame is not final. */
  setTimeout(guard("bounds", pushBounds), 0);
  setTimeout(guard("bounds", pushBounds), 120);
  clearInterval(st.boundsTimer);
  st.boundsTimer = setInterval(guard("bounds tick", pushBounds), 700);
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
      pins: st.pins.length, history: st.history.length, engine: st.engine,
      bounds: st.lastBounds, url: (activeTab() || {}).url || null,
      unavailable: st.unavailable
    };
  })
};

})(typeof window !== "undefined" ? window : this);
