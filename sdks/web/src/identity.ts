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
  let meta = readJson<Meta>(storage, META_KEY);
  if (!meta || meta.aid !== anonymousId) {
    meta = { aid: anonymousId, first_seen_at: new Date(now).toISOString(), session_number: 0 };
    writeJson(storage, META_KEY, meta);
  }
  return {
    anonymousId,
    firstSeenAt: meta.first_seen_at,
    sessionNumber: meta.session_number,
  };
}

/** +1 per session rotation (SPEC §2). Falls back when storage is unavailable. */
export function bumpSessionNumber(fallbackCurrent = 0): number {
  const storage = local();
  const meta = readJson<Meta>(storage, META_KEY);
  if (!meta) return fallbackCurrent + 1;
  meta.session_number += 1;
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
