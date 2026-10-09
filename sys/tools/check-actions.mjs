// Prüft die Prozess-Aktionen gegen die laufende Anwendung – und zwar nur an einem Prozess, den dieses Skript
// selbst startet und wieder beendet (ein Node-Kind, das nur wartet). Ablauf:
//   1. Opferprozess starten, PID merken.
//   2. Sperren prüfen: fremde Herkunft, fremde Seite, kein JSON, System-PID (alles muss abgelehnt werden).
//   3. Priorität über die API setzen und im Messwert nachsehen, ob sie wirklich steht.
//   4. Über die Oberfläche: Zeilenmenü, "End task", Rückfrage muss erscheinen, "Cancel" darf nichts tun,
//      erst die Bestätigung beendet – und der Toast muss das Ergebnis nennen.
//
//   node tools/check-actions.mjs [--port 8077]
// Voraussetzungen: npm install im Ordner tools/ und Google Chrome. Der Server muss laufen.
import { chromium } from "playwright-core";
import { spawn } from "node:child_process";

const args = process.argv.slice(2);
const value = (name, fallback) => {
    const i = args.indexOf(name);
    return i >= 0 && i + 1 < args.length ? args[i + 1] : fallback;
};
const port = Number(value("--port", "8077"));
const base = `http://127.0.0.1:${port}`;

const checks = [];
const check = (name, ok, detail) => checks.push({ name, ok, detail });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// Ein Prozess, den niemand braucht: ein Node-Kind, das 15 Minuten nichts tut. Kein Konsolenfenster nötig.
const victim = spawn(process.execPath, ["-e", "setTimeout(() => {}, 900000)"], { stdio: "ignore", windowsHide: true });
const pid = victim.pid;
const alive = () => victim.exitCode === null;

let browser;
try {
    await sleep(800);
    check("the sacrificial process is running", alive(), `pid ${pid}`);

    // ── Sperren (aus Node: im Browser ist der Origin-Kopf gesperrt) ──
    const post = async (headers, body) => {
        const r = await fetch(`${base}/api/process/end`, { method: "POST", headers, body });
        let parsed = null;
        try { parsed = await r.json(); } catch { /* 415 hat keinen Rumpf */ }
        return { status: r.status, body: parsed };
    };
    const crossSite = await post({ "Content-Type": "application/json", "Sec-Fetch-Site": "cross-site" }, JSON.stringify({ pid: 4, tree: false }));
    check("a cross-site request is refused", crossSite.status === 403, `status ${crossSite.status}, "${crossSite.body?.message}"`);

    const foreign = await post({ "Content-Type": "application/json", Origin: "http://example.com" }, JSON.stringify({ pid: 4, tree: false }));
    check("a foreign origin is refused", foreign.status === 403, `status ${foreign.status}, "${foreign.body?.message}"`);

    const notJson = await post({ "Content-Type": "text/plain" }, "pid=4");
    check("a non-JSON body is refused", notJson.status === 415 || notJson.status === 400, `status ${notJson.status}`);

    const system = await post({ "Content-Type": "application/json" }, JSON.stringify({ pid: 4, tree: false }));
    check("a system PID is refused", system.status === 409 && /system process/i.test(system.body?.message ?? ""), `"${system.body?.message}"`);

    // ── Priorität ──
    const priority = await (await fetch(`${base}/api/process/priority`, {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ pid, level: "high" }),
    })).json();
    check("priority is accepted", priority.ok === true, `"${priority.message}"`);

    // Der Messwert ist die letzte Messung: er zieht erst mit der nächsten Sekunde nach.
    let seen = null;
    for (let i = 0; i < 8 && seen !== "High"; i++) {
        await sleep(700);
        const snapshot = await (await fetch(`${base}/api/snapshot`)).json();
        seen = snapshot.processes.find((p) => p.pid === pid)?.priority ?? null;
    }
    check("the snapshot reports the new priority", seen === "High", `priority=${seen}`);

    // ── Oberfläche ──
    browser = await chromium.launch({ channel: "chrome", headless: true });
    const page = await browser.newPage({ viewport: { width: 1400, height: 1000 } });
    const problems = [];
    page.on("pageerror", (e) => problems.push(`pageerror: ${e.message}`));
    page.on("console", (m) => { if (m.type() === "error") problems.push(`console: ${m.text()}`); });
    await page.goto(`${base}/`, { waitUntil: "load" });
    await sleep(2000);
    await page.fill("#q", String(pid));
    await sleep(2200);

    const line = page.locator(`#proc-rows .req[data-pid="${pid}"]`);
    const count = await line.count();
    check("the row is in the list", count === 1, `${count} row`);
    if (count === 1) {
        check("the priority chip is visible", (await line.locator(".mdl .chipi").textContent()).trim() === "High", await line.locator(".mdl .chipi").textContent());
        check("the menu marks the current priority", await line.locator('button[data-level="high"]').isDisabled(), "High is disabled in the menu");

        await line.locator("details.menu > summary").click();
        await line.locator('button[data-act="end"]').click();
        await sleep(300);
        const confirm = await page.locator("#confirm .notice").count();
        check("asking appears instead of killing at once", confirm === 1, await page.locator("#confirm .txt").textContent().catch(() => "no bar"));
        check("nothing was ended by asking", alive(), `pid ${pid}`);

        await page.locator('#confirm button[data-act="cancel"]').click();
        await sleep(300);
        check("cancel clears the bar", await page.locator("#confirm .notice").count() === 0, "empty");
        check("cancel ends nothing", alive(), `pid ${pid}`);

        await line.locator("details.menu > summary").click();
        await line.locator('button[data-act="end"]').click();
        await page.locator('#confirm button[data-act="confirm"]').click();
        await sleep(1500);
        const text = (await page.locator("#toast").textContent()).trim();
        check("the toast names the result", new RegExp(`\\(PID ${pid}\\) ended`).test(text), `"${text}"`);
        check("the process is really gone", !alive(), `exitCode=${victim.exitCode}`);
    }
    console.log("\nproblems:", problems.length ? problems : "none");
} catch (error) {
    // Ein Fehler ist auch ein Ergebnis: berichten, statt zu werfen.
    check("the check ran to the end", false, String(error.message).split("\n")[0]);
} finally {
    try { victim.kill(); } catch { }
    if (browser) await browser.close();
}

console.log(JSON.stringify({ pid, checks }, null, 2));
const failed = checks.filter((c) => !c.ok);
console.log(`\n${checks.length - failed.length}/${checks.length} checks passed`);
if (failed.length) { console.log("FAILED:", failed.map((f) => `${f.name} (${f.detail})`).join(" | ")); process.exitCode = 1; }
