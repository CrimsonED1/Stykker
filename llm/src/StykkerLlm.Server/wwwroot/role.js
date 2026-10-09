// Rolle dieses Geräts beim Server erfragen. Nötig, weil die Blazor-Schleife als Client läuft und den HttpContext
// des Browsers nicht mehr sieht – das Gerätecookie hat nur der Server. Der Aufrufer (ViewerSession) bleibt
// solange im Viewer-Modus, bis die Antwort da ist.
window.stykkerRole = async function () {
    const r = await fetch('/api/whoami', { credentials: 'same-origin', cache: 'no-store' });
    return await r.text();
};