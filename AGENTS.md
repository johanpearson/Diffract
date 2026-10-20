# AGENTS.md

Rules for any coding agent (Claude Code, GitHub Copilot) working in this repo. Johan is the only human on the project. Keep him in the loop through PRs, not long chat explanations.

## Read first

1. `docs/brief.md` for what Diffract is and the decisions that are already made.
2. The current milestone, linked from the "Status" section of `README.md`.
3. `docs/visual-plan.html` if you need to see the intended UI. It is a static mock, not code to reuse.

Decisions in the brief are locked. If you think one is wrong, say so in the PR description. Do not quietly work around it.

## Safety rules (non-negotiable)

- **Never run a git command that writes against a repo you did not create yourself.** Real repos on Johan's machine are read-only for you. Writing is only allowed inside fixture repos created in a temp directory by the test suite or the fixture command.
- Never delete, prune, reset, checkout, stash, tag, push or fetch in a real repo. Not even "just to test".
- In milestone 1, `GitRunner` must enforce this: when created in read-only mode it rejects any subcommand that is not on an explicit allowlist, and a test proves it.
- If a task seems to require writing to a real repo, stop and ask Johan.

## How Diffract talks to git

- Use the git CLI only. No LibGit2Sharp or other git libraries.
- Minimum git version is 2.40. Check it at startup and fail with a clear message if it is older.
- Always use machine-readable output: `--porcelain=v2`, `--porcelain`, `-z`, or `--format` with explicit separators. Never parse output meant for humans.
- Pass arguments with `ProcessStartInfo.ArgumentList`, never by building a command string.
- Set these environment variables on every git process:
  - `GIT_OPTIONAL_LOCKS=0` so `git status` does not take index locks while agents are working in the same repo
  - `GIT_TERMINAL_PROMPT=0` so git never waits for input
- Treat stderr and exit codes as data. A non-zero exit becomes a typed exception that includes the command, exit code and stderr.
- Support cancellation and a timeout on every call.

## Code conventions

- .NET 10, latest C#, nullable enabled, warnings as errors (see `Directory.Build.props`).
- File-scoped namespaces. Records for data. Small classes with one job.
- All library projects must be Native AOT compatible: `<IsAotCompatible>true</IsAotCompatible>`, no reflection-based serialization. Use System.Text.Json source generation.
- Use `TimeProvider` for anything time-based so tests can control the clock.
- No new NuGet, npm or cargo packages without a line in the PR description saying why. Pre-approved:
  - NuGet: `xunit.v3`, `Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`, `Microsoft.Extensions.TimeProvider.Testing`.
  - npm: `react`, `react-dom`, `@types/react`, `@types/react-dom`, `vite`, `@vitejs/plugin-react`, `typescript`, `@tauri-apps/api`, `@tauri-apps/cli`, `@tauri-apps/plugin-dialog`, `vitest`, `jsdom`, `@testing-library/react`, `@testing-library/dom`, `@playwright/test`.
  - cargo: `tauri`, `tauri-build`, `tauri-plugin-dialog`, `tauri-plugin-shell`, `serde`, `serde_json`.
- Commit lockfiles (`package-lock.json`, `Cargo.lock`) and install with `npm ci` in CI.
- Line endings are LF in the repo (`.gitattributes`). Do not fight it.

## Testing rules

Johan is a QA automation engineer. Tests are not optional and not an afterthought.

- Every parser gets tests against captured real git output stored as sample files, including nasty cases (spaces and non-ASCII in paths, detached HEAD, locked and prunable worktrees, bare repos).
- Every leftover rule gets at least one test against a real fixture repo created by the fixture builder, plus a test proving it does not fire on a clean repo.
- Tests isolate git from the developer's own config: set `GIT_CONFIG_GLOBAL` to an empty temp file, `GIT_CONFIG_NOSYSTEM=1`, and set author name, email and dates explicitly so results are deterministic.
- Tests must run in parallel safely. Each test gets its own temp directory and cleans it up.
- `dotnet test` must pass on `windows-latest` in CI before a PR is ready.

## Workflow

- `main` is protected: no direct pushes, no force pushes, linear history, and the CI job `build` must pass before anything merges. Never try to work around this, and never rename the `build` job.
- One branch per task, named like `m1/parsers` or `m1/fixtures`.
- Johan merges with either squash or rebase. With rebase, your commits land on main as they are, so every commit must build, pass tests and have a meaningful conventional message (`feat:`, `fix:`, `test:`, `docs:`, `chore:`). No "wip" or "fix typo" commits; squash them locally before opening the PR.
- Do not merge your own PRs.
- When the milestone doc says "checkpoint", stop and wait for Johan.
- When Johan signs off a milestone, the last PR sets its status line to `done` and points the README "Status" section at the next milestone doc, if one exists. If it doesn't, say so under "Needs your decision".

## Pull requests

Johan does not review code. He reads the PR description, checks that CI is green and merges. That makes two things matter more than usual:

1. **Tests are the review.** If something isn't covered by a test, assume nobody will ever look at it.
2. **The PR description is the explanation.** It must make sense to someone who never opens the diff.

Use `.github/pull_request_template.md` and follow these rules:

- The visible part (TL;DR, Try it, How I know it works, Needs your decision) stays under about 150 words.
- Write for a person, not a reviewer. Say what is different and why it matters. No file lists, class names or code in the visible part unless a command is needed under "Try it".
- Technical detail goes in the collapsed "Technical notes" section, or nowhere.
- If something needs Johan's decision, it goes under "Needs your decision" and nowhere else, so he can't miss it.
- Remove all template comments. Tick a checklist item only if it is true; if not, leave it unticked and say why in Technical notes.

## Writing style for docs and PRs

Plain, direct English. Short sentences. No hype. Avoid em-dashes; use commas, colons or full stops instead.
