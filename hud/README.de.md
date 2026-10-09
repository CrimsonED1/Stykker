# StykkerHUD

Live-Monitor für diese Maschine: Prozessor, Speicher, Grafik, Datenträger und Netzwerk. Teil der Stykker-Suite, aufgebaut auf dem gemeinsamen Design-System aus [`shared/design-system`](../shared/design-system/): dieselben Farben, Komponenten und beiden Themen (*Dark*, *Spacepunk Titan*).

Die Prozessliste gehört nicht mehr zu diesem Werkzeug. Sie steht in [StykkerSYS](../sys/README.de.md), die der Reiter **Processes** auf Port 8077 öffnet.

![StykkerHUD, Thema Dark](docs/screenshots/hud-dark.jpg)

![StykkerHUD, Thema Spacepunk Titan](docs/screenshots/hud-titan.jpg)

Die Oberfläche ist ein Instrumentenfeld, keine Tabelle: jede Zahl steht in einer Festbreiten-Schrift mit gleich breiten Ziffern, jeder Zustand trägt ein Zeichen neben der Farbe, und die Kurven bewegen sich nur, wenn sich die Daten bewegen.

## Was sie zeigt

- **Prozessor**: Gesamtlast, die Fünf-Minuten-Kurve und jeder Kern hinter einer **zugeklappten Zeile**. Zugeklappt nennt sie den vollsten Kern; ein Klick öffnet die Balken, die nur gebaut werden, solange sie offen sind.
- **Speicher**: belegter und gesamter RAM sowie die Commit-Auslastung.
- **Grafik**: Auslastung, VRAM, Temperatur, Leistung gegen das Limit, Takt von Grafik und Speicher (NVIDIA über `nvml.dll`) und die Aufteilung der Last auf die Engines (3D, Compute, Copy, Video-Decode, Video-Encode) als gestapelter Balken mit Legende. Ohne `nvml.dll` (Intel- oder AMD-Grafik) kommen Auslastung, Engine-Aufteilung und belegter Speicher aus den Windows-GPU-Zählern; Temperatur, Leistung, Takte und Gesamtspeicher zeigen dann `–`.
- **Datenträger und Netzwerk**: Lesen/Schreiben und Empfangen/Senden als **Balken**. Jede Zeile zeigt den Wert gegen den Höchstwert der Sitzung (`10,6 MB/s / 23,8 MB/s`). Summen über alle physischen Laufwerke und alle aktiven Adapter.

## Aufbau

| Pfad | Was es ist |
|---|---|
| `src/StykkerHud.Core/` | Das Modell (`Samples.cs`), die Messung (`MetricsSampler`) und der Dienst (`HudService`, eine Messschleife aus `shared/`). Kennt kein Betriebssystem. |
| `src/StykkerHud.Platform.Windows/` | Die Windows-Seite: CPU und Speicher (`kernel32`, `ntdll`), GPU (`nvml.dll`), Datenträgerdurchsatz (`pdh.dll`), Netzwerkzähler. Jeder Teil ist verzichtbar: fehlt einer, zeigt sein Wert `–`. |
| `src/StykkerHud.Server/` | Blazor Server auf `http://127.0.0.1:8079`, nur lokal, mit Tray-Symbol. Liefert die Seite und `/api/snapshot`. |
| `src/StykkerHud.UI/` | `StykkerHUD.exe`, das Photino-Fenster (WebView2) um diese Seiten. |
| `../shared/` | `Stykker.Shared`: GPU-Zählerabfragen, PDH-Wrapper, Tray-Symbol, Design-System-Hosting, Messschleife. Siehe [dessen README](../shared/README.md). |
| `tools/shot.mjs` | Misst die gerenderte Seite (Werte, Farben, Geometrie, beide Themen) und schreibt die Bilder. |
| `tools/check-ui.mjs` | Verhaltensprüfungen der laufenden Seite: Kerne beim Start zugeklappt, Aufklappen zeigt je Kern eine Zeile, die Durchsatzbalken haben Breiten und Wert/Spitze-Text, keine Kennzahl läuft über ihre Spalte, die Grafikkarte meldet ihre Engine-Aufteilung. |
| `tools/onepager.mjs` | Baut aus einem Entwurf eine eigenständige Seite, indem das Design-System eingebettet wird (für Artefakte). |
| `docs/` | Bilder, Design-Entwürfe und die offene Entscheidung (`entscheidungen.md`). |

**Ohne Blick wird nichts gelesen.** Der Server belegt seinen Port und antwortet sofort, noch ohne eine einzige Messung. Die Schleife startet mit dem ersten Abruf von `/api/snapshot` und hält nach **15 Sekunden** ohne Abruf an. Die Seite fragt nur, solange sie sichtbar ist; ein geschlossenes oder verborgenes Fenster kostet also nichts. Nach einem Neustart beginnen die Differenzen (CPU-Zeiten, Durchsatzraten) von vorn, und der Verlauf fängt neu an, weil die älteren Punkte zu einem anderen Zeitfenster gehören. Die Schleife liegt im Dienst, nicht in der Seite, deshalb sieht jeder Betrachter denselben Messwert.

