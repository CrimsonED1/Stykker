// Prüft die Weiterleitung fremder Dienste der Zentrale (docs/plan-stykkernode.md, Abschnitt 4.5 bis 4.8 und 10). Startet den
// Test-Daemon aus dem Qwen-Fork mit Local Control (wie die Desktop-App, nur mit einer Temp-Ablage), trägt seine URL über den
// Endpunkt der Zentrale ein, startet die Zentrale neu und prüft: Weiterleitung, Tausch gegen ein Geräte-Token, Ablehnen eines
// fremden Ziels, Ersetzen und Entfernen. Dazu einen kleinen Strom- und WebSocket-Server (Node-http, ohne Pakete). "Handy" =
// Anfragen an die Netzadresse dieser Maschine.
//
//   node tools/check-proxy.mjs --qwen-cli <Pfad zu cli.js> [--hud-dll <Pfad zu StykkerHUD-Server.dll>]
//
// Der Pfad zum Qwen-CLI gehört nicht in die Repo: er kommt von der Kommandozeile und landet nur in der Temp-Konfiguration.
// Die Ablagen liegen in einem Temp-Ordner. Die echte Konfiguration (%APPDATA%\Stykker) und die Daten der echten Qwen-App werden
// nie gelesen oder geschrieben. Adressen, Geheimnisse und Tokens werden nie ausgegeben, nur ihre Länge oder ob sie fehlen.
import { chromium } from "playwright-core";
import { spawn, execFile } from "node:child_process";
import { createServer, request as httpRequest } from "node:http";
import { createHash, randomBytes } from "node:crypto";
import { mkdtempSync, writeFileSync, readFileSync, existsSync, rmSync } from "node:fs";
import { tmpdir, networkInterfaces } from "node:os";
import { join, resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { createServer as createNetServer } from "node:net";

const here = dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const value = (name, fallback) => {
    const i = args.indexOf(name);
    return i >= 0 && i + 1 < args.length ? args[i + 1] : fallback;
};
const qwenCli = value("--qwen-cli", "");
const hudDll = resolve(value("--hud-dll", join(here, "../src/StykkerHud.Server/bin/Debug/net10.0/StykkerHUD-Server.dll")));
if (!qwenCli || !existsSync(qwenCli)) {
    console.error("usage: node tools/check-proxy.mjs --qwen-cli <path to cli.js> [--hud-dll <path>]");
    process.exit(2);
}

// Ports dieses Probelaufs: Zentrale 8090; Weiterleitungen 8091 (Qwen), 8092 (Strom-Test); der Strom-Dienst 41998; der
// Test-Daemon 17451, nur wenn frei (sonst 41999). Local Control nimmt den Port des Daemons auf seiner Netzadresse.
const HUD = 8090, QWEN_FWD = 8091, STREAM_FWD = 8092, STREAM = 41998;
const DAEMON = await freePort([17451, 41999]);
const PHONE_UA = "Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 Chrome/141.0 Mobile Safari/537.36";
const LAN = pickLan();
const QWEN_MESSAGE = "Qwen Code is not running. Start the Qwen Code desktop app on the PC.";

const tmp = mkdtempSync(join(tmpdir(), "stykker-proxy-check-"));
const qwenHome = join(tmp, "qwen-home");
const configFile = join(tmp, "stykker.json");
const pairingFile = join(tmp, "pairing.dat");

// Die Ausgangslage: nur der Strom-Dienst steht in der Datei. Qwen kommt später über den Endpunkt dazu.
const streamOnly = () => ({
    modules: [{ id: "stream", name: "Stream test", external: true, port: STREAM_FWD, target: `http://127.0.0.1:${STREAM}` }],
});
writeFileSync(configFile, JSON.stringify(streamOnly(), null, 2));
const readConfig = () => JSON.parse(readFileSync(configFile, "utf8"));
const writeConfig = (obj) => writeFileSync(configFile, JSON.stringify(obj, null, 2));
const configText = () => readFileSync(configFile, "utf8");

const checks = [];
const check = (name, ok, detail = "") => { checks.push({ name, ok: !!ok, detail: String(detail) }); };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const newToken = () => randomBytes(32).toString("hex");

// ── PowerShell: nur für Prozesse, die Fenster-Konsole der Zentrale und die Netzlisten ──
function ps(script) {
    const encoded = Buffer.from(script, "utf16le").toString("base64");
    return new Promise((resolvePs, reject) =>
        execFile("powershell", ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], { timeout: 60000, windowsHide: true },
            (err, stdout) => (err && !stdout ? reject(err) : resolvePs(stdout.trim()))));
}

// Die Zentrale beenden wie beim Schließen ihres Fensters: taskkill ohne /F (ein Fenster-Signal, kein Erzwingen).
const taskkill = (pid) => new Promise((resolveKill) =>
    execFile("taskkill", ["/PID", String(pid)], { windowsHide: true, timeout: 20000 },
        (err, stdout) => resolveKill(err ? "failed" : stdout.trim())));
// Einen Prozess beenden, den das Skript selbst gestartet hat: nur über seine PID (mit seinen Kindern)
const killOwn = (pid) => new Promise((resolveKill) =>
    execFile("taskkill", ["/PID", String(pid), "/T", "/F"], { windowsHide: true, timeout: 20000 },
        (err, stdout) => resolveKill(err ? "failed" : stdout.trim())));

