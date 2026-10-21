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

Johan reviewed the check on 2026-10-21 and chose this approach over the PowerShell module. `git.update` runs only after the user confirms in a dialog that names both versions and lists the programs using git right now (`git.inUse`).

1. **Check again.** The sidecar runs the same `winget list` first. If winget no longer offers exactly the version the user confirmed, or the git Diffract uses is not the one winget manages, nothing is installed and Diffract says why. The dialog and the update can never disagree.
2. **Upgrade to that version only:**

   ```
   winget upgrade --id Git.Git --exact --version <confirmed> --source winget --disable-interactivity --accept-package-agreements
   ```

3. **Catch up.** When it is done, the UI asks `app.info` and winget again, and refreshes the repo without `fsck`. A git that was too old and now works starts the app over.

The update runs in the background, and Diffract shows nothing while it runs: the installer shows its own window. Refresh and Open repo wait until it is done. Afterwards a good update shows only as the new git version in the top bar. A bar appears only when the user has something to do: the update failed, or Windows must restart.

### What the first real update taught us

Johan's first update from the installed app, on 2026-10-21, failed with `0x8A150006`. The winget and installer logs in `%LOCALAPPDATA%\Packages\Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\LocalState\DiagOutputDir` showed why:

- The download took 4 seconds. The installer then waited about 3.5 minutes and gave up: two long-running `git check-ignore --stdin` processes, started by the CodeScene extension in VS Code, were using Git for Windows.
- winget runs the installer with `/SILENT /SUPPRESSMSGBOXES`, so it cannot ask the user to close them. It answers its own "close these programs" question with Cancel and ends with exit code 1, which winget reports as `0x8A150006`.
- Diffract's message then guessed that the admin prompt had been refused. It had not. Messages no longer guess.

What changed because of it:

- **Who uses git is shown before the update**, and again when the installer stops. The sidecar reads the process list (the Tool Help functions, no git calls), finds every process running from Git for Windows' folder, and names the program that started it, such as "CodeScene cs-ide tool for windows in Visual Studio Code" or "Git Bash". Diffract's own git calls are left out.
- **Live refresh pauses during the update**, so Diffract's own git calls cannot stop the installer. This replaces the first plan, which let them fail and retry.
- **No progress from Diffract.** winget prints no progress at all when its output is redirected: a captured `winget download` shows only a few lines of text and no progress bar. A first version showed a stage read from the process list, but Johan found it noise next to the installer's own window, so it was removed.
- **winget is asked less often.** Running it while a repo opens slows the opening on Johan's laptop. The check now runs only after the repo is open, and an answer is reused for a day unless git itself changed. A git that is too old is checked at once, since nothing else can happen until it is updated. A "Check for git updates" button was tried and removed, to keep the screen clean.

Johan's second update, with the programs using git closed, installed Git for Windows 2.55.0.5.

Notes:

- `--accept-package-agreements` is needed because winget cannot ask. The confirm dialog says that updating accepts the Git for Windows license (GPL-2.0). Johan agreed to this. Without it, winget fails with `0x8A150041`.
- Git for Windows installs for all users, so Windows shows its own admin prompt. If the user says no there, winget is expected to end with an error code, which Diffract shows. This has not been tried for real yet.
- Diffract never stops the installer once it has started, so there is no time limit and no cancel. Killing an installer halfway could leave git broken. If Diffract closes meanwhile, winget should carry on by itself, since the Rust core only stops the sidecar process. This has not been tried for real yet.
- Only one update runs at a time.
- Exit codes with their own message: `0x8A150109`, `0x8A15010A` and `0x8A15010B` (restart needed); `0x8A150006`, `0x8A150101`, `0x8A150103` and `0x8A150111` (the installer stopped, usually because git is in use, and Diffract names who uses it); `0x8A15010C` (cancelled); `0x8A15002B` (nothing to update); and `0x8A150008`, `0x8A150107`, `0x8A15002E` and `0x8A150086` (download or network). Any other code is shown as is.
- When winget cannot update git, the UI offers the download page instead. The sidecar opens the fixed address <https://gitforwindows.org/> through Explorer, so the UI can never make it open anything else.

## Samples

The captured output is in `tests/Diffract.Sidecar.Tests/Samples/winget-list-*.txt`. `capture-winget-samples.ps1` in the same folder captures them again. It only runs `winget list`.
