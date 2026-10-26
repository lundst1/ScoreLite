// T5/T6: kör appens riktiga index.html + bridge.js + Verovio i en riktig webbläsare (Chrome/Edge, headless) med MEI från korpusen.
// Användning: node test.mjs <mapp med .mei från tests/verovio-t6/MeiDump>
// HybridWebView ersätts av en testkrok (window.__bridgeSend). Själva WebView på enhet testas i M0.
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import puppeteer from 'puppeteer-core';

const meiDir = process.argv[2];
const root = path.resolve('../../src/ScoreLite.App/Resources/Raw/wwwroot');
const browserPath = [
  'C:/Program Files/Google/Chrome/Application/chrome.exe',
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
].find(p => fs.existsSync(p));

const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css' };
const server = http.createServer((req, res) => {
  let p = decodeURIComponent(req.url.split('?')[0]);
  if (p === '/') p = '/index.html';
  if (p === '/favicon.ico') { res.writeHead(204); res.end(); return; }
  if (p.startsWith('/_framework/')) { res.writeHead(200, { 'Content-Type': 'text/javascript' }); res.end('/* HybridWebView.js ersätts av testkrok */'); return; }
  const f = path.join(root, p);
  if (!f.startsWith(root) || !fs.existsSync(f)) { res.writeHead(404); res.end(); return; }
  // Samma CSP som i index.html ska gälla även för filerna; sidan sätter den själv via <meta>.
  res.writeHead(200, { 'Content-Type': types[path.extname(f)] || 'application/octet-stream' });
  fs.createReadStream(f).pipe(res);
});
await new Promise(r => server.listen(0, r));
const url = `http://localhost:${server.address().port}/`;

let failed = 0, total = 0;
function check(name, cond, extra = '') {
  total++;
  if (cond) console.log('  OK   ' + name);
  else { failed++; console.log('  FAIL ' + name + (extra ? '\n         ' + extra : '')); }
}

const browser = await puppeteer.launch({ executablePath: browserPath, headless: true, args: ['--no-sandbox'] });

async function openPage(width = 420, height = 800) {
  const page = await browser.newPage();
  await page.setViewport({ width, height, deviceScaleFactor: 1 });
  const sent = [];
  const problems = [];
  await page.exposeFunction('__hostReceive', text => sent.push(JSON.parse(text)));
  await page.evaluateOnNewDocument(() => { window.__bridgeSend = t => window.__hostReceive(t); });
  page.on('console', m => { if (m.type() === 'error') problems.push('console: ' + m.text()); });
  page.on('pageerror', e => problems.push('pageerror: ' + e.message));
  await page.goto(url);
  const send = obj => page.evaluate(j => window.bridge.receive(j), JSON.stringify({ v: 1, ...obj }));
  const waitFor = async (pred, what, ms = 8000) => {
    const t0 = Date.now();
    while (Date.now() - t0 < ms) { const m = sent.find(pred); if (m) return m; await new Promise(r => setTimeout(r, 25)); }
    throw new Error('timeout: ' + what);
  };
  return { page, sent, problems, send, waitFor };
}

