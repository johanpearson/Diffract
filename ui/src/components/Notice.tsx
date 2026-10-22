/** A message the user can dismiss. Nothing when there is no message. */
export function Notice({ message, onDismiss }: { message: string | null; onDismiss(): void }) {
  if (!message) return null;
  return (
    <div className="notice" role="status">
      <span>{message}</span>
      <button className="abtn" onClick={onDismiss}>
        Dismiss
      </button>
    </div>
  );
}
