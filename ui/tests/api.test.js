import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  DELIVERY_STATUSES,
  eventsUrl,
  fetchEvents,
  fetchIdentity,
  fetchTenantInfo,
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

  it('sends from/to as instants, so the server reads the picked time', () => {
    // The pickers are datetime-local (naive); the server parses with
    // RoundtripKind, i.e. as its own local time, and clamps in UTC. Sent naive,
    // a browser east of UTC asks for a window offset by its own zone and loses
    // that much of it against the clamp silently.
    const picked = '2026-08-22T14:30';
    const url = eventsUrl(single, { from: picked });
    const sent = new URL(url, 'http://x').searchParams.get('from');
    expect(sent).toBe(new Date(picked).toISOString());
    expect(sent).toMatch(/(Z|[+-]\d{2}:\d{2})$/);
  });

  it('passes a value that already carries a zone through untouched', () => {
    const url = eventsUrl(single, { to: '2026-07-10T00:00:00Z' });
    expect(new URL(url, 'http://x').searchParams.get('to')).toBe('2026-07-10T00:00:00Z');
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

describe('fetchEvents error reporting', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    delete globalThis.window;
  });

  function stubFailure(body) {
    globalThis.window = {};
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({
        ok: false,
        status: 400,
        statusText: 'Bad Request',
        json: async () => {
          if (body === undefined) throw new SyntaxError('Unexpected end of JSON input');
          return body;
        },
      }),
    );
  }

  it('names the filter the server rejected', async () => {
    // The server populates `detail` precisely so the operator does not have to
    // guess which of eight filters was malformed.
    stubFailure({ error: 'invalid_uuid', detail: 'anonymous_id' });
    await expect(fetchEvents(single, { anonymous_id: '0f2937de' })).rejects.toThrow(
      'invalid_uuid: anonymous_id',
    );
  });

  it('uses the error alone when there is no detail', async () => {
    stubFailure({ error: 'unauthorized' });
    await expect(fetchEvents(single, {})).rejects.toThrow('unauthorized');
  });

  it('falls back to the status line for a non-JSON body', async () => {
    // e.g. an nginx 502 page in front of a stopped api.
    stubFailure(undefined);
    await expect(fetchEvents(single, {})).rejects.toThrow('400 Bad Request');
  });

  it('falls back to the status line for JSON that is not our shape', async () => {
    stubFailure({ message: 'something else' });
    await expect(fetchEvents(single, {})).rejects.toThrow('400 Bad Request');
  });
});

describe('a 404 means different things on different endpoints', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    delete globalThis.window;
  });

  function stub404() {
    globalThis.window = {};
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({
        ok: false,
        status: 404,
        statusText: 'Not Found',
        headers: { get: () => null },
        json: async () => {
          throw new SyntaxError('Unexpected end of JSON input');
        },
      }),
    );
  }

  it('blames the mount when the endpoint every tenant has is missing', async () => {
    stub404();
    await expect(fetchTenantInfo(kefah)).rejects.toThrow(/no such tenant mount/);
    await expect(fetchTenantInfo(kefah)).rejects.toThrow(/\/t\/kefahapp\/internal\/v1\/query\//);
  });

  it('does not blame the mount for a session key that simply is not there', async () => {
    // QueryApi.IdentityAsync 404s with an empty body whenever the key is not in
    // identity_registry for this app_id — a server-origin event, an expired key,
    // one pasted from another tenant. The mount is fine.
    stub404();
    await expect(fetchIdentity(kefah, 'abc')).rejects.toThrow(/no identity row/);
    await expect(fetchIdentity(kefah, 'abc')).rejects.not.toThrow(/tenant mount/);
  });
});

describe('diagnosing an unproxied query path', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    delete globalThis.window;
    vi.resetModules();
  });

  function stubResponse({ ok, status, statusText, headers = {}, json }) {
    globalThis.window = {};
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({
        ok,
        status,
        statusText,
        headers: { get: (name) => headers[name] ?? null },
        json,
      }),
    );
  }

  it("names the cause when nginx challenges instead of the browser sending credentials", async () => {
    // The v0.6.0 subpath bug: the query API sits outside the prefix the UI
    // authenticated on, so the browser attaches nothing and nginx challenges.
    stubResponse({
      ok: false,
      status: 401,
      statusText: 'Unauthorized',
      headers: { 'WWW-Authenticate': 'Basic realm="Event Pump"' },
      json: async () => {
        throw new SyntaxError('Unexpected token <');
      },
    });
    await expect(fetchEvents(single, {})).rejects.toThrow(/never challenged on this path/);
    await expect(fetchEvents(single, {})).rejects.toThrow(/\/internal\/v1\/query\//);
  });

  it('keeps our own 401 about the token, not about the path', async () => {
    // No WWW-Authenticate: the request reached the API, which rejected the
    // bearer nginx injected. That is one proxy_set_header line, not a missing
    // location, and saying "nginx proxies…" would send the reader to the wrong
    // half of the config.
    stubResponse({
      ok: false,
      status: 401,
      statusText: 'Unauthorized',
      json: async () => ({ error: 'unauthorized' }),
    });
    await expect(fetchEvents(single, {})).rejects.toThrow(/internal_token/);
    await expect(fetchEvents(single, {})).rejects.not.toThrow(/nginx proxies/);
  });

  it('names the cause when the SPA index.html is served in place of the API', async () => {
    // A 200 carrying HTML: try_files caught the unproxied query path. Parsing
    // it raises a bare "Unexpected token <", which reads like a UI bug.
    stubResponse({
      ok: true,
      status: 200,
      statusText: 'OK',
      json: async () => {
        throw new SyntaxError('Unexpected token <');
      },
    });
    await expect(fetchEvents(single, {})).rejects.toThrow(/non-JSON body/);
    await expect(fetchEvents(single, {})).rejects.toThrow(/index\.html/);
    await expect(fetchEvents(single, {})).rejects.toThrow(/nginx proxies/);
  });

  it('lets a dropped connection through instead of blaming nginx', async () => {
    // fetch resolves at the headers, so a body that never arrives rejects with
    // a TypeError. A correctly proxied deployment that hits a network blip must
    // not be told to go edit its locations.
    stubResponse({
      ok: true,
      status: 200,
      statusText: 'OK',
      json: async () => {
        throw new TypeError('network error');
      },
    });
    await expect(fetchEvents(single, {})).rejects.toThrow('network error');
    await expect(fetchEvents(single, {})).rejects.not.toThrow(/nginx proxies/);
  });

  it('offers re-authentication as the other cause of a Basic challenge', async () => {
    // nginx challenges the same way for a rotated htpasswd on a correctly
    // proxied path, and the operator should try that before touching nginx.
    stubResponse({
      ok: false,
      status: 401,
      statusText: 'Unauthorized',
      headers: { 'WWW-Authenticate': 'Basic realm="Event Pump"' },
      json: async () => {
        throw new SyntaxError('Unexpected token <');
      },
    });
    await expect(fetchEvents(single, {})).rejects.toThrow(/re-authenticate/);
  });
});
