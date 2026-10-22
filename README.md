# Diffract

A modern Git desktop client for Windows that understands worktrees, cleans up after AI agents and makes rebases less scary.

Personal project. Windows only.

## Status

Current milestone: [docs/milestones/m4-changes.md](docs/milestones/m4-changes.md), changes and commit. Milestone 3, [the history graph](docs/milestones/m3-history.md), is done. Milestone 2, [the Tauri shell, sidecar and live worktree view](docs/milestones/m2-shell.md), is done. Safe cleanup moved to milestone 5 and is planned in [docs/milestones/m5-cleanup.md](docs/milestones/m5-cleanup.md). Milestone 1, [the spike for the repo snapshot and agent leftover rules](docs/milestones/m1-spike.md), is done.

## Docs

- [docs/brief.md](docs/brief.md): what Diffract is, locked decisions, architecture
- [docs/milestones/m4-changes.md](docs/milestones/m4-changes.md): current milestone, scope and acceptance criteria
- [docs/milestones/m3-history.md](docs/milestones/m3-history.md): milestone 3 (done), scope and acceptance criteria
- [docs/milestones/m5-cleanup.md](docs/milestones/m5-cleanup.md): milestone 5, safe cleanup, planned
- [docs/milestones/m2-shell.md](docs/milestones/m2-shell.md): milestone 2 (done), scope and acceptance criteria
- [docs/milestones/m1-spike.md](docs/milestones/m1-spike.md): milestone 1 (done), scope and acceptance criteria
- [docs/findings/agent-leftovers.md](docs/findings/agent-leftovers.md): what Claude Code and Copilot really leave behind in a repo
- [docs/findings/winget.md](docs/findings/winget.md): how Diffract asks winget about Git for Windows
- [docs/visual-plan.html](docs/visual-plan.html): the visual plan with a clickable prototype (open locally in a browser)
- [docs/setup.md](docs/setup.md): what to install to build Diffract on Windows
- [AGENTS.md](AGENTS.md): rules for coding agents working in this repo
- [docs/START-PROMPT.md](docs/START-PROMPT.md): the prompt that starts the agent on the current milestone
- [docs/github.md](docs/github.md): merging PRs, what protects main, and how to get unstuck

## Stack

Tauri (WebView2) with a React and TypeScript UI, and a .NET 10 sidecar built with Native AOT that drives the git CLI. The sidecar talks to the Tauri core over stdio JSON-RPC.
