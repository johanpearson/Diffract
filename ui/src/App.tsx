import { useCallback, useState } from "react";
import { CommandsProvider } from "./commands/Commands";
import { GitUpdateConfirmDialog, GitUpdateOffer, GitUpdateOutcome } from "./components/GitUpdatePrompts";
import type { Filter } from "./components/LeftoversView";
import type { View } from "./components/Tabs";
import { Notice } from "./components/Notice";
import { MainScreen, RepoSidebar, type ScreenProps } from "./components/Screens";
import { TopBar } from "./components/TopBar";
import type { AppHost } from "./host";
import { messageOf } from "./model/format";
import { useAppStart } from "./model/useAppStart";
import { offeredVersion, useGitUpdate } from "./model/useGitUpdate";
import { useNow } from "./model/useNow";
import { useRepoWork } from "./model/useRepoWork";
import { useRepoEvents, useRepoSession } from "./model/useRepoSession";
import { useTerminals } from "./model/useTerminals";
import type { RpcClient } from "./rpc";

export { LastRepoKey } from "./model/session";
export { TerminalKey } from "./model/useTerminals";

interface Props {
  host: AppHost;
  client: RpcClient;
  /** The clock for "5 min ago". Tests pass a fixed one. */
  clock?: () => Date;
  /** The view a repo opens on. History, unless a test is about another view. */
  initialView?: View;
  /** How long a plain success stays in the command window. Tests shorten it. */
  autoCloseMs?: number;
}

const systemClock = () => new Date();

/** The app, with the command window every view runs git commands in. */
export function App({ autoCloseMs, ...props }: Props) {
  return (
    <CommandsProvider client={props.client} autoCloseMs={autoCloseMs}>
      <AppScreens {...props} />
    </CommandsProvider>
  );
}

function AppScreens({ host, client, clock = systemClock, initialView = "history" }: Omit<Props, "autoCloseMs">) {
  const [filter, setFilter] = useState<Filter>("all");
  const [view, setView] = useState<View>(initialView);
  const [notice, setNotice] = useState<string | null>(null);
  const git = useGitUpdate(client, host, clock);
  const terminals = useTerminals(client, host, setNotice);
  const now = useNow(clock);
  const repo = useRepoSession(client, host, setNotice);
  const { info, afterUpdate } = useAppStart(repo.session, repo.starts, { checkForUpdate: git.checkForUpdate, setTerminals: terminals.setList });
  useRepoEvents(repo.session, repo.rows);
  const ready = repo.screen.kind === "ready" ? repo.screen.state : null;
  const work = useRepoWork(client, ready, repo.session.repo.current?.path ?? ready?.repoRoot ?? "", host.storage);
  const { revealCommit } = work;
  const reveal = useCallback(
    (commitId: string) => {
      setView("history");
      revealCommit(commitId);
    },
    [revealCommit],
  );

  const openDownloadPage = useCallback(() => {
    client.call("git.openDownloadPage", undefined).catch((error: unknown) => setNotice(messageOf(error)));
  }, [client]);

  const openFolder = useCallback(
    (path: string) => {
      client.call("system.openFolder", { path }).catch((error: unknown) => setNotice(messageOf(error)));
    },
    [client],
  );

  const offered = offeredVersion(git.check);
  const screens: ScreenProps = { repo, git, terminals, offered, filter, onFilter: setFilter, view, onView: setView, onOpenFolder: openFolder, onDownloadPage: openDownloadPage, now, work, onReveal: reveal };

  return (
    <div className="app">
      <TopBar info={info} canOpen={repo.screen.kind !== "starting" && repo.screen.kind !== "gitProblem"} updating={git.running} onOpen={repo.pickAndOpen} />
      <GitUpdateOutcome git={git} offered={offered} onDownloadPage={openDownloadPage} />
      <GitUpdateOffer git={git} info={info} offered={offered} />
      <Notice message={notice} onDismiss={() => setNotice(null)} />
      <div className="body">
        <RepoSidebar {...screens} />
        <main className="main">
          <MainScreen {...screens} />
        </main>
      </div>
      <GitUpdateConfirmDialog git={git} info={info} onUpdated={afterUpdate} />
    </div>
  );
}
