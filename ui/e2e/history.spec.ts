import { expect, test, type Page } from "@playwright/test";
import type {} from "../src/e2eHost";

// The repo in the repo.open contract sample, which the fake sidecar serves.
const repoRoot = "C:\\src\\shop";
const BigHistory = 50_000;

/** Opens the sample repo with a generated history of `commits` commits, and shows the History tab. */
async function openHistory(page: Page, commits = BigHistory) {
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Open a repo" })).toBeVisible();
  await page.evaluate(
    ([path, count]) => {
      window.diffractTest.nextFolder = path;
      window.diffractTest.sidecar.useGeneratedHistory(count);
    },
    [repoRoot, commits] as const,
  );
  await page.getByRole("button", { name: "Open repo…" }).first().click();
  await page.getByRole("tab", { name: "History" }).click();
  await expect(page.getByRole("status").filter({ hasText: "commits" })).toHaveText(`${commits.toLocaleString("en-US")} commits`);
}

const list = (page: Page) => page.getByRole("listbox", { name: "Commits" });
const selected = (page: Page) => list(page).getByRole("option", { selected: true });
const panel = (page: Page) => page.getByRole("complementary", { name: "Commit details" });
const subjectOf = (page: Page) => panel(page).getByRole("heading", { level: 2 });

test("shows a 50,000-commit history and draws only the rows in view", async ({ page }) => {
  await openHistory(page);

  await expect(selected(page)).toHaveAttribute("aria-posinset", "1");
  await expect(subjectOf(page)).toHaveText(/^Merge branch|^Change|^Work/);
  expect(await list(page).getByRole("option").count()).toBeLessThan(100);
});

test("draws the graph on the canvas, in the theme's lane colours", async ({ page }) => {
  for (const colorScheme of ["dark", "light"] as const) {
    await page.emulateMedia({ colorScheme });
    await openHistory(page, 200);

    // The newest commit's dot: first lane, middle of the first row. A merge is a ring, so sample its edge.
    const pixel = await page.evaluate(() => {
      const canvas = document.querySelector<HTMLCanvasElement>("canvas.graph")!;
      const scale = canvas.width / canvas.clientWidth;
      const [r, g, b, a] = canvas.getContext("2d")!.getImageData(Math.round(16 * scale), Math.round(9 * scale), 1, 1).data;
      return { r, g, b, a };
    });
    const lane0 = colorScheme === "dark" ? { r: 0x8f, g: 0xb8, b: 0xa0 } : { r: 0x3d, g: 0x7a, b: 0x58 };
    expect(pixel.a).toBeGreaterThan(200);
    expect(Math.abs(pixel.r! - lane0.r) + Math.abs(pixel.g! - lane0.g) + Math.abs(pixel.b! - lane0.b)).toBeLessThan(30);
  }
});

test("keeps the graph visible on the selected row", async ({ page }) => {
  await page.emulateMedia({ colorScheme: "dark" });
  await openHistory(page, 200);
  await expect(selected(page)).toHaveAttribute("aria-posinset", "1");

  // What is on screen, not what is on the canvas: a row's background must not cover the dot.
  const box = (await page.locator("canvas.graph").boundingBox())!;
  const shot = await page.screenshot({ clip: { x: box.x + 12, y: box.y + 10, width: 8, height: 8 } });
  const brightest = await page.evaluate(async (png) => {
    const image = new Image();
    image.src = `data:image/png;base64,${png}`;
    await image.decode();
    const canvas = new OffscreenCanvas(image.width, image.height);
    const ctx = canvas.getContext("2d")!;
    ctx.drawImage(image, 0, 0);
    const data = ctx.getImageData(0, 0, image.width, image.height).data;
    let max = 0;
    for (let i = 0; i < data.length; i += 4) max = Math.max(max, data[i + 1]!);
    return max;
  }, shot.toString("base64"));

  // The dark theme's first lane is #8fb8a0; the selected row's background is far darker.
  expect(brightest).toBeGreaterThan(0x90);
});

test("scrolls through 50,000 commits without dropping frames", async ({ page }) => {
  await openHistory(page);

  const frames = await page.evaluate(async () => {
    const scroller = document.querySelector<HTMLElement>(".history-scroll")!;
    const nextFrame = () => new Promise<number>((resolve) => requestAnimationFrame(resolve));
    const times: number[] = [];
    let last = await nextFrame();
    for (let i = 0; i < 150; i++) {
      scroller.scrollTop += 280;
      const now = await nextFrame();
      times.push(now - last);
      last = now;
    }
    return times.sort((a, b) => a - b);
  });

  // 95% of frames within 50 ms, so scrolling keeps up even on a slow CI runner; locally they take about 16 ms.
  expect(frames[Math.floor(frames.length * 0.95)]).toBeLessThan(50);
  await expect(list(page).getByRole("option").first()).toBeVisible();
});

test("shows the rows at the end after jumping there", async ({ page }) => {
  await openHistory(page);

  await list(page).evaluate((el) => (el.scrollTop = el.scrollHeight));

  const last = list(page).getByRole("option").last();
  await expect(last).toHaveAttribute("aria-posinset", String(BigHistory));
  await expect(last).toBeVisible();
});

test("works from the keyboard and the details follow the selection", async ({ page }) => {
  await openHistory(page, 2_000);
  await list(page).focus();

  await page.keyboard.press("ArrowDown");
  await page.keyboard.press("ArrowDown");
  await expect(selected(page)).toHaveAttribute("aria-posinset", "3");
  await page.keyboard.press("PageDown");
  await expect(selected(page)).toBeInViewport();
  await page.keyboard.press("End");
  await expect(selected(page)).toHaveAttribute("aria-posinset", "2000");
  await expect(selected(page)).toBeInViewport();
  await expect(panel(page)).toContainText("None, this is a root commit");
  await page.keyboard.press("Home");
  await expect(selected(page)).toHaveAttribute("aria-posinset", "1");

  await page.keyboard.press("Enter");
  await expect(panel(page)).toBeFocused();
});

test("selects a commit by clicking it, and goes to its parent from the details", async ({ page }) => {
  await openHistory(page, 2_000);

  await list(page).getByRole("option").nth(4).click();
  await expect(selected(page)).toHaveAttribute("aria-posinset", "5");
  const subject = await selected(page).locator(".subject").textContent();
  await expect(subjectOf(page)).toHaveText(subject!);

  await panel(page).getByRole("button").first().click();

  await expect(selected(page)).not.toHaveAttribute("aria-posinset", "5");
  await expect(subjectOf(page)).not.toHaveText(subject!);
});
