# Versuch: Zentrale als Hülle mit Frame, YARP für fremde Dienste

Stand 2026-10-13 · Code: `spikes/frame-proxy` · Teil von [plan-stykkernode.md](plan-stykkernode.md), Phase 0

## Frage

Die Zentrale soll oben ihre Leiste behalten und das Modul darunter in einem Frame zeigen, auch fremde Web-Dienste. Qwen Code ist der Testfall, weil es das Einbetten verbietet. Reicht ein Frame, und wenn nicht: reicht YARP davor?

## Ergebnis

**Ein Frame allein reicht für Qwen nicht. Mit YARP davor geht es, einschließlich WebSocket, und der Browser braucht dann kein Qwen-Token mehr.**

| Fall | Ergebnis |
|---|---|
| Frame direkt auf den Qwen-Daemon | Vom Browser blockiert: `frame-ancestors 'none'`. |
| Frame über YARP, Token kommt vom Browser (`#token=`) | Oberfläche lädt, WebSocket `/acp` geht auf und antwortet. |
| Frame über YARP, YARP setzt das Token ein, Browser hat keins | Oberfläche lädt ohne Anmeldeseite, WebSocket geht auf und antwortet. |
| Frame über YARP, kein Token von niemandem | Qwen zeigt seine Anmeldeseite (401). So soll es sein. |
| Andere Seite ruft die Weiterleitung auf (anderer Origin) | `fetch` und WebSocket abgewiesen. |

Geprüft mit Edge über Playwright gegen einen Test-Daemon aus dem Fork (`qwen serve`, eigener Datenordner, Token gesetzt), alles auf `127.0.0.1`. Bilder: `design/screenshots/frame-direct.png` und `frame-proxy-injected-token.png`.

## Was die Weiterleitung tun musste

Vier Eingriffe, alle in `FrameTransformer` (`spikes/frame-proxy/Program.cs`):

1. **Antwort:** `X-Frame-Options` entfernen und in der `Content-Security-Policy` `frame-ancestors 'none'` durch die Adresse der Zentrale ersetzen. Das Verbot gilt damit weiter, nur nicht mehr für die Zentrale.
2. **`Host`:** den des Ziels senden. Qwen lässt nur seinen eigenen Host-Namen zu.
3. **`Origin`:** auf den des Ziels umschreiben. Die Prüfung, die Qwen damit nicht mehr selbst machen kann, steht in der Weiterleitung: ein fremder Origin bekommt 403.
4. **Token:** `Authorization: Bearer …` einsetzen, wenn der Browser keins mitbringt. Das gilt auch für die Upgrade-Anfrage des WebSockets; Qwen nimmt den Kopf dort an.

Qwen lässt das Einbetten per Einstellung nur für Browser-Erweiterungen zu (`chrome-extension://`, `moz-extension://`), nicht für eine Webseite. Ohne Eingriff 1 ginge es also nur mit einer Änderung im Fork.

## Was das für den Plan heißt

- **Entscheidung F ist gefallen:** Frames zuerst. YARP kommt dazu, wo ein Frame allein nicht reicht – für fremde Dienste und später als einzige Tür des Knotens. Das Paket `Yarp.ReverseProxy` 2.3.0 ist freigegeben.
- **Ein Port je fremdem Dienst, kein Pfad.** Die Qwen-Oberfläche liegt an der Wurzel ihrer Adresse. Im Versuch hat die Weiterleitung einen eigenen Port; unter `/qwen` ist es nicht versucht.
- **Token-Einsatz macht die doppelte Kopplung überflüssig.** Prüft der Knoten vor der Weiterleitung die Stykker-Kopplung und setzt dann das Token ein, braucht das Handy keinen QR-Code von Qwen mehr.

## Eigene Module im Frame

Zweiter Teil, selber Tag: StykkerHUD, StykkerSYS und StykkerLLM (im Simulator) direkt im Frame der Hülle, ohne Weiterleitung.

