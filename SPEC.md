# Event Pump — SPEC

**Status: APPROVED v1.1 (2026-07-14). Behavior changes require spec re-approval.**

**v1.1 changes:** adds §6.1 (user attributes) — person-scoped storage
(`user_attributes` table keyed by `user_id`), allowlisted in the tracking plan,
delivered via existing senders under per-destination consent gates. Adds §9.6
(DSR delete endpoint, DB-only). Extends §7 (SDK API: `setUserAttributes` on
both SDKs; Flutter `track()` / `screen()` become named-arg for `properties:`),
§9.2 (wire body), §11 (new table + reserved event `ep_attributes_synced` +
destination `moengage_customer`), §12 (per-destination mapping), §13 (config +
tracking plan). See §6.1.

This document is the contract between the client SDKs (`/sdks/web`, `/sdks/flutter`),
the ingestion API, the platform's in-database SQL producers, and the delivery worker.
Code conforms to this spec; a change of behavior requires a spec change first.

---

## 0. Overview

Event Pump ingests product events from web/mobile clients and backend services into a
PostgreSQL outbox and delivers them via a worker to downstream destinations (GA4
Measurement Protocol, Amplitude HTTP V2, MoEngage Data API, Adjust S2S; later
Meta/Snap/TikTok CAPI) through their server-to-server APIs.

The outbox lives **inside the e-commerce platform's existing PostgreSQL 18 database**,
so platform services can emit server-fact events in the same transaction as the
business write they describe (§8, §10).

```
 web SDK ──────┐                                      ┌─> GA4 MP
 flutter SDK ──┤  POST /v1/events, /v1/identity       ├─> Amplitude
               ▼                                      ├─> MoEngage
        ┌─────────────┐        ┌──────────────┐       ├─> Adjust S2S
        │ eventpump   │        │  PostgreSQL  │       └─> Meta CAPI (base, off)
        │    api      │───────>│  outbox +    │────┐
        └─────────────┘        │  delivery +  │    │  ┌─────────────┐
 external backend ────────────>│  identity    │<───┴──│ eventpump   │
   POST /internal/v1/events    └──────────────┘ claim │   worker    │
                                      ▲               └─────────────┘
 platform services ── emit_event() ───┘  (same business transaction)
```

Terminology: "destination" = a downstream S2S API; "producer" = anything that creates
events; "handle" = a destination-specific identity value (§6).

---

## 1. Canonical event model

| Field          | Type                  | Set by            | Notes                                                        |
|----------------|-----------------------|-------------------|--------------------------------------------------------------|
| `event_id`     | uuid v4               | producer          | Dedupe key. At-least-once delivery from SDKs; server dedupes. |
| `event_name`   | text, snake_case      | producer          | Must be in the per-origin allowlist (tracking plan, §13).    |
| `origin`       | `client` \| `server`  | **server**        | Stamped by ingestion path. Never producer-supplied.          |
| `occurred_at`  | timestamptz (ISO 8601 with offset) | producer | Stamped at `track()` time, not at flush time.       |
| `received_at`  | timestamptz           | **server**        | Ingestion time. Partition key.                               |
| `user_id`      | text, nullable        | producer          | Only ever from `setUser()` / server knowledge. Never inferred. |
| `anonymous_id` | uuid, nullable        | producer          | Required on client-origin events; optional on server-origin. |
| `session_key`  | uuid v7, nullable     | producer          | Joins to `identity_registry` for enrichment.                 |
| `properties`   | JSON object           | producer          | Free-form event payload.                                     |
| `context`      | JSON object           | producer + server | Per-event minimal context (§5). Server injects `ip`.         |

### Server validation — per event, independently

An event is **rejected** (reported, not stored) when any of:

- an unknown **top-level** field is present (strict envelope; see context leniency below);
- `event_name` is not in the allowlist for the ingesting origin;
- the serialized event object exceeds **32 KB** (UTF-8 bytes);
- `occurred_at` is outside `(received_at − 7 days, received_at + 1 hour)`;
- `event_id` / `anonymous_id` / `session_key` are present but not valid UUIDs;
- `event_name` is not snake_case (`^[a-z][a-z0-9_]{0,63}$`).

Batch rules: client batches carry at most **100 events**; a larger batch is rejected
whole with `400`. Otherwise acceptance is **per event**: valid events are stored,
invalid ones are reported back by index (§9.1). A duplicate `event_id` is an
idempotent success (no new row, counted as accepted).

Inside `context`, unknown keys are **silently dropped** (forward compatibility across
SDK versions), never a rejection cause.

---

## 2. Identity model — three levels

Both SDKs implement this identically.

| Level   | Field          | Format | Storage & lifetime                                                       |
|---------|----------------|--------|--------------------------------------------------------------------------|
| Person  | `user_id`      | ours   | `setUser()` on login only; never inferred                                |
| Device  | `anonymous_id` | UUIDv4 | web: cookie `ep_aid` (**server-set**); flutter: `shared_preferences`     |
| Session | `session_key`  | UUIDv7 | web: `sessionStorage` (per-tab visit); flutter: memory + persisted (§3)  |

Rules:

- **`anonymous_id` IS the device id on web.** No hardware id exists by design; a
  persisted random UUID is the industry construct. Web scope = browser profile;
  incognito / another browser = new id. **No fingerprinting** (canvas/font/WebGL/
  entropy) ever; no recovery after cookie clear.
