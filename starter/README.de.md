# Stykker Starter

Betreibt Programme aus einem Quellordner als lokale Kopien und hält sie aktuell. Man trägt den Pfad einer `.exe` ein. Der Starter kopiert **den ganzen Ordner** dieser Datei nach `%LOCALAPPDATA%\Stykker\Starter\<id>\<version>\` und startet die Kopie. Die Quelle kann danach ersetzt oder neu gebaut werden, während die Kopie läuft. Der Starter bleibt im Tray, prüft die Quelle in einem Abstand und bietet ein Update an, wenn die Quelle neuer ist. Teil der Stykker-Suite. Er nutzt das Design-System aus [`shared/design-system`](../shared/design-system/); es gibt keinen Server und keinen Port.

## Was er tut

- **Programm hinzufügen:** die `.exe` wählen. Der Starter liest ihren Ordner, zeigt die Dateiversion, kopiert den Ordner in einen neuen Versionsordner, prüft jede Datei gegen die Quelle (Größe und SHA-256) und startet die Kopie.
- **Version:** die Dateiversion der `.exe` (`FileVersion`, sonst `ProductVersion`). Ist sie gleich und der Ordnerinhalt anders, kommt die neue Kopie nach `<version>+<8 Zeichen eines Hashes>`. Ohne Dateiversion benennt der Hash der Ordnerliste die Version.
- **Update:** die neue Version wird zuerst **daneben** kopiert. Erst dann beendet der Starter die Prozesse, die aus dem alten Ordner laufen: erst höflich, nach 10 s endgültig. Danach startet er die neue Version. Startet die neue nicht (der Start scheitert, oder sie endet innerhalb von 5 s mit einem Code ungleich 0), startet er die alte wieder und sagt es im Fenster.
- **Beenden nach Ordner:** beendet werden alle Prozesse, deren Programmdatei im Ordner dieser Version liegt, einschließlich dessen, was das Programm von dort aus gestartet hat. Nie über den Namen. Ordner mit gleichem Anfang (`1.0` und `1.0.1`) bleiben getrennt.
- **Kopien sind nie halb:** eine Kopie entsteht in `<version>.partial`, wird geprüft und erst dann benannt. Eine abgebrochene oder gescheiterte Kopie hinterlässt weder einen Teilordner noch einen fertigen Ordner; die laufende Version bleibt unberührt.
- **Aufräumen:** eine alte Kopie wird entfernt, wenn nichts aus ihrem Ordner läuft und sie außerhalb der behaltenen Versionen liegt (Vorgabe 2, damit man zurückgehen kann).
- **Quelle nicht erreichbar** (eine Freigabe ist offline): das Programm läuft weiter aus der letzten Kopie, der Status sagt es.
- **Autostart:** ein Eintrag unter `HKCU\…\CurrentVersion\Run`, der den Starter im Tray startet (`--tray`), nur wenn die Einstellung an ist. Ein Testdatenordner ändert die Registry nie.
- **Ein Starter je Benutzer:** ein zweiter Start übergibt dem ersten und endet.

## Bauen und starten

```powershell
dotnet build starter\StykkerStarter.slnx -c Debug
dotnet test  starter\StykkerStarter.slnx

# der Starter (öffnet sein Fenster; das Tray-Symbol bleibt nach dem Schließen)
starter\src\StykkerStarter.App\bin\Debug\net10.0-windows\StykkerStarter.exe
```

| Schalter | Wirkung |
|---|---|
| `--tray` | nur das Tray-Symbol, kein Fenster (der Autostart nutzt ihn) |
| `--data-dir <Ordner>` | anderer Datenordner (Tests, Probeläufe). Dann schreibt der Starter nie in die Registry |
| `--gpu` | zeichnet über die Grafikkarte. Ohne ihn zeichnet das Fenster in Software, wie StykkerUI |
| `--check-every <Sekunden>` | Prüfabstand in Sekunden statt der Einstellung (für Probeläufe) |

Der Datenordner enthält `starter.json` (Programme und Einstellungen, keine Geheimnisse, keine Prozess-IDs), `starter.log` (eine Zeile je Ereignis), die Versionsordner und den Cache der WebView2 in `web-shell\`.

## Aufbau

| Pfad | Inhalt |
|---|---|
| `src/StykkerStarter.Core/` | Konfiguration (`Config/`), Quellbaum und sein Listen-Hash (`Source/`), die Versionsregel (`Versions/`), die geprüfte Kopie (`Copy/`), Prozesse nach Ordner und das Beenden (`Processes/`), die Startprüfung (`Start/`), Update, Rücksprung und Aufräumen (`Update/ProgramService.cs`) und die Regel, wann ohne Rückfrage übernommen wird (`Update/StarterEngine.cs`). Kennt keine Windows-API. |
| `src/StykkerStarter.Platform.Windows/` | Prozessliste mit Programmpfaden (`QueryFullProcessImageNameW`), höfliches Schließen und Beenden über die PID, der Starter, die Dateiversion, der Autostart-Eintrag. |
| `src/StykkerStarter.App/` | `StykkerStarter.exe`: Tray und Fenster (`Shell.cs`), die Warteschlange, die alle Vorgänge der Reihe nach ausführt (`Model.cs`), die Sperre gegen einen zweiten Start und die Seite (`Web/`: `index.html`, `app.css`, `app.js`), mit dem Design-System aus `shared/design-system` eingebettet. |
| `tests/StykkerStarter.Tests/` | xUnit-Tests. Kopien in echten Temp-Ordnern, Fakes für Prozesse und Starten, ein Integrationstest mit einem echten Prozess. |
| `tests/StykkerStarter.TestTarget/` | ein kleines Konsolenprogramm als Ziel der Tests. Seine Dateiversion stellt `-p:TargetVersion=…` ein. |

## Grenzen

- **Konsolenprogramme** haben kein Hauptfenster, das Schließen bittet sie also nicht. Sie werden nach der Frist von 10 s beendet.
- **Von Hand gestartet:** ein Programm, das jemand von Hand aus dem Ordner der Kopie gestartet hat, wird beim Update ebenfalls beendet. Der Update-Dialog nennt die Prozesse.
- **Erkennung nur am Inhalt** (ohne Dateiversion) gibt es nicht als Auswahl; die Dateiversion entscheidet, und bei gleicher Version entscheidet der Ordnerinhalt.
- **Nicht änderbar:** ein Programm lässt sich nach dem Hinzufügen nicht bearbeiten. Man entfernt es und fügt es neu hinzu.

## Prüfen

```powershell
dotnet test starter\StykkerStarter.slnx
```

Letzter Lauf am 2026-10-14: 57 von 57 Tests bestanden, der Build zeigt 0 Warnungen. Ein Probelauf mit dem Testprogramm in zwei Versionen zeigte: Kopie und Prüfung von Version 1, Start, die nächste Prüfung findet Version 2, das alte Programm wird beendet (nach 10 s), Version 2 startet mit neuer Prozess-ID, Version 1 wird entfernt, und ein zweiter Start übergibt an die erste Instanz.

Lizenz: Apache-2.0 (siehe [`LICENSE`](LICENSE)).
