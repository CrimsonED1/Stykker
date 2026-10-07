# User interfaces

There is **one** interface that controls everything: the **web UI** (Razor pages in `src/StykkerLlm.Server/Components`). It
is shown in any browser, on the phone and in the app window **StykkerUI**. The terminal program **`stykker`** is a **display
only**: it shows the state and sends the user to the web UI for anything that changes something (key `w`).

## Web UI (areas, in this order)

| # | Area | Where |
|---|---|---|
| 1 | Header: name, main pages, ☰ menu, free VRAM, stop server | header bar |
| 2 | Status line + notice line (proxy, persistent hints with *Dismiss*, *Open log*) | top of Monitor |
| 3 | Saved profiles (start with pre-check, bench, edit, remove) | Monitor |
| 4 | Running servers (t/s, slots, chips, proxy chip, record, details, save, stop, **Prompt**) | Monitor cards |
| 5 | GPU (load, VRAM, power, temp, clocks, throttling, VRAM per program) | Monitor, `/gpu` |
| 6 | System (CPU, RAM per program) | Monitor |
| 7 | Recent requests | Monitor |
| 8 | History with search, start again, save, forget | Monitor, `/history/{key}` |
| 9 | Model tests: Runs, Results, Catalog, Models; run on this PC / a node / automatic | `/runs`, `/results`, `/catalog`, `/models` |
| 10 | Benchmarks, recordings, compare | `/bench`, `/recordings`, `/compare` |
| 11 | Phone access: Home/VPN switch, six-digit code + QR, approve a device that shows a code, devices and roles | `/phone`, `/approve` |
| 12 | Nodes: search, pair, per-node cards, actions, model comparison | `/nodes` |
| 13 | Prompt tester with tools (read, list, write, edit, cmd/PowerShell; approve per call) | `/prompt` |
| 14 | Settings: theme, limits, *Keep the server running*, servers by URL, about | `/settings` |
| 15 | Bug report: zip with logs, settings, state (no secrets) + GitHub issue link | `/bugreport` |

## Terminal display (`stykker`)

Read-only view of the server state (`StateSnapshot`): no input line, no commands, no options in a release build.

| Shown | Keys |
|---|---|
| Header with clock, GPU, system | – |
| Running servers: state dot, name, port, sparkline, t/s, VRAM, slots, queue | `↑` `↓` pick, `Enter`/`d` details |
| Web address: local, every network address, Tailscale marked | `w` opens the browser signed in (pair URL with the code) |
| Panels: code + QR, recent requests, memory per program, details, help | `c`, `r`, `m`, `d`, `?`; `Esc` closes; `q` quits |

One-shot commands: `stykker status` (the header once), `web`, `stop`, `bugreport <text>`, `help`, `version`.
The display starts the server when none runs and holds it while open (`ServerSource`); tests and the debug switch `--sim`
feed it from the simulator (`SimSource`).

## Rules

- All text from `src/StykkerLlm.Core/Strings*.cs` (English).
- No logic in pages: actions (`ActionApi`) and state (`StateJson`) only. A new function goes into the web UI; the terminal
  display only gets it if it is something to *look at*.
- Themes: five themes in `Core/ThemeCatalog.cs` (colors, radius, glow, sheen); the web UI gets them as CSS variables, the
  terminal as colors (`Tui/Palette.cs`).
- The window renders without GPU (`html.shell`): no endless animations, blur or large soft shadows there; static glow is fine.
