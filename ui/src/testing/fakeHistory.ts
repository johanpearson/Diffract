import detailsSample from "../../../tests/Diffract.Sidecar.Tests/Contract/history.details.json";
import type { CommitDetails, CommitEntry, HistoryFile, HistoryRef } from "../protocol";

const authors = [
  { name: "Johan Pearson", email: "johan@example.com" },
  { name: "Claude", email: "noreply@anthropic.com" },
  { name: "GitHub Copilot", email: "198982749+Copilot@users.noreply.github.com" },
  { name: "Åsa Öberg", email: "åsa@exempel.se" },
];

/** Branches that run next to main, so the graph has several lanes. */
const SideBranches = 4;
/** Every this many commits, one side branch is merged into main and starts again. */
const MergeEvery = 37;

/** A made-up but plausible history: main, a few side branches and regular merges, newest first in topological order. */
export function generateHistory(count: number, newest = Date.parse("2026-10-21T09:00:00+02:00")): { commits: CommitEntry[]; refs: HistoryRef[] } {
  const builder = new HistoryBuilder(newest - count * 60_000);
  for (let step = 0; builder.size < count; step++) builder.step(step);
  const commits = builder.commits.slice(0, count).reverse();
  return { commits, refs: builder.refs(new Set(commits.map((c) => c.id))) };
}

/** Creates commits oldest first, so every parent exists before its children; reversed, that is a topological order. */
class HistoryBuilder {
  readonly commits: CommitEntry[] = [];
  private main: string | null = null;
  private readonly tips: (string | null)[] = Array.from({ length: SideBranches }, () => null);
  private readonly oldest: number;

  constructor(oldest: number) {
    this.oldest = oldest;
  }

  get size(): number {
    return this.commits.length;
  }

  step(step: number): void {
    if (step % MergeEvery === MergeEvery - 1) return this.merge(step % SideBranches);
    const branch = step % (SideBranches + 1);
    if (branch === 0) this.main = this.add([this.main], `Change ${step} on main`);
    else this.tips[branch - 1] = this.add([this.tips[branch - 1] ?? this.main], `Work ${step} on feature ${branch}`);
  }

  refs(present: Set<string>): HistoryRef[] {
    const refs: HistoryRef[] = [
      { name: "main", kind: "Branch", targetId: this.main! },
      { name: "origin/main", kind: "RemoteBranch", targetId: this.main! },
      ...this.tips.flatMap((tip, i): HistoryRef[] => (tip ? [{ name: `feature/${i + 1}`, kind: "Branch", targetId: tip }] : [])),
      { name: "v1.0", kind: "Tag", targetId: this.commits[0]!.id },
    ];
    return refs.filter((r) => present.has(r.targetId));
  }

  private merge(branch: number): void {
    const tip = this.tips[branch];
    if (!tip) return;
    this.main = this.add([this.main, tip], `Merge branch 'feature/${branch + 1}'`);
    this.tips[branch] = null;
  }

  private add(parents: (string | null)[], subject: string): string {
    const n = this.commits.length;
    const author = authors[n % authors.length]!;
    const at = new Date(this.oldest + n * 60_000).toISOString();
    // Multiplying by an odd number scrambles n without collisions, so ids look like hashes and stay unique.
    const id = (Math.imul(n + 1, 0x9e3779b1) >>> 0).toString(16).padStart(8, "0").repeat(5);
    this.commits.push({ id, parentIds: parents.filter((p) => p !== null), authorName: author.name, authorEmail: author.email, authoredAt: at, committedAt: at, subject });
    return id;
  }
}

/** Details for a commit of a generated history: its own fields, a short body and one changed file. */
export function detailsOf(commit: CommitEntry): CommitDetails {
  return {
    ...commit,
    committerName: commit.authorName,
    committerEmail: commit.authorEmail,
    body: `Details of ${commit.subject}.`,
    files: [
      {
        kind: "Modified",
        path: "src/cart.cs",
        oldPath: null,
        oldId: "1".repeat(40),
        newId: "2".repeat(40),
        similarity: null,
        additions: 3,
        deletions: 1,
        isBinary: false,
        isSubmodule: false,
      },
    ],
  };
}

const cartBefore = ["namespace Shop;", "", "public class Cart", "{", "    public decimal Total => 0m;", "}", ""].join("\n");
const cartAfter = ["namespace Shop;", "", "public class Cart", "{", "    public decimal Total => Lines.Sum(l => l.Price);", "    public List<Line> Lines { get; } = [];", "}", ""].join("\n");
const [renamed, picture] = (detailsSample as { result: CommitDetails }).result.files;

/** File contents by blob id: the generated history's cart.cs, and the files of the history.details contract sample. */
export const sampleFiles: [string, HistoryFile][] = [
  ["1".repeat(40), { kind: "Text", text: cartBefore, message: null }],
  ["2".repeat(40), { kind: "Text", text: cartAfter, message: null }],
  [renamed!.oldId!, { kind: "Text", text: cartBefore, message: null }],
  [renamed!.newId!, { kind: "Text", text: cartAfter.replace("Cart", "Räksmörgås"), message: null }],
  [picture!.newId!, { kind: "Binary", text: null, message: "This is a binary file." }],
];
