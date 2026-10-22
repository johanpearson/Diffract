import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { AppHost } from "../host";
import type { RpcClient } from "../rpc";
import { FlashMs, settleRows, type Row } from "./rows";
import { pickAndOpen, refreshRepo, restart, retry, showIfCurrent, sidecarStopped, type Screen, type Session, type ShowNotice } from "./session";

/** The repo on screen and what the screen shows, with the actions that change them. */
export function useRepoSession(client: RpcClient, host: AppHost, showNotice: ShowNotice) {
  const [screen, setScreen] = useState<Screen>({ kind: "starting" });
  const [rows, setRows] = useState<Row[]>([]);
  const [refreshing, setRefreshing] = useState(false);
  const [starts, setStarts] = useState(0);
  const repo = useRef<{ id: string; path: string } | null>(null);
  const openSeq = useRef(0);
  const session = useMemo<Session>(
    () => ({ client, host, showNotice, repo, openSeq, setScreen, setRows, setRefreshing, setStarts }),
    [client, host, showNotice],
  );

  const pick = useCallback(() => pickAndOpen(session), [session]);
  const retryOpen = useCallback((path: string | null) => retry(session, path), [session]);
  const refresh = useCallback((includeUnreachable = true) => refreshRepo(session, includeUnreachable), [session]);
  const startOver = useCallback(() => restart(session), [session]);

  return { session, screen, rows, refreshing, starts, pickAndOpen: pick, retry: retryOpen, refresh, restart: startOver };
}

export type RepoSession = ReturnType<typeof useRepoSession>;

/** Live updates for the repo on screen, highlights that fade, and the sidecar stopping. */
export function useRepoEvents(session: Session, rows: Row[]): void {
  const { client, host, setRows } = session;

  useEffect(() => client.onNotification("repo.changed", ({ state }) => showIfCurrent(session, state)), [client, session]);

  // Highlights fade, and removed rows go, a moment after the last update.
  useEffect(() => {
    if (!rows.some((row) => row.flash)) return;
    const timer = setTimeout(() => setRows(settleRows), FlashMs);
    return () => clearTimeout(timer);
  }, [rows, setRows]);

  useEffect(() => host.onSidecarEvent((event) => sidecarStopped(session, event)), [host, session]);
}
