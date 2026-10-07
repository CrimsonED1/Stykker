# AGENTS.md – working on StykkerLLM

This file is for coding agents (Claude Code, Qwen Code, Codex, …) and for people who want the short version.
Read it before changing anything. `CLAUDE.md` and `QWEN.md` only point here.

## What it is

StykkerLLM watches and controls local LLM servers (llama.cpp and forks, Ollama, LM Studio, vLLM): tokens/s, VRAM,
requests, recordings, benchmarks, model tests, a proxy that serves every model under one URL, and several PCs paired
as nodes. **The server is the core.** Every user interface is a client of it:

| Program | Project | What it is |
|---|---|---|
| `StykkerLLM-Server.exe` | `src/StykkerLlm.Server` | ASP.NET Core + Blazor Server. Measures, starts/stops, runs tests, serves the web UI on **:8078** |
| `StykkerUI.exe` | `src/StykkerLlm.UI` | A window around the web UI (Photino: WebView2 / WKWebView / WebKitGTK), draws without GPU by default |
| `stykker.exe` | `src/StykkerLlm.Cli` | Terminal **display** (read-only, keys only) and `status`, `web`, `stop`, `bugreport` |
| – | `src/StykkerLlm.Core` | All logic, UI-free and platform-free. Strings, state JSON, actions, eval, proxy, nodes |
| – | `src/StykkerLlm.Platform.Windows` | `IPlatform` for Windows (processes, ports, NVML, DPAPI, tray icon) |
| – | `tests/StykkerLlm.Tests` | MSTest, no network, no real servers (fakes and the simulator) |

There is **one UI codebase for the window, the phone and other PCs: the Razor pages** in
`src/StykkerLlm.Server/Components`. The web UI is the only interface that controls anything; `stykker` only displays (see `docs/ui.md`).

## Branches

- **`dev`**: all ongoing work happens here (commit to `dev` or to a short-lived branch merged into `dev`).
- **`main`**: stable; only receives tested merges from `dev` for a release (tag `v*` starts the release workflow).
- Never push to `main` or create tags/releases without the owner's approval.

## Build, test, run

```
dotnet build StykkerLlm.slnx -c Release        # must end with 0 warnings, 0 errors
dotnet test tests/StykkerLlm.Tests -c Release  # all green (2 tray tests skip without a desktop)
```

- Run the server: `src/StykkerLlm.Server/bin/Release/net10.0/StykkerLLM-Server.exe --no-browser` → http://127.0.0.1:8078
- Run the window: `src/StykkerLlm.UI/bin/Release/net10.0/StykkerUI.exe` (starts the server if none runs)
- Run the terminal display: `src/StykkerLlm.Cli/bin/Debug/net10.0/stykker.exe` (starts the server if none runs)
- Look at the UI without real model servers: `StykkerLLM-Server.exe --sim --no-browser --port 8090 --data-dir <temp folder>`
  (Debug build only; simulated llama.cpp/Ollama/LM Studio servers). Pair a browser with the code from the terminal
  display (`stykker --data-dir <same> --port 8090`, key `c`). `stykker --sim` shows the simulator in the terminal.
- Developer switches (`--sim`, `--data-dir`, `--port`, `--snapshot`, `--keys`, `--size`) exist **only in Debug builds**
  (`#if DEBUG`); a release has no options. The server keeps `--data-dir`/`--port` because the window starts it with them.
- Logs: one per program (`StykkerLLM-Server.log`, `StykkerUI.log`, `stykker.log`) in `logs/` next to the exe when that folder
  is writable, otherwise in the data folder's `logs/` (`Core/AppLog.cs`). Write important events with `AppLog.Write`.
- Server lifetime: the server runs only while a StykkerUI window, an interactive `stykker`, a web page or a hub needs it
  (`Core/Server/ServerHolds.cs`, `POST/DELETE /api/hold`). `--stay` or the setting *Keep the server running* keep it up.
  When you start a server by hand for testing, pass `--stay`, or it ends about 75 s after start without a client.
- Bug report: `Core/BugReport.cs` (zip in `bug-reports/`, secrets never included, redaction), action `bugreport.create`,
  web `/bugreport`, `stykker bugreport <text>`.
- A running server **locks its build output**. Stop it (tray icon, ⏻ in the web UI, `stykker stop`) before building.
- Use a separate data folder for experiments: `--data-dir <folder>` (server, UI and CLI all accept it).
- Release zip: `powershell -ExecutionPolicy Bypass -File build/make-release.ps1 -Version x.y.z` (publishes nothing).

