import type { CommitEntry, HistoryRefs } from "../protocol";

/** The history toggles that fade rows. */
export interface FadeToggles {
  /** Fade commits that reached main through a merge. */
  fadeMerged: boolean;
  /** Fade every commit that does not look like an agent's. */
  agentsOnly: boolean;
}

type Graph = ReadonlyMap<string, CommitEntry>;

/** The commits loaded so far, by id. */
export function graphOf(commits: readonly CommitEntry[]): Graph {
  return new Map(commits.map((c) => [c.id, c]));
}

/**
 * Every loaded commit reached from `starts` through the parents `next` picks, without passing a commit in `stop`.
 * Parents that are not loaded yet are skipped.
 */
function walk(graph: Graph, starts: readonly string[], next: (commit: CommitEntry) => readonly string[], stop: ReadonlySet<string> = new Set()): Set<string> {
  const reached = new Set<string>();
  const stack = [...starts];
  while (stack.length > 0) {
    const id = stack.pop()!;
    const commit = graph.get(id);
    if (!commit || reached.has(id) || stop.has(id)) continue;
    reached.add(id);
    stack.push(...next(commit));
  }
  return reached;
}

const allParents = (c: CommitEntry) => c.parentIds;
const firstParent = (c: CommitEntry) => c.parentIds.slice(0, 1);

/** Main's own line: its tip and each first parent after it. Commits merged into main are not on it. */
export function mainLine(graph: Graph, mainTip: string | null): Set<string> {
  return walk(graph, mainTip === null ? [] : [mainTip], firstParent);
}

/** Commits main reaches that are not on its own line: the work of branches merged into it. */
export function mergedCommits(graph: Graph, mainTip: string | null, line: ReadonlySet<string>): Set<string> {
  const reached = walk(graph, mainTip === null ? [] : [mainTip], allParents);
  return new Set([...reached].filter((id) => !line.has(id)));
}

/**
 * Commits that look like an agent's: those on an agent's branch, checkpoint or worktree, followed back by first
 * parent until main's line, and those whose subject starts with an agent's prefix. A hint, like all attribution.
 */
export function agentCommits(graph: Graph, refs: HistoryRefs, line: ReadonlySet<string>): Set<string> {
  const tips = [...refs.refs.filter((r) => r.agent !== null).map((r) => r.targetId), ...refs.agentHeads];
  const agent = walk(graph, tips, firstParent, line);
  const prefixed = [...graph.values()].filter((c) => refs.agentSubjectPrefixes.some((prefix) => c.subject.startsWith(prefix)));
  for (const commit of prefixed) agent.add(commit.id);
  return agent;
}

/** The ids of the commits to fade, given the toggles. */
export function fadedCommits(commits: readonly CommitEntry[], refs: HistoryRefs, mainBranch: string, toggles: FadeToggles): ReadonlySet<string> {
  if (!toggles.fadeMerged && !toggles.agentsOnly) return new Set();
  const graph = graphOf(commits);
  const mainTip = refs.refs.find((r) => r.kind === "Branch" && r.name === mainBranch)?.targetId ?? null;
  const line = mainLine(graph, mainTip);
  const merged = toggles.fadeMerged ? mergedCommits(graph, mainTip, line) : [];
  const notAgent = toggles.agentsOnly ? notAgentCommits(commits, agentCommits(graph, refs, line)) : [];
  return new Set([...merged, ...notAgent]);
}

function notAgentCommits(commits: readonly CommitEntry[], agent: ReadonlySet<string>): string[] {
  return commits.filter((c) => !agent.has(c.id)).map((c) => c.id);
}
