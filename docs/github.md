# Working with GitHub

How PRs get merged, what protects main, and how to get unstuck.

## Merging a PR

1. Read the TL;DR and "Needs your decision".
2. Check that CI is green.
3. Click **Squash and merge** for most PRs. Use **Rebase and merge** when the separate commits are worth keeping on main.
   Or click **Enable auto-merge** and GitHub merges it as soon as CI passes.
4. Update your local copy:
   ```
   git switch main
   git pull
   git fetch --prune
   ```

## What protects main

`scripts/setup-github.ps1` applies two things:

- **Merge settings:** squash and rebase merges allowed, merge commits off, branches deleted after merge, auto-merge available.
- **Ruleset "Protect main"** from `.github/rulesets/protect-main.json`: no direct pushes, no force pushes, no deleting main, linear history, and every change goes through a PR where the `build` check must pass. No approval is required.

GitHub doesn't read the JSON file from the repo on its own. After changing it, run the script again.

## If you get stuck

You own the repo, so you can't permanently lock yourself out. An admin can always switch a ruleset off.

**Escape hatch:**

```
powershell -ExecutionPolicy Bypass -File scripts\github-unprotect.ps1
```

This disables "Protect main" without deleting it. Do what you need, then run `setup-github.ps1` again to switch protection back on.

**Without the script:** Settings, Rules, Rulesets, Protect main, set Enforcement status to Disabled, Save.

| What you see | Why | What to do |
|---|---|---|
| PR says the `build` check is "Expected, waiting for status to be reported" | The CI job was renamed, the workflow was deleted, or it didn't trigger | Fix the workflow in the PR. If that's not possible, unprotect, merge, protect again |
| CI is broken on main and every PR fails | A bad change got merged | Unprotect, push a fix straight to main, protect again |
| "Rebase and merge" is not available | The PR branch contains merge commits, which linear history forbids | Use "Squash and merge" instead, or ask the agent to rebase its branch on main |
| A script fails with 403 or 404 | `gh` is not logged in as the owner, or lacks permissions | Run `gh auth status`, then `gh auth login` again if needed |

## Rules to keep

- **Never rename the CI job `build`.** The ruleset requires a check with exactly that name. CI runs its work in parallel jobs (.NET tests, UI tests, app and installer). The `build` job waits for them and passes only if they all passed, so it is the only check the ruleset needs.
- **Docs-only PRs skip the build.** When a PR changes only Markdown files or files under `docs/`, the other jobs are skipped and `build` passes in under a minute. It still runs, so the required check is always reported. Pushes to main always run everything.
- **Don't add a bypass for yourself.** Agents push using your credentials, so a bypass for you is a bypass for them.
