# StykkerHUD

Live resource monitor for this machine: processes, GPU, CPU and memory. Part of the Stykker family
(`StykkerLLM`, `StykkerNanoCut`) and built on the **shared StykkerLLM design system** — same tokens,
same components, same two themes (*Dark*, *Spacepunk Titan*).

The interface is an instrument panel, not a table dump: every figure is mono with tabular digits,
each state carries a symbol beside its colour, and the curves move only when the data moves.

## What it shows

- **Graphics** — utilisation, VRAM, temperature, board power against its limit, graphics and memory clock (NVIDIA
  via `nvml.dll`), and the split of the load across the engines — 3D, Compute, Copy, Video decode, Video encode —
  as a stacked bar with a legend. The engines come from the same performance counters as the process rows, so the
  figures and the bar can be checked against each other. Without `nvml.dll` (Intel or AMD graphics) utilisation, the
  engine split and the memory in use come from the same Windows counters; temperature, power, clocks and total
  memory then show `–`.
- **Processor** — total load, the five-minute curve, and every core behind a **folded line**: folded it names
  the busiest core, one click opens the 16 bars (which are only built while open).
- **Memory** — used/total RAM and commit charge.
- **Storage and network** — read/write and received/sent as **bars**, each row showing the value against the
  peak of the session (`10.6 MB/s / 23.8 MB/s`). Totals over all physical drives (PDH) and all active adapters.
- **Processes** — 40 rows, sorted by CPU, memory, GPU or name, filterable. Per process: CPU share,
  working set, **GPU share and VRAM from the Windows performance counters (PDH)** — the same numbers
  Task Manager shows — plus thread count, total CPU time, a state lamp and the priority when it is not Normal.
- **Grouped by name and path** — ten processes of the same program **from the same file** become one row `×10`
  with the summed CPU, memory, GPU, VRAM and threads; the arrow on the left opens the ten individual rows (each
  with its own PID and its own menu). A search expands the matching groups **once** — you are looking for a
  process, not a sum — and they can be collapsed again. The group's menu acts on **all** members: *End all 10
  tasks* asks first and then reports `10 of 10 done` (or the first refusal, if one happens).
- **Acting on processes** — every row has a menu: *Open file location*, the five priority levels (the current
  one is marked in the menu) and *End task*, which **asks first** and reports the outcome as a toast.

## Layout

| Path | What it is |
|---|---|
| `src/StykkerHud.Core/` | The model (`Samples.cs`), the measuring loop (`HudService`: one second while a viewer asks, nothing otherwise) and the sampler. Knows no operating system. |
| `src/StykkerHud.Platform.Windows/` | The Windows side: CPU/RAM (`kernel32`/`ntdll`), GPU (`nvml.dll`), GPU per process and disk throughput (`pdh.dll`), network counters, tray icon. Every part is optional — if one is missing the value shows `–`. |
| `src/StykkerHud.Server/` | Blazor Server on `http://127.0.0.1:8079`, local only, with the tray icon. Serves the pages and `/api/snapshot`. |
| `src/StykkerHud.UI/` | `StykkerHUD.exe` — the Photino window (WebView2) around those pages. |
| `tools/shot.mjs` | Measures the rendered page (numbers, colours, geometry, both themes) and writes screenshots into `docs/`. Also measures the metric rows: cells and columns, and how far a cell runs over its column. |
| `tools/check-ui.mjs` | Behaviour checks on the running page: cores folded at first, opening them fills 16 rows, the four throughput bars carry widths and their value/peak text, no metric cell runs over its column, and the graphics card reports its engine split. |
| `tools/check-actions.mjs` | End-to-end test of the process actions: starts a sacrificial process, sets its priority, drives the row menu in the browser, checks that asking protects it and that confirming ends it — plus the refusals (foreign origin, cross-site, non-JSON, system PID). |
| `tools/check-groups.mjs` | Behaviour of the grouping: two processes of the same name and path become one `×2` row, a search expands them, the arrow folds them again, the sums are shown in the folded row, and *End all* ends both — only after asking. |
| `tools/onepager.mjs` | Builds a standalone page out of a draft by embedding the design system (for artifacts). |
| `docs/` | Measurements and screenshots of the current state. |

