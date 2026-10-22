import { useCallback, useEffect, useState } from "react";
import type { AppInfo } from "../protocol";
import type { Session } from "./session";
import { afterUpdate, startApp, type AppStart } from "./startup";

/** Starts the app, again each time `starts` goes up, and checks git again after an update. */
export function useAppStart(session: Session, starts: number, deps: Pick<AppStart, "checkForUpdate" | "setTerminals">) {
  const [info, setInfo] = useState<AppInfo | null>(null);
  const { checkForUpdate, setTerminals } = deps;

  useEffect(() => {
    let current = true;
    startApp(session, { setInfo, setTerminals, checkForUpdate, isCurrent: () => current });
    return () => {
      current = false;
    };
  }, [session, starts, checkForUpdate, setTerminals]);

  const wasSupported = info?.gitSupported ?? false;
  const updated = useCallback(() => afterUpdate(session, { setInfo, checkForUpdate }, wasSupported), [session, checkForUpdate, wasSupported]);

  return { info, afterUpdate: updated };
}
