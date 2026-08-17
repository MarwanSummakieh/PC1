#!/usr/bin/env node
// patch-index-applifecycle.mjs
// ─────────────────────────────────────────────────────────────────────────────
// The page half of app lifecycle control, applied to index.html by anchor.
//
// index.html is owned by another agent and is edited concurrently, so this is a
// script rather than a diff: every edit is located by a unique anchor string, not
// by a line number, and every edit is idempotent — running it twice changes
// nothing the second time. Nothing is written unless EVERY edit either applies
// cleanly or is already present.
//
//   node patch-index-applifecycle.mjs [path/to/index.html] [--check] [--verbose]
//
//   --check    report what would happen and write nothing (exit 0 = clean,
//              exit 3 = an anchor is missing)
//
// Exit codes: 0 applied or already applied · 2 file/usage error · 3 anchor missing.
//
// The six edits, and why each exists:
//
//   1  CSS          .tile-live / .tile.running — the "this is open" pill on a rail
//                   tile and the dimming that goes with it.
//   2  ICONS        play / shell / stop, the guide menu's three row icons.
//   3  ROUTER       the four host->page messages the host now sends: apps,
//                   guide{ev:menu}, shellforward, app{ev:resume|minimise|close}.
//   4  RAIL         re-apply the running marks after buildRail throws the old
//                   tiles away.
//   5  MODULE       the Apps store (running + background, pushed by the host,
//                   never polled), applyRunningMarks, the guide menu panel, and
//                   ACT.appResume / appMinimise / appClose.
//   6  HERO         paintHero asks isRunningId(app.id) for its "Resume" label, but
//                   the helper did not exist. It threw inside safe(), so the hero
//                   stopped painting mid-write and focus fell back to the hint bar:
//                   the home rail became unreachable and NOTHING could be launched.
//                   Bench-observed on 2026-08-16 against v10:
//                     [PAGE] ERROR paintHero: isRunningId is not defined
//                     [PAGE] pad right -> no target to the right
//                   The definition has to be a hoisted function with a try/catch,
//                   not a reference to Apps: buildRail runs during boot, before the
//                   const is initialised, and a TDZ read is only survivable in a
//                   catch. (index.html gained exactly this definition from its own
//                   owner at 13:01 on 2026-08-16; this edit now reports as already
//                   applied there and exists for any copy that predates it.)
// ─────────────────────────────────────────────────────────────────────────────

import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const argv = process.argv.slice(2);
const CHECK = argv.includes("--check");
const VERBOSE = argv.includes("--verbose");
const positional = argv.filter((a) => !a.startsWith("--"));

const here = path.dirname(fileURLToPath(import.meta.url));
const target = path.resolve(positional[0] || path.join(here, "..", "..", "index.html"));

if (!fs.existsSync(target)) {
  console.error("index.html not found: " + target);
  process.exit(2);
}

let text = fs.readFileSync(target, "utf8");
const before = text;
const eol = text.includes("\r\n") ? "\r\n" : "\n";
// Work in \n internally; restore the file's own line ending on write.
text = text.replace(/\r\n/g, "\n");

const report = [];
let missing = 0;

/** Insert `block` immediately BEFORE the first occurrence of `anchor`. */
function insertBefore(name, present, anchor, block) {
  if (text.includes(present)) { report.push([name, "already applied", ""]); return; }
  const at = text.indexOf(anchor);
  if (at < 0) { report.push([name, "ANCHOR MISSING", anchor.split("\n")[0].trim()]); missing++; return; }
  if (text.indexOf(anchor, at + 1) >= 0) {
    report.push([name, "ANCHOR NOT UNIQUE", anchor.split("\n")[0].trim()]); missing++; return;
  }
  text = text.slice(0, at) + block + text.slice(at);
  report.push([name, "applied", block.split("\n").length - 1 + " lines inserted"]);
}

