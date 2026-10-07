# Plan: model hosts (private) and gateway (business)

Status: **P1 done** (StykkerHost skeleton with tray, 2026-10-07); P2 next. Replaces the hub/node design in [nodes.md](nodes.md).

## Decisions

| Topic | Decision |
|---|---|
| Roles | One **server** (UI, data, proxy, tests). All other PCs are **model hosts**: no UI, they only run models. |
| Host program | Own small program **`StykkerHost.exe`** in the same zip (no web UI, no WebView2). |
| Connection | **Hosts dial in** to the server (outgoing WebSocket). Only the server listens. No firewall prompt on hosts, works over Tailscale and behind NAT. |
| Placement | **Automatic**: a request for a model goes to the host that runs it, or the server starts it where the file is and VRAM is free. |
| Goal 1 | **Private**: simple, six digits, one user, HTTP in LAN/VPN/Tailscale. |
| Goal 2 | **Business**: the server becomes a **gateway** with SSO (generic OIDC, tested with Keycloak), personal API keys, rules per group, quotas and audit. |
| Business operation | **On-prem only**, **one company per gateway**. |
| Prompt texts in audit | **Off by default, can be switched on** by the company (with retention period). |
| Repository | **Same repository** (BUSL-1.1: commercial use needs a license). |
| Service | Server/gateway and host can run as a **Windows service** (and systemd on Linux) – standard for business, optional for private. |

## Shared building blocks

- `StykkerHost`: Core engine headless (measuring, model file scan, launcher), host link, tray on Windows.
- Host link: one WebSocket from host to server; framed messages with ids: state push, commands (start, stop, unload,
  list models), and **tunnelled HTTP** for inference (request/response chunks multiplexed on the same socket, streaming).
- Server side: `HostRegistry` (from `NodeRegistry`), scheduler (from `NodeScheduler`), proxy routes into the tunnel.
- Reused from today: pairing code logic, UDP discovery (reversed: a host finds servers), scheduler, proxy routing,
  `StateJson` per host.

## Goal 1 – private (work packages)

| # | Package | Content | Test |
|---|---|---|---|
| P1 ✓ | StykkerHost skeleton | New project `src/StykkerLlm.Host`, Core engine without Blazor, tray (status, pairing, quit, start with Windows), log `StykkerHost.log` | starts, measures, tray appears, no web port open |
| P2 | Host link | WebSocket client in the host, `/hosts/connect` on the server, reconnect with backoff, state push every second | simulated host ↔ server in-process, reconnect after drop |
| P3 | Pairing | Server shows **add host**: code + address/QR. Host: tray **Pair…** opens a small local page (127.0.0.1 only) with the servers found in the LAN and the code field. Token stored on both sides (DPAPI), revocable | wrong code, expired code, revoke token |
| P4 | Commands + tunnel | start/stop/unload on host, model file list per host, inference tunnel with streaming (OpenAI + Anthropic), cancel on disconnect | streaming through the tunnel with the simulator, byte-exact |
| P5 | Server UI | Page **Hosts** replaces Nodes; monitor shows servers of all hosts with a host badge, GPU card per host; profiles get **Run on: this PC / host …**; model picker per host | web, phone; display in `stykker` (read-only) |
| P6 | Automatic placement | Proxy: running → route; else file + free VRAM → start there, wait until ready; idle unload as today; clear errors (no host has the model, no VRAM) | scheduler unit tests, start-on-demand in the simulator |
| P7 | Clean-up | Remove hub/node code and pages (role hub, result sync `/api/eval/runs`, node web pages), keep discovery/scheduler; docs, README | build, all tests, no dead strings |
| P8 | Host as service (optional) | `StykkerHost install-service` / `uninstall-service` (needs admin); tray companion talks to the service locally | install/uninstall on a test VM |

Order: P1 → P2 → P3 → P4 → P5 → P6 → P7, P8 any time after P4.

## Goal 2 – business (outline)

| # | Package | Content |
|---|---|---|
| B1 | Service hosting | Server as **Windows service** and **systemd/Docker**; data in `%ProgramData%\StykkerLLM` (or `/var/lib/stykker`); secrets with machine scope (DPAPI LocalMachine on Windows, file with ACL on Linux); always on (no UI binding); HTTPS with a configured certificate (`gateway.json`) |
| B2 | Accounts and keys | Local admin account for the first start; **personal API keys** per user (hashed, revocable, expiry); admin web for users and keys |
| B3 | SSO | Generic **OIDC** (authorization code + PKCE), tested with **Keycloak**; works with Entra ID and Google; groups from the token claims mapped to StykkerLLM groups |
| B4 | Rules | Per group: allowed models, quotas (tokens/day, requests/minute), priority in the queue; enforcement in the proxy; refused requests get 403 with the reason |
| B5 | Audit and usage | Per request: user, group, model, host, tokens, durations, result; **prompt texts optional** (off by default, retention in days); export CSV/JSON (SIEM); usage per person/group/model |
| B6 | Hosts for business | Enrollment keys from the admin web instead of six digits; hosts dial in over **HTTPS/WSS**; host as Windows service or systemd |
| B7 | Linux host | Finish the Linux platform (processes, ports, `nvidia-smi`) for headless GPU servers; install script + systemd unit |

## Windows service – what changes

- **Session 0**: a service has no desktop, so no tray icon. An optional **tray companion** in the user session shows status
  and the pairing code and talks to the service over a local endpoint.
- **Secrets**: DPAPI bound to the user does not fit a service account → machine scope (LocalMachine) or a key file with ACL.
- **Data folder**: `%ProgramData%\StykkerLLM` instead of `%APPDATA%`.
- **Lifetime**: a service is always on; the binding to windows/terminals (ServerHolds) is off in service mode.
- **Models**: model servers started by the service run in the service session; detecting servers started by users works
  (the service may even see more processes).
- **Installation**: words, not options: `StykkerHost install-service`, `StykkerLLM-Server install-service`
  (admin rights); later an MSI.
- Needs the NuGet packages `Microsoft.Extensions.Hosting.WindowsServices` and `…Systemd` (download only with consent).

## Open questions

- Gateway on Windows Server: StykkerUI there too, or admin only through the browser? (Proposal: browser only.)
- License check for business (key file) or trust only? (Proposal: trust first, BUSL text.)
- Quotas: hard stop or soft warning first?
