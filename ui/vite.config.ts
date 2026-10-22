/// <reference types="vitest/config" />
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Tauri loads the dev server from a fixed port, so Vite must not pick another one.
export default defineConfig({
  plugins: [react()],
  clearScreen: false,
  server: { port: 5173, strictPort: true },
  // Monaco is one chunk of about 2.7 MB, loaded from disk only when the first diff opens.
  build: { target: "es2022", chunkSizeWarningLimit: 3000 },
  // No persistent module cache: it bakes the list of contract samples into the cached test file,
  // so a sample added later would go unchecked until the cache happened to be cleared.
  test: { environment: "jsdom", fsModuleCache: false, include: ["src/**/*.test.{ts,tsx}"] },
});
