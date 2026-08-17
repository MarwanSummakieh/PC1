/* ═══════════════════════════════════════════════════════════════════════
   MarwanOS — homeart.js
   The home screen's artwork: what the background IS.

   The shell's ground used to be a fixed dark blue with a gradient wash
   that changed between ten hardcoded hues. That is the look of every
   media-centre template ever shipped. This module replaces it with the
   only material the machine actually has that is worth looking at: the
   cover art of the thing the cursor is pointing at.

   Three products per title, all derived from one image and all cached:

     wash    A deliberately TINY bitmap — the cover downsampled to 32×48
             and drawn back out at 256×384 — used as a full-bleed
             background. Blowing a 32×48 texture across a 3440-wide panel
             IS a blur, performed by the same bilinear filter that was
             going to composite the layer anyway. There is no filter:blur
             pass, no canvas running per frame, and nothing at all happens
             while the pad is untouched. That matters on a machine whose
             entire job is to hand its frames to a game.

     accent  The cover's dominant CHROMA, not its dominant colour. A
             ranked vote over hue buckets weighted by saturation, with
             near-greys and near-blacks excluded unless the art is
             genuinely monochrome. Then forced into a band of lightness
             and saturation that is safe to put a hairline or a rule in.
             Palworld lands on sky blue, Dota 2 on ember orange,
             Counter-Strike on its amber — which is the point: the screen
             takes its mood from the game rather than from a palette
             somebody picked once.

     face    For a title with NO cover — four of the seven games on the
             development laptop, and every game on a machine whose
             library came from Start Menu shortcuts — a generated
             portrait built from the executable's own icon: the icon's
             accent as a two-stop ground and the icon itself at
             readable size. Missing art has to look like a
             decision, and on this machine it is the majority case rather
             than an edge case.

   Nothing here reaches for the library, the host or the DOM. It is given
   data URIs and returns data URIs and colours, so it can be tested on its
   own and so ui/library.js stays the only file that knows what a game is.

   ── Public surface ──────────────────────────────────────────────────
     MarwanArt.build(key, {cover, icon, seed})  Promise<art>, cached by key.
     MarwanArt.get(key)                          the cached art, or null.
     MarwanArt.neutral()                         the art for "nothing here" —
                                              the empty machine's ground.
   `art` is {cover, wash, accent, deep, ink, hasCover}.
   ══════════════════════════════════════════════════════════════════════ */