## Bauen und starten

```powershell
dotnet build StykkerHud.slnx -c Debug

# Fenster (startet den Server selbst, falls keiner läuft)
src\StykkerHud.UI\bin\Debug\net10.0\StykkerHUD.exe

# nur der Server, im Browser
src\StykkerHud.Server\bin\Debug\net10.0\StykkerHUD-Server.exe --port 8079
```

| Schalter | Wirkung |
|---|---|
| `--port <Zahl>` | Web-Port (Standard 8079) |
| `--no-tray` / `--no-browser` | kein Tray-Symbol / kein Browser beim Start |
| `--basic` | kein Systemzugriff: jeder Wert zeigt `–` |
| `--design-system <Ordner>` | woher das Design-System gelesen wird (siehe unten) |
| `--server <Pfad>` (Fenster) | der Server, der gestartet wird, wenn keiner läuft; sonst wird er neben dem Fenster oder im Build-Baum gesucht |
| `--gpu` (Fenster) | zeichnet über die Grafikkarte. Ohne den Schalter zeichnet das Fenster in Software, damit der Monitor nicht die GPU belegt, die er misst |

## Das Design-System ist eine Quelle, keine Kopie

Die Stile sind in diesem Repository nicht kopiert. Der Server liefert sie unter `/ds` direkt aus `shared/design-system` der Monorepo (Standard `C:\_AI\Stykker\MonoRepo\shared\design-system`). Eine Änderung dort wirkt beim nächsten Laden, für die ganze Familie. Anderer Ort: `--design-system <Ordner>` oder die Umgebungsvariable `STYKKERHUD_DESIGN_SYSTEM`.

Verwendet werden: `tokens.css`, `components/bundle.css`, `components/bundle.js` (das Symbol-Sprite) und `assets/Logos/app-icon.png`. `wwwroot/app.css` enthält nur die wenigen Regeln, die das Design-System nicht hat.

Am 2026-10-08 kamen drei Symbole für diese App in den gemeinsamen Satz: `i-arrow-up` und `i-arrow-down` (Übertragungsrichtung) und `i-disk` (das Laufwerk). StykkerLLM und StykkerSYS bekommen sie ebenfalls. Achtung: `i-down` und `i-peak` sind **Trend**-Zeichen (eine Linie mit Knick), keine Richtungspfeile.

Zwei Layout-Regeln sind wichtig: `.mgrid` passt seine Spalten an die Karte an (`repeat(auto-fit, minmax(72px, 1fr))`), und ein leerer `.stack` oder `.legend` in der Seitenleiste braucht eine eigene `[hidden]`-Regel in `wwwroot/app.css`, weil die `display`-Regeln des Design-Systems das Attribut `hidden` überstimmen.

## Prüfen

Braucht Node.js und Google Chrome. Einmal `npm install` im Ordner `tools/`.

```powershell
# Bilder und Messwerte der laufenden Anwendung (Port 8079 muss antworten);
# --wait hält die Seite so viele Sekunden offen, damit die Prozessorkurve eine Form hat
node tools\shot.mjs --port 8079 --out docs\screenshots --name hud --wait 25

# Verhalten der laufenden Seite
node tools\check-ui.mjs --port 8079
```

Jeder Lauf von `shot` schreibt `hud-measure.json` (DOM-Messwerte, Konsolenprobleme, fehlgeschlagene Anfragen), die beiden Themen-Bilder, eine ganze Seite als PNG und ein kleines Prüfbild. Ein Lauf mit `problems: none` hat keine 404 und keinen Konsolenfehler. Die DOM-Messwerte sind die Prüfung; das Bild bestätigt sie nur.

Letzter Lauf am 2026-10-09: `check-ui` 18/18 Prüfungen bestanden, keine Probleme.

## Design-Entwürfe

`docs/design/mockup-1.html` und `mockup-2.html` sind die Entwürfe von vor der Aufteilung. Sie zeigen noch die Prozesstabelle, die jetzt zu StykkerSYS gehört. Sie bleiben als Dokument der Gestaltung erhalten, nicht als aktueller Bildschirm. `mockup-2-onepager.html` ist derselbe Entwurf als eigenständige Seite (Design-System eingebettet), zum Veröffentlichen als Artefakt.

## Noch nicht gebaut

- **Durchsatz je Laufwerk und je Adapter.** Heute sind die Balken Summen über alle Laufwerke und Adapter.
- **Dauerbetrieb:** mit Windows starten und nur im Tray leben.
- **Eigenes Programm-Symbol** (`.ico`) und die drei Symbole, die dem Design-System noch fehlen (Temperatur, Leistung, Lüfter). Diese Werte tragen einen `title` statt eines Zeichens.
- **Offene Entscheidung**: Blazor/Photino oder Avalonia für die Oberfläche. Siehe [`docs/entscheidungen.md`](docs/entscheidungen.md).
