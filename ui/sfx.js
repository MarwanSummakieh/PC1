/* ═══════════════════════════════════════════════════════════════════════════
   ARC OS — the sensory layer.   ui/sfx.js

   Two objects live here and nothing else touches the page:

     ArcSfx    a Web Audio synthesiser. Every cue is built from oscillators
               and filtered noise at play time — there are no audio files,
               because a strict CSP applies, the shell has to stay
               self-contained, and synthesis is the only way to control the
               attack and the decay to the millisecond. That control is the
               whole difference between "crafted" and "stock".

     ArcFeel   the glue. It wraps Nav.action(), works out what just happened
               from the focus depth and the action's own return string, and
               fires the matching sound AND the matching haptic so the two
               are never out of step. It knows nothing about the shell's
               internals beyond the two objects it is handed.

   ── The point of view ──────────────────────────────────────────────────────
   A console is used from three metres away, often late, often for hours. So:

     * Nothing is a sine beep. Every cue is a tone with a filtered noise
       transient under it. The transient is what your ear reads as an
       *object* being touched rather than a tone being played.
     * Nothing lasts longer than ~150 ms except the boot signature.
     * Movement is the quietest thing in the palette by a wide margin,
       because it fires ten times a second and anything with presence
       becomes torture by the third minute.
     * Confirmation is warm and rises. Retreat is darker, shorter and falls.
       They are the same gesture played backwards, so you learn one and get
       both.
     * Refusal is dry. It is a dead, lowpassed thud with a slow beat in it,
       never a buzzer — this plays on a television at two in the morning.
     * Everything is in A. Cues that land together (a push over a move) are
       consonant by construction rather than by luck.

   ── Anti-fatigue machinery ────────────────────────────────────────────────
     * Detune: each repeat of a cue takes the next entry from a small
       rotating cent table plus a few cents of jitter, so holding a
       direction never sounds like a machine gun.
     * Position pitch: a move is pitched by where the newly focused element
       physically is on screen. Walking right up the rail walks the pitch up
       with it. It costs one getBoundingClientRect and it is most of why the
       rail feels like a place.
     * Rate limit: the same cue inside 60 ms is ducked hard, inside 28 ms it
       is dropped. Faster than that is a repeat storm, not an intention.
     * A zero-latency soft clipper sits on the master bus purely as a safety
       ceiling, so no pile-up of cues can ever clip the output. Deliberately
       not a DynamicsCompressor — see the note on buildChain().
   ═══════════════════════════════════════════════════════════════════════════ */

