# StykkerLLM

[English](README.md) · **Deutsch**

Monitor und Schaltzentrale für lokale LLM-Server: **llama.cpp** (und Ableger), **Ollama**, **LM Studio** und **vLLM**.
StykkerLLM findet die Server auf deinem Rechner von selbst und zeigt, was sie gerade tun: Tokens pro Sekunde je Slot,
Kontext, VRAM, Anfragen-Log, Aufnahmen, Benchmarks und eine Testreihe für Modelle. Im eigenen Fenster, in jedem Browser,
auf dem Handy oder im Terminal. Nichts verlässt deinen Rechner.

![StykkerLLM Monitor](docs/images/monitor.png)

<sub>Alle Bilder zeigen den eingebauten Simulator (`--sim`): ausgedachte Server und Zahlen. Die Rangliste der Modelltests ist echt.</sub>

## Was es kann

- **Findet Server von selbst**: laufende llama.cpp-Server über die Befehlszeile des Prozesses, Ollama, LM Studio, dazu vLLM
  oder alles andere, was du per URL einträgst (auch in WSL und Docker).
- **Live-Ansicht**: Tokens/s je Slot, Prompt lesen oder schreiben, Kontextbalken, Warteschlange, Verlaufskurve; die Karte
  leuchtet, solange ein Modell schreibt.
- **GPU und System**: Last, VRAM, Leistung, Temperatur, Takt, Drosselung, dazu VRAM und RAM **je Programm** als Balken.
  **Free VRAM** stoppt lokale llama.cpp-Server und entlädt Ollama-Modelle mit einem Klick.
- **Profile und Verlauf**: einen laufenden Server als Profil speichern und später wieder starten (mit Vorprüfung: Port frei,
  Modelldatei da, VRAM-Schätzung); auf Wunsch Neustart nach Absturz und Entladen nach Leerlauf.
- **Stykker-Proxy** auf Port 17500: ein OpenAI- und Anthropic-kompatibler Zugang für alle lokalen Modelle, andere PCs und
  Cloud-Anbieter. Er zählt Denken und Antwort, Tool-Aufrufe und Zeit bis zum ersten Token, aber nie Prompt-Texte.
- **Aufnahmen** mit Zeitleiste je Anfrage, Vergleich zweier Aufnahmen, Export als Markdown/CSV.
- **Benchmarks**: Kontext-Leiter, Tool-Test, parallele Anfragen, mit Warnung bei Rückschritten.
- **Modelltests**: Reihen *basic*, *hard*, *creative* und *agent* (das Modell arbeitet mit echten Werkzeugen in einem
  Wegwerf-Ordner), automatisch bewertet, Rangliste je Modell.
- **Prompt-Tester** an jedem geladenen Modell, mit Werkzeugen (lesen, auflisten, schreiben, bearbeiten, cmd/PowerShell);
  jeden Aufruf gibst du frei.
- **Nodes**: andere PCs mit StykkerLLM koppeln, ihre Server sehen und Modelltests auf sie verteilen.
- **Handy-Zugang**: sechsstelliger Code oder QR-Code, Rollen *Admin* und *Viewer*, Schalter Home/VPN.
- **Fünf Themen**: Deep Sea, Cyber Grid, Space Glass, Obsidian, Phosphor.

| Modelltests | GPU-Details |
|---|---|
| ![Rangliste der Modelltests](docs/images/model-tests.png) | ![GPU-Details](docs/images/gpu.png) |

| Benchmarks | Nodes |
|---|---|
| ![Benchmarks](docs/images/benchmarks.png) | ![Nodes](docs/images/nodes.png) |

## Drei Wege

| Programm | Was es ist |
|---|---|
| `StykkerUI.exe` | Das App-Fenster. Startet den Server bei Bedarf und zeigt die Weboberfläche ohne Browser. Braucht **keinen Grafikspeicher** (0 MB VRAM, Zeichnen in Software). Beim Schließen fragt es: in den Tray, beenden oder abbrechen. |
| `StykkerLLM-Server.exe` | Der Kern: misst und liefert die Weboberfläche auf **http://127.0.0.1:8078**, für jeden Browser und das Handy. |
| `stykker.exe` | Die Terminal-Seite: einzelne Befehle (`stykker status`, `stykker start`, `stykker eval …`) und eine volle TUI. |

![Fenster StykkerUI](docs/images/window.png)

