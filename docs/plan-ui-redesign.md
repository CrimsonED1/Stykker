# Plan: new web interface (Dark + Spacepunk Titan)

Status: **U1–U4 done** (2026-10-07), U5 next. Approved by the owner on 2026-10-07. Work happens on `dev`, package by package; after each package:
build, all tests, a screenshot in Dark and Titan, commit.

Reference: the mockups in `docs/design/` (open them in a browser, controls at the bottom left).
`mockup-7.html` is the target; the earlier ones show the way there. Notes and reviews:
`proposals.de.md` (12 proposals, contrast tables), `review-verify-mockup5.de.md` (data and controls check),
`review-features-mockup5.de.md` (gaps and ideas). These three are in German.

## Decisions

| Topic | Decision |
|---|---|
| Themes | Only **Dark** (from Space Glass) and **Spacepunk Titan** (light), plus **System** (light → Titan, dark → Dark). Deep Sea, Cyber Grid, Obsidian, Phosphor and Space Glass go; a stored old theme becomes Dark. |
| Titan | Matte metal grey, signal orange accent, backend lacquer colours; glow **only** in dark display windows (tokens/s, sparkline). Plates (PORT, HOST, SLOT), lamps with ring **and** symbol, hazard stripes only for real problems. |
| Glow | Stronger than today in Dark; one strength per theme; animations only bound to data (numbers count, sparkline moves, bars glide, working card pulses). |
| No-GPU window (`html.shell`) | Only number and bar transitions; no halos, running lights, sparkline motion, blur or heavy shadows. `prefers-reduced-motion`: no animation. |
| Text | Labels become inline SVG symbols (one style: 16 px, 1.5 px stroke, `currentColor`) with the word in a tooltip/`aria-label`. Every field keeps its place; a value the backend does not report shows a muted "–". |
| Undo | Only for *forget* (history) and *dismiss* (notices). Stop asks in a small popover. |
| Roles | Viewer: actions hidden or disabled with a reason; read-only: notice bar. |
| UI parity | Web is the control surface; `stykker` stays a display (docs/ui.md). The TUI gets the same colours where the terminal allows. |

## Packages

| # | Package | Contents | Test |
|---|---|---|---|
| U1 ✓ | Tokens and themes | New `app.css` token set (surface, text, border, accent, backend and state colours, glow), Dark + Titan + System, settings migration of old themes, `html.shell` and reduced-motion rules, theme switch in Settings and header | unit test migration; screenshots both themes; shell check |
| U2 ✓ | Symbols | One SVG sprite/component (`<Icon Name=… />`) with all symbols from the mockup (VRAM, RAM, CPU, ctx, slots, clients, port, host, draft, restart, idle-unload, tools, thinking, result states, backends) | render test; no missing names |
| U3 ✓ | Frame | Header as status bar (proxy, GPU/VRAM, RAM, servers, search, Free VRAM, menu), two-column monitor (running left; hardware, profiles right), mobile layout (< 640 px tables become rows) | screenshots wide/375 px |
| U4 ✓ | Server cards | One card for all backends with the fixed 10-field row, backend stripe, state lamp, slots as tiles (fill = context, click = details), sparkline in a display window, actions (Prompt, details, record with timer, save as profile, stop with popover), queue chip; Ollama table of loaded models; host cards with host badge | bUnit/markup tests per backend; sim screenshots |
| U5 | Hardware card | GPU and system in one card, VRAM and RAM per program with legend, link to `/gpu` | screenshot |
| U6 | Profiles | Tiles with the fixed six symbols, running = lit with live t/s, start/stop, menu (edit, idle-unload, delete), command line folded; **VRAM forecast** before start (free vs. needed) | unit test forecast; screenshot |
| U7 | Recent requests | Fixed columns: client kind, host, model dot, duration bar, t/s colour scale, tools, thinking, result, age. New data: **client kind**, **tool calls**, **thinking tokens**, **result** per request (proxy tap) | unit tests on the tap; screenshot |
| U8 | History | Grouped by day, run bar, avg/peak t/s, tokens, **end reason** (clean, crashed, stopped from outside – new field), backend filter, `/` search, row actions, forget with undo, link to `/history/{key}` | unit test end reason; screenshot |
| U9 | Proxy panel | All proxy settings in one panel (on/off, port 17500, LAN, target/model, remote Stykker machines, cloud providers with write-only key), opened from the proxy chip and Ctrl+K | existing proxy tests; screenshot |
| U10 | Commands and feedback | Ctrl+K command palette (pages and actions, focus trap), `/` focuses search, toasts, stop popover, undo, consistent focus ring, Server-lost banner, read-only/viewer states | keyboard walk-through in the browser |
| U11 | Hints | "Slower than usual" (compared with the profile's average), VRAM short, hazard stripes in Titan | unit test thresholds |
| U12 | Other pages | Hosts, Runs, Results, Catalog, Models, Bench, Recordings, Compare, Prompt, Phone, Settings, GPU, history detail, bug report: new tokens, symbols and controls; no old theme left | screenshot per page, both themes |
| U13 | Clean-up and docs | Remove old theme code and unused CSS, update README images, docs/ui.md, CHANGELOG | build, all tests, no dead CSS classes |

Order: U1 → U2 → U3 → U4 → U5 → U6 → U7 → U8 → U9 → U10 → U11 → U12 → U13. U7 and U8 need new data in Core; their data part
can come first if the UI waits.

## Not now

From the reviews, left for later: failed start with cancel, state "sleeping", energy per 1k tokens, notifications,
time to first token as a column.
