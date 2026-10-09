// Usage: node animate.mjs <frames-dir> <out.gif> [width] [height] [delayMs] [step]
// Renders the frame sequence written by the snapshot sample (anim-*) with three.js and encodes an animated GIF.
import { createServer } from 'node:http';
import { readFile, writeFile } from 'node:fs/promises';
import { extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
import gifenc from 'gifenc';
const { GIFEncoder, quantize, applyPalette } = gifenc;

const root = resolve(fileURLToPath(new URL('../..', import.meta.url)));
const dataDir = resolve(process.argv[2]);
const out = resolve(process.argv[3] ?? 'animation.gif');
const width = Number(process.argv[4] ?? 720), height = Number(process.argv[5] ?? 450);
const delay = Number(process.argv[6] ?? 90), step = Number(process.argv[7] ?? 1);
const types = { '.html': 'text/html', '.js': 'text/javascript', '.json': 'application/json' };

const server = createServer(async (req, res) => {
  try {
    const path = normalize(decodeURIComponent(new URL(req.url, 'http://x').pathname));
    const file = path.startsWith('/__data/') ? join(dataDir, path.slice(8)) : join(root, path);
    res.writeHead(200, { 'content-type': types[extname(file)] ?? 'application/octet-stream' });
    res.end(await readFile(file));
  } catch {
    res.writeHead(404);
    res.end();
  }
}).listen(0);

const browser = await chromium.launch({ args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const page = await browser.newPage({ viewport: { width, height } });
page.on('pageerror', (e) => console.error('[page error]', e.message));
await page.goto(`http://localhost:${server.address().port}/tools/snapshot/anim.html`);
await page.waitForFunction(() => window.ready === true, null, { timeout: 120000 });
const count = await page.evaluate(() => window.frameCount);

const gif = GIFEncoder();
for (let i = 0; i < count; i += step) {
  const b64 = await page.evaluate((k) => window.renderFrame(k), i);
  const rgba = new Uint8Array(Buffer.from(b64, 'base64'));
  const palette = quantize(rgba, 256);
  const index = applyPalette(rgba, palette);
  const last = i + step >= count;
  gif.writeFrame(index, width, height, { palette, delay: last ? 2500 : delay });
  process.stdout.write(`\rframe ${i + 1}/${count}`);
}
gif.finish();
await writeFile(out, gif.bytes());
await browser.close();
server.close();
console.log(`\nsaved ${out} (${(gif.bytes().length / 1e6).toFixed(1)} MB)`);
