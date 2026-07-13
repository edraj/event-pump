import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createEventPump, type EventPump } from '../src/client';

const active: EventPump[] = [];

function newClient(): EventPump {
  const client = createEventPump();
  active.push(client);
  return client;
}

interface Captured {
  url: string;
  body: Record<string, any>;
  resolve: (ok?: boolean) => void;
}

let calls: Captured[] = [];
let autoResolve = true;

function fetchMock(url: string | URL, options?: RequestInit): Promise<Response> {
  return new Promise((resolvePromise) => {
    const captured: Captured = {
      url: String(url),
      body: options?.body ? JSON.parse(String(options.body)) : {},
      resolve: (ok = true) =>
        resolvePromise(
          new Response(ok ? '{"accepted":1,"rejected":[]}' : '{"error":"x"}', {
            status: ok ? 200 : 500,
          }),
        ),
    };
    calls.push(captured);
    if (autoResolve) captured.resolve();
  });
}

function identityCalls(): Captured[] {
  return calls.filter((c) => c.url.includes('/v1/identity'));
}

function eventCalls(): Captured[] {
  return calls.filter((c) => c.url.includes('/v1/events'));
}

async function settle(): Promise<void> {
  for (let i = 0; i < 10; i++) await Promise.resolve();
}

const CONFIG = { endpoint: 'https://collect.test', appToken: 'tok-web' };

function clearCookies(): void {
  for (const pair of document.cookie.split(';')) {
    const name = pair.split('=')[0]?.trim();
    if (name) document.cookie = `${name}=; expires=Thu, 01 Jan 1970 00:00:00 GMT`;
  }
}

beforeEach(() => {
  vi.useFakeTimers({ now: 1_700_000_000_000 });
  localStorage.clear();
  sessionStorage.clear();
  clearCookies();
  calls = [];
  autoResolve = true;
  vi.stubGlobal('fetch', fetchMock);
  Object.defineProperty(document, 'visibilityState', { value: 'visible', configurable: true });
});

