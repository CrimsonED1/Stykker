// Dateien: ansehen, herunterladen, suchen, und verwalten – auswählen, an PCs senden, umbenennen, verschieben, löschen
// (wirkt auf allen PCs, gelöscht wird endgültig). Hochladen über Dropzone oder Auswahl, an alle PCs oder nur an
// bestimmte (Chips), in einen Unterordner, mit Abbrechen.
(() => {
    "use strict";
    const { $, esc, post, size, html, toast, tick } = MS;
    const ShowAtMost = 300;

    // ── Stand ──
    let files = [];
    let nodes = [];            // {id, name, self}
    let status = null;
    let lastSeq = -1;
    const selected = new Set();
    let shown = [];            // die gerade angezeigten Dateien (für „alle auswählen“)

    const self = () => nodes.find((n) => n.self);
    const others = () => nodes.filter((n) => !n.self);
    const nameOf = (id) => nodes.find((n) => n.id === id)?.name ?? "another PC";
    const base = (p) => p.slice(p.lastIndexOf("/") + 1);
    const dir = (p) => (p.includes("/") ? p.slice(0, p.lastIndexOf("/")) : "");
    const short = (name) => (name.length > 10 ? name.slice(0, 9) + "…" : name);

    async function loadFiles() {
        let answer;
        try { answer = await fetch("/api/files").then((r) => r.json()); }
        catch { return; }
        files = answer.files ?? [];
        nodes = answer.nodes ?? [];
        const paths = new Set(files.map((f) => f.path));
        for (const p of [...selected]) if (!paths.has(p)) selected.delete(p);
        renderChips();
        renderFiles();
        renderTargets();
    }
    MS.filesChanged = () => { lastSeq = -1; };
    MS.onStatus((st) => {
        status = st;
        if (st.node.seq !== lastSeq) {
            lastSeq = st.node.seq;
            loadFiles();
        } else renderFiles();   // „kommt gerade an“ ändert sich mit jedem Takt
    });

    // Wie eine Datei auf einem PC steht: y hat sie, h kommt gerade an, w soll sie bekommen, "" nicht da.
    function stateOn(f, id) {
        if (f.on.includes(id)) return "y";
        if (self()?.id === id) {
            const t = status?.node?.transfer;
            if ((t && t.path === f.path) || status?.queue?.some((q) => q.path === f.path)) return "h";
        } else if (status?.peers?.find((p) => p.id === id)?.progress?.file === f.path) return "h";
        if (f.to && f.to.includes(id)) return "w";
        return "";
    }

    // ── Dateiliste ──
    function renderFiles() {
        const q = ($("q").value || "").trim().toLowerCase();
        const matching = files
            .filter((f) => !q || f.path.toLowerCase().includes(q) || (q === "conflict" && f.conflict))
            .sort((a, b) => new Date(b.modified) - new Date(a.modified));
        shown = matching.slice(0, ShowAtMost);
        const many = nodes.length > 1;
        html($("files"), shown.length
            ? shown.map((f) => {
                const path = encodeURIComponent(f.path);
                const name = esc(f.path);
                const sel = selected.has(f.path);
                // Liegt sie hier: öffnen (Bilder, PDFs, Text, Audio, Video im Browser) oder herunterladen. Sonst nur der Name.
                const open = !f.here
                    ? `<span title="Not on this PC">${name}</span>`
                    : f.viewable
                        ? `<a href="/api/download?path=${path}&inline=1" target="_blank" rel="noopener" title="Open ${name}">${name}</a>`
                        : `<a href="/api/download?path=${path}" title="Download ${name}">${name}</a>`;
                const badge = f.conflict ? `<span class="badge warn" title="Changed on two PCs at the same time; both versions are kept">conflict</span>` : "";
                const on = many ? nodes.map((n) => {
                    const s = stateOn(f, n.id);
                    const what = { y: "has it", h: "receiving it now", w: "will get it", "": "not there" }[s];
                    return `<span class="ms-pc" title="${esc(n.name)}${n.self ? " (this PC)" : ""}: ${what}"><i class="ms-dot ${s}"></i>${esc(short(n.name))}</span>`;
                }).join("") : "";
                const dl = f.here
                    ? `<a class="quiet sq ms-dl" href="/api/download?path=${path}" download aria-label="Download ${name}" title="Download"><svg class="ic"><use href="#i-arrow-down" /></svg></a>`
                    : `<span class="ms-dl"></span>`;
                return `<div class="ms-row ms-frow${sel ? " sel" : ""}${f.here ? "" : " away"}">`
                    + `<input type="checkbox" data-sel="${esc(f.path)}" aria-label="Select ${name}"${sel ? " checked" : ""} />`
                    + `<span class="nm">${open}${badge}</span><span class="num">${size(f.size)}</span><span class="ms-on">${on}</span>${dl}</div>`;
            }).join("")
            : `<p class="muted" style="margin:0">${files.length ? "No file matches." : "No files yet. Drop some above."}</p>`);
        $("files-legend").hidden = !many || !files.length;
        renderSelection();
        const conflicts = files.filter((f) => f.conflict).length;
        const notes = [];
        if (matching.length > ShowAtMost) notes.push(`Showing the newest ${ShowAtMost} of ${matching.length.toLocaleString()} – search to narrow.`);
        else if (files.length) notes.push(`${files.length.toLocaleString()} file${files.length === 1 ? "" : "s"}, newest first.`);
        if (conflicts) notes.push(`${conflicts} conflict cop${conflicts === 1 ? "y" : "ies"} – search "conflict" to list them.`);
        MS.set("files-note", notes.join(" "));
    }
    $("q").addEventListener("input", renderFiles);

    function renderSelection() {
        const n = selected.size;
        MS.set("sel-count", n ? `${n} selected` : "Select all");
        $("sel-acts").hidden = n === 0;
        const all = $("sel-all");
        const shownSel = shown.filter((f) => selected.has(f.path)).length;
        all.checked = shown.length > 0 && shownSel === shown.length;
        all.indeterminate = shownSel > 0 && shownSel < shown.length;
    }
    $("files").addEventListener("change", (e) => {
        const box = e.target.closest("[data-sel]");
        if (!box) return;
        if (box.checked) selected.add(box.dataset.sel); else selected.delete(box.dataset.sel);
        renderFiles();
    });
    $("sel-all").addEventListener("change", () => {
        const on = $("sel-all").checked;
        for (const f of shown) if (on) selected.add(f.path); else selected.delete(f.path);
        renderFiles();
    });

    // ── Aktionen: ein Dialog für senden, löschen, umbenennen, verschieben ──
    let action = null;
    const dlg = $("act");
    function openDialog(kind) {
        const list = files.filter((f) => selected.has(f.path));
        if (!list.length) return;
        const count = list.length === 1 ? `"${esc(base(list[0].path))}"` : `${list.length} files`;
        const body = $("act-body");
        const ok = $("act-ok");
        $("act-err").textContent = "";
        ok.classList.remove("danger");
        body.onchange = null;
        if (kind === "send") {
            const targets = others();
            if (!targets.length) { toast("No other PC known yet", true); return; }
            const anyHere = list.some((f) => f.here);
            MS.set("act-title", "SEND TO");
            body.innerHTML = `<p>Send ${count} to</p><div class="ms-pcs">${targets.map((n) =>
                `<label class="ms-check"><input type="checkbox" name="pc" value="${esc(n.id)}" /><span>${esc(n.name)}</span></label>`).join("")}</div>`
                + (anyHere ? `<label class="ms-check"><input type="checkbox" name="remove-here" /><span>Remove from ${esc(self()?.name ?? "this PC")} once it arrived</span></label>` : "");
            ok.textContent = "Send";
        } else if (kind === "delete") {
            MS.set("act-title", "DELETE");
            // Vorgewählt: die PCs, auf denen eine der Dateien liegt oder ankommen soll.
            const related = (n) => list.some((f) => f.on.includes(n.id) || (f.to ? f.to.includes(n.id) : true));
            body.innerHTML = `<p>Delete ${count} on</p><div class="ms-pcs">${nodes.map((n) => {
                const has = list.some((f) => f.on.includes(n.id));
                return `<label class="ms-check"><input type="checkbox" name="pc" value="${esc(n.id)}"${related(n) ? " checked" : ""} /><span>${esc(n.name)}${n.self ? " (this PC)" : ""}${has ? "" : ` <span class="muted">(not there)</span>`}</span></label>`;
            }).join("")}</div><p class="muted">Deleted for good, there is no trash. The space is free right away.</p>`;
            ok.classList.add("danger");
            const label = () => {
                const on = [...body.querySelectorAll("input[name=pc]:checked")].map((i) => i.value);
                const all = nodes.map((n) => n.id);
                const everywhere = on.length > 0 && list.every((f) => (f.to ?? all).every((id) => on.includes(id)));
                ok.textContent = !on.length ? "Delete" : everywhere ? "Delete everywhere" : `Delete on ${on.length} PC${on.length === 1 ? "" : "s"}`;
            };
            body.onchange = label;
            label();
        } else if (kind === "rename") {
            if (list.length !== 1) { toast("Select one file to rename", true); return; }
            MS.set("act-title", "RENAME");
            body.innerHTML = `<label class="ms-label" for="act-name">New name</label><div class="search"><input id="act-name" name="name" value="${esc(base(list[0].path))}" /></div>`;
            ok.textContent = "Rename";
        } else if (kind === "move") {
            MS.set("act-title", "MOVE");
            const folders = [...new Set(files.map((f) => dir(f.path)).filter(Boolean))].sort();
            body.innerHTML = `<label class="ms-label" for="act-folder">Move ${count} into folder</label>`
                + `<div class="search"><input id="act-folder" name="folder" list="act-folders" placeholder="empty = the shared folder itself" value="${esc(dir(list[0].path))}" /></div>`
                + `<datalist id="act-folders">${folders.map((d) => `<option value="${esc(d)}"></option>`).join("")}</datalist>`
                + `<span class="muted">A new folder is made when needed.</span>`;
            ok.textContent = "Move";
        }
        action = { kind, list };
        dlg.hidden = false;
        const field = body.querySelector("input:not([type=checkbox])");
        if (field) { field.focus(); field.select(); }
        else body.querySelector("input")?.focus();
    }
    function closeDialog() { dlg.hidden = true; action = null; }

    async function confirmDialog() {
        if (!action) return;
        const body = $("act-body");
        const err = (text) => { $("act-err").textContent = text; };
        const paths = action.list.map((f) => f.path);
        const pcs = [...body.querySelectorAll("input[name=pc]:checked")].map((i) => i.value);
        let r;
        if (action.kind === "send") {
            const removeHere = body.querySelector("input[name=remove-here]")?.checked ?? false;
            if (!pcs.length) return err("Choose at least one PC.");
            r = await post("/api/files/send", { paths, to: pcs, removeHere });
        } else if (action.kind === "delete") {
            if (!pcs.length) return err("Choose at least one PC.");
            r = await post("/api/files/delete", { paths, on: pcs });
        } else if (action.kind === "rename") {
            const name = (body.querySelector("[name=name]").value || "").trim();
            if (!name || name.includes("/") || name.includes("\\")) return err("Enter a name without slashes.");
            const from = paths[0];
            const folder = dir(from);
            r = await post("/api/files/move", { moves: [{ from, to: folder ? `${folder}/${name}` : name }] });
        } else if (action.kind === "move") {
            const folder = (body.querySelector("[name=folder]").value || "").trim().replace(/\\/g, "/").replace(/^\/+|\/+$/g, "");
            r = await post("/api/files/move", { moves: paths.map((from) => ({ from, to: folder ? `${folder}/${base(from)}` : base(from) })) });
        }
        if (!r?.ok) return err(r?.message ?? "That did not work.");
        toast(r.message ?? "Done");
        selected.clear();
        closeDialog();
        MS.filesChanged();
        tick();
    }
    $("sel-acts").addEventListener("click", (e) => {
        const b = e.target.closest("[data-act]");
        if (b) openDialog(b.dataset.act);
    });
    $("act-cancel").addEventListener("click", closeDialog);
    $("act-ok").addEventListener("click", confirmDialog);
    $("act-body").addEventListener("submit", (e) => { e.preventDefault(); confirmDialog(); });
    dlg.addEventListener("click", (e) => { if (e.target === dlg) closeDialog(); });
    document.addEventListener("keydown", (e) => { if (e.key === "Escape" && !dlg.hidden) closeDialog(); });

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

    // ── An wen: alle PCs oder bestimmte (gemerkt in diesem Browser) ──
    let sendTo = [];
    try { sendTo = JSON.parse(localStorage.getItem("meshshare.sendTo") || "[]"); } catch { sendTo = []; }
    function renderChips() {
        const targets = others();
        if (nodes.length) sendTo = sendTo.filter((id) => targets.some((n) => n.id === id));
        html($("to-chips"), targets.length
            ? `<span class="muted">To</span><button type="button" class="ms-chip${sendTo.length ? "" : " on"}" data-to="" aria-pressed="${!sendTo.length}">Every PC</button>`
                + targets.map((n) => `<button type="button" class="ms-chip${sendTo.includes(n.id) ? " on" : ""}" data-to="${esc(n.id)}" aria-pressed="${sendTo.includes(n.id)}">${esc(n.name)}</button>`).join("")
            : "");
        $("remove-here-box").hidden = !sendTo.length;
        MS.set("remove-here-text", `Remove from ${self()?.name ?? "this PC"} once it arrived`);
        MS.set("to-hint", sendTo.length
            ? `Only ${sendTo.map(nameOf).join(" and ")} get${sendTo.length === 1 ? "s" : ""} them. The other PCs don't download them.`
            : "They go into the shared folder and reach every PC.");
    }
    $("to-chips").addEventListener("click", (e) => {
        const chip = e.target.closest("[data-to]");
        if (!chip) return;
        const id = chip.dataset.to;
        if (!id) sendTo = [];
        else sendTo = sendTo.includes(id) ? sendTo.filter((x) => x !== id) : [...sendTo, id];
        try { localStorage.setItem("meshshare.sendTo", JSON.stringify(sendTo)); } catch { }
        renderChips();
    });

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

    function upload(item, index, count, query) {
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
            xhr.open("POST", `/api/upload?path=${encodeURIComponent(item.path)}${query}`);
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
        // Das Ziel gilt für den ganzen Stapel, so wie es beim Ablegen gewählt war.
        const query = sendTo.length ? `&to=${encodeURIComponent(sendTo.join(","))}&keep=${$("remove-here").checked ? 0 : 1}` : "";
        const where = sendTo.length ? ` for ${sendTo.map(nameOf).join(" and ")}` : "";
        running++;
        let ok = 0;
        for (let i = 0; i < items.length; i++) if (await upload(items[i], i, items.length, query)) ok++;
        running--;
        toast(ok === items.length ? `${ok} file${ok === 1 ? "" : "s"} added${where}` : `${ok} of ${items.length} files added`, ok !== items.length);
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
})();
