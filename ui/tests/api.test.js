import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  eventsUrl,
  fetchEvents,
  identityUrl,
  shortId,
  statusClass,
} from '../src/lib/api.js';

describe('eventsUrl', () => {
  it('omits empty filters and always carries a limit', () => {
    const url = eventsUrl({ event_name: 'product_viewed', user_id: '  ', origin: '' });
    expect(url).toBe('/internal/v1/query/events?event_name=product_viewed&limit=50');
  });

  it('carries cursor, custom limit, and time range', () => {
    const url = eventsUrl(
      { status: 'dead', from: '2026-07-10T00:00:00Z' },
      { cursor: '123-45', limit: 10 },
    );
    const params = new URL(url, 'http://x').searchParams;
    expect(params.get('status')).toBe('dead');
    expect(params.get('from')).toBe('2026-07-10T00:00:00Z');
    expect(params.get('limit')).toBe('10');
    expect(params.get('cursor')).toBe('123-45');
  });
});

describe('identityUrl', () => {
  it('escapes the session key', () => {
    expect(identityUrl('abc/def')).toBe('/internal/v1/query/identity/abc%2Fdef');
  });
});

describe('helpers', () => {
  it('statusClass maps every delivery state distinctly', () => {
    const classes = ['pending', 'delivered', 'failed', 'dead', 'skipped'].map(statusClass);
    expect(new Set(classes).size).toBe(5);
  });

  it('shortId truncates', () => {
    expect(shortId('0f2937de-92f9-4b6c-a222-abcdefabcdef')).toBe('0f2937de…');
    expect(shortId(null)).toBe('');
  });
});

describe('fetchEvents auth', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    delete globalThis.window;
  });

  function stubFetch() {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => ({ events: [] }),
    });
    vi.stubGlobal('fetch', fetchMock);
    return fetchMock;
  }

  it('sends no Authorization by default — nginx injects it', async () => {
    globalThis.window = {};
    const fetchMock = stubFetch();
    await fetchEvents({});
    expect(fetchMock.mock.calls[0][1].headers.Authorization).toBeUndefined();
  });

  it('sends the bearer when EP_QUERY_TOKEN is set (proxy-less dev)', async () => {
    globalThis.window = { EP_QUERY_TOKEN: 'internal-secret' };
    const fetchMock = stubFetch();
    await fetchEvents({});
    expect(fetchMock.mock.calls[0][1].headers.Authorization).toBe('Bearer internal-secret');
  });
});
