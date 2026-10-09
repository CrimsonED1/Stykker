// StykkerHUD — die Zahlen der Seite. Die Messschleife läuft im Server; hier wird nur gelesen und gezeichnet.
// Die Seite ist einmal gerendert (kein Blazor-Kreis): die Werte kommen im Sekundentakt aus /api/snapshot, damit
// die Anzeige selbst keine Prozessorzeit kostet und auch ein stockender Abruf nichts kaputt macht.
(() => {
    "use strict";

    const $ = (id) => document.getElementById(id);
    const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
    const missing = (v) => v === null || v === undefined || v < 0;

    const num = (v, digits) => missing(v) ? "–" : v.toFixed(digits);
    const words = (v, unit, digits) => missing(v) ? "–" : `${v.toFixed(digits)} ${unit}`;
    const mb = (v) => {
        if (missing(v)) return "–";
        return v >= 1024 ? (v / 1024).toFixed(1) + " GB" : Math.round(v) + " MB";
    };
    // Byte je Sekunde, wie die Zahlen im Design: eine Stelle hinter dem Komma, unter 1 MB/s in KB/s.
    const rate = (bps) => {
        if (missing(bps)) return "–";
        return bps >= 1048576 ? (bps / 1048576).toFixed(1) + " MB/s" : (bps / 1024).toFixed(0) + " KB/s";
    };
    const uptime = (seconds) => {
        const d = Math.floor(seconds / 86400), h = Math.floor((seconds % 86400) / 3600), m = Math.floor((seconds % 3600) / 60);
        return d ? `${d}d ${h}h` : h ? `${h}h ${m}m` : `${m}m`;
    };

    function set(id, value) {
        const el = $(id);
        if (el && el.textContent !== value) el.textContent = value;
    }
    function width(id, percent) {
        const el = $(id);
        if (el) el.style.width = Math.max(0, Math.min(100, percent || 0)).toFixed(1) + "%";
    }

    // ── Kurven: Werte 0…100 über die volle Breite (viewBox 0 0 100 40, y wächst nach unten) ──
    function line(values) {
        if (!values || values.length < 2) return "";
        let d = "";
        for (let i = 0; i < values.length; i++) {
            const x = (i / (values.length - 1)) * 100;
            const y = 40 - Math.max(0, Math.min(100, missing(values[i]) ? 0 : values[i])) * 0.4;
            d += (i ? " L" : "M") + x.toFixed(2) + " " + y.toFixed(2);
        }
        return d;
    }
    function draw(prefix, values) {
        if (!values || values.length < 2) return;
        const d = line(values);
        const ref = $(prefix + "-line"), glow = $(prefix + "-g1"), area = $(prefix + "-area"), tip = $(prefix + "-tip");
        if (ref) ref.setAttribute("d", d);
        if (glow) glow.setAttribute("d", d);
        if (area) area.setAttribute("d", d + " L100 40 L0 40 Z");
        if (tip) tip.style.top = (100 - Math.max(0, Math.min(100, missing(values[values.length - 1]) ? 0 : values[values.length - 1]))) + "%";
        const peak = $(prefix + "-peak");
        if (peak) peak.textContent = "peak " + Math.round(Math.max(0, ...values.map((v) => missing(v) ? 0 : v))) + "%";
    }

    // ── Kerne, GPU, Speicher, Durchsatz, Hinweise ──
    // Die Kerne stecken in einem aufklappbaren Block: zusammengeklappt steht in der Zeile nur die Spitze, und das
    // Raster aus 16 Zeilen entsteht erst beim Öffnen.
    const coresFold = $("cores-fold");
    let coresShown = false, lastCores = null;
    if (coresFold) {
        coresShown = coresFold.open;
        coresFold.addEventListener("toggle", () => {
            coresShown = coresFold.open;
            if (coresShown && lastCores) renderCores(lastCores);
        });
    }

    function renderCores(loads) {
        const host = $("cores");
        if (!host) return;
        if (!loads || !loads.length) {
            if (host.dataset.n !== "0") { host.dataset.n = "0"; host.innerHTML = '<div class="hud-empty">no per-core values</div>'; }
            return;
        }
        if (host.childElementCount !== loads.length) {
            host.dataset.n = String(loads.length);
            host.innerHTML = loads.map((_, i) => `<div class="ctx"><span class="num">${i}</span><span class="bar"><i></i></span><span class="num">–</span></div>`).join("");
        }
        const kids = host.children;
        for (let i = 0; i < loads.length && i < kids.length; i++) {
            const v = Math.round(loads[i]);
            const fillBar = kids[i].querySelector(".bar i");
            if (fillBar) fillBar.style.width = v + "%";
            const label = kids[i].lastElementChild;
            if (label && label.textContent !== v + "%") label.textContent = v + "%";
        }
    }

    // Durchsatz als Balken: der Wert am bisher größten der Sitzung (den Sockel rechnet der Server aus).
    function renderIo(s) {
        const io = s.io, peaks = s.peaks || {};
        const rows = [
            ["disk-read", io && io.diskReadBps, peaks.diskReadBps],
            ["disk-write", io && io.diskWriteBps, peaks.diskWriteBps],
            ["net-rx", io && io.netRxBps, peaks.netRxBps],
            ["net-tx", io && io.netTxBps, peaks.netTxBps],
        ];
        for (const [id, value, peak] of rows) {
            width("t-" + id, value && peak > 0 ? (100 * value) / peak : 0);
            set("v-" + id, io ? `${rate(value)} / ${rate(peak)}` : "–");
        }
    }

    // Speicher der Grafikkarte: mit Gesamtgröße, wenn sie bekannt ist (nvml.dll), sonst nur der belegte Teil.
    function vramText(gpu) {
        if (!gpu || missing(gpu.vramUsedGb)) return "– / – GB";
        return gpu.vramTotalGb > 0
            ? `${gpu.vramUsedGb.toFixed(1)} / ${gpu.vramTotalGb.toFixed(0)} GB`
            : `${gpu.vramUsedGb.toFixed(1)} GB in use`;
    }

    function renderGraphics(s) {
        const gpu = s.gpu;
        set("v-gpu-name", gpu ? gpu.name : "–");
        set("v-gpu-big", num(gpu && gpu.utilPercent, 0));
        set("v-gpu", num(gpu && gpu.utilPercent, 0));
        set("v-gpu-vram", num(gpu && gpu.vramUsedGb, 1));
        set("v-vram", num(gpu && gpu.vramUsedGb, 1));
        set("v-gpu-temp", num(gpu && gpu.tempC, 0));
        set("v-gpu-power", num(gpu && gpu.powerW, 0));
        set("v-gpu-clock", num(gpu && gpu.gfxClockMhz, 0));
        set("v-gpu-memclock", num(gpu && gpu.memClockMhz, 0));
        const pct = gpu && gpu.vramTotalGb > 0 ? (100 * gpu.vramUsedGb) / gpu.vramTotalGb : 0;
        width("t-vram", pct);
        set("v-vram-text", vramText(gpu));
        // Das Leistungsziel steht neben dem Verbrauch; ab 90 % davon wird die Zelle zur Warnung.
        const limit = gpu && gpu.powerLimitW > 0 ? gpu.powerLimitW : 0;
        set("v-gpu-power-max", limit > 0 ? `/ ${num(limit, 0)} W` : "W");
        const power = $("gpu-power");
        if (power) power.classList.toggle("warn", limit > 0 && gpu.powerW >= 0.9 * limit);
        renderEngines(gpu);
        const card = $("gpu-card");
        if (card) card.classList.toggle("busy", !!gpu && gpu.utilPercent >= 25);
        width("t-gpu", gpu ? gpu.utilPercent : 0);
    }

    // Feste Reihenfolge und feste Farbe je Engine – sonst wechselten Balken und Farbe mit der Reihenfolge, in der
    // die Leistungsindikatoren ihre Instanzen melden. Die Summe darf über 100 % liegen: mehrere Engines laufen
    // gleichzeitig, der Balken zeigt deshalb je Engine den eigenen Wert.
    const ENGINE_ORDER = ["3D", "Compute", "Copy", "Video decode", "Video encode"];
    const ENGINE_COLOUR = ["var(--acc)", "var(--second)", "var(--good)", "var(--warn)", "var(--st-load)"];
    const engineRank = (name) => { const i = ENGINE_ORDER.indexOf(name); return i < 0 ? ENGINE_ORDER.length : i; };
    const engineColour = (name) => ENGINE_COLOUR[engineRank(name) % ENGINE_COLOUR.length];
    const clamp100 = (v) => Math.max(0, Math.min(100, v));

    function renderEngines(gpu) {
        const stack = $("gpu-stack"), legend = $("gpu-engines");
        if (!stack || !legend) return;
        const rows = ((gpu && gpu.engines) || []).slice()
            .sort((a, b) => engineRank(a.engine) - engineRank(b.engine) || b.percent - a.percent);
        stack.hidden = legend.hidden = rows.length === 0;
        const key = rows.map((r) => `${r.engine}=${r.percent.toFixed(1)}`).join("|");
        if (stack.dataset.key === key) return;
        stack.dataset.key = key;
        stack.innerHTML = rows.map((r) => `<i style="width:${clamp100(r.percent)}%;background:${engineColour(r.engine)}"></i>`).join("");
        legend.innerHTML = rows.map((r) =>
            `<span class="sw" style="background:${engineColour(r.engine)}"></span>` +
            `<span class="p">${esc(r.engine)}</span><span class="g">${r.percent.toFixed(0)} %</span>`).join("");
    }

    function renderMemory(s, history) {
        const sys = s.system;
        set("v-cpu", num(sys && sys.cpuPercent, 0));
        width("t-cpu", sys ? sys.cpuPercent : 0);
        set("v-cores", sys ? `${sys.cores} cores` : "–");
        set("v-ram", num(sys && sys.ramUsedGb, 1));
        set("v-ram-text", sys ? `${sys.ramUsedGb.toFixed(1)} / ${sys.ramTotalGb.toFixed(0)} GB` : "– / – GB");
        set("v-commit-text", sys ? `${sys.commitUsedGb.toFixed(1)} / ${sys.commitTotalGb.toFixed(0)} GB` : "– / – GB");
        width("t-ram", sys && sys.ramTotalGb > 0 ? (100 * sys.ramUsedGb) / sys.ramTotalGb : 0);
        width("t-commit", sys && sys.commitTotalGb > 0 ? (100 * sys.commitUsedGb) / sys.commitTotalGb : 0);
        if (sys) {
            lastCores = sys.coreLoads;
            const busiest = lastCores && lastCores.length ? Math.round(Math.max(...lastCores)) : -1;
            set("v-cores-summary", busiest < 0 ? "–" : `busiest ${busiest} %`);
            if (coresShown) renderCores(lastCores);
        }
    }

    function renderNotes(s) {
        const host = $("notes");
        if (!host) return;
        const out = [];
        for (const note of s.notes || [])
            out.push(`<div class="notice hud-note"><span class="badge warn plain">Hint</span><span class="txt">${esc(note)}</span></div>`);
        const gpu = s.gpu;
        if (gpu && gpu.vramTotalGb > 0 && gpu.vramUsedGb / gpu.vramTotalGb > 0.95)
            out.push(`<div class="notice alarm hud-note"><span class="badge bad">VRAM short</span><span class="txt">Graphics memory nearly full – ${gpu.vramUsedGb.toFixed(1)} of ${gpu.vramTotalGb.toFixed(0)} GB.</span></div>`);
        const key = out.join("");
        if (host.dataset.key !== key) { host.dataset.key = key; host.innerHTML = key; }
    }

    function render(s) {
        set("v-uptime", uptime(s.uptimeSeconds));
        renderMemory(s, s.history);
        renderGraphics(s);
        renderIo(s);
        renderNotes(s);
        const history = s.history || [];
        draw("cpu", history.map((h) => h.cpu));
        draw("gpu", history.map((h) => h.gpu));
    }

    async function tick() {
        // Nur lesen, was sichtbar ist: ein verborgenes Fenster fragt nicht, und der Server hört dann von selbst
        // auf zu messen (siehe HudService).
        if (document.hidden) return;
        try {
            const response = await fetch("/api/snapshot", { cache: "no-store" });
            if (response.ok) render(await response.json());
        } catch {
            /* ein ausgefallener Abruf darf die Anzeige nicht anhalten: die alte Zahl bleibt stehen */
        }
    }
    document.addEventListener("visibilitychange", () => { if (!document.hidden) tick(); });

    // Themenwahl: beide Themen des Design-Systems (dark, titan), gemerkt im Browser.
    for (const button of document.querySelectorAll(".seg button[data-theme-choice]")) {
        button.addEventListener("click", () => {
            const theme = button.dataset.themeChoice;
            document.documentElement.dataset.theme = theme;
            try { localStorage.setItem("stykkerhud.theme", theme); } catch { }
            for (const other of document.querySelectorAll(".seg button[data-theme-choice]")) other.classList.toggle("on", other === button);
        });
        button.classList.toggle("on", document.documentElement.dataset.theme === button.dataset.themeChoice);
    }

    tick();
    setInterval(tick, 1000);
})();
