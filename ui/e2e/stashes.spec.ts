import { expect, test, type Page } from "@playwright/test";
import type {} from "../src/e2eHost";

// The repo in the repo.open contract sample, which the fake sidecar serves. Its one stash is "Halvfärdigt".
const repoRoot = "C:\\src\\shop";

async function openRepo(page: Page) {
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Open a repo" })).toBeVisible();
  await page.evaluate((path) => (window.diffractTest.nextFolder = path), repoRoot);
  await page.getByRole("button", { name: "Open repo…" }).first().click();
  await page.getByRole("navigation", { name: "Repository" }).getByRole("button", { name: /^Stashes/ }).click();
}

const stashes = (page: Page) => page.getByRole("list", { name: "Stashes" }).getByRole("button");

test("stash changes in Changes, then see it at the top of the sidebar", async ({ page }) => {
  await openRepo(page);
  await page.getByRole("tab", { name: "Changes" }).click();
  await page.getByRole("textbox", { name: "Commit message" }).fill("Mitt i allt");

  await page.getByRole("button", { name: "Stash changes" }).click();

  await expect(page.getByText("Stashed your changes. The stash keeps them until you drop it.")).toBeVisible();
  await expect(stashes(page)).toHaveText(["Mitt i allt", "Halvfärdigt"]);
});

test("drop a stash after the dialog, then undo the drop", async ({ page }) => {
  await openRepo(page);

  await stashes(page).first().click({ button: "right" });
  await page.getByRole("menuitem", { name: "Drop…" }).click();
  const dialog = page.getByRole("dialog", { name: "Drop this stash?" });
  await expect(dialog).toContainText("Keep a backup tag archive/stash/");
  await dialog.getByRole("button", { name: "Drop" }).click();

  await expect(stashes(page)).toHaveCount(0);
  await page.getByRole("button", { name: "Undo drop" }).click();
  await expect(stashes(page)).toHaveText(["Halvfärdigt"]);
});

test("the drop dialog opens with Cancel focused, so Enter drops nothing", async ({ page }) => {
  await openRepo(page);
  await stashes(page).first().click({ button: "right" });
  await page.getByRole("menuitem", { name: "Drop…" }).click();
  await expect(page.getByRole("dialog", { name: "Drop this stash?" })).toBeVisible();

  await page.keyboard.press("Enter");

  await expect(page.getByRole("dialog", { name: "Drop this stash?" })).toHaveCount(0);
  await expect(stashes(page)).toHaveText(["Halvfärdigt"]);
});
