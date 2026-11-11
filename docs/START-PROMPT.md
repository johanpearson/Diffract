# Start prompt

Paste everything below the line into Claude Code (or Copilot agent mode) from the repo root. It works for every issue. Add an issue number at the end to pick one, or leave it out and the agent chooses.

---

Read these files in order before doing anything:

1. `AGENTS.md`: rules for working in this repo. The safety rules are non-negotiable.
2. `docs/brief.md`: what Diffract is and the decisions that are locked.

Then find your issue. If I gave you a number, use it. Otherwise take the lowest-numbered open issue in the earliest open GitHub milestone, as `AGENTS.md` says. List them with `gh api repos/johanpearson/Diffract/milestones` and `gh api "repos/johanpearson/Diffract/issues?milestone=<number>&state=open"`. Read the whole issue and its comments: it is the spec. Stop and wait at any issue labelled `checkpoint`.

Then do the issue in one PR that starts with `Closes #<number>`.

How I want you to work:

- Write tests alongside the code, not after. If a detail in the issue turns out to be wrong once you see real git output, say so under "Needs your decision" in the PR and propose a fix rather than guessing.
- Never write to a repo you didn't create in a temp directory. If you are unsure whether a git command writes, treat it as a write.
- I don't review code. I read the PR description and check that CI is green. Follow the PR rules in AGENTS.md.
- If something would change a locked decision in the brief, stop and ask.
- To build and run anything beyond `dotnet test`, follow `docs/setup.md`.

Start by confirming you've read the files, name the issue you chose and say why, summarize it in your own words, and list anything in it that looks ambiguous or still open. Then begin.
