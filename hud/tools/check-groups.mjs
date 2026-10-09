// Prüft das Zusammenfassen gleicher Programme: zwei Opferprozesse mit gleichem Namen und Pfad müssen **eine**
// Zeile „×2" ergeben, die sich auf zwei Einzelzeilen aufklappen lässt, und „End all" darf erst nach der Rückfrage
// beide beenden. Angefasst werden nur die Prozesse, die diese Prüfung selbst startet.
//
//   node tools/check-groups.mjs [--port 8079]
import { createRequire } from "node:module";
import { spawn } from "node:child_process";

const require = createRequire("C:/_AI/llama.cpp-prism/build/tools/ui/ui-src/node_modules/");
const { chromium } = require("playwright-core");

const args = process.argv.slice(2);
const value = (name, fallback) => {
    const i = args.indexOf(name);
    return i >= 0 && i + 1 < args.length ? args[i + 1] : fallback;
};
const port = Number(value("--port", "8079"));
const base = `http://127.0.0.1:${port}`;

const checks = [];
const check = (name, ok, detail) => checks.push({ name, ok, detail });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// Zwei Prozesse mit gleichem Namen und Pfad, die niemand braucht.
const victims = [spawn("ping.exe", ["-n", "900", "127.0.0.1"], { stdio: "ignore", windowsHide: true }),
                 spawn("ping.exe", ["-n", "900", "127.0.0.1"], { stdio: "ignore", windowsHide: true })];
const pids = victims.map((v) => v.pid);
const alive = () => victims.every((v) => v.exitCode === null);

let browser;
try {
    await sleep(900);
    check("both sacrificial processes are running", alive(), pids.join(", "));

    const snapshot = await (await fetch(`${base}/api/snapshot`)).json();
    const mine = snapshot.processes.filter((p) => pids.includes(p.pid));
    const name = mine[0]?.name ?? "ping";
    const path = mine[0]?.path ?? "";
    check("the snapshot knows both", mine.length === 2, `${mine.length} of 2`);
    check("both share name and path", mine.length === 2 && mine[0].path === mine[1].path, `${name} · ${path}`);
    const siblings = snapshot.processes.filter((p) => p.name === name && p.path === path).length;
    check("that name and path is only ours", siblings === 2, `${siblings} processes with it`);

    browser = await chromium.launch({
        executablePath: "C:/Program Files (x86)/Google/Chrome/Application/chrome.exe",
        headless: true,
    });
    const page = await browser.newPage({ viewport: { width: 1400, height: 1000 } });
    const problems = [];
    page.on("pageerror", (e) => problems.push(`pageerror: ${e.message}`));
    page.on("console", (m) => { if (m.type() === "error") problems.push(`console: ${m.text()}`); });
    await page.goto(`${base}/`, { waitUntil: "load" });
    await sleep(2000);

    // Suchen: die passende Gruppe klappt einmal auf.
    await page.fill("#q", name);
    await sleep(2400);
    const expanded = await page.evaluate((wanted) => {
        const rows = [...document.querySelectorAll("#proc-rows .req")];
        const group = rows.find((r) => r.classList.contains("grp")
            && (r.querySelector(".mdl > span")?.textContent ?? "").toLowerCase() === wanted.toLowerCase());
        const children = rows.filter((r) => r.classList.contains("child"));
        return {
            group: !!group,
            count: group?.querySelector(".chipi.cnt")?.textContent ?? null,
            chevron: !!group?.querySelector(".chev"),
            children: children.length,
            childPids: children.map((r) => Number(r.dataset.pid)),
        };
    }, name);
    check("the search folds the two into one group", expanded.count === "×2", `"${expanded.count}"`);
    check("the group carries an expand button", expanded.chevron, "chevron present");
    check("a search shows the members at once", expanded.children === 2, `${expanded.children} children`);

    // Zuklappen: dann steht in der Zeile die Summe. Bei aktiver Suche gibt es genau eine Gruppe.
    const groupRow = page.locator("#proc-rows .req.grp").first();
    const before = (await groupRow.locator(".chev").textContent()).trim();
    await groupRow.locator(".chev").click();
    await sleep(600);
    const folded = await page.evaluate(() => {
        const rows = [...document.querySelectorAll("#proc-rows .req")];
        const group = rows.find((r) => r.classList.contains("grp") && r.querySelector(".chipi.cnt"));
        return {
            children: rows.filter((r) => r.classList.contains("child")).length,
            chevron: group?.querySelector(".chev")?.textContent?.trim() ?? null,
            memory: group?.children[4]?.textContent?.trim() ?? null,
            cpu: group?.querySelector(".dur .pc")?.textContent?.trim() ?? null,
            threads: group?.querySelectorAll(".ex b")[1]?.textContent?.trim() ?? null,
            chipsInSingleRows: rows.filter((r) => !r.classList.contains("grp") && r.querySelector(".chipi.cnt")).length,
        };
    });
    check("collapsing hides the members", folded.children === 0, `arrow was ${before}, now ${folded.chevron}, ${folded.children} children`);
    check("the folded row shows the arrow again", folded.chevron === "▸", `"${folded.chevron}"`);
    check("the folded row sums memory, cpu and threads",
        folded.memory !== "–" && folded.cpu !== "–" && folded.threads !== "–",
        `memory ${folded.memory}, cpu ${folded.cpu}, threads ${folded.threads}`);
    check("only group rows carry the ×N chip", folded.chipsInSingleRows === 0, `${folded.chipsInSingleRows} single rows with a count`);

    // End all: fragen, abbrechen, bestätigen.
    await groupRow.locator("details.menu > summary").click();
    await groupRow.locator('button[data-act="end"]').click();
    await sleep(400);
    const ask = (await page.locator("#confirm .txt").textContent().catch(() => "")).trim();
    check("the group's End task asks for all of them", /all 2 .* tasks/.test(ask), `"${ask}"`);
    check("asking ends nothing", alive(), pids.join(", "));

    await page.locator('#confirm button[data-act="cancel"]').click();
    await sleep(300);
    check("cancel ends nothing", alive(), pids.join(", "));

    await groupRow.locator("details.menu > summary").click();
    await groupRow.locator('button[data-act="end"]').click();
    await page.locator('#confirm button[data-act="confirm"]').click();
    await sleep(2500);
    const toastText = (await page.locator("#toast").textContent()).trim();
    check("the toast counts what was done", /2 of 2 done/.test(toastText), `"${toastText}"`);
    check("both processes are really gone", !alive(), victims.map((v) => v.exitCode).join(", "));

    console.log("\nproblems:", problems.length ? problems : "none");
} catch (error) {
    check("the check ran to the end", false, String(error.message).split("\n")[0]);
} finally {
    for (const v of victims) { try { v.kill(); } catch { } }
    if (browser) await browser.close();
}

console.log(JSON.stringify({ pids, checks }, null, 2));
const failed = checks.filter((c) => !c.ok);
console.log(`\n${checks.length - failed.length}/${checks.length} checks passed`);
if (failed.length) { console.log("FAILED:", failed.map((f) => `${f.name} (${f.detail})`).join(" | ")); process.exitCode = 1; }