/** A problem in plain English, with what the user can do about it. */
export function ErrorPanel({ title, message, actions }: { title: string; message: string; actions: { label: string; run(): void }[] }) {
  return (
    <div className="panel error" role="alert">
      <h1>{title}</h1>
      <p>{message}</p>
      <div className="panel-actions">
        {actions.map((a) => (
          <button key={a.label} className="abtn" onClick={a.run}>
            {a.label}
          </button>
        ))}
      </div>
    </div>
  );
}