(function (root) {
  "use strict";

  var cache = {};          /* key -> Promise<art> */
  var settled = {};        /* key -> art, once resolved, for synchronous reads */

  /* ═══ Colour ═══════════════════════════════════════════════════════ */

  function clamp(v, lo, hi) { return v < lo ? lo : (v > hi ? hi : v); }

  function rgbToHsl(r, g, b) {
    r /= 255; g /= 255; b /= 255;
    var max = Math.max(r, g, b), min = Math.min(r, g, b);
    var h = 0, s = 0, l = (max + min) / 2, d = max - min;
    if (d > 0.0001) {
      s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
      if (max === r) h = ((g - b) / d + (g < b ? 6 : 0));
      else if (max === g) h = (b - r) / d + 2;
      else h = (r - g) / d + 4;
      h *= 60;
    }
    return [h, s, l];
  }

  function hslCss(h, s, l) {
    return "hsl(" + Math.round(h) + " " + Math.round(s * 100) + "% " + Math.round(l * 100) + "%)";
  }

  /* The dominant chroma, by a vote over 24 hue buckets weighted by
     saturation × a mid-lightness preference. Weighting by saturation is
     what stops a cover that is 70% dark sky from returning navy: the
     small bright thing on it is what the eye takes as its colour, and it
     is what a person would name if asked what colour the game is. */
  function dominant(data, w, h) {
    var N = 24, bins = new Float64Array(N), hs = new Float64Array(N), ls = new Float64Array(N);
    var grey = 0, greyL = 0, n = w * h, i, px;
    for (i = 0; i < n; i++) {
      px = i * 4;
      if (data[px + 3] < 24) continue;
      var hsl = rgbToHsl(data[px], data[px + 1], data[px + 2]);
      var H = hsl[0], S = hsl[1], L = hsl[2];
      /* Mid lightness is where colour actually lives; the extremes are
         where compression noise and letterboxing live. */
      var lw = 1 - Math.abs(L - 0.52) * 1.7;
      if (lw < 0) lw = 0;
      if (S < 0.16 || L < 0.07 || L > 0.95) { grey += lw; greyL += L * lw; continue; }
      var b = Math.floor(H / 360 * N) % N;
      var wgt = S * S * lw;
      bins[b] += wgt; hs[b] += H * wgt; ls[b] += L * wgt;
    }
    var best = -1, bestV = 0;
    for (i = 0; i < N; i++) if (bins[i] > bestV) { bestV = bins[i]; best = i; }

    /* Genuinely monochrome art — a black-and-white cover, or an icon that
       is a white glyph on transparent — gets a neutral rather than a hue
       invented out of rounding error. */
    if (best < 0 || bestV < grey * 0.045) {
      var L2 = grey > 0 ? clamp(greyL / grey, 0.35, 0.72) : 0.5;
      /* Hue 44, not 220. At 6% saturation this is barely a colour at all,
         but it is the difference between a monochrome cover reading as
         cold-grey and reading as the same warm chalk-on-concrete as the
         rest of the shell — and the shell's OWN two tiles (Files, Browser)
         come down this path, so it is the tint an empty machine wears. */
      return { h: 44, s: 0.06, l: L2, mono: true };
    }
    var H2 = hs[best] / bins[best], L3 = ls[best] / bins[best];
    return { h: H2, s: 0.62, l: clamp(L3, 0.42, 0.66), mono: false };
  }

  /* Two colours out of one hue: `accent` is safe to draw a hairline or a
     rule in over the shell's ground, `deep` is safe to sit BEHIND white
     text. Both are pinned to a lightness band rather than taken raw,
     because "derive the colour from the art" and "keep the contrast
     ratio" are the two halves of the same requirement — a wash that
     honestly reproduces a bright cover is a wash you cannot read on. */
  function pair(d) {
    var s = d.mono ? 0.08 : 0.58;
    return {
      accent: hslCss(d.h, clamp(s + 0.06, 0.08, 0.72), clamp(d.l + 0.10, 0.52, 0.70)),
      deep:   hslCss(d.h, clamp(s * 0.72, 0.05, 0.5),  clamp(d.l * 0.30, 0.07, 0.17)),
      ink:    hslCss(d.h, clamp(s * 0.5, 0.04, 0.35),  clamp(d.l * 0.16, 0.035, 0.085))
    };
  }

  /* ═══ Bitmaps ══════════════════════════════════════════════════════ */

  function loadImage(uri) {
    return new Promise(function (resolve, reject) {
      if (!uri) { reject(new Error("no image")); return; }
      var img = new Image();
      img.onload = function () { resolve(img); };
      img.onerror = function () { reject(new Error("image failed to decode")); };
      img.src = uri;
    });
  }

  function canvas(w, h) {
    var c = document.createElement("canvas");
    c.width = w; c.height = h;
    return c;
  }

  /* The wash. Downsample hard, then draw the small result back out at a
     size the compositor is happy to stretch. Two smoothed resamples cost
     under a millisecond each and buy a background that costs nothing at
     all to composite afterwards — which is the whole trade. */
  function washFrom(img) {
    var s = canvas(32, 48), sc = s.getContext("2d");
    sc.imageSmoothingEnabled = true;
    try { sc.imageSmoothingQuality = "high"; } catch (e) {}
    sc.drawImage(img, 0, 0, 32, 48);
    var d = sc.getImageData(0, 0, 32, 48);

    var b = canvas(256, 384), bc = b.getContext("2d");
    bc.imageSmoothingEnabled = true;
    try { bc.imageSmoothingQuality = "high"; } catch (e) {}
    bc.drawImage(s, 0, 0, 256, 384);
    return { uri: b.toDataURL("image/png"), data: d.data };
  }

  /* A wash with no art to make it from: a soft vertical field in the
     title's own accent. Used for a game whose only artwork is an icon,
     and for the shell's own tiles. */
  function washFromColour(accent, deep, ink) {
    var c = canvas(64, 96), x = c.getContext("2d");
    var g = x.createLinearGradient(0, 0, 44, 96);
    g.addColorStop(0, accent); g.addColorStop(0.42, deep); g.addColorStop(1, ink);
    x.fillStyle = g; x.fillRect(0, 0, 64, 96);
    var r = x.createRadialGradient(46, 16, 2, 46, 16, 62);
    r.addColorStop(0, accent); r.addColorStop(1, "rgba(0,0,0,0)");
    x.globalAlpha = 0.5; x.fillStyle = r; x.fillRect(0, 0, 64, 96);
    return c.toDataURL("image/png");
  }

  /* The generated portrait for a title with no cover.

     Not a placeholder and not a "missing art" plate: a real 3:4 face
     built to the same rules as the rest of the shell — the ground is the
     icon's own colour, and the icon is drawn large and sharp rather
     than stretched to fill. Held next to a
     Steam cover on the same rail it reads as a different KIND of cover,
     which is honest, rather than as a broken one. */
  function faceFrom(icon, colours) {
    var W = 300, H = 400;
    var c = canvas(W, H), x = c.getContext("2d");

    var g = x.createLinearGradient(0, 0, W * 0.75, H);
    g.addColorStop(0, colours.accent);
    g.addColorStop(0.46, colours.deep);
    g.addColorStop(1, colours.ink);
    x.fillStyle = g; x.fillRect(0, 0, W, H);

    /* Darken the lower third so the name printed over the cover by the
       rail has something to sit on no matter what the icon's colour is. */
    var s = x.createLinearGradient(0, H * 0.42, 0, H);
    s.addColorStop(0, "rgba(0,0,0,0)");
    s.addColorStop(1, "rgba(0,0,0,.62)");
    x.fillStyle = s; x.fillRect(0, 0, W, H);

    if (icon) {
      var side = Math.min(icon.width, icon.height) || 1;
      var draw = W * 0.42;
      var iw = icon.width / side * draw, ih = icon.height / side * draw;
      x.save();
      x.shadowColor = "rgba(0,0,0,.45)"; x.shadowBlur = 18; x.shadowOffsetY = 4;
      x.drawImage(icon, (W - iw) / 2, H * 0.38 - ih / 2, iw, ih);
      x.restore();
    }
    return c.toDataURL("image/png");
  }

  /* ═══ Build ════════════════════════════════════════════════════════ */

  function record(o) { return o; }

  function fromSeed(seed) {
    /* 146 — the shell's own spray green — where this was 218. A title with
       no cover at all gets a portrait in the product's colour rather than
       in a blue nobody chose. */
    var d = { h: (seed && seed.h) || 146, s: 0.5, l: 0.5, mono: !!(seed && seed.mono) };
    var p = pair(d);
    return record({
      cover: null, face: null, hasCover: false,
      wash: washFromColour(p.accent, p.deep, p.ink),
      accent: p.accent, deep: p.deep, ink: p.ink
    });
  }

  function build(key, opts) {
    if (Object.prototype.hasOwnProperty.call(cache, key)) return cache[key];
    opts = opts || {};

    var p;
    if (opts.cover) {
      p = loadImage(opts.cover).then(function (img) {
        var w = washFrom(img);
        var col = pair(dominant(w.data, 32, 48));
        return record({
          cover: opts.cover, face: null, hasCover: true,
          wash: w.uri, accent: col.accent, deep: col.deep, ink: col.ink
        });
      });
    } else if (opts.icon) {
      p = loadImage(opts.icon).then(function (img) {
        /* The icon is small and usually has a lot of transparent margin,
           so it is sampled at its own size rather than downsampled — the
           whole point is to find the one colour the brand actually is. */
        var w = Math.max(1, Math.min(64, img.width || 32));
        var h = Math.max(1, Math.min(64, img.height || 32));
        var c = canvas(w, h), x = c.getContext("2d");
        x.drawImage(img, 0, 0, w, h);
        var col = pair(dominant(x.getImageData(0, 0, w, h).data, w, h));
        return record({
          cover: null, face: faceFrom(img, col), hasCover: false,
          wash: washFromColour(col.accent, col.deep, col.ink),
          accent: col.accent, deep: col.deep, ink: col.ink
        });
      });
    } else {
      p = Promise.resolve(fromSeed(opts.seed));
    }

    /* A decode failure must never leave the screen without a ground. */
    p = p.then(function (art) { settled[key] = art; return art; },
               function () {
                 var art = fromSeed(opts.seed);
                 settled[key] = art;
                 return art;
               });
    cache[key] = p;
    return p;
  }

  var neutralArt = null;

  /* The ground for a machine with nothing on it, and for the shell's own
     two tiles. Deliberately not "grey because we gave up": a field with a
     colour somebody chose, so an empty library is a screen somebody
     designed rather than a screen that failed.

     Hue 214 — a cool blue graphite — until the shell took the site's warm
     wall-and-chalk palette. An empty machine was then the ONE screen still
     lit blue, which is exactly the screen where the identity has to do all
     the work because there is no cover art to carry it. Hue 146 at low
     saturation is the spray green thinned down to a concrete: the same
     pigment as the accent, nowhere near saturated enough to compete with a
     real cover when one arrives. */
  function neutral() {
    if (!neutralArt) {
      var d = { h: 146, s: 0.16, l: 0.40, mono: false };
      var col = pair(d);
      col.accent = "hsl(146 26% 58%)";
      neutralArt = record({
        cover: null, face: null, hasCover: false,
        wash: washFromColour("hsl(146 22% 34%)", "hsl(150 12% 14%)", "hsl(150 8% 6%)"),
        accent: col.accent, deep: "hsl(150 12% 14%)", ink: "hsl(150 8% 6%)"
      });
    }
    return neutralArt;
  }

  root.MarwanArt = {
    build: build,
    get: function (key) { return settled[key] || null; },
    neutral: neutral,
    /* exposed for the headless colour tests */
    _dominant: dominant,
    _pair: pair
  };
})(typeof window !== "undefined" ? window : this);
