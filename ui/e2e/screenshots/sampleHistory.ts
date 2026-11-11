import type { CommitEntry, HistoryRef, HistoryRefKind } from "../../src/protocol";

/** The moment the screenshots show: when the contract samples were made, so their fixed dates agree with the ages. */
export const ShotsNow = new Date("2026-10-21T07:30:00Z");

type Person = "maja" | "erik" | "asa" | "claude" | "copilot";

const people: Record<Person, { name: string; email: string }> = {
  maja: { name: "Maja Lindqvist", email: "maja@example.com" },
  erik: { name: "Erik Holm", email: "erik@example.com" },
  asa: { name: "Åsa Öberg", email: "asa@example.com" },
  claude: { name: "Claude", email: "noreply@anthropic.com" },
  copilot: { name: "GitHub Copilot", email: "copilot@example.com" },
};

interface Step {
  name: string;
  parents: string[];
  by: Person;
  hoursAgo: number;
  subject: string;
  /** Set for the commits the contract samples already describe, so the selected commit's details line up. */
  id?: string;
}

/** A made-up history: main, a merged feature, and two agents' branches. Newest first. */
const steps: Step[] = [
  { name: "c2", parents: ["c1"], by: "claude", hoursAgo: 0.5, subject: "Add a test for the redirect after login" },
  { name: "m10", parents: ["m9"], by: "maja", hoursAgo: 2, subject: "Bump version to 1.2.0" },
  { name: "m9", parents: ["m8", "k1"], by: "asa", hoursAgo: 3, subject: "Merge branch 'claude/räksmörgås-fix'", id: "9fceb02d0ae598e95dc970b74767f19372d61af8" },
  { name: "k1", parents: ["m7"], by: "claude", hoursAgo: 5, subject: "Fix the cart total when a price changes", id: "e83c5163316f89bfbde7d9ab23ca2e25604af290" },
  { name: "c1", parents: ["m5"], by: "claude", hoursAgo: 6.5, subject: "Fix redirect loop after login" },
  { name: "m8", parents: ["m7"], by: "maja", hoursAgo: 26, subject: "Show the cart total with two decimals", id: "4b825dc642cb6eb9a060e54bf8d69288fbee4904" },
  { name: "p1", parents: ["m6"], by: "copilot", hoursAgo: 30, subject: "Draft the order history page" },
  { name: "m7", parents: ["m6"], by: "erik", hoursAgo: 50, subject: "Cache the product list" },
  { name: "m6", parents: ["m5"], by: "erik", hoursAgo: 72, subject: "Update dependencies" },
  { name: "m5", parents: ["m4", "f2"], by: "maja", hoursAgo: 96, subject: "Merge branch 'feature/discounts'" },
  { name: "f2", parents: ["f1"], by: "asa", hoursAgo: 118, subject: "Apply the discount to the cart total" },
  { name: "m4", parents: ["m3"], by: "maja", hoursAgo: 120, subject: "Round VAT to two decimals" },
  { name: "f1", parents: ["m3"], by: "asa", hoursAgo: 144, subject: "Add the discount rule" },
  { name: "m3", parents: ["m2"], by: "erik", hoursAgo: 168, subject: "Set up CI" },
  { name: "m2", parents: ["m1"], by: "maja", hoursAgo: 192, subject: "Add tests for the cart" },
  { name: "m1", parents: ["m0"], by: "maja", hoursAgo: 216, subject: "Add the cart total" },
  { name: "m0", parents: [], by: "maja", hoursAgo: 240, subject: "Initial commit" },
];

interface RefSpec {
  name: string;
  kind: HistoryRefKind;
  at: string;
  agent?: string;
}

const refSpecs: RefSpec[] = [
  { name: "main", kind: "Branch", at: "m10" },
  { name: "origin/main", kind: "RemoteBranch", at: "m9" },
  { name: "v1.2.0", kind: "Tag", at: "m10" },
  { name: "v1.1.0", kind: "Tag", at: "m5" },
  { name: "claude/räksmörgås-fix", kind: "Branch", at: "k1", agent: "Claude Code" },
  { name: "claude/fix-login-redirect", kind: "Branch", at: "c2", agent: "Claude Code" },
  { name: "copilot/order-history", kind: "Branch", at: "p1", agent: "GitHub Copilot" },
];

/** A repeatable 40-character id for a commit that has no fixed one. */
function idOf(name: string): string {
  let hash = 0x811c9dc5;
  let id = "";
  while (id.length < 40) {
    for (const char of `${name}${id.length}`) hash = Math.imul(hash ^ char.charCodeAt(0), 16777619);
    id += (hash >>> 0).toString(16).padStart(8, "0");
  }
  return id.slice(0, 40);
}

export function sampleHistory(): { commits: CommitEntry[]; refs: HistoryRef[] } {
  const ids = new Map(steps.map((s) => [s.name, s.id ?? idOf(s.name)]));
  const commits = steps.map((s): CommitEntry => {
    const at = new Date(ShotsNow.getTime() - s.hoursAgo * 3_600_000).toISOString();
    const { name, email } = people[s.by];
    return { id: ids.get(s.name)!, parentIds: s.parents.map((p) => ids.get(p)!), authorName: name, authorEmail: email, authoredAt: at, committedAt: at, subject: s.subject };
  });
  const refs = refSpecs.map((r): HistoryRef => ({ name: r.name, kind: r.kind, targetId: ids.get(r.at)!, agent: r.agent ?? null }));
  return { commits, refs };
}
