# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## Unreleased

### Added
- **Compact view for the web interface**, chosen per device: switch to **Auto / Compact / Full** in the ☰. Below 780 px
  it turns on by itself. In compact the header wraps instead of hiding its tabs behind a sideways swipe, wide tables
  scroll inside their block instead of pushing the whole page, and dropdowns in table rows expand in place.
- **Back and forward** (⟵ ⟶, Alt+←/→) in the window shell StykkerLLM-Web — where there are no browser buttons. A
  browser never shows them.
- **Closing StykkerLLM-Web asks where to go**: *Keep in tray* really hides the window (gone from the taskbar too) and
  the tray icon brings it back, *Quit* ends it. The shell starts the server with `--no-tray` (one icon, not two) and
  shuts it down on quit if it started it. Windows only; elsewhere the window closes as before.

### Changed
- **The web interface is called StykkerLLM** as well: the header, the page title and the pairing page no longer say
  "Server".
- **The web header carries fewer main points and the rest in the ☰**: Monitor, Model tests, Benchmarks and Recordings
  stay visible; Runs, Catalog, Models, GPU details, Prompt, Compare, Nodes, Phone access and Settings are grouped
  under EVAL / TOOLS / VIEW (like the window's ☰ panel). The ☰ lights up when the current page sits inside it.
- **"Details …" on the GPU card is gone**: the whole list of VRAM consumers is reached by clicking **"+n more"** (the
  window opens *GPU details*, the web goes to `/gpu`, which is also in the ☰). On the System card "+n more" expands
  the remaining RAM consumers in place.
- **The program is called StykkerLLM now** (was Stykker-LLM-Monitor). The window is `StykkerLLM.exe`, the server
  `StykkerLLM-Server.exe`; the terminal companion keeps its short name `stykker`. The head shows STYKKER LLM, the
  third word MONITOR is gone. npm (`stykker-llm`, `@stykker-llm/win32-x64`), winget (`CrimsonED1.StykkerLLM`) and
  the repository (github.com/CrimsonED1/Stykker-LLM) follow. Nothing has to be moved: the data folder was already
  `%APPDATA%\StykkerLLM`. Only the autostart entry changes its name – an old entry stays behind in
  `HKCU\...\Run` and is worth deleting once.

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
