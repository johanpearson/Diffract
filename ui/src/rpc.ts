/** Carries one JSON-RPC message per call. The Tauri transport relays them to and from the sidecar. */
export interface Transport {
  send(message: string): Promise<void>;
  /** Calls the handler for every message from the sidecar. Returns a function that stops it. */
  onMessage(handler: (message: string) => void): () => void;
}

export class RpcError extends Error {
  readonly code: number;

  constructor(code: number, message: string) {
    super(message);
    this.name = "RpcError";
    this.code = code;
  }
}

interface Pending {
  resolve(result: unknown): void;
  reject(error: Error): void;
}

/** A JSON-RPC 2.0 client: numbers each request and matches responses to it by id. */
export class RpcClient {
  private nextId = 1;
  private readonly pending = new Map<number, Pending>();
  private readonly stop: () => void;
  private readonly transport: Transport;

  constructor(transport: Transport) {
    this.transport = transport;
    this.stop = transport.onMessage((message) => this.receive(message));
  }

  request<T>(method: string, params?: unknown): Promise<T> {
    const id = this.nextId++;
    const message = JSON.stringify(params === undefined ? { jsonrpc: "2.0", id, method } : { jsonrpc: "2.0", id, method, params });
    return new Promise<T>((resolve, reject) => {
      this.pending.set(id, { resolve: (result) => resolve(result as T), reject });
      this.transport.send(message).catch((error: unknown) => {
        this.pending.delete(id);
        reject(error instanceof Error ? error : new Error(String(error)));
      });
    });
  }

  /** Fails every request still waiting, for example when the sidecar has stopped. */
  failAll(reason: string): void {
    for (const pending of this.pending.values()) pending.reject(new Error(reason));
    this.pending.clear();
  }

  dispose(): void {
    this.stop();
    this.failAll("The connection to the sidecar was closed.");
  }

  private receive(message: string): void {
    let parsed: { id?: unknown; result?: unknown; error?: { code: number; message: string } };
    try {
      parsed = JSON.parse(message);
    } catch {
      return;
    }
    if (typeof parsed.id !== "number") return;

    const pending = this.pending.get(parsed.id);
    if (!pending) return;
    this.pending.delete(parsed.id);
    if (parsed.error) pending.reject(new RpcError(parsed.error.code, parsed.error.message));
    else pending.resolve(parsed.result);
  }
}
