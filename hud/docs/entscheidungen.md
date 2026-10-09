# Offene Entscheidungen

## UI-Technik: Blazor/Photino oder Avalonia

**Stand: 2026-10-09 – offen, noch kein Umbau.**

Heute läuft die Oberfläche als Blazor Server, im Fenster gezeigt von Photino (WebView2). Das Schwesterprojekt
`StykkerCMD` nutzt Avalonia.

Core und Windows-Schicht enthalten keinen UI-Code und bleiben bei einem Wechsel unverändert. Betroffen wäre nur
die Oberfläche: bei StykkerHUD `Hud.razor`, `hud.js`, `app.css` und der Fensterstart, zusammen rund 520 Zeilen;
bei StykkerSYS (`sys/`) `Processes.razor`, `sys.js`, `app.css` und der Fensterstart, zusammen rund 640 Zeilen.
Die Entscheidung betrifft also beide Werkzeuge.

Die Entscheidung hängt ab von:

- den Erfahrungen aus dem CMD-Repo mit Avalonia (Tray-Symbol, Rendering, Tests),
- ob die Resident-Funktion kommt (Autostart, nur im Tray). Das Tray-Symbol sind 302 Zeilen eigener
  Win32-Code (`shared/src/Stykker.Shared/Windows/TrayIcon.cs`, seit der Aufteilung von beiden Werkzeugen genutzt).

Bis zur Entscheidung wird die Oberfläche nicht umgebaut.
