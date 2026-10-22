import { useCallback, useState } from "react";
import type { AppHost } from "../host";
import type { GitInUse, GitUpdateCheck, GitUpdateResult } from "../protocol";
import type { RpcClient } from "../rpc";
import { messageOf } from "./format";

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
  const [confirm, setConfirm] = useState<GitUpdateConfirm | null>(null);
  const [running, setRunning] = useState(false);
  // Kept only when the user has something to do: the update failed, or Windows must restart. A good update shows
  // in the git version in the top bar, and the installer shows its own window meanwhile.
  const [result, setResult] = useState<GitUpdateResult | null>(null);

  /** `force` asks winget even when a recent answer is remembered. Null when winget could not be asked. */
  const checkForUpdate = useCallback(
    async (gitVersion: string | null, force: boolean): Promise<GitUpdateCheck | null> => {
      const remembered = force ? null : rememberedCheck(host, gitVersion, clock());
      if (!remembered) return askWinget({ client, host, clock, setCheck });
      setCheck(remembered);
      return remembered;
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
      setResult(null);
      setRunning(true);
      const answer = await runUpdate(client, version);
      setRunning(false);
      if (answer.outcome !== "Updated") setResult(answer);
      then();
    },
    [client],
  );

  return {
    check,
    confirm,
    result,
    running,
    checkForUpdate,
    askToUpdate,
    findUsers,
    cancelConfirm: () => setConfirm(null),
    update,
    dismissResult: () => setResult(null),
  };
}

export type GitUpdate = ReturnType<typeof useGitUpdate>;

/** The newer Git for Windows that winget can install, or null. */
export function offeredVersion(check: GitUpdateCheck | null): string | null {
  return check?.status === "UpdateAvailable" ? check.availableVersion : null;
}

/** Asks winget, and remembers the answer. Null when winget could not be asked. */
async function askWinget({ client, host, clock, setCheck }: {
  client: RpcClient;
  host: AppHost;
  clock: () => Date;
  setCheck(check: GitUpdateCheck): void;
}): Promise<GitUpdateCheck | null> {
  try {
    const answer = await client.call("git.checkUpdate", undefined);
    setCheck(answer);
    // "Unknown" is often a passing problem, such as no network, so it is asked again next time.
    if (answer.status !== "Unknown") host.storage.set(GitUpdateCheckKey, JSON.stringify({ checkedAt: clock().getTime(), check: answer }));
    return answer;
  } catch {
    return null;
  }
}

async function runUpdate(client: RpcClient, version: string): Promise<GitUpdateResult> {
  try {
    return await client.call("git.update", { version });
  } catch (error) {
    return { outcome: "Failed", message: messageOf(error), inUse: [] };
  }
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
