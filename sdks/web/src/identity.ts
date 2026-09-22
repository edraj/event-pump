import { readCookie } from './cookies';
import { local, readJson, writeJson } from './storage';
import { uuidv4 } from './uuid';

const META_KEY = 'ep_meta';
const CLICK_KEY = 'ep_click_ids';

interface Meta {
  aid: string;
  first_seen_at: string;
  session_number: number;
}

export interface DeviceIdentity {
  anonymousId: string;
  firstSeenAt: string;
  sessionNumber: number;
  /**
   * The anonymous_id is not the one the metadata was bound to — a device we
   * have never seen, or a cookie/localStorage pair that diverged. The session
   * has to rotate with it (SPEC §2, §3).
   */
  rebound: boolean;
  /**
   * session_number was just set to 1 by this call. The rotation that starts
   * this session must not bump it: 1 already *is* this session's number, and
   * bumping would report 2 for a brand-new device.
   */
  counterReset: boolean;
}

export interface ClickId {
  value: string;
  captured_at: string;
}

/**
 * S0 (SPEC §3): load or create the device identity. The SDK only READS the
 * server-set ep_aid cookie; when absent a UUIDv4 is generated and held in
 * memory by the caller — never written to document.cookie. first_seen_at and
 * session_number are persisted bound to the anonymous_id they belong to.
 */
export function loadDevice(now: number): DeviceIdentity {
  const anonymousId = readCookie('ep_aid') ?? uuidv4();
  const storage = local();
  const meta = readJson<Meta>(storage, META_KEY);

  // The anonymous_id is not the one this metadata belongs to (cookie cleared
  // while localStorage survived, or vice versa). SPEC §2: both fields reset
  // together — `first_seen_at = now, session_number = 1`. Minting 0 here and
  // leaving the caller to bump it made an invalid counter representable, and
  // on the one path that does not rotate it was reported as-is: /v1/identity
  // answers 400 to session_number 0.
  if (!meta || meta.aid !== anonymousId) {
    const fresh: Meta = {
      aid: anonymousId,
      first_seen_at: new Date(now).toISOString(),
      session_number: 1,
    };
    writeJson(storage, META_KEY, fresh);
    return {
      anonymousId,
      firstSeenAt: fresh.first_seen_at,
      sessionNumber: fresh.session_number,
      rebound: true,
      counterReset: true,
    };
  }

  // Same device, unusable counter. Repaired in place rather than through the
  // full reset above: first_seen_at is still this device's and losing it would
  // re-date the device and disturb first-visit reporting (§8).
  if (!isCounter(meta.session_number)) {
    const repaired: Meta = { ...meta, session_number: 1 };
    writeJson(storage, META_KEY, repaired);
    return {
      anonymousId,
      firstSeenAt: repaired.first_seen_at,
      sessionNumber: repaired.session_number,
      rebound: false,
      counterReset: true,
    };
  }

  return {
    anonymousId,
    firstSeenAt: meta.first_seen_at,
    sessionNumber: meta.session_number,
    rebound: false,
    counterReset: false,
  };
}

/**
 * A session counter is a positive integer and nothing else. localStorage hands
 * back whatever JSON.parse produced, so this is a type check as much as a range
 * check: a stored `"0"` would survive `< 1` comparisons by coercion and then
 * turn `+= 1` into the string concatenation `"01"`, persisting corruption that
 * grows by one character per rotation.
 */
function isCounter(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 1;
}

/** +1 per session rotation (SPEC §2). Falls back when storage is unavailable. */
export function bumpSessionNumber(fallbackCurrent = 1): number {
  const storage = local();
  const meta = readJson<Meta>(storage, META_KEY);
  if (!meta) return isCounter(fallbackCurrent) ? fallbackCurrent + 1 : 1;
  // Repair rather than increment: arithmetic on a value that is not a counter
  // produces another value that is not a counter.
  meta.session_number = isCounter(meta.session_number) ? meta.session_number + 1 : 1;
  writeJson(storage, META_KEY, meta);
  return meta.session_number;
}

/**
 * SPEC §6: harvest ALL landing-URL params matching the configured list into
 * {name: {value, captured_at}}, persisted at anonymous_id scope. Adding a
 * platform is a config string, not an SDK release.
 */
export function harvestClickIds(paramNames: string[], search: string, nowIso: string): void {
  let params: URLSearchParams;
  try {
    params = new URLSearchParams(search);
  } catch {
    return;
  }
  const existing = getClickIds();
  let changed = false;
  for (const name of paramNames) {
    const value = params.get(name);
    if (value) {
      existing[name] = { value, captured_at: nowIso };
      changed = true;
    }
  }
  if (changed) writeJson(local(), CLICK_KEY, existing);
}

export function getClickIds(): Record<string, ClickId> {
  return readJson<Record<string, ClickId>>(local(), CLICK_KEY) ?? {};
}