const childNodePids = async (parentPid) => {
    const out = await ps(`Get-CimInstance Win32_Process -Filter "ParentProcessId=${parentPid}" | Where-Object { $_.Name -eq 'node.exe' } | ForEach-Object { $_.ProcessId }`);
    return out.split(/\s+/).filter(Boolean).map(Number);
};
const listeners = (port) => ps(`Get-NetTCPConnection -LocalPort ${port} -State Listen -ErrorAction SilentlyContinue | ForEach-Object { $_.LocalAddress } | Sort-Object -Unique`);
const alive = (pid) => ps(`if (Get-Process -Id ${pid} -ErrorAction SilentlyContinue) { 'yes' } else { 'no' }`).then((s) => s === "yes");

// ── HTTP ──
function http({ host = "127.0.0.1", port, path, method = "GET", headers = {}, body }) {
    return new Promise((resolveHttp, reject) => {
        const req = httpRequest({ host, port, path, method, headers, timeout: 15000 }, (res) => {
            const chunks = [];
            res.on("data", (c) => chunks.push(c));
            res.on("end", () => resolveHttp({ status: res.statusCode, headers: res.headers, body: Buffer.concat(chunks).toString("utf8") }));
        });
        req.on("timeout", () => req.destroy(new Error("timeout")));
        req.on("error", reject);
        if (body !== undefined) {
            req.setHeader("Content-Type", "application/json");
            req.end(JSON.stringify(body));
        } else req.end();
    });
}
const local = (port, path, headers, method, body) => http({ port, path, headers, method, body });
const phone = (port, path, headers = {}, method) => http({ host: LAN, port, path, method, headers: { "User-Agent": PHONE_UA, ...headers } });
const tryHttp = (opts) => http(opts).catch((e) => ({ status: 0, body: "", error: e.code || e.message }));
const json = (r) => { try { return JSON.parse(r.body); } catch { return {}; } };

// WebSocket-Handshake: 101 mit Socket, oder die Antwort einer Ablehnung (z. B. 403)
function wsOpen({ host = "127.0.0.1", port, path, headers = {} }) {
    return new Promise((resolveWs, reject) => {
        const key = randomBytes(16).toString("base64");
        const req = httpRequest({ host, port, path, headers: { Connection: "Upgrade", Upgrade: "websocket", "Sec-WebSocket-Version": "13", "Sec-WebSocket-Key": key, ...headers }, timeout: 15000 });
        req.on("upgrade", (res, socket, head) => resolveWs({ status: 101, socket, head }));
        req.on("response", (res) => {
            const chunks = [];
            res.on("data", (c) => chunks.push(c));
            res.on("end", () => resolveWs({ status: res.statusCode, body: Buffer.concat(chunks).toString("utf8") }));
        });
        req.on("timeout", () => req.destroy(new Error("timeout")));
        req.on("error", reject);
        req.end();
    });
}

// Ein Textrahmen nach RFC 6455; der Client maskiert, der Server nicht.
function encodeFrame(text) {
    const payload = Buffer.from(text);
    const len = payload.length;
    const header = len < 126 ? Buffer.from([0x81, 0x80 | len]) : Buffer.from([0x81, 0x80 | 126, len >> 8, len & 0xff]);
    const key = randomBytes(4);
    const masked = Buffer.from(payload.map((b, i) => b ^ key[i % 4]));
    return Buffer.concat([header, key, masked]);
}
function parseFrame(buf) {
    if (buf.length < 2) return null;
    const opcode = buf[0] & 0x0f;
    const masked = (buf[1] & 0x80) !== 0;
    let len = buf[1] & 0x7f;
    let off = 2;
    if (len === 126) { if (buf.length < 4) return null; len = buf.readUInt16BE(2); off = 4; }
    let key = null;
    if (masked) { if (buf.length < off + 4) return null; key = buf.subarray(off, off + 4); off += 4; }
    if (buf.length < off + len) return null;
    const payload = Buffer.from(buf.subarray(off, off + len));
    if (key) for (let i = 0; i < payload.length; i++) payload[i] ^= key[i % 4];
    return { opcode, payload, total: off + len };
}
function serverFrame(text) {
    const payload = Buffer.from(text);
    const len = payload.length;
    const header = len < 126 ? Buffer.from([0x81, len]) : Buffer.from([0x81, 126, len >> 8, len & 0xff]);
    return Buffer.concat([header, payload]);
}

