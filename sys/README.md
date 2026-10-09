# StykkerSYS

The process list of this machine: every process with its CPU share, memory, GPU share and graphics memory, and three things you can do to a process: open its file location, change its priority, end it. Part of the Stykker suite. It was split out of [StykkerHUD](../hud/README.md), which keeps the system monitor. The interface uses the same shared design system as StykkerHUD.

## What it shows

- **One row per program.** Processes with the same name **and** the same file become one row `×N`, with the summed CPU, memory, GPU, graphics memory and threads. The chevron on the left opens the individual processes, each with its own PID and its own menu. Two programs with the same name but different files stay apart.
- **Columns:** name, PID, CPU share (bar and value, of the whole machine), memory (working set), GPU share and graphics memory (Windows GPU counters), threads, a state lamp, and CPU time. The lamp shows *idle* below 2 % CPU, *active* from 2 % and *busy* from 25 %.
- **At most 40 rows** at a time. The heading gives the number of groups shown and the number of processes in the list.
- **Sort** by CPU, memory, GPU or name. **Filter** by name, path or PID. A search expands the matching groups once, so you see the processes behind a sum; you can fold them again.
- **Priority:** on a single process, the current level is greyed out in the menu, and a chip next to the name shows it when it is not Normal.

## Acting on processes

Each row has a menu (`⋯`):

- **Open file location** opens Explorer with the file selected.
- **Priority:** Idle, Below normal, Normal, Above normal, High. **RealTime is not offered**, because a process in that class can lock up the machine.
- **End task** asks first. A bar above the list names the process and what it holds, for example `End chrome (PID 8)? 357 MB working set – unsaved work in it is lost.` Only the button in that bar acts. The outcome arrives as a toast.

On a group row the menu applies to **all** members. *End all 10 tasks* asks once and then reports `10 of 10 done`, or `N of M done` with the first refusal.

## Local only, this page only

- **This machine only.** The server listens on `127.0.0.1`.
- **This page only.** A request must carry the page's own `Origin` (or `Sec-Fetch-Site: same-origin`). Anything else is refused with `403`.
- **JSON only.** Without `Content-Type: application/json` the actions answer `415`, so a form on another website cannot reach them.
- **Never touched:** PIDs 4 and below (System and the like) and StykkerSYS itself. The server refuses with `409` and says why.
- **Access denied:** Windows refuses the action, and the message says to start StykkerSYS as administrator for that process.

No path from the UI ends a whole process tree. The API takes `tree: true`, but no button calls it.

## What the list does not show

Windows does not give an ordinary program the CPU time of every process. Those processes are **left out of the list**: `System` (PID 4), `Registry`, `Memory Compression`, `lsass`, the antivirus engine and many services. In a measurement on 2026-10-09, 215 of 346 running processes were missing.

Their load counts in the processor total of StykkerHUD, so the rows of this list can add up to **less than that total**. In the same measurement the listed rows added up to about half of the processor total, most of the rest being kernel time. A note line that counts the hidden processes is an open item.

GPU values per process come from the Windows GPU counters. Where those counters do not answer, the GPU and graphics-memory columns stay `–`, and the list says so in a note line.

## Layout

| Path | What it is |
|---|---|
| `src/StykkerSys.Core/` | The model (`Samples.cs`), the actions and their refusals (`ProcessActions.cs`), the sampler (`ProcessSampler`: CPU share from time differences, memory every third tick, GPU attached per process) and `SysService` (the viewer loop). Knows no Windows API. |
| `src/StykkerSys.Platform.Windows/` | `WindowsProcessProbe`: GPU per process through the shared counters, file paths through `OpenProcess` and `QueryFullProcessImageNameW`. `Shell.cs` opens Explorer with the file selected. |
| `src/StykkerSys.Server/` | Blazor Server on `http://127.0.0.1:8077`: the page (`Processes.razor`, `sys.js`), `/api/snapshot`, the three action routes under `/api/process/`, and the tray icon. |
| `src/StykkerSys.UI/` | `StykkerSYS.exe`, the Photino window around those pages. Starts the server if none runs. |
| `tests/StykkerSys.Tests/` | xUnit tests: state thresholds, refusals, end and priority on a sacrificial `ping`, the sampler with a fake probe, the Windows probe. |
| `tools/check-actions.mjs` | End-to-end test of the actions on a sacrificial process: changes its priority, drives the row menu, checks that asking protects it, that cancelling ends nothing and that confirming ends it, plus the refusals (cross-site, foreign origin, non-JSON, system PID). |
| `tools/check-groups.mjs` | Grouping: two processes with one name and path become one `×2` row; a search expands them; the arrow folds them again; the folded row shows the sums; *End all* asks first, cancelling ends nothing, confirming ends both. |
| `tools/shot.mjs` | Measures the page and writes screenshots into `docs/screenshots/` when run. |

## Build and run

```powershell
dotnet build StykkerSys.slnx -c Debug

# window (starts the server itself if none runs)
src\StykkerSys.UI\bin\Debug\net10.0\StykkerSYS.exe

# server alone, in the browser
src\StykkerSys.Server\bin\Debug\net10.0\StykkerSYS-Server.exe --port 8077
```

| Switch | Effect |
|---|---|
| `--port <number>` | web port (default 8077) |
| `--no-tray` / `--no-browser` | no tray icon / do not open a browser |
| `--basic` | no GPU values and no file paths; CPU and memory are still shown |
| `--design-system <folder>` | where the design system is read from (default: the monorepo's `shared/design-system`) |
| `--server <path>` (window) | the server to start when none runs |
| `--gpu` (window) | draw through the graphics card. Without it the window draws in software |

Environment variable for the design system: `STYKKERSYS_DESIGN_SYSTEM`.

## Verifying

```powershell
# unit tests (19)
dotnet test tests\StykkerSys.Tests\StykkerSys.Tests.csproj

# with the server running on port 8077 (needs Google Chrome and `npm install` once in tools/)
node tools\check-actions.mjs --port 8077   # 16 checks
node tools\check-groups.mjs --port 8077    # 16 checks
node tools\shot.mjs --port 8077 --out docs\screenshots --name sys
```

Last run on 2026-10-09: 19/19 unit tests, 16/16 action checks, 16/16 grouping checks.

## Not built yet

- **Affinity**: which processors a process may use.
- **Ending a process tree** from the UI. The API takes `tree: true`; no button offers it yet.
- **A note line that counts the hidden processes** (see above).
- **Resident mode**: start with Windows and live only in the tray.
- **Open decision**: Blazor/Photino or Avalonia for the interface. See [StykkerHUD's decision record](../hud/docs/entscheidungen.md).
