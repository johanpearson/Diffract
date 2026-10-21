import { describe, expect, it } from "vitest";
import { ErrorCodes, RpcClient, RpcError } from "./rpc";
import { FakeTransport } from "./testing/fakeTransport";

describe("RpcClient", () => {
  it("sends JSON-RPC 2.0 requests with increasing ids", async () => {
    const transport = new FakeTransport(() => "ok");
    const client = new RpcClient(transport);

    await client.request("a");
    await client.request("b", { path: "C:\\src" });

    expect(transport.sent).toEqual([
      { jsonrpc: "2.0", id: 1, method: "a" },
      { jsonrpc: "2.0", id: 2, method: "b", params: { path: "C:\\src" } },
    ]);
  });

  it("matches responses to requests by id, in any order", async () => {
    const transport = new FakeTransport();
    const client = new RpcClient(transport);

    const first = client.request("first");
    const second = client.request("second");
    transport.emit('{"jsonrpc":"2.0","id":2,"result":"two"}');
    transport.emit('{"jsonrpc":"2.0","id":1,"result":"one"}');

    expect(await first).toBe("one");
    expect(await second).toBe("two");
  });

  it("turns a JSON-RPC error into an RpcError", async () => {
    const client = new RpcClient(new FakeTransport(() => {
      throw { code: -32601, message: "Unknown method 'nope'" };
    }));

    const error = await client.request("nope").catch((e: unknown) => e);

    expect(error).toBeInstanceOf(RpcError);
    expect(error).toMatchObject({ code: -32601, message: "Unknown method 'nope'" });
  });

  it("ignores lines that are not responses to its requests", async () => {
    const transport = new FakeTransport();
    const client = new RpcClient(transport);
    const pending = client.request("slow");

    transport.emit("not json");
    transport.emit('{"jsonrpc":"2.0","method":"repo.changed","params":{}}');
    transport.emit('{"jsonrpc":"2.0","id":99,"result":"someone else"}');
    transport.emit('{"jsonrpc":"2.0","id":1,"result":"mine"}');

    expect(await pending).toBe("mine");
  });

  it("rejects a request the transport could not send", async () => {
    const client = new RpcClient({
      send: () => Promise.reject(new Error("The sidecar is not running")),
      onMessage: () => () => {},
    });

    await expect(client.request("app.info")).rejects.toThrow("The sidecar is not running");
  });

  it("cancels a request when its signal aborts, and tells the sidecar", async () => {
    const transport = new FakeTransport();
    const client = new RpcClient(transport);
    const abort = new AbortController();
    const pending = client.request("repo.open", { path: "C:\src" }, { signal: abort.signal }).catch((e: unknown) => e);

    abort.abort();

    expect(await pending).toMatchObject({ code: ErrorCodes.RequestCancelled });
    expect(transport.sent.at(-1)).toEqual({ jsonrpc: "2.0", method: "$/cancelRequest", params: { id: 1 } });
  });

  it("ignores the sidecar's late answer to a cancelled request", async () => {
    const transport = new FakeTransport();
    const client = new RpcClient(transport);
    const abort = new AbortController();
    const pending = client.request("slow", undefined, { signal: abort.signal }).catch((e: unknown) => e);
    abort.abort();

    transport.emit('{"jsonrpc":"2.0","id":1,"error":{"code":-32800,"message":"The request was cancelled"}}');

    expect(await pending).toMatchObject({ code: ErrorCodes.RequestCancelled, message: "The request was cancelled" });
  });

  it("does not send a request whose signal has already aborted", async () => {
    const transport = new FakeTransport();
    const abort = new AbortController();
    abort.abort();

    await expect(new RpcClient(transport).request("app.info", undefined, { signal: abort.signal })).rejects.toBeInstanceOf(RpcError);
    expect(transport.sent).toEqual([]);
  });

  it("does not cancel a request that has already been answered", async () => {
    const transport = new FakeTransport(() => "done");
    const abort = new AbortController();

    expect(await new RpcClient(transport).request("app.info", undefined, { signal: abort.signal })).toBe("done");
    abort.abort();

    expect(transport.sent.map((m) => m.method)).toEqual(["app.info"]);
  });

  it("hands notifications to the listeners for that method until they stop", () => {
    const transport = new FakeTransport();
    const client = new RpcClient(transport);
    const received: unknown[] = [];
    const stop = client.onNotification("repo.changed", (params) => received.push(params));

    transport.emit('{"jsonrpc":"2.0","method":"repo.changed","params":{"n":1}}');
    transport.emit('{"jsonrpc":"2.0","method":"something.else","params":{"n":2}}');
    stop();
    transport.emit('{"jsonrpc":"2.0","method":"repo.changed","params":{"n":3}}');

    expect(received).toEqual([{ n: 1 }]);
  });

  it("calls methods with typed params", async () => {
    const transport = new FakeTransport(() => null);

    await new RpcClient(transport).call("repo.refresh", { repoId: "1", includeUnreachable: true });

    expect(transport.sent).toEqual([{ jsonrpc: "2.0", id: 1, method: "repo.refresh", params: { repoId: "1", includeUnreachable: true } }]);
  });

  it("fails waiting requests when disposed", async () => {
    const client = new RpcClient(new FakeTransport());
    const pending = client.request("never");

    client.dispose();

    await expect(pending).rejects.toThrow("closed");
  });
});
