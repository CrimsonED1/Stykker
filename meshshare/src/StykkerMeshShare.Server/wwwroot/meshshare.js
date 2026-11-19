// StykkerMeshShare — die Seite. Der Abgleich läuft im Server; hier wird nur gelesen, gezeichnet und hochgeladen.
// /api/status kommt alle 1,5 s; die Dateiliste nur, wenn sich der Index geändert hat (seq).
(() => {
    "use strict";

    const $ = (id) => document.getElementById(id);
    const esc = (s) => String(s ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
    const post = (url, body) => fetch(url, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body ?? {}),
    }).then((r) => r.json().catch(() => ({ ok: r.ok })));

    function size(bytes) {
        if (!Number.isFinite(bytes) || bytes < 0) return "–";
        const units = ["B", "KB", "MB", "GB", "TB"];
        let v = bytes, u = 0;
        while (v >= 1024 && u < units.length - 1) { v /= 1024; u++; }
        return `${v.toFixed(v >= 100 || u === 0 ? 0 : 1)} ${units[u]}`;
    }
    function ago(iso) {
        if (!iso) return "never";
        const s = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
        if (s < 10) return "just now";
        if (s < 60) return `${Math.round(s)} s ago`;
        if (s < 3600) return `${Math.round(s / 60)} min ago`;
        if (s < 86400) return `${Math.round(s / 3600)} h ago`;
        return new Date(iso).toLocaleDateString();
    }
    const clock = (iso) => new Date(iso).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
    function set(id, text) { const el = $(id); if (el && el.textContent !== text) el.textContent = text; }

    function toast(text, warn) {
        const box = $("toasts");
        const el = document.createElement("div");
        el.className = "toast" + (warn ? " warn" : "");
        el.textContent = text;
        box.appendChild(el);
        setTimeout(() => { el.classList.add("out"); setTimeout(() => el.remove(), 400); }, 3500);
    }

    // ── Sync-Status ──
    const LAMP = {
        idle: ["idle", "Up to date"],
        sync: ["gen", "Syncing"],
        scan: ["load", "Waiting for files in use"],
        off: ["off", "Problem"],
        alone: ["read", "No other PC yet"],
    };
    function lamp(kind, text) {
        const [s, title] = LAMP[kind];
        const el = $("v-lamp");
        el.dataset.s = s;
        el.title = title;
        el.setAttribute("aria-label", title);
        $("v-lamp-ic").setAttribute("href", `#s-${s}`);
        set("v-state", text ?? title);
    }

    let lastSeq = -1;
    let currentFolder = "";
    function renderStatus(st) {
        const n = st.node;
        set("v-files", String(n.files));
        set("v-size", size(n.bytes));
        set("v-folder", n.folder);
        $("folder-btn").hidden = !n.canChangeFolder;
        $("open-folder").hidden = !n.canChangeFolder;
        currentFolder = n.folder;
        const online = st.peers.filter((p) => p.online).length;
        set("v-peers", `${online}/${st.peers.length}`);
        set("v-peer-count", String(st.peers.length));
        $("v-peers-chip").classList.toggle("on", online > 0);

        const syncing = n.transfer || st.peers.some((p) => p.syncing);
        if (n.problem) lamp("off", n.problem);
        else if (n.heldDeletes) lamp("off", `${n.heldDeletes} files vanished – deletion held`);
        else if (syncing) lamp("sync", n.transfer ? `Receiving ${n.transfer.path}` : "Syncing");
        else if (n.pending) lamp("scan");
        else if (!st.peers.length) lamp("alone");
        else if (!online) lamp("off", "No other PC reachable");
        else lamp("idle", `Up to date · checked ${ago(n.lastScan)}`);

        // Hinweis bei angehaltenen Löschungen: erst nach Bestätigung gehen sie an die anderen PCs.
        const notes = $("notes");
        const held = n.heldDeletes
            ? `<div class="notice"><span><b>${n.heldDeletes} files are missing from the shared folder.</b> The deletion is held so it does not reach the other PCs by mistake (for example, a drive that is not connected).</span><button type="button" class="danger" id="allow">Delete on all PCs</button></div>`
            : "";
        if (notes.dataset.html !== held) {
            notes.dataset.html = held;
            notes.innerHTML = held;
            const allow = $("allow");
            if (allow) allow.addEventListener("click", async () => {
                const r = await post("/api/allow-deletes");
                toast(r.message ?? "Done", !r.ok);
            });
        }

        $("peers").innerHTML = st.peers.length
            ? st.peers.map((p) => {
                const dot = p.syncing ? "sync" : p.online ? "" : "off";
                const via = p.route ? ` via ${p.route}` : "";
                const state = p.duplicate ? `standby${via}: connected over a faster way`
                    : p.online ? `synced ${ago(p.lastSync)}${via}` : (p.error ? "offline" : "connecting…");
                const remove = p.manual ? `<button type="button" class="quiet sq" data-remove="${esc(p.address)}" title="Remove this PC" aria-label="Remove ${esc(p.address)}"><svg class="ic"><use href="#i-x" /></svg></button>` : "";
                return `<div class="ms-peer">
                    <span class="dot ${dot}" title="${p.online ? "online" : "offline"}"></span>
                    <span class="nm"><b>${esc(p.name ?? p.address)}</b><span class="muted mono">${esc(p.address)} · ${esc(state)}</span>${p.error && !p.online ? `<span class="err">${esc(p.error)}</span>` : ""}</span>
                    ${remove}
                </div>`;
            }).join("")
            : `<div class="empty"><div><b>No other PC yet</b>Start StykkerMeshShare on another PC in the home network, or add one by name.</div></div>`;

        $("activity").innerHTML = st.activity.length
            ? st.activity.map((a) => `<div class="ms-act"><span class="k ${esc(a.kind)}">${esc(a.kind)}</span><span class="p" title="${esc(a.path)}">${esc(a.path)}${a.from ? ` <span class="muted">· ${esc(a.from)}</span>` : ""}</span><span class="t">${clock(a.time)}</span></div>`).join("")
            : `<p class="muted" style="margin:0">Nothing happened yet.</p>`;

        renderTransfers(st);

        if (n.seq !== lastSeq) {
            lastSeq = n.seq;
            loadFiles();
        }
    }

    // ── Dateien ──
    let files = [];
    async function loadFiles() {
        try { files = await fetch("/api/files").then((r) => r.json()); }
        catch { return; }
        renderFiles();
    }
    function renderFiles() {
        const q = ($("q").value || "").trim().toLowerCase();
        const shown = files
            .filter((f) => !q || f.path.toLowerCase().includes(q))
            .sort((a, b) => new Date(b.modified) - new Date(a.modified))
            .slice(0, 300);
        $("files").innerHTML = shown.length
            ? shown.map((f) => `<div class="ms-row"><a href="/api/download?path=${encodeURIComponent(f.path)}" title="Download ${esc(f.path)}">${esc(f.path)}</a><span class="num">${size(f.size)}</span><span class="when">${esc(ago(f.modified))}</span></div>`).join("")
            : `<p class="muted" style="margin:0">${files.length ? "No file matches." : "The shared folder is empty."}</p>`;
    }
    $("q").addEventListener("input", renderFiles);

    // ── Dropzone ──
    const drop = $("drop");
    const pick = $("pick");

    // Ordner beim Ablegen: die Einträge rekursiv lesen, damit die Unterordner erhalten bleiben.
    async function collect(entry, prefix, out) {
        if (entry.isFile) {
            const file = await new Promise((resolve, reject) => entry.file(resolve, reject));
            out.push({ file, path: prefix + file.name });
        } else if (entry.isDirectory) {
            const reader = entry.createReader();
            for (;;) {
                const batch = await new Promise((resolve, reject) => reader.readEntries(resolve, reject));
                if (!batch.length) break;
                for (const child of batch) await collect(child, prefix + entry.name + "/", out);
            }
        }
    }

    function upload(item) {
        return new Promise((resolve) => {
            const row = document.createElement("div");
            row.className = "ms-up";
            row.innerHTML = `<span class="mono ms-ellipsis" title="${esc(item.path)}">${esc(item.path)}</span><span class="ms-prog"><i></i></span><span class="p muted">0 %</span>`;
            $("uploads").prepend(row);
            const bar = row.querySelector(".ms-prog i");
            const pct = row.querySelector(".p");
            const xhr = new XMLHttpRequest();
            xhr.open("POST", `/api/upload?path=${encodeURIComponent(item.path)}`);
            xhr.upload.onprogress = (e) => {
                if (!e.lengthComputable) return;
                const p = Math.round((e.loaded / e.total) * 100);
                bar.style.width = p + "%";
                pct.textContent = p + " %";
            };
            xhr.onload = () => {
                let answer = {};
                try { answer = JSON.parse(xhr.responseText); } catch { }
                const ok = xhr.status === 200 && answer.ok;
                row.classList.add(ok ? "ok" : "err");
                bar.style.width = "100%";
                pct.textContent = ok ? "done" : "failed";
                if (!ok) row.title = answer.message ?? `HTTP ${xhr.status}`;
                setTimeout(() => row.remove(), ok ? 6000 : 20000);
                resolve(ok);
            };
            xhr.onerror = () => { row.classList.add("err"); pct.textContent = "failed"; resolve(false); };
            xhr.send(item.file);
        });
    }

    async function uploadAll(items) {
        if (!items.length) return;
        let ok = 0;
        for (const item of items) if (await upload(item)) ok++;
        toast(ok === items.length ? `${ok} file${ok === 1 ? "" : "s"} added` : `${ok} of ${items.length} files added`, ok !== items.length);
        tick();
    }

    drop.addEventListener("click", (e) => { if (!e.target.closest("button")) pick.click(); });
    drop.addEventListener("keydown", (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); pick.click(); } });
    $("pick-btn").addEventListener("click", () => pick.click());
    pick.addEventListener("change", () => {
        uploadAll([...pick.files].map((file) => ({ file, path: file.name })));
        pick.value = "";
    });
    for (const type of ["dragenter", "dragover"]) drop.addEventListener(type, (e) => { e.preventDefault(); drop.classList.add("over"); });
    drop.addEventListener("dragleave", (e) => { if (!drop.contains(e.relatedTarget)) drop.classList.remove("over"); });
    drop.addEventListener("drop", async (e) => {
        e.preventDefault();
        drop.classList.remove("over");
        const items = [];
        const entries = [...(e.dataTransfer.items || [])].map((i) => i.webkitGetAsEntry && i.webkitGetAsEntry()).filter(Boolean);
        if (entries.length) for (const entry of entries) await collect(entry, "", items);
        else for (const file of e.dataTransfer.files) items.push({ file, path: file.name });
        uploadAll(items);
    });
    // Nicht versehentlich die Datei im Fenster öffnen, wenn sie neben der Dropzone landet.
    for (const type of ["dragover", "drop"]) window.addEventListener(type, (e) => { if (!drop.contains(e.target)) e.preventDefault(); });

    $("open-folder").addEventListener("click", async () => {
        const r = await post("/api/open-folder");
        if (!r.ok) toast(r.message ?? "Could not open the folder", true);
    });

    // ── Ordner wechseln ──
    const folderForm = $("folder-form");
    $("folder-btn").addEventListener("click", () => {
        folderForm.hidden = !folderForm.hidden;
        if (!folderForm.hidden) { $("folder-path").value = currentFolder; $("folder-path").select(); }
    });
    $("folder-cancel").addEventListener("click", () => { folderForm.hidden = true; });
    folderForm.addEventListener("submit", async (e) => {
        e.preventDefault();
        const move = folderForm.querySelector("input[name=folder-mode]:checked").value === "move";
        const apply = $("folder-apply");
        apply.setAttribute("aria-busy", "true");
        apply.disabled = true;
        try {
            const r = await post("/api/folder", { path: $("folder-path").value, move });
            toast(r.message ?? (r.ok ? "Changed" : "Failed"), !r.ok);
            if (r.ok) { folderForm.hidden = true; lastSeq = -1; }
        } finally {
            apply.removeAttribute("aria-busy");
            apply.disabled = false;
        }
        tick();
    });

    // ── Gegenstellen ──
    $("add-peer").addEventListener("submit", async (e) => {
        e.preventDefault();
        const input = $("peer-address");
        const r = await post("/api/peers", { address: input.value });
        toast(r.message ?? (r.ok ? "Added" : "Failed"), !r.ok);
        if (r.ok) input.value = "";
        tick();
    });
    $("peers").addEventListener("click", async (e) => {
        const button = e.target.closest("button[data-remove]");
        if (!button) return;
        const r = await post("/api/peers/remove", { address: button.dataset.remove });
        toast(r.message ?? "Removed", !r.ok);
        tick();
    });

    // ── Themenwahl ──
    for (const button of document.querySelectorAll(".seg button[data-theme-choice]")) {
        button.addEventListener("click", () => {
            const theme = button.dataset.themeChoice;
            document.documentElement.dataset.theme = theme;
            try { localStorage.setItem("stykkermeshshare.theme", theme); } catch { }
            for (const other of document.querySelectorAll(".seg button[data-theme-choice]")) other.classList.toggle("on", other === button);
        });
        button.classList.toggle("on", document.documentElement.dataset.theme === button.dataset.themeChoice);
    }

    // ── Übertragung ──
    // Die laufende Datei mit Fortschritt, Rate und Restzeit; darunter die Warteschlange. Ziehen ändert die Reihenfolge
    // (am Handy „Nach oben“). Während gezogen wird, zeichnet die Abfrage die Liste nicht neu.
    let dragging = null;
    let waitingOrder = [];   // ohne die laufende Datei
    let currentPath = null;
    const rate = (bps) => bps > 0 ? `${size(bps)}/s` : "–";
    function left(seconds) {
        if (!Number.isFinite(seconds) || seconds <= 0) return "";
        if (seconds < 60) return `${Math.ceil(seconds)} s left`;
        if (seconds < 3600) return `${Math.ceil(seconds / 60)} min left`;
        return `${(seconds / 3600).toFixed(1)} h left`;
    }

    function renderTransfers(st) {
        const t = st.node.transfer;
        const queue = st.queue || [];
        const paused = st.paused || [];
        const waiting = queue.filter((q) => !t || q.path !== t.path);
        const rest = waiting.reduce((sum, q) => sum + Math.max(0, q.size - q.done), 0) + (t ? Math.max(0, t.size - t.done) : 0);
        set("v-rate", t ? `${rate(t.rate)} · ${size(rest)} to go${t.rate > 0 ? " · " + left(rest / t.rate) : ""}` : "");

        const cur = $("current");
        if (t) {
            const pct = t.size > 0 ? Math.min(100, (t.done / t.size) * 100) : 100;
            const info = t.checking
                ? [`checking the ${size(t.done)} already here before it continues`]
                : [`from ${esc(t.from)}`, left((t.size - t.done) / t.rate)];
            if (t.resumed > 0 && !t.checking) info.push(`continued at ${size(t.resumed)}`);
            cur.innerHTML = `<div class="ms-cur-top"><b title="${esc(t.path)}">${esc(t.path)}</b><span class="num">${size(t.done)} of ${size(t.size)} · ${pct.toFixed(0)} %</span></div>
                <div class="ms-prog"><i style="width:${pct.toFixed(1)}%"></i></div>
                <span class="muted ms-cur-info">${info.filter(Boolean).join(" · ")}</span>`;
        } else {
            cur.innerHTML = queue.length || paused.length ? "" : `<p class="muted" style="margin:0">Nothing to transfer.</p>`;
        }

        if (dragging) return;
        currentPath = t ? t.path : null;
        waitingOrder = waiting.map((q) => q.path);
        const row = (q, i, isPaused) => {
            const pct = q.size > 0 ? Math.round((q.done / q.size) * 100) : 0;
            const state = q.done > 0 ? `paused at ${pct} %` : size(q.size);
            const top = !isPaused && (i > 0 || t)
                ? `<button type="button" class="quiet sq" data-top="${esc(q.path)}" title="Load this now; a running file pauses and continues later" aria-label="Load ${esc(q.path)} now"><svg class="ic"><use href="#i-arrow-up" /></svg></button>`
                : `<span></span>`;
            return `<div class="ms-q${q.done > 0 ? " paused" : ""}" ${isPaused ? "" : `draggable="true" data-path="${esc(q.path)}"`}>
                <span class="grip" aria-hidden="true">${isPaused ? "" : "⠿"}</span>
                <span class="p" title="${esc(q.path)}">${esc(q.path)}${q.from ? ` <span class="muted">· ${esc(q.from)}</span>` : ""}</span>
                <span class="num">${q.done > 0 ? `${size(q.size)} · ${state}` : state}</span>${top}</div>`;
        };
        const html = (waiting.length ? `<div class="muted ms-q-head">Next (${waiting.length}) · drag to change the order</div>` : "")
            + waiting.map((q, i) => row(q, i, false)).join("")
            + (paused.length ? `<div class="muted ms-q-head">Started, continues when the PC that has it is online</div>` + paused.map((q, i) => row(q, i, true)).join("") : "");
        const box = $("queue");
        if (box.dataset.html !== html) { box.dataset.html = html; box.innerHTML = html; }
    }

    // Ganz oben in „Next“ heißt: jetzt laden (vor der laufenden Datei, die dann pausiert). Sonst nach ihr.
    async function saveOrder(waiting, now) {
        const order = currentPath
            ? (now ? [waiting[0], currentPath, ...waiting.slice(1)] : [currentPath, ...waiting])
            : waiting;
        waitingOrder = waiting;
        const r = await post("/api/queue/order", { paths: order });
        if (!r.ok) toast(r.message ?? "Could not change the order", true);
        tick();
    }

    const queueBox = $("queue");
    queueBox.addEventListener("click", (e) => {
        const button = e.target.closest("[data-top]");
        if (!button) return;
        const path = button.dataset.top;
        saveOrder([path, ...waitingOrder.filter((p) => p !== path)], true);
    });
    queueBox.addEventListener("dragstart", (e) => {
        const row = e.target.closest(".ms-q[data-path]");
        if (!row) return;
        dragging = row.dataset.path;
        row.classList.add("dragging");
        e.dataTransfer.effectAllowed = "move";
        e.dataTransfer.setData("text/plain", dragging);
    });
    function clearMarks() { for (const el of queueBox.querySelectorAll(".drop-before,.drop-after")) el.classList.remove("drop-before", "drop-after"); }
    function dropTarget(e) {
        const row = e.target.closest(".ms-q[data-path]");
        if (!row || row.dataset.path === dragging) return null;
        const box = row.getBoundingClientRect();
        return { row, after: e.clientY > box.top + box.height / 2 };
    }
    queueBox.addEventListener("dragover", (e) => {
        if (!dragging) return;
        e.preventDefault();   // auch über der eigenen Zeile: sonst zeigt der Browser „nicht erlaubt“
        const target = dropTarget(e);
        clearMarks();
        if (target) target.row.classList.add(target.after ? "drop-after" : "drop-before");
    });
    queueBox.addEventListener("drop", (e) => {
        if (!dragging) return;
        e.preventDefault();
        const target = dropTarget(e);
        const moved = dragging;
        clearMarks();
        if (!target) return;
        const order = waitingOrder.filter((p) => p !== moved);
        let at = order.indexOf(target.row.dataset.path);
        if (target.after) at++;
        order.splice(at, 0, moved);
        saveOrder(order, at === 0);
    });
    queueBox.addEventListener("dragend", () => {
        dragging = null;
        clearMarks();
        for (const el of queueBox.querySelectorAll(".dragging")) el.classList.remove("dragging");
        delete queueBox.dataset.html;   // beim nächsten Mal neu zeichnen
    });

    async function tick() {
        try { renderStatus(await fetch("/api/status").then((r) => r.json())); }
        catch { lamp("off", "The server does not answer"); }
    }
    tick();
    setInterval(tick, 1500);
})();
