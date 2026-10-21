import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import type { Transport } from "./rpc";

/**
 * Relays messages through the Rust core, which writes them to the sidecar's stdin and emits every
 * line the sidecar prints as a "sidecar-message" event. Resolves once the listener is in place,
 * so no response can arrive before anyone listens for it.
 */
export async function createTauriTransport(): Promise<Transport> {
  const handlers = new Set<(message: string) => void>();
  await listen<string>("sidecar-message", (event) => {
    for (const handler of handlers) handler(event.payload);
  });

  return {
    send: (message) => invoke("sidecar_send", { message }),
    onMessage(handler) {
      handlers.add(handler);
      return () => handlers.delete(handler);
    },
  };
}
