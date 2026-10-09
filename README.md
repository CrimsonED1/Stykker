# StykkerCMD

Zwei-Panel-Dateimanager für Windows und Linux. Tastatur zuerst, ohne Browser, Server oder offenen Port.
Die Farben kommen aus dem Design-System der Stykker-Familie (Themes Dark und Spacepunk Titan).

## Starten

```bash
dotnet run --project src/StykkerCmd.UI -f net10.0
```

Startordner per Argument: `--left <ordner> --right <ordner>`. Fehlt ein Pfad, startet das Panel im Benutzerordner.

## Tasten

| Taste | Wirkung |
|---|---|
| Tab | Panel wechseln |
| Pfeiltasten, Pos1, Ende, Bild auf/ab | Cursor bewegen |
| Enter | Ordner öffnen, Datei mit dem Standardprogramm öffnen |
| Rücktaste | Einen Ordner nach oben |
| Einfg oder Leertaste | Eintrag markieren, eine Zeile weiter |
| Strg+A | Alle markieren (zweimal drücken löst die Markierung) |
| F5 | Markierte oder aktuelle Einträge in den anderen Ordner kopieren |
| F6 | Verschieben |
| F7 | Neuen Ordner anlegen |
| F8 | In den Papierkorb (nach Bestätigung) |
| Umschalt+F8 oder Umschalt+Entf | Endgültig löschen (doppelt bestätigt) |
| Strg+1 bis Strg+4 | Sortieren nach Name, Erweiterung, Größe, Datum (zweimal = Richtung umkehren) |
| Strg+F | Filter eingeben; `*` und `?` sind Platzhalter, sonst zählt jeder Teiltreffer |
| Esc | Filter leeren; laufenden Auftrag abbrechen |
| Strg+R | Ordner neu laden |
| F9 | Thema wechseln (Dark und Titan) |

## Sicherheit

- Konflikte fragen nach: überschreiben, überspringen, umbenennen oder abbrechen. Mit "für alle" gilt die Antwort für den ganzen Vorgang. Ordner werden nie über Dateien geschrieben.
- Kopieren und Verschieben arbeiten mit Teilstücken. Ein abgebrochener Vorgang lässt nur markierte Teilstände (Endung `.stykker-teil`) zurück, die Quelle bleibt vollständig.
- Verschieben über Datenträgergrenzen ist Kopieren plus Löschen. Die Quelle wird erst nach geprüfter Größe des Ziels entfernt.
- Links und Junctions werden verfolgt, aber nie doppelt. Eine Schleife wird erkannt und gemeldet.
- Löschen fragt immer. Datenträger ohne Papierkorb (Netzlaufwerke, USB) löschen nur endgültig, nach doppelter Bestätigung.
- Ist eine Datei gesperrt, nennt die Meldung das Programm, das sie hält (Windows: Restart Manager, Linux: `/proc`).
- Verweigerter Zugriff bietet die Wiederholung mit Administratorrechten an (UAC unter Windows, pkexec unter Linux). Der Helfer führt nur die übergebenen Einzelschritte aus, ohne Shell und ohne Muster.

## Aufbau

| Projekt | Inhalt |
|---|---|
| `src/StykkerCmd.Core` | Modell, Sortierung, Filter, Operationslauf, Schnittstellen. Ohne Avalonia und ohne Betriebssystemcode |
| `src/StykkerCmd.Platform.Windows` | Lange Pfade (`\\?\`), Papierkorb (IFileOperation), Restart Manager, UAC, Fokus |
| `src/StykkerCmd.Platform.Linux` | FreeDesktop-Papierkorb, `/proc`-Sperrsuche, `realpath`, pkexec, xdg-open |
| `src/StykkerCmd.UI` | Avalonia-Oberfläche, Tastenbelegung, Themes aus `Themes/tokens.json` (Kopie des Design-Systems) |
| `tests/StykkerCmd.Core.Tests` | Tests mit Speicher-Dateisystem: Konflikte, Abbruch, Schleifen, Papierkorb, Helfer, Sortierung |
| `tests/StykkerCmd.Platform.Tests` | Integrationstests je Betriebssystem (werden auf dem anderen System übersprungen) |

Abhängigkeiten zeigen nur nach innen: UI → Platform → Core.

## Tests

```bash
dotnet test StykkerCmd.slnx
```

Zwei Tests brauchen eine Umgebungsvariable und laufen sonst nicht: der 2-GB-Abbruchtest mit `STYKKER_GROSSTEST=1`, der Papierkorbtest mit `STYKKER_PAPIERKORB_TEST=1` (er legt eine Datei im echten Papierkorb ab).

## Bekannte Grenzen

- Linux ist kompiliert und mit Tests abgedeckt, aber auf diesem Stand nicht auf einem Linux-System ausgeführt.
- Die UAC- und pkexec-Abfrage selbst ist nicht automatisiert geprüft; geprüft ist der Helfer-Lauf ohne Abfrage.
- Auf Linux gibt es nur den Papierkorb des Benutzers. Andere Datenträger löschen endgültig.
- Wayland erlaubt Anwendungen keinen Fokuswechsel; Drag & Drop ist nicht umgesetzt (laut Plan ein Vorschlag).
- Keine Archive, keine Suche, kein Mehrfach-Umbenennen, kein Viewer, keine Netzwerkprotokolle (laut Plan nicht im MVP).
