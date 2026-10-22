import type { RepoState } from "../protocol";
import { formatAge, formatTime } from "../model/format";

/** When the snapshot was taken, and how old the count of unreachable commits is. */
export function StatusLine({ state, now }: { state: RepoState; now: Date }) {
  const counted = state.unreachableCountedAt;
  return (
    <p className="status-line">
      Snapshot taken {formatTime(state.takenAt)}. {counted ? `Unreachable commits counted ${formatAge(counted, now)}.` : "Unreachable commits not counted yet."}
    </p>
  );
}
