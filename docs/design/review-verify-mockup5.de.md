# Prüfung mockup-5.html (Monitor-Ersatz StykkerLLM)

Geprüft gegen `C:\_AI\StykkerLLM\stykker-llm` (Monitor.razor, MainLayout.razor, ServerState.cs, Library.cs,
ServerWatcher.cs, ProxyManager.cs, Recording.cs, ActionApi.cs, docs/ui.md). Mockup im Browser-Pane angesehen
(Testserver auf 127.0.0.1:8090, danach beendet), Breiten 460 und 375 px, Dark/Light, `shell`.
JS-Konsole: **keine Fehler, keine Warnungen**. Kein waagrechtes Scrollen bei 375/460 px.

Zeilenangaben = `mockup-5.html`. Aufwand: S < 1 h, M halber Tag, L mehr.

---

## MUSS

1. **Handy: Sparkline klappt auf 2 px zusammen.** Unter 640 px wird `.body2` zur Spalte, `.body2 .spark{flex:1}`
   (Z. 396) setzt die Basis auf 0 und verdrängt `height:64px` – gemessen: Spark 311×2 px. Die t/s-Kurve fehlt auf dem
   Telefon ganz. Abhilfe: im Media-Block `.body2 .spark{flex:none}` (oder `flex:1 1 auto`). (Z. 443–449)
2. **Proxy-Bedienung fehlt fast vollständig.** Kopfzeilen-Chip (Z. 684) zeigt nur einen Toast „side panel“. Nicht
   gestaltet: Proxy an/aus (`proxy.toggle`), Ziel/„serviertes Modell“ mit Auto und Cloud-Modellen (`proxy.set`/Choices),
   LAN-Bindung (`proxy.lan`), Remote-Rechner hinzufügen/entfernen, Cloud-Anbieter mit Schlüssel (hinzufügen, Schlüssel
   setzen, entfernen, Fehler/Modelle anzeigen), Fehlermeldung nach Toggle (`_flash`), `secondsToRetry`. Heute
   Monitor.razor Z. 13–80. Mindestens das Seitenpanel skizzieren.
3. **Read-only und Viewer-Rolle nicht abgebildet.** Kein Chip „read-only“/„Viewer“ in der Kopfzeile (heute
   MainLayout Z. 51–58), keine deaktivierten Knöpfe, kein Hinweistext (RoTip). Im Mockup ist alles klickbar.
   Festlegen: welche Icon-Knöpfe ausgegraut, welche ganz ausgeblendet (z. B. Stop, Forget, Remove).
4. **„Save“ (laufenden Server als Profil merken) fehlt auf der Serverkarte.** Kartenleiste hat nur Prompt, Details,
   Record, Stop (Z. 709/719/729). Heute Monitor Z. 170 (`save`), mit `canSave`/`saveBlock` im State.
5. **History: „Details“ fehlt.** Zeilenaktionen nur Start again, Save, Forget (Z. 1055). Heute `history/{key}`
   (Monitor Z. 324, Seite HistoryDetail.razor).
6. **Falsche Datenbehauptungen im Mockup** (würden so nicht erreichbar oder irreführend sein):
   - Ollama-Karte (Z. 735): Peak 102, Avg 84.0, 12.8k Tokens – für Ollama gibt es kein eigenes t/s; `Current` kommt
     nur aus beobachteten Kind-Engines (ServerWatcher.PollExternalAsync Z. 215–259), sonst 0. „Context per slot 8k“
     gibt es bei Ollama nicht (Kontext je Modell steht schon in der Tabelle).
   - vLLM-Karte (Z. 741): VRAM 14.1 G – vLLM liefert absichtlich keinen VRAM (Kommentar ServerWatcher Z. 235–236,
     „–“ statt erfundener Zahl); Slots 1/2 und ctx 32k gibt es bei vLLM nicht (keine Slots, nur `queue`).
   - Host-Server (Z. 740/744): „Proxy: not available for host servers“ ist falsch – Host-Server sind Proxy-Ziele
     (ProxyManager `HostSources`, P6). Umgekehrt „RAM/CPU/Peak/Avg/Tokens: not reported by the model host“: der Host
     schickt einen vollständigen `StateSnapshot`, `RemoteServer` hat `ramGb`, `cpu`, `peakTps`, `avgActiveTps`,
     `generated` – die Werte sind da (nur heute nicht angezeigt).