/** Insert `block` immediately AFTER the first occurrence of `anchor`. */
function insertAfter(name, present, anchor, block) {
  if (text.includes(present)) { report.push([name, "already applied", ""]); return; }
  const at = text.indexOf(anchor);
  if (at < 0) { report.push([name, "ANCHOR MISSING", anchor.split("\n")[0].trim()]); missing++; return; }
  if (text.indexOf(anchor, at + 1) >= 0) {
    report.push([name, "ANCHOR NOT UNIQUE", anchor.split("\n")[0].trim()]); missing++; return;
  }
  const end = at + anchor.length;
  text = text.slice(0, end) + block + text.slice(end);
  report.push([name, "applied", block.split("\n").length - 1 + " lines inserted"]);
}

/** Replace `from` with `to` exactly once. */
function replaceOnce(name, from, to) {
  if (text.includes(to)) { report.push([name, "already applied", ""]); return; }
  const at = text.indexOf(from);
  if (at < 0) { report.push([name, "ANCHOR MISSING", from.trim()]); missing++; return; }
  if (text.indexOf(from, at + 1) >= 0) { report.push([name, "ANCHOR NOT UNIQUE", from.trim()]); missing++; return; }
  text = text.slice(0, at) + to + text.slice(at + from.length);
  report.push([name, "applied", "1 expression replaced"]);
}

// ── 1. The running pill on a rail tile ───────────────────────────────────────
insertBefore(
  "1 CSS       .tile-live",
  ".tile.running .tile-dim",
  "/* ── Hint bar ",
  `/* "This is open right now." Bottom-left so it never collides with the
   count badge top-right, and a live dot rather than a colour alone so
   it survives a colour-blind reading and a photograph of the screen. */
.tile-live{
  position:absolute;left:.5rem;bottom:.5rem;
  display:inline-flex;align-items:center;gap:.3rem;
  padding:.1rem .4rem .1rem .3rem;border-radius:var(--r-s);
  background:rgba(0,0,0,.62);color:#fff;
  font-size:.62rem;font-weight:700;letter-spacing:.06em;text-transform:uppercase;
  box-shadow:inset 0 0 0 .0625rem rgba(255,255,255,.22);
}
.tile-live::before{
  content:"";width:.4rem;height:.4rem;border-radius:50%;
  background:var(--good,#54d18c);
  box-shadow:0 0 .35rem var(--good,#54d18c);
}
@media (prefers-reduced-motion:no-preference){
  .tile-live::before{animation:tileLivePulse 2.4s ease-in-out infinite}
}
@keyframes tileLivePulse{0%,100%{opacity:1}50%{opacity:.45}}
.tile.running .tile-dim{opacity:.15}
`
);

// ── 2. The guide menu's row icons ────────────────────────────────────────────
insertBefore(
  "2 ICONS     play/shell/stop",
  "  stop:   '<rect x=\"6.4\"",
  "  rest:   '<path d=\"M20 14.5A8.2",
  `  /* the guide menu's three: go back into it, come back out of it, end it */
  play:   '<path d="M8.2 5.4v13.2L19 12z"/>',
  shell:  '<path d="M3.6 11.2 12 4.2l8.4 7"/><path d="M6 10.4v8.2a1 1 0 0 0 1 1h10a1 1 0 0 0 1-1v-8.2"/>',
  stop:   '<rect x="6.4" y="6.4" width="11.2" height="11.2" rx="1.6"/>',
`
);

// ── 3. The host->page lifecycle messages ─────────────────────────────────────
insertBefore(
  "3 ROUTER    apps/guide/app",
  'if (m.type === "apps"){ Apps.handle(m); return; }',
  '    if (m.type === "browser"){',
  `    /* ── App lifecycle ─────────────────────────────────────
       The host owns process state and pushes it here. Three
       messages, all host->page:
         apps          what is running, foreground and background
         guide{ev:menu} a short press of PS with something open
         shellforward   the shell was pulled in front of an app
         app{ev:...}    the result of resume / minimise / close */
    if (m.type === "apps"){ Apps.handle(m); return; }
    if (m.type === "guide"){
      if (m.ev === "menu") openGuideMenu(m.app);
      return;
    }
    if (m.type === "shellforward"){
      hostLog("host brought the shell forward (" + (m.reason || "?") + ")" +
              (m.app ? " over " + m.app : ""));
      return;
    }
    if (m.type === "app"){
      if (m.ev === "resume" && m.ok === false) toast("Could not bring " + (m.title || "it") + " back.");
      if (m.ev === "close"){
        toast(m.ok ? (m.title || "It") + " was closed." : "Nothing was left to close.");
      }
      return;
    }
`
);

