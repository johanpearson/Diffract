/** A problem in plain English, with what the user can do about it. */
/** `detail` is a second, smaller paragraph, such as why git cannot be updated from here. */
export function ErrorPanel({ title, message, detail, actions }: { title: string; message: string; detail?: string | null; actions: { label: string; run(): void }[] }) {
  return (
    <div className="panel error" role="alert">
      <h1>{title}</h1>
      <p>{message}</p>
      {detail && <p className="detail">{detail}</p>}
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