## SOLLTE

### Daten, die neu erhoben werden müssten

| Anzeige im Mockup | Fundstelle | Gibt es? | Wo entstehen | Aufwand |
|---|---|---|---|---|
| History-Endgrund (sauber / Absturz / von außen) | Z. 1054, `.end` | nein (bekannt) | Library.Observe + ServerRegistry beim Verschwinden; LostServer/CrashRestartPolicy kennen Absturz | M |
| History Tokens gesamt | Z. 1048 `tk` | nein | HistoryEntry.GeneratedTotal, in Observe aufsummieren | S |
| History Laufzeit „run time“ je Lauf | Z. 1052 | nur `TotalSeconds` (alle Läufe) | letzte Laufdauer aus `_runs` (Start) + LastSeen | S |
| History Backend-Farbe/Filter | Z. 760–766, 1049 | nein | aus `Program` ableiten (llama-server/ollama/lms/vllm) oder beim Observe speichern | S |
| History Host-Spalte / Host-Einträge | Z. 1046 | nur lokal | Snapshots der Hosts haben `history`; zusammenführen + Host-Name | S–M |
| Recent: Client-Art (CLI/Editor/Web/Agent) + Name je Anfrage | Z. 951 | nein: `FinishedRequest.Client` = alle beim Ende verbundenen Prozesse, nicht je Anfrage | über Proxy: `ProxyRecord.UserAgent` → Klassifizierung; direkt am Server nicht zuverlässig | M–L |
| Recent: Tool-Aufrufe, Thinking-Tokens | Z. 956 | nur in Aufnahmen | `ProxyRecord.Tools`/`ReasoningTokens`; Ring der letzten Proxy-Records in den State, `RequestEvent.Merge` existiert. Ohne Proxy immer „–“ | M |
| Recent: Ergebnis „error“ | Z. 946 | nein (ReqStatus nur Done/Truncated/Cancelled) | `ProxyRecord.HttpStatus`/`ClientAborted` | S (mit obigem) |
| Recent: „context full“ | Z. 946 | nur `Truncated` (auch max_tokens) | Truncated aufteilen oder Text „truncated“ verwenden | S |
| Recent: Anfragen der Host-Server | Z. 965/1013 | im Host-Snapshot (`recent`), heute nicht gemischt | Monitor/State zusammenführen | S |
| Server: Clients als Art („terminal“, „editor“) | Tooltip Z. 715 | nur Prozessnamen | Zuordnungstabelle Prozessname → Art | S |
| Server: „idle 4 min“ | Z. 721 | nur über 300er-Verlauf (≈5 min) | `LastActive` in ServerWatcher | S |
| Server: Sparkline-Zeitachse „−2 min“ | Z. 711 | ableitbar | `HistoryLength × intervalMs` (≈5 min bei 1 s) – Label rechnen, nicht fest | S |
| Profil: ngl „40 of 48“ (Gesamtschichten) | Z. 811 | nur `Ngl` aus Args | Schichtzahl aus GGUF-Metadaten (/props hat sie nicht) | M |
| Profil: Draft an/aus | Z. 804 | indirekt | `LlamaServerArgs` hat keinen Draft-Getter; `-md/--model-draft` ergänzen | S |
| Profil: Mini-Sparkline Verlauf | Z. 805 | nein (nur BestTps, TpsSum) | t/s je Lauf in HistoryEntry speichern | M |
| Profil für Ollama („ollama run … keep_alive 5m“) | Z. 822–827 | nein: Profile sind Kommandozeilen eines Servers; Ollama-Modelle sind kein Profil | Konzept klären oder Karte streichen | M/–  |