// ── 4. Re-apply the marks after a rail rebuild ───────────────────────────────
insertAfter(
  "4 RAIL      re-mark on rebuild",
  'typeof applyRunningMarks === "function"',
  "    rail.appendChild(t);\n  });\n",
  "  /* A rebuild throws the running marks away with the old tiles, so put\n" +
  "     them back before anything is painted. Guarded because buildRail runs\n" +
  "     during boot, before Apps exists. */\n" +
  '  try{ if (typeof applyRunningMarks === "function") applyRunningMarks(); }catch(e){}\n'
);

// ── 5. The Apps store and the guide menu ─────────────────────────────────────
insertBefore(
  "5 MODULE    Apps + guide menu",
  "const Apps = (function(){",
  "/* ═══ Semantic action router ",
  `/* ═══ What is running ════════════════════════════════════
   The host publishes; this subscribes. It is never polled — the
   page cannot see the process table and any interval it invented
   here would either lag or burn a scan the host has already done.

   Message shape (host -> page), pushed on every change:

     {type:"apps",
      running:[{id,title,kind,pid,tracked,foreground,pids}],
      background:[{name,category,pid,procs,actionable}]}

   \`running\` is zero or one entry: the app the shell has yielded to
   and can Resume, Minimise or Close. \`background\` is everything
   worth naming that is up, whether the shell started it or not —
   Steam, Discord, Vanguard — each with a friendly name, a category
   (game | launcher | social | media | capture | service | anticheat)
   and \`actionable\`, which is false for the things a shell must not
   offer to kill.

   This module is the data and nothing else. paintRunning() below
   turns Apps.background() into the status bar's chips, and draws
   nothing at all until a message has actually arrived. */
const Apps = (function(){
  let running = null;
  let background = [];
  const listeners = [];

  function emit(){
    listeners.forEach(fn => { try{ fn(running, background); }catch(err){ report("apps listener", err); } });
  }

  return {
    handle(m){
      const was = running && running.id;
      running = (m.running && m.running.length) ? m.running[0] : null;
      background = m.background || [];
      if ((running && running.id) !== was)
        hostLog("apps: running = " + (running ? running.title + " (pid " + running.pid +
                (running.tracked ? ", tracked" : ", untracked") + ")" : "nothing"));
      applyRunningMarks();
      try{ paintRunning(); }catch(err){ report("running status", err); }
      emit();
    },
    /* True for the library id of the app that is open right now. The rail
       and the hero both ask this; nothing else should need to. */
    isRunning(id){ return !!(running && id && running.id === id); },
    current(){ return running; },
    background(){ return background.slice(); },
    onChange(fn){ listeners.push(fn); try{ fn(running, background); }catch(e){} },
    ask(){ Host.post({ type:"app", cmd:"state" }); }
  };
})();

/* The rail and the hero both ask "is this one open?" — from buildRail(),
   which runs during boot, and from MarwanLibrary.onUpdate, which can fire
   at any moment. Naming the const directly from either of those is a
   temporal-dead-zone ReferenceError, and paintHero()'s safe() wrapper
   swallowed it AFTER writing the title and BEFORE writing the tags, the
   fact table and the class that fades the block back in: the hero
   rendered half-drawn, permanently, and nothing on screen said so.
   The try/catch is the point — it is the only construct that survives a
   TDZ read. */
function isRunningId(id){
  try{ return Apps.isRunning(id); }
  catch(e){ return false; }
}

/* ── What is running, in the status bar ───────────────────
   Rendered from Apps.background() and from nothing else. The host is
   the only thing in this system that can see the process table; if it
   has not spoken, this draws NOTHING — no skeleton, no example, no
   "Steam · Discord" placeholder that a photograph of the screen would
   turn into a lie. An empty status bar is the honest state of a shell
   that has not been told.

   Chips are focusables in row 0 of the home scope, where the gear and
   the power glyph used to be, so pressing up from the tabs still lands
   on the top right. */
const RUN_MAX = 3;          // beyond this, one "+N" chip that opens the list

function paintRunning(){
  const wrap = $("#running"), list = $("#runningList");
  if (!wrap || !list) return;
  const bg = Apps.background();

  list.innerHTML = "";
  if (!bg.length){
    /* Nothing running, or nothing said. Either way there is nothing to
       show, and a "Nothing running" label on a television is a row of
       text that never changes. */
    wrap.hidden = true;
    refreshHomeItems();
    return;
  }
  wrap.hidden = false;

  const shown = bg.slice(0, RUN_MAX);
  shown.forEach((b, i) => list.appendChild(runChip(b, i)));
  if (bg.length > shown.length){
    const more = document.createElement("button");
    more.className = "runchip more";
    more.setAttribute("data-nav", "0," + shown.length);
    more.dataset.act = "run";
    more.dataset.label = "running:more";
    more.innerHTML = "<b></b>";
    more.querySelector("b").textContent = "+" + (bg.length - shown.length) + " more";
    more.setAttribute("aria-label", (bg.length - shown.length) + " more running");
    more.__mosRun = () => runningListPanel();
    wireRunChip(more);
    list.appendChild(more);
  }
  refreshHomeItems();
}

function runChip(b, i){
  const el = document.createElement("button");
  el.className = "runchip k-" + (b.category || "service");
  el.setAttribute("data-nav", "0," + i);
  el.dataset.act = "run";
  el.dataset.label = "running:" + (b.name || "?");
  el.setAttribute("aria-label", (b.name || "Unknown") + " — " + runCategoryWord(b.category));
  el.innerHTML = "<i></i><b></b>";
  el.querySelector("b").textContent = b.name || "Unknown";
  el.__mosRun = () => runningPanel(b);
  wireRunChip(el);
  return el;
}

function wireRunChip(el){
  el.addEventListener("click", safe("runchip click", (ev) => {
    if (ev.detail === 0) return;
    Input.set("key"); Focus.focusEl(el); Focus.activate();
  }));
}

/* The host's category vocabulary, spelled for a human. Unknown values
   are passed through rather than mapped to "Other": a category this
   page has not heard of is the host telling us something new, and
   swallowing it would hide it. */
const RUN_CATEGORY = {
  game:"Game", launcher:"Launcher", social:"Social", media:"Media",
  capture:"Capture", service:"Service", anticheat:"Anti-cheat"
};
function runCategoryWord(c){ return RUN_CATEGORY[c] || (c || "Running"); }

/* One background app, named and explained. What it can DO with it is
   decided by what the host can actually carry out: resume and close
   exist for the one app the shell has yielded to and is tracking, and
   for nothing else — there is no close-this-pid command, and a Close
   that quietly terminated a different process tree would be far worse
   than no Close at all. */
function runningPanel(b){
  const cur = Apps.current();
  const isTracked = !!(cur && (cur.pid === b.pid || (cur.title || "") === (b.name || "")));
  const node = panelShell(b.name || "Running",
    isTracked ? "This is the app the shell is in front of."
              : "Running behind the shell. The host reported it; the shell did not start it.",
    "Running");
  const body = node.querySelector(".panel-body");

  body.appendChild(factsEl([
    ["Kind",       runCategoryWord(b.category)],
    ["Process id", b.pid ? String(b.pid) : "—"],
    ["Processes",  typeof b.procs === "number" ? String(b.procs) : "—"],
    ["Foreground", isTracked ? (cur && cur.foreground ? "Yes" : "No — the shell is in front") : "No"]
  ]));

  let n = 0;
  if (isTracked){
    [
      { label:"Resume",   sub:"Go back to " + (b.name || "it"),                 icon:"play",  act:"appResume" },
      { label:"Minimise", sub:"Stay here. " + (b.name || "It") + " keeps running.", icon:"shell", act:"appMinimise" },
      { label:"Close",    sub:"Quit it and everything it started",              icon:"stop",  act:"appClose", tone:"danger" }
    ].forEach(o => body.appendChild(rowEl(o, n++, 0)));
  }
  body.appendChild(actionRow({
    label:"Refresh", sub:"Ask the host what is running right now", icon:"update",
    run: () => { Apps.ask(); toast("Asked the host for the running list."); }
  }, n++, 0));

  if (!isTracked){
    body.appendChild(noteEl(b.actionable
      ? "The shell can only resume or close the app it launched and is tracking. " +
        "<b>" + escHTML(b.name || "This") + " came up on its own</b>, so there is nothing here that " +
        "could close it without the host growing a close-by-process-id command."
      : "<b>" + escHTML(b.name || "This") + " cannot be closed from a menu.</b> The host reports it as " +
        "not actionable — an anti-cheat driver or a Windows service is not something a living-room " +
        "shell gets to terminate."));
  }
  return pushPanel("running", node, { hints:["navUD", "select", "back"] });
}

/* Everything, when there is more than the status bar has room for. */
function runningListPanel(){
  const bg = Apps.background();
  pickerPanel({
    id:"runningAll", eyebrow:"Running", title:"Running in the background",
    sub:"Everything the host can see that is worth naming. It is a curated list, not the process table.",
    items: bg.map(b => ({
      bg: b, label: b.name || "Unknown",
      sub: [runCategoryWord(b.category), b.pid ? "pid " + b.pid : null,
            typeof b.procs === "number" ? b.procs + (b.procs === 1 ? " process" : " processes") : null]
           .filter(Boolean).join(" · "),
      icon: b.category === "game" ? "play" : (b.category === "anticheat" ? "info" : "shell")
    })),
    emptyText:"Nothing is running.",
    onPick: (it) => { if (it.bg) runningPanel(it.bg); }
  });
}

/* Paint the running state onto the rail and the hero. Cheap and
   idempotent: called on every apps message and after every rail
   rebuild, because a rebuild throws the marks away with the tiles. */
function applyRunningMarks(){
  const list = APPS[tab] || [];
  [...$("#rail").children].forEach((t, n) => {
    const app = list[n];
    const on = !!(app && Apps.isRunning(app.id));
    t.classList.toggle("running", on);
    let pill = t.querySelector(".tile-live");
    if (on && !pill){
      pill = document.createElement("span");
      pill.className = "tile-live";
      pill.textContent = "Running";
      t.appendChild(pill);
    } else if (!on && pill){
      pill.remove();
    }
  });
  const app = list[idx];
  if (app){
    const on = Apps.isRunning(app.id);
    $("#launchLabel").textContent = on ? "Resume" : (app.action || "Launch");
    $("#btnLaunch").dataset.label = on ? "resume" : "launch";
  }
}

/* ═══ The guide menu ═════════════════════════════════════
   What a short press of PS opens when something is running. Three
   rows and no cleverness: the thing you almost always want (Resume)
   is focused, the thing that loses work (Close) is last and behind a
   confirmation. It is an ordinary panel on the existing focus stack,
   so Circle backs out of it exactly like every other panel and the
   pad hints are the ones the human already knows. */
function openGuideMenu(app){
  const a = app || Apps.current();
  if (!a){
    /* The host only sends this with something running, so arriving
       here means the app exited between the press and the message.
       Say so rather than opening an empty menu. */
    toast("Nothing is running.");
    return;
  }
  Overlay.closeAll();
  const node = panelShell(a.title, a.tracked
    ? "This app is open. The shell is in front of it."
    : "This app is open, but the shell could not attach to it — it will not know when the app closes.",
    "Guide");
  const body = node.querySelector(".panel-body");
  [
    { label:"Resume",   sub:"Go back to " + a.title, icon:"play",  act:"appResume" },
    { label:"Minimise", sub:"Stay here. " + a.title + " keeps running.", icon:"shell", act:"appMinimise" },
    { label:"Close",    sub:"Quit " + a.title + " and everything it started", icon:"stop",
      act:"appClose", tone:"danger" }
  ].forEach((o, i) => body.appendChild(rowEl(o, i, 0)));
  pushPanel("guide", node, { hints:["navUD", "select", "back"] });
  hostLog("guide menu opened for " + a.title);
}

ACT.appResume = function(){
  const a = Apps.current();
  Overlay.closeAll();
  hostLog("guide: resume " + (a ? a.title : "(nothing)"));
  Host.post({ type:"app", cmd:"resume" });
};

ACT.appMinimise = function(){
  Overlay.closeAll();
  hostLog("guide: minimise — staying in the shell");
  Host.post({ type:"app", cmd:"minimise" });
};

/* Close terminates a process tree. A game does not get to save first,
   so this is the one guide action that asks. Cancel is the default. */
ACT.appClose = function(){
  const a = Apps.current();
  const name = a ? a.title : "this app";
  confirmAction({
    id:"appClose",
    eyebrow:"Close",
    title:"Close " + name + "?",
    sub:"It will be shut down immediately, together with everything it started. " +
        "Anything unsaved is lost.",
    goLabel:"Close it",
    cancelLabel:"Keep it open",
    onConfirm(){
      Overlay.closeAll();
      hostLog("guide: close " + name + " (confirmed)");
      Host.post({ type:"app", cmd:"close" });
    }
  });
};

`
);

