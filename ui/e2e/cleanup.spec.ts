import { expect, test, type Page } from "@playwright/test";
import type {} from "../src/e2eHost";

// The repo in the repo.open contract sample, which the fake sidecar serves.
const repoRoot = "C:\\src\\shop";

/** Opens the repo, then its "Cleanup" tab. */
async function openRepo(page: Page) {
  await page.goto("/");
  // The app sets up its test hooks as it starts, so wait for it first.
  await expect(page.getByRole("heading", { name: "Open a repo" })).toBeVisible();
  await page.evaluate((path) => (window.diffractTest.nextFolder = path), repoRoot);
  await page.getByRole("button", { name: "Open repo…" }).first().click();
  await page.getByRole("tab", { name: "Cleanup" }).click();
}

const leftovers = (page: Page) => page.getByRole("list", { name: "Leftovers" });
const row = (page: Page, name: string) => leftovers(page).getByRole("listitem", { name });

test("select, confirm, see the rows go, undo, see them come back", async ({ page }) => {
  await openRepo(page);
  await expect(leftovers(page).getByRole("listitem")).toHaveCount(3);
  await expect(row(page, "räksmörgås fix").getByRole("checkbox")).toHaveCount(0);

  await page.getByRole("checkbox", { name: "Select old-task" }).check();
  await page.getByRole("checkbox", { name: "Select copilot/fix-login" }).check();
  await page.getByRole("button", { name: "Clean up 2 selected" }).click();

  const dialog = page.getByRole("dialog", { name: "Clean up 2 leftovers?" });
  await expect(dialog).toContainText("Move folder C:\\src\\shop\\.claude\\worktrees\\old-task to the Recycle Bin");
  await expect(dialog).toContainText("Diffract cannot undo this. Restore it from the Recycle Bin.");
  await expect(dialog).toContainText("Keep a backup tag archive/branch/copilot/fix-login-2026-10-21 for 2 commits that exist nowhere else");
  await dialog.getByRole("button", { name: "Clean up" }).click();

  await expect(page.getByRole("region", { name: "Cleanup result" })).toContainText("Kept as a backup");
  await expect(leftovers(page).getByRole("listitem")).toHaveCount(1);
  await expect(row(page, "räksmörgås fix")).toBeVisible();

  await page.getByRole("region", { name: "Cleanup result" }).getByRole("button", { name: "Undo" }).click();

  await expect(page.getByRole("region", { name: "Undo result" })).toBeVisible();
  await expect(leftovers(page).getByRole("listitem")).toHaveCount(3);
  await page.getByText("Past cleanups (2)").click();
  await expect(page.getByRole("list", { name: "Past cleanups" }).getByRole("listitem")).toHaveCount(2);
});

test("a row that needs review says what to check, opens to list its commits, and shows in History", async ({ page }) => {
  await openRepo(page);
  const branch = row(page, "copilot/fix-login");
  await expect(branch).toContainText("Look at the 2 commits only on this branch in History.");

  await branch.getByRole("button", { name: "Show the commits of copilot/fix-login" }).click();

  await expect(page.getByRole("list", { name: "Commits of copilot/fix-login" })).toContainText("Retry the login on räksmörgås");
  await branch.getByRole("button", { name: "Show copilot/fix-login in History" }).click();
  await expect(page.getByRole("tab", { name: "History" })).toHaveAttribute("aria-selected", "true");
});

test("the confirm dialog opens with Cancel focused, so Enter removes nothing", async ({ page }) => {
  await openRepo(page);
  await page.getByRole("checkbox", { name: "Select old-task" }).check();
  await page.getByRole("button", { name: "Clean up 1 selected" }).click();
  await expect(page.getByRole("dialog")).toBeVisible();

  await page.keyboard.press("Enter");

  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expect(leftovers(page).getByRole("listitem")).toHaveCount(3);
});
