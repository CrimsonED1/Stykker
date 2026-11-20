// StykkerMeshShare — gemeinsame Hilfen und der Takt der Seite. Der Abgleich läuft im Server; die Seite liest nur,
// zeichnet und schickt Aktionen. /api/status kommt alle 1,5 s, aber nur solange die Seite sichtbar ist, und nie zwei
// Abfragen gleichzeitig. Die anderen Skripte (status.js, transfers.js, files.js) melden sich mit MS.onStatus an.
window.MS = (() => {
    "use strict";

    const $ = (id) => document.getElementById(id);
    const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
    const post = (url, body) => fetch(url, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body ?? {}),
    }).then((r) => r.json().catch(() => ({ ok: r.ok }))).catch(() => ({ ok: false, message: "The server does not answer" }));

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
    function left(seconds) {
        if (!Number.isFinite(seconds) || seconds <= 0) return "";
        if (seconds < 60) return `${Math.ceil(seconds)} s left`;
        if (seconds < 3600) return `${Math.ceil(seconds / 60)} min left`;
        return `${(seconds / 3600).toFixed(1)} h left`;
    }
    function set(id, text) { const el = $(id); if (el && el.textContent !== text) el.textContent = text; }
    // Nur neu zeichnen, wenn sich etwas geändert hat: sonst verliert ein Knopf alle 1,5 s den Fokus.
    function html(el, markup) { if (el.dataset.html !== markup) { el.dataset.html = markup; el.innerHTML = markup; } }

    function toast(text, warn) {
        const box = $("toasts");
        const el = document.createElement("div");
        el.className = "toast" + (warn ? " warn" : "");
        el.textContent = text;
        box.appendChild(el);
        setTimeout(() => { el.classList.add("out"); setTimeout(() => el.remove(), 400); }, 3500);
    }

    const listeners = [];
    let busy = false;
    let lastStatus = null;
    async function tick() {
        if (busy) return;
        busy = true;
        try {
            const st = await fetch("/api/status").then((r) => r.json());
            lastStatus = st;
            for (const fn of listeners) {
                try { fn(st); } catch (e) { console.error(e); }
            }
        } catch {
            for (const fn of offline) fn();
        } finally { busy = false; }
    }
    const offline = [];

    let timer = null;
    function start() {
        if (timer) return;
        tick();
        timer = setInterval(tick, 1500);
    }
    function stop() { clearInterval(timer); timer = null; }
    document.addEventListener("visibilitychange", () => (document.hidden ? stop() : start()));

    // ── Themenwahl ──
    function themeButtons() {
        for (const button of document.querySelectorAll(".seg button[data-theme-choice]")) {
            const mark = () => {
                const on = document.documentElement.dataset.theme === button.dataset.themeChoice;
                button.classList.toggle("on", on);
                button.setAttribute("aria-pressed", String(on));
            };
            button.addEventListener("click", () => {
                const theme = button.dataset.themeChoice;
                document.documentElement.dataset.theme = theme;
                try { localStorage.setItem("stykkermeshshare.theme", theme); } catch { }
                for (const other of document.querySelectorAll(".seg button[data-theme-choice]")) other.dispatchEvent(new Event("theme"));
                themeColor();
            });
            button.addEventListener("theme", mark);
            mark();
        }
        themeColor();
    }
    // Die Farbe der Statusleiste am Handy folgt dem Thema.
    function themeColor() {
        const meta = document.querySelector('meta[name="theme-color"]');
        if (meta) meta.content = getComputedStyle(document.body).backgroundColor || meta.content;
    }

    // ── Eigenes Fenster: Titelleiste wie beim Starter ──
    // Knöpfe und Ziehen schicken einfache Nachrichten an das Photino-Fenster (siehe StykkerMeshShare.UI).
    function windowBar() {
        if (!document.documentElement.classList.contains("window")) return;
        const send = (msg) => { try { window.external.sendMessage(msg); } catch { } };
        let maximized = false;
        const show = (max) => {
            maximized = max;
            const b = $("t-max");
            b.textContent = max ? "❐" : "□";
            b.title = max ? "Restore" : "Maximize";
            b.setAttribute("aria-label", b.title);
        };
        const toggle = () => send(maximized ? "window:restore" : "window:maximize");
        $("t-min").addEventListener("click", () => send("window:minimize"));
        $("t-max").addEventListener("click", toggle);
        $("t-close").addEventListener("click", () => send("window:close"));
        const chip = $("t-theme");
        const label = () => { chip.textContent = document.documentElement.dataset.theme === "titan" ? "Dark" : "Titan"; };
        chip.addEventListener("click", () => {
            const next = document.documentElement.dataset.theme === "titan" ? "dark" : "titan";
            document.querySelector(`.seg button[data-theme-choice="${next}"]`)?.click();
            label();
        });
        label();
        // Die Frage beim Schließen (das Fenster schickt window:ask-close, wenn nichts gemerkt ist).
        const ask = $("ask-close");
        const answer = (choice) => {
            ask.hidden = true;
            send(`window:close-${choice}${$("ask-close-remember").checked ? " remember" : ""}`);
        };
        $("ask-close-cancel").addEventListener("click", () => { ask.hidden = true; });
        $("ask-close-quit").addEventListener("click", () => answer("quit"));
        $("ask-close-tray").addEventListener("click", () => answer("tray"));
        ask.addEventListener("keydown", (e) => { if (e.key === "Escape") ask.hidden = true; });
        try {
            window.external.receiveMessage((raw) => {
                if (typeof raw !== "string") return;
                if (raw.startsWith("window:state ")) show(raw.includes("maximized"));
                else if (raw === "window:ask-close") {
                    $("ask-close-remember").checked = false;
                    ask.hidden = false;
                    $("ask-close-tray").focus();
                }
            });
        } catch { }
        // Ziehen an der Leiste bewegt das Fenster; Knöpfe, Felder und Links nicht.
        const bar = document.querySelector(".top");
        const noDrag = "button, input, select, label, a";
        let drag = false, x = 0, y = 0;
        bar.addEventListener("mousedown", (e) => {
            if (e.button !== 0 || e.target.closest(noDrag)) return;
            drag = true; x = e.screenX; y = e.screenY;
            e.preventDefault();
        });
        document.addEventListener("mousemove", (e) => {
            if (!drag) return;
            const dx = e.screenX - x, dy = e.screenY - y;
            x = e.screenX; y = e.screenY;
            if (dx || dy) send(`window:move ${dx},${dy}`);
        });
        document.addEventListener("mouseup", () => { drag = false; });
        bar.addEventListener("dblclick", (e) => { if (!e.target.closest(noDrag)) toggle(); });
        send("window:ready");
    }

    document.addEventListener("DOMContentLoaded", () => { themeButtons(); windowBar(); start(); });

    return {
        $, esc, post, size, ago, left, set, html, toast, tick,
        onStatus: (fn) => listeners.push(fn),
        onOffline: (fn) => offline.push(fn),
        get status() { return lastStatus; },
    };
})();
