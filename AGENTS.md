# AGENTS.md – working in the Stykker monorepo

This file is for coding agents (Claude Code, Qwen Code, Codex, …) and for people who want the short version.
Read it before changing anything. `CLAUDE.md` and `QWEN.md` only point here.

## Where the information is

**Look in the folder above this repository for a folder named `Docs` and read what is there first.**
From the repository root that is `../Docs`. It holds the notes this repository does not carry: which models run
where, how the local models are wired up, the hardware. It is not part of the repository, so on another machine it
may be missing; say so then instead of guessing.

Work orders and plans written for an agent go into that `Docs` folder too, not into this repository.

Inside the repository:

| You want to know … | Read |
|---|---|
| what the tools are and how to build them | [`README.md`](README.md) |
| where the suite is heading (StykkerNode, the hub, shared pairing) and what is built | [`docs/plan-stykkernode.md`](docs/plan-stykkernode.md) |
| what comes next, in order, and which decisions are open | [`docs/naechste-schritte.md`](docs/naechste-schritte.md) |
| how one tool works and its own rules | that tool's folder: `llm/AGENTS.md`, `cmd/PLAN.md`, the `README.md` of `hud/`, `sys/`, `shared/` |
| what the hub should look like | [`docs/design/mockup-zentrale.html`](docs/design/mockup-zentrale.html) |

## Branches

- **Work on `main` is fine.** There is no `dev` branch to go through. Use a short-lived branch when a change is
  large or risky, and merge it back.
- Releases are tags per tool (`llm-v…`, `hud-v…`, `sys-v…`, `cmd-v…`). Create tags and releases only when the owner
  asks for them.

## Rules for the whole repository

1. **The repository is public.** Nothing personal goes in: no machine names, no network addresses, no personal
   paths, no access codes.
2. **No screenshots of StykkerSYS.** They show the processes running on the machine. That includes SYS shown
   inside the hub. `sys/docs/` stays out of the repository.
3. **Stage file by file.** Read `git status -s` first and name what goes in; never add a whole tool folder.
4. **No new dependencies** (NuGet, npm) without asking the owner.
5. **Build and test before a commit.** Each tool has its own solution, listed in [`README.md`](README.md). A build
   ends with 0 warnings.
6. **Shared code** goes to `shared/` only when two tools need it.
7. Code comments may be German or English. Text in the user interface is English.

A tool's own `AGENTS.md` adds to these rules for its folder.
