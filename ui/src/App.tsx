import { useCallback, useEffect, useRef, useState } from "react";
import { ErrorPanel } from "./components/ErrorPanel";
import { GitUpdateBar } from "./components/GitUpdateBar";
import { GitUpdateDialog } from "./components/GitUpdateDialog";
import { LeftoversView, type Filter } from "./components/LeftoversView";
import { Sidebar } from "./components/Sidebar";
import type { AppHost } from "./host";
import { folderName } from "./model/format";
import { FlashMs, initialRows, settleRows, updateRows, type Row } from "./model/rows";
import { useGitUpdate } from "./model/useGitUpdate";
import { terminalKinds, type AppInfo, type RepoState, type TerminalInfo, type TerminalKind } from "./protocol";
import { ErrorCodes, RpcError, type RpcClient } from "./rpc";

/** The storage key for the last repo that opened, which opens again on start. */
export const LastRepoKey = "diffract.lastRepo";

/** The storage key for the terminal chosen last, which "Open" uses until another is chosen. */
export const TerminalKey = "diffract.terminal";

type Screen =
  | { kind: "starting" }
  | { kind: "gitProblem"; problem: string }
  | { kind: "welcome" }
  | { kind: "opening"; path: string }
  | { kind: "ready"; state: RepoState }
  /** `path` is the repo to try again, or null when the sidecar itself is the problem. */
  | { kind: "failed"; title: string; message: string; path: string | null };

interface Props {
  host: AppHost;
  client: RpcClient;
  /** The clock for "5 min ago". Tests pass a fixed one. */
  clock?: () => Date;
}

const systemClock = () => new Date();

