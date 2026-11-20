// Sync-Status: Lampe und Satz, die anderen PCs, angehaltene Löschungen, Aktivität und das Wechseln des Ordners.
(() => {
    "use strict";
    const { $, esc, post, size, ago, set, html, toast, tick } = MS;

    // Zustände der Lampe (Symbole aus dem Design-System). „Kein PC erreichbar“ ist kein Fehler (der andere PC kann
    // einfach aus sein) und bekommt darum nicht das rote „Problem“.
    const LAMP = {
        idle: ["idle", "Up to date"],
        sync: ["gen", "Syncing"],
        scan: ["load", "Waiting for files in use"],
        off: ["off", "Problem"],
        away: ["load", "No other PC reachable"],
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
    MS.onOffline(() => lamp("off", "The server does not answer"));

    // Was in der Aktivität steht, in Worten statt als Kennung.
    function activityText(a) {
        const by = a.from ? ` by ${a.from}` : "";
        switch (a.kind) {
            case "added": return "Added here";
            case "changed": return "Changed here";
            case "deleted": return "Deleted here";
            case "received": return `Received from ${a.from ?? "another PC"}`;
            case "removed": return `Deleted${by}`;
            case "conflict": return "Conflict copy (changed on two PCs)";
            case "kept": return `Kept: changed here, deleted${by}`;
            case "restored": return "Restored from the trash";
            case "folder": return a.message ?? "Folder changed";
            case "error": return a.message ?? "Problem";
            default: return a.kind;
        }
    }

    let currentFolder = "";
    MS.onStatus((st) => {
        const n = st.node;
        set("v-name", n.name);
        set("v-files", String(n.files));
        set("v-size", size(n.bytes));
        renderDisk(n);
        set("v-folder", n.folder);
        $("folder-btn").hidden = !n.local;
        $("open-folder").hidden = !n.local;
        $("add-peer").hidden = !n.local;   // PCs verwaltet man nur am PC selbst
        currentFolder = n.folder;
        const active = st.peers.filter((p) => !p.duplicate);
        const online = active.filter((p) => p.online).length;
        set("v-peers", `${online}/${active.length}`);
        set("v-peer-count", String(active.length));
        $("v-peers-chip").classList.toggle("on", online > 0);

        const syncing = n.transfer || st.peers.some((p) => p.syncing);
        if (n.problem) lamp("off", n.problem);
        else if (n.disk?.low) lamp("scan", `Receiving paused · only ${size(n.disk.free)} free`);
        else if (n.heldDeletes) lamp("off", `${n.heldDeletes} files vanished – deletion held`);
        else if (n.transfer) lamp("sync", n.transfer.checking ? `Checking ${n.transfer.path}` : `Receiving ${n.transfer.path}`);
        else if (syncing) lamp("sync");
        else if (n.transfersPaused && (st.queue.length || st.paused.length)) lamp("scan", "Downloads paused");
        else if (n.pending) lamp("scan");
        else if (!active.length) lamp("alone");
        else if (!online) lamp("away");
        else lamp("idle", `Up to date · checked ${ago(n.lastScan)}`);

        // Angehaltene Löschungen: erst nach Bestätigung (am PC selbst) gehen sie an die anderen PCs.
        // Platte fast voll: dieser PC nimmt nichts an, bis wieder genug frei ist.
        html($("notes"), (n.heldDeletes
            ? `<div class="notice"><span><b>${n.heldDeletes} files are missing from the shared folder.</b> The deletion is held so it does not reach the other PCs by mistake (for example, a drive that is not connected).</span>${n.local ? `<button type="button" class="danger" id="allow">Delete on all PCs</button>` : `<span class="muted">Confirm on the PC itself.</span>`}</div>`
            : "")
            + (n.disk?.low
                ? `<div class="notice"><span><b>Only ${size(n.disk.free)} free on ${esc(n.disk.root)} – receiving is paused.</b> New files from other PCs wait until at least ${size(n.disk.minFree)} are free; uploads here are refused. This PC still sends its own changes and deletions. Free up space or change the folder.</span></div>`
                : ""));

        html($("peers"), st.peers.length
            ? st.peers.map((p) => {
                const dot = p.syncing ? "sync" : p.online ? "" : "off";
                const via = p.route ? ` via ${p.route}` : "";
                const state = p.duplicate ? `standby${via}: connected over a faster way`
                    : p.online ? `synced ${ago(p.lastSync)}${via}` : (p.error ? "offline" : "connecting…");
                const space = spaceOf(p);
                const remove = p.manual && n.local ? `<button type="button" class="quiet sq" data-remove="${esc(p.address)}" title="Remove this PC" aria-label="Remove ${esc(p.address)}"><svg class="ic"><use href="#i-x" /></svg></button>` : "";
                return `<div class="ms-peer${p.duplicate ? " standby" : ""}">
                    <span class="dot ${dot}" title="${p.online ? "online" : "offline"}"></span>
                    <span class="nm"><b>${esc(p.name ?? p.address)}</b><span class="muted mono">${esc(p.address)} · ${esc(state)}</span>${p.error && !p.online ? `<span class="err">${esc(p.error)}</span>` : ""}</span>
                    ${space}${remove || "<span></span>"}
                </div>`;
            }).join("")
            : `<div class="empty"><div><b>No other PC yet</b>Start StykkerMeshShare on another PC in the home network, or add one by name.</div></div>`);

        html($("activity"), st.activity.length
            ? st.activity.map((a) => `<div class="ms-act"><span class="k ${esc(a.kind)}">${esc(activityText(a))}</span><span class="p" title="${esc(a.path)}">${esc(a.path)}</span><span class="t" title="${esc(new Date(a.time).toLocaleString())}">${esc(ago(a.time))}</span></div>`).join("")
            : `<p class="muted" style="margin:0">Nothing happened yet.</p>`);
    });

    // ── Freier Platz ──
    let minFree = 0;
    function renderDisk(n) {
        const d = n.disk;
        $("disk").hidden = !d;
        $("disk-hint").hidden = !d;
        if (!d) { set("v-free", "–"); return; }
        const used = d.total > 0 ? Math.min(100, ((d.total - d.free) / d.total) * 100) : 0;
        set("v-free", size(d.free));
        $("v-free-chip").classList.toggle("low", d.low);
        $("disk").classList.toggle("low", d.low);
        set("v-disk", `${d.root} · ${size(d.free)} free`);
        set("v-disk-total", `of ${size(d.total)}`);
        $("v-disk-bar").style.width = `${used.toFixed(1)}%`;
        set("v-minfree-text", d.minFree <= 0 ? "No limit: receives until the disk is full."
            : d.low ? `Receiving continues by itself once ${size(d.minFree)} are free.`
            : `Receiving stops below ${size(d.minFree)} free.`);
        $("minfree-btn").hidden = !n.local;
        minFree = d.minFree;
    }
    // Freier Platz eines anderen PCs (meldet er mit; ältere Fassungen nicht).
    function spaceOf(p) {
        if (p.free == null) return `<span class="space"></span>`;
        const used = p.total > 0 ? Math.min(100, ((p.total - p.free) / p.total) * 100) : 0;
        const low = p.receiving === false;
        const text = low ? `${size(p.free)} free · not receiving` : p.paused ? `${size(p.free)} free · paused` : `${size(p.free)} free`;
        return `<span class="space${low ? " low" : ""}" title="Free space on the drive of that PC's shared folder">${esc(text)}<span class="bar"><i style="width:${used.toFixed(1)}%"></i></span></span>`;
    }
    const gb = (bytes) => Math.round((bytes / 1073741824) * 10) / 10;
    $("minfree-btn").addEventListener("click", () => {
        const form = $("minfree-form");
        form.hidden = !form.hidden;
        if (!form.hidden) { $("minfree-gb").value = String(gb(minFree)); $("minfree-gb").select(); }
    });
    $("minfree-cancel").addEventListener("click", () => { $("minfree-form").hidden = true; });
    $("minfree-form").addEventListener("submit", async (e) => {
        e.preventDefault();
        const value = Number($("minfree-gb").value);
        if (!Number.isFinite(value) || value < 0) { toast("Enter a number of GB, 0 or more.", true); return; }
        const r = await post("/api/settings/min-free", { gb: value });
        toast(r.message ?? "", !r.ok);
        if (r.ok) $("minfree-form").hidden = true;
        tick();
    });

    $("notes").addEventListener("click", async (e) => {
        if (!e.target.closest("#allow")) return;
        const r = await post("/api/allow-deletes");
        toast(r.message ?? "Done", !r.ok);
        tick();
    });

    // ── Ordner wechseln ──
    const form = $("folder-form");
    const path = $("folder-path");
    const check = $("folder-check");
    let checkTimer = null;
    $("folder-btn").addEventListener("click", () => {
        form.hidden = !form.hidden;
        if (!form.hidden) { path.value = currentFolder; path.select(); }
    });
    $("folder-cancel").addEventListener("click", () => { form.hidden = true; });
    // Beim Tippen fragen, ob der Ordner taugt (leer, kein Systemordner, kein Netzwerkpfad).
    path.addEventListener("input", () => {
        clearTimeout(checkTimer);
        checkTimer = setTimeout(async () => {
            if (!path.value.trim()) return;
            const r = await post("/api/folder/check", { path: path.value });
            check.textContent = r.message ?? "";
            check.classList.toggle("bad", !r.ok);
            check.classList.toggle("good", !!r.ok);
        }, 350);
    });
    form.addEventListener("submit", async (e) => {
        e.preventDefault();
        const move = form.querySelector("input[name=folder-mode]:checked").value === "move";
        const apply = $("folder-apply");
        apply.setAttribute("aria-busy", "true");
        apply.disabled = true;
        try {
            const r = await post("/api/folder", { path: path.value, move });
            toast(r.message ?? (r.ok ? "Changed" : "Failed"), !r.ok);
            if (r.ok) { form.hidden = true; MS.filesChanged?.(); }
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

    $("open-folder").addEventListener("click", async () => {
        const r = await post("/api/open-folder");
        if (!r.ok) toast(r.message ?? "Could not open the folder", true);
    });
})();
