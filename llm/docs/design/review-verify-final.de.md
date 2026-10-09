# Prüfbericht: neue Weboberfläche StykkerLLM (Stand 6c6a3fd, Branch dev)

Geprüft: Plan `docs/plan-ui-redesign.md` (U1–U14) gegen den Code; Korrektheit, Sicherheit, Leistung und Barrierefreiheit im Code;
im Browser Sim-Server http://localhost:8090 mit Dark und Titan, bei 1400 px und 375 px (Monitor, Hosts, Settings, Befehlsfenster).
Nichts im Repo geändert. Proxy einmal über Ctrl+K aus- und wieder eingeschaltet (Endzustand wie vorher: an, :17590).

Hinweis: Das Thema des Sim-Servers steht auf **Dark**, nicht auf „System“ (Settings zeigt Dark aktiv, `<meta stykker-theme>` = dark).
Titan habe ich deshalb nur im Browser angesehen, indem ich das Meta-Tag dort auf `titan` gesetzt habe; die Einstellung blieb unverändert.

**Ergebnis:** Kein Muss-Fehler. Alle Pakete sind im Wesentlichen umgesetzt; keine Konsolenfehler, kein seitliches Scrollen
(1400 und 375), alle `<use href>` des Sprites gefunden, Ctrl+K / Pfeile / Enter / Esc / Tab-Fang gehen. Schreibaktionen sind
auf dem Server gesperrt (ActionApi.cs:106–107 prüft Rolle und Read-only); die Funde unten betreffen UI-Zustände, eine
Abweichung vom Plan und ein paar Randfälle.

Zählung: **muss 0 · sollte 8 · Kleinigkeit 13**

---

## Sollte

1. **Befehlsfenster: Enter führt den Eintrag einer veralteten Liste aus.** `CommandPalette.razor:241,309–316`. `Key(e, items)`
   bekommt die Liste vom letzten Zeichnen. Wer schnell tippt und dann Enter drückt (Blazor Server, Hin und Rück über die Leitung),
   löst womöglich einen Eintrag der ungefilterten Liste aus, z. B. „Monitor“ statt „Turn the proxy off“. Im Browser zu sehen: Direkt
   nach dem Tippen war die Liste noch ungefiltert. *Vorschlag:* in `Key` die Liste neu bauen
   (`var items = Items().Where(i => Hit(i.Label)).Take(12).ToList();`) statt der Closure-Liste.

2. **Viewer sieht auf den Profil-Kacheln und beim Hinweis aktive Schreibknöpfe.** `Monitor.razor:259` (Stop), `:263` (Start),
   `:273/279/282` (Bearbeiten, Leerlauf, Entfernen), `:25` (Hinweis schließen): Gesperrt wird dort nur bei `ro`, nicht bei `!CanWrite`.
   Start tut beim Viewer stumm nichts (`Start` prüft CanWrite). Entfernen zeigt erst den Bestätigungsdialog und danach „Viewer only“.
   Das widerspricht der Plan-Entscheidung „Viewer: actions hidden or disabled with a reason“. *Vorschlag:* überall
   `disabled="@(ro || !CanWrite)"` und als Title den Grund (`Strings.ViewerOnly` / `ServerReadOnlyHint`). Das gilt auch für
   die History-Knöpfe `:164–167`, die zwar gesperrt sind, deren Title aber keinen Grund nennt.

3. **Hinweis „Dismiss“ ohne Rückgängig (Plan U10/Entscheidung „Undo“).** `Monitor.razor:25` ruft `notice.dismiss` sofort auf.
   Laut Plan gibt es Rückgängig für *forget* und *dismiss (notices)*; umgesetzt ist es nur für forget. *Vorschlag:* wie
   `Forget` den Hinweis lokal ausblenden und `Toast.ShowUndo(..., commit: () => Act("notice.dismiss"))` nutzen, oder die Abweichung im
   Plan festhalten.

4. **Liste `ServerWatcher.Finished` wird beim Zeichnen ohne Sperre gelesen.** `ServerWatcher.cs:55,491–492,795–796` (`List`,
   `Insert/RemoveAt` beim Pollen) gegen `Monitor.razor:82–85` → `RequestRows.Build` (`OrderByDescending` kopiert die Liste). Der
   Monitor zeichnet jede Sekunde neu. Ein gleichzeitiges `Insert` kann eine Lücke oder einen null-Eintrag liefern (NRE bei `f.Seen`)
   oder eine „Collection was modified“-Ausnahme werfen. *Vorschlag:* `Finished` unter einer Sperre ändern und eine Kopie herausgeben
   (`IReadOnlyList<FinishedRequest> FinishedSnapshot { get { lock(_fin) return _finished.ToArray(); } }`).

