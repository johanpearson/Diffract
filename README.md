# Diffract

A modern Git desktop client for Windows that understands worktrees, cleans up after AI agents and makes rebases less scary.

Personal project. Windows only.

## Status

Current milestone: [docs/milestones/m1-spike.md](docs/milestones/m1-spike.md), the spike for the repo snapshot and agent leftover rules. No UI yet.

## Docs

- [docs/brief.md](docs/brief.md): what Diffract is, locked decisions, architecture
- [docs/milestones/m1-spike.md](docs/milestones/m1-spike.md): current milestone, scope and acceptance criteria
- [docs/findings/agent-leftovers.md](docs/findings/agent-leftovers.md): what Claude Code and Copilot really leave behind in a repo
- [docs/visual-plan.html](docs/visual-plan.html): the visual plan with a clickable prototype (open locally in a browser)
- [AGENTS.md](AGENTS.md): rules for coding agents working in this repo
- [docs/START-PROMPT.md](docs/START-PROMPT.md): the prompt that starts the agent on the current milestone
- [docs/github.md](docs/github.md): merging PRs, what protects main, and how to get unstuck

## Stack

Tauri (WebView2) with a React and TypeScript UI, and a .NET 10 sidecar built with Native AOT that drives the git CLI. The sidecar talks to the Tauri core over stdio JSON-RPC.
