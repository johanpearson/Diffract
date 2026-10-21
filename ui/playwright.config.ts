import { defineConfig, devices } from "@playwright/test";

// Playwright runs this file in Node. Declared here because @types/node is not an approved package.
declare const process: { env: Record<string, string | undefined> };

// The real UI, built in "e2e" mode so it talks to a fake sidecar in the page instead of Tauri.
// Chromium, because WebView2 is Chromium too.
export default defineConfig({
  testDir: "e2e",
  forbidOnly: !!process.env.CI,
  retries: 0,
  reporter: process.env.CI ? [["list"], ["html", { open: "never" }]] : "list",
  use: { baseURL: "http://localhost:4173", trace: "retain-on-failure" },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
  webServer: {
    command: "npm run build:e2e && npx vite preview --mode e2e --outDir dist-e2e --port 4173 --strictPort",
    url: "http://localhost:4173",
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
});
