import { expect, test, type Page } from "@playwright/test";
import type {} from "../src/e2eHost";

// The repo in the repo.open contract sample, which the fake sidecar serves.
const repoRoot = "C:\\src\\shop";

/** Opens the repo, then its "Cleanup" tab, unless the folder is not a repo. */
async function openRepo(page: Page, folder = repoRoot) {
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Open a repo" })).toBeVisible();
  await page.evaluate((path) => (window.diffractTest.nextFolder = path), folder);
  await page.getByRole("button", { name: "Open repo…" }).first().click();
  const tab = page.getByRole("tab", { name: "Cleanup" });
  await expect(tab.or(page.getByRole("alert"))).toBeVisible();
  if (await tab.isVisible()) await tab.click();
}

/** Opens the sidebar's Worktrees section, which starts closed: branches come first. */
async function openWorktrees(page: Page) {
  await page.getByRole("navigation", { name: "Repository" }).getByRole("button", { name: /^Worktrees(, \d+)?$/ }).click();
}

const leftovers = (page: Page) => page.getByRole("list", { name: "Leftovers" });
const row = (page: Page, name: string) => leftovers(page).getByRole("listitem", { name });

test("opens a repo and shows its worktrees and leftovers", async ({ page }) => {
  await openRepo(page);

  await expect(page.getByRole("heading", { name: "Cleanup" })).toBeVisible();
  await expect(leftovers(page).getByRole("listitem")).toHaveCount(3);
  await expect(row(page, "copilot/fix-login")).toContainText("Needs review");
  await expect(row(page, "old-task")).toContainText("At risk: -");
  await openWorktrees(page);
  await expect(page.getByRole("navigation", { name: "Repository" })).toContainText("claude/räksmörgås-fix");
});

test("filters by verdict", async ({ page }) => {
  await openRepo(page);

  await page.getByRole("button", { name: "Needs review" }).click();

  await expect(leftovers(page).getByRole("listitem")).toHaveCount(1);
  await expect(row(page, "copilot/fix-login")).toBeVisible();
  await page.getByRole("button", { name: "All", exact: true }).click();
  await expect(leftovers(page).getByRole("listitem")).toHaveCount(3);
});

test("a repo.changed from the sidecar updates the row and highlights it", async ({ page }) => {
  await openRepo(page);
  await expect(leftovers(page).getByRole("listitem")).toHaveCount(3);

  await page.evaluate(() =>
    window.diffractTest.sidecar.change("1", (s) => ({
      ...s,
      leftovers: s.leftovers.map((l) => (l.name === "copilot/fix-login" ? { ...l, verdict: "InUse" as const, reason: "Files changed a minute ago" } : l)),
      summary: { ...s.summary, needsReview: 0, inUse: 2 },
    })),
  );

  const changed = row(page, "copilot/fix-login");
  await expect(changed).toHaveAttribute("data-flash", "changed");
  await expect(changed).toContainText("In use");
  await expect(changed).toContainText("Files changed a minute ago");
  await expect(changed).not.toHaveAttribute("data-flash", /.+/);
});

test("rows that disappear fade out and go", async ({ page }) => {
  await openRepo(page);
  await expect(leftovers(page).getByRole("listitem")).toHaveCount(3);

  await page.evaluate(() =>
    window.diffractTest.sidecar.change("1", (s) => ({ ...s, leftovers: s.leftovers.filter((l) => l.name !== "old-task") })),
  );

  await expect(row(page, "old-task")).toHaveAttribute("data-flash", "removed");
  await expect(leftovers(page).getByRole("listitem")).toHaveCount(2);
});

test("works from the keyboard", async ({ page }) => {
  await openRepo(page);
  await expect(leftovers(page).getByRole("listitem")).toHaveCount(3);

  const inUse = page.getByRole("button", { name: "In use" });
  for (let i = 0; i < 20 && !(await inUse.evaluate((el) => el === document.activeElement)); i++) await page.keyboard.press("Tab");
  await expect(inUse).toBeFocused();
  await page.keyboard.press("Enter");

  await expect(leftovers(page).getByRole("listitem")).toHaveCount(1);
});

test("says plainly when the folder is not a repo", async ({ page }) => {
  await openRepo(page, "C:\\Users\\johan\\Downloads");

  const alert = page.getByRole("alert");
  await expect(alert).toContainText("Could not open Downloads");
  await expect(alert).toContainText("Not a git repository");
  await expect(alert.getByRole("button", { name: "Retry" })).toBeVisible();
});

