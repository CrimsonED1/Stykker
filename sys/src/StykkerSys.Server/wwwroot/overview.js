// StykkerSYS — die Übersicht (/overview). Die Messschleifen laufen im Server; hier wird nur gelesen und gezeichnet.
// Die Werte kommen im Sekundentakt aus /api/overview (Prozessor, Speicher, GPU, größte Programme und Laufwerke).
// Fehlt eine Zahl, steht „–" (nie eine erfundene 0). Der Verlauf sammelt die Seite selbst seit dem Öffnen, wie bei den Laufwerken.
// Aussehen und Markup wie in StykkerLLM: die Bausteine hw-* des Design-Systems (components/hardware.css).
(() => {
    "use strict";

    const root = document.getElementById("ov-system");
    if (!root) return;           // nur auf der Übersicht

    const $ = (id) => document.getElementById(id);
    const esc = (s) => String(s ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
    const isNum = (v) => typeof v === "number" && Number.isFinite(v);
    const DASH = "–";
    const POINTS = 300;                    // wie StykkerLLM: die letzten 300 Abrufe
    const KIND = { Nvme: "NVMe", Ssd: "SSD", Hdd: "HDD", Usb: "USB", Unknown: "unknown" };
    // Farben der Anteile: Akzent, Zweitfarbe, Grün, Gelb, dann Mischungen (wie StykkerLLM)
    const PALETTE = [
        "var(--acc)", "var(--second)", "var(--good)", "var(--warn)",
        "color-mix(in srgb,var(--acc),var(--bad))", "color-mix(in srgb,var(--second),var(--good))",
        "color-mix(in srgb,var(--warn) 60%,var(--bad))", "color-mix(in srgb,var(--acc) 60%,var(--ink))",
    ];
    const history = { cpu: [], ram: [], gpu: [], vram: [] };   // Prozent, GB, Prozent, GB; null = keine Messung

    // Größen: Zweierpotenzen. Gigabyte mit einer Nachkommastelle bei kleinen Zahlen, MB unter 1 GB.
    function gbText(gb) {
        if (!isNum(gb)) return DASH;
        return gb >= 10 ? gb.toFixed(0) : gb.toFixed(1);
    }
    function memParts(mb) {
        if (!isNum(mb)) return [DASH, ""];
        if (mb < 1024) return [mb.toFixed(0), "MB"];
        return [gbText(mb / 1024), "GB"];
    }
    const memText = (mb) => { const [v, u] = memParts(mb); return v === DASH ? DASH : `${v} ${u}`; };
    function size(bytes) {
        if (!isNum(bytes) || bytes < 0) return DASH;
        const units = ["B", "KB", "MB", "GB", "TB"];
        let v = bytes, u = 0;
        while (v >= 1024 && u < units.length - 1) { v /= 1024; u++; }
        const digits = v >= 100 || u === 0 ? 0 : 1;
        return `${v.toFixed(digits)} ${units[u]}`;
    }
    const rate = (bps) => (isNum(bps) ? `${size(bps)}/s` : DASH);
    const pctText = (v) => (isNum(v) ? `${Math.round(v)} %` : DASH);

    // Eine Kennzahl wie HwMeter in StykkerLLM: Wert in der Zahlen-Schrift, Einheit klein, Balken nur bei bekanntem Anteil
    function meter({ label, value, unit, fraction, note, warn = false }) {
        const ok = value !== null && value !== undefined;
        const frac = isNum(fraction) ? Math.min(1, Math.max(0, fraction)) : null;
        const bar = frac === null ? "" : `<div class="hw-bar${frac > 0.92 ? " hot" : ""}"><i style="--w:${Math.round(frac * 100)}%"></i></div>`;
        return `<div class="hw-meter"><div class="hw-meter-hd"><span>${esc(label)}</span></div>`
            + `<div class="hw-val${ok ? "" : " na"}"><b>${esc(ok ? value : DASH)}</b>${unit && ok ? `<small>${esc(unit)}</small>` : ""}</div>`
            + bar
            + (note ? `<div class="hw-note${warn ? " warn" : ""}" title="${esc(note)}">${esc(note)}</div>` : "")
            + `</div>`;
    }

    // Verlauf: eine Linie je Reihe, 300 × 70 wie in den Karten; der neueste Wert steht rechts, fehlende fallen aus der Linie
    function points(values, max) {
        if (values.length < 2 || !(max > 0)) return "";
        const out = [];
        values.forEach((v, i) => {
            if (!isNum(v)) return;
            const x = (i * 300) / (values.length - 1);
            const y = 70 - Math.min(1, Math.max(0, v / max)) * 70;
            out.push(`${x.toFixed(1)},${y.toFixed(1)}`);
        });
        return out.join(" ");
    }
    function sparkBlock(title, values, max, cls) {
        return `<div><div class="hw-spark-hd">${esc(title)}</div>`
            + `<svg class="hw-spark" viewBox="0 0 300 70" preserveAspectRatio="none"><polyline class="line${cls ? " " + cls : ""}" fill="none" points="${points(values, max)}"/></svg></div>`;
    }
    // Der größte Wert der Reihe, mindestens floor (eine ruhige Leitung füllt nicht die ganze Höhe)
    const scale = (values, floor) => Math.max(floor, ...values.filter(isNum), 0);
    function remember(list, value) {
        list.push(isNum(value) ? value : null);
        if (list.length > POINTS) list.splice(0, list.length - POINTS);
    }
    function historyBlock(items) {
        const longEnough = items.some((it) => it.values.filter(isNum).length >= 2);
        if (!longEnough) return `<div class="hw-subh">History</div><div class="hw-sub">Not enough readings yet.</div>`;
        return `<div class="hw-subh">History</div><div class="hw-sparks">${items.map((it) => it.html).join("")}</div>`;
    }

    // ── Prozessor und Speicher ──
    function renderSystem(s) {
        const cpu = s?.cpuPercent ?? null;
        const used = s?.ramUsedGb ?? null, total = s?.ramTotalGb ?? null;
        const commitUsed = s?.commitUsedGb ?? null, commitLimit = s?.commitLimitGb ?? null;
        const ramFrac = isNum(used) && isNum(total) && total > 0 ? used / total : null;
        const commitFrac = isNum(commitUsed) && isNum(commitLimit) && commitLimit > 0 ? commitUsed / commitLimit : null;

        $("ov-system").innerHTML = s
            ? meter({ label: "Processor", value: isNum(cpu) ? Math.round(cpu) : null, unit: "%",
                      fraction: isNum(cpu) ? cpu / 100 : null, note: `${s.cores} logical cores` })
              + meter({ label: "Memory", value: isNum(used) ? gbText(used) : null, unit: `/ ${gbText(total)} GB`, fraction: ramFrac,
                        note: isNum(used) && isNum(total) ? `${gbText(Math.max(0, total - used))} GB free` : null })
              + meter({ label: "Commit", value: isNum(commitUsed) ? gbText(commitUsed) : null, unit: `/ ${gbText(commitLimit)} GB`, fraction: commitFrac,
                        note: "Memory and page file promised to programs" })
            : `<span class="hw-sub">${DASH}</span>`;

        // Kerne als Säulen: eine je logischem Kern; ohne Messung (erster Takt) bleibt es leer
        const cores = s?.coreLoads ?? null;
        $("ov-cores").innerHTML = cores && cores.length
            ? `<div class="hw-cores">${cores.map((c, i) => `<i style="--h:${Math.max(2, c).toFixed(1)}%" title="Core ${i + 1}: ${Math.round(c)} %"></i>`).join("")}</div>`
            : `<span class="hw-sub">${DASH}</span>`;
    }

    // Die größten Programme nach Speicher: Anteile am belegten Speicher, dazu die Legende
    function renderRam(s, programs) {
        if (!s || programs.length === 0) { $("ov-ram").innerHTML = `<span class="hw-sub">${DASH}</span>`; return; }
        const sum = programs.reduce((a, p) => a + p.ramMb / 1024, 0);
        const total = Math.max(s.ramTotalGb || 0, s.ramUsedGb || 0, sum);
        const pct = (v) => (total > 0 ? ((100 * v) / total).toFixed(3) : "0") + "%";
        const rest = (s.ramUsedGb || 0) - sum;
        $("ov-ram").innerHTML = `<div class="hw-stack">`
            + programs.map((p, i) => `<i style="width:${pct(p.ramMb / 1024)};--c:${PALETTE[i % PALETTE.length]}" title="${esc(p.name)}: ${esc(memText(p.ramMb))}"></i>`).join("")
            + (rest > 0.05 ? `<i style="width:${pct(rest)};--c:rgb(var(--muted-rgb)/.45)" title="Other"></i>` : "")
            + `</div><div class="hw-legend">`
            + programs.map((p, i) => {
                const [v, u] = memParts(p.ramMb);
                return `<div title="${esc(p.name)}"><i style="--c:${PALETTE[i % PALETTE.length]}"></i><span>${esc(p.name)}${p.count > 1 ? ` ×${p.count}` : ""}</span><span class="hw-val"><b>${esc(v)}</b><small>${esc(u)}</small></span></div>`;
              }).join("")
            + `</div>`;
    }

    // Die größten Programme nach Prozessorzeit
    function renderCpuPrograms(programs) {
        $("ov-cpu-procs").innerHTML = `<table class="hw-table"><tr><th>Name</th><th class="num">CPU</th><th class="num">Memory</th><th class="num">Processes</th></tr>`
            + (programs.length === 0 ? `<tr><td colspan="4" class="hw-sub">${DASH}</td></tr>` : "")
            + programs.map((p) => `<tr>
                    <td class="hw-name" title="${esc(p.name)}">${esc(p.name)}</td>
                    <td class="num${p.cpuPercent >= 50 ? " good-text" : ""}">${isNum(p.cpuPercent) ? p.cpuPercent.toFixed(1) + " %" : DASH}</td>
                    <td class="num">${esc(memText(p.ramMb))}</td>
                    <td class="num">${esc(p.count)}</td>
                </tr>`).join("")
            + `</table>`;
    }

    // ── Grafikkarte ──
    function renderGpu(g) {
        const util = g?.utilPercent ?? null;
        const vramMb = g?.vramUsedMb ?? null;
        $("ov-gpu").innerHTML = g
            ? meter({ label: "Load", value: isNum(util) ? Math.round(util) : null, unit: "%",
                      fraction: isNum(util) ? util / 100 : null, note: "busiest engine" })
              + meter({ label: "Graphics memory", value: isNum(vramMb) ? gbText(vramMb / 1024) : null, unit: "GB used",
                        fraction: null, note: "Total not reported by the counters" })
            : `<span class="hw-sub">${DASH}</span>`;

        const engines = g?.engines ?? [];
        $("ov-engines").innerHTML = engines.length
            ? `<div class="hw-meters">${engines.map((e) => meter({ label: e.engine, value: Math.round(e.percent), unit: "%", fraction: e.percent / 100 })).join("")}</div>`
            : `<span class="hw-sub">${DASH}</span>`;

        const programs = g?.programs ?? [];
        $("ov-gpu-procs").innerHTML = `<table class="hw-table"><tr><th>Program</th><th class="num">GPU</th><th class="num">Graphics memory</th></tr>`
            + (programs.length === 0 ? `<tr><td colspan="3" class="hw-sub">${DASH}</td></tr>` : "")
            + programs.map((p) => `<tr>
                    <td class="hw-name" title="${esc(p.name)}">${esc(p.name)}${p.count > 1 ? ` ×${p.count}` : ""}</td>
                    <td class="num${p.gpuPercent >= 50 ? " good-text" : ""}">${esc(pctText(p.gpuPercent))}</td>
                    <td class="num">${esc(memText(p.vramMb))}</td>
                </tr>`).join("")
            + `</table>`;
    }

    // ── Laufwerke (kompakt; die Details stehen auf /disks) ──
    function renderDisks(disks) {
        const volumes = disks?.disks?.volumes ?? [];
        $("ov-volumes").innerHTML = volumes.length === 0
            ? `<span class="hw-sub">${DASH}</span>`
            : volumes.map((v) => {
                const total = v.totalBytes ?? null, free = v.freeBytes ?? null;
                const used = isNum(total) && isNum(free) ? total - free : null;
                const frac = isNum(total) && total > 0 && isNum(used) ? Math.min(1, Math.max(0, used / total)) : 0;
                const kind = KIND[v.kind] ?? v.kind;
                const label = v.label ? `${v.label} ` : "";
                return `<div class="hw-vol">
                    <div class="hw-vol-top">
                        <span class="hw-letter">${esc(v.letter)}:</span>
                        <span class="hw-kind" data-k="${esc(v.kind)}" title="${esc(kind)}">${esc(kind)}</span>
                        <span class="hw-sub">${esc(label + (v.fileSystem ?? ""))}</span>
                        <span class="hw-free">${size(used)} used · ${size(free)} free · ${size(total)}</span>
                    </div>
                    <div class="hw-bar${frac > 0.92 ? " hot" : ""}"><i style="--w:${Math.round(frac * 100)}%"></i></div>
                    <div class="hw-io">
                        <span>Read <b>${rate(v.readBytesPerSec)}</b></span>
                        <span>Write <b>${rate(v.writeBytesPerSec)}</b></span>
                    </div>
                </div>`;
            }).join("");
        $("ov-disk-notes").innerHTML = (disks?.notes ?? []).map((n) => `<p class="sys-note hw-sub">${esc(n)}</p>`).join("");
    }

    function render(data) {
        const hw = data.hardware ?? {};
        const disks = data.disks ?? null;
        const s = hw.system ?? null, g = hw.gpu ?? null;

        $("v-ov-time").textContent = hw.at ? new Date(hw.at).toLocaleTimeString() : DASH;
        $("v-cpu").textContent = pctText(s?.cpuPercent);
        $("v-ram").textContent = s ? `${gbText(s.ramUsedGb)} / ${gbText(s.ramTotalGb)} GB` : DASH;
        $("v-gpu").textContent = pctText(g?.utilPercent);
        $("ov-notes").innerHTML = (hw.notes ?? []).map((n) => `<p class="sys-note hw-sub">${esc(n)}</p>`).join("");

        renderSystem(s);
        renderRam(s, hw.topByRam ?? []);
        renderCpuPrograms(hw.topByCpu ?? []);
        renderGpu(g);
        renderDisks(disks);

        // Verlauf: je Abruf ein Punkt
        remember(history.cpu, s?.cpuPercent);
        remember(history.ram, s?.ramUsedGb);
        remember(history.gpu, g?.utilPercent);
        remember(history.vram, isNum(g?.vramUsedMb) ? g.vramUsedMb / 1024 : null);

        const ramTop = s?.ramTotalGb ?? 0;
        $("ov-sys-history").innerHTML = historyBlock([
            { values: history.cpu, html: sparkBlock("Processor · max 100 %", history.cpu, 100) },
            { values: history.ram, html: sparkBlock(`Memory · max ${gbText(ramTop)} GB`, history.ram, ramTop, "second") },
        ]);
        const vramTop = scale(history.vram, 1);
        $("ov-gpu-history").innerHTML = historyBlock([
            { values: history.gpu, html: sparkBlock("GPU load · max 100 %", history.gpu, 100) },
            { values: history.vram, html: sparkBlock(`Graphics memory · max ${gbText(vramTop)} GB`, history.vram, vramTop, "second") },
        ]);
    }

    // Nur lesen, was sichtbar ist: ein verborgenes Fenster fragt nicht, und der Server hört dann von selbst auf zu messen
    async function poll() {
        if (document.hidden) return;
        try {
            const response = await fetch("/api/overview", { cache: "no-store" });
            if (response.ok) render(await response.json());
        } catch {
            /* ein ausgefallener Abruf darf die Anzeige nicht anhalten: die alte Anzeige bleibt stehen */
        }
    }
    document.addEventListener("visibilitychange", () => { if (!document.hidden) poll(); });
    poll();
    setInterval(poll, 1000);
})();
