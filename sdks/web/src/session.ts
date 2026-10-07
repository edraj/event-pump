import { session, writeJson } from './storage';
import { uuidv7 } from './uuid';

const SESSION_KEY = 'ep_session';

/** GA4's session window — keeps session counts reconcilable (SPEC §3). */
export const SESSION_WINDOW_MS = 30 * 60_000;

interface StoredSession {
  key: string;
  last_active_at: number;
  /**
   * The anonymous_id this session was registered under. One session belongs
   * to one device (SPEC §2): identity_registry is keyed on session_key alone,
   * so registering the same key under a second id re-points the row and
   * re-attributes every event already delivered under the first. Absent in
   * sessions written by earlier SDK builds.
   */
  aid?: string;
}

// Fallback when sessionStorage is unavailable (SPEC §3: memory-only session;
// NEVER derive session ids from anonymous_id + time buckets).
let memorySession: StoredSession | null = null;

function read(): StoredSession | null {
  try {
    const raw = window.sessionStorage.getItem(SESSION_KEY);
    return raw ? (JSON.parse(raw) as StoredSession) : null;
  } catch {
    return memorySession;
  }
}

function write(state: StoredSession): void {
  memorySession = state;
  writeJson(session(), SESSION_KEY, state);
}

export interface SessionResult {
  sessionKey: string;
  rotated: boolean;
}

/**
 * S1 (SPEC §3): resume within 30 minutes of last activity if the session
 * belongs to `anonymousId`, else mint a UUIDv7 bound to it.
 *
 * The anonymous_id check is what keeps a session on one device when ep_meta
 * (shared by every tab) is rebound by another tab between two page loads of
 * this one: the reload then reads a different id while this tab's
 * sessionStorage still holds the session registered under the previous one.
 *
 * A live session with no recorded id was written by an earlier SDK build. It
 * is adopted rather than rotated, so upgrading does not start a new session
 * in every open tab. Those builds kept ep_meta bound to the id they last
 * registered, which is the id this page reads back unless another tab has
 * rebound it since.
 */
export function ensureSession(now: number, anonymousId: string): SessionResult {
  const current = read();
  if (current && now - current.last_active_at <= SESSION_WINDOW_MS) {
    if (current.aid === anonymousId) return { sessionKey: current.key, rotated: false };
    if (current.aid === undefined) {
      write({ ...current, aid: anonymousId });
      return { sessionKey: current.key, rotated: false };
    }
  }
  return { sessionKey: rotateSession(now, anonymousId), rotated: true };
}

/** Forced rotation for clearUser() and a rebound device (SPEC §2, §3). */
export function rotateSession(now: number, anonymousId: string): string {
  const fresh: StoredSession = { key: uuidv7(now), last_active_at: now, aid: anonymousId };
  write(fresh);
  return fresh.key;
}

/** Updated on every track/page and on background/hidden (SPEC §3). */
export function touchSession(now: number): void {
  const current = read();
  if (current) write({ ...current, last_active_at: now });
}