afterEach(() => {
  active.splice(0).forEach((client) => client.destroy());
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('S0–S4 ordering (SPEC §3)', () => {
  it('registers identity before any events are sent, and buffered events flush after S3', async () => {
    autoResolve = false;
    const ep = newClient();
    ep.init(CONFIG);
    ep.track('product_viewed', { sku: 'A1' });
    await settle();

    expect(identityCalls()).toHaveLength(1);
    expect(eventCalls()).toHaveLength(0); // never sent before S3 completes

    identityCalls()[0]!.resolve();
    await settle();
    ep.flush();
    await settle();

    expect(eventCalls()).toHaveLength(1);
    const sent = eventCalls()[0]!.body.events;
    expect(sent).toHaveLength(1);
    expect(sent[0].event_name).toBe('product_viewed');
    expect(sent[0].properties).toEqual({ sku: 'A1' });
    // identity registration carried the same ids the event uses
    const identity = identityCalls()[0]!.body;
    expect(sent[0].anonymous_id).toBe(identity.anonymous_id);
    expect(sent[0].session_key).toBe(identity.session_key);
    expect(identity.session_number).toBe(1);
    expect(identity.handles.amplitude_device_id).toBe(identity.anonymous_id);
  });

  it('uses the ep_aid cookie and never writes cookies itself', async () => {
    document.cookie = 'ep_aid=0f2937de-92f9-4b6c-a222-abcdefabcdef';
    const ep = newClient();
    ep.init(CONFIG);
    await settle();
    expect(identityCalls()[0]!.body.anonymous_id).toBe('0f2937de-92f9-4b6c-a222-abcdefabcdef');
    expect(document.cookie).toBe('ep_aid=0f2937de-92f9-4b6c-a222-abcdefabcdef');
  });
});

describe('session rotation (SPEC §3)', () => {
  it('rotates on visible return after 30 minutes and re-registers', async () => {
    const ep = newClient();
    ep.init(CONFIG);
    await settle();
    const firstKey = identityCalls()[0]!.body.session_key;

    vi.advanceTimersByTime(31 * 60_000);
    document.dispatchEvent(new Event('visibilitychange')); // visible
    await settle();

    expect(identityCalls()).toHaveLength(2);
    const second = identityCalls()[1]!.body;
    expect(second.session_key).not.toBe(firstKey);
    expect(second.session_number).toBe(2);
    expect(second.anonymous_id).toBe(identityCalls()[0]!.body.anonymous_id);
  });

  it('does not rotate within the window', async () => {
    const ep = newClient();
    ep.init(CONFIG);
    await settle();
    ep.track('product_viewed'); // touches last_active_at
    vi.advanceTimersByTime(5 * 60_000);
    document.dispatchEvent(new Event('visibilitychange'));
    await settle();
    expect(identityCalls()).toHaveLength(1);
  });
});

describe('engagement time (SPEC §4)', () => {
  it('each event carries time accumulated since the previous event', async () => {
    const ep = newClient();
    ep.init(CONFIG);
    await settle();

    vi.advanceTimersByTime(1_200);
    ep.track('product_viewed');
    vi.advanceTimersByTime(3_400);
    ep.track('product_viewed');
    ep.flush();
    await settle();

    const events = eventCalls().flatMap((c) => c.body.events);
    expect(events[0].context.engagement_time_msec).toBe(1_200);
    expect(events[1].context.engagement_time_msec).toBe(3_400);
    expect(events[0].context.session_number).toBe(1);
    expect(events[0].context.sdk.name).toBe('event-pump-web');
  });
});

describe('user transitions (SPEC §3)', () => {
  it('setUser re-registers with user_id and NEVER rotates anonymous_id', async () => {
    const ep = newClient();
    ep.init(CONFIG);
    await settle();
    const before = identityCalls()[0]!.body;

    ep.setUser('u-42');
    await settle();

    const after = identityCalls()[1]!.body;
    expect(after.user_id).toBe('u-42');
    expect(after.anonymous_id).toBe(before.anonymous_id);
    expect(after.session_key).toBe(before.session_key);

    ep.track('product_viewed');
    ep.flush();
    await settle();
    expect(eventCalls().at(-1)!.body.events[0].user_id).toBe('u-42');
  });

  it('clearUser drops user_id and rotates the session only', async () => {
    const ep = newClient();
    ep.init(CONFIG);
    await settle();
    ep.setUser('u-42');
    await settle();
    const before = identityCalls().at(-1)!.body;

    ep.clearUser();
    await settle();

    const after = identityCalls().at(-1)!.body;
    expect(after.user_id).toBeUndefined();
    expect(after.session_key).not.toBe(before.session_key);
    expect(after.session_number).toBe(2);
    expect(after.anonymous_id).toBe(before.anonymous_id);
  });
});

describe('flush triggers (SPEC §7)', () => {
  it('flushes automatically at 20 buffered events', async () => {
    const ep = newClient();
    ep.init(CONFIG);
    await settle();
    for (let i = 0; i < 20; i++) ep.track('product_viewed');
    await settle();
    expect(eventCalls().flatMap((c) => c.body.events)).toHaveLength(20);
  });

  it('flushes on the 30s timer', async () => {
    const ep = newClient();
    ep.init(CONFIG);
    await settle();
    ep.track('product_viewed');
    await vi.advanceTimersByTimeAsync(30_000);
    await settle();
    expect(eventCalls().flatMap((c) => c.body.events)).toHaveLength(1);
  });

  it('uses sendBeacon with a token query param on hidden', async () => {
    const beacon = vi.fn(() => true);
    Object.defineProperty(navigator, 'sendBeacon', { value: beacon, configurable: true });
    const ep = newClient();
    ep.init(CONFIG);
    await settle();
    ep.track('product_viewed');

    Object.defineProperty(document, 'visibilityState', { value: 'hidden', configurable: true });
    document.dispatchEvent(new Event('visibilitychange'));

    expect(beacon).toHaveBeenCalledOnce();
    const [url, payload] = beacon.mock.calls[0] as unknown as [string, string];
    expect(url).toBe('https://collect.test/v1/events?token=tok-web');
    expect(JSON.parse(payload).events).toHaveLength(1);
  });

  it('gives up on events older than 24h', async () => {
    const ep = newClient();
    ep.init(CONFIG);
    await settle();
    autoResolve = false;
    ep.track('product_viewed');
    ep.flush();
    await settle();
    calls.at(-1)!.resolve(false); // send fails; stays queued
    await settle();

    vi.advanceTimersByTime(25 * 60 * 60 * 1000);
    autoResolve = true;
    calls = [];
    ep.flush();
    await settle();
    expect(eventCalls()).toHaveLength(0); // dropped, not sent
  });
});

describe('eventHeaders (SPEC §8)', () => {
  it('returns the tier-2 headers', async () => {
    document.cookie = 'ep_aid=0f2937de-92f9-4b6c-a222-abcdefabcdef';
    const ep = newClient();
    ep.init(CONFIG);
    await settle();
    const headers = ep.eventHeaders('add_to_cart');
    expect(headers['X-Event']).toBe('add_to_cart');
    expect(headers['X-Anonymous-Id']).toBe('0f2937de-92f9-4b6c-a222-abcdefabcdef');
    expect(headers['X-Session-Key']).toMatch(/^[0-9a-f-]{36}$/);
  });
});
