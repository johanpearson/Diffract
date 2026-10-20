# Milestone 1: snapshot and rules spike

**Status:** in progress
**Estimate:** about 1 week of evenings.
**Done when:** the spike correctly classifies leftovers in Johan's real repos, and every rule is covered by fixture tests.

No UI, no Tauri, no JSON-RPC yet. This milestone builds the two libraries everything else stands on, a console app to exercise them, and the test harness.

Everything in this milestone is **read-only against real repos**. See the safety rules in `AGENTS.md`.

## Deliverables

```
Diffract.slnx
sidecar/Diffract.Git/            class library
sidecar/Diffract.Leftovers/      class library, embeds diffract.json as defaults
tools/Diffract.Spike/            console app
tests/Diffract.Fixtures/         class library: FixtureRepoBuilder and scenarios
tests/Diffract.Git.Tests/        xUnit v3
tests/Diffract.Leftovers.Tests/  xUnit v3
tests/fixtures/make-repos.ps1    wrapper around `Diffract.Spike fixtures`
.github/workflows/ci.yml         already exists, extend it
docs/findings/agent-leftovers.md what Claude Code and Copilot really leave behind
```

## Phase 0: discovery (read-only)

Before writing rules, find out what the agents actually leave behind.

1. Ask Johan for 1 to 3 local repo paths where Claude Code and Copilot have been used.
2. Run only these read-only commands in each:
   - `git --version`
   - `git worktree list --porcelain`
   - `git for-each-ref --format="%(refname)%09%(objectname:short)%09%(committerdate:iso-strict)"`
   - `git stash list`
   - `git branch -vv --all`
   - `git log --oneline -n 30 --all`
   - `git reflog -n 50`
   - `git fsck --unreachable --no-reflogs` (may be slow on big repos, skip if it takes over a minute)
   - list the contents of `.claude/` if it exists, without reading file contents
3. Write `docs/findings/agent-leftovers.md`: branch names, worktree paths, stash messages and refs you could attribute to each agent, with counts. No secrets or file contents.
4. Propose updates to `diffract.json` patterns based on the findings.

**Checkpoint:** stop and wait for Johan to review the findings and pattern changes.

## Phase 1: solution and CI

- `Diffract.slnx` with the projects above. Use `Directory.Build.props` at the root.
- Library projects set `<IsAotCompatible>true</IsAotCompatible>`.
- `.github/workflows/ci.yml` already exists. It passes when there is no solution and builds and tests `Diffract.slnx` once it appears, so it should work without changes. Adjust it only if needed.
- The job id must stay `build`. Main's ruleset requires a passing check with exactly that name. Renaming it blocks every PR.


## Phase 2: Diffract.Git

### GitRunner

- `GitRunner(string workingDirectory, GitRunnerOptions options)`.
- `Task<GitResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)` returning stdout as bytes and as text, stderr, exit code and duration.
- Environment on every call: `GIT_OPTIONAL_LOCKS=0`, `GIT_TERMINAL_PROMPT=0`, plus any extra variables from options (tests use this to isolate config).
- Default timeout 30 seconds, configurable.
- Non-zero exit throws `GitCommandException` with command, exit code and stderr. Some commands treat non-zero as an answer (for example `merge-base --is-ancestor`); expose a way to allow that.
- **Read-only mode** (`options.ReadOnly = true`, the default): only these subcommands are allowed: `version`, `rev-parse`, `worktree list`, `status`, `for-each-ref`, `stash list`, `rev-list`, `merge-base`, `log`, `reflog` (show only), `fsck`, `config --get`, `config --list`, `symbolic-ref` (read only), `cat-file`, `show-ref`. Anything else throws `ReadOnlyViolationException` before a process is started. Fixture creation uses a runner with `ReadOnly = false`.
- `GitVersion` check: fail clearly below 2.40.

### Parsers

One parser per output format, each a pure function from bytes or text to records. No process calls inside parsers.

