# Contributing to StykkerLLM

Thank you for considering a contribution!

1. **Open an issue first** for anything bigger than a small fix, so we can agree on the direction.
2. **Fork the repository** and create a branch for your change.
3. **Build and test.** You need the .NET 10 SDK on Windows:
   ```
   dotnet build StykkerLlm.slnx -c Release
   dotnet test tests/StykkerLlm.Tests -c Release
   ```
   The build must produce 0 warnings and 0 errors, and all tests must pass. Please add tests for logic in `src/StykkerLlm.Core/`.
4. **No new NuGet or npm dependencies** unless discussed in an issue.
5. **Keep it small and focused.** Match the style of the surrounding code; UI texts are English.
6. **Privacy:** user data stays under `%APPDATA%\StykkerLLM\`; never commit API keys, tokens or personal paths.
7. Read [AGENTS.md](AGENTS.md) – layout, rules and where to put what (also for coding agents).

Project layout: `src/StykkerLlm.Core/` (UI-free logic: discovery, recording, proxy, benchmark, simulation),
`src/StykkerLlm.Platform.Windows/` (Windows specifics), `src/StykkerLlm.Server/` (the core server with the web UI),
`src/StykkerLlm.UI/` (the window `StykkerUI`), `src/StykkerLlm.Cli/` (terminal companion `stykker`), `tests/StykkerLlm.Tests/` (MSTest), `build/` (packaging scripts).

Thank you for helping!
