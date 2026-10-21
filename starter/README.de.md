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
- **Ohne Kopie** (Option „Copy to this PC before starting“ aus): das Programm läuft direkt aus dem Ordner seiner Quelle. Es wird nichts kopiert und nichts unter dem Datenordner abgelegt. Der Starter prüft die Quelle in jedem Takt. Ein geänderter Build zählt, sobald zwei Prüfungen hintereinander denselben Stand sehen; läuft das Programm dann, beendet der Starter, was aus diesem Ordner läuft (höflich, nach 10 s endgültig), und startet neu, oder bietet es nur an, wenn die Einstellungen es so verlangen. Version und Dateilisten-Hash, mit dem das Programm gestartet wurde, stehen in `starter.json`. Ein neuer Build, der nicht startet, wird nicht zurückgenommen; der Status sagt es. Ist die Quelle nicht erreichbar, kann das Programm nicht starten. Nur `.exe`- und `.dll`-Dateien in allen Unterordnern zählen als geänderter Build; Logs, Daten und Einstellungen nicht.
- **Autostart:** ein Eintrag unter `HKCU\…\CurrentVersion\Run`, der den Starter im Tray startet (`--tray`), nur wenn die Einstellung an ist. Ein Testdatenordner ändert die Registry nie.
- **Ein Starter je Benutzer:** ein zweiter Start übergibt dem ersten und endet.
- **Neuer Build statt Update:** hat die Quelle dieselbe Version wie die laufende Kopie und nur anderen Inhalt, meldet der Starter „new build of <Version>“ und zeigt den Zeitpunkt, zu dem die Programmdateien der Quelle geschrieben wurden (`sourceBuiltAt`, Ortszeit).
- **Keine Doppelten:** dieselbe Programmdatei (Groß- und Kleinschreibung egal) mit denselben Argumenten gibt es nur einmal. Das Hinzufügen lehnt das ab; die Suche nach einer Datei meldet den vorhandenen Eintrag (`duplicateOf`).
- **Bearbeiten:** Name, Argumente, Startoptionen, Neustart nach Absturz, Pause vor dem Start, Bereit-Prüfung und Kopie an oder aus. Die Programmdatei lässt sich nicht ändern. Ein neuer Name oder neue Argumente gelten beim nächsten Start. Kopie aus: die Kopien bleiben liegen, bis das Aufräumen sie entfernt. Kopie an: nur wenn das Programm nicht läuft; sonst erst stoppen.
- **Zurück zur vorigen Version** (nur mit Kopie): die jüngste behaltene ältere Kopie startet und wird die aktuelle; die neuere Kopie bleibt liegen. Danach übernimmt die automatische Prüfung nichts ohne Rückfrage, bis man selbst aktualisiert.
- **Vorgänge sichtbar:** während ein Vorgang für ein Programm läuft, trägt sein Eintrag `busy` (`queued`, `starting`, `stopping`, `restarting`, `updating`, `copying`, `rollback`) und Beginn (`busySince`); beim höflichen Schließen die Frist (`busyGraceMs`, 10 s).
- **Neustart nach Absturz** (Vorgabe aus): ein Absturz ist ein Ende, das weder der Starter noch der Benutzer veranlasst hat (Stop, Stop all, Update und Beenden merken sich das). Der Starter prüft die Prozessliste alle 5 s und startet neu nach 5 s, 15 s, dann jeweils 60 s. Mehr als fünf Neustarte in zehn Minuten: er gibt auf (`crashState` = `gaveUp`) und meldet es. Ein Start von Hand setzt den Zähler zurück.
- **Reihenfolge und Pausen:** die Startreihenfolge ist die Reihenfolge in `starter.json` und lässt sich verschieben. Vor jedem Programm gibt es eine eigene Pause beim Hochfahren (`startDelaySeconds`, 0 bis 120 s; nicht beim Start von Hand). Vor dem Ganzen wartet der Starter nach dem Fenster die Einstellung `startupDelaySeconds` (0 bis 60 s, Vorgabe 3). „Start now“ beendet die Pausen, „Skip“ lässt die Startfolge entfallen. Während der Startfolge zeigt der Zustand `startupNext` und `startupLeftMs`.
- **„Läuft“ heißt bereit** (optional, `readyCheck`): eine Portnummer (geprüft wird 127.0.0.1) oder eine `http://`- oder `https://`-Adresse, deren Host `localhost`, `127.0.0.1` oder `[::1]` ist. Nach dem Start prüft der Starter jede Sekunde, bis das Programm antwortet oder 60 s um sind (`ready` = `waiting`, `ready` oder `timeout`). Läuft es, wird alle 5 s weiter geprüft; eine fehlschlagende Prüfung macht es wieder „waiting“, ein Neustart folgt daraus nicht.
- **Alle starten, alle stoppen:** die Befehle `startAll` und `stopAll` gehen die Programme der Reihe nach durch; ohne die Startpausen.

