# stykker-llm

Windows monitor for local LLM servers (**llama.cpp**, **Ollama**, **LM Studio**): discovery, tokens/s, VRAM, request log, proxy and benchmarks.

> Not published on npm yet ("coming soon"). Until then, download the zip from
> [GitHub Releases](https://github.com/CrimsonED1/Stykker-LLM/releases).

## Install

```
npm install -g stykker-llm
```

Works with `--ignore-scripts` (this package uses **no install scripts**; the Windows app comes as the
optional dependency `@stykker-llm/win32-x64`, about 75 MB).

## Run

```
stykker-llm
```

The `stykker-llm` command starts the GUI detached from the terminal. Supported: Windows x64
(installs on other platforms but refuses to run with a clear error).

## Requirements

- Windows 10 1809 or newer, x64. No .NET or other runtime needs to be installed.
- Node.js 18+ (only for this launcher)

- License: Business Source License 1.1 (free for personal and non-commercial use; Apache-2.0 after four years)