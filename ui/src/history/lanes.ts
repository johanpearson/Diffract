/** The part of a commit the layout needs. */
export interface LaneCommit {
  id: string;
  /** First parent first. */
  parentIds: readonly string[];
}

/**
 * One line inside one row's band, so a row can be drawn without looking at any other row.
 * - "through": a lane that passes the row, from the top edge to the bottom edge. Always straight: `from` equals `to`.
 * - "upper": from the top edge at lane `from` to the commit's dot at lane `to`. A child coming down to this commit.
 * - "lower": from the dot at lane `from` to the bottom edge at lane `to`. A line leaving for a parent.
 */
export interface LaneLine {
  part: "through" | "upper" | "lower";
  from: number;
  to: number;
  /** A colour index. The renderer picks the palette colour with `color % palette.length`. */
  color: number;
}

export interface LaneRow {
  /** The column of the commit's dot, 0 on the left. */
  lane: number;
  color: number;
  lines: LaneLine[];
}

/** Where the layout stands after the last row: what each lane waits for. Pass it back to lay out the next page. */
export interface LaneState {
  /** For each lane, the commit it leads to next, or null when the lane is free. */
  readonly lanes: readonly (string | null)[];
  readonly colors: readonly number[];
  readonly nextColor: number;
  /** The most lanes any row has used so far. */
  readonly width: number;
}

export const emptyLaneState: LaneState = { lanes: [], colors: [], nextColor: 0, width: 0 };

/**
 * Lays out commits in topological order (children before parents) into lanes. A pure function: pass the state from
 * the previous page to continue, and the rows come out exactly as if all pages had been laid out at once.
 *
 * A lane keeps its column for as long as it lives, so a branch is a straight line and is easy to follow. A freed
 * column is reused by the next branch that needs one. A commit takes the leftmost lane that leads to it; a branch
 * tip with no child above it takes the leftmost free lane and a new colour. A first parent continues in the commit's
 * lane and colour. Other parents of a merge join the lane already leading to them, or start a new lane to the right.
 */
export function layoutLanes(commits: readonly LaneCommit[], previous: LaneState = emptyLaneState): { rows: LaneRow[]; state: LaneState } {
  const lanes = new Lanes(previous);
  const rows = commits.map((commit) => lanes.add(commit));
  return { rows, state: lanes.state() };
}

/** The working copy of a layout's state while one page is laid out. Mutable, so layoutLanes can stay pure outside. */
class Lanes {
  private readonly waiting: (string | null)[];
  private readonly colors: number[];
  private nextColor: number;
  private width: number;

  constructor(previous: LaneState) {
    this.waiting = previous.lanes.slice();
    this.colors = previous.colors.slice();
    this.nextColor = previous.nextColor;
    this.width = previous.width;
  }

  state(): LaneState {
    return { lanes: this.waiting, colors: this.colors, nextColor: this.nextColor, width: this.width };
  }

  /** One row: lines come down to the commit in its top half and leave for its parents in its bottom half. */
  add(commit: LaneCommit): LaneRow {
    const lines: LaneLine[] = [];
    const arrived = this.comeDown(commit.id, lines);
    const lane = arrived >= 0 ? arrived : this.freeLane(0);
    const color = arrived >= 0 ? this.colors[lane]! : this.nextColor++;
    this.leaveFor(commit.parentIds, lane, color, lines);
    this.width = Math.max(this.width, this.waiting.length, lane + 1);
    this.trim();
    return { lane, color, lines };
  }

  /** Lanes leading to this commit come down to its dot and are freed, every other lane passes by. Returns the dot's lane, or -1. */
  private comeDown(id: string, lines: LaneLine[]): number {
    let lane = -1;
    this.waiting.forEach((waitingFor, i) => {
      if (waitingFor === null) return;
      const color = this.colors[i]!;
      if (waitingFor !== id) {
        lines.push({ part: "through", from: i, to: i, color });
        return;
      }
      if (lane < 0) lane = i;
      lines.push({ part: "upper", from: i, to: lane, color });
      this.waiting[i] = null;
    });
    return lane;
  }

  /** The first parent continues in the commit's lane and colour; other parents join or start a lane to the right. */
  private leaveFor(parentIds: readonly string[], lane: number, color: number, lines: LaneLine[]) {
    const [first, ...others] = parentIds;
    if (first !== undefined) {
      this.waiting[lane] = first;
      this.colors[lane] = color;
      lines.push({ part: "lower", from: lane, to: lane, color });
    }
    for (const parent of others) {
      const target = this.laneFor(parent, lane + 1);
      lines.push({ part: "lower", from: lane, to: target, color: this.colors[target]! });
    }
  }

  /** The lane already leading to `parent`, or a new one in a new colour at or after `start`. */
  private laneFor(parent: string, start: number): number {
    const existing = this.waiting.indexOf(parent);
    if (existing >= 0) return existing;
    const lane = this.freeLane(start);
    this.waiting[lane] = parent;
    this.colors[lane] = this.nextColor++;
    return lane;
  }

  /** The first free lane at or after `start`, or a new one at the end. Colours stay as long as lanes. */
  private freeLane(start: number): number {
    const free = this.waiting.indexOf(null, start);
    if (free >= 0) return free;
    while (this.waiting.length < start) {
      this.waiting.push(null);
      this.colors.push(0);
    }
    return this.waiting.length;
  }

  /** Drops free lanes on the right, so the graph is only as wide as it needs to be. */
  private trim() {
    while (this.waiting.length > 0 && this.waiting[this.waiting.length - 1] === null) {
      this.waiting.pop();
      this.colors.pop();
    }
  }
}
