# StykkerSYS

Die Prozessliste dieser Maschine: jeder Prozess mit seinem CPU-Anteil, Speicher, GPU-Anteil und Grafikspeicher, und drei Dinge, die man mit einem Prozess tun kann: Dateiort öffnen, Priorität ändern, beenden. Teil der Stykker-Suite. Entstanden ist es aus [StykkerHUD](../hud/README.de.md), das den Systemmonitor behält. Die Oberfläche nutzt dasselbe gemeinsame Design-System wie StykkerHUD.

## Was sie zeigt

- **Eine Zeile je Programm.** Prozesse mit gleichem Namen **und** gleicher Datei werden zu einer Zeile `×N` zusammengefasst, mit den summierten Werten für CPU, Speicher, GPU, Grafikspeicher und Threads. Der Winkel links klappt die einzelnen Prozesse auf, jeder mit eigener PID und eigenem Menü. Zwei Programme mit gleichem Namen, aber verschiedenen Dateien, bleiben getrennt.
- **Spalten:** Name, PID, CPU-Anteil (Balken und Wert, bezogen auf die ganze Maschine), Speicher (Arbeitssatz), GPU-Anteil und Grafikspeicher (Windows-GPU-Zähler), Threads, ein Zustandszeichen und die CPU-Zeit. Das Zeichen zeigt *idle* unter 2 % CPU, *active* ab 2 % und *busy* ab 25 %.
- **Höchstens 40 Zeilen** auf einmal. Die Überschrift nennt die Zahl der gezeigten Gruppen und die Zahl der Prozesse in der Liste.
- **Sortieren** nach CPU, Speicher, GPU oder Name. **Filtern** nach Name, Pfad oder PID. Eine Suche klappt die passenden Gruppen einmal auf, damit man die Prozesse hinter einer Summe sieht; danach lassen sie sich wieder zuklappen.
- **Priorität:** Bei einem einzelnen Prozess ist die aktuelle Stufe im Menü ausgegraut, und ein Kennzeichen neben dem Namen zeigt sie, wenn sie nicht Normal ist.

## Aktionen

Jede Zeile hat ein Menü (`⋯`):

- **Open file location** öffnet den Explorer mit markierter Datei.
- **Priorität:** Idle, Below normal, Normal, Above normal, High. **Echtzeit (RealTime) wird nicht angeboten**, weil ein Prozess in dieser Klasse die Maschine blockieren kann.
- **End task** fragt zuerst nach. Eine Leiste über der Liste nennt den Prozess und was er belegt, zum Beispiel `End chrome (PID 8)? 357 MB working set – unsaved work in it is lost.` Nur der Knopf in dieser Leiste handelt. Das Ergebnis erscheint als Hinweis.

Auf einer Gruppenzeile gilt das Menü für **alle** Mitglieder. *End all 10 tasks* fragt einmal nach und meldet dann `10 of 10 done`, oder `N of M done` mit der ersten Ablehnung.

## Nur lokal, nur diese Seite

- **Nur diese Maschine.** Der Server hört auf `127.0.0.1`.
- **Nur diese Seite.** Eine Anfrage muss die eigene `Origin` der Seite tragen (oder `Sec-Fetch-Site: same-origin`). Alles andere wird mit `403` abgewiesen.
- **Nur JSON.** Ohne `Content-Type: application/json` antworten die Aktionen mit `415`, damit ein Formular auf einer anderen Website sie nicht erreicht.
- **Nie angefasst:** PID 4 und darunter (System und Ähnliches) sowie StykkerSYS selbst. Der Server lehnt mit `409` ab und sagt, warum.
- **Zugriff verweigert:** Windows lehnt die Aktion ab; die Meldung rät, StykkerSYS für diesen Prozess als Administrator zu starten.

Aus der Oberfläche endet kein ganzer Prozessbaum. Die API kennt `tree: true`, aber kein Knopf ruft es auf.

## Was die Liste nicht zeigt

Windows gibt einem gewöhnlichen Programm nicht für jeden Prozess die CPU-Zeit heraus. Diese Prozesse fehlen **in der Liste**: `System` (PID 4), `Registry`, `Memory Compression`, `lsass`, die Antivirus-Engine und viele Dienste. Bei einer Messung am 2026-10-09 fehlten 215 von 346 laufenden Prozessen.

Ihre Last zählt in der Prozessorsumme von StykkerHUD mit. Deshalb können die Zeilen dieser Liste **weniger als diese Summe** ergeben. Bei derselben Messung ergaben die gezeigten Zeilen etwa die Hälfte der Prozessorsumme, der Rest war vor allem Kernel-Zeit. Die Liste nennt oben in einer Hinweiszeile, wie viele Prozesse sie auslässt, zum Beispiel `Not listed: 114 of 233 processes.`

