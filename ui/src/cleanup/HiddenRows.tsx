import { kindLabels } from "../model/format";
import type { Leftover } from "../protocol";

interface Props {
  hidden: Leftover[];
  onUnhide(leftover: Leftover): void;
}

/**
 * The rows the user hid, folded away under one line like past cleanups. Each comes back into the list by itself
 * when it changes, or with "Show again".
 */
export function HiddenRows({ hidden, onUnhide }: Props) {
  if (hidden.length === 0) return null;
  return (
    <details className="past-cleanups hidden-rows">
      <summary>Hidden ({hidden.length})</summary>
      <ul aria-label="Hidden rows">
        {hidden.map((l) => (
          <li key={`${l.kind}|${l.path ?? l.name}|${l.agent ?? ""}`} className="past-cleanup" aria-label={l.name}>
            <span className="what">{l.name}</span>
            <span className="when">{kindLabels[l.kind]}</span>
            <button className="link" onClick={() => onUnhide(l)} aria-label={`Show ${l.name} again`}>
              Show again
            </button>
          </li>
        ))}
      </ul>
    </details>
  );
}
