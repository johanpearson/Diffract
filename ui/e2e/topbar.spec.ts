import { expect, test, type Page } from "@playwright/test";
import type {} from "../src/e2eHost";

const repoRoot = "C:\\src\\shop";
const longBranch = "issue-77/history-delete-branch-through-the-cleanup-planner-with-undo";

async function openRepo(page: Page) {
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Open a repo" })).toBeVisible();
  await page.evaluate(
    ([path, branch]) => {
      const wt = window.diffractTest.sidecar.worktree(path!);
      wt.status = { ...wt.status, branch: branch!, upstream: `origin/${branch}`, ahead: 12, behind: 3 };
      window.diffractTest.nextFolder = path!;
    },
    [repoRoot, longBranch],
  );
  await page.getByRole("button", { name: "Open repo…" }).first().click();
  await expect(page.getByRole("group", { name: "Branch" })).toBeVisible();
}

/** Every piece of the bar: the name, the branch and its badge, each button. */
async function pieces(page: Page) {
  return page.evaluate(() => {
    const bar = document.querySelector("header.topbar")!;
    const parts = [...bar.querySelectorAll(".brand, .branch-name, .sync-badge, button")];
    return {
      overflows: bar.scrollWidth > bar.clientWidth,
      boxes: parts.map((p) => {
        const r = p.getBoundingClientRect();
        return { name: p.getAttribute("aria-label") ?? p.textContent ?? "", left: r.left, right: r.right, width: r.width };
      }),
      barRight: bar.getBoundingClientRect().right,
    };
  });
}

for (const scheme of ["light", "dark"] as const) {
  for (const width of [640, 900, 1600]) {
    test(`nothing in the top bar overlaps at ${width}px wide, ${scheme}`, async ({ page }, info) => {
      await page.emulateMedia({ colorScheme: scheme });
      await page.setViewportSize({ width, height: 500 });
      await openRepo(page);

      const { overflows, boxes, barRight } = await pieces(page);

      expect(overflows).toBe(false);
      const sorted = boxes.toSorted((a, b) => a.left - b.left);
      for (const [i, box] of sorted.entries()) {
        expect(box.width, box.name).toBeGreaterThan(0);
        expect(box.right, box.name).toBeLessThanOrEqual(barRight);
        if (i > 0) expect(box.left, `${box.name} against ${sorted[i - 1]!.name}`).toBeGreaterThanOrEqual(sorted[i - 1]!.right - 0.5);
      }
      await page.locator("header.topbar").screenshot({ path: info.outputPath(`topbar-${width}-${scheme}.png`) });
    });
  }
}

test("the long branch name is cut with an ellipsis, and its tooltip is the whole name", async ({ page }) => {
  await page.setViewportSize({ width: 640, height: 500 });
  await openRepo(page);

  const name = page.locator(".branch-name");
  await expect(name).toHaveAttribute("title", longBranch);
  expect(await name.evaluate((el) => el.scrollWidth > el.clientWidth)).toBe(true);
});

test("the buttons are icons with names, and About holds the versions", async ({ page }) => {
  await page.setViewportSize({ width: 1100, height: 500 });
  await openRepo(page);

  const bar = page.locator("header.topbar");
  for (const name of ["Fetch", "Prune", "Pull", `Push to origin/${longBranch}`, "About", "Open repo…"]) {
    const button = bar.getByRole("button", { name, exact: true });
    await expect(button).toBeVisible();
    await expect(button).toHaveText("");
  }
  await expect(bar.getByText(/sidecar/)).toBeHidden();

  await bar.getByRole("button", { name: "About" }).click();
  await expect(page.getByRole("region", { name: "About Diffract" })).toContainText("sidecar");
  await page.keyboard.press("Escape");
  await expect(page.getByRole("region", { name: "About Diffract" })).toBeHidden();
});