GPU-Werte je Prozess kommen aus den Windows-GPU-Zählern. Antworten sie nicht, bleiben die Spalten GPU und Grafikspeicher bei `–`, und die Liste sagt es in einer Hinweiszeile.

## Aufbau

| Pfad | Was es ist |
|---|---|
| `src/StykkerSys.Core/` | Das Modell (`Samples.cs`), die Aktionen und ihre Ablehnungen (`ProcessActions.cs`), die Messung (`ProcessSampler`: CPU-Anteil aus Zeitdifferenzen, Speicher alle drei Takte, GPU je Prozess angehängt) und `SysService` (die Messschleife). Kennt keine Windows-API. |
| `src/StykkerSys.Platform.Windows/` | `WindowsProcessProbe`: GPU je Prozess über die gemeinsamen Zähler, Dateipfade über `OpenProcess` und `QueryFullProcessImageNameW`. `Shell.cs` öffnet den Explorer mit markierter Datei. |
| `src/StykkerSys.Server/` | Blazor Server auf `http://127.0.0.1:8077`: die Seite (`Processes.razor`, `sys.js`), `/api/snapshot`, die drei Aktionswege unter `/api/process/` und das Tray-Symbol. |
| `src/StykkerSys.UI/` | `StykkerSYS.exe`, das Photino-Fenster um diese Seiten. Startet den Server, falls keiner läuft. |
| `tests/StykkerSys.Tests/` | xUnit-Tests: Zustandsgrenzen, Ablehnungen, Beenden und Priorität an einem Opfer-`ping`, die Messung mit einer Attrappe, die Windows-Sonde. |
| `tools/check-actions.mjs` | Ende-zu-Ende-Prüfung der Aktionen an einem Opferprozess: ändert seine Priorität, bedient das Zeilenmenü, prüft dass Nachfragen ihn schützt, Abbrechen nichts ändert und Bestätigen ihn beendet, dazu die Ablehnungen (fremde Herkunft, Cross-Site, kein JSON, System-PID). |
| `tools/check-groups.mjs` | Gruppierung: zwei Prozesse mit gleichem Namen und Pfad werden eine `×2`-Zeile; eine Suche klappt sie auf; der Winkel klappt sie wieder zu; die zugeklappte Zeile zeigt die Summen; *End all* fragt zuerst, Abbrechen beendet nichts, Bestätigen beendet beide. |
| `tools/shot.mjs` | Misst die Seite und schreibt beim Lauf Bilder nach `docs/screenshots/`. |

## Bauen und starten

```powershell
dotnet build StykkerSys.slnx -c Debug

# Fenster (startet den Server selbst, falls keiner läuft)
src\StykkerSys.UI\bin\Debug\net10.0\StykkerSYS.exe

# nur der Server, im Browser
src\StykkerSys.Server\bin\Debug\net10.0\StykkerSYS-Server.exe --port 8077
```

| Schalter | Wirkung |
|---|---|
| `--port <Zahl>` | Web-Port (Standard 8077) |
| `--no-tray` / `--no-browser` | kein Tray-Symbol / kein Browser beim Start |
| `--basic` | keine GPU-Werte und keine Dateipfade; CPU und Speicher werden weiter gezeigt |
| `--design-system <Ordner>` | woher das Design-System gelesen wird (Standard: `shared/design-system` der Monorepo) |
| `--server <Pfad>` (Fenster) | der Server, der gestartet wird, wenn keiner läuft |
| `--gpu` (Fenster) | zeichnet über die Grafikkarte (ohne den Schalter zeichnet das Fenster in Software) |

Umgebungsvariable für das Design-System: `STYKKERSYS_DESIGN_SYSTEM`.

## Prüfen

```powershell
# Komponententests (21)
dotnet test tests\StykkerSys.Tests\StykkerSys.Tests.csproj

# mit laufendem Server auf Port 8077 (braucht Google Chrome und einmal `npm install` in tools/)
node tools\check-actions.mjs --port 8077   # 16 Prüfungen
node tools\check-groups.mjs --port 8077    # 16 Prüfungen
node tools\shot.mjs --port 8077 --out docs\screenshots --name sys
```

Letzter Lauf am 2026-10-09: 21/21 Komponententests, 16/16 Aktionsprüfungen, 16/16 Gruppenprüfungen.

## Noch nicht gebaut

- **Zugehörigkeit (Affinität)**: welche Prozessorkerne ein Prozess nutzen darf.
- **Beenden eines ganzen Prozessbaums** aus der Oberfläche. Die API kennt `tree: true`; noch bietet kein Knopf es an.
- **Dauerbetrieb**: mit Windows starten und nur im Tray leben.
- **Offene Entscheidung**: Blazor/Photino oder Avalonia für die Oberfläche. Siehe [die Entscheidungsnotiz von StykkerHUD](../hud/docs/entscheidungen.md).
