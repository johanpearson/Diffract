import { CommandsProvider } from "../commands/Commands";
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

/** History as the app shows it, with the history and branch actions it shares with the sidebar, and the command window. */
export function HistoryHarness(props: Props) {
  return (
    <CommandsProvider client={props.client}>
      <History {...props} />
    </CommandsProvider>
  );
}

function History({ client, state, now, storage, openedPath = state.repoRoot }: Props) {
  const work = useRepoWork(client, state, openedPath, storage);
  return <HistoryView client={client} state={state} now={now} work={work} storage={storage} />;
}
