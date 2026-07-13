import { session, writeJson } from './storage';
import { uuidv7 } from './uuid';

const SESSION_KEY = 'ep_session';

/** GA4's session window — keeps session counts reconcilable (SPEC §3). */
export const SESSION_WINDOW_MS = 30 * 60_000;

interface StoredSession {
  key: string;
  last_active_at: number;
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

/** S1 (SPEC §3): resume within 30 minutes of last activity, else mint UUIDv7. */
export function ensureSession(now: number): SessionResult {
  const current = read();
  if (current && now - current.last_active_at <= SESSION_WINDOW_MS) {
    return { sessionKey: current.key, rotated: false };
  }
  const fresh = { key: uuidv7(now), last_active_at: now };
  write(fresh);
  return { sessionKey: fresh.key, rotated: true };
}

/** Forced rotation for clearUser() (SPEC §3). */
export function rotateSession(now: number): string {
  const fresh = { key: uuidv7(now), last_active_at: now };
  write(fresh);
  return fresh.key;
}

/** Updated on every track/page and on background/hidden (SPEC §3). */
export function touchSession(now: number): void {
  const current = read();
  if (current) write({ ...current, last_active_at: now });
}
