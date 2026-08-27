import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  DELIVERY_STATUSES,
  eventsUrl,
  fetchEvents,
  handlesFor,
  identityUrl,
  shortId,
  statusClass,
  tenantUrl,
  windowFloor,
} from '../src/lib/api.js';

const kefah = { app_id: 'kefahapp', base: '/t/kefahapp' };
const single = { app_id: null, base: '' };

describe('tenant-scoped urls', () => {
  // The bearer selects the tenant, so which mount a call goes to IS which
  // tenant it asks about. Getting the prefix wrong silently reads the wrong
  // dataset, which is the whole class of bug this scoping exists to prevent.
  it('prefixes every query with the tenant mount', () => {
    expect(tenantUrl(kefah)).toBe('/t/kefahapp/internal/v1/query/tenant');
    expect(eventsUrl(kefah, {})).toBe('/t/kefahapp/internal/v1/query/events?limit=50');
    expect(identityUrl(kefah, 'abc')).toBe('/t/kefahapp/internal/v1/query/identity/abc');
  });

  it('falls back to same-origin for the single implicit mount', () => {
    expect(tenantUrl(single)).toBe('/internal/v1/query/tenant');
    expect(eventsUrl(single, {})).toBe('/internal/v1/query/events?limit=50');
  });
});

describe('eventsUrl', () => {
  it('omits empty filters and always carries a limit', () => {
    const url = eventsUrl(single, { event_name: 'product_viewed', user_id: '  ', origin: '' });
    expect(url).toBe('/internal/v1/query/events?event_name=product_viewed&limit=50');
  });

  it('carries cursor, custom limit, and time range', () => {
    const url = eventsUrl(
      single,
      { status: 'dead', from: '2026-07-10T00:00:00Z' },
      { cursor: '123-45', limit: 10 },
    );
    const params = new URL(url, 'http://x').searchParams;
    expect(params.get('status')).toBe('dead');
    expect(params.get('from')).toBe('2026-07-10T00:00:00Z');
    expect(params.get('limit')).toBe('10');
    expect(params.get('cursor')).toBe('123-45');
  });

  it('never sends an app_id — the token is what selects the tenant', () => {
    const url = eventsUrl(kefah, { app_id: 'someone-else', event_name: 'x' });
    expect(url).not.toContain('app_id');
  });
});

describe('identityUrl', () => {
  it('escapes the session key', () => {
    expect(identityUrl(single, 'abc/def')).toBe('/internal/v1/query/identity/abc%2Fdef');
  });
});

describe('handlesFor', () => {
  // A tenant that runs no Adjust has no adjust_adid to show; rendering the row
  // anyway makes "not captured yet" and "not configured" look identical.
  it('lists handles only for the destinations the tenant runs', () => {
    const groups = handlesFor([{ code: 'ga4' }, { code: 'adjust' }]);
    expect(groups.map((g) => g.destination)).toEqual(['ga4', 'adjust']);
    expect(groups[0].fields).toContain('ga4_client_id');
    expect(groups[1].fields).toEqual(['adjust_adid', 'adjust_platform_ad_id']);
  });

  it('drops destinations that carry no identity handle', () => {
    // moengage keys off user_id / the MoEngage customer id, neither of which is
    // a column on the identity row.
    expect(handlesFor([{ code: 'moengage' }, { code: 'moengage_customer' }])).toEqual([]);
    expect(handlesFor()).toEqual([]);
  });
});

describe('windowFloor', () => {
  // The API clamps `from` to EP_QUERY_MAX_DAYS ago no matter what the form
  // asks. Offering a wider range would answer a 30-day question with 5 days of
  // rows and no indication that it had.
  it('is the tenant query ceiling, in the local format the picker wants', () => {
    const now = new Date(2026, 7, 27, 14, 30);
    expect(windowFloor(5, now)).toBe('2026-08-22T14:30');
    expect(windowFloor(1, now)).toBe('2026-08-26T14:30');
  });
});

describe('helpers', () => {
  it('statusClass maps every delivery state distinctly', () => {
    const classes = DELIVERY_STATUSES.map(statusClass);
    expect(new Set(classes).size).toBe(DELIVERY_STATUSES.length);
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

  function stubFetch(response = { ok: true, json: async () => ({ events: [] }) }) {
    const fetchMock = vi.fn().mockResolvedValue(response);
    vi.stubGlobal('fetch', fetchMock);
    return fetchMock;
  }

  it('sends no Authorization by default — the tenant mount injects it', async () => {
    globalThis.window = {};
    const fetchMock = stubFetch();
    await fetchEvents(kefah, {});
    expect(fetchMock.mock.calls[0][1].headers.Authorization).toBeUndefined();
  });

  it('sends the bearer when EP_QUERY_TOKEN is set (proxy-less dev)', async () => {
    globalThis.window = { EP_QUERY_TOKEN: 'internal-secret' };
    const fetchMock = stubFetch();
    await fetchEvents(single, {});
    expect(fetchMock.mock.calls[0][1].headers.Authorization).toBe('Bearer internal-secret');
  });

  it('explains a 401 as a mount that is not injecting a token', async () => {
    globalThis.window = {};
    stubFetch({ ok: false, status: 401, statusText: 'Unauthorized' });
    await expect(fetchEvents(kefah, {})).rejects.toThrow(/internal_token/);
  });
});
