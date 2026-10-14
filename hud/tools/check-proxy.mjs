// Prüft die Weiterleitung fremder Dienste der Zentrale (docs/plan-stykkernode.md, Abschnitt 4.5 bis 4.7; Auftrag Schritt 5,
// Abschnitt 8.2). Startet eine Zentrale mit einer Temp-Konfiguration, den Test-Daemon aus dem Qwen-Fork und einen kleinen
// Strom- und WebSocket-Server (Node-http, ohne Pakete). "Handy" = Anfragen an die Netzadresse dieser Maschine.
//
//   node tools/check-proxy.mjs --qwen-cli <Pfad zu cli.js> [--hud-dll <Pfad zu StykkerHUD-Server.dll>]
//
// Der Pfad zum Qwen-CLI gehört nicht in die Repo: er kommt von der Kommandozeile und landet nur in der Temp-Konfiguration.
// Die Ablagen liegen in einem Temp-Ordner; die echte Konfiguration und die echte Kopplung werden nie gelesen.
import { chromium } from "playwright-core";
import { spawn, execFile } from "node:child_process";
import { createServer, request as httpRequest } from "node:http";
import { createHash, randomBytes } from "node:crypto";
import { mkdtempSync, writeFileSync, existsSync, rmSync } from "node:fs";
import { tmpdir, networkInterfaces } from "node:os";
import { join, resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";

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

// Ports dieses Probelaufs (Auftrag, Abschnitt 7): Zentrale 8090, Weiterleitung 8091, Test-Daemon 41999.
// Der Strom-Dienst im Skript bekommt 8092 und 41998.
const HUD = 8090, QWEN_FWD = 8091, STREAM_FWD = 8092, DAEMON = 41999, STREAM = 41998;
const PHONE_UA = "Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 Chrome/141.0 Mobile Safari/537.36";
const LAN = pickLan();

const tmp = mkdtempSync(join(tmpdir(), "stykker-proxy-check-"));
const qwenHome = join(tmp, "qwen-home");
const configFile = join(tmp, "stykker.json");
const pairingFile = join(tmp, "pairing.dat");
const servicesFile = join(tmp, "services.dat");

const config = () => ({
    modules: [
        {
            id: "qwen", name: "Qwen Code", external: true, port: QWEN_FWD, target: `http://127.0.0.1:${DAEMON}`,
            start: { command: "node", args: [qwenCli, "serve", "--port", String(DAEMON), "--no-open"], env: { QWEN_HOME: qwenHome }, tokenEnv: "QWEN_SERVER_TOKEN" },
        },
        { id: "stream", name: "Stream test", external: true, port: STREAM_FWD, target: `http://127.0.0.1:${STREAM}` },
    ],
});
writeFileSync(configFile, JSON.stringify(config(), null, 2));

const checks = [];
const check = (name, ok, detail = "") => { checks.push({ name, ok: !!ok, detail: String(detail) }); };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ── PowerShell: nur für Prozesse, die Fenster-Konsole der Zentrale und die DPAPI-Ablage ──
function ps(script) {
    const encoded = Buffer.from(script, "utf16le").toString("base64");
    return new Promise((resolvePs, reject) =>
        execFile("powershell", ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], { timeout: 60000, windowsHide: true },
            (err, stdout) => (err && !stdout ? reject(err) : resolvePs(stdout.trim()))));
}

// Die Zentrale beenden wie beim Schließen ihres Fensters: taskkill ohne /F (ein Fenster-Signal, kein Erzwingen).
// Das geordnete Herunterfahren mit Strg+C ließ sich aus der Probe nicht auslösen; das Job-Objekt des Dienstes muss
// deshalb auch so enden, ohne dass die Zentrale noch etwas tut.
const taskkill = (pid) => new Promise((resolveKill) =>
    execFile("taskkill", ["/PID", String(pid)], { windowsHide: true, timeout: 20000 },
        (err, stdout) => resolveKill(err ? "failed" : stdout.trim())));

const childNodePids = async (parentPid) => {
    const out = await ps(`Get-CimInstance Win32_Process -Filter "ParentProcessId=${parentPid}" | Where-Object { $_.Name -eq 'node.exe' } | ForEach-Object { $_.ProcessId }`);
    return out.split(/\s+/).filter(Boolean).map(Number);
};
const commandLine = (pid) => ps(`(Get-CimInstance Win32_Process -Filter "ProcessId=${pid}").CommandLine`);
const listeners = (port) => ps(`Get-NetTCPConnection -LocalPort ${port} -State Listen -ErrorAction SilentlyContinue | ForEach-Object { $_.LocalAddress } | Sort-Object -Unique`);

