import { describe, expect, it } from 'vitest';
import { eventsUrl, identityUrl, shortId, statusClass } from '../src/lib/api.js';

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