**Nothing is read while nobody looks.** The server binds the port and answers straight away — measured: port
open after **638 ms**, first answer on `/` after **764 ms**, and that is without a single measurement. The loop
starts with the first `/api/snapshot` request and stops again after **15 seconds** without one; the page only
polls while it is visible, so a closed *or hidden* window costs nothing. Starting again resets the deltas (CPU
times, throughput rates) and begins a **fresh curve**, because the points from before belong to another time
window. The loop lives in the **service**, not in the page, so every viewer reads the same reading.

## Build and run

```powershell
dotnet build StykkerHud.slnx -c Debug

# window (starts the server itself if none runs)
src\StykkerHud.UI\bin\Debug\net10.0\StykkerHUD.exe

# server alone, in the browser
src\StykkerHud.Server\bin\Debug\net10.0\StykkerHUD-Server.exe --port 8079
```

| Switch | Effect |
|---|---|
| `--port <number>` | web port (default 8079) |
| `--no-tray` / `--no-browser` | no tray icon / do not open a browser |
| `--basic` | no system access: every value shows `–` |
| `--design-system <folder>` | where the shared design system is read from (see below) |
| `--gpu` (window) | draw through the GPU; without it the window draws in software, so the monitor does not consume the GPU it measures |

## Acting on processes

End task, priority and *open file location* are the only things the app can do to another process, and they
are fenced in:

- **This machine only** (the server listens on `127.0.0.1`) **and this page only**: a request must carry the
  page's own `Origin` (or `Sec-Fetch-Site: same-origin`) — anything else is refused with `403`.
- **JSON only**: without `Content-Type: application/json` the doors answer `415`, so a form on some other
  website cannot reach them.
- **PID 4 and below, and the server itself, are never touched** — those come back as a refusal, not as a crash.
- **End task asks first** (a bar above the list, naming the process and what it holds); only *Confirm* acts,
  and the result arrives as a toast naming what happened.
- Protected processes answer **"Access denied … run StykkerHUD as administrator"** instead of failing silently.
- Priority offers idle, below normal, normal, above normal and high — **not RealTime**, which can lock up the
  machine. The current level is marked in the menu, and a chip appears next to the name when it is not Normal.
- No path from the UI ends a whole process tree (`tree: true` exists in the API; nothing calls it yet).
- A group row's *End all N tasks* asks **once** for all of them (naming the count and the summed working set) and
  reports `N of M done`, with the first refusal if any member could not be touched.

Verified end to end against a sacrificial process (16/16 checks): the refusals refuse, priority really changes,
asking protects it, cancelling changes nothing, confirming ends it, and the toast names the result.

## The design system is one source, not a copy

The styles are **not** copied into this repository. The server serves them at `/ds` straight from
`C:\_AI\Stykker\Design-System` (the folder the design-system zip was extracted to), so a change
there is visible here at the next reload — one version for the whole family. Another location:
`--design-system <folder>` or the environment variable `STYKKERHUD_DESIGN_SYSTEM`.

Consumed: `tokens.css`, `components/bundle.css` (the component classes), `components/bundle.js`
(the icon sprite), `assets/Logos/app-icon.png` (as the page icon). `wwwroot/app.css` holds only the
few rules the design system does not have (the tool row above the process list, the core grid).

Extended 2026-10-08: this app needed three icons the set did not have — `i-arrow-up`, `i-arrow-down`
(direction of transfer, for network and for disk read/write) and `i-disk` (the drive itself). They went into
the shared folder (`assets/Icons/*.svg`, the sprite in `components/bundle.js`, the list in
`assets/Icons/README.md`), so StykkerLLM sees them too. Watch out: `i-down` and `i-peak` are **trend**
glyphs — a line with a knee — not direction arrows.

