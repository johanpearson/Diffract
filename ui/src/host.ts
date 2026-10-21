import type { Transport } from "./rpc";

/** What the Rust core says about the sidecar process. */
export type SidecarEvent =
  /** It stopped unexpectedly and was started again. Requests that were waiting are lost, and so is its state. */
  | { kind: "restarted" }
  /** It stopped too often, so the core gave up restarting it. `restartSidecar` tries again. */
  | { kind: "stopped" };

/** Everything the UI needs from its surroundings: the Tauri app, or a fake one in tests. */
export interface AppHost {
  transport: Transport;
  /** A folder dialog. Null when cancelled. */
  pickFolder(): Promise<string | null>;
  onSidecarEvent(handler: (event: SidecarEvent) => void): () => void;
  restartSidecar(): Promise<void>;
  /** Small per-user settings, such as the last opened repo. Reads may return null at any time. */
  storage: { get(key: string): string | null; set(key: string, value: string): void };
}

/** localStorage, but never throws: settings are a convenience, so a failure just means "not remembered". */
export const browserStorage: AppHost["storage"] = {
  get(key) {
    try {
      return localStorage.getItem(key);
    } catch {
      return null;
    }
  },
  set(key, value) {
    try {
      localStorage.setItem(key, value);
    } catch {
      // Not remembered this time.
    }
  },
};
