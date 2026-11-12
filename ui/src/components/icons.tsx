import type { ReactNode } from "react";

/** Small inline SVG icons, drawn on a 16px grid in the current text colour. No icon package is needed. */
function Icon({ children }: { children: ReactNode }) {
  return (
    <svg width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" focusable="false">
      {children}
    </svg>
  );
}

/** Fetch: two arrows going round, as in "refresh from the remote". */
export const FetchIcon = () => (
  <Icon>
    <path d="M13.5 6A5.5 5.5 0 0 0 3.6 4.4M2.5 10a5.5 5.5 0 0 0 9.9 1.6" />
    <path d="M13.8 2.5V6H10.3M2.2 13.5V10h3.5" />
  </Icon>
);

/** Prune: a pair of shears. */
export const PruneIcon = () => (
  <Icon>
    <circle cx="4" cy="4" r="1.7" />
    <circle cx="4" cy="12" r="1.7" />
    <path d="M5.4 5 13.5 12M5.4 11 13.5 4" />
  </Icon>
);

export const PullIcon = () => (
  <Icon>
    <path d="M8 2.5v9M4.5 8 8 11.5 11.5 8M3 14h10" />
  </Icon>
);

export const PushIcon = () => (
  <Icon>
    <path d="M8 13.5v-9M4.5 8 8 4.5 11.5 8M3 2h10" />
  </Icon>
);

export const FolderIcon = () => (
  <Icon>
    <path d="M1.8 4.2c0-.7.5-1.2 1.2-1.2h3l1.5 1.7H13c.7 0 1.2.5 1.2 1.2v6.1c0 .7-.5 1.2-1.2 1.2H3c-.7 0-1.2-.5-1.2-1.2z" />
  </Icon>
);

export const InfoIcon = () => (
  <Icon>
    <circle cx="8" cy="8" r="6" />
    <path d="M8 7.2v3.6M8 5.1v.1" />
  </Icon>
);
