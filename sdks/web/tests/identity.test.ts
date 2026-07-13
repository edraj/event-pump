import { beforeEach, describe, expect, it } from 'vitest';
import {
  bumpSessionNumber,
  getClickIds,
  harvestClickIds,
  loadDevice,
} from '../src/identity';

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

function clearCookies(): void {
  for (const pair of document.cookie.split(';')) {
    const name = pair.split('=')[0]?.trim();
    if (name) document.cookie = `${name}=; expires=Thu, 01 Jan 1970 00:00:00 GMT`;
  }
}

beforeEach(() => {
  localStorage.clear();
  sessionStorage.clear();
  clearCookies();
});

describe('S0 device identity (SPEC §2)', () => {
  it('generates a memory-held anonymous_id when the ep_aid cookie is absent and never writes cookies', () => {
    const device = loadDevice(1_700_000_000_000);
    expect(device.anonymousId).toMatch(UUID_RE);
    expect(device.sessionNumber).toBe(0);
    expect(document.cookie).not.toContain('ep_aid'); // server-set only, NEVER document.cookie
  });

  it('uses the server-set ep_aid cookie when present', () => {
    document.cookie = 'ep_aid=0f2937de-92f9-4b6c-a222-abcdefabcdef';
    const device = loadDevice(1_700_000_000_000);
    expect(device.anonymousId).toBe('0f2937de-92f9-4b6c-a222-abcdefabcdef');
  });

  it('persists first_seen_at and session_number bound to the anonymous_id', () => {
    document.cookie = 'ep_aid=0f2937de-92f9-4b6c-a222-abcdefabcdef';
    const first = loadDevice(1_700_000_000_000);
    bumpSessionNumber();
    bumpSessionNumber();
    const again = loadDevice(1_700_009_999_999);
    expect(again.firstSeenAt).toBe(first.firstSeenAt);
    expect(again.sessionNumber).toBe(2);
  });

  it('resets metadata when the anonymous_id changes (cookie cleared)', () => {
    document.cookie = 'ep_aid=0f2937de-92f9-4b6c-a222-abcdefabcdef';
    loadDevice(1_700_000_000_000);
    bumpSessionNumber();
    clearCookies();

    const fresh = loadDevice(1_700_100_000_000); // new memory-held id
    expect(fresh.anonymousId).not.toBe('0f2937de-92f9-4b6c-a222-abcdefabcdef');
    expect(fresh.sessionNumber).toBe(0);
    expect(fresh.firstSeenAt).toBe(new Date(1_700_100_000_000).toISOString());
  });
});

describe('click-id harvesting (SPEC §6)', () => {
  it('captures configured params from the landing URL with capture time', () => {
    harvestClickIds(['gclid', 'fbclid'], '?gclid=g1&fbclid=f1&utm_source=x', '2026-07-13T00:00:00.000Z');
    expect(getClickIds()).toEqual({
      gclid: { value: 'g1', captured_at: '2026-07-13T00:00:00.000Z' },
      fbclid: { value: 'f1', captured_at: '2026-07-13T00:00:00.000Z' },
    });
  });

  it('merges later captures, newest click wins, others retained', () => {
    harvestClickIds(['gclid', 'fbclid'], '?gclid=g1&fbclid=f1', '2026-07-13T00:00:00.000Z');
    harvestClickIds(['gclid', 'fbclid'], '?gclid=g2', '2026-07-14T00:00:00.000Z');
    expect(getClickIds()).toEqual({
      gclid: { value: 'g2', captured_at: '2026-07-14T00:00:00.000Z' },
      fbclid: { value: 'f1', captured_at: '2026-07-13T00:00:00.000Z' },
    });
  });
});