// Die Token der Ablage services.dat, mit DPAPI des Benutzers entschlüsselt (nur diese Probe, nur die Temp-Ablage).
const serviceTokens = async () => {
    const script = `
Add-Type -AssemblyName System.Security
$raw = [IO.File]::ReadAllText('${servicesFile.replace(/'/g, "''")}').Trim()
$bytes = [Convert]::FromBase64String($raw)
$entropy = [Text.Encoding]::UTF8.GetBytes('Stykker.services.v1')
$plain = [System.Security.Cryptography.ProtectedData]::Unprotect($bytes, $entropy, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
[Text.Encoding]::UTF8.GetString($plain)`;
    return JSON.parse(await ps(script)).tokens;
};

// ── HTTP ──
function http({ host = "127.0.0.1", port, path, method = "GET", headers = {} }) {
    return new Promise((resolveHttp, reject) => {
        const req = httpRequest({ host, port, path, method, headers, timeout: 15000 }, (res) => {
            const chunks = [];
            res.on("data", (c) => chunks.push(c));
            res.on("end", () => resolveHttp({ status: res.statusCode, headers: res.headers, body: Buffer.concat(chunks).toString("utf8") }));
        });
        req.on("timeout", () => req.destroy(new Error("timeout")));
        req.on("error", reject);
        req.end();
    });
}
const local = (port, path, headers, method) => http({ port, path, headers, method });
const phone = (port, path, headers = {}, method) => http({ host: LAN, port, path, method, headers: { "User-Agent": PHONE_UA, ...headers } });
const tryHttp = (opts) => http(opts).catch((e) => ({ status: 0, error: e.code || e.message }));

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
// Die Zentrale läuft als eigener Prozess mit eigener (verborgener) Konsole, damit Strg+C sie geordnet beendet. Ihre
// Ausgabe landet in Dateien im Temp-Ordner. Die Dienste, die sie startet, merkt sich die Probe vor dem Beenden.
const logFiles = [];
const ownedDaemons = new Set();
let hud = null;
// Die Logs liest PowerShell: die Umleitung der Zentrale hält die Datei offen, und Node liest sie dann nicht.
const readLogs = () => ps(logFiles.map((f) => `if (Test-Path -LiteralPath ${q(f)}) { Get-Content -Raw -LiteralPath ${q(f)} }`).join("\n"))
    .catch(() => "");
