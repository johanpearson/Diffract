# Milestone 2: Tauri shell, sidecar and live worktree view

**Status:** in progress
**Estimate:** 2 to 3 weeks of evenings.
**Done when:** an installed build runs on Johan's machine, shows the worktrees and leftovers of a repo he opens, and updates live while an agent works in it.

This is the first milestone with a UI. It wraps the milestone 1 libraries in a sidecar process, puts a Tauri app in front of it, and keeps the view current while files and refs change.

Everything in this milestone is still **read-only against real repos**. Cleanup actions come in milestone 3. The only things Diffract starts that are not read-only git calls are a terminal (when you ask for one) and a Git for Windows update through winget (only after you confirm it). See the safety rules in `AGENTS.md`.

## What milestone 1 taught us

These findings shape this milestone. Details are in `docs/findings/agent-leftovers.md` and the milestone 1 doc.

- **Git calls are expensive on Johan's laptop.** Starting any git process takes 170 to 360 ms, probably because of antivirus. A full snapshot of his busiest repo takes 3 to 6 seconds, and `fsck` alone takes about 5. A live view cannot rebuild the whole snapshot on every change. It must refresh only the part that changed.
- **fsmonitor is on in Johan's repos.** `GitRunner` already turns it off for its own calls, so a scan never starts a daemon. The watcher in this milestone must not rely on it either.
- **Agents change repos all the time.** Copilot alone wrote 163 checkpoint refs in two weeks. The watcher must handle bursts of changes without flooding git.
- **Native AOT needs the C++ build tools.** Publishing the spike failed on Johan's machine because they are missing. Tauri needs them too.

## Deliverables

```
sidecar/Diffract.Sidecar/        console app, Native AOT: JSON-RPC host over stdio
sidecar/Diffract.Git/            extended: RepoWatcher, partial refresh
src-tauri/                       Rust core: starts and supervises the sidecar, relays messages
ui/                              React, TypeScript and Vite: the Worktrees and agents view
tests/Diffract.Sidecar.Tests/    xUnit v3: protocol, methods, watcher
ui/src/**/*.test.ts(x)           Vitest: components and protocol client
ui/e2e/                          Playwright: the view against a fake sidecar
docs/setup.md                    toolchain for building Diffract on Windows
.github/workflows/ci.yml         extended: UI, Rust and the installer, still one job named build
```

## Phase 0: toolchain and decisions

1. Write `docs/setup.md`: what to install to build Diffract on Windows. That is the .NET 10 SDK, Node LTS, Rust (`rustup`, `x86_64-pc-windows-msvc`), and the Visual Studio Build Tools with the "Desktop development with C++" workload. WebView2 ships with Windows 11. Give one `winget` command per tool where one exists.
2. Check Johan's machine read-only and list what is missing. On 2026-10-20 Rust and the C++ build tools were missing.
3. Propose the npm and cargo packages this milestone needs, each with one line on why (see "Dependencies" below).

**Checkpoint:** Johan installs the missing tools and approves the package list.

Found on 2026-10-20: on Johan's machine, .NET 10, Node 24, Git 2.47, WebView2, Windows Terminal and winget were already there. Rust was missing, and so was the C++ workload in both installed copies of Visual Studio. `tauri-plugin-shell` was added to the package list, and so were the React types and `@testing-library/dom`.

## Phase 1: walking skeleton and installer

The smallest app that proves the whole chain works, built and packaged in CI from the start.

- `ui/`: Vite, React and TypeScript, one page that shows "Diffract", the sidecar version and the git version.
- `src-tauri/`: Tauri 2 app that bundles the sidecar as an external binary, starts it, and relays one request and its response.
- `sidecar/Diffract.Sidecar/`: answers `app.info` with its own version and the git version, using `GitVersion.RequireSupportedAsync`.
- The installer is NSIS, per user, with no admin rights needed. It is unsigned, as the brief says.
- CI: the `build` job also installs Node and Rust, runs the UI and Rust tests, publishes the sidecar with Native AOT, and builds the installer. The installer is uploaded as a workflow artifact. Cache npm, cargo and NuGet so the job stays under about 10 minutes. The job id stays `build`.

**Checkpoint:** Johan downloads the installer from a CI run, installs it, and sees both versions on screen.

## Phase 2: the sidecar protocol

JSON-RPC 2.0 over the sidecar's stdin and stdout.

- **Framing:** one JSON message per line, UTF-8, `\n` terminated. JSON escapes newlines inside strings, so a line is always one whole message. The sidecar writes only protocol messages to stdout. Logs go to stderr, and the Rust core writes them to a log file.
- **Serialization:** System.Text.Json source generation, camelCase, enums as strings, as in the spike's `--json`.
- **Methods:**

