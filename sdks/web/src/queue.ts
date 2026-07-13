import { local, readJson, writeJson } from './storage';

const QUEUE_KEY = 'ep_queue';

export interface QueuedEnvelope {
  event: Record<string, unknown>;
  /** For the 24h give-up rule (SPEC §7). Never transmitted. */
  firstAttemptAt: number;
}

/**
 * Local event buffer (SPEC §7): persists across restarts in localStorage,
 * cap 200 with oldest-first drop, removed only after a 2xx (at-least-once;
 * the server dedupes on event_id).
 */
export class EventQueue {
  private items: QueuedEnvelope[] = [];

  constructor(private readonly cap = 200) {}

  restore(): void {
    this.items = readJson<QueuedEnvelope[]>(local(), QUEUE_KEY) ?? [];
  }

  push(event: Record<string, unknown>, now: number): void {
    this.items.push({ event, firstAttemptAt: now });
    if (this.items.length > this.cap) this.items.splice(0, this.items.length - this.cap);
    this.persist();
  }

  peek(max: number): QueuedEnvelope[] {
    return this.items.slice(0, max);
  }

  ack(envelopes: QueuedEnvelope[]): void {
    const sent = new Set(envelopes);
    this.items = this.items.filter((item) => !sent.has(item));
    this.persist();
  }

  size(): number {
    return this.items.length;
  }

  private persist(): void {
    writeJson(local(), QUEUE_KEY, this.items);
  }
}
