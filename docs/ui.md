# UI parity: web and TUI

There are two user interfaces: the **web UI** (Razor pages in `src/StykkerLlm.Server/Components`, shown in a browser,
on the phone and in the window `StykkerUI`) and the **TUI** (`stykker`). Both offer the same functions in the same
order. A new feature lands in both, or this file says why not.

## Areas (same order everywhere)

| # | Area | Web | TUI |
|---|---|---|---|
| 1 | Header: name, main actions, menu, free VRAM, stop server | header bar + ☰ | `/` command list, `/freevram`, `/server stop` |
| 2 | Status line + notice line (proxy, persistent hints with *Dismiss*, *Open log*) | top of Monitor | header line, `/notice` |
| 3 | Saved profiles (start with pre-check, bench, edit, remove) | Monitor | `/saved`, `/start` |
| 4 | Running servers (t/s, slots, chips, proxy chip, record, details, save, stop, **Prompt**) | Monitor cards | status table, `/show`, `/stop`, `/unload`, `/prompt` |
| 5 | GPU (load, VRAM, power, temp, clocks, throttling, VRAM per program) | Monitor, `/gpu` | `/gpu` |
| 6 | System (CPU, RAM per program) | Monitor | `/gpu` |
| 7 | Recent requests | Monitor | `/recent` |
| 8 | History with search, start again, save, forget | Monitor, `/history/{key}` | `/history`, `/show` |
| 9 | Model tests: Runs, Results, Catalog, Models; suites basic, hard, creative, agent; run on this PC / a node / automatic | `/runs`, `/results`, `/catalog`, `/models` | `/eval runs·results·models·catalog·try·all·spread` |
| 10 | Benchmarks, recordings, compare | `/bench`, `/recordings`, `/compare` | `/bench`, `/recordings` |
| 11 | Phone access: Home/VPN switch, six-digit code + QR, approve a device that shows a code, devices and roles | `/phone`, `/approve` | `/remote`, `/qr`, `/approve`, `/devices`, `/role` |
| 12 | Nodes: search, pair, per-node cards, actions, model comparison | `/nodes` | `/nodes` |
| 13 | Prompt tester with tools (read, list, write, edit, cmd/PowerShell; approve per call) | `/prompt` | `/prompt`, `/prompt tools`, `/prompt auto` |
| 14 | Settings, theme, about | `/settings` | `/theme`, `/about` |

## Known differences

- The TUI shows details as text (`/show`) instead of a separate page.
- Logs are opened in the web UI (`/log`); the TUI names the path.
- Settings for limits and server URLs exist only in the web UI.

## Rules

- All text from `src/StykkerLlm.Core/Strings*.cs` (English).
- No logic in pages or TUI commands: actions (`ActionApi`) and state (`StateJson`) only.
- Themes: five themes in `Core/ThemeCatalog.cs`; the web UI gets them as CSS variables, the TUI as terminal colors.
