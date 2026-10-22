import type { Dispatch, SetStateAction } from "react";
import type { AppInfo, GitUpdateCheck, TerminalInfo } from "../protocol";
import { messageOf } from "./format";
import { LastRepoKey, openRepo, refreshRepo, restart, type Session } from "./session";

/** What starting the app sets besides the repo session. */
export interface AppStart {
  setInfo: Dispatch<SetStateAction<AppInfo | null>>;
  setTerminals(list: TerminalInfo[]): void;
  /** `force` asks winget even when a recent answer is remembered. */
  checkForUpdate(gitVersion: string | null, force: boolean): Promise<GitUpdateCheck | null>;
  /** False once this start is over, because the app started again or closed. */
  isCurrent(): boolean;
}

/** Start: check git, then open the last repo again. */
export function startApp(session: Session, app: AppStart): void {
  session.setScreen({ kind: "starting" });
  session.client
    .call("app.info", undefined)
    .then((appInfo) => {
      if (app.isCurrent()) showStart(session, app, appInfo);
    })
    .catch((error: unknown) => {
      if (app.isCurrent()) session.setScreen({ kind: "failed", title: "The sidecar did not answer", message: messageOf(error), path: null });
    });
}

function showStart(session: Session, app: AppStart, appInfo: AppInfo): void {
  app.setInfo(appInfo);
  // Not knowing the terminals only means "Open terminal" uses the default one.
  session.client.call("system.listTerminals", undefined).then((list) => app.isCurrent() && app.setTerminals(list), () => {});
  // A git that is too old is the only thing on screen, so its update is checked at once. Otherwise winget is
  // asked only once the repo is open, at most once a day: it starts processes, which slows opening the repo.
  if (!appInfo.gitSupported) {
    void app.checkForUpdate(appInfo.gitVersion, true);
    session.setScreen({ kind: "gitProblem", problem: appInfo.gitProblem ?? "This git cannot be used." });
    return;
  }
  openLastRepo(session, app, appInfo.gitVersion);
}

function openLastRepo(session: Session, app: AppStart, gitVersion: string | null): void {
  const last = session.host.storage.get(LastRepoKey);
  if (last)
    void openRepo(session, last).then(() => {
      if (app.isCurrent()) void app.checkForUpdate(gitVersion, false);
    });
  else {
    session.setScreen({ kind: "welcome" });
    void app.checkForUpdate(gitVersion, false);
  }
}

/**
 * After an update, whatever its outcome: the git now installed, a new update check, and the repo refreshed,
 * since live refresh was paused meanwhile. A git that was too old and now works starts the app over.
 */
export async function afterUpdate(session: Session, app: Pick<AppStart, "setInfo" | "checkForUpdate">, wasSupported: boolean): Promise<void> {
  try {
    const appInfo = await session.client.call("app.info", undefined);
    app.setInfo(appInfo);
    void app.checkForUpdate(appInfo.gitVersion, true);
    if (appInfo.gitSupported && !wasSupported) restart(session);
    else if (appInfo.gitSupported) await refreshRepo(session, false);
  } catch (error) {
    session.showNotice(`Could not check git after the update: ${messageOf(error)}`);
  }
}
