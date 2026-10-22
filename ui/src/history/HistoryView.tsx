import { useMemo, useRef, useState } from "react";
import type { RepoState } from "../protocol";
import type { RpcClient } from "../rpc";
import { CommitPanel } from "./CommitPanel";
import { HistoryList } from "./HistoryList";
import { markersByCommit } from "./markers";
import { useCommitDetails } from "./useCommitDetails";
import { useHistory, type History } from "./useHistory";

interface Props {
  client: RpcClient;
  state: RepoState;
  now: Date;
}

/** The repo's history as a graph, with the selected commit's details beside it. */
export function HistoryView({ client, state, now }: Props) {
  const history = useHistory(client, state.repoId);
  const markers = useMemo(() => markersByCommit(history.refs, state.snapshot.worktrees), [history.refs, state.snapshot.worktrees]);
  const indexById = useMemo(() => new Map(history.commits.map((c, i) => [c.id, i])), [history.commits]);
  const [chosen, setChosen] = useState<string | null>(null);
  // The first commit until the user picks one.
  const selectedId = chosen ?? history.commits[0]?.id ?? null;
  const details = useCommitDetails(client, state.repoId, selectedId);
  const panel = useRef<HTMLElement>(null);

  return (
    <section className="view history" aria-labelledby="history-title">
      <div className="bar">
        <h1 id="history-title">History</h1>
        <span className="grow" />
        <span className="history-count" role="status">
          {countText(history)}
        </span>
      </div>
      {history.error && (
        <p className="empty" role="alert">
          Could not read the history: {history.error}
        </p>
      )}
      <div className="history-body">
        <HistoryList
          history={history}
          markers={markers}
          selected={selectedId === null ? -1 : (indexById.get(selectedId) ?? -1)}
          onSelect={(index) => setChosen(history.commits[index]?.id ?? null)}
          onOpen={() => panel.current?.focus()}
          now={now}
        />
        <CommitPanel
          ref={panel}
          state={details}
          markers={(selectedId && markers.get(selectedId)) || []}
          onSelect={setChosen}
          isLoaded={(id) => indexById.has(id)}
        />
      </div>
    </section>
  );
}

function countText({ commits, done }: History): string {
  const count = commits.length.toLocaleString("en-US");
  if (done) return commits.length === 1 ? "1 commit" : `${count} commits`;
  return commits.length === 0 ? "Reading history…" : `${count} commits so far…`;
}
