import type { Transport } from "../rpc";

/**
 * A transport for tests. Every request is passed to `answer`; whatever it returns is sent back as the result,
 * and a thrown { code, message } becomes a JSON-RPC error. Return undefined to leave a request unanswered.
 */
export class FakeTransport implements Transport {
  readonly sent: { id: number; method: string; params?: unknown }[] = [];
  private readonly handlers = new Set<(message: string) => void>();
  private readonly answer: (method: string, params: unknown) => unknown;

  constructor(answer: (method: string, params: unknown) => unknown = () => undefined) {
    this.answer = answer;
  }

  async send(message: string): Promise<void> {
    const request = JSON.parse(message) as { id: number; method: string; params?: unknown };
    this.sent.push(request);
    let reply: object | undefined;
    try {
      const result = this.answer(request.method, request.params);
      if (result !== undefined) reply = { jsonrpc: "2.0", id: request.id, result };
    } catch (error) {
      reply = { jsonrpc: "2.0", id: request.id, error };
    }
    if (reply) queueMicrotask(() => this.emit(JSON.stringify(reply)));
  }

  onMessage(handler: (message: string) => void): () => void {
    this.handlers.add(handler);
    return () => this.handlers.delete(handler);
  }

  /** Delivers a raw line, as if the sidecar had printed it. */
  emit(message: string): void {
    for (const handler of this.handlers) handler(message);
  }
}