// Der Strom-Dienst: /probe (für Kopfzeilen), /stream (20 Teile, je 300 ms), /ws (Echo)
const seen = {};
const stream = createServer(async (req, res) => {
    if (req.url.startsWith("/last")) {
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(JSON.stringify(seen.probe ?? {}));
        return;
    }
    if (req.url === "/probe") {
        seen.probe = { host: req.headers.host, cookie: req.headers.cookie ?? null, authorization: req.headers.authorization ?? null, origin: req.headers.origin ?? null };
        res.writeHead(200, { "Content-Type": "text/plain" });
        res.end("ok");
        return;
    }
    if (req.url === "/stream") {
        res.writeHead(200, { "Content-Type": "text/plain", "Cache-Control": "no-store" });
        for (let i = 0; i < 20; i++) {
            res.write(`part ${i}\n`);
            if (i < 19) await sleep(300);
        }
        res.end();
        return;
    }
    res.writeHead(404).end();
});
stream.on("upgrade", (req, socket) => {
    if (req.url !== "/ws") { socket.destroy(); return; }
    const accept = createHash("sha1").update(req.headers["sec-websocket-key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").digest("base64");
    socket.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n\r\n`);
    let buf = Buffer.alloc(0);
    socket.on("data", (chunk) => {
        buf = Buffer.concat([buf, chunk]);
        for (let f = parseFrame(buf); f; f = parseFrame(buf)) {
            buf = buf.subarray(f.total);
            if (f.opcode === 1) socket.write(serverFrame(f.payload.toString()));
            if (f.opcode === 8) { socket.end(); return; }
        }
    });
    socket.on("error", () => { });
});
await new Promise((r) => stream.listen(STREAM, "127.0.0.1", r));

// ── Zentrale starten und beenden ──
// Die Zentrale läuft als eigener Prozess mit verborgener Konsole, damit taskkill sie geordnet beendet. Ihre Ausgabe landet
// in Dateien im Temp-Ordner. Die Logs liest PowerShell, weil die Umleitung der Zentrale die Dateien offen hält.
const logFiles = [];
let hud = null;
const q = (s) => `'${String(s).replace(/'/g, "''")}'`;
const readLogs = () => ps(logFiles.map((f) => `if (Test-Path -LiteralPath ${q(f)}) { Get-Content -Raw -LiteralPath ${q(f)} }`).join("\n"))
    .catch(() => "");
async function startHud(cfg = configFile) {
    const n = logFiles.length / 2;
    const out = join(tmp, `hud-${n}.out.log`), err = join(tmp, `hud-${n}.err.log`);
    logFiles.push(out, err);
    const argList = [hudDll, "--port", String(HUD), "--hub-config", cfg, "--pairing-file", pairingFile, "--no-browser", "--no-tray"].map(q).join(",");
    const pid = Number(await ps(`$p = Start-Process -FilePath 'dotnet' -ArgumentList @(${argList}) -PassThru -WindowStyle Hidden -RedirectStandardOutput ${q(out)} -RedirectStandardError ${q(err)}
$p.Id`));
    hud = { pid };
    return hud;
}
async function waitUntil(test, ms = 40000) {
    for (const end = Date.now() + ms; Date.now() < end;) {
        try { if (await test()) return true; } catch { }
        await sleep(500);
    }
    return false;
}
const hubUp = () => waitUntil(async () => (await local(HUD, "/api/hub")).status === 200);
// Beendet die Zentrale und wartet, bis sie weg ist. Erst nach 45 s ohne Ende gilt sie als erzwungen beendet (forced).
async function stopHud() {
    if (!hud) return { sent: "none", gone: true, forced: false, seconds: 0 };
    const pid = hud.pid;
    const sent = await taskkill(pid);
    const startedAt = Date.now();
    let gone = await waitUntil(async () => !(await alive(pid)), 45000);
    const seconds = Math.round((Date.now() - startedAt) / 1000);
    let forced = false;
    if (!gone) {
        forced = true;
        await ps(`Stop-Process -Id ${pid} -Force -ErrorAction SilentlyContinue`);
        gone = !(await alive(pid));
    }
    hud = null;
    return { sent, gone, forced, seconds };
}

// ── Test-Daemon: Qwen mit Local Control, wie die Desktop-App es einschaltet (Netz, Kopplung, eigenes Geräte-Token) ──
const daemons = [];
async function startDaemon() {
    const child = spawn("node", [qwenCli, "serve", "--port", String(DAEMON), "--hostname", "127.0.0.1", "--local-control", "--no-open", "--workspace", tmp],
        { windowsHide: true, stdio: "ignore", env: { ...process.env, QWEN_HOME: qwenHome } });
    daemons.push(child);
    const active = await waitUntil(async () => json(await local(DAEMON, "/workspace/local-control")).active === true, 60000);
    return { pid: child.pid, active };
}
// Die Adresse und das Geheimnis, wie Qwen sie unter Local Control zeigt. Der Daemon liefert sie auf loopback ohne Token.
// Nur im Speicher des Skripts; nichts davon wird ausgegeben.
async function localControlUrl() {
    const status = json(await local(DAEMON, "/workspace/local-control"));
    return status.active && status.url ? status.url : null;
}
// Den Daemon beenden (nur über die eigene PID mit ihren Kindern) und warten, bis er weg ist
async function killDaemon(pid) {
    const sent = await killOwn(pid);
    const gone = await waitUntil(async () => !(await alive(pid)), 20000);
    return { sent, gone };
}

// ── Browser ──
const browser = await chromium.launch({ channel: "chrome", headless: true });
async function pcOpensQwen() {
    const page = await browser.newPage({ viewport: { width: 1200, height: 800 } });
    try {
        await page.goto(`http://127.0.0.1:${HUD}/hub`, { waitUntil: "load" });
        await page.locator('nav button[data-id="qwen"]').click();
        await page.waitForFunction(() => document.getElementById("frame")?.src.includes(":8091/"));
        const handle = await page.locator("#frame").elementHandle();
        const frame = await handle.contentFrame();
        await frame.waitForLoadState("load");
        await frame.waitForFunction(() => document.body && document.body.getBoundingClientRect().width > 0 && document.body.querySelectorAll("*").length > 5, null, { timeout: 15000 }).catch(() => { });
        await page.waitForTimeout(1500);
        return await frame.evaluate(async () => {
            const ws = await new Promise((resolveWs) => {
                const socket = new WebSocket(`ws://${location.host}/acp`);
                const timer = setTimeout(() => resolveWs("timeout"), 8000);
                socket.onopen = () => { clearTimeout(timer); socket.close(); resolveWs("open"); };
                socket.onerror = () => { clearTimeout(timer); resolveWs("error"); };
            });
            const text = (document.body?.innerText || "") + " " + document.title;
            return {
                url: location.href, signIn: /sign in|not paired|unauthorized|401/i.test(text), bodyChars: text.length, ws,
                elements: document.body ? document.body.querySelectorAll("*").length : 0,
                bodyWidth: Math.round(document.body?.getBoundingClientRect().width ?? 0),
            };
        });
    } finally {
        await page.close();
    }
}
async function phoneOpensQwen(cookie) {
    const context = await browser.newContext({ viewport: { width: 420, height: 860 }, isMobile: true, userAgent: PHONE_UA });
    try {
        await context.addCookies([{ name: "stykker_device", value: cookie, url: `http://${LAN}:${HUD}/` }]);
        const page = await context.newPage();
        await page.goto(`http://${LAN}:${HUD}/hub`, { waitUntil: "load" });
        // Am Handy ist die Leiste zugeklappt: erst öffnen, dann das Modul wählen
        if (await page.locator("#unfold").isVisible()) await page.locator("#unfold").click();
        await page.locator('nav button[data-id="qwen"]').click({ timeout: 15000 });
        await page.waitForFunction(() => document.getElementById("frame")?.src.includes(":8091/"), null, { timeout: 15000 });
        const frame = await (await page.locator("#frame").elementHandle()).contentFrame();
        await frame.waitForLoadState("load");
        await page.waitForTimeout(1500);
        await frame.waitForFunction(() => document.body && document.body.getBoundingClientRect().width > 0 && document.body.querySelectorAll("*").length > 5, null, { timeout: 15000 }).catch(() => { });
        return await frame.evaluate(() => ({
            url: location.href, bodyChars: (document.body?.innerText || "").length,
            ready: document.readyState, htmlChars: document.documentElement.outerHTML.length,
            elements: document.body ? document.body.querySelectorAll("*").length : 0,
            bodyWidth: Math.round(document.body?.getBoundingClientRect().width ?? 0),
        }));
    } finally {
        await context.close();
    }
}

// ── Ablauf ──
let secret = null;      // das Geheimnis aus der Local-Control-URL (nur im Speicher)
let deviceToken = null; // das Geräte-Token, das die Zentrale in stykker.json eintragen soll (nur im Speicher)
try {
    // Teil 1: Qwen mit Local Control, die Zentrale ohne Netzschalter (nur diese Maschine)
    const d1 = await startDaemon();
    check("1 test daemon runs with Local Control on", d1.active, `pid ${d1.pid}`);
    const daemonPid = d1.pid;
    const url = await localControlUrl();
    check("1 Local Control gives a URL with a secret and a LAN address", !!url && /#token=/.test(url) && !/^http:\/\/127\.0\.0\.1[:/]/.test(url),
        url ? "address and secret held in the script only" : "none");
    secret = url ? url.split("#token=")[1] : null;
    const lanUrl = url;
    const listen = (await listeners(DAEMON)).split(/\s+/).filter(Boolean);
    check("1 Local Control listens on a LAN address (not only loopback)", listen.some((a) => a !== "127.0.0.1"), `listen: ${listen.length} addresses`);

    await startHud();
    check("1 hub starts and answers on its port", await hubUp(), `port ${HUD}`);
    await sleep(1500);
    check("1 the hub starts no service", (await childNodePids(hud.pid)).length === 0, "node children of the hub");
    let log = await readLogs();

    // Die Ausgangslage: nur der Strom-Dienst, ohne Token
    const streamList = json(await local(HUD, "/api/hub/services")).services ?? [];
    check("2 the services list shows the stream service without a token", streamList.length === 1 && streamList[0].id === "stream" && streamList[0].tokenSet === false,
        JSON.stringify(streamList.map((s) => ({ id: s.id, tokenSet: s.tokenSet, running: s.running }))));
    const suggested = json(await local(HUD, "/api/hub/services")).suggestedPort;
    check("2 the suggested port is the first free one from 17450", suggested === 17450, `suggested ${suggested}`);

    // 3: fremdes Ziel und andere Fehler werden abgelehnt, ohne dass etwas geschrieben oder gezeigt wird
    const before = configText();
    const foreignUrl = `http://192.0.2.10:16000/#token=placeholder-secret-value`;
    const foreign = await local(HUD, "/api/hub/services", { Origin: `http://127.0.0.1:${HUD}` }, "POST",
        { name: "Qwen Code", id: "qwen", port: QWEN_FWD, url: foreignUrl });
    check("3 a URL on another machine is refused with 400", foreign.status === 400 && json(foreign).message?.includes("not one of this PC's addresses"),
        `status ${foreign.status}: ${json(foreign).message ?? ""}`);
    check("3 the refusal holds no secret and no token", !foreign.body.includes("placeholder-secret-value"), "");
    check("3 the refused entry is not written", configText() === before, "");
    const notURL = await local(HUD, "/api/hub/services", { Origin: `http://127.0.0.1:${HUD}` }, "POST",
        { name: "Qwen Code", id: "qwen", port: QWEN_FWD, url: "http://127.0.0.1:41999/" });
    check("3 a URL without a secret is refused", notURL.status === 400 && json(notURL).message?.includes("not a Local Control URL"), `status ${notURL.status}`);
    const sameId = await local(HUD, "/api/hub/services", { Origin: `http://127.0.0.1:${HUD}` }, "POST",
        { name: "Again", id: "stream", port: 17460, url: lanUrl });
    check("3 a taken id is refused before anything is paired", sameId.status === 400 && json(sameId).message === "This id is taken.", `status ${sameId.status}`);
    const foreignPage = await tryHttp({ port: HUD, path: "/api/hub/services", headers: { Origin: "http://evil.example" }, method: "GET" });
    check("3 the services list answers only this page (foreign origin on POST refused)", (await local(HUD, "/api/hub/services", { Origin: "http://evil.example" }, "POST",
        { name: "x", id: "evil", port: 17470, url: lanUrl })).status === 403, `GET from foreign origin: ${foreignPage.status}`);

    // 4: die Local-Control-URL eintragen: Tausch, Datei, Antwort ohne Token
    const add = await local(HUD, "/api/hub/services", { Origin: `http://127.0.0.1:${HUD}` }, "POST",
        { name: "Qwen Code", id: "qwen", port: QWEN_FWD, url: lanUrl });
    check("4 adding the Local Control URL gives 200 and asks for a restart", add.status === 200 && json(add).restart === true, `status ${add.status}: ${json(add).message ?? ""}`);
    check("4 the answer holds no token and no secret", !add.body.includes(secret) && !/"token"/.test(add.body), "");
    const cfgAfterAdd = readConfig();
    const qwenEntry = cfgAfterAdd.modules.find((m) => m.id === "qwen");
    deviceToken = qwenEntry?.token ?? null;
    check("4 stykker.json holds a device token of its own (not the secret)", !!deviceToken && deviceToken !== secret && deviceToken.length === 43 && !configText().includes(secret),
        `token length ${deviceToken?.length ?? 0}, secret in file: ${configText().includes(secret ?? "x")}`);
    check("4 the entry keeps the LAN address Qwen showed, with the port", qwenEntry?.port === QWEN_FWD && qwenEntry?.external === true && qwenEntry?.target?.endsWith(`:${DAEMON}`),
        `port ${qwenEntry?.port}, target ${qwenEntry?.target ? qwenEntry.target.replace(/\/\/[^:]+/, "//<address>") : "none"}`);
    const devices = json(await local(DAEMON, "/workspace/local-control/devices"));
    check("4 the device appears in Qwen's device list as \"Stykker hub\"", (devices.devices ?? []).some((d) => d.label === "Stykker hub"),
        `${(devices.devices ?? []).length} devices, labels: ${(devices.devices ?? []).map((d) => d.label).join(", ")}`);
    const services1 = json(await local(HUD, "/api/hub/services"));
    const qwenListed = (services1.services ?? []).find((s) => s.id === "qwen");
    check("4 the services list says the token is set and the forward starts after a restart", qwenListed?.tokenSet === true && qwenListed?.running === false,
        `tokenSet ${qwenListed?.tokenSet}, running ${qwenListed?.running}`);

    // 5: Neustart der Zentrale: der Weg läuft, die Seite lädt, der WebSocket geht auf, das Token steht nicht in den Antworten
    await stopHud();
    await startHud();
    check("5 hub restarts with the Qwen entry", await hubUp(), "");
    await sleep(1000);
    const viaForwarder = await tryHttp({ port: QWEN_FWD, path: "/workspace/local-control/devices" });
    check("5 through the forwarder with the device token -> 200", viaForwarder.status === 200, `status ${viaForwarder.status}`);
    const hubNow = await local(HUD, "/api/hub");
    const accessNow = await local(HUD, "/api/hub/access");
    const servicesNow = await local(HUD, "/api/hub/services");
    check("5 the token and the secret are in no answer of the hub (/api/hub, /access, /services)",
        [hubNow, accessNow, servicesNow].every((r) => !r.body.includes(deviceToken) && !r.body.includes(secret)), "");
    check("5 the forward is listed as running in /api/hub", json(hubNow).modules.some((m) => m.id === "qwen" && m.up === true), "");
    const pc = await pcOpensQwen();
    check("5 Qwen opens in the frame at the PC, no sign-in page", pc.url.includes(`:${QWEN_FWD}/`) && !pc.signIn && pc.elements > 5 && pc.bodyWidth > 0,
        `frame ${pc.url.replace(/^https?:\/\/[^/]+/, "")}, ${pc.elements} elements, body ${pc.bodyWidth} px, ${pc.bodyChars} text chars`);
    check("5 WebSocket /acp opens through the forwarder", pc.ws === "open", `ws: ${pc.ws}`);

    // 6: fremde Origin und fremder Name im Host-Kopf (DNS-Rebinding): 403, auch als WebSocket
    const foreignHttp = await tryHttp({ port: QWEN_FWD, path: "/capabilities", headers: { Origin: "http://evil.example" } });
    const foreignWs = await wsOpen({ port: QWEN_FWD, path: "/acp", headers: { Origin: "http://evil.example" } });
    check("6 foreign Origin on 8091 -> 403", foreignHttp.status === 403, `status ${foreignHttp.status}`);
    check("6 foreign Origin on 8091 as WebSocket -> 403", foreignWs.status === 403, `status ${foreignWs.status}`);
    const rebindHeaders = { Host: "evil.example:8091", Origin: "http://evil.example:8091" };
    const rebindHttp = await tryHttp({ port: QWEN_FWD, path: "/capabilities", headers: rebindHeaders });
    const rebindWs = await wsOpen({ port: QWEN_FWD, path: "/acp", headers: rebindHeaders });
    check("6 foreign Host name on 8091 (DNS rebinding) -> 403", rebindHttp.status === 403, `status ${rebindHttp.status}`);
    check("6 foreign Host name on 8091 as WebSocket -> 403", rebindWs.status === 403, `status ${rebindWs.status}`);

    // 7: was beim Dienst ankommt (der Strom-Dienst hat kein Token: kein Authorization), Strom und Echo über die Weiterleitung
    await local(STREAM_FWD, "/probe", { Cookie: "a=1; stykker_device=device-secret-value; b=2", Origin: `http://127.0.0.1:${STREAM_FWD}` });
    const last = json(await local(STREAM, "/last"));
    check("7 device cookie is removed, other cookies stay", last.cookie === "a=1; b=2", `cookie seen: ${last.cookie}`);
    check("7 without a token no Authorization is sent", last.authorization === null, `authorization: ${last.authorization ? "present" : "none"}`);
    check("7 Host is the target's", last.host === `127.0.0.1:${STREAM}`, `host ${last.host}`);
    check("7 Origin is rewritten to the target", last.origin === `http://127.0.0.1:${STREAM}`, `origin ${last.origin}`);
    const stamps = [];
    await new Promise((resolveStream, reject) => {
        const req = httpRequest({ host: "127.0.0.1", port: STREAM_FWD, path: "/stream" }, (res) => {
            res.on("data", (c) => { for (const _ of c.toString().matchAll(/part \d+/g)) stamps.push(Date.now()); });
            res.on("end", resolveStream);
        });
        req.on("error", reject);
        req.end();
    });
    check("7 all 20 parts arrive through the forwarder", stamps.length === 20, `${stamps.length} parts`);
    check("7 parts arrive while the stream runs, not at the end", stamps.length === 20 && stamps[19] - stamps[0] >= 4000,
        `first to last ${stamps.length ? stamps[stamps.length - 1] - stamps[0] : 0} ms`);
    const echo = await wsOpen({ port: STREAM_FWD, path: "/ws" });
    let echoed = [];
    if (echo.status === 101) {
        let buf = Buffer.from(echo.head ?? []);
        echoed = await new Promise((resolveEcho, reject) => {
            const got = [];
            const pump = () => {
                for (let f = parseFrame(buf); f; f = parseFrame(buf)) {
                    buf = buf.subarray(f.total);
                    if (f.opcode === 1) got.push(f.payload.toString());
                    if (got.length === 50) { resolveEcho(got); return; }
                }
            };
            echo.socket.on("data", (c) => { buf = Buffer.concat([buf, c]); pump(); });
            echo.socket.on("error", reject);
            for (let i = 0; i < 50; i++) echo.socket.write(encodeFrame(`m${i}`));
            pump();
            setTimeout(() => reject(new Error(`only ${got.length} echoes`)), 10000);
        }).catch((e) => e.message);
        echo.socket.destroy();
    }
    check("7 50 messages echo back through the forwarder", Array.isArray(echoed) && echoed.length === 50 && echoed[49] === "m49",
        Array.isArray(echoed) ? `${echoed.length} echoes` : echoed);

    // 8: Änderungen wirken ohne Neustart: ein anderes Token (Daemon lehnt es ab), dann ein totes Ziel (503), dann zurück
    const cfgLive = readConfig();
    cfgLive.modules.find((m) => m.id === "qwen").token = "wrong-token-value-not-the-device";
    writeConfig(cfgLive);
    await sleep(300);
    const badToken = await tryHttp({ port: QWEN_FWD, path: "/workspace/local-control/devices" });
    check("8 a changed token in the file takes effect without a restart (daemon refuses it)", badToken.status === 401, `status ${badToken.status}`);
    const cfgDead = readConfig();
    cfgDead.modules.find((m) => m.id === "qwen").token = deviceToken;
    const deadPort = await freePort([8192, 8193, 8194]);   // ein Port, auf dem nichts hört
    cfgDead.modules.find((m) => m.id === "qwen").target = `http://127.0.0.1:${deadPort}`;
    writeConfig(cfgDead);
    await sleep(300);
    const deadTarget = await tryHttp({ port: QWEN_FWD, path: "/" });
    check("8 a target that does not answer -> 503 with the not-running text", deadTarget.status === 503 && deadTarget.body.includes(QWEN_MESSAGE),
        `status ${deadTarget.status}`);
    const cfgBack = readConfig();
    cfgBack.modules.find((m) => m.id === "qwen").target = `http://${new URL(lanUrl).hostname}:${DAEMON}`;
    writeConfig(cfgBack);
    await sleep(300);
    const back = await tryHttp({ port: QWEN_FWD, path: "/workspace/local-control/devices" });
    check("8 the file back as it was -> the next request goes through", back.status === 200, `status ${back.status}`);

    // 9: URL ersetzen: dasselbe Gerät (Tausch mit dem gespeicherten Token), andere URL: die Probe ist abgelehnt
    const devicesBeforeReplace = (json(await local(DAEMON, "/workspace/local-control/devices")).devices ?? []).length;
    const replaced = await local(HUD, "/api/hub/services/qwen/url", { Origin: `http://127.0.0.1:${HUD}` }, "POST", { url: lanUrl });
    check("9 replacing the URL with the same one gives 200 and works at once", replaced.status === 200 && json(replaced).restart === false, `status ${replaced.status}`);
    const devicesAfterReplace = (json(await local(DAEMON, "/workspace/local-control/devices")).devices ?? []).length;
    check("9 replacing reuses the device: no second device in Qwen's list", devicesAfterReplace === devicesBeforeReplace, `${devicesBeforeReplace} before, ${devicesAfterReplace} after`);
    check("9 the token in the file is still the device's", readConfig().modules.find((m) => m.id === "qwen").token === deviceToken, "");
    const loopbackUrl = `http://127.0.0.1:${DAEMON}/#token=${secret}`;
    const replacedLoop = await local(HUD, "/api/hub/services/qwen/url", { Origin: `http://127.0.0.1:${HUD}` }, "POST", { url: loopbackUrl });
    check("9 a URL on loopback (not the Local Control listener) is refused with 400", replacedLoop.status === 400, `status ${replacedLoop.status}: ${json(replacedLoop).message ?? ""}`);
    check("9 the refused replace holds no secret", !replacedLoop.body.includes(secret), "");
    check("9 after the refused replace the file is as before", readConfig().modules.find((m) => m.id === "qwen").target === `http://${new URL(lanUrl).hostname}:${DAEMON}`, "");

    // 10: Log und Datei: kein Token, kein Geheimnis im Log
    log = await readLogs();
    check("10 the hub log holds no device token and no secret", !log.includes(deviceToken) && !log.includes(secret), `${log.length} chars checked`);

    // 11: Netzschalter an, Handy: der gekoppelte Gerät mit Rolle admin für Qwen lädt Qwen
    // Der Schalter wird am laufenden Hub umgelegt; wirksam wird er mit dem Neustart (der Port öffnet sich dann)
    const switched = await local(HUD, "/api/hub/remote?on=true", { Origin: `http://127.0.0.1:${HUD}` }, "POST");
    check("11 the network switch is set on the running hub", switched.status === 200, `status ${switched.status}`);
    await stopHud();
    await startHud();
    check("11 hub restarts with network access on", await hubUp(), "");
    if (LAN) {
        const unpaired = await tryHttp({ host: LAN, port: QWEN_FWD, path: "/", headers: { "User-Agent": PHONE_UA } });
        check("11 phone without device on 8091 -> 401", unpaired.status === 401, `status ${unpaired.status}`);
        const access = json(await local(HUD, "/api/hub/access"));
        const paired = await phone(HUD, `/pair?code=${access.code.replace(/\s/g, "")}`);
        const cookieHeader = paired.headers["set-cookie"]?.[0] ?? "";
        const device = (cookieHeader.match(/stykker_device=([^;]+)/) || [])[1];
        check("11 phone pairs with the code (cookie set)", paired.status === 302 && !!device, `status ${paired.status}`);
        const hubList = json(await phone(HUD, "/api/hub", { Cookie: `stykker_device=${device}` }));
        check("11 /api/hub does not name qwen for a viewer", !hubList.modules.some((m) => m.id === "qwen"), `modules seen: ${hubList.modules.map((m) => m.id).join(", ") || "none"}`);
        const viewer = await phone(QWEN_FWD, "/", { Cookie: `stykker_device=${device}` });
        check("11 phone viewer on 8091 -> 403", viewer.status === 403, `status ${viewer.status}`);
        const pcPage = await browser.newPage({ viewport: { width: 1200, height: 900 } });
        try {
            await pcPage.goto(`http://127.0.0.1:${HUD}/hub`, { waitUntil: "load" });
            await pcPage.locator("#devices tbody tr", { hasText: "Chrome on Android" })
                .locator('td[data-k="Qwen Code"] button[data-role="admin"]').click({ timeout: 15000 });
            await pcPage.waitForTimeout(1500);
        } finally {
            await pcPage.close();
        }
        const hubAfter = json(await phone(HUD, "/api/hub", { Cookie: `stykker_device=${device}` }));
        check("11 after admin for qwen, /api/hub names qwen", hubAfter.modules.some((m) => m.id === "qwen" && m.up === true), `modules seen: ${hubAfter.modules.map((m) => m.id).join(", ")}`);
        const framed = await phoneOpensQwen(device);
        check("11 Qwen loads in the frame on the phone", framed.url.includes(`:${QWEN_FWD}/`) && framed.elements > 5 && framed.bodyWidth > 0,
            `${framed.elements} elements, body ${framed.bodyWidth} px wide, ${framed.bodyChars} text chars, ${framed.htmlChars} html chars, ${framed.ready}`);
    } else {
        check("11 a LAN address exists for the phone checks", false, "none");
    }

    // 12: der Daemon geht aus, der Eintrag bleibt: 503 mit dem Text, /api/hub meldet up: false. Der Strom-Dienst geht weiter.
    const killed = await killDaemon(daemonPid);
    check("12 the test daemon is killed by its own pid", killed.gone, `taskkill: ${killed.sent}`);
    const down = await tryHttp({ port: QWEN_FWD, path: "/" });
    check("12 daemon gone -> 503 with the not-running text", down.status === 503 && down.body.includes(QWEN_MESSAGE), `status ${down.status}`);
    const hubDown = json(await local(HUD, "/api/hub"));
    check("12 daemon gone -> /api/hub says up: false for qwen", hubDown.modules.some((m) => m.id === "qwen" && m.up === false), "");
    check("12 the stream service still forwards", (await tryHttp({ port: STREAM_FWD, path: "/probe" })).status === 200, "");

    // 13: entfernen: der Eintrag geht aus der Datei, der Weg antwortet bis zum Neustart mit 503, danach ist der Port zu
    const removed = await local(HUD, "/api/hub/services/qwen", { Origin: `http://127.0.0.1:${HUD}` }, "DELETE");
    check("13 removing the service gives 200 and names the device to remove in Qwen", removed.status === 200 && json(removed).message?.includes("Remove the device"), `status ${removed.status}`);
    check("13 the entry is gone from stykker.json, the stream service stays", !readConfig().modules.some((m) => m.id === "qwen") && readConfig().modules.some((m) => m.id === "stream"), "");
    const gone503 = await tryHttp({ port: QWEN_FWD, path: "/" });
    check("13 until the restart the port answers 503 (the entry is gone)", gone503.status === 503 && gone503.body.includes(QWEN_MESSAGE), `status ${gone503.status}`);
    const removedAgain = await local(HUD, "/api/hub/services/qwen", { Origin: `http://127.0.0.1:${HUD}` }, "DELETE");
    check("13 removing it again gives 404", removedAgain.status === 404, `status ${removedAgain.status}`);
    const stop2 = await stopHud();
    check("13 the hub ends on taskkill without /F, not forced", stop2.gone && !stop2.forced, `taskkill: ${stop2.sent}, after ${stop2.seconds} s, forced: ${stop2.forced}`);
    await startHud();
    check("13 after the restart port 8091 is closed", (await hubUp()) && (await tryHttp({ port: QWEN_FWD, path: "/" })).status === 0, "");
    await stopHud();

    // 14: eine Datei mit fremdem Ziel von Hand: kein Port, der Dienst als nicht laufend gelistet
    const foreignFile = join(tmp, "foreign-target.json");
    writeFileSync(foreignFile, JSON.stringify({ modules: [{ id: "qwen", name: "Qwen Code", external: true, port: 8093, target: "http://192.0.2.10:16000", token: "placeholder-token" }] }, null, 2));
    await startHud(foreignFile);
    check("14 hub runs with a target on another machine", await hubUp(), "");
    check("14 target on another machine -> no forwarding port (8093 refuses)", (await tryHttp({ port: 8093, path: "/" })).status === 0, "");
    const refusedList = json(await local(HUD, "/api/hub"));
    check("14 the entry is listed as not running", refusedList.modules.some((m) => m.id === "qwen" && m.up === false), "");
    await stopHud();

    // 15: eine kaputte Datei wird nicht überschrieben
    writeFileSync(configFile, "{ broken");
    await startHud();
    check("15 hub runs with a broken stykker.json (defaults)", await hubUp(), "");
    const brokenAdd = await local(HUD, "/api/hub/services", { Origin: `http://127.0.0.1:${HUD}` }, "POST",
        { name: "Qwen Code", id: "qwen", port: QWEN_FWD, url: "http://192.0.2.10:16000/#token=placeholder" });
    check("15 a broken stykker.json is not overwritten", brokenAdd.status === 400 && configText() === "{ broken", `status ${brokenAdd.status}`);
    await stopHud();
    writeConfig(streamOnly());

    // 16: ohne stykker.json die Vorgabe
    await startHud(join(tmp, "missing", "stykker.json"));
    check("16 without any stykker.json the defaults are listed", await hubUp(), "");
    const defaults = json(await local(HUD, "/api/hub"));
    check("16 defaults: monitor, sys, llm", defaults.modules.map((m) => m.id).join(",") === "monitor,sys,llm", defaults.modules.map((m) => m.id).join(","));
    await stopHud();
} catch (error) {
    // Ein unerwarteter Fehler im Ablauf: er gilt als nicht bestanden und wird mit seiner Art gemeldet, nie mit Inhalten
    check("the run stopped early", false, `${error.code || error.name || "error"}: ${String(error.message).replace(/\S+@\S+|[0-9a-f]{32,}|[A-Za-z0-9_-]{40,}/g, "<hidden>")}`);
} finally {
    // Aufräumen auch nach einem Fehler: nur die Prozesse, die diese Probe gestartet hat, je über ihre PID
    if (hud) await stopHud().catch(() => { });
    for (const child of daemons) if (await alive(child.pid).catch(() => false)) await killOwn(child.pid);
    await browser.close();
    stream.close();
    // Bei einem Fehler bleibt der Temp-Ordner mit den Logs der Zentrale zum Nachsehen
    if (checks.some((c) => !c.ok)) console.log(`kept for inspection (temp): ${tmp}`);
    else rmSync(tmp, { recursive: true, force: true });
}

const failed = checks.filter((c) => !c.ok);
for (const c of checks) console.log(`${c.ok ? "PASS" : "FAIL"}  ${c.name}${c.detail ? "  (" + c.detail + ")" : ""}`);
console.log(`\n${checks.length - failed.length}/${checks.length} checks passed`);
if (failed.length) {
    console.log("FAILED:", failed.map((f) => f.name).join(" | "));
    process.exitCode = 1;
}

// Der erste freie Port der Liste (17451, sonst 41999)
async function freePort(candidates) {
    for (const port of candidates) {
        const free = await new Promise((resolveFree) => {
            const server = createNetServer();
            server.once("error", () => resolveFree(false));
            server.listen(port, "127.0.0.1", () => server.close(() => resolveFree(true)));
        });
        if (free) return port;
    }
    throw new Error(`no free port in ${candidates.join(", ")}`);
}

function pickLan() {
    for (const list of Object.values(networkInterfaces()))
        for (const n of list ?? [])
            if (n.family === "IPv4" && !n.internal && !n.address.startsWith("169.254.")) return n.address;
    return null;
}
