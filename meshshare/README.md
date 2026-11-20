# StykkerMeshShare

A shared folder on every PC. Each PC syncs everything in its folder with all other PCs it knows. There is no central
server: the PCs connect to each other. A phone (iPhone) opens the same page in the browser to look at files,
download them and add new ones; it does not keep a copy of the folder.

Status: first version. Tested with several PCs simulated in one process and with two servers on one machine.
Not yet tried between two real PCs, over Tailscale or on a phone.

## Use

Start `StykkerMeshShare.exe` (window) or `StykkerMeshShare-Server.exe` (tray only). The page shows:

- **Drop zone**: drop files or whole folders, or choose files or a folder. **To** picks who gets them: *Every PC*
  (the mirror) or only certain PCs, for example just the laptop. With certain PCs, *Remove from this PC once it
  arrived* turns it into a move from A to B. "Into" picks a subfolder. Each upload can be cancelled; closing the page
  during an upload asks first. The choice of PCs is remembered in the browser.
- **Sync status**: state of this PC, the other PCs (online, last sync, LAN or Tailscale), and a field to add a PC by
  name or address.
- **Transfers**: the file being received (progress, rate, time left), a bar for all files of this round, and the
  waiting files. Change the order by dragging or with the arrows; ▶ loads a file now, ✕ does not load it.
  The progress also shows in the window title.
- **Files**: every file in the mesh, also those that only lie on other PCs. Each row shows per PC whether it has the
  file (green), is receiving it or will get it (half), or not. Click to open (pictures, PDFs, text, audio and video
  open in the browser, also on the phone; other files download) or use the arrow to download; a file that is not on
  this PC shows only its name. Conflict copies are marked; search "conflict" lists them.
  **Select** files (round checks) to manage them from any PC or the phone:
  - **Send to…**: also to these PCs; optionally remove it from this PC once it arrived.
  - **Rename**, **Move** into a folder (a new folder is made when needed). The other PCs move their copy instead of
    loading it again.
  - **Delete…** on chosen PCs. Deleted is gone for good, there is no trash: the space is free right away. Chosen on
    every PC that has it, the file is gone everywhere.
- **Activity**: what happened lately, including "arrived at …" for files sent from here.

**Other PCs** in the list show, live while the page is open: what they are receiving (file, %, rate, what is left),
their free space, whether their sync is paused, and **outdated** when they run an older StykkerMeshShare than the
newest PC in the mesh (each PC reports when its program was built). If this PC is the outdated one, the page says so.

**The window** (`StykkerMeshShare.exe`) looks like the Stykker Starter: no Windows frame, the page header is the title
bar. It has the tray icon: Open, Open the shared folder, Quit. Closing the window asks: keep it in the tray, or quit
("Don't ask again" is kept; the tray menu offers to ask again). **Quit also stops the server**, so syncing on this PC
ends with the window. The window starts the server without a tray icon of its own; a second start only brings the
window to the front. The tray also shows a notice for a conflict copy, a held deletion and a full disk.

The shared folder is `%USERPROFILE%\StykkerMeshShare`. **Change folder** (on the PC itself, not from the phone) picks
another one; it is remembered in `settings.json`, and `--folder` does the same from the command line. While typing,
the page says whether the folder works. The new folder must be empty or not exist yet:

- **Move the files there**: everything moves (times are kept). The other PCs notice nothing.
- **Start empty**: the new folder gets all files from the other PCs. The old folder stays as it is, and no deletion
  reaches the other PCs. Files that existed only on this PC stay in the old folder.

A whole drive, the user profile itself, Windows, the program folders, AppData and the data folder cannot be shared.
Run on its own (`StykkerMeshShare-Server.exe`), the server has its own tray icon with **Shut down**.

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
- **No trash**: a file that is overwritten or deleted by another PC is gone for good. MeshShare is for passing files
  around, not for keeping them; the disks should not fill up. (A trash from an older version is emptied at start.)
- **Targets**: each file knows the PCs that should have it (or all). A PC that is not a target does not load it. A PC
  that has it but is not a target (the sender, or one where it was deleted) keeps it until every target has it, then
  removes it. Each PC learns who has what from the change lists of the others.
- **Held deletion**: if (almost) all files vanish at once, for example when a drive is not connected, the deletion
  is held and the page asks before it reaches the other PCs.
- A new PC with files of its own merges them in. The same path with different content becomes a conflict;
  nothing is overwritten.
- Not synced: `~$*`, `*.tmp`, `*.partial`, `*.part`, `*.crdownload`, `Thumbs.db`, `desktop.ini`, `.DS_Store`,
  anything starting with `.stykker`, links and junctions (they are not followed). Empty folders are not synced.

Index and settings live in `%APPDATA%\Stykker\MeshShare`, not in the shared folder.

## Transfers

- **Progress**: the page shows the file being received, how much of it, the rate and the time left, and what comes
  next.
- **Order**: drag the waiting files into the order you want (on the phone: the arrow button). A file dragged to the
  very top is loaded now: the running file pauses and later continues where it stopped. The order is kept in
  `queue.json` and survives a restart.