<table><tr>
<td width="30%"><img src="docs/images/phone.jpg" alt="Handy-Ansicht"></td>
<td><img src="docs/images/tui.png" alt="Terminal (stykker)"></td>
</tr><tr><td>Handy (kompakte Ansicht)</td><td>Terminal: <code>stykker</code></td></tr></table>

Alle drei arbeiten mit demselben Server: ein Prozess misst, jede Oberfläche zeigt denselben Stand und dieselben Knöpfe.
Der Server läuft nur, solange du ihn nutzt: Er beendet sich wenige Sekunden nach dem letzten Fenster, Terminal oder Browser-Tab
(außer ein Modelltest läuft noch). Für einen PC, den andere als Node nutzen, in den Einstellungen *Keep the server running*
einschalten oder ihn mit `stykker server start` starten.

## Installation

Voraussetzung: **Windows 10 1809 oder neuer, x64**. Keine .NET-Laufzeit nötig. Das Fenster nutzt WebView2; das ist in
Windows 11 und aktuellen Windows-10-Installationen schon dabei.

1. `StykkerLLM-<version>-win-x64.zip` unter [Releases](https://github.com/CrimsonED1/Stykker-LLM/releases) herunterladen.
2. Irgendwohin entpacken (der Ordner ist portabel) und `StykkerUI.exe` starten.

*Später:* `winget install CrimsonED1.StykkerLLM` und `npm install -g stykker-llm`.

Das Programm ist nicht signiert. SmartScreen meldet vielleicht „Der Computer wurde durch Windows geschützt“: *Weitere
Informationen*, dann *Trotzdem ausführen*. Den Download gegen `SHA256SUMS.txt` aus dem Release prüfen:

```powershell
(Get-FileHash .\StykkerLLM-0.3.1-win-x64.zip -Algorithm SHA256).Hash.ToLower()
```

## Schnellstart

1. Einen Server starten: `llama-server -m model.gguf --port 8081`, oder Ollama, oder LM Studio mit eingeschaltetem Server.
2. `StykkerUI.exe` öffnen. Nach wenigen Sekunden steht der Server da.
3. **Save** speichert ihn als Profil, **Prompt** spricht mit ihm, **Record** nimmt eine Zeitleiste auf, **Proxy** gibt deinen
   Coding-Tools einen einzigen Zugang.

Kein Server zur Hand? Der Simulator:

```bash
stykker sim
```

oder `StykkerLLM-Server.exe --sim --data-dir <leerer Ordner>` für die Weboberfläche mit simulierten Servern. Der Simulator
fasst weder echte Server noch deinen Datenordner an.

## Handy und andere PCs

- **Schalter Home/VPN** (☰ → Phone access, oder `stykker remote on|off`): aus = nur dieser PC, an = im Netz erreichbar.
- **Anmelden**: Das Handy scannt den QR-Code oder tippt den **sechsstelligen Code** ein (10 Minuten gültig, einmal). Oder das
  neue Gerät zeigt einen Code, und du gibst ihn auf einem angemeldeten Gerät frei (☰ → Phone access, oder
  `stykker approve <code>`).
- **Rollen**: *Admin* darf alles, *Viewer* nur zusehen (keine Aktionen, kein Zugangscode).
- **Nodes** (☰ → Nodes): im Netz suchen, einen anderen PC mit seinem Code koppeln, dann seine Server sehen, starten und
  stoppen und Modelltests darauf laufen lassen. Seine Modelle erscheinen auch im Stykker-Proxy als `node/modell`.
  Siehe [docs/nodes.md](docs/nodes.md) (englisch).
- **Messwerte** für Statusleisten und Dashboards: `http://127.0.0.1:8078/api/metrics` (Prometheus-Text, nur dieser Rechner).

HTTPS gibt es noch nicht: im eigenen Netz oder über VPN nutzen, nicht offen im Internet.

## Stykker-Proxy

- Ein lokaler Proxy auf Port **17500** (vorgegeben nur für diesen Rechner). Einschalten oben im Monitor.
- OpenAI-kompatible Clients (Qwen Code, Aider, …): `http://127.0.0.1:17500/v1`, Modell **`stykker`** oder ein Modell aus
  `/v1/models`. Anthropic-kompatible Clients (Claude Code, …): `http://127.0.0.1:17500`.
- `/v1/models` listet alle lokalen Modelle, die der gekoppelten Nodes und anderer Stykker-Rechner, dazu Cloud-Anbieter, die du
  mit Name, URL und Schlüssel einträgst (mit Windows-DPAPI gespeichert, nie wieder angezeigt). Das Feld `model` entscheidet,
  wohin eine Anfrage geht.
- Der Proxy reicht Anfragen unverändert durch und zählt nur Zahlen und Namen. Prompt- und Antworttexte werden nie gespeichert.

## Modelltests

`stykker eval <server|port|url>`, oder *Model tests* in der Weboberfläche. Bereiche: **coding** (der Python-Code des Modells
läuft nach Rückfrage gegen Tests in einem Temp-Ordner; keine Sandbox), **reasoning**, **format**, **tools**, **long context**,
**creative** und **agent** (mehrstufige Aufgaben mit echten Datei- und Shell-Werkzeugen). Ergebnisse landen im Datenordner,
die Rangliste ist je Modell; Läufe lassen sich auf gekoppelte Nodes verteilen. Eigene Reihen: JSON-Dateien unter `eval\` im
Datenordner.

## Protokolle und Fehlerberichte

Jedes Programm schreibt sein eigenes Protokoll (`StykkerLLM-Server.log`, `StykkerUI.log`, `stykker.log`) in `logs` neben dem
Programm, oder in den Datenordner, wenn der Programmordner schreibgeschützt ist. **☰ → Report a bug** (oder
`stykker bugreport <text>`) packt deine Beschreibung, die Protokolle, Einstellungen und den aktuellen Stand in ein Zip –
Schlüssel, Zugangscode, Geräte und Anbieter-Schlüssel sind nie dabei; API-Schlüssel, Tokens, dein Windows-Benutzername und der
PC-Name werden geschwärzt – und öffnet ein vorausgefülltes GitHub-Issue. Hochgeladen wird nichts: Das Zip hängst du selbst an.

## Daten und Datenschutz

- Alles liegt in `%APPDATA%\StykkerLLM\` (Einstellungen, Profile, Verlauf, Aufnahmen, Logs, Ergebnisse). Nichts verlässt den
  Rechner, außer Anfragen an einen Cloud-Anbieter, den du selbst einträgst.
- API-Schlüssel gestarteter Server (`--api-key`, `--hf-token`, …) werden nie gespeichert, angezeigt oder kopiert.
- Zugangscode, Geräte und Serverschlüssel sind an dein Windows-Konto gebunden (DPAPI). Von Geräte-Cookies wird nur ein Hash
  gespeichert.
- Eine gespeicherte Befehlszeile bestätigst du, bevor sie das erste Mal läuft und nach jeder Änderung.

## Selbst bauen

.NET 10 SDK unter Windows:

```bash
dotnet build StykkerLlm.slnx
dotnet test tests/StykkerLlm.Tests
```

Release-Zip: `powershell -ExecutionPolicy Bypass -File build\make-release.ps1 -Version 0.3.1`.
Aufbau und Regeln für Mitwirkende (und Coding-Agenten): [AGENTS.md](AGENTS.md), [docs/architecture.md](docs/architecture.md),
[docs/ui.md](docs/ui.md) (englisch).

## Bekannte Grenzen

- Vorerst nur Windows. Server und `stykker` starten auch unter Linux, aber ohne automatische Erkennung und GPU-Werte; eine
  Linux-Plattform ist geplant.
- GPU-Werte brauchen eine NVIDIA-GPU (NVML).
- Ollama und LM Studio nur lesend (kein Stoppen, kein Speichern).
- Im Netz nur HTTP (siehe oben).

## Mitmachen

Siehe [CONTRIBUTING.md](CONTRIBUTING.md). Sicherheitslücken: [SECURITY.md](SECURITY.md).

## Lizenz

[Business Source License 1.1](LICENSE) (BUSL-1.1): frei für private und nicht-kommerzielle Nutzung (privat, Ausbildung,
Forschung, gemeinnützig). Kommerzielle Nutzung braucht eine Lizenz vom Autor – bitte ein Issue öffnen. Jede Version wird vier
Jahre nach ihrer Veröffentlichung zu Apache-2.0.

## Spenden

<a href="https://www.paypal.me/crimsoned"><img src="https://www.paypalobjects.com/en_US/i/btn/btn_donateCC_LG.gif" alt="Mit PayPal spenden"></a>
