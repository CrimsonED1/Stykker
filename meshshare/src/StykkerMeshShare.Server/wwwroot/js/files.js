// Dateien (ansehen, herunterladen, suchen), Hochladen über Dropzone oder Auswahl (in einen Unterordner, mit
// Abbrechen) und der Papierkorb zum Zurückholen.
(() => {
    "use strict";
    const { $, esc, post, size, ago, html, toast, tick } = MS;
    const ShowAtMost = 300;

    // ── Dateien ──
    let files = [];
    let lastSeq = -1;
    async function loadFiles() {
        try { files = await fetch("/api/files").then((r) => r.json()); }
        catch { return; }
        renderFiles();
        renderTargets();
    }
    MS.filesChanged = () => { lastSeq = -1; };
    MS.onStatus((st) => {
        if (st.node.seq !== lastSeq) {
            lastSeq = st.node.seq;
            loadFiles();
        }
    });

    function renderFiles() {
        const q = ($("q").value || "").trim().toLowerCase();
        const matching = files
            .filter((f) => !q || f.path.toLowerCase().includes(q) || (q === "conflict" && f.conflict))
            .sort((a, b) => new Date(b.modified) - new Date(a.modified));
        const shown = matching.slice(0, ShowAtMost);
        html($("files"), shown.length
            ? shown.map((f) => {
                const path = encodeURIComponent(f.path);
                const name = esc(f.path);
                // Bilder, PDFs, Text, Audio, Video öffnen im Browser; alles andere wird heruntergeladen.
                const open = f.viewable
                    ? `<a href="/api/download?path=${path}&inline=1" target="_blank" rel="noopener" title="Open ${name}">${name}</a>`
                    : `<a href="/api/download?path=${path}" title="Download ${name}">${name}</a>`;
                const badge = f.conflict ? `<span class="badge warn" title="Changed on two PCs at the same time; both versions are kept">conflict</span>` : "";
                return `<div class="ms-row"><span class="nm">${open}${badge}${f.from ? `<span class="muted ms-from">${esc(f.from)}</span>` : ""}</span>
                    <span class="num">${size(f.size)}</span><span class="when">${esc(ago(f.modified))}</span>
                    <a class="quiet sq ms-dl" href="/api/download?path=${path}" download aria-label="Download ${name}" title="Download"><svg class="ic"><use href="#i-arrow-down" /></svg></a></div>`;
            }).join("")
            : `<p class="muted" style="margin:0">${files.length ? "No file matches." : "The shared folder is empty."}</p>`);
        const conflicts = files.filter((f) => f.conflict).length;
        const notes = [];
        if (matching.length > ShowAtMost) notes.push(`Showing the newest ${ShowAtMost} of ${matching.length.toLocaleString()} – search to narrow.`);
        else if (files.length) notes.push(`${files.length.toLocaleString()} file${files.length === 1 ? "" : "s"}, newest first.`);
        if (conflicts) notes.push(`${conflicts} conflict cop${conflicts === 1 ? "y" : "ies"} – search "conflict" to list them.`);
        MS.set("files-note", notes.join(" "));
    }
    $("q").addEventListener("input", renderFiles);

    // Ziel beim Hochladen: der Ordner selbst oder einer seiner Unterordner.
    function renderTargets() {
        const select = $("target");
        const dirs = new Set();
        for (const f of files) {
            const parts = f.path.split("/");
            for (let i = 1; i < parts.length; i++) dirs.add(parts.slice(0, i).join("/"));
        }
        const list = [...dirs].sort((a, b) => a.localeCompare(b)).slice(0, 500);
        const markup = `<option value="">the shared folder</option>` + list.map((d) => `<option value="${esc(d)}">${esc(d)}</option>`).join("");
        if (select.dataset.html === markup) return;
        const keep = select.value;
        select.dataset.html = markup;
        select.innerHTML = markup;
        select.value = list.includes(keep) ? keep : "";
    }

    // ── Hochladen ──
    const drop = $("drop");
    const pick = $("pick");
    const pickDir = $("pick-dir");
    let running = 0;
    const active = new Set();

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

    function upload(item, index, count) {
        return new Promise((resolve) => {
            const row = document.createElement("div");
            row.className = "ms-up";
            row.innerHTML = `<span class="mono ms-ellipsis" title="${esc(item.path)}">${count > 1 ? `${index + 1}/${count} · ` : ""}${esc(item.path)}</span><span class="bar"><i></i></span><span class="p muted">0 %</span><button type="button" class="quiet sq" aria-label="Cancel ${esc(item.path)}" title="Cancel"><svg class="ic"><use href="#i-x" /></svg></button><span class="msg"></span>`;
            $("uploads").prepend(row);
            const bar = row.querySelector(".bar i");
            const pct = row.querySelector(".p");
            const msg = row.querySelector(".msg");
            const cancel = row.querySelector("button");
            const xhr = new XMLHttpRequest();
            active.add(xhr);
            const done = (ok, text) => {
                active.delete(xhr);
                row.classList.add(ok ? "ok" : "err");
                cancel.remove();
                pct.textContent = ok ? "done" : text === "Cancelled" ? "cancelled" : "failed";
                if (!ok && text) msg.textContent = text;   // der Grund sichtbar, auch am Handy
                setTimeout(() => row.remove(), ok ? 6000 : 20000);
                resolve(ok);
            };
            cancel.addEventListener("click", () => xhr.abort());
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
                bar.style.width = "100%";
                done(ok, ok ? null : answer.message ?? `HTTP ${xhr.status}`);
            };
            xhr.onerror = () => done(false, "The connection broke off.");
            xhr.onabort = () => done(false, "Cancelled");
            xhr.send(item.file);
        });
    }

    async function uploadAll(items) {
        if (!items.length) return;
        const target = $("target").value;
        if (target) items = items.map((i) => ({ ...i, path: `${target}/${i.path}` }));
        running++;
        let ok = 0;
        for (let i = 0; i < items.length; i++) if (await upload(items[i], i, items.length)) ok++;
        running--;
        toast(ok === items.length ? `${ok} file${ok === 1 ? "" : "s"} added` : `${ok} of ${items.length} files added`, ok !== items.length);
        tick();
    }
    // Fenster schließen während eines Uploads: nachfragen.
    window.addEventListener("beforeunload", (e) => { if (running > 0 || active.size) { e.preventDefault(); e.returnValue = ""; } });

    $("pick-btn").addEventListener("click", () => pick.click());
    $("pick-dir-btn").addEventListener("click", () => pickDir.click());
    pick.addEventListener("change", () => {
        uploadAll([...pick.files].map((file) => ({ file, path: file.name })));
        pick.value = "";
    });
    pickDir.addEventListener("change", () => {
        uploadAll([...pickDir.files].map((file) => ({ file, path: file.webkitRelativePath || file.name })));
        pickDir.value = "";
    });
    for (const type of ["dragenter", "dragover"]) drop.addEventListener(type, (e) => {
        if (![...(e.dataTransfer?.types || [])].includes("Files")) return;
        e.preventDefault();
        drop.classList.add("over");
    });
    drop.addEventListener("dragleave", (e) => { if (!drop.contains(e.relatedTarget)) drop.classList.remove("over"); });
    drop.addEventListener("drop", async (e) => {
        if (![...(e.dataTransfer?.types || [])].includes("Files")) return;
        e.preventDefault();
        drop.classList.remove("over");
        const items = [];
        const entries = [...(e.dataTransfer.items || [])].map((i) => i.webkitGetAsEntry && i.webkitGetAsEntry()).filter(Boolean);
        if (entries.length) for (const entry of entries) await collect(entry, "", items);
        else for (const file of e.dataTransfer.files) items.push({ file, path: file.name });
        uploadAll(items);
    });
    // Nicht versehentlich die Datei im Fenster öffnen, wenn sie neben der Dropzone landet.
    for (const type of ["dragover", "drop"]) window.addEventListener(type, (e) => {
        if (!drop.contains(e.target) && [...(e.dataTransfer?.types || [])].includes("Files")) e.preventDefault();
    });

    // ── Papierkorb ──
    async function loadTrash() {
        let items = [];
        try { items = await fetch("/api/trash").then((r) => r.json()); } catch { return; }
        html($("trash"), items.length
            ? items.map((t) => `<div class="ms-row"><span class="nm" title="${esc(t.path)}">${esc(t.path)}</span><span class="num">${size(t.size)}</span><span class="when">${esc(ago(t.removed))}</span>
                <button type="button" class="quiet" data-restore="${esc(t.id)}" aria-label="Restore ${esc(t.path)}">Restore</button></div>`).join("")
            : `<p class="muted" style="margin:0">The trash is empty.</p>`);
    }
    $("trash-box").addEventListener("toggle", () => { if ($("trash-box").open) loadTrash(); });
    $("trash").addEventListener("click", async (e) => {
        const button = e.target.closest("[data-restore]");
        if (!button) return;
        const r = await post("/api/trash/restore", { id: button.dataset.restore });
        toast(r.message ?? "", !r.ok);
        loadTrash();
        tick();
    });
})();