test("tells the user when the sidecar was restarted, and opens the repo again", async ({ page }) => {
  await openRepo(page);
  await expect(leftovers(page).getByRole("listitem")).toHaveCount(3);

  await page.evaluate(() => window.diffractTest.sidecarEvent({ kind: "restarted" }));

  await expect(page.getByRole("status")).toContainText("was restarted");
  await expect(leftovers(page).getByRole("listitem")).toHaveCount(3);
});

for (const colorScheme of ["light", "dark"] as const) {
  test(`follows the Windows ${colorScheme} theme`, async ({ page }) => {
    await page.emulateMedia({ colorScheme });
    await openRepo(page);

    const background = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);
    const [r = 0, g = 0, b = 0] = background.match(/\d+/g)!.map(Number);
    const lightness = (r + g + b) / 3;
    expect(colorScheme === "dark" ? lightness < 60 : lightness > 200).toBe(true);
  });
}

test("opens a terminal from the sidebar's menu with the keyboard", async ({ page }) => {
  await openRepo(page);
  await expect(leftovers(page).getByRole("listitem")).toHaveCount(3);
  await openWorktrees(page);

  const entry = page.getByRole("listitem", { name: "claude/räksmörgås-fix" });
  for (let i = 0; i < 20 && !(await entry.evaluate((el) => el === document.activeElement)); i++) await page.keyboard.press("Tab");
  await expect(entry).toBeFocused();
  await page.keyboard.press("Shift+F10");

  const menu = page.getByRole("menu", { name: "Actions for claude/räksmörgås-fix" });
  await expect(menu).toBeVisible();
  await expect(menu.getByRole("menuitem", { name: "Open in Windows Terminal" })).toBeFocused();
  await page.keyboard.press("ArrowDown");
  await page.keyboard.press("ArrowDown");
  await expect(menu.getByRole("menuitem", { name: "Open in Git Bash" })).toBeFocused();
  await page.keyboard.press("Enter");

  await expect(menu).toBeHidden();
  await expect(entry).toBeFocused();
  expect(await page.evaluate(() => window.diffractTest.sidecar.openedTerminals)).toEqual([String.raw`C:\src\shop\.claude\worktrees\räksmörgås fix in GitBash`]);
  await expect(page.getByRole("button", { name: "Open in Git Bash" })).toBeVisible();
});

test("chooses a terminal from the arrow next to the button", async ({ page }) => {
  await openRepo(page);

  await page.getByRole("button", { name: "Choose terminal" }).click();
  await page.getByRole("menuitemradio", { name: "Windows PowerShell" }).click();

  expect(await page.evaluate(() => window.diffractTest.sidecar.openedTerminals)).toEqual([String.raw`C:\src\shop in WindowsPowerShell`]);
  await expect(page.getByRole("button", { name: "Open in Windows PowerShell" })).toBeVisible();
});

test("offers a newer Git for Windows and installs it only after the user confirms", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Open a repo" })).toBeVisible();

  // Forget the answer from the first start, then start over: with no repo open, a restart does that.
  await page.evaluate(() => {
    window.diffractTest.sidecar.gitUpdate = {
      status: "UpdateAvailable",
      installedVersion: "2.47.1",
      availableVersion: "2.55.0.5",
      problem: null,
      downloadPage: "https://gitforwindows.org/",
    };
    window.diffractTest.storage.clear();
    window.diffractTest.sidecarEvent({ kind: "restarted" });
  });
  await page.getByRole("button", { name: "Update…" }).click();

  const dialog = page.getByRole("dialog");
  await expect(dialog).toContainText("From 2.47.1 to 2.55.0.5.");
  await expect(dialog).toContainText("No other program is using git right now.");
  await expect(dialog.getByRole("button", { name: "Cancel" })).toBeFocused();
  expect(await page.evaluate(() => window.diffractTest.sidecar.gitUpdates)).toEqual([]);

  // Afterwards winget has nothing newer to offer, as on a real machine.
  await page.evaluate(() => {
    window.diffractTest.sidecar.gitUpdate = { ...window.diffractTest.sidecar.gitUpdate, status: "UpToDate", availableVersion: null };
  });
  await dialog.getByRole("button", { name: "Update" }).click();

  await expect(dialog).toBeHidden();
  await expect.poll(() => page.evaluate(() => window.diffractTest.sidecar.gitUpdates)).toEqual(["2.55.0.5"]);
  await expect(page.getByText(/is available|is installed/)).toHaveCount(0);
});
