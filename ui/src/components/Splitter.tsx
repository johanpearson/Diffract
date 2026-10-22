import { useRef, useState, type KeyboardEvent, type PointerEvent } from "react";
import type { AppHost } from "../host";

/** A size in pixels with its limits, remembered between runs under `key`. */
export interface SizeSpec {
  key: string;
  initial: number;
  min: number;
  max: number;
}

/** A remembered size, kept within its limits. */
export function useStoredSize(storage: AppHost["storage"], spec: SizeSpec): [number, (size: number) => void] {
  const [size, setSize] = useState(() => clamp(Number(storage.get(spec.key) ?? spec.initial) || spec.initial, spec));
  const update = (next: number) => {
    const value = Math.round(clamp(next, spec));
    setSize(value);
    storage.set(spec.key, String(value));
  };
  return [size, update];
}

interface Props {
  label: string;
  /** "vertical" is an upright bar between columns; "horizontal" lies between rows. */
  orientation: "vertical" | "horizontal";
  spec: SizeSpec;
  size: number;
  onSize(size: number): void;
  /** The panel it sizes lies left of or above the bar, so moving the bar right or down grows it. Otherwise it shrinks it. */
  growsForward: boolean;
}

/** How far one arrow key press moves the bar. */
export const KeyStep = 16;

/** A bar between two panels. Drag it, or focus it and use the arrow keys, Home and End. A double-click resets it. */
export function Splitter({ label, orientation, spec, size, onSize, growsForward }: Props) {
  const drag = useRef<{ from: number; size: number } | null>(null);
  const sign = growsForward ? 1 : -1;
  const position = (e: PointerEvent) => (orientation === "vertical" ? e.clientX : e.clientY);

  const onKeyDown = (e: KeyboardEvent) => {
    const next = keySize(e.key, orientation, size, spec, sign);
    if (next === null) return;
    e.preventDefault();
    onSize(next);
  };

  return (
    <div
      className={`splitter ${orientation}`}
      role="separator"
      aria-label={label}
      aria-orientation={orientation}
      aria-valuenow={size}
      aria-valuemin={spec.min}
      aria-valuemax={spec.max}
      tabIndex={0}
      onPointerDown={(e) => {
        e.currentTarget.setPointerCapture?.(e.pointerId);
        drag.current = { from: position(e), size };
      }}
      onPointerMove={(e) => drag.current && onSize(drag.current.size + sign * (position(e) - drag.current.from))}
      onPointerUp={() => (drag.current = null)}
      onDoubleClick={() => onSize(spec.initial)}
      onKeyDown={onKeyDown}
    />
  );
}

const forwardKeys: Record<Props["orientation"], [string, string]> = { vertical: ["ArrowRight", "ArrowLeft"], horizontal: ["ArrowDown", "ArrowUp"] };

/** The size after a key press, or null when the key does nothing here. */
function keySize(key: string, orientation: Props["orientation"], size: number, spec: SizeSpec, sign: number): number | null {
  const [forward, back] = forwardKeys[orientation];
  if (key === forward) return size + sign * KeyStep;
  if (key === back) return size - sign * KeyStep;
  if (key === "Home") return spec.min;
  if (key === "End") return spec.max;
  return null;
}

function clamp(value: number, { min, max }: SizeSpec): number {
  return Math.min(Math.max(value, min), max);
}
