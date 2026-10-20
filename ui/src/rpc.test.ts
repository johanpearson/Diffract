import { describe, expect, it } from "vitest";
import { RpcClient, RpcError } from "./rpc";
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

  it("fails waiting requests when disposed", async () => {
    const client = new RpcClient(new FakeTransport());
    const pending = client.request("never");

    client.dispose();

    await expect(pending).rejects.toThrow("closed");
  });
});
