import type { GitUpdate } from "../model/useGitUpdate";
import type { RepoSession } from "../model/useRepoSession";
import type { Terminals } from "../model/useTerminals";
import type { Screen } from "../model/session";
import { ErrorPanel } from "./ErrorPanel";
import { LeftoversView, type Filter } from "./LeftoversView";
import { Sidebar } from "./Sidebar";
import { tabId, Tabs, type View } from "./Tabs";
import { HistoryView } from "../history/HistoryView";

/** What the screens need besides the screen itself. */
export interface ScreenProps {
  repo: RepoSession;
  git: GitUpdate;
  terminals: Terminals;
  /** The newer Git for Windows that winget can install, or null. */
  offered: string | null;
  filter: Filter;
  onFilter(filter: Filter): void;
  view: View;
  onView(view: View): void;
  onOpenFolder(path: string): void;
  onDownloadPage(): void;
  now: Date;
}

/** The sidebar, only once a repo is open. */
export function RepoSidebar({ repo, terminals, onOpenFolder }: ScreenProps) {
  if (repo.screen.kind !== "ready") return null;
  return (
    <Sidebar state={repo.screen.state} terminals={terminals.list} terminal={terminals.current} onOpenFolder={onOpenFolder} onOpenTerminal={terminals.open} />
  );
}

/** The main area for each screen. */
export function MainScreen(props: ScreenProps) {
  const screen = props.repo.screen;
  switch (screen.kind) {
    case "starting":
      return <p className="hint">Starting…</p>;
    case "opening":
      return <p className="hint">Opening {screen.path}…</p>;
    case "welcome":
      return <Welcome onOpen={props.repo.pickAndOpen} />;
    case "gitProblem":
      return <GitProblem problem={screen.problem} {...props} />;
    case "failed":
      return <Failed screen={screen} repo={props.repo} />;
    case "ready":
      return <Ready {...props} />;
  }
}

function Welcome({ onOpen }: { onOpen(): Promise<void> }) {
  return (
    <div className="panel">
      <h1>Open a repo</h1>
      <p>Diffract shows the repo's worktrees and what agents left behind, and keeps it current while they work.</p>
      <div className="panel-actions">
        <button className="abtn primary" onClick={() => void onOpen()}>
          Open repo…
        </button>
      </div>
    </div>
  );
}

function GitProblem({ problem, git, offered, repo, onDownloadPage }: ScreenProps & { problem: string }) {
  return (
    <ErrorPanel
      title="Diffract cannot use this git"
      message={problem}
      detail={git.check?.status === "Unknown" ? git.check.problem : null}
      actions={[
        offered ? { label: `Update to ${offered}…`, run: () => git.askToUpdate(offered) } : { label: "Open download page", run: onDownloadPage },
        { label: "Check again", run: repo.restart },
      ]}
    />
  );
}

function Failed({ screen, repo }: { screen: Extract<Screen, { kind: "failed" }>; repo: RepoSession }) {
  return (
    <ErrorPanel
      title={screen.title}
      message={screen.message}
      actions={[
        { label: "Retry", run: () => void repo.retry(screen.path) },
        ...(screen.path ? [{ label: "Open another folder", run: () => void repo.pickAndOpen() }] : []),
      ]}
    />
  );
}

function Ready(props: ScreenProps) {
  const { repo, view, onView } = props;
  if (repo.screen.kind !== "ready") return null;
  const state = repo.screen.state;
  return (
    <>
      <Tabs view={view} onView={onView} />
      <div className={`tab-panel ${view}`} role="tabpanel" aria-labelledby={tabId(view)}>
        {view === "history" ? <HistoryView key={state.repoId} client={repo.session.client} state={state} now={props.now} /> : <Leftovers {...props} />}
      </div>
    </>
  );
}

function Leftovers({ repo, git, terminals, filter, onFilter, onOpenFolder, now }: ScreenProps) {
  if (repo.screen.kind !== "ready") return null;
  return (
    <LeftoversView
      state={repo.screen.state}
      rows={repo.rows}
      filter={filter}
      onFilter={onFilter}
      onOpenFolder={onOpenFolder}
      onOpenTerminal={terminals.open}
      terminalName={terminals.current?.name ?? null}
      onRefresh={() => void repo.refresh()}
      refreshing={repo.refreshing}
      refreshBlocked={git.running ? "Wait until Git for Windows is updated" : null}
      now={now}
    />
  );
}
