// Übertragung: die laufende Datei mit Fortschritt, Rate und Restzeit, ein Gesamtbalken, die Warteschlange und was
// angehalten oder übersprungen ist. Die Reihenfolge ändert man durch Ziehen, mit den Pfeilen (Tastatur, Handy) oder
// mit „Load now“ (die laufende Datei pausiert und lädt später weiter). Der Fortschritt steht auch im Fenstertitel.
(() => {
    "use strict";
    const { $, esc, post, size, left, set, html, toast, tick } = MS;
    const baseTitle = document.title;

    let dragging = null;
    let waitingOrder = [];   // ohne die laufende Datei
    let currentPath = null;
    let refocus = null;      // nach dem Umsortieren: denselben Knopf derselben Datei wieder fokussieren
    const rate = (bps) => (bps > 0 ? `${size(bps)}/s` : "–");
    const icon = (id) => `<svg class="ic"><use href="#${id}" /></svg>`;

    MS.onStatus((st) => {
        const n = st.node;
        const t = n.transfer;
        const queue = st.queue || [];
        const paused = st.paused || [];
        const skipped = st.skipped || [];
        const waiting = queue.filter((q) => !t || q.path !== t.path);

        // Pause

        // Gesamt: alles, was in dieser Runde noch kommt
        const totalSize = queue.reduce((sum, q) => sum + q.size, 0);
        const totalDone = queue.reduce((sum, q) => sum + (t && q.path === t.path ? t.done : q.done), 0);
        const rest = Math.max(0, totalSize - totalDone);
        const pct = totalSize > 0 ? Math.min(100, (totalDone / totalSize) * 100) : 0;
        $("total").hidden = queue.length < 2;
        $("total").querySelector(".bar i").style.width = `${pct.toFixed(1)}%`;
        set("v-total", `${queue.length} files · ${size(totalDone)} of ${size(totalSize)}`);
        set("v-rate", t && !t.checking ? `${rate(t.rate)} · ${size(rest)} to go${t.rate > 0 ? " · " + left(rest / t.rate) : ""}` : (n.transfersPaused && queue.length ? "Sync paused" : ""));
        document.title = t && totalSize > 0 ? `${pct.toFixed(0)} % · ${baseTitle}` : baseTitle;

        // Die laufende Datei
        const cur = $("current");
        if (t) {
            const p = t.size > 0 ? Math.min(100, (t.done / t.size) * 100) : 100;
            const info = t.checking
                ? [`checking the ${size(t.done)} already here before it continues`]
                : [`from ${esc(t.from)}`, left((t.size - t.done) / t.rate)];
            if (t.resumed > 0 && !t.checking) info.push(`continued at ${size(t.resumed)}`);
            cur.innerHTML = `<div class="ms-cur-top"><b title="${esc(t.path)}">${esc(t.path)}</b><span class="num">${size(t.done)} of ${size(t.size)} · ${p.toFixed(0)} %</span></div>
                <span class="bar"><i style="width:${p.toFixed(1)}%"></i></span>
                <span class="ms-cur-row"><span class="muted ms-cur-info">${info.filter(Boolean).join(" · ")}</span>
                <button type="button" class="quiet" data-skip="${esc(t.path)}" title="Do not load this file until you allow it again">Don't load</button></span>`;
        } else {
            cur.innerHTML = queue.length || paused.length || skipped.length
                ? ""
                : `<p class="muted" style="margin:0">Everything is here. New files load by themselves.</p>`;
        }

        if (dragging) return;
        currentPath = t ? t.path : null;
        waitingOrder = waiting.map((q) => q.path);
        const row = (q, i, count) => {
            const p = q.size > 0 ? Math.round((q.done / q.size) * 100) : 0;
            const state = q.done > 0 ? `${size(q.size)} · paused at ${p} %` : size(q.size);
            const path = esc(q.path);
            return `<div class="ms-q${q.done > 0 ? " paused" : ""}" role="listitem" draggable="true" data-path="${path}">
                <span class="grip" aria-hidden="true"></span>
                <span class="p" title="${path}">${path}${q.from ? ` <span class="muted">· ${esc(q.from)}</span>` : ""}</span>
                <span class="num">${state}</span>
                <span class="acts">
                    <button type="button" class="quiet sq" data-move="-1" data-path="${path}" ${i === 0 ? "disabled" : ""} aria-label="Move ${path} up" title="Move up">${icon("i-arrow-up")}</button>
                    <button type="button" class="quiet sq" data-move="1" data-path="${path}" ${i === count - 1 ? "disabled" : ""} aria-label="Move ${path} down" title="Move down">${icon("i-arrow-down")}</button>
                    <button type="button" class="quiet sq" data-now="${path}" aria-label="Load ${path} now" title="Load now; a running file pauses and continues later">${icon("i-play")}</button>
                    <button type="button" class="quiet sq" data-skip="${path}" aria-label="Don't load ${path}" title="Don't load this file">${icon("i-x")}</button>
                </span></div>`;
        };
        const other = (q, kind) => `<div class="ms-q other" role="listitem"><span class="grip"></span>
            <span class="p" title="${esc(q.path ?? q)}">${esc(q.path ?? q)}</span>
            <span class="num">${kind === "skip" ? "not loaded" : `${size(q.size)} · ${q.size ? Math.round((q.done / q.size) * 100) : 0} % here`}</span>
            <span class="acts">${kind === "skip" ? `<button type="button" class="quiet" data-unskip="${esc(q)}">Load</button>` : ""}</span></div>`;
        html($("queue"), (waiting.length ? `<div class="muted ms-q-head">Next (${waiting.length}) · drag or use the arrows to change the order</div>` : "")
            + waiting.map((q, i) => row(q, i, waiting.length)).join("")
            + (paused.length ? `<div class="muted ms-q-head">${n.transfersPaused ? "Started; continues when the sync is resumed" : "Started; continues when the PC that has it is online"}</div>` + paused.map((q) => other(q, "paused")).join("") : "")
            + (skipped.length ? `<div class="muted ms-q-head">Not loaded</div>` + skipped.map((q) => other(q, "skip")).join("") : ""));
        if (refocus) {
            const target = $("queue").querySelector(`button[data-path="${CSS.escape(refocus.path)}"][data-move="${refocus.move}"]`);
            if (target && !target.disabled) target.focus();
            else $("queue").querySelector(`button[data-now="${CSS.escape(refocus.path)}"]`)?.focus();
            refocus = null;
        }
    });

    // Ganz oben heißt „jetzt laden“ (vor der laufenden Datei, die dann pausiert). Sonst nach ihr.
    async function saveOrder(waiting, now, announce) {
        const order = currentPath
            ? (now ? [waiting[0], currentPath, ...waiting.slice(1)] : [currentPath, ...waiting])
            : waiting;
        waitingOrder = waiting;
        const r = await post("/api/queue/order", { paths: order });
        if (!r.ok) toast(r.message ?? "Could not change the order", true);
        else if (announce) set("queue-live", announce);
        tick();
    }

    const box = $("transfers");
    box.addEventListener("click", async (e) => {
        const move = e.target.closest("[data-move]");
        const now = e.target.closest("[data-now]");
        const skip = e.target.closest("[data-skip]");
        const unskip = e.target.closest("[data-unskip]");
        if (move) {
            const path = move.dataset.path;
            const at = waitingOrder.indexOf(path);
            const to = at + Number(move.dataset.move);
            if (at < 0 || to < 0 || to >= waitingOrder.length) return;
            const order = waitingOrder.filter((p) => p !== path);
            order.splice(to, 0, path);
            refocus = { path, move: move.dataset.move };
            saveOrder(order, false, `${path}: position ${to + 1} of ${order.length}`);
        } else if (now) {
            const path = now.dataset.now;
            saveOrder([path, ...waitingOrder.filter((p) => p !== path)], true, `${path} loads now`);
        } else if (skip || unskip) {
            const path = (skip ?? unskip).dataset.skip ?? unskip.dataset.unskip;
            const r = await post("/api/queue/skip", { path, skip: !!skip });
            toast(r.message ?? "", !r.ok);
            tick();
        }
    });

    // Ziehen und Ablegen (Maus). Während gezogen wird, zeichnet die Abfrage die Liste nicht neu.
    const queueBox = $("queue");
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
        const rect = row.getBoundingClientRect();
        return { row, after: e.clientY > rect.top + rect.height / 2 };
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
        saveOrder(order, at === 0, `${moved}: position ${at + 1} of ${order.length}`);
    });
    queueBox.addEventListener("dragend", () => {
        dragging = null;
        clearMarks();
        for (const el of queueBox.querySelectorAll(".dragging")) el.classList.remove("dragging");
        delete queueBox.dataset.html;   // beim nächsten Mal neu zeichnen
    });
})();
