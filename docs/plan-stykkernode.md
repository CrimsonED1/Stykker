# Plan: StykkerNode, zentrale Kopplung und Zentrale

Stand 2026-10-13 · **Phase 1 umgesetzt** (`shared/src/Stykker.Pairing`, Abschnitt 10), **StykkerLLM an der Kopplung umgesetzt** (L2, Abschnitt 10), alles andere Entwurf · Bild: [architektur.svg](architektur.svg) · Was als Nächstes kommt: [naechste-schritte.md](naechste-schritte.md)

## 1. Anforderungen

Aus deiner Nachricht vom 2026-10-10, in Prüfbares übersetzt:

1. Jede App hat eine **Web-UI** und eine **Server-Version**. Die Server-Version ist das Programm, die Web-UI ist seine Oberfläche.
2. **Kopplung zentral**: ein Mechanismus, eine Ablage, eine Geräteliste für alle Module.
3. **Konfiguration zentral**: eine Stelle für Module, Netz und Knoten.
4. Ein **neues Modul sieht die vorhandene Kopplung** und koppelt nicht neu.
5. **HUD ist die Zentrale**: von dort erreicht man alle Module.
6. **StykkerNode lädt alle Module.**
7. **StykkerLLM ist das erste Modul.**

Dazu gilt weiter: Jede App bleibt **eigenständig** lauffähig.

Name: du schreibst „stykkernode“. Vorschlag: `StykkerNode`, geschrieben wie die anderen Werkzeuge (so wie bei StykkerSYS). Im Text heißt es „der Knoten“.

## 2. Begriffe

| Begriff | Bedeutung |
|---|---|
| **Modul** | Ein Werkzeug der Suite: LLM, SYS, HUD, CMD. Aus drei Teilen: *Core* (Logik, ohne Oberfläche), *Web* (Razor-Seiten), *Server* (Programm, das Web und API hostet). |
| **Server-Version** | Das eigenständige Programm eines Moduls, z. B. `StykkerLLM-Server.exe`. Läuft allein oder unter dem Knoten. |
| **Kopplung** | Geräte, Zugangscode, Rollen. Eine Ablage für alle Module: `pairing.dat`. |
| **Konfiguration** | `stykker.json`: Module, Ports, Netzschalter. Ohne Geheimnisse. |
| **StykkerNode** | Programm, das Module startet, den Zugang nach außen bündelt und die Kopplung hält. |
| **Zentrale** | Startseite im HUD: Module, Status, Geräte. |

## 3. Ist-Stand

Aus dem Code geprüft am 2026-10-10.

| Werkzeug | Web-UI | Server-Version | Kopplung heute | Erreichbar heute |
|---|---|---|---|---|
| StykkerLLM | ja (Razor) | ja, `StykkerLLM-Server`, Port 8078 | gemeinsam: `pairing.dat` (Geräte, Code, Rollen, Hosts; seit L2) | Netz nur mit Schalter Home/VPN |
| StykkerSYS | ja | ja, Port 8077 | keine | nur 127.0.0.1 |
| StykkerHUD | ja | ja, Port 8079 | keine | nur 127.0.0.1 |
| StykkerCMD | nein (Avalonia) | nein | keine | nur das Fenster |

Was der Code zeigt:

- **LLM hat schon eine vollständige Kopplung.** Sechs Ziffern, zehn Minuten, einmal gültig, nach fünf Fehlversuchen ein neuer Code. Geräte nur als Hash mit Präfix `StykkerLLM.device.v1|` (`Core/Server/AccessControl.cs`). Hosts haben einen zweiten Code und Token-Hashes mit derselben Funktion (`Core/Host/HostPairing.cs`, `HostRegistry.cs`). Die Logik ist also da. Sie muss nur aus LLM heraus.
- **Der Zugang prüft die Herkunft** über `loopback` und `IsLocal` (`Server/AccessGate.cs`, `/api/metrics` in `Server/Program.cs`). Eine Weiterleitung über einen Knoten würde diese Prüfungen verändern (Abschnitt 4.6).
- **SYS und HUD** prüfen die Herkunft über `LocalRequests.SameOrigin`: `Origin` muss `Schema://Host` der Anfrage entsprechen (`shared/src/Stykker.Shared/Web/LocalRequests.cs`).
- **`Server/Program.cs` hat 355 Zeilen.** Dienste, Zugang, Host-Endpunkte, Auto-Stopp, Tray, Entdeckung und Browserstart stehen in einer Datei. Das ist der größte Umbau in LLM.
- **Entdeckung:** UDP 17501, Antwort mit `app: StykkerLLM-Server` (`Core/Server/NodeDiscovery.cs`).

## 4. Zielbild

Die Skizze steht in [architektur.svg](architektur.svg).

### 4.1 Kopplung (zentral)

