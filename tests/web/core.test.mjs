// Enhetstest av bridge-core.js (ren logik utan DOM). Kör: node core.test.mjs
import { createRequire } from 'node:module';
import assert from 'node:assert/strict';
const C = createRequire(import.meta.url)('../../src/ScoreLite.App/Resources/Raw/wwwroot/bridge-core.js');
let n = 0; const t = (name, f) => { f(); n++; console.log('  OK   ' + name); };

t('findEntryIndex: binärsökning på playMs', () => {
  const e = [[0, 1, 0], [500, 2, 0], [500.5, 3, 0], [2000, 4, 1]];
  assert.equal(C.findEntryIndex(e, -1), -1); assert.equal(C.findEntryIndex(e, 0), 0); assert.equal(C.findEntryIndex(e, 499.9), 0);
  assert.equal(C.findEntryIndex(e, 500.2), 1); assert.equal(C.findEntryIndex(e, 1e9), 3); assert.equal(C.findEntryIndex([], 5), -1);
});
t('currentPlayMs: ankare + förfluten tid + bryggfördröjning (AL-12)', () => {
  assert.equal(C.currentPlayMs({ playMs: 1000, localAt: 50, bridgeDelayMs: 9 }, 250), 1209);
});
t('parseId: bara n/m + siffror, allt annat är null (id är data)', () => {
  assert.deepEqual(C.parseId('n418'), { kind: 'n', index: 418 }); assert.deepEqual(C.parseId('m0'), { kind: 'm', index: 0 });
  for (const bad of ['', 'x1', 'n', 'n-1', 'n1;alert(1)', "n1'", 'N1', null, undefined, 5, 'n12345678901']) assert.equal(C.parseId(bad), null, String(bad));
});
t('scaleFor: klampar till 0–4', () => {
  assert.equal(C.scaleFor(-3), C.ZOOM_SCALES[0]); assert.equal(C.scaleFor(99), C.ZOOM_SCALES[4]); assert.equal(C.scaleFor(2.9), C.ZOOM_SCALES[2]);
  assert.ok(C.ZOOM_SCALES.every((v, i, a) => i === 0 || v > a[i - 1]));
});
t('followScrollTop: systemet hamnar 30 % ned, klampat till sidan', () => {
  assert.equal(C.followScrollTop(1000, 800, 5000), 760); assert.equal(C.followScrollTop(100, 800, 5000), 0); assert.equal(C.followScrollTop(9000, 800, 2000), 2000);
});
t('nearestMeasure: träff i takten, annars närmaste i rätt system', () => {
  const m = [{ index: 0, left: 0, right: 100, top: 0, bottom: 50 }, { index: 1, left: 100, right: 200, top: 0, bottom: 50 }, { index: 2, left: 0, right: 100, top: 100, bottom: 150 }];
  assert.equal(C.nearestMeasure(m, 150, 20).index, 1); assert.equal(C.nearestMeasure(m, 50, 120).index, 2);
  assert.equal(C.nearestMeasure(m, 400, 20).index, 1); assert.equal(C.nearestMeasure(m, 50, 60).index, 0); assert.equal(C.nearestMeasure([], 1, 1), null);
});
console.log(`\n${n} tester gröna`);