- **Web: the SDK only READS `ep_aid`; only the SERVER sets it** via first-party
  `Set-Cookie` (~13 months, §9.5) on API responses when the request carries no
  `ep_aid`. Never `document.cookie` writes (Safari ITP caps script-written storage
  at 7 days). If absent client-side: generate a UUIDv4, hold it **in memory**, and
  transmit it so the server can set the cookie.
- **Flutter:** `shared_preferences`; dies on uninstall; no ANDROID_ID / IMEI / IDFA.
- Alongside `anonymous_id` the SDK persists `first_seen_at` and `session_number`
  (= 1 at creation, +1 per session rotation). `session_number` rides in every
  `/v1/identity` registration and every event's context. The **server** owns
  authoritative first-visit determination (§8); SDKs only report.
- The persisted metadata (`first_seen_at`, `session_number`) is bound to the
  `anonymous_id` value it was created with. If the current `anonymous_id` no longer
  matches (cookie cleared while localStorage survived, or vice versa), the metadata
  resets: `first_seen_at = now`, `session_number = 1`.

Storage keys (normative):

| SDK     | Key                                            | Contents                                              |
|---------|------------------------------------------------|-------------------------------------------------------|
| web     | cookie `ep_aid`                                | `anonymous_id` (server-set; SDK read-only)            |
| web     | localStorage `ep_meta`                         | `{anonymous_id, first_seen_at, session_number}`       |
| web     | localStorage `ep_click_ids`                    | click-id map (§6), `anonymous_id`-scoped              |
| web     | localStorage `ep_queue`                        | persisted event queue (§7)                            |
| web     | sessionStorage `ep_session`                    | `{session_key, last_active_at}`                       |
| flutter | prefs `ep_aid`, `ep_first_seen_at`, `ep_session_number` | device identity + metadata                    |
| flutter | prefs `ep_session_key`, `ep_last_active_at`    | session state (§3)                                    |
| flutter | file `<app-support>/event_pump/queue.jsonl`    | persisted event queue (§7)                            |

---

## 3. Session initialization & rotation — state machine

On init (cold start / first page load), **strictly ordered**:

- **S0** — Load or create `anonymous_id` (+ `first_seen_at`, `session_number`).
- **S1** — If there is no persisted `last_active_at`, OR it is older than **30
  minutes**: mint a NEW `session_key` (UUIDv7) and increment `session_number`.
  Otherwise resume the existing session.
- **S2** — Collect full context (§5). Async parts may resolve late — **never block**.
- **S3** — `POST /v1/identity {session_key, anonymous_id, user_id?, session_number,
  handles (§6), context}`.
- **S4** — Open the event queue for flushing.

Events `track()`ed during S0–S3 are **buffered, never dropped, and never sent before
S3 completes** — the identity row must exist before the first flush so server-side
enrichment can join on `session_key`.

**Rotation.** On foreground/visible return (flutter: `resumed`; web:
`visibilitychange -> visible` and `pageshow` with `persisted == true` i.e. BFCache
restore), if `last_active_at` is older than **30 minutes** (GA4's session window —
keeps session counts reconcilable with GA4), rerun S1–S4.

`last_active_at` is updated on every `track()` / `screen()` / `page()` call and on
transition to background/hidden.

- Offline events keep the `session_key` that was current at `track()` time.
- Storage unavailable (privacy mode, quota): memory-only `session_key`. **NEVER**
  derive session ids from `anonymous_id` + time buckets.
- **Flutter persistence:** `session_key` is persisted in `shared_preferences`
  alongside `last_active_at`, held in memory at runtime. A cold start within 30
  minutes of `last_active_at` therefore **resumes** the same session (S1 semantics);
  otherwise a new session is minted.
- **Web:** `session_key` lives in `sessionStorage`, so a session is scoped to one
  tab and survives same-tab reloads. A new tab is a new session.

`setUser(id)`: attach `user_id` and re-register (rerun S3 with the same
`session_key`). **NEVER rotates `anonymous_id`** — rotating would orphan the
pre-login funnel exactly at conversion.

`clearUser()` (logout): drop `user_id`, rotate `session_key` only (new UUIDv7,
`session_number`+1, rerun S3–S4). `anonymous_id` is untouched.

---

## 4. Engagement time

SDKs run a foreground/visible stopwatch:

- Web: runs while the document is visible; pauses on `hidden`.
- Flutter: runs while the app lifecycle state is `resumed`; pauses otherwise.

Each event carries `context.engagement_time_msec` = milliseconds accumulated since
the **previous event was stamped** (the stopwatch resets to 0 every time an event is
enqueued, so multiple events in one flush batch each carry their own slice). Required
for GA4 MP engagement and realtime reporting.

---

## 5. Context enrichment — automatic, downstream-driven

Full context is collected **once per session** at S2 and sent in `/v1/identity`.
Per-event context carries ONLY: page path / screen name, `engagement_time_msec`,
`session_number`, and `sdk {name, version}`.

Fields exist because downstream senders consume them:

