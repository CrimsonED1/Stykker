# Plan: StykkerLLM als erstes Modul

Stand 2026-10-13 · **L2 umgesetzt** (Kopplung, siehe [plan-stykkernode.md](plan-stykkernode.md), Abschnitt 10) · L4: nur `/api/ping` · der Rest ist Entwurf · Teil von [plan-stykkernode.md](plan-stykkernode.md), Phasen 1 bis 3 · Bild: [architektur.svg](architektur.svg)

## 1. Ziel

StykkerLLM bleibt eigenständig: `StykkerLLM-Server.exe` auf Port 17401, Fenster und Terminal wie heute. Zusätzlich nutzt es die zentrale Kopplung und Konfiguration und kann unter dem Knoten laufen, ohne dass es dafür neu gebaut werden muss.

Die Logik liegt schon in `Core`. Die Arbeit sitzt an drei Stellen: Kopplung, Programmaufbau (`Server/Program.cs`) und Zugangsprüfung (`Server/AccessGate.cs`).

## 2. Ist-Stand

| Teil | Datei | Heute |
|---|---|---|
| Geräte und Code | `src/StykkerLlm.Core/Server/AccessControl.cs` | `access.dat` (DPAPI). Sechs Ziffern, zehn Minuten, einmal gültig, nach fünf Fehlversuchen ein neuer Code. Rollen `admin` und `viewer`. Anmeldung in beide Richtungen (`Pair`, `RequestPairing`, `Approve`, `Poll`). Geräte nur als Hash mit Präfix `StykkerLLM.device.v1\|`. Netzschalter `RemoteEnabled` in derselben Datei. |
| Hosts | `Core/Host/HostPairing.cs`, `HostRegistry.cs` | Eigener Code, `hosts.dat` (DPAPI). Token nur als Hash, mit derselben Funktion `AccessControl.Hash`. |
| Zugang über HTTP | `src/StykkerLlm.Server/AccessGate.cs` | Schlüssel `server.key`. Gerät als Cookie oder Kopf `X-Stykker-Device`. Netzschalter. Endpunkte `/pair…`, `/hosts/`, `/api/ping`. `/api/metrics` nur bei Loopback. Prüfung des Host-Kopfs. |
| Entdeckung | `Core/Server/NodeDiscovery.cs` | UDP 17501, Probe `STYKKER-DISCOVER/1`, Antwort `app: StykkerLLM-Server`, nur bei eingeschaltetem Schalter. |
| Programm | `src/StykkerLlm.Server/Program.cs` (355 Zeilen) | Alles in einer Datei: Dienste, Zugang, Host-Endpunkte, Lebensdauer (`ServerHolds`), Tray, Entdeckung, Browserstart. |
| Oberfläche | `src/StykkerLlm.Server/Components/**`, `wwwroot/` | Blazor Server. Assets `app.css`, `ui.js`, `role.js`. |
| Tests | `tests/StykkerLlm.Tests/` | Unter anderem `N50_PairingTests`, `N57_HostPairingTests`, `N60_HostServiceTests`, `S2_AccessQrTests`, `DiscoveryTests`. |

## 3. Zielbild

Projekte nach dem Umbau:

| Projekt | Inhalt | Art |
|---|---|---|
| `StykkerLlm.Core` | Logik, ohne ASP.NET und ohne Plattform. Kern bleibt unverändert. | Bibliothek |
| `StykkerLlm.Web` (neu) | Razor-Seiten, `wwwroot`, Endpunkte, Zugangsprüfung, `AddStykkerLlm()` und `MapStykkerLlm()`. | Razor Class Library |
| `StykkerLlm.Server` | Nur noch das Standalone-Programm: Startargumente, Tray, Browserstart, Auto-Stopp. | Exe `StykkerLLM-Server.exe` |
| `StykkerLlm.UI`, `.Cli`, `.Host`, `.Platform.Windows` | Unverändert. | |
| `shared/src/Stykker.Pairing` (neu) | Kopplung für alle Module. | Bibliothek |