5. **Server-Stop-Knopf ⏻ im Kopf ruft `Life.StopApplication()` direkt auf, ohne Rollenprüfung im Handler.**
   `MainLayout.razor:74,78`. Geschützt ist er nur durch `disabled` am ersten Knopf; der zweite Knopf („Wirklich“) prüft nichts.
   Ein manipulierter Blazor-Client kann Ereignisse an gesperrte Knöpfe schicken. Alle anderen Schreibwege laufen über
   `ActionApi` oder prüfen `CanWrite` im Handler (Monitor.razor:583–585 sagt das ausdrücklich). *Vorschlag:* über
   `Web.RunAsync(new ActionRequest { Action = "shutdown" })` gehen (wie Settings.razor:178) oder im Handler `if (Viewer.IsViewer) return;`.
   Dasselbe gilt für `FreeVram`: Es läuft schon über die Aktionen, also ok.

6. **Telefon (375 px): Der klebende Kopf nimmt etwa 375 von 812 px ein (≈ 45 %) und bleibt beim Scrollen stehen.**
   Seite Monitor, `header.top` (app.css:12 `position:sticky`; bei small 4 Zeilen: Marke, 3 Chip-Zeilen, Reiter). Auf dem
   Telefon bleibt so wenig Platz für Inhalt. *Vorschlag:* bei `[data-view=small]` den Kopf nicht kleben lassen
   (`position:static`) oder nur Marke und ☰ kleben lassen; die Status-Chips einzeilig mit `overflow-x:auto`, Free VRAM und ⏻ ins ☰.

7. **Hosts: „Remove“ entfernt einen gekoppelten Host ohne Rückfrage.** `Hosts.razor:93,172` → `host.remove`
   (ActionApi.cs:486) löscht sofort. Der Host muss danach neu gekoppelt werden. *Vorschlag:* `Prompt.ConfirmAsync`
   (wie beim Entfernen eines Profils, Monitor.razor:671) oder Rückgängig per Toast.

8. **Befehlsfenster für Screenreader stumm.** `CommandPalette.razor:241–252`: Der Fokus bleibt im Eingabefeld, die Auswahl
   ändert sich nur über die Klasse `sel`. Es fehlen `aria-activedescendant`, `aria-controls` und `id`s an den Optionen. Nach dem
   Schließen geht der Fokus auf `<body>` statt an das vorher fokussierte Element (im Browser geprüft: `activeElement` = BODY).
   *Vorschlag:* Optionen mit `id="cmdk-opt-@i"`, am Input `role="combobox" aria-expanded="true" aria-controls="cmdk-list"
   aria-activedescendant="cmdk-opt-@_sel"`. Beim Öffnen `document.activeElement` merken (ui.js) und beim Schließen zurücksetzen.

## Kleinigkeiten

1. **SMBIOS: Typcodes falsch zugeordnet.** `Smbios.cs:71`: `0x13` ist laut Spezifikation **DDR2** (nicht SDRAM, SDRAM = `0x0F`);
   `0x13 + 6` = `0x19` ist FBD2, nicht DDR2. Außerdem wird `0xFFFF` beim Takt (`:42–43`) als 65535 MT/s genommen; laut SMBIOS 3.3 steht
   der Wert dann im erweiterten Feld (Offset 0x54/0x58). Und ein negativer Längenwert im Kopf (`:20`) würde `AsSpan` werfen lassen.
   *Vorschlag:* `0x0F => "SDRAM", 0x13 => "DDR2"`, `0x19` streichen. `0xFFFF` als 0 behandeln oder das erweiterte Feld lesen
   (len ≥ 0x5C). `len` auf `≥ 0` klemmen. Einen Testfall für DDR2 ergänzen.

2. **Ende eines Laufs: Absturz ohne erkannte Ursache wird als „von außen beendet“ gespeichert.** `ServerRegistry.cs:181–190`: `end`
   wird nur dann „crashed“, wenn `CrashAnalysis.Guess` etwas findet. Das Banner „Server verloren“ erscheint trotzdem, die History
   zeigt aber das gelbe „outside“-Symbol. *Vorschlag:* ein eigener Wert „lost“ (unbekannt), oder „crashed“, wenn der Log-Schluss
   auf einen Fehler deutet; mindestens im Tooltip „Ursache unbekannt“.

3. **Slot-Kacheln sind nicht klickbar** (Plan U4: „click = details“). `ServerCard.razor:118` ist ein `span` nur mit Tooltip.
   *Vorschlag:* als `<a href="serverdetail/{key}#slot-{id}">` bzw. `button`, oder den Plan anpassen.

4. **History ohne Backend-Filter** (Plan U8). `Monitor.razor:131` hat nur all/running/crashed. Das ist vertretbar, weil die History
   nur llama.cpp führt (`hdot` ist fest `--b-llama`, :153). *Vorschlag:* im Plan als bewusst weggelassen vermerken.

5. **`html.shell`: Lichthöfe bleiben teilweise.** Plan: „no halos … or heavy shadows“. Abgeschaltet sind Animationen, Blur,
   Kartenschatten, Spark-Filter und Toast/Cmdk-Schatten (app.css:207–218, 427–429, 548, 605, 629). Es bleiben `.bar>div`
   `box-shadow … 0 0 12px` (:229), mehrlagige `text-shadow` an `.brand`, `.big`, `.kpi`, `td.num b` (:225–238) sowie `.spark-tip`
   (:242–243). Bei jedem Takt neu gezeichnet kostet das ohne GPU wenig, aber messbar. *Vorschlag:*
   `html.shell .bar>div,html.shell .spark-tip{box-shadow:none} html.shell .big,html.shell .kpi,html.shell .brand .logo,html.shell .brand .acc,html.shell td.num b{text-shadow:none}`.

