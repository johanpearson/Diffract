# Reading history fast

The spike from milestone 3, phase 0: how fast git can hand Diffract a repo's history, and what slows it down. Measured on 2026-10-21 on Johan's laptop with Git for Windows 2.55.0.5. Every command was read-only, except writing a commit-graph in a generated test repo in a temp folder.

## The repos

- **Repo A**, Johan's busiest work repo, is small: 115 commits on its branches, tags and HEAD, 277 counting every ref, and 188 refs. Most refs are Copilot checkpoints. It has no commit-graph file.
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

The launcher also sets a few environment variables that some git features need: it puts Git for Windows' `mingw64\bin` and `usr\bin` folders on PATH, and sets `MSYSTEM`. Read-only commands such as `log` and `status` work without them, but pushing and pulling over SSH (milestone 4) may not. Calling the real git directly would mean setting the same variables on Diffract's side.

This needs Johan's decision, since it changes how every git call starts.
