import type { ErrorKind, Methods, Notifications } from "./protocol";

/** Carries one JSON-RPC message per call. The Tauri transport relays them to and from the sidecar. */
export interface Transport {
  send(message: string): Promise<void>;
  /** Calls the handler for every message from the sidecar. Returns a function that stops it. */
  onMessage(handler: (message: string) => void): () => void;
}

/** The JSON-RPC error codes the UI acts on. */
export const ErrorCodes = {
  /** The request was cancelled, by the UI or because the sidecar is shutting down. */
  RequestCancelled: -32800,
} as const;

export class RpcError extends Error {
  readonly code: number;
  /** Set on Diffract's own errors, such as "NotARepo". */
  readonly kind: ErrorKind | undefined;

  constructor(code: number, message: string, kind?: ErrorKind) {
    super(message);
    this.name = "RpcError";
    this.code = code;
    this.kind = kind;
  }
}

export interface RequestOptions {
  /** Aborting cancels the request: the sidecar is told to stop, and the promise rejects at once. */
  signal?: AbortSignal;
}

interface Pending {
  resolve(result: unknown): void;
  reject(error: Error): void;
}

interface Incoming {
  id?: unknown;
  method?: unknown;
  params?: unknown;
  result?: unknown;
  error?: { code: number; message: string; data?: { kind?: ErrorKind } };
}

/** A JSON-RPC 2.0 client: numbers each request and matches responses to it by id. */
export class RpcClient {
  private nextId = 1;
  private readonly pending = new Map<number, Pending>();
  private readonly listeners = new Map<string, Set<(params: never) => void>>();
  private readonly stop: () => void;
  private readonly transport: Transport;

  constructor(transport: Transport) {
    this.transport = transport;
    this.stop = transport.onMessage((message) => this.receive(message));
  }

  /** A request to one of the sidecar's methods, with its params and result typed. */
  call<M extends keyof Methods>(method: M, params: Methods[M]["params"], options?: RequestOptions): Promise<Methods[M]["result"]> {
    return this.request<Methods[M]["result"]>(method, params, options);
  }

  request<T>(method: string, params?: unknown, options: RequestOptions = {}): Promise<T> {
    const { signal } = options;
    if (signal?.aborted) return Promise.reject(cancelled());

    const id = this.nextId++;
    const message = JSON.stringify(params === undefined ? { jsonrpc: "2.0", id, method } : { jsonrpc: "2.0", id, method, params });
    return new Promise<T>((resolve, reject) => {
      const onAbort = () => {
        if (!this.pending.delete(id)) return;
        reject(cancelled());
        this.transport.send(JSON.stringify({ jsonrpc: "2.0", method: "$/cancelRequest", params: { id } })).catch(() => {});
      };
      const settle = () => signal?.removeEventListener("abort", onAbort);

      this.pending.set(id, {
        resolve: (result) => (settle(), resolve(result as T)),
        reject: (error) => (settle(), reject(error)),
      });
      signal?.addEventListener("abort", onAbort, { once: true });
      this.transport.send(message).catch((error: unknown) => {
        if (this.pending.delete(id)) {
          settle();
          reject(error instanceof Error ? error : new Error(String(error)));
        }
      });
    });
  }

  /** Calls the handler for every notification of this kind. Returns a function that stops it. */
  onNotification<N extends keyof Notifications>(method: N, handler: (params: Notifications[N]) => void): () => void {
    let handlers = this.listeners.get(method);
    if (!handlers) this.listeners.set(method, (handlers = new Set()));
    handlers.add(handler);
    return () => handlers.delete(handler);
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
    let parsed: Incoming;
    try {
      parsed = JSON.parse(message) as Incoming;
    } catch {
      return;
    }

    if (parsed.id === undefined && typeof parsed.method === "string") {
      for (const handler of this.listeners.get(parsed.method) ?? []) handler(parsed.params as never);
      return;
    }
    if (typeof parsed.id !== "number") return;

    const pending = this.pending.get(parsed.id);
    if (!pending) return;
    this.pending.delete(parsed.id);
    if (parsed.error) pending.reject(new RpcError(parsed.error.code, parsed.error.message, parsed.error.data?.kind));
    else pending.resolve(parsed.result);
  }
}

function cancelled(): RpcError {
  return new RpcError(ErrorCodes.RequestCancelled, "The request was cancelled");
}