(function (global) {
  "use strict";

  /* ── Musical constants ────────────────────────────────────────────────────
     A2 = 110 Hz. Everything in the palette is a member of this set, which is
     why two cues landing on top of each other never sound like an accident. */
  var A1 = 55, A2 = 110, CS3 = 138.59, D3 = 146.83, E3 = 164.81, A3 = 220,
      CS4 = 277.18, E4 = 329.63, A4 = 440, CS5 = 554.37, E5 = 659.26, A5 = 880;

  var STORE_KEY = "arc.sfx.v1";

  /* ═══ ArcSfx — the synthesiser ═══════════════════════════════════════════ */

  var ArcSfx = (function () {

    var ctx = null;               // the live AudioContext (lazy)
    var chain = null;             // { master, limiter } for the live context
    var noiseCache = null;        // WeakMap-ish: one noise buffer per context
    var noiseCtx = null;

    var volume = 0.7;             // the user's UI-sound volume, 0..1
    var muted = false;
    var sysLevel = 1;             // Windows output volume, as the shell knows it
    var sysMuted = false;
    var tracking = true;          // whether to follow the system volume at all

    var lastAt = Object.create(null);   // per-cue throttle clock
    var detuneAt = Object.create(null); // per-cue rotating detune index
    var live = 0;                       // rough count of cues in flight
    var LIVE_CAP = 10;

    /* A rotating table rather than pure randomness: pure randomness
       occasionally repeats the same value twice, which is exactly the
       artefact the detune exists to kill. */
    var DETUNE = [0, 7, -5, 11, -9, 3, -12, 6];

    var log = function () {};

    /* ── Volume model ──────────────────────────────────────────────────────
       Windows already attenuates this process's stream by the system volume,
       so applying the system volume a second time at full strength would
       make UI sound vanish entirely at 30%. What we actually want is for the
       cues to stay *proportionate* — a shade quieter than linear as the TV
       gets quieter, so they never feel disproportionately loud at night —
       hence the partial curve. System mute is honoured absolutely. */
    function effectiveGain() {
      if (muted) return 0;
      if (tracking && sysMuted) return 0;
      var sys = tracking ? (0.4 + 0.6 * clamp01(sysLevel)) : 1;
      /* Perceptual taper: volume^1.6 tracks loudness far better than a
         straight multiply, which feels dead across the bottom half. */
      return Math.pow(clamp01(volume), 1.6) * sys;
    }

    function clamp01(v) { return v < 0 ? 0 : (v > 1 ? 1 : (typeof v === "number" && v === v ? v : 0)); }

    /* ── Context lifecycle ─────────────────────────────────────────────────
       The context is created on the first cue, not at load: a shell that has
       never made a sound should not be holding an audio device open.

       Chromium starts an AudioContext suspended until the page has user
       activation. The shell is driven by HID reports relayed from the host,
       which are not activation, so the host launches this WebView with
       --autoplay-policy=no-user-gesture-required. resume() is still
       attempted on every cue and on any real input event, so that a build
       without the flag recovers the moment a human touches anything. */
    function context() {
      if (ctx) return ctx;
      var C = global.AudioContext || global.webkitAudioContext;
      if (!C) return null;
      try { ctx = new C({ latencyHint: "interactive" }); }
      catch (e) { try { ctx = new C(); } catch (e2) { return null; } }
      chain = buildChain(ctx);
      armUnlock();
      log("sfx: AudioContext created, state=" + ctx.state + " sr=" + ctx.sampleRate);
      return ctx;
    }

    /* The safety ceiling.

       A DynamicsCompressor was the obvious choice here and it is the wrong
       one: Chromium's has a ~6 ms lookahead and a gain that settles over
       ~140 ms, so it quietly *swallows* short transients while making long
       cues louder. Measured on the bench harness, the move cue came out
       11 dB below its designed level while the boot cue came out above it.
       That is precisely the mush that makes UI sound feel cheap.

       So: a waveshaper running y = x / (1 + |x|^3)^(1/3). Unity slope at the
       origin (-0.02 dB at the loudest cue in the palette — genuinely
       transparent), asymptotic to a hard ceiling, and instantaneous. No
       lookahead, no release, nothing to smear an 8 ms tick. */
    var SHAPE_CURVE = null;
    function softClipCurve() {
      if (SHAPE_CURVE) return SHAPE_CURVE;
      var n = 8192, cur = new Float32Array(n);
      for (var i = 0; i < n; i++) {
        var x = (i / (n - 1)) * 2 - 1;
        var a = Math.abs(x);
        cur[i] = x / Math.pow(1 + a * a * a, 1 / 3);
      }
      SHAPE_CURVE = cur;
      return cur;
    }

    function buildChain(c) {
      var master = c.createGain();
      master.gain.value = 1;
      var lim = c.createWaveShaper();
      lim.curve = softClipCurve();
      try { lim.oversample = "4x"; } catch (e) {}
      master.connect(lim);
      lim.connect(c.destination);
      return { master: master, limiter: lim };
    }

    var unlockArmed = false;
    function armUnlock() {
      if (unlockArmed || typeof document === "undefined") return;
      unlockArmed = true;
      var fn = function () { unlock(); };
      ["pointerdown", "keydown", "mousedown", "touchstart"].forEach(function (ev) {
        try { document.addEventListener(ev, fn, { capture: true, passive: true }); } catch (e) {}
      });
    }

    function unlock() {
      var c = ctx;
      if (!c) return;
      if (c.state === "suspended") { try { c.resume(); } catch (e) {} }
    }

    /* ── Primitives ────────────────────────────────────────────────────────
       K is the little kit every cue builder is handed: the context, the node
       it should connect into, and a shared noise buffer. Cues never touch
       ctx.destination, so the exact same builder renders live or offline. */

    function noiseBuffer(c) {
      if (noiseCtx === c && noiseCache) return noiseCache;
      var n = Math.floor(c.sampleRate * 0.5);
      var b = c.createBuffer(1, n, c.sampleRate);
      var d = b.getChannelData(0);
      /* Deterministic (an LCG, not Math.random) so an offline render of a
         cue is reproducible and the verification numbers mean something. */
      var s = 22222;
      for (var i = 0; i < n; i++) {
        s = (s * 1103515245 + 12345) & 0x7fffffff;
        d[i] = (s / 0x3fffffff) - 1;
      }
      noiseCtx = c; noiseCache = b;
      return b;
    }

    /* An amplitude envelope with a real attack and a natural (exponential)
       decay. Web Audio cannot ramp exponentially to zero, so it ramps to
       -80 dB and then a 4 ms linear ramp takes it to true silence — without
       that last ramp every cue ends on a faint DC step you hear as a click. */
    function shape(g, t0, peak, attack, decay) {
      var p = Math.max(peak, 0.0002);
      g.setValueAtTime(0.0001, t0);
      g.exponentialRampToValueAtTime(p, t0 + attack);
      g.exponentialRampToValueAtTime(0.0001, t0 + attack + decay);
      g.linearRampToValueAtTime(0, t0 + attack + decay + 0.004);
      return t0 + attack + decay + 0.004;
    }

    /* A pitched voice: oscillator -> lowpass -> envelope. `to` glides the
       pitch; `lp` opens or closes the filter over the same span. */
    function tone(K, o) {
      var c = K.ctx, t0 = o.at;
      var osc = c.createOscillator();
      osc.type = o.type || "triangle";
      osc.frequency.setValueAtTime(o.freq, t0);
      if (o.to && o.to !== o.freq)
        osc.frequency.exponentialRampToValueAtTime(o.to, t0 + (o.glide || o.decay || 0.06));
      if (o.detune) osc.detune.setValueAtTime(o.detune, t0);

      var f = c.createBiquadFilter();
      f.type = "lowpass";
      f.Q.value = o.q || 0.7;
      f.frequency.setValueAtTime(o.lp || 4000, t0);
      if (o.lpTo) f.frequency.exponentialRampToValueAtTime(o.lpTo, t0 + (o.lpTime || o.decay || 0.08));

      var g = c.createGain();
      var end = shape(g.gain, t0, o.gain, o.attack || 0.004, o.decay || 0.08);

      osc.connect(f); f.connect(g); g.connect(K.out);
      osc.start(t0);
      osc.stop(end + 0.02);
      return end;
    }

    /* The transient. A short bandpassed noise burst under a tone is what
       gives a UI sound a body — the tone alone reads as a test signal. */
    function noise(K, o) {
      var c = K.ctx, t0 = o.at;
      var src = c.createBufferSource();
      src.buffer = K.noise;
      src.loop = true;
      /* Start at a different offset each time: reusing the same 12 ms of
         the same buffer is audible as a repeated texture. */
      src.loopStart = 0;
      src.loopEnd = K.noise.duration;
      var off = (o.seed || 0) % 0.4;

      var f = c.createBiquadFilter();
      f.type = o.filter || "bandpass";
      f.Q.value = o.q || 1.1;
      f.frequency.setValueAtTime(o.freq, t0);
      if (o.to) f.frequency.exponentialRampToValueAtTime(o.to, t0 + (o.sweep || o.decay || 0.05));

      var g = c.createGain();
      var end = shape(g.gain, t0, o.gain, o.attack || 0.001, o.decay || 0.03);

      src.connect(f); f.connect(g); g.connect(K.out);
      src.start(t0, off);
      src.stop(end + 0.02);
      return end;
    }

    /* ═══ The palette ═════════════════════════════════════════════════════
       Every builder takes (K, t0, o) and returns the time it finishes.
       o.pitch is a ratio (screen position, direction), o.detune is cents,
       o.scale scales the cue's own level. */

    var CUES = {

      /* MOVE — tile to tile.
         The most-played cue in the shell by an order of magnitude, so it is
         also the quietest and the darkest: a 2.4 kHz tick with a 40 ms
         triangle body under a 1.6 kHz lowpass. There is deliberately no
         energy above 3 kHz — that band is what makes repeated UI sound
         tiring, and it is the first thing a cheap replica gets wrong. */
      move: function (K, t, o) {
        var p = o.pitch || 1;
        noise(K, { at: t, freq: 2400 * p, q: 1.4, gain: 0.030 * o.scale,
                   attack: 0.0008, decay: 0.022, seed: o.seed });
        return tone(K, { at: t, freq: A4 * p, type: "triangle", detune: o.detune,
                         lp: 1600 * p, lpTo: 900 * p, lpTime: 0.04,
                         gain: 0.055 * o.scale, attack: 0.002, decay: 0.042 });
      },

      /* NUDGE — the edge of the rail, a direction with nothing in it.
         Move with the tone taken away: all transient, no pitch, lowpassed
         to a dull knock. You feel the wall rather than hear an error. */
      nudge: function (K, t, o) {
        noise(K, { at: t, freq: 320, q: 0.8, filter: "lowpass", gain: 0.060 * o.scale,
                   attack: 0.001, decay: 0.048, seed: o.seed });
        return tone(K, { at: t, freq: A2, type: "sine", lp: 240,
                         gain: 0.045 * o.scale, attack: 0.003, decay: 0.055 });
      },

      /* ACTIVATE — the "yes".
         Warmer and fuller than anything else in the navigation set: a fifth
         (A3 + E4) with the fifth entering 16 ms late so the interval opens
         rather than arrives, and a 2 % upward glide across the first 40 ms.
         The glide is small enough that you do not hear a pitch bend, only a
         lift. That lift is the entire emotional content of the cue. */
      activate: function (K, t, o) {
        noise(K, { at: t, freq: 3000, q: 1.2, gain: 0.0495 * o.scale,
                   attack: 0.001, decay: 0.026, seed: o.seed });
        tone(K, { at: t, freq: A3, to: A3 * 1.02, glide: 0.04, type: "triangle",
                  detune: o.detune, lp: 2600, gain: 0.143 * o.scale,
                  attack: 0.008, decay: 0.125 });
        return tone(K, { at: t + 0.016, freq: E4, to: E4 * 1.02, glide: 0.04,
                         type: "sine", detune: o.detune, lp: 3200,
                         gain: 0.0935 * o.scale, attack: 0.010, decay: 0.105 });
      },

      /* LAUNCH — activate with the floor under it.
         Handing the machine over to another program is a bigger event than
         opening a panel, so the same chord gains an octave below and a
         longer tail. Still under 200 ms of body. */
      launch: function (K, t, o) {
        noise(K, { at: t, freq: 2600, q: 1.0, gain: 0.06 * o.scale,
                   attack: 0.001, decay: 0.032, seed: o.seed });
        tone(K, { at: t, freq: A2, type: "sine", lp: 700,
                  gain: 0.144 * o.scale, attack: 0.012, decay: 0.190 });
        tone(K, { at: t, freq: A3, to: A3 * 1.02, glide: 0.05, type: "triangle",
                  detune: o.detune, lp: 2600, gain: 0.156 * o.scale,
                  attack: 0.008, decay: 0.150 });
        return tone(K, { at: t + 0.020, freq: E4, to: E4 * 1.02, glide: 0.05,
                         type: "sine", lp: 3400, gain: 0.108 * o.scale,
                         attack: 0.012, decay: 0.130 });
      },

      /* BACK — activate played backwards.
         Same two notes, same timbre, descending instead of rising, and cut
         to two thirds the length and two thirds the level. Retreat should
         never feel as good as arrival; if it does, people go backwards. */
      back: function (K, t, o) {
        noise(K, { at: t, freq: 1500, q: 1.0, gain: 0.032 * o.scale,
                   attack: 0.001, decay: 0.020, seed: o.seed });
        tone(K, { at: t, freq: E4, to: A3, glide: 0.055, type: "triangle",
                  detune: o.detune, lp: 2200, lpTo: 1100, lpTime: 0.07,
                  gain: 0.088 * o.scale, attack: 0.004, decay: 0.085 });
        return tone(K, { at: t + 0.010, freq: A3, to: A2 * 2 * 0.999, glide: 0.055,
                         type: "sine", lp: 1400, gain: 0.050 * o.scale,
                         attack: 0.005, decay: 0.075 });
      },

      /* PUSH — a scope opens, an overlay comes up.
         A rise, but a *soft* one: 12 ms attack, a fourth of pitch travel
         (E4 -> A4) and a filter opening from 500 Hz to 3 kHz underneath. The
         opening filter is the sound of something being revealed; the pitch
         alone would just be a chirp. */
      push: function (K, t, o) {
        noise(K, { at: t, freq: 500, to: 3000, sweep: 0.085, q: 0.9,
                   gain: 0.038 * o.scale, attack: 0.010, decay: 0.080, seed: o.seed });
        return tone(K, { at: t, freq: E4, to: A4, glide: 0.075, type: "triangle",
                         detune: o.detune, lp: 1200, lpTo: 3600, lpTime: 0.08,
                         gain: 0.085 * o.scale, attack: 0.012, decay: 0.100 });
      },

      /* POP — its mirror. Falls, closes, and starts harder (4 ms) because a
         thing closing is an event with an edge, not a reveal. */
      pop: function (K, t, o) {
        noise(K, { at: t, freq: 2600, to: 420, sweep: 0.075, q: 0.9,
                   gain: 0.034 * o.scale, attack: 0.002, decay: 0.075, seed: o.seed });
        return tone(K, { at: t, freq: A4, to: E4, glide: 0.070, type: "triangle",
                         detune: o.detune, lp: 3200, lpTo: 900, lpTime: 0.075,
                         gain: 0.075 * o.scale, attack: 0.004, decay: 0.090 });
      },

      /* TAB — the lateral move between Play and Media.
         Not a navigation move (the whole screen changed) and not a push
         (nothing opened). A move with a fifth stacked on it: same family,
         obviously bigger. */
      tab: function (K, t, o) {
        var p = o.pitch || 1;
        noise(K, { at: t, freq: 2200 * p, q: 1.2, gain: 0.036 * o.scale,
                   attack: 0.001, decay: 0.026, seed: o.seed });
        tone(K, { at: t, freq: A4 * p, type: "triangle", detune: o.detune,
                  lp: 2000, gain: 0.065 * o.scale, attack: 0.003, decay: 0.070 });
        return tone(K, { at: t + 0.012, freq: E5 * p, type: "sine", detune: o.detune,
                         lp: 3000, gain: 0.038 * o.scale, attack: 0.004, decay: 0.055 });
      },

      /* TOGGLE ON / OFF — a pair, by construction.
         Identical timbre, identical length, identical level; the only thing
         that differs is the order of the two notes. Nobody has to be taught
         which is which. */
      toggleOn: function (K, t, o) {
        noise(K, { at: t, freq: 2800, q: 1.4, gain: 0.030 * o.scale,
                   attack: 0.001, decay: 0.016, seed: o.seed });
        tone(K, { at: t, freq: CS5, type: "triangle", detune: o.detune,
                  lp: 2600, gain: 0.070 * o.scale, attack: 0.003, decay: 0.030 });
        return tone(K, { at: t + 0.038, freq: A5, type: "triangle", detune: o.detune,
                         lp: 3400, gain: 0.070 * o.scale, attack: 0.003, decay: 0.048 });
      },
      toggleOff: function (K, t, o) {
        noise(K, { at: t, freq: 2800, q: 1.4, gain: 0.030 * o.scale,
                   attack: 0.001, decay: 0.016, seed: o.seed });
        tone(K, { at: t, freq: A5, type: "triangle", detune: o.detune,
                  lp: 3400, gain: 0.070 * o.scale, attack: 0.003, decay: 0.030 });
        return tone(K, { at: t + 0.038, freq: CS5, type: "triangle", detune: o.detune,
                         lp: 2600, gain: 0.070 * o.scale, attack: 0.003, decay: 0.048 });
      },

      /* ERROR — refused.
         Dry, dark and over in 90 ms. Two square waves a semitone-ish apart
         (C#3 and D3) beat against each other at ~8 Hz, which the ear reads
         as roughness — "wrong" — without a single component above 700 Hz.
         Nothing here can startle someone at 2 a.m., and there is no tail:
         the envelope chokes rather than rings, which is what makes it read
         as a refusal instead of a note. */
      error: function (K, t, o) {
        noise(K, { at: t, freq: 700, q: 3.0, gain: 0.0248 * o.scale,
                   attack: 0.001, decay: 0.030, seed: o.seed });
        tone(K, { at: t, freq: CS3, type: "square", lp: 620,
                  gain: 0.0383 * o.scale, attack: 0.003, decay: 0.062 });
        return tone(K, { at: t, freq: D3, type: "square", lp: 620,
                         gain: 0.0315 * o.scale, attack: 0.003, decay: 0.062 });
      },

      /* BOOT — the low swell, fired as the boot arc starts.
         Deliberately almost subliminal: a 700 ms breath on A1/A2 with the
         filter opening slowly. It is not a fanfare, it is the room being
         switched on. Anything more here would have to compete with the
         signature 4 seconds later, and one of them would lose. */
      boot: function (K, t, o) {
        noise(K, { at: t, freq: 90, to: 600, sweep: 0.55, q: 0.6, filter: "lowpass",
                   gain: 0.077 * o.scale, attack: 0.180, decay: 0.520, seed: o.seed });
        tone(K, { at: t, freq: A1, type: "sine", lp: 400,
                  gain: 0.154 * o.scale, attack: 0.220, decay: 0.560 });
        return tone(K, { at: t + 0.060, freq: A2, type: "triangle", lp: 320, lpTo: 900,
                         lpTime: 0.5, gain: 0.098 * o.scale, attack: 0.260, decay: 0.520 });
      },

      /* BOOT DONE — the signature, fired on the frame the home screen wipes in.
         A2 already ringing underneath, then A3, E4 and A4 arriving 110 ms
         apart: root, fifth, octave. It resolves upward and stops — no
         seventh, no suspension, nothing that asks a question. A noise wipe
         sweeps 200 Hz -> 6 kHz across the first 260 ms and lands with the
         A4, so the sound of the screen arriving and the picture of the
         screen arriving are the same event.

         This is the one cue allowed to be expressive, and the restraint is
         the point: three notes of one chord, 1.2 seconds, then silence. */
      bootDone: function (K, t, o) {
        noise(K, { at: t, freq: 200, to: 6000, sweep: 0.26, q: 0.5, filter: "bandpass",
                   gain: 0.119 * o.scale, attack: 0.020, decay: 0.300, seed: o.seed });
        tone(K, { at: t, freq: A2, type: "sine", lp: 500,
                  gain: 0.1785 * o.scale, attack: 0.030, decay: 0.900 });
        tone(K, { at: t, freq: A3, type: "triangle", lp: 1800,
                  gain: 0.17 * o.scale, attack: 0.014, decay: 0.520 });
        tone(K, { at: t + 0.110, freq: E4, type: "triangle", lp: 2400,
                  gain: 0.1445 * o.scale, attack: 0.014, decay: 0.480 });
        tone(K, { at: t + 0.220, freq: A4, type: "sine", lp: 3600,
                  gain: 0.153 * o.scale, attack: 0.016, decay: 0.720 });
        /* The bloom: E5 a whisper under the octave, 40 ms late, at a third
           the level. You do not hear it as a note, you hear the A4 as
           bigger than it is. */
        return tone(K, { at: t + 0.260, freq: E5, type: "sine", lp: 4200,
                         gain: 0.051 * o.scale, attack: 0.030, decay: 0.600 });
      }
    };

    /* Longest possible tail per cue, for offline rendering and for the
       in-flight counter. Generous by ~30 %. */
    var LENGTH = {
      move: 0.10, nudge: 0.12, activate: 0.22, launch: 0.30, back: 0.20,
      push: 0.22, pop: 0.22, tab: 0.16, toggleOn: 0.16, toggleOff: 0.16,
      error: 0.16, boot: 1.10, bootDone: 1.60
    };

    /* ── play ──────────────────────────────────────────────────────────── */

    function nextDetune(name) {
      var i = (detuneAt[name] = ((detuneAt[name] || 0) + 1) % DETUNE.length);
      return DETUNE[i] + (Math.random() * 8 - 4);
    }

    function play(name, opt) {
      opt = opt || {};
      var build = CUES[name];
      if (!build) return false;
      if (effectiveGain() <= 0) return false;

      var now = (global.performance && performance.now) ? performance.now() : Date.now();
      var prev = lastAt[name] || -1e9;
      var dt = now - prev;
      var duck = 1;
      /* Faster than a human intention: drop it. Fast but plausible: duck it
         hard, so a held direction becomes a decaying flutter instead of a
         machine gun. */
      if (dt < 28) return false;
      if (dt < 60) duck = 0.45;
      else if (dt < 110) duck = 0.75;
      lastAt[name] = now;

      if (live > LIVE_CAP) return false;

      var c = context();
      if (!c) return false;
      if (c.state === "suspended") { try { c.resume(); } catch (e) {} }

      var K = { ctx: c, out: chain.master, noise: noiseBuffer(c) };
      chain.master.gain.setTargetAtTime(effectiveGain(), c.currentTime, 0.01);

      var t0 = c.currentTime + 0.005;
      try {
        build(K, t0, {
          pitch: opt.pitch || 1,
          detune: typeof opt.detune === "number" ? opt.detune : nextDetune(name),
          scale: (typeof opt.scale === "number" ? opt.scale : 1) * duck,
          seed: Math.random() * 0.4
        });
      } catch (e) { log("sfx: cue '" + name + "' threw: " + e); return false; }

      live++;
      setTimeout(function () { if (live > 0) live--; }, ((LENGTH[name] || 0.3) * 1000) + 60);
      return true;
    }

    /* ── Offline render, for verification ───────────────────────────────────
       Renders one cue through the exact shipping chain (master gain at the
       given level, then the limiter) into an OfflineAudioContext, so the
       numbers in a test report describe what actually reaches the speakers
       rather than a parallel implementation of it. */
    function render(name, opt) {
      opt = opt || {};
      var OAC = global.OfflineAudioContext || global.webkitOfflineAudioContext;
      if (!OAC || !CUES[name]) return Promise.reject(new Error("cannot render " + name));
      var sr = opt.sampleRate || 48000;
      var secs = opt.seconds || ((LENGTH[name] || 0.3) + 0.25);
      var c = new OAC(2, Math.ceil(sr * secs), sr);
      var ch = buildChain(c);
      ch.master.gain.value = typeof opt.gain === "number" ? opt.gain : 1;
      var K = { ctx: c, out: ch.master, noise: noiseBuffer(c) };
      CUES[name](K, 0.005, {
        pitch: opt.pitch || 1,
        detune: typeof opt.detune === "number" ? opt.detune : 0,
        scale: typeof opt.scale === "number" ? opt.scale : 1,
        seed: 0.1
      });
      return c.startRendering();
    }

    /* Renders several cues on top of each other through one shipping chain.
       This exists to prove the limiter: the worst case in the shell is the
       boot signature still ringing while a launch, a push and a move land
       on top of it, and that sum must not reach 0 dBFS. */
    function renderMix(list, opt) {
      opt = opt || {};
      var OAC = global.OfflineAudioContext || global.webkitOfflineAudioContext;
      if (!OAC) return Promise.reject(new Error("no OfflineAudioContext"));
      var sr = opt.sampleRate || 48000;
      var secs = opt.seconds || 2.0;
      var c = new OAC(2, Math.ceil(sr * secs), sr);
      var ch = buildChain(c);
      ch.master.gain.value = typeof opt.gain === "number" ? opt.gain : 1;
      var K = { ctx: c, out: ch.master, noise: noiseBuffer(c) };
      for (var i = 0; i < list.length; i++) {
        var it = list[i];
        if (!CUES[it.cue]) continue;
        CUES[it.cue](K, (it.at || 0) + 0.005, {
          pitch: it.pitch || 1, detune: it.detune || 0,
          scale: typeof it.scale === "number" ? it.scale : 1, seed: 0.1 + i * 0.05
        });
      }
      return c.startRendering();
    }

    /* ── Persistence ───────────────────────────────────────────────────── */
    function save() {
      try {
        localStorage.setItem(STORE_KEY, JSON.stringify({
          volume: volume, muted: muted, tracking: tracking
        }));
      } catch (e) {}
    }
    function load() {
      try {
        var s = JSON.parse(localStorage.getItem(STORE_KEY) || "null");
        if (s && typeof s === "object") {
          if (typeof s.volume === "number") volume = clamp01(s.volume);
          if (typeof s.muted === "boolean") muted = s.muted;
          if (typeof s.tracking === "boolean") tracking = s.tracking;
        }
      } catch (e) {}
    }
    load();

    return {
      version: "1.0",
      play: play,
      render: render,
      renderMix: renderMix,
      unlock: unlock,
      cues: function () { var k = []; for (var n in CUES) k.push(n); return k; },
      length: function (n) { return LENGTH[n] || 0; },

      get volume() { return volume; },
      setVolume: function (v) { volume = clamp01(v); save(); if (chain && ctx) chain.master.gain.setTargetAtTime(effectiveGain(), ctx.currentTime, 0.02); return volume; },
      get muted() { return muted; },
      setMuted: function (m) { muted = !!m; save(); return muted; },
      get followsSystem() { return tracking; },
      setFollowsSystem: function (t) { tracking = !!t; save(); return tracking; },

      /* The shell already reads the Windows mixer for the Sound panel; this
         is where it hands that reading over. */
      setSystemVolume: function (level, isMuted) {
        if (typeof level === "number") sysLevel = clamp01(level);
        if (typeof isMuted === "boolean") sysMuted = isMuted;
        if (chain && ctx) chain.master.gain.setTargetAtTime(effectiveGain(), ctx.currentTime, 0.05);
      },
      get effectiveGain() { return effectiveGain(); },
      get state() { return ctx ? ctx.state : "none"; },
      setLogger: function (fn) { if (typeof fn === "function") log = fn; }
    };
  })();

  /* ═══ ArcFeel — sound + haptics, driven off the shell's own return values ══

     One wrapper, on Nav.action. Every pad action, every keyboard action and
     every synthetic action from the host shim passes through that function
     and comes back with a plain-English string saying what happened
     ("moved left", "no target to the left", "tab=media"). Between that
     string and the change in Focus.depth() there is enough to tell every
     event in the palette apart without the shell having to announce
     anything — which is what keeps this file removable.                    */

  var ArcFeel = (function () {

    var HAPTIC_KEY = "arc.haptic.v1";

    var hapticOn = true;
    var hapticLevel = 0.55;      // default: on, but gentle
    var attached = false;
    var log = function () {};

    try {
      var s = JSON.parse(localStorage.getItem(HAPTIC_KEY) || "null");
      if (s && typeof s === "object") {
        if (typeof s.on === "boolean") hapticOn = s.on;
        if (typeof s.level === "number") hapticLevel = Math.max(0, Math.min(1, s.level));
      }
    } catch (e) {}

    function saveHaptic() {
      try { localStorage.setItem(HAPTIC_KEY, JSON.stringify({ on: hapticOn, level: hapticLevel })); }
      catch (e) {}
    }

    function post(o) {
      try {
        if (typeof global.arcPost === "function") { global.arcPost(o); return true; }
        if (global.chrome && global.chrome.webview && global.chrome.webview.postMessage) {
          global.chrome.webview.postMessage(JSON.stringify(o));
          return true;
        }
      } catch (e) {}
      return false;
    }

    function haptic(effect) {
      if (!hapticOn || hapticLevel <= 0 || !effect) return false;
      return post({ type: "haptic", effect: effect, intensity: hapticLevel });
    }

    /* The map from a cue to a feel. They are named the same on purpose:
       one event, one sound, one sensation, and no table to keep in sync. */
    var FEEL = {
      move: "move", nudge: "nudge", activate: "activate", launch: "launch",
      back: "back", push: "push", pop: "pop", tab: "tab",
      toggleOn: "toggle", toggleOff: "toggle", error: "error",
      boot: null, bootDone: "bootDone"
    };

    /* When the shell speaks for itself — ArcFeel.play("launch") at the actual
       launch site, ArcFeel.toggle() on a switch — the wrapper around
       Nav.action must not then add its own guess on the way out. A short
       claim window is enough: the two always happen inside the same turn. */
    var claimedAt = -1e9;
    function nowMs() { return (global.performance && performance.now) ? performance.now() : Date.now(); }

    function fire(cue, opt) {
      ArcSfx.play(cue, opt);
      if (FEEL[cue] !== undefined && FEEL[cue] !== null) haptic(FEEL[cue]);
    }
    function fireClaimed(cue, opt) { claimedAt = nowMs(); fire(cue, opt); }

    /* Where the focused thing physically is, 0..1 across the screen, turned
       into a pitch ratio. Horizontal position for horizontal moves, vertical
       for vertical: the pitch always travels with the eye. */
    function pitchOf(Focus, vertical) {
      try {
        var sc = Focus.top && Focus.top();
        var cu = sc && Focus.current && Focus.current(sc);
        var el = cu && cu.el;
        if (!el || !el.getBoundingClientRect) return 1;
        var r = el.getBoundingClientRect();
        var frac = vertical
          ? (r.top + r.height / 2) / Math.max(1, innerHeight)
          : (r.left + r.width / 2) / Math.max(1, innerWidth);
        if (!(frac >= 0 && frac <= 1)) return 1;
        /* Vertical is inverted: further down the screen is lower. Roughly a
           major second of travel end to end — enough to feel, not enough to
           sound like a melody. */
        var t = vertical ? (1 - frac) : frac;
        return 0.945 + t * 0.115;
      } catch (e) { return 1; }
    }

    var MOVES = { left: 1, right: 1, up: 1, down: 1 };
    var VERT = { up: 1, down: 1 };
    var OPEN = { cc: 1, guide: 1, square: 1, triangle: 1 };

    /* The shell's answers are prose, not codes — which is a gift, because the
       prose is written for a human and is therefore unambiguous. These are the
       shapes it actually returns, read off the shell's own source:

         moved left / no target to the left        Focus.move
         activated <label> / nothing focused       Focus.activate
         focused item is disabled / no action bound to <x>
         left <scope> / already at the shell root  Focus.back
         control centre opened / overlay closed    the overlay
         tab=play / tabs are a home-screen control
         osk: <key> / osk: ignored <key> (modal, not passed through)
         files: <a> / files: ignored <a>
         browser: <a> / browser: passed <a>

       Anything unrecognised is treated as "something happened" rather than as
       a failure. A false error cue is far worse than a missing one. */
    var REFUSED = /^(no target|no scope|nothing focused|no action bound|focused item is disabled|unknown action|tabs are|options is|already |release )/i;
    var FAILED  = /(failed|unsupported|refused|cannot |could not)/i;
    var MODAL   = /^(osk|files|browser):/i;
    var UNTAKEN = /\b(ignored|passed)\b/i;

    function judge(name, phase, result, d0, d1) {
      /* Releases are silent: the shell acts on press. Repeats are not — they
         genuinely move the cursor, and the rate limiter is what stops a held
         direction becoming a drone. */
      if (phase === "release") return;
      /* Something upstream already spoke for this action (a real app launch, a
         toggle). Do not add a second opinion. */
      if (nowMs() - claimedAt < 90) return;

      var r = String(result == null ? "" : result);

      if (FAILED.test(r)) { fire("error"); return; }

      /* The three modal routers answer before the shell's own table does, and
         they never touch the shell's focus stack — so they are classified by
         the action, not by the depth. */
      if (MODAL.test(r)) {
        if (UNTAKEN.test(r)) { fire("nudge"); return; }
        if (MOVES[name]) { fire("move", { pitch: pitchOf(FOCUS, !!VERT[name]) }); return; }
        if (name === "launch" || name === "select") { fire("activate"); return; }
        if (name === "back") { fire("back"); return; }
        /* An on-screen-keyboard key is a typing event: move weight, not an
           activation. Typing a password at activation weight is exhausting. */
        fire("move", { pitch: pitchOf(FOCUS, false), scale: 0.85 });
        return;
      }

      /* Depth next: it is the most reliable signal in the shell and it
         subsumes every opener and closer there is. */
      if (d1 > d0) { fire("push"); return; }
      if (d1 < d0) { fire("pop");  return; }

      if (MOVES[name]) {
        if (REFUSED.test(r)) fire("nudge");
        else fire("move", { pitch: pitchOf(FOCUS, !!VERT[name]) });
        return;
      }
      if (name === "launch" || name === "select") {
        fire(REFUSED.test(r) ? "nudge" : "activate");
        return;
      }
      if (name === "back") {
        fire(REFUSED.test(r) ? "nudge" : "back");
        return;
      }
      if (name === "tabPlay" || name === "tabMedia") {
        if (REFUSED.test(r)) fire("nudge");
        else fire("tab", { pitch: name === "tabMedia" ? 1.06 : 0.95 });
        return;
      }
      /* cc / guide / square / triangle all open something, so the depth check
         above has already spoken for them. Reaching here means nothing opened. */
      if (OPEN[name]) { fire("nudge"); return; }
      if (!REFUSED.test(r) && r) fire("move", { pitch: pitchOf(FOCUS, false), scale: 0.85 });
    }

    var FOCUS = null;

    function attach(Nav, Focus) {
      if (attached) return false;
      if (!Nav || typeof Nav.action !== "function") return false;
      FOCUS = Focus || null;
      var orig = Nav.action;
      Nav.action = function (name, phase, button) {
        var d0 = 0, d1 = 0;
        try { d0 = FOCUS && FOCUS.depth ? FOCUS.depth() : 0; } catch (e) {}
        var r = orig.call(Nav, name, phase, button);
        try {
          d1 = FOCUS && FOCUS.depth ? FOCUS.depth() : 0;
          judge(name, phase, r, d0, d1);
        } catch (e) { log("feel: judge threw: " + e); }
        return r;
      };
      attached = true;
      log("feel: attached to Nav.action (sfx " + ArcSfx.version
          + ", haptics " + (hapticOn ? Math.round(hapticLevel * 100) + "%" : "off") + ")");
      return true;
    }

    return {
      version: "1.0",
      attach: attach,
      /* Direct cues, for the events that are not pad actions. Each of these
         claims the current action, so the Nav.action wrapper stays quiet
         rather than adding a generic cue on top. */
      play: function (cue, opt) { fireClaimed(cue, opt); },
      boot: function () { fire("boot"); },
      bootDone: function () { fire("bootDone"); },
      error: function () { fireClaimed("error"); },
      toggle: function (on) { fireClaimed(on ? "toggleOn" : "toggleOff"); },
      haptic: haptic,

      get hapticEnabled() { return hapticOn; },
      setHapticEnabled: function (v) {
        hapticOn = !!v; saveHaptic();
        post({ type: "haptic", cmd: "intensity", value: hapticOn ? hapticLevel : 0 });
        return hapticOn;
      },
      get hapticLevel() { return hapticLevel; },
      setHapticLevel: function (v) {
        hapticLevel = Math.max(0, Math.min(1, +v || 0)); saveHaptic();
        post({ type: "haptic", cmd: "intensity", value: hapticOn ? hapticLevel : 0 });
        return hapticLevel;
      },
      testHaptic: function (effect) { return haptic(effect || "activate"); },
      setLogger: function (fn) { if (typeof fn === "function") { log = fn; ArcSfx.setLogger(fn); } }
    };
  })();

  global.ArcSfx = ArcSfx;
  global.ArcFeel = ArcFeel;

})(typeof window !== "undefined" ? window : this);
