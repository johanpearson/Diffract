# Asking winget about Git for Windows

The spike from milestone 2, phase 6: how Diffract finds out, without parsing output meant for people, whether a newer Git for Windows is available, and how it will run the update. Checked on Johan's laptop on 2026-10-21 with winget 1.29.380 and Git for Windows 2.47.1.

## What was tried

| Way | Machine-readable? | Verdict |
|---|---|---|
| `winget list`, `show`, `search`, `upgrade` | No. There is no `--output json` or similar in winget 1.29. | Usable with care, see below |
| winget's exit codes | Yes. They are documented and stable. | Used |
| `winget export --include-versions` | Yes, JSON. But it lists installed versions only, never the available one. | Not enough on its own |
| `Microsoft.WinGet.Client` PowerShell module | Yes, objects with `InstalledVersion` and `IsUpdateAvailable`. | Not installed on Johan's machine, and Diffract would have to install a module and start PowerShell for every check |
| winget's COM API | Yes. It is what the PowerShell module uses. | Needs WinRT interop, which works against Native AOT |
| `winget mcp` | Yes, JSON over an MCP server. | Far too heavy for one question |

## What Diffract uses

One read-only call:

```
winget list --id Git.Git --exact --source winget --disable-interactivity
```

- **The exit code decides first.** 0 means winget found Git for Windows. `0x8A150014` means winget does not manage the Git for Windows on this machine, for example a portable git or one installed another way. `0x8A150046` means winget's source agreement was never accepted. Any other code is shown as is.
- **Then one table row is read.** With exit code 0, the output is a small table. The column headers are translated into the Windows display language and the name column can hold spaces, so Diffract never reads the headers or column positions. It finds the row by the exact id `Git.Git` and takes the words after it: the installed version, then the available one when there is one. Versions never hold spaces.
- **`--source winget`** keeps the Microsoft Store source and its agreement out of it, and removes the source column, so a row has exactly one or two versions after the id. A row of any other shape is refused.
- **`--disable-interactivity`** makes sure winget never waits for an answer. Output that is redirected has no progress spinner; the captured samples prove it.
- **Anything unexpected becomes "Unknown"** with a reason in plain English, and the UI then shows the versions it knows and a link to gitforwindows.org instead of an update button. Diffract never guesses.

On Johan's laptop the call takes about 1.5 seconds. The sidecar gives it 60 seconds, because winget first refreshes its package index when it is old.

### Versions

winget writes Git for Windows versions with its own release number as a fourth part: `2.55.0.5` is Git for Windows 2.55.0(5), which `git version` calls `2.55.0.windows.5`. The installed version in winget's table can have three parts (`2.47.1`). Versions are compared part by part, with missing parts counted as 0.

### The git Diffract uses may not be the one winget manages

Diffract runs the git on PATH. If its version is a different release from the one winget manages, updating through winget would not change Diffract's git. Diffract then says so and offers the download link instead of the update.

## How the update runs

Johan reviewed the check on 2026-10-21 and chose this approach over the PowerShell module. `git.update` runs only after the user confirms in a dialog that names both versions.

1. **Check again.** The sidecar runs the same `winget list` first. If winget no longer offers exactly the version the user confirmed, or the git Diffract uses is not the one winget manages, nothing is installed and the dialog says why. The dialog and the update can never disagree.
2. **Upgrade to that version only:**

   ```
   winget upgrade --id Git.Git --exact --version <confirmed> --source winget --disable-interactivity --accept-package-agreements
   ```

3. **Start over.** When it is done, the UI runs `app.info` and the update check again and opens the repo again, with whichever git is there now.

Notes:

- `--accept-package-agreements` is needed because winget cannot ask. The confirm dialog says that updating accepts the Git for Windows license (GPL-2.0). Johan agreed to this. Without it, winget fails with `0x8A150041`.
- Git for Windows installs for all users, so Windows shows its own admin prompt. If the user says no there, winget is expected to end with an error code, which Diffract shows. This has not been tried for real yet.
- Diffract never stops the installer once it has started, so there is no time limit and no cancel. Killing an installer halfway could leave git broken. If Diffract closes meanwhile, winget should carry on by itself, since the Rust core only stops the sidecar process. This has not been tried for real yet.
- Only one update runs at a time.
- Exit codes with their own message: `0x8A150109`, `0x8A15010A` and `0x8A15010B` (restart needed), `0x8A150101`, `0x8A150103` and `0x8A150111` (git is in use, for example by an agent or a Git Bash window), `0x8A15010C` (cancelled), `0x8A15002B` (nothing to update), and `0x8A150008`, `0x8A150107`, `0x8A15002E` and `0x8A150086` (download or network). Any other code is shown as is.
- Live refresh is not paused during the update, as first proposed. A live refresh that fails is already logged and tried again on the next change, and the fresh start after the update replaces everything anyway.
- When winget cannot update git, the UI offers the download page instead. The sidecar opens the fixed address <https://gitforwindows.org/> through Explorer, so the UI can never make it open anything else.

## Samples

The captured output is in `tests/Diffract.Sidecar.Tests/Samples/winget-list-*.txt`. `capture-winget-samples.ps1` in the same folder captures them again. It only runs `winget list`.
