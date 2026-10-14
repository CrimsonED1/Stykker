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

## Die Zentrale

Die Zentrale ist die eine Adresse für die Stykker-Werkzeuge dieser Maschine: `/hub` auf dem Port des Monitors, standardmäßig `http://127.0.0.1:8079/hub`.

- **Leiste.** Eine Leiste oben nennt die Module. Der Winkel rechts klappt sie ein; übrig bleibt ein Zeichen (`◆`) in der Ecke. Am PC merkt sich der Browser diese Wahl.
- **Übersicht.** Ohne gewähltes Modul zeigt die Zentrale je Modul eine Kachel: Name, „Running“ oder „Not running“, Adresse und den Knopf **Open**, der aus bleibt, solange das Modul nicht antwortet. Die Überschrift zählt die laufenden Module.
- **Frame.** Ein gewähltes Modul öffnet sich im Frame unter der Leiste. Die Wahl steht in der Adresse (`#sys`); ein Neuladen bleibt also beim selben Modul.
- **Module** kommen aus `%APPDATA%\Stykker\stykker.json`:

  ```json
  { "modules": [ { "id": "sys", "name": "SYS", "port": 8077 } ] }
  ```

  Ein Eintrag mit `port` ist ein Modul dieser Maschine. Die Zentrale öffnet es unter demselben Namen, unter dem sie selbst geöffnet wurde, damit die Anmeldung auch in den Frame geht. Ein Eintrag mit `url` ist ein fremder Web-Dienst und wird unverändert gezeigt. Ohne Datei, oder wenn sie kein gültiges Modul nennt, gilt die Vorgabe: der Monitor selbst, SYS auf 8077 und LLM auf 8078.
- **Weitergeleitete Dienste.** Ein fremder Eintrag mit `port` und `target` läuft anderswo auf diesem PC, und die Zentrale leitet ihn weiter: die Zentrale hört auf `port`, die Weiterleitung geht nur an `target`. Als Ziel gilt nur `http://127.0.0.1:<Port>` oder `http://localhost:<Port>`. Beispiel für Qwen Code, mit Platzhaltern für die Pfade:

  ```json
  { "modules": [
    { "id": "qwen", "name": "Qwen Code", "external": true, "port": 8076,
      "target": "http://127.0.0.1:41999",
      "start": { "command": "node",
                 "args": ["<Pfad zu cli.js>", "serve", "--port", "41999", "--no-open"],
                 "env": { "QWEN_HOME": "<Ordner>" },
                 "tokenEnv": "QWEN_SERVER_TOKEN" } } ] }
  ```

- **Dienst starten.** `start` ist optional. Die Zentrale startet den Dienst nur, wenn auf `target` nichts antwortet. Ein Dienst, der schon läuft, bleibt unberührt: die Zentrale startet ihn nicht und beendet ihn nicht. `command` und `tokenEnv` sind Pflicht; `args` und `env` enthalten nur Zeichenketten. Die Zentrale ergänzt die Argumente um `--hostname 127.0.0.1 --require-auth` (eigene Schalter, die schon in `args` stehen, fallen vorher weg) und gibt ihr Token in der Umgebungsvariable, die `tokenEnv` nennt, nie in der Kommandozeile. Die Ausgabe landet im Log der Zentrale, mit dem Token ersetzt durch `***`. Endet die Zentrale, auch wenn sie hart geschlossen wird, endet der Dienst, den sie gestartet hat, mit ihr, einschließlich der Prozesse, die dieser Dienst selbst gestartet hat.
- **Dienst-Token.** Die Zentrale hält je Modul-Kennung ein Token in `services.dat`, neben `pairing.dat`, und Windows schützt die Datei genauso. Ohne diesen Schutz schreibt die Zentrale die Datei nicht im Klartext. Ein Gerät bekommt das Token nie; die Zentrale setzt es in jede weitergeleitete Anfrage.
- **Wer einen weitergeleiteten Dienst sieht und öffnet.** Wie jeder fremde Eintrag ist er nur an diesem PC und für Geräte mit der Rolle `admin` für dieses Modul aufgeführt. Die anderen sehen ihn nicht in der Leiste. Eine Anfrage an ihn von einer fremden Seite bekommt `403`, auch von einem Admin-Gerät.
- **Host-Name.** Die Weiterleitung antwortet nur unter einer IP-Adresse, `localhost`, dem Namen dieses PCs oder dem Namen mit der eigenen DNS-Endung dieses PCs. Jeder andere Host-Name bekommt `403`, auch von diesem PC und mit passender `Origin`. So scheitert DNS-Rebinding: Eine Webseite, deren Name auf `127.0.0.1` zeigt, erreicht den Dienst über diesen Namen nicht. Ein Name wie `<PC-Name>.<andere Domain>` ist ebenfalls abgewiesen, weil die Domain jedem gehören kann. Tailscale-Namen (`<PC>.<Tailnet>.ts.net`) sind abgewiesen, außer die DNS-Endung des PCs ist dieses Tailnet.
- **Kleine Bildschirme.** Am Handy und in der installierten App beginnt die Leiste eingeklappt und öffnet sich über ein Zeichen unten rechts als Menü, in Daumennähe. Die Wahl eines Moduls klappt das Menü wieder zu.
- **Thema.** Der Knopf in der Leiste wechselt wie im Monitor zwischen *Dark* und *Spacepunk Titan*. Der Frame folgt sofort.
- **Als App installieren.** Die Zentrale hat ein Web-App-Manifest (`/manifest.webmanifest`, eigenständig angezeigt, Start bei `/hub`), mit dem der Browser sie installieren kann.
- **Eigene Module im Frame.** HUD und SYS zeigen sich im Frame ohne eigene Marke und im Thema der Zentrale: die Zentrale hängt `?embed=1&theme=…` an ihre Adresse. Ein fremder Dienst behält sein Aussehen.

