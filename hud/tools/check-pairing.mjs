// Prüft die Kopplung so, wie sie einem Menschen begegnet: die Zentrale am PC (127.0.0.1), und das Handy als eigener
// Browser-Kontext über eine Netzadresse dieses PCs. Die Adresse steht nicht im Skript; sie kommt aus /api/hub/access.
// Die Ausgabe nennt keine Adressen, keine Codes und keine Tokens.
//
//   node tools/check-pairing.mjs [--port 8090]
//
// Vorher läuft die Zentrale mit --pairing-file <Temp>\pairing.dat (Netzschalter darin an), --hub-config <Temp>\stykker.json,
// --no-browser --no-tray. Der Browser ist Google Chrome (wie bei check-ui.mjs).
import { chromium } from "playwright-core";

const args = process.argv.slice(2);
const value = (name, fallback) => {
    const i = args.indexOf(name);
    return i >= 0 && i + 1 < args.length ? args[i + 1] : fallback;
};
const port = Number(value("--port", "8090"));
const hub = `http://127.0.0.1:${port}`;

const checks = [];
const check = (name, ok, detail = "") => {
    checks.push({ name, ok: !!ok });
    console.log(`${ok ? "PASS" : "FAIL"}  ${name}${detail ? `  (${detail})` : ""}`);
};
const access = async () => (await fetch(`${hub}/api/hub/access`, { cache: "no-store" })).json();
const digitsOf = (text) => text.replace(/\D/g, "");
const post = (url, body) => fetch(url, { method: "POST", headers: { "Content-Type": "application/json" }, body: body ?? "{}" });

const first = await access();
if (!first.local || !first.remote || !first.listening) {
    console.log("The hub must run with network access on and the switch on at start (see the header of this file).");
    process.exit(2);
}
// Die Adresse, unter der dieser PC im Netz antwortet (die erste IPv4-Adresse der Liste)
const lan = first.qrAddresses.find((a) => /^\d+\.\d+\.\d+\.\d+$/.test(a));
if (!lan) {
    console.log("No LAN address to test the phone with.");
    process.exit(2);
}
const phoneBase = `http://${lan}:${port}`;

const browser = await chromium.launch({ channel: "chrome", headless: true });
const problems = [];

