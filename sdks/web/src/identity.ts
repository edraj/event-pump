import { readCookie } from './cookies';
import { local, readJson, writeJson } from './storage';
import { uuidv4 } from './uuid';

const META_KEY = 'ep_meta';
const CLICK_KEY = 'ep_click_ids';

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * The value as an anonymous_id the API will accept, lower-cased, or null.
 *
 * Both sources are untrusted: localStorage hands back whatever JSON.parse
 * produced, and a cookie or ep_meta can be written by the host page or an
 * extension. An empty string or junk id is not a device we can recover — every
 * /v1/identity call answers 400, every event is rejected inside a 200 and acked
 * away, and nothing would ever replace the stored value — so it is discarded
 * and a fresh id minted instead. Lower-cased because the server canonicalizes
 * to lower case, and a case difference must not read as a different device.
 */
export function asAnonymousId(value: unknown): string | null {
  return typeof value === 'string' && UUID_RE.test(value) ? value.toLowerCase() : null;
}

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
 * S0 (SPEC §2, §3): load or create the device identity, in this order:
 *
 * 1. the server-set ep_aid cookie. The SDK only reads it. It is the durable
 *    copy: Safari ITP caps script-written storage at 7 days and exempts
 *    server-set first-party cookies, and SDK bundles already deployed read
 *    nothing else.
 * 2. the anonymous_id ep_meta is bound to. This is what keeps a device stable
 *    where the cookie never arrives — an API on a different registrable domain
 *    than the site, or a browser blocking it — which used to mint a new id on
 *    every page load.
 * 3. a new UUIDv4.
 *
 * first_seen_at and session_number are persisted bound to the anonymous_id
 * they belong to.
 */
export function loadDevice(now: number): DeviceIdentity {
  const storage = local();
  const meta = readJson<Meta>(storage, META_KEY);
  const metaAid = asAnonymousId(meta?.aid);
  const anonymousId = asAnonymousId(readCookie('ep_aid')) ?? metaAid ?? uuidv4();

  // The anonymous_id is not the one this metadata belongs to (the cookie and
  // localStorage diverged, the metadata is missing, or its id was unusable).
  // SPEC §2: both fields reset together — `first_seen_at = now,
  // session_number = 1`. Minting 0 here and leaving the caller to bump it made
  // an invalid counter representable, and on the one path that does not rotate
  // it was reported as-is: /v1/identity answers 400 to session_number 0.
  if (!meta || metaAid !== anonymousId) {
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

/**
 * +1 per session rotation (SPEC §2) of `anonymousId`'s counter. Falls back to
 * `fallbackCurrent + 1`, without writing, when storage is unavailable or
 * ep_meta now belongs to a different anonymous_id.
 *
 * ep_meta is shared by every tab and can be rebound by another tab after this
 * page loaded (its cookie and localStorage disagreed, or it minted an id).
 * That counter is the other id's: incrementing it would number this page's
 * session from another device's history and skip one of that device's.
 */
export function bumpSessionNumber(anonymousId: string, fallbackCurrent = 1): number {
  const storage = local();
  const meta = readJson<Meta>(storage, META_KEY);
  if (!meta || asAnonymousId(meta.aid) !== anonymousId) {
    return isCounter(fallbackCurrent) ? fallbackCurrent + 1 : 1;
  }
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
