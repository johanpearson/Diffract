import { describe, expect, it } from "vitest";
import { RpcClient } from "../rpc";
import { FakeSidecar, sampleState } from "../testing/fakeSidecar";
import { readStatus } from "./statusReads";

const worktree = sampleState.repoRoot;

describe("readStatus", () => {
  it("makes one call for reads asked for in the same turn, and a new one for a read asked for later", async () => {
    const sidecar = new FakeSidecar();
    const client = new RpcClient(sidecar.transport);
    const calls = () => sidecar.transport.sent.filter((m) => m.method === "changes.status").length;

    const [a, b] = await Promise.all([readStatus(client, sampleState.repoId, worktree), readStatus(client, sampleState.repoId, worktree.toUpperCase())]);
    expect(calls()).toBe(1);
    expect(b).toBe(a);

    await readStatus(client, sampleState.repoId, worktree);
    expect(calls()).toBe(2);
  });

  it("keeps worktrees and clients apart", async () => {
    const sidecar = new FakeSidecar();
    const one = new RpcClient(sidecar.transport);
    const other = new RpcClient(sidecar.transport);

    await Promise.all([readStatus(one, sampleState.repoId, worktree), readStatus(other, sampleState.repoId, worktree)]);

    expect(sidecar.transport.sent.filter((m) => m.method === "changes.status")).toHaveLength(2);
  });
});
