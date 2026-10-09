# Offene Entscheidungen

## UI-Technik: Blazor/Photino oder Avalonia

**Stand: 2026-10-09 – offen, noch kein Umbau.**

Heute läuft die Oberfläche als Blazor Server, im Fenster gezeigt von Photino (WebView2). Das Schwesterprojekt
`StykkerCMD` nutzt Avalonia.

Core und Windows-Schicht enthalten keinen UI-Code und bleiben bei einem Wechsel unverändert. Betroffen wäre nur
die Oberfläche: `Hud.razor`, `hud.js`, `app.css` und der Fensterstart, zusammen rund 750 Zeilen.

Die Entscheidung hängt ab von:

- den Erfahrungen aus dem CMD-Repo mit Avalonia (Tray-Symbol, Rendering, Tests),
- ob die Resident-Funktion kommt (Autostart, nur im Tray). Das Tray-Symbol ist hier 302 Zeilen eigener
  Win32-Code (`src/StykkerHud.Platform.Windows/TrayIcon.cs`).

Bis zur Entscheidung wird die Oberfläche nicht umgebaut.
