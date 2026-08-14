/* ═══════════════════════════════════════════════════════════════════════
   ARC OS — on-screen keyboard  (ui/osk.js)

   A controller-driven text-entry surface. No dependencies, no build step,
   no module system: this file defines one global, `ArcOSK`, in the same
   plain-JS style as index.html.

   Public API
   ──────────
     ArcOSK.open({ title, value, mode, maxLength, placeholder,
                   commitLabel, onCommit, onCancel })
     ArcOSK.close()                 // dismiss, fire nothing
     ArcOSK.isOpen()
     ArcOSK.handleAction(action)    // the pad channel — see ACTIONS below
     ArcOSK.beginRepeat(action)     // hold-to-repeat: press
     ArcOSK.endRepeat()             // hold-to-repeat: release
     ArcOSK.getValue()
     ArcOSK.debugState()            // { open, mode, layer, shift, r, i, key, value, caret }
     ArcOSK._model                  // pure grid model, for the headless nav test

   Crash isolation
   ───────────────
   This UI runs inside the Windows shell process. An escaped exception blanks
   a television, so every entry point — public method, event listener, timer —
   is wrapped by guard(). Failures are logged to the console and, when the
   ShellHostWeb shim is present, forwarded to the host log via window.arcPost.

   Styling lives in ui/osk.css. It is a separate file rather than an injected
   <style> block because a strict CSP (`style-src 'self'`) blocks inline style
   elements but allows a same-origin stylesheet. If the sheet is not already
   linked when open() is first called, this file adds a <link> pointing at
   osk.css next to itself — a resource fetch, which CSP permits.
   ═══════════════════════════════════════════════════════════════════════ */

