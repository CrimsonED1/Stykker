# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## Unreleased

### Added
- **Proxy failover.** The default model can have up to two fallback models (proxy dialog, tab *Routing*). A request goes
  to the first fallback that can take it when the default model is offline, still loading, just crashed (no connection),
  answers 503, has too little context (checked before sending, and the backend's "exceeds the available context" 400), or
  when a cloud default answers 429 or 5xx. This happens before a byte reaches the client, for OpenAI clients and for the
  Anthropic bridge (Claude Code) alike; the client gets the fallback's answer instead of an error. A target that failed is
  skipped for 20 s, so the next requests go straight to the fallback. Without a fallback the proxy waits up to 30 s for a
  crashed model to come back (crash restart) and sends the request again (setting *Wait for a crashed model*, on by
  default; a model that loads again is waited for like a loading one). A model asked for by name never jumps to another
  model, and a local 500 (for example a tool-call parse error) is passed on unchanged. New routes `fallback` and `retry` in
  the request list and `proxy.csv`; the proxy card's *Routed* line counts *rescued* requests. Actions `proxy.fallback`
  (Number = slot, Arg = choice value) and `proxy.restartwait`.
- **`GET /health` on the proxy.** The proxy answers it itself: 200 with status `ok` (default model ready) or `fallback`
  (only a fallback ready), 503 with `loading` or `unavailable`; names, models, context and readiness of the default model
  and the fallbacks, no provider addresses and no keys.
- **Benchmark page reworked.** Each result shows two small charts, read and write t/s by context size (log scale, one
  line per result). Up to four results can be ticked and compared side by side: overlaid charts, one table and a
  Markdown export of all of them (`/api/bench/compare?ids=`). The tool call reads as passed or failed instead of
  meaningless speeds; parallel requests show the combined throughput (Σ); the read speed of very short prompts is
  dimmed. Context sizes that do not fit the target's context per slot are disabled, and choosing a target preselects
  the ones that fit, up to 32k. The steps of a running benchmark show in a live table. Results can be deleted
  (`bench.delete`) and filtered by target. A saved profile started for a benchmark can be stopped again afterwards.
- **Profile note** shows two lines on the tile and opens fully on click or tap; the editor field takes several lines.
- **Live status of the model tests.** While the classifier or the vision test runs, the proxy dialog shows a live line
  above the table: the model being asked, its phase (sending, waiting for the first byte, receiving, pause, rate-limit
  wait), the seconds with a bar up to the 60 s limit, the counts (done, ok, failed, skipped) and a rough time left. The
  running model sits at the top of the table with a spinner. Known errors read short in the table ("no image input",
  "provider busy (limit)", "not available for this account", "slower than 60 s"); the original message stays in the
  tooltip and in the CSV.
- **Disks.** The monitor's Hardware area gets a card **Disks**: one row per drive with its letter, type (NVMe, SSD,
  HDD, USB or unknown), used and free space, read and write per second and busy percentage. When a model server reads
  more than 200 MB/s, a line says so ("<server> reads 1.2 GB/s from C:"); the drive comes from the model's path. The
  details are on `/disks`: the history of the last five minutes per drive, the physical disks and the programs with the
  most disk traffic. The figures come from the Windows counters (`shared/`, `Stykker.Shared.Disk`); the state JSON has a
  `disks` block (additive). The simulator has an NVMe and an HDD in Debug builds.
