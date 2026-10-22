import { useState } from "react";
import type { AppHost } from "../host";
import type { HistoryShow } from "../protocol";
import type { FadeToggles } from "./emphasis";

export type Toggles = FadeToggles & HistoryShow;

type ToggleName = keyof Toggles;

/** In the order shown, with the visual plan's defaults: merged branches fade, the rest is off. */
const toggles: { name: ToggleName; label: string; title: string; initial: boolean }[] = [
  { name: "fadeMerged", label: "Fade merged branches", title: "Dim the commits that reached main through a merge", initial: true },
  { name: "agentsOnly", label: "Agent work only", title: "Dim every commit that does not look like an agent's, from the patterns in diffract.json", initial: false },
  { name: "checkpoints", label: "Checkpoints", title: "Show agents' checkpoint refs and the commits only they reach", initial: false },
  { name: "stashes", label: "Stashes", title: "Show every stash and the commits it is made of", initial: false },
];

const storageKey = (name: ToggleName) => `diffract.history.${name}`;

/** The toggles, remembered between runs. */
export function useHistoryToggles(storage: AppHost["storage"]): [Toggles, (name: ToggleName, on: boolean) => void] {
  const [values, setValues] = useState<Toggles>(() => {
    const entries = toggles.map(({ name, initial }) => [name, (storage.get(storageKey(name)) ?? String(initial)) === "true"]);
    return Object.fromEntries(entries) as Toggles;
  });
  const set = (name: ToggleName, on: boolean) => {
    storage.set(storageKey(name), String(on));
    setValues((v) => ({ ...v, [name]: on }));
  };
  return [values, set];
}

export function HistoryToggles({ values, onChange }: { values: Toggles; onChange(name: ToggleName, on: boolean): void }) {
  return (
    <div className="history-toggles" role="group" aria-label="Show">
      {toggles.map(({ name, label, title }) => (
        <label key={name} className="toggle" title={title}>
          <input type="checkbox" checked={values[name]} onChange={(e) => onChange(name, e.target.checked)} />
          {label}
        </label>
      ))}
    </div>
  );
}
