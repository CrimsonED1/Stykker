// StykkerSYS — die Laufwerkseite (/disks). Die Messschleife läuft im Server; hier wird nur gelesen und gezeichnet.
// Die Werte kommen im Sekundentakt aus /api/disks. Fehlt eine Zahl, steht „–" (nie eine erfundene 0).
// Aussehen und Markup wie in StykkerLLM: die Bausteine hw-* des Design-Systems (components/hardware.css).
// Der Verlauf (Sparklines) sammelt die Seite selbst aus den Abrufen seit dem Öffnen; der Server hat keinen Verlauf.
(() => {
    "use strict";

    const volumesEl = document.getElementById("disk-volumes");
    if (!volumesEl) return;      // nur auf der Laufwerkseite

    const $ = (id) => document.getElementById(id);
    const esc = (s) => String(s ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
    const KIND = { Nvme: "NVMe", Ssd: "SSD", Hdd: "HDD", Usb: "USB", Unknown: "unknown" };
    const POINTS = 300;                    // wie StykkerLLM: die letzten 300 Abrufe
    const history = new Map();             // Laufwerksbuchstabe -> [{ read, write, busy }]

    // Bytes wie in StykkerLLM: Zweierpotenzen, eine Nachkommastelle bei kleinen Zahlen
    function size(bytes) {
        if (bytes === null || bytes === undefined || !Number.isFinite(bytes) || bytes < 0) return "–";
        const units = ["B", "KB", "MB", "GB", "TB"];
        let v = bytes, u = 0;
        while (v >= 1024 && u < units.length - 1) { v /= 1024; u++; }
        const digits = v >= 100 || u === 0 ? 0 : 1;
        return `${v.toFixed(digits)} ${units[u]}`;
    }
    const rate = (bps) => (bps === null || bps === undefined ? "–" : `${size(bps)}/s`);
    const pct = (v) => (v === null || v === undefined ? "–" : `${Math.round(v)} %`);
    const queue = (v) => (v === null || v === undefined ? "–" : v.toFixed(2));
    const kindLabel = (kind) => KIND[kind] ?? kind;
    const kindChip = (kind) => `<span class="hw-kind" data-k="${esc(kind)}" title="${esc(kindLabel(kind))}">${esc(kindLabel(kind))}</span>`;

    // Eine Sparkline wie in StykkerLLM (300 × 70): der neueste Wert steht rechts, fehlende Werte fallen aus der Linie
    function points(values, max) {
        if (values.length < 2 || !(max > 0)) return "";
        const out = [];
        values.forEach((v, i) => {
            if (v === null || v === undefined || !Number.isFinite(v)) return;
            const x = (i * 300) / (values.length - 1);
            const y = 70 - Math.min(1, Math.max(0, v / max)) * 70;
            out.push(`${x.toFixed(1)},${y.toFixed(1)}`);
        });
        return out.join(" ");
    }
    // Der größte Wert der Reihe, mindestens floor (eine ruhige Leitung füllt nicht die ganze Höhe)
    const scale = (values, floor) => Math.max(floor, ...values.filter((v) => v !== null && v !== undefined && Number.isFinite(v)), 0);

    function remember(volumes) {
        for (const v of volumes) {
            const list = history.get(v.letter) ?? [];
            list.push({ read: v.readBytesPerSec ?? null, write: v.writeBytesPerSec ?? null, busy: v.busyPercent ?? null });
            if (list.length > POINTS) list.splice(0, list.length - POINTS);
            history.set(v.letter, list);
        }
    }

    function sparks(letter) {
        const list = history.get(letter) ?? [];
        if (list.length < 2) return `<div class="hw-subh">History</div><div class="hw-sub">Not enough readings yet.</div>`;
        const reads = list.map((p) => p.read), writes = list.map((p) => p.write), busy = list.map((p) => p.busy);
        const readTop = scale(reads, 1024 * 1024), writeTop = scale(writes, 1024 * 1024);
        return `<div class="hw-subh">History</div><div class="hw-sparks">
            <div><div class="hw-spark-hd">Read · max ${esc(rate(readTop))}</div>
                <svg class="hw-spark" viewBox="0 0 300 70" preserveAspectRatio="none"><polyline class="line" fill="none" points="${points(reads, readTop)}"/></svg></div>
            <div><div class="hw-spark-hd">Write · max ${esc(rate(writeTop))}</div>
                <svg class="hw-spark" viewBox="0 0 300 70" preserveAspectRatio="none"><polyline class="line second" fill="none" points="${points(writes, writeTop)}"/></svg></div>
            <div><div class="hw-spark-hd">Busy · max 100 %</div>
                <svg class="hw-spark" viewBox="0 0 300 70" preserveAspectRatio="none"><polyline class="line warn" fill="none" points="${points(busy, 100)}"/></svg></div>
        </div>`;
    }

    function render(snap) {
        const disks = snap.disks;
        const volumes = disks?.volumes ?? [];
        const physical = disks?.disks ?? [];
        const procs = snap.processes ?? [];
        $("v-drives").textContent = disks ? String(volumes.length) : "–";
        $("v-disk-time").textContent = snap.at ? new Date(snap.at).toLocaleTimeString() : "–";

        $("disk-notes").innerHTML = (snap.notes ?? []).map((n) => `<p class="sys-note hw-sub">${esc(n)}</p>`).join("");

        remember(volumes);
        volumesEl.innerHTML = (volumes.length === 0 ? `<span class="hw-sub">–</span>` : "")
            + volumes.map((v) => {
                const total = v.totalBytes ?? null, free = v.freeBytes ?? null;
                const used = total !== null && free !== null ? total - free : null;
                const frac = total ? Math.min(1, Math.max(0, used / total)) : 0;
                const label = v.label ? `${esc(v.label)} ` : "";
                return `<div class="hw-vol" data-drive="${esc(v.letter)}">
                    <div class="hw-vol-top">
                        <span class="hw-letter">${esc(v.letter)}:</span>
                        ${kindChip(v.kind)}
                        <span class="hw-sub">${label}${esc(v.fileSystem)}</span>
                        <span class="hw-free">${size(used)} used · ${size(free)} free · ${size(total)}</span>
                    </div>
                    <div class="hw-bar"><i style="--w:${Math.round(frac * 100)}%"></i></div>
                    <div class="hw-io">
                        <span>Read <b>${rate(v.readBytesPerSec)}</b></span>
                        <span>Write <b>${rate(v.writeBytesPerSec)}</b></span>
                        <span>Busy <b>${pct(v.busyPercent)}</b></span>
                        <span>Queue <b>${queue(v.queueLength)}</b></span>
                    </div>
                    ${sparks(v.letter)}
                </div>`;
            }).join("");

        $("disk-physical").innerHTML = `<table class="hw-table"><tr><th>Type</th><th class="num">#</th><th>Model</th><th class="num">Read</th><th class="num">Write</th><th class="num">Busy</th><th class="num">Queue</th></tr>`
            + (physical.length === 0 ? `<tr><td colspan="7" class="hw-sub">–</td></tr>` : "")
            + physical.map((p) => `<tr>
                    <td>${kindChip(p.kind)}</td>
                    <td class="num">${esc(p.number)}</td>
                    <td class="hw-name" title="${esc(p.model)}">${esc(p.model) || "–"}</td>
                    <td class="num">${rate(p.readBytesPerSec)}</td>
                    <td class="num">${rate(p.writeBytesPerSec)}</td>
                    <td class="num">${pct(p.busyPercent)}</td>
                    <td class="num">${queue(p.queueLength)}</td>
                </tr>`).join("") + `</table>`;

        $("disk-procs").innerHTML = `<table class="hw-table"><tr><th>Name</th><th class="num">PID</th><th class="num">Read</th><th class="num">Write</th></tr>`
            + (procs.length === 0 ? `<tr><td colspan="4" class="hw-sub">No program reads or writes the disks right now.</td></tr>` : "")
            + procs.map((p) => `<tr>
                    <td class="hw-name" title="${esc(p.name)}">${esc(p.name)}</td>
                    <td class="num">${esc(p.pid)}</td>
                    <td class="num">${rate(p.readBytesPerSec)}</td>
                    <td class="num">${rate(p.writeBytesPerSec)}</td>
                </tr>`).join("") + `</table>`;
    }

    // Nur lesen, was sichtbar ist: ein verborgenes Fenster fragt nicht, und der Server hört dann von selbst auf zu messen
    async function poll() {
        if (document.hidden) return;
        try {
            const response = await fetch("/api/disks", { cache: "no-store" });
            if (response.ok) render(await response.json());
        } catch {
            /* ein ausgefallener Abruf darf die Anzeige nicht anhalten: die alte Anzeige bleibt stehen */
        }
    }
    document.addEventListener("visibilitychange", () => { if (!document.hidden) poll(); });
    poll();
    setInterval(poll, 1000);
})();
