import type { AppHost, SidecarEvent } from "./host";
import { FakeSidecar } from "./testing/fakeSidecar";

/** What the Playwright tests reach through `window.diffractTest`. */
export interface E2eControls {
  sidecar: FakeSidecar;
  /** The folder the next "Open repo…" dialog returns. Null acts as cancel. */
  nextFolder: string | null;
  /** As if the Rust core had restarted the sidecar, or given up on it. */
  sidecarEvent(event: SidecarEvent): void;
  /** The app's settings for this page load, such as the remembered answer from winget. */
  storage: Map<string, string>;
}

declare global {
  interface Window {
    diffractTest: E2eControls;
  }
}

/**
 * Only in the "e2e" build, which Playwright runs in a plain browser: the real UI against a fake sidecar.
 * The app's own build never includes this file.
 */
export function createE2eHost(): AppHost {
  const handlers = new Set<(event: SidecarEvent) => void>();
  const memory = new Map<string, string>();
  const controls: E2eControls = {
    sidecar: new FakeSidecar(),
    nextFolder: null,
    sidecarEvent: (event) => handlers.forEach((handler) => handler(event)),
    storage: memory,
  };
  window.diffractTest = controls;

  return {
    transport: controls.sidecar.transport,
    pickFolder: async () => controls.nextFolder,
    onSidecarEvent(handler) {
      handlers.add(handler);
      return () => handlers.delete(handler);
    },
    restartSidecar: async () => {},
    // In memory, so every page load starts with no last repo.
    storage: { get: (key) => memory.get(key) ?? null, set: (key, value) => void memory.set(key, value) },
  };
}