| Method | Params | Result |
|---|---|---|
| `app.info` | none | sidecar version, git version, whether it is supported |
| `repo.open` | `path` | `repoId`, snapshot summary, leftovers. Starts watching. |
| `repo.refresh` | `repoId`, `includeUnreachable` | the same as `repo.open`. A full rebuild, with `fsck` only when asked. |
| `repo.close` | `repoId` | stops watching |
| `system.openTerminal` | `path` | none (phase 5) |
| `git.checkUpdate` | none | installed and available Git for Windows versions (phase 6) |
| `git.update` | none | starts the winget update (phase 6) |
| `app.shutdown` | none | none, then the sidecar exits |

- **Notifications from the sidecar:** `repo.changed` with `repoId`, what changed, the new snapshot summary and the new leftovers. The UI replaces its state with what it receives and animates the difference.
- **Errors:** standard JSON-RPC error objects. Diffract's own errors get codes and a `data.kind` that matches the exception: not a repo, git too old, git failed, timed out, config invalid. The message is the plain-English one the spike already prints.
- **Requests run concurrently**, and each has its own cancellation. `$/cancelRequest` with the request id cancels it, as in LSP.
- The sidecar only ever builds read-only `GitRunner`s. A test proves no code path in `Diffract.Sidecar` creates a writable one.

Tests: protocol tests run the host in process over in-memory streams. Contract tests write each response and notification shape to `tests/Diffract.Sidecar.Tests/Contract/*.json`. The UI's tests read the same files, so the C# and TypeScript sides cannot drift apart unnoticed.

## Phase 3: RepoWatcher and partial refresh

Keep the snapshot current without rebuilding it.

**What to watch:**
- The common git directory: `HEAD`, `packed-refs`, `refs/**`, and `worktrees/*/{HEAD,index,locked}`.
- Each worktree folder, recursively. Git's own ignore rules decide what is a change, through `git status`. Diffract does not reimplement them.
- Worktree container folders, not recursively, so leftover folders show up and disappear.

**What a change refreshes:**

| Change | Refresh | Git calls |
|---|---|---|
| Files in one worktree | that worktree's status and last activity | 1 |
| That worktree's `HEAD` or `index` | that worktree's status, ahead and behind | 2 |
| `refs/**` or `packed-refs` | refs, merged, commits at risk, all worktrees' ahead and behind | as in a full snapshot, without `fsck` |
| `worktrees/` added or removed, or a container folder | worktree list, then as above for new worktrees | |
| Watcher buffer overflow | full snapshot without `fsck` | |

- `fsck` never runs on a live refresh. It runs when a repo is opened and on `repo.refresh` with `includeUnreachable`. The orphan rows keep their last known value in between and say how old it is.
- **Debounce:** wait until a source has been quiet for 300 ms, but refresh at least every 2 seconds while changes keep coming. Use `TimeProvider`, so tests control the clock.
- At most one refresh runs per repo at a time. Changes that arrive meanwhile are merged into the next one.
- Diffract's own git calls never write (`GIT_OPTIONAL_LOCKS=0`, fsmonitor off), so they cannot trigger the watcher. A test proves a refresh does not cause another one.
- Leftovers are classified again after every refresh. Rules are pure and fast.

Tests: unit tests drive the debouncer with `FakeTimeProvider`. Fixture repo tests change a file, a ref and a worktree, and assert which refresh ran and which git calls it made. A call budget test per row of the table above, as in milestone 1.

## Phase 4: Worktrees and agents view

The read-only version of the "Worktrees and agents" view in `docs/visual-plan.html`. It is a mock to look at, not code to reuse.

- **Open a repo** with a folder dialog. The last opened repo opens again on start. One repo per window in this milestone.
- **Sidebar:** the repo's worktrees with branch, and dirty or active state. A "Leftovers" entry with the count of everything that is not "In use".
- **Main view:** three summary counts (safe, needs review, in use), filter chips, and one row per leftover. Each row shows name, kind, agent hint, reason, path when there is one, commits at risk ("-" when it cannot be counted), and the verdict.
- **Row actions:** "Open folder" for worktrees and folders that exist. "Open terminal here" arrives in phase 5. There are no cleanup buttons or checkboxes yet. Milestone 3 adds them.
- **Live:** rows that appear, change verdict or disappear are briefly highlighted. A small status line shows when the snapshot was taken, and when unreachable commits were last counted.
- **Errors** are shown in the view in plain English, with a retry button. Examples: the sidecar crashed and was restarted, the folder is not a repo, git is too old.
- Dark and light theme following Windows. Keyboard reachable.