- **Kopplung:** `AccessControl` und `HostRegistry` werden dünne Adapter auf `Stykker.Pairing`. Die öffentlichen Aufrufe bleiben. Die Tests gelten dann weiter als Verhaltensprüfung.
- **Assets:** Nach dem Umzug liegen die Dateien unter `_content/StykkerLlm.Web/`. So kollidieren sie nicht mit anderen Modulen, die ebenfalls eine `app.css` haben.

## 4. Schritte

### L1: Aufteilen, Verhalten unverändert

- Razor-Seiten, `wwwroot` und Komponenten nach `StykkerLlm.Web`. Die Router-Komponente der Server-Version bekommt die zusätzliche Assembly.
- Inhalt von `Program.cs` aufteilen: Dienste und Endpunkte nach `AddStykkerLlm()` und `MapStykkerLlm()`. Standalone-Teile (Tray, `--stay`, Auto-Stopp, Browserstart) bleiben im Server-Programm.
- Alle Startschalter bleiben: `--port`, `--data-dir`, `--no-browser`, `--no-tray`, `--stay`, `--platform basic`. `--sim` nur in Debug.
- **Prüfung:** `dotnet build StykkerLlm.slnx -c Release` mit 0 Warnungen und 0 Fehlern. `dotnet test tests/StykkerLlm.Tests -c Release` grün. Vorher und nachher: `/api/state` hat dieselben Felder. Eine Browser-Prüfung vergleicht die Startseite per Screenshot.

### L2: Kopplung zentral

**Stand 2026-10-13: umgesetzt.** Was gebaut und geprüft ist, steht in [plan-stykkernode.md](plan-stykkernode.md), Abschnitt 10, „StykkerLLM an der gemeinsamen Kopplung“. Offen bleiben dort die Prüfungen am echten Handy und an einem zweiten PC (Abnahme 3).

- Ablage `%APPDATA%\Stykker\pairing.dat` (für den Vorgabe-Ordner), Zugriff über `Stykker.Pairing` (Plan Knoten, Abschnitt 4.1).
- **Migration beim ersten Start:** `access.dat` und `hosts.dat` werden gelesen. Geräte, Rollen, Netzschalter und Host-Token-Hashes werden übernommen. Die Originale bleiben als `*.bak` liegen und werden nicht gelöscht.
- **Pflicht, sonst sind alle Geräte abgemeldet:**
  - Hash-Verfahren und Präfix `StykkerLLM.device.v1|` bleiben unverändert.
  - Cookie-Name aus `StateJson` bleibt unverändert.
  - Host-Token-Hashes werden unverändert übernommen.
- Alle Schreiber nutzen die Sperre: Server (Code, Geräte), Fenster (`/pair/local`), Migration.
- **Tests:**
  - Fixture aus der Vorversion: alte `access.dat`. Ein Gerät meldet sich mit seinem alten Cookie an.
  - Zwei Prozesse schreiben gleichzeitig: kein Eintrag geht verloren.
  - Abbruch beim Schreiben: keine halb geschriebene Datei (atomar).

### L3: Konfiguration

- `stykker.json` lesen. `node.remote` ersetzt den Schalter aus `access.dat`. Ports und Datenordner aus der Datei gelten nur, wenn die Kommandozeile nichts angibt.
- Ohne Datei: wie heute, eigenständig.
- Einstellungen, die nur LLM betreffen, bleiben in `%APPDATA%\StykkerLLM\settings.json`.

### L4: Modul-Vertrag

- `GET /api/ping`: zusätzlich `module: "llm"`, `version` und `gate: "pairing-v1"`. Nur Felder hinzufügen. Zustandsfelder werden nie umbenannt (`llm/AGENTS.md`). **Umgesetzt** (2026-10-13, geprüft mit einem Probelauf).
- `GET /api/module` (neu): Name, Version, Kachel-Status (Server läuft, Modelle geladen, Auslastung). Ab Rolle `viewer`.
- **Entdeckung:** Der `DiscoveryResponder` läuft nur in der Standalone-Hülle. Ist ein Knoten aktiv, antwortet nur er, also ein Antwortender je PC. Die Antwort behält `app: "StykkerLLM-Server"`, weil installierte StykkerHost-Versionen danach suchen.

