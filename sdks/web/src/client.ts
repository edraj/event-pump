import { USER_ATTRIBUTES_ALLOWLIST } from './allowlist';
import { buildFbc, parseGaClientId, parseGaSessionId, readCookie, readFbp } from './cookies';
import { collectContext, collectLateContext } from './context';
import { EngagementStopwatch } from './engagement';
import {
  bumpSessionNumber,
  getClickIds,
  harvestClickIds,
  loadDevice,
  type DeviceIdentity,
} from './identity';
import { EventQueue } from './queue';
import { ensureSession, rotateSession, touchSession } from './session';
import { uuidv4 } from './uuid';

export const SDK_NAME = 'event-pump-web';
export const SDK_VERSION = '0.1.0';

const DEFAULT_CLICK_PARAMS = ['gclid', 'fbclid', 'ttclid', 'ScCid', 'twclid', 'epik', 'msclkid'];
const FLUSH_AT = 20;
const FLUSH_INTERVAL_MS = 30_000;
const GIVE_UP_MS = 24 * 60 * 60 * 1000;
const BACKOFF_MS = [5_000, 30_000, 120_000];

export interface EpConfig {
  endpoint: string;
  appToken: string;
  appVersion?: string;
  build?: string;
  clickIdParams?: string[];
  debug?: boolean;
}

export interface Handles {
  amplitude_device_id?: string;
  ga4_client_id?: string;
  ga4_session_id?: string;
  firebase_app_instance_id?: string;
  adjust_adid?: string;
  adjust_platform_ad_id?: string;
  fbp?: string;
  fbc?: string;
  click_ids?: Record<string, { value: string; captured_at: string }>;
}

/**
 * Person-scoped user attributes (SPEC §6.1). Partial upsert — pass only the
 * keys you want to change; pass `null` to clear a stored value.
 */
export interface Attributes {
  first_name?: string | null;
  last_name?: string | null;
  email?: string | null;
  phone?: string | null;
  gender?: string | null;
  city?: string | null;
}

export interface EventPump {
  init(config: EpConfig): void;
  track(eventName: string, properties?: Record<string, unknown>): void;
  page(properties?: Record<string, unknown>): void;
  setUser(userId: string): void;
  clearUser(): void;
  identify(handles: Handles): void;
  setUserAttributes(attributes: Attributes): void;
  eventHeaders(eventName?: string): Record<string, string>;
  flush(): void;
  /** Removes listeners and timers (SPA teardown). */
  destroy(): void;
}

interface PreInitEvent {
  eventName: string;
  properties?: Record<string, unknown>;
  occurredAt: string;
  eventId: string;
  now: number;
}