(function (root) {
"use strict";

var VERSION = "1.0.0";

/* ═══ Crash isolation ══════════════════════════════════════════════════ */

function emit(level, where, what) {
  var msg = "ArcOSK[" + where + "] " + (what && what.stack ? what.stack : what);
  try { if (root.console && console[level]) console[level](msg); } catch (e) {}
  try { if (typeof root.arcPost === "function") root.arcPost({ type: "log", target: msg }); } catch (e) {}
}

/* something went wrong */
function report(where, err) { emit("error", where, err); }

/* something worth seeing in the host log, but not a fault — an unknown pad
   action is a wiring question, and logging it as an error trains people to
   ignore errors */
function note(where, what) { emit("info", where, what); }

/* Wrap fn so nothing it throws can escape into the shell. */
function guard(where, fn) {
  return function () {
    try { return fn.apply(this, arguments); }
    catch (err) { report(where, err); return undefined; }
  };
}

/* ═══ Layout model ═════════════════════════════════════════════════════

   A layer is { cols, rows }, a row is an array of cells, and a cell is one
   of:

     { out:"q",  w:2 }                 a character key (shift uppercases it)
     { out:".com", label:".com", w:4 } a literal-insert key
     { act:"shift", label:"⇧", w:3 }   an action key
     { spacer:true, w:16 }             dead space — never focusable

   `w` is in grid units. Every row in a layer sums to `cols`, so the model's
   arithmetic and the CSS grid agree exactly; the nav test asserts it.
   Navigation itself does not rely on that — it normalises each row by its own
   total — so a future ragged row still behaves.
   ═════════════════════════════════════════════════════════════════════ */

function K(ch, w)            { return { out: ch, label: ch, w: w || 2 }; }
function INS(text, label, w) { return { out: text, label: label || text, w: w || 2, wide: true }; }
function A(act, label, w, cls) { return { act: act, label: label, w: w, cls: cls || "" }; }
function SP(w)               { return { spacer: true, w: w }; }

function row(str, w) {
  var out = [], i;
  for (i = 0; i < str.length; i++) out.push(K(str.charAt(i), w));
  return out;
}

var DIGITS = "1234567890";
var QWERTY = "qwertyuiop";
var HOME   = "asdfghjkl";
var BOTTOM = "zxcvbnm";

/* Layers are frozen-by-convention: build() deep-copies before decorating. */
var LAYOUTS = {

  /* ── text / password / search ───────────────────────────── 20 units ── */
  text: { cols: 20, rows: [
    row(DIGITS, 2),
    row(QWERTY, 2),
    row(HOME, 2).concat([ K("-", 2) ]),
    [ A("shift", "shift", 3, "osk-mod") ].concat(row(BOTTOM, 2), [ A("backspace", "back", 3, "osk-mod") ]),
    [ A("symbols", "?123", 4, "osk-mod"), K(",", 2), A("space", "space", 8, "osk-mod osk-space"),
      K(".", 2), A("commit", "Done", 4, "osk-mod osk-commit") ]
  ]},

  /* ── symbols / numbers layer ────────────────────────────── 20 units ── */
  symbols: { cols: 20, rows: [
    row("!@#$%^&*()", 2),
    row("-_=+[]{}\\|", 2),
    row(";:'\"<>,.?/", 2),
    row("~`€£¥°•§", 2).concat([ A("backspace", "back", 4, "osk-mod") ]),
    [ A("base", "ABC", 4, "osk-mod"), K("_", 2), A("space", "space", 8, "osk-mod osk-space"),
      K(".", 2), A("commit", "Done", 4, "osk-mod osk-commit") ]
  ]},

  /* ── url ────────────────────────────────────────────────── 20 units ──
     Row 5 is the whole reason url is its own layer: /, :, ., -, www. and
     .com are two presses deep on a generic keyboard and one press here. */
  url: { cols: 20, rows: [
    row(DIGITS, 2),
    row(QWERTY, 2),
    row(HOME, 2).concat([ K("-", 2) ]),
    [ A("shift", "shift", 3, "osk-mod") ].concat(row(BOTTOM, 2), [ A("backspace", "back", 3, "osk-mod") ]),
    [ K("/", 3), K(":", 3), K(".", 3), K("_", 3), INS("www.", "www.", 4), INS(".com", ".com", 4) ],
    [ A("symbols", "?123", 4, "osk-mod"), K("@", 2), A("space", "space", 6, "osk-mod osk-space"),
      K("~", 2), A("commit", "Go", 6, "osk-mod osk-commit") ]
  ]},

  /* ── number ─────────────────────────────────────────────── 12 units ── */
  number: { cols: 12, rows: [
    [ K("1", 4), K("2", 4), K("3", 4) ],
    [ K("4", 4), K("5", 4), K("6", 4) ],
    [ K("7", 4), K("8", 4), K("9", 4) ],
    [ K("-", 4), K("0", 4), K(".", 4) ],
    [ A("backspace", "back", 6, "osk-mod"), A("commit", "Done", 6, "osk-mod osk-commit") ]
  ]}
};

/* Which layer a mode starts on, and what the commit key says. */
var MODES = {
  text:     { base: "text",   commit: "Done",   hint: "Text" },
  password: { base: "text",   commit: "Done",   hint: "Password" },
  search:   { base: "text",   commit: "Search", hint: "Search" },
  url:      { base: "url",    commit: "Go",     hint: "Address" },
  number:   { base: "number", commit: "Done",   hint: "Number" }
};

/* ── grid construction (pure) ─────────────────────────────────────────── */

function copyCell(c) {
  var o = {}, k;
  for (k in c) if (Object.prototype.hasOwnProperty.call(c, k)) o[k] = c[k];
  return o;
}

/*
  build(layerName, opts) -> grid

    grid.rows  [r][i] = cell, decorated with c0 / c1 (integer column offsets
                        within its row), tot (the row's column total), cx (the
                        fractional centre — for display and debugging only),
                        plus r and i back-references
    grid.live  [r]    = indices of focusable cells in row r
    grid.cols         = declared unit width of the layer

  Everything navigation does is integer arithmetic on c0/c1/tot. That is not
  fussiness: with fractions, (12/20 + 14/20) / 2 is 0.6499999999999999 rather
  than 0.65, and "down" from j silently lands on b instead of n. Columns are
  whole numbers, so compare whole numbers.

  opts.reveal   prepend the field-chrome row carrying the password Show
                toggle, so it is reachable by pressing up from the top key row
  opts.commit   label override for the commit key
*/
function build(layerName, opts) {
  opts = opts || {};
  var src = LAYOUTS[layerName];
  if (!src) throw new Error("unknown layer '" + layerName + "'");

  var rows = [], r, i, cells;

  if (opts.reveal) {
    /* The toggle sits at the right-hand end of the field, so it is modelled
       as a row of its own holding two units at the far right. Pressing up from
       the top key row therefore lands on it, which is where it visually is —
       and renderKeys() sizes the real button from these same two units, so the
       model is not describing a layout the CSS might quietly disagree with. */
    rows.push([ SP(src.cols - 2), A("reveal", "Show", 2, "osk-mod osk-reveal") ]);
  }

  for (r = 0; r < src.rows.length; r++) {
    cells = [];
    for (i = 0; i < src.rows[r].length; i++) cells.push(copyCell(src.rows[r][i]));
    rows.push(cells);
  }

  var grid = { cols: src.cols, layer: layerName, rows: rows, live: [], total: [] };

  for (r = 0; r < rows.length; r++) {
    var total = 0;
    for (i = 0; i < rows[r].length; i++) total += rows[r][i].w;
    if (total <= 0) total = 1;
    grid.total.push(total);

    var live = [], x = 0;
    for (i = 0; i < rows[r].length; i++) {
      var c = rows[r][i];
      c.r = r; c.i = i;
      c.c0 = x;
      x += c.w;
      c.c1 = x;
      c.tot = total;
      c.cx = (c.c0 + c.c1) / (2 * total);   /* display only — never navigated on */
      if (c.act === "commit" && opts.commit) c.label = opts.commit;
      if (!c.spacer) live.push(i);
    }
    grid.live.push(live);
  }
  return grid;
}

/*
  A position is { r, i, a2, at }.

    r, i    the focused cell
    a2/at   the horizontal anchor as an exact rational: the anchor sits at
            a2 / (2·at) across its row. Carried unchanged through vertical
            moves, so up-down-up returns to where it started even when the
            intervening row has fewer, wider keys; reset by horizontal moves.
*/
function posFor(grid, r, i) {
  var c = grid.rows[r][i];
  return { r: r, i: i, a2: c.c0 + c.c1, at: c.tot };
}

/* first focusable cell of the first non-empty row — the fallback landing spot */
function firstPos(grid) {
  for (var r = 0; r < grid.rows.length; r++) {
    if (grid.live[r].length) return posFor(grid, r, grid.live[r][0]);
  }
  return { r: 0, i: 0, a2: 1, at: 1 };
}

/*
  Which cell of row r sits under the anchor a2/(2·at)?

  Containment first — that is what makes "down lands on the key visually
  below" true across rows of differing key widths — nearest centre as the
  fallback, so a row with a hole in it (the field row, which holds only the
  Show toggle) can never swallow the cursor.

  Both tests cross-multiply into integers so the answer cannot depend on
  floating-point rounding at a key boundary. Containment is half-open,
  [c0, c1), so an anchor exactly on a seam resolves rightward, always.
*/
function pick(grid, r, a2, at) {
  var live = grid.live[r], cells = grid.rows[r];
  var best = live[0], bestD = Infinity, n, c, d;
  for (n = 0; n < live.length; n++) {
    c = cells[live[n]];
    if (2 * at * c.c0 <= a2 * c.tot && a2 * c.tot < 2 * at * c.c1) return live[n];
  }
  for (n = 0; n < live.length; n++) {
    c = cells[live[n]];
    d = Math.abs((c.c0 + c.c1) * at - a2 * c.tot);
    if (d < bestD) { bestD = d; best = live[n]; }
  }
  return best;
}

/*
  step(grid, pos, dir) -> new pos  (pure; never mutates pos)

    horizontal  moves within the row and WRAPS at both ends
    vertical    does NOT wrap: up from the top row and down from the bottom
                row return the same position, so the cursor can never fall
                out of the keyboard
*/
function step(grid, pos, dir) {
  var rows = grid.rows;
  var r = pos && pos.r, i = pos && pos.i;
  if (!rows[r] || !rows[r][i] || rows[r][i].spacer) return firstPos(grid);
  var a2 = (typeof pos.a2 === "number") ? pos.a2 : rows[r][i].c0 + rows[r][i].c1;
  var at = (typeof pos.at === "number" && pos.at > 0) ? pos.at : rows[r][i].tot;

  if (dir === "left" || dir === "right") {
    var live = grid.live[r];
    if (!live.length) return { r: r, i: i, a2: a2, at: at };
    var k = -1, n;
    for (n = 0; n < live.length; n++) if (live[n] === i) { k = n; break; }
    if (k < 0) k = 0;
    k = (dir === "right") ? (k + 1) % live.length
                          : (k - 1 + live.length) % live.length;
    return posFor(grid, r, live[k]);
  }

  var d = (dir === "up") ? -1 : 1;
  var tr = r + d;
  while (tr >= 0 && tr < rows.length && grid.live[tr].length === 0) tr += d;
  if (tr < 0 || tr >= rows.length) return { r: r, i: i, a2: a2, at: at };  /* boundary: stay */
  return { r: tr, i: pick(grid, tr, a2, at), a2: a2, at: at };
}

/* ═══ Action vocabulary ════════════════════════════════════════════════

   The canonical names the host is expected to post, plus aliases for the
   DualSense buttons and for the action names ShellHostWeb already sends
   today (launch / back / tabPlay / tabMedia / cc), so the keyboard is
   drivable by the current host binary with no C# change.
   ═════════════════════════════════════════════════════════════════════ */

var ALIAS = {
  /* pad face buttons */
  cross: "accept",     a: "accept",       launch: "accept",   enter: "accept",
  circle: "back",      b: "back",         cancel: "back",     escape: "back",
  square: "backspace", x: "backspace",    bksp: "backspace",  delete: "backspace",
  triangle: "shift",   y: "shift",        caps: "shift",
  options: "commit",   start: "commit",   done: "commit",     cc: "commit",
  /* shoulders — cursor movement inside the text */
  l1: "cursorleft",    lb: "cursorleft",  tabplay: "cursorleft",
  r1: "cursorright",   rb: "cursorright", tabmedia: "cursorright",
  /* layer + misc */
  sym: "symbols",      numbers: "symbols",
  reveal: "togglemask", show: "togglemask"
};

var REPEATABLE = { up: 1, down: 1, left: 1, right: 1, backspace: 1, cursorleft: 1, cursorright: 1 };

/* ═══ Runtime state ════════════════════════════════════════════════════ */

var st = {
  open: false,
  cfg: null,
  mode: "text",
  base: "text",       /* layer to return to from symbols */
  layer: "text",
  grid: null,
  pos: { r: 0, i: 0, a2: 1, at: 1 },
  value: "",
  caret: 0,
  shift: 0,           /* 0 off · 1 one-shot · 2 caps lock */
  masked: true,
  maxLength: null,
  el: null,           /* DOM cache */
  prevFocus: null,
  repeatTimer: null,
  repeatAction: null,
  cssChecked: false
};

var REPEAT_FIRST = 420, REPEAT_NEXT = 70;

function reducedMotion() {
  try { return !!(root.matchMedia && root.matchMedia("(prefers-reduced-motion: reduce)").matches); }
  catch (e) { return false; }
}

/* ═══ Stylesheet ═══════════════════════════════════════════════════════ */

/* Probe a sentinel custom property that osk.css defines. If it is missing the
   sheet was not linked, so link it — relative to this script's own URL. */
function ensureCSS(doc) {
  if (st.cssChecked) return;
  st.cssChecked = true;
  var probe = doc.createElement("div");
  probe.className = "arc-osk-probe";
  doc.body.appendChild(probe);
  var loaded = false;
  try { loaded = getComputedStyle(probe).getPropertyValue("--osk-loaded").indexOf("1") >= 0; }
  catch (e) {}
  doc.body.removeChild(probe);
  if (loaded) return;

  var href = "osk.css";
  try {
    var me = doc.currentScript;
    if (!me) {
      var all = doc.getElementsByTagName("script");
      for (var n = all.length - 1; n >= 0; n--) {
        if (all[n].src && all[n].src.indexOf("osk.js") >= 0) { me = all[n]; break; }
      }
    }
    if (me && me.src) href = me.src.replace(/osk\.js(\?.*)?$/, "osk.css");
  } catch (e) {}

  var link = doc.createElement("link");
  link.rel = "stylesheet";
  link.href = href;
  link.setAttribute("data-arc-osk", "auto");
  doc.head.appendChild(link);
  note("css", "osk.css was not linked; injected <link href=\"" + href + "\">");
}

/* ═══ Inline SVG ═══════════════════════════════════════════════════════ */

var SVG = {
  shift:     '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 4 4 12h4.4v6.2h7.2V12H20z"/></svg>',
  backspace: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M8.4 5.5h11a1.6 1.6 0 0 1 1.6 1.6v9.8a1.6 1.6 0 0 1-1.6 1.6h-11L3 12z"/><path d="m11.4 9.6 5 4.8M16.4 9.6l-5 4.8"/></svg>',
  space:     '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 10.5v3.2h16v-3.2"/></svg>',
  eye:       '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M2.5 12S6 6.5 12 6.5 21.5 12 21.5 12 18 17.5 12 17.5 2.5 12 2.5 12z"/><circle cx="12" cy="12" r="2.6"/></svg>',
  eyeOff:    '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M2.5 12S6 6.5 12 6.5c1.6 0 3 .4 4.2 1M21.5 12s-3.5 5.5-9.5 5.5c-1.7 0-3.2-.4-4.4-1.1"/><path d="M4 4l16 16"/></svg>',
  cross:     '<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="9"/><path d="m8.6 8.6 6.8 6.8M15.4 8.6l-6.8 6.8"/></svg>',
  circle:    '<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="9"/><circle cx="12" cy="12" r="4.4"/></svg>',
  square:    '<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="9"/><rect x="8.2" y="8.2" width="7.6" height="7.6" rx="1"/></svg>',
  triangle:  '<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="9"/><path d="M12 7.4 16 15H8z"/></svg>'
};

var HINTS = [
  { svg: SVG.cross,    text: "Select" },
  { svg: SVG.square,   text: "Backspace" },
  { svg: SVG.triangle, text: "Shift" },
  { svg: SVG.circle,   text: "Cancel" },
  { pad: "L1 R1",      text: "Move cursor" },
  { pad: "OPTIONS",    text: "Confirm" }
];

/* ═══ DOM ══════════════════════════════════════════════════════════════ */

function el(tag, cls, parent) {
  var e = document.createElement(tag);
  if (cls) e.className = cls;
  if (parent) parent.appendChild(e);
  return e;
}

function buildDOM() {
  var doc = document;
  ensureCSS(doc);

  var scrim = el("div", "arc-osk");
  scrim.setAttribute("role", "dialog");
  scrim.setAttribute("aria-modal", "true");
  scrim.setAttribute("aria-label", "On-screen keyboard");
  scrim.setAttribute("data-version", VERSION);

  var panel = el("div", "osk-panel", scrim);
  var head  = el("div", "osk-head", panel);

  var title = el("div", "osk-title", head);
  var field = el("div", "osk-field", head);
  var input = el("div", "osk-input", field);
  input.setAttribute("role", "textbox");
  input.setAttribute("aria-readonly", "true");   /* edited through the keys, not typed into */

  var pre    = el("span", "osk-pre", input);
  var caret  = el("span", "osk-caret", input);
  var post   = el("span", "osk-post", input);
  var ph     = el("span", "osk-ph", input);

  var acts   = el("div", "osk-field-actions", field);
  var line   = el("div", "osk-line", head);
  var hintMode = el("span", "osk-modehint", line);
  var count    = el("span", "osk-count", line);

  var keys = el("div", "osk-keys", panel);

  var hints = el("div", "osk-hints", panel);
  for (var n = 0; n < HINTS.length; n++) {
    var h = el("span", "osk-hint", hints);
    if (HINTS[n].svg) { var g = el("span", "osk-glyph", h); g.innerHTML = HINTS[n].svg; }
    else { var p = el("span", "osk-padkey", h); p.textContent = HINTS[n].pad; }
    var t = el("span", null, h);
    t.textContent = HINTS[n].text;
  }

  doc.body.appendChild(scrim);

  /* Mouse works everywhere — the README's rule — but the pad is the target. */
  scrim.addEventListener("mousedown", guard("scrim.mousedown", function (ev) {
    if (ev.target === scrim) { doAction("back"); }
  }));

  st.el = {
    scrim: scrim, panel: panel, title: title, field: field, input: input,
    pre: pre, caret: caret, post: post, ph: ph, acts: acts,
    modehint: hintMode, count: count, keys: keys
  };
  return st.el;
}

/* ── key rendering ────────────────────────────────────────────────────── */

function keyLabel(cell) {
  if (cell.act === "shift")     return null;   /* icon */
  if (cell.act === "backspace") return null;   /* icon */
  if (cell.act === "space")     return null;   /* icon */
  if (cell.act === "reveal")    return null;   /* icon + text */
  if (cell.out !== undefined && !cell.wide) return shiftChar(cell.out);
  return cell.label;
}

function renderKeys() {
  var e = st.el, grid = st.grid, r, i;
  e.keys.innerHTML = "";
  e.acts.innerHTML = "";
  /* Reset the field's right-hand slot: without this a password session leaves
     its 10% behind as dead space in the next text-mode field. */
  e.acts.style.flex = "";

  for (r = 0; r < grid.rows.length; r++) {
    var cells = grid.rows[r];
    var isChrome = (cells.length && cells[cells.length - 1].act === "reveal" && grid.live[r].length === 1);
    var host, rowEl = null;

    if (isChrome) {
      /* The Show toggle lives inside the field rather than in the key grid, so
         give its container exactly the share of the width the model claims for
         it. Derived, not duplicated: change the cell's `w` and the button
         follows, and the harness's pixel test proves the two agree. */
      host = e.acts;
      var rev = cells[cells.length - 1];
      e.acts.style.flex = "0 0 " + (rev.w / grid.total[r] * 100) + "%";
    } else {
      rowEl = el("div", "osk-row", e.keys);
      rowEl.style.gridTemplateColumns = "repeat(" + grid.total[r] + ", minmax(0,1fr))";
      host = rowEl;
    }

    for (i = 0; i < cells.length; i++) {
      var c = cells[i];
      if (c.spacer) {
        if (rowEl) { var sp = el("div", "osk-spacer", rowEl); sp.style.gridColumn = "span " + c.w; }
        continue;
      }
      var b = el("button", "osk-key " + (c.cls || ""), host);
      b.type = "button";
      b.setAttribute("data-r", String(r));
      b.setAttribute("data-i", String(i));
      b.tabIndex = -1;
      if (rowEl) b.style.gridColumn = "span " + c.w;
      paintKeyFace(b, c);
      b.addEventListener("mousedown", makeKeyMouse(r, i));
      b.addEventListener("mouseup", guard("key.mouseup", endRepeat));
      b.addEventListener("mouseleave", guard("key.mouseleave", endRepeat));
    }
  }
  paintFocus();
}

function makeKeyMouse(r, i) {
  return guard("key.mousedown", function (ev) {
    ev.preventDefault();
    st.pos = posFor(st.grid, r, i);
    paintFocus();
    activate();
    var c = st.grid.rows[r][i];
    if (c.act === "backspace") beginRepeat("backspace");
  });
}

function paintKeyFace(b, c) {
  b.innerHTML = "";
  var icon = null, text = null;

  if (c.act === "shift") {
    icon = SVG.shift;
    text = (st.shift === 2) ? "caps" : "shift";
    b.setAttribute("aria-label", text === "caps" ? "Caps lock on" : "Shift");
  } else if (c.act === "backspace") {
    icon = SVG.backspace;
    b.setAttribute("aria-label", "Backspace");
  } else if (c.act === "space") {
    icon = SVG.space;
    text = "space";
    b.setAttribute("aria-label", "Space");
  } else if (c.act === "reveal") {
    icon = st.masked ? SVG.eye : SVG.eyeOff;
    text = st.masked ? "Show" : "Hide";
    b.setAttribute("aria-label", st.masked ? "Show password" : "Hide password");
  } else {
    text = keyLabel(c);
    b.setAttribute("aria-label", c.act ? c.label : "Key " + text);
  }

  if (icon) { var g = el("span", "osk-keyicon", b); g.innerHTML = icon; }
  if (text !== null && text !== undefined) { var s = el("span", "osk-keytext", b); s.textContent = text; }

  b.classList.toggle("is-on", c.act === "shift" && st.shift > 0);
  b.classList.toggle("is-lock", c.act === "shift" && st.shift === 2);
}

/* Repaint only what shift/mask changes — the labels — without rebuilding. */
function repaintFaces() {
  var e = st.el, list = e.scrim.querySelectorAll(".osk-key"), n;
  for (n = 0; n < list.length; n++) {
    var b = list[n];
    var r = parseInt(b.getAttribute("data-r"), 10);
    var i = parseInt(b.getAttribute("data-i"), 10);
    var c = st.grid.rows[r] && st.grid.rows[r][i];
    if (c) paintKeyFace(b, c);
  }
}

function paintFocus() {
  var e = st.el, list = e.scrim.querySelectorAll(".osk-key"), n;
  for (n = 0; n < list.length; n++) {
    var b = list[n];
    var on = (parseInt(b.getAttribute("data-r"), 10) === st.pos.r &&
              parseInt(b.getAttribute("data-i"), 10) === st.pos.i);
    b.classList.toggle("is-focus", on);
    b.setAttribute("aria-selected", on ? "true" : "false");
  }
}

/* ── the text field ───────────────────────────────────────────────────── */

function displayText(s) {
  if (st.mode === "password" && st.masked) {
    var out = "", n;
    for (n = 0; n < s.length; n++) out += "•";
    return out;
  }
  return s;
}

function paintField() {
  var e = st.el;
  var v = st.value, c = st.caret;
  if (c < 0) c = 0;
  if (c > v.length) c = v.length;
  st.caret = c;

  e.pre.textContent  = displayText(v.slice(0, c));
  e.post.textContent = displayText(v.slice(c));
  e.ph.style.display = v.length ? "none" : "";

  if (st.maxLength) {
    e.count.textContent = v.length + " / " + st.maxLength;
    e.count.classList.toggle("is-full", v.length >= st.maxLength);
  } else {
    e.count.textContent = "";
    e.count.classList.remove("is-full");
  }

  /* keep the caret in view without measuring during a transition */
  try {
    var box = e.input, car = e.caret;
    var left = car.offsetLeft, pad = 40;
    if (left - box.scrollLeft > box.clientWidth - pad) box.scrollLeft = left - box.clientWidth + pad;
    else if (left < box.scrollLeft + 20) box.scrollLeft = Math.max(0, left - 20);
  } catch (err) { /* layout not ready — harmless */ }
}

function flashLimit() {
  var e = st.el;
  e.count.classList.remove("is-hit");
  /* force reflow so the animation restarts */
  void e.count.offsetWidth;
  e.count.classList.add("is-hit");
  e.field.classList.remove("is-hit");
  void e.field.offsetWidth;
  e.field.classList.add("is-hit");
}

/* ── editing ──────────────────────────────────────────────────────────── */

function shiftChar(ch) {
  if (st.shift > 0 && typeof ch === "string") {
    var up = ch.toUpperCase();
    if (up !== ch) return up;
  }
  return ch;
}

function insert(text) {
  if (!text) return;
  if (st.maxLength && st.value.length + text.length > st.maxLength) {
    var room = st.maxLength - st.value.length;
    if (room <= 0) { flashLimit(); return; }
    text = text.slice(0, room);
    flashLimit();
  }
  st.value = st.value.slice(0, st.caret) + text + st.value.slice(st.caret);
  st.caret += text.length;
  if (st.shift === 1) { st.shift = 0; repaintFaces(); }
  paintField();
}

function backspace() {
  if (st.caret <= 0) return;
  st.value = st.value.slice(0, st.caret - 1) + st.value.slice(st.caret);
  st.caret--;
  paintField();
}

function forwardDelete() {
  if (st.caret >= st.value.length) return;
  st.value = st.value.slice(0, st.caret) + st.value.slice(st.caret + 1);
  paintField();
}

function moveCaret(d) {
  st.caret = Math.max(0, Math.min(st.value.length, st.caret + d));
  paintField();
}

function cycleShift() {
  st.shift = (st.shift + 1) % 3;
  repaintFaces();
}

function setLayer(name) {
  st.layer = name;
  st.grid = build(name, { reveal: st.mode === "password", commit: st.cfg.commitLabel });
  st.pos = firstPos(st.grid);
  /* land on something useful rather than the first cell of the top row */
  var home = homePos(st.grid);
  if (home) st.pos = home;
  renderKeys();
}

/* Opening focus: the middle of the home row reads as "ready to type" and is
   one press from everything. Falls back to the first live cell. */
function homePos(grid) {
  var r = Math.min(grid.rows.length - 1, grid.rows.length > 4 ? 2 : 1);
  while (r >= 0 && grid.live[r].length === 0) r--;
  if (r < 0) return null;
  var live = grid.live[r];
  return posFor(grid, r, live[Math.floor(live.length / 2)]);
}

/* ── activation ───────────────────────────────────────────────────────── */

function currentCell() {
  var rw = st.grid.rows[st.pos.r];
  return rw ? rw[st.pos.i] : null;
}

function activate() {
  var c = currentCell();
  if (!c) return;
  bump(c);
  if (c.act) { runAct(c.act); return; }
  insert(c.wide ? c.out : shiftChar(c.out));
}

function bump(c) {
  var e = st.el;
  var b = e.scrim.querySelector('.osk-key[data-r="' + c.r + '"][data-i="' + c.i + '"]');
  if (!b) return;
  b.classList.remove("is-hit");
  void b.offsetWidth;
  b.classList.add("is-hit");
}

function runAct(act) {
  switch (act) {
    case "shift":     cycleShift(); break;
    case "backspace": backspace(); break;
    case "space":     insert(" "); break;
    case "symbols":   setLayer("symbols"); break;
    case "base":      setLayer(st.base); break;
    case "commit":    commit(); break;
    case "reveal":    toggleMask(); break;
    default: report("runAct", "unknown key action '" + act + "'");
  }
}

function toggleMask() {
  st.masked = !st.masked;
  repaintFaces();
  paintField();
}

function commit() {
  var cfg = st.cfg, v = st.value;
  teardown();
  if (cfg && typeof cfg.onCommit === "function") {
    try { cfg.onCommit(v); } catch (err) { report("onCommit", err); }
  }
}

function cancel() {
  var cfg = st.cfg;
  teardown();
  if (cfg && typeof cfg.onCancel === "function") {
    try { cfg.onCancel(); } catch (err) { report("onCancel", err); }
  }
}

/* ═══ Action dispatch ══════════════════════════════════════════════════ */

function normalise(action) {
  if (typeof action !== "string") return null;
  var a = action.trim().toLowerCase();
  a = a.replace(/[\s_-]+/g, "");
  if (ALIAS[a]) a = ALIAS[a];
  return a;
}

function doAction(action) {
  if (!st.open) return false;
  var a = normalise(action);
  if (!a) return false;

  switch (a) {
    case "up": case "down": case "left": case "right":
      st.pos = step(st.grid, st.pos, a);
      paintFocus();
      return true;
    case "accept":      activate(); return true;
    case "back":        cancel(); return true;
    case "backspace":   backspace(); return true;
    case "forwarddelete": forwardDelete(); return true;
    case "shift":       cycleShift(); return true;
    case "space":       insert(" "); return true;
    case "symbols":     setLayer(st.layer === "symbols" ? st.base : "symbols"); return true;
    case "commit":      commit(); return true;
    case "cursorleft":  moveCaret(-1); return true;
    case "cursorright": moveCaret(1); return true;
    case "home":        st.caret = 0; paintField(); return true;
    case "end":         st.caret = st.value.length; paintField(); return true;
    case "clear":       st.value = ""; st.caret = 0; paintField(); return true;
    case "togglemask":  if (st.mode === "password") toggleMask(); return true;
    default:
      note("handleAction", "unknown action '" + action + "' (ignored)");
      return false;
  }
}

/* ── hold-to-repeat ───────────────────────────────────────────────────── */

function endRepeat() {
  if (st.repeatTimer) { clearTimeout(st.repeatTimer); clearInterval(st.repeatTimer); }
  st.repeatTimer = null;
  st.repeatAction = null;
}

function beginRepeat(action) {
  endRepeat();
  var a = normalise(action);
  if (!a || !REPEATABLE[a]) return;
  st.repeatAction = a;
  st.repeatTimer = setTimeout(guard("repeat.first", function () {
    st.repeatTimer = setInterval(guard("repeat.tick", function () {
      if (!st.open || !st.repeatAction) { endRepeat(); return; }
      doAction(st.repeatAction);
    }), REPEAT_NEXT);
  }), REPEAT_FIRST);
}

/* ═══ Physical keyboard (debugging, and a real keyboard if one is plugged in)

   Arrows navigate · Enter presses the focused key · Backspace deletes ·
   Delete forward-deletes · Escape cancels · Tab cycles shift · F2 toggles the
   symbols layer · Ctrl+Enter commits · Home/End jump the caret ·
   printable characters type straight into the field.
   ═════════════════════════════════════════════════════════════════════ */

function onKeyDown(ev) {
  if (!st.open) return;
  var k = ev.key;
  var handled = true;

  if (k === "ArrowUp")         doAction("up");
  else if (k === "ArrowDown")  doAction("down");
  else if (k === "ArrowLeft")  { ev.shiftKey ? doAction("cursorleft") : doAction("left"); }
  else if (k === "ArrowRight") { ev.shiftKey ? doAction("cursorright") : doAction("right"); }
  else if (k === "Enter")      { ev.ctrlKey ? doAction("commit") : doAction("accept"); }
  else if (k === "Backspace")  doAction("backspace");
  else if (k === "Delete")     doAction("forwarddelete");
  else if (k === "Escape")     doAction("back");
  else if (k === "Tab")        doAction("shift");
  else if (k === "F2")         doAction("symbols");
  else if (k === "Home")       doAction("home");
  else if (k === "End")        doAction("end");
  else if (k === " ")          doAction("space");
  else if (k && k.length === 1 && !ev.ctrlKey && !ev.altKey && !ev.metaKey) insert(k);
  else handled = false;

  if (handled) { ev.preventDefault(); ev.stopPropagation(); }
}

/* ═══ Open / close ═════════════════════════════════════════════════════ */

function teardown() {
  endRepeat();
  st.open = false;
  if (st.el) {
    st.el.scrim.classList.remove("is-open");
    st.el.scrim.setAttribute("aria-hidden", "true");
  }
  try { document.removeEventListener("keydown", keyHandler, true); } catch (e) {}
  try { if (st.prevFocus && st.prevFocus.focus) st.prevFocus.focus(); } catch (e) {}
  st.prevFocus = null;
  st.cfg = null;
}

var keyHandler = guard("keydown", onKeyDown);

function open(cfg) {
  cfg = cfg || {};
  if (st.open) teardown();          /* re-open with new config, silently */

  var mode = MODES[cfg.mode] ? cfg.mode : "text";
  st.cfg = cfg;
  st.mode = mode;
  st.base = MODES[mode].base;
  st.value = (typeof cfg.value === "string") ? cfg.value : "";
  st.maxLength = (typeof cfg.maxLength === "number" && cfg.maxLength > 0) ? cfg.maxLength : null;
  if (st.maxLength && st.value.length > st.maxLength) st.value = st.value.slice(0, st.maxLength);
  st.caret = st.value.length;
  st.shift = 0;
  st.masked = (mode === "password");
  st.cfg.commitLabel = cfg.commitLabel || MODES[mode].commit;

  if (!st.el) buildDOM();
  var e = st.el;

  e.scrim.classList.toggle("osk--number", mode === "number");
  e.title.textContent = (typeof cfg.title === "string" && cfg.title) ? cfg.title : "Enter text";
  e.scrim.setAttribute("aria-label", e.title.textContent + " — on-screen keyboard");
  e.input.setAttribute("aria-label", e.title.textContent);
  e.ph.textContent = (typeof cfg.placeholder === "string") ? cfg.placeholder : "";
  e.modehint.textContent = MODES[mode].hint;
  e.input.classList.toggle("is-password", mode === "password");

  setLayer(st.base);
  paintField();

  st.prevFocus = document.activeElement;
  st.open = true;
  e.scrim.setAttribute("aria-hidden", "false");
  e.scrim.classList.add("is-open");
  e.scrim.classList.toggle("is-still", reducedMotion());

  document.addEventListener("keydown", keyHandler, true);
  return true;
}

/* ═══ Public surface ═══════════════════════════════════════════════════ */

var API = {
  version: VERSION,
  open: guard("open", open),
  close: guard("close", function () { teardown(); }),
  isOpen: guard("isOpen", function () { return !!st.open; }),
  handleAction: guard("handleAction", function (a) { return doAction(a); }),
  beginRepeat: guard("beginRepeat", function (a) { doAction(a); beginRepeat(a); }),
  endRepeat: guard("endRepeat", endRepeat),
  getValue: guard("getValue", function () { return st.value; }),
  debugState: guard("debugState", function () {
    var c = st.grid ? currentCell() : null;
    return {
      open: st.open, mode: st.mode, layer: st.layer, shift: st.shift, masked: st.masked,
      r: st.pos.r, i: st.pos.i,
      key: c ? (c.act ? ("[" + c.act + "]") : c.out) : null,
      value: st.value, caret: st.caret
    };
  }),
  /* pure model — the headless navigation test drives exactly this */
  _model: { LAYOUTS: LAYOUTS, MODES: MODES, build: build, step: step, pick: pick,
            firstPos: firstPos, posFor: posFor }
};

root.ArcOSK = API;
if (typeof module !== "undefined" && module.exports) module.exports = API;

})(typeof window !== "undefined" ? window
   : (typeof globalThis !== "undefined" ? globalThis : this));
