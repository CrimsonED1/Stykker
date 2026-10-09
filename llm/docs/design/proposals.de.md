# StykkerLLM Weboberfläche: Vorschläge (zum Mockup `mockup-1.html`)

Die Nummern passen zu den gelben Markern im Mockup. Aufwand: S = unter 1 h, M = halber Tag, L = mehr.
Pfade relativ zu `src/StykkerLlm.Server/`.

## Ist-Stand (kurz)

- Alles 14 px, Überschriften 15 px, `.big` 34 px, dazwischen 11/12/13/16 px ad hoc. Ziffern nur in `.num` tabellarisch.
- Monitor-Reihenfolge: Proxy-Zeile, aufklappbare Proxy-Einstellungen (große Karte), Hinweis, SAVED, RUNNING, Hosts, GPU,
  System, Recent, History. Das Wichtigste (laufende Server) steht erst nach zwei bis drei Blöcken.
- Serverkarte: Kennzahlen als Satz (`Strings.Stats`) plus Chips, Knöpfe/Links gemischt (Record, Save, Details, Stop),
  alle Slots als volle Zeilen, „t/s“ in jeder Zelle.
- Knöpfe und Status-Pillen sind beide runde Pillen (`.pill` auch auf `<button>` für „→ Proxy“): Zustand und Aktion
  sehen gleich aus. Jeder Knopf hebt sich beim Hover und leuchtet.
- Viele Daueranimationen: Logo dreht/pulsiert, `.dot.on` pulsiert, Glanz auf jedem Balken, Lichtstreif auf busy-Karten,
  `.card` mit `rise` bei jedem Seitenaufruf. In `html.shell` werden sie per Override abgeschaltet.
- Leerzustände: meist nur „none yet“ klein und grau. Rückmeldung auf Aktionen: `_flash`-Text oder Modal.

## Vorschläge

