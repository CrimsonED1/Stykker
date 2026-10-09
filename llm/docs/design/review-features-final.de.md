# StykkerLLM – neue Weboberfläche: Lücken und Ideen (Endprüfung)

Stand: `dev` @ 6c6a3fd (U1–U14). Vergleich mit 166d57c (Monitor, MainLayout, Hosts, Settings), dazu `CommandPalette.razor`,
`ServerCard(.Model)`, `ActionApi.cs` und die Screenshots `monitor-dark.png` und `gpu.png`.
Pfade relativ zu `C:\_AI\StykkerLLM\stykker-llm\src\`. Nur gelesen, nichts geändert.

## Teil A – Lücken (nach Wichtigkeit)

Es gibt Lücken: **10 Punkte**. Fast alles aus dem Monitor ist wieder erreichbar, viele Teile sogar besser (Proxy-Panel,
Aufnahme mit Stopp, Entladen je Ollama-Modell, Idle-Unload). Die Liste enthält einen möglichen Fehler (Nr. 1), Rückschritte
gegenüber vorher (Nr. 2 bis 5) und Reste aus Plan und Mockup-Bericht (Nr. 6 bis 10).

| # | Lücke | Fundstelle | Vorher |
|---|---|---|---|
| 1 | **Stop-Knopf auf Host-Karten für alle Backends.** `ServerCard` zeigt Stop bei `!m.IsLocal` auch für Ollama, LM Studio und vLLM auf einem Model-Host. `host.stop` endet in `LaunchCoordinator.StopServerAsync` auf dem Host und beendet dort den Prozess per PID. Hat ein Ollama-Dienst eine PID, wird der **ganze Dienst** beendet, sonst kommt trotzdem „gestoppt“ zurück (`RemotePrompt` schluckt den Hinweis). Lokal ist Stop nur für llama.cpp erlaubt. | `Components/ServerCard.razor` (`acts3`: `m.Kind == LlamaCpp \|\| !m.IsLocal`), `Core/Host/HostCommands.cs:93–100` | Alte `Hosts.razor` zeigte Stop nur bei `Backend == "llama.cpp"` und `!Manual`. |
| 2 | **Warnung „Kontext fast voll“ fehlt.** Früher stand je Slot ein roter Text (`CtxAlmostFullOf`). Heute zeigt die Slot-Kachel nur noch eine Füllhöhe und einen Tooltip. Die gemeinsame Regel `CtxPressure` nutzt nur noch `/metrics`. Auf dem Handy (kein Hover) sieht man das gar nicht. | `ServerCard.razor` (`tiles`), `Core/CtxPressure.cs` | `Monitor.razor` (alt) Z. 207 |
| 3 | **Host-Server auf dem Monitor: kein Entladen und keine Aufnahme.** `Monitor.razor:78` übergibt Host-Karten nur `OnStop`, nicht `OnUnload`. Ollama-Modelle auf einem Host lassen sich nur auf `/hosts` entladen. | `Pages/Monitor.razor:78` | Vorher gab es auf dem Monitor gar keine Host-Aktionen, also kein Rückschritt. Gleiche Karten mit ungleichen Knöpfen verwirren aber. |
| 4 | **Viewer sieht aktive Knöpfe auf den Profil-Kacheln.** Start, Edit, Idle-Segmente und Remove haben nur `disabled="@ro"`, nicht `!CanWrite`. `Start()` tut dann still nichts. Edit öffnet den Editor, Speichern scheitert mit „Action failed“. Der Plan verlangt „hidden or disabled with a reason“. | `Monitor.razor:263–286` | Alt ebenso nur `ro`, aber mit `RoTip`. Die Karten sind inzwischen sauber (`CanWrite && !ro`). |
| 5 | **Angaben nur noch im Tooltip (vorher sichtbar):** Task-Nr. der Anfrage (`ColTask`), Fehlertext und Modellnamen der Cloud-Anbieter (`p.Error`, `p.Models`), URL der Remote-Rechner, Client-Namen der Server (nur die Anzahl ist sichtbar), Max-VRAM in der History-Zeile. Die Spalte „Prompt“ zeigt Prompt-t/s, ohne Einheit. Man hält sie für Tokens (Screenshot: 962, 809 …). | `Monitor.razor:99–113, 349–394`, `ServerCard.razor` (Metric „users“) | `Monitor.razor` (alt) Z. 65–67, 165, 300–307 und History-Spalte VRAM |
| 6 | **Start abbrechen, Startfehler schließen, „Restart on crash“ umschalten.** `launch.stop`, `launch.dismiss` und `profile.restart` werden in keiner Razor-Seite benutzt. Die Kachel zeigt beim Start nur einen Spinner, Restart nur als Anzeige. Ein hängender Start lässt sich im Web nicht abbrechen. | `Core/Server/ActionApi.cs:145–200`, `Monitor.razor:234–268` | Auch vorher nicht im Web (Plan „Not now“). Das Fenster und `ActionApi` können es. |
| 7 | **Proxy-Port lässt sich nicht ändern.** Plan U9 nennt „port 17500“ im Panel. Die Aktion `proxy.port` gibt es, im Web wird sie nicht benutzt. Der Port steht nur in der URL. | `ActionApi.cs:267`, `Monitor.razor:312–322` | Vorher auch nicht im Web |
| 8 | **Ctrl+K unvollständig.** Es fehlen die Aktionen: Aufnahme starten/stoppen, Server als Profil speichern, Benchmark für ein Profil, Start again (History), Profil bearbeiten, Notice schließen, neuer Pairing-Code (`host.code`, `code.rotate`), Server-lost-Banner schließen, Ansicht Auto/Klein/Voll. An Seiten fehlen `/approve` sowie Details je Server und je History-Eintrag. Vorhanden sind 15 Seiten, Free VRAM, Proxy an/aus, Thema, Profil starten und Server stoppen. | `Components/CommandPalette.razor:44–74` | – |
| 9 | **Profilnotiz wird nirgends angezeigt.** Sie lässt sich bearbeiten (`profile.note`), erscheint aber weder auf der Kachel noch im Tooltip. | `Monitor.razor:234–250, 300` | Alt ebenso nicht |
| 10 | **Kleinere Punkte.** Der History-Filter kennt kein Backend, obwohl Plan U8 einen „backend filter“ nennt (nur all/running/crashed). Host-GPUs fehlen in der Hardware-Karte (nur als Chip auf der Leerlaufkarte des Hosts). Slot-Kacheln öffnen beim Klick keine Details (Plan U4: „click = details“). Das Server-lost-Banner hat keinen Knopf „Start again“. | `Monitor.razor:126–140, 181–216`, `ServerCard.razor`, `Layout/MainLayout.razor:114–133` | – |

Gut erreichbar und **keine** Lücke mehr: Proxy-Einstellungen (Panel `#proxy`, Header-Chip, Ctrl+K), Aufnahme mit Laufzeit
und Stopp, Profil bearbeiten mit Name, Notiz und Args, Remote-Rechner, Cloud-Anbieter mit Schlüssel, Notices mit Alarm und
Logpfad, Hosts (eigene Seite mit `ServerCard`), Phone access, Bug report, Theme, Dichte, Zurück/Vor, ⏻ mit Rückfrage.

