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
| `stykker.exe` | `src/StykkerLlm.Cli` | Terminal: one-shot commands (`stykker status`) and an interactive TUI (`stykker`) |
| – | `src/StykkerLlm.Core` | All logic, UI-free and platform-free. Strings, state JSON, actions, eval, proxy, nodes |
| – | `src/StykkerLlm.Platform.Windows` | `IPlatform` for Windows (processes, ports, NVML, DPAPI, tray icon) |
| – | `tests/StykkerLlm.Tests` | MSTest, no network, no real servers (fakes and the simulator) |

There is **one UI codebase for the window, the phone and other PCs: the Razor pages** in
`src/StykkerLlm.Server/Components`. The TUI is the second UI and must offer the same functions (see `docs/ui.md`).

## Build, test, run

```
dotnet build StykkerLlm.slnx -c Release        # must end with 0 warnings, 0 errors
dotnet test tests/StykkerLlm.Tests -c Release  # all green (2 tray tests skip without a desktop)
```

- Run the server: `src/StykkerLlm.Server/bin/Release/net10.0/StykkerLLM-Server.exe --no-browser` → http://127.0.0.1:8078
- Run the window: `src/StykkerLlm.UI/bin/Release/net10.0/StykkerUI.exe` (starts the server if none runs)
- Run the TUI: `src/StykkerLlm.Cli/bin/Release/net10.0/stykker.exe`
- A running server **locks its build output**. Stop it (tray icon, ⏻ in the web UI, `stykker server stop`) before building.
- Use a separate data folder for experiments: `--data-dir <folder>` (server, UI and CLI all accept it).
- Release zip: `powershell -ExecutionPolicy Bypass -File build/make-release.ps1 -Version x.y.z` (publishes nothing).

## Where things are

| You want to … | Look at |
|---|---|
| change what the server knows (servers, GPU, history) | `Core/MonitorEngine.cs`, `Core/ServerWatcher.cs`, `Core/ServerRegistry.cs`, `Core/Backends*.cs` |
| add a button that changes something | an action in `Core/Server/ActionApi.cs` (`case "my.action"`), then call it from a Razor page (`WebActions.RunAsync`) and from the TUI |
| show a new value in all UIs | write it in `Core/Server/ServerState.cs` (`StateJson`), read it back in `StateSnapshot` – **additive only, never rename fields** |
| add or change UI text | `Core/Strings*.cs` only (English). No literal text in pages or commands |
| change a web page | `Server/Components/Pages/*.razor`, layout in `Components/Layout/MainLayout.razor`, styles in `wwwroot/app.css` (theme colors come from `Core/ThemeCatalog.cs`) |
| change the TUI | `Cli/Tui/TuiApp.cs` (command table + dispatch), `Cli/Tui/*Commands.cs`, one-shot commands in `Cli/*Command.cs` |
| access, pairing, roles | `Core/Server/AccessControl.cs` (6-digit codes, devices, roles), `Server/AccessGate.cs` (HTTP entry) |
| nodes (several PCs) | `Core/Server/Node*.cs`, page `Nodes.razor`, `docs/nodes.md` |
| model tests (eval) | `Core/Eval/` – suites are JSON (`suite-*.json`, embedded), graders in `EvalGrader.cs`, queue in `EvalQueue.cs` |
| prompt tester with tools | `Core/PromptHarness.cs`, `Core/PromptTools.cs`, page `PromptPage.razor`, TUI `Cli/Tui/PromptCommand.cs` |
| proxy (one URL for all models) | `Core/ProxyManager.cs`, `Core/RequestProxy.cs`, `Core/ProxyRouter.cs` |
| fake servers for tests and screenshots | `Core/Simulation/` (`SimWorld`, `SimHandler`) |

Architecture notes: `docs/architecture.md`. UI parity rules: `docs/ui.md`. Nodes: `docs/nodes.md`.

## Rules

1. **Logic in Core, text in Strings, UI thin.** A page or TUI command calls an action or reads the state; it does not
   compute. If Web and TUI would need the same code, it belongs in Core.
2. **Web and TUI stay equal.** A feature in one is a feature in the other (or `docs/ui.md` says why not).
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
real two-PC test of nodes, big-model runs of the eval suites, screenshots for the README.