## starter.json

Neben den bisherigen Feldern (`id`, `name`, `sourceExe`, `arguments`, `startWithStarter`, `updateWithoutAsking`, `keepPrevious`, `copyToLocal`, `copies`, `currentFolder`) kennt der Starter:

| Feld | Vorgabe | Bedeutung |
|---|---|---|
| `restartOnCrash` | `false` | nach einem Absturz neu starten (siehe „Neustart nach Absturz“) |
| `startDelaySeconds` | `0` | Pause vor dem Start dieses Programms beim Hochfahren, 0 bis 120 |
| `readyCheck` | leer | Bereit-Prüfung: Portnummer oder `http(s)://`-Adresse auf localhost; leer heißt aus |
| `heldAfterRollback` | `false` | nach einem Zurückgehen fragt die Prüfung, bis man selbst aktualisiert; der Starter setzt es |

In `settings` kommt `startupDelaySeconds` dazu (Vorgabe 3, 0 bis 60). Fehlt ein Feld, gilt seine Vorgabe; eine ältere `starter.json` lädt unverändert.

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

Der Datenordner enthält `starter.json` (Programme und Einstellungen, keine Geheimnisse, keine Prozess-IDs), die Versionsordner und den Cache der WebView2 in `web-shell\`. Das Protokoll hat eine Zeile je Ereignis und liegt neben der Exe im Ordner `logs\` als `starter-<Rechner>.log`, damit sich Protokolle mehrerer PCs auf einer Freigabe nicht mischen. Ist dieser Ordner nicht beschreibbar, liegt es als `logs\starter.log` im Datenordner.

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
- **Ohne Kopie** kann ein Build in den Quellordner scheitern, solange das Programm läuft, weil Windows die laufenden Dateien sperrt. Jeder Prozess, der aus dem Quellordner oder einem Unterordner läuft, zählt beim Neustart zum Programm und wird beendet.
- **Quelle fest:** die Programmdatei eines Eintrags lässt sich nach dem Hinzufügen nicht ändern. Dafür entfernt man den Eintrag und fügt ihn neu hinzu; alles andere ändert man beim Bearbeiten.

## Prüfen

```powershell
dotnet test starter\StykkerStarter.slnx
```

Letzter Lauf am 2026-10-21: 146 von 146 Tests bestanden (vorher 90), der Build zeigt 0 Warnungen. Ein Probelauf mit dem Testprogramm und einem eigenen Datenordner (mit Kopie, `--tray`, `--check-every 20`) zeigte: Kopie und Start von Version 1.0.0.0; ein von Hand beendetes Programm wird nach dem Absturz neu gestartet, und das Protokoll meldet „crash … restart ok“ mit neuer Prozess-ID. Starter und Programm wurden danach über ihre eigenen Prozess-IDs beendet. Ein Probelauf mit dem Testprogramm ohne Kopie zeigte: Version 1 startet aus dem Quellordner; eine geänderte `.dll` in einem Unterordner wird bei der zweiten Prüfung erkannt, und das Programm startet aus demselben Ordner neu (das alte Programm endete nach 10 s); eine Logdatei und eine Textdatei ändern nichts; bei gestopptem Programm wird eine Version 2 im Ordner als geändert gemeldet, und nichts startet; ein neu gestarteter Starter startet Version 2; mit „Ask before updating“ an wird eine Änderung nur angeboten, und das laufende Programm bleibt unberührt. Der Probelauf mit Kopie (2026-10-14) zeigte: Kopie und Prüfung von Version 1, Start, die nächste Prüfung findet Version 2, das alte Programm wird beendet (nach 10 s), Version 2 startet mit neuer Prozess-ID, Version 1 wird entfernt, und ein zweiter Start übergibt an die erste Instanz.

Lizenz: Apache-2.0 (siehe [`LICENSE`](LICENSE)).