- **Route of every proxy request.** Each request through the proxy records the way it went: `named` (model asked for by
  name), `classifier` (Claude Code's safety check, auto mode), `vision` (request with an image) or `default`. `proxy.csv`
  gets a `Route` column at the end; an existing file with the old header is kept and the new rows go to `proxy.v2.csv`.
  In "Recent requests" a small `auto` or `image` tag marks the row, and requests to cloud targets get their own rows
  (name of the target, host `cloud`), which the monitor did not list before.

### Changed
- **Benchmark page.** A result shows **Read t/s** and **Write t/s** (were "Best prompt t/s" and "Chat t/s"; the Markdown and
  CSV exports use the same names, the CSV columns are now `ReadTokensPerSec` and `WriteTokensPerSec`). A column **vs last**
  compares each test with the last earlier run of the same target: green from +5 %, red from -5 %, grey otherwise. While a
  run goes, "about N min left" estimates the rest from the speeds so far. The parallel box is disabled with a hint when the
  server has one slot and shows no hint when it has several. The line under "Run benchmark" names the time and the result
  ("last run 20:42 · ok") instead of the model name again.
- **Ports in one block.** The suite moves to 17400–17499, below the LLM proxy (17500): the hub (StykkerHUD) listens on
  **17400** (was 8079), StykkerLLM's web and API on **17401** (was 8078), StykkerSYS on **17402** (was 8077). Foreign
  services start at 17450. The window, `--port`, the `--hub-port` default and the search answer (UDP 17501) follow; the
  proxy (17500) and the search port (17501) stay. An explicit `--port` still wins.
- **Old port 8078 for installed model hosts, for a while.** StykkerHost agents installed before this change saved an
  address with `:8078`. On the default port the server also listens on 8078, and there only for `/hosts/connect` and
  `POST /hosts/pair`. Every other request on 8078 gets `410 Gone` with the text "Stykker LLM moved to port 17401.". A host
  that connects over 8078 writes one line to the log asking for a new pairing; if 8078 is in use, the server starts
  without it and logs that. **This transition is removed in a later version.** Pair the hosts again with the address on
  port 17401 before then.
- **Host name check on every path.** The server refuses a request whose Host header does not name this PC, before anything
  else: `/pair`, `/api/ping`, `/api/state` and all other paths, also with Home/VPN off. The two model-host ways
  (`/hosts/connect`, `POST /hosts/pair`) are the exception; they keep their own token and code checks. Behaviour change:
  with Home/VPN off, `/api/ping` now answers `403` to a name other than a loopback name (it answered `200` before).
- **One sign-in for HUD, SYS and LLM.** Devices, the access code, the roles and the model hosts are in the shared file
  `pairing.dat`. Roles are per module: a device that signed in at the hub is a *Viewer* here until it is changed there.
  The old `access.dat` and `hosts.dat` are imported once and kept as `access.dat.bak` and `hosts.dat.bak`.
- **In the hub's frame** LLM shows no brand of its own and takes the hub's theme. Behaviour change: the server now allows
  the hub on port 17400 (same host name) to show the page in a frame, also when the window starts it. The server opens no
  extra port; `--hub-port 0` turns the permission off.
- `/api/ping` adds `module`, `version` and `gate`. Nothing was renamed.

### Added
- `--hub-config <file>` for the server, and `"hostNames"` in `stykker.json`: further names this PC answers to, such as a
  Tailscale name (plain DNS names only).
- `--pairing-file <file>` for the server, to use another pairing file (tests, test runs).
- The hub shows the LLM role of each device and changes it.
- **Prompt cache in the monitor.** Recent requests get a `cache` column: how much of the prompt was reused (`44.9k ↺`),
  `—` when the whole prompt was read, empty when unknown. The server card's curve marks the read phase of each request in
  a second colour; hover shows the read tokens, the seconds and the reused tokens. llama.cpp reports the reuse through the
  prompt length from `/slots`; Strata through its log line (only for a server StykkerLLM started itself).
- `requests.csv` has a `PromptReused` column at the end (-1 = unknown). The header differs from the existing file, so on an
  existing install the rows go to `requests.v2.csv`; the old file stays as it is.

### Fixed
- **Several tool calls at once through the proxy.** llama-server allows only one tool call per answer unless the request
  sets `parallel_tool_calls`, and stopped with 400 "This model only supports single tool-calls at once!" when a model
  called several (seen with pi, which does not send the field). For local llama.cpp targets the proxy now sets it to true
  on requests with tools, unless the client chose a value itself.

## 0.4.0

### Changed
- **New web interface** (docs/plan-ui-redesign.md, mockups in docs/design/): only two themes, *Dark* and *Spacepunk
  Titan* (light, matte, glow only in the dark display windows), plus *System*. The five old themes are gone; a stored old
  theme becomes Dark.
- **Monitor**: status bar in the header, two columns; one server card for every backend and for model hosts (backend
  stripe, state symbol, slot tiles, twelve fixed metrics); hardware card with RAM type and theoretical bandwidth;
  saved profiles as tiles with a VRAM forecast; recent requests with client kind, tool calls, thinking tokens and result;
  history grouped by day with how each run ended, filters and forget-with-undo; proxy panel reworked.
- **Keyboard**: `Ctrl+K` command palette (pages and actions), `/` jumps into the history search.
- *Server lost* is a banner instead of a dialog; read-only and viewer get a notice bar.

### Added
- **Model hosts** (docs/hosts.md): `StykkerHost.exe` runs on PCs that only run models – a tray icon, no window. It
  pairs with the server by a six-digit code and dials in over one WebSocket (no open port on the host).
- **☰ → Hosts**: a card per host with GPU, VRAM and RAM, its model servers (stop, unload) and GGUF files (start).
  The monitor lists the servers of all hosts with a host badge.
- **Proxy on hosts**: models running on a host are offered and reached through the host connection. A requested model
  that is not running but lies on a host starts there (most free VRAM wins; a clear error when no host has room).
- **StykkerHost as a Windows service**: `StykkerHost install-service`, `pair-service <server> <code>`, `unpair-service`,
  `uninstall-service` (as administrator). Starts with Windows, restarts after a crash, data in `%ProgramData%`.

### Removed
- **Nodes** (pairing whole Stykker servers as nodes of a hub, spreading model tests, `/api/eval/runs`). Model hosts
  replace them. Devices that were paired as a hub keep read-only access; remove them in ☰ → Phone access.

## 0.3.1

### Added
- **The server lives as long as you use it.** StykkerUI windows and the terminal display sign in every few seconds, open
  web pages and hubs count too. When nobody is left (and no model test or benchmark runs), the server ends by itself –
  about 3 seconds after the last window or terminal closes. **Settings → Keep the server running** keeps it up (node PCs).
- **Start screen in StykkerUI**: the window opens at once with a status line ("Starting the server …", "Signing in …") and
  a *Try again* button if something fails – including a clear message when the port is taken by another program.
- **Glow like the original window**: the big tokens/s numbers and their unit, the brand, status dots, bars and the
  sparkline (wide soft strokes and a glowing dot at the current value) shine softly, cards get a sheen on top. The strength
  follows the theme, as in the old window (Space Glass most, Obsidian hardly at all).
- **One log per program** (`StykkerLLM-Server.log`, `StykkerUI.log`, `stykker.log`) next to the program when it may write
  there, otherwise in the data folder. The server also logs ASP.NET Core warnings, notices and unhandled exceptions.
- **Bug report**: ☰ → *Report a bug* or `stykker bugreport <text>` – a zip with logs, settings and state (no keys, access
  code, devices; API keys, tokens, user and PC name blacked out) plus a prefilled GitHub issue.

### Changed
- **`stykker` is now a display, the web UI controls.** No input line and no `/commands` any more: keys show what you want
  to see – `w` opens the web interface signed in, `c` code + QR, `r` recent requests, `m` memory per program, `↑↓ Enter`
  details of a server, `?` help. The web address is shown with every network address, Tailscale marked. When no server
  runs, the display starts one and keeps it alive. Remaining commands: `stykker status`, `web`, `stop`, `bugreport`,
  `help`, `version` – without any options. Start/stop, model tests, benchmarks, recordings, proxy, nodes, devices and
  settings are in the web interface only.
- Developer switches (`--sim`, `--data-dir`, `--port`, `--snapshot`) exist only in Debug builds.
- Quitting StykkerUI no longer stops the server outright: it signs off, and the server ends when nobody else (a terminal,
  a web page) still needs it.
- About 340 texts left over from the WinForms window and the old terminal interface were removed.

### Fixed
- StykkerUI could crash right after opening (access violation in Photino) when it sent a message before the page was
  ready.
- StykkerUI now watches its window process: Photino occasionally fails while creating the window (heap corruption inside
  its native part); the window then simply opens again (up to three times).
- A second server start no longer opens a browser when `--no-browser` was given.

## 0.3.0

A new start in a new repository: the server with its web interface is the core, and every interface is a view on it.

### Changed
- **One UI codebase.** The WinForms window is gone. The app window is now **StykkerUI** (`StykkerUI.exe`), a small
  Photino window around the web interface. It starts the server if needed and signs itself in. It draws in software and
  uses **no GPU memory** (`--gpu` turns the GPU on).
- **The web interface got a new look**: glowing header line and active tab, buttons with light and press effects, cards
  that fade in, a card that glows and shows a moving light while its server is writing, filled sparklines, bars with a
  moving shine, pulsing status dots, section heads with an accent bar, animated menus and dialogs. Motion is switched off
  for people who prefer reduced motion.
- **The web header carries fewer main items** (Monitor, Model tests, Benchmarks, Recordings); everything else is grouped
  in the ☰ (EVAL / TOOLS / VIEW), and the ☰ lights up when the current page is inside it. "+n more" opens the full list of
  VRAM consumers.
- Names: the web interface is called StykkerLLM; the server is `StykkerLLM-Server.exe`, the terminal companion `stykker`.
  The data folder stays `%APPDATA%\StykkerLLM`.

### Added
- **Compact view**, per device: Auto / Compact / Full in the ☰ (Auto switches below 780 px). The header wraps, wide tables
  scroll inside their block.
- **StykkerUI: back and forward** (← →, Alt+←/→), and **closing asks** in a dialog over the current page: *Keep in tray*,
  *Quit* or *Cancel*, with *Don't ask again*. The tray menu brings the question back (*Ask when closing*). From the tray the
  window comes back on the page you left.
- **`StykkerLLM-Server --sim`**: the web interface with simulated servers (demo, screenshots).
- **Nodes**: pair other PCs with a six-digit code or by searching the network, see and control their servers, spread model
  tests over them, and use their models through the Stykker-Proxy (`node/model`).
- **Pairing with six digits and QR code** in both directions: type the code of the PC, or approve the code a new device
  shows. Roles Admin, Viewer and Hub.
- **Prompt tester** on every loaded model (web and TUI): streaming, thinking shown apart, time to first token and tokens/s,
  and tools (list, read, write, edit, cmd/PowerShell) with approval per call.
- **Model test suite "agent"**: multi-step tasks where the model works with real tools in a throwaway folder; checked by
  running Python in that folder.
- **AGENTS.md** and `docs/` (architecture, UI parity, nodes) for contributors and coding agents.
- **One StykkerUI per data folder**: a second start brings the open window to the front (also out of the tray).

### Fixed
- **The tray icon never appeared** (server and window): `Shell_NotifyIconW` was looked up in `user32.dll` instead of
  `shell32.dll`, and the window class name went to the Unicode API as ANSI text.
- **A second server on the same data folder was not stopped**: the name of its lock used `string.GetHashCode()`, which
  differs in every .NET process. A busy port now ends the server with a clear message instead of a crash.
- The window draws without a GPU, so endless animations, blur and large soft shadows are left out there (CPU in the
  window with four busy simulated servers: about 5–9 % of one core instead of 55 %).

## 0.1.0

First release (Windows 10 1809+ x64, self-contained, no runtime needed).

License: **Business Source License 1.1** – free for personal and non-commercial use; this version becomes Apache-2.0 four years after its release (see LICENSE).

### Added
- **vLLM is a known backend** (it only runs on Linux). The monitor recognises it, reads the served model from `/v1/models`
  and takes tokens/s, running and waiting requests and the GPU cache share from its Prometheus `/metrics`. That needed one
  exception in the detection: a vLLM runs as `python -m vllm…`, and Python processes were skipped so far (Ollama's runner,
  Strata) – now only command lines that mention vLLM get through. The simulator has a vLLM server as well, so the card can
  be seen without installing one.
- The Stykker server and `stykker` **run on Linux**. What differs there is written down in the README; without a Linux platform
  layer there is no automatic detection, no GPU/system values and no DPAPI, so only servers entered by hand are measured.
  `--platform basic` shows that mode on Windows.
- Detection and live monitoring of llama.cpp servers (and forks), Ollama and LM Studio: tokens/s per slot, context use, max-tokens warning, VRAM, RAM, CPU, request log.
- Saved profiles, running servers and history with one-click start and pre-checks; details window with the redacted command line.
- Recordings with a per-request timeline, optional local pass-through proxy (OpenAI-style and Anthropic-style streams), comparison and CSV export.
- Stykker-Proxy: a single local proxy on a fixed port (**17500**; was 8079) that lists the models of all backends together, routes by the `model` field to the right backend, and answers the virtual model `stykker`, an empty or unknown model from the chosen default target. The model served as `stykker` is chosen from the active models in the UI, and **other machines running Stykker can be attached** (name + URL): their models appear in `/v1/models` like local ones, so clients keep one entry. Colliding names keep the local model and expose the remote one as `machine/model`. The proxy can optionally be offered on the LAN (no access code) for those machines.
- Benchmarks (context ladder, tool call, parallel requests) with hardware fingerprint, regression alert, Markdown/CSV export.
  The regression hint now stays on the saved result: the window, the web page (`/bench`, Markdown export) and the
  terminal (`/bench`, `/bench run`) all show it, and a reload does not lose it. From which drop it is shown can be
  set in the settings (default 10 %, 0 = never).
- Time-to-first-token percentiles (p50/p95/p99) in the recording summary and comparison.
- Simulator window ("Simulate…"): simulated llama.cpp, Ollama and LM Studio servers with their own temporary data folder; simulated cards are marked SIMULATED.
- Mini overlay, tray icon, five themes.
- Instead of the row of ten buttons: three main buttons in the header (Add server, Record all, Benchmark) and a ☰ panel with tiles in groups (Server, Record, Tools, View, Theme, Help); shortcuts Ctrl+N, Ctrl+R, Ctrl+B, Ctrl+O, Ctrl+K, F1. Buttons in all dialogs are drawn as pills like in the main window.
- VRAM and RAM per program as one stacked bar with a tooltip per program, the top 3 below and the rest under "+n more"; tables with a header band and row stripes.
- Terminal companion `stykker` (`status`, `list`, `start`, `stop`, `unload`, `sim`), shipped next to the window in the same zip.
- System card with one **Memory** bar (used / total, like the Task Manager); the commit reserve only shows as a yellow (85 %) or red (95 %) hint when it runs low.
- The window ships as one self-contained file (`Stykker-LLM-Monitor.exe`, about 50 MB), no folder of runtime files.
- Fixed: the window could no longer act at all ("read-only"), and it quit silently without any message when a server
  was already running. Both came from the window and the server sharing one single-instance name: only the window
  holds it now. The server is the engine and always writes; should it ever run read-only, all surfaces say so.
- New button **🌐 Web** in the status line next to the proxy switch (and in the ☰ panel) opens the web interface of
  the server.
- The server starts without a console window any more (neither from the window nor from `stykker web`).
- Crash hint: when a local server disappears without being stopped by the monitor, a tray notification opens a window with the likely cause (out of memory, unknown architecture, port in use, …) and the last log lines.
- **Free VRAM** (☰ panel and tray): stops all local llama.cpp servers and unloads Ollama models after one confirmation that lists everything.
- **Context almost full** is announced in all three surfaces: the context bar of a card turns red at 90 % of the slot's
  context and names the used and the full value (web and terminal as well, which had no such warning).
- **Speculative decoding is measured**: a *draft* chip in card, web page and terminal shows the acceptance rate and the
  average accepted length. The numbers come from the `llamacpp:spec_decode_*` counters of the server's `/metrics`
  (the `/slots` answer does not contain them); without a draft model nothing is shown.
- **Metrics for other tools:** `http://127.0.0.1:8078/api/metrics` returns the values in the Prometheus text format –
  per server and per slot tokens/s, peak, requests, queue and drafts, plus GPU, system and the largest VRAM/RAM/GPU
  posts per process. Reachable without the access code, but **only from the local machine**, because a scraper would
  otherwise have to read the code from the data folder.
- **The commands are written down:** `stykker --help` lists the server commands (`web`, `server`, `qr`, `remote`,
  `devices`, `role`), and the server itself answers `--help` with its four switches and its endpoints.
- **Find models on the disk** in the profile editor: it walks the model folders (the same ones as the model test suite),
  reads only the header of each GGUF and offers name, size, quantization, parameter count, trained context and path; the
  chosen file lands in the `-m` line.
- **A saved profile can restart itself after a crash** (off by default, with a limit of restarts per time window) and can
  **unload after idle time** (off by default, minutes without a request): a llama.cpp server is then stopped, an Ollama or
  LM Studio model is unloaded. A busy or unreachable server is never unloaded.
- **A server behind WSL or Docker** (`wslrelay`, `wslhost`, `wslservice`) is now measured instead of skipped. Along the way
  two errors that the live tests showed were fixed: a python process with `-m <model file>` was skipped although every
  profile started by the monitor has one, and the start error named the profile id instead of the program.
- **About** window (version, GPU, driver, CPU, .NET, data folder) with **Start with Windows** (starts minimized, current user only).
- Profile editor: **Copy as PowerShell** / **Copy as cmd** turns a saved profile into a start script (redacted secrets stay redacted).
- Search box in the history list (name, model file, port, parameters).
- **Model test suite** `stykker eval`: 24 tasks in five categories (coding with executed tests, reasoning, format, tools, long context), automatic scoring, saved runs, comparison and Markdown report; own suites as JSON files.
- **Strata** servers (github.com/Niko1221/Strata) are recognized: live tokens/s, prompt reading, context and finished requests come from Strata's own `/metrics`; VRAM, RAM and CPU are measured on its engine process. The simulator can add a Strata server.
- A second monitor instance (for example `stykker` next to the open window) no longer lists the window's Stykker-Proxy as a server.
- **Cloud providers in the Stykker-Proxy:** a provider is added with name, base URL and API key; the proxy then fetches its
  model list live and offers the models as `provider/model` (this namespace always applies), so a client keeps its single
  entry and can address either the virtual model `stykker` or one provider model. The proxy sets the provider's
  `Authorization: Bearer` header itself – the key of the client is not passed on. The key is stored protected with Windows
  DPAPI (`providers\<hash>.key` in the data folder), never in clear text in `settings.json`, the display, the log or the
  state. A provider is only ever asked when it has been added **with** a key (opt-in), and a provider model never becomes
  the silent default target for `stykker` – it must be chosen explicitly. Recordings count the tokens of provider
  requests like local ones (no cost tracking). Window, web and terminal show the same: a masked key field next to every
  provider, a hint that requests leave this computer, `/proxy providers` and `/proxy serve provider/model`.
- **The web interface uses the five themes of the window.** Choosing a theme in the settings now paints the whole web
  interface – background, cards, buttons, tables, the stacked memory bars and the QR code – instead of always showing
  one fixed blue palette. The colours come from the same values the window draws with (including the starry,
  dotted-grid, scanline and plain backgrounds, the corner radius of the cards and Phosphor's console font and
  lower-case labels). The sign-in page on the phone is themed as well. The theme is written into the page on every
  tick, so a change from the window or the terminal is visible without reloading.
- The window is a client of the Stykker-LLM-Server: it starts it if needed and draws **every** value from its state (cards,
  slots, sparkline, GPU/system, history, overlay and all dialogs) instead of measuring a second time. Settings therefore
  show the server as a given address instead of a switch; model tests and benchmarks open in the web interface, where one
  queue and one benchmark service serve window, phone and terminal. If the server does not answer, the window says so and
  retries every 15 s (only the simulator, the demo and the test images measure on their own).

### Security
- **Two roles per signed-in device.** A device that signed in with the access code is **Admin** (as before) or **Viewer**.
  A viewer sees every value and every list, but no action is carried out for it – not starting a server, not testing,
  not changing a setting – and it does not get the access code either, because it could sign in as admin with that.
  Window, web page and terminal set the role the same way (`stykker role <id> viewer|admin`, `/role` in the TUI); the
  window and `stykker` themselves always act as admin, they authenticate with the internal key.
- The start confirmation for saved command lines is bound to a key protected with Windows DPAPI (`%APPDATA%\StykkerLLM\confirm.key`); an edited `library.json` can no longer skip the question.
- The proxy cuts off requests that stop sending data (15 s without a byte), not only after 30 s in total.
- Recordings skip lines longer than 1 MB; the request log (`requests.csv`) is only written inside the data folder.
- The release workflow pins every action to a commit SHA and gives each job the smallest rights; only the job that creates the GitHub release can write.

### Notes
- LM Studio starts a llama.cpp engine as a child process when a model is loaded. It is shown inside the LM Studio card (VRAM, slots, tokens/s) and is no longer listed as a separate server with Stop and Save.
- The Ollama tray application's own user-interface port is not mistaken for an Ollama server.
- Secrets in short flags with an inline value (`-hft=...`) are redacted like the long forms.
- Anthropic `/v1/messages` replies (stream and non-stream) are evaluated properly: thinking vs. answer, tool names, stop reason.
- Very large tokens/s values in "Recent requests" are shown compactly (for example `3.4k t/s`).
- The program is not code-signed; see the README for the SmartScreen hint and the SHA256 check.
