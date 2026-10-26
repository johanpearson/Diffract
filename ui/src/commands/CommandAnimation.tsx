import type { ReactNode } from "react";
import type { CommandKind, RunState } from "./commandRun";

/** A remote: a cloud at the top. */
const cloud = <path className="anim-cloud" d="M20 18a6 6 0 0 1 11-5 7 7 0 0 1 13 3 5 5 0 0 1 0 10H20a4 4 0 0 1 0-8z" />;

/** Commits moving along a line, staggered. */
function Dots({ className, xs, y }: { className: string; xs: number[]; y: number }) {
  return (
    <>
      {xs.map((x, i) => (
        <circle key={x} className={className} cx={x} cy={y} r="3" style={{ animationDelay: `${i * 0.25}s` }} />
      ))}
    </>
  );
}

/** A branch line with commits on it. */
function Line({ y, xs }: { y: number; xs: number[] }) {
  return (
    <>
      <line className="anim-line" x1="6" y1={y} x2="58" y2={y} />
      {xs.map((x) => (
        <circle key={x} className="anim-commit" cx={x} cy={y} r="3.5" />
      ))}
    </>
  );
}

/** One picture per kind: what the command does to commits and branches. */
const pictures: Record<CommandKind, ReactNode> = {
  // Commits come down from the remote.
  fetch: (
    <>
      {cloud}
      <Dots className="anim-drop" xs={[24, 32, 40]} y={28} />
      <line className="anim-line" x1="16" y1="45" x2="48" y2="45" />
    </>
  ),
  // Commits come down, and the branch moves forward to take them.
  pull: (
    <>
      {cloud}
      <Dots className="anim-drop" xs={[26, 38]} y={28} />
      <line className="anim-line" x1="6" y1="45" x2="58" y2="45" />
      <line className="anim-grow" x1="10" y1="45" x2="54" y2="45" />
    </>
  ),
  // Commits go up to the remote.
  push: (
    <>
      {cloud}
      <Dots className="anim-rise" xs={[24, 32, 40]} y={42} />
      <line className="anim-line" x1="16" y1="45" x2="48" y2="45" />
    </>
  ),
  // A new commit lands at the end of the line.
  commit: (
    <>
      <Line y={32} xs={[12, 25, 38]} />
      <circle className="anim-land" cx="51" cy="32" r="4" />
    </>
  ),
  // HEAD hops from one branch to the other.
  switch: (
    <>
      <Line y={16} xs={[16, 40]} />
      <Line y={38} xs={[24, 48]} />
      <circle className="anim-hop" cx="40" cy="16" r="6.5" />
    </>
  ),
  // A new branch grows from a commit.
  branch: (
    <>
      <Line y={38} xs={[14, 30, 46]} />
      <path className="anim-grow" d="M30 38 Q34 14 56 14" />
    </>
  ),
  // A tag swings onto a commit.
  tag: (
    <>
      <Line y={38} xs={[14, 32, 50]} />
      <path className="anim-swing" d="M26 10h12l4 6-4 6H26z" />
    </>
  ),
  // Leftover commits at the end of a line fade away, while the line itself stays.
  cleanup: (
    <>
      <Line y={30} xs={[12, 26]} />
      <Dots className="anim-fade" xs={[40, 54]} y={30} />
    </>
  ),
  // HEAD slides back to an earlier commit.
  reset: (
    <>
      <Line y={30} xs={[12, 26, 40, 54]} />
      <circle className="anim-slide" cx="54" cy="30" r="6.5" />
    </>
  ),
};

/** How it ended, over the picture once git has stopped. */
function Badge({ state }: { state: RunState }) {
  if (state === "running") return null;
  const mark = state === "done" ? "M54 44l3 3 6-6" : state === "warning" ? "M59 38v5M59 46v.5" : "M56 41l6 6M62 41l-6 6";
  return (
    <g className={`anim-badge ${state}`}>
      <circle cx="59" cy="44" r="7" />
      <path d={mark} />
    </g>
  );
}

/**
 * A short animation for the kind of command, which loops while git runs and stops when it ends. With Windows' "reduce
 * motion" on, the picture stands still.
 */
export function CommandAnimation({ kind, state }: { kind: CommandKind; state: RunState }) {
  return (
    <svg className={`command-animation anim-${kind}${state === "running" ? " is-running" : ""}`} viewBox="0 0 68 54" width="68" height="54" aria-hidden="true">
      {pictures[kind]}
      <Badge state={state} />
    </svg>
  );
}
