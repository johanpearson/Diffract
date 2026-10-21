import type { RepoState, WorktreeInfo } from "../protocol";
import { folderName } from "../model/format";

/** The repo's worktrees with their state, and the Leftovers entry with the count of everything not in use. */
export function Sidebar({ state }: { state: RepoState }) {
  const inUse = new Set(state.leftovers.filter((l) => l.verdict === "InUse" && l.path).map((l) => l.path!.toLowerCase()));
  const notInUse = state.summary.total - state.summary.inUse;

  return (
    <nav className="sidebar" aria-label="Repository">
      <div className="repo">
        <div className="repo-name">{folderName(state.repoRoot)}</div>
        <div className="repo-path" title={state.repoRoot}>
          {state.repoRoot}
        </div>
      </div>
      <h2>Worktrees</h2>
      <ul className="worktrees">
        {state.snapshot.worktrees.map((w) => (
          <li key={w.path} title={w.path}>
            <span className={`dot${w.isMain ? " is-main" : inUse.has(w.path.toLowerCase()) ? " is-active" : ""}`} aria-hidden="true" />
            <div className="wt">
              <span className="wt-name">{worktreeName(w)}</span>
              <span className="badges">
                {badges(w, inUse.has(w.path.toLowerCase())).map((badge) => (
                  <span key={badge} className="badge">
                    {badge}
                  </span>
                ))}
              </span>
            </div>
          </li>
        ))}
      </ul>
      <ul className="nav">
        <li aria-current="page">
          Leftovers <span className="count" aria-label={`${notInUse} not in use`}>{notInUse}</span>
        </li>
      </ul>
    </nav>
  );
}

function worktreeName(w: WorktreeInfo): string {
  if (w.branch) return w.branch;
  return w.isDetached && !w.isMain ? `${folderName(w.path)} (detached)` : folderName(w.path);
}

function badges(w: WorktreeInfo, inUse: boolean): string[] {
  const list: string[] = [];
  if (w.isMain) list.push("primary");
  if (w.isPrunable) list.push("folder gone");
  if (w.isLocked) list.push("locked");
  if (w.dirtyFileCount) list.push(`${w.dirtyFileCount} changed`);
  else if (inUse && !w.isLocked) list.push("active");
  return list;
}
