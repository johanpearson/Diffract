import type { CommitEntry, HistoryRefs, HistoryShow, HistoryUpdateResult } from "../protocol";
import { messageOf } from "../model/format";
import { isCancelled, type RpcClient } from "../rpc";
import { emptyLaneState, layoutLanes, type LaneRow, type LaneState } from "./lanes";

/** The history read so far, laid out in lanes as it arrives. */
export interface History {
  commits: CommitEntry[];
  /** One per commit, in the same order. */
  rows: LaneRow[];
  /** The widest the graph has been so far, in lanes. */
  width: number;
  refs: HistoryRefs;
  /** Every page has arrived. */
  done: boolean;
  /** Why reading failed, or null. */
  error: string | null;
  /** Changes whenever the commits do, even when their count stays the same. */
  version: number;
}

export const emptyRefs: HistoryRefs = { refs: [], agentHeads: [], agentSubjectPrefixes: [] };

export const emptyHistory: History = { commits: [], rows: [], width: 0, refs: emptyRefs, done: false, error: null, version: 0 };

/** What the history should match: the repo's snapshot, by when it was taken, and the toggles. */
export interface HistoryTarget {
  snapshotAt: string;
  show: HistoryShow;
}

let reads = 0;

/**
 * Keeps one repo's history in step with the repo. It reads the whole history once, page by page. After that, each
 * new target asks the sidecar only what changed, one update at a time, and new commits go on top. When too much
 * changed, it reads the whole history again while the old one stays on screen.
 */
export class HistorySync {
  private readonly client: RpcClient;
  private readonly repoId: string;
  private readonly publish: (history: History) => void;
  private readonly abort = new AbortController();
  private history: History = emptyHistory;
  private lanes: LaneState = emptyLaneState;
  /** The tips the commits were read from. Null until the first read has finished. */
  private tips: string[] | null = null;
  private wanted: HistoryTarget;
  private applied: HistoryTarget;
  private busy = false;

  constructor(client: RpcClient, repoId: string, target: HistoryTarget, publish: (history: History) => void) {
    this.client = client;
    this.repoId = repoId;
    this.publish = publish;
    this.wanted = this.applied = target;
  }

  /** Reads the whole history and its refs, then follows the targets. */
  async start(): Promise<void> {
    this.loadRefs();
    try {
      this.tips = await this.read(this.applied.show, (commits) => this.append(commits));
      this.set({ done: true });
      await this.follow();
    } catch (error) {
      this.fail(error);
    }
  }

  /** Brings the history up to date with `target` once the read and any update before it are done. */
  want(target: HistoryTarget): void {
    this.wanted = target;
    void this.follow().catch((error: unknown) => this.fail(error));
  }

  stop(): void {
    this.abort.abort();
  }

  /** The first read has finished, no update is running, and the history is behind what is wanted. */
  private get behind(): boolean {
    const idle = !this.busy && this.tips !== null && !this.abort.signal.aborted;
    return idle && !sameTarget(this.wanted, this.applied);
  }

  private async follow(): Promise<void> {
    while (this.behind) {
      this.busy = true;
      const target = this.wanted;
      try {
        await this.update(target.show);
      } finally {
        this.applied = target;
        this.busy = false;
      }
    }
  }

  private async update(show: HistoryShow): Promise<void> {
    this.loadRefs();
    const change = await this.client.call("history.update", { repoId: this.repoId, tips: this.tips!, ...show }, { signal: this.abort.signal });
    if (change.reread) return this.reread(show);
    this.tips = change.tips;
    if (change.added.length > 0 || change.removed.length > 0) this.replace(applyChange(this.history.commits, change));
  }

  /** Reads everything again into a list of its own, and swaps it in at the end, so the screen never empties. */
  private async reread(show: HistoryShow): Promise<void> {
    const commits: CommitEntry[] = [];
    this.tips = await this.read(show, (page) => commits.push(...page));
    this.replace(commits);
  }

  /** One history.read, handing on each page. Answers with the tips it was read from. */
  private async read(show: HistoryShow, onPage: (commits: CommitEntry[]) => void): Promise<string[]> {
    const token = `read-${++reads}`;
    const stop = this.client.onNotification("history.page", (page) => {
      if (page.token === token) onPage(page.commits);
    });
    try {
      return (await this.client.call("history.read", { repoId: this.repoId, token, ...show }, { signal: this.abort.signal })).tips;
    } finally {
      stop();
    }
  }

  private loadRefs(): void {
    this.client
      .call("history.refs", { repoId: this.repoId }, { signal: this.abort.signal })
      .then((refs) => this.set({ refs }))
      .catch((error: unknown) => this.fail(error));
  }

  /** The next page of the first read, laid out where the last one stopped. */
  private append(commits: CommitEntry[]): void {
    const { rows, state } = layoutLanes(commits, this.lanes);
    this.lanes = state;
    this.set({ commits: this.history.commits.concat(commits), rows: this.history.rows.concat(rows), width: state.width });
  }

  /** A whole new list, laid out from the top: new commits on top change every lane below them. */
  private replace(commits: CommitEntry[]): void {
    const { rows, state } = layoutLanes(commits);
    this.lanes = state;
    this.set({ commits, rows, width: state.width });
  }

  private set(change: Partial<History>): void {
    if (this.abort.signal.aborted) return;
    const version = change.commits ? this.history.version + 1 : this.history.version;
    this.history = { ...this.history, ...change, version };
    this.publish(this.history);
  }

  private fail(error: unknown): void {
    if (!this.abort.signal.aborted && !isCancelled(error)) this.set({ error: messageOf(error) });
  }
}

/** The added commits on top, then the ones already there that are still reachable. */
export function applyChange(commits: readonly CommitEntry[], { added, removed }: Pick<HistoryUpdateResult, "added" | "removed">): CommitEntry[] {
  const gone = new Set(removed);
  return [...added, ...commits.filter((c) => !gone.has(c.id))];
}

function sameTarget(a: HistoryTarget, b: HistoryTarget): boolean {
  return a.snapshotAt === b.snapshotAt && a.show.checkpoints === b.show.checkpoints && a.show.stashes === b.show.stashes;
}
