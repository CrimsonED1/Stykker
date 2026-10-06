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

    // Im Fenster StykkerUI gibt es keine Browser-Knöpfe und -Tasten: Photino baut in jede Seite
    // window.external ein – daran ist die Hülle zu erkennen.
    const inShell = () => !!(window.external && typeof window.external.receiveMessage === 'function');
    if (inShell()) {
        root.classList.add('shell');
        window.addEventListener('keydown', function (e) {
            if (!e.altKey || e.ctrlKey || e.metaKey) return;
            if (e.key === 'ArrowLeft') window.history.back();
            else if (e.key === 'ArrowRight') window.history.forward();
        });
    }

    window.stykkerUi = {
        density: read,
        set: function (d) { try { localStorage.setItem(KEY, d); } catch (e) { /* egal: dann eben nur diese Sitzung */ } apply(); },
        shell: inShell,
        back: function () { window.history.back(); },
        forward: function () { window.history.forward(); }
    };
})();