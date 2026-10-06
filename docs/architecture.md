# Architecture

## The server is the core

```
 StykkerUI (window)   phone / other PC (browser)   stykker (TUI/CLI)   coding tools
        \                    |                          |                  |
         \------------- HTTP :8078 (web UI, /api) ------/            proxy :17500
                              |                                            |
                     StykkerLLM-Server  ───────────────────────────────────┘
                     MonitorEngine · LaunchCoordinator · EvalQueue · BenchmarkService · NodeRegistry
                              |
                     IPlatform (Windows: processes, ports, NVML, DPAPI)
                              |
             llama.cpp / Ollama / LM Studio / vLLM servers on this PC (and nodes on other PCs)
```

- **MonitorEngine** (Core) measures once per tick: finds servers (`ServerDiscovery`, by listening ports and command lines),
  reads their state through backend adapters (`Backends*.cs`), GPU and system values through `IPlatform`.
- **StateJson** (`Core/Server/ServerState.cs`) writes the whole state as JSON (schema 1, fields are only ever added).
  The web pages read the engine directly; the TUI and other clients read `/api/state` or the live stream `/api/stream`
  and parse it with `StateSnapshot`.
- **ActionApi** (`Core/Server/ActionApi.cs`) is the single place that changes anything: start/stop/unload, profiles,
  proxy, recordings, benchmarks, eval queue, settings, access, nodes. Web pages call it in-process (`WebActions`),
  clients via `POST /api/action`. Roles are checked there (viewer = read only, hub = everything except access).
- **AccessGate** (`Server/AccessGate.cs`) decides who gets in: the data-folder key (`server.key`, local TUI/scripts),
  a device token (cookie for browsers, `X-Stykker-Device` header for hubs), or nothing (only `/api/ping` and `/pair…`).
  Remote access needs the switch *Home/VPN*; the access code has six digits, lives ten minutes and works once.

## Programs

| Program | Notes |
|---|---|
| `StykkerLLM-Server.exe` | `net10.0`, `OutputType Exe` (WinExe would drop `blazor.web.js`). Tray icon on Windows (`--no-tray` to skip). |
| `StykkerUI.exe` | Photino window. Starts the server if needed, gets its own device via `/pair/local` (needs the data-folder key), sets the cookie through `/pair/adopt`. No GPU by default (`--gpu` to enable). One window per data folder (a second start brings it to the front). Close → a dialog over the current page (`ui.js`, message from the shell): keep in tray, quit or cancel, optionally remembered in `web-shell-close.txt`. Log: `logs/web-shell.log`. |
| `stykker.exe` | One-shot commands and the TUI. Uses the server when it runs; some commands can measure locally (`--local`). |

## Data and files

All user data is in `%APPDATA%\StykkerLLM` (or `--data-dir`): settings, library (profiles, history, benchmarks),
`access.dat`, `server.key`, `nodes.dat`, eval models/settings/results, recordings, logs. Files with secrets are bound to the
Windows user (DPAPI) where possible.

## Testing without hardware

`Core/Simulation` provides fake llama.cpp/Ollama/LM Studio/vLLM servers (`SimWorld` + `SimHandler` as an
`HttpMessageHandler`). Tests use it and `FakeHandler`/`FakePlatform`; nothing in the test suite needs a GPU or network.
