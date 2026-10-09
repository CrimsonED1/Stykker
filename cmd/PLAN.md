# StykkerCMD: Mini-Plan (MVP)

Stand: 9. Oktober 2026. Nur Planung, es wurde nichts gebaut oder installiert.
Repo: https://github.com/CrimsonED1/Stykker-CMD (Branch `main`, Lizenz Apache-2.0)
Status: Entwurf, noch nicht freigegeben.

## 1. Ziel und MVP-Umfang

StykkerCMD ist ein nativer Zwei-Panel-Dateimanager für Windows und Linux, mit Tastatur-Bedienung, ohne Browser, Server oder offenen Port. Der MVP umfasst Navigation, Sortierung und Filter pro Panel sowie F5 Kopieren, F6 Verschieben, F7 Ordner anlegen und F8 Löschen in den Papierkorb. Kopieren und Verschieben zeigen Fortschritt, sind abbrechbar und fragen bei existierenden Dateien nach; Löschen fragt immer. Nicht im MVP: Archive, Suche, Mehrfach-Umbenennen, Viewer, Netzwerkprotokolle und (Vorschlag) Drag & Drop.

## 2. Projektstruktur

Solution `StykkerCmd.slnx` mit `Directory.Build.props` (wie StykkerHUD) und `Directory.Packages.props` (zentrale Paketversionen).

- `src/StykkerCmd.Core`: Modell, Sortierung, Filter, Operationsplanung, Schnittstellen. Kein Avalonia, kein OS-Code.
- `src/StykkerCmd.Platform.Windows`: Long Paths, Reparse Points, `IFileOperation` mit Papierkorb, Restart Manager, UAC-Helfer.
- `src/StykkerCmd.Platform.Linux`: FreeDesktop-Trash, `/proc`-Scan, pkexec-Helfer.
- `src/StykkerCmd.UI`: einzige Avalonia-Schicht (Views, Tastatur, Themes aus `tokens.json`, Composition Root). Ausgabe `StykkerCMD`.
- `tests/StykkerCmd.Core.Tests` (xUnit, Fake-Dateisystem) und `tests/StykkerCmd.Platform.Tests` (Integrationstests je OS).

Abhängigkeiten zeigen nur nach innen (UI → Platform → Core). Muster aus StykkerHUD werden kopiert, nicht geteilt.

## 3. Meilensteine

1. **Gerüst:** Projekte, Avalonia-Fenster mit Zwei-Panel-Layout, Themes Dark und Titan. *Prüfung:* startet unter Windows 11 und Linux in beiden Themes; Core referenziert kein Avalonia.
2. **Panels:** Lesen, Sortieren, Filtern, Tastatur, Schleifenschutz für Symlinks und Junctions (über Gerät plus Datei-ID), lange Pfade. *Prüfung:* Symlink-Schleife bricht ab; 300-Zeichen-Pfad öffnet sich (Windows); `a.txt` und `A.txt` sind unter Linux zwei Einträge.
3. **Kopieren, Verschieben, Ordner anlegen:** Fortschritt, Abbruch, Konfliktdialog (überschreiben, überspringen, umbenennen, abbrechen). Verschieben über Laufwerksgrenzen als Kopie plus Löschen; die Quelle wird erst nach geprüftem Ziel entfernt. *Prüfung:* 10 000 Dateien plus eine 2-GB-Datei; Abbruch hinterlässt nur markierte Teilstände, die Quelle bleibt vollständig.
4. **Löschen:** F8 mit Bestätigung in den Windows-Papierkorb bzw. FreeDesktop-Trash. *Prüfung:* Datei ist im jeweiligen Papierkorb wiederherstellbar; Abbrechen ändert nichts.
5. **Rechte und Sperren:** UAC- und pkexec-Helfer; Sperren über Restart Manager (Windows) bzw. `/proc` (Linux). Dort blockiert ein offener Handle das Löschen meist nicht, wohl aber das Schreiben in laufende Programme (ETXTBSY). *Prüfung:* abgelehnte Rechteabfrage bricht mit Meldung ab; eine geöffnete Datei nennt ihren Prozess (unter Linux nur eigene).
6. **Härtung:** Dateinamen nur als Text, Performance, Titan-Kontrast. *Prüfung:* `<b>x</b>` erscheint wörtlich; 50 000 Einträge sortieren und filtern in unter 1 s; kleine Titan-Schrift nur auf card/bg, wie `tokens.json` vorgibt; Integrationstests grün auf beiden Systemen.

## 4. Risiken und offene Fragen

1. **Wayland und Drag & Drop:** Avalonias Wayland-Backend ist laut Avalonia-Blog 12.1 experimentell und nur per Opt-in aktiv. Drag & Drop wird dort genannt, ist aber nicht unabhängig geprüft; X11 bekommt laut Blog vollständiges XDND. Abnahme daher auf X11 bzw. XWayland, natives Wayland als eigener Test.
2. **Rechte-Helfer:** größte Angriffsfläche. Nur typisierte Operationen, keine Shell-Strings, Pfade direkt vor dem Schreiben erneut prüfen (Symlink-Wettlauf), Helfer nur für eine Operation.
3. **Datenverlust beim Verschieben:** Abbruch oder Stromausfall darf die Quelle nicht vor dem geprüften Ziel entfernen.
4. **Frage Schriften:** Soll eine freie Schrift mitgeliefert werden (Lizenz prüfen), oder genügt eine Fallback-Kette aus Systemschriften mit leicht abweichendem Layout?
5. **Frage Löschen ohne Papierkorb:** Netzlaufwerke und USB-Datenträger haben unter Windows meist keinen Papierkorb. Dort endgültig löschen (nach doppelter Bestätigung) oder verweigern?

## 5. Die ersten drei Umsetzungsschritte (nur benennen)

1. Solution, Projekte, `Directory.Build.props` und `Directory.Packages.props` anlegen; Avalonia 12.1.3 festlegen.
2. Tokens für Dark und Titan aus `tokens.json` als Kopie in `StykkerCmd.UI` übernehmen; Schriften-Fallback festlegen.
3. Leeres Avalonia-Hauptfenster mit Platzhalter-Panels und Tastatur-Routing; Start unter Windows und Linux prüfen.

## Quellen

- Avalonia 12.1.3 ist laut NuGet-Index (https://www.nuget.org/packages/Avalonia) die neueste stabile Version, geprüft am 9. Oktober 2026. Ziele net8.0 und net10.0. Rückfall: 11.3.22.
- Avalonia-Blog 12.1: https://avaloniaui.net/blog/release-12-1
- Release-Notes 12.1.1 (nennen `UseWaylandWithFallback` ohne Erklärung): https://github.com/AvaloniaUI/Avalonia/releases/tag/12.1.1
- Papierkorb Linux: FreeDesktop Trash Specification 1.0 (2014): https://specifications.freedesktop.org/trash/1.0
- Design: lokal `C:\_AI\Stykker\MonoRepo\shared\design-system\tokens.json` (Stand 2026-10-07; Pfad seit dem Umzug unter `C:\_AI\Stykker`).
