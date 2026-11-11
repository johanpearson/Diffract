# Diffract brief

## What it is

A modern Git desktop client for Windows, built by and for Johan to replace Git Extensions. It treats worktrees as a first-class concept, finds and safely cleans up what AI agents leave behind, and makes merges and rebases understandable.

The full visual plan, with a clickable prototype of every main view, is in `visual-plan.html`.

## Goals for v1

- A modern, fast Windows app you enjoy looking at all day.
- Worktrees as a first-class concept: every worktree, its state and who is using it.
- Find and safely clean up agent leftovers from Claude Code and GitHub Copilot.
- A full daily driver: commit, merge, interactive rebase and conflict resolution.
- Repo setup made visible: line endings, default branch, identity, signing, and where each value comes from.
- Never lose work. Every destructive action has a preview and an undo path.
- Open a terminal in any worktree, and keep git itself up to date from the app.

## Not in v1

- macOS and Linux.
- Hosting integration (PRs, issues, reviews).
- A plugin system.
- Reimplementing git. Diffract only drives the git CLI.

## Locked decisions

| Topic | Decision |
|---|---|
| Platform | Windows only |
| Desktop shell | Tauri with WebView2 |
| UI | React and TypeScript, Vite, Monaco for diffs |
| Git layer | .NET 10 sidecar driving the git CLI (2.40+) |
| Sidecar build | Native AOT, single win-x64 binary |
| Shell to sidecar | JSON-RPC over stdio, no open ports |
| History graph | Canvas 2D with virtualized rows |
| Agents supported | Claude Code, GitHub Copilot |
| Unmerged work on cleanup | Keep a backup tag under `archive/`, then remove |
| Old backup tags | Shown as leftovers after 30 days (configurable) |
| Conflict layout | Both sides next to each other, result below |
| Conflict labels | "On main" and "Your commit", never ours and theirs |
| Repo settings in v1 | Line endings, default branch, identity, commit signing, pull strategy, prune on fetch, .gitignore templates |
| Code signing | Unsigned for now |
| Start dogfooding | After changes and commit (done) |

## Architecture

```
Tauri app (WebView2)
├─ React UI          views: history, worktrees, changes, rebase, conflicts, settings
└─ Rust core (thin)  starts and supervises the sidecar, relays commands and events,
                     dialogs, menus, updater
        │
        │  JSON-RPC over stdio
        ▼
.NET 10 sidecar (Native AOT)
├─ GitRunner         process calls, -z porcelain parsers
├─ RepoWatcher       watches .git and worktrees, debounced refresh
├─ LeftoverRules     classifies leftovers, patterns from diffract.json
├─ Operations        merge, rebase and cherry-pick sessions
└─ OperationLog      previews, backup tags, undo
        │
        ▼
git CLI on the repository and all its worktrees
```

Key ideas:

- **Snapshot, not live objects.** One immutable `RepoSnapshot` per refresh. The UI diffs snapshots to show what changed.
- **Rules are pure functions** over a snapshot and config. Easy to test.
- **Plan, then apply.** Destructive work produces a plan first. The confirm dialog shows the plan, so the dialog and the real operation can never disagree.
- **Agent attribution is a hint**, derived from config patterns. It is never used to decide what to delete.

## Repo layout

```
diffract/
├─ src-tauri/                     Rust core
├─ ui/                            React app
├─ sidecar/
│  ├─ Diffract.Sidecar/           JSON-RPC host
│  ├─ Diffract.Git/               GitRunner, parsers, snapshot builder, changes and cleanup runners
│  ├─ Diffract.Leftovers/         rules, config and cleanup planning
│  └─ Diffract.Operations/        merge and rebase sessions (planned)
├─ tools/
│  └─ Diffract.Spike/             console app that classifies leftovers in any repo
├─ tests/
│  ├─ Diffract.Fixtures/          shared fixture repo builder
│  ├─ Diffract.*.Tests/
│  └─ fixtures/make-repos.ps1     thin wrapper that creates fixtures for manual runs
└─ diffract.json                  default config, embedded in Diffract.Leftovers
```

## The plan

The plan is in GitHub: [milestones](https://github.com/johanpearson/Diffract/milestones) group the work and every piece is an [issue](https://github.com/johanpearson/Diffract/issues). Agents work from an issue, as `AGENTS.md` says. This file only keeps what does not change from issue to issue: the goals and the locked decisions.

Work may revisit how earlier views feel and perform, as new views change what matters most. Such a change is proposed in its issue or PR, like any other change to the plan.
