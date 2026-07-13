import { createEventPump, type EventPump } from './client';

/**
 * IIFE entry (`ep.js`). The async stub snippet (see README) creates
 * window.ep = {q: []} and shims the public methods to push [method, args].
 * On load we replace the stub with the real client and drain the queue in
 * order — pre-load calls (including setUser) flow through S0–S4 (SPEC §7).
 */
declare global {
  interface Window {
    ep?: EventPump | { q?: [string, unknown[]][] };
  }
}

const stub = window.ep as { q?: [string, unknown[]][] } | undefined;
const client = createEventPump();
window.ep = client;

if (stub?.q) {
  for (const [method, args] of stub.q) {
    const fn = (client as unknown as Record<string, unknown>)[method];
    if (typeof fn === 'function') {
      try {
        (fn as (...a: unknown[]) => void).apply(client, args);
      } catch {
        /* a bad queued call must not break the drain */
      }
    }
  }
}

export {};