try {
  const meiFiles = fs.readdirSync(meiDir).filter(f => f.endsWith('.mei')).sort();
  const nocturne = fs.readFileSync(path.join(meiDir, meiFiles.find(f => f.startsWith('Nocturne'))), 'utf8');
  const nocturneInfo = JSON.parse(fs.readFileSync(path.join(meiDir, 'Nocturne.json'), 'utf8'));

  // ------------------------------------------------------------ Start och CSP
  {
    const t = await openPage();
    const ready = await t.waitFor(m => m.t === 'ready', 'ready');
    check('ready skickas med Verovio-version', /^\d+\.\d+/.test(ready.verovioVersion), ready.verovioVersion);
    check('protokollversion 1 på alla meddelanden', t.sent.every(m => m.v === 1));
    check('ingen CSP- eller skriptfel vid start', t.problems.length === 0, t.problems.join('\n'));
    await t.page.close();
  }

  // ------------------------------------------------------------ Rendering och layoutReady
  const t = await openPage();
  await t.waitFor(m => m.t === 'ready', 'ready');
  await t.send({ t: 'loadScore', requestId: 7, mei: nocturne, viewportWidthPx: 420, zoomLevel: 2, theme: 'light' });
  const layout = await t.waitFor(m => m.t === 'layoutReady', 'layoutReady');
  const notes = layout.items.filter(i => i[0][0] === 'n'), measures = layout.items.filter(i => i[0][0] === 'm');
  check('layoutReady bär requestId och layoutGen', layout.requestId === 7 && layout.layoutGen >= 1);
  check('en post per not och takt (T6: inga saknade id)', notes.length === nocturneInfo.notes && measures.length === nocturneInfo.measures,
        `noter ${notes.length}/${nocturneInfo.notes}, takter ${measures.length}/${nocturneInfo.measures}`);
  check('system har y och h', layout.systems.length > 1 && layout.systems.every(s => s.h > 20 && s.y >= 0));
  check('varje post har geometri (x ≥ 0, giltigt system)', layout.items.every(i => i[2] >= 0 && i[3] >= 0 && i[3] < layout.systems.length));
  check('noternas takt-index är giltiga', notes.every(i => i[1] >= 0 && i[1] < nocturneInfo.measures));
  check('SVG-bredden följer vyn', await t.page.evaluate(() => Math.abs(document.querySelector('#score svg').getBoundingClientRect().width - document.documentElement.clientWidth) < 3));
  check('ingen CSP- eller skriptfel under rendering', t.problems.length === 0, t.problems.join('\n'));

  // Notlinjeavstånd i pixlar per zoomnivå (kalibrerar BridgeCore.ZOOM_SCALES).
  const interline = [];
  for (let z = 0; z <= 4; z++) {
    t.sent.length = 0;
    await t.send({ t: 'setZoom', zoomLevel: z });
    await t.waitFor(m => m.t === 'layoutReady', 'layoutReady z' + z);
    interline.push(await t.page.evaluate(() => {
      const l = document.querySelectorAll('g.staff path')[0]; const staff = l.closest('g.staff').querySelectorAll('path');
      const ys = [...staff].slice(0, 5).map(p => p.getBoundingClientRect().top); return (ys[4] - ys[0]) / 4;
    }));
  }
  console.log('      notlinjeavstånd (px) per zoomnivå: ' + interline.map(v => v.toFixed(1)).join(', '));
  check('zoom växer monotont', interline.every((v, i) => i === 0 || v > interline[i - 1]), interline.join(','));

  // ------------------------------------------------------------ Tryck (AL-13)
  t.sent.length = 0;
  await t.send({ t: 'setZoom', zoomLevel: 2 });
  const layout2 = await t.waitFor(m => m.t === 'layoutReady', 'layoutReady');
  const noteId = layout2.items.find(i => i[0] === 'n20')[0];
    await t.page.evaluate(id => window.scrollTo(0, document.getElementById(id).getBoundingClientRect().top + window.scrollY - 200), noteId);
  const pos2 = await t.page.evaluate(id => { const r = document.querySelector('#' + id + ' .notehead').getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; }, noteId);
  t.sent.length = 0;
  await t.page.mouse.click(pos2.x, pos2.y);
  const tap = await t.waitFor(m => m.t === 'tap', 'tap på not');
  check('tryck på not ger tap med not-id', tap.id === noteId, JSON.stringify(tap));
  check('tap bär takt och x', Number.isInteger(tap.measure) && tap.x > 0);

  t.sent.length = 0;
  const mPos = await t.page.evaluate(() => {
    const m = document.querySelector('g.measure'); const r = m.getBoundingClientRect();
    return { id: m.id, x: r.right - 2, y: r.top + 4 };
  });
  await t.page.evaluate(() => window.scrollTo(0, 0));
  const mPos2 = await t.page.evaluate(id => { const r = document.getElementById(id).getBoundingClientRect(); return { x: r.right - 3, y: r.top + 3 }; }, mPos.id);
  await t.page.mouse.click(mPos2.x, mPos2.y);
  const tap2 = await t.waitFor(m => m.t === 'tap', 'tap på takt');
  check('tryck utanför noter ger tap med takt-id eller null och giltig takt', (tap2.id === null || /^[nm]\d+$/.test(tap2.id)) && tap2.measure >= 0, JSON.stringify(tap2));

  // ------------------------------------------------------------ Startmarkör
  await t.page.evaluate(() => window.scrollTo(0, 0));
  await t.send({ t: 'setMarker', anchorId: 'n3', measure: 0, tickInMeasure: 0 });
  const mk = await t.page.evaluate(() => { const m = document.getElementById('marker'); const n = document.getElementById('n3').getBoundingClientRect(); return { hidden: m.hidden, left: parseFloat(m.style.left), noteLeft: n.left }; });
  check('startmarkören visas vid ankaret', !mk.hidden && Math.abs(mk.left - mk.noteLeft) < 3, JSON.stringify(mk));
  await t.send({ t: 'setMarker', anchorId: "'); alert(1); //", measure: 0, tickInMeasure: 0 });
  await t.send({ t: 'setMarker', anchorId: 'n99999999', measure: 0, tickInMeasure: 0 });
  check('ogiltiga ankare-id döljer markören utan fel (S4)', await t.page.evaluate(() => document.getElementById('marker').hidden) && t.problems.length === 0, t.problems.join('\n'));
  await t.send({ t: 'setMarker', anchorId: 'm2', measure: 2, tickInMeasure: 0 });
  check('takt-ankare fungerar', !(await t.page.evaluate(() => document.getElementById('marker').hidden)));

  // ------------------------------------------------------------ Pekare (AL-12)
  const sysOfNote = id => layout2.items.find(i => i[0] === id);
  const a = sysOfNote('n0'), b = sysOfNote('n10');
  const entries = [[0, a[2], a[3]], [1000, b[2], b[3]], [60000, b[2], b[3]]];
  await t.send({ t: 'setTimeline', layoutGen: layout2.layoutGen, entries });
  await t.send({ t: 'setClock', epoch: 1, state: 'stopped', playMs: 0, bridgeDelayMs: 0 });
  check('stopped: pekaren är dold', await t.page.evaluate(() => document.getElementById('cursor').hidden));
  await t.send({ t: 'setClock', epoch: 1, state: 'countingIn', playMs: 0, bridgeDelayMs: 0 });
  check('countingIn: pekaren är dold', await t.page.evaluate(() => document.getElementById('cursor').hidden));
  await t.send({ t: 'setClock', epoch: 2, state: 'playing', playMs: 0, bridgeDelayMs: 0 });
  await new Promise(r => setTimeout(r, 200));
  let cur = await t.page.evaluate(() => { const c = document.getElementById('cursor'); return { hidden: c.hidden, left: parseFloat(c.style.left) }; });
  check('playing: pekaren på första posten vid t≈0', !cur.hidden && Math.abs(cur.left - a[2]) < 2, JSON.stringify(cur));
  await new Promise(r => setTimeout(r, 1100));
  cur = await t.page.evaluate(() => parseFloat(document.getElementById('cursor').style.left));
  check('playing: pekaren går vidare till nästa post efter 1 s', Math.abs(cur - b[2]) < 2, `${cur} mot ${b[2]}`);
  await t.send({ t: 'setClock', epoch: 3, state: 'playing', playMs: 0, bridgeDelayMs: 1500 });
  await new Promise(r => setTimeout(r, 120));
  cur = await t.page.evaluate(() => parseFloat(document.getElementById('cursor').style.left));
  check('bridgeDelayMs läggs till tiden (AL-12 regel 2)', Math.abs(cur - b[2]) < 2, `${cur} mot ${b[2]}`);
  await t.send({ t: 'setClock', epoch: 4, state: 'paused', playMs: 0, bridgeDelayMs: 0 });
  await new Promise(r => setTimeout(r, 1200));
  cur = await t.page.evaluate(() => parseFloat(document.getElementById('cursor').style.left));
  check('paused: pekaren står kvar på ankarets post', Math.abs(cur - a[2]) < 2, `${cur} mot ${a[2]}`);
  await t.send({ t: 'setClock', epoch: 5, state: 'stopped', playMs: 0, bridgeDelayMs: 0 });

  // ------------------------------------------------------------ Följ-läge
  const farNote = layout2.items.filter(i => i[0][0] === 'n').at(-3);
  const far = [[0, a[2], a[3]], [500, farNote[2], farNote[3]]];
  await t.page.evaluate(() => window.scrollTo(0, 0));
  await t.send({ t: 'setFollow', enabled: true });
  await t.send({ t: 'setTimeline', layoutGen: layout2.layoutGen, entries: far });
  await t.send({ t: 'setClock', epoch: 6, state: 'playing', playMs: 0, bridgeDelayMs: 0 });
  await new Promise(r => setTimeout(r, 1500));
  const scrolled = await t.page.evaluate(() => window.scrollY);
  check('följ-läge scrollar till systemet med pekaren', scrolled > 100, 'scrollY=' + scrolled);
  t.sent.length = 0;
  await t.page.mouse.move(200, 400);
  await t.page.mouse.wheel({ deltaY: -300 });
  await new Promise(r => setTimeout(r, 400));
  await t.page.mouse.wheel({ deltaY: 200 });
  const fc = await t.waitFor(m => m.t === 'followChanged', 'followChanged', 3000).catch(() => null);
  check('egen scroll stänger av följ-läget och skickar followChanged(false)', fc && fc.enabled === false, JSON.stringify(t.sent));
  await t.send({ t: 'setClock', epoch: 7, state: 'stopped', playMs: 0, bridgeDelayMs: 0 });

  // ------------------------------------------------------------ Ping, okänd typ, trasig indata, tema, rotation
  t.sent.length = 0;
  await t.send({ t: 'ping', id: 42 });
  const pong = await t.waitFor(m => m.t === 'pong', 'pong');
  check('ping besvaras med pong och samma id', pong.id === 42);
  await t.send({ t: 'finnsInte' });
  await t.page.evaluate(() => window.bridge.receive('inte json'));
  await t.page.evaluate(() => window.bridge.receive('{"t":5}'));
  check('okända och trasiga meddelanden ignoreras utan fel', t.problems.filter(p => p.startsWith('pageerror')).length === 0, t.problems.join('\n'));
  await t.send({ t: 'loadScore', requestId: 8, mei: nocturne, viewportWidthPx: 420, zoomLevel: 2, theme: 'dark' });
  await t.waitFor(m => m.t === 'layoutReady' && m.requestId === 8, 'layoutReady dark');
  check('mörkt tema sätts som attribut', await t.page.evaluate(() => document.body.dataset.theme === 'dark'));
  // decisions #30: notbilden är alltid svart på vitt, även i mörkt tema.
  const dark = await t.page.evaluate(() => {
    const cs = getComputedStyle(document.body);
    const svg = document.querySelector('#score svg');
    return { bg: cs.backgroundColor, ink: svg ? getComputedStyle(svg).color : null, scheme: getComputedStyle(document.documentElement).colorScheme };
  });
  check('mörkt tema: vit bakgrund', dark.bg === 'rgb(255, 255, 255)', dark.bg);
  check('mörkt tema: svart bläck', dark.ink === 'rgb(0, 0, 0)', dark.ink);
  check('mörkt tema: color-scheme only light', /only/.test(dark.scheme) && /light/.test(dark.scheme), dark.scheme);
  t.sent.length = 0;
  await t.page.setViewport({ width: 820, height: 420 });
  const re = await t.waitFor(m => m.t === 'layoutReady', 'omlayout vid rotation', 4000).catch(() => null);
  check('rotation ger ny layoutReady med färre system (bredare vy)', re && re.systems.length < layout2.systems.length, re ? `${re.systems.length} mot ${layout2.systems.length}` : 'ingen layoutReady');
  await t.page.close();

  // ------------------------------------------------------------ Hela korpusen
  for (const f of meiFiles) {
    const info = JSON.parse(fs.readFileSync(path.join(meiDir, f.replace('.mei', '.json')), 'utf8'));
    const p = await openPage(420, 800);
    await p.waitFor(m => m.t === 'ready', 'ready');
    const t0 = Date.now();
    await p.send({ t: 'loadScore', requestId: 1, mei: fs.readFileSync(path.join(meiDir, f), 'utf8'), viewportWidthPx: 420, zoomLevel: 2, theme: 'light' });
    const lr = await p.waitFor(m => m.t === 'layoutReady', 'layoutReady ' + f, 20000);
    const ms = Date.now() - t0;
    const n = lr.items.filter(i => i[0][0] === 'n').length, mm = lr.items.filter(i => i[0][0] === 'm').length;
    check(`Korpus ${f}: alla ${info.notes} noter och ${info.measures} takter har geometri (${ms} ms, ${lr.systems.length} system)`, n === info.notes && mm === info.measures, `noter ${n}, takter ${mm}`);
    check(`Korpus ${f}: inga fel`, p.problems.length === 0 && !p.sent.some(m => m.t === 'renderError'), p.problems.join('\n'));
    await p.page.close();
  }
} catch (e) {
  failed++; console.log('  FAIL oväntat fel: ' + e.stack);
} finally {
  await browser.close();
  server.close();
}
console.log(`\n${total - failed}/${total} kontroller gröna`);
process.exit(failed ? 1 : 0);
