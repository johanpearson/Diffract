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

test("shows a file's diff side by side, and loads Monaco only then", async ({ page }) => {
  const monaco: string[] = [];
  page.on("request", (request) => {
    if (/editor\.api|monacoEditor|editor\.worker/.test(request.url())) monaco.push(request.url());
  });
  await openHistory(page, 200);
  const file = panel(page).getByRole("button", { name: /src\/cart\.cs/ });
  await expect(file).toBeVisible();
  expect(monaco).toEqual([]);

  await file.click();

  const diff = page.getByRole("region", { name: "Diff of src/cart.cs" });
  const original = diff.locator(".editor.original");
  const modified = diff.locator(".editor.modified");
  await expect(modified).toContainText("Lines.Sum(l => l.Price)");
  await expect(original).toContainText("Total => 0m");
  const [left, right] = [(await original.boundingBox())!, (await modified.boundingBox())!];
  expect(left.x + left.width).toBeLessThanOrEqual(right.x + 1);
  expect(monaco.length).toBeGreaterThan(0);

  await page.keyboard.press("Escape");
  await expect(diff).toBeHidden();
});

test("the diff follows the Windows theme", async ({ page }) => {
  for (const colorScheme of ["dark", "light"] as const) {
    await page.emulateMedia({ colorScheme });
    await openHistory(page, 50);
    await panel(page).getByRole("button", { name: /src\/cart\.cs/ }).click();

    const editor = page.locator(".monaco-diff-editor .editor.modified .monaco-editor");
    await expect(editor).toBeVisible();
    await expect(editor).toHaveClass(colorScheme === "dark" ? /vs-dark/ : /\bvs\b/);
  }
});

test("finds commits from the find box and jumps to them", async ({ page }) => {
  await openHistory(page);

  await page.keyboard.press("Control+f");
  const find = page.getByRole("searchbox", { name: "Find commits" });
  await expect(find).toBeFocused();
  await find.fill("Change 25000 on");
  await find.press("Enter");

  await expect(selected(page)).toContainText("Change 25000 on main");
  await expect(selected(page)).toBeInViewport();
  await expect(page.getByText("1 of 1")).toBeVisible();
});

test("switches the diff between side by side and inline, and remembers it", async ({ page }) => {
  await openHistory(page, 50);
  await panel(page).getByRole("button", { name: /src\/cart\.cs/ }).click();
  const diff = page.getByRole("region", { name: "Diff of src/cart.cs" });
  await expect(diff.locator(".monaco-diff-editor")).toHaveClass(/side-by-side/);

  await diff.getByRole("button", { name: "Inline" }).click();

  await expect(diff.locator(".monaco-diff-editor")).not.toHaveClass(/side-by-side/);
  await expect(diff.locator(".editor.modified")).toContainText("Total => 0m");
  expect(await page.evaluate(() => window.diffractTest.storage.get("diffract.diffLayout"))).toBe("inline");
});

test("shows an added file on its own, without a diff", async ({ page }) => {
  await openHistory(page, 50);

  await panel(page).getByRole("button", { name: /docs\/changes\.md/ }).click();

  const view = page.getByRole("region", { name: "Diff of docs/changes.md" });
  await expect(view).toContainText("The cart now sums its lines.");
  await expect(view.locator(".monaco-diff-editor")).toHaveCount(0);
  await expect(view.getByRole("group", { name: "Diff layout" })).toHaveCount(0);
});

test("scrolls to the first change, and moves between changes", async ({ page }) => {
  await openHistory(page, 50);

  await panel(page).getByRole("button", { name: /src\/long\.cs/ }).click();

  const diff = page.getByRole("region", { name: "Diff of src/long.cs" });
  await expect(diff.getByText("// the new line 301")).toBeInViewport();
  await expect(diff.getByText("// line 1", { exact: true })).toHaveCount(0);
  await diff.getByRole("button", { name: "Previous change" }).click();
  await expect(diff.getByText("// the new line 301")).toBeInViewport();
});

test("resizes the panels by dragging, and remembers the sizes", async ({ page }) => {
  await openHistory(page, 50);
  await panel(page).getByRole("button", { name: /src\/cart\.cs/ }).click();
  const before = (await panel(page).boundingBox())!.width;
  const diffBefore = (await page.locator(".diff-frame").boundingBox())!.height;

  await drag(page, page.getByRole("separator", { name: "Resize commit details" }), -100, 0);
  await drag(page, page.getByRole("separator", { name: "Resize diff" }), 0, 60);
  await drag(page, page.getByRole("separator", { name: "Resize sidebar" }), 40, 0);

  expect((await panel(page).boundingBox())!.width).toBeCloseTo(before + 100, -1);
  expect((await page.locator(".diff-frame").boundingBox())!.height).toBeCloseTo(diffBefore - 60, -1);
  expect((await page.locator(".sidebar-frame").boundingBox())!.width).toBeCloseTo(290, -1);
  const stored = await page.evaluate(() => [...window.diffractTest.storage.keys()].filter((k) => k.startsWith("diffract.size.")).sort());
  expect(stored).toEqual(["diffract.size.details", "diffract.size.diff", "diffract.size.sidebar"]);
});

async function drag(page: Page, handle: ReturnType<Page["getByRole"]>, dx: number, dy: number) {
  const box = (await handle.boundingBox())!;
  const [x, y] = [box.x + box.width / 2, box.y + box.height / 2];
  await page.mouse.move(x, y);
  await page.mouse.down();
  await page.mouse.move(x + dx, y + dy, { steps: 5 });
  await page.mouse.up();
}

test("finds a commit by a tag or branch on it", async ({ page }) => {
  await openHistory(page);
  const find = page.getByRole("searchbox", { name: "Find commits" });

  await find.fill("v1.0");
  await find.press("Enter");

  await expect(selected(page)).toHaveAttribute("aria-posinset", String(BigHistory));
  await expect(selected(page)).toContainText("v1.0");
  await expect(selected(page)).toBeInViewport();
});

test("opens on History and shows it before the leftover scan has finished", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Open a repo" })).toBeVisible();
  await page.evaluate((path) => {
    window.diffractTest.nextFolder = path;
    window.diffractTest.sidecar.scanOnOpen = true;
    window.diffractTest.sidecar.useGeneratedHistory(500);
  }, repoRoot);
  await page.getByRole("button", { name: "Open repo…" }).first().click();

  await expect(page.getByRole("tab", { name: "History" })).toHaveAttribute("aria-selected", "true");
  await expect(list(page).getByRole("option").first()).toBeVisible();
  await page.getByRole("tab", { name: "Worktrees and agents" }).click();
  await expect(page.getByText("Looking for leftovers. History is ready to use meanwhile.")).toBeVisible();

  await page.evaluate(() => window.diffractTest.sidecar.finishScan("1"));

  await expect(page.getByRole("list", { name: "Leftovers" }).getByRole("listitem")).toHaveCount(3);
  await page.getByRole("tab", { name: "History" }).click();
  await expect(list(page).getByRole("option").first()).toBeVisible();
});
