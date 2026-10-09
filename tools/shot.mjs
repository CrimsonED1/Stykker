// Nimmt eine Seite auf: DOM-Messwerte (Zahlen, Farben, Geometrie) als Text und je ein Bild der beiden Themen.
// Chrome startet headless über Playwright; das Design-System prüft man mit Zahlen, nicht mit dem Auge.
// Lädt auch lokale Entwürfe: ohne --url wird die laufende Anwendung auf dem Port genommen, mit --url jede Adresse
// (auch file:///…), damit Entwurf und gebaute Fassung dieselbe Messung bekommen.
//
//   node tools/shot.mjs [--port 8079] [--url <adresse>] [--out docs] [--name hud] [--format jpeg|png]
//                       [--width 1400] [--height 950]
import { createRequire } from "node:module";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const require = createRequire("C:/_AI/llama.cpp-prism/build/tools/ui/ui-src/node_modules/");
const { chromium } = require("playwright-core");

const args = process.argv.slice(2);
const value = (name, fallback) => {
    const i = args.indexOf(name);
    return i >= 0 && i + 1 < args.length ? args[i + 1] : fallback;
};
const port = Number(value("--port", "8079"));
const out = value("--out", "docs");
const name = value("--name", "hud");
const format = value("--format", "jpeg");
const width = Number(value("--width", "1400"));
const height = Number(value("--height", "950"));
const target = value("--url", `http://127.0.0.1:${port}/`);
const ext = format === "png" ? "png" : "jpg";
const image = (file) => ({ path: join(out, file), type: format === "png" ? "png" : "jpeg", ...(format === "png" ? {} : { quality: 72 }) });
mkdirSync(out, { recursive: true });

const browser = await chromium.launch({
    executablePath: "C:/Program Files (x86)/Google/Chrome/Application/chrome.exe",
    headless: true,
});

