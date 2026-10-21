# Start prompt

Paste everything below the line into Claude Code (or Copilot agent mode) from the repo root. It works for every milestone: the current one is named in the README under "Status".

---

Read these files in order before doing anything:

1. `AGENTS.md`: rules for working in this repo. The safety rules are non-negotiable.
2. `docs/brief.md`: what Diffract is and the decisions that are locked.
3. `README.md`: the "Status" section names the current milestone.
4. The current milestone doc in `docs/milestones/`.

Then work through the milestone phase by phase, one PR per phase, and stop at every checkpoint the milestone doc names. A phase marked **Done** is finished: start at the first phase that is not, and say which one that is. When Johan signs off a checkpoint, mark that phase **Done** in the milestone doc in your next PR, with the date and the PR numbers.

How I want you to work:

- Write tests alongside the code, not after. If a spec detail turns out to be wrong once you see real git output, say so under "Needs your decision" in the PR and propose a fix rather than guessing.
- Never write to a repo you didn't create in a temp directory. If you are unsure whether a git command writes, treat it as a write.
- I don't review code. I read the PR description and check that CI is green. Follow the PR rules in AGENTS.md.
- If something would change a locked decision in the brief, stop and ask.
- To build and run anything beyond `dotnet test`, follow `docs/setup.md`.

Start by confirming you've read the files, give a short summary of the milestone in your own words, and list anything in it that looks ambiguous. Then begin the first phase that is not marked **Done**.
