# Stykker

Source of truth for the Stykker tools: local programs for your own machine, kept in one repository.
Each tool builds on its own and has its own README and license.

| Tool | Folder | What it does | License |
| --- | --- | --- | --- |
| **StykkerHUD** | [`hud/`](hud/) | System monitor: processor, memory, graphics, storage and network | Apache-2.0 |
| **StykkerSYS** | [`sys/`](sys/) | Process list: CPU, memory and GPU per process, with end, priority and open file location | Apache-2.0 |
| **StykkerLLM** | [`llm/`](llm/) | Monitor and control center for local LLM servers (llama.cpp, Ollama, LM Studio, vLLM) | BUSL-1.1 |
| **StykkerCmd** | [`cmd/`](cmd/) | Two-panel, keyboard-first file manager for Windows and Linux | Apache-2.0 |

The license of a tool is the `LICENSE` file in its folder.

[`shared/`](shared/) holds the code the tools share (`Stykker.Shared`, C#, see its [README](shared/README.md)) and the design system they are styled with ([`shared/design-system/`](shared/design-system/)).

## Build

Each tool has its own solution:

```bash
dotnet build cmd/StykkerCmd.slnx
dotnet build hud/StykkerHud.slnx
dotnet build sys/StykkerSys.slnx
dotnet build llm/StykkerLlm.slnx
dotnet build shared/Stykker.Shared.slnx
```

The browser checks and screenshots of StykkerHUD and StykkerSYS run from their `tools/` folders with Node.js and Google Chrome. Run `npm install` once in `hud/tools` and in `sys/tools`.

## History

The folders were imported with `git subtree` from the individual repositories
([Stykker-CMD](https://github.com/CrimsonED1/Stykker-CMD), [Stykker-HUD](https://github.com/CrimsonED1/Stykker-HUD),
[Stykker-LLM](https://github.com/CrimsonED1/Stykker-LLM)).
Their commits keep their original hashes, so the history of each tool is still reachable from here.

StykkerSYS was split out of `hud/` in October 2026. The earlier history of the process code is under `hud/`.
