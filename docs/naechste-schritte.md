# Was als Nächstes kommt

Stand 2026-10-14 · gehört zu [plan-stykkernode.md](plan-stykkernode.md) (das Zielbild und was gebaut ist) · Bild: [architektur.svg](architektur.svg) · Mockup: [design/mockup-zentrale.html](design/mockup-zentrale.html)

Die Reihenfolge folgt einem Ziel: **die Zentrale als App auf dem Handy, mit einer Anmeldung für alles.** Jeder Schritt ist für sich fertig und prüfbar. Was eine Entscheidung braucht, steht dabei.

## Wo wir stehen

| Gebaut und geprüft | Fehlt dafür noch |
|---|---|
| Gemeinsame Kopplung (`Stykker.Pairing`), eine Ablage für HUD, SYS und LLM | Prüfung am echten Handy und an einem zweiten PC (Schritt 1, Abnahme 3 in [plan-llm-modul.md](plan-llm-modul.md)) |
| Zentrale im HUD unter `/hub`: Leiste, Übersicht, Module im Frame, einklappbar | Qwen Code in der eigenen `stykker.json` eintragen (Beispiel in `hud/README.md`) |
| Zugangsprüfung für HUD und SYS, Geräte mit Rolle je Modul | kein HTTPS, nicht am echten Handy geprüft |
| Fremde Dienste in der Zentrale: Qwen Code über eigene Weiterleitung, mit Prüfung davor und Start durch die Zentrale (Schritt 4) | nur Qwen Code geprüft, nicht am echten Handy; Ctrl+C-Weg nicht geprüft |

## Die Schritte

### 1. Am echten Handy ausprobieren

- **Was:** Netzschalter in der Zentrale einschalten, HUD und SYS neu starten, am Handy die angezeigte Adresse öffnen und den Code eintippen. Einmal im Heimnetz, einmal über Tailscale.
- **Warum zuerst:** Alles bisher lief im Browser dieser Maschine. Firewall, Handy-Browser und das Einklapp-Symbol am Daumen zeigen sich erst am Gerät.
- **Fertig, wenn:** Monitor und SYS am Handy laufen, und notiert ist, was hakt.
- **Braucht:** dich und dein Handy. Kein Code.

### 2. HTTPS für die Module

- **Was:** HUD, SYS und LLM nehmen ein Zertifikat aus einem festen Ordner und hören dann über HTTPS. Derselbe Rechnername, ein Zertifikat für alle Ports.
- **Warum:** Ohne HTTPS lässt sich die Zentrale auf Android und iOS nicht als App installieren, und das Anmelde-Cookie wandert im Klartext durchs Netz.
- **Fertig, wenn:** die Zentrale am Handy unter `https://…` läuft, sich installieren lässt und das Cookie nur noch über HTTPS geht (`Secure`).
- **Entscheidung:** woher das Zertifikat kommt.
  - *Empfehlung: Tailscale.* `tailscale cert` stellt ein echtes Zertifikat für den Namen der Maschine im Tailnet aus. Kein Aufwand am Handy, aber nur über Tailscale erreichbar.
  - *Eigene Zertifizierungsstelle fürs Heimnetz:* geht ohne Tailscale, dafür muss jedes Handy der Stelle einmal vertrauen.
- **Nicht** `tailscale serve` davorsetzen: dann käme jede Anfrage von dieser Maschine, und die Prüfung hielte sie für lokal (Abschnitt 4.6 im Plan).

### 3. StykkerLLM an die gemeinsame Kopplung (umgesetzt am 2026-10-13)

- **Stand:** umgesetzt. Was gebaut und geprüft ist, steht in [plan-stykkernode.md](plan-stykkernode.md), Abschnitt 10. Offen sind die Prüfungen am echten Handy, an einem zweiten PC mit einem echten StykkerHost und mit der echten `access.dat` des Besitzers.
- **Was:** `AccessControl` und `HostRegistry` lesen und schreiben `pairing.dat` statt `access.dat` und `hosts.dat`. Die alten Dateien werden einmal übernommen. Das ist Schritt L2 aus [plan-llm-modul.md](plan-llm-modul.md).
- **Warum:** Heute meldet man sich an der Zentrale an und im Frame bei LLM noch einmal. Danach gilt eine Anmeldung für alle drei.
- **Dazu gehört:** die eingebettete Darstellung für LLM (ohne eigene Marke, im Thema der Zentrale) und LLM in der Geräteliste der Zentrale.
- **Fertig, wenn:** die 585 LLM-Tests grün sind, ein vorher angemeldetes Gerät angemeldet bleibt, ein Model-Host sich ohne neues Koppeln verbindet, und ein an der Zentrale gekoppeltes Handy LLM im Frame ohne zweiten Code sieht.
- **Risiko:** der größte Eingriff in bestehenden Code. In kleinen Schritten, jeder mit den vorhandenen Tests.
- **Nicht dabei:** das Aufteilen in `StykkerLlm.Web` (L1). Es ist für die Zentrale mit Frames nicht nötig und kann warten.

### 4. Fremde Dienste in der Zentrale, Qwen Code zuerst (umgesetzt am 2026-10-14)

