import type { ReactNode } from "react";

interface Props {
  /** The accessible name, and the tooltip unless `title` says more. */
  label: string;
  /** The tooltip, when it should say more than the name, such as why the button is off. */
  title?: string;
  icon: ReactNode;
  disabled?: boolean;
  pressed?: boolean;
  onClick(): void;
}

/** A button with an icon and no text. The text is its accessible name and its tooltip. */
export function IconButton({ label, title, icon, disabled = false, pressed, onClick }: Props) {
  return (
    <button className="abtn icon" aria-label={label} aria-pressed={pressed} title={title ?? label} disabled={disabled} onClick={onClick}>
      {icon}
    </button>
  );
}
