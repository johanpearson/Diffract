import { useCallback, useState } from "react";
import type { AppHost } from "../host";
import { terminalKinds, type TerminalInfo, type TerminalKind } from "../protocol";
import type { RpcClient } from "../rpc";
import { messageOf } from "./format";
import type { ShowNotice } from "./session";

/** The storage key for the terminal chosen last, which "Open" uses until another is chosen. */
export const TerminalKey = "diffract.terminal";

/** The installed terminals, the one "Open" uses, and opening one in a folder. */
export function useTerminals(client: RpcClient, host: AppHost, showNotice: ShowNotice) {
  const [list, setList] = useState<TerminalInfo[]>([]);
  const [chosen, setChosen] = useState<TerminalKind | null>(() => storedTerminal(host));
  // The chosen terminal if it is still installed, otherwise the sidecar's default, which it lists first.
  const current = list.find((t) => t.id === chosen) ?? list[0] ?? null;

  /** Without `kind`, the current terminal. With it, that one, which becomes the current one. */
  const open = useCallback(
    (path: string, kind?: TerminalKind) => {
      if (kind) {
        setChosen(kind);
        host.storage.set(TerminalKey, kind);
      }
      openTerminal(client, path, kind ?? current?.id, showNotice);
    },
    [client, host, current, showNotice],
  );

  return { list, setList, current, open };
}

export type Terminals = ReturnType<typeof useTerminals>;

function storedTerminal(host: AppHost): TerminalKind | null {
  const stored = host.storage.get(TerminalKey);
  return terminalKinds.find((k) => k === stored) ?? null;
}

function openTerminal(client: RpcClient, path: string, terminal: TerminalKind | undefined, showNotice: ShowNotice): void {
  client
    .call("system.openTerminal", terminal ? { path, terminal } : { path })
    .catch((error: unknown) => showNotice(`Could not open a terminal: ${messageOf(error)}`));
}
