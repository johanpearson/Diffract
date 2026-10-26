import { expect, test, type Page } from "@playwright/test";
import type {} from "../src/e2eHost";

// The repo in the repo.open contract sample: main on a merge, whose first parent is the root commit with origin/main
// and v1.0 on it.
const repoRoot = "C:\\src\\shop";

async function openHistory(page: Page) {
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Open a repo" })).toBeVisible();
  await page.evaluate((path) => (window.diffractTest.nextFolder = path), repoRoot);
  await page.getByRole("button", { name: "Open repo…" }).first().click();
  await expect(rows(page)).toHaveCount(2);
}

const list = (page: Page) => page.getByRole("listbox", { name: "Commits" });
const rows = (page: Page) => list(page).getByRole("option");
const menu = (page: Page) => page.getByRole("menu");
const dialog = (page: Page) => page.getByRole("dialog");
const asked = (page: Page) => page.evaluate(() => window.diffractTest.sidecar.refsAsked.map((a) => a.method).filter((m) => m !== "ref.checkName"));

/** Right-clicks the root commit's row and picks an item. */
async function onRoot(page: Page, item: string | RegExp) {
  await rows(page).nth(1).click({ button: "right" });
  await menu(page).getByRole("menuitem", { name: item }).click();
}

test("opens with the keyboard on the selected row, and copies its hash", async ({ page, context }) => {
  await context.grantPermissions(["clipboard-read", "clipboard-write"]);
  await openHistory(page);
  await list(page).focus();

  await page.keyboard.press("Shift+F10");
  await expect(menu(page).getByRole("menuitem").first()).toBeFocused();
  await page.keyboard.press("Enter");

  await expect(page.getByText("Copied the hash.")).toBeVisible();
  expect(await page.evaluate(() => navigator.clipboard.readText())).toMatch(/^9fceb02d/);
});

test("stays inside the window when opened on a row at its bottom, with every item reachable", async ({ page }) => {
  await openHistory(page);
  const row = (await rows(page).nth(1).boundingBox())!;
  const viewport = page.viewportSize()!;
  await page.setViewportSize({ width: viewport.width, height: Math.ceil(row.y + row.height + 8) });

  await rows(page).nth(1).click({ button: "right" });

  const box = (await menu(page).boundingBox())!;
  const height = page.viewportSize()!.height;
  expect(box.y).toBeGreaterThanOrEqual(0);
  expect(box.y + box.height).toBeLessThanOrEqual(height);
  await menu(page).getByRole("menuitem").last().click({ trial: true });
  const last = (await menu(page).getByRole("menuitem").last().boundingBox())!;
  expect(last.y + last.height).toBeLessThanOrEqual(height);
});

test("creates a branch without switching, then switches to it from its row", async ({ page }) => {
  await openHistory(page);
  await onRoot(page, "Create branch here…");

  await dialog(page).getByRole("textbox", { name: "Name" }).fill("main");
  await expect(dialog(page).getByText("There is already a branch named main.")).toBeVisible();
  await dialog(page).getByRole("textbox", { name: "Name" }).fill("fix/räksmörgås");
  await dialog(page).getByRole("checkbox", { name: "Switch to it" }).uncheck();
  await dialog(page).getByRole("button", { name: "Create branch" }).click();
  await expect(page.getByText(/^Created branch fix\/räksmörgås at 4b825dc6\.$/)).toBeVisible();
  await expect(rows(page).nth(1)).toContainText("fix/räksmörgås");

  await onRoot(page, "Switch to fix/räksmörgås");

  await expect(page.getByText("Switched to fix/räksmörgås.")).toBeVisible();
  expect(await asked(page)).toEqual(["branch.create", "branch.switch"]);
});

test("creates a tag, and checks out a commit on a detached HEAD", async ({ page }) => {
  await openHistory(page);
  await onRoot(page, "Create tag here…");
  await dialog(page).getByRole("textbox", { name: "Name" }).fill("v2.0");
  await dialog(page).getByRole("button", { name: "Create tag" }).click();
  await expect(page.getByText("Created tag v2.0 at 4b825dc6.")).toBeVisible();

  await onRoot(page, "Check out this commit (detached)");

  await expect(page.getByText("Checked out 4b825dc6 on a detached HEAD.")).toBeVisible();
  expect(await asked(page)).toEqual(["tag.create", "branch.switch"]);
});

test("resets main soft after showing what leaves it, and undoes the reset", async ({ page }) => {
  await openHistory(page);
  await onRoot(page, "Reset main to here…");

  await expect(dialog(page)).toContainText("1 commit leaves main:");
  await expect(dialog(page).getByRole("radio", { name: /Soft/ })).toBeChecked();
  await dialog(page).getByRole("button", { name: "Reset" }).click();
  await expect(page.getByText("Reset main (soft) from 9fceb02d to 4b825dc6.")).toBeVisible();
  await page.getByRole("button", { name: "Undo reset" }).click();

  await expect(page.getByText("Undid the reset: main is back at 9fceb02d.")).toBeVisible();
  expect(await asked(page)).toEqual(["branch.reset", "branch.undoReset"]);
});

test("the sidebar shows a tag's commit on click, and switches to a branch on double-click", async ({ page }) => {
  await openHistory(page);
  const sidebar = page.getByRole("navigation", { name: "Repository" });
  await sidebar.getByRole("button", { name: /^Tags(, \d+)?$/ }).click();

  await sidebar.getByRole("button", { name: "v1.0" }).click();
  await expect(rows(page).nth(1)).toHaveAttribute("aria-selected", "true");

  await sidebar.getByRole("button", { name: /^claude(, \d+)?$/ }).click();
  await sidebar.getByRole("button", { name: "räksmörgås-fix" }).dblclick();
  await expect(page.getByText("Switched to claude/räksmörgås-fix.")).toBeVisible();
  await expect(sidebar.getByRole("button", { name: "räksmörgås-fix" })).toHaveAttribute("aria-current", "true");
  expect(await asked(page)).toEqual(["branch.switch"]);
});
