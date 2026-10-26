import type { GitUpdate } from "../model/useGitUpdate";
import type { RepoSession } from "../model/useRepoSession";
import type { Terminals } from "../model/useTerminals";
import type { RepoWork } from "../model/useRepoWork";
import type { Screen } from "../model/session";
import type { RepoState } from "../protocol";
import { ErrorPanel } from "./ErrorPanel";
import { LeftoversView, type Filter } from "./LeftoversView";
import { Sidebar } from "./Sidebar";
import { Splitter, useStoredSize, type SizeSpec } from "./Splitter";
import { tabId, Tabs, type View } from "./Tabs";
import type { SwitchTo } from "../history/commitMenu";
import { HistoryView } from "../history/HistoryView";
import { ChangesView } from "../changes/ChangesView";
import { useCleanup } from "../cleanup/useCleanup";

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
  /** History, its refs and the branch actions, shared by the sidebar and History. */
  work: RepoWork;
  /** Shows History with this commit selected. */
  onReveal(commitId: string): void;
}

export const SidebarWidth: SizeSpec = { key: "diffract.size.sidebar", initial: 250, min: 180, max: 480 };

/** The sidebar, only once a repo is open, with a bar to change its width. */
export function RepoSidebar({ repo, terminals, onOpenFolder, work, onReveal, onView }: ScreenProps) {
  const storage = repo.session.host.storage;
  const [width, setWidth] = useStoredSize(storage, SidebarWidth);
  if (repo.screen.kind !== "ready") return null;
  const refs = work.history.refs.refs;
  const branches = { current: work.current.branch, localBranches: new Set(refs.filter((r) => r.kind === "Branch").map((r) => r.name)) };
  const commands = {
    reveal: onReveal,
    // Shown first, so the outcome appears in History, next to the commit it is about.
    switchTo: (target: SwitchTo, commitId: string) => {
      onReveal(commitId);
      work.actions.switchTo(target);
    },
    copy: work.actions.copy,
  };
  return (
    <>
      <div className="sidebar-frame" style={{ width }}>
        <Sidebar
          state={repo.screen.state}
          terminals={terminals.list}
          terminal={terminals.current}
          onOpenFolder={onOpenFolder}
          onOpenTerminal={terminals.open}
          storage={storage}
          refs={refs}
          branches={branches}
          commands={commands}
          onShowLeftovers={() => onView("leftovers")}
        />
      </div>
      <Splitter label="Resize sidebar" orientation="vertical" spec={SidebarWidth} size={width} onSize={setWidth} growsForward />
    </>
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
        <ViewOf {...props} state={state} />
      </div>
    </>
  );
}

function ViewOf(props: ScreenProps & { state: RepoState }) {
  const { repo, view, state } = props;
  const { client, host } = repo.session;
  switch (view) {
    case "history":
      return <HistoryView key={state.repoId} client={client} state={state} now={props.now} work={props.work} storage={host.storage} />;
    case "changes":
      return <ChangesView key={state.repoId} client={client} state={state} openedPath={repo.session.repo.current?.path ?? state.repoRoot} storage={host.storage} />;
    case "leftovers":
      return <Leftovers {...props} />;
  }
}

function refreshBlocked(updatingGit: boolean, scanning: boolean): string | null {
  if (updatingGit) return "Wait until Git for Windows is updated";
  return scanning ? "Wait until the scan has finished" : null;
}

function Leftovers(props: ScreenProps) {
  if (props.repo.screen.kind !== "ready") return null;
  return <LeftoversScreen key={props.repo.screen.state.repoId} {...props} state={props.repo.screen.state} />;
}

/** One repo's leftovers. Its key is the repo, so a selection or a result never carries over to another repo. */
function LeftoversScreen({ repo, git, terminals, filter, onFilter, onOpenFolder, now, state }: ScreenProps & { state: RepoState }) {
  const cleanup = useCleanup(repo.session.client, state.repoId, repo.rows, repo.session.showNotice);
  return (
    <LeftoversView
      state={state}
      cleanup={cleanup}
      rows={repo.rows}
      filter={filter}
      onFilter={onFilter}
      onOpenFolder={onOpenFolder}
      onOpenTerminal={terminals.open}
      terminalName={terminals.current?.name ?? null}
      onRefresh={() => void repo.refresh()}
      refreshing={repo.refreshing}
      refreshBlocked={refreshBlocked(git.running, state.scanning)}
      now={now}
    />
  );
}
