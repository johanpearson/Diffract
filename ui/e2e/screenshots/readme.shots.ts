import { test, type Page } from "@playwright/test";
import type {} from "../../src/e2eHost";
import { sampleHistory, ShotsNow } from "./sampleHistory";

// Regenerates the README's screenshots in docs/images from the real UI against the fake sidecar's sample data.
// Run `npm run screenshots` in ui/. They are UI screenshots, so shrink them before committing (128 colours is plenty).

const repoRoot = "C:\\src\\shop";
const schemes = ["light", "dark"] as const;
type Scheme = (typeof schemes)[number];

async function startApp(page: Page, scheme: Scheme) {
  await page.emulateMedia({ colorScheme: scheme });
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.clock.setFixedTime(ShotsNow);
  await page.goto("/");
  await page.getByRole("heading", { name: "Open a repo" }).waitFor();
}

async function openRepo(page: Page) {
  await page.evaluate((path) => (window.diffractTest.nextFolder = path), repoRoot);
  await page.getByRole("button", { name: "Open repo…" }).first().click();
}

async function shoot(page: Page, view: string, scheme: Scheme) {
  await page.waitForTimeout(1200);
  await page.screenshot({ path: `../docs/images/${view}-${scheme}.png` });
}

for (const scheme of schemes) {
  test(`history, ${scheme}`, async ({ page }) => {
    await startApp(page, scheme);
    const history = sampleHistory();
    await page.evaluate((h) => Object.assign(window.diffractTest.sidecar, { history: h.commits, refs: h.refs }), history);
    await openRepo(page);
    await page.getByRole("listbox", { name: "Commits" }).getByRole("option").nth(2).click();
    await shoot(page, "history", scheme);
  });

  test(`changes, ${scheme}`, async ({ page }) => {
    await startApp(page, scheme);
    await openRepo(page);
    await page.getByRole("tab", { name: "Changes" }).click();
    await page.getByRole("textbox", { name: "Commit message" }).fill("Round the cart total to two decimals");
    await page.getByRole("heading", { name: "Changes" }).click();
    await shoot(page, "changes", scheme);
  });

  test(`cleanup, ${scheme}`, async ({ page }) => {
    await startApp(page, scheme);
    await openRepo(page);
    await page.getByRole("tab", { name: "Cleanup" }).click();
    await page.getByRole("checkbox", { name: "Select old-task" }).check();
    await page.getByRole("checkbox", { name: "Select copilot/fix-login" }).check();
    await page.getByRole("button", { name: "Clean up 2 selected" }).click();
    await page.getByRole("dialog").waitFor();
    await shoot(page, "cleanup", scheme);
  });
}