| Parser | Command |
|---|---|
| `WorktreeListParser` | `git worktree list --porcelain -z` |
| `StatusParser` | `git status --porcelain=v2 --branch -z` |
| `RefListParser` | `git for-each-ref` with a `%00`-separated `--format` |
| `StashListParser` | `git stash list -z --format=...` |
| `UnreachableParser` | `git fsck --unreachable --no-reflogs` |

Sample-based tests: capture real output into `tests/Diffract.Git.Tests/Samples/` and test against it. Cover at least: main worktree, linked worktree, detached HEAD, locked (with and without reason), prunable, bare repo, paths with spaces, paths with å ä ö, very long paths, renamed and untracked files in status, branch with no upstream.

### RepoSnapshotBuilder

Builds a `RepoSnapshot` for a repo path:

```csharp
public sealed record RepoSnapshot(
    string RepoRoot,
    string MainBranch,
    GitVersion GitVersion,
    IReadOnlyList<WorktreeInfo> Worktrees,
    IReadOnlyList<BranchInfo> Branches,       // local and remote-tracking
    IReadOnlyList<TagInfo> Tags,
    IReadOnlyList<StashInfo> Stashes,
    IReadOnlyList<RefInfo> OtherRefs,         // outside heads, tags, remotes, stash
    IReadOnlyList<string> UnreachableCommits,
    DateTimeOffset TakenAt);
```

- Main branch when config says `auto`: `origin/HEAD` if set, else `init.defaultBranch` if that branch exists, else `main`, else `master`.
- For each worktree: status (dirty file count, ahead and behind main), last commit time, and last activity time. Last activity is the newest of: last commit time, modification time of changed files from status, and modification time of the worktree's index file. Do not walk the whole worktree.
- For each branch and tag that rules care about: whether it is merged into main, and **commits at risk**, meaning commits reachable from it and from no other branch, remote-tracking branch or tag. Exclude the ref itself when counting, and do not count reachability through worktree HEADs or reflogs. Prove this with fixture tests.
- Run independent git calls in parallel where it helps. Report timing per call when the spike runs with `--timing`.

## Phase 3: fixtures

`tests/Diffract.Fixtures` contains `FixtureRepoBuilder`, which creates repos in a temp directory with a writable `GitRunner`, isolated config and fixed dates. Each scenario is a named method, reused by tests and by `Diffract.Spike fixtures <dir>`.

| Scenario | What it creates | Expected verdict |
|---|---|---|
| `Clean` | Repo with commits on main only | No leftovers |
| `MergedWorktree` | Worktree on a branch merged into main, clean | Safe |
| `PrunableWorktree` | Worktree whose folder was deleted | Safe |
| `StaleAgentWorktree` | `claude/...` worktree, 2 unmerged commits, last activity 9 days ago | Review, 2 at risk |
| `ActiveAgentWorktree` | `claude/...` worktree with a file changed 1 minute ago | In use |
| `LockedWorktree` | Worktree locked with a reason | In use |
| `UserDirtyWorktree` | Non-agent branch worktree with uncommitted changes | In use |
| `AgentBranchMerged` | `copilot/...` branch, no worktree, merged | Safe |
| `AgentBranchUnmerged` | `copilot/...` branch, no worktree, 3 unmerged commits | Review, 3 at risk |
| `RemoteAgentBranch` | Local bare "origin" with a merged `copilot/...` branch, fetched | Safe |
| `ToolStash` | Stash with message containing "autostash" | Review |
| `CheckpointRefReachable` | Ref under `refs/checkpoints/` pointing into a branch | Safe |
| `CheckpointRefUnreachable` | Same, pointing at a commit on no branch | Review |
| `OrphanedCommit` | Commit made then reset away | Review |
| `OldArchiveTag` | `archive/...` tag 45 days old with unique commits | Review |
| `RecentArchiveTag` | `archive/...` tag 5 days old | No leftover |
| `AwkwardPaths` | Worktrees in folders with spaces, å ä ö, and a 200+ character path | Parsers and rules still work |
| `DetachedWorktree` | Worktree with detached HEAD | In use if dirty, otherwise Review |
| `MasterAsMain` | Repo whose main branch is `master` | Rules use `master` as main |