Vorhanden und korrekt gemappt: Kopfzeile (Proxy-Port, `targetName`, VRAM, RAM frei, Serverzahl/busy abgeleitet),
Serverkarte llama.cpp (Backend, Name, Zustand, Port, Proxy served über `servedKey`, VRAM, RAM, CPU, Shared-RAM/Spill,
ctx je Slot, Slots busy, Peak, Avg, Generated, Draft-Quote, Clients-Anzahl), Slot-Kacheln (Zustand, ctx, t/s,
generiert), Ollama-Modelltabelle (VRAM, auf GPU = vram/size, ctx, ExpiresAt), Hardware-Karte komplett (inkl. Drossel,
VRAM/RAM je Prozess), Profil: ctx, Port, Idle-Unload (`unloadAfterIdleMin`), Restart (`restartOnCrash`, `maxRestarts`,
0 = 3), Best t/s, „needs 22.8 GB“ (`MaxVramGb`), Live-t/s; History: Name, ctx, Port, Runs, Avg (`MeanTps`), Best,
Zeit (`LastSeen` – Achtung: letzter Kontakt, nicht Startzeit).

### Bedienung / Inhalt

7. **Undo verspricht, was es nicht gibt.** Stop (Z. 709 ff., `busyIcon(...,'Undo')`), Forget (Z. 1061) und Dismiss
   (Z. 701) zeigen „Undo“. `stop` ist nicht umkehrbar (höchstens Neustart), `history.forget` und `notice.dismiss`
   haben kein Gegenstück in ActionApi. Entweder Bestätigung behalten (heute `Prompt.ConfirmAsync` bei Forget/Remove)
   oder Undo im Kern bauen (M). Profil-Remove im Menü hat im Mockup keine Bestätigung.
8. **Fehlende Kennzahlen gegenüber heute:** Warteschlange `queue` (heute Chip, Z. 185; bei vLLM die einzige
   Last-Angabe), Modelldatei-GB, History-Spalte VRAM max (heute Z. 322), Recent: Prompt-t/s, Uhrzeit, Task.
   „From log“-Einträge (`Seen = null`) haben im Mockup keinen Fall (`ago` braucht eine Zeit).
9. **Zustände fehlen:** `sleeping` (ServerStateNames, `--sleep-idle-seconds`), Aufnahme läuft (`recording` je Server,
   Record wird zu Stop-Record), fehlgeschlagener Start (`launches` mit `failureReason`, `logTail`, `launch.dismiss`/
   `launch.stop`), manuelle Server (`manual`: heute keine Record/Save/Stop-Knöpfe).
10. **Shared RAM vermischt mit Prozess-RAM.** Karte qwen (Z. 725): RAM-Zelle zeigt Spill 1.4 G statt `ramGb`. Es sind
    zwei Werte – eigene Zelle oder Zusatz, sonst fehlt der Prozess-RAM.
11. **Profil bearbeiten nur als Menüpunkt.** Editor für Name, Notiz, Args (heute Z. 132–143) nicht gestaltet; Restart-
    Schalter (`profile.restart` existiert in ActionApi) fehlt im Menü, Idle-Unload-Dialog nur als Eintrag.
12. **Themes:** Mockup reduziert auf Dark/Light (Z. 9, 854). docs/ui.md und ThemeCatalog haben fünf Themes, die TUI
    (`Tui/Palette.cs`) und `theme.set` hängen daran. Entscheidung nötig, sonst Mockup-Farben je Theme abbilden.