| # | Was | Warum | Datei(en) | Aufwand |
|---|---|---|---|---|
| 1 | **Typo-Skala mit fünf Stufen** (`--t-xs 11`, `--t-sm 12`, `--t-md 13`, `--t-lg 15`, `--t-xl 26`) und `font-variant-numeric: tabular-nums` am `body`; Messwerte immer in `--font-num`. | Ruhigeres Bild, Zahlen springen beim 2-s-Takt nicht mehr hin und her, dichtere Darstellung ohne Lesbarkeitsverlust. | `wwwroot/app.css` | S |
| 2 | **Reihenfolge Monitor:** RUNNING oben links, rechts eine schmale Spalte (≈340 px) mit Hardware und Saved profiles (kompakte Liste); unter 1020 px einspaltig. Proxy-Einstellungen aus dem Seitenkopf in eine eigene Seite oder ein Seitenpanel. | Der wichtigste Inhalt ist sofort sichtbar, breite Bildschirme werden genutzt statt Leerraum rechts. | `Components/Pages/Monitor.razor`, `app.css` | M |
| 3 | **Kennzahlen als beschriftetes Raster** (`<dl class="figs">`: Peak, Avg, Generated, Slots, VRAM, RAM, CPU, Draft/Model file/Shared RAM), Warnwerte farbig. Ersetzt Satz + Chips. | Schneller zu scannen, gleiche Position in jeder Karte, Warnungen fallen auf. | `Monitor.razor`, ggf. `Strings.cs` (Labels) | S |
| 4 | **Kopfzeile als Statusleiste:** Proxy-Zustand als klickbarer Chip (ersetzt Proxy-Zeile oben im Monitor), GPU-VRAM mit Mini-Balken, RAM frei, „Servers 3 · 2 busy“. Aktiver Reiter als Unterstrich statt leuchtender Pille. Logo ohne Daueranimation. | Gesamtzustand auf jeder Seite sichtbar, eine Zeile weniger auf dem Monitor, ruhiger Kopf (auch ohne GPU). | `Components/Layout/MainLayout.razor`, `app.css` | M |
| 5 | **Drei Knopfstufen** (primary, normal, danger) plus `quiet`; Ecken 6 px statt Pille; Status-Badges mit Punkt und 4 px Ecken. Selten genutzte Aktionen (Record, Save, Bench) in „More ▾“. Kein `translateY`/Glow beim Hover. | Aktion vs. Zustand sofort unterscheidbar, weniger visuelles Rauschen, klare Hauptaktion je Karte. | `app.css`, `Monitor.razor`, andere Seiten mit `.pill`-Knöpfen | M |
| 6 | **Slots-Tabelle:** Einheiten in den Kopf („t/s“), idle-Slots zusammengeklappt („+ 2 idle slots“), Kontextbalken mit Zahl daneben, fast voll = rot mit Text. Ebenso in Recent requests: relative Zeit („12 s ago“, absolute im `title`), Einheiten im Kopf. | Bei 4–8 Slots halbiert sich die Kartenhöhe; weniger Wiederholung in Zellen. | `Monitor.razor`, `app.css` | S |
| 7 | **Eine Hardware-Karte** für GPU und System: 2×2-Messwerte (Load, VRAM, Power, Temp mit „clocks free“) und darunter VRAM pro Prozess mit Legende (Farbe, Prozess, GB). | Weniger Karten, die Stack-Bar wird lesbar (heute nur Farben ohne Zuordnung im Blick). | `Monitor.razor`, `Components/StackBar.razor` | M |
| 8 | **Leerzustände mit einem nächsten Schritt** (Titel, ein Satz, ein Knopf), z. B. „No server detected“ → *Start profile*, „No benchmark yet“ → *Run bench*, History leer → Hinweis. Gestrichelter Rahmen statt leerer Karte. | Neue Nutzer wissen, was zu tun ist; die Seite wirkt nicht kaputt. | alle Seiten (Monitor, Bench, Runs, Results, Hosts, Recordings) | S–M |
| 9 | **Mikro-Feedback:** Knopf zeigt während der Aktion „Stopping …“ (`aria-busy`, disabled), danach Toast unten rechts (`aria-live`), bei Stop/Forget/Remove mit *Undo* (falls `ActionApi` das hergibt, sonst nur Bestätigung). Ersetzt `_flash` und manche Modals. | Klarheit, ob ein Klick gewirkt hat; weniger Unterbrechung als Modals. | `MainLayout.razor` (Toast-Bereich), neuer kleiner Dienst neben `WebPrompt`, `Monitor.razor` | M |
| 10 | **Command-Palette (Ctrl+K):** Seiten und Aktionen suchen und ausführen (Profil starten, Server stoppen, Prompt öffnen, Free VRAM, Proxy-Ziel). Ersetzt nicht das ☰, ergänzt es. | Schneller Zugriff für Entwickler, besonders auf Seiten im ☰; gleiche Aktionen wie in `ActionApi`, keine neue Logik in Seiten. | `MainLayout.razor`, `wwwroot/ui.js`, `Strings.cs` | L |
| 11 | **Tastatur und Fokus:** `/` fokussiert die Suche (History, Catalog), einheitlicher Fokusring für alle Elemente, sortierbare Spalten in History (Recent / Best t/s / Runs als Segment). | Bedienbar ohne Maus, sichtbar wo man ist; Sortierung fehlt heute ganz. | `ui.js`, `app.css`, `Monitor.razor` | S–M |
| 12 | **Handy: Tabellen werden Zeilen** unter 640 px (`table.stackable`: Kopf aus, jede Zeile als 2-spaltiges Raster mit kleinen Labels), Kopfzeile zweizeilig mit scrollbaren Reitern, weniger wichtige Status-Chips ausgeblendet, 16 px Rand. | Heute scrollen breite Tabellen seitlich in ihrem Block; auf dem Handy sind Slots/Requests so ohne Wischen lesbar. | `app.css`, Tabellen in `Monitor.razor`, `Phone.razor`, `Hosts.razor` | M |

## Leitplanken (eingehalten im Mockup)

- Kein Einblenden beim Scrollen, keine Parallax-, Blob- oder Glas-Effekte, kein Hero, keine Deko-Icons.
- Glow bleibt über `--glow` je Theme (t/s-Zahl, Balken, aktiver Reiter, Logo), aber statisch.
- Keine Daueranimationen mehr nötig: busy-Server erkennt man am grünen Rand und Punkt mit Ring. Damit werden die
  `html.shell`-Overrides kürzer (nur noch Schatten aus).
- `backdrop-filter` in der Kopfzeile und im ☰ entfällt (nahezu deckender Hintergrund statt Blur).

## Mockup bedienen

Unten links: Theme (Deep Sea, Obsidian, Cyber Grid, Phosphor), Marker an/aus, `shell` (simuliert `html.shell`).
Klickbar: Stop, Start, Free VRAM (Busy-Zustand + Toast), „+ 2 idle slots“, More-Menüs, Ctrl+K bzw. „Search“, `/`.

## Light-Kontraste (Stand mockup-7.html)

Hinweis: Diese Datei beschreibt oben die erste Fassung. Ab Fassung 7 gibt es nur noch zwei Themes: **Dark** und
**Spacepunk Titan** (hell); „System“ wählt bei hellem System Titan, sonst Dark. Kontrast nach WCAG 2.x
(AA: Text ≥ 4,5:1, Grafik/Symbole ≥ 3:1).

