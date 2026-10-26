import { RemoteActions } from "../changes/RemoteBar";
import { useRemote } from "../changes/useRemote";
import { useCommands } from "../commands/Commands";
import { useWorktreeStatus } from "../model/statusReads";
import { inUseReason } from "../model/worktrees";
import type { RepoState } from "../protocol";
import type { RpcClient } from "../rpc";
import { BranchInfo } from "./BranchInfo";

interface Props {
  client: RpcClient;
  state: RepoState;
  /** The worktree the repo was opened from, which Fetch, Pull and Push act on, as the history menu does. */
  worktree: string;
}

/**
 * The branch, how it stands against its upstream, and Fetch, Pull and Push, at the top on every view. Each runs in
 * the command window. An agent's worktree is left alone: its buttons are off, and say why.
 */
export function BranchBar({ client, state, worktree }: Props) {
  const { status, apply } = useWorktreeStatus(client, state.repoId, worktree);
  const remote = useRemote(client, state.repoId, worktree, apply);
  const commands = useCommands();
  if (status === null) return null;
  const inUse = inUseReason(state, worktree);
  return (
    <div className="branch-bar" role="group" aria-label="Branch" title={inUse ?? undefined}>
      <BranchInfo status={status} />
      <RemoteActions status={status} remote={remote} disabled={commands.busy || inUse !== null} />
    </div>
  );
}
