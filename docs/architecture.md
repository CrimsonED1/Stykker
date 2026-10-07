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
| `StykkerLLM-Server.exe` | `net10.0`, `OutputType Exe` (WinExe would drop `blazor.web.js`). Tray icon on Windows (`--no-tray` to skip). Ends by itself when nobody needs it (see below); `--stay` or the setting *Keep the server running* keep it up. |
| `StykkerUI.exe` | Photino window. Starts the server if needed, gets its own device via `/pair/local` (needs the data-folder key), sets the cookie through `/pair/adopt`. No GPU by default (`--gpu` to enable). One window per data folder (a second start brings it to the front). Close → a dialog over the current page (`ui.js`, message from the shell): keep in tray, quit or cancel, optionally remembered in `web-shell-close.txt`. Shows a start screen with a status line while the server starts. Holds the server (`/api/hold`) while open, also in the tray. |
| `stykker.exe` | One-shot commands and the TUI. Uses the server when it runs; some commands can measure locally (`--local`). |

## Lifetime of the server

`ServerHolds` (Core) counts who needs the server: StykkerUI windows and interactive `stykker` sessions renew a lease every
5 s (`POST /api/hold`, 20 s lease, `DELETE` on exit), web pages count while their Blazor connection is up (`HoldCircuits`),
and a hub's requests count for that hub. With nobody left and no model test or benchmark running, the server stops after
15 s (3 s after the last client signed off; a fresh server waits 60 s for its first client). `stykker server start` and
starts without a browser pass `--stay` (scripts, node PCs).

## Logs and bug reports

One log per program (`AppLog`): `StykkerLLM-Server.log`, `StykkerUI.log`, `stykker.log` in `logs/` next to the exe if
writable, else in the data folder. The server also writes ASP.NET Core warnings, engine notices and unhandled exceptions.
`BugReport` zips the description, environment, the tails of all logs, `settings.json` and the state (no access code) into
`bug-reports/`, with secrets blacked out; it never reads `access.dat`, `server.key`, `nodes.dat`, providers or tokens.

## Data and files

All user data is in `%APPDATA%\StykkerLLM` (or `--data-dir`): settings, library (profiles, history, benchmarks),
`access.dat`, `server.key`, `nodes.dat`, eval models/settings/results, recordings, logs. Files with secrets are bound to the
Windows user (DPAPI) where possible.

## Testing without hardware

`Core/Simulation` provides fake llama.cpp/Ollama/LM Studio/vLLM servers (`SimWorld` + `SimHandler` as an
`HttpMessageHandler`). Tests use it and `FakeHandler`/`FakePlatform`; nothing in the test suite needs a GPU or network.