6. **Befehlsfenster: Tab, Shift und Strg setzen die Auswahl auf den ersten Eintrag zurück.** `CommandPalette.razor:317`
   (`default: _sel = 0`). Im Browser gesehen: ArrowDown, dann Tab, und die Auswahl stand wieder oben. *Vorschlag:* nur bei
   Zeicheneingabe zurücksetzen (z. B. in einem `@bind:after` für `_q`), nicht bei jedem anderen Key.

7. **Befehlsfenster: Fehler beim Proxy-Umschalten wird verschluckt.** `CommandPalette.razor:288` `Toggle(out _)`.
   *Vorschlag:* den Fehler als Warn-Toast zeigen (`Toasts` injizieren).

8. **Monitor: Direkte Engine-Aufrufe prüfen `CanWrite`, aber nicht `Host.ReadOnly`.** `Monitor.razor:542,587–647` (SetLan,
   ToggleProxy, AddRemote, Provider, Serve). Die UI sperrt sie bei `ro`, der Handler nicht. *Vorschlag:*
   `private bool CanWrite => Web.CanWrite && !Host.ReadOnly;`.

9. **Hosts-Seite beachtet Read-only nicht.** `Hosts.razor:17,81,85`: Es wird nur `Viewer.CanWrite` geprüft. Auf dem Server ist
   das gesperrt (ActionApi.cs:107), Knöpfe und Kopplungscode erscheinen aber trotzdem. *Vorschlag:* `Viewer.CanWrite && !Host.ReadOnly`.

10. **Toast-Commit nach geschlossenem Tab:** `Monitor.razor:687` ruft bei einem Fehler in `Act` `Prompt.InformAsync` auf. Ist die
    Verbindung weg, wartet der Dialog ins Leere (der Task hängt, kein Absturz). *Vorschlag:* im Commit `Web.RunAsync` direkt
    aufrufen und einen Fehler nur protokollieren bzw. als Toast zeigen.

11. **Icon-Knöpfe ohne `aria-label`:** ⏻ (`MainLayout.razor:78`) und ☰ (`:82`) haben nur `title`. Das ist als Name ausreichend,
    aber uneinheitlich. *Vorschlag:* `aria-label` ergänzen. Die Kopfzeile der „Recent requests“ ist `aria-hidden`
    (`Monitor.razor:95`), damit lesen Screenreader Zahlen ohne Spaltenbezug. *Vorschlag:* `role="table"/"row"/"columnheader"`
    oder `aria-label` je Zelle.

12. **Lampen ohne Symbol:** Die Proxy-Quellen-Lampen (`Monitor.razor:349,373`, app.css:517–519) unterscheiden sich nur über die Farbe
    (mit Title). Plan Titan: „lamps with ring **and** symbol“. *Vorschlag:* ein kleines `r-ok`/`r-err`-Icon statt des Punkts
    oder zusätzlich.

13. **Darstellung:** Die Proxy-Adresse im Panel ist bei 1400 px abgeschnitten („http://127.0.0.1:17590/…“, Monitor.razor:315);
    Kopieren geht, sehen kann man sie nicht ganz. *Vorschlag:* eigene Zeile unter dem Schalter oder `/v1` weglassen.
    Auf 375 px überdeckt die Slot-Nummer die Füllkante der Kachel (ServerCard.razor:119), und die History-Suche ist
    auf „Search his…“ gekürzt.

## Ohne Befund

- U1 Themen und Migration, U2 Sprite (keine fehlenden Namen im DOM), U3 Kopf und zwei Spalten, U5 Hardware-Karte, U6 Profile mit
  VRAM-Vorhersage, U7 RequestRows (Zuordnung gierig nach kleinstem Zeitabstand ≤ 4 s, jede Proxy-Aufzeichnung nur einmal;
  Host-Zeilen bekommen bewusst keine Proxy-Daten), U9 Proxy-Panel (Schlüssel nur schreibend), U11 SpeedHint (Schwellen sinnvoll,
  null bei zu wenigen Messpunkten), U12, U13, U14 (Anzeige DDR5-6000 · 2×32 GB · ≈96 GB/s stimmt rechnerisch).
- Toasts: je Verbindung (Scoped), Liste unter Sperre, Undo und Ablauf schließen sich gegenseitig aus, Commit-Ausnahmen werden
  protokolliert.
- `prefers-reduced-motion`: Die globale Regel schaltet alle Animationen und Übergänge ab (app.css:197–198).
- Fokusrahmen: `:focus-visible` einheitlich (app.css:627, 634), Eingabefelder mit eigenem Rahmen.
- Konsole ohne Fehler; kein seitliches Scrollen bei 1400 und 375 px; Settings und Hosts in Dark sauber.
