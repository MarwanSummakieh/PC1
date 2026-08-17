/* ═══════════════════════════════════════════════════════════════════════
   MarwanOS — the navigation layer injected into every browsed page
   (ui/mosnav.js)

   WHERE THIS RUNS
   ───────────────
   Not in the shell. The host reads this file off disk at startup and hands
   the text to CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync on the
   CONTENT WebView, so it is the first script in every document that WebView
   ever loads — youtube.com, a bank, a PDF viewer, anything. It is a .js file
   rather than a string constant in ShellHostWeb.cs so that it can be syntax
   checked and read like code; the host only ever transports it.

   Consequences of running inside somebody else's page, all of which this
   file is built around:

     * The page's CSS is hostile by accident. Every element we create lives
       inside a closed shadow root, so no page rule can reach it and nothing
       we do leaks out.
     * The page's Content-Security-Policy is hostile on purpose. A strict
       `style-src 'self'` blocks inline style attributes AND <style>
       elements — that is most of the modern web. Our styling therefore goes
       through a constructable stylesheet adopted by the shadow root, which
       CSP does not police, with the inline path kept only as the fast path
       where it is allowed. See paint().
     * The page can and does replace its whole DOM under us. Nothing is
       cached across a move; the focusable set is rebuilt on demand and
       re-validated before every use.
     * The page may be broken. Every entry point is wrapped, because an
       exception thrown out of a message handler here would silently kill
       controller navigation and leave a television with a page it cannot
       move on.

   PROTOCOL
   ────────
   host -> page   window.chrome.webview messages:
       {t:"act",  action, phase}     one semantic pad action
       {t:"axes", lx, ly, rx, ry}    analog sticks, ~30 Hz, -1..1
       {t:"mode", mode}              "spatial" | "cursor" | "toggle"
       {t:"text", value}             the OSK's answer for the pending field
       {t:"cancel"}                  the OSK was cancelled
       {t:"zoom"}                    re-measure after a zoom change
       {t:"scan"}                    rebuild the focusable set now

   page -> host   window.chrome.webview.postMessage:
       {type:"mosnav", ev:"ready",  url, title}
       {type:"mosnav", ev:"mode",   mode, items}
       {type:"mosnav", ev:"focus",  label, kind, href}
       {type:"mosnav", ev:"edit",   value, inputType, label, multiline}
       {type:"mosnav", ev:"select", label, options:[...], index}
       {type:"mosnav", ev:"media",  playing, muted, time, duration, fullscreen}
       {type:"mosnav", ev:"nofocus"}      nothing focusable on this page
       {type:"mosnav", ev:"log",    text}
   ═══════════════════════════════════════════════════════════════════════ */