- **Continue after a break**: a file is received into `.stykker\partial\` inside the shared folder (hidden, never
  synced; on the same drive, so taking it over is a rename, not a copy). Every 8 MB the data is written to disk and
  the block's SHA-256 is noted. After a crash or a restart, the blocks already there are read and checked again, and
  the transfer continues after the last good block (HTTP range request). A file that was paused or lost its
  connection keeps its partial file open and continues exactly where it stopped. It also continues from another PC
  that has the same content. Unused partial files are removed after 14 days.
- **Checked by hash**: a received file is used only if the SHA-256 of the whole file equals the hash of the sending
  PC. Otherwise it is thrown away (the activity list shows "Check failed") and that content is not loaded again for
  10 minutes, so a broken file does not cost the same gigabytes over and over.
- **Changed files**: each PC notices changes in its folder (watcher plus a full scan every minute; size and time,
  then SHA-256; a time in the future counts as settled). Hashing runs without holding up the other PCs. A PC does not
  hand out a file that changed since its last scan, and it hashes what it sends: if the content changed without a new
  size or time, it hashes the file again before sending it once more. If a file changes on the other PC during a
  transfer, the half-received old version is dropped and the new one is loaded.
- **Stuck connections**: if no data comes for 60 seconds, the transfer stops and continues later; TCP keepalive
  notices a dead connection. A file that is open here (for example in Word) and should be deleted or replaced is
  tried again later; the other changes still arrive.
- **Disk space**: a file is received only if the drive keeps at least 512 MB free.
- **Free space**: the page shows the free space of the drive of the shared folder, for this PC and for every other
  PC (each PC sends it along with its answers). Below a limit (default 5 GB, "Change" on the PC itself, 0 = off,
  kept in `settings.json`) a PC receives nothing: no downloads from other PCs and no uploads from the page. It still
  sends its own changes and deletions, and it continues by itself once enough is free. A download starts only if
  the limit stays free afterwards. The other PCs show "not receiving" for it, and the tray says so.
- **Sync pause, from any PC**: "Pause sync" in the status card stops all syncing of this PC; the pause button in the
  row of another PC does the same for that PC, and so does the phone. A paused PC receives nothing (no files, no
  deletions), and the other PCs take nothing from it. It keeps its own changes and sends them once resumed.
  Everyone sees who paused it ("sync paused by NB2"); anyone can resume. The pause survives a restart. A PC asks
  another one over `POST /mesh/pause` with the header `X-Stykker-Mesh: 1` (a web page cannot send it without CORS).
- **Skip**: "Don't load" skips a file until you allow it again. Kept in `queue.json`, like the pause.

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
read the shared folder and add files to it.** The connection between PCs is plain HTTP; over Tailscale it is
encrypted by Tailscale. Pairing through `Stykker.Pairing` and HTTPS come later.

- **Only on the PC itself**: changing the folder, adding and removing PCs, confirming held deletions and opening the
  Explorer. Network paths (`\server\share`) and network drives cannot be the shared folder.
- **Only from the page itself** (same origin): uploads, sending, renaming, moving and deleting files, the queue, pause and skip.
- **Host names**: the server answers only under this PC's own names: an IP address, `localhost`, the computer name
  (with the network's DNS suffix), `<computer name>.<tailnet>.ts.net`, or a name listed under `hostNames` in
  `%APPDATA%\Stykker\stykker.json`. This blocks web pages that point their own name at this PC (DNS rebinding).
  If another PC reaches this one under a different name (an alias), add that name there.
- **Links**: files behind a link or junction inside the shared folder are never read, sent or written.
- **Limits**: answers from other PCs are limited in size and time, at most 64 PCs found by broadcast are kept, and a
  broadcast gets at most one answer per sender every 5 seconds.
- **Not solved without pairing**: a device in the home network can claim to be another PC (its ID). It can then
  stop syncing with that PC over a slower way. Pairing will fix this.
Windows asks once whether the program may use the network; allow it for private networks.

## Phone

The phone uses the page as a web app. For that it needs HTTPS, which Tailscale provides on this PC (the PCs among
each other stay on plain HTTP on 17404):

```powershell
tailscale serve --bg --https=17444 http://127.0.0.1:17404
```

Then open `https://<pc>.<tailnet>.ts.net:17444/` in Safari and choose "Add to Home Screen". Tailscale keeps this
setting across restarts; `tailscale serve --https=17444 off` removes it. Only devices in your tailnet reach it.

The server recognizes requests that `tailscale serve` passes on (from 127.0.0.1 with `X-Forwarded-For` and
`X-Forwarded-Proto`, trusted only from 127.0.0.1): the phone's real address counts, so the phone is never treated as
"the PC itself", and the page works over https. Port 17444 and not 17404, because on 17404 the PCs talk to each other
over Tailscale in plain HTTP; a serve on that port would take those connections.

Without Tailscale, `http://<pc>:17404/` works in the home network, but only as a bookmark, not as a web app.
Safari cannot offer a web page in the Share menu of other apps, so on the iPhone files are added with
**Choose files** on the page. Pictures, PDFs and videos open directly in Safari. On the phone the page cannot change
the folder, add or remove PCs or confirm held deletions (only on the PC itself).

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
- Choosing the folder in a dialog (today the path is typed; the page checks it while typing).
- Finding a file with the same content already here is a scan over the index for every download (fine for tens of
  thousands of files, slow for millions).
