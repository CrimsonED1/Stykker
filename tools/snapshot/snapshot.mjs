// Usage: node snapshot.mjs <data-dir> <out.png> [iso|end]
// Serves the repository root, opens view.html in headless Chromium and saves a screenshot.
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const root = resolve(fileURLToPath(new URL('../..', import.meta.url)));
const dataDir = process.argv[2] ?? 'snapshot-out';
const out = resolve(process.argv[3] ?? 'snapshot.png');
const view = process.argv[4] ?? 'iso';
const types = { '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript', '.json': 'application/json' };

const server = createServer(async (req, res) => {
  try {
    const path = normalize(decodeURIComponent(new URL(req.url, 'http://x').pathname));
    const file = path.startsWith('/__data/') ? join(resolve(dataDir), path.slice(8)) : join(root, path);
    const body = await readFile(file);
    res.writeHead(200, { 'content-type': types[extname(file)] ?? 'application/octet-stream' });
    res.end(body);
  } catch {
    res.writeHead(404);
    res.end();
  }
}).listen(0);
const port = server.address().port;

const browser = await chromium.launch({ args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('console', (m) => console.log('[page]', m.text()));
page.on('pageerror', (e) => console.error('[page error]', e.message));
await page.goto(`http://localhost:${port}/tools/snapshot/view.html?data=/__data&view=${view}`);
await page.waitForFunction(() => window.snapshotReady === true, null, { timeout: 120000 });
await page.screenshot({ path: out });
await browser.close();
server.close();
console.log(`saved ${out}`);
