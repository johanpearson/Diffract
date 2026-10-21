import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { open } from "@tauri-apps/plugin-dialog";
import { browserStorage, type AppHost, type SidecarEvent } from "./host";
import { createTauriTransport } from "./tauriTransport";

/** The real host: the Rust core relays the sidecar, shows dialogs and restarts the sidecar when it stops. */
export async function createTauriHost(): Promise<AppHost> {
  const transport = await createTauriTransport();
  const handlers = new Set<(event: SidecarEvent) => void>();
  const emit = (event: SidecarEvent) => handlers.forEach((handler) => handler(event));
  await listen("sidecar-restarted", () => emit({ kind: "restarted" }));
  await listen("sidecar-stopped", () => emit({ kind: "stopped" }));

  return {
    transport,
    async pickFolder() {
      const picked = await open({ directory: true, multiple: false, title: "Open a git repository" });
      return typeof picked === "string" ? picked : null;
    },
    onSidecarEvent(handler) {
      handlers.add(handler);
      return () => handlers.delete(handler);
    },
    restartSidecar: () => invoke("sidecar_restart"),
    storage: browserStorage,
  };
}
