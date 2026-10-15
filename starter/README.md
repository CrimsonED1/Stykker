# Stykker Starter

Runs programs from a source folder as local copies and keeps them up to date. You give it the path of a `.exe`. It copies the **whole folder** of that file to `%LOCALAPPDATA%\Stykker\Starter\<id>\<version>\` and starts the copy. The source can then be replaced or rebuilt while the copy runs. The Starter stays in the tray, checks the source every few minutes, and offers an update when the source is newer. Part of the Stykker suite. It uses the design system of [`shared/design-system`](../shared/design-system/); there is no server and no port.

## What it does

- **Add a program:** choose the `.exe`. The Starter reads its folder, shows the file version, copies the folder into a new version folder, checks every file against the source (size and SHA-256), and starts the copy.
- **Version:** the file version of the `.exe` (`FileVersion`, else `ProductVersion`). If that is the same and the folder content differs, the new copy goes into `<version>+<8 characters of a hash>`. Without a file version, the hash of the folder list names the version.
- **Update:** the new version is copied next to the running one first. Only then does the Starter stop the processes that run from the old folder: first a polite close, after 10 s the end. Then it starts the new version. If the new one does not start (the start fails, or it ends with a code other than 0 within 5 s), the old one is started again and the window says so.
- **Stopping is by folder:** every process whose program file lies in that version's folder is stopped, including what the program started from there. Never by name. Folder names with a common prefix (`1.0` and `1.0.1`) stay apart.
- **Copies are never half:** a copy goes into `<version>.partial`, is checked, and only then gets its name. A cancelled or failed copy leaves no partial folder and no finished folder; the running version is untouched.
- **Cleanup:** an old copy is removed when nothing runs from its folder and it is outside the number of versions kept (default 2, so you can go back).
- **Source not reachable** (a share is offline): the program keeps running from its last copy, and the status says so.
- **Without a copy** (option "Copy to this PC before starting" off): the program runs directly from the folder of its source. Nothing is copied and nothing is kept under the data folder. The Starter checks the source at each check. A changed build counts once two checks in a row see the same state; then, if the program runs, the Starter stops what runs from that folder (polite close, after 10 s the end) and starts it again, or only offers it when the settings ask for that. The version and the file-list hash the program was started with are kept in `starter.json`. A new build that does not start is not rolled back; the status says so. If the source is not reachable, the program cannot start. Only `.exe` and `.dll` files in all subfolders count as a changed build; logs, data and settings files do not.
- **Autostart:** an entry under `HKCU\…\CurrentVersion\Run` that starts the Starter in the tray (`--tray`), only when the setting is on. A test data folder never changes the registry.
- **One Starter per user:** a second start hands over to the first and exits.

## Build and run

```powershell
dotnet build starter\StykkerStarter.slnx -c Debug
dotnet test  starter\StykkerStarter.slnx

# the Starter (opens its window; the tray icon stays after closing)
starter\src\StykkerStarter.App\bin\Debug\net10.0-windows\StykkerStarter.exe
```

| Switch | Effect |
|---|---|
| `--tray` | tray icon only, no window (used by the autostart entry) |
| `--data-dir <folder>` | another data folder (tests, trial runs). Then the Starter never writes to the registry |
| `--gpu` | draw through the graphics card. Without it the window draws in software, like StykkerUI |
| `--check-every <seconds>` | check interval in seconds instead of the setting (for trial runs) |

The data folder holds `starter.json` (programs and settings, no secrets, no process IDs), `starter.log` (one line per event), the version folders, and the WebView2 cache in `web-shell\`.

## Layout

| Path | What it is |
|---|---|
| `src/StykkerStarter.Core/` | Configuration (`Config/`), the source tree and its list hash (`Source/`), the version rule (`Versions/`), the checked copy (`Copy/`), processes by folder and the stop (`Processes/`), the start check (`Start/`), the update, rollback and cleanup (`Update/ProgramService.cs`) and the update policy (`Update/StarterEngine.cs`). Knows no Windows API. |
| `src/StykkerStarter.Platform.Windows/` | Process list with image paths (`QueryFullProcessImageNameW`), polite close and kill by PID, the launcher, the file version, the autostart entry. |
| `src/StykkerStarter.App/` | `StykkerStarter.exe`: the tray and the window (`Shell.cs`), the worker that runs every operation in order (`Model.cs`), the single-instance guard, and the page (`Web/`: `index.html`, `app.css`, `app.js`), embedded with the design system from `shared/design-system`. |
| `tests/StykkerStarter.Tests/` | xUnit tests. Real temp folders for copies; fakes for processes and starts; one integration test with the real process. |
| `tests/StykkerStarter.TestTarget/` | a small console program used as the target of the tests. Its file version is set with `-p:TargetVersion=…`. |

## Limits

- **Console programs** have no main window, so the polite close does nothing. They are ended after the 10 s grace period.
- **Started from elsewhere:** a program that someone started by hand from the copy's folder is stopped on an update too. The update dialog says which processes are ended.
- **Detection by content only** (without the file version) is not offered as a choice; the file version decides, and the folder content decides when it is equal.
- **Without a copy**, a build into the source folder can fail while the program runs, because Windows locks the running files. Every process that runs from the source folder, or from a subfolder of it, is stopped on a restart.
- **No editing** of a program after adding it. Remove it and add it again.

## Verifying

```powershell
dotnet test starter\StykkerStarter.slnx
```

Last run on 2026-10-15: 72 of 72 tests passed, the build shows 0 warnings. A trial run with the test program, with no copy, showed: version 1 starts from the source folder; a changed `.dll` in a subfolder is seen at the second check, and the program is restarted from the same folder (the old process ended after 10 s); a log file and a text file change nothing; with the program stopped, a version 2 in the folder is reported as changed and nothing is started, and a restarted Starter starts version 2; with "ask before updating" on, a change is only offered and the running program is left alone. The trial run with a copy (2026-10-14) showed: copy and check of version 1, start, the next check finding version 2, stop of the old process (ended after 10 s), start of version 2 on a new process ID, removal of version 1, and a second start handing over to the first instance.

License: Apache-2.0 (see [`LICENSE`](LICENSE)).