try {
    // ── PC: die Zentrale mit dem QR-Code ──
    const pcContext = await browser.newContext({ viewport: { width: 1200, height: 900 } });
    const pc = await pcContext.newPage();
    pc.on("pageerror", () => problems.push("pc"));
    pc.on("console", (m) => { if (m.type() === "error") problems.push("pc"); });
    await pc.goto(`${hub}/hub`, { waitUntil: "load" });
    const qrShown = await pc.waitForFunction(() => {
        const img = document.getElementById("qr");
        return !document.getElementById("qr-box").hidden && img.complete && img.naturalWidth > 0;
    }, null, { timeout: 15000 }).then(() => true, () => false);
    const qr = await pc.evaluate(() => ({
        src: document.getElementById("qr").getAttribute("src") || "",
        address: document.getElementById("qr-address").value,
        options: [...document.querySelectorAll("#qr-address option")].map((o) => o.value),
    }));
    const offered = (await access()).qrAddresses;
    check("1 PC: the QR code shows under Devices, fetched from /api/hub/qr for one of the shown addresses",
        qrShown && qr.src.startsWith("/api/hub/qr?address=") && offered.includes(qr.address),
        `${qr.options.length} addresses to choose from`);

    // 3: eine fremde Adresse wird abgewiesen
    const foreign = await fetch(`${hub}/api/hub/qr?address=192.0.2.1`, { cache: "no-store" });
    check("3 PC: /api/hub/qr with a foreign address gives 400", foreign.status === 400, `status ${foreign.status}`);

    // 2: Handy scannt – hier: die URL aus Adresse, Port und dem angezeigten Code
    const shownCode = (await access()).code;
    const phoneA = await browser.newContext();
    const pa = await phoneA.newPage();
    pa.on("pageerror", () => problems.push("phone-a"));
    await pa.goto(`http://${qr.address}:${port}/pair?code=${digitsOf(shownCode)}`, { waitUntil: "load" });
    const landed = await pa.waitForURL((u) => new URL(u).pathname === "/hub", { timeout: 10000 }).then(() => true, () => false);
    const cookieA = (await phoneA.cookies()).some((c) => c.name === "stykker_device");
    check("2 phone: the QR address signs it in and /hub loads", landed && cookieA && (await pa.title()) === "Stykker Hub",
        `page title "${await pa.title()}"`);

    // 11: ein neuer Code am PC: der QR-Code wechselt mit
    const srcBefore = await pc.evaluate(() => document.getElementById("qr").getAttribute("src"));
    const svgBefore = await fetch(`${hub}/api/hub/qr?address=${encodeURIComponent(qr.address)}`, { cache: "no-store" }).then((r) => r.text());
    await pc.click("#code-new");
    const changed = await pc.waitForFunction((old) => document.getElementById("qr").getAttribute("src") !== old, srcBefore, { timeout: 10000 })
        .then(() => true, () => false);
    const svgAfter = await fetch(`${hub}/api/hub/qr?address=${encodeURIComponent(qr.address)}`, { cache: "no-store" }).then((r) => r.text());
    check("11 PC: a new code changes the QR code", changed && svgBefore !== svgAfter, changed ? "picture changed" : "picture did not change");

    // 4: vom Handy sind die Verwaltungsadressen gesperrt (das Handy ist hier angemeldet)
    const phoneStatus = await pa.evaluate(async (address) => {
        const qrResponse = await fetch(`/api/hub/qr?address=${encodeURIComponent(address)}`, { cache: "no-store" });
        const denyResponse = await fetch("/api/hub/requests/none/deny", { method: "POST", headers: { "Content-Type": "application/json" }, body: "{}" });
        return [qrResponse.status, denyResponse.status];
    }, qr.address);
    check("4 phone: /api/hub/qr and /api/hub/requests/... give 403", phoneStatus[0] === 403 && phoneStatus[1] === 403,
        `statuses ${phoneStatus.join(", ")}`);

    // 5: ein zweites, unangemeldetes Handy zeigt die Ziffern
    const phoneB = await browser.newContext();
    const pb = await phoneB.newPage();
    pb.on("pageerror", () => problems.push("phone-b"));
    await pb.goto(`${phoneBase}/pair`, { waitUntil: "load" });
    await pb.click("#rq-go");
    await pb.waitForSelector("#rq .digits", { timeout: 10000 });
    const digitsB = (await pb.textContent("#rq .digits")).trim();
    check("5 phone: 'Show a code on this device instead' shows six digits", /^\d{3} \d{3}$/.test(digitsB), `${digitsB.replace(/\d/g, "•")}`);

    // 6: die Anfrage steht bei „Waiting devices“ mit denselben Ziffern
    const listed = await pc.waitForFunction((d) => [...document.querySelectorAll("#waiting-list .waiting-code")]
        .some((e) => e.textContent === d), digitsB, { timeout: 10000 }).then(() => true, () => false);
    check("6 PC: the request is under Waiting devices with the same digits", listed);

    // 7: Freigabe als Zuschauer – das Handy landet ohne Neuladen auf der Startseite
    const beforeIds = (await access()).devices.map((d) => d.id);
    await pc.fill("#approve-code", digitsB);
    await pc.click("#approve-viewer");
    const approvedLanded = await pb.waitForURL((u) => new URL(u).pathname === "/hub", { timeout: 15000 }).then(() => true, () => false);
    const afterDevices = (await access()).devices;
    const added = afterDevices.filter((d) => !beforeIds.includes(d.id));
    check("7 phone lands on /hub after 'Approve as viewer'; the device is viewer for hud",
        approvedLanded && added.length === 1 && added[0].roles.hud === "viewer",
        `new devices ${added.length}, role ${added[0]?.roles.hud ?? "none"}`);

    // 8: ein weiteres Handy fragt an, der PC lehnt ab – „denied“, und kein neues Gerät
    const phoneC = await browser.newContext();
    const pcx = await phoneC.newPage();
    pcx.on("pageerror", () => problems.push("phone-c"));
    await pcx.goto(`${phoneBase}/pair`, { waitUntil: "load" });
    await pcx.click("#rq-go");
    await pcx.waitForSelector("#rq .digits", { timeout: 10000 });
    const digitsC = (await pcx.textContent("#rq .digits")).trim();
    await pc.waitForFunction((d) => [...document.querySelectorAll("#waiting-list .waiting-code")]
        .some((e) => e.textContent === d), digitsC, { timeout: 10000 });
    await pc.evaluate((d) => {
        const row = [...document.querySelectorAll(".waiting-row")].find((r) => r.querySelector(".waiting-code")?.textContent === d);
        row?.querySelector("button")?.click();
    }, digitsC);
    const denied = await pcx.waitForFunction(() => /denied/i.test(document.getElementById("rq")?.innerText || ""), null, { timeout: 10000 })
        .then(() => true, () => false);
    const countAfterDeny = (await access()).devices.length;
    const cookieC = (await phoneC.cookies()).some((c) => c.name === "stykker_device");
    check("8 phone shows 'denied'; PC denies; no new device", denied && !cookieC && countAfterDeny === afterDevices.length,
        `devices ${countAfterDeny}, was ${afterDevices.length}`);

    // 9: Antworten ohne Token; das Cookie nur als Set-Cookie bei Freigabe
    const requestResponse = await fetch(`${phoneBase}/pair/request`, { method: "POST", headers: { "Content-Type": "application/json" }, body: "{}" });
    const requestText = await requestResponse.text();
    const requestJson = JSON.parse(requestText);
    const pollBody = JSON.stringify({ id: requestJson.id, secret: requestJson.secret });
    const pendingResponse = await fetch(`${phoneBase}/pair/poll?cookie=1`, { method: "POST", headers: { "Content-Type": "application/json" }, body: pollBody });
    const pendingText = await pendingResponse.text();
    check("9a request and pending poll answers have no token and no Set-Cookie",
        !/token/i.test(requestText) && !/token/i.test(pendingText) && !pendingResponse.headers.get("set-cookie")
        && JSON.parse(pendingText).status === "pending",
        `status ${JSON.parse(pendingText).status}`);
    await pc.fill("#approve-code", requestJson.code);
    await pc.click("#approve-admin");
    const approvedResponse = await fetch(`${phoneBase}/pair/poll?cookie=1`, { method: "POST", headers: { "Content-Type": "application/json" }, body: pollBody });
    const approvedText = await approvedResponse.text();
    const setCookie = approvedResponse.headers.get("set-cookie") || "";
    const tokenValue = /stykker_device=([^;]+)/.exec(setCookie)?.[1] ?? "";
    check("9b approved poll: the token only as Set-Cookie, never in the body",
        JSON.parse(approvedText).status === "approved" && tokenValue.length > 0 && !approvedText.includes(tokenValue) && !/token/i.test(approvedText),
        `status ${JSON.parse(approvedText).status}`);

    // 10: neun Anfragen hintereinander – die neunte wird abgewiesen
    for (const r of (await access()).requests) await fetch(`${hub}/api/hub/requests/${r.id}/deny`, { method: "POST" });
    const statuses = [];
    for (let i = 0; i < 9; i++) {
        const r = await fetch(`${phoneBase}/pair/request`, { method: "POST", headers: { "Content-Type": "application/json" }, body: "{}" });
        statuses.push(r.status);
    }
    check("10 nine requests in a row: the ninth is refused", statuses.slice(0, 8).every((s) => s === 200) && statuses[8] === 429,
        statuses.join(" "));
    for (const r of (await access()).requests) await fetch(`${hub}/api/hub/requests/${r.id}/deny`, { method: "POST" });

    check("no errors on the pages", problems.length === 0, `${problems.length} errors`);
} finally {
    await browser.close();
}

const failed = checks.filter((c) => !c.ok).length;
console.log(`\n${checks.length - failed} of ${checks.length} checks passed`);
process.exit(failed === 0 ? 0 : 1);