| Context field                    | Web source                     | Flutter source            | Consumed by         |
|----------------------------------|--------------------------------|---------------------------|---------------------|
| language, languages              | navigator                      | PlatformDispatcher        | GA4 device.language |
| timezone                         | Intl resolvedOptions           | DateTime/native           | internal            |
| screen_resolution, viewport, dpr | screen/window                  | views API (post-1st-frame)| GA4 device          |
| os, os_version, model, category  | userAgentData high-entropy     | device_info_plus          | GA4 device object   |
| raw user_agent                   | navigator.userAgent            | n/a                       | GA4/CAPI user_agent |
| app_version, build               | config-injected                | package_info_plus         | all destinations    |
| connection type, save_data       | navigator.connection (guarded) | connectivity_plus         | telemetry throttle  |
| orientation, color_scheme, touch | matchMedia/screen              | MediaQuery                | internal            |
| referrer, initial URL, utm_*     | document/location              | n/a                       | GA4/attribution     |
| click_ids                        | landing URL params             | n/a (Adjust owns app side)| CAPI senders        |

- Chromium-only fields (`userAgentData` high-entropy, `navigator.connection`)
  degrade to `undefined` elsewhere — never throw, never polyfill.
- All web collectors are SSR-safe (no top-level `window`/`document` access).
- Late async collectors (high-entropy UA hints, flutter post-first-frame view
  metrics, `device_info_plus`) patch via a **follow-up partial `/v1/identity`
  upsert** — init never blocks on them.
- The **server** additionally records the client IP (from `X-Real-IP`) into the
  identity registry and into each client-origin event's context at ingestion —
  consumed by GA4 `ip_override`, Adjust S2S, and CAPI `user_data`. Config option
  (§13): resolve IP → geo at ingestion and store the location object **instead of**
  the raw IP.

### Per-event context shape (normative)

```jsonc
{
  "page":   { "path": "/checkout", "title": "…", "referrer": "…" },  // web
  "screen": { "name": "CheckoutScreen" },                            // flutter
  "engagement_time_msec": 1234,
  "session_number": 7,
  "sdk": { "name": "event-pump-web", "version": "1.0.0" }
}
```

---

## 6. Destination identity handles — automatic harvesting

`identify(handles)` supports **partial late updates** (each call upserts only the
keys provided). SDK defaults require no app code:

- `amplitude_device_id := anonymous_id` — never mint a separate one.
- `ga4_client_id`: web — parse the `_ga` cookie when present, else `anonymous_id`;
  flutter — from the Firebase integration when present, else `anonymous_id`.
  `ga4_session_id` likewise when available (web: `_ga_<container>` cookie; flutter:
  Firebase Analytics session id).
- Meta: web — read `_fbp`; build/read `_fbc` from a landing `fbclid`
  (`fb.1.<unix_ms>.<fbclid>` per Meta's parameter spec, verified against current
  docs at implementation time).
- `click_ids`: the web SDK harvests **ALL** landing-URL params matching a
  configurable list (default: `gclid, fbclid, ttclid, ScCid, twclid, epik,
  msclkid`) into `{name: {value, captured_at}}`, persisted at `anonymous_id` scope.
  Adding a platform = a config string, not an SDK release. Re-captured ids
  overwrite (latest click wins).
- `adjust_adid`: **flutter only**, supplied via `identify()` once the Adjust SDK
  yields it (late partial update is the designed path). Web never sets it — the
  server's no-adid ⇒ `skipped` rule (§12) is intended behavior, not an error.

Handle set (registry columns, §11): `amplitude_device_id`, `ga4_client_id`,
`ga4_session_id`, `firebase_app_instance_id`, `adjust_adid`,
`adjust_platform_ad_id`, `fbp`, `fbc`, `click_ids`.

### 6.1 User attributes

Distinct from destination handles (§6): **attributes** describe the *person*,
not the device or session. Other CDPs call these "traits" (Segment) or
"user properties" (GA4/Amplitude) or "user attributes" (MoEngage).

- **Wire slot:** new top-level `"attributes"` object on `POST /v1/identity`
  (§9.2), sibling of `handles` and `context`. Partial upsert — keys present
  replace, keys absent survive. `null` value clears a stored key.
- **Requires `user_id`:** the `attributes` block requires `user_id` in the
  same body OR already stored on `identity_registry[session_key]`. Otherwise
  rejected with `attributes_require_user_id` (400).
- **Person-scoped storage:** table `user_attributes` keyed by `user_id`
  (§11). Persists across sessions, devices, and logouts. `clearUser()` does
  NOT delete stored attributes.
- **Allowlist:** declared in the tracking-plan JSON under `attributes`
  (§13). A name not in the allowlist is rejected with
  `unknown_attribute:<name>` (400). Empty/absent block disables the feature.
- **PII:** values are PII by construction. Never log values; log attribute
  *names* only (§13). Server error responses may reference names, never
  values.
- **SDK method (both SDKs):** `setUserAttributes(Map)` — partial upsert.
  Requires `user_id` (via `setUser()`) first, else no-op (release) /
  warn (debug). SDKs hardcode the shipped allowlist and drop unknown keys
  locally to avoid roundtrip rejections.
- **Consent gating:** attribute-derived outbound fields for each destination
  are behind per-destination `EP_<X>_ATTRIBUTES_ENABLED` flags (§13). When
  OFF, the destination sender still delivers the event core; only
  attribute-derived fields are omitted.

**Normative allowlist (v1.1):**

| Name         | Type   | Normalization                                     | Length cap |
|--------------|--------|---------------------------------------------------|------------|
| `first_name` | string | trim                                              | 128        |
| `last_name`  | string | trim                                              | 128        |
| `email`      | string | trim + lowercase; RFC 5322 basic shape            | 254        |
| `phone`      | string | E.164 (`+` then 8–15 digits, no separators)       | 16         |
| `gender`     | enum   | one of `male` / `female` / `other` / `unknown`    | —          |
| `city`       | string | trim                                              | 128        |

