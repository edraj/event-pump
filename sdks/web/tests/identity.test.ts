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
  const AID = '0f2937de-92f9-4b6c-a222-abcdefabcdef';
  const OTHER = '7a1b2c3d-4e5f-4a6b-8c7d-0123456789ab';

  function storeMeta(meta: Record<string, unknown>): void {
    localStorage.setItem('ep_meta', JSON.stringify(meta));
  }

  it('mints an anonymous_id when there is neither a cookie nor a stored one, and never writes cookies', () => {
    const device = loadDevice(1_700_000_000_000);
    expect(device.anonymousId).toMatch(UUID_RE);
    expect(device.sessionNumber).toBe(1);
    expect(device.rebound).toBe(true);
    expect(document.cookie).not.toContain('ep_aid'); // server-set only, NEVER document.cookie
  });

  it('uses the server-set ep_aid cookie when present', () => {
    document.cookie = `ep_aid=${AID}`;
    const device = loadDevice(1_700_000_000_000);
    expect(device.anonymousId).toBe(AID);
  });

  it('reuses the anonymous_id ep_meta is bound to when there is no cookie', () => {
    // An API on another registrable domain never gets its cookie stored, which
    // used to mean a new device on every page load.
    const first = loadDevice(1_700_000_000_000);
    const reload = loadDevice(1_700_000_060_000);
    expect(reload.anonymousId).toBe(first.anonymousId);
    expect(reload.rebound).toBe(false);
    expect(reload.firstSeenAt).toBe(first.firstSeenAt);
    expect(document.cookie).toBe('');
  });

  it('keeps the device when the cookie is cleared but ep_meta survives', () => {
    document.cookie = `ep_aid=${AID}`;
    const first = loadDevice(1_700_000_000_000);
    bumpSessionNumber(AID);
    clearCookies();

    const again = loadDevice(1_700_100_000_000);
    expect(again.anonymousId).toBe(AID);
    expect(again.rebound).toBe(false);
    expect(again.sessionNumber).toBe(2);
    expect(again.firstSeenAt).toBe(first.firstSeenAt);
  });

  it('prefers the cookie when it and ep_meta disagree, and resets the metadata', () => {
    storeMeta({ aid: OTHER, first_seen_at: '2026-01-01T00:00:00.000Z', session_number: 9 });
    document.cookie = `ep_aid=${AID}`;

    const device = loadDevice(1_700_100_000_000);
    expect(device.anonymousId).toBe(AID);
    expect(device.rebound).toBe(true);
    expect(device.sessionNumber).toBe(1);
    expect(device.firstSeenAt).toBe(new Date(1_700_100_000_000).toISOString());
    expect(JSON.parse(localStorage.getItem('ep_meta')!).aid).toBe(AID);
  });

  it('discards a stored anonymous_id that is not a UUID instead of sending it', () => {
    // An empty or junk id makes every /v1/identity call a 400 and every event
    // a rejection inside a 200, permanently: nothing would ever replace it.
    for (const aid of ['', 'not-a-uuid', 42, null, `${AID}x`]) {
      localStorage.clear();
      storeMeta({ aid, first_seen_at: '2026-01-01T00:00:00.000Z', session_number: 4 });
      const device = loadDevice(1_700_000_000_000);
      expect(device.anonymousId).toMatch(UUID_RE);
      expect(device.anonymousId).not.toBe(aid);
      expect(device.rebound).toBe(true);
      expect(device.sessionNumber).toBe(1);
    }
  });

  it('ignores an ep_aid cookie that is not a UUID and falls back to ep_meta', () => {
    storeMeta({ aid: AID, first_seen_at: '2026-01-01T00:00:00.000Z', session_number: 3 });
    document.cookie = 'ep_aid=garbage';
    const device = loadDevice(1_700_000_000_000);
    expect(device.anonymousId).toBe(AID);
    expect(device.rebound).toBe(false);
    expect(device.sessionNumber).toBe(3);
  });

  it('treats a different letter case as the same device', () => {
    storeMeta({ aid: AID.toUpperCase(), first_seen_at: '2026-01-01T00:00:00.000Z', session_number: 5 });
    const device = loadDevice(1_700_000_000_000);
    expect(device.anonymousId).toBe(AID); // canonical lower case on the wire
    expect(device.rebound).toBe(false);
    expect(device.sessionNumber).toBe(5);
    expect(bumpSessionNumber(AID)).toBe(6);
  });

  it('persists first_seen_at and session_number bound to the anonymous_id', () => {
    document.cookie = `ep_aid=${AID}`;
    const first = loadDevice(1_700_000_000_000);
    // SPEC §2: 1 at creation, +1 per session rotation.
    expect(first.sessionNumber).toBe(1);
    bumpSessionNumber(AID);
    bumpSessionNumber(AID);
    const again = loadDevice(1_700_009_999_999);
    expect(again.firstSeenAt).toBe(first.firstSeenAt);
    expect(again.sessionNumber).toBe(3);
  });

  it("does not bump another anonymous_id's counter", () => {
    // Another tab rebound ep_meta to a different id after this page loaded.
    loadDevice(1_700_000_000_000);
    storeMeta({ aid: OTHER, first_seen_at: '2026-01-01T00:00:00.000Z', session_number: 7 });

    expect(bumpSessionNumber(AID, 4)).toBe(5); // this page's own count, in memory
    expect(JSON.parse(localStorage.getItem('ep_meta')!)).toMatchObject({ aid: OTHER, session_number: 7 });
  });

  it('replaces a counter that is not a counter, keeping first_seen_at', () => {
    document.cookie = `ep_aid=${AID}`;
    const first = loadDevice(1_700_000_000_000);

    // A JSON string survives `< 1` by coercion and turns `+= 1` into the
    // concatenation "01", which persists and grows on every rotation.
    storeMeta({ aid: AID, first_seen_at: first.firstSeenAt, session_number: '0' });

    const repaired = loadDevice(1_700_000_500_000);
    expect(repaired.sessionNumber).toBe(1);
    expect(repaired.rebound).toBe(false); // same device: the session may resume
    expect(repaired.counterReset).toBe(true); // ...but this session is number 1
    expect(repaired.firstSeenAt).toBe(first.firstSeenAt);
    expect(JSON.parse(localStorage.getItem('ep_meta')!).session_number).toBe(1);

    // and it counts as a number from there on, rather than concatenating
    expect(bumpSessionNumber(AID)).toBe(2);
  });

  it('never lets a negative counter out, on load or on bump', () => {
    document.cookie = `ep_aid=${AID}`;
    loadDevice(1_700_000_000_000);
    storeMeta({ aid: AID, first_seen_at: 'x', session_number: -3 });
    // -3 + 1 = -2 is still rejected by /v1/identity, and would creep up one
    // per load forever.
    expect(loadDevice(1_700_000_500_000).sessionNumber).toBe(1);
    expect(bumpSessionNumber(AID)).toBe(2);
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
