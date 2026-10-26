import { expect, test, type Page } from "@playwright/test";
import type {} from "../src/e2eHost";

// The repo in the repo.open contract sample, which the fake sidecar serves with a few changes in its worktree.
const repoRoot = "C:\\src\\shop";

async function openChanges(page: Page) {
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Open a repo" })).toBeVisible();
  await page.evaluate((path) => (window.diffractTest.nextFolder = path), repoRoot);
  await page.getByRole("button", { name: "Open repo…" }).first().click();
  await page.getByRole("tab", { name: "Changes" }).click();
  await expect(list(page)).toBeVisible();
}

const list = (page: Page) => page.getByRole("listbox", { name: "Changed files" });
const group = (page: Page, name: "Unstaged" | "Staged") => list(page).getByRole("group", { name: new RegExp(`^${name}`) });
const rows = (page: Page, name: "Unstaged" | "Staged") => group(page, name).getByRole("option");

test("stages a file, stages a hunk from the diff, commits, and the lists are empty", async ({ page }) => {
  await openChanges(page);
  await expect(rows(page, "Unstaged")).toHaveText([/src\/Cart\.cs/, /docs\/notes\.md/]);

  // A hunk, from its own button in Monaco's diff. Monaco hides its zones from screen readers, so it is found by its
  // title, as the mouse finds it; the bar above the diff is the way for the keyboard.
  const diff = page.getByRole("region", { name: "Diff of src/Cart.cs" });
  await expect(diff.locator(".monaco-diff-editor")).toBeVisible();
  await diff.getByTitle("Stage the change at line 9").click();
  await expect(rows(page, "Staged")).toHaveText([/README\.md/, /src\/Cart\.cs/]);
  await expect(diff.getByTitle(/^Stage the change/)).toHaveCount(1);

  // The rest: one hunk left in Cart.cs, and the new file.
  await group(page, "Unstaged").getByRole("button", { name: "Stage all" }).click();
  await expect(rows(page, "Unstaged")).toHaveCount(0);

  await page.getByRole("textbox", { name: "Commit message" }).fill("Round the cart total\n\nAnd write it down.");
  await page.getByRole("button", { name: "Commit 3 files" }).click();

  await expect(rows(page, "Staged")).toHaveCount(0);
  await expect(page.getByText("Nothing has changed in this worktree.")).toBeVisible();
  expect(await page.evaluate((path) => window.diffractTest.sidecar.worktree(path).commits, repoRoot)).toEqual([
    { message: "Round the cart total\n\nAnd write it down.", amend: false },
  ]);

  // History shows the new commit at the top.
  await page.getByRole("tab", { name: "History" }).click();
  await expect(page.getByRole("listbox", { name: "Commits" }).getByRole("option").first()).toContainText("Round the cart total");
});

test("works from the keyboard: arrows move, Space stages, Ctrl+Enter commits", async ({ page }) => {
  await openChanges(page);

  await list(page).focus();
  await page.keyboard.press("ArrowDown");
  await expect(list(page).getByRole("option", { selected: true })).toContainText("docs/notes.md");
  await page.keyboard.press("Space");
  await expect(rows(page, "Staged")).toHaveText([/README\.md/, /docs\/notes\.md/]);

  await page.getByRole("textbox", { name: "Commit message" }).fill("Add notes");
  await list(page).focus();
  await page.keyboard.press("Control+Enter");

  await expect(rows(page, "Staged")).toHaveCount(0);
  await expect(rows(page, "Unstaged")).toHaveText([/src\/Cart\.cs/]);
});

test("unstages a hunk of a staged file from the diff", async ({ page }) => {
  await openChanges(page);

  await rows(page, "Staged").first().click();
  const diff = page.getByRole("region", { name: "Diff of README.md" });
  await expect(diff).toContainText("Staged, against HEAD");
  await diff.getByTitle("Unstage the change at line 1").click();

  await expect(rows(page, "Staged")).toHaveCount(0);
  await expect(rows(page, "Unstaged")).toHaveText([/README\.md/, /src\/Cart\.cs/, /docs\/notes\.md/]);
});