| Fall | Ergebnis |
|---|---|
| HUD und SYS im Frame | Laden und zeigen Werte. Sie senden kein Einbett-Verbot. |
| LLM im Frame, unverändert | Blockiert: Blazor Server setzt von sich aus `frame-ancestors 'self'`, und die Hülle auf einem anderen Port ist für den Browser eine andere Herkunft. |
| LLM im Frame, mit `--hub-port` | Lädt, die Blazor-Verbindung (`/_blazor`) steht. |
| Gerät an LLM `:8090` angemeldet, Frame zeigt eine zweite LLM-Instanz auf `:8091` mit derselben Geräteliste | Ohne neue Anmeldung drin. Das Cookie gilt für den Rechnernamen, nicht für den Port, und es wird im Frame mitgeschickt. |
| Dasselbe mit einem Gerät ohne Anmeldung | Die Anmeldeseite erscheint im Frame. |
| Hülle unter einem anderen Rechnernamen als das Modul | Blockiert (und ohne das Verbot käme die Anmeldeseite: der Browser gibt das Cookie dann nicht in den Frame). |

Daraus folgt:

- **`--hub-port` in StykkerLLM** (neu, `llm/src/StykkerLlm.Core/Server/FramePolicy.cs`): erlaubt genau die Zentrale unter demselben Rechnernamen und diesem Port. Ohne den Schalter bleibt alles wie bisher. Der Port kommt später aus `stykker.json`.
- **Hülle und Module müssen unter demselben Rechnernamen aufgerufen werden.** Sonst gilt die Anmeldung im Frame nicht.
- **Die gemeinsame Geräteliste trägt die Anmeldung über die Module.** Das war die Annahme hinter „ein neues Modul sieht die vorhandene Kopplung“; mit zwei LLM-Instanzen und kopierter `access.dat` ist sie im Browser bestätigt.
- **Doppelte Kopfzeile:** Im Frame steht unter der Leiste der Zentrale die eigene Kopfzeile des Moduls mit Marke und Thema. Ein Modul braucht eine eingebettete Darstellung ohne Marke, die dem Thema der Zentrale folgt. Im Mockup ist das so gezeichnet, gebaut ist es nicht.

Bilder: `design/screenshots/frame-hud.png`, `frame-llm.png`, `frame-llm-other-port.png`. Von SYS im Frame gibt es bewusst kein Bild im Repo: es zeigt die Prozesse der Maschine.

HUD und SYS prüfen noch keine Kopplung und hören nur auf `127.0.0.1`. Im Netz lassen sie sich so erst zeigen, wenn sie die gemeinsame Prüfung haben (Phasen 5 und 6) oder der Knoten davor steht.

## Was der Versuch nicht zeigt

- **Keine Kopplung davor.** Der Versuch hört nur auf `127.0.0.1` und prüft niemanden. Mit Token-Einsatz wäre er im Netz eine offene Tür zu einem Coding-Agenten. Im Knoten gehört die Prüfung von Kopplung und Rolle `admin` vor die Weiterleitung. Die Prüfung davor steht seit 2026-10-14 im HUD-Server (`hud/src/StykkerHud.Server/ServiceForwarding.cs`), dieser Versuch prüft aber weiterhin niemanden.
- **Kein echter Auftrag an ein Modell.** Der Test-Daemon hat kein Modell eingerichtet. Geprüft sind Laden der Oberfläche, die Anfragen beim Start und ein WebSocket mit einer Antwort; ein langer Antwortstrom, Datei-Upload und das Terminal sind es nicht.
- **Nicht gegen die Desktop-App.** Deren Daemon hat einen zufälligen Port und ein Token, das nur die App kennt. Der Weg dorthin (Token übergeben, oder über den Local-Control-Port mit einem eigenen Geräte-Token) ist offen.
- **Nicht am Handy** und nur mit Edge.
- **Service Worker und PWA** von Qwen im Frame sind nicht geprüft.
- **Aktionen im Frame** (ein Modell stoppen, einen Prozess beenden) sind nicht ausgelöst worden, nur das Laden und die laufende Anzeige.

## Nachstellen

```powershell
# 1. ein Qwen-Daemon zum Testen (eigener Datenordner, damit nichts Echtes berührt wird)
$env:QWEN_HOME = "$env:TEMP\qwen-spike"; $env:QWEN_SERVER_TOKEN = "frame-spike-token"
node <fork>\dist\cli.js serve --port 41999 --hostname 127.0.0.1 --no-open

# 2. Hülle auf 8075, Weiterleitung auf 8076, Token wird eingesetzt
dotnet run --project spikes\frame-proxy -- --target http://127.0.0.1:41999 --token frame-spike-token

# 3. http://127.0.0.1:8075/            Qwen über die Weiterleitung
#    http://127.0.0.1:8075/?src=direct  zum Vergleich direkt (bleibt leer)
```
