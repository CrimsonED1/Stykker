# User interfaces

There is **one** interface that controls everything: the **web UI** (Razor pages in `src/StykkerLlm.Server/Components`). It
is shown in any browser, on the phone and in the app window **StykkerUI**. The terminal program **`stykker`** is a **display
only**: it shows the state and sends the user to the web UI for anything that changes something (key `w`).

## Web UI (areas, in this order)

| # | Area | Where |
|---|---|---|
| 1 | Header: name, main pages, ☰ menu (view, labels, **Hardware bar**, **Alerts** per browser), free VRAM, stop server; tab title and favicon show t/s or a lost server | header bar |
| 1a | Hardware bar: VRAM, GPU, CPU, RAM, fullest drive, disk I/O, network, GPU temperature in two rows of four (`Core/MiniBar.cs`, draft `docs/design/minibar-preview.svg`); fields link to `/gpu`, the monitor's hardware (`#hw`) and `/disks`; a faint line of the last 60 readings behind each (`MiniHistory`); on the phone VRAM, GPU, temperature and *+5*; off with the switch in ☰ | every page except the Monitor |
| 1b | Phone (≤ 640 px): the main pages as a fixed bar at the bottom with symbols | every page |
| 1c | Messages for activity events (restart, idle unload, finished tests and benchmarks) with *Show* → `#activity`; not on the Monitor, not for a crash (banner) | every page except the Monitor |
| 2 | Status line + notice line (proxy, persistent hints with *Dismiss*, *Open log*) | top of Monitor |
| 3 | Saved profiles (start with pre-check, bench, edit, remove, start script download) | Monitor |
| 4 | Running servers (t/s, slots, chips, proxy chip, record, details, save, stop, **Prompt**; prompt t/s and time to first token, idle time with unload countdown) | Monitor cards, `/serverdetail/{key}` |
| 4a | Model view: name, phase now, MoE experts k of n with active parameters, layer strip GPU/CPU (attention above, experts below; from `-ngl`, `--cpu-moe`, `--n-cpu-moe`, `-ot`), slots, requests through the proxy (thinking, answering, tool) (`Core/ModelAnatomy.cs`, `Components/ModelView.razor`); chip *k/n* on the card; *Storage*: load mode, buffers from the load log, mapped part in RAM, hard page faults, reads from the model's drive while paging (`Core/ModelPaging.cs`) | `/serverdetail/{key}` |
| 5 | GPU (load, VRAM, power, temp, clocks, throttling, VRAM per program) | Monitor, `/gpu` |
| 6 | System (CPU, RAM per program) | Monitor |
| 7 | Recent requests (ttft column, filter *problems*, *Show more*, download `requests.csv` and `proxy.csv`) | Monitor |
| 8 | History with search, start again, save, forget; start script download on the details page | Monitor, `/history/{key}` |
| 8a | Activity: crashes with cause, restarts, idle unloads, finished model tests and benchmarks, with time | Monitor |
| 9 | Model tests: Runs, Results, Catalog, Models | `/runs`, `/results`, `/catalog`, `/models` |
| 10 | Benchmarks, recordings, compare | `/bench`, `/recordings`, `/compare` |
| 11 | Phone access: Home/VPN switch, six-digit code + QR, approve a device that shows a code, devices and roles | `/phone`, `/approve` |
| 12 | Model hosts: pairing code, per-host cards (GPU, servers, model files, start/stop), host servers on the monitor | `/hosts`, Monitor |
| 13 | Prompt tester with tools (read, list, write, edit, cmd/PowerShell; approve per call); answers as Markdown with copy, figures per answer, compare two models side by side | `/prompt` |
| 14 | Settings: theme, limits, *Keep the server running*, servers by URL, about | `/settings` |
| 15 | Bug report: zip with logs, settings, state (no secrets) + GitHub issue link | `/bugreport` |
| 16 | Keyboard shortcuts (`?`), empty pages with the next step | everywhere |

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
- Every form field has a name: a label connected with `for`/`id`, an enclosing label or `aria-label` (test N111). An empty page says what will appear there and the next step (`EmptyState`).
- No logic in pages: actions (`ActionApi`) and state (`StateJson`) only. A new function goes into the web UI; the terminal
  display only gets it if it is something to *look at*.
- Themes: *Dark* and *Spacepunk Titan* in `Core/ThemeCatalog.cs` (the terminal's colors and the window's login and dialog pages),
  plus *System* (`ui.js` picks by `prefers-color-scheme`). The web UI reads its colors, radius and glow from the design system's
  `shared/design-system/tokens.css` under `html[data-theme]` (one source, see `shared/design-system/README.md`); the terminal
  uses the colors of `Tui/Palette.cs`. Titan's shapes (joints, plates, chamfer, hazard stripes) live
  in the Titan section of `app.css`. Design reference: `docs/design/mockup-7.html`, plan `docs/plan-ui-redesign.md`.
- Symbols: one sprite (`Components/IconSprite.razor`), used via `<Icon Name="…" Title="…" />`; a value a backend does not
  report keeps its place as a muted "–" with the reason in the tooltip. A state is never shown by colour alone.
- The window renders without GPU (`html.shell`): no endless animations, blur or large soft shadows there; static glow is fine.
