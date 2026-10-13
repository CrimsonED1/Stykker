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

## Was der Versuch nicht zeigt

- **Keine Kopplung davor.** Der Versuch hört nur auf `127.0.0.1` und prüft niemanden. Mit Token-Einsatz wäre er im Netz eine offene Tür zu einem Coding-Agenten. Im Knoten gehört die Prüfung von Kopplung und Rolle `admin` vor die Weiterleitung.
- **Kein echter Auftrag an ein Modell.** Der Test-Daemon hat kein Modell eingerichtet. Geprüft sind Laden der Oberfläche, die Anfragen beim Start und ein WebSocket mit einer Antwort; ein langer Antwortstrom, Datei-Upload und das Terminal sind es nicht.
- **Nicht gegen die Desktop-App.** Deren Daemon hat einen zufälligen Port und ein Token, das nur die App kennt. Der Weg dorthin (Token übergeben, oder über den Local-Control-Port mit einem eigenen Geräte-Token) ist offen.
- **Nicht am Handy** und nur mit Edge.
- **Service Worker und PWA** von Qwen im Frame sind nicht geprüft.
- **Unsere eigenen Module im Frame** sind nicht geprüft. Die Annahme, dass das Geräte-Cookie über die Ports hinweg gilt, steht noch aus.

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