**Rejection codes:** `unknown_attribute:<name>`, `invalid_attribute:<name>`,
`attributes_too_large` (serialized `attributes` > 4 KB),
`attributes_require_user_id`.

**Per-destination mapping** (verified against each destination's current
public API docs at implementation time per §12):

| Attribute    | GA4 MP                              | Amplitude V2                  | MoEngage `type:"customer"`         | Adjust S2S                        |
|--------------|-------------------------------------|-------------------------------|------------------------------------|-----------------------------------|
| `first_name` | `user_properties.first_name`        | `user_properties.first_name`  | `attributes.first_name` (raw)      | `partner_params.first_name`       |
| `last_name`  | `user_properties.last_name`         | `user_properties.last_name`   | `attributes.last_name` (raw)       | `partner_params.last_name`        |
| `email`      | `user_data.sha256_email_address`    | `user_properties.email`       | `attributes.email` (raw)           | `s2s_email` (SHA-256 of lower)    |
| `phone`      | `user_data.sha256_phone_number`     | `user_properties.phone`       | `attributes.mobile` (E.164)        | `s2s_phone` (SHA-256, no `+`)     |
| `gender`     | `user_properties.gender`            | `user_properties.gender`      | `attributes.gender` (`M`/`F`/`O`)  | `partner_params.gender` (`m`/`f`) |
| `city`       | `user_properties.city`              | `user_properties.city`        | `attributes.city`                  | `partner_params.city`             |

A destination with no mapped attributes for the current stored state omits
them. **Attributes never rescue an event that would otherwise be `skipped`
for missing identity** (§12).

**MoEngage `type:"customer"` sync — routed through the outbox.** MoEngage
requires a separate `type:"customer"` payload to set user attributes. The
sync runs through the standard outbox/delivery machinery (retries,
dead-letter, circuit breaker) — not fire-and-forget — so a lost sync
self-heals on retry rather than requiring a repeat client call:

- New destination code `moengage_customer` (distinct from `moengage`
  events).
- Reserved server-origin event `ep_attributes_synced` — auto-registered
  in `event_registry` at boot with `"reserved": true`; producers cannot
  emit it via `emit_event()` or `/internal/v1/events` (error:
  `reserved_event_name`). Its only route: `["moengage_customer"]`.
- On `/v1/identity` upsert where `attributes` was changed AND
  `EP_MOENGAGE_ATTRIBUTES_ENABLED=true` AND
  `user_attributes.hash != user_attributes.moengage_synced_hash`: the API
  handler inserts a synthetic outbox row (`event_name =
  'ep_attributes_synced'`, `user_id = <the user>`), fanning out one
  delivery row to `moengage_customer`.
- The MoEngage sender dispatches by destination: `moengage` →
  `type:"event"`; `moengage_customer` → fetch `user_attributes` by
  `user_id`, capture `hash_at_fetch` alongside the attributes used to
  build the payload, send `type:"customer"`. On `delivered`: update
  `user_attributes.moengage_synced_hash = <hash_at_fetch>` (the hash of
  the payload actually sent — **not** the row's current hash at
  write-back time), `moengage_synced_at = now()`. This preserves
  correctness under a concurrent `setUserAttributes` landing between
  fetch and delivery: the row's `hash` is still ahead of
  `moengage_synced_hash`, so the sweep and the next upsert both correctly
  re-enqueue.
- Optional worker sweep (piggybacks on retention timer): re-enqueue rows
  where `hash != moengage_synced_hash AND updated_at < now() - '1 hour'`.
  Belt-and-suspenders for the healthy enqueue-on-upsert path.

---

## 7. SDK queue & flush

- Buffer locally; flush when ANY of: **20 events**, **30 s** timer, transition to
  background/hidden (web: `sendBeacon`, fallback `fetch` with `keepalive: true`).
- **At-least-once**: the server dedupes on `event_id`; a batch is removed from the
  queue only after a `2xx` response.
- Retry backoff on failure: **5 s → 30 s → 2 m**, then every 2 m; give up **24 h**
  after an event's first send attempt, dropping it with a debug log.
- Queue persists across restarts:
  - web: localStorage `ep_queue`, cap **200** events;
  - flutter: JSONL file in the app-support dir, atomic truncate-on-success via
    temp-file + rename, cap **500** events;
  - both: oldest-first drop on overflow.
- Flutter: a `connectivity_plus` listener triggers a flush when connectivity is
  regained.
- `save_data` / OS data-saver active: halve the flush batch size and skip optional
  context fields; **never block core delivery**.

### SDK public API (normative)

Web (`/sdks/web`):

```ts
init(config: { endpoint: string; appToken: string; app_version?: string;
               build?: string; clickIdParams?: string[]; debug?: boolean })
track(event_name: string, properties?: object)
page(properties?: object)          // stamps context.page from location
setUser(user_id: string)           // login only
clearUser()                        // logout: rotate session only
identify(handles: Partial<Handles>)
setUserAttributes(attributes: Partial<Attributes>)   // §6.1 — partial upsert, requires setUser()
eventHeaders(event_name?: string): Record<string, string>   // §8 X-Event pattern
flush(): Promise<void>
```

