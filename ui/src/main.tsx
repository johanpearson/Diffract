import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { isTauri } from "@tauri-apps/api/core";
import { App } from "./App";
import type { AppHost } from "./host";
import { RpcClient } from "./rpc";
import "./styles.css";

const root = createRoot(document.getElementById("root")!);

// Vite replaces MODE when it builds, so the app's bundle never contains the fake host and the e2e bundle never needs Tauri.
async function createHost(): Promise<AppHost | null> {
  if (import.meta.env.MODE === "e2e") return (await import("./e2eHost")).createE2eHost();
  return isTauri() ? (await import("./tauriHost")).createTauriHost() : null;
}

const host = await createHost();
if (host) {
  root.render(
    <StrictMode>
      <App host={host} client={new RpcClient(host.transport)} />
    </StrictMode>,
  );
} else {
  root.render(<p className="hint">Diffract runs inside its desktop app. Start it with the Tauri CLI, see docs/setup.md.</p>);
}