- **Stand:** umgesetzt im HUD-Server. Was gebaut und geprüft ist, steht in [plan-stykkernode.md](plan-stykkernode.md), Abschnitt 10. Geprüft mit `hud/tools/check-proxy.mjs` (46 von 46), nur Qwen Code, nur von dieser Maschine aus; am echten Handy und über die Windows-Firewall nicht geprüft.
- **Was:** Ein Eintrag in `stykker.json` mit `"external": true`, `port` und `target` bekommt eine eigene Weiterleitung auf einem eigenen Port (YARP 2.3.0). Sie prüft die Stykker-Kopplung und die Rolle `admin` für dieses Modul, setzt das Token des Dienstes ein und nimmt das Einbett-Verbot aus der Antwort. Antwortet auf `target` nichts, startet die Zentrale den Dienst mit `start` selbst. Der Code dafür stand als Versuch in `spikes/frame-proxy`.
- **Warum:** Qwen Code erscheint unter der Leiste der Zentrale, und das Handy braucht keinen zweiten Code.
- **Fertig, wenn:** Qwen Code am Handy im Frame läuft, ein nicht gekoppeltes Gerät und ein Zuschauer abgewiesen werden, und ein langer Antwortstrom durchläuft.
- **Entscheidung (2026-10-14):** Die Zentrale startet ihr eigenes `qwen serve` auf `127.0.0.1` mit einem Token nur für die Zentrale (`--require-auth`, das Token in der Umgebung, nie in der Kommandozeile). Die frühere Empfehlung, über Local Control zu koppeln, ist verworfen. Geprüft im Fork von Qwen Code, `docs/design/2026-08-28-trusted-loopback-full-api.md`, Abschnitt „Listener-scoped Local Control credentials“: Local Control hat einen eigenen LAN-Listener mit eigenen, widerrufbaren Kopplungs-Token und liegt nicht hinter der Stykker-Prüfung. Ob das Token bei jedem Einschalten wechselt, habe ich nicht geprüft. Die Alternative, eine Datei der Desktop-App, braucht eine Änderung im Fork und war damit nicht nötig.
- **Wo es läuft:** zunächst im HUD-Server. Später zieht es in den Knoten (Schritt 6).

### 5. Geräte bequemer koppeln

- **Was:** ein QR-Code neben dem Code, und die Kopplung andersherum (das neue Gerät zeigt sechs Ziffern, die Zentrale gibt frei). Die Bibliothek kann das schon; es fehlen die Seiten.
- **Warum:** Am Handy eine Adresse abzutippen ist der mühsamste Teil.
- **Entscheidung:** der QR-Code braucht entweder ein Paket (Rückfrage nötig) oder rund 300 Zeilen eigenen Code.

### 6. StykkerNode

- **Was:** das Programm aus Phase 4 des Plans. Es startet die Module, ist die eine Tür nach außen und gibt den Modulen einen Schlüssel mit, damit sie der weitergereichten Herkunft trauen dürfen.
- **Warum erst jetzt:** Mit Frames auf die Modul-Ports läuft die Zentrale auch ohne Knoten. Der Knoten bringt eine Adresse statt mehrerer Ports und den Neustart abgestürzter Module.
- **Entscheidung:** der Port des Knotens (vorgeschlagen 8075).

### 7. StykkerCMD im Web

- **Was:** Web-Oberfläche und Server-Version für CMD, Avalonia entfällt (entschieden am 13.10.).
- **Vorher zu klären:** welche Ordner aus dem Netz erreichbar sein dürfen. Dateizugriff ist die größte Angriffsfläche der Suite.

## Kleines, das zwischendurch geht

- `sys/README.md` und ihre deutsche Fassung beschreiben noch „nur auf 127.0.0.1“. Die Zentrale, der Netzschalter und die Rollen fehlen dort. (Für das HUD ist das nachgezogen.)
- `hud/docs/entscheidungen.md` und `cmd/PLAN.md` stehen noch auf dem Stand vor „alles Web“.
- Der Verweis „Processes“ im Monitor zeigt fest auf `127.0.0.1:8077`. In der Zentrale sollte er das Modul SYS wählen.
- HUD hat keine Tests. Die Endpunkte der Zentrale (`/api/hub…`) wären der erste Kandidat.
- Ein Service Worker, damit die installierte App ohne Netz wenigstens sagt, dass der PC nicht erreichbar ist.
- `stykker.json` kennt jetzt `modules` mit `external`, `port`, `target` und `start`. Knoten und Netzschalter aus Abschnitt 4.2 des Plans fehlen.
- Workflows im Wurzelordner mit Pfadfiltern und Tag-Releases je Werkzeug. Die Workflows unter `llm/.github` laufen in der Monorepo nicht.
- Die Einzel-Repos (`Stykker-CMD`, `-HUD`, `-LLM`) brauchen einen Verweis auf die Monorepo.

## Offen bei dir

- **Zertifikat** (Schritt 2): Tailscale oder eigene Zertifizierungsstelle.
- **Eintrag für Qwen Code** in deiner `stykker.json` (Schritt 4): Pfad zur `cli.js` und Datenordner setzt du. Das Token kommt von der Zentrale, du brauchst keins von Qwen.
- **Alte Commits bei GitHub:** Am 13.10. waren Bildschirmfotos der Prozessliste kurz auf `origin/dev`. Der Branch ist bereinigt und inzwischen gelöscht; per Hash können die Commits bei GitHub noch abrufbar sein. Endgültig entfernt sie nur der GitHub-Support.
- **NanoCut-Historie:** steckt weiter in der Historie der Monorepo. Entfernen geht nur mit Umschreiben und erzwungenem Push.