// ── 6. paintHero's undefined isRunningId() ───────────────────────────────────
insertBefore(
  "6 HERO      isRunningId()",
  "function isRunningId(id){",
  "function applyRunningMarks(){",
  `/* The rail and the hero both ask "is this one open?" — from buildRail(),
   which runs during boot, and from MarwanLibrary.onUpdate, which can fire
   at any moment. Naming the const directly from either of those is a
   temporal-dead-zone ReferenceError, and paintHero()'s safe() wrapper
   swallowed it AFTER writing the title and BEFORE writing the tags, the
   fact table and the class that fades the block back in: the hero
   rendered half-drawn, permanently, and nothing on screen said so.
   The try/catch is the point — it is the only construct that survives a
   TDZ read. */
function isRunningId(id){
  try{ return Apps.isRunning(id); }
  catch(e){ return false; }
}

`
);

// ─────────────────────────────────────────────────────────────────────────────
const width = Math.max(...report.map((r) => r[0].length));
for (const [name, status, detail] of report) {
  console.log("  " + name.padEnd(width) + "  " + status.padEnd(18) + (VERBOSE ? detail : detail && status !== "applied" ? detail : ""));
}

if (missing) {
  console.error("\n" + missing + " anchor(s) could not be located in " + target + ". Nothing was written.");
  console.error("index.html has moved underneath this script; re-derive the anchors before retrying.");
  process.exit(3);
}

const changed = report.some((r) => r[1] === "applied");
if (!changed) { console.log("\nNothing to do: " + path.basename(target) + " already carries all six edits."); process.exit(0); }
if (CHECK) { console.log("\n--check: " + report.filter((r) => r[1] === "applied").length + " edit(s) would be applied. Nothing written."); process.exit(0); }

const out = eol === "\r\n" ? text.replace(/\n/g, "\r\n") : text;
const backup = target + ".pre-applifecycle";
if (!fs.existsSync(backup)) fs.writeFileSync(backup, before);
fs.writeFileSync(target, out);
console.log("\nWrote " + target + " (" + before.length + " -> " + out.length + " bytes); previous copy at " + path.basename(backup) + ".");
