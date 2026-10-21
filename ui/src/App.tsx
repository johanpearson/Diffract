import { useEffect, useState } from "react";
import type { AppInfo } from "./protocol";
import type { RpcClient } from "./rpc";

type State = { kind: "loading" } | { kind: "ready"; info: AppInfo } | { kind: "failed"; message: string };

export function App({ client }: { client: RpcClient }) {
  const [state, setState] = useState<State>({ kind: "loading" });

  useEffect(() => {
    let current = true;
    client
      .call("app.info", undefined)
      .then((info) => current && setState({ kind: "ready", info }))
      .catch((error: unknown) => current && setState({ kind: "failed", message: error instanceof Error ? error.message : String(error) }));
    return () => {
      current = false;
    };
  }, [client]);

  return (
    <main className="app">
      <h1>Diffract</h1>
      {state.kind === "loading" && <p>Starting…</p>}
      {state.kind === "failed" && (
        <p role="alert">The sidecar did not answer: {state.message}</p>
      )}
      {state.kind === "ready" && (
        <dl>
          <dt>Sidecar</dt>
          <dd>{state.info.sidecarVersion}</dd>
          <dt>Git</dt>
          <dd>{state.info.gitVersion ?? "not found"}</dd>
          {state.info.gitProblem && <dd role="alert">{state.info.gitProblem}</dd>}
        </dl>
      )}
    </main>
  );
}
