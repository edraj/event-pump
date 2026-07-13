import { beforeEach, describe, expect, it } from 'vitest';
import { SESSION_WINDOW_MS, ensureSession, touchSession } from '../src/session';

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

beforeEach(() => sessionStorage.clear());

describe('S1 session state machine (SPEC §3)', () => {
  it('mints a new UUIDv7 session on first load', () => {
    const result = ensureSession(1_700_000_000_000);
    expect(result.sessionKey).toMatch(UUID_RE);
    expect(result.rotated).toBe(true);
  });

  it('resumes within the 30-minute window', () => {
    const first = ensureSession(1_700_000_000_000);
    touchSession(1_700_000_060_000);
    const resumed = ensureSession(1_700_000_120_000);
    expect(resumed.sessionKey).toBe(first.sessionKey);
    expect(resumed.rotated).toBe(false);
  });

  it('rotates when last_active_at is older than 30 minutes', () => {
    const first = ensureSession(1_700_000_000_000);
    touchSession(1_700_000_000_000);
    const rotated = ensureSession(1_700_000_000_000 + SESSION_WINDOW_MS + 1);
    expect(rotated.sessionKey).not.toBe(first.sessionKey);
    expect(rotated.rotated).toBe(true);
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
      const a = ensureSession(1_700_000_000_000);
      expect(a.sessionKey).toMatch(UUID_RE);
      touchSession(1_700_000_001_000);
      const b = ensureSession(1_700_000_002_000);
      expect(b.sessionKey).toBe(a.sessionKey); // memory fallback keeps the session
    } finally {
      Object.defineProperty(window, 'sessionStorage', {
        value: window.sessionStorage,
        configurable: true,
      });
    }
  });
});