try {
    const context = await browser.newContext({ viewport: { width, height }, deviceScaleFactor: 1 });
    const page = await context.newPage();
    const problems = [];
    page.on("pageerror", (e) => problems.push(`pageerror: ${e.message}`));
    page.on("console", (m) => { if (m.type() === "error") problems.push(`console: ${m.text()}`); });
    page.on("requestfailed", (r) => problems.push(`request failed: ${r.url()}`));
    page.on("response", (r) => { if (r.status() >= 400) problems.push(`${r.status()} ${r.url()}`); });

    await page.goto(target, { waitUntil: "load" });
    await page.waitForTimeout(2500);   // die erste Messung kommt per fetch bzw. der erste Takt des Entwurfs

    const report = await page.evaluate(() => {
        const text = (sel) => document.querySelector(sel)?.textContent?.trim() ?? null;
        const style = (sel, prop) => {
            const el = document.querySelector(sel);
            return el ? getComputedStyle(el)[prop] : null;
        };
        const box = (sel) => {
            const el = document.querySelector(sel);
            if (!el) return null;
            const r = el.getBoundingClientRect();
            return { x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height) };
        };
        // Wie die Zellen wirklich liegen: je sichtbarer Zeile die Zahl der Zellen, z. B. [3,2]. Die Spaltenzahl aus
        // getComputedStyle taugt bei auto-fit nicht – dort steht am Ende eine Spur, die keine Zelle trägt.
        const layoutRows = (cells) => {
            const byTop = new Map();
            for (const cell of cells) {
                const top = Math.round(cell.getBoundingClientRect().top);
                byTop.set(top, (byTop.get(top) ?? 0) + 1);
            }
            return [...byTop.entries()].sort((a, b) => a[0] - b[0]).map(([, count]) => count);
        };
        const table = document.querySelector("#procs");
        const app = {
            header: {
                cpu: text("#v-cpu"), ram: text("#v-ram"), gpu: text("#v-gpu"), vram: text("#v-vram"),
                procs: text("#v-procs"), uptime: text("#v-uptime"), gpuName: text("#v-gpu-name"),
            },
            gpuCard: {
                util: text("#v-gpu-big"), vram: text("#v-gpu-vram"), temp: text("#v-gpu-temp"),
                power: text("#v-gpu-power"), clock: text("#v-gpu-clock"),
            },
            bars: {
                cpu: style("#t-cpu", "width"), ram: style("#t-ram", "width"),
                commit: style("#t-commit", "width"), vram: style("#t-vram", "width"),
            },
            notes: document.querySelectorAll("#notes .notice").length,
            lampStates: [...document.querySelectorAll("#proc-rows .st")].slice(0, 6).map((el) => el.dataset.s),
            firstRow: document.querySelector("#proc-rows .req")?.innerText.replace(/\s+/g, " ").trim() ?? null,
        };
        return {
            url: location.href,
            title: document.title,
            theme: document.documentElement.dataset.theme,
            shellClass: document.documentElement.classList.contains("shell"),
            spriteIcons: document.querySelectorAll("svg.ic use").length,
            unresolvedIcons: [...document.querySelectorAll("svg.ic use")].filter((u) => {
                const id = (u.getAttribute("href") || "").slice(1);
                return id && !document.getElementById(id);
            }).length,
            cards: document.querySelectorAll(".card").length,
            rows: document.querySelectorAll("#proc-rows .req").length,
            cores: document.querySelectorAll("#cores .ctx").length,
            // Kennzahlen-Zeilen: läuft eine Zelle über ihre Spalte hinaus, schreibt sie über die Nachbarzelle.
            metrics: [...document.querySelectorAll(".mgrid")].map((grid, i) => {
                const cells = [...grid.querySelectorAll(".mc")];
                const overflow = cells.map((c) => c.scrollWidth - c.clientWidth);
                return {
                    i,
                    cells: cells.length,
                    rows: layoutRows(cells),
                    cellWidth: cells.length ? Math.round(cells[0].getBoundingClientRect().width) : null,
                    maxOverflowPx: overflow.length ? Math.max(...overflow) : 0,
                };
            }),
            // Die Werte der Anwendung gibt es nur dort: im Entwurf sind dieselben Plätze leer.
            app: document.getElementById("v-cpu") ? app : null,
            spark: {
                cpu: document.querySelector("#cpu-line")?.getAttribute("d")?.length ?? 0,
                second: document.querySelector("#gpu-line")?.getAttribute("d")?.length ?? 0,
                peak: text("#cpu-peak"),
            },
            colours: {
                body: style("body", "backgroundColor"),
                accent: style(".brand .acc", "color"),
                ink: style("body", "color"),
                gradient: style(".card", "backgroundImage")?.slice(0, 60) ?? null,
            },
            fonts: { body: style("body", "fontFamily"), figure: style("#v-cpu", "fontFamily") },
            layout: {
                top: box(".top"),
                layoutColumns: style(".layout", "gridTemplateColumns"),
                left: box(".layout > .col:first-child"),
                rail: box(".layout > .col:last-child"),
                table: box("#procs"),
                tableOverflowX: table ? table.scrollWidth - table.clientWidth : null,
                pageOverflowX: document.documentElement.scrollWidth - window.innerWidth,
                pageHeight: document.documentElement.scrollHeight,
                chosenSegments: document.querySelectorAll(".seg button.on").length,
            },
        };
    });

    writeFileSync(join(out, `${name}-measure.json`), JSON.stringify({ report, problems }, null, 2));
    await page.screenshot(image(`${name}-dark.${ext}`));

    await page.evaluate(() => { document.documentElement.dataset.theme = "titan"; });
    await page.waitForTimeout(400);
    await page.screenshot(image(`${name}-titan.${ext}`));

    // Das zweite Thema muss sich messbar unterscheiden: heller Grund, signalorange Akzente, harte Kanten.
    const titan = await page.evaluate(() => ({
        theme: document.documentElement.dataset.theme,
        body: getComputedStyle(document.body).backgroundColor,
        accent: getComputedStyle(document.querySelector(".brand .acc")).color,
        cardRadius: getComputedStyle(document.querySelector(".card")).borderRadius,
    }));

    await page.evaluate(() => { document.documentElement.dataset.theme = "dark"; });
    await page.waitForTimeout(300);
    await page.screenshot({ path: join(out, `${name}-full.png`), fullPage: true });

    // Ein kleines Bild (unter 70 kB), das auch die Bildprüfung noch durchlässt.
    await page.setViewportSize({ width: 1120, height: 660 });
    await page.waitForTimeout(400);
    await page.screenshot({ path: join(out, `${name}-review.jpg`), type: "jpeg", quality: 55 });

    console.log(JSON.stringify(report, null, 2));
    console.log("\ntitan:", JSON.stringify(titan));
    console.log("problems:", problems.length ? problems : "none");
} finally {
    await browser.close();
}