## Teil B – Ideen (6)

| # | Idee | Nutzen (ein Satz) | Daten / Fundstelle | Aufwand |
|---|---|---|---|---|
| 1 | **Time to first token und Prompt-Verarbeitung je Server** (Median der letzten 20 Anfragen als Kennzahl, TTFT als Spalte in Recent requests, Prompt-Tokens und gecachte Tokens im Tooltip) | Bei Agents mit langen Prompts bremst meist das Einlesen, nicht die Generierung. Das sieht man heute nicht. | Vorhanden: `FinishedRequest.PromptTokens/PromptTotal/PromptTps` (`Core/ServerWatcher.cs:15`), `ProxyTap.FirstToken` (`Core/ProxyTap.cs:14`). Bisher nicht in `RequestRow` (`Core/RequestRows.cs:6`). | S |
| 2 | **Profil als Startskript exportieren** (PowerShell/Batch) im ⋯-Menü der Kachel und auf `/history/{key}` | Eine funktionierende Konfiguration lässt sich in ein Repo, eine CI oder auf einen anderen Rechner übernehmen, ohne Args abzutippen. | Vorhanden und ungenutzt: `Core/ScriptExport.cs` (schwärzt Geheimnisse). Nur Download-Endpunkt und Menüpunkt fehlen. | S |
| 3 | **Anfragen-Log herunterladen und filtern** (CSV der letzten Stunden, Filter nach Server, Client, Ergebnis) | Langsame oder abgeschnittene Anfragen eines Agents lassen sich nachträglich finden und belegen, statt nur die letzten 8 zu sehen. | Vorhanden: `Core/RequestLog.cs` (schreibt schon CSV, `MonitorEngine`), `ProxyTap`-Daten (Tools, Thinking, Ergebnis). Neu: Endpunkt mit Zeitraum, Link „all requests“ an der Karte. | M |
| 4 | **Leerlauf belegt VRAM** auf der llama.cpp-Karte: „idle 40 min, entlädt in 12 min“, Knopf „jetzt entladen“, dazu Hinweis „Stop X → Profil Y passt“ an der VRAM-Vorhersage | Macht Idle-Unload sichtbar und erklärt eine rote Vorhersage gleich mit dem passenden Ausweg. | Vorhanden: `IdleUnload.DueAt` (`Core/IdleUnload.cs:27`), `LastBusyAt`, `UnloadIfIdleAsync`, Vorhersage `VramNeed` (`Monitor.razor:495`), `s.VramGb` je Server. | S–M |
| 5 | **Start abbrechen und Startfehler als Kachelzustand** (Spinner mit ✕, danach eine Fehlerzeile mit „Open log“ und „Dismiss“) | Ein hängender Start (Modell auf dem Netzlaufwerk, falsche Args) blockiert heute Port und VRAM, ohne dass man im Web eingreifen kann. | Vorhanden: `launch.stop`, `launch.dismiss` (`ActionApi.cs:145–160`), `LaunchCoordinator.StartingFor/StopLaunchedAsync`, `CrashAnalysis.Guess`. | M |
| 6 | **Absturz-Benachrichtigung im Browser/Handy** (Web Notification) mit Ursache und Neustartzähler | Wer im Editor arbeitet, merkt sonst erst am Timeout des Agents, dass der Server weg ist. | Vorhanden: `Host.LastLost` mit Ursache und Log (`MainLayout.razor:114`), `CrashRestartPolicy`. Neu: Aufruf in `wwwroot/ui.js`, Schalter in Settings. | M |