Rust core: restarts the sidecar if it exits unexpectedly, at most 3 times a minute, and tells the UI. On window close it sends `app.shutdown` and waits 2 seconds before killing the process.

Tests: Vitest and Testing Library for components, against the contract samples. Playwright runs the built UI in a browser against a fake sidecar transport and checks: open a repo, see the rows, filter, receive a `repo.changed` and see the row update. Rust unit tests cover the line framing and the restart policy.

## Phase 5: Open terminal here

- A button on the repo and on each worktree row, and in the sidebar's context menu.
- Opens Windows Terminal (`wt.exe -d <path>`) when it is installed, otherwise PowerShell with its working directory set to the path. Never `cmd /c` with a built command string. Arguments go through `ProcessStartInfo.ArgumentList`, as with git.
- Implemented in the sidecar as `system.openTerminal`, so the Rust core stays thin.
- Tests: which program and which arguments are chosen, with a fake process starter. One test opens nothing real.

## Phase 6: git version check and update

- On start, the UI shows the git version from `app.info`. Below 2.40, the view says Diffract cannot work and offers the update.
- `git.checkUpdate` asks winget whether a newer Git for Windows (`Git.Git`) is available. If one is, a quiet notice offers it.
- `git.update` runs `winget upgrade --id Git.Git --exact` only after the user confirms in a dialog that names both versions. The UI shows progress and the result, then calls `app.info` again.
- **Start with a spike:** winget's normal output is a table meant for people. Find a machine-readable way first: `winget`'s exit codes, `winget export`, or the `Microsoft.WinGet.Client` PowerShell module. Write down which one is used and why. If none is reliable, show the versions and a link to the download page instead of updating.
- Tests: version comparison and the decision to offer an update, against captured winget output stored as sample files. No test runs winget for real.

**Checkpoint:** Johan reviews how the update check works before it is wired into the UI.

## Phase 7: real-world check

1. Johan installs the build from CI and opens his busiest repos.
2. While an agent works in one of them, the view follows: new worktrees appear, rows change from "In use" to "Needs review" over time, and removed worktrees disappear.
3. Every wrong or missing update becomes a test and a fix.

**Checkpoint:** Johan uses the installed build for a few days and signs off.

## Dependencies

Approved in phase 0, and added to the pre-approved lists in `AGENTS.md`. Versions are the current major versions on 2026-10-20. The lockfiles (`package-lock.json`, `Cargo.lock`) pin the exact versions.

| Package | Major | Why |
|---|---|---|
| `react`, `react-dom` | 19 | UI framework from the brief |
| `@types/react`, `@types/react-dom` | 19 | types for React in TypeScript |
| `vite`, `@vitejs/plugin-react` | 8, 6 | build tooling from the brief |
| `typescript` | 7 | the UI language from the brief |
| `@tauri-apps/api`, `@tauri-apps/cli` | 2 | talk to the Rust core, build the app |
| `@tauri-apps/plugin-dialog` | 2 | the folder picker, from the UI side |
| `vitest`, `jsdom` | 5, 30 | component tests |
| `@testing-library/react`, `@testing-library/dom` | 16, 10 | component tests; the second is required by the first |
| `@playwright/test` | 1 | end-to-end tests |
| `tauri`, `tauri-build` (cargo) | 2 | the shell |
| `tauri-plugin-dialog` (cargo) | 2 | the folder picker |
| `tauri-plugin-shell` (cargo) | 2 | starts and talks to the bundled sidecar, Tauri's documented way |
| `serde`, `serde_json` (cargo) | 1 | read the line framing |

No UI component library and no state library. The view is small enough without them.

## Acceptance criteria

- [ ] `dotnet test`, the UI tests, the Playwright tests and `cargo test` pass locally and in CI on `windows-latest`.
- [ ] CI builds an NSIS installer as an artifact on every run.
- [ ] Every JSON-RPC method and notification has a contract sample that both the C# and the TypeScript tests use.
- [ ] A test proves the sidecar never creates a writable `GitRunner`.
- [ ] Each kind of change in the phase 3 table refreshes only its part, and a test checks its git call budget.
- [ ] A refresh never triggers another refresh.
- [ ] On Johan's laptop, a file change in a worktree shows in the view within 2 seconds of the last write.
- [ ] Killing the sidecar process shows a message and the app recovers by itself.
- [ ] Git for Windows is only ever updated after the user confirms.
- [ ] Johan has used the installed build for a few days and signs off.

## Out of scope for milestone 2

Cleanup actions, backup tags, undo, the history graph, diffs, commits, settings, several repos in one window, auto-update of Diffract itself, and code signing.
