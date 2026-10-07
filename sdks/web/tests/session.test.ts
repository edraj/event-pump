import { beforeEach, describe, expect, it } from 'vitest';
import { SESSION_WINDOW_MS, ensureSession, rotateSession, touchSession } from '../src/session';

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
const AID = '0f2937de-92f9-4b6c-a222-abcdefabcdef';
const OTHER = '7a1b2c3d-4e5f-4a6b-8c7d-0123456789ab';

beforeEach(() => sessionStorage.clear());

describe('S1 session state machine (SPEC §3)', () => {
  it('mints a new UUIDv7 session on first load', () => {
    const result = ensureSession(1_700_000_000_000, AID);
    expect(result.sessionKey).toMatch(UUID_RE);
    expect(result.rotated).toBe(true);
  });

  it('resumes within the 30-minute window', () => {
    const first = ensureSession(1_700_000_000_000, AID);
    touchSession(1_700_000_060_000);
    const resumed = ensureSession(1_700_000_120_000, AID);
    expect(resumed.sessionKey).toBe(first.sessionKey);
    expect(resumed.rotated).toBe(false);
  });

  it('rotates when last_active_at is older than 30 minutes', () => {
    const first = ensureSession(1_700_000_000_000, AID);
    touchSession(1_700_000_000_000);
    const rotated = ensureSession(1_700_000_000_000 + SESSION_WINDOW_MS + 1, AID);
    expect(rotated.sessionKey).not.toBe(first.sessionKey);
    expect(rotated.rotated).toBe(true);
  });

  it('rotates a live session registered under a different anonymous_id', () => {
    // ep_meta is shared by every tab: another tab can rebind it, so a reload
    // here reads a different id while this tab still holds the old session.
    // Resuming it would register one session under two devices (SPEC §2).
    const first = ensureSession(1_700_000_000_000, AID);
    const other = ensureSession(1_700_000_060_000, OTHER);
    expect(other.rotated).toBe(true);
    expect(other.sessionKey).not.toBe(first.sessionKey);
    expect(JSON.parse(sessionStorage.getItem('ep_session')!).aid).toBe(OTHER);
  });

  it('adopts a live session written by an earlier SDK build instead of rotating it', () => {
    sessionStorage.setItem(
      'ep_session',
      JSON.stringify({ key: 'k-from-old-bundle', last_active_at: 1_700_000_000_000 }),
    );
    const resumed = ensureSession(1_700_000_060_000, AID);
    expect(resumed).toEqual({ sessionKey: 'k-from-old-bundle', rotated: false });
    expect(JSON.parse(sessionStorage.getItem('ep_session')!).aid).toBe(AID);
    // ...and from then on it is bound like any other.
    expect(ensureSession(1_700_000_120_000, OTHER).rotated).toBe(true);
  });

  it('binds rotations to the anonymous_id and keeps the binding across touches', () => {
    const key = rotateSession(1_700_000_000_000, AID);
    touchSession(1_700_000_060_000);
    expect(JSON.parse(sessionStorage.getItem('ep_session')!)).toEqual({
      key,
      last_active_at: 1_700_000_060_000,
      aid: AID,
    });
    expect(ensureSession(1_700_000_120_000, AID)).toEqual({ sessionKey: key, rotated: false });
  });

  it('survives storage being unavailable (memory-only session)', () => {
    const broken = {
      getItem: () => {
        throw new Error('blocked');
      },
      setItem: () => {
        throw new Error('blocked');
      },
      removeItem: () => {
        throw new Error('blocked');
      },
    };
    Object.defineProperty(window, 'sessionStorage', { value: broken, configurable: true });
    try {
      const a = ensureSession(1_700_000_000_000, AID);
      expect(a.sessionKey).toMatch(UUID_RE);
      touchSession(1_700_000_001_000);
      const b = ensureSession(1_700_000_002_000, AID);
      expect(b.sessionKey).toBe(a.sessionKey); // memory fallback keeps the session
    } finally {
      Object.defineProperty(window, 'sessionStorage', {
        value: window.sessionStorage,
        configurable: true,
      });
    }
  });
});
