import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

let calls: { url: string; body: Record<string, any> }[] = [];

function fetchMock(url: string | URL, options?: RequestInit): Promise<Response> {
  calls.push({ url: String(url), body: options?.body ? JSON.parse(String(options.body)) : {} });
  return Promise.resolve(new Response('{"accepted":1,"rejected":[]}', { status: 200 }));
}

async function settle(): Promise<void> {
  for (let i = 0; i < 10; i++) await Promise.resolve();
}

beforeEach(() => {
  localStorage.clear();
  sessionStorage.clear();
  calls = [];
  vi.stubGlobal('fetch', fetchMock);
  vi.resetModules();
});

afterEach(() => vi.unstubAllGlobals());

describe('IIFE stub drain (SPEC §7)', () => {
  it('drains pre-load calls in order through S0–S4, including setUser', async () => {
    (window as any).ep = {
      q: [
        ['init', [{ endpoint: 'https://collect.test', appToken: 'tok-web' }]],
        ['setUser', ['u-1']],
        ['track', ['product_viewed', { sku: 'A1' }]],
      ],
    };

    await import('../src/iife');
    await settle();
    (window as any).ep.flush();
    await settle();

    expect(typeof (window as any).ep.track).toBe('function');

    const identity = calls.filter((c) => c.url.includes('/v1/identity'));
    const events = calls.filter((c) => c.url.includes('/v1/events'));
    expect(identity.length).toBeGreaterThanOrEqual(1);
    // setUser drained BEFORE registration materialized -> user_id present
    expect(identity[0]!.body.user_id).toBe('u-1');
    // identity registered before any events left
    expect(calls[0]!.url).toContain('/v1/identity');
    const sent = events.flatMap((c) => c.body.events);
    expect(sent.map((e: any) => e.event_name)).toContain('product_viewed');
    expect(sent[0].user_id).toBe('u-1');
  });
});
