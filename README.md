# StykkerLLM

A lightweight Windows monitor for local LLM servers: **llama.cpp** (and forks), **Ollama** and **LM Studio**.
It finds the servers on your machine by itself and shows what they are doing right now: tokens per second per slot,
context use, VRAM, a request log, recordings with a per-request timeline, and benchmarks. Nothing leaves your machine.

![main window](docs/screenshots/main.png)

*All screenshots in this document show the built-in [Simulator](#simulator) with made-up servers and numbers.*

## Features

- Auto-detection of running llama.cpp servers (incl. forks) from the process command line
- Live tokens/s per slot, context bar, max-tokens warning, prompt-processing progress
- GPU card (NVIDIA): load, VRAM, memory controller, clocks, throttling, top VRAM processes; system CPU and memory (used / total like the Task Manager, plus a warning when the commit reserve runs low)
- Saved profiles / Running / History with one-click start (pre-checks: port free, model file exists, VRAM estimate)
- Profile editor with **Find models** (walks the model folders, reads only the GGUF header), optional **restart after a crash**
  (with a limit per time window) and **unload after idle time** (both off by default, per profile)
- Details window (command line with secrets redacted, parameters, `/props`, GGUF header, log tail)
- **Ollama** and **LM Studio**, read-only: loaded models, VRAM, and for LM Studio the engine it started (slots, tokens/s, VRAM) on the same card
- **vLLM** (Linux): the served model from `/v1/models`, tokens/s, running and waiting requests and the GPU cache share from its
  Prometheus `/metrics`. The simulator has a vLLM server too (☰ → Simulator), so the card can be seen without a vLLM installed.
- Recording with a per-request timeline (prompt / thinking / answer / waiting), optional local pass-through proxy
  (loopback only, stores numbers only, never prompt contents); OpenAI-style and Anthropic-style (`/v1/messages`) streams are understood
- Compare recordings, CSV export
- Benchmark (context ladder, tool-call test, parallel requests) with Markdown/CSV export and a regression alert
- Crash hint: a server that disappears without being stopped by the monitor raises a tray notification with the likely cause and the last log lines
- **Free VRAM** with one click (stops local llama.cpp servers, unloads Ollama models – after one confirmation)
- Profiles as start scripts (**Copy as PowerShell / cmd**), search in the history, **Start with Windows** (About window)
- Three main buttons in the header (Add server, Record all, Benchmark) and a ☰ panel with everything else (tiles in groups, Ctrl+K); VRAM and RAM per program as one stacked bar with tooltips and the top 3 below
- Web interface and phone access (`StykkerLLM-Server.exe`): browser, phone and terminal show and act on the same state; QR code with access code, Home/VPN switch
- Mini overlay, tray icon, 5 themes (Deep Sea, Cyber Grid, Space Glass, Obsidian, Phosphor)
- Light on resources: in idle (no server) about 1 % of one CPU core, with four simulated servers about 2.5 %; about 200 MB working set
  (about 110 MB private), measured with the release build on a 16-thread desktop

## Install

Requirements: **Windows 10 1809 or newer, x64**. No .NET or other runtime has to be installed; everything is in the download.

1. Download `StykkerLLM-<version>-win-x64.zip` from the [GitHub Releases](https://github.com/CrimsonED1/Stykker-LLM/releases) page (version 0.1.0).
2. Unzip it anywhere (the folder is portable) and start `StykkerLLM.exe`. It is one self-contained file (.NET is inside);
   the zip also holds `stykker.exe`, the [terminal companion](#terminal-companion-stykker).

*Coming soon:* `winget install CrimsonED1.StykkerLLM` and `npm install -g stykker-llm`. Neither is published yet.

### SmartScreen and antivirus

The program is not code-signed. Windows SmartScreen may show "Windows protected your PC" on first start: choose
*More info*, then *Run anyway*. Some antivirus tools are wary of any program that reads the command line of other processes;
the monitor needs that to detect llama.cpp servers and only reads the programs that look like model servers.
You can check your download against `SHA256SUMS.txt` from the release:

```powershell
(Get-FileHash .\StykkerLLM-0.1.0-win-x64.zip -Algorithm SHA256).Hash.ToLower()
```

## Quick start

1. Start a `llama-server` (for example `llama-server.exe -m model.gguf --port 8081`), or Ollama, or LM Studio with its server on.
2. Open the monitor. It lists the servers within a few seconds.
3. **Save** a running server to keep its start command as a profile, **Start** to launch it again later.
4. **Record** to capture a timeline, **Proxy** to see thinking vs. answer, tool calls and time to first token.

No server at hand? open **☰ → Simulator …**, see below.

### Terminal companion: stykker

`stykker.exe` (in the same zip) works in a terminal on the same core as the window: `stykker status` shows GPU, system and the running
servers once (`--watch` repeats, `--json` is machine-readable), `stykker list` shows saved profiles, history, benchmarks and recordings,
and `stykker start`, `stop` and `unload` do what the buttons do (asking first; `--yes` for scripts). `stykker sim` shows the simulator.
`status` and `list` only read and work next to the open window. See `stykker --help`.

### Web interface and phone access: StykkerLLM-Server

`StykkerLLM-Server.exe` (in the same zip) holds the measurement and serves the web interface on **http://127.0.0.1:8078** –
the same numbers, the same buttons, plus the model tests and benchmarks. **The window starts it by itself** and shows its
state; you can also start it with `stykker web` (opens the browser) or from the window: **☰ → Phone access**.

* **Home/VPN switch** (in the web page, in `stykker remote on|off` and in the window dialog): off = only this PC, on = reachable in the network.
* **QR code with access code**: scan it with the phone camera, the phone signs in with a device cookie. `stykker qr` prints it in the terminal.
  Devices can be removed individually (window, web page or `stykker devices <id>`).
* **Two roles per device**: **Admin** may do everything the window can (default for every new device), **Viewer** may only look –
  every value, every list, but no action at all. A viewer does not see the access code either, because it could sign in as admin with it.
  Set it in the window dialog, on `/phone` or with `stykker role <id> viewer|admin`.
- **Local metrics endpoint** for status bars and dashboards: `http://127.0.0.1:8078/api/metrics` returns Prometheus text
  (GPU, RAM, per-process top lists, every server with tokens/s, slots and context). No key needed – but only from
  **this machine**; from the network it answers 403 like every other page. Example for a Starship/waybar script:
  `curl -s http://127.0.0.1:8078/api/metrics | grep '^stykker_gpu_utilization_percent'`.
- The window, `stykker` and the phone all act on the same server: one process measures, all interfaces show the same state.
- **Another machine running Stykker** is attached under *Proxy settings → Add machine* – enter the **Stykker-Proxy** of that
  machine (its port 17500, not its web address 8078), and its models appear in your client's list like local ones, marked
  `stykker-proxy`. That machine has to allow network access (Home/VPN on) for this.
- Access is protected three ways: the server's internal key is bound to your Windows account (window and terminal can read it, a web page
  in the browser cannot), browsers need the access code, and requests from outside are refused unless Home/VPN is on.
- No HTTPS yet (see Known limitations); the access code travels in the URL of the QR code, so use it in your own network.

### Ollama and LM Studio

Both are shown read-only (no stop, no save). Tested live with Ollama 0.34.4 (card "running, nothing loaded")
and with LM Studio (card "1 model loaded"; the llama.cpp engine LM Studio starts for a loaded model is shown
inside the LM Studio card, not as a separate server).

### Servers in WSL or Docker

A llama.cpp server inside WSL2 or Docker is reachable on `localhost` through a Windows port relay. The monitor probes
those relays (it does not probe other Windows system programs), so such a server is normally found on its own; if it
is not, use **+ Add server** and enter the address (for example `http://127.0.0.1:8081`) – the monitor then watches it
like any other server. That also applies to a **vLLM** in WSL2 – pick the backend „vLLM" there, then tokens/s, waiting
requests and the model come from its own answers.

### Running the server on Linux

`StykkerLLM-Server.exe` starts on Linux too, and `stykker` as well. What differs is what the server can see:
there is no automatic detection (no listeners, no process list), no GPU or system values, no stopping of processes,
and the files `access.dat` and `server.key` are **not** bound to the user (DPAPI exists only on Windows) – a data
folder that was created on Windows therefore cannot be read there and gets a new access code.

Everything else works: the web interface, library, history, recordings, benchmarks, model tests, the Stykker-Proxy and
every server entered by hand under „Add server by URL" (measured over the backend's own answers, vLLM included).
To see that mode on Windows, start the server once with `--platform basic`.

**Not built yet:** a real Linux implementation of the platform layer (`/proc/net/tcp`, `/proc/<pid>`, `nvidia-smi`).
Until then automatic detection only works on Windows.

### Model test suite (stykker eval)

`stykker eval <server|port|url>` gives a local model 24 tasks and scores them automatically: **coding** (the model's Python code is
run against tests, after asking, in a temporary folder with a time limit – it is not a sandbox), **reasoning** (exact numbers), **format**
(JSON only, one word, exactly three lines …), **tools** (right tool, right arguments, no tool when none is needed) and **long context**
(a code word hidden in 8k / 32k tokens). Result: percent per category and a total; runs are saved in the data folder under `eval-results`.
`stykker eval results` compares the last runs side by side, `stykker eval models` shows one line per model (mean over all runs of a suite,
± spread, speed), `--md report.md` writes a Markdown table. Own suites: JSON files in the
data folder under `eval\` (same format as the built-in one, see `src/StykkerLlm.Core/Eval/suite-basic.json`).

## Simulator

**☰ → Simulator …** opens the Simulator window: add and remove simulated servers (llama.cpp, Ollama, LM Studio, Strata; model, context, slots,
tokens/s range, requests per minute, share of thinking, tool calls, optional simulated GPU and system values), then start, pause
or stop the simulation. While it runs, the main window shows the simulated servers, each marked **SIMULATED**.
Stop, Start and Proxy only act on the simulation, and the simulation uses its own temporary data folder: nothing lands in your
library, history, recordings or request log. Handy to try the monitor or to make screenshots.

![simulator](docs/screenshots/simulator.png)

## Screenshots

| Details | Recording timeline |
|---|---|
| ![](docs/screenshots/details.png) | ![](docs/screenshots/recording-timeline.png) |

| Recordings | Compare two recordings |
|---|---|
| ![](docs/screenshots/recordings.png) | ![](docs/screenshots/recording-compare.png) |

| Benchmark | Benchmark results |
|---|---|
| ![](docs/screenshots/benchmark.png) | ![](docs/screenshots/benchmark-results.png) |

| Profile editor | Add server by URL | Settings |
|---|---|---|
| ![](docs/screenshots/profile-editor.png) | ![](docs/screenshots/add-server.png) | ![](docs/screenshots/settings.png) |

Phone access (QR code for the phone, Home/VPN switch, signed-in devices):

![phone access](docs/screenshots/phone-access.png)

## Themes

| Deep Sea (default) | Cyber Grid | Space Glass | Obsidian | Phosphor |
|---|---|---|---|---|
| ![](docs/screenshots/theme-deep-sea.png) | ![](docs/screenshots/theme-cyber-grid.png) | ![](docs/screenshots/theme-space-glass.png) | ![](docs/screenshots/theme-obsidian.png) | ![](docs/screenshots/theme-phosphor.png) |

The mini overlay (double-click the header of the main window to open or close it):

![overlay](docs/screenshots/overlay.png)

## The Stykker-Proxy and recording explained

- There is **one** local proxy on a fixed port (**17500** by default), on loopback only. Turn it on with the switch under the title
  (or **☰ → Proxy**); **☰ → Proxy settings …** picks the **model served as `stykker`** (from the active models), changes the port and
  copies the client URL. The choice applies from the next request; a running client session may need a reload (a different backend can
  use a different chat template).
- Point OpenAI-compatible clients (Qwen Code, Aider, …) at `http://127.0.0.1:17500/v1` and use the model **`stykker`**, or any
  model from `/v1/models`. Anthropic-compatible clients (Claude Code, …) use the base URL `http://127.0.0.1:17500` (without `/v1`).
- `/v1/models` lists the models of all detected backends (llama.cpp, Ollama, LM Studio) together plus the virtual entry `stykker`.
  Requests are routed by the `model` field to the backend that serves it; `stykker`, an empty or unknown model goes to the default
  target (Auto = the only running llama.cpp server or the first reachable one). Without a target the proxy answers `503` with a JSON error.
- **Other machines with Stykker** can be attached in the same dialog (name + URL such as `http://machine:17500`): their models then
  appear in `/v1/models` like local ones, so clients keep **one** entry for local and remote models. Name collisions keep the local
  model and expose the remote one as `machine/model`. The other machine must offer its Stykker-Proxy on the LAN (its own LAN switch);
  this proxy is loopback-only unless you enable its LAN switch too — **without an access code**.
- **Cloud providers** (OpenRouter, NVIDIA, …) can be added in the same dialog with **name, base URL and API key**. The proxy then
  fetches their model list live and offers the models as `provider/model` – so a client keeps its **one** entry and can address
  either `stykker` or a provider model. The proxy sets the provider's `Authorization: Bearer` header itself; the key of the client is
  not passed on. Three rules apply: a provider is only asked once it has been added **with** a key (nothing goes out before that), a
  provider model never becomes the silent default for `stykker` (it must be chosen explicitly), and requests to a provider **leave this
  computer**. The key is stored protected with Windows DPAPI (`%APPDATA%\StykkerLLM\providers\`), never in clear text – not in
  `settings.json`, not in the display, not in the log, not in the state. Recordings count the tokens of provider requests like local
  ones (costs are the provider's business). In the terminal: `/proxy providers`, target with
  `stykker proxy serve provider/model`.
- The proxy forwards requests unchanged (streaming included) and reads each reply only to count numbers and names (tokens, thinking
  vs. answer, tool names, time to first token); prompt and answer texts are never stored.
- A recording captures a timeline per request: prompt reading, thinking, answer, waiting. Export as Markdown or CSV.

![proxy settings: served model, port, remote machines, cloud providers (name, URL, key) and LAN switch](docs/screenshots/proxy.png)

The screenshot shows two providers after they have been entered; the model list and the served-model list fill up as soon as a
key is stored (Stykker then asks the provider once per minute).

## Data and privacy

- User data lives under `%APPDATA%\StykkerLLM\` (settings, library with profiles and history, logs, recordings). Nothing leaves the machine.
  The one exception is a cloud provider you add yourself: only then do requests go to that provider (see the proxy chapter).
- API keys of started servers (`--api-key`, `--hf-token`, ...) are never stored, shown or copied. A **cloud provider key** you enter
  yourself is the one exception: it is stored, but only protected with Windows DPAPI for your user, and it is never shown again.
- Before a saved command line is started the first time (and after every change) you confirm it. The confirmation is bound to a
  secret key in `%APPDATA%\StykkerLLM\confirm.key` that only your Windows account can read (DPAPI); editing `library.json` cannot fake it.
- The request log (`requests.csv`) is only written inside the data folder.
- With the web interface: the access code and the list of devices live in `%APPDATA%\StykkerLLM\access.dat`, bound to your Windows account
  (DPAPI). Only a hash of each device cookie is stored, never the cookie. The server's internal key is in `server.key`, likewise bound.
  The window and `stykker` read it locally; a browser never sees it.

## Build from source

- .NET 10 SDK, Windows.
- Build and test:
  ```
  dotnet build src/StykkerLlm.App/StykkerLlm.App.csproj -c Release
  dotnet test tests/StykkerLlm.Tests
  ```
  The window is a WinForms app (`src/StykkerLlm.App`); `src/StykkerLlm.Cli` is the `stykker` command; both share `src/StykkerLlm.Core`.
- Release package (zip with `StykkerLLM.exe` and `stykker.exe`, `SHA256SUMS.txt`): `powershell -ExecutionPolicy Bypass -File build\make-release.ps1 -Version 0.1.0`

## Known limitations

- Windows only (10 1809 or newer, x64). Windows 10 1809 itself could not be tested; feedback is welcome.
- GPU values need an NVIDIA GPU (NVML); without one the GPU card says so.
- Ollama and LM Studio are read-only.
- The web interface uses plain HTTP (no HTTPS yet). It listens on all addresses but refuses requests from outside unless the
  Home/VPN switch is on; browsers need the access code (see above). Use it in your own network, not on the internet.
- The window is a client of the StykkerLLM-Server: it starts the server if needed and draws every value (cards, slots,
  sparkline, GPU/system, history, dialogs) from the server's state; all actions go to it as well. Benchmark and model
  tests therefore run in the web interface or with `stykker`. Only the simulator, the demo and the test images measure on
  their own.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Security issues: [SECURITY.md](SECURITY.md).

## License

[Business Source License 1.1](LICENSE) (BUSL-1.1): free for personal and non-commercial use (private, education, research, non-profit).
Commercial use needs a license from the author – open an issue. Each version becomes Apache-2.0 four years after its release.

## Donate

<a href="https://www.paypal.me/crimsoned"><img src="https://www.paypalobjects.com/en_US/i/btn/btn_donateCC_LG.gif" alt="Donate with PayPal"></a>
If you like it, consider supporting the project.