test("moves through a file's changes from the bar above the diff and stages one", async ({ page }) => {
  await openChanges(page);
  const changes = page.getByRole("group", { name: "Changes in this file" });
  await expect(changes).toContainText("Change 1 of 2, line 5");

  await changes.getByRole("button", { name: "Next change" }).click();
  await expect(changes).toContainText("Change 2 of 2, line 9");
  await changes.getByRole("button", { name: "Stage this change" }).click();

  await expect(rows(page, "Staged")).toHaveText([/README\.md/, /src\/Cart\.cs/]);
  await expect(changes).toContainText("Change 1 of 1, line 5");
  const asked = await page.evaluate(() => window.diffractTest.sidecar.stagesAsked);
  expect(asked.at(-1)).toMatchObject({ method: "changes.stage", hunk: { path: "src/Cart.cs", id: "cart-round" } });
});

const commandWindow = (page: Page) => page.getByRole("dialog");

test("pushes in the command window, which closes by itself, and the bar shows it is up to date", async ({ page }) => {
  await openChanges(page);
  const remote = page.getByRole("group", { name: "Remote" });

  await remote.getByRole("button", { name: "Push to origin/main" }).click();

  await expect(commandWindow(page)).toContainText("Pushed main to origin/main.");
  await expect(commandWindow(page).getByRole("log", { name: "Git output" })).toContainText("> git push");
  await expect(commandWindow(page)).toBeHidden();
  await expect(page.getByText("Up to date with origin/main")).toBeVisible();
  expect(await page.evaluate(() => window.diffractTest.sidecar.remoteAsked)).toEqual([`remote.push ${repoRoot}`]);
});

test("a fetch can be cancelled from the command window", async ({ page }) => {
  await openChanges(page);
  await page.evaluate(() => window.diffractTest.sidecar.held.add("remote.fetch"));
  const remote = page.getByRole("group", { name: "Remote" });

  await remote.getByRole("button", { name: "Fetch" }).click();
  await expect(commandWindow(page).getByRole("status")).toHaveText("Running");
  await expect(commandWindow(page).locator("svg.command-animation")).toHaveClass(/is-running/);
  await commandWindow(page).getByRole("button", { name: "Cancel" }).click();

  await expect(commandWindow(page)).toBeHidden();
  await expect(remote.getByRole("button", { name: "Fetch" })).toBeEnabled();
});

test("the animation moves, and stands still with reduce motion on", async ({ page }) => {
  await openChanges(page);
  await page.evaluate(() => window.diffractTest.sidecar.held.add("remote.fetch"));
  await page.getByRole("group", { name: "Remote" }).getByRole("button", { name: "Fetch" }).click();
  const drop = commandWindow(page).locator(".anim-drop").first();

  await expect.poll(() => drop.evaluate((el) => getComputedStyle(el).animationName)).toBe("anim-drop");
  await page.emulateMedia({ reducedMotion: "reduce" });
  await expect.poll(() => drop.evaluate((el) => getComputedStyle(el).animationName)).toBe("none");
});

for (const layout of ["Side by side", "Inline"]) {
  test(`Stage hunk sits in its own room above the change, ${layout.toLowerCase()}`, async ({ page }) => {
    await openChanges(page);
    const diff = page.getByRole("region", { name: "Diff of src/Cart.cs" });
    await diff.getByRole("group", { name: "Diff layout" }).getByRole("button", { name: layout }).click();
    await expect(diff.locator(".monaco-diff-editor")).toBeVisible();
    const button = diff.getByTitle("Stage the change at line 9");
    await expect(button).toBeVisible();

    // No line of code in the editor the button is in, removed lines included, lies under it. A pixel is rounding.
    const box = (await button.boundingBox())!;
    const lines = await diff.locator(".editor.modified .view-line").evaluateAll((els) =>
      els.filter((el) => el.textContent!.trim() !== "").map((el) => ({ text: el.textContent, ...el.getBoundingClientRect().toJSON() as DOMRect })),
    );
    const overlap = (a0: number, a1: number, b0: number, b1: number) => Math.min(a1, b1) - Math.max(a0, b0);
    const covered = lines.filter((r) => overlap(r.top, r.bottom, box.y, box.y + box.height) > 1 && overlap(r.left, r.right, box.x, box.x + box.width) > 1);
    expect(lines.length).toBeGreaterThan(0);
    expect(covered.map((r) => r.text)).toEqual([]);

    // A plain click: Playwright fails it if anything else would take the click.
    await button.click();
    await expect(rows(page, "Staged")).toHaveText([/README\.md/, /src\/Cart\.cs/]);
  });
}
