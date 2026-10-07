// Ansicht (kompakt/voll) und die Fenster-Hülle. Läuft vor Blazor und setzt die Ansicht am <html>-Element, damit
// die Seite nicht nach dem ersten Takt umspringt. Die Wahl gilt je Gerät (localStorage), nicht je Server: ein
// Telefon will kompakt, ein großer Bildschirm nicht.
(function () {
    const KEY = 'stykker.density';
    const root = document.documentElement;
    const read = () => { try { return localStorage.getItem(KEY) || 'auto'; } catch (e) { return 'auto'; } };

    // auto = nach Fensterbreite, sonst die feste Wahl. Der Umschalter im ☰ überstimmt sie.
    function apply() {
        const d = read();
        const narrow = window.innerWidth < 780;
        root.dataset.view = d === 'small' || (d === 'auto' && narrow) ? 'small' : 'full';
        root.dataset.density = d;
    }
    apply();
    window.addEventListener('resize', apply);

    // Thema: html[data-theme] = dark | titan. Die Wahl kommt vom Server (data-theme-choice beim ersten Bild, danach
    // <meta name="stykker-theme"> im Kopf, den Blazor bei einem Wechsel neu schreibt); „system“ folgt dem Hell/Dunkel
    // des Systems und wechselt mit, wenn es sich ändert.
    const light = window.matchMedia ? window.matchMedia('(prefers-color-scheme: light)') : null;
    function choice() {
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

    // Tastatur (U10): Ctrl+K öffnet das Befehlsfenster, „/“ springt in die Suche der History (sonst Befehlsfenster);
    // im offenen Befehlsfenster bleibt der Fokus im Eingabefeld (Tab springt nicht hinaus)
    window.stykkerFocus = function (id) { const el = document.getElementById(id); if (el) { el.focus(); if (el.select) el.select(); } };
    document.addEventListener('keydown', function (e) {
        const t = e.target, typing = t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT' || t.isContentEditable);
        const open = () => { const b = document.getElementById('cmdk-open'); if (b) b.click(); };
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
        box.append(h, p, lab, row);
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
        askClose: askClose,     // auch zum Ansehen ohne Hülle (Bilder, Prüfung)
        back: function () { window.history.back(); },
        forward: function () { window.history.forward(); }
    };
})();