Flutter (`event_pump`):

```dart
EventPump.init(EventPumpConfig(endpoint, appToken, ...));
EventPump.instance.track(name, properties: {...});      // v1.1: positional → named `properties:`
EventPump.instance.screen(name, properties: {...});     // v1.1: same
EventPump.instance.setUser(userId);  EventPump.instance.clearUser();
EventPump.instance.identify({handles});                 // e.g. late adjust_adid
EventPump.instance.setUserAttributes({attributes});     // §6.1 — partial upsert, requires setUser()
EventPump.instance.eventHeaders([eventName]);           // Map<String, String>
EventPump.instance.flush();
// EventPumpDioInterceptor (dio), plain-http helper, optional EventPumpRouteObserver (OFF by default)
```

> **v1.1 breaking change (Flutter only):** `track()` and `screen()` parameter
> lists changed from positional to named — call sites must use
> `properties: {...}`. Web SDK signatures are unchanged.

The IIFE build ships with an async stub snippet: the inline snippet creates
`window.ep = {q: []}`; all pre-load calls (including `setUser`) are queued and
**drained in order** through S0–S4 once `ep.js` loads.

---

## 8. Server-side semantics

### Producer paths (three)

Each event name is assigned to exactly **one** path in the tracking plan, enforced
by per-origin allowlists:

| Path | Producer | Origin | Mechanism |
|------|----------|--------|-----------|
| a | Platform services sharing the database | `server` | `emit_event(...)` called **inside their business transaction** — the PRIMARY server-fact path. Ships as `sql/producer_contract.sql` (§10). |
| b | Backend producers **outside** this database | `server` | `POST /internal/v1/events` — separate auth token, separate internal listener. |
| c | Client SDKs | `client` | `POST /v1/events` — `origin='client'` names only. |

Services that share the database use the SQL contract, **not** the internal HTTP
endpoint (stated plainly in the README).

### X-Event header pattern (tier-2 client-fact events)

Platform API endpoints emit an event **only when the client stamped** these headers
on a request it already makes:

```
X-Event:        <event_name>
X-Session-Key:  <session_key>
X-Anonymous-Id: <anonymous_id>
```

The platform handler passes them to `emit_event(...)`; unknown names are rejected
by the function. SDKs produce the headers via `eventHeaders()`. No extra HTTP
round-trip is ever introduced for tier-2 events.

### First-visit (server-authoritative)

On `/v1/identity` (and on SQL emission carrying an `anonymous_id`):
`INSERT ... ON CONFLICT DO NOTHING` into `first_seen(anonymous_id, first_seen_at)`.
A **successful insert** emits the canonical server event `first_visit` — once ever
per `anonymous_id` by construction. Default routing: **MoEngage + internal only**
("internal" = the outbox row itself; no delivery rows). **Never** forward
`first_visit` to GA4/Amplitude — they derive their own new-user status and
forwarding double-counts.

### Routing map

`event_name -> [destinations]` in config (§13). An event gets delivery rows **only**
for its routed destinations at insert time — no blanket `skipped` noise. An event
with no routed destinations is stored in the outbox with zero delivery rows. The
map includes per-destination event-name translation (Meta standard names, Adjust
event tokens).

---

## 9. HTTP API wire contract

Common: JSON bodies, UTF-8, `Content-Type: application/json`. Errors:
`{"error": "<machine_code>", "detail": "<human text>"}`. `401` bad/missing token,
`400` malformed body / oversize batch, `413` body over 4 MB, `429` rate-limited
(with `Retry-After`).

### 9.1 `POST /v1/events` — client SDK batches

- Auth: `Authorization: Bearer <client app token>` (per client app; token → app_id
  map in config). Tokens are distributed with the app and are not secrets; they
  identify + rate-limit, they do not authorize privileged actions.
- Rate limit per token (config: requests/window). `429` + `Retry-After` on breach.
- Body: `{"events": [<event>, …]}`, max 100. Event shape: `event_id`, `event_name`,
  `occurred_at`, `anonymous_id`, `session_key?`, `user_id?`, `properties?`,
  `context?` — nothing else (§1).
- Server stamps `origin='client'`, `received_at`, and `context.ip` from
  `X-Real-IP`.
- Response `200`:
  `{"accepted": <n>, "rejected": [{"index": i, "event_id": "…", "reason": "…"}]}`.

### 9.2 `POST /v1/identity` — identity registration / partial upsert

- Auth + rate limit: same as 9.1.
- Body: `{"session_key", "anonymous_id", "session_number", "user_id"?,
  "first_seen_at"?, "handles"?: {…§6…}, "attributes"?: {…§6.1…},
  "context"?: {…§5 full…}}`.
- **Partial upsert**: only the fields present are written. `handles.click_ids`
  merges per click-id name (latest `captured_at` wins). `attributes` merges at
  the top level (present keys replace, absent keys survive; `null` clears a
  key); values validated and normalized per §6.1; requires `user_id` in scope.
  A hash change vs `user_attributes.moengage_synced_hash` enqueues a
  `moengage_customer` delivery per §6.1. `context` merges at the
  top level (present keys replace, absent keys survive) so late collectors can
  patch without erasing earlier fields.
- Server records `client_ip` (or resolved geo) from `X-Real-IP` on every upsert.
- Runs the first-visit logic (§8).
- Response: `204`.

### 9.3 `POST /internal/v1/events` — external backend producers

