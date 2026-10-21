import { useCallback, useEffect, useState } from "react";
import type { GitUpdateRun } from "../components/GitUpdateBar";
import type { AppHost } from "../host";
import type { GitInUse, GitUpdateCheck, GitUpdateResult } from "../protocol";
import type { RpcClient } from "../rpc";

/** The storage key for the last answer from winget, so it is asked at most once a day. */
export const GitUpdateCheckKey = "diffract.gitUpdateCheck";

export const OneDayMs = 24 * 60 * 60 * 1000;

/** The confirm dialog: the version offered, and who is using git. `inUse` is null while Diffract is looking. */
export interface GitUpdateConfirm {
  version: string;
  inUse: GitInUse[] | null;
}

/**
 * Checking for, confirming and running a Git for Windows update. Asking winget starts processes, which is slow on
 * a busy machine, so a known answer is reused for a day unless git itself changed.
 */
export function useGitUpdate(client: RpcClient, host: AppHost, clock: () => Date) {
  const [check, setCheck] = useState<GitUpdateCheck | null>(null);
  const [checking, setChecking] = useState(false);
  const [confirm, setConfirm] = useState<GitUpdateConfirm | null>(null);
  const [run, setRun] = useState<GitUpdateRun | null>(null);

  /** `force` asks winget even when a recent answer is remembered. Null when winget could not be asked. */
  const checkForUpdate = useCallback(
    async (gitVersion: string | null, force: boolean): Promise<GitUpdateCheck | null> => {
      const remembered = force ? null : rememberedCheck(host, gitVersion, clock());
      if (remembered) {
        setCheck(remembered);
        return remembered;
      }
      setChecking(true);
      try {
        const answer = await client.call("git.checkUpdate", undefined);
        setCheck(answer);
        // "Unknown" is often a passing problem, such as no network, so it is asked again next time.
        if (answer.status !== "Unknown") host.storage.set(GitUpdateCheckKey, JSON.stringify({ checkedAt: clock().getTime(), check: answer }));
        return answer;
      } catch {
        return null;
      } finally {
        setChecking(false);
      }
    },
    [client, host, clock],
  );

  const findUsers = useCallback(() => {
    setConfirm((c) => c && { ...c, inUse: null });
    client.call("git.inUse", undefined).then(
      (inUse) => setConfirm((c) => c && { ...c, inUse }),
      // Not knowing only means no warning. The update still says who used git if they stop it.
      () => setConfirm((c) => c && { ...c, inUse: [] }),
    );
  }, [client]);

  const askToUpdate = useCallback(
    (version: string) => {
      setConfirm({ version, inUse: null });
      findUsers();
    },
    [findUsers],
  );

  /** Runs the confirmed update. `then` runs when it has ended, whatever the outcome. */
  const update = useCallback(
    async (version: string, then: () => void) => {
      setConfirm(null);
      setRun({ kind: "running", version, stage: "Downloading", startedAt: clock().getTime() });
      let result: GitUpdateResult;
      try {
        result = await client.call("git.update", { version });
      } catch (error) {
        result = { outcome: "Failed", message: error instanceof Error ? error.message : String(error), inUse: [] };
      }
      setRun({ kind: "done", version, result });
      then();
    },
    [client, clock],
  );

  useEffect(
    () => client.onNotification("git.updateProgress", ({ stage }) => setRun((r) => (r?.kind === "running" ? { ...r, stage } : r))),
    [client],
  );

  return {
    check,
    checking,
    confirm,
    run,
    running: run?.kind === "running",
    checkForUpdate,
    askToUpdate,
    findUsers,
    cancelConfirm: () => setConfirm(null),
    update,
    dismissRun: () => setRun(null),
  };
}

/** The remembered answer, when it is less than a day old and about the git Diffract uses now. */
function rememberedCheck(host: AppHost, gitVersion: string | null, now: Date): GitUpdateCheck | null {
  try {
    const stored = JSON.parse(host.storage.get(GitUpdateCheckKey) ?? "null") as { checkedAt?: number; check?: GitUpdateCheck } | null;
    const fresh = typeof stored?.checkedAt === "number" && now.getTime() - stored.checkedAt < OneDayMs && stored.checkedAt <= now.getTime();
    return fresh && stored?.check && stored.check.installedVersion === gitVersion ? stored.check : null;
  } catch {
    return null;
  }
}
