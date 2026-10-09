// Usage: node demo-gif.mjs <demo-url> <page> <compute-button-text> <out.gif> [frames] [width] [engine] [babylon.js path]
// Opens a page of the published demo, runs its computation, scrubs through the stored playback and encodes
// the 3D view as an animated GIF. The demo must be served (e.g. python3 -m http.server in publish/wwwroot).
import { readFile, writeFile } from 'node:fs/promises';
import { chromium } from 'playwright';
import gifenc from 'gifenc';
const { GIFEncoder, quantize, applyPalette } = gifenc;

const [url, href, button, out] = process.argv.slice(2, 6);
const frames = Number(process.argv[6] ?? 60), width = Number(process.argv[7] ?? 640);
const engine = process.argv[8] ?? 'three', babylonFile = process.argv[9];

const browser = await chromium.launch({ args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.on('pageerror', (e) => console.error('[page error]', e.message));
if (babylonFile)
  await page.route('https://cdn.babylonjs.com/babylon.js', async (r) => r.fulfill({ contentType: 'text/javascript', body: await readFile(babylonFile) }));
await page.goto(url);
await page.waitForSelector(`a[href="${href}"]`, { timeout: 120000 });
await page.click(`a[href="${href}"]`);
if (engine === 'babylon') { await page.click('.engine button:has-text("Babylon.js")'); await page.waitForTimeout(4000); }
await page.click(`button:has-text("${button}")`);
await page.waitForSelector(`button:has-text("${button}"):not([disabled])`, { timeout: 900000 });
await page.click('button:has-text("Pause")').catch(() => {});

// Decoder page: turns element screenshots into scaled RGBA.
const decoder = await browser.newPage();
const gif = GIFEncoder();
let height = 0;
for (let i = 0; i < frames; i++) {
  const v = Math.round((1000 * i) / (frames - 1));
  await page.$eval('.scrub', (e, val) => { e.value = String(val); e.dispatchEvent(new Event('input', { bubbles: true })); }, v);
  await page.waitForTimeout(200);
  const png = (await page.locator('#viewport').screenshot()).toString('base64');
  const { data, h } = await decoder.evaluate(async ([b64, w]) => {
    const img = new Image();
    img.src = 'data:image/png;base64,' + b64;
    await img.decode();
    const h = Math.round((img.height * w) / img.width);
    const c = new OffscreenCanvas(w, h), g = c.getContext('2d');
    g.drawImage(img, 0, 0, w, h);
    const px = g.getImageData(0, 0, w, h).data;
    let s = ''; for (let k = 0; k < px.length; k += 0x8000) s += String.fromCharCode(...px.subarray(k, k + 0x8000));
    return { data: btoa(s), h };
  }, [png, width]);
  height = h;
  const rgba = new Uint8Array(Buffer.from(data, 'base64'));
  const palette = quantize(rgba, 256);
  gif.writeFrame(applyPalette(rgba, palette), width, height, { palette, delay: i === frames - 1 ? 2500 : 90 });
  process.stdout.write(`\rframe ${i + 1}/${frames}`);
}
gif.finish();
await writeFile(out, gif.bytes());
console.log(`\n${out}: ${width}x${height}`);
await browser.close();
