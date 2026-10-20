import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { isTauri } from "@tauri-apps/api/core";
import { App } from "./App";
import { RpcClient } from "./rpc";
import { createTauriTransport } from "./tauriTransport";
import "./styles.css";

const root = createRoot(document.getElementById("root")!);

if (isTauri()) {
  const client = new RpcClient(await createTauriTransport());
  root.render(
    <StrictMode>
      <App client={client} />
    </StrictMode>,
  );
} else {
  root.render(<p className="app">Diffract runs inside its desktop app. Start it with the Tauri CLI, see docs/setup.md.</p>);
}
