// Ansicht (kompakt/voll) und die Fenster-Hülle. Läuft vor Blazor und setzt die Ansicht am <html>-Element, damit
// die Seite nicht nach dem ersten Takt umspringt. Die Wahl gilt je Gerät (localStorage), nicht je Server: ein
// Telefon will kompakt, ein großer Bildschirm nicht.
(function () {
    const KEY = 'stykker.density';
    const TAB_KEY = 'stykker.proxyTab';
    const root = document.documentElement;
    const read = () => { try { return localStorage.getItem(KEY) || 'auto'; } catch (e) { return 'auto'; } };

    // Im Frame der Zentrale (?embed=1&theme=dark|titan): ohne eigene Marke, im Thema der Zentrale. Blazor ändert bei
    // jedem Seitenwechsel die Adresse, dann fehlt ?embed – darum merkt die Sitzung die Wahl. Nur im Frame: ein eigener
    // Tab auf dem Port zeigt weiter die volle Oberfläche.
    const EMBED_KEY = 'stykker.embed';
    const framed = window.self !== window.top;
    let embedTheme = null;   // 'dark' oder 'titan' im Frame, sonst null
    try {
        if (framed && new URLSearchParams(location.search).has('embed')) {
            sessionStorage.setItem(EMBED_KEY, new URLSearchParams(location.search).get('theme') === 'titan' ? 'titan' : 'dark');
        }
        if (framed) embedTheme = sessionStorage.getItem(EMBED_KEY);
    } catch (e) { /* ohne Sitzungsspeicher: im Frame bleibt es beim Thema des Servers */ }
    if (embedTheme) root.classList.add('embed');

    // auto = nach Fensterbreite, sonst die feste Wahl. Der Umschalter im ☰ überstimmt sie.
    function apply() {
        const d = read();
        const narrow = window.innerWidth < 780;
        root.dataset.view = d === 'small' || (d === 'auto' && narrow) ? 'small' : 'full';
        root.dataset.density = d;
    }
    apply();
    window.addEventListener('resize', apply);

    // Kurznamen unter den Kennzahlen der Serverkarten (Schalter „Labels“ im ☰ → Ansicht): je Browser gemerkt, aus
    // ist der Standard. Die Klasse am <html> setzt schon dieses Skript, damit die Karten nicht umspringen.
    const LABELS_KEY = 'stykker.cardLabels';
    const labelsRead = () => { try { return localStorage.getItem(LABELS_KEY) === 'on'; } catch (e) { return false; } };
    function applyLabels() { root.dataset.cardLabels = labelsRead() ? 'on' : 'off'; }
    applyLabels();

    // Thema: html[data-theme] = dark | titan. Die Wahl kommt vom Server (data-theme-choice beim ersten Bild, danach
    // <meta name="stykker-theme"> im Kopf, den Blazor bei einem Wechsel neu schreibt); „system“ folgt dem Hell/Dunkel
    // des Systems und wechselt mit, wenn es sich ändert.
    const light = window.matchMedia ? window.matchMedia('(prefers-color-scheme: light)') : null;
    function choice() {
        if (embedTheme) return embedTheme;   // eingebettet: das Thema der Zentrale, nicht die Wahl des Servers
        const m = document.querySelector('meta[name="stykker-theme"]');
        return (m && m.content) || root.dataset.themeChoice || 'system';
    }
    function theme() {
        const c = choice();
        const t = c === 'titan' || (c === 'system' && light && light.matches) ? 'titan' : 'dark';
        if (root.dataset.theme !== t) root.dataset.theme = t;
    }
    theme();
    if (light && light.addEventListener) light.addEventListener('change', theme);
    // nicht jede Umgebung meldet den Wechsel (WebView, Emulation): zusätzlich beim Zurückkehren und alle 3 s nachsehen
    document.addEventListener('visibilitychange', theme);
    setInterval(theme, 3000);
    new MutationObserver(theme).observe(document.head, { childList: true, subtree: true, attributes: true, attributeFilter: ['content'] });

    // Tab-Zustand (TabStatus im Server, <meta name="stykker-status">): ein Punkt im Favicon (grün = erzeugt Tokens, rot =
    // Server verloren) und bei einem Absturz oder einem fertigen Lauf (Tests, Benchmark) eine Browser-Benachrichtigung – nur wenn auf diesem Gerät eingeschaltet, nur
    // einmal je Ereignis (über alle Tabs) und nicht, wenn der Tab vorn ist (dort steht das Banner).
    const NOTIFY_KEY = 'stykker.notify', SEEN_KEY = 'stykker.alertSeen';
    const canNotify = () => 'Notification' in window && window.isSecureContext;
    function notifyState() {
        if (!canNotify()) return 'unsupported';
        let on = false;
        try { on = localStorage.getItem(NOTIFY_KEY) === 'on'; } catch (e) { /* ohne Speicher: aus */ }
        if (Notification.permission === 'denied') return on ? 'blocked' : 'off';
        return on && Notification.permission === 'granted' ? 'on' : 'off';
    }
    function show(title, body, tag) {
        const icon = document.querySelector('link[rel="icon"]');
        const n = new Notification(title, { body: body, tag: tag, icon: icon ? (icon.dataset.base || icon.href) : undefined });
        n.onclick = function () { window.focus(); n.close(); };
    }
    async function setNotify(on) {
        if (!canNotify()) return 'unsupported';
        if (on && Notification.permission === 'default') {
            try { await Notification.requestPermission(); } catch (e) { /* alte Browser: Rückruf statt Promise, dann eben ohne */ }
        }
        try { localStorage.setItem(NOTIFY_KEY, on ? 'on' : 'off'); } catch (e) { /* egal: dann eben nur diese Sitzung */ }
        if (!on) return 'off';
        if (Notification.permission === 'denied') return 'blocked';
        if (Notification.permission !== 'granted') return 'off';
        // Probe: Android-Chrome kennt Notification, wirft aber ohne Service Worker beim Anlegen – dann geht es hier nicht
        try { show('Stykker LLM', document.title, 'stykker-test'); } catch (e) {
            try { localStorage.setItem(NOTIFY_KEY, 'off'); } catch (e2) { /* egal */ }
            return 'unsupported';
        }
        return 'on';
    }
    let iconImg = null, iconLevel = '';
    function favicon(level) {
        if (level === iconLevel) return;
        iconLevel = level;
        const link = document.querySelector('link[rel="icon"]');
        if (!link) return;
        if (!link.dataset.base) link.dataset.base = link.href;
        if (level !== 'busy' && level !== 'alarm') { link.href = link.dataset.base; return; }
        const draw = function () {
            if (iconLevel !== 'busy' && iconLevel !== 'alarm') return;   // inzwischen wieder ruhig
            const c = document.createElement('canvas');
            c.width = c.height = 64;
            const g = c.getContext('2d');
            g.drawImage(iconImg, 0, 0, 64, 64);
            g.beginPath();
            g.arc(46, 46, 15, 0, 2 * Math.PI);
            g.fillStyle = iconLevel === 'alarm' ? '#ff5c7a' : '#6cffb0';
            g.fill();
            g.lineWidth = 5;
            g.strokeStyle = '#060916';
            g.stroke();
            try { link.href = c.toDataURL('image/png'); } catch (e) { /* Bild nicht lesbar: ohne Punkt */ }
        };
        if (iconImg && iconImg.complete && iconImg.naturalWidth > 0) { draw(); return; }
        iconImg = new Image();
        iconImg.onload = draw;
        iconImg.src = link.dataset.base;
    }
    function status() {
        const m = document.querySelector('meta[name="stykker-status"]');
        if (!m) return;
        favicon(m.content || 'idle');
        const id = m.dataset.alertId;
        if (!id) return;
        let seen = '';
        try { seen = localStorage.getItem(SEEN_KEY) || ''; } catch (e) { /* ohne Speicher: je Tab einmal */ }
        if (seen === id || m.dataset.shown === id) return;
        m.dataset.shown = id;
        try { localStorage.setItem(SEEN_KEY, id); } catch (e) { /* egal */ }
        // eine alte Meldung (Seite später geöffnet oder neu geladen) nur merken, nicht melden
        if (Date.now() - (Number(m.dataset.alertAt) || 0) > 120000) return;
        if (notifyState() !== 'on') return;
        if (document.visibilityState === 'visible' && document.hasFocus()) return;
        try { show(m.dataset.alertTitle || 'Stykker LLM', m.dataset.alertBody || '', id); } catch (e) { /* Browser lehnt ab: das Banner bleibt */ }
    }
    new MutationObserver(status).observe(document.head, { childList: true, subtree: true, attributes: true, attributeFilter: ['content', 'data-alert-id'] });

    // Gespräch auf /prompt (.chat): beim Streamen wächst die Antwort unten. Wer unten ist, bleibt unten; wer hochgescrollt hat,
    // um etwas nachzulesen, wird nicht weggerissen.
    const follow = new WeakMap();
    document.addEventListener('scroll', function (e) {
        const el = e.target;
        if (el && el.classList && el.classList.contains('chat')) follow.set(el, el.scrollTop + el.clientHeight >= el.scrollHeight - 48);
    }, true);
    new MutationObserver(function () {
        document.querySelectorAll('.chat').forEach(function (el) { if (follow.get(el) !== false) el.scrollTop = el.scrollHeight; });
    }).observe(document.documentElement, { childList: true, subtree: true, characterData: true });

    // Tastatur (U10): Ctrl+K öffnet das Befehlsfenster, „/“ springt in die Suche der History (sonst Befehlsfenster);
    // im offenen Befehlsfenster bleibt der Fokus im Eingabefeld (Tab springt nicht hinaus)
    let lastFocus = null;
    window.stykkerRestoreFocus = function () { if (lastFocus && document.contains(lastFocus)) lastFocus.focus(); lastFocus = null; };
    window.stykkerFocus = function (id) { const el = document.getElementById(id); if (el) { el.focus(); if (el.select) el.select(); } };
    document.addEventListener('keydown', function (e) {
        const t = e.target, typing = t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT' || t.isContentEditable);
        const open = () => { lastFocus = document.activeElement; const b = document.getElementById('cmdk-open'); if (b) b.click(); };
        if ((e.ctrlKey || e.metaKey) && (e.key === 'k' || e.key === 'K')) { e.preventDefault(); open(); return; }
        if (document.querySelector('.cmdk') && e.key === 'Tab') { e.preventDefault(); window.stykkerFocus('cmdk-input'); return; }
        if (!typing && e.key === '/' && !e.ctrlKey && !e.metaKey && !e.altKey) {
            e.preventDefault();
            if (document.getElementById('hsearch')) window.stykkerFocus('hsearch'); else open();
        }
    });

    // Im Fenster StykkerUI gibt es keine Browser-Knöpfe und -Tasten: Photino baut in jede Seite
    // window.external ein – daran ist die Hülle zu erkennen.
    const inShell = () => !!(window.external && typeof window.external.receiveMessage === 'function');
    if (inShell()) {
        root.classList.add('shell');
        // Die Hülle fragt beim Schließen über eine Nachricht; der Dialog liegt über der Seite, die Seite bleibt stehen.
        window.external.receiveMessage(function (raw) {
            let m;
            try { m = JSON.parse(raw); } catch (e) { return; }
            if (m && m.t === 'ask') askClose(m);
        });
        window.addEventListener('keydown', function (e) {
            if (!e.altKey || e.ctrlKey || e.metaKey) return;
            if (e.key === 'ArrowLeft') window.history.back();
            else if (e.key === 'ArrowRight') window.history.forward();
        });
    }

    // Dialog „Beim Schließen?“: Abbrechen, Beenden, in den Tray; „nicht mehr fragen“ hängt ein ! an die Antwort
    function askClose(m) {
        const tell = (msg) => { try { window.external.sendMessage(msg); } catch (e) { /* Fenster schon weg */ } };
        tell('shell.ack');
        if (document.getElementById('ask-close')) return;
        const back = document.createElement('div');
        back.id = 'ask-close';
        back.className = 'modal-back';
        const box = document.createElement('div');
        box.className = 'modal';
        const h = document.createElement('h2');
        h.innerHTML = '<span class="logo">◆</span> STYKKER <span class="acc">LLM</span>';
        const p = document.createElement('p');
        p.textContent = m.text;
        // Hinweis zum Server und Warnung, wenn eine Anfrage über den Proxy abbräche
        const extra = [];
        if (m.note) { const n = document.createElement('p'); n.className = 'muted'; n.textContent = m.note; extra.push(n); }
        if (m.warning) { const w = document.createElement('p'); w.className = 'warn'; w.textContent = m.warning; extra.push(w); }
        const lab = document.createElement('label');
        lab.className = 'check';
        const rem = document.createElement('input');
        rem.type = 'checkbox';
        lab.append(rem, ' ' + m.remember);
        const row = document.createElement('div');
        row.className = 'row end';
        const close = (msg, keep) => { back.remove(); document.removeEventListener('keydown', esc); tell(keep && rem.checked ? msg + '!' : msg); };
        const btn = (text, cls, msg, keep) => {
            const b = document.createElement('button');
            b.textContent = text;
            if (cls) b.className = cls;
            b.onclick = () => close(msg, keep);
            return b;
        };
        const tray = btn(m.tray, 'primary', 'shell.tray', true);
        row.append(btn(m.cancel, 'ghost', 'shell.cancel', false), btn(m.quit, 'bad', 'shell.quit', true), tray);
        box.append(h, p, ...extra, lab, row);
        back.append(box);
        back.addEventListener('click', (e) => { if (e.target === back) close('shell.cancel', false); });
        function esc(e) { if (e.key === 'Escape') close('shell.cancel', false); }
        document.addEventListener('keydown', esc);
        document.body.append(back);
        tray.focus();
    }

    window.stykkerUi = {
        density: read,
        set: function (d) { try { localStorage.setItem(KEY, d); } catch (e) { /* egal: dann eben nur diese Sitzung */ } apply(); },
        shell: inShell,
        // zuletzt gewählter Reiter des Proxy-Dialogs (je Browser); ohne Speicher bleibt es bei dem Standard des Dialogs
        cardLabels: labelsRead,
        setCardLabels: function (on) { try { localStorage.setItem(LABELS_KEY, on ? 'on' : 'off'); } catch (e) { /* egal: dann eben nur diese Sitzung */ } applyLabels(); },
        proxyTab: function () { try { return localStorage.getItem(TAB_KEY) || ''; } catch (e) { return ''; } },
        setProxyTab: function (t) { try { localStorage.setItem(TAB_KEY, t); } catch (e) { /* egal: dann eben nur diese Sitzung */ } },
        notify: notifyState,    // Absturz-Meldung: on | off | blocked | unsupported
        setNotify: setNotify,
        askClose: askClose,     // auch zum Ansehen ohne Hülle (Bilder, Prüfung)
        back: function () { window.history.back(); },
        forward: function () { window.history.forward(); }
    };
})();