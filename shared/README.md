# Stykker.Shared

Code that the Stykker tools share. It is a library, not a program: nothing in it starts by itself. Each tool references it as a project, so a change here reaches every tool when that tool is built.

The rule for what belongs here: code that two tools use, or that would otherwise be copied between them. Anything that only one tool needs stays in that tool.

## What is in it

| Folder | Contents | Used by |
|---|---|---|
| `Gpu/` | `GpuRows.cs`: the rows of a GPU reading, per-process values and engine percentages. | StykkerHUD, StykkerSYS |
| `Windows/Pdh.cs` | A thin wrapper over `pdh.dll`: open, collect and read counters, and split instance names into PID and engine. | StykkerHUD, StykkerSYS |
| `Windows/GpuCounters.cs` | `GpuUtilQuery` (utilisation per engine and per process) and `GpuProcessMemoryQuery` (graphics memory per process). | StykkerHUD, StykkerSYS |
| `Windows/TrayIcon.cs` | Tray icon with a context menu, on a message-only window in its own thread. | StykkerHUD, StykkerSYS |
| `Web/DesignSystemHost.cs` | Serves the design system under `/ds` from a folder: `--design-system`, the environment variable, or the default `shared/design-system`. | StykkerHUD, StykkerSYS |
| `Web/LocalRequests.cs` | Guard for local actions: same-origin check (403), failed action (409), done (200). | StykkerSYS |
| `Web/ToolHost.cs` | One server per port (a named mutex `Local\<tool>-server-<port>`) and opening the browser. | StykkerHUD, StykkerSYS |
| `Web/OneDecimalJson.cs` | JSON converter that rounds every number to one decimal place. | StykkerHUD, StykkerSYS |
| `Sampling/ViewerLoop.cs` | Measures only while someone looks: the first read starts a one-second loop, and 15 seconds without a read stop it. | StykkerHUD, StykkerSYS |
| `ActionResult.cs` | `ActionResult(bool Ok, string Message)`: the answer of an action. | StykkerSYS |

## Stykker.Pairing

A second library in `src/Stykker.Pairing`: pairing for all tools. One file, `%APPDATA%\Stykker\pairing.dat`, holds the access code, the paired devices with a role per tool, and the model hosts. A tool that starts later reads the same file and knows the devices that paired elsewhere; there they are viewers until someone gives them more.

HUD, SYS and StykkerLLM use it. StykkerLLM moved over last (`docs/plan-llm-modul.md`, step L2).

| File | Contents |
|---|---|
| `PairingStore.cs` | The file. Several processes read and write it: a named mutex per file, a fresh read before every write, atomic replace. Bound to the Windows user with DPAPI; plain JSON elsewhere. |
| `PairingService.cs` | The rules: a six-digit code that lasts ten minutes and one sign-in, pairing in both directions, devices, roles per tool, hosts. |
| `PairingCodes.cs` | Codes, tokens and their hash. The hash is the one StykkerLLM stored devices with, so they stay signed in. |
| `LegacyLlmImport.cs` | Takes over `access.dat` and `hosts.dat` from StykkerLLM's data folder, once. StykkerLLM then renames them to `*.bak`; the file records what it imported. |

It has no ASP.NET reference and no package dependency.

## Build and test

```powershell
dotnet build Stykker.Shared.slnx -c Debug
dotnet test Stykker.Shared.slnx
```

The PDH and tray parts only work on Windows. `Stykker.Shared` targets `net10.0` and references ASP.NET Core (`FrameworkReference Microsoft.AspNetCore.App`), because `Web/` uses its types.

`tests/Stykker.Pairing.LockProbe` is a helper program, not a test project: the pairing tests start it several times at once to write the same file.

Last run on 2026-10-13: 63 of 63 tests passed in `Stykker.Shared.Tests` and 42 of 42 in `Stykker.Pairing.Tests`, the build shows no warnings.

## Using it from a tool

```xml
<ProjectReference Include="..\..\..\shared\src\Stykker.Shared\Stykker.Shared.csproj" />
```

The tool's own solution lists the project under a `/shared/` folder, so it builds with the tool.

License: Apache-2.0 (see [`LICENSE`](LICENSE)).