Corrected 2026-10-08: `.mgrid` was a fixed twelve columns, so this app's 340px rail got twelve columns of 18px
and the six Graphics figures wrote over each other. It now fits its own columns to the card
(`repeat(auto-fit, minmax(72px, 1fr))`): the rail lays its five figures out as 3+2 at 97px each, none narrower
than 72px. StykkerLLM and NanoCut are untouched — StykkerLLM carries its own `.mgrid` rule in its `app.css` and
does not link the bundle, and NanoCut never uses the class. Related: an empty `.stack`/`.legend` in the rail needs
its own `[hidden]` rule in `wwwroot/app.css`, because the design system's `display:flex`/`grid` outranks the
`hidden` attribute.

## How it should look — `docs/design/mockup-2.html`

A mockup in this family is one self-contained HTML page. `mockup-2.html` is the app as pure surface: no
annotations, no labels — the finished screen. It carries the **same element ids** as the running page, so
draft and build can be measured with the same command. Numbers and curves are simulated, shaped after this
machine's readings. Everything else is built from the shared design system, which the page links instead of
copying, so the draft cannot drift.

![StykkerHUD as it should look, Dark theme](docs/screenshots/mockup-2-dark.png)

*Header strip (CPU, RAM, GPU, VRAM, processes, uptime) with the Dark/Titan switch; hint notice; processor
card with the five-minute curve and 16 core bars; process table with CPU, memory, GPU share, VRAM, disk
read/write, network rates, state lamp and the row menu (`⋯`); rail with the graphics card and its engine
breakdown, the memory composition (in use / cached / free), the storage volumes with read/write bars and a
network curve with received/sent bars; footer with sample rate, history, port and sources. The core bars sit
behind a folded line that names the busiest core.*

The same screen in **Spacepunk Titan**:

![StykkerHUD as it should look, Spacepunk Titan](docs/screenshots/mockup-2-titan.png)

`docs/design/mockup-1.html` is the annotated working draft beside it: the same screen plus the tray menu with the
resident-mode switches (still missing) and the process actions (built on 2026-10-08 — as a confirmation **bar
above the list**, not as the popover this draft drew).

![StykkerHUD mockup 1, the annotated draft](docs/screenshots/mockup-1-full.png)

Regenerate (both themes; `-full.png` is the whole page, `-review.jpg` a small one for image reading):

```powershell
node tools\shot.mjs --url file:///C:/_AI/Stykker/HUD/docs/design/mockup-2.html --out docs\screenshots --name mockup-2 --format png --height 1120
node tools\shot.mjs --url file:///C:/_AI/Stykker/HUD/docs/design/mockup-1.html --out docs\screenshots --name mockup-1 --format png
```

`docs/design/mockup-2-onepager.html` is the same mockup as a **standalone** page: the design system is
embedded instead of linked, so it renders with no other files around and can be published as an artifact.
Regenerate it with `node tools\onepager.mjs`.

## Verifying

```powershell
# the running application (port 8079 must answer)
node tools\shot.mjs --port 8079 --out docs

# any other address, including a local draft
node tools\shot.mjs --url <address> --out <folder> --name <prefix> --format jpeg|png

# behaviour of the running page (cores folded, opening them, throughput bars)
node tools\check-ui.mjs --port 8079

# process actions, end to end, on a process the check starts and ends itself
node tools\check-actions.mjs --port 8079

# grouping: two processes of one name become one row, fold, sums, End all
node tools\check-groups.mjs --port 8079
```

Each run writes `<prefix>-measure.json` (DOM measurements, console problems, failed requests) and the
pictures of both themes. Measuring the DOM beats looking at the picture: colours, fonts, geometry and
overflow are numbers. A run that reports `problems: none` has no 404 and no console error — that is how
the mockup's relative link to the design system is checked.

## Not built yet

- **Affinity** (which cores a process may use) and ending a process tree from the UI — the API takes `tree: true`,
  but no button offers it yet.
- **Throughput per drive and per adapter** — today the bars are totals over all drives and all adapters.
- **Resident mode**: start with Windows, live only in the tray.
- **Own app icon** (`.ico`) and the three icons the design system still lacks: temperature, power and fan —
  those values carry a `title` instead of a symbol.