- Separate listener (own port, intended to be firewalled / bound to an internal
  interface). Auth: `Authorization: Bearer <internal token>` — a real secret.
- Same envelope as 9.1; `origin='server'` allowlist; `anonymous_id` optional,
  `user_id` allowed; no cookie handling, no `X-Real-IP` capture (config-optional).

### 9.4 `GET /healthz` — both processes

`SELECT 1` against the database: `200 {"status":"ok"}` or `503`.
`GET /metrics` — Prometheus text exposition, both processes (§13 observability).

### 9.5 `ep_aid` cookie contract

When a request to 9.1/9.2 carries **no** `ep_aid` cookie and the body provides a
valid UUID `anonymous_id`, the response sets:

```
Set-Cookie: ep_aid=<anonymous_id>; Max-Age=34128000; Path=/;
            Domain=<config>; Secure; SameSite=Lax
```

- `Max-Age` 34 128 000 s ≈ 395 days (~13 months). **Not** `HttpOnly` — the SDK must
  read it.
- Server-set first-party cookies are exempt from Safari ITP's 7-day cap; this is
  the entire reason the server owns the write.
- **Deployment requirement:** the ingestion API must be served from a subdomain of
  the site's registrable domain (e.g. `collect.example.com` for `www.example.com`)
  with `Domain=.example.com`, so `SameSite=Lax` cookies flow on SDK requests.
- CORS: exact-origin allowlist (config) echoed in
  `Access-Control-Allow-Origin`, with `Access-Control-Allow-Credentials: true`;
  the web SDK sends `credentials: 'include'`. (`sendBeacon` carries cookies by
  default; cookie-setting always happens on the S3 identity fetch anyway.)

### 9.6 `DELETE /internal/v1/user_attributes/{user_id}` — DSR deletion

- Internal listener, same as 9.3. Auth: `Authorization: Bearer <internal token>`.
- Deletes the `user_attributes` row for the given `user_id`.
- **Idempotent:** returns `204` whether the row existed or not.
- **DB-only in v1.1.** Fan-out to destination delete APIs (MoEngage,
  GA4 User Deletion, Amplitude User Privacy, Adjust Forget Device) is
  **deferred to a follow-up branch**. After this endpoint fulfills a DSR
  request, the user's PII is gone from our DB and from any future outbound
  payload — but data already delivered to destinations remains until their
  own retention or a manual per-destination deletion. Documented gap.

---

## 10. SQL producer contract (`sql/producer_contract.sql`)

```sql
-- Called INSIDE the producing service's business transaction.
-- The event becomes durable iff the business transaction commits.
FUNCTION emit_event(
    p_event_name   text,
    p_properties   jsonb       DEFAULT '{}',
    p_user_id      text        DEFAULT NULL,
    p_anonymous_id uuid        DEFAULT NULL,
    p_session_key  uuid        DEFAULT NULL,
    p_context      jsonb       DEFAULT '{}',
    p_occurred_at  timestamptz DEFAULT now(),
    p_event_id     uuid        DEFAULT gen_random_uuid()
) RETURNS uuid   -- the event_id; NULL when p_event_id was a duplicate (no-op)
```

