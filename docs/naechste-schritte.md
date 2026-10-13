# Was als Nächstes kommt

Stand 2026-10-13 · gehört zu [plan-stykkernode.md](plan-stykkernode.md) (das Zielbild und was gebaut ist) · Bild: [architektur.svg](architektur.svg) · Mockup: [design/mockup-zentrale.html](design/mockup-zentrale.html)

Die Reihenfolge folgt einem Ziel: **die Zentrale als App auf dem Handy, mit einer Anmeldung für alles.** Jeder Schritt ist für sich fertig und prüfbar. Was eine Entscheidung braucht, steht dabei.

## Wo wir stehen

| Gebaut und geprüft | Fehlt dafür noch |
|---|---|
| Gemeinsame Kopplung (`Stykker.Pairing`), eine Ablage für HUD, SYS und LLM | Prüfung am echten Handy und an einem zweiten PC (Schritt 1, Abnahme 3 in [plan-llm-modul.md](plan-llm-modul.md)) |
| Zentrale im HUD unter `/hub`: Leiste, Übersicht, Module im Frame, einklappbar | Qwen Code und andere fremde Dienste sind nicht eingetragen |
| Zugangsprüfung für HUD und SYS, Geräte mit Rolle je Modul | kein HTTPS, nicht am echten Handy geprüft |
| Versuch: fremde Dienste über YARP einbetten (Qwen Code) | nur ein Versuch unter `spikes/`, keine Prüfung davor |

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

### 4. Fremde Dienste in der Zentrale, Qwen Code zuerst

- **Was:** Ein Eintrag in `stykker.json` mit `"external": true` bekommt eine eigene Weiterleitung auf einem eigenen Port. Sie prüft die Stykker-Kopplung und die Rolle `admin`, setzt dann das Token des Dienstes ein und nimmt das Einbett-Verbot aus der Antwort. Der Code dafür steht als Versuch in `spikes/frame-proxy`.
- **Warum:** Qwen Code erscheint unter der Leiste der Zentrale, und das Handy braucht keinen zweiten Code.
- **Fertig, wenn:** Qwen Code am Handy im Frame läuft, ein nicht gekoppeltes Gerät und ein Zuschauer abgewiesen werden, und ein langer Antwortstrom durchläuft.
- **Entscheidung:** wie die Zentrale an das Token von Qwen kommt.
  - *Empfehlung:* über Local Control von Qwen. Die Zentrale koppelt sich dort einmal als Gerät und hält ihr eigenes Token. Das nutzt, was im Fork schon da ist.
  - *Alternative:* die Desktop-App schreibt ihr Daemon-Token in eine Datei, die die Zentrale liest. Braucht eine Änderung im Fork.
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

- `hud/README.md`, `sys/README.md` und ihre deutschen Fassungen beschreiben noch „nur auf 127.0.0.1“. Die Zentrale, der Netzschalter und die Rollen fehlen dort.
- `hud/docs/entscheidungen.md` und `cmd/PLAN.md` stehen noch auf dem Stand vor „alles Web“.
- Der Verweis „Processes“ im Monitor zeigt fest auf `127.0.0.1:8077`. In der Zentrale sollte er das Modul SYS wählen.
- HUD hat keine Tests. Die Endpunkte der Zentrale (`/api/hub…`) wären der erste Kandidat.
- Ein Service Worker, damit die installierte App ohne Netz wenigstens sagt, dass der PC nicht erreichbar ist.
- `stykker.json` kennt nur `modules`. Knoten, Ports und der Netzschalter aus Abschnitt 4.2 des Plans fehlen.
- Workflows im Wurzelordner mit Pfadfiltern und Tag-Releases je Werkzeug. Die Workflows unter `llm/.github` laufen in der Monorepo nicht.
- Die Einzel-Repos (`Stykker-CMD`, `-HUD`, `-LLM`) brauchen einen Verweis auf die Monorepo.

## Offen bei dir

- **Zertifikat** (Schritt 2): Tailscale oder eigene Zertifizierungsstelle.
- **Token von Qwen** (Schritt 4): über Local Control oder über eine Datei der Desktop-App.
- **Alte Commits bei GitHub:** Am 13.10. waren Bildschirmfotos der Prozessliste kurz auf `origin/dev`. Der Branch ist bereinigt und inzwischen gelöscht; per Hash können die Commits bei GitHub noch abrufbar sein. Endgültig entfernt sie nur der GitHub-Support.
- **NanoCut-Historie:** steckt weiter in der Historie der Monorepo. Entfernen geht nur mit Umschreiben und erzwungenem Push.
