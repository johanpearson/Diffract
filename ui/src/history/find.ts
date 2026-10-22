import type { CommitEntry } from "../protocol";

/**
 * The rows of the commits that match `query`, in order: a hash that starts with it, or a subject, author name or
 * email that contains it. Case does not matter. An empty query matches nothing.
 */
export function findMatches(commits: readonly CommitEntry[], query: string): number[] {
  const q = query.trim().toLowerCase();
  if (q === "") return [];
  const matches: number[] = [];
  commits.forEach((c, i) => {
    if (matchesCommit(c, q)) matches.push(i);
  });
  return matches;
}

function matchesCommit(c: CommitEntry, q: string): boolean {
  return c.id.startsWith(q) || [c.subject, c.authorName, c.authorEmail].some((text) => text.toLowerCase().includes(q));
}

/**
 * The match to go to from row `from`: the next one after it, or with `backwards` the one before it. Wraps around
 * at either end. Null when nothing matches.
 */
export function nextMatch(matches: readonly number[], from: number, backwards = false): number | null {
  if (matches.length === 0) return null;
  if (backwards) return [...matches].reverse().find((row) => row < from) ?? matches[matches.length - 1]!;
  return matches.find((row) => row > from) ?? matches[0]!;
}
