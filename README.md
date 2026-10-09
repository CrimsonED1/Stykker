# Stykker

Source of truth for the Stykker tools: local programs for your own machine, kept in one repository.
Each tool builds on its own, has its own README and license, and is released on its own.

| Tool | Folder | What it does | License |
| --- | --- | --- | --- |
| **StykkerHUD** | [`hud/`](hud/) | Live monitor for processes, GPU, CPU and memory | Apache-2.0 |
| **StykkerLLM** | [`llm/`](llm/) | Monitor and control center for local LLM servers (llama.cpp, Ollama, LM Studio, vLLM) | BUSL-1.1 |
| **StykkerCmd** | [`cmd/`](cmd/) | Two-panel, keyboard-first file manager for Windows and Linux | Apache-2.0 |
| **Stykker.NanoCut** | [`nanocut/`](nanocut/) | C# library for material removal and penetration in 2D and 3D | BUSL-1.1 |

The license of a tool is the `LICENSE` file in its folder.

## Build

Each tool has its own solution:

```bash
dotnet build cmd/StykkerCmd.slnx
dotnet build hud/StykkerHud.slnx
dotnet build llm/StykkerLlm.slnx
dotnet build nanocut/Stykker.NanoCut.slnx
```

## History

The folders were imported with `git subtree` from the individual repositories
([Stykker-CMD](https://github.com/CrimsonED1/Stykker-CMD), [Stykker-HUD](https://github.com/CrimsonED1/Stykker-HUD),
[Stykker-LLM](https://github.com/CrimsonED1/Stykker-LLM), [Stykker-NanoCut](https://github.com/CrimsonED1/Stykker-NanoCut)).
Their commits keep their original hashes, so the history of each tool is still reachable from here.