## Zugang aus dem Netz

Standardmäßig erreicht niemand außerhalb dieses PCs StykkerHUD oder StykkerSYS. Der Zugang aus dem Netz ist ein eigener, zweiter Schritt.

- **Dieser PC ist Admin.** Anfragen von dieser Maschine kommen ohne Code herein und gelten als `admin`. Das gilt für StykkerHUD und StykkerSYS.
- **Netzschalter.** Das Netz ist standardmäßig aus. Der Schalter steht in der Zentrale unter „Network access“, mit den Knöpfen „Turn on“ und „Turn off“. Der Server öffnet seinen Port nur dann im Netz, wenn der Schalter beim Start des Programms an ist; sonst hört er nur auf `127.0.0.1`. Ein Umschalten wirkt deshalb nach dem nächsten Start von StykkerHUD und StykkerSYS, wie die Zentrale auch sagt. Ausschalten sperrt Anfragen von außen sofort; der Port bleibt bis zum nächsten Start offen.
- **Code.** Ist der Schalter an, zeigt die Zentrale unter „Add a device“ den Code und die Adressen dieses PCs mit Port und `/hub`. Der Code hat sechs Ziffern, angezeigt als `482 913`. Er gilt zehn Minuten und funktioniert einmal. Nach fünf Fehlversuchen zeigt der PC einen neuen. „New code“ ersetzt ihn sofort.
- **Anmeldung.** Ein Gerät gibt den Code auf der Anmeldeseite `/pair` ein. Danach hält es das Cookie `stykker_device`: `HttpOnly`, `SameSite=Lax`, 730 Tage gültig. Das Attribut `Secure` wird nur bei HTTPS gesetzt. Geräte und Codes stehen in `%APPDATA%\Stykker\pairing.dat`, die Windows für den angemeldeten Benutzer schützt. Gespeichert wird nur ein Hash des Geräte-Tokens.
- **Rollen.** Jedes Gerät hat je Modul eine Rolle: `admin` oder `viewer`. Angemeldet an einem Modul ist es dort `admin`, in jedem anderen Modul `viewer`. In der Geräteliste der Zentrale ändert man die Rolle je Modul (eine Spalte je Modul, etwa „Monitor“, „SYS“, „LLM“ und „Qwen Code“) oder entfernt das Gerät. Ein weitergeleiteter Dienst hat keine eigene Anmeldeseite; ein Gerät ist dort nur `admin`, wenn der PC es so einstellt.
- **Was die Rollen ändern.** In StykkerSYS darf `admin` Prozesse beenden und Prioritäten setzen; `viewer` sieht nur zu (siehe [StykkerSYS](../sys/README.de.md#wer-was-darf)). Der Monitor hat keine Aktion; dort ändert die Rolle bisher nichts. Bei einem weitergeleiteten Dienst entscheidet die Rolle, ob sein Frame sich öffnet: `admin` kommt durch, `viewer` bekommt `403`. Die Einstellung steht in der Geräteliste unter der Spalte des Dienstes.
- **Wer verwaltet.** Netzschalter, Code und Geräteliste lassen sich nur an diesem PC bedienen, und nur von der Zentrale selbst aus. Ein Gerät im Netz sieht weder Code noch Geräteliste; seine Anfrage danach bekommt nur `local: false`.
- **Ohne Anmeldung aus dem Netz.** Ist der Schalter an, brauchen diese Wege kein Gerät: `/api/ping`, `/manifest.webmanifest`, die Symbole `/hub-icon…` und die Anmeldeseite `/pair`. Alles andere braucht ein gültiges Gerät. Ein API-Aufruf oder eine Anfrage außer GET bekommt `401`; jede andere GET-Anfrage, auch eine Seite, wird zu `/pair` umgeleitet. Ist der Schalter aus, bekommen Anfragen von außen `403`, auch für diese Wege.
- **Host-Name.** Jede Anfrage an StykkerHUD und StykkerSYS muss eine IP-Adresse, `localhost` oder den Namen dieses PCs nennen (mit oder ohne seine eigene DNS-Endung). Jeder andere Host-Name bekommt `403`, auch von diesem PC. So scheitert DNS-Rebinding.
- **Fremde Seiten können den Monitor nicht einbetten.** Seine Seiten erlauben das Einbetten nur durch sich selbst (`frame-ancestors 'self'`). Keine andere Website kann sie im Frame zeigen.
- **Rahmen für weitergeleitete Dienste.** Die Zentrale entfernt den Header `X-Frame-Options` aus der Antwort eines weitergeleiteten Dienstes und setzt `frame-ancestors` auf die eigene Adresse der Zentrale. Der Dienst lässt sich dann im Frame der Zentrale zeigen, in keinem fremden.
- **Kein HTTPS.** Das Cookie wandert im Klartext durchs Netz. Den Netzzugang deshalb nur im eigenen Netz oder über ein VPN nutzen.

## Aufbau

| Pfad | Was es ist |
|---|---|
| `src/StykkerHud.Core/` | Das Modell (`Samples.cs`), die Messung (`MetricsSampler`) und der Dienst (`HudService`, eine Messschleife aus `shared/`). Kennt kein Betriebssystem. |
| `src/StykkerHud.Platform.Windows/` | Die Windows-Seite: CPU und Speicher (`kernel32`, `ntdll`), GPU (`nvml.dll`), Datenträgerdurchsatz (`pdh.dll`), Netzwerkzähler. Jeder Teil ist verzichtbar: fehlt einer, zeigt sein Wert `–`. |
| `src/StykkerHud.Server/` | Blazor Server auf `http://127.0.0.1:8079`, mit Tray-Symbol. Liefert den Monitor, die Zentrale unter `/hub` und `/api/snapshot`. Nur an diesem PC, außer der Netzschalter ist beim Start an (siehe [Zugang aus dem Netz](#zugang-aus-dem-netz)). Leitet die fremden Dienste aus `stykker.json` weiter und startet sie (`ServiceForwarding.cs`, `ServiceProcesses.cs`; die Regeln stehen in `shared/…/Web/ServiceProxyRules.cs`). |
| `src/StykkerHud.UI/` | `StykkerHUD.exe`, das Photino-Fenster (WebView2) um diese Seiten. |
| `../shared/` | `Stykker.Shared`: GPU-Zählerabfragen, PDH-Wrapper, Tray-Symbol, Design-System-Hosting, Messschleife. Siehe [dessen README](../shared/README.md). |
| `tools/shot.mjs` | Misst die gerenderte Seite (Werte, Farben, Geometrie, beide Themen) und schreibt die Bilder. |
| `tools/check-ui.mjs` | Verhaltensprüfungen der laufenden Seite: Kerne beim Start zugeklappt, Aufklappen zeigt je Kern eine Zeile, die Durchsatzbalken haben Breiten und Wert/Spitze-Text, keine Kennzahl läuft über ihre Spalte, die Grafikkarte meldet ihre Engine-Aufteilung. |
| `tools/check-proxy.mjs` | Prüft einen weitergeleiteten Dienst (Qwen Code) durchgehend: Zugangsregeln, Token, Frame, Stream, WebSocket, eigener Daemon und das Beenden mit der Zentrale. Siehe [Prüfen](#prüfen). |
| `tools/onepager.mjs` | Baut aus einem Entwurf eine eigenständige Seite, indem das Design-System eingebettet wird (für Artefakte). |
| `docs/` | Bilder, Design-Entwürfe und die Entscheidungsnotiz (`entscheidungen.md`). |

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
| `--pairing-file <Datei>` | die Kopplungsablage: Geräte, Code und Netzschalter. Standard `%APPDATA%\Stykker\pairing.dat` |
| `--hub-config <Datei>` | die Module der Zentrale statt `%APPDATA%\Stykker\stykker.json` (Probeläufe) |

## Das Design-System ist eine Quelle, keine Kopie

Die Stile sind in diesem Repository nicht kopiert. Der Server liefert sie unter `/ds` direkt aus `shared/design-system` der Monorepo, damit eine Änderung dort beim nächsten Laden wirkt, für die ganze Familie.

Den Ordner findet das Programm selbst. Es beginnt im eigenen Programmordner und geht aufwärts, bis es einen Ordner mit `shared/design-system/tokens.css` findet. Der erste Treffer gilt; das Programm muss dafür nur im Repo liegen. Findet die Suche nichts, nimmt das Programm einen festen Rückfallpfad aus dem Code; fehlt auch der, läuft die Seite ohne Stile, und die Konsole nennt den Ordner, in dem gesucht wurde.

Anderer Ort: `--design-system <Ordner>` oder die Umgebungsvariable `STYKKERHUD_DESIGN_SYSTEM`.

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

# ein weitergeleiteter Dienst (Qwen Code): braucht den gebauten Server und die cli.js von Qwen Code.
# Er startet eine eigene Zentrale und ein eigenes qwen serve auf 127.0.0.1, mit Temp-Ordnern und den
# Ports 8090, 8091, 8092, 41998 und 41999; beendet werden nur die Prozesse, die er selbst gestartet hat.
node tools\check-proxy.mjs --qwen-cli <Pfad zu cli.js>
```

`check-proxy` braucht außerdem Google Chrome, das es über Playwright startet. Sein Temp-Ordner wird nach einem bestandenen Lauf gelöscht und nach einem fehlgeschlagenen behalten.

Jeder Lauf von `shot` schreibt `hud-measure.json` (DOM-Messwerte, Konsolenprobleme, fehlgeschlagene Anfragen), die beiden Themen-Bilder, eine ganze Seite als PNG und ein kleines Prüfbild. Ein Lauf mit `problems: none` hat keine 404 und keinen Konsolenfehler. Die DOM-Messwerte sind die Prüfung; das Bild bestätigt sie nur.

Letzter Lauf am 2026-10-09: `check-ui` 18/18 Prüfungen bestanden, keine Probleme. Letzter Lauf am 2026-10-14: `check-proxy` 48/48 Prüfungen bestanden.

## Design-Entwürfe

`docs/design/mockup-1.html` und `mockup-2.html` sind die Entwürfe von vor der Aufteilung. Sie zeigen noch die Prozesstabelle, die jetzt zu StykkerSYS gehört. Sie bleiben als Dokument der Gestaltung erhalten, nicht als aktueller Bildschirm. `mockup-2-onepager.html` ist derselbe Entwurf als eigenständige Seite (Design-System eingebettet), zum Veröffentlichen als Artefakt.

## Noch nicht gebaut

- **Durchsatz je Laufwerk und je Adapter.** Heute sind die Balken Summen über alle Laufwerke und Adapter.
- **Dauerbetrieb:** mit Windows starten und nur im Tray leben.
- **Eigenes Programm-Symbol** (`.ico`) und die drei Symbole, die dem Design-System noch fehlen (Temperatur, Leistung, Lüfter). Diese Werte tragen einen `title` statt eines Zeichens.
- **Entschieden am 2026-10-13:** die Oberfläche ist Web: Blazor, gezeigt in einem Photino-Fenster. Siehe [`docs/entscheidungen.md`](docs/entscheidungen.md).
- **Für Zentrale und Zugang fehlt noch:**
  - **HTTPS.** Ohne HTTPS wandert das Cookie im Klartext. Die Installation der Zentrale als App verlangt außerhalb von `localhost` in der Regel ebenfalls HTTPS.
  - **Prüfung am echten Handy.** Der Zugang aus dem Netz ist noch nicht an einem echten Handy geprüft, auch nicht über die Windows-Firewall hinweg. Der weitergeleitete Dienst ist von diesem PC über `127.0.0.1` und seine LAN-Adresse geprüft worden, mit einem Browser als Ersatz für das Handy.
  - **Ordentliches Beenden eines weitergeleiteten Dienstes.** Strg+C in der Konsole der Zentrale ist nicht geprüft; `check-proxy` beendet die Zentrale mit `taskkill`.
  - **Kopplung andersherum und QR-Code.** Das neue Gerät zeigt den Code, ein gekoppeltes Gerät gibt ihn frei. Das können die Seiten noch nicht, und einen QR-Code gibt es nicht.
  - **Service Worker.** Es gibt keinen. Das Manifest reicht zum Installieren; ohne Verbindung zeigt die App nichts.