### L5: Knotenbetrieb vorbereiten (ohne Knoten lauffähig)

- Neuer Startschalter `--node`: Listen nur auf 127.0.0.1, kein Auto-Stopp, Knotenschlüssel über Standardeingabe.
- Die Loopback-Prüfungen (`AccessGate`, `/api/metrics`, `IsLocal`) nutzen die verifizierte Herkunft. Eine weitergereichte Adresse zählt nur mit gültigem Knotenschlüssel, sonst die Verbindungsadresse.
- **Tests:**
  - Anfrage mit Herkunft 127.0.0.1 ohne Knotenschlüssel: wird wie eine fremde Anfrage behandelt.
  - Mit Knotenschlüssel und fremder Herkunft: Schalter-Prüfung und Code-Seite wie für fremde Geräte.
  - `/api/metrics` über den Knoten: ohne Anmeldung abgewiesen.

### L6: Aufräumen und Doku

- `Server/Program.cs`, Zeile 291: der Verweis auf `docs/nodes.md` ist veraltet. Kommentar ändern.
- `docs/hosts.md`, `docs/architecture.md` und `AGENTS.md` (Tabelle „Where things are“, Kopplung) an die neue Ablage anpassen.
- Die Weiterleitung `/nodes` nach `/hosts` bleibt, damit alte Lesezeichen funktionieren.

## 5. Akzeptanz

Alles muss stimmen:

1. **Eigenständig wie heute:** `StykkerLLM-Server.exe --no-browser` startet auf 17401. Die Anmeldung mit dem Code geht. `StykkerUI.exe` öffnet sich, `stykker.exe` zeigt den Zustand.
2. **Bestehende Geräte bleiben angemeldet:** Ein Gerät, das vor dem Umbau angemeldet war, ist nach dem Umbau noch angemeldet.
3. **Hosts verbinden sich weiter:** Ein StykkerHost mit der bisherigen Version verbindet sich nach dem Umbau weiter, ohne neu zu koppeln. Test auf einem zweiten PC mit alter und neuer Version.
4. **Neues Modul erbt die Kopplung:** Ein zweiter Prozess, der nur `Stykker.Pairing` nutzt (ein Testprogramm, später das neue Modul), liest dieselbe `pairing.dat`. Das LLM-Gerät ist dort bekannt, mit Rolle `viewer`, ohne neu zu koppeln. SYS selbst bekommt die Prüfung erst in Phase 6.
5. **Migration:** Die Datei aus der Vorversion wird übernommen. Das Original bleibt als `*.bak`.
6. **Tests grün, 0 Warnungen.**
7. **Host-Protokoll unverändert.**

## 6. Nicht in diesem Schritt

- Kein Knoten (Phase 4), keine Zentrale (Phase 5), keine Weiterleitung.
- Keine neue NuGet-Abhängigkeit.
- Der Proxy auf 17500 bleibt, wie er ist.
- Die Linux-Plattform (`LinuxPlatform.cs.wip`) bleibt außen vor.

## 7. Offen und zu prüfen

- **Bindung des Proxys 17500:** Ob er nur auf Loopback lauscht oder auch im Netz erreichbar ist, habe ich noch nicht geprüft. Das klärt sich vor L5.
- **Gerätecookie:** gilt zwei Jahre (`SetCookie`). Die Laufzeit für Geräte, die über die Zentrale verwaltet werden, sollte überdacht werden.
- **Tests `N50`, `N57` und `S2`:** Sie brauchen angepasste Aufrufe, wenn sich die Namen der Adapter ändern. Die Prüfungen selbst bleiben.
- **Commits:** nur auf Auftrag. Gearbeitet wird auf `main`; einen Branch `dev` braucht es nicht (`AGENTS.md` im Wurzelordner).
