// Prüft die Verhaltensweisen der Oberfläche, die man mit einem Blick nicht sicher beurteilt: sind die Kerne
// zugeklappt (und das Raster leer), öffnen sie sich auf Klick, und tragen Datenträger und Netzwerk Balken?
//
//   node tools/check-ui.mjs [--port 8079]
import { createRequire } from "node:module";

const require = createRequire("C:/_AI/llama.cpp-prism/build/tools/ui/ui-src/node_modules/");
const { chromium } = require("playwright-core");

const args = process.argv.slice(2);
const value = (name, fallback) => {
    const i = args.indexOf(name);
    return i >= 0 && i + 1 < args.length ? args[i + 1] : fallback;
};
const port = Number(value("--port", "8079"));

const browser = await chromium.launch({
    executablePath: "C:/Program Files (x86)/Google/Chrome/Application/chrome.exe",
    headless: true,
});

const checks = [];
const check = (name, ok, detail) => { checks.push({ name, ok, detail }); };

try {
    const page = await browser.newPage({ viewport: { width: 1400, height: 1000 } });
    const problems = [];
    page.on("pageerror", (e) => problems.push(`pageerror: ${e.message}`));
    page.on("console", (m) => { if (m.type() === "error") problems.push(`console: ${m.text()}`); });

    await page.goto(`http://127.0.0.1:${port}/`, { waitUntil: "load" });
    await page.waitForTimeout(2500);

    const closed = await page.evaluate(() => ({
        hidden: document.hidden,
        foldOpen: document.getElementById("cores-fold")?.open ?? null,
        rows: document.querySelectorAll("#cores .ctx").length,
        summary: document.getElementById("v-cores-summary")?.textContent?.trim() ?? null,
        io: ["disk-read", "disk-write", "net-rx", "net-tx"].map((id) => ({
            id,
            text: document.getElementById("v-" + id)?.textContent?.trim() ?? null,
            bar: document.getElementById("t-" + id)?.style.width ?? null,
        })),
    }));

    check("page is visible (otherwise nothing is measured)", closed.hidden === false, `document.hidden=${closed.hidden}`);
    check("cores are folded by default", closed.foldOpen === false, `open=${closed.foldOpen}`);
    check("no core rows while folded", closed.rows === 0, `${closed.rows} rows`);
    check("folded line names the busiest core", /busiest \d+ %/.test(closed.summary || ""), `"${closed.summary}"`);

    await page.locator("#cores-fold > summary").click();
    await page.waitForTimeout(1200);
    const opened = await page.evaluate(() => ({
        foldOpen: document.getElementById("cores-fold")?.open ?? null,
        rows: document.querySelectorAll("#cores .ctx").length,
        first: document.querySelector("#cores .ctx")?.innerText.replace(/\s+/g, " ").trim() ?? null,
    }));
    check("clicking the line opens it", opened.foldOpen === true, `open=${opened.foldOpen}`);
    check("core rows appear when open", opened.rows === 16, `${opened.rows} rows, first "${opened.first}"`);

    // Die Balken: Breite gesetzt (nicht leer) und der Text nennt Wert und Sockel.
    for (const row of closed.io) {
        check(`${row.id}: bar has a width`, !!row.bar && row.bar.endsWith("%"), `width=${row.bar}`);
        check(`${row.id}: value and peak shown`, /\d .*\/ .*(MB|KB)\/s/.test(row.text || ""), `"${row.text}"`);
    }

    // Kennzahlen-Zeilen: keine Zelle darf über ihre Spalte hinauslaufen (sonst schreibt sie über die Nachbarzelle).
    const grid = await page.evaluate(() => {
        // Wie die Zellen liegen: je sichtbarer Zeile die Zahl der Zellen, z. B. [3,2].
        const layoutRows = (cells) => {
            const byTop = new Map();
            for (const cell of cells) {
                const top = Math.round(cell.getBoundingClientRect().top);
                byTop.set(top, (byTop.get(top) ?? 0) + 1);
            }
            return [...byTop.entries()].sort((a, b) => a[0] - b[0]).map(([, count]) => count);
        };
        return {
            rows: [...document.querySelectorAll(".mgrid")].map((el) => {
                const cells = [...el.querySelectorAll(".mc")];
                return {
                    layout: layoutRows(cells),
                    cellWidth: cells.length ? Math.round(cells[0].getBoundingClientRect().width) : 0,
                    overflow: cells.map((c) => c.scrollWidth - c.clientWidth).reduce((a, b) => Math.max(a, b), 0),
                };
            }),
            engines: [...document.querySelectorAll("#gpu-engines .g")].map((el) => el.textContent.trim()),
            bars: [...document.querySelectorAll("#gpu-stack i")].map((el) => el.style.width),
            power: document.getElementById("v-gpu-power-max")?.textContent?.trim() ?? null,
        };
    });
    for (const [i, row] of grid.rows.entries())
        check(`metric row ${i}: no cell runs over its column`, row.overflow === 0,
            `rows ${row.layout.join("+")} of ${row.cellWidth}px, worst overflow ${row.overflow}px`);
    check("engine bars and legend rows match", grid.bars.length === grid.engines.length,
        `${grid.bars.length} bars, ${grid.engines.length} legend rows`);
    check("power cell names the limit", /^\/ \d+ W$/.test(grid.power || ""), `"${grid.power}"`);

    // Die Engine-Aufteilung kommt aus den Leistungsindikatoren; über ein paar Takte muss sie einmal auftauchen.
    let engines = [];
    for (let i = 0; i < 6 && engines.length === 0; i++) {
        engines = await page.evaluate(async () =>
            (await (await fetch("/api/snapshot", { cache: "no-store" })).json()).gpu?.engines ?? []);
        if (!engines.length) await page.waitForTimeout(1000);
    }
    check("the graphics card reports an engine split", engines.length > 0,
        engines.map((e) => `${e.engine} ${e.percent} %`).join(", ") || "none in six ticks");

    console.log(JSON.stringify({ closed, opened, checks, problems }, null, 2));
    const failed = checks.filter((c) => !c.ok);
    console.log(`\n${checks.length - failed.length}/${checks.length} checks passed`);
    console.log("problems:", problems.length ? problems : "none");
    if (failed.length) { console.log("FAILED:", failed.map((f) => f.name).join(" | ")); process.exitCode = 1; }
} finally {
    await browser.close();
}