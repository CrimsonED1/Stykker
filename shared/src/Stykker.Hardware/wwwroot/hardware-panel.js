// Meldet dem Hardware-Baustein, ob die Seite sichtbar ist. Ist der Tab oder das Fenster verborgen, fragt er nicht nach,
// und nach 15 Sekunden hält die Messung an (ViewerLoop).
export function watch(panel) {
    const report = () => panel.invokeMethodAsync("Visible", !document.hidden).catch(() => { });
    document.addEventListener("visibilitychange", report);
    return { stop: () => document.removeEventListener("visibilitychange", report) };
}

export function visible() {
    return !document.hidden;
}
