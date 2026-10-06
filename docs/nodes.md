# Nodes – several PCs paired with one hub

Every installation is a **node** (a server). One of them can act as **hub**: it shows and controls the others.
Window, TUI, phone and coding tools talk only to the hub. Every node stays fully usable on its own; if the hub is
gone, the nodes keep running. (The notes below are the original design, in German.)

**Status:** pairing (both directions, six digits + QR), node list with live state, search on the LAN (UDP 17501),
actions on nodes, distributed model tests with result sync, proxy across nodes and the per-node model comparison are
implemented. Not yet tested with a real second PC. Open: node registers itself at the hub (NAT), HTTPS with a pinned
fingerprint, Wake-on-LAN.

## Idee

Jede Installation ist ein **Node** (der Server, wie heute). Ein Node wird zusätzlich zum **Hub**: dort sieht und steuert man
die ganze Flotte. Fenster, TUI, Handy und Coding-Tools sprechen nur mit dem Hub. Jeder Node bleibt allein voll bedienbar;
fällt der Hub aus, laufen die Nodes einfach weiter.

```
Fenster   TUI   Handy   Coding-Tools (Proxy :17500)
     \     |      |      /
          Hub (Haupt-PC)          Flotte, Warteschlange, Ergebnisse, Cluster-Proxy
      /        |         \        Live-Strom + Befehle, Token je Node
 Node A     Node B     Node C     normale Installationen mit Home/VPN an
```

## Warum Hub statt „alle gleich“ (Mesh)

- Eine Stelle für Ergebnisse, Warteschlange und Proxy: keine Abgleichs- und Konfliktlogik
- Baut direkt auf dem auf, was es gibt: `/api/state`, `/api/stream` (SSE), `/api/action`, Kopplung mit Code, Rollen
- Jeder Node kann Hub werden (Schalter), also kein eigenes Programm

## Kopplung

1. Der Node zeigt Code und QR wie heute (Fenster ☰ → Phone access, `stykker qr`, Web `/phone`). Home/VPN muss an sein.
2. Am Hub: **Im Netz suchen** schickt einen UDP-Broadcast (Port 17501, nur LAN); Nodes antworten mit Name, Port, Version, GPU.
   Ohne Fund (VPN, anderes Netz): Adresse von Hand eingeben.
3. Der Hub schickt den Code an `POST /node/pair`; der Node antwortet mit einem eigenen **Node-Token** (256 Bit, auf dem Node
   nur als Hash gespeichert), Rolle `hub`.
4. Der Hub speichert Adresse, Token und Namen (`nodes.json` im Datenordner) und abonniert `/api/stream` des Nodes.
5. Der Node zeigt „Gekoppelt mit HAUPT-PC“ unter Geräten, mit **Trennen** (zieht das Token zurück).

Sicherheit: wie beim Handy nur LAN/VPN und ohne HTTPS. Das Token geht im Klartext durchs Heimnetz, das ist dieselbe Annahme
wie beim Gerätecookie heute. Später möglich: selbst signiertes Zertifikat, dessen Fingerabdruck bei der Kopplung mitkommt
(Vertrauen beim ersten Mal, danach festgenagelt).

## Was die Flotte kann (Pakete)

| Paket | Inhalt | Nutzen |
|---|---|---|
| N1 | Kopplung, Suche, Node-Liste, online/offline, Trennen; `nodes.json`; Rolle `hub` im AccessGate | Grundlage |
| N2 | Seite **Nodes** (Karten: GPU, VRAM, laufende Modelle, t/s, Fehler) in Web, Fenster, TUI; Umschalter oben „Alle / Node …“; Befehle (Start, Stop, VRAM frei) gehen an den gewählten Node | alles auf einen Blick, Fernsteuerung |
| N3 | Verteilte Tests: die Eval-Warteschlange gibt Aufträge an Nodes, auf denen das Modell liegt und VRAM frei ist; Ergebnisse landen zentral (Spalte Machine) | Rangliste über alle PCs, doppelte Geschwindigkeit |
| N4 | Cluster-Proxy: `:17500` am Hub leitet nach Modellname zu dem Node, auf dem es läuft (sonst startet es dort, wo es liegt) | Coding-Tools sehen einen Endpunkt für alle Modelle |
| N5 | Benchmarks und Modell-Liste je Node vergleichen (welche GGUF wo, wer ist schneller) | Hardware-Vergleich |
| N6 (später) | Node meldet sich selbst beim Hub (für Rechner hinter NAT), HTTPS mit Fingerabdruck, Wake-on-LAN | Komfort |

Alle UIs gleich (UI-SPEC.md): Texte in Strings, Logik in Core (`NodeRegistry`, `NodeClient`, `NodeDiscovery`), Tests für
Kopplung, Token-Widerruf, Verteilen der Warteschlange und das Routing des Proxys (mit Simulator-Nodes, ohne echtes Netz).
