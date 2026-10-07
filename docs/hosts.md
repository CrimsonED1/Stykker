# Model hosts

One PC runs **StykkerLLM-Server** (the web interface, the proxy, the model tests). Other PCs that only run models
run **StykkerHost**: no window, no web page, just a tray icon. The server shows and controls them, and its proxy uses
their models as if they were local.

```
 clients (Qwen Code, Claude Code, …)
        │  http://server:17500/v1
        ▼
 StykkerLLM-Server ──────────────┐
   proxy · web · tests           │  WebSocket (the host dials in)
        │                        ▼
   models on this PC        StykkerHost on PC 2, PC 3 …
                              llama-server / Ollama / LM Studio
```

## Pair a host

1. On the server open **☰ → Hosts**. It shows a six-digit code (valid 10 minutes, once).
2. Start `StykkerHost.exe` on the other PC. Its tray icon offers **Pair with a server …**: a small page lists the servers
   it finds in the network (or type the address, e.g. `http://pc:8078`) and asks for the code.
3. The host stores a token (Windows DPAPI) and connects. It reconnects by itself after a restart or a network gap.

Hosts in the network need **Home/VPN** switched on at the server (☰ → Phone access). The host opens no port: it connects
to the server, all commands and requests travel over that one connection.

## What the server can do with a host

- **☰ → Hosts**: one card per host with GPU, VRAM and RAM, its running model servers (stop, unload) and its GGUF files
  (start with the host's `llama-server`).
- **Monitor**: the servers of all hosts appear under *On model hosts*, with the host's name as a badge.
- **Proxy**: models running on a host are offered in `/v1/models` under their file name and requests go through the
  connection to the host. A model that runs nowhere yet but lies as a GGUF file on a host is **started there on the
  first request** – on the host with the most free graphics memory that has room for it. If no host has room, the client
  gets a clear error; if no host has the file, the request goes to the default target as before.
- **Remove**: the host's token is deleted on the server; the host stops trying after it is refused.

## Host settings

`host-settings.json` in the host's data folder (`%LOCALAPPDATA%\StykkerHost`):

| Field | Meaning | Default |
|---|---|---|
| `ModelRoots` | folders searched for GGUF files | `~/.lmstudio/models` |
| `LlamaServer` | `llama-server` used for *Start* (name in PATH or full path) | `llama-server` |
| `LlamaArgs` | extra arguments before `-m` | `-ngl 99` |
| `AllowPrograms` | further programs the server may start (besides llama-server, Ollama, LM Studio, vLLM, koboldcpp) | empty |

The host only starts model servers and only forwards requests to its own PC (`localhost`). The tray menu also has
**Start with Windows**, **Open log** and **Unpair**.

## As a Windows service

Without anybody signed in, e.g. on a PC in a cupboard: run StykkerHost as a Windows service. In a terminal **as
administrator**, in the folder where `StykkerHost.exe` should stay:

```
StykkerHost install-service
StykkerHost pair-service http://server:8078 123456
```

- The service starts with Windows (delayed) and restarts after a crash. It has no tray icon; the server shows it in
  ☰ → Hosts as usual.
- Its data folder is `%ProgramData%\StykkerLLM\host` (only administrators and the service may write there). The model
  folders are taken from your own `host-settings.json` when the service is installed.
- `StykkerHost unpair-service` drops the pairing, `StykkerHost uninstall-service` removes the service (the data folder
  stays).
- Quit the tray icon and turn off its *Start with Windows*, otherwise the PC shows up twice on the server.
- The service runs as LocalSystem; model servers it starts run under that account too.

## Security notes

- The token is a long random value, stored hashed on the server and protected with DPAPI on the host.
- The pairing code is good for one host, expires after 10 minutes and changes after five wrong tries.
- There is no HTTPS yet: use it in your own network or over a VPN such as Tailscale, not on the open internet.
