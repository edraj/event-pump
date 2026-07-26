import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createEventPump, type EventPump } from '../src/client';

const active: EventPump[] = [];

interface Captured {
  url: string;
  body: Record<string, any>;
}

let calls: Captured[] = [];

function fetchMock(url: string | URL, options?: RequestInit): Promise<Response> {
  calls.push({
    url: String(url),
    body: options?.body ? JSON.parse(String(options.body)) : {},
  });
  return Promise.resolve(new Response('{}', { status: 204 }));
}

async function settle(): Promise<void> {
  for (let i = 0; i < 10; i++) await Promise.resolve();
}

function clearCookies(): void {
  for (const pair of document.cookie.split(';')) {
    const name = pair.split('=')[0]?.trim();
    if (name) document.cookie = `${name}=; expires=Thu, 01 Jan 1970 00:00:00 GMT`;
  }
}

const CONFIG = { endpoint: 'https://collect.test', appToken: 'tok-web' };

beforeEach(() => {
  vi.useFakeTimers({ now: 1_700_000_000_000 });
  localStorage.clear();
  sessionStorage.clear();
  clearCookies();
  calls = [];
  vi.stubGlobal('fetch', fetchMock);
  Object.defineProperty(document, 'visibilityState', { value: 'visible', configurable: true });
});

afterEach(() => {
  active.splice(0).forEach((client) => client.destroy());
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

function newClient(): EventPump {
  const client = createEventPump();
  active.push(client);
  return client;
}

async function initReady(ep: EventPump): Promise<void> {
  ep.init(CONFIG);
  await settle(); // let the initial identity POST resolve
}

function attributesCalls(): Captured[] {
  return calls.filter((c) => c.url.includes('/v1/identity') && 'attributes' in c.body);
}

describe('setUserAttributes (SPEC §6.1)', () => {
  it('is a no-op without a prior setUser call', async () => {
    const ep = newClient();
    await initReady(ep);
    const before = calls.length;

    ep.setUserAttributes({ email: 'a@b.co' });
    await settle();

    expect(calls.length).toBe(before);
  });

  it('posts allowlisted attributes with user_id after setUser', async () => {
    const ep = newClient();
    await initReady(ep);
    ep.setUser('u-42');
    await settle();

    ep.setUserAttributes({
      first_name: 'Ali',
      last_name: 'Hassan',
      email: 'ali@example.com',
      phone: '+9647701234567',
      gender: 'male',
      city: 'Baghdad',
    });
    await settle();

    const [call] = attributesCalls();
    expect(call).toBeDefined();
    expect(call!.body.user_id).toBe('u-42');
    expect(call!.body.attributes).toEqual({
      first_name: 'Ali',
      last_name: 'Hassan',
      email: 'ali@example.com',
      phone: '+9647701234567',
      gender: 'male',
      city: 'Baghdad',
    });
  });

  it('drops keys outside the six-name allowlist locally', async () => {
    const ep = newClient();
    await initReady(ep);
    ep.setUser('u-42');
    await settle();

    ep.setUserAttributes({
      first_name: 'Ali',
      // @ts-expect-error not in the Attributes type but must still drop at runtime
      ssn: '123-45-6789',
      // @ts-expect-error
      passport_number: 'X99',
    });
    await settle();

    const [call] = attributesCalls();
    expect(Object.keys(call!.body.attributes)).toEqual(['first_name']);
  });

  it('does not send when every provided key is dropped', async () => {
    const ep = newClient();
    await initReady(ep);
    ep.setUser('u-42');
    await settle();
    const before = calls.length;

    ep.setUserAttributes({
      // @ts-expect-error entirely non-allowlisted
      ssn: '123',
    });
    await settle();

    expect(calls.length).toBe(before);
  });

  it('null values pass through so the server can clear stored keys', async () => {
    const ep = newClient();
    await initReady(ep);
    ep.setUser('u-42');
    await settle();

    ep.setUserAttributes({ email: null });
    await settle();

    const [call] = attributesCalls();
    expect(call!.body.attributes).toEqual({ email: null });
  });
});