export function App({ host, client, clock = systemClock }: Props) {
  const [info, setInfo] = useState<AppInfo | null>(null);
  const [screen, setScreen] = useState<Screen>({ kind: "starting" });
  const [rows, setRows] = useState<Row[]>([]);
  const [filter, setFilter] = useState<Filter>("all");
  const [notice, setNotice] = useState<string | null>(null);
  const [refreshing, setRefreshing] = useState(false);
  const [starts, setStarts] = useState(0);
  const [terminals, setTerminals] = useState<TerminalInfo[]>([]);
  const git = useGitUpdate(client, host, clock);
  const { checkForUpdate } = git;
  // The available version the user said "Not now" to. A newer one is offered again.
  const [declined, setDeclined] = useState<string | null>(null);
  const [terminalId, setTerminalId] = useState<TerminalKind | null>(() => {
    const stored = host.storage.get(TerminalKey);
    return terminalKinds.find((k) => k === stored) ?? null;
  });
  // The chosen terminal if it is still installed, otherwise the sidecar's default, which it lists first.
  const terminal = terminals.find((t) => t.id === terminalId) ?? terminals[0] ?? null;
  const now = useNow(clock);

  // The repo on screen. Refs, because notifications and sidecar events arrive outside React's render cycle.
  const repo = useRef<{ id: string; path: string } | null>(null);
  // Only the newest open may change the screen; a slower earlier one is ignored.
  const openSeq = useRef(0);

  const open = useCallback(
    async (path: string) => {
      const seq = ++openSeq.current;
      const previous = repo.current;
      repo.current = null;
      if (previous) client.call("repo.close", { repoId: previous.id }).catch(() => {});
      setScreen({ kind: "opening", path });
      try {
        const state = await client.call("repo.open", { path });
        if (seq !== openSeq.current) return;
        repo.current = { id: state.repoId, path };
        host.storage.set(LastRepoKey, path);
        setRows(initialRows(state.leftovers));
        setScreen({ kind: "ready", state });
      } catch (error) {
        if (seq !== openSeq.current) return;
        setScreen({ kind: "failed", title: `Could not open ${folderName(path)}`, message: messageOf(error), path });
      }
    },
    [client, host],
  );

  const pickAndOpen = useCallback(async () => {
    const path = await host.pickFolder();
    if (path) await open(path);
  }, [host, open]);

  // Start: check git, then open the last repo again.
  useEffect(() => {
    let current = true;
    setScreen({ kind: "starting" });
    client
      .call("app.info", undefined)
      .then((appInfo) => {
        if (!current) return;
        setInfo(appInfo);
        // Not knowing the terminals only means "Open terminal" uses the default one.
        client.call("system.listTerminals", undefined).then((list) => current && setTerminals(list), () => {});
        // A git that is too old is the only thing on screen, so its update is checked at once. Otherwise winget is
        // asked only once the repo is open, at most once a day: it starts processes, which slows opening the repo.
        if (!appInfo.gitSupported) {
          void checkForUpdate(appInfo.gitVersion, true);
          return setScreen({ kind: "gitProblem", problem: appInfo.gitProblem ?? "This git cannot be used." });
        }
        const last = host.storage.get(LastRepoKey);
        if (last)
          void open(last).then(() => {
            if (current) void checkForUpdate(appInfo.gitVersion, false);
          });
        else {
          setScreen({ kind: "welcome" });
          void checkForUpdate(appInfo.gitVersion, false);
        }
      })
      .catch((error: unknown) => {
        if (current) setScreen({ kind: "failed", title: "The sidecar did not answer", message: messageOf(error), path: null });
      });
    return () => {
      current = false;
    };
  }, [client, host, open, starts, checkForUpdate]);

  // Live updates for the repo on screen.
  useEffect(
    () =>
      client.onNotification("repo.changed", ({ state }) => {
        if (state.repoId !== repo.current?.id) return;
        setRows((previous) => updateRows(previous, state.leftovers));
        setScreen({ kind: "ready", state });
      }),
    [client],
  );

  // Highlights fade, and removed rows go, a moment after the last update.
  useEffect(() => {
    if (!rows.some((row) => row.flash)) return;
    const timer = setTimeout(() => setRows(settleRows), FlashMs);
    return () => clearTimeout(timer);
  }, [rows]);

  // The sidecar lost its state when it stopped: open the repo again, or start over.
  useEffect(
    () =>
      host.onSidecarEvent((event) => {
        client.failAll("The sidecar stopped.");
        const path = repo.current?.path ?? null;
        repo.current = null;
        if (event.kind === "stopped") {
          openSeq.current++;
          setScreen({
            kind: "failed",
            title: "The sidecar keeps stopping",
            message: "Diffract's background process stopped several times in a row, so Diffract stopped restarting it.",
            path: null,
          });
          return;
        }
        setNotice("Diffract's background process stopped unexpectedly and was restarted.");
        if (path) void open(path);
        else setStarts((n) => n + 1);
      }),
    [client, host, open],
  );

  const retry = useCallback(
    async (path: string | null) => {
      if (path) return open(path);
      await host.restartSidecar().catch(() => {});
      setStarts((n) => n + 1);
    },
    [host, open],
  );

  /** `includeUnreachable` also runs `git fsck`, the slowest call by far. */
  const refresh = useCallback(async (includeUnreachable = true) => {
    const current = repo.current;
    if (!current) return;
    setRefreshing(true);
    try {
      const state = await client.call("repo.refresh", { repoId: current.id, includeUnreachable });
      if (state.repoId !== repo.current?.id) return;
      setRows((previous) => updateRows(previous, state.leftovers));
      setScreen({ kind: "ready", state });
    } catch (error) {
      if (!(error instanceof RpcError && error.code === ErrorCodes.RequestCancelled)) setNotice(`Refresh failed: ${messageOf(error)}`);
    } finally {
      setRefreshing(false);
    }
  }, [client]);

  /**
   * After an update, whatever its outcome: the git now installed, a new update check, and the repo refreshed,
   * since live refresh was paused meanwhile. A git that was too old and now works starts the app over.
   */
  const afterUpdate = useCallback(async () => {
    try {
      const appInfo = await client.call("app.info", undefined);
      const wasSupported = info?.gitSupported ?? false;
      setInfo(appInfo);
      void checkForUpdate(appInfo.gitVersion, true);
      if (appInfo.gitSupported && !wasSupported) setStarts((n) => n + 1);
      else if (appInfo.gitSupported) await refresh(false);
    } catch (error) {
      setNotice(`Could not check git after the update: ${messageOf(error)}`);
    }
  }, [client, info, checkForUpdate, refresh]);

  const checkNow = useCallback(async () => {
    const answer = await checkForUpdate(info?.gitVersion ?? null, true);
    if (!answer) setNotice("Could not check for a newer Git for Windows.");
    else if (answer.status === "UpToDate") setNotice(`Git for Windows ${answer.installedVersion ?? ""} is up to date.`);
    else if (answer.status === "Unknown") setNotice(answer.problem ?? "Could not check for a newer Git for Windows.");
    else setDeclined(null);
  }, [checkForUpdate, info]);

  const openDownloadPage = useCallback(() => {
    client.call("git.openDownloadPage", undefined).catch((error: unknown) => setNotice(messageOf(error)));
  }, [client]);

  /** The newer Git for Windows that winget can install, or null. */
  const offered = git.check?.status === "UpdateAvailable" ? git.check.availableVersion : null;

  const openFolder = useCallback(
    (path: string) => {
      client.call("system.openFolder", { path }).catch((error: unknown) => setNotice(messageOf(error)));
    },
    [client],
  );

  const openTerminal = useCallback(
    (path: string, kind?: TerminalKind) => {
      if (kind) {
        setTerminalId(kind);
        host.storage.set(TerminalKey, kind);
      }
      const chosen = kind ?? terminal?.id;
      client
        .call("system.openTerminal", chosen ? { path, terminal: chosen } : { path })
        .catch((error: unknown) => setNotice(`Could not open a terminal: ${messageOf(error)}`));
    },
    [client, host, terminal],
  );

  return (
    <div className="app">
      <header className="topbar">
        <span className="brand">Diffract</span>
        <span className="grow" />
        {info && (
          <span className="versions">
            git {info.gitVersion ?? "not found"} · sidecar {info.sidecarVersion}
          </span>
        )}
        {info && (
          <button className="abtn small" onClick={() => void checkNow()} disabled={git.checking || git.running}>
            {git.checking ? "Checking…" : "Check for git updates"}
          </button>
        )}
        <button
          className="abtn"
          onClick={() => void pickAndOpen()}
          disabled={screen.kind === "starting" || screen.kind === "gitProblem" || git.running}
          title={git.running ? "Wait until Git for Windows is updated" : undefined}
        >
          Open repo…
        </button>
      </header>

      {git.run && (
        <GitUpdateBar
          run={git.run}
          clock={clock}
          onTryAgain={() => git.askToUpdate(git.run!.version)}
          onDownloadPage={openDownloadPage}
          onDismiss={git.dismissRun}
        />
      )}

      {offered && info?.gitSupported && declined !== offered && !git.run && (
        <div className="notice quiet" role="status">
          <span>
            Git for Windows {offered} is available. You have {git.check?.installedVersion}.
          </span>
          <button className="abtn" onClick={() => git.askToUpdate(offered)}>
            Update…
          </button>
          <button className="abtn" onClick={() => setDeclined(offered)}>
            Not now
          </button>
        </div>
      )}

      {notice && (
        <div className="notice" role="status">
          <span>{notice}</span>
          <button className="abtn" onClick={() => setNotice(null)}>
            Dismiss
          </button>
        </div>
      )}

      <div className="body">
        {screen.kind === "ready" && <Sidebar state={screen.state} terminals={terminals} terminal={terminal} onOpenFolder={openFolder} onOpenTerminal={openTerminal} />}
        <main className="main">
          {screen.kind === "starting" && <p className="hint">Starting…</p>}
          {screen.kind === "opening" && <p className="hint">Opening {screen.path}…</p>}
          {screen.kind === "welcome" && (
            <div className="panel">
              <h1>Open a repo</h1>
              <p>Diffract shows the repo's worktrees and what agents left behind, and keeps it current while they work.</p>
              <div className="panel-actions">
                <button className="abtn primary" onClick={() => void pickAndOpen()}>
                  Open repo…
                </button>
              </div>
            </div>
          )}
          {screen.kind === "gitProblem" && (
            <ErrorPanel
              title="Diffract cannot use this git"
              message={screen.problem}
              detail={git.check?.status === "Unknown" ? git.check.problem : null}
              actions={[
                offered
                  ? { label: `Update to ${offered}…`, run: () => git.askToUpdate(offered) }
                  : { label: "Open download page", run: openDownloadPage },
                { label: "Check again", run: () => setStarts((n) => n + 1) },
              ]}
            />
          )}
          {screen.kind === "failed" && (
            <ErrorPanel
              title={screen.title}
              message={screen.message}
              actions={[
                { label: "Retry", run: () => void retry(screen.path) },
                ...(screen.path ? [{ label: "Open another folder", run: () => void pickAndOpen() }] : []),
              ]}
            />
          )}
          {screen.kind === "ready" && (
            <LeftoversView
              state={screen.state}
              rows={rows}
              filter={filter}
              onFilter={setFilter}
              onOpenFolder={openFolder}
              onOpenTerminal={openTerminal}
              terminalName={terminal?.name ?? null}
              onRefresh={() => void refresh()}
              refreshing={refreshing}
              refreshBlocked={git.running ? "Wait until Git for Windows is updated" : null}
              now={now}
            />
          )}
        </main>
      </div>

      {git.confirm && (
        <GitUpdateDialog
          installed={info?.gitVersion ?? null}
          version={git.confirm.version}
          inUse={git.confirm.inUse}
          onCheckAgain={git.findUsers}
          onConfirm={() => void git.update(git.confirm!.version, () => void afterUpdate())}
          onCancel={git.cancelConfirm}
        />
      )}
    </div>
  );
}

/** The current time, updated every 30 seconds so "5 min ago" stays true. */
function useNow(clock: () => Date): Date {
  const [now, setNow] = useState(clock);
  useEffect(() => {
    const timer = setInterval(() => setNow(clock()), 30_000);
    return () => clearInterval(timer);
  }, [clock]);
  return now;
}

function messageOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
