/* ═══════════════════════════════════════════════════════════════════════
   MarwanOS — on-screen keyboard: grid navigation test  (ui/osk-navtest.js)

   Grid navigation is where on-screen keyboards fail, and the failures are
   very visible: a cursor that sticks in a corner, a "down" that lands two
   columns over because the row below has wider keys, a row you can only
   leave by going the long way round. This walks the pure model exported as
   MarwanOSK._model and asserts every one of those away.

   Runs in two places, from the same file:

     node ui/osk-navtest.js          headless, exit code 0/1
     <script src="osk-navtest.js">   in the harness page, then
                                     MarwanOSKNavTest.run() -> { pass, fail, lines }

   The browser path is what proves it inside WebView2 on the bench, where the
   result is written into document.title and posted to the host log.
   ═══════════════════════════════════════════════════════════════════════ */

(function (root) {
"use strict";

function getAPI() {
  if (root.MarwanOSK) return root.MarwanOSK;
  if (typeof require === "function") return require("./osk.js");
  throw new Error("MarwanOSK is not loaded");
}

var DIRS = ["up", "down", "left", "right"];

function run() {
  var API = getAPI();
  var M = API._model;
  var lines = [], pass = 0, fail = 0;

  function ok(cond, what) {
    if (cond) { pass++; return true; }
    fail++;
    lines.push("  FAIL  " + what);
    return false;
  }
  function note(s) { lines.push(s); }

  var layerNames = [];
  for (var n in M.LAYOUTS) if (Object.prototype.hasOwnProperty.call(M.LAYOUTS, n)) layerNames.push(n);

  var cases = [];
  for (var a = 0; a < layerNames.length; a++) {
    cases.push({ layer: layerNames[a], reveal: false });
    cases.push({ layer: layerNames[a], reveal: true });
  }

  for (var ci = 0; ci < cases.length; ci++) {
    var c = cases[ci];
    var grid = M.build(c.layer, { reveal: c.reveal });
    var tag = c.layer + (c.reveal ? " +reveal" : "");
    var cells = 0, r, i, k;

    /* rows that actually contain something focusable */
    var liveRows = [];
    for (r = 0; r < grid.rows.length; r++) if (grid.live[r].length) liveRows.push(r);
    var topRow = liveRows[0], bottomRow = liveRows[liveRows.length - 1];

    /* ── 1. geometry: declared units and rendered units must agree ─────
       If a row's widths do not sum to the layer width, the CSS grid and the
       nav model disagree and "down" lands somewhere that is not below. */
    for (r = 0; r < grid.rows.length; r++) {
      var sum = 0;
      for (i = 0; i < grid.rows[r].length; i++) sum += grid.rows[r][i].w;
      ok(sum === grid.cols, tag + " row " + r + " sums to " + sum + ", expected " + grid.cols);
      /* the column spans must tile the row exactly: no gap, no overlap, and
         integer throughout — a fractional column offset means the model and
         the CSS grid have drifted apart */
      var x = 0;
      for (i = 0; i < grid.rows[r].length; i++) {
        var cell0 = grid.rows[r][i];
        ok(cell0.c0 === x, tag + " row " + r + " cell " + i + " starts at " + cell0.c0 + ", expected " + x);
        ok(cell0.c1 === x + cell0.w, tag + " row " + r + " cell " + i + " span is not its width");
        ok(cell0.c0 === Math.floor(cell0.c0) && cell0.c1 === Math.floor(cell0.c1),
           tag + " row " + r + " cell " + i + " has a fractional column offset");
        ok(cell0.tot === sum, tag + " row " + r + " cell " + i + " carries the wrong row total");
        x = cell0.c1;
      }
      ok(x === grid.cols, tag + " row " + r + " ends at column " + x + ", expected " + grid.cols);
    }

    /* ── 2. every move from every key is valid and never lands on a hole ── */
    for (var li = 0; li < liveRows.length; li++) {
      r = liveRows[li];
      for (k = 0; k < grid.live[r].length; k++) {
        i = grid.live[r][k];
        cells++;
        var src = grid.rows[r][i];
        var from = M.posFor(grid, r, i);

        for (var d = 0; d < DIRS.length; d++) {
          var dir = DIRS[d];
          var to = M.step(grid, from, dir);
          var cell = grid.rows[to.r] && grid.rows[to.r][to.i];

          if (!ok(!!cell, tag + " " + dir + " from (" + r + "," + i + ") left the grid")) continue;
          ok(!cell.spacer, tag + " " + dir + " from (" + r + "," + i + ") landed on a spacer");

          if (dir === "left" || dir === "right") {
            ok(to.r === r, tag + " " + dir + " from (" + r + "," + i + ") changed row");
            /* only a one-key row may stand still horizontally */
            ok(to.i !== i || grid.live[r].length === 1,
               tag + " " + dir + " from (" + r + "," + i + ") did not move");
          } else {
            var atEdge = (dir === "up" && r === topRow) || (dir === "down" && r === bottomRow);
            if (atEdge) {
              /* no vertical wrap: the cursor must stay exactly where it was */
              ok(to.r === r && to.i === i,
                 tag + " " + dir + " at the " + (dir === "up" ? "top" : "bottom") +
                 " edge from (" + r + "," + i + ") moved to (" + to.r + "," + to.i + ")");
            } else {
              ok(dir === "up" ? to.r < r : to.r > r,
                 tag + " " + dir + " from (" + r + "," + i + ") went to row " + to.r);
              /* Visually below/above. Checked against an independent reference:
                 if any key in the landing row spans the source key's centre,
                 that is the only acceptable answer, whatever the two rows' key
                 widths are; if the row has a hole there (the field row, which
                 holds only the Show toggle), the nearest centre is. */
              var want = expectTarget(grid, to.r, src.c0 + src.c1, src.tot);
              ok(to.i === want.i,
                 tag + " " + dir + " from (" + r + "," + i + ") centre column " +
                 ((src.c0 + src.c1) / 2) + " landed on cell " + to.i + " [" + cell.c0 + "," + cell.c1 +
                 ") but the " + want.why + " is cell " + want.i);
            }
          }
        }

        /* ── 3. vertical moves are reversible (the anchor does its job) ── */
        if (r !== bottomRow) {
          var down = M.step(grid, from, "down");
          var backUp = M.step(grid, down, "up");
          ok(backUp.r === r && backUp.i === i,
             tag + " down-then-up from (" + r + "," + i + ") returned (" + backUp.r + "," + backUp.i + ")");
        }
        if (r !== topRow) {
          var up = M.step(grid, from, "up");
          var backDown = M.step(grid, up, "down");
          ok(backDown.r === r && backDown.i === i,
             tag + " up-then-down from (" + r + "," + i + ") returned (" + backDown.r + "," + backDown.i + ")");
        }
      }

      /* ── 4. horizontal wrap makes a complete cycle of the row ───────── */
      var startI = grid.live[r][0];
      var walk = M.posFor(grid, r, startI);
      var seen = {};
      for (k = 0; k < grid.live[r].length; k++) {
        seen[walk.i] = true;
        walk = M.step(grid, walk, "right");
      }
      var count = 0;
      for (var key in seen) if (Object.prototype.hasOwnProperty.call(seen, key)) count++;
      ok(count === grid.live[r].length,
         tag + " row " + r + ": walking right visited " + count + " of " + grid.live[r].length + " keys");
      ok(walk.i === startI, tag + " row " + r + ": right x" + grid.live[r].length + " did not return to the start");
    }

    /* ── 5. connectivity: every key is reachable from the opening key ── */
    var startPos = M.firstPos(grid);
    var queue = [startPos], seenIds = {}, reached = 0;
    seenIds[startPos.r + ":" + startPos.i] = true;
    while (queue.length) {
      var p = queue.shift();
      reached++;
      for (var dd = 0; dd < DIRS.length; dd++) {
        var q = M.step(grid, p, DIRS[dd]);
        var id = q.r + ":" + q.i;
        if (!seenIds[id]) { seenIds[id] = true; queue.push(M.posFor(grid, q.r, q.i)); }
      }
    }
    ok(reached === cells, tag + ": reached " + reached + " of " + cells + " keys by walking (unreachable keys)");

    /* ── 6. random walk: nothing throws, nothing escapes, nothing sticks ── */
    var pos = M.firstPos(grid), stuck = 0, lastId = pos.r + ":" + pos.i, bad = 0;
    for (var w = 0; w < 20000; w++) {
      pos = M.step(grid, pos, DIRS[(Math.random() * 4) | 0]);
      var cc = grid.rows[pos.r] && grid.rows[pos.r][pos.i];
      if (!cc || cc.spacer) { bad++; break; }
      var id2 = pos.r + ":" + pos.i;
      if (id2 === lastId) stuck++; else stuck = 0;
      lastId = id2;
      if (stuck > 200) break;
    }
    ok(bad === 0, tag + ": random walk left the grid");
    ok(stuck <= 200, tag + ": random walk stuck on one key for " + stuck + " moves");

    note("  " + pad(tag, 18) + pad(cells + " keys", 10) + pad(grid.rows.length + " rows", 9) + "ok");
  }

  /* ── 7. junk input cannot corrupt a position ─────────────────────── */
  var g = M.build("text", { reveal: false });
  var junk = [ { r: -1, i: 0 }, { r: 99, i: 0 }, { r: 0, i: 99 }, {} ];
  for (var j = 0; j < junk.length; j++) {
    var res = M.step(g, junk[j], "down");
    var cellJ = g.rows[res.r] && g.rows[res.r][res.i];
    ok(!!cellJ && !cellJ.spacer, "junk position " + JSON.stringify(junk[j]) + " was not recovered");
  }

  lines.unshift("MarwanOSK grid navigation — " + cases.length + " layer cases");
  lines.push((fail === 0 ? "PASS" : "FAIL") + "  " + pass + " assertions passed, " + fail + " failed");
  return { pass: pass, fail: fail, lines: lines };
}

/* Reference answer for "which key of row r sits under the anchor a2/(2·at)",
   written out independently of the model's own pick() so the two have to
   agree. Deliberately uses a different formulation — real division against a
   tolerance rather than cross-multiplied integers — so a shared arithmetic
   mistake cannot hide in both. */
function expectTarget(grid, r, a2, at) {
  var live = grid.live[r], cells = grid.rows[r], n, c;
  var x = a2 / (2 * at);
  for (n = 0; n < live.length; n++) {
    c = cells[live[n]];
    if (x >= c.c0 / c.tot - 1e-12 && x < c.c1 / c.tot - 1e-12) {
      return { i: live[n], why: "key spanning that column" };
    }
  }
  var best = live[0], bestD = Infinity;
  for (n = 0; n < live.length; n++) {
    c = cells[live[n]];
    var d = Math.abs((c.c0 + c.c1) / (2 * c.tot) - x);
    if (d < bestD - 1e-12) { bestD = d; best = live[n]; }
  }
  return { i: best, why: "nearest key centre" };
}

function pad(s, n) { s = String(s); while (s.length < n) s += " "; return s; }

root.MarwanOSKNavTest = { run: run };

if (typeof module !== "undefined" && module.exports) {
  module.exports = { run: run };
  if (typeof require !== "undefined" && require.main === module) {
    var r = run();
    console.log(r.lines.join("\n"));
    if (typeof process !== "undefined") process.exit(r.fail ? 1 : 0);
  }
}

})(typeof window !== "undefined" ? window
   : (typeof globalThis !== "undefined" ? globalThis : this));
