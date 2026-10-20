# One-time setup

Do these steps by hand, once. This isn't a prompt for an agent. It takes about 10 minutes.

When you're done, the agent deletes this file in its first PR. Everything you need afterwards is in `docs/github.md`.

## 0. Tools

- git 2.40 or later: `git --version`
- .NET 10 SDK: `dotnet --version`
- GitHub CLI, logged in as the repo owner:
  ```
  winget install GitHub.cli
  gh auth login
  gh auth status
  ```

## 1. Push the kickoff package

Main has no protection yet, so a direct push works. Extract from Downloads so the zip itself doesn't end up in the repo.

```
git clone https://github.com/johanpearson/Diffract.git
cd Diffract
Expand-Archive "$HOME\Downloads\diffract-m1-kickoff.zip" -DestinationPath .
git add -A
git commit -m "docs: plan, agent instructions and milestone 1 spec"
git branch -M main
git push -u origin main
```

## 2. Wait for green CI

The push starts the CI workflow. It passes even though there's no code yet.

```
gh run list --limit 1
```

Wait until it says `completed  success`. **Don't continue until it's green.** The next step makes this check required, so a broken CI here would block every PR.

## 3. Protect main

```
powershell -ExecutionPolicy Bypass -File scripts\setup-github.ps1
```

Check the result at https://github.com/johanpearson/Diffract/rules

## 4. Start the agent

Open Claude Code in the repo folder and paste the prompt from `docs/START-PROMPT.md`.

From here on, see `docs/github.md` for merging PRs and the escape hatch if anything gets stuck.