`tests/fixtures/make-repos.ps1` just runs `dotnet run --project tools/Diffract.Spike -- fixtures <dir>` so Johan can inspect the repos by hand.

## Phase 4: Diffract.Leftovers

```csharp
public enum Verdict { SafeToRemove, NeedsReview, InUse }

public enum LeftoverKind
{
    Worktree, PrunableWorktree, LocalBranch, RemoteBranch,
    Stash, CheckpointRef, OrphanedCommit, BackupTag
}

public sealed record Leftover(
    LeftoverKind Kind,
    string Name,
    string? Path,
    string? Agent,          // from config patterns, a hint only
    Verdict Verdict,
    int CommitsAtRisk,
    string Reason);         // one plain-English sentence

public interface ILeftoverRule
{
    IEnumerable<Leftover> Evaluate(RepoSnapshot repo, DiffractConfig config, TimeProvider time);
}
```

- One rule per row in the fixture table's verdict logic. Rules are pure: no git calls, no file system.
- `LeftoverClassifier` runs all rules and resolves overlaps. An item reported by more than one rule appears once, with the most cautious verdict (`InUse` over `NeedsReview` over `SafeToRemove`).
- The main worktree is never a leftover. The worktree Diffract runs from is never a leftover.
- `DiffractConfig` is loaded with System.Text.Json source generation. Defaults come from the embedded `diffract.json`. The spike accepts `--config <path>` to override.
- Glob matching for branch and path patterns: write a small matcher (`*` within a segment, `**` across segments, case-insensitive on Windows paths) with its own tests. No package for this.

Every rule has a fixture test for when it fires and a test on `Clean` proving it does not.

## Phase 5: Diffract.Spike

```
diffract-spike scan <repo-path> [--json] [--timing] [--config <path>]
diffract-spike fixtures <output-dir> [--scenario <name>]
```

`scan` output, sorted by verdict then kind:

```
Diffract spike  C:\src\checkout
main: main   git: 2.47.1   snapshot: 412 ms

Verdict        Kind          Name                         Agent           At risk  Reason
Safe           Worktree      copilot/update-deps          GitHub Copilot  0        Branch merged into main, worktree is clean
Safe           Prunable      claude/spike-cache           Claude Code     0        Folder was deleted outside git
Needs review   Worktree      claude/refactor-cart         Claude Code     2        No activity for 9 days, 2 commits exist nowhere else
In use         Worktree      claude/fix-flaky-login       Claude Code     0        Files changed 2 minutes ago

4 leftovers: 2 safe, 1 needs review, 1 in use
```

- `--json` writes the full snapshot summary and leftovers as JSON (source-generated serializer).
- Exit codes: 0 success, 1 error, 2 path is not a git repo, 3 git too old.
- Never writes to the scanned repo. The read-only `GitRunner` guarantees it.

## Phase 6: real-world check

1. Run `scan` against the repos from phase 0.
2. Compare with what Johan expects. Every item that is wrong becomes a new fixture scenario and a fix.
3. Update `docs/findings/agent-leftovers.md` with the final pattern set.

**Checkpoint:** Johan runs the spike on his own repos and signs off.

## Acceptance criteria

- [ ] `dotnet test` passes locally and in CI on `windows-latest`.
- [ ] Every fixture scenario has a test asserting the exact leftovers and verdicts.
- [ ] A test proves read-only mode rejects a write command without starting a process.
- [ ] Tests do not depend on the developer's global git config.
- [ ] `diffract-spike scan` on Johan's real repos matches his expectations.
- [ ] `diffract-spike scan` takes under 1 second on a typical repo, excluding `fsck`. Report `fsck` timing separately.
- [ ] Library projects build with AOT analyzers enabled and no warnings.
- [ ] `docs/findings/agent-leftovers.md` exists and `diffract.json` reflects it.

## Out of scope for milestone 1

Cleanup actions, backup tag creation, file watching, JSON-RPC, Tauri, any UI.