Behavior (all inside the caller's transaction):

1. Validates `p_event_name` against the `origin='server'` allowlist (the
   `event_registry` table, synced from config at process boot — §13); raises an
   exception on unknown names (fails the caller's transaction **by design**: an
   unknown event name is a deploy-time bug, not a runtime condition). Names
   marked `"reserved": true` (e.g. `ep_attributes_synced`) are also rejected
   with `reserved_event_name` — producers cannot emit them.
2. Dedupe insert (`events_dedupe`); duplicate ⇒ return NULL, no-op.
3. Inserts the outbox row (`origin='server'`, `received_at = now()`).
4. Fans out delivery rows per the routing map.
5. If `p_anonymous_id` is provided: first-visit logic (§8).

The X-Event tier-2 pattern (§8) is a thin wrapper: the platform handler reads the
three headers and calls `emit_event(header_name, …, p_anonymous_id => hdr,
p_session_key => hdr)`.

---

## 11. Storage & delivery semantics (contract-relevant summary)

Full DDL lives in `/server/migrations`; this section fixes the semantics.

- **`events_outbox`** — partitioned `BY RANGE (received_at)`, daily partitions.
- **`events_dedupe(event_id uuid PRIMARY KEY, received_at timestamptz)`** — a
  non-partitioned side table providing the global dedupe guarantee (a partitioned
  table cannot carry a unique index on `event_id` alone). Rows pruned after 30
  days — far beyond the 24 h SDK retry window + 7 day `occurred_at` window.
- **`events_delivery`** — `(event_ref, destination, status, attempts,
  next_attempt_at, last_error, delivered_at)`, `UNIQUE (event_ref, destination)`
  within partition, partitioned identically to the outbox. Fanned out per the
  routing map **at insert**.
- **`identity_registry`** — keyed by `session_key`: `{anonymous_id, user_id?,
  session_number, ga4_client_id, ga4_session_id, firebase_app_instance_id,
  amplitude_device_id, adjust_adid, adjust_platform_ad_id, fbp, fbc,
  click_ids jsonb, context jsonb, client_ip (or resolved geo), updated_at}`.
  Partial upserts per §9.2.
- **`user_attributes`** — person-scoped attribute store (§6.1), keyed by
  `user_id`. Columns: `{attributes jsonb, hash text, moengage_synced_hash
  text, moengage_synced_at timestamptz, created_at, updated_at}`. Partial
  upsert merges `attributes` at the top level (`||` operator). `hash` is
  `sha256(canonical_json(attributes))`; when `hash != moengage_synced_hash`
  and MoEngage attributes are enabled, `/v1/identity` enqueues a
  `moengage_customer` delivery (§6.1). Retention: TBD (deferred to
  follow-up); rows persist indefinitely until DSR deletion via §9.6.
- **Reserved server-origin event `ep_attributes_synced`** — auto-registered
  in `event_registry` at boot with `"reserved": true`; routes to
  `moengage_customer` only. Both `emit_event()` and `/internal/v1/events`
  reject calls with reserved names (error: `reserved_event_name`).
- **Destination `moengage_customer`** — separate delivery target from
  `moengage` (events). Independent circuit breaker and retry state. Handled
  by the MoEngage sender via a `type:"customer"` payload built from
  `user_attributes` (§6.1). On `delivered`, the sender updates the source
  row's `moengage_synced_hash` and `moengage_synced_at`.
- **`first_seen(anonymous_id PRIMARY KEY, first_seen_at)`**.

### Delivery status lifecycle

```
pending ──send ok──────────────> delivered            (terminal)
   │ └────send failed──> failed ──retries──> delivered
   │                        └──── attempt 10 ─> dead   (terminal)
   └──missing required identity/token──────> skipped   (terminal, reason in last_error)
```

- Retry: exponential backoff with jitter — base **30 s**, ×2 per attempt, cap
  **1 h**, max **10 attempts** ⇒ `dead`.
- `skipped` reasons are machine-readable strings, e.g. `no_ga4_identity`,
  `no_adjust_adid`, `no_event_token`, `destination_disabled`, `consent_absent`,
  `no_attributes`, `attributes_disabled`.

### Worker claim protocol (N instances safe)

Lease-based claim, per destination:
`SELECT … WHERE destination = $1 AND status IN ('pending','failed') AND
next_attempt_at <= now() ORDER BY next_attempt_at LIMIT $2 FOR UPDATE SKIP LOCKED`,
then in the same short transaction `UPDATE … SET next_attempt_at = now() +
<lease (5 min)>` and commit — no transaction held across HTTP calls. A crashed
worker's claims self-release when the lease expires. Graceful SIGTERM: stop
claiming, drain in-flight sends, reset `next_attempt_at = now()` on claimed-but-
unsent rows.

Serving index (documented with the DDL):
`(destination, next_attempt_at) WHERE status IN ('pending','failed')` — partial,
per partition.

### Partitions & retention

A worker-hosted timer pre-creates partitions **3 days ahead** and drops expired
ones. Retention: a day's outbox+delivery partitions are dropped once older than
**30 days** (config) — **unless** the delivery partition contains `dead` rows, in
which case the pair is retained up to **90 days** (config) to preserve the failure
evidence, then dropped regardless. `events_dedupe` and terminal-state cleanup ride
the same timer.

`user_attributes` is **not** subject to partition retention — rows persist
indefinitely until DSR deletion (§9.6). Bulk age-out policy for inactive users
is a v1.1 open item, deferred to a follow-up branch.

---

## 12. Destinations

Common sender interface; every implementation consults the destination's **current**
public API docs (via web search) at implementation time — never from memory. Every
outbound call has explicit timeouts. Per-destination circuit breaker (N consecutive
failures ⇒ pause M minutes; config), independent pipelines — one slow destination
never blocks the others.

| Destination | Identity required (from registry via `session_key`) | Absent ⇒ | Notes |
|---|---|---|---|
| GA4 MP | `ga4_client_id` (+ `ga4_session_id`) or `firebase_app_instance_id` | `skipped: no_ga4_identity` — **never fabricate identity** | includes `engagement_time_msec`; builds `device{}` and `user_location{}`/`ip_override` from registry context/IP |
| Amplitude HTTP V2 | `amplitude_device_id` | `skipped` | `insert_id = event_id` (their dedupe); `device_id` from registry; `time` in ms |
| MoEngage Data API (`moengage`) | `user_id` → their customer id | `skipped: no_user_id` | `type:"event"` transport; auth per current docs; receives `first_visit`; attribute-derived fields per §6.1 gated by `EP_MOENGAGE_ATTRIBUTES_ENABLED` |
| MoEngage customer sync (`moengage_customer`) | `user_id` + non-empty `attributes` | `skipped: no_attributes` / `skipped: no_user_id` / `skipped: attributes_disabled` | `type:"customer"` transport; triggered by `ep_attributes_synced` enqueue (§6.1); flag: `EP_MOENGAGE_ATTRIBUTES_ENABLED` (default ON) |
| Adjust S2S | `adjust_adid` (or platform ad id) + config event-token map | `skipped: no_adjust_adid` / `no_event_token` | revenue+currency on purchases; includes IP (AEM requirement); follows their idempotency guidance |
| Meta CAPI (reference subclass) | `fbp`/`fbc`/hashed user_data | `skipped` | built on `PixelPlatformSender`; **disabled by default** |

Each sender additionally pulls user attributes from `user_attributes`
(§6.1) via `user_id` and includes the mapped fields per §6.1's mapping
table, **gated by that destination's `EP_<X>_ATTRIBUTES_ENABLED` flag**
(§13). When the flag is OFF, the sender still delivers the event core;
attribute-derived fields are omitted. Missing rows or missing fields are
silently omitted. Attributes never rescue an event that would otherwise be
`skipped` for missing identity.

**`PixelPlatformSender`** (abstract base, built now, subclassed later for
Meta/Snap/TikTok CAPI): SHA-256 normalization of email/phone, `event_id` dedup
plumbing, consent gating (config flag, **default OFF** — no CAPI traffic until
explicitly enabled), `user_data` assembly from registry (`fbp`/`fbc`/`click_ids`/
IP/UA).

**Microsoft Clarity: OUT OF SCOPE.** Clarity has no server-side ingestion API
(device SDK only). Stated in the README so nobody adds it later.

---

## 13. Configuration surface

Env-var driven (systemd `EnvironmentFile`). Structured config (allowlists, routing,
token maps) lives in a **tracking-plan JSON file referenced by env var** — one
source of truth for names, origins, routing, and translations.

| Variable | Purpose |
|---|---|
| `EP_DB_CONNSTRING` | PostgreSQL connection string |
| `EP_LISTEN` / `EP_INTERNAL_LISTEN` / `EP_METRICS_LISTEN` | bind addresses (api public, api internal, worker metrics) |
| `EP_CLIENT_TOKENS` | `app_id:token` pairs for `/v1/events` + `/v1/identity` |
| `EP_INTERNAL_TOKEN` | bearer secret for `/internal/v1/events` |
| `EP_COOKIE_DOMAIN` | `Domain=` for `ep_aid` |
| `EP_CORS_ORIGINS` | exact-origin allowlist |
| `EP_RATE_LIMIT` | per-token requests/window |
| `EP_TRACKING_PLAN` | path to tracking-plan JSON (below) |
| `EP_RETENTION_DAYS` / `EP_RETENTION_DEAD_DAYS` | 30 / 90 defaults |
| `EP_IP_MODE` | `raw` (default) \| `geo` (resolve at ingestion, store location object) |
| `EP_GA4_*`, `EP_AMPLITUDE_*`, `EP_MOENGAGE_*`, `EP_ADJUST_*`, `EP_META_*` | per-destination: `ENABLED`, credentials, rate limit, breaker thresholds, timeouts |
| `EP_GA4_ATTRIBUTES_ENABLED` | Attribute-derived fields (`user_properties`, `user_data`) in GA4 payloads. Default: OFF |
| `EP_AMPLITUDE_ATTRIBUTES_ENABLED` | Attribute-derived `user_properties` in Amplitude payloads. Default: OFF |
| `EP_MOENGAGE_ATTRIBUTES_ENABLED` | MoEngage `type:"customer"` sync path (§6.1). Default: ON (MoEngage is the designated raw-PII destination) |
| `EP_ADJUST_ATTRIBUTES_ENABLED` | Attribute-derived `s2s_email` / `s2s_phone` / `partner_params` in Adjust payloads. Default: OFF |

Tracking-plan JSON (synced into the `event_registry` table at api/worker boot so
`emit_event` shares the same allowlist + routing):

```jsonc
{
  "attributes": {
    "first_name": { "type": "string", "max_length": 128 },
    "last_name":  { "type": "string", "max_length": 128 },
    "email":      { "type": "email",  "max_length": 254 },
    "phone":      { "type": "e164",   "max_length": 16  },
    "gender":     { "type": "enum",   "values": ["male", "female", "other", "unknown"] },
    "city":       { "type": "string", "max_length": 128 }
  },
  "events": {
    "product_viewed": { "origin": "client", "destinations": ["ga4", "amplitude"] },
    "order_placed":   { "origin": "server", "destinations": ["ga4", "amplitude", "adjust", "moengage"],
                        "meta_name": "Purchase", "adjust_token": "abc123" },
    "ep_attributes_synced": { "origin": "server", "reserved": true, "destinations": ["moengage_customer"] },
    "first_visit":    { "origin": "server", "destinations": ["moengage"] }
  }
}
```

### Observability

Prometheus `/metrics` on both processes (AOT-safe exposition — see PLAN.md):

```
events_ingested_total{origin,endpoint}
deliveries_total{destination,status}
outbox_pending{destination}
circuit_state{destination}
delivery_latency_seconds{destination}
```

Structured JSON logs on every delivery state transition. **Never log payloads
or attribute values (PII):** `event_id` + `event_name` + `destination` +
`status` only. Validation errors may reference attribute *names* (e.g.
`invalid_attribute:phone`) but never *values*. No secrets in code, logs, or tests.

---

## 14. Ground rules (restated, binding)

- The word "analytics" must not appear in package names, binaries, globals,
  cookies, or endpoints (docs/comments may use it descriptively).
- No third-party tracking/measurement libraries in the SDKs. No PII collected
  automatically; `user_id` via `setUser()` only. **User attributes (§6.1) are
  the sole exception**: explicitly user-provided via `setUserAttributes()`,
  stored in `user_attributes`, never inferred or auto-collected.
- .NET 10 Native AOT compliance is non-negotiable: source-generated
  `System.Text.Json` contexts for ALL serialization, zero AOT/trim warnings on
  publish. A non-AOT-safe library ⇒ pick another or hand-roll, and say so.
- Web SDK: zero runtime dependencies, ≤ 8 KB gzipped (number reported).
- Any deviation from this spec: stop and ask.
