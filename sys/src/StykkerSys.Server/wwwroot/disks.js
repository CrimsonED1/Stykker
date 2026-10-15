// StykkerSYS — die Laufwerkseite (/disks). Die Messschleife läuft im Server; hier wird nur gelesen und gezeichnet.
// Die Werte kommen im Sekundentakt aus /api/disks. Fehlt eine Zahl, steht „–" (nie eine erfundene 0).
(() => {
    "use strict";

    const volumesEl = document.getElementById("disk-volumes");
    if (!volumesEl) return;      // nur auf der Laufwerkseite

    const $ = (id) => document.getElementById(id);
    const esc = (s) => String(s ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
    const KIND = { Nvme: "NVMe", Ssd: "SSD", Hdd: "HDD", Usb: "USB", Unknown: "unknown" };

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
    const kindChip = (kind) => `<span class="chipi" title="${esc(KIND[kind] ?? kind)}">${esc(KIND[kind] ?? kind)}</span>`;
    const bar = (frac) => `<span class="bar"><i style="width:${Math.round(Math.min(1, Math.max(0, frac)) * 100)}%"></i></span>`;

    function render(snap) {
        const disks = snap.disks;
        const volumes = disks?.volumes ?? [];
        const physical = disks?.disks ?? [];
        const procs = snap.processes ?? [];
        $("v-drives").textContent = disks ? String(volumes.length) : "–";
        $("v-disk-time").textContent = snap.at ? new Date(snap.at).toLocaleTimeString() : "–";

        $("disk-notes").innerHTML = (snap.notes ?? []).map((n) => `<p class="sys-note muted">${esc(n)}</p>`).join("");

        volumesEl.innerHTML = `<div class="req head"><span>Type</span><span>Drive</span><span>Used</span><span>Read</span><span>Write</span><span>Busy</span></div>`
            + (volumes.length === 0 ? `<div class="req"><span></span><span class="muted">–</span></div>` : "")
            + volumes.map((v) => {
                const total = v.totalBytes ?? null, free = v.freeBytes ?? null;
                const used = total !== null && free !== null ? total - free : null;
                const label = v.label ? ` · ${esc(v.label)}` : "";
                return `<div class="req" data-drive="${esc(v.letter)}">
                    <span class="cl">${kindChip(v.kind)}</span>
                    <span class="mdl"><span><b>${esc(v.letter)}:</b>${label} <span class="muted">${esc(v.fileSystem)}</span></span></span>
                    <span class="dur">${total ? bar(used / total) : ""}<span>${size(used)} / ${size(total)}</span></span>
                    <span class="mono">${rate(v.readBytesPerSec)}</span>
                    <span class="mono">${rate(v.writeBytesPerSec)}</span>
                    <span class="mono">${pct(v.busyPercent)}</span>
                </div>`;
            }).join("");

        $("disk-physical").innerHTML = `<div class="req head"><span>Type</span><span>#</span><span>Model</span><span>Read</span><span>Write</span><span>Busy</span><span>Queue</span></div>`
            + (physical.length === 0 ? `<div class="req"><span></span><span class="muted">–</span></div>` : "")
            + physical.map((p) => `<div class="req">
                    <span class="cl">${kindChip(p.kind)}</span>
                    <span class="mono">${esc(p.number)}</span>
                    <span class="mdl"><span title="${esc(p.model)}">${esc(p.model) || "–"}</span></span>
                    <span class="mono">${rate(p.readBytesPerSec)}</span>
                    <span class="mono">${rate(p.writeBytesPerSec)}</span>
                    <span class="mono">${pct(p.busyPercent)}</span>
                    <span class="mono">${queue(p.queueLength)}</span>
                </div>`).join("");

        $("disk-procs").innerHTML = `<div class="req head"><span></span><span>Name</span><span>PID</span><span>Read</span><span>Write</span></div>`
            + (procs.length === 0 ? `<div class="req"><span></span><span class="muted">No program reads or writes the disks right now.</span></div>` : "")
            + procs.map((p) => `<div class="req">
                    <span class="cl"></span>
                    <span class="mdl"><span title="${esc(p.name)}">${esc(p.name)}</span></span>
                    <span class="mono">${esc(p.pid)}</span>
                    <span class="mono">${rate(p.readBytesPerSec)}</span>
                    <span class="mono">${rate(p.writeBytesPerSec)}</span>
                </div>`).join("");
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
