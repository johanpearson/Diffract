# Reading history fast

The spike from milestone 3, phase 0: how fast git can hand Diffract a repo's history, and what slows it down. Measured on 2026-10-21 on Johan's laptop with Git for Windows 2.55.0.5. Every command was read-only, except writing a commit-graph in a generated test repo in a temp folder.

## The repos

- **Repo A**, Johan's busiest work repo, is small: 115 commits on its branches, tags and HEAD, 277 counting every ref, and 188 refs. Most refs are Copilot checkpoints. It has no commit-graph file.
- **Two of Johan's own projects** are smaller still: 83 commits with 19 tags, and 14 commits. Neither has a merge or a commit-graph file. Reading all of either takes about 0.1 to 0.2 seconds.
- **The large test repo** is generated with `git fast-import` (`LargeHistory` in the fixtures library, or `diffract-spike fixtures <folder> --large-history 50000`): 50,000 commits, 5,262 merges, 6 open branches and 22 tags. Building it takes well under a minute.

Repo A is too small to show anything about the graph's speed, so the large repo is the one that matters for performance.

## How long git takes

Three runs each, in milliseconds, on the large repo. The format is the one history will use: hash, parents, author name and email, author date and subject, `-z` separated.

| What | Without commit-graph | With commit-graph |
|---|---|---|
| `git version` (just starting git) | 128 / 122 / 111 | 120 / 119 / 119 |
| `--topo-order`, first 500 commits | 490 / 558 / 617 | 169 / 198 / 198 |
| `--date-order`, first 500 | 807 / 862 / 635 | 199 / 189 / 161 |
| git's default order, first 500 | 167 / 279 / 186 | 131 / 147 / 162 |
| `--topo-order`, all 50,000 | 841 / 667 / 675 | 746 / 680 / 683 |

What it means:

- **Without a commit-graph, `--topo-order` reads every commit before it prints the first one.** So the first 500 cost almost as much as all 50,000. With a commit-graph, git can stream them.
- **All 50,000 commits take under a second**, and the output is about 7.5 MB. One call can read the whole history.
- **Diffract cannot make a commit-graph** in a real repo: writing one is a write. git writes one itself during its automatic maintenance. Turning that on could be a setting in milestone 6.

## Proposal: one streamed call

Read the whole history with one `git log --topo-order` call, and parse it as it streams. The first rows go to the UI as soon as they are parsed, and the rest follow in pages. This is one process start, instead of one per page, and the first page arrives as fast as git can produce it either way.

Proposed budgets, to check again on Johan's laptop in phase 7: the first rows on screen within 1 second for a 50,000-commit repo, and lane layout for 50,000 commits under 300 ms on a CI runner.

## Starting git costs twice what it should

Diffract runs the `git` it finds on PATH, which is `C:\Program Files\Git\cmd\git.exe`. That is a small launcher: it sets up the environment and starts the real git, `mingw64\bin\git.exe`, as a second process. On Johan's laptop that second process start is the expensive part.

`git version`, median of 7 runs, in milliseconds:

| Machine state | `cmd\git.exe` | `mingw64\bin\git.exe` |
|---|---|---|
| Quiet | 345, 371 | 132, 159 |
| Busy, right after updating git | about 1,400 to 2,000 | about 270 to 950 |

Every git call Diffract makes would save 200 ms on a quiet machine, and more on a busy one. Opening a repo starts git several times, so this matters well beyond history.

What the launcher does differently, checked by printing the environment git hands its child processes through both executables:

- Git for Windows' source shows the launcher setting `MSYSTEM`, `PLINK_PROTOCOL` and `HOME`, and putting its own folders in front of PATH.
- The real git sets `HOME`, `MSYSTEM` and `PLINK_PROTOCOL` itself, to the same values, and adds `mingw64\bin` and `usr\bin` to PATH too.
- The only difference left is PATH's order: the launcher puts `mingw64\bin` and `usr\bin` before the user's own `%USERPROFILE%\bin`.

**Decided** by Johan on 2026-10-21: Diffract starts the real git. It looks at the first `git.exe` on PATH, as Windows would. When that is Git for Windows' launcher (`cmd\git.exe` or `bin\git.exe`) or the real git, it starts `mingw64\bin\git.exe` and puts `mingw64\bin` and `usr\bin` in front of PATH, which makes the environment match the launcher's. Anything else on PATH, such as a package manager's shim, is started as before.

Opening Repo A through the sidecar took 6.5 to 14 seconds both before and after the change, on a laptop busy enough that a single git start took over a second. Differences of a few hundred milliseconds disappear in that noise, so the end-to-end gain is not proven yet. Phase 7 checks it on Johan's laptop.

## Through the sidecar

Measured in phase 1 on 2026-10-21, with the published Native AOT sidecar driven over stdio the way the app drives it. Times are from sending `history.read` to receiving the first `history.page`, and to receiving the result after the last page.

| Repo | Commits | First page | All of it | JSON sent |
|---|---|---|---|---|
| Large test repo | 50,000 in 100 pages | 336 / 332 ms | 0.76 / 0.81 s | 14.8 MB |
| Repo A | 118 | 78 ms | the same | under 0.1 MB |
| Johan's two projects | 83 and 14 | under 0.2 s | the same | under 0.1 MB |

A commit's details took 60 to 120 ms, one git call each. The JSON is about 300 bytes per commit, so a page of 500 is about 150 KB.