13. **Shell (ohne GPU) nicht ruhig genug.** Gemessen mit `html.shell`: die Sparkline-Gruppe `g.move` läuft jede Sekunde
    eine 1-s-Transform-Transition (Z. 321) → Dauerbewegung; `html.shell *{animation:none}` stoppt keine Transitions.
    Im Light-Theme behalten alle `.card` den weichen Schatten `0 6px 18px` (`--shadow`, Z. 19/358). Body-Hintergrund
    `fixed`-Gradient (Z. 44) ist beim Scrollen ohne GPU teuer. Dazu Zahlen-Tween per rAF (Z. 896). Vorschlag:
    `html.shell .spark g.move{transition:none}`, `html.shell{--shadow:none}`, `background-attachment:scroll`,
    Tween in shell überspringen.
14. **Icon-Knöpfe nur mit `title`.** 46 Knöpfe/Links ohne Text und ohne `aria-label` (Details, Record, Stop, Serve,
    Open, History-Aktionen, Filter-Chips). `title` reicht formal als Name, ist aber auf Touch nicht sichtbar und nicht
    zuverlässig vorgelesen → `aria-label` setzen. Zustands-Icons (`.st`, `.mc`, `.res`, `.end`) sind `span` mit
    `title`: Screenreader/Touch erfahren den Zustand nicht → `role="img"` + `aria-label` oder versteckter Text.
15. **Kontrast „fehlender Wert“.** `.na` (muted 65 %) gemessen 3.1:1 (Dark) / 2.9:1 (Light), `.req .ex .na` 2.6/2.4:1 –
    unter 4.5:1 für 11–13-px-Text. Übrige Farben ok (muted 5.5/6.2, warn 11.2/5.8, bad 5.8/5.9, good 13.6/5.8,
    acc 11.3/5.5).

## KLEINIGKEIT

16. Toasts mit Undo verschwinden nach 4.5/6 s (Z. 1068, 1088) – bei Hover/Fokus anhalten.
17. Befehlspalette (Z. 861–877): kein `aria-modal`, kein Fokus-Fang, Einträge ohne `role="option"`/`aria-activedescendant`,
    Fokus kehrt nach Schließen nicht zurück. ☰-Menüs schließen nicht mit Esc oder Klick daneben.
18. Kontext fast voll an Slot-Kachel nur Farbe (rot) + Tooltip (Z. 934); auf der Kachel selbst kein Zeichen.
19. Kopfzeile auf dem Handy 141 px hoch und sticky (≈17 % von 812 px). Trefferflächen 28 px (WCAG 2.2 AA 24 px ok,
    44 px empfohlen); „command line“-Summary 16 px hoch.
20. Im ☰ fehlen der Dichte-Umschalter Auto/Small/Full und im Fenster-Modus die Zurück/Vor-Knöpfe (MainLayout Z. 24–30,
    88–92); ⏻ hat im Mockup keine Zwei-Schritt-Bestätigung (heute `_confirm`). Free VRAM: heutige Bestätigung beibehalten.
21. Ollama: Entladen je Modell (`unload` in ActionApi) wäre jetzt naheliegend, fehlt (heute auch nicht im Monitor).
    Host-Server: „Details“ – serverdetail kennt nur lokale Server; `host.stop` gibt es (S).
22. Temp-Balken 64 % ohne Bezugsgröße (Z. 781); `proposals.de.md` beschreibt noch `mockup-1.html` mit vier Themes.
23. Lost-Server-Dialog (Absturzmeldung, MainLayout) – passt zum neuen Endgrund in der History, im Mockup nicht bedacht.

## Erfüllt

- `prefers-reduced-motion`: CSS schaltet alle Animationen und Transitions ab (Z. 354), JS-Tween springt (Z. 898),
  Forget ohne Ausblenden (Z. 1064).
- Zustand nie nur Farbe: Slots, Serverzustand, Ergebnis und Endgrund haben eigene Icon-Formen; Proxy „nicht möglich“
  mit Durchstrich.
- Einheitlicher Fokusring (Z. 80), `aria-live` für Toasts, Notice `role=status`, `lang="en"` passt zu Strings.cs.
- Keine JS-Fehler, kein Overflow bei 375/460 px, Tabellen werden auf dem Handy zu Zeilen.
