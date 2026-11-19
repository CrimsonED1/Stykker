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
    function renderStatus(st) {
        const n = st.node;
        set("v-files", String(n.files));
        set("v-size", size(n.bytes));
        set("v-folder", n.folder);
        const online = st.peers.filter((p) => p.online).length;
        set("v-peers", `${online}/${st.peers.length}`);
        set("v-peer-count", String(st.peers.length));
        $("v-peers-chip").classList.toggle("on", online > 0);

        const syncing = n.transfer || st.peers.some((p) => p.syncing);
        if (n.problem) lamp("off", n.problem);
        else if (n.heldDeletes) lamp("off", `${n.heldDeletes} files vanished – deletion held`);
        else if (syncing) lamp("sync", n.transfer ? `Receiving ${n.transfer}` : "Syncing");
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
                const state = p.duplicate ? "same PC under another address"
                    : p.online ? `synced ${ago(p.lastSync)}` : (p.error ? "offline" : "connecting…");
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

    async function tick() {
        try { renderStatus(await fetch("/api/status").then((r) => r.json())); }
        catch { lamp("off", "The server does not answer"); }
    }
    tick();
    setInterval(tick, 1500);
})();