- Neue Bibliothek `shared/src/Stykker.Pairing`. Vier Werkzeuge brauchen sie, also erfüllt sie die Regel für `shared/` (nur, was zwei Werkzeuge brauchen).
- Inhalt wie bei LLM heute: Zugangscode, Anmeldung mit dem Code oder Freigabe durch ein angemeldetes Gerät, Geräte, Host als Gerätetyp.
- Neu: **Rolle je Gerät und Modul**, z. B. `llm: admin`, `sys: viewer`.
- Ablage `%APPDATA%\Stykker\pairing.dat`, geschützt mit DPAPI (Windows-Benutzer). Für den Dienst später `%ProgramData%\Stykker\` (Abschnitt 4.7).
- **Schreiben aus mehreren Prozessen** (Module, Knoten, Fenster): benannter Mutex plus atomare Datei. Lesen prüft die Änderungszeit.
- **Kompatibilität:** Hash-Verfahren und Präfix `StykkerLLM.device.v1|` bleiben, der Cookie-Name bleibt. Sonst sind alle Geräte abgemeldet und alle Hosts getrennt.

### 4.2 Konfiguration (zentral)

- `%APPDATA%\Stykker\stykker.json`, lesbar, ohne Geheimnisse:
  - `node`: Name, Port, Netzschalter (`remote`).
  - `modules`: je Modul `id`, `program`, `port` (intern), `dataDir`, `enabled`.
- Ohne Datei verhält sich jedes Programm wie heute.
- Startargumente haben Vorrang (wie `--port` heute).
- Der Netzschalter wandert aus `access.dat` in diese Datei. Er gilt dann für alle Module.

### 4.3 Modul-Vertrag

Jedes Modul spricht dieselbe HTTP-Sprache. Der Knoten muss dafür keine C#-Details kennen.

- `GET /api/ping`: gibt es bei LLM schon. Ergänzt um `module` (Id), `version` und `gate` (z. B. `pairing-v1`). Nur hinzufügen, nichts umbenennen.
- `GET /api/module`: neu. Name, Version und Kachel-Status für die Zentrale. Ab Rolle `viewer`.
- **Der Knoten leitet nur an Module weiter, die `gate: pairing-v1` melden.** SYS und HUD haben heute keine Kopplung. HUD bekommt die Prüfung in Phase 5, SYS erst in Phase 6. Bis dahin bleibt SYS hinter dem Knoten gesperrt.
- Startschalter bleiben gleich: `--port`, `--data-dir`, `--no-browser`, `--no-tray`, `--stay`. Das Fenster startet den LLM-Server heute schon so. Der Knoten macht dasselbe.

### 4.4 StykkerNode: wie er Module lädt

Die Art des Ladens ist **Entscheidung A** (Abschnitt 6).

**Empfehlung: eigene Prozesse mit Weiterleitung.**

- StykkerNode startet jede Server-Version als eigenen Prozess auf 127.0.0.1, und zwar nur, was in `stykker.json` steht. Er beendet nur Prozesse, die er selbst gestartet hat (AGENTS-Regel 8 in `llm/AGENTS.md`).
- Er leitet `/llm/…`, `/sys/…`, `/hud/…` über seinen Port weiter. Die Module sind von außen nicht erreichbar.
- Gründe:
  1. LLM ist ein Dauerdienst mit eigener Lebensdauer und eigenen Verbindungen (Proxy 17500 für Claude Code, Host-Verbindungen). Ein Absturz oder Neustart eines Moduls soll die anderen nicht mitreißen.
  2. Jedes Modul bleibt das Programm, das es allein schon ist. Das bedeutet weniger Umbau und weniger Risiko.
  3. Ein Modul lässt sich neu starten, ohne alle zu stoppen.
- Kosten: Die Weiterleitung braucht eine Bibliothek (YARP ist ein NuGet-Paket und braucht deine Freigabe) oder eine eigene Weiterleitung. WebSockets müssen durch (Blazor `/_blazor`, Hosts `/hosts/connect`). Abschnitt 4.6 gilt.

**Alternative: alle Module in einem Prozess.**

- Ein Blazor-Server, Module tragen Seiten bei. Ein Port, eine Shell.
- Offen und ungeprüft: mehrere Modul-Routen nebeneinander, Pfade der Assets (`_content/…`), Lebensdauer, und ein Absturz reißt alle Module mit. Ein Modul einzeln neu zu starten ist nicht vorgesehen.
- Nur wählen, wenn der Spike (Phase 0) das sauber zeigt.

### 4.5 Zentrale (HUD)

- Startseite: eine Kachel je Modul mit Name, Status und Link. Der Status kommt aus `/api/module`.
- Geräteverwaltung an einer Stelle für alle Module, mit Rollen je Modul.
- Knotenstatus: läuft er, welche Module laufen, Neustart eines einzelnen Moduls.
- Der HUD-Monitor (Prozessor, Speicher, GPU) bleibt als Modul erhalten.
- Das HUD-Fenster startet den Knoten, wenn keiner läuft. Dasselbe Muster, das das LLM-Fenster schon nutzt.
- Oberflächentext englisch, Doku deutsch (wie bisher).
- **Die Kopplungsseite gibt es in jedem Modul**, damit ein Modul allein koppeln kann. Die Zentrale zeigt sie zusätzlich. Eine Ablage, ein Code, eine Logik, mehrere Seiten.

### 4.6 Zugang über den Knoten

Eine Weiterleitung verändert, was die Module über den Absender wissen. Das muss gelöst sein, bevor ein Modul über den Knoten erreichbar ist.

| Problem (aus dem Code) | Folge ohne Gegenmaßnahme | Maßnahme |
|---|---|---|
| `loopback` und `IsLocal` (`AccessGate`, `/api/metrics`) werten die Verbindungsadresse aus. Der Knoten leitet von 127.0.0.1 aus weiter. | Jede weitergeleitete Anfrage gilt als lokal. Der Zugangscode erscheint auf der Anmeldeseite für fremde Geräte. `/api/metrics` ist ohne Anmeldung erreichbar. Die Prüfung „von außen nur mit Schalter“ greift nicht. | Der Knoten prüft Schalter, Kopplung und Rolle zuerst. Das Modul wertet die weitergereichte Herkunft (`X-Forwarded-For`) nur mit gültigem Knotenschlüssel aus. Sonst zählt die Verbindungsadresse. |
| `LocalRequests.SameOrigin` vergleicht `Origin` mit dem `Host` der Anfrage. | Bei jeder Aktion in SYS und HUD 403, wenn der Knoten den `Host` umschreibt. | Der Knoten reicht `Host` unverändert weiter. Test im Spike. |
| Module hören heute auf allen Adressen (LLM: `0.0.0.0:8078`). | Ein Modul ließe sich am Knoten vorbei erreichen. | Im Knotenbetrieb nur 127.0.0.1. Der Knoten ist die einzige Tür. |

Weitere Regeln:

- **Knotenschlüssel:** neu bei jedem Start, übergeben über Standardeingabe oder Umgebungsvariable, nicht in der Kommandozeile (dort sieht ihn jeder Prozess desselben Benutzers).
- **SYS:** Prozesse beenden und Priorität ändern sind heute nur lokal erreichbar. Über den Knoten erreichbar wären sie, sobald der Schalter an ist. Darum nur Rolle `admin`, und neue Module starten mit `viewer` (Entscheidung C).
- **Gerätecookie:** gilt heute zwei Jahre (`SetCookie` in `AccessGate.cs`). Für ein Gerät mit Zugriff auf alle Module ist das lang. Ausgleich: Widerruf an einer Stelle in der Zentrale. Die Laufzeit entscheiden wir später.
- **Proxy 17500** (Claude Code, Qwen Code) ist kein Web-Pfad und wird nicht über den Knoten geleitet. Er bleibt am LLM-Modul. Sein Netzverhalten wird im Umbau geprüft.

### 4.7 Lebensdauer und Dauerbetrieb

- Der Knoten ist der Dauerbetrieb (Entscheidung D). Er läuft, solange er gewollt ist, etwa per Autostart, unabhängig von Fenstern.
- Module im Knoten haben keinen Auto-Stopp. Der Auto-Stopp über `ServerHolds` gilt nur eigenständig.
- Als Windows-Dienst (Phase 8): Die Kopplung braucht dann Schutz auf Rechnerebene (`LocalMachine`), weil das Dienstkonto die DPAPI des Benutzers nicht lesen kann. Das ist dasselbe Problem wie beim StykkerHost-Dienst (`llm/docs/hosts.md`).

### 4.8 CMD

- CMD bekommt Web-UI und Server-Version wie alle Module. `Core` und `Platform.*` bleiben.
- Avalonia entfällt für CMD, wenn du Entscheidung E bestätigst. Die offene Frage Blazor oder Avalonia (`hud/docs/entscheidungen.md`) ist damit beantwortet: Die Oberfläche ist Web, Photino ist nur die Hülle.
- Dateizugriff im Web ist die größte Angriffsfläche der Suite. Vorschlag: nur Rolle `admin`, nur gekoppelte Geräte, nur freigegebene Wurzelordner. Das muss noch präzisiert werden.

## 5. Phasen

| Phase | Inhalt | Prüfung und Ergebnis |
|---|---|---|
| **0** | Entscheidungen A bis G (Abschnitt 6). Spike: ein Blazor-Modul (LLM) hinter einem Pfadpräfix. Seiten und `/_blazor` laufen durch den Weiterleiter. `/hosts/connect` mit Kopf `X-Stykker-Host`. `Host` und `Origin` bleiben unverändert. Knotenschlüssel. Die Code-Seite zeigt fremder Herkunft keinen Code. `/api/metrics` ist ohne Anmeldung nicht erreichbar. | Spike-Notiz in `docs/`, Browser-Prüfung wie bei SYS. Entscheidung A bestätigt. |
| **1** ✓ | `shared/src/Stykker.Pairing`: Ablage, Code, Geräte, Rollen, Anfragen, Gerätetyp Host, Sperre über Prozesse. | Unit-Tests. Zwei-Prozess-Test der Sperre. Migrationstest mit einer echten `access.dat` aus der Vorversion. |
| **2** | `stykker.json` lesen. Vorgaben ohne Datei. | Tests. Ohne Datei startet LLM wie heute. |
| **3** | LLM als erstes Modul (eigener Plan: [plan-llm-modul.md](plan-llm-modul.md)). | Alle LLM-Tests grün, 0 Warnungen. Altes Gerät bleibt angemeldet. Altes Host-Gerät verbindet sich weiter. Ein zweiter Prozess mit `Stykker.Pairing` sieht das LLM-Gerät ohne neues Koppeln. |
| **4** | `StykkerNode`: startet Module als Prozesse, Weiterleitung, Netzschalter, Entdeckung, Tray. | Tests für Herkunft und Schlüssel. Zugriff von außen ohne Schalter wird am Knoten abgewiesen. Stürzt ein Modul ab, startet der Knoten es neu, die anderen laufen weiter. |
| **5** | HUD als Zentrale: Startseite, Geräte, Knotenstatus, gemeinsame Prüfung (`gate: pairing-v1`). Das HUD-Fenster startet den Knoten. | Browser-Prüfung mit Screenshot: LLM und HUD als Kacheln. SYS als „noch nicht angebunden“. |
| **6** | SYS auf gemeinsame Prüfung und Rollen. SYS-Aktionen nur für `admin`. | Tests für Rollen. SYS erscheint als Kachel. |
| **7** | CMD: Web-UI und Server-Version (nach E). | Core-Tests bleiben grün. |
| **8** | Dauerbetrieb als Dienst (optional). | Installation auf einem Testrechner. |

Phase 1 kommt vor Phase 3. Phase 0 kommt vor Phase 4. Die Phasen 5 bis 7 sind unabhängig voneinander, sobald Phase 4 steht.

## 6. Entscheidungen

Bitte kurz antworten. Die Empfehlung steht jeweils in der ersten Option.

| # | Frage | Empfehlung | Warum |
|---|---|---|---|
| **A** | Module im Knoten: eigene Prozesse mit Weiterleitung, oder alle in einem Prozess? | Eigene Prozesse | Dauerdienste, Isolation, Neustart einzeln. Module bleiben eigenständig (4.4). |
| **B** | Konfiguration: eine Datei oder zwei? | Zwei: `stykker.json` (lesbar) und `pairing.dat` (DPAPI), im selben Ordner | Geräte haben Namen und Adressen. Die Konfiguration soll lesbar bleiben. |
| **C** | Rollen je Modul? Vorgabe für neue Module? | Ja. Neue Module starten mit `viewer`. SYS-Aktionen nur mit `admin` | Ein gekoppeltes Gerät darf nicht automatisch Prozesse beenden. |
| **D** | StykkerNode als eigener Dauerbetrieb-Prozess, die Fenster nur als Gäste? | Ja | Der LLM-Proxy (17500) darf nicht vom HUD-Fenster abhängen. |
| **E** | CMD auf Web-UI umstellen und Avalonia aufgeben? | Ja | Eine Oberfläche für alle Module. Die Frage Blazor oder Avalonia ist damit beantwortet. |
| **F** | Weiterleitung: YARP (NuGet, braucht Freigabe) oder eigene Weiterleitung? | YARP, wenn du zustimmst | Repo-Regel: keine neue Abhängigkeit ohne Freigabe. |
| **G** | Knoten-Port | 8075, vorläufig, noch nicht auf Freiheit geprüft | Vor Phase 4 prüfen. |

Zusätzlich zu bestätigen, ohne Frage: Name `StykkerNode`, Konfiguration `stykker.json`, Ablage `%APPDATA%\Stykker\`.

## 7. Was sich nicht ändert

- Host-Protokoll (`/hosts/connect`, Kopf `X-Stykker-Host`, Nachrichtenformat). Installierte StykkerHost-Versionen müssen weiter funktionieren.
- Cookie-Name, Hash-Verfahren, Rollennamen.
- Ports im eigenständigen Betrieb: LLM 8078 und 17500, SYS 8077, HUD 8079.
- Jede App startet allein und ohne Knoten.
- Keine neue NuGet-Abhängigkeit ohne Freigabe. Die Regeln in `llm/AGENTS.md` gelten im Modul weiter.
- Keine Commits und Pushes ohne Auftrag.

## 8. Risiken

- **Herkunft und Host-Kopf** (4.6): Spike und Tests vor allem anderen.
- **Blazor mit Pfadpräfix:** Seiten, Skripte, Assets und Verbindungen können brechen. Erst der Spike, dann die Browser-Prüfung mit Screenshots.
- **Eine Datei, mehrere Schreiber:** Sperre und Änderungszeit (4.1). Eigener Test.
- **Eine Kopplung für alle:** Ein gestohlenes Gerät hat Zugriff auf alle Module. Ausgleich: Rollen je Modul, Widerruf in der Zentrale.
- **Dienstkonto und DPAPI** (Phase 8).
- **Umfang:** LLM hat rund 300 Quelldateien. Der Umbau wird in kleine, einzeln geprüfte Schritte geschnitten.

## 9. Doku, die danach mit angepasst wird

- `hud/docs/entscheidungen.md`: Entscheidung E festhalten.
- `llm/docs/hosts.md`: Kopplung ist dann zentral, die Anleitung passt sich an.
- `llm/src/StykkerLlm.Server/Program.cs`, Zeile 291: der Verweis auf `docs/nodes.md` ist veraltet, weil der alte Knoten-Code in P7 entfernt wurde.
- `cmd/PLAN.md`: steht noch auf „nichts gebaut“.

## 10. Stand der Umsetzung

### Phase 1: `Stykker.Pairing` (2026-10-13)

Seit der Zugangsprüfung vom selben Tag nutzen HUD und SYS die Bibliothek, seit L2 (unten) auch StykkerLLM.

| Datei | Inhalt |
|---|---|
| `PairingStore.cs` | Die Ablage `pairing.dat`: benannter Mutex je Datei, frisches Lesen vor jedem Schreiben, atomares Ersetzen, Neuladen bei geänderter Zeit oder Länge. Eine unlesbare Datei wird vor dem Überschreiben als `pairing.dat.unreadable-<Zeit>` beiseitegelegt. |
| `PairingService.cs` | Die Regeln aus `AccessControl`, `HostPairing` und `HostRegistry`: Code, Anmeldung in beide Richtungen, Geräte, Rollen je Modul, Hosts. |
| `PairingCodes.cs` | Code, Token, Hash. Verfahren und Präfix `StykkerLLM.device.v1\|` unverändert, durch einen Test mit festem Wert gesichert. |
| `DataProtection.cs` | DPAPI des Benutzers über `crypt32.dll` (kein NuGet-Paket), Zusatzwert `Stykker.pairing.v1`. Ohne Windows Klartext. |
| `LegacyLlmImport.cs` | Liest `access.dat` und `hosts.dat` aus dem Datenordner von StykkerLLM (mit dessen Zusatzwert) und übernimmt Geräte, Rollen, Code, Netzschalter und Hosts. Die alten Dateien werden nur gelesen. |

Prüfung: 42 Tests in `shared/tests/Stykker.Pairing.Tests`, darunter die drei aus der Phasentabelle – drei Prozesse schreiben gleichzeitig je 40 Geräte und keines geht verloren, ein Gerät aus einer alten `access.dat` meldet sich mit seinem alten Cookie an, und ein zweiter Prozess sieht ein woanders gekoppeltes Gerät als `viewer`. Build ohne Warnungen.

Dabei entschieden, weil die Bibliothek es brauchte (Abschnitt 6, jeweils die Empfehlung):

- **B:** `pairing.dat` ist eine eigene, geschützte Datei.
- **C:** Rollen je Modul. Wer sich an einem Modul anmeldet, ist dort `admin` und überall sonst `viewer`.

Von dir entschieden am 2026-10-13:

- **E: ja, alles Web.** CMD bekommt Web-UI und Server-Version, Avalonia entfällt. Die Frage Blazor oder Avalonia aus `hud/docs/entscheidungen.md` ist damit beantwortet.
- **Gearbeitet wird auf `main`.** Der Branch `dev` vom 13.10. ist am selben Tag nach `main` gemergt; er muss nicht weitergeführt werden (siehe `AGENTS.md` im Wurzelordner).
- **F: Frames zuerst, YARP zusätzlich.** Die Zentrale ist eine Hülle: Leiste oben, das Modul im Frame darunter, für eigene Module wie für fremde Dienste. YARP (`Yarp.ReverseProxy`, freigegeben) kommt dazu, wo ein Frame allein nicht reicht, und später als einzige Tür des Knotens.
- **Qwen Code ist der Testfall für fremde Dienste**, weil es das Einbetten verbietet. Der Versuch dazu ist gelaufen: [spike-frame-proxy.md](spike-frame-proxy.md). Mit YARP davor lässt sich Qwen einbetten, und das Handy braucht dann kein Qwen-Token mehr.
- **Fremde Dienste als externe Module:** ein Eintrag in `stykker.json`, sichtbar nur für Geräte mit Rolle `admin` für diesen Eintrag.

Über den Plan hinaus:

- **Fehlversuche stehen in der Ablage**, nicht im Speicher. Sonst hätte jeder Prozess seine eigenen fünf Versuche.
- **Freigabe aus einem anderen Prozess** (Zentrale gibt frei, Modul hat die Anfrage angenommen): Die Anfrage steht mit zwei Hashes in der Ablage. Das Token hält nur der annehmende Prozess im Speicher; startet er neu, verfällt die Anfrage.
- **Einmalige Übernahme:** `imported` merkt sich die übernommenen Quellen. Ein zentral entferntes Gerät kommt nicht aus der alten Datei zurück.

### Zentrale im HUD, erster Teil (2026-10-13)

Vorgezogen aus Phase 5, zusammen mit dem Teil von Phase 2, den sie braucht.

| Wo | Was |
|---|---|
| `shared/src/Stykker.Shared/Config/HubModules.cs` | Liest die Module aus `%APPDATA%\Stykker\stykker.json`. Ohne Datei: Monitor, SYS (8077), LLM (8078). Eigene Module hängen am Rechnernamen der Anfrage, ein fremder Dienst an seiner Adresse. |
| `hud/…/wwwroot/hub.html`, `hub.js`, `hub.css` | Die Hülle unter `/hub`: Leiste, Übersicht mit einer Kachel je Modul, das gewählte Modul im Frame. Die Leiste klappt ein; am Handy und als installierte App ist sie zu und öffnet sich über ein Symbol als Menü. |
| `hud/…/Program.cs` | `/hub`, `/api/hub` (Module und ob ihr Port antwortet), `/manifest.webmanifest`. |
| `hud`, `sys`: `App.razor`, `app.css` | Eingebettete Darstellung: `?embed=1` nimmt Marke und Themenwahl weg, `?theme=` folgt dem Thema der Zentrale. |
| `llm`: `--hub-port` | Erlaubt der Zentrale das Einbetten (siehe [spike-frame-proxy.md](spike-frame-proxy.md)). |
| `shared/…/Web/DesignSystemHost.cs` | Die Vorgabe für das Design-System wird vom Programmordner aufwärts gesucht. Der feste Pfad `C:\_AI\…` stimmte seit dem Umzug der Repo nicht mehr: HUD und SYS liefen ohne ihre Stile. |

Geprüft im Browser (Edge über Playwright, alles auf `127.0.0.1`): Übersicht mit drei Kacheln, Monitor, SYS und LLM im Frame, Themenwechsel wirkt im Frame, Einklappen am PC, Start eingeklappt in Handybreite, Menü öffnet und schließt nach der Wahl. Bilder: `hud/docs/screenshots/hub-*.png` (ohne SYS: dessen Bild zeigt die Prozesse der Maschine und bleibt draußen).

### Zugangsprüfung für HUD und SYS (2026-10-13)

Die gemeinsame Prüfung aus Abschnitt 4.3 (`gate: pairing-v1`), für HUD und SYS.

| Wo | Was |
|---|---|
| `shared/…/Web/PairingGate.cs` | Diese Maschine kommt ohne Anmeldung herein und ist Admin, wie bisher. Aus dem Netz nur mit Netzschalter und gekoppeltem Gerät (Cookie `stykker_device`, wie bei LLM). Offen sind nur `/api/ping`, `/pair` und Manifest samt Symbolen. Die Anmeldeseite steht für sich, ohne Design-System. |
| `shared/…/Web/FrameAncestors.cs` | Einbetten nur durch die Seite selbst und die Zentrale unter demselben Rechnernamen. |
| `hud/…/Program.cs` | Prüfung vor allem anderen. `/api/hub/access`, `/remote`, `/code`, `/devices/…`: Netzschalter, Code und Geräte, nur an der Maschine und nur von der eigenen Seite. |
| `hud/…/hub.*` | Bereich „Devices“ in der Übersicht: Netzschalter mit den Adressen, Code mit Restzeit, Geräteliste mit Rolle je Modul und Entfernen. Ein Gerät im Netz bekommt diesen Bereich nicht. |
| `sys/…/Program.cs` | Prüfung vor allem anderen. Beenden und Priorität nur mit Rolle `admin`; „Dateiort öffnen“ nur an der Maschine. `--hub-port` (Vorgabe 8079). |

Der Port wird nur im Netz geöffnet, wenn der Netzschalter beim Start an ist. Umschalten wirkt nach dem nächsten Start von HUD und SYS; so fragt die Windows-Firewall niemanden, der die Werkzeuge nur lokal benutzt.

Geprüft im Browser mit einer Test-Ablage (`--pairing-file`), das „Handy“ über die Netzadresse dieser Maschine:

- Schalter aus: aus dem Netz keine Verbindung. Schalter an, nach Neustart: `/hub` führt zur Anmeldeseite, `/api/snapshot` antwortet 401, `/api/ping` ist offen.
- Falscher Code: abgewiesen. Richtiger Code: Cookie, weiter zur Zentrale.
- Das Gerät ist in HUD `admin`, in SYS `viewer`. SYS im Frame auf dem anderen Port nimmt dasselbe Cookie.
- Als Viewer einen Prozess beenden: 403. Nach dem Klick auf `admin` in der Zentrale: die Aktion läuft (geprüft mit einer PID, die es nicht gibt).
- Netzschalter und Geräteliste vom Handy aus: 403 und leer.
- Entfernen in der Zentrale: HUD und SYS antworten dem Gerät sofort 401.

Noch nicht da:

- **Nicht am echten Handy geprüft**, und nicht über die Windows-Firewall hinweg: die Anfragen kamen von dieser Maschine über ihre Netzadresse. Beim ersten Start mit Netzschalter fragt die Firewall für `StykkerHUD-Server.exe` und `StykkerSYS-Server.exe`.
- **Kein HTTPS.** Im Heimnetz und über Tailscale wandert das Cookie im Klartext. Für die Installation als App verlangen Android und iOS außerhalb von `localhost` in der Regel HTTPS; über Tailscale ginge das mit dessen Zertifikaten. Offen.
- **Kopplung andersherum** (das neue Gerät zeigt den Code) hat die Seite noch nicht, und keinen QR-Code.
- **Loopback gilt als Admin.** Hinter einer Weiterleitung auf derselben Maschine träfe das auf jede Anfrage zu; vor dem Knoten braucht es den Knotenschlüssel aus Abschnitt 4.6.
- **LLM ist seit L2 dabei** (unten): eine Anmeldung für alle drei Module.
- **LLM im Frame** hat seit L2 die eingebettete Darstellung (unten).
- **Geräte, Rollen, Knoten** aus dem Mockup fehlen. Jedes Modul meldet sich noch selbst an.
- **Kein Service Worker:** das Manifest reicht zum Installieren; ohne Netz zeigt die App nichts.
- **Der Verweis „Processes“ im Monitor** zeigt fest auf `127.0.0.1:8077` und öffnet SYS im Frame ohne eingebettete Darstellung.
- **`stykker.json`** kennt bisher nur `modules`; Knoten und Netzschalter (Abschnitt 4.2) fehlen.

Offen aus Phase 1:

- Der **Host-Code** gilt nur in dem Prozess, der ihn zeigt. Für LLM reicht das; die Zentrale kann ihn so nicht anzeigen.
- Der **Netzschalter** steht noch in `pairing.dat` und zieht in Phase 2 nach `stykker.json`.
- Die Sperre heißt `Local\…` (je Windows-Sitzung). Der Dienst in Phase 8 braucht `Global\…`.
- Zeiten stehen in UTC in der Ablage; StykkerLLM rechnet mit Ortszeit. Der Adapter in L2 rechnet um (unten).

### StykkerLLM an der gemeinsamen Kopplung (L2, 2026-10-13)

Eine Anmeldung gilt für HUD, SYS und LLM. `AccessControl` und `HostRegistry` sind dünne Adapter auf `Stykker.Pairing`: Namen und Aufrufe bleiben, die Ablage ist jetzt die gemeinsame.

| Datei | Inhalt |
|---|---|
| `llm/src/StykkerLlm.Core/Server/SharedPairing.cs` | Öffnet die Ablage eines Datenordners und übernimmt `access.dat` und `hosts.dat` einmal; danach heißen sie `*.bak`. Rechnet Ortszeit und UTC um. |
| `llm/src/StykkerLlm.Core/Server/AccessControl.cs` | Geräte je Modul. Ein Gerät aus der Zentrale ist hier `viewer`, ein Gerät, das sich hier anmeldet, `admin`. Der Code ist nur für Admins lesbar. |
| `llm/src/StykkerLlm.Core/Host/HostRegistry.cs` | Die Hosts in der Ablage, nur mit dem Hash ihres Tokens. |
| `llm/src/StykkerLlm.Core/Settings.cs` | `AppPaths.PairingFile()`: für die Vorgabe die gemeinsame Ablage, sonst `pairing.dat` im Datenordner; `--pairing-file` hat Vorrang. `AppPaths.FromDataDir()`: der Vorgabe-Ordner über `--data-dir` ist die Vorgabe. |
| `llm/src/StykkerLlm.Server/EngineHost.cs`, `Program.cs`, `AccessGate.cs` | Ein Dienst für Geräte und Hosts. `--pairing-file`; `--hub-port` mit Vorgabe 8079 (`0` schaltet das Einbetten ab). `/api/ping` mit `module`, `version`, `gate: "pairing-v1"`; `app` bleibt. |
| `llm/src/StykkerLlm.Server/wwwroot/ui.js`, `app.css` | Eingebettet (`?embed=1&theme=…`): ohne Marke, im Thema der Zentrale. Die Wahl merkt sich die Sitzung, nur im Frame. |
| `llm/src/StykkerLlm.UI/Program.cs` | Reicht `--pairing-file` nur weiter, wenn das Fenster es selbst bekommen hat (Entwickler-Schalter). |
| `hud/src/StykkerHud.Server/Program.cs`, `wwwroot/hub.js`, `hub.css` | Die Zentrale zeigt die Rolle `llm` je Gerät und ändert sie. |
| `llm/tests/StykkerLlm.Tests/N66_SharedPairingTests.cs` | 19 neue Tests: Lage der Ablage, Adapter, fremde Änderungen, Übernahme, Rollen je Modul. |

Dabei entschieden, weil die Umsetzung es verlangte:

- **Vorgabe-Ordner über `--data-dir`:** Fenster und Terminal starten den Server immer mit `--data-dir`. Ohne diese Regel hätte der Server eine zweite `pairing.dat` neben `access.dat` angelegt, und die Anmeldung wäre nur beim Start aus dem Browser gemeinsam gewesen.
- **`--hub-port` mit Vorgabe 8079:** LLM lässt sich jetzt auch aus dem Fenster heraus in die Zentrale einbetten. Bisher ging das nur, wenn jemand den Server von Hand mit dem Schalter startete.
- **Gesperrte Ablage:** Dann schlägt nur die eine Anmeldung fehl. `HostRegistry.Add` gibt die Ausnahme weiter, weil `HostPairing` unverändert bleibt (siehe unten, nicht geprüft).

Geprüft (2026-10-13, wie angegeben):

- **Build:** LLM, HUD und SYS ohne Warnungen.
- **Tests:** LLM 605, davon 604 bestanden und 1 übersprungen (der Scan des echten Modellordners). Vorher 586: 585 bestanden, 1 übersprungen. Shared: 63 in `Stykker.Shared.Tests` und 42 in `Stykker.Pairing.Tests`, alle bestanden. SYS: 21 bestanden.
- **Probelauf mit zwei Prozessen und einer Test-Ablage** (LLM auf 8090, HUD auf 8093, `--pairing-file` in einem Temp-Ordner, Abfragen mit `curl`):
  - LLM koppelt ein Gerät mit dem Code der Zentrale. Die Zentrale listet es mit `llm: admin`, `hud: viewer`, `sys: viewer`.
  - Die Zentrale setzt `llm` auf `viewer`. LLM erkennt das beim nächsten Zugriff.
  - Der Viewer bekommt `/api/state` ohne Zugangscode.
  - Die Zentrale entfernt das Gerät. LLM antwortet dem Cookie mit 401.
  - Die echte `%APPDATA%\Stykker\pairing.dat` wurde nicht berührt.
- **Browser:** Eingebettet in einem Frame im selben Ursprung: Klasse `embed`, Thema der Zentrale, Marke ausgeblendet, Navigation und Statusleiste da. Das bleibt nach einem Seitenwechsel per Klick, nach fünf Sekunden und nach einem vollen Neuladen. Ein eigener Tab zeigt die volle Oberfläche. In der Zentrale steht die Spalte LLM mit der Rolle; bei 375 Pixeln gibt es keinen Überlauf und die Schalter brechen nicht um. Keine Fehler in der Konsole.
- **Übernahme:** Tests für eine alte `access.dat` (Gerät mit altem Cookie und Rolle), eine alte `hosts.dat` (Host mit altem Token), den zweiten Start ohne Doppeleinträge, ein entferntes Gerät, das nicht zurückkommt, Rollen ohne Feld und `hub`, eine Ablage mit Geräten und Code der Zentrale, und eine unlesbare Datei, die liegen bleibt.

Noch nicht geprüft:

- **Echtes Handy, Windows-Firewall, Tailscale.** Die Browser-Prüfung kam von dieser Maschine über `127.0.0.1`.
- **Echter StykkerHost auf einem zweiten PC.** Geprüft ist nur der Test mit der alten `hosts.dat` als Vorlage.
- **Die echte `access.dat` des Besitzers.** Ihre Übernahme läuft beim ersten Start der neuen Version; danach liegt sie als `access.dat.bak`.
- **LLM im Frame der echten Zentrale** (Fall 2 der Prüfliste): der Probelauf hatte keine `stykker.json`. Geprüft ist die Einbettung über ein Frame im selben Ursprung.
- **Netzschalter über alle drei Module:** im Probelauf liefen LLM und HUD zusammen, SYS nicht.
- **Die Sperre über zehn Sekunden** (ein anderer Prozess hält die Ablage): nicht ausgelöst.
- **Bilder** für `hud/docs/screenshots/`: nicht gemacht. Dafür braucht es eine Auswahl ohne SYS.
