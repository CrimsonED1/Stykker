// StykkerSYS — die Prozessliste der Seite. Die Messschleife läuft im Server; hier wird nur gelesen und gezeichnet.
// Die Liste kommt im Sekundentakt aus /api/snapshot. Gleiche Zeilen bleiben stehen (Menü, Aufklappen, Fokus), nur
// die Zahlen wandern.
(() => {
    "use strict";

    const $ = (id) => document.getElementById(id);
    const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
    const missing = (v) => v === null || v === undefined || v < 0;

    function set(id, value) {
        const el = $(id);
        if (el && el.textContent !== value) el.textContent = value;
    }
    const dur = (seconds) => {
        const h = Math.floor(seconds / 3600), m = Math.floor((seconds % 3600) / 60), s = Math.floor(seconds % 60);
        return h ? `${h}:${String(m).padStart(2, "0")}h` : `${m}:${String(s).padStart(2, "0")}`;
    };
    const uptime = (seconds) => {
        const d = Math.floor(seconds / 86400), h = Math.floor((seconds % 86400) / 3600), m = Math.floor((seconds % 3600) / 60);
        return d ? `${d}d ${h}h` : h ? `${h}h ${m}m` : `${m}m`;
    };

    // ── Prozessliste: gleiche Programme werden eine Zeile mit „×N", die sich aufklappen lässt ──
    const view = { sort: "cpu", query: "", signature: null, open: new Set(), rows: new Map(), byId: new Map(), last: [], expandAll: false };

    const WORST = { idle: 0, read: 1, gen: 2 };

    // Was in der Gruppenzeile steht: Summen über die Mitglieder. Fehlt ein Wert bei allen, bleibt er null.
    function summaryOf(group) {
        const m = group.members;
        const sum = (field) => m.reduce((total, p) => total + (p[field] ?? 0), 0);
        const sumOrNull = (field) => m.every((p) => p[field] === null || p[field] === undefined) ? null : sum(field);
        const priority = m.every((p) => p.priority === m[0].priority) ? m[0].priority : null;
        return {
            pid: m[0].pid, name: group.name, path: group.path, count: m.length,
            cpuPercent: sum("cpuPercent"), ramMb: sum("ramMb"),
            gpuPercent: sumOrNull("gpuPercent"), vramMb: sumOrNull("vramMb"),
            threads: sum("threads"), cpuSeconds: sum("cpuSeconds"),
            state: m.reduce((worst, p) => (WORST[p.state] ?? 0) > (WORST[worst] ?? 0) ? p.state : worst, "idle"),
            priority: priority === "Normal" ? null : priority,
        };
    }

    // Gruppen aus Name **und** Pfad: zwei verschiedene Programme mit gleichem Namen bleiben getrennt. Ein einzelner
    // Prozess wird keine Gruppe – die Zeile sieht dann aus wie bisher.
    function groupOf(list) {
        const q = view.query.toLowerCase();
        const wanted = list.filter((p) => !q
            || p.name.toLowerCase().includes(q)
            || (p.path || "").toLowerCase().includes(q)
            || String(p.pid) === q);
        const groups = new Map();
        for (const p of wanted) {
            const key = p.name + "\u0000" + (p.path || "");
            let group = groups.get(key);
            if (!group) {
                group = { key, name: p.name, path: p.path, members: [] };
                groups.set(key, group);
            }
            group.members.push(p);
        }
        const rows = [...groups.values()];
        for (const group of rows) {
            group.count = group.members.length;
            group.summary = summaryOf(group);
        }
        const cmp = {
            cpu: (a, b) => b.summary.cpuPercent - a.summary.cpuPercent,
            ram: (a, b) => b.summary.ramMb - a.summary.ramMb,
            gpu: (a, b) => (b.summary.gpuPercent || 0) - (a.summary.gpuPercent || 0),
            name: (a, b) => a.name.localeCompare(b.name) || (a.path || "").localeCompare(b.path || ""),
        }[view.sort];
        return rows.sort(cmp).slice(0, 40);
    }

    function rowHtml(p, child = false) {
        const heat = p.cpuPercent >= 50 ? "var(--bad)" : p.cpuPercent >= 15 ? "var(--warn)" : "var(--muted)";
        return `<div class="req${child ? " child" : ""}" data-pid="${p.pid}" data-row="p${p.pid}" style="--b:${heat}">
            <span class="cl" title="${esc(p.path || "Process")}"><svg class="ic"><use href="#i-cpu" /></svg></span>
            <span class="mdl"><i></i><span>${esc(p.name)}</span>${p.priority && p.priority !== "Normal" ? `<span class="chipi warnc" title="Priority: ${esc(p.priority)}">${esc(p.priority)}</span>` : ""}</span>
            <span class="hostc"><span class="chipi">${p.pid}</span></span>
            <span class="dur"><span class="bar"><i></i></span><span class="pc">–</span></span>
            <span class="pp">–</span>
            <span class="pp">–</span>
            <span class="ex"><span><svg class="ic"><use href="#i-vram" /></svg><b>–</b></span><span><svg class="ic"><use href="#i-slots" /></svg><b>${p.threads}</b></span></span>
            <span class="res"><span class="st" data-s="${p.state}"><svg class="ic"><use href="#s-${p.state}" /></svg></span></span>
            <span class="ago">${dur(p.cpuSeconds)}</span>
            <span class="act"><details class="menu"><summary class="btn sq act" aria-label="Actions">⋯</summary>
                <div><span class="grp">${esc(p.name)} (PID ${p.pid})</span>
                <button type="button" data-act="open" data-pid="${p.pid}"><svg class="ic"><use href="#i-open" /></svg>Open file location</button>
                <button type="button" data-act="priority" data-pid="${p.pid}" data-level="abovenormal"><svg class="ic"><use href="#i-slots" /></svg>Priority: Above normal</button>
                <button type="button" data-act="priority" data-pid="${p.pid}" data-level="normal"><svg class="ic"><use href="#i-slots" /></svg>Priority: Normal</button>
                <button type="button" data-act="priority" data-pid="${p.pid}" data-level="belownormal"><svg class="ic"><use href="#i-slots" /></svg>Priority: Below normal</button>
                <button type="button" data-act="priority" data-pid="${p.pid}" data-level="high"><svg class="ic"><use href="#i-slots" /></svg>Priority: High</button>
                <button type="button" data-act="priority" data-pid="${p.pid}" data-level="idle"><svg class="ic"><use href="#i-slots" /></svg>Priority: Idle</button>
                <hr>
                <button type="button" class="danger" data-act="end" data-pid="${p.pid}"><svg class="ic"><use href="#i-stop" /></svg>End task</button>
                </div></details></span>
        </div>`;
    }

    // Die Zeile einer Gruppe: Name, „×N", Summen – und ein Menü, das für **alle** Mitglieder gilt. Der Winkel links
    // klappt die einzelnen Prozesse darunter auf.
    function groupHtml(g) {
        const open = view.open.has(g.id);
        const heat = g.summary.cpuPercent >= 50 ? "var(--bad)" : g.summary.cpuPercent >= 15 ? "var(--warn)" : "var(--muted)";
        return `<div class="req grp" data-row="${g.id}" data-group="${g.id}" style="--b:${heat}">
            <span class="cl"><button type="button" class="chev" data-act="fold" data-group="${g.id}" aria-expanded="${open}"
                title="${open ? "Collapse" : "Expand"} ${g.count} processes">${open ? "▾" : "▸"}</button></span>
            <span class="mdl" title="${esc(g.path || g.name)}"><i></i><span>${esc(g.name)}</span><span class="chipi cnt">×${g.count}</span></span>
            <span class="hostc"><span class="chipi">${g.count}</span></span>
            <span class="dur"><span class="bar"><i></i></span><span class="pc">–</span></span>
            <span class="pp">–</span>
            <span class="pp">–</span>
            <span class="ex"><span><svg class="ic"><use href="#i-vram" /></svg><b>–</b></span><span><svg class="ic"><use href="#i-slots" /></svg><b>–</b></span></span>
            <span class="res"><span class="st" data-s="${g.summary.state}"><svg class="ic"><use href="#s-${g.summary.state}" /></svg></span></span>
            <span class="ago">–</span>
            <span class="act"><details class="menu"><summary class="btn sq act" aria-label="Actions">⋯</summary>
                <div><span class="grp">${esc(g.name)} · ${g.count} processes</span>
                <button type="button" data-act="open" data-group="${g.id}"><svg class="ic"><use href="#i-open" /></svg>Open file location</button>
                <button type="button" data-act="priority" data-group="${g.id}" data-level="abovenormal"><svg class="ic"><use href="#i-slots" /></svg>Priority: Above normal</button>
                <button type="button" data-act="priority" data-group="${g.id}" data-level="normal"><svg class="ic"><use href="#i-slots" /></svg>Priority: Normal</button>
                <button type="button" data-act="priority" data-group="${g.id}" data-level="belownormal"><svg class="ic"><use href="#i-slots" /></svg>Priority: Below normal</button>
                <button type="button" data-act="priority" data-group="${g.id}" data-level="high"><svg class="ic"><use href="#i-slots" /></svg>Priority: High</button>
                <button type="button" data-act="priority" data-group="${g.id}" data-level="idle"><svg class="ic"><use href="#i-slots" /></svg>Priority: Idle</button>
                <hr>
                <button type="button" class="danger" data-act="end" data-group="${g.id}"><svg class="ic"><use href="#i-stop" /></svg>End all ${g.count} tasks</button>
                </div></details></span>
        </div>`;
    }

    function bind(row, el) {
        row.el = el;
        row.bar = el.querySelector(".dur .bar i");
        row.cpu = el.querySelector(".dur .pc");
        row.ram = el.children[4];
        row.gpu = el.children[5];
        row.vram = el.querySelector(".ex b");
        row.lamp = el.querySelector(".st");
        row.time = el.querySelector(".ago");
        row.mdl = el.querySelector(".mdl");
        row.menu = el.querySelector(".act details");
        row.threads = el.querySelectorAll(".ex b")[1] || null;
        row.single = (el.dataset.row || "").startsWith("p");
    }

    function fill(row, p) {
        if (row.bar) row.bar.style.width = Math.min(100, p.cpuPercent).toFixed(1) + "%";
        if (row.cpu) row.cpu.textContent = Math.round(p.cpuPercent) + "%";
        if (row.ram) row.ram.textContent = p.ramMb >= 1024 ? (p.ramMb / 1024).toFixed(1) + " GB" : Math.round(p.ramMb) + " MB";
        if (row.gpu) row.gpu.textContent = missing(p.gpuPercent) ? "–" : Math.round(p.gpuPercent) + "%";
        if (row.vram) row.vram.textContent = missing(p.vramMb) ? "–" : (p.vramMb >= 1024 ? (p.vramMb / 1024).toFixed(1) : Math.round(p.vramMb));
        if (row.time) row.time.textContent = dur(p.cpuSeconds);
        if (row.threads) row.threads.textContent = String(p.threads ?? "–");
        // Die Priorität steht im Menü: die passende Zeile ist gesperrt, damit man den Ist-Zustand sieht. Bei einer
        // Gruppe (mehrere Prozesse) wäre beides eine Behauptung – dort bleibt es weg.
        if (row.single && row.menu) {
            const level = (p.priority || "").toLowerCase();
            for (const item of row.menu.querySelectorAll("button[data-level]")) item.disabled = item.dataset.level === level;
        }
        if (row.single && row.mdl) updateChip(row.mdl, p.priority);
        if (row.lamp && row.lamp.dataset.s !== p.state) {
            row.lamp.dataset.s = p.state;
            const use = row.lamp.querySelector("use");
            if (use) use.setAttribute("href", "#s-" + p.state);
        }
    }

    // Das Zeichen neben dem Namen folgt der Messung: es entsteht und verschwindet, ohne dass die Zeile neu gebaut
    // werden muss (sonst wäre eine geänderte Priorität erst nach einer Umsortierung zu sehen).
    function updateChip(mdl, priority) {
        const wanted = priority && priority !== "Normal" ? priority : null;
        let chip = mdl.querySelector(".chipi");
        if (!wanted) {
            if (chip) chip.remove();
            return;
        }
        if (!chip) {
            chip = document.createElement("span");
            chip.className = "chipi warnc";
            mdl.appendChild(chip);
        }
        if (chip.textContent !== wanted) {
            chip.textContent = wanted;
            chip.title = "Priority: " + wanted;
        }
    }

    // Die Kennung einer Gruppe kommt aus Name und Pfad, nicht aus ihrer Position: sonst hinge das Aufklappen beim
    // Umsortieren an einem anderen Programm. (djb2 – bei ein paar Dutzend Gruppen praktisch kollisionsfrei.)
    function groupId(key) {
        let h = 5381;
        for (let i = 0; i < key.length; i++) h = (Math.imul(h, 33) ^ key.charCodeAt(i)) >>> 0;
        return "g" + h.toString(36);
    }

    function renderProcesses(list) {
        const host = $("proc-rows");
        if (!host) return;
        view.last = list;
        const groups = groupOf(list);
        view.byId.clear();
        groups.forEach((g) => { g.id = groupId(g.key); view.byId.set(g.id, g); });
        // Bei einer Suche klappen die passenden Gruppen einmal auf: man sucht einen Prozess, nicht eine Summe. Danach
        // darf man sie wieder zuklappen – deshalb nur einmal je Suche, nicht bei jedem Takt.
        if (view.expandAll) {
            for (const g of groups) view.open.add(g.id);
            view.expandAll = false;
        }

        // Neu bauen nur, wenn sich die Zeilen unterscheiden: Gruppen, aufgeklappte Mitglieder und Einzel-PIDs.
        const signature = groups.map((g) => g.count === 1
            ? "p" + g.members[0].pid
            : g.id + (view.open.has(g.id) ? "+" + g.members.map((m) => m.pid).join(".") : "-")).join(",");
        if (signature !== view.signature) {
            view.signature = signature;
            view.rows.clear();
            let html = "";
            for (const g of groups) {
                if (g.count === 1) { html += rowHtml(g.members[0], false); continue; }
                html += groupHtml(g);
                if (view.open.has(g.id)) for (const m of g.members) html += rowHtml(m, true);
            }
            host.innerHTML = html;
            for (const el of host.children) {
                const row = {};
                bind(row, el);
                view.rows.set(el.dataset.row, row);
            }
        }

        // Zahlen nachziehen: die Gruppenzeile bekommt die Summen, jede Einzelzeile ihren Prozess.
        for (const g of groups) {
            const row = view.rows.get(g.count === 1 ? "p" + g.members[0].pid : g.id);
            if (row) fill(row, g.summary);
            if (g.count > 1 && view.open.has(g.id)) {
                for (const m of g.members) {
                    const child = view.rows.get("p" + m.pid);
                    if (child) fill(child, m);
                }
            }
        }
        set("v-proc-shown", `${groups.length} groups of ${list.length}`);
    }

    // ── Hinweise: was die Liste nicht messen konnte ──
    function renderNotes(s) {
        const host = $("notes");
        if (!host) return;
        const out = (s.notes || []).map((note) =>
            `<div class="notice sys-note"><span class="badge warn plain">Hint</span><span class="txt">${esc(note)}</span></div>`);
        const key = out.join("");
        if (host.dataset.key !== key) { host.dataset.key = key; host.innerHTML = key; }
    }

    function render(s) {
        set("v-procs", String(s.processes.length));
        set("v-uptime", uptime(s.uptimeSeconds));
        renderNotes(s);
        renderProcesses(s.processes);
    }

    async function tick() {
        // Nur lesen, was sichtbar ist: ein verborgenes Fenster fragt nicht, und der Server hört dann von selbst
        // auf zu messen (siehe SysService).
        if (document.hidden) return;
        try {
            const response = await fetch("/api/snapshot", { cache: "no-store" });
            if (response.ok) render(await response.json());
        } catch {
            /* ein ausgefallener Abruf darf die Anzeige nicht anhalten: die alte Liste bleibt stehen */
        }
    }
    document.addEventListener("visibilitychange", () => { if (!document.hidden) tick(); });

    // ── Aktionen ──
    // Ein Klick auf einen Menüpunkt fragt höchstens nach (Beenden) und schickt sonst sofort. Die Antwort des
    // Servers sagt in Worten, was passiert ist; sie landet unverändert im Toast.
    let pending = null;   // { pids, name, ramMb } – der Prozess, für den gerade nachgefragt wird

    async function act(path, body) {
        try {
            const response = await fetch(path, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(body),
            });
            return await response.json();
        } catch {
            return { ok: false, message: "No answer from the service." };
        }
    }

    function toast(result) {
        const host = $("toast");
        if (!host) return;
        host.innerHTML = `<div class="toast${result.ok ? "" : " warn"}">${esc(result.message)}</div>`;
        setTimeout(() => { if (host.firstElementChild) host.innerHTML = ""; }, 8000);
    }

    // Eine Aktion auf mehrere Prozesse: der Reihe nach, mit einer Antwort für alle zusammen.
    async function actAll(url, pids, makeBody) {
        let done = 0;
        const refusals = [];
        for (const pid of pids) {
            const result = await act(url, makeBody(pid));
            if (result.ok) done++;
            else refusals.push(result.message);
        }
        if (!refusals.length) return { ok: true, message: `${done} of ${pids.length} done.` };
        return { ok: done > 0, message: `${done} of ${pids.length} done. First refusal: ${refusals[0]}` };
    }

    function askBeforeEnd(target) {
        const host = $("confirm");
        if (!host) return;
        pending = target;
        const size = target.ramMb >= 1024 ? (target.ramMb / 1024).toFixed(1) + " GB" : Math.round(target.ramMb) + " MB";
        const what = target.pids.length > 1 ? `all ${target.pids.length} ${esc(target.name)} tasks` : `${esc(target.name)} (PID ${target.pids[0]})`;
        host.innerHTML = `<div class="notice alarm sys-note"><span class="badge bad">End task</span>
            <span class="txt">End ${what}? ${size} working set – unsaved work in it is lost.</span>
            <span class="acts"><button type="button" class="quiet" data-act="cancel">Cancel</button>
            <button type="button" class="danger" data-act="confirm">End ${target.pids.length > 1 ? "all" : "task"}</button></span></div>`;
    }

    function clearConfirm() {
        const host = $("confirm");
        if (host) host.innerHTML = "";
        pending = null;
    }

    document.addEventListener("click", async (event) => {
        const button = event.target.closest("button[data-act]");
        if (!button) return;
        const action = button.dataset.act;
        const menu = button.closest("details");
        if (menu) menu.open = false;

        // Eine Aktion gehört entweder zu einer Gruppe (dann gilt sie für alle Mitglieder) oder zu einer Zeile.
        const group = button.dataset.group ? view.byId.get(button.dataset.group) : null;
        const pids = group ? group.members.map((m) => m.pid) : [Number(button.dataset.pid ?? pending?.pids?.[0])];

        if (action === "fold") {
            const id = button.dataset.group;
            if (view.open.has(id)) view.open.delete(id);
            else view.open.add(id);
            view.signature = null;
            renderProcesses(view.last);   // sofort aufklappen, nicht erst mit der nächsten Messung
            return;
        }
        if (action === "cancel") { clearConfirm(); return; }
        if (action === "open") { toast(await act("/api/process/open", { pid: pids[0] })); return; }
        if (action === "priority") {
            const level = button.dataset.level;
            toast(group
                ? await actAll("/api/process/priority", pids, (pid) => ({ pid, level }))
                : await act("/api/process/priority", { pid: pids[0], level }));
            tick();
            return;
        }
        if (action === "end") {
            // Name und Speicher aus der sichtbaren Zeile, damit die Rückfrage konkret ist.
            const row = button.closest(".req");
            const memText = row?.children[4]?.textContent ?? "0";
            askBeforeEnd({
                pids,
                name: row?.querySelector(".mdl > span")?.textContent ?? `PID ${pids[0]}`,
                ramMb: Number(memText.replace(/[^\d.]/g, "")) * (memText.includes("GB") ? 1024 : 1),
            });
            return;
        }
        if (action === "confirm") {
            const target = pending;
            clearConfirm();
            if (!target) return;
            const result = target.pids.length === 1
                ? await act("/api/process/end", { pid: target.pids[0], tree: false })
                : await actAll("/api/process/end", target.pids, (pid) => ({ pid, tree: false }));
            toast(result);
            if (result.ok) tick();
        }
    });

    // Ein Klick außerhalb schließt ein offenes Zeilenmenü (das native details tut das nicht).
    document.addEventListener("click", (event) => {
        for (const menu of document.querySelectorAll("details.menu[open]")) {
            if (!menu.contains(event.target)) menu.open = false;
        }
    });

    for (const button of document.querySelectorAll(".seg button[data-sort]")) {
        button.addEventListener("click", () => {
            view.sort = button.dataset.sort;
            for (const other of document.querySelectorAll(".seg button[data-sort]")) other.classList.toggle("on", other === button);
        });
    }
    // Themenwahl: beide Themen des Design-Systems (dark, titan), gemerkt im Browser.
    for (const button of document.querySelectorAll(".seg button[data-theme-choice]")) {
        button.addEventListener("click", () => {
            const theme = button.dataset.themeChoice;
            document.documentElement.dataset.theme = theme;
            try { localStorage.setItem("stykkersys.theme", theme); } catch { }
            for (const other of document.querySelectorAll(".seg button[data-theme-choice]")) other.classList.toggle("on", other === button);
        });
        button.classList.toggle("on", document.documentElement.dataset.theme === button.dataset.themeChoice);
    }
    const filter = $("q");
    if (filter) filter.addEventListener("input", () => {
        view.query = filter.value.trim();
        view.open.clear();        // eine neue Suche fängt mit ihren eigenen Gruppen an
        view.expandAll = true;    // und zeigt die Treffer aufgeklappt
    });

    tick();
    setInterval(tick, 1000);
})();
