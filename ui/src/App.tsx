import { useCallback, useState } from "react";
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
}

const systemClock = () => new Date();

export function App({ host, client, clock = systemClock }: Props) {
  const [filter, setFilter] = useState<Filter>("all");
  const [view, setView] = useState<View>("leftovers");
  const [notice, setNotice] = useState<string | null>(null);
  const git = useGitUpdate(client, host, clock);
  const terminals = useTerminals(client, host, setNotice);
  const now = useNow(clock);
  const repo = useRepoSession(client, host, setNotice);
  const { info, afterUpdate } = useAppStart(repo.session, repo.starts, { checkForUpdate: git.checkForUpdate, setTerminals: terminals.setList });
  useRepoEvents(repo.session, repo.rows);

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
  const screens: ScreenProps = { repo, git, terminals, offered, filter, onFilter: setFilter, view, onView: setView, onOpenFolder: openFolder, onDownloadPage: openDownloadPage, now };

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
