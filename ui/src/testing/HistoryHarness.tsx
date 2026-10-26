import type { AppHost } from "../host";
import { HistoryView } from "../history/HistoryView";
import { useRepoWork } from "../model/useRepoWork";
import type { RepoState } from "../protocol";
import type { RpcClient } from "../rpc";

interface Props {
  client: RpcClient;
  state: RepoState;
  now: Date;
  storage: AppHost["storage"];
  openedPath?: string;
}

/** History as the app shows it, with the history and branch actions it shares with the sidebar. */
export function HistoryHarness({ client, state, now, storage, openedPath = state.repoRoot }: Props) {
  const work = useRepoWork(client, state, openedPath, storage);
  return <HistoryView client={client} state={state} now={now} work={work} storage={storage} />;
}