export function createEventPump(): EventPump {
  const ssr = typeof window === 'undefined';

  let config: EpConfig | null = null;
  let device: DeviceIdentity | null = null;
  let sessionKey = '';
  let sessionNumber = 0;
  let userId: string | null = null;
  let fullContext: Record<string, unknown> = {};
  let saveData = false;

  const queue = new EventQueue();
  const stopwatch = new EngagementStopwatch();
  const preInit: PreInitEvent[] = [];

  // S4 gate: no events leave before S3 completes (SPEC §3)
  let gateOpen = false;
  let sending = false;
  let failures = 0;
  let nextAttemptAt = 0;
  let flushTimer: ReturnType<typeof setInterval> | null = null;

  function debugLog(message: string): void {
    if (config?.debug) console.debug(`[event-pump] ${message}`);
  }

  function handles(): Handles {
    const clickIds = getClickIds();
    const fbclid = clickIds['fbclid'];
    const result: Handles = {
      amplitude_device_id: device!.anonymousId, // never mint a separate one (SPEC §6)
      ga4_client_id: parseGaClientId() ?? device!.anonymousId,
      fbp: readFbp() ?? undefined,
      fbc: readCookie('_fbc')
        ?? (fbclid ? buildFbc(fbclid.value, Date.parse(fbclid.captured_at)) : undefined),
      click_ids: Object.keys(clickIds).length > 0 ? clickIds : undefined,
    };
    const gaSession = parseGaSessionId();
    if (gaSession) result.ga4_session_id = gaSession;
    return result;
  }

  async function postJson(path: string, body: unknown): Promise<boolean> {
    try {
      const response = await fetch(`${config!.endpoint}${path}`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Authorization: `Bearer ${config!.appToken}`,
        },
        credentials: 'include', // ep_aid rides on same-site requests (SPEC §9.5)
        keepalive: true,
        body: JSON.stringify(body),
      });
      return response.ok;
    } catch {
      return false;
    }
  }

  function registerIdentity(extra?: Partial<Record<string, unknown>>): void {
    gateOpen = false;
    const body: Record<string, unknown> = {
      session_key: sessionKey,
      anonymous_id: device!.anonymousId,
      session_number: sessionNumber,
      first_seen_at: device!.firstSeenAt,
      handles: handles(),
      context: fullContext,
      ...extra,
    };
    if (userId !== null) body.user_id = userId;
    void postJson('/v1/identity', body).then(() => {
      gateOpen = true; // S4: queue opens only after the identity row exists
      flush();
    });
  }

  function materialize(event: PreInitEvent): void {
    const payload: Record<string, unknown> = {
      event_id: event.eventId,
      event_name: event.eventName,
      occurred_at: event.occurredAt,
      anonymous_id: device!.anonymousId,
      session_key: sessionKey,
      context: {
        page:
          typeof location === 'undefined'
            ? undefined
            : { path: location.pathname, title: document.title || undefined, referrer: document.referrer || undefined },
        engagement_time_msec: stopwatch.take(event.now),
        session_number: sessionNumber,
        sdk: { name: SDK_NAME, version: SDK_VERSION },
      },
    };
    if (userId !== null) payload.user_id = userId;
    if (event.properties) payload.properties = event.properties;
    queue.push(payload, event.now);
    touchSession(event.now);
    if (queue.size() >= FLUSH_AT) flush();
  }

  function flush(): void {
    if (ssr || !gateOpen || sending || !config) return;
    const now = Date.now();
    if (now < nextAttemptAt) return;

    // 24h give-up (SPEC §7)
    const expired = queue.peek(queue.size()).filter((e) => now - e.firstAttemptAt > GIVE_UP_MS);
    if (expired.length > 0) {
      queue.ack(expired);
      debugLog(`gave up on ${expired.length} event(s) older than 24h`);
    }

    const batch = queue.peek(saveData ? Math.floor(FLUSH_AT / 2) : FLUSH_AT);
    if (batch.length === 0) return;

    sending = true;
    void postJson('/v1/events', { events: batch.map((e) => e.event) }).then((ok) => {
      sending = false;
      if (ok) {
        queue.ack(batch);
        failures = 0;
        nextAttemptAt = 0;
        if (queue.size() > 0) flush();
      } else {
        const delay = BACKOFF_MS[Math.min(failures, BACKOFF_MS.length - 1)]!;
        failures += 1;
        nextAttemptAt = Date.now() + delay;
        debugLog(`flush failed; retrying in ${delay}ms`);
      }
    });
  }

  function beaconFlush(): void {
    if (ssr || !gateOpen || !config) return;
    const batch = queue.peek(100);
    if (batch.length === 0) return;
    // sendBeacon cannot set headers, so the token rides as a query param.
    // Deliberately NOT acked: at-least-once wins — if the user returns, the
    // batch is re-sent and the server dedupes on event_id (SPEC §7).
    const url = `${config.endpoint}/v1/events?token=${encodeURIComponent(config.appToken)}`;
    const payload = JSON.stringify({ events: batch.map((e) => e.event) });
    if (typeof navigator !== 'undefined' && navigator.sendBeacon) {
      navigator.sendBeacon(url, payload);
    } else {
      void postJson('/v1/events', { events: batch.map((e) => e.event) }).then(
        (ok) => ok && queue.ack(batch),
      );
    }
  }

  function rotationCheck(now: number): void {
    const result = ensureSession(now);
    if (!result.rotated) return;
    sessionKey = result.sessionKey;
    sessionNumber = bumpSessionNumber(sessionNumber);
    device = { ...device!, sessionNumber };
    registerIdentity(); // rerun S3–S4 (SPEC §3)
  }

  function onVisibilityChange(): void {
    const now = Date.now();
    if (document.visibilityState === 'hidden') {
      stopwatch.pause(now);
      touchSession(now);
      beaconFlush();
    } else {
      stopwatch.start(now);
      rotationCheck(now);
    }
  }

  function onPageShow(event: PageTransitionEvent): void {
    if (!event.persisted) return; // BFCache restore only (SPEC §3)
    const now = Date.now();
    stopwatch.start(now);
    rotationCheck(now);
  }

  const client: EventPump = {
    init(cfg: EpConfig): void {
      if (ssr || config) return;
      config = cfg;
      const now = Date.now();

      // S0: device identity + click-id harvest at anonymous_id scope
      device = loadDevice(now);
      harvestClickIds(
        cfg.clickIdParams ?? DEFAULT_CLICK_PARAMS,
        location.search,
        new Date(now).toISOString(),
      );

      // S1: session
      const sessionResult = ensureSession(now);
      sessionKey = sessionResult.sessionKey;
      sessionNumber = sessionResult.rotated
        ? bumpSessionNumber(device.sessionNumber)
        : device.sessionNumber;

      // S2: context (sync now; async parts patch later — never block)
      fullContext = collectContext({ appVersion: cfg.appVersion, build: cfg.build });
      saveData = fullContext.save_data === true;

      if (document.visibilityState === 'visible') stopwatch.start(now);
      queue.restore();

      // S3 deferred one microtask so pre-load stub calls (incl. setUser)
      // drain in order before the registration materializes (SPEC §3, §7)
      void Promise.resolve().then(() => {
        registerIdentity();
        void collectLateContext().then((late) => {
          if (late && Object.keys(late).length > 0) {
            fullContext = { ...fullContext, ...late };
            void postJson('/v1/identity', {
              session_key: sessionKey,
              anonymous_id: device!.anonymousId,
              context: late,
            });
          }
        });
      });

      // buffered pre-init events materialize with the now-known ids
      for (const pending of preInit.splice(0)) materialize(pending);

      flushTimer = setInterval(flush, FLUSH_INTERVAL_MS);
      document.addEventListener('visibilitychange', onVisibilityChange);
      window.addEventListener('pageshow', onPageShow as EventListener);
    },

    track(eventName: string, properties?: Record<string, unknown>): void {
      if (ssr) return;
      const now = Date.now();
      const stamped: PreInitEvent = {
        eventName,
        properties,
        occurredAt: new Date(now).toISOString(),
        eventId: uuidv4(),
        now,
      };
      if (!device) {
        preInit.push(stamped); // buffered, never dropped (SPEC §3)
        return;
      }
      materialize(stamped);
    },

    page(properties?: Record<string, unknown>): void {
      client.track('page_view', properties);
    },

    setUser(newUserId: string): void {
      userId = newUserId;
      // NEVER rotates anonymous_id — that would orphan the pre-login funnel
      if (device) registerIdentity();
    },

    clearUser(): void {
      userId = null;
      if (!device) return;
      const now = Date.now();
      sessionKey = rotateSession(now); // rotate session_key only (SPEC §3)
      sessionNumber = bumpSessionNumber(sessionNumber);
      registerIdentity();
    },

    identify(extraHandles: Handles): void {
      if (!device) return;
      void postJson('/v1/identity', {
        session_key: sessionKey,
        anonymous_id: device.anonymousId,
        handles: extraHandles,
      });
    },

    setUserAttributes(attributes: Attributes): void {
      if (ssr || !device) return;
      if (userId === null) {
        debugLog('setUserAttributes ignored: no user_id (call setUser() first)');
        return;
      }
      const filtered: Record<string, unknown> = {};
      for (const key of Object.keys(attributes)) {
        if (USER_ATTRIBUTES_ALLOWLIST.has(key)) {
          filtered[key] = (attributes as Record<string, unknown>)[key];
        } else {
          debugLog(`setUserAttributes: dropped unknown key "${key}"`);
        }
      }
      if (Object.keys(filtered).length === 0) return;
      void postJson('/v1/identity', {
        session_key: sessionKey,
        anonymous_id: device.anonymousId,
        user_id: userId,
        attributes: filtered,
      });
    },

    eventHeaders(eventName?: string): Record<string, string> {
      if (!device) return {};
      const headers: Record<string, string> = {
        'X-Session-Key': sessionKey,
        'X-Anonymous-Id': device.anonymousId,
      };
      if (eventName) headers['X-Event'] = eventName;
      return headers;
    },

    flush,

    destroy(): void {
      if (ssr) return;
      if (flushTimer !== null) clearInterval(flushTimer);
      flushTimer = null;
      document.removeEventListener('visibilitychange', onVisibilityChange);
      window.removeEventListener('pageshow', onPageShow as EventListener);
    },
  };

  return client;
}