Bewusst weggelassen: Energie pro 1k Tokens (Daten nur für Aufnahmen), Chat im Monitor, Ranglisten.

## Teil C – Stand der 16 Lücken aus `review-features-mockup5.de.md`

**Erledigt: 10 · teilweise: 5 · offen: 1**

| # | Lücke (Mockup 5) | Stand | Beleg / Rest |
|---|---|---|---|
| 1 | Proxy-Einstellungen insgesamt | **erledigt** (bis auf Port) | Panel `#proxy`: Schalter, URL kopieren, Ziel, LAN, Remote-Rechner, Cloud mit Schlüssel, Modellliste. Port nicht änderbar (A7). |
| 2 | Laufenden Server als Profil speichern | **erledigt** | Speichern-Knopf auf der Karte (`OnSave`) |
| 3 | History-Details | **erledigt** | ⓘ in der History-Zeile → `/history/{key}` |
| 4 | Link Hardware → `/gpu`, Drosselgrund | **erledigt** | „details“ an der GPU-Karte, Header-Chip, `+N more`; `ThrottleText()` bei Drosselung |
| 5 | RAM pro Programm | **erledigt** | `StackBar Items="e.RamTop"` |
| 6 | Absturzdialog, Bestätigung für ⏻ | **erledigt** | Banner `lost-bar` mit Ursache, Logzeilen, Open log; ⏻ mit Rückfrage. Ohne „Start again“ (A10). |
| 7 | Queue-Chip und Modelldatei | **erledigt** | Kennzahlen `queue` (warn bei > 0) und `file` |
| 8 | Prompt-Verarbeitung, Task-Nr. | **teilweise** | Prompt-t/s ist da. Prompt-Tokens, gecachte Tokens und Task-Nr. fehlen (A5, Idee B1). |
| 9 | Rollen- und Nur-Lesen-Zustand | **teilweise** | Pills und Hinweisleiste vorhanden, Karten sauber. Profil-Kacheln für Viewer aktiv (A4). |
| 10 | Dichte Auto/Klein/Voll, Zurück/Vor | **erledigt** | ☰-Segment und `navback` in der Hülle |
| 11 | Aufnahme läuft / stoppen | **erledigt** | `recchip` mit Laufzeit, Knopf wechselt zu `record.stop` |
| 12 | Profil bearbeiten | **teilweise** | Editor mit Name, Notiz und Args vorhanden. Notiz wird nie angezeigt (A9). |
| 13 | Start abbrechen, Fehler schließen, Ollama entladen, Restart umschalten | **teilweise** | Entladen je Modell (⏏) und Idle-Unload erledigt. `launch.stop`, `launch.dismiss` und `profile.restart` weiter ungenutzt (A6). |
| 14 | Host ohne laufende Server | **teilweise** | Leerlaufkarte mit GPU-Chip erledigt. Host-GPUs fehlen in der Hardware-Karte (A10). |
| 15 | Ctrl+K unvollständig | **offen** | Seiten fast vollständig, Aktionen weiter lückenhaft (A8) |
| 16 | Themes | **erledigt** | Dark, Spacepunk Titan, System (`ThemeCatalog.Choices`) |

Kleinere Punkte von damals: Notice-Variante **Alarm** und Logpfad-Zeile sind erledigt (`Monitor.razor:15–33`).

Ideen aus dem damaligen Teil B:
- **Umgesetzt:** „Langsamer als sonst“ (U11, `SlowerPct`), VRAM-Vorhersage (U6, ohne Vorschlag „Stop X“).
- **Teilweise:** Queue (nur Karte, nicht im Kopf, kein vLLM-KV-Cache), Ziel-Auswahl im Proxy (Auswahl ja, lokale Profile nicht startbar).
- **Offen:** Benachrichtigung, Energie, TTFT, Leerlauf-VRAM.
