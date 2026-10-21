/** Result of `app.info`. Mirrors AppInfo in the sidecar. */
export interface AppInfo {
  sidecarVersion: string;
  /** Null when git could not be run. */
  gitVersion: string | null;
  gitSupported: boolean;
  /** Why Diffract cannot use this git, in plain English. Null when it can. */
  gitProblem: string | null;
}
