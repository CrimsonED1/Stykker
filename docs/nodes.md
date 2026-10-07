# Nodes – several PCs paired with one hub

Every installation is a **node** (a server). One of them can act as **hub**: it shows and controls the others.
The window, the browser, the phone and coding tools talk only to the hub. Every node stays fully usable on its own; if the
hub is gone, the nodes keep running.

**Note:** this design is being replaced by [plan-hosts-gateway.md](plan-hosts-gateway.md) (one server, model hosts that dial in).

**Status:** pairing (both directions, six digits + QR), node list with live state, search on the LAN (UDP 17501),
actions on nodes, distributed model tests with result sync, proxy across nodes and the per-node model comparison are
implemented. Not yet tested with a real second PC. Open: node registers itself at the hub (NAT), HTTPS with a pinned
fingerprint, Wake-on-LAN.

```
Window   Browser   Phone   Coding tools (proxy :17500)
     \      |        |      /
          Hub (main PC)           fleet, queue, results, cluster proxy
      /        |         \        live state + actions, one token per node
 Node A     Node B     Node C     normal installations with Home/VPN on
```

## Why a hub and not "everyone equal" (mesh)

- One place for results, queue and proxy: no syncing or conflict logic.
- Builds directly on what exists: `/api/state`, `/api/stream` (SSE), `/api/action`, pairing with a code, roles.
- Every node can become a hub; there is no separate program.

## Pairing

1. The node shows its code and QR as usual (☰ → Phone access in the window or web UI, key `c` in `stykker`). Home/VPN must
   be on. A node PC without a screen keeps its server running with **Settings → Keep the server running**.
2. On the hub, **Search network** sends a UDP broadcast (port 17501, LAN only); nodes answer with name, port, version and GPU.
   Nothing found (VPN, other network): enter the address by hand.
3. The hub sends the code; the node answers with its own **node token** (256 bit, stored on the node only as a hash) with
   the role `hub`. The other direction works too: the hub shows a code and the node's owner approves it.
4. The hub stores address, token and name in `nodes.dat` (data folder, bound to the Windows user) and follows the node's
   `/api/stream`. Its requests (header `X-Stykker-Device`) also keep the node's server alive.
5. The node lists the hub under its devices, with **Remove** (revokes the token). A hub may control servers, tests and the
   proxy of a node, but never its access (code, devices, Home/VPN) and cannot create bug reports there.

Security: like the phone, LAN/VPN only and no HTTPS yet. The token crosses the home network in clear text – the same
assumption as the device cookie. Possible later: a self-signed certificate whose fingerprint comes along with the pairing
(trust on first use, then pinned).

## What the fleet can do (packages)

| Package | Content | Benefit |
|---|---|---|
| N1 | Pairing, search, node list, online/offline, remove; `nodes.dat`; role `hub` in the AccessGate | foundation |
| N2 | Page **Nodes** (cards: GPU, VRAM, running models, t/s, errors); actions (start, stop, free VRAM) go to the chosen node | everything at a glance, remote control |
| N3 | Distributed tests: the eval queue hands jobs to nodes that have the model and free VRAM; results land centrally (column Machine) | one ranking across all PCs, more speed |
| N4 | Cluster proxy: `:17500` on the hub routes by model name to the node that runs it (`node/model`) | coding tools see one endpoint for all models |
| N5 | Compare benchmarks and model lists per node (which GGUF where, who is faster) | hardware comparison |
| N6 (later) | Node registers itself at the hub (machines behind NAT), HTTPS with fingerprint, Wake-on-LAN | comfort |

Text in Strings, logic in Core (`NodeRegistry`, `NodeDiscovery`, `NodeScheduler`, `NodeStateJson`), tests for pairing, token
revocation, distributing the queue and proxy routing (with simulated nodes, no real network).
