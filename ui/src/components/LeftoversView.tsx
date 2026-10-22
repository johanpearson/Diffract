import { verdicts, type Leftover, type RepoState, type Verdict } from "../protocol";
import { formatAtRisk, kindLabels, verdictLabels } from "../model/format";
import type { Row } from "../model/rows";
import { StatusLine } from "./StatusLine";

export type Filter = Verdict | "all";

const verdictClass: Record<Verdict, string> = { SafeToRemove: "safe", NeedsReview: "review", InUse: "use" };

interface Props {
  state: RepoState;
  rows: Row[];
  filter: Filter;
  onFilter(filter: Filter): void;
  onOpenFolder(path: string): void;
  onOpenTerminal(path: string): void;
  /** The terminal "Open" uses, for the button's label. Null when unknown. */
  terminalName: string | null;
  onRefresh(): void;
  refreshing: boolean;
  /** Why Refresh cannot be used right now, or null when it can. */
  refreshBlocked?: string | null;
  now: Date;
}

/** The read-only "Worktrees and agents" view: counts, filter chips and one row per leftover. */
export function LeftoversView(props: Props) {
  const { state, refreshing, refreshBlocked = null, onRefresh, now } = props;
  return (
    <section className="view" aria-labelledby="view-title">
      <div className="bar">
        <h1 id="view-title">Worktrees and agents</h1>
        <span className="grow" />
        <button
          className="abtn"
          onClick={onRefresh}
          disabled={refreshing || refreshBlocked !== null}
          title={refreshBlocked ?? "Also counts unreachable commits, which takes a few seconds"}
        >
          {refreshing ? "Refreshing…" : "Refresh"}
        </button>
      </div>
      <ScanResult {...props} />
      <StatusLine state={state} now={now} />
    </section>
  );
}

/** The leftovers once the scan after opening has found them, or why there are none to show yet. */
function ScanResult({ state, ...props }: Props) {
  if (state.scanning)
    return (
      <p className="empty" role="status">
        Looking for leftovers. History is ready to use meanwhile.
      </p>
    );
  if (state.scanProblem !== null)
    return (
      <p className="empty" role="alert">
        Could not finish looking for leftovers: {state.scanProblem} Refresh to try again.
      </p>
    );
  return (
    <>
      <Summary state={state} />
      <Chips filter={props.filter} onFilter={props.onFilter} />
      <LeftoverList {...props} />
    </>
  );
}

function Summary({ state }: { state: RepoState }) {
  const counts: Record<Verdict, number> = {
    SafeToRemove: state.summary.safeToRemove,
    NeedsReview: state.summary.needsReview,
    InUse: state.summary.inUse,
  };
  return (
    <div className="summary">
      {verdicts.map((v) => (
        <div key={v} className={`sum ${verdictClass[v]}`}>
          <b>{counts[v]}</b>
          <span>{verdictLabels[v]}</span>
        </div>
      ))}
    </div>
  );
}

function Chips({ filter, onFilter }: Pick<Props, "filter" | "onFilter">) {
  return (
    <div className="chips" role="group" aria-label="Show">
      {(["all", ...verdicts] as const).map((f) => (
        <button key={f} className="chip" aria-pressed={filter === f} onClick={() => onFilter(f)}>
          {f === "all" ? "All" : verdictLabels[f]}
        </button>
      ))}
    </div>
  );
}

function LeftoverList({ rows, filter, onOpenFolder, onOpenTerminal, terminalName }: Omit<Props, "state">) {
  const shown = rows.filter((row) => filter === "all" || row.leftover.verdict === filter);
  if (shown.length === 0) return <p className="empty">{rows.length === 0 ? "Nothing here. Agents left your repo tidy." : "Nothing with this verdict."}</p>;
  return (
    <ul className="items" aria-label="Leftovers">
      {shown.map((row) => (
        <LeftoverRow key={row.key} row={row} onOpenFolder={onOpenFolder} onOpenTerminal={onOpenTerminal} terminalName={terminalName} />
      ))}
    </ul>
  );
}

interface RowProps {
  row: Row;
  onOpenFolder(path: string): void;
  onOpenTerminal(path: string): void;
  terminalName: string | null;
}

function LeftoverRow({ row, onOpenFolder, onOpenTerminal, terminalName }: RowProps) {
  const l = row.leftover;
  return (
    <li className={`item${row.flash ? ` flash-${row.flash}` : ""}`} data-flash={row.flash ?? undefined} aria-label={l.name}>
      <div className="item-main">
        <div>
          <span className="nm">{l.name}</span>
          <span className="kind">{l.agent ? `${kindLabels[l.kind]}, ${l.agent}` : kindLabels[l.kind]}</span>
        </div>
        <div className="why">{l.reason}</div>
        {l.path && <div className="path">{l.path}</div>}
        {canOpen(l) && (
          <div className="item-actions">
            <button className="abtn" onClick={() => onOpenFolder(l.path!)} aria-label={`Open folder ${l.path}`}>
              Open folder
            </button>
            {l.kind === "Worktree" && (
              <button className="abtn" onClick={() => onOpenTerminal(l.path!)} aria-label={`Open terminal in ${l.path}`}>
                {terminalName ? `Open in ${terminalName}` : "Open terminal here"}
              </button>
            )}
          </div>
        )}
      </div>
      <div className="item-side">
        <span className={`verdict ${verdictClass[l.verdict]}`}>{verdictLabels[l.verdict]}</span>
        <span className="risk" title="Commits that exist nowhere else">
          At risk: {formatAtRisk(l.commitsAtRisk)}
        </span>
      </div>
    </li>
  );
}

/** Worktrees and leftover folders exist on disk. A prunable worktree's folder is gone. */
function canOpen(l: Leftover): boolean {
  return l.path !== null && (l.kind === "Worktree" || l.kind === "LeftoverFolder");
}
