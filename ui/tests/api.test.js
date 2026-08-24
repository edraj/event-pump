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

describe('apiBase', () => {
  afterEach(() => {
    delete globalThis.window;
    vi.resetModules();
  });

  it('follows the UI base so query calls stay inside the auth scope', async () => {
    globalThis.window = { EP_UI_BASE: '/ep/ui' };
    vi.resetModules();
    const api = await import('../src/lib/api.js');
    expect(api.eventsUrl({})).toBe('/ep/ui/internal/v1/query/events?limit=50');
  });

  it('lets EP_QUERY_BASE override it', async () => {
    globalThis.window = { EP_UI_BASE: '/ep/ui', EP_QUERY_BASE: '/ep' };
    vi.resetModules();
    const api = await import('../src/lib/api.js');
    expect(api.eventsUrl({})).toBe('/ep/internal/v1/query/events?limit=50');
  });

  it('honours an empty EP_QUERY_BASE as "the root", not as unset', async () => {
    // The escape hatch for a vhost with no auth_basic, where the query API can
    // legitimately sit at the root while the UI is on a subpath. `||` would
    // swallow this and silently use the UI base instead.
    globalThis.window = { EP_UI_BASE: '/ep/ui', EP_QUERY_BASE: '' };
    vi.resetModules();
    const api = await import('../src/lib/api.js');
    expect(api.eventsUrl({})).toBe('/internal/v1/query/events?limit=50');
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
    await expect(fetchEvents({ anonymous_id: '0f2937de' })).rejects.toThrow(
      'invalid_uuid: anonymous_id',
    );
  });

  it('uses the error alone when there is no detail', async () => {
    stubFailure({ error: 'unauthorized' });
    await expect(fetchEvents({})).rejects.toThrow('unauthorized');
  });

  it('falls back to the status line for a non-JSON body', async () => {
    // e.g. an nginx 502 page in front of a stopped api.
    stubFailure(undefined);
    await expect(fetchEvents({})).rejects.toThrow('400 Bad Request');
  });

  it('falls back to the status line for JSON that is not our shape', async () => {
    stubFailure({ message: 'something else' });
    await expect(fetchEvents({})).rejects.toThrow('400 Bad Request');
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
    await expect(fetchEvents({})).rejects.toThrow(/never challenged on this path/);
    await expect(fetchEvents({})).rejects.toThrow(/\/internal\/v1\/query\//);
  });

  it('leaves our own 401 alone — a bad internal_token is a different fix', async () => {
    stubResponse({
      ok: false,
      status: 401,
      statusText: 'Unauthorized',
      json: async () => ({ error: 'unauthorized' }),
    });
    await expect(fetchEvents({})).rejects.toThrow('unauthorized');
    await expect(fetchEvents({})).rejects.not.toThrow(/nginx proxies/);
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
    await expect(fetchEvents({})).rejects.toThrow(/non-JSON body/);
    await expect(fetchEvents({})).rejects.toThrow(/index\.html/);
    await expect(fetchEvents({})).rejects.toThrow(/nginx proxies/);
  });
});
