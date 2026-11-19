# StykkerMeshShare

A shared folder on every PC. Each PC syncs everything in its folder with all other PCs it knows. There is no central
server: the PCs connect to each other. A phone (iPhone) opens the same page in the browser to look at files,
download them and add new ones; it does not keep a copy of the folder.

Status: first version. Tested with several PCs simulated in one process and with two servers on one machine.
Not yet tried between two real PCs, over Tailscale or on a phone.

## Use

Start `StykkerMeshShare.exe` (window) or `StykkerMeshShare-Server.exe` (tray only). The page has two parts:

- **Drop zone**: drop files or whole folders, or choose files. They land in the shared folder and reach every PC.
- **Sync status**: state of this PC, the other PCs (online, last sync), and a field to add a PC by name or address.

Below are the files (click to download) and what happened recently.

The shared folder is `%USERPROFILE%\StykkerMeshShare`. **Change folder** (on the PC itself, not from the phone) picks
another one; it is remembered in `settings.json`, and `--folder` does the same from the command line. The new folder
must be empty or not exist yet:

- **Move the files there**: everything moves (times are kept). The other PCs notice nothing.
- **Start empty**: the new folder gets all files from the other PCs. The old folder stays as it is, and no deletion
  reaches the other PCs. Files that existed only on this PC stay in the old folder.

A whole drive, the user profile itself, Windows, the program folders, AppData and the data folder cannot be shared.
Closing the window does not stop syncing; the tray icon has **Shut down**.

## How it syncs

```
PC A  ◀──── pull ────▶  PC B  ◀──── pull ────▶  PC C
 index.json              index.json              index.json
```

- Each PC keeps an index of its folder: path, size, time, SHA-256 and a **version vector** (one counter per PC).
  A local change raises this PC's counter.
- Each PC **pulls** from every other PC: "what changed since number N?" (`/mesh/changes`), then the files
  (`/mesh/file`). A long poll (`/mesh/wait`) says when something changed. Nobody pushes files to anyone.
- Changes also travel over a middle PC: if A and C do not see each other but both see B, they still sync.
- A file that exists here with the same content (a rename, a copy) is copied locally instead of downloaded.
- **Conflict** (changed on two PCs at the same time): the later change keeps the name, the other becomes
  `name (conflict <PC> <date time>).ext`. Both PCs make the same copy, so nothing is lost.
- **Changed here, deleted there**: the change wins and the file comes back.
- **Trash**: a file that is overwritten or deleted by another PC goes to `trash\` in the data folder for 30 days.
- **Held deletion**: if (almost) all files vanish at once, for example when a drive is not connected, the deletion
  is held and the page asks before it reaches the other PCs.
- A new PC with files of its own merges them in. The same path with different content becomes a conflict;
  nothing is overwritten.
- Not synced: `~$*`, `*.tmp`, `*.partial`, `*.part`, `*.crdownload`, `Thumbs.db`, `desktop.ini`, `.DS_Store`,
  anything starting with `.stykker`, links and junctions (they are not followed). Empty folders are not synced.

Index, trash and settings live in `%APPDATA%\Stykker\MeshShare`, not in the shared folder.

## Transfers

- **Progress**: the page shows the file being received, how much of it, the rate and the time left, and what comes
  next.
- **Order**: drag the waiting files into the order you want (on the phone: the arrow button). A file dragged to the
  very top is loaded now: the running file pauses and later continues where it stopped. The order is kept in
  `queue.json` and survives a restart.
- **Continue after a break**: a file is received into `partial\` in the data folder. Every 8 MB the data is written
  to disk and the block's SHA-256 is noted. After a lost connection, a crash or a restart, the blocks already there
  are read and checked again, and the transfer continues after the last good block (HTTP range request). It also
  continues from another PC that has the same content. Unused partial files are removed after 14 days.
- **Checked by hash**: a received file is used only if the SHA-256 of the whole file equals the hash of the sending
  PC. Otherwise it is thrown away and loaded again (the activity list shows "Check failed").
- **Changed files**: each PC notices changes in its folder (watcher plus a full scan every minute; size and time,
  then SHA-256). A PC does not hand out a file that changed since its last scan. If a file changes on the other PC
  during a transfer, the half-received old version is dropped and the new one is loaded.

## Finding the other PCs

- **Home network**: PCs find each other by themselves. Each PC sends a short UDP broadcast on port 17404 into every
  home network it is connected to (each network adapter on its own), at start, every 20 seconds and when the network
  changes. A PC that hears it answers at once, so two PCs find each other within seconds. Found addresses that do not
  answer for 10 minutes are forgotten.
- **Tailscale** (no broadcast there): enter the other PC's Tailscale name in the page, for example
  `pc.example.ts.net`. It is kept in `settings.json`.

**LAN first.** The same PC under two addresses syncs over only one of them, and over the best way: the home network
before Tailscale. When a PC that is connected over Tailscale shows up in the home network, syncing moves to the home
network at once, also in the middle of a transfer (the file continues over the faster way). When the home network
address stops answering, the Tailscale address takes over. The page shows the way (`via LAN`, `via Tailscale`).

## Access (no pairing yet)

The server answers this machine, the private ranges of the home network (`10/8`, `172.16/12`, `192.168/16`,
link-local) and Tailscale (`100.64.0.0/10`, `fc00::/7`). **There is no pairing yet: everyone in the home network can
read and change the shared folder.** The connection between PCs is plain HTTP; over Tailscale it is encrypted by
Tailscale. Pairing through `Stykker.Pairing` and HTTPS come later.

Uploads and other actions are accepted only from the page itself (same origin).
Windows asks once whether the program may use the network; allow it for private networks.

## Phone

Open `http://<pc>:17404/` in Safari. "Add to Home Screen" puts it on the home screen. Installing it as a real web
app needs HTTPS, which is not there yet. Safari cannot offer a web page in the Share menu of other apps, so on the
iPhone files are added with **Choose files** on the page.

## Switches

```
StykkerMeshShare-Server [--port 17404] [--folder <folder>] [--data-dir <folder>] [--name <text>]
                        [--peer <host[:port]>]... [--no-discovery] [--local-only] [--no-tray] [--no-browser]
StykkerMeshShare [--port 17404] [--server <path>] [--gpu]
```

Trial run with two servers on one machine (temp folders, no network):

```
StykkerMeshShare-Server --port 27404 --folder <temp>\A\share --data-dir <temp>\A\data --local-only --peer 127.0.0.1:27405 --no-tray
StykkerMeshShare-Server --port 27405 --folder <temp>\B\share --data-dir <temp>\B\data --local-only --peer 127.0.0.1:27404 --no-tray
```

## Build and test

```
dotnet build meshshare/StykkerMeshShare.slnx -c Release
dotnet test meshshare/tests/StykkerMeshShare.Tests -c Release
```

## Open

- Pairing (`Stykker.Pairing`) and HTTPS between the PCs.
- Tombstones are kept forever; a cleanup after a long time is still missing.
- The queue shows only the files of the PC being synced right now, not those still waiting on other PCs.
- Tile in the hub (`/api/module`).
