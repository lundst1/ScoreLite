// Verovio-spik (V10): renderar MEI från MeiExporter och kontrollerar att xml:id bevaras.
// Användning: node run.mjs <mapp med .mei och .json>   (Verovio hämtas med `npm install` i denna mapp)
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const createVerovioModule = require('verovio/wasm').default ?? require('verovio/wasm');
const { VerovioToolkit } = require('verovio/esm');

const dir = process.argv[2];
const VerovioModule = await createVerovioModule();
const tk = new VerovioToolkit(VerovioModule);
console.log('Verovio', tk.getVersion());
let failed = 0;
for (const f of fs.readdirSync(dir).filter(x => x.endsWith('.mei')).sort()) {
  const mei = fs.readFileSync(path.join(dir, f), 'utf8');
  const expect = JSON.parse(fs.readFileSync(path.join(dir, f.replace('.mei', '.json')), 'utf8'));
  tk.setOptions({ pageWidth: 1200, scale: 40, breaks: 'auto', adjustPageHeight: true, svgViewBox: true });
  const t0 = performance.now();
  const ok = tk.loadData(mei);
  const pages = ok ? tk.getPageCount() : 0;
  let svgNotes = 0, svgMeasures = 0, ids = new Set();
  for (let p = 1; p <= pages; p++) {
    const svg = tk.renderToSVG(p);
    for (const m of svg.matchAll(/<g id="(n\d+)" class="note/g)) { svgNotes++; ids.add(m[1]); }
    for (const m of svg.matchAll(/<g id="(m\d+)" class="measure/g)) { svgMeasures++; }
  }
  const ms = Math.round(performance.now() - t0);
  const good = ok && svgNotes === expect.notes && svgMeasures === expect.measures;
  if (!good) failed++;
  console.log(`${good ? 'OK  ' : 'FAIL'} ${f}: laddad=${ok} sidor=${pages} noter ${svgNotes}/${expect.notes} takter ${svgMeasures}/${expect.measures} (${ms} ms)`);
}
process.exit(failed ? 1 : 0);
