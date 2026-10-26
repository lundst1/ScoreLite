// Brygga mellan C# och Verovio (IF-5, AL-12, AL-13). Ingen data från filen tolkas som kod (S4): alla meddelanden är JSON.
(function () {
  'use strict';
  var Core = window.BridgeCore;
  var PROTOCOL = 1;

  var scoreEl = document.getElementById('score');
  var cursorEl = document.getElementById('cursor');
  var markerEl = document.getElementById('marker');

  var tk = null;                 // Verovio-toolkit
  var mei = null;                // senast laddade MEI (behålls för omlayout)
  var requestId = 0;
  var layoutGen = 0;
  var zoomLevel = 2;
  var systems = [];              // [{sys, y, h}]
  var itemById = Object.create(null);
  var timeline = { gen: -1, entries: [] };
  var anchor = { playMs: 0, localAt: 0, state: 'stopped', bridgeDelayMs: 0 };
  var followEnabled = true;
  var lastSys = -1;
  var lastProgrammaticScroll = 0, lastUserInput = 0;
  var markerAnchorId = null;
  var rafId = 0;

  // ------------------------------------------------------------ Transport

  function post(msg) {
    msg.v = PROTOCOL;
    var text = JSON.stringify(msg);
    if (window.__bridgeSend) window.__bridgeSend(text);                       // testsele
    else if (window.HybridWebView && window.HybridWebView.SendRawMessage) window.HybridWebView.SendRawMessage(text);
  }

  // C# -> JS: window.bridge.receive(jsonString). HybridWebView levererar samma sträng som en händelse.
  window.addEventListener('HybridWebViewMessageReceived', function (e) { receive(e.detail && e.detail.message); });

  function receive(json) {
    var m;
    try { m = JSON.parse(json); } catch (e) { return; }
    if (!m || typeof m.t !== 'string') return;
    try {
      switch (m.t) {
        case 'loadScore': onLoadScore(m); break;
        case 'setTimeline': onSetTimeline(m); break;
        case 'setClock': onSetClock(m); break;
        case 'setMarker': onSetMarker(m); break;
        case 'setFollow': followEnabled = !!m.enabled; break;
        case 'setZoom': onSetZoom(m); break;
        case 'ping': post({ t: 'pong', id: m.id | 0 }); break;
        default: console.warn('bridge: okänd typ ' + m.t);   // IF-5: okända t ignoreras och loggas
      }
    } catch (err) {
      post({ t: 'renderError', code: 'RENDER_FAILED', message: String(err && err.message || err) });
    }
  }
  window.bridge = { receive: receive };

  // ------------------------------------------------------------ Rendering (AL-13 steg 1, layoutReady)

  function viewportWidth() { return Math.max(200, document.documentElement.clientWidth || window.innerWidth || 400); }

  function applyTheme(theme) { document.body.setAttribute('data-theme', theme === 'dark' ? 'dark' : 'light'); }

  function render() {
    var scale = Core.scaleFor(zoomLevel);
    var width = viewportWidth();
    tk.setOptions({
      scale: scale,
      pageWidth: Math.floor(width * 100 / scale),   // SVG-bredd i pixlar = pageWidth * scale / 100
      pageHeight: 60000,
      adjustPageHeight: true,
      breaks: 'auto',
      header: 'none',
      footer: 'none',
      pageMarginLeft: 30, pageMarginRight: 30, pageMarginTop: 30, pageMarginBottom: 30,
    });
    if (!tk.loadData(mei)) throw new Error('Verovio kunde inte läsa MEI');
    var pages = tk.getPageCount();
    var html = [];
    for (var p = 1; p <= pages; p++) html.push(tk.renderToSVG(p));
    scoreEl.innerHTML = html.join('');   // SVG från Verovio, genererad ur modellen, inte rå filtext

    collectGeometry();
    if (markerAnchorId) placeMarker(markerAnchorId);
  }

  function collectGeometry() {
    var top = scoreEl.getBoundingClientRect();
    var ox = top.left + window.scrollX, oy = top.top + window.scrollY;
    systems = []; itemById = Object.create(null);
    var sysIndex = new Map();
    var sysEls = scoreEl.querySelectorAll('g.system');
    for (var i = 0; i < sysEls.length; i++) {
      var r = sysEls[i].getBoundingClientRect();
      systems.push({ sys: i, y: r.top + window.scrollY - oy, h: r.height });
      sysIndex.set(sysEls[i], i);
    }
    var items = [];
    var els = scoreEl.querySelectorAll('g.note, g.measure');
    for (var k = 0; k < els.length; k++) {
      var el = els[k];
      var id = Core.parseId(el.id);
      if (!id) continue;
      var rect = el.getBoundingClientRect();
      var sysEl = el.closest('g.system');
      var sys = sysEl ? sysIndex.get(sysEl) : 0;
      var measure;
      if (id.kind === 'm') measure = id.index;
      else {
        var mEl = el.closest('g.measure');
        var mid = mEl ? Core.parseId(mEl.id) : null;
        measure = mid ? mid.index : 0;
      }
      var x = rect.left + window.scrollX - ox;
      items.push([el.id, measure, round1(x), sys]);
      itemById[el.id] = { x: x, sys: sys, measure: measure };
    }
    layoutGen++;
    post({ t: 'layoutReady', requestId: requestId, layoutGen: layoutGen,
           systems: systems.map(function (s) { return { sys: s.sys, y: round1(s.y), h: round1(s.h) }; }), items: items });
  }

  function round1(v) { return Math.round(v * 10) / 10; }

  function onLoadScore(m) {
    requestId = m.requestId | 0;
    zoomLevel = Math.max(0, Math.min(4, m.zoomLevel | 0));
    applyTheme(m.theme);
    mei = String(m.mei);
    markerAnchorId = null; markerEl.hidden = true; cursorEl.hidden = true;
    render();
  }

  function onSetZoom(m) {
    zoomLevel = Math.max(0, Math.min(4, m.zoomLevel | 0));
    if (mei) render();   // omlayout -> ny layoutReady -> ny tidslinje (§7.5 steg 6)
  }

  var resizeTimer = 0, lastWidth = 0;
  window.addEventListener('resize', function () {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(function () {
      var w = viewportWidth();
      if (mei && Math.abs(w - lastWidth) > 4) { lastWidth = w; try { render(); } catch (e) { reportError(e); } }
    }, 150);
  });

  function reportError(err) { post({ t: 'renderError', code: 'RENDER_FAILED', message: String(err && err.message || err) }); }

  // ------------------------------------------------------------ Tidslinje, klocka och pekare (AL-12)

  function onSetTimeline(m) {
    timeline = { gen: m.layoutGen | 0, entries: Array.isArray(m.entries) ? m.entries : [] };
    updateCursor(true);   // regel 7: fortsätt från samma t
  }

  function onSetClock(m) {
    anchor = { playMs: +m.playMs || 0, localAt: performance.now(), state: String(m.state), bridgeDelayMs: +m.bridgeDelayMs || 0 };
    if (anchor.state === 'playing') { startLoop(); }
    else { stopLoop(); updateCursor(true); }
  }

  function startLoop() { if (!rafId) rafId = requestAnimationFrame(tick); }
  function stopLoop() { if (rafId) { cancelAnimationFrame(rafId); rafId = 0; } }

  function tick() {
    rafId = 0;
    if (anchor.state !== 'playing') return;
    updateCursor(false);
    rafId = requestAnimationFrame(tick);
  }

  function updateCursor(force) {
    // Regel 3: bara 'playing' och 'paused' visar pekaren. Annars gäller startmarkören.
    if (anchor.state !== 'playing' && anchor.state !== 'paused') { cursorEl.hidden = true; return; }
    var t = anchor.state === 'playing' ? Core.currentPlayMs(anchor, performance.now()) : anchor.playMs;
    var i = Core.findEntryIndex(timeline.entries, t);
    if (i < 0) { if (timeline.entries.length === 0) { cursorEl.hidden = true; return; } i = 0; }
    var e = timeline.entries[i];
    var s = systems[e[2] | 0];
    if (!s) { cursorEl.hidden = true; return; }
    cursorEl.hidden = false;
    cursorEl.style.left = e[1] + 'px';
    cursorEl.style.top = s.y + 'px';
    cursorEl.style.height = s.h + 'px';
    if (e[2] !== lastSys || force) {
      var changed = e[2] !== lastSys;
      lastSys = e[2];
      if (followEnabled && (changed || force) && anchor.state === 'playing') followTo(s);
    }
  }

  function followTo(s) {
    var wrapTop = scoreEl.getBoundingClientRect().top + window.scrollY;
    var max = document.documentElement.scrollHeight - window.innerHeight;
    var target = Core.followScrollTop(wrapTop + s.y, window.innerHeight, max);
    lastProgrammaticScroll = performance.now();
    window.scrollTo({ top: target, behavior: 'smooth' });
  }

  // Regel 6: egen scroll av användaren stänger av följ-läget.
  ['touchstart', 'touchmove', 'wheel', 'pointerdown', 'keydown'].forEach(function (n) {
    window.addEventListener(n, function () { lastUserInput = performance.now(); }, { passive: true });
  });
  window.addEventListener('scroll', function () {
    var now = performance.now();
    if (followEnabled && now - lastUserInput < 400 && now - lastProgrammaticScroll > 700) {
      followEnabled = false;
      post({ t: 'followChanged', enabled: false });
    }
  }, { passive: true });

  // ------------------------------------------------------------ Startmarkör

  function onSetMarker(m) {
    markerAnchorId = String(m.anchorId);
    placeMarker(markerAnchorId);
  }

  function placeMarker(id) {
    var it = itemById[id];
    if (!it) { markerEl.hidden = true; return; }
    var s = systems[it.sys];
    if (!s) { markerEl.hidden = true; return; }
    markerEl.hidden = false;
    markerEl.style.left = it.x + 'px';
    markerEl.style.top = s.y + 'px';
    markerEl.style.height = s.h + 'px';
  }

  // ------------------------------------------------------------ Tryck (AL-13)

  scoreEl.addEventListener('click', function (ev) {
    var top = scoreEl.getBoundingClientRect();
    var x = ev.clientX - top.left;
    var noteEl = ev.target.closest ? ev.target.closest('g.note') : null;
    var measureEl = ev.target.closest ? ev.target.closest('g.measure') : null;
    var nid = noteEl ? Core.parseId(noteEl.id) : null;
    var mid = measureEl ? Core.parseId(measureEl.id) : null;

    if (nid && nid.kind === 'n') {
      post({ t: 'tap', id: noteEl.id, measure: mid ? mid.index : 0, x: round1(x) });
      return;
    }
    if (mid && mid.kind === 'm') {
      post({ t: 'tap', id: measureEl.id, measure: mid.index, x: round1(x) });
      return;
    }
    // Reserv: närmaste takt på x och system.
    var ms = [];
    scoreEl.querySelectorAll('g.measure').forEach(function (el) {
      var id = Core.parseId(el.id);
      if (!id) return;
      var r = el.getBoundingClientRect();
      ms.push({ index: id.index, left: r.left, right: r.right, top: r.top, bottom: r.bottom });
    });
    var best = Core.nearestMeasure(ms, ev.clientX, ev.clientY);
    if (best) post({ t: 'tap', id: null, measure: best.index, x: round1(x) });
  });

  // ------------------------------------------------------------ Start

  // WASM-modulen initieras asynkront och Verovio 6 anropar inte alltid onRuntimeInitialized efter att skriptet laddats.
  // Konstruktorn kastar tills modulen är klar, så vi provar tills den lyckas (högst cirka 20 s).
  function init(attempt) {
    if (tk) return;
    try {
      tk = new verovio.toolkit();
    } catch (e) {
      if (attempt < 400) { setTimeout(function () { init(attempt + 1); }, 50); return; }
      reportError(new Error('Verovio startade inte: ' + (e && e.message || e)));
      return;
    }
    lastWidth = viewportWidth();
    post({ t: 'ready', verovioVersion: tk.getVersion() });
  }

  if (typeof verovio === 'undefined') { reportError(new Error('Verovio saknas')); }
  else init(0);
})();