## Where things are

| You want to … | Look at |
|---|---|
| change what the server knows (servers, GPU, history) | `Core/MonitorEngine.cs`, `Core/ServerWatcher.cs`, `Core/ServerRegistry.cs`, `Core/Backends*.cs` |
| add a button that changes something | an action in `Core/Server/ActionApi.cs` (`case "my.action"`), then call it from a Razor page (`WebActions.RunAsync`) |
| show a new value in all UIs | write it in `Core/Server/ServerState.cs` (`StateJson`), read it back in `StateSnapshot` – **additive only, never rename fields** |
| add or change UI text | `Core/Strings*.cs` only (English). No literal text in pages or commands |
| change a web page | `Server/Components/Pages/*.razor`, layout in `Components/Layout/MainLayout.razor`, styles in `wwwroot/app.css` (theme colors come from `Core/ThemeCatalog.cs`) |
| change the terminal display | `Cli/Tui/TuiApp.cs` (frame, keys, panels), `Cli/Tui/StateSource.cs` (server / simulator), commands in `Cli/Program.cs` |
| access, pairing, roles | `Core/Server/AccessControl.cs` (6-digit codes, devices, roles), `Server/AccessGate.cs` (HTTP entry) |
| nodes (several PCs) | `Core/Server/Node*.cs`, page `Nodes.razor`, `docs/nodes.md` |
| model tests (eval) | `Core/Eval/` – suites are JSON (`suite-*.json`, embedded), graders in `EvalGrader.cs`, queue in `EvalQueue.cs` |
| prompt tester with tools | `Core/PromptHarness.cs`, `Core/PromptTools.cs`, page `PromptPage.razor` |
| proxy (one URL for all models) | `Core/ProxyManager.cs`, `Core/RequestProxy.cs`, `Core/ProxyRouter.cs` |
| fake servers for tests and screenshots | `Core/Simulation/` (`SimWorld`, `SimHandler`) |

Architecture notes: `docs/architecture.md`. UI rules: `docs/ui.md`. Nodes today: `docs/nodes.md`; the next step (model hosts, gateway): `docs/plan-hosts-gateway.md`.

## Rules

1. **Logic in Core, text in Strings, UI thin.** A page calls an action or reads the state; it does not
   compute. If two places would need the same code, it belongs in Core.
2. **The web UI controls, `stykker` only shows.** New functions go into the web UI; the terminal display only gets
   things to look at, never buttons or commands (`docs/ui.md`).
3. **Tests for every logic change**, without network: use `FakeHandler`/`FakePlatform` (`tests/.../Fakes.cs`) or the simulator.
4. **0 warnings.** Warnings are errors in spirit; do not suppress them without a comment why.
5. **No new dependencies** (NuGet/npm) without asking the maintainer.
6. **Security:** state JSON never contains tokens, keys or the access code for viewers. Device tokens are stored only as hashes.
   Model-written code and agent tools run only after explicit consent. Never log secrets.
7. **Privacy:** no personal paths, names or machine names in code, tests or docs. User data lives in `%APPDATA%\StykkerLLM`.
8. **Do not kill processes by name** (users run their own copies with the same names). Stop by PID you started.
9. **Commits:** small, one topic, message says what and why. Do not commit `bin/`, `obj/`, data folders or test output.
10. Code comments may be German or English; UI text is English.

## Ports and files

- 8078 web/API, 17500 proxy, 17501 UDP node discovery.
- Data folder: `settings.json`, `access.dat` (devices, code; DPAPI on Windows), `server.key`, `nodes.dat`, `eval-*.json`,
  `eval-results/`, recordings, logs.

## Open work

Tracked as GitHub issues. Larger known gaps: Linux platform (`Core/LinuxPlatform.cs.wip`, not built yet),
real two-PC test of nodes, big-model runs of the eval suites. The web UI runs inside StykkerUI without GPU: keep
endless animations, blur and large shadows out of `html.shell` (see the end of `app.css`), they cost CPU on every tick.
StykkerUI crashed twice in Photino's native window setup (`Photino_ctor`, heap corruption) during testing and could
not be reproduced since; `StykkerUI.log` and the dumps in `%LOCALAPPDATA%\CrashDumps` help if it shows up again. Never call
`SendWebMessage` before the page reported `shell.ready` – that crashed Photino with an access violation (0xc0000005).
