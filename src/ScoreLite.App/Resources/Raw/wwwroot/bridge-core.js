// Ren logik utan DOM för bridge.js (AL-12, AL-13). Testas i Node (tests/web).
(function (root, factory) {
  if (typeof module === 'object' && module.exports) module.exports = factory();
  else root.BridgeCore = factory();
})(typeof self !== 'undefined' ? self : this, function () {
  'use strict';

  // Zoomnivå 0–4 till Verovios `scale`. Mätt så att notlinjeavståndet blir ungefär 4,5 / 6 / 8 / 10,5 / 14 CSS-pixlar.
  var ZOOM_SCALES = [28, 38, 52, 68, 90];

  function scaleFor(level) {
    var l = Math.max(0, Math.min(4, level | 0));
    return ZOOM_SCALES[l];
  }

  /** Index för sista posten med entries[i][0] <= t (binärsökning), eller -1 om t ligger före första posten. */
  function findEntryIndex(entries, t) {
    var lo = 0, hi = entries.length - 1, ans = -1;
    while (lo <= hi) {
      var mid = (lo + hi) >> 1;
      if (entries[mid][0] <= t) { ans = mid; lo = mid + 1; } else hi = mid - 1;
    }
    return ans;
  }

  /** AL-12 regel 2: t = anchor.playMs + (nu - anchor.localAt) + anchor.bridgeDelayMs. */
  function currentPlayMs(anchor, nowMs) {
    return anchor.playMs + (nowMs - anchor.localAt) + anchor.bridgeDelayMs;
  }

  /** Id från notbilden: 'n123' -> {kind:'n', index:123}, 'm4' -> {kind:'m', index:4}, annars null. Id behandlas som data. */
  function parseId(id) {
    if (typeof id !== 'string') return null;
    var m = /^([nm])(\d{1,9})$/.exec(id);
    return m ? { kind: m[1], index: parseInt(m[2], 10) } : null;
  }

  /** AL-12 regel 6: systemets överkant hamnar cirka 30 % ned från vyns topp. */
  function followScrollTop(systemTop, viewportHeight, maxScroll) {
    var target = systemTop - 0.3 * viewportHeight;
    return Math.max(0, Math.min(maxScroll > 0 ? maxScroll : target, target));
  }

  /**
   * AL-13 steg 1, reserv: närmaste takt när trycket inte träffade ett element med giltigt id.
   * measures: [{index, left, right, top, bottom}] i sidkoordinater. Först den som innehåller punkten, annars närmast.
   */
  function nearestMeasure(measures, x, y) {
    var best = null, bestD = Infinity;
    for (var i = 0; i < measures.length; i++) {
      var m = measures[i];
      var dx = x < m.left ? m.left - x : (x > m.right ? x - m.right : 0);
      var dy = y < m.top ? m.top - y : (y > m.bottom ? y - m.bottom : 0);
      var d = dy * 1000 + dx;   // rätt system väger tyngre än avstånd i sidled
      if (d < bestD) { bestD = d; best = m; }
    }
    return best;
  }

  return { ZOOM_SCALES: ZOOM_SCALES, scaleFor: scaleFor, findEntryIndex: findEntryIndex, currentPlayMs: currentPlayMs,
           parseId: parseId, followScrollTop: followScrollTop, nearestMeasure: nearestMeasure };
});