(function () {
"use strict";

if (window.__mosnav) return;

/* TOP FRAME ONLY. WebView2 injects this into every document in the WebView,
   which means every iframe as well - an ad, a video embed, a consent widget.
   Each copy would build its own focus ring and each would answer "ready" and
   "nofocus" to the host, and the host would have no way to tell which page
   the human is actually looking at.
   The cost is real and worth stating plainly: content inside an iframe is not
   reachable by spatial navigation. A cross-origin frame could not be scripted
   from the top document anyway, so the alternative was never "navigate into
   frames", it was "navigate into frames incoherently". Cursor mode still
   works over an iframe, because a synthesised click at a screen position is
   delivered by the browser to whatever is under it, frame or not - which is
   exactly the case cursor mode exists for. */
if (window.top !== window) return;

var VERSION = "1.0.0";
var ACCENT = "#50e380";
var ACCENT_HOT = "#c9e5bb";

/* ═══ Never throw ══════════════════════════════════════════════════════
   This layer is the only way the human can move on this page. If it dies
   the television is stuck on whatever is on screen, so every boundary is
   wrapped and every failure is reported to the host log rather than to a
   console nobody is looking at. */

function post(o) {
  try { window.chrome.webview.postMessage(JSON.stringify(o)); } catch (e) {}
}
function say(text) { post({ type: "mosnav", ev: "log", text: String(text) }); }
function guard(where, fn) {
  return function () {
    try { return fn.apply(this, arguments); }
    catch (err) { say("mosnav[" + where + "] " + (err && err.stack ? err.stack : err)); }
  };
}

/* ═══ State ════════════════════════════════════════════════════════════ */

var st = {
  mode: "spatial",
  items: [],            // last built focusable set
  el: null,             // the focused element itself, not an index: the page
                        // renumbers its own DOM constantly and an index goes
                        // stale between one D-pad press and the next
  rect: null,
  cx: 0, cy: 0,         // cursor-mode pointer, in client coordinates
  vx: 0, vy: 0,         // cursor velocity
  sx: 0, sy: 0,         // scroll velocity
  raf: 0,
  pending: null,        // the element waiting for the OSK's answer
  inlineStyleWorks: null,
  shadow: null,
  sheet: null,
  ring: null,
  dot: null,
  hint: null,
  lastScan: 0
};

/* ═══ The overlay ══════════════════════════════════════════════════════
   A single host element, a CLOSED shadow root, and one constructable
   stylesheet. Closed because a page that walks its own DOM should not be
   able to find, style or remove the focus ring — several sites sanitise
   unexpected children out of <body> on a timer.

   The stylesheet is the CSP story. `sheet.replaceSync(text)` mutates CSSOM
   directly and is not a style-src event, whereas a <style> element or an
   element.style assignment both are. Rewriting a 300-byte sheet on every
   move is cheap; in cursor mode it happens per animation frame, which is
   still under a tenth of a millisecond. */

function ensureOverlay() {
  if (st.shadow && st.ring && st.ring.isConnected !== false && document.documentElement.contains(st.host)) return true;
  if (!document.documentElement) return false;

  var host = document.createElement("arc-nav-overlay");
  st.host = host;
  var shadow;
  try { shadow = host.attachShadow({ mode: "closed" }); }
  catch (e) { shadow = host.attachShadow({ mode: "open" }); }

  var sheet = null;
  try {
    sheet = new CSSStyleSheet();
    shadow.adoptedStyleSheets = [sheet];
  } catch (e) {
    /* Pre-constructable-stylesheet engines only. A <style> element is the
       fallback and CSP may refuse it, in which case the ring is invisible
       but navigation still works — degraded, not broken. */
    var s = document.createElement("style");
    shadow.appendChild(s);
    sheet = { replaceSync: function (t) { try { s.textContent = t; } catch (e2) {} } };
  }
  st.sheet = sheet;

  var ring = document.createElement("div"); ring.setAttribute("part", "ring");
  var dot = document.createElement("div");  dot.setAttribute("part", "dot");
  var hint = document.createElement("div"); hint.setAttribute("part", "hint");
  shadow.appendChild(ring); shadow.appendChild(dot); shadow.appendChild(hint);
  st.shadow = shadow; st.ring = ring; st.dot = dot; st.hint = hint;

  /* The host element itself must not be clickable, must not scroll the page,
     and must not participate in the page's layout at all. */
  base();
  document.documentElement.appendChild(host);
  paint();
  return true;
}

function base() {
  /* Everything that does not change per frame. Written once. */
  st.baseCss =
    ':host{all:initial;position:fixed;left:0;top:0;width:0;height:0;pointer-events:none;z-index:2147483647}' +
    'div{position:fixed;pointer-events:none;box-sizing:border-box}' +
    '[part=ring]{border:0.1875rem solid ' + ACCENT + ';border-radius:0.375rem;' +
      'box-shadow:0 0 0 0.125rem rgba(4,6,11,.85),0 0 1.5rem rgba(76,141,255,.55);' +
      'transition:left .09s cubic-bezier(.16,.84,.24,1),top .09s cubic-bezier(.16,.84,.24,1),' +
      'width .09s cubic-bezier(.16,.84,.24,1),height .09s cubic-bezier(.16,.84,.24,1);display:none}' +
    '[part=dot]{width:1.5rem;height:1.5rem;margin:-0.75rem 0 0 -0.75rem;border-radius:50%;' +
      'border:0.125rem solid ' + ACCENT_HOT + ';background:rgba(80,227,128,.32);' +
      'box-shadow:0 0 0 0.0625rem rgba(4,6,11,.9),0 0 1rem rgba(166,212,255,.6);display:none}' +
    '[part=hint]{display:none}';
}

/* Position the ring and the cursor. Two paths: element.style where the page's
   CSP allows it (one property write, no reparse), and a stylesheet rewrite
   where it does not. Which one is in use is decided once, by writing a value
   and reading it back — a blocked inline style silently does nothing, so the
   read-back is the only reliable detector. */
function paint() {
  if (!st.sheet) return;

  var r = st.rect;
  var ringCss = (st.mode === "spatial" && r)
    ? '[part=ring]{display:block;left:' + Math.round(r.left) + 'px;top:' + Math.round(r.top) +
      'px;width:' + Math.round(r.width) + 'px;height:' + Math.round(r.height) + 'px}'
    : '';
  var dotCss = (st.mode === "cursor")
    ? '[part=dot]{display:block;left:' + Math.round(st.cx) + 'px;top:' + Math.round(st.cy) + 'px}'
    : '';

  try { st.sheet.replaceSync(st.baseCss + ringCss + dotCss); }
  catch (e) { say("paint: " + e); }
}

/* ═══ Finding what can be focused ══════════════════════════════════════
   Deliberately generous on the way in and strict on the way out: collect
   anything that could plausibly be interactive, then throw away everything
   that is invisible, disabled, zero-sized, or wholly contained inside
   another candidate. Being generous is what makes div-based buttons work;
   being strict afterwards is what stops a page with 4000 <a> tags in a
   hidden mega-menu from making the D-pad useless. */

var SEL = [
  'a[href]', 'area[href]', 'button', 'summary', 'video', 'audio',
  'input:not([type="hidden"])', 'select', 'textarea',
  '[tabindex]:not([tabindex="-1"])',
  '[contenteditable=""]', '[contenteditable="true"]',
  '[role="button"]', '[role="link"]', '[role="checkbox"]', '[role="radio"]',
  '[role="tab"]', '[role="menuitem"]', '[role="menuitemcheckbox"]',
  '[role="option"]', '[role="switch"]', '[role="slider"]', '[role="searchbox"]',
  '[role="textbox"]', '[role="combobox"]'
].join(",");

/* Shadow DOM is not optional any more. YouTube's entire player and shell are
   custom elements; without this walk the page looks like it has four links on
   it. Closed roots stay invisible — nothing can be done about those, and
   cursor mode is the answer when a site is built that way. */
function collectRoots() {
  var roots = [document];
  var seen = 0;
  while (seen < roots.length && roots.length < 400) {
    var root = roots[seen++];
    var all;
    try { all = root.querySelectorAll("*"); } catch (e) { continue; }
    for (var i = 0; i < all.length; i++) {
      var sr = all[i].shadowRoot;
      if (sr) roots.push(sr);
    }
  }
  return roots;
}

function visible(el, vw, vh) {
  var r;
  try { r = el.getBoundingClientRect(); } catch (e) { return null; }
  if (!r || r.width < 6 || r.height < 6) return null;
  /* One viewport of slack either side: an item just off screen is a legal
     move target, and moving to it is what scrolls the page to it. */
  if (r.bottom < -vh || r.top > vh * 2 || r.right < -vw || r.left > vw * 2) return null;
  var cs;
  try { cs = getComputedStyle(el); } catch (e) { return null; }
  if (!cs || cs.visibility === "hidden" || cs.display === "none" || cs.pointerEvents === "none") return null;
  if (cs.opacity !== "" && parseFloat(cs.opacity) < 0.08) return null;
  if (el.disabled) return null;
  if (el.getAttribute && el.getAttribute("aria-hidden") === "true") return null;
  return r;
}

function scan() {
  var vw = window.innerWidth || 1, vh = window.innerHeight || 1;
  var roots = collectRoots();
  var out = [], i, j;

  for (i = 0; i < roots.length; i++) {
    var found;
    try { found = roots[i].querySelectorAll(SEL); } catch (e) { continue; }
    for (j = 0; j < found.length; j++) {
      var el = found[j];
      var r = visible(el, vw, vh);
      if (!r) continue;
      out.push({ el: el, r: r, cx: r.left + r.width / 2, cy: r.top + r.height / 2 });
      if (out.length > 1200) break;      // a hard ceiling; see the note above
    }
    if (out.length > 1200) break;
  }

  /* Drop a candidate that is entirely inside another candidate and much
     smaller than it — an <a> wrapping a <button> wrapping an <img role=link>
     is three targets in one place, and only the outermost useful one should
     be stoppable. */
  out.sort(function (a, b) { return (b.r.width * b.r.height) - (a.r.width * a.r.height); });
  var kept = [];
  for (i = 0; i < out.length; i++) {
    var c = out[i], swallowed = false;
    for (j = 0; j < kept.length; j++) {
      var k = kept[j];
      if (c.r.left >= k.r.left - 1 && c.r.right <= k.r.right + 1 &&
          c.r.top >= k.r.top - 1 && c.r.bottom <= k.r.bottom + 1 &&
          (c.r.width * c.r.height) > 0.62 * (k.r.width * k.r.height)) { swallowed = true; break; }
    }
    if (!swallowed) kept.push(c);
  }

  kept.sort(function (a, b) { return (a.cy - b.cy) || (a.cx - b.cx); });
  st.items = kept;
  st.lastScan = Date.now();
  return kept;
}

function fresh() {
  /* Rescanning costs a layout pass, so it is not done per keypress on a
     static page — but 250 ms is short enough that a page which rebuilt
     itself (an infinite scroll, a menu opening) is never navigated stale. */
  if (!st.items.length || Date.now() - st.lastScan > 250) scan();
  return st.items;
}

function rectOf(el) {
  try {
    var r = el.getBoundingClientRect();
    if (!r || (!r.width && !r.height)) return null;
    return r;
  } catch (e) { return null; }
}

/* ═══ Spatial movement ═════════════════════════════════════════════════
   Geometry, not DOM order. The rule that makes this feel right on real
   pages: an item that overlaps the current one on the perpendicular axis
   is always preferred over one that does not, however much closer the
   second is in a straight line. Without that, moving down a column of
   search results jumps sideways into the sidebar the moment a result is a
   few pixels shorter than its neighbour. */

function score(from, cand, dx, dy) {
  var a = from, b = cand.r;
  var ahead, lateral, overlap;

  if (dy) {
    ahead = dy > 0 ? (b.top - a.bottom) : (a.top - b.bottom);
    overlap = Math.min(a.right, b.right) - Math.max(a.left, b.left);
    lateral = Math.abs((b.left + b.right) / 2 - (a.left + a.right) / 2);
  } else {
    ahead = dx > 0 ? (b.left - a.right) : (a.left - b.right);
    overlap = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top);
    lateral = Math.abs((b.top + b.bottom) / 2 - (a.top + a.bottom) / 2);
  }
  if (ahead < -4) return Infinity;                 // behind us, or the same row
  var s = Math.max(ahead, 0) + lateral * 2.2;
  if (overlap > 0) s -= Math.min(overlap, 400) * 1.6;   // strongly prefer the same column/row
  return s;
}

function move(dx, dy) {
  var items = fresh();
  if (!items.length) { post({ type: "mosnav", ev: "nofocus" }); return scrollBy(dx, dy) ? "scrolled" : "nothing here"; }

  var cur = st.el && rectOf(st.el);
  if (!cur) {
    /* No focus yet, or the focused element has gone. Start from the top-left
       of the viewport rather than from wherever the last one used to be. */
    focusItem(nearest(items, 0, 0));
    return "entered";
  }

  var best = null, bestScore = Infinity;
  for (var i = 0; i < items.length; i++) {
    if (items[i].el === st.el) continue;
    var s = score(cur, items[i], dx, dy);
    if (s < bestScore) { bestScore = s; best = items[i]; }
  }

  if (!best) {
    /* Nothing that way. Scroll instead — on a long article that is exactly
       what the human meant, and it is what stops the D-pad feeling dead
       between one block of links and the next. */
    if (scrollBy(dx, dy)) { setTimeout(guard("rescan", function () { scan(); reFocus(); }), 140); return "scrolled"; }
    return "edge";
  }
  focusItem(best);
  return "moved";
}

function nearest(items, x, y) {
  var best = items[0], bd = Infinity;
  for (var i = 0; i < items.length; i++) {
    var d = Math.abs(items[i].cx - x) + Math.abs(items[i].cy - y);
    if (d < bd) { bd = d; best = items[i]; }
  }
  return best;
}

function focusItem(it) {
  if (!it) return;
  st.el = it.el;
  /* Real DOM focus as well as the ring: it is what makes the Enter key,
     screen readers and the page's own :focus styling agree with us, and it
     is what scrolls a scroll container to the item without us having to work
     out which container that is.

     But it must not raise the keyboard. Moving the ring ACROSS a page is not
     the human asking to type - a search results page with a search box at
     the top would throw a keyboard up every time the D-pad passed over it,
     and there would be no way to get past it. Cross on the field opens the
     keyboard; the focus listener covers the case the human did not drive,
     which is the page moving focus itself. */
  suppress(it.el);
  try { it.el.focus({ preventScroll: true }); } catch (e) {}
  ensureVisible(it.el);
  reFocus();
}

function reFocus() {
  if (!st.el) return;
  var r = rectOf(st.el);
  if (!r) { st.el = null; st.rect = null; paint(); return; }
  st.rect = r;
  paint();
  /* Only when it actually changed. reFocus() is called by the mutation
     settle, the zoom handler, resize and the scroll loop as well as by a real
     move, and a page that repaints itself every 400 ms would otherwise fill
     the host log with the same line forever. */
  if (st.el !== st.reported) {
    st.reported = st.el;
    post({
      type: "mosnav", ev: "focus",
      label: describe(st.el),
      kind: kindOf(st.el),
      href: (st.el.tagName === "A" && st.el.href) ? String(st.el.href).slice(0, 400) : null
    });
  }
  if (kindOf(st.el) === "video") reportMedia(st.el);
}

/* padFrac biases how much clearance the element is given. The default 0.16
   is the navigation case ("do not leave it against an edge"). A text field
   asks for more, and for it to sit high rather than centred, because the
   keyboard occupies the bottom of the display: when the content view is
   shown again after a commit, the field and the text now in it should be in
   the upper half where they are certainly not behind anything. */
/* Returns true when it actually asked the page to move, false when the
   element was already inside the band and nothing happened. Every caller but
   R3 ignores it; R3 needs it because it has to tell the human which of the
   two occurred instead of claiming "recentred" either way.

   The false answer is certain: the element was in the band, no scroll was
   requested, nothing moved. The true answer is "a scroll was issued" rather
   than "the viewport is now elsewhere" - a smooth scroll has not applied by
   the time this returns, so the result cannot be measured here, only the
   request. That is the same reason scrollBy() below compares capability and
   not result. Saying "recentred" for a scroll that was issued against a
   scroll container already at its stop is the one remaining overclaim, and
   it is small: the element was genuinely off-band, so the human's press was
   not a no-op in the way "already in view" would suggest. */
function ensureVisible(el, padFrac) {
  var r = rectOf(el); if (!r) return false;
  var vh = window.innerHeight, vw = window.innerWidth;
  var frac = (typeof padFrac === "number") ? padFrac : 0.16;
  var pad = Math.round(vh * frac);
  if (r.top < pad || r.bottom > vh - pad || r.left < 0 || r.right > vw) {
    try { el.scrollIntoView({ block: "center", inline: "nearest", behavior: "smooth" }); }
    catch (e) { try { el.scrollIntoView(); } catch (e2) {} }
    return true;
  }
  return false;
}

/* NEVER let a field's contents into this string.
 *
 * describe() is the label that goes into {ev:"focus"}, and the shell writes
 * that straight to the host log so an unattended run can be read back. It used
 * to fall back to el.value for an <input>, which meant that every time the ring
 * landed on a filled-in field its contents were logged - including a password,
 * whose type carries no placeholder to shadow it. The value is never a
 * legitimate label. What a field IS gets described here; what is IN it does
 * not leave the page except in the {ev:"edit"} payload, which exists solely to
 * seed the keyboard and is never logged.
 */
function describe(el) {
  var t = "";
  try {
    if (isTextField(el)) {
      t = fieldTitle(el);
    } else {
      t = (el.getAttribute && el.getAttribute("aria-label")) ||
          (el.getAttribute && el.getAttribute("title")) ||
          (el.innerText || el.textContent || "").trim() ||
          (el.tagName === "IMG" ? el.alt : "") || "";
    }
  } catch (e) {}
  t = String(t).replace(/\s+/g, " ").trim();
  if (!t) t = "<" + String(el.tagName || "?").toLowerCase() + ">";
  return t.length > 90 ? t.slice(0, 88) + "…" : t;
}

var TEXTY = { text:1, search:1, email:1, url:1, tel:1, number:1, password:1, "":1 };

/* ═══ Editable fields ══════════════════════════════════════════════════
   The set the keyboard opens for, and it is deliberately narrower than the
   set the RING stops on. A <input type=checkbox> is focusable and is not
   typeable; a field that is readonly or disabled is focusable in some
   engines and must never raise a keyboard the human cannot commit.

   Zero-size and hidden fields are excluded for a specific reason rather
   than tidiness: a great many login pages carry a hidden honeypot input,
   and several carry an off-screen 1x1 field that real browsers focus
   during autofill. Raising the keyboard for one of those would be a
   keyboard the human cannot see a field for. */

var OSK_TYPES = { text:1, password:1, email:1, url:1, search:1, tel:1, number:1, "":1 };

/* input type -> the keyboard's own mode vocabulary. Anything not named here
   is text, which is the safe default: the wrong layout is recoverable, a
   missing keyboard is not. */
var OSK_MODE = {
  password: "password",
  url: "url",
  number: "number",
  email: "text",
  search: "text",
  text: "text",
  tel: "text",
  "": "text"
};

function typeOf(el) {
  return String((el && el.type) || "text").toLowerCase();
}

function isTextField(el) {
  if (!el || el.nodeType !== 1) return false;
  var tag = String(el.tagName || "").toLowerCase();
  if (tag === "textarea") return true;
  if (tag === "input") return !!OSK_TYPES[typeOf(el)];
  try { if (el.isContentEditable) return true; } catch (e) {}
  return false;
}

/* Focusable, typeable, and actually on the screen. */
function isEditable(el) {
  if (!isTextField(el)) return false;
  try {
    if (el.disabled) return false;
    if (el.readOnly) return false;
    if (el.getAttribute && el.getAttribute("aria-readonly") === "true") return false;
    if (el.getAttribute && el.getAttribute("aria-hidden") === "true") return false;
    var r = el.getBoundingClientRect();
    if (!r || r.width < 2 || r.height < 2) return false;
    var cs = getComputedStyle(el);
    if (!cs || cs.visibility === "hidden" || cs.display === "none") return false;
    if (cs.opacity !== "" && parseFloat(cs.opacity) < 0.05) return false;
    if (clippedAway(el)) return false;
  } catch (e) { return false; }
  return true;
}

/* The field itself measures normally and is still invisible, because an
   ancestor collapsed to nothing and clips it:

       <div style="width:0;height:0;overflow:hidden"><input ...></div>

   getBoundingClientRect on the INPUT reports its own layout box and knows
   nothing about the clip, so the size test above passes it. This is not an
   edge case to be thorough about — it is how hidden fields are actually
   built. Login pages carry honeypots in this shape to catch form-filling
   bots, and a keyboard raised for a field the human cannot see is a keyboard
   over a page with no visible field, which is indistinguishable from a bug.

   Deliberately narrow: ONLY a clipping ancestor with no area at all counts.
   A field merely scrolled out of its scroll container is left alone, because
   focus() scrolls it into view before focusin fires, and rejecting that case
   would break the one thing this whole path exists for. */
function clippedAway(el) {
  var node = el.parentElement, hops = 0;
  while (node && hops++ < 12) {
    var cs;
    try { cs = getComputedStyle(node); } catch (e) { return false; }
    if (cs.overflowX !== "visible" || cs.overflowY !== "visible") {
      var pr;
      try { pr = node.getBoundingClientRect(); } catch (e2) { return false; }
      if (pr && (pr.width < 2 || pr.height < 2)) return true;
    }
    node = node.parentElement;
  }
  return false;
}

/* A name for the field, for the keyboard's title bar. In the order a human
   would read the page: the accessible name first, then the visible label,
   then the placeholder, and "Text" rather than nothing. Never the value —
   see describe(). */
function fieldTitle(el) {
  var t = "";
  try {
    t = (el.getAttribute && el.getAttribute("aria-label")) || "";

    if (!t) {
      var by = el.getAttribute && el.getAttribute("aria-labelledby");
      if (by) {
        var parts = by.split(/\s+/), acc = [];
        for (var i = 0; i < parts.length; i++) {
          var n = document.getElementById(parts[i]);
          if (n) acc.push(n.innerText || n.textContent || "");
        }
        t = acc.join(" ");
      }
    }

    /* el.labels is the engine's own answer to "which <label> is this?" and
       handles both wrapping and for= without us reimplementing it. */
    if (!t) {
      try {
        if (el.labels && el.labels.length) {
          var ls = [];
          for (var j = 0; j < el.labels.length && j < 3; j++)
            ls.push(el.labels[j].innerText || el.labels[j].textContent || "");
          t = ls.join(" ");
        }
      } catch (e2) {}
    }
    if (!t && el.closest) {
      var lab = el.closest("label");
      if (lab) t = lab.innerText || lab.textContent || "";
    }
    if (!t && el.id) {
      var forLab = document.querySelector('label[for="' + String(el.id).replace(/"/g, '\\"') + '"]');
      if (forLab) t = forLab.innerText || forLab.textContent || "";
    }

    if (!t) t = (el.placeholder || "");
    if (!t) t = (el.getAttribute && el.getAttribute("title")) || "";
    if (!t) t = (el.name || "");
  } catch (e) {}

  t = String(t).replace(/\s+/g, " ").trim();
  if (!t) {
    var ty = typeOf(el);
    t = ty === "password" ? "Password"
      : ty === "email"    ? "Email"
      : ty === "search"   ? "Search"
      : ty === "url"      ? "Address"
      : ty === "tel"      ? "Telephone"
      : ty === "number"   ? "Number"
      : "Text";
  }
  return t.length > 60 ? t.slice(0, 58) + "…" : t;
}

function currentValue(el) {
  try {
    if (el.value !== undefined && el.value !== null) return String(el.value);
    return String(el.innerText || el.textContent || "");
  } catch (e) { return ""; }
}

/* A declared maxlength only. The DOM reports 524288 (and some engines -1)
   for "no limit", and passing that to the keyboard as a real cap would put
   a meaningless counter under it. */
function maxLenOf(el) {
  var n = -1;
  try { n = el.maxLength; } catch (e) { return 0; }
  if (typeof n !== "number" || n <= 0 || n >= 524288) return 0;
  return n;
}

/* ═══ Asking the shell for the keyboard ════════════════════════════════
   One place, whichever way the field was reached: the human pressed Cross
   on it, the page moved focus into it (the second half of a login form),
   or the page autofocused it on load.

   st.pending is the element the answer belongs to. It is held rather than
   an index because the page is free to rebuild its DOM while the keyboard
   is up, and writing the answer into whatever now occupies that position
   would be worse than dropping it. */

function requestEdit(el, why) {
  if (!isEditable(el)) return "not an editable field";
  st.pending = el;
  st.lastField = el;

  /* Put the field where it will be visible when the page comes back. The
     shell hides the content view entirely while the keyboard is up (it is a
     child window; nothing can be drawn over it), so this is not about
     clearing the panel - it is so that the field, and what was just typed
     into it, is on screen the moment the view is shown again. */
  ensureVisible(el, 0.34);

  var ty = typeOf(el);
  var multiline = false;
  try { multiline = el.tagName === "TEXTAREA" || !!el.isContentEditable; } catch (e) {}

  post({
    type: "mosnav", ev: "edit",
    /* The one message in this protocol that carries what the human typed.
       It goes to the keyboard and nowhere else; the shell does not log it,
       and nothing downstream of the shell ever sees it. */
    value: currentValue(el),
    inputType: multiline && el.tagName === "TEXTAREA" ? "textarea" : ty,
    mode: multiline ? "text" : (OSK_MODE[ty] || "text"),
    maxLength: maxLenOf(el),
    label: fieldTitle(el),
    secure: ty === "password",
    multiline: multiline,
    why: why || "activate"
  });
  return "editing";
}

function kindOf(el) {
  var tag = String(el.tagName || "").toLowerCase();
  if (tag === "video") return "video";
  if (tag === "audio") return "audio";
  if (tag === "select") return "select";
  if (tag === "textarea") return "text";
  if (tag === "input") {
    var ty = String(el.type || "text").toLowerCase();
    if (TEXTY[ty]) return "text";
    if (ty === "checkbox" || ty === "radio") return "toggle";
    if (ty === "range") return "slider";
    return "button";
  }
  if (el.isContentEditable) return "text";
  var role = el.getAttribute && el.getAttribute("role");
  if (role === "textbox" || role === "searchbox") return "text";
  if (role === "slider") return "slider";
  if (tag === "a") return "link";
  return "button";
}

/* ═══ Activation ═══════════════════════════════════════════════════════
   A synthesised click has to be the whole sequence, in order, with real
   coordinates. Pages listen on pointerdown, on mousedown, on mouseup and on
   click in roughly equal numbers, and menus in particular open on
   pointerdown and close again on a click they never saw start. */

function clickAt(el, x, y) {
  var opts = { bubbles: true, cancelable: true, composed: true, clientX: x, clientY: y, button: 0, buttons: 1 };
  var seq = ["pointerover", "pointerenter", "pointermove", "pointerdown", "mousedown",
             "pointerup", "mouseup", "click"];
  for (var i = 0; i < seq.length; i++) {
    var name = seq[i];
    var Ctor = name.indexOf("pointer") === 0 ? (window.PointerEvent || MouseEvent) : MouseEvent;
    var o = opts;
    if (name === "pointerup" || name === "mouseup" || name === "click") o = merge(opts, { buttons: 0 });
    var ev;
    try {
      ev = new Ctor(name, name.indexOf("pointer") === 0
        ? merge(o, { pointerId: 1, pointerType: "mouse", isPrimary: true })
        : o);
    } catch (e) { continue; }
    try { el.dispatchEvent(ev); } catch (e) {}
  }
}

function merge(a, b) {
  var o = {}, k;
  for (k in a) if (Object.prototype.hasOwnProperty.call(a, k)) o[k] = a[k];
  for (k in b) if (Object.prototype.hasOwnProperty.call(b, k)) o[k] = b[k];
  return o;
}

function activate() {
  if (!st.el) { var it = fresh()[0]; if (it) focusItem(it); return "entered"; }
  var kind = kindOf(st.el);
  var r = rectOf(st.el);
  var x = r ? r.left + r.width / 2 : 0, y = r ? r.top + r.height / 2 : 0;

  if (kind === "text") {
    /* Focus it for real first. A page that styles :focus, and a page whose
       own script watches focus to decide what the field means, both need
       this to have happened before the keyboard goes up - and it is what
       makes the field the one a later Triangle reopens. */
    try { st.el.focus({ preventScroll: true }); } catch (e) {}
    return requestEdit(st.el, "activate");
  }

  if (kind === "select") {
    var opts = [], o;
    try {
      for (var i = 0; i < st.el.options.length && i < 300; i++) {
        o = st.el.options[i];
        opts.push({ label: (o.label || o.text || o.value || "").slice(0, 90), value: o.value });
      }
    } catch (e) {}
    st.pending = st.el;
    post({ type: "mosnav", ev: "select", label: describe(st.el), options: opts, index: st.el.selectedIndex });
    return "choosing";
  }

  if (kind === "video" || kind === "audio") { return togglePlay(st.el); }

  try { st.el.focus({ preventScroll: true }); } catch (e) {}
  clickAt(st.el, x, y);
  /* Whatever it did probably changed the page. Re-measure rather than leave
     a ring floating over what used to be there. */
  setTimeout(guard("post-click", function () { scan(); reFocus(); }), 220);
  return "clicked " + kind;
}

/* ═══ Text fields ══════════════════════════════════════════════════════
   The OSK lives in the shell, one process-internal hop away. The value comes
   back here and has to be written the way a human would have written it, or
   React and friends will overwrite it on their next render: set the value
   through the native setter so the framework's own property descriptor sees
   it, then fire input and change. */

/* Why the native setter, spelled out, because it looks like superstition
   and is not:
 *
 * React does not read the DOM. It keeps its own copy of the value and, on
 * every render, compares it to what it last wrote to the node. Assigning
 * el.value = x updates the node but ALSO updates React's private "last
 * written" tracker (React installs its own value setter on the instance to
 * do exactly that), so React concludes nothing changed, never runs the
 * onChange handler, and overwrites the field on the next render. The field
 * visibly reverts a moment after the keyboard closes.
 *
 * Calling the PROTOTYPE's setter writes the node without touching React's
 * per-instance tracker. The tracker now disagrees with the DOM, so when the
 * input event arrives React sees a real change and runs onChange. The same
 * reasoning covers Vue, Angular and Svelte, which all bind on input.
 *
 * Then the events. input first (that is what every framework binds), change
 * second (that is what plain HTML forms and validation libraries bind), both
 * bubbling and composed so a field inside a shadow root still reports to a
 * listener on the host. input is a real InputEvent rather than a bare Event:
 * a handful of editors read inputType to decide whether to accept the edit,
 * and one built with `new Event("input")` has no inputType at all.
 */
function writeText(value) {
  var el = st.pending;
  st.pending = null;
  if (!el) return "no field waiting";

  /* The write moves focus back onto the field, which would immediately ask
     for the keyboard again. Hold that off for this element. */
  suppress(el);

  var secure = false;
  try { secure = typeOf(el) === "password"; } catch (e) {}

  try {
    var cap = maxLenOf(el);
    if (cap && value.length > cap) value = value.slice(0, cap);

    if (el.isContentEditable) {
      el.textContent = value;
    } else {
      var proto = (el.tagName === "TEXTAREA") ? window.HTMLTextAreaElement : window.HTMLInputElement;
      var desc = proto && Object.getOwnPropertyDescriptor(proto.prototype, "value");
      if (desc && desc.set) desc.set.call(el, value);
      else el.value = value;
      try { el.setSelectionRange(value.length, value.length); } catch (e0) {}
    }

    var ev;
    try {
      ev = new InputEvent("input", {
        bubbles: true, composed: true, cancelable: false,
        inputType: "insertReplacementText", data: value
      });
    } catch (e1) {
      ev = new Event("input", { bubbles: true });
    }
    el.dispatchEvent(ev);
    el.dispatchEvent(new Event("change", { bubbles: true }));
  } catch (e) { say("writeText failed: " + (e && e.message ? e.message : e)); }

  try { el.focus({ preventScroll: true }); } catch (e) {}
  ensureVisible(el, 0.34);
  reFocus();

  /* NOT the value, and not its length either. A length is not a secret for a
     search box and is a real one for a password, and a log line whose safety
     depends on which field it happened to be is a log line waiting to be
     wrong. The field's name is enough to read a run back. */
  return secure ? "filled the password field" : "filled " + fieldTitle(el);
}

/* An element we have just written to or restored focus to, which must not
   raise the keyboard again on the focus event our own write causes. Short
   and element-scoped: a genuine second visit to the same field a moment
   later still opens, which is what makes a mistyped password recoverable. */
function suppress(el) {
  st.suppressEl = el;
  st.suppressUntil = Date.now() + 700;
}
function suppressed(el) {
  return st.suppressEl === el && Date.now() < st.suppressUntil;
}

function chooseOption(index) {
  var el = st.pending;
  st.pending = null;
  if (!el) return "no list waiting";
  try {
    el.selectedIndex = index;
    el.dispatchEvent(new Event("input", { bubbles: true }));
    el.dispatchEvent(new Event("change", { bubbles: true }));
  } catch (e) { say("chooseOption: " + e); }
  reFocus();
  return "selected " + index;
}

/* ═══ Media ════════════════════════════════════════════════════════════
   When the focused element is a video the D-pad changes meaning, which is
   the only place in this file where that is true. It earns the exception:
   nobody wants to hunt for a seek bar with a virtual cursor. */

function media() {
  if (st.el && (st.el.tagName === "VIDEO" || st.el.tagName === "AUDIO")) return st.el;
  /* Fullscreen is the other case: the video may not be the focused element
     but it is the only thing on screen. */
  var fs = document.fullscreenElement;
  if (fs) {
    if (fs.tagName === "VIDEO") return fs;
    var v = fs.querySelector && fs.querySelector("video");
    if (v) return v;
  }
  return null;
}

function togglePlay(v) {
  try {
    if (v.paused) { var p = v.play(); if (p && p.catch) p.catch(function () {}); }
    else v.pause();
  } catch (e) {}
  reportMedia(v);
  return v.paused ? "paused" : "playing";
}

function reportMedia(v) {
  try {
    post({
      type: "mosnav", ev: "media",
      playing: !v.paused, muted: !!v.muted,
      time: Math.round(v.currentTime || 0), duration: Math.round(v.duration || 0),
      volume: Math.round((v.volume === undefined ? 1 : v.volume) * 100),
      fullscreen: !!document.fullscreenElement
    });
  } catch (e) {}
}

function seek(v, d) {
  try { v.currentTime = Math.max(0, Math.min((v.duration || 1e9), (v.currentTime || 0) + d)); } catch (e) {}
  reportMedia(v);
  return "seek " + d + "s";
}

/* The page's own Fullscreen API.
 *
 * This works ONLY because discrete pad actions arrive through the host's
 * ExecuteScriptAsync rather than through a web message: requestFullscreen()
 * demands a transient user activation, and a handler running off
 * chrome.webview's message event has none. Delivered the other way it is
 * granted, and the site's own fullscreen UI - the one with its controls in
 * it - engages properly.
 *
 * It is still not relied on. The shell hides its chrome and gives the
 * content window the whole display at the same moment, so a site that
 * refuses, or has no fullscreen support at all, still fills the television.
 * The refusal is reported rather than swallowed, so the log says which of
 * the two actually happened.
 */
/* What may be put full screen, and why it is not "whatever is focused".
 *
 * The old target was `v || media() || st.el`, and st.el on a page with no
 * video at all is a link or a button. That fell into the closest()
 * ["class contains player"] walk, which on a page with none of those
 * resolves to the element itself, and the shell then took a <a> full screen
 * and logged "asked the page for full screen" - a true sentence about a
 * meaningless act, which reads to a human as if Square had done the thing
 * Square is for. Full screen is only meaningful for something playing.
 *
 * So the focused element qualifies only when it IS a video or sits inside the
 * chrome wrapped around one - a play button, a scrub bar - and "contains a
 * video somewhere" is NOT enough to prove that. Every element on a page has
 * <body> as an ancestor, and on a page with an article and a video in it the
 * article wrapper contains both; taking the first ancestor with a <video>
 * under it puts a link's grandparent DIV full screen and tells the same lie
 * one level up. Two things have to hold:
 *
 *   1. It is close. A play button is inside its player's chrome, not six
 *      layout wrappers above it, so the walk gives up after a few levels and
 *      never considers body or the document element at all.
 *   2. It is mostly video. The container's box has to be largely filled by
 *      the video's box - which is what a player IS, and what a page section
 *      that merely happens to hold a video is not. A 1250x2400 column with a
 *      320x180 clip in it fails this by two orders of magnitude; a player
 *      div hugging its video passes at nearly 1.
 *
 * Anything that fails both is refused, honestly, and the shell's own full
 * screen - which is already covering the television - is what happens. */
var PLAYER_FILL = 0.4;   // video area / container area before it counts as a player
var PLAYER_DEPTH = 6;    // ancestors to consider above the focused element

function mostlyVideo(box, v) {
  var b, r;
  try { b = box.getBoundingClientRect(); r = v.getBoundingClientRect(); } catch (e) { return false; }
  var ba = b.width * b.height, ra = r.width * r.height;
  if (ba <= 0 || ra <= 0) return false;
  return ra >= ba * PLAYER_FILL;
}

function playerFor(el) {
  if (!el || el.nodeType !== 1) return null;
  if (el.tagName === "VIDEO") return el;
  var node = el, up = 0;
  while (node && node.nodeType === 1 && up++ < PLAYER_DEPTH) {
    if (node === document.body || node === document.documentElement) break;
    var v = null;
    try { v = node.querySelector && node.querySelector("video"); } catch (e) {}
    if (v && mostlyVideo(node, v)) return node;
    node = node.parentElement || (node.getRootNode && node.getRootNode().host) || null;
  }
  return null;
}

function fullscreen(v) {
  try {
    if (document.fullscreenElement) { document.exitFullscreen(); return "left the page's full screen"; }
    var target = v || media() || playerFor(st.el);
    if (!target) return "no page full screen available; the shell's is what you get";
    var box = target.closest ? (target.closest("[class*=player],[id*=player],video") || target) : target;
    var p = (box.requestFullscreen ? box.requestFullscreen() : target.requestFullscreen());
    if (p && p["catch"]) p["catch"](function (err) {
      say("the page refused full screen (" + (err && err.message ? err.message : err)
        + ") - the shell's own full screen is in effect instead");
    });
    return "asked the page for full screen";
  } catch (e) { return "the page refused full screen: " + e; }
}

/* ═══ Scrolling ════════════════════════════════════════════════════════
   The right stick scrolls whatever is actually scrollable under the focus,
   which on a modern site is very often not the document. Walk up from the
   focused element (or from under the cursor) until something can move. */

function scroller(from) {
  var el = from || st.el || document.body;
  var guardCount = 0;
  while (el && el !== document.body && el !== document.documentElement && guardCount++ < 40) {
    var cs;
    try { cs = getComputedStyle(el); } catch (e) { break; }
    var oy = cs.overflowY, ox = cs.overflowX;
    if ((oy === "auto" || oy === "scroll" || ox === "auto" || ox === "scroll") &&
        (el.scrollHeight > el.clientHeight + 4 || el.scrollWidth > el.clientWidth + 4)) return el;
    el = el.parentElement || (el.getRootNode && el.getRootNode().host) || null;
  }
  return null;
}

function scrollBy(dx, dy, amount) {
  var vh = window.innerHeight, vw = window.innerWidth;
  var stepY = amount === undefined ? Math.round(vh * 0.22) : amount;
  var stepX = amount === undefined ? Math.round(vw * 0.22) : amount;
  var box = scroller(st.el);
  var before, after;
  if (box) {
    before = box.scrollTop;
    box.scrollBy({ left: dx * stepX, top: dy * stepY, behavior: "smooth" });
    after = box.scrollTop;
    /* smooth scrolling has not applied yet, so compare capability not result */
    if (dy > 0 && box.scrollTop + box.clientHeight >= box.scrollHeight - 2) box = null;
    else if (dy < 0 && box.scrollTop <= 0) box = null;
    else return true;
  }
  var y0 = window.scrollY, x0 = window.scrollX;
  var maxY = Math.max(0, (document.documentElement.scrollHeight || 0) - vh);
  if (dy > 0 && y0 >= maxY - 2) return false;
  if (dy < 0 && y0 <= 0) return false;
  if (dx && Math.abs(dx) && (document.documentElement.scrollWidth || 0) <= vw) return false;
  window.scrollBy({ left: dx * stepX, top: dy * stepY, behavior: "smooth" });
  return true;
}

/* ═══ Cursor mode ══════════════════════════════════════════════════════
   The escape hatch, and the reason the human is never stuck. Spatial
   navigation will fail on some sites — canvas-drawn UIs, maps, anything
   that paints its controls rather than marking them up — and on those the
   left stick becomes a mouse. It is slower and everyone knows it; the point
   is that there is no page it cannot operate. */

function toCursor() {
  st.mode = "cursor";
  var r = st.el && rectOf(st.el);
  st.cx = r ? r.left + r.width / 2 : window.innerWidth / 2;
  st.cy = r ? r.top + r.height / 2 : window.innerHeight / 2;
  st.vx = st.vy = 0;
  paint(); pump();
  post({ type: "mosnav", ev: "mode", mode: "cursor", items: st.items.length });
  return "cursor mode";
}

function toSpatial() {
  st.mode = "spatial";
  scan();
  var it = st.items.length ? nearest(st.items, st.cx, st.cy) : null;
  if (it) focusItem(it); else { st.rect = null; paint(); }
  post({ type: "mosnav", ev: "mode", mode: "spatial", items: st.items.length });
  return "spatial mode";
}

function cursorClick() {
  var el = null;
  try { el = deepElementFromPoint(st.cx, st.cy); } catch (e) {}
  if (!el) return "nothing under the cursor";
  try { if (el.focus) el.focus({ preventScroll: true }); } catch (e) {}
  clickAt(el, st.cx, st.cy);
  return "clicked " + describe(el);
}

/* elementFromPoint stops at a shadow host. Recurse through open roots so a
   click lands on the real control inside a custom element. */
function deepElementFromPoint(x, y) {
  var el = document.elementFromPoint(x, y), guardCount = 0;
  while (el && el.shadowRoot && guardCount++ < 20) {
    var inner = el.shadowRoot.elementFromPoint(x, y);
    if (!inner || inner === el) break;
    el = inner;
  }
  return el;
}

function cursorHover() {
  var el = null;
  try { el = deepElementFromPoint(st.cx, st.cy); } catch (e) {}
  if (!el || el === st.hoverEl) return;
  st.hoverEl = el;
  try {
    el.dispatchEvent(new MouseEvent("pointermove", { bubbles: true, composed: true, clientX: st.cx, clientY: st.cy }));
    el.dispatchEvent(new MouseEvent("mousemove", { bubbles: true, composed: true, clientX: st.cx, clientY: st.cy }));
    el.dispatchEvent(new MouseEvent("mouseover", { bubbles: true, composed: true, clientX: st.cx, clientY: st.cy }));
  } catch (e) {}
}

/* The D-pad in cursor mode.
 *
 * It used to answer "use the stick" and do nothing, which is the wrong
 * answer twice over: a DualSense stick is not precise enough to put a
 * pointer on a 20 px close button from across a room, and there is no reason
 * the coarse control should be dead when the fine one exists. The D-pad now
 * nudges the same st.cx/st.cy the stick drives, one fixed step per press, so
 * the human rides the stick to the neighbourhood and taps the pad the last
 * few pixels.
 *
 * The step is a fraction of the SMALLER viewport dimension rather than a
 * pixel count, because this file runs on a 4K television and on a windowed
 * 720p WebView both, and a fixed 40 px is either a crawl or a leap depending
 * on which. 4% puts ~25 presses across the short axis - coarse enough to
 * cross the screen if you lean on it, fine enough to land on a control. The
 * floor stops it becoming sub-pixel in a tiny window. */
var CURSOR_STEP_FRAC = 0.04;

function cursorStepPx() {
  return Math.max(8, Math.round(Math.min(window.innerWidth, window.innerHeight) * CURSOR_STEP_FRAC));
}

/* Can the document still move this way? Asked BEFORE scrolling, not measured
   after: a page with CSS scroll-behavior:smooth has not moved by the time
   scrollBy() returns, so a before/after comparison would report "did not
   scroll" for a scroll that is under way. */
function pageCanScrollY(dy) {
  var y = window.scrollY || 0;
  var maxY = Math.max(0, (document.documentElement.scrollHeight || 0) - window.innerHeight);
  return dy > 0 ? y < maxY - 1 : y > 1;
}

function cursorStep(dx, dy) {
  var step = cursorStepPx();
  var x0 = st.cx, y0 = st.cy;
  /* Same clamp as the stick path in frame(), so the two controls cannot
     disagree about where the edge is. */
  st.cx = Math.max(2, Math.min(Math.max(2, window.innerWidth - 2), st.cx + dx * step));
  st.cy = Math.max(2, Math.min(Math.max(2, window.innerHeight - 2), st.cy + dy * step));

  if (st.cx !== x0 || st.cy !== y0) {
    paint();
    cursorHover();
    return "cursor moved " + (dx < 0 ? "left" : dx > 0 ? "right" : dy < 0 ? "up" : "down");
  }

  /* Pinned against the edge. Vertically the stick pushes the page instead of
     stopping dead (see frame()); the pad mirrors that, by its own step so a
     press moves the same distance whichever way it is spent. There is no
     horizontal equivalent to mirror - the stick does not side-scroll at the
     edge - so left/right simply reports the wall rather than inventing a
     behaviour the stick does not have. */
  var edge = dx < 0 ? "left" : dx > 0 ? "right" : dy < 0 ? "top" : "bottom";
  if (dy && pageCanScrollY(dy)) {
    window.scrollBy(0, dy * step);
    /* What is under the cursor changed even though the cursor did not. */
    cursorHover();
    return "cursor at " + edge + " edge, scrolled";
  }
  return "cursor at " + edge + " edge";
}

/* ═══ The analog loop ══════════════════════════════════════════════════
   One rAF driving both the cursor and inertial scrolling, started only when
   a stick is off centre and stopped as soon as both are back. A permanent
   rAF on every page in the world would be a battery and a fan. */

var CURSOR_SPEED = 15;        // px per frame at full deflection
var SCROLL_SPEED = 22;

function pump() {
  if (st.raf) return;
  st.raf = requestAnimationFrame(guard("frame", frame));
}

function frame() {
  st.raf = 0;
  var busy = false;

  if (st.mode === "cursor" && (st.vx || st.vy)) {
    st.cx = Math.max(2, Math.min(window.innerWidth - 2, st.cx + st.vx * CURSOR_SPEED));
    st.cy = Math.max(2, Math.min(window.innerHeight - 2, st.cy + st.vy * CURSOR_SPEED));
    paint();
    cursorHover();
    /* At the edges the cursor pushes the page instead of stopping dead. */
    if (st.cy <= 4 && st.vy < 0) window.scrollBy(0, -SCROLL_SPEED);
    if (st.cy >= window.innerHeight - 4 && st.vy > 0) window.scrollBy(0, SCROLL_SPEED);
    busy = true;
  }

  if (st.sx || st.sy) {
    var box = scroller(st.el) || document.scrollingElement || document.documentElement;
    try { box.scrollBy(st.sx * SCROLL_SPEED, st.sy * SCROLL_SPEED); } catch (e) {}
    if (st.mode === "spatial" && st.el) { st.rect = rectOf(st.el); paint(); }
    busy = true;
  }

  if (busy) pump();
}

/* A stick byte arrives as -1..1 already normalised by the host. Square the
   magnitude: it keeps small deflections genuinely slow, which is what makes
   a thumbstick usable as a pointer at all. */
function curve(v) {
  var dead = 0.16;
  var m = Math.abs(v);
  if (m < dead) return 0;
  var t = (m - dead) / (1 - dead);
  return (v < 0 ? -1 : 1) * t * t;
}

/* ═══ Actions ══════════════════════════════════════════════════════════ */

function action(name, phase) {
  if (phase === "release") return "";
  var v = media();
  var inVideo = !!v && (st.mode === "spatial") &&
                (document.fullscreenElement || (st.el && st.el.tagName === "VIDEO"));

  switch (name) {
    case "up":    return inVideo ? volume(v, +0.1) : (st.mode === "cursor" ? cursorStep(0, -1) : move(0, -1));
    case "down":  return inVideo ? volume(v, -0.1) : (st.mode === "cursor" ? cursorStep(0, 1)  : move(0, 1));
    case "left":  return inVideo ? seek(v, -10)    : (st.mode === "cursor" ? cursorStep(-1, 0) : move(-1, 0));
    case "right": return inVideo ? seek(v, +10)    : (st.mode === "cursor" ? cursorStep(1, 0)  : move(1, 0));

    case "cross": case "launch": case "select": case "activate":
      return st.mode === "cursor" ? cursorClick() : activate();

    case "square":        return fullscreen(v);

    /* Triangle is the way back to a keyboard that was dismissed. The field
       is still focused and still holds what it held; this reopens the
       keyboard on it rather than making the human find the field again.
       Documented in the shell's hint bar as "Keyboard" whenever the focused
       thing is typeable. Falls through to its old meanings otherwise. */
    case "triangle":
      if (v) return togglePlay(v);
      {
        var f = editableFocus();
        if (f) { try { f.focus({ preventScroll: true }); } catch (e) {} return requestEdit(f, "reopen"); }
      }
      return st.el ? activate() : "nothing focused";
    case "l1": case "tabPlay":  return scrollBy(0, -1, Math.round(window.innerHeight * 0.85)) ? "page up" : "top";
    case "r1": case "tabMedia": return scrollBy(0,  1, Math.round(window.innerHeight * 0.85)) ? "page down" : "bottom";
    case "l3":            return st.mode === "cursor" ? toSpatial() : toCursor();
    case "r3":            return st.el ? (ensureVisible(st.el) ? "recentred" : "already in view") : "nothing focused";
    case "rescan":        scan(); reFocus(); return "rescanned: " + st.items.length + " targets";
  }
  return "";
}

function volume(v, d) {
  try { v.volume = Math.max(0, Math.min(1, (v.volume === undefined ? 1 : v.volume) + d)); } catch (e) {}
  reportMedia(v);
  return "volume " + Math.round((v.volume || 0) * 100) + "%";
}

/* ═══ Host channel ═════════════════════════════════════════════════════ */

var onHost = guard("host message", function (ev) {
  var d = ev ? ev.data : null;
  if (typeof d !== "string") { try { d = JSON.stringify(d); } catch (e) { return; } }
  var m; try { m = JSON.parse(d); } catch (e) { return; }
  if (!m || !m.t) return;

  switch (m.t) {
    case "act": {
      if (!ensureOverlay()) return;
      var r = action(m.action, m.phase);
      if (r) say(m.action + " -> " + r);
      return;
    }
    case "axes": {
      if (!ensureOverlay()) return;
      if (st.mode === "cursor") { st.vx = curve(m.lx || 0); st.vy = curve(m.ly || 0); }
      else { st.vx = st.vy = 0; }
      st.sx = curve(m.rx || 0) * 0.6;
      st.sy = curve(m.ry || 0);
      if (st.vx || st.vy || st.sx || st.sy) pump();
      return;
    }
    case "mode":
      if (!ensureOverlay()) return;
      if (m.mode === "toggle") say(st.mode === "cursor" ? toSpatial() : toCursor());
      else if (m.mode === "cursor") say(toCursor());
      else say(toSpatial());
      return;
    case "text":   say(writeText(String(m.value === undefined ? "" : m.value))); return;
    case "option": say(chooseOption(m.index | 0)); return;
    case "cancel": {
      /* The field keeps exactly what it had - nothing is written on a
         cancel, not even an empty string - and the focus goes back to it so
         Triangle reopens the keyboard on the same field. Suppressed, or
         restoring the focus would reopen it immediately and the keyboard
         could not be dismissed at all. */
      var was = st.pending;
      st.pending = null;
      if (was) {
        suppress(was);
        try { was.focus({ preventScroll: true }); } catch (e) {}
        ensureVisible(was, 0.34);
      }
      reFocus();
      return;
    }
    case "zoom":   scan(); reFocus(); return;
    case "scan":   scan(); reFocus(); say("rescanned: " + st.items.length + " targets"); return;
  }
});

try { window.chrome.webview.addEventListener("message", onHost); }
catch (e) { /* not under the content WebView; nothing to do */ }

/* ═══ Page lifecycle ═══════════════════════════════════════════════════ */

function announce() {
  if (!ensureOverlay()) { setTimeout(guard("announce", announce), 120); return; }
  scan();
  if (st.items.length) focusItem(st.items[0]);
  else post({ type: "mosnav", ev: "nofocus" });
  post({ type: "mosnav", ev: "ready", url: location.href, title: document.title, items: st.items.length });
}

if (document.readyState === "loading")
  document.addEventListener("DOMContentLoaded", guard("domready", announce));
else
  announce();

/* Single-page applications never fire another load event. Re-measure when
   the DOM settles after a burst of mutations, and after any history move. */
var settle = 0;
try {
  var mo = new MutationObserver(function () {
    clearTimeout(settle);
    settle = setTimeout(guard("mutation settle", function () {
      if (!st.el || !rectOf(st.el)) { scan(); if (st.items.length && !st.el) focusItem(st.items[0]); else reFocus(); }
      else scan();
    }), 400);
  });
  mo.observe(document.documentElement, { childList: true, subtree: true });
} catch (e) {}

window.addEventListener("popstate", guard("popstate", function () { setTimeout(announce, 200); }));
window.addEventListener("resize", guard("resize", function () { scan(); reFocus(); }));
document.addEventListener("fullscreenchange", guard("fs", function () {
  var v = media(); if (v) reportMedia(v);
  setTimeout(guard("fs rescan", function () { scan(); reFocus(); }), 250);
}));

/* Anything the page plays should be reported, so the shell's chrome can show
   a transport even when the video is not the focused element. */
document.addEventListener("play", guard("play", function (e) {
  if (e.target && e.target.tagName === "VIDEO") reportMedia(e.target);
}), true);
document.addEventListener("pause", guard("pause", function (e) {
  if (e.target && e.target.tagName === "VIDEO") reportMedia(e.target);
}), true);

/* ═══ The keyboard follows the page's own focus ════════════════════════
   The half of the keyboard story the human does not drive.

   A login form is the case that matters and it cannot be done with Cross
   alone: the human presses Cross on the username field, types, commits -
   and then the PAGE moves focus to the password field, because that is what
   a login form does when you press Enter or Next. Nobody pressed anything
   on the password field, so nothing would ask for a keyboard, and the human
   would be left looking at a focused password box with no way to type into
   it. Same for the very common case of a page autofocusing its first field
   on load.

   So: focus arriving in a typeable field raises the keyboard, EXCEPT when
   we put it there ourselves. focusItem() suppresses around its own focus()
   call and writeText() around its write-back, which is what stops the ring
   passing over a search box from being a trap and stops a commit from
   reopening the keyboard it just closed.

   focusin, not focus: focus does not bubble, and the listener has to be one
   listener on the document rather than one per field on a page that creates
   fields as you go. */

function editableFocus() {
  var a = null;
  try { a = document.activeElement; } catch (e) {}
  /* activeElement stops at a shadow host, exactly as elementFromPoint does. */
  var hops = 0;
  while (a && a.shadowRoot && a.shadowRoot.activeElement && hops++ < 20) a = a.shadowRoot.activeElement;
  if (isEditable(a)) return a;
  if (isEditable(st.el)) return st.el;
  try {
    if (st.lastField && st.lastField.isConnected && isEditable(st.lastField)) return st.lastField;
  } catch (e) {}
  return null;
}

document.addEventListener("focusin", guard("focusin", function (e) {
  var el = e && e.target;
  if (!isEditable(el)) return;
  if (suppressed(el)) return;
  /* Already collecting an answer for this very field; a second ask would
     race the first one's reply. */
  if (st.pending === el) return;

  /* Keep the ring with the keyboard, so that dismissing it leaves the
     focus where the human expects and Triangle reopens the right field. */
  st.el = el;
  reFocus();
  say(requestEdit(el, "page focus"));
}), true);

window.__mosnav = {
  version: VERSION,

  /* The same entry point as the {t:"act"} message, exposed so the host can
     reach it with ExecuteScriptAsync instead.
     WHY BOTH EXIST: a click synthesised from a message handler carries no
     user activation, and Chromium withholds three things without one -
     requestFullscreen(), autoplay, and a back-forward history entry that is
     not marked skippable. Losing the third is what makes Circle unable to go
     back on a page the human navigated to by pressing Cross. Script run
     through ExecuteScriptAsync may be granted activation where a message
     handler is not, so discrete actions take this route and the 30 Hz stick
     stream stays on the cheap one. */
  act: function (name, phase) {
    try {
      if (!ensureOverlay()) return "no overlay";
      var r = action(name, phase || "press");
      if (r) say(name + " -> " + r);
      return r;
    } catch (err) { say("act(" + name + ") threw: " + err); return "threw"; }
  },
  debug: function () {
    return { mode: st.mode, items: st.items.length, focused: st.el ? describe(st.el) : null,
             cursor: [Math.round(st.cx), Math.round(st.cy)], url: location.href };
  }
};

})();
