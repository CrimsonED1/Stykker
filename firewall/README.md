# StykkerFirewall

Application firewall for Windows. When an app without a rule tries to connect, a small window asks what to do:

| Answer | Effect |
|---|---|
| **Allow all** | Rule: this app may connect anywhere (in this direction) |
| **Allow this target only** | Rule: this app may reach this address, port and protocol (inbound: this local port) |
| **Always block** | Rule: this app is blocked |
| **Block this time** | No rule; the app is asked again after a quiet period (default 2 minutes) |

The overview lists all rules, connections waiting for an answer, and the latest blocked connections.

Status: first version. Built and tested with the simulator backend; the Windows Firewall backend is not yet tried
on a real machine.

## How it works

```
StykkerFirewall.exe (tray, user)  ──named pipe──  StykkerFirewall.Service (Windows service, SYSTEM)
  WPF windows + Blazor pages                         FirewallCoordinator ── RuleStore (rules.json)
                                                           │
                                                     IFirewallBackend
                                          ┌────────────────┴───────────────┐
                                  windows-firewall                     simulator
```

- **Core** (`StykkerFirewall.Core`): rule model, answer → rule, prompt gate (no flood of dialogs when an app
  retries), reconciler, pipe protocol, and the interface `IFirewallBackend`. No Windows code.
- **Backends** are exchangeable. The service picks one with the setting `Backend`:
  - `windows-firewall` (default): rules through the Windows Firewall COM API (group `StykkerFirewall`),
    blocked connections from the Security log (event 5157, audit "Filtering Platform Connection").
  - `simulator`: invented apps and connections, rules in memory. For trying the interface without admin rights.
  - A new backend (for example direct WFP) implements `IFirewallBackend` and adds one line to the
    `BackendCatalog` in `StykkerFirewall.Service/Program.cs`.
- `rules.json` (in `%ProgramData%\Stykker\Firewall`) is the truth. The service reconciles the backend with it at
  start and every minute, so rules deleted by hand in the Windows Firewall come back.

### Limits

- The first connection attempt of an unknown app fails. Holding a connection until the answer needs a signed kernel
  driver; this tool works without one. Most apps retry by themselves.
- If another firewall takes over, Windows Firewall is off and StykkerFirewall has no effect. The overview says so.
- Any signed-in (interactive) user can answer prompts through the pipe.

## Modes and lock-out protection

- **Monitor**: Windows default (outbound allowed). Only blocked inbound connections are shown.
- **Ask for every app**: outbound is blocked by default; every app without a rule causes a prompt.

Switching to *Ask* is a trial first: the overview shows a countdown and **switches back to Monitor after 60 seconds
unless you press "Keep ask mode"**. Until then the mode is not saved, so a restart also lands in Monitor.

If you are ever locked out, run from an administrator prompt:

```
StykkerFirewall.Service.exe -reset
```

It restores the Windows default (outbound allowed, Windows notifications on), removes all StykkerFirewall rules from
Windows Firewall and sets the saved mode to Monitor.

## Build and test

```
dotnet build firewall/StykkerFirewall.slnx -c Release
dotnet test firewall/tests/StykkerFirewall.Tests -c Release
```

## Try it without touching the real firewall

```
StykkerFirewall.Service.exe -backend simulator -data-dir <temp folder> -pipe StykkerFirewall-sim
StykkerFirewall.exe -pipe StykkerFirewall-sim -show
```

## Install as a service (real firewall)

From an administrator prompt, with the published service in a fixed folder:

```
sc.exe create StykkerFirewall binPath= "<folder>\StykkerFirewall.Service.exe" start= auto
sc.exe start StykkerFirewall
```

Then start `StykkerFirewall.exe` in the user session (tray). It starts in Monitor mode.
