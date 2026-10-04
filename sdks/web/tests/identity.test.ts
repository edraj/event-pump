import { beforeEach, describe, expect, it } from 'vitest';
import {
  bumpSessionNumber,
  getClickIds,
  harvestClickIds,
  keepAnonymousId,
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
  it('generates an anonymous_id when none is saved and never writes cookies', () => {
    const device = loadDevice(1_700_000_000_000);
    expect(device.anonymousId).toMatch(UUID_RE);
    expect(device.sessionNumber).toBe(1);
    expect(document.cookie).not.toContain('ep_aid'); // kept in ep_meta, NEVER document.cookie
  });

  it('persists first_seen_at and session_number bound to the anonymous_id', () => {
    const first = loadDevice(1_700_000_000_000);
    // SPEC §2: 1 at creation, +1 per session rotation.
    expect(first.sessionNumber).toBe(1);
    bumpSessionNumber();
    bumpSessionNumber();
    const again = loadDevice(1_700_009_999_999);
    expect(again.firstSeenAt).toBe(first.firstSeenAt);
    expect(again.sessionNumber).toBe(3);
  });

  it('replaces a counter that is not a counter, keeping first_seen_at', () => {
    const first = loadDevice(1_700_000_000_000);

    // A JSON string survives `< 1` by coercion and turns `+= 1` into the
    // concatenation "01", which persists and grows on every rotation.
    localStorage.setItem(
      'ep_meta',
      JSON.stringify({ aid: '0f2937de-92f9-4b6c-a222-abcdefabcdef', first_seen_at: first.firstSeenAt, session_number: '0' }),
    );

    const repaired = loadDevice(1_700_000_500_000);
    expect(repaired.sessionNumber).toBe(1);
    expect(repaired.rebound).toBe(false); // same device: the session may resume
    expect(repaired.counterReset).toBe(true); // ...but this session is number 1
    expect(repaired.firstSeenAt).toBe(first.firstSeenAt);
    expect(JSON.parse(localStorage.getItem('ep_meta')!).session_number).toBe(1);

    // and it counts as a number from there on, rather than concatenating
    expect(bumpSessionNumber()).toBe(2);
  });

  it('never lets a negative counter out, on load or on bump', () => {
    loadDevice(1_700_000_000_000);
    localStorage.setItem(
      'ep_meta',
      JSON.stringify({ aid: '0f2937de-92f9-4b6c-a222-abcdefabcdef', first_seen_at: 'x', session_number: -3 }),
    );
    // -3 + 1 = -2 is still rejected by /v1/identity, and would creep up one
    // per load forever.
    expect(loadDevice(1_700_000_500_000).sessionNumber).toBe(1);
    expect(bumpSessionNumber()).toBe(2);
  });

  it('reuses the saved anonymous_id on reload', () => {
    const first = loadDevice(1_700_000_000_000);
    const reload = loadDevice(1_700_000_060_000);
    expect(reload.anonymousId).toBe(first.anonymousId);
    expect(reload.rebound).toBe(false);
    expect(reload.firstSeenAt).toBe(first.firstSeenAt);
  });

  it('keeps the anonymous_id /v1/identity returned for the next page load', () => {
    const first = loadDevice(1_700_000_000_000);
    keepAnonymousId(first.anonymousId, 1_700_000_030_000); // same id: nothing changes
    expect(loadDevice(1_700_000_060_000).firstSeenAt).toBe(first.firstSeenAt);

    keepAnonymousId('7a1b2c3d-4e5f-4a6b-8c7d-0123456789ab', 1_700_000_090_000);
    const reload = loadDevice(1_700_000_120_000);
    expect(reload.anonymousId).toBe('7a1b2c3d-4e5f-4a6b-8c7d-0123456789ab');
    expect(reload.rebound).toBe(false);
    // A new id never inherits the old id's metadata (SPEC §2).
    expect(reload.firstSeenAt).toBe(new Date(1_700_000_090_000).toISOString());
    expect(reload.sessionNumber).toBe(1);
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
