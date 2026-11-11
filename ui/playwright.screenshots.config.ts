import { defineConfig } from "@playwright/test";
import base from "./playwright.config";

// Only the README screenshots, which the normal e2e run does not match. Run with `npm run screenshots`.
export default defineConfig({ ...base, testDir: "e2e/screenshots", testMatch: "*.shots.ts", reporter: "list", workers: 1 });
