// Nimmt die Prozessliste auf: DOM-Messwerte als Text und je ein Bild der beiden Themen. Chrome startet headless
// über Playwright; die Oberfläche prüft man mit Zahlen, nicht mit dem Auge.
//
//   node tools/shot.mjs [--port 8077] [--out docs/screenshots] [--name sys] [--width 1400] [--height 950]
// Voraussetzungen: npm install im Ordner tools/ und Google Chrome. Der Server muss laufen.
import { chromium } from "playwright-core";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const args = process.argv.slice(2);
const value = (name, fallback) => {
    const i = args.indexOf(name);
    return i >= 0 && i + 1 < args.length ? args[i + 1] : fallback;
};
const port = Number(value("--port", "8077"));
const out = value("--out", "docs/screenshots");
const name = value("--name", "sys");
const width = Number(value("--width", "1400"));
const height = Number(value("--height", "950"));
mkdirSync(out, { recursive: true });

const browser = await chromium.launch({ channel: "chrome", headless: true });
try {
    const context = await browser.newContext({ viewport: { width, height }, deviceScaleFactor: 1 });
    const page = await context.newPage();
    const problems = [];
    page.on("pageerror", (e) => problems.push(`pageerror: ${e.message}`));
    page.on("console", (m) => { if (m.type() === "error") problems.push(`console: ${m.text()}`); });
    page.on("response", (r) => { if (r.status() >= 400) problems.push(`${r.status()} ${r.url()}`); });

    await page.goto(`http://127.0.0.1:${port}/`, { waitUntil: "load" });
    await page.waitForTimeout(2500);   // die erste Liste kommt per fetch

    const report = await page.evaluate(() => {
        const text = (sel) => document.querySelector(sel)?.textContent?.trim() ?? null;
        const rows = [...document.querySelectorAll("#proc-rows .req")];
        const table = document.getElementById("procs");
        return {
            title: document.title,
            theme: document.documentElement.dataset.theme,
            header: { processes: text("#v-procs"), uptime: text("#v-uptime") },
            list: {
                heading: text("#v-proc-shown"),
                rows: rows.length,
                groups: rows.filter((r) => r.classList.contains("grp")).length,
            },
            firstRow: rows[0]?.innerText.replace(/\s+/g, " ").trim() ?? null,
            unresolvedIcons: [...document.querySelectorAll("svg.ic use")].filter((u) => {
                const id = (u.getAttribute("href") || "").slice(1);
                return id && !document.getElementById(id);
            }).length,
            layout: {
                pageOverflowX: document.documentElement.scrollWidth - window.innerWidth,
                tableOverflowX: table ? table.scrollWidth - table.clientWidth : null,
                pageHeight: document.documentElement.scrollHeight,
            },
        };
    });
    writeFileSync(join(out, `${name}-measure.json`), JSON.stringify({ report, problems }, null, 2));

    await page.screenshot({ path: join(out, `${name}-dark.jpg`), type: "jpeg", quality: 72 });
    await page.evaluate(() => { document.documentElement.dataset.theme = "titan"; });
    await page.waitForTimeout(400);
    await page.screenshot({ path: join(out, `${name}-titan.jpg`), type: "jpeg", quality: 72 });

    console.log(JSON.stringify(report, null, 2));
    console.log("problems:", problems.length ? problems : "none");
} finally {
    await browser.close();
}