### Dark (Karte #111A33)

| Farbe | Wert | Karte |
|---|---|---|
| Text | #E8EEFF | 14,9 |
| gedämpft | #8292B8 | 5,5 |
| fehlender Wert „–“ | #7A87AB | 4,8 (vorher 3,1 mit 65 % Deckkraft) |
| Akzent / llama.cpp | #4FE3FF | 11,3 |
| gut | #6CFFB0 | 13,6 |
| Warnung | #FFC857 | 11,2 |
| Fehler | #FF5C7A | 5,8 |
| Ollama | #B18CFF | 6,6 |
| LM Studio | #FFB35C | 9,7 |
| vLLM | #FF6FB5 | 6,7 |

### Spacepunk Titan (mockup-7.html)

Gerechnet gegen Paneel #CBD0CF, Hintergrund #D9DCDB und Paneel tief #BFC5C4; Display-Fenster gegen #141819.
Die gewählten Lack- und Lampenfarben sind als **Flächen** (Streifen, Plaketten, Lampen mit Ring und Symbol) gesetzt.
Als **Text/Symbol** auf dem Paneel erreichen viele von ihnen AA nicht, dafür gibt es dunklere Textstufen:

| Farbe | Wert | Paneel | Grund | tief | Verwendung |
|---|---|---|---|---|---|
| Text | #1A1E20 | 10,8 | 12,2 | 9,6 | Text |
| Text gedämpft (gewählt) | #52595C | 4,6 | 5,2 | **4,1** | – ersetzt, weil auf „tief“ unter 4,5 |
| Text gedämpft (verwendet) | #484F52 | 5,4 | 6,1 | 4,8 | Labels, Plaketten-Präfix |
| fehlender Wert „–“ | #4F5659 | 4,8 | 5,4 | 4,3 | nur auf Paneel |
| Fuge | #7E8789 | 2,4 | 2,7 | 2,1 | nur Linie (Paneel-Grenze, keine Info allein) |
| Signal-Orange | #E0601F | 2,3 | 2,6 | 2,1 | Fläche: Navi-Unterstrich, Fokus-Halo; **weiße Schrift darauf 3,6 – nicht AA** |
| Primärknopf (verwendet) | #BD5017 | 3,1 | 3,5 | 2,8 | Fläche, weiße Schrift 4,9 (AA) |
| Link/Akzent-Text | #8F3A0C | 4,9 | 5,5 | 4,3 | Links |
| llama.cpp | #C2531A | 3,0 | 3,4 | 2,6 | Streifen/Plakette, weißes Symbol |
| Ollama Petrol | #1F6E73 | 3,8 | 4,3 | 3,4 | Streifen/Plakette |
| LM Studio Ocker | #A67C1E | 2,4 | 2,8 | 2,2 | Streifen/Plakette (Name steht daneben als Text) |
| vLLM Ziegel | #9A3B2E | 4,4 | 5,0 | 4,0 | Streifen/Plakette |
| Host Stahlblau | #4A5A6A | 4,6 | 5,1 | 4,1 | Plakette, weiße Schrift 6,7 |
| Lampe schreibt/ok | #3F8F4E | 2,6 | 2,9 | 2,3 | Lampe mit Ring + weißem Symbol; Text: #256030 (4,8) |
| Lampe liest Prompt | #2F6FA8 | 3,4 | 3,8 | 3,0 | Lampe; Text: #225684 (4,9) |
| Lampe wartet | #949B9D | 1,8 | 2,1 | 1,6 | Lampe mit dunklem Symbol und Ring; Text: #4F5659 (4,8) |
| Lampe Warnung | #D99A1E | 1,6 | 1,8 | 1,4 | Lampe + Warnstreifen; Text: #734F08 (4,7) |
| Lampe Fehler/offline | #B8321F | 3,8 | 4,3 | 3,4 | Lampe; Text: #962817 (5,2) |
| Display: Phosphor | #7CFF9E | Display 14,2 | | | Werte (t/s, Sparkline, VRAM-Zahl) |
| Display: Amber | #FFB347 | Display 10,0 | | | Einheiten, Achsen, „–“ |

Lampen mit Ring (2 px Paneel + 1 px Fuge) bleiben bei 1,6–2,6:1 unter 3:1 für Grafik – der Zustand ist deshalb nie
allein an der Farbe ablesbar: Symbol in der Lampe, Wort im Tooltip/aria-label.
