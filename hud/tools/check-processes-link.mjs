// Prüft den Verweis „Processes" im Monitor (hud.js, linkToSys):
//   1. allein im Browser: der Verweis zeigt auf die Adresse von SYS aus /api/hub und ist sichtbar;
//   2. im Frame der Zentrale: ein Klick führt auf /hub#sys, und SYS ist dort gewählt;
//   3. ohne sys-Eintrag in stykker.json: der Verweis bleibt versteckt (allein und im Frame).
// Die Prüfung startet den Monitor selbst, zweimal: mit und ohne sys-Eintrag. Die Konfiguration liegt in einem
// Temp-Ordner. Beendet wird nur der eigene Prozess (über seine PID).
//
//   node tools/check-processes-link.mjs [--exe <StykkerHUD-Server.exe>] [--port 8090] [--sys-port 8092]
import { chromium } from "playwright-core";
import { spawn } from "node:child_process";
import { mkdtempSync, writeFileSync, rmSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const args = process.argv.slice(2);
const value = (name, fallback) => {
    const i = args.indexOf(name);
    return i >= 0 && i + 1 < args.length ? args[i + 1] : fallback;
};
const here = dirname(fileURLToPath(import.meta.url));
const exe = resolve(value("--exe", join(here, "..", "..", "hud", "src", "StykkerHud.Server", "bin", "Debug", "net10.0", "StykkerHUD-Server.exe")));
const port = Number(value("--port", "8090"));
const sysPort = Number(value("--sys-port", "8092"));
const base = `http://127.0.0.1:${port}`;

if (!existsSync(exe)) {
    console.error(`Kein Server gefunden: ${exe} (erst dotnet build hud/StykkerHud.slnx)`);
    process.exit(2);
}

const dir = mkdtempSync(join(tmpdir(), "stykker-processes-link-"));
const pairingFile = join(dir, "pairing.dat");
const configWithSys = join(dir, "with-sys.json");
const configWithoutSys = join(dir, "without-sys.json");
// Der Monitor steht als Eintrag in der Zentrale, damit er im Frame der Zentrale erscheint.
writeFileSync(configWithSys, JSON.stringify({ modules: [{ id: "monitor", name: "Monitor" }, { id: "sys", name: "SYS", port: sysPort }] }));
writeFileSync(configWithoutSys, JSON.stringify({ modules: [{ id: "monitor", name: "Monitor" }, { id: "llm", name: "LLM", port: 17401 }] }));

const checks = [];
const check = (name, ok, detail) => { checks.push({ name, ok, detail }); };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// Startet den Monitor mit einer Konfiguration und wartet, bis er antwortet. Gibt eine Funktion zum Beenden zurück.
async function startMonitor(configFile) {
    const child = spawn(exe, ["--port", String(port), "--no-browser", "--no-tray", "--hub-config", configFile, "--pairing-file", pairingFile], { stdio: "ignore", windowsHide: true });
    const pid = child.pid;
    const stop = async () => {
        if (child.exitCode === null) {
            try { process.kill(pid); } catch { }
            await new Promise((r) => child.once("exit", r));
        }
    };
    for (let i = 0; i < 60; i++) {
        try {
            const response = await fetch(`${base}/api/hub`, { cache: "no-store" });
            if (response.ok) return { pid, stop };
        } catch { }
        await sleep(250);
    }
    await stop();
    throw new Error(`Der Monitor (PID ${pid}) antwortet nicht auf ${base}/api/hub`);
}

const browser = await chromium.launch({ channel: "chrome", headless: true });
const problems = [];

async function linkState(page) {
    return page.evaluate(() => {
        const link = document.getElementById("sys-link");
        return link ? { hidden: link.hidden, visible: link.offsetParent !== null, href: link.getAttribute("href"), target: link.getAttribute("target") } : null;
    });
}

try {
    // ── Lauf 1: mit sys-Eintrag ──
    let server = await startMonitor(configWithSys);
    try {
        const page = await browser.newPage({ viewport: { width: 1400, height: 1000 } });
        page.on("pageerror", (e) => problems.push(`pageerror: ${e.message}`));

        // 1. allein im Browser
        await page.goto(`${base}/`, { waitUntil: "load" });
        await page.waitForFunction(() => document.getElementById("sys-link")?.hidden === false, null, { timeout: 8000 }).catch(() => { });
        const alone = await linkState(page);
        check("alone: link is shown", alone?.hidden === false && alone?.visible === true, JSON.stringify(alone));
        check("alone: link points to SYS from /api/hub", alone?.href === `http://127.0.0.1:${sysPort}/`, `href=${alone?.href}`);
        check("alone: link does not force a target", alone?.target === null, `target=${alone?.target}`);

        // 2. im Frame der Zentrale
        await page.goto(`${base}/hub`, { waitUntil: "load" });
        await page.locator('#nav button[data-id="monitor"]').click();
        await page.locator("#frame").waitFor({ state: "visible", timeout: 8000 });
        const inFrame = page.frameLocator("#frame");
        const embedded = inFrame.locator("#sys-link");
        await embedded.waitFor({ state: "visible", timeout: 8000 }).catch(() => { });
        const frameLink = await inFrame.locator("#sys-link").evaluate((el) => ({ hidden: el.hidden, visible: el.offsetParent !== null, href: el.getAttribute("href"), target: el.getAttribute("target") })).catch((e) => ({ error: e.message }));
        check("in frame: link is shown", frameLink.visible === true, JSON.stringify(frameLink));
        check("in frame: link goes to /hub#sys in the top window", frameLink.href === "/hub#sys" && frameLink.target === "_top", JSON.stringify(frameLink));

        await embedded.click();
        await page.waitForURL(/\/hub#sys$/, { timeout: 8000 }).catch(() => { });
        check("click: the top window is at /hub#sys", /\/hub#sys$/.test(page.url()), `url=${page.url().replace(base, "")}`);
        const selected = await page.locator('#nav button[data-id="sys"][aria-current="page"]').count();
        check("click: SYS is the selected module in the hub", selected === 1, `${selected} selected`);
        const frameSrc = await page.locator("#frame").getAttribute("src");
        check("click: the hub frame shows SYS", frameSrc?.startsWith(`http://127.0.0.1:${sysPort}/`) === true, `src=${frameSrc}`);
        await page.close();
    } finally {
        await server.stop();
    }

    // ── Lauf 2: ohne sys-Eintrag ──
    server = await startMonitor(configWithoutSys);
    try {
        const page = await browser.newPage({ viewport: { width: 1400, height: 1000 } });
        page.on("pageerror", (e) => problems.push(`pageerror: ${e.message}`));

        await page.goto(`${base}/`, { waitUntil: "load" });
        await sleep(2000);
        const alone = await linkState(page);
        check("without sys entry, alone: link stays hidden", alone?.hidden === true && alone?.visible === false, JSON.stringify(alone));

        await page.goto(`${base}/hub`, { waitUntil: "load" });
        await page.locator('#nav button[data-id="monitor"]').click();
        await page.locator("#frame").waitFor({ state: "visible", timeout: 8000 });
        await sleep(2000);
        const frameLink = await page.frameLocator("#frame").locator("#sys-link").evaluate((el) => ({ hidden: el.hidden, visible: el.offsetParent !== null })).catch((e) => ({ error: e.message }));
        check("without sys entry, in frame: link stays hidden", frameLink.hidden === true && frameLink.visible === false, JSON.stringify(frameLink));
        await page.close();
    } finally {
        await server.stop();
    }
} finally {
    await browser.close();
    rmSync(dir, { recursive: true, force: true });
}

console.log(JSON.stringify({ checks, problems }, null, 2));
const failed = checks.filter((c) => !c.ok);
console.log(`\n${checks.length - failed.length}/${checks.length} checks passed`);
console.log("problems:", problems.length ? problems : "none");
if (failed.length || problems.length) {
    console.log("FAILED:", failed.map((f) => f.name).concat(problems).join(" | "));
    process.exitCode = 1;
}
