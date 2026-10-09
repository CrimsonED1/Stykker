# Mockup 5 (Monitor): Lücken und Feature-Ideen

Grundlage: `mockup-5.html` (Text, Skripte, Menüs, Ctrl+K-Liste), `proposals.de.md`, `docs/ui.md`, README und
`src/StykkerLlm.Server/Components/{Pages,Layout}/*.razor` sowie `Core/Server/ActionApi.cs`.
Pfade unten relativ zu `C:\_AI\StykkerLLM\stykker-llm\src\`.

## Teil A – Lücken (nach Wichtigkeit)

Es gibt Lücken: 16 Punkte. 1–6 sind wichtig, weil sonst Funktionen verloren gehen, die heute auf dem Monitor bedienbar sind.

| # | Was fehlt | Heute im Code | Im Mockup |
|---|---|---|---|
| 1 | **Proxy-Einstellungen insgesamt**: Proxy an/aus mit Port, LAN-Freigabe, Ziel (Auto/Server), entfernte Stykker-Proxys hinzufügen/entfernen, Cloud-Anbieter mit URL, API-Key speichern, Modellliste des Anbieters | `Pages/Monitor.razor` Z. 14–75 (`ToggleProxy`, `OnLanChanged`, `AddRemote`, `AddProvider`, `SaveKey`) | Nur Chip „Proxy :8080 → stykker“, Klick zeigt Toast „would open as a side panel“. Kein Entwurf des Panels, kein Ctrl+K-Eintrag „Proxy an/aus“ oder „Proxy-Einstellungen“. Port im Mockup 8080, Vorgabe ist 17500 (`Core/Settings.cs:100`). |
| 2 | **Laufenden Server als Profil speichern** („Save“) | `Monitor.razor:170`, `Server.razor` (`Act("save")`) | Karten haben Prompt, Details, Record, Stop, aber kein Save; Speichern nur aus der History. |
| 3 | **History-Details** (`/history/{key}`: Kommando, Modelldatei, Läufe, VRAM-Schätzung) | `Monitor.razor:324` `OpenDetails`, `Pages/HistoryDetail.razor` | History-Zeile hat nur Start, Save, Forget; kein Link, kein Klick auf den Namen. |
| 4 | **Link Hardware-Karte → GPU-Detailseite** (`/gpu`: Takte, Drosselgrund, VRAM je Prozess vollständig) | `StackBar … MoreHref="gpu"` in `Monitor.razor`, `Pages/Gpu.razor` | Nur über ☰ und Ctrl+K. „other (6)“ in der Legende ist nicht klickbar. Drosselgrund (`ThrottleText()`) fehlt, nur „clocks free“. |
| 5 | **RAM pro Programm** (System-StackBar `RamTop`) | `Monitor.razor` System-Karte | System zeigt nur CPU und Memory. |
| 6 | **Absturzdialog „Server lost“** (Ursache, letzte Logzeilen, *Open log*) und **Bestätigung für ⏻** (App beenden) | `Layout/MainLayout.razor:62, 101–113`, `Core/ServerLost.cs` | Kein Zustand für einen abgestürzten Server; ⏻ ohne Rückfrage. |
| 7 | **Queue-Chip** (wartende Anfragen) und **Modelldatei-Größe** | `Monitor.razor:185` (`QueueCount`), `ChipModelFile` | Kennzahlenraster ohne Queue und ohne Modelldatei. README wirbt mit „queue“. |
| 8 | **Recent requests: Prompt-Verarbeitung** (Prompt-Tokens, Prompt-t/s, gecachte Tokens) und Task-Nr. | `Monitor.razor:300` (`ColReqPrompt`, `ColTask`), `FinishedRequest.PromptTps/PromptTotal` (`Core/ServerWatcher.cs:15`) | Nur Generierungs-t/s und Dauer. |
| 9 | **Rollen- und Nur-Lesen-Zustand**: Pill „read-only“ / „Viewer“, alle Aktionen deaktiviert mit Tooltip | `MainLayout.razor:53–57`, `RoTip` in allen Seiten | Nicht dargestellt (wichtig fürs Handy mit Rolle Viewer). |
| 10 | **Ansicht Auto/Klein/Voll** (Dichte) und **Zurück/Vor** im App-Fenster | `MainLayout.razor:27–28, 86–88` | ☰ ohne Dichte-Umschalter, keine Zurück/Vor-Knöpfe für `StykkerUI`. |
| 11 | **Aufnahme läuft / Aufnahme stoppen** auf der Karte | `record.start` auf dem Monitor, `record.stop` nur auf `Recordings.razor` | Record-Knopf ohne Laufzustand und ohne Stop. |
| 12 | **Profil bearbeiten**: Name, Notiz, Argumente (Textfeld) | `Monitor.razor:130–142` (`SaveEdit`, `profile.rename/note/args`) | Menüpunkt „Edit“ ohne Entwurf; Notiz wird nirgends angezeigt. |
| 13 | **Start abbrechen / Startfehler schließen**, **Ollama-Modell entladen**, **Neustart bei Absturz umschalten** | `ActionApi.cs`: `launch.stop`, `launch.dismiss`, `unload`, `profile.restart`, `profile.idleunload` – heute in **keiner** Razor-Seite benutzt | Mockup zeigt „Restart on crash“ nur als Anzeige, „Idle unload …“ im Menü (gut, schließt halb die Lücke), aber kein Abbrechen während „Checking“, kein Entladen pro Ollama-Modell, kein Schalter für Restart. |
| 14 | **Host ohne laufende Server** (Host verbunden, GPU-Belegung des Hosts) | `Monitor.razor:244–252` (`HostsNothingRunning` + GPU-Chip) | Nur Hosts mit Servern; Host-GPUs fehlen auch in der Hardware-Spalte. |
| 15 | **Ctrl+K unvollständig**: Seiten Runs, Catalog, Models, Prompt (ohne Server), Compare, Bug report, Log fehlen; Aktionen Record, Save, Bench, Proxy an/aus, „Start again“ (History), Edit, Notice dismiss, neuer Pairing-Code fehlen | ☰ in `MainLayout.razor:72–84` | Palette hat nur 7 Seiten und 6 Aktionen. |
| 16 | **Themes**: App hat fünf Themes (auch Space Glass), kein Hell/Dunkel/System | `Core/ThemeCatalog.cs`, `Settings.razor` (`theme.set`) | Mockup-Leiste bietet Dark/Light/System und vier Themes – entweder als neues Feature planen oder an die fünf Themes angleichen. |

Kleinere Punkte: Notice-Variante **Alarm** (rot) neben Hint und die Zeile mit dem Logpfad (`Monitor.razor:86–98`) fehlen;
Seite `/approve` (Gerät zeigt Code) ist nur über Phone erreichbar – im Mockup ebenso, also kein Rückschritt.

### Daten, die das Mockup zeigt, die es aber (noch) nicht gibt

Keine Lücke der App, aber vor der Umsetzung klären, sonst entstehen leere Spalten:

- Recent requests: **Tool-Aufrufe, Thinking-Tokens, Client-Art** (Icon cli/editor/agent), **Host** – `FinishedRequest` hat nur `Client` (Name);
  Tools und Reasoning gibt es nur für Anfragen über den Proxy (`Core/ProxyTap.cs:17–19`). Ergebnis „context full“ = `ReqStatus.Truncated` (vorhanden).
- History: **Endzustand** (sauber/abgestürzt/von außen beendet) und **Tokens gesamt** je Eintrag fehlen in `HistoryEntry` (`Core/Library.cs:38`); Laufzeit (`TotalSeconds`) und Ø (`MeanTps`) gibt es.
- Profil-Sparkline (t/s-Verlauf über Läufe): nicht gespeichert, nur `BestTps`/`MeanTps`.

## Teil B – Ideen für die Monitor-Seite

| # | Idee | Nutzen (ein Satz) | Daten / Fundstelle | Aufwand |
|---|---|---|---|---|
| 1 | **Langsamer als sonst** – Badge auf der Karte („−22 % vs. Ø dieses Profils“) mit wahrscheinlichem Grund (Shared RAM, Drosselung, fremder Prozess im VRAM) | Man merkt sofort, wenn ein Treiber-Update, ein Spiel im Hintergrund oder eine Spill-Konfiguration Geschwindigkeit kostet. | Vorhanden: `HistoryEntry.MeanTps/BestTps` (`Core/Library.cs:38`), live `AverageActive()`, Schwellenlogik wie `Benchmark.Regression` (`Core/Benchmark.cs:298`), Gründe aus `ServerWatcher.SpillGb`, `Gpu.Throttled`, `VramTop`. | S |
| 2 | **VRAM-Vorhersage im Profil vor dem Start** – Balken „braucht 22.8 GB / frei 6.8 GB“ und Vorschlag „Stop coder-fast → passt“ | Statt Start → Fehlermeldung sieht man vorher, ob es passt und was man dafür beenden müsste. | Vorhanden: `VramEstimate.Estimate` (`Core/VramEstimate.cs:31`), schon im Pre-Check `Core/ServerLauncher.cs:281/297`; Belegung je Server `s.VramGb`. Nur Anzeige im Profil fehlt. | S–M |
| 3 | **Absturz-Alarm als Benachrichtigung** (Browser/Handy, Web Notification API) mit Ursache und Neustart-Zähler („2/3 in 10 min“) | Wer im Editor arbeitet, merkt sonst erst am Timeout seines Agents, dass der Server weg ist. | Vorhanden: Ereignis `ServerLost` (`Core/MonitorEngine.cs:61`), `CrashAnalysis.Guess` (`Core/ServerLost.cs:32`), `CrashRestartPolicy.Count` (`Core/CrashRestartPolicy.cs:45`). Neu: Notification-Aufruf in `wwwroot/ui.js` (gibt es noch nicht), Einstellung an/aus. | M |
| 4 | **Warteschlange und Prompt-Cache sichtbar** – „3 waiting“ in Karte und Kopfzeile, rot wenn > Slots; bei vLLM KV-Cache-% | Zeigt, ob mehr Parallelität (`-np`) oder ein zweiter Server nötig ist, statt nur „langsam“ zu fühlen. | Vorhanden: `QueueCount` (`Core/ServerWatcher.cs:91`, aus `requests_deferred`, `Core/Backends.cs:238`), vLLM `Waiting`/`GpuCachePct` (`Core/Backends.Vllm.cs:20`). Neu: Verlauf der Queue im Ringpuffer neben `History`. | S |
| 5 | **Energie pro 1k Tokens live und heute** in der Hardware-Karte (Wh/1k, kWh heute, optional €-Preis aus Settings) | Macht Quantisierung/Modellwahl auch nach Stromverbrauch vergleichbar, nicht nur nach t/s. | Teilweise: Rechnung existiert für Aufnahmen (`Core/RecordingMetrics.cs:180–182`), live gibt es `Gpu.PowerW` und `GeneratedTotal` je Server. Neu: laufendes Integral im `MonitorEngine`, Aufteilung bei mehreren Servern nach VRAM oder Last. | M |
| 6 | **Modell-Schnellwechsel im Proxy-Chip** – Dropdown mit laufenden Servern, Host-Modellen und Profilen („starten und bedienen“) | Agent/Editor zeigen immer auf `stykker`; Modellwechsel ohne Client-Konfiguration anzufassen. | Vorhanden: `proxy.target` (`Core/Server/ActionApi.cs`), `RouterProxy.VirtualModel`, `StartableModels`/`HostStart` (`Core/ProxyRouter.cs:30, 45–46`). Neu: lokale Profile als startbare Ziele. | M |
| 7 | **Time to first token und Prompt-t/s je Server** (Median der letzten 20 Anfragen) im Kennzahlenraster | Für Agents mit langen Prompts ist die Lesezeit oft der Engpass, nicht die Generierung – die Karte zeigt heute nur Generierungs-t/s. | Vorhanden: `FinishedRequest.PromptTps/PromptTokens/PromptTotal` (`Core/ServerWatcher.cs:15`), TTFT über Proxy `ProxyTap.FirstToken` (`Core/ProxyTap.cs:14`). | S |
| 8 | **Leerlauf belegt VRAM** – „idle 40 min, hält 17 GB, entlädt in 12 min“ mit Knopf „Jetzt entladen“; Hinweis, wenn ein blockiertes Profil dadurch passen würde | Spart den Griff zu „Free VRAM“ (alles) und macht die Idle-Unload-Einstellung sichtbar. | Vorhanden: `IdleUnload.DueAt` (`Core/IdleUnload.cs:27`), `LastBusyAt` (`Core/ServerWatcher.cs:101`), Aktionen `unload`, `profile.idleunload` (in der UI heute ungenutzt). | S |

Bewusst weggelassen: Chat im Monitor (gibt es als Prompt-Seite), KI-Zusammenfassungen, Ranglisten, Animationen.
