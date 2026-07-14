import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createEventPump, type EventPump } from '../src/client';

const active: EventPump[] = [];

function newClient(): EventPump {
  const client = createEventPump();
  active.push(client);
  return client;
}

let calls: { url: string; body: Record<string, any>; resolve: () => void }[] = [];
let autoResolve = true;

function fetchMock(url: string | URL, options?: RequestInit): Promise<Response> {
  return new Promise((resolvePromise) => {
    const captured = {
      url: String(url),
      body: options?.body ? JSON.parse(String(options.body)) : {},
      resolve: () => resolvePromise(new Response('{}', { status: 204 })),
    };
    calls.push(captured);
    if (autoResolve) captured.resolve();
  });
}

async function settle(): Promise<void> {
  for (let i = 0; i < 10; i++) await Promise.resolve();
}

const CONFIG = { endpoint: 'https://collect.test', appToken: 'tok-web' };

beforeEach(() => {
  localStorage.clear();
  sessionStorage.clear();
  calls = [];
  autoResolve = true;
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  active.splice(0).forEach((client) => client.destroy());
  vi.unstubAllGlobals();
});

describe('reportError (thin /v1/errors path)', () => {
  it('posts immediately, bypassing the queue and the S4 gate', async () => {
    autoResolve = false; // identity registration hangs — errors must still leave
    const ep = newClient();
    ep.init(CONFIG);
    await settle();

    ep.reportError(new TypeError('x is null'));
    await settle();

    const errorCalls = calls.filter((c) => c.url.includes('/v1/errors'));
    expect(errorCalls).toHaveLength(1);
    const body = errorCalls[0]!.body;
    expect(body.kind).toBe('TypeError');
    expect(body.message).toBe('x is null');
    expect(typeof body.stack).toBe('string');
    expect(body.anonymous_id).toMatch(/^[0-9a-f-]{36}$/);
    expect(body.session_key).toMatch(/^[0-9a-f-]{36}$/);
    expect(body.sdk.name).toBe('event-pump-web');
    // never through the events endpoint
    expect(calls.filter((c) => c.url.includes('/v1/events'))).toHaveLength(0);
  });

  it('wraps non-Error values', async () => {
    const ep = newClient();
    ep.init(CONFIG);
    await settle();
    ep.reportError('plain string failure');
    await settle();
    const body = calls.filter((c) => c.url.includes('/v1/errors'))[0]!.body;
    expect(body.message).toContain('plain string failure');
  });

  // Registration is asserted via spies and the captured handler is invoked
  // directly — dispatching real ErrorEvents trips vitest's own
  // unhandled-page-error detection in CI.
  it('auto-capture is OFF by default', async () => {
    const added = vi.spyOn(window, 'addEventListener');
    const ep = newClient();
    ep.init(CONFIG);
    await settle();

    const hooked = added.mock.calls.map((c) => c[0]);
    expect(hooked).not.toContain('error');
    expect(hooked).not.toContain('unhandledrejection');
  });

  it('captureErrors: true hooks window errors and destroy unhooks', async () => {
    const added = vi.spyOn(window, 'addEventListener');
    const removed = vi.spyOn(window, 'removeEventListener');
    const ep = newClient();
    ep.init({ ...CONFIG, captureErrors: true });
    await settle();

    const errorHook = added.mock.calls.find((c) => c[0] === 'error');
    const rejectionHook = added.mock.calls.find((c) => c[0] === 'unhandledrejection');
    expect(errorHook).toBeDefined();
    expect(rejectionHook).toBeDefined();

    // invoke the captured handler as the browser would
    (errorHook![1] as (e: { error: Error }) => void)({ error: new Error('boom') });
    await settle();
    const errorCalls = calls.filter((c) => c.url.includes('/v1/errors'));
    expect(errorCalls).toHaveLength(1);
    expect(errorCalls[0]!.body.message).toBe('boom');

    ep.destroy();
    expect(removed.mock.calls.map((c) => c[0])).toContain('error');
    expect(removed.mock.calls.map((c) => c[0])).toContain('unhandledrejection');
  });
});