const q = (s) => `'${String(s).replace(/'/g, "''")}'`;
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
    const pid = hud.pid;
    const kids = await childNodePids(pid).catch(() => []);
    kids.forEach((k) => ownedDaemons.add(k));
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
    return { sent, gone, forced, seconds, kids };
}
const alive = (pid) => ps(`if (Get-Process -Id ${pid} -ErrorAction SilentlyContinue) { 'yes' } else { 'no' }`).then((s) => s === "yes");
const anyAlive = async (pids) => { for (const p of pids) if (await alive(p)) return true; return false; };

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
        // Gerendert? Ein Text allein sagt wenig: die Oberfläche zählt, sobald sie Elemente mit Größe hat.
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
let tokens = {};
let daemonPid = null;
let other = null;   // ein Daemon, den die Probe von Hand startet (Teil 11)
try {
    // Teil 1: Netzschalter aus (nur diese Maschine)
    await startHud();
    check("hub starts and answers on its port", await hubUp(), `port ${HUD}`);
    tokens = await serviceTokens();
    check("the hub made one token per forwarded module", !!tokens.qwen && !!tokens.stream && tokens.qwen !== tokens.stream,
        `ids: ${Object.keys(tokens).join(", ")}`);
    await waitUntil(async () => (await tryHttp({ port: DAEMON, path: "/health" })).status === 401);
    daemonPid = (await childNodePids(hud.pid))[0] ?? null;

    // 1: der Test-Daemon hört nur auf 127.0.0.1, im Log steht kein Token, die Kommandozeile hat keins
    const daemonListen = (await listeners(DAEMON)).split(/\s+/).filter(Boolean);
    check("1 test daemon listens on 127.0.0.1 only", daemonListen.length > 0 && daemonListen.every((a) => a === "127.0.0.1"), `listen: ${daemonListen.join(",")}`);
    const log = await readLogs();
    check("1 hub log holds no token", log.length > 0 && !log.includes(tokens.qwen) && !log.includes(tokens.stream), `${log.length} chars checked`);
    const cmd = daemonPid ? await commandLine(daemonPid) : "";
    check("1 daemon command line has --hostname 127.0.0.1 and --require-auth, no token",
        cmd.includes("--hostname 127.0.0.1") && cmd.includes("--require-auth") && !cmd.includes(tokens.qwen), `pid ${daemonPid}`);

    // 3: die API des Daemons ohne Token: 401; mit dem Token aus services.dat: 200
    const noToken = await tryHttp({ port: DAEMON, path: "/capabilities" });
    const noTokenHealth = await tryHttp({ port: DAEMON, path: "/health" });
    check("3 daemon /capabilities without token -> 401", noToken.status === 401, `status ${noToken.status}`);
    check("3 daemon /health without token -> 401", noTokenHealth.status === 401, `status ${noTokenHealth.status}`);
    const withToken = await tryHttp({ port: DAEMON, path: "/capabilities", headers: { Authorization: `Bearer ${tokens.qwen}` } });
    check("token in services.dat is the daemon's token", withToken.status === 200, `status ${withToken.status}`);

    // 2: am PC /hub, Qwen wählen: Oberfläche im Frame, WebSocket /acp geht auf
    const pc = await pcOpensQwen();
    check("2 Qwen opens in the frame at the PC, no sign-in page", pc.url.includes(`:${QWEN_FWD}/`) && !pc.signIn && pc.elements > 5 && pc.bodyWidth > 0,
        `frame ${pc.url.replace(/^https?:\/\/[^/]+/, "")}, ${pc.elements} elements, body ${pc.bodyWidth} px, ${pc.bodyChars} text chars`);
    check("2 WebSocket /acp opens through the forwarder", pc.ws === "open", `ws: ${pc.ws}`);

    // 7 (vom PC): fremde Origin, auch als WebSocket -> 403
    const foreignHttp = await tryHttp({ port: QWEN_FWD, path: "/capabilities", headers: { Origin: "http://evil.example" } });
    const foreignWs = await wsOpen({ port: QWEN_FWD, path: "/acp", headers: { Origin: "http://evil.example" } });
    check("7 foreign Origin on 8091 -> 403", foreignHttp.status === 403, `status ${foreignHttp.status}`);
    check("7 foreign Origin on 8091 as WebSocket -> 403", foreignWs.status === 403, `status ${foreignWs.status}`);

    // 7 (DNS-Rebinding): fremder Name im Host-Kopf, von dieser Maschine, auch mit passender Origin -> 403, auch als WebSocket
    const rebindHeaders = { Host: "evil.example:8091", Origin: "http://evil.example:8091" };
    const rebindHttp = await tryHttp({ port: QWEN_FWD, path: "/capabilities", headers: rebindHeaders });
    const rebindWs = await wsOpen({ port: QWEN_FWD, path: "/acp", headers: rebindHeaders });
    check("7 foreign Host name on 8091 (DNS rebinding) -> 403", rebindHttp.status === 403, `status ${rebindHttp.status}: ${rebindHttp.body?.slice(0, 60) ?? ""}`);
    check("7 foreign Host name on 8091 as WebSocket -> 403", rebindWs.status === 403, `status ${rebindWs.status}`);

    // 8: was beim Daemon ankommt: kein stykker_device, Authorization gesetzt, Host des Ziels
    await local(STREAM_FWD, "/probe", { Cookie: "a=1; stykker_device=device-secret-value; b=2", Origin: `http://127.0.0.1:${STREAM_FWD}` });
    const last = JSON.parse((await local(STREAM, "/last")).body);
    check("8 device cookie is removed, other cookies stay", last.cookie === "a=1; b=2", `cookie seen: ${last.cookie}`);
    check("8 Authorization is set to the service token", last.authorization === `Bearer ${tokens.stream}`, `set: ${last.authorization ? "yes" : "no"}`);
    check("8 Host is the target's", last.host === `127.0.0.1:${STREAM}`, `host ${last.host}`);
    check("8 Origin is rewritten to the target", last.origin === `http://127.0.0.1:${STREAM}`, `origin ${last.origin}`);

    // 9: Antwortstrom: der erste Teil kommt an, bevor der letzte geschrieben ist; alle 20 Teile
    const stamps = [];
    await new Promise((resolveStream, reject) => {
        const req = httpRequest({ host: "127.0.0.1", port: STREAM_FWD, path: "/stream" }, (res) => {
            res.on("data", (c) => { for (const _ of c.toString().matchAll(/part \d+/g)) stamps.push(Date.now()); });
            res.on("end", resolveStream);
        });
        req.on("error", reject);
        req.end();
    });
    check("9 all 20 parts arrive through the forwarder", stamps.length === 20, `${stamps.length} parts`);
    check("9 parts arrive while the stream runs, not at the end", stamps.length === 20 && stamps[19] - stamps[0] >= 4000,
        `first to last ${stamps.length ? stamps[stamps.length - 1] - stamps[0] : 0} ms`);

    // 10: WebSocket-Echo über die Weiterleitung, 50 Nachrichten hin und zurück
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
    check("10 50 messages echo back through the forwarder", Array.isArray(echoed) && echoed.length === 50 && echoed[49] === "m49",
        Array.isArray(echoed) ? `${echoed.length} echoes` : echoed);

    // Netzschalter einschalten (wirkt nach dem nächsten Start), dann beenden
    await local(HUD, "/api/hub/remote?on=true", {}, "POST");
    const stop1 = await stopHud();
    check("the hub ends on taskkill without /F, not forced", stop1.gone && !stop1.forced, `taskkill: ${stop1.sent}, after ${stop1.seconds} s, forced: ${stop1.forced}`);
    check("11 the test daemon the hub started is gone", !(await anyAlive(stop1.kids)), `daemons started by the hub: ${stop1.kids.length}`);

    // Teil 2: Netzschalter an, Handy
    await startHud();
    check("hub restarts with network access on", await hubUp(), "");
    const lanOk = !!LAN;
    check("a LAN address exists for the phone checks", lanOk, LAN ? "found" : "none");
    if (lanOk) {
        // 4: Handy ohne Gerät auf 8091 -> 401
        const unpaired = await tryHttp({ host: LAN, port: QWEN_FWD, path: "/", headers: { "User-Agent": PHONE_UA } });
        check("4 phone without device on 8091 -> 401", unpaired.status === 401, `status ${unpaired.status}: ${unpaired.body?.slice(0, 80) ?? ""}`);

        // Kopplung: Code am PC abholen, das Handy meldet sich an der Zentrale an
        const access = JSON.parse((await local(HUD, "/api/hub/access")).body);
        const paired = await phone(HUD, `/pair?code=${access.code.replace(/\s/g, "")}`);
        const cookieHeader = paired.headers["set-cookie"]?.[0] ?? "";
        const device = (cookieHeader.match(/stykker_device=([^;]+)/) || [])[1];
        check("phone pairs with the code (cookie set)", paired.status === 302 && !!device, `status ${paired.status}`);

        // 5: gekoppelt, aber Rolle viewer für qwen
        const hubList = JSON.parse((await phone(HUD, "/api/hub", { Cookie: `stykker_device=${device}` })).body);
        check("5 /api/hub does not name qwen for a viewer", !hubList.modules.some((m) => m.id === "qwen"),
            `modules seen: ${hubList.modules.map((m) => m.id).join(", ") || "none"}`);
        const viewer = await phone(QWEN_FWD, "/", { Cookie: `stykker_device=${device}` });
        check("5 phone viewer on 8091 -> 403", viewer.status === 403, `status ${viewer.status}: ${viewer.body.slice(0, 90)}`);

        // 7 (vom Handy): fremde Origin mit gültigem Gerät, auch als WebSocket -> 403
        const foreignPhone = await phone(QWEN_FWD, "/capabilities", { Cookie: `stykker_device=${device}`, Origin: "http://evil.example" });
        const foreignPhoneWs = await wsOpen({ host: LAN, port: QWEN_FWD, path: "/acp", headers: { Cookie: `stykker_device=${device}`, Origin: "http://evil.example", "User-Agent": PHONE_UA } });
        check("7 phone with foreign Origin on 8091 -> 403", foreignPhone.status === 403, `status ${foreignPhone.status}`);
        check("7 phone with foreign Origin on 8091 as WebSocket -> 403", foreignPhoneWs.status === 403, `status ${foreignPhoneWs.status}`);

        // 6: am PC Rolle admin für qwen setzen (in der Oberfläche), dann lädt Qwen am Handy
        const pcPage = await browser.newPage({ viewport: { width: 1200, height: 900 } });
        try {
            await pcPage.goto(`http://127.0.0.1:${HUD}/hub`, { waitUntil: "load" });
            await pcPage.locator("#devices tbody tr", { hasText: "Chrome on Android" })
                .locator('td[data-k="Qwen Code"] button[data-role="admin"]').click({ timeout: 15000 });
            await pcPage.waitForTimeout(1500);
        } finally {
            await pcPage.close();
        }
        const hubAfter = JSON.parse((await phone(HUD, "/api/hub", { Cookie: `stykker_device=${device}` })).body);
        check("6 after admin for qwen, /api/hub names qwen", hubAfter.modules.some((m) => m.id === "qwen" && m.up === true),
            `modules seen: ${hubAfter.modules.map((m) => m.id).join(", ")}`);
        const adminPhone = await phone(QWEN_FWD, "/", { Cookie: `stykker_device=${device}` });
        check("6 phone admin on 8091 -> the Qwen page", adminPhone.status === 200, `status ${adminPhone.status}`);
        const framed = await phoneOpensQwen(device);
        check("6 Qwen loads in the frame on the phone", framed.url.includes(`:${QWEN_FWD}/`) && framed.elements > 5 && framed.bodyWidth > 0,
            `${framed.elements} elements, body ${framed.bodyWidth} px wide, ${framed.bodyChars} text chars, ${framed.htmlChars} html chars, ${framed.ready}`);
    }
    const stop2 = await stopHud();
    check("the second run ends on taskkill without /F, not forced", stop2.gone && !stop2.forced, `taskkill: ${stop2.sent}, after ${stop2.seconds} s, forced: ${stop2.forced}`);
    check("11 the daemons of the second run are gone", !(await anyAlive(stop2.kids)), `daemons started by the hub: ${stop2.kids.length}`);

    // 11 (zweiter Teil): ein schon laufender Daemon bleibt laufen
    other = spawn("node", [qwenCli, "serve", "--port", String(DAEMON), "--hostname", "127.0.0.1", "--no-open", "--require-auth"],
        { windowsHide: true, stdio: "ignore", env: { ...process.env, QWEN_HOME: join(tmp, "qwen-home-2"), QWEN_SERVER_TOKEN: "probe-other-token" } });
    const otherUp = await waitUntil(async () => (await tryHttp({ port: DAEMON, path: "/health" })).status === 401, 30000);
    check("11 a daemon started by hand answers on the target", otherUp, `pid ${other.pid}`);
    await startHud();
    check("11 hub run 2 with the target taken answers, starts nothing", await hubUp(), "");
    await sleep(1500);
    const hudKids = await childNodePids(hud.pid);
    check("11 the hub does not start a second daemon", hudKids.length === 0, `node children: ${hudKids.length}`);
    const answersLogged = await waitUntil(async () => (await readLogs()).includes("answers on http://127.0.0.1:41999"), 10000);
    check("11 the hub log says the target answers", answersLogged, `${logFiles.length} log files, ${(await readLogs()).length} chars read`);
    await stopHud();
    const stillUp = await tryHttp({ port: DAEMON, path: "/health" });
    check("11 the daemon not started by the hub keeps running", stillUp.status === 401, `status ${stillUp.status}`);
    other.kill();
    other = null;

    // 12: ohne die neuen Felder: kein Port, /hub wie vorher
    const plainFile = join(tmp, "plain.json");
    writeFileSync(plainFile, JSON.stringify({ modules: [{ id: "qwen", name: "Qwen Code", external: true, url: `http://127.0.0.1:${QWEN_FWD}/` }] }));
    await startHud(plainFile);
    check("12 hub runs with the old config", await hubUp(), "");
    const closed = await tryHttp({ port: QWEN_FWD, path: "/" });
    check("12 no extra port is open (8091 refuses)", closed.status === 0, closed.error ?? `status ${closed.status}`);
    const hubPage = await local(HUD, "/hub");
    check("12 /hub answers as before", hubPage.status === 200, `status ${hubPage.status}`);
    const plainList = JSON.parse((await local(HUD, "/api/hub")).body);
    check("12 external url is listed as before", plainList.modules.some((m) => m.id === "qwen" && m.url === `http://127.0.0.1:${QWEN_FWD}/`), "");
    check("12 no test daemon is started", (await tryHttp({ port: DAEMON, path: "/health" })).status === 0, "");
    await stopHud();

    // 12 (ohne stykker.json): die Vorgabe
    await startHud(join(tmp, "missing", "stykker.json"));
    check("12 without any stykker.json the defaults are listed", await hubUp(), "");
    const defaults = JSON.parse((await local(HUD, "/api/hub")).body);
    check("12 defaults: monitor, sys, llm", defaults.modules.map((m) => m.id).join(",") === "monitor,sys,llm",
        defaults.modules.map((m) => m.id).join(","));
    await stopHud();
} finally {
    // Aufräumen auch nach einem Fehler: nur die Prozesse, die diese Probe gestartet hat, je über ihre PID
    if (hud) await stopHud().catch(() => { });
    if (other) other.kill();
    for (const pid of ownedDaemons) if (await alive(pid).catch(() => false)) await ps(`Stop-Process -Id ${pid} -Force -ErrorAction SilentlyContinue`);
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

function pickLan() {
    for (const list of Object.values(networkInterfaces()))
        for (const n of list ?? [])
            if (n.family === "IPv4" && !n.internal && !n.address.startsWith("169.254.")) return n.address;
    return null;
}
