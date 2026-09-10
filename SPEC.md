# Event Pump — SPEC

**Status: APPROVED v1.2 (2026-07-27). Behavior changes require spec re-approval.**

**v1.2 changes:** Event Pump is now **multi-tenant by client token**. Each
tenant (application) has its own tracking plan, credentials, and cookie/CORS
boundary; all tenants share one PostgreSQL, one API process, and one worker
process. `app_id` is resolved from the bearer token on every request and
flows through validation, storage, and delivery. Extends §0 (overview), §1
(canonical event model gains `app_id`), §8 (three producer paths carry
`app_id`), §9 (§9.1/§9.2/§9.3/§9.6 note the token→`app_id` resolution;
§9.6 URL becomes `/internal/v1/user_attributes/{app_id}/{user_id}`), §10
(`emit_event(p_app_id, ...)` — new required first parameter), §11 (every
domain table gains `app_id`, primary keys are composite), §13 (config split
into global env + per-tenant JSON files in `EP_TENANTS_DIR`), §13
observability (metrics labels gain `app_id`).

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

One Event Pump deployment (one API process, one worker process, one PostgreSQL)
serves **N tenants**. Each tenant is an application (Zainmart, App-B, App-C, …).
Every request carries a bearer token which resolves to exactly one `app_id`
(§13). All domain rows (§11) carry `app_id`; all destination credentials and
tracking plans are per-tenant (§13). Tenants share nothing at runtime beyond
process resources and one Postgres.

```
 [ zainmart web SDK ]───┐
 [ zainmart flutter ]───┤ POST /v1/events, /v1/identity
                        │ Bearer <token> ─┐
 [   app-B web SDK ]────┤                 │
 [   app-B flutter ]────┤                 ▼
                        │       ┌──────────────────┐    ┌──────────────┐    ┌─> GA4 MP (per tenant)
                        └──────▶│    eventpump     │────│ PostgreSQL   │    ├─> Amplitude
                                │       api        │    │ outbox +     │    ├─> MoEngage
                                │  token→app_id    │───▶│ delivery +   │    ├─> Adjust S2S
                                │                  │    │ identity +   │    └─> Meta CAPI (base, off)
                                └──────────────────┘    │ user_attrs   │             ▲
                                                        │ (all keyed   │             │
 [ backends: emit_event(p_app_id, ...) ]───────────────▶│  by app_id)  │◀── claim ───┤
 [ backends: POST /internal/v1/events   ]───────────────│              │      per (app_id, dest)
                                                        └──────────────┘   ┌──────────────┐
                                                                           │  eventpump   │
                                                                           │    worker    │
                                                                           └──────────────┘
```

Terminology: "destination" = a downstream S2S API; "producer" = anything that
creates events; "handle" = a destination-specific identity value (§6); "tenant"
/ "app" = one configured application, identified by `app_id`.

---

## 1. Canonical event model

| Field          | Type                  | Set by            | Notes                                                        |
|----------------|-----------------------|-------------------|--------------------------------------------------------------|
| `app_id`       | text                  | **server**        | Resolved from bearer token (HTTP) or supplied by `emit_event` (SQL). Never producer-supplied over the wire. Scopes every subsequent id (`event_id`, `session_key`, `user_id`, `anonymous_id`) to one tenant. |
| `event_id`     | uuid v4               | producer          | Dedupe key. At-least-once delivery from SDKs; server dedupes per `(app_id, event_id)`. |
| `event_name`   | text, snake_case      | producer          | Must be in the tenant's per-origin allowlist (tracking plan, §13). |
| `origin`       | `client` \| `server`  | **server**        | Stamped by ingestion path. Never producer-supplied.          |
| `occurred_at`  | timestamptz (ISO 8601 with offset) | producer | Stamped at `track()` time, not at flush time.       |
| `received_at`  | timestamptz           | **server**        | Ingestion time. Partition key.                               |
| `user_id`      | text, nullable        | producer          | Only ever from `setUser()` / server knowledge. Never inferred. Unique within `app_id` only. |
| `anonymous_id` | uuid, nullable        | producer          | Required on client-origin events; optional on server-origin. Unique within `app_id` only. |
| `session_key`  | uuid v7, nullable     | producer          | Joins to `identity_registry` per `(app_id, session_key)` for enrichment. Absent (backend producers) ⇒ identity resolves by `(app_id, user_id)` instead — §12. |
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
  `EP_MOENGAGE_ATTRIBUTES_ENABLED=true` AND the merged state is non-empty AND
  `user_attributes.hash != user_attributes.moengage_synced_hash`: the API
  handler inserts a synthetic outbox row (`event_name =
  'ep_attributes_synced'`, `user_id = <the user>`), fanning out one
  delivery row to `moengage_customer`. An empty merged state (every key
  cleared, or a first upsert carrying only `null`s) enqueues nothing — the
  sender could only resolve such a row to `skipped: no_attributes`.
- The `moengage_customer` sender is registered whenever MoEngage is enabled,
  independent of `EP_MOENGAGE_ATTRIBUTES_ENABLED`. The flag gates *delivery*
  (`skipped: attributes_disabled`), not registration: the worker runs one
  pipeline per registered sender, so gating registration would strand rows
  enqueued before the flag was turned off in `pending` forever.
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
init(config: { endpoint: string; tenantApiKey: string; app_version?: string;
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
EventPump.init(EventPumpConfig(endpoint, tenantApiKey, ...));
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

Each event name is assigned to exactly **one** path in the tenant's tracking
plan, enforced by per-origin allowlists. Every path resolves `app_id` before
the event enters the pipeline:

| Path | Producer | Origin | Mechanism | How `app_id` is set |
|------|----------|--------|-----------|---------------------|
| a | Platform services sharing the database | `server` | `emit_event(p_app_id, ...)` called **inside their business transaction** — the PRIMARY server-fact path. Ships as `sql/producer_contract.sql` (§10). | Caller supplies `p_app_id`. A platform service typically belongs to exactly one tenant and hard-codes its own `app_id`. |
| b | Backend producers **outside** this database | `server` | `POST /internal/v1/events` — authenticated by the tenant's `internal_token`, a real secret kept out of any client bundle. | Server resolves bearer via each tenant's `internal_token` (§13). |
| c | Client SDKs | `client` | `POST /v1/events` — `origin='client'` names only. | Server resolves bearer via each tenant's `tenant_api_key` (§13) — the client-side key that ships in mobile/web bundles. SDKs never send `app_id` explicitly. |

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
`INSERT ... ON CONFLICT DO NOTHING` into
`first_seen(app_id, anonymous_id, first_seen_at)`. A **successful insert**
emits the canonical server event `first_visit` — once ever per
`(app_id, anonymous_id)` by construction. Default routing: **MoEngage +
internal only** ("internal" = the outbox row itself; no delivery rows).
**Never** forward `first_visit` to GA4/Amplitude — they derive their own
new-user status and forwarding double-counts. `first_visit` is per-tenant:
Zainmart's `first_visit` never reaches App-B's destinations, and the same
`anonymous_id` in two tenants yields two separate `first_visit` events.

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

- Auth: `Authorization: Bearer <tenant_api_key>` — the **client-side** per-tenant
  key. Looked up across all tenants' `tenant_api_key` (§13); the match resolves
  the request's `app_id`. `401 unauthorized` if not found. Distributed inside
  the app bundle — treat as a build-time secret and rotate on suspicion of
  leak. A leaked `tenant_api_key` cannot authenticate on `/internal/v1/*`
  (that listener has its own `internal_token`).
- Rate limit per caller, bucketed per tenant — the partition key is
  `(app_id, caller address)` (config: requests/window; tenants may override via
  the tenant file). `429` + `Retry-After` on breach. NOT per key alone: the
  `tenant_api_key` is identical for every visitor of a tenant, so keying on it
  would give the whole tenant a single bucket. The caller address comes from
  `X-Real-IP` when the peer is listed in `EP_TRUSTED_PROXIES`, else from the
  socket peer. The limiter is per process — N api instances mean N buckets.
- Body: `{"events": [<event>, …]}`, max 100. Event shape: `event_id`, `event_name`,
  `occurred_at`, `anonymous_id`, `session_key?`, `user_id?`, `properties?`,
  `context?` — nothing else (§1). **`app_id` is never in the body**; it is set
  server-side from the key.
- Server stamps `app_id` (from key), `origin='client'`, `received_at`, and
  `context.ip` from `X-Real-IP`.
- Response `200`:
  `{"accepted": <n>, "rejected": [{"index": i, "event_id": "…", "reason": "…"}]}`.
- **`200` even when every event was rejected**, and this is deliberate. A batch
  is validated per event, so the outcome is per event: the `rejected` array is
  the report, not the status line. A `4xx` for a wholly refused batch would be
  read by both shipped SDKs as a transient failure — they ack only on `2xx`
  (§7) — so the batch would never leave the queue and would be re-uploaded on
  max backoff until the 24h give-up, delaying every valid event behind it. A
  producer that checks only the status code learns nothing from `200` here;
  it has to read `accepted` and `rejected`, which is what they are for.
  Operators do not have to: every rejection increments
  `events_rejected_total{app_id,origin,endpoint,reason}` and writes a warning
  naming the index, event id, event name and reason.
- A batch refused *before* it parses into events — `malformed_json`,
  `missing_events_array`, `batch_too_large` — is a `400`, since nothing about
  it was per event, and counts as one `events_rejected_total` under that
  reason.

### 9.2 `POST /v1/identity` — identity registration / partial upsert

- Auth + rate limit: same as 9.1.
- Body: `{"session_key", "anonymous_id", "session_number", "user_id"?,
  "first_seen_at"?, "handles"?: {…§6…}, "attributes"?: {…§6.1…},
  "context"?: {…§5 full…}}`.
- `first_seen_at` is **accepted and ignored**. The SDKs send it on every call,
  but a client's claim about when it first ran is unverifiable, and it would
  compete with the value gating the once-ever `first_visit` event — so
  `first_seen` records the server's own observation instead (§8) and this is
  never read. It stays on the accepted list because the envelope is strict:
  removing the name would `400` every request from every SDK build already
  deployed.
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
  interface). Auth: `Authorization: Bearer <internal_token>` — the **server-side**
  per-tenant secret, distinct from `tenant_api_key` and never shipped in any
  client bundle. Looked up via each tenant's `internal_token` (§13); the match
  resolves `app_id`. `401` if not found — a `tenant_api_key` used here does
  NOT resolve, by design.
- Same envelope as 9.1; `origin='server'` allowlist; `anonymous_id` optional,
  `user_id` allowed; no cookie handling, no `X-Real-IP` capture (config-optional).
- Server stamps `app_id` from the key, exactly like 9.1.

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
- CORS: exact-origin allowlist echoed in `Access-Control-Allow-Origin`, with
  `Access-Control-Allow-Credentials: true`. Both the `Domain=` and the CORS
  allowlist are **per-tenant** (from that tenant's `cookie_domain` and
  `cors_origins` — §13). Each tenant must be served from its own subdomain of
  its own registrable domain (e.g. `collect.zainmart.com` for
  `www.zainmart.com`, `collect.appb.com` for `www.appb.com`) so the `SameSite=Lax`
  `ep_aid` cookie flows on that tenant's SDK requests only. Nginx routes to the
  shared Event Pump backend regardless of the subdomain; tenant isolation is
  by token (§9.1), not by hostname.

### 9.6 `DELETE /internal/v1/user_attributes/{app_id}/{user_id}` — DSR deletion

- Internal listener, same as 9.3. Auth: `Authorization: Bearer <internal token>`.
  The token must belong to the tenant identified by `{app_id}` — an internal
  token for tenant A cannot delete tenant B's data; mismatch returns `401`.
- Deletes the `user_attributes` row for the given `(app_id, user_id)` pair.
- **Idempotent:** returns `204` whether the row existed or not.
- **DB-only.** This endpoint never contacts a destination. Use §9.7 when the
  data already delivered downstream has to go too.

### 9.7 `POST /internal/v1/erasure/{app_id}/{user_id}` — DSR erasure with destination fan-out

- Internal listener and auth exactly as §9.6: the internal token must belong to
  the tenant named by `{app_id}`; a mismatch returns `401`. Not routed on the
  public listener — a leaked client key can never erase anyone.
- Two variants:
  - `POST /internal/v1/erasure/{app_id}/{user_id}` — erase the person. Deletes
    the local `user_attributes` row and queues a delete on every erasure
    destination enabled for the tenant.
  - `POST /internal/v1/erasure/{app_id}/{user_id}/attributes` — erase the
    person's information only, keeping their record and event history. Queues
    only `moengage_erasure`: MoEngage's customer API is the sole destination
    that can clear attributes without deleting the whole subject. Adjust
    forgets a device, Amplitude deletes a user, GA4 deletes an id — all three
    would erase far more than asked, so they are excluded rather than
    approximated.
- **Optional narrowing.** `?destinations=a,b` restricts the fan-out. Omitting
  it means *every* enabled destination, which is the correct behaviour for a
  real request; naming destinations is for re-driving one that failed. A
  destination the tenant does not erase to is rejected `400`, never ignored —
  answering `202` would report an erasure that was queued nowhere. Present but
  naming nothing (`?destinations=`, or a string of only separators) is the same
  mistake and is rejected `400 no_destinations`: asking for every destination
  is spelled by leaving the parameter off.
- **In-flight deliveries are cancelled first.** A `pending` or `failed` row for
  that person, landing after the downstream delete, re-creates exactly what was
  erased: the destination builds a profile from an event whose id it does not
  know. Both variants flip those rows to `skipped: erased` before enqueueing
  and report the count in `cancelled_deliveries`. The window is bounded by
  `EP_RETENTION_DAYS`, since a row held `failed` behind circuit-breaker backoff
  outlives any shorter bound. Events ingested *after* the erasure are outside
  this boundary — the producer has to stop emitting for that person.
  A row the worker has already **claimed** is cancelled too: the claim leases
  by moving `next_attempt_at`, leaving the status `pending`, so a delivery
  waiting in a worker's send queue is inside the cancellation and counted in
  it. The worker re-reads each row's status immediately before sending for
  that reason — without it the send still went out, up to a full
  `EP_WORKER_LEASE_S` after the erasure committed, and the terminal-status
  guard then discarded the result, so the row read `skipped: erased` with the
  POST already delivered. It narrows that window rather than closing it: the
  cancellation is one statement in a transaction that commits several
  statements later, so a worker that re-reads before that commit sees
  `pending` and sends. The exposure is the milliseconds between the two, not
  the lease, and a delivery sent inside it is still counted in
  `cancelled_deliveries` — `202` has never meant the data is gone.
- **Their pre-login events count too.** Events from before the person signed in
  carry no `user_id`, but they ship the same device's advertising id and client
  id, so one delivered after the erasure rebuilds the very profile that was
  deleted. They are reached through `identity_registry`: the `anonymous_id`s
  that person's sessions were recorded under, matched against outbox rows that
  *no* `user_id` ever claimed. `ep_aid` is per browser rather than per person,
  so an `anonymous_id` some **other** account has also signed in under is
  dropped from that set entirely: its unclaimed rows are as likely to be the
  other account's pre-login sessions as this person's, and erasing them would
  delete a second person's data on the first person's request. The residual
  gap this cannot close: a co-user of the same browser who never signed in
  leaves rows indistinguishable from the requester's own pre-login ones, and
  those are erased together.
- **The identity sent downstream is the handle that destination knows.**
  `ResolveErasureHandlesAsync` reads every vendor handle from
  `identity_registry` for `(app_id, user_id)` and stamps them on the outbox
  row's context. Deleting under our own `user_id` would report success while
  leaving the real profile intact. Migration `0011_erasure_lookup.sql` added
  the index that lookup needs; `0013_identity_person_lookup.sql` widens it to
  serve the person lookup of §12 as well and drops the narrower one, since the
  two had the same partial predicate and the same leading columns. Two shapes,
  because the vendors differ:
  - **Person-scoped** ids — MoEngage customer, Amplitude user, GA4 client and
    user — are single: the vendor holds one profile. Most recent non-null wins
    per column, since handles are recorded per session and one can sit on a
    different row than the newest activity.
  - **Device-scoped** ids are lists. Adjust's forget-device API erases one
    device and Amplitude keeps events per device, so someone who used two
    phones has two of each; sending only the newest leaves the older phone
    tracked while the delivery reads `delivered` and the audit trail reports
    the destination complete. Each Adjust entry carries the `os` recorded on
    **the same row** as its ad id — `os` names nobody, it is only what says
    whether that id is an IDFA or a GAID, so taking it from whichever row was
    newest sends an Android GAID under `idfa` for someone whose last session
    was on iOS.
  The context is written as `adjust_devices` / `amplitude_device_ids`; the
  single-device keys written before the fan-out (`adjust_adid`,
  `adjust_platform_ad_id`, `os`, `amplitude_device_id`) are still read, so an
  erasure queued or audited by an older build does not lose its handles on
  upgrade.
- **Delivery rides the outbox, not the request handler.** Each variant enqueues
  a reserved server event — `ep_erasure_requested` or
  `ep_attributes_erasure_requested` — with one delivery row per destination, so
  it inherits the worker's retry, backoff, circuit breaker and delivery-status
  visibility. An erasure a destination rejects becomes a `dead` row rather than
  being silently lost. Both event names are reserved: a producer cannot forge
  one over HTTP or SQL.
- **Idempotent per destination.** A repeat call while an erasure is still
  `pending`/`failed` for a destination does not queue a second one for it, but
  *does* re-drive any destination that has no erasure in flight — so a `dead`
  Adjust erasure can be retried without duplicating a live MoEngage one.
- **One transaction, and not cancellable by the caller.** The cancellation,
  the enqueue, the audit row, the `user_attributes` delete and the
  `identity_registry` delete commit together. Split across five statements they
  could half-apply: a client disconnecting mid-request would leave deletes
  queued and deliveries cancelled while the local PII and the audit row that
  proves the request was handled never landed, and an audit row committed
  *after* its outbox row lets the worker record an outcome against a row that
  does not exist yet — that `UPDATE` matches nothing and the outcome is lost.
  For the same reason the writes do not run under the request's cancellation
  token: once an erasure has begun it is finished.
- Returns `202` with `{status, destinations, cancelled_deliveries}`.
  `destinations` lists what was queued, and is empty when the tenant erases
  nowhere or an erasure was already in flight everywhere. `202` means the
  erasure was **accepted**, not that the data is gone — destinations complete
  deletion asynchronously (MoEngage: up to 7 days, up to 60 to clear logs and
  backups).

- **Person erasure also drops `identity_registry`.** That table holds the
  advertising ids, device ids, IP and location we recorded per session — all
  personal data — and nothing ages it out: it is unpartitioned and
  `PartitionMaintenance` never touches it. Leaving it would make the erasure
  incomplete. It is deleted last, after the handles are stamped on the queued
  outbox rows and recorded on the audit row, so a re-drive can still name the
  person downstream; when the live lookup then returns nothing, the resolver
  falls back to the handles on the most recent audit row. The person's
  **pre-login sessions go with it**, found the same way the cancellation finds
  them — rows sharing an `anonymous_id` with one of their sessions and claimed
  by no `user_id`. Those rows hold the same device's ADID, device id, IP and
  location; filtering on `user_id` alone would leave them in an unpartitioned,
  never-aged table forever. The **attributes variant does not** delete any of
  it — that variant preserves the record by definition.
- **Events already ingested are not purged.** `events_outbox` rows for an
  erased person keep their `user_id` and properties until `EP_RETENTION_DAYS`
  drops the partition (30 days by default). This is deliberate: the window is
  bounded, retention already guarantees the deletion, and rewriting historical
  partitions on every DSR request would be a large cost for a 30-day
  improvement. Destinations receive nothing further for that person, because
  every in-flight delivery was cancelled.

#### 9.7.1 Audit trail

- Every request writes a row to `erasure_audit` (migration
  `0012_erasure_audit.sql`) — including requests that queue nothing, since a
  tenant erasing nowhere is still a request that must be shown to have been
  handled.
- The row records who was erased, under which handles, which destinations were
  queued, how many deliveries were cancelled, and — written back by the worker
  as each delivery reaches a terminal state — what every destination did with
  it. `failed` is excluded: it retries, so it is not an outcome yet.
- **Deliberately unpartitioned and exempt from `PartitionMaintenance`.**
  `events_outbox`/`events_delivery` are dropped at `EP_RETENTION_DAYS` (30) or
  `EP_RETENTION_DEAD_DAYS` (90); a complaint about an ignored erasure can arrive
  long after. GDPR's accountability principle requires demonstrating
  compliance, so the proof must outlive the machinery that produced it.

- `GET /internal/v1/erasure/{app_id}/{user_id}/audit` returns that history
  (newest first, max 100) so accountability does not depend on database access.
  Same tenant scoping as the erasure endpoints: a mismatched `app_id` is `401`.

#### 9.7.2 Destination support

| Destination | Erasure API | Status |
|---|---|---|
| `moengage_erasure` | `POST {endpoint}/v1/customer/delete?app_id=…` | Full. Also serves the attributes variant via the customer-update API. |
| `adjust_erasure` | `POST gdpr.adjust.com/gdpr_forget_device` | Full. One call per recorded device; without any, `skipped: no_adjust_device` rather than a false success. |
| `amplitude_erasure` | `POST /api/2/deletions/users` | Needs `secret_key` in addition to the event API key. Absent ⇒ `skipped: no_secret_key`. Every recorded device goes in one request's `device_ids`. |
| `ga4_erasure` | Google Analytics User Deletion API | **Not implemented.** OAuth-authenticated, unlike the `api_secret` the event sender uses. Records `skipped: ga4_oauth_not_configured` so the gap is visible per request rather than silent. |

- **Adjust: which parameter carries the device is not interchangeable.** Same
  resolution order as the event sender — `adid` is Adjust's own device id,
  while `adjust_platform_ad_id` is the raw platform advertising id and Adjust
  recognises it only as `idfa` (iOS) or `gps_adid` (Android), decided by the
  recorded `os`. Sending a GAID as `adid` matches no device, and Adjust answers
  `200` either way, so the delivery would read `delivered` with nothing
  forgotten. An `os` we cannot classify is `skipped: no_adjust_device` rather
  than a guess. The call carries `s2s=1`, as the event endpoint does; without
  it Adjust can reject the request, and any non-429/non-5xx is recorded `dead`
  — one attempt and the erasure would be abandoned for good.
- **Adjust: the erasure is scoped by `adjust.environment` too.** A `sandbox`
  tenant's forget carries `environment=sandbox`, the same value its event
  sends carry. Left off, the call lands on the production scope, where Adjust
  answers `200` for a device it has never seen — `delivered`, nothing
  forgotten — and where a UAT handset's *real* install is what would be erased
  instead. Tenants that leave the field unset send the URL they always sent.
- **Adjust erases one device per call.** Every device the person was recorded
  on gets its own request, and the delivery is `delivered` only when all of
  them succeeded. A transient failure on any device ends the pass and retries
  the whole delivery — `gdpr_forget_device` is idempotent, so the devices
  already forgotten cost nothing on the second pass, and walking the rest
  would spend one sender timeout apiece proving a destination-wide fault. A
  partial result names the shortfall (`http_400 (1/3 forgotten)`) rather than
  reading like a single failed call, and the denominator counts every device
  held, including ones whose `os` could not be classified: those make the
  delivery `dead`, never `delivered`, so a device we cannot address is a
  visible gap rather than a silent one. The fan-out also stops while the
  worker's lease still holds, so a person with many devices cannot run past
  `EP_WORKER_LEASE_S` and have a second worker re-claim the row mid-pass.
- **Amplitude's device ids are chunked**, at most 100 per request. They are
  browser `anonymous_id`s, so a person who clears cookies accumulates one per
  device; sent as a single array, a large enough set is a payload Amplitude
  answers `4xx` to, and a `4xx` is recorded `dead` on the first attempt. The
  person delete rides the first chunk only.
- **Amplitude sends `ignore_invalid_id: false`.** True makes Amplitude answer
  `2xx` for ids it holds nothing under, which we would record `delivered` — a
  DSR reported complete against a profile never touched. That is the likely
  case rather than the exotic one, because the sender falls back to our own
  `user_id`, which is not guaranteed to satisfy Amplitude's default 5-character
  minimum (events go out under `min_id_length: 1`; the deletion API has no
  equivalent). False turns it into a `4xx`, recorded `dead` — visible in the
  audit trail and re-drivable.
- Meta is absent by design: it exposes no per-user deletion API we can call, so
  there is nothing to gate or record.
- Per-destination `erasure_enabled` (tenant file) / `EP_*_ERASURE_ENABLED` (env)
  default **ON** — erasure is a legal obligation, so opting a destination out is
  deliberate. The flag gates *delivery*, not registration: rows enqueued before
  it flipped still reach a terminal state.
- Like every `EP_*` boolean, these accept `true`/`false`, `1`/`0`, `yes`/`no`
  and `on`/`off`, case-insensitively; anything else stops the boot rather than
  resolving to the default (§13.1). A default-ON flag that read `False` or `0`
  as "not the string `false`, therefore on" would leave a destination erasing
  after an operator switched it off, and say nothing about it.
- **Upgrading onto that parser is not a no-op.** The reads it replaced were
  case-sensitive, so a value the old build and this one disagree about changes
  meaning on the restart, in both directions and with nothing else to announce
  it: `EP_GA4_ENABLED=True` was off and is now on — a destination dark since
  install starts sending live traffic — and `EP_MOENGAGE_ATTRIBUTES_ENABLED=0`
  was on and is now off. Boot writes one line to stderr per such variable,
  naming the old reading and the new one, so the flip is visible to an
  operator who changed nothing. Re-spell the value either way to silence it.

---

## 10. SQL producer contract (`sql/producer_contract.sql`)

```sql
-- Called INSIDE the producing service's business transaction.
-- The event becomes durable iff the business transaction commits.
FUNCTION emit_event(
    p_app_id       text,        -- REQUIRED: which tenant this event belongs to
    p_event_name   text,
    p_properties   jsonb       DEFAULT '{}',
    p_user_id      text        DEFAULT NULL,
    p_anonymous_id uuid        DEFAULT NULL,
    p_session_key  uuid        DEFAULT NULL,
    p_context      jsonb       DEFAULT '{}',
    p_occurred_at  timestamptz DEFAULT now(),
    p_event_id     uuid        DEFAULT gen_random_uuid()
) RETURNS uuid   -- the event_id; NULL when (p_app_id, p_event_id) was a duplicate (no-op)
```

Behavior (all inside the caller's transaction):

1. Validates `(p_app_id, p_event_name)` against the `origin='server'` allowlist
   (the `event_registry` table, synced from each tenant's tracking plan at
   process boot — §13); raises an exception on unknown names (fails the
   caller's transaction **by design**: an unknown event name is a deploy-time
   bug, not a runtime condition). Names marked `"reserved": true` (e.g.
   `ep_attributes_synced`) are also rejected with `reserved_event_name` —
   producers cannot emit them.
2. Dedupe insert (`events_dedupe`) keyed on `(p_app_id, p_event_id)`;
   duplicate ⇒ return NULL, no-op. An identical `p_event_id` in a different
   tenant is a distinct row, not a duplicate.
3. Inserts the outbox row (`app_id = p_app_id`, `origin='server'`,
   `received_at = now()`).
4. Fans out delivery rows per that tenant's routing map, each row carrying
   `app_id = p_app_id`.
5. If `p_anonymous_id` is provided: first-visit logic (§8), keyed on
   `(p_app_id, p_anonymous_id)`.

The X-Event tier-2 pattern (§8) is a thin wrapper: the platform handler
reads the three headers plus its own hard-coded tenant `app_id` and calls
`emit_event(app_id, header_name, …, p_anonymous_id => hdr,
p_session_key => hdr)`.

Platform services typically belong to exactly one tenant and hard-code
their own `app_id` in every `emit_event` call. Where a service is shared
across tenants (rare), it must resolve which tenant's business tx it is
inside and pass that `app_id`.

---

## 11. Storage & delivery semantics (contract-relevant summary)

Full DDL lives in `/server/migrations`; this section fixes the semantics.
**Every domain table carries `app_id text NOT NULL`**; primary keys are
composite `(app_id, …)`. All the same-shape SQL, all the same partitioning —
just scoped per tenant.

- **`events_outbox`** — partitioned `BY RANGE (received_at)`, daily
  partitions. Each row carries `app_id`; delivery fanout and every worker
  claim filter on it.
- **`events_dedupe(app_id text, event_id uuid, received_at timestamptz,
  PRIMARY KEY (app_id, event_id))`** — non-partitioned side table providing
  the per-tenant dedupe guarantee (a partitioned table cannot carry a unique
  index on `event_id` alone). An identical `event_id` in two tenants is two
  distinct rows. Rows pruned after 30 days.
- **`events_delivery`** — `(app_id, event_ref, destination, status, attempts,
  next_attempt_at, last_error, delivered_at, received_at)`, PRIMARY KEY
  `(app_id, received_at, event_ref, destination)` within partition. Fanned
  out per the tenant's routing map at insert.
- **`identity_registry`** — PRIMARY KEY `(app_id, session_key)`. Same
  handles/context/ip columns as before; a `session_key` in tenant A is
  distinct from the same UUID in tenant B.
- **`user_attributes`** — person-scoped attribute store (§6.1), PRIMARY KEY
  `(app_id, user_id)`. Same `{attributes jsonb, hash text,
  moengage_synced_hash text, moengage_synced_at timestamptz, created_at,
  updated_at}` columns. Partial upsert semantics unchanged; the same
  `user_id` in two tenants is two distinct rows.
- **`event_registry`** — PRIMARY KEY `(app_id, event_name)`. Synced from each
  tenant's tracking plan at boot; a name in one tenant may not exist in
  another. The reserved `ep_attributes_synced` is auto-registered per tenant.
- **`first_seen(app_id text, anonymous_id uuid, first_seen_at timestamptz,
  PRIMARY KEY (app_id, anonymous_id))`** — first-visit gate per tenant.
- **Reserved server-origin event `ep_attributes_synced`** — auto-registered
  in each tenant's `event_registry` at boot with `"reserved": true`; routes
  to `moengage_customer` only. Both `emit_event()` and `/internal/v1/events`
  reject calls with reserved names (error: `reserved_event_name`).
- **Destination `moengage_customer`** — separate delivery target from
  `moengage` (events). Independent circuit breaker and retry state
  **per-tenant** (§11 worker claim). Handled by the MoEngage sender via a
  `type:"customer"` payload built from that tenant's `user_attributes`
  (§6.1). On `delivered`, the sender updates the source row's
  `moengage_synced_hash` and `moengage_synced_at`.

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
  `no_adjust_adid`, `stale_adjust_adid`, `no_event_token`, `destination_disabled`, `consent_absent`,
  `no_attributes`, `attributes_disabled`.

### Worker claim protocol (N instances safe, per-tenant pipelines)

Lease-based claim, keyed per `(app_id, destination)`:
`SELECT … WHERE app_id = $1 AND destination = $2 AND status IN ('pending','failed') AND
next_attempt_at <= now() ORDER BY next_attempt_at LIMIT $3 FOR UPDATE SKIP LOCKED`,
then in the same short transaction `UPDATE … SET next_attempt_at = now() +
<lease (5 min)>` and commit — no transaction held across HTTP calls.

The worker runs **one pipeline per `(app_id, destination)` pair** — a slow
Zainmart Adjust does not block App-B's Adjust. Circuit breakers are keyed
per pair too; each tenant × destination has its own retry state and its own
outage window. N worker instances remain safe (the lease + `FOR UPDATE SKIP
LOCKED` is unchanged). A crashed worker's claims self-release when the
lease expires. Graceful SIGTERM: stop claiming, drain in-flight sends,
reset `next_attempt_at = now()` on claimed-but-unsent rows.

Serving index (documented with the DDL):
`(app_id, destination, next_attempt_at) WHERE status IN ('pending','failed')`
— partial, per partition.

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

**Identity resolution.** The worker resolves each event's `identity_registry`
row at claim time, and how it does so depends on what the producer could
supply:

- **`session_key` present** (client SDKs, §9.1) — join on `(app_id,
  session_key)`. A miss is a race, not an absence: `/v1/events` and
  `/v1/identity` are separate requests, so the delivery retries for
  `EP_IDENTITY_GRACE_S` and then settles as `skipped`. It never falls back to
  another row — the session names a specific device, and substituting a
  different one would misattribute the event permanently.
- **`session_key` absent** (backend producers, §9.3 / `emit_event`) — resolve
  the **person** instead: the most recently updated row for `(app_id,
  user_id)` (`EP_IDENTITY_USER_FALLBACK`, default ON). A backend is not
  inside a client session and cannot invent one, so without this every
  identity-gated destination would skip every server-origin event.

  The lookup applies **no age limit**. How stale a handle may be is a
  per-destination question: an `amplitude_device_id` or `ga4_client_id` ages
  harmlessly — the event carries `user_id` too, so the person stays correct —
  while an `adjust_adid` names an *install* and carries that install's
  attribution. Bounding the lookup would deny GA4 and Amplitude a perfectly
  good row in order to protect Adjust, so instead the row travels with its
  `updated_at` and `AdjustSender` applies `adjust.max_identity_age_days`
  (`EP_ADJUST_MAX_IDENTITY_AGE_DAYS`, default 30, `0` = no limit) itself,
  skipping as `stale_adjust_adid`. Session-resolved rows are never aged out —
  they describe the session the event happened in. The age is judged only once
  there is an ADID for it to be about: a row carrying no Adjust handle at all
  is `no_adjust_adid`, the reason that names the actual gap, not
  `stale_adjust_adid`.

  This is a lookup of data already recorded, not a new handle: `setUser(id)`
  reruns S3 on the **same** `session_key` (§3), so the row holding that
  device's `amplitude_device_id` / `ga4_client_id` / `adjust_adid` ends up
  carrying `user_id` too. Rows for never-logged-in sessions have `user_id`
  NULL and are unreachable by this path, which is correct — a backend event
  always names a known person.

  A person-resolved row contributes **handles only**. Its session-scoped and
  device-context fields — `session_number`, `ga4_session_id`, `context`
  (`os`, `os_version`, `model`, `language`, `app_version`) and `client_ip` —
  are dropped, because the event did not happen in that session, on that
  device, at that IP. Carrying them would file the event into a client session
  it never touched and report a backend event as an Android/iOS/browser one,
  distorting GA4's `device{}`/`ip_override` and Amplitude's
  `os_name`/`device_model`/`app_version`/`ip`. The event keeps its own
  `context` (typically `{"platform":"backend"}`).

  One exception, and it is not a device fact about the *event*: the row's `os`
  travels beside the handles rather than inside the dropped context, because
  it is the only thing that says whether `adjust_platform_ad_id` is an IDFA or
  a GAID. Dropped with the rest, a person whose row holds a platform ad id and
  no `adjust_adid` is skipped `no_adjust_adid` with a usable handle in hand.
  It is read by that one branch of `AdjustSender` and reaches no payload.

| Destination | Identity required (from registry, resolved as above) | Absent ⇒ | Notes |
|---|---|---|---|
| GA4 MP | `ga4_client_id` (+ `ga4_session_id`) or `firebase_app_instance_id` | `skipped: no_ga4_identity` — **never fabricate identity** | includes `engagement_time_msec`; builds `device{}` and `user_location{}`/`ip_override` from registry context/IP |
| Amplitude HTTP V2 | `amplitude_device_id`, **or** `user_id` on a server-origin event with no `session_key` | `skipped: no_amplitude_device_id` | `insert_id = event_id` (their dedupe); `time` in ms. Amplitude requires user_id **or** device_id and derives the device id from a hash of `user_id` when it is absent (docs verified 2026-09), so a backend event with no device to look up still delivers — see below |
| MoEngage Data API (`moengage`) | `user_id` → their customer id | `skipped: no_user_id` | `type:"event"` transport; auth per current docs; receives `first_visit`; attribute-derived fields per §6.1 gated by `EP_MOENGAGE_ATTRIBUTES_ENABLED` |
| MoEngage customer sync (`moengage_customer`) | `user_id` + non-empty `attributes` | `skipped: no_attributes` / `skipped: no_user_id` / `skipped: attributes_disabled` | `type:"customer"` transport; triggered by `ep_attributes_synced` enqueue (§6.1); flag: `EP_MOENGAGE_ATTRIBUTES_ENABLED` (default ON) |
| Adjust S2S | `adjust_adid` (or platform ad id) + config event-token map | `skipped: no_adjust_adid` / `no_event_token` / `stale_adjust_adid` | revenue+currency on purchases; includes IP (AEM requirement); follows their idempotency guidance |
| Meta CAPI (reference subclass) | `fbp`/`fbc`/hashed user_data | `skipped` | built on `PixelPlatformSender`; **disabled by default**; attribute-derived hashed `em`/`ph` gated by `EP_META_ATTRIBUTES_ENABLED` |

**Amplitude's user_id-only path.** Amplitude HTTP V2 requires `user_id` **or**
`device_id` and returns 400 only when both are absent; with no `device_id` it
sets one to a hashed version of the `user_id`. That hash is deterministic, so
the same person always resolves to the same Amplitude device — it is their
documented behaviour, not a fabricated identity, and does not contradict the
never-fabricate rule.

The sender uses it for exactly one shape of event: `origin='server'` with no
`session_key` and a `user_id`. Such an event has no device by construction and
never will, so there is nothing to wait for. Every other case still skips:

- a **client** event missing `amplitude_device_id` is an SDK bug (`identify()`
  sets it from `anonymous_id`, §6) and stays visible as `skipped`;
- an event that **does** name a `session_key` has a real device id on the way —
  `/v1/events` and `/v1/identity` are separate requests — so it keeps its
  `NoIdentity` grace window rather than settling for the hashed id early;
- an event with neither identifier is never sent, because Amplitude would 400.

This is Amplitude-only. GA4, Adjust and Meta have no equivalent vendor-side
fallback — a real `ga4_client_id` / `adjust_adid` / `fbp` is genuinely
required — so backend events for a person with no registry row keep skipping
there, and that is correct.

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

Config is split in two: **global env vars** (process-level, one set per
deployment) + **per-tenant JSON files** (one file per app, in a directory
pointed at by `EP_TENANTS_DIR`). Everything that varies per app moves into
the tenant file; env vars carry only what is truly process-level.

### 13.1 Global env vars (`/etc/eventpump/eventpump.env`, systemd `EnvironmentFile`)

| Variable | Purpose |
|---|---|
| `EP_DB_CONNSTRING` | one PostgreSQL connection string; all tenants share it |
| `EP_LISTEN` / `EP_INTERNAL_LISTEN` / `EP_METRICS_LISTEN` | bind addresses (api public, api internal, worker metrics) |
| `EP_TENANTS_DIR` | directory of per-tenant JSON files (`<app_id>.json` each) |
| `EP_RATE_LIMIT` | default rate limit (per `(app_id, caller address)` bucket, requests/window). Tenants may override in their file. |
| `EP_TRUSTED_PROXIES` | comma-separated addresses/CIDRs whose `X-Real-IP` is believed; default `127.0.0.1/32,::1/128`. Sets both the stored `client_ip` and the rate-limit bucket. |
| `EP_RETENTION_DAYS` / `EP_RETENTION_DEAD_DAYS` | 30 / 90 defaults — one retention policy for all tenants |
| `EP_IP_MODE` | `raw` (default) \| `geo` — one IP handling policy for all tenants |
| `EP_WORKER_*` | worker tuning: poll, claim batch, concurrency, backoff, breaker thresholds, lease, sender timeout — all process-level |
| `EP_IDENTITY_GRACE_S` | 300 default — how long a delivery waiting on a missing identity row keeps retrying before it settles as `skipped` |
| `EP_IDENTITY_USER_FALLBACK` | ON by default — person-scoped identity resolution for server-origin events (§12). OFF restores `session_key`-only resolution |
| `EP_ADJUST_MAX_IDENTITY_AGE_DAYS` | 30 default — Adjust refuses a person-resolved ADID older than this (§12); `0` = no limit. Tenants may override as `adjust.max_identity_age_days` |
| `EP_ADJUST_ENVIRONMENT` | unset (⇒ Adjust's own `production` default) — `sandbox` routes a UAT deployment's Adjust traffic away from live attribution. Scopes the `adjust_erasure` call too. Any other value stops the boot; case is normalised. Tenants may override as `adjust.environment` |

**Booleans.** Every `EP_*` boolean accepts `true`/`false`, `1`/`0`, `yes`/`no`
or `on`/`off`, case-insensitively, and any other value stops the boot naming
the variable and what it was set to. Silently resolving a typo to the default
is how a destination ends up erasing after an operator switched it off. The
reads this replaced were case-sensitive and disagreed with each other
(default-off flags read `== "true"`, default-on ones `!= "false"`), so a value
they read differently from this parser changes meaning on the upgrade —
`EP_GA4_ENABLED=True` from off to on, `EP_MOENGAGE_ATTRIBUTES_ENABLED=0` from
on to off. Boot writes one stderr line per such variable, giving both
readings; re-spelling the value silences it.

**Removed from env** (moved into per-tenant JSON, §13.2): `EP_TRACKING_PLAN`,
`EP_TENANT_API_KEY`, `EP_INTERNAL_TOKEN`, `EP_COOKIE_DOMAIN`,
`EP_CORS_ORIGINS`, `EP_GA4_*`, `EP_AMPLITUDE_*`, `EP_MOENGAGE_*`,
`EP_ADJUST_*`, `EP_META_*`, all `EP_<X>_ATTRIBUTES_ENABLED`.
(`EP_TENANT_API_KEY` and `EP_INTERNAL_TOKEN` are still consulted for the
single-tenant back-compat path when `EP_TENANTS_DIR` is unset — see the
`deploy/tenants/README.md` "Back-compat" section.)

At boot the process:
- reads `eventpump.env` for the settings above,
- scans `EP_TENANTS_DIR` for `*.json`,
- loads each file (JSONC allowed), validates it (per §6.1, §6.2, §13.2 below),
- fails loud if any file is malformed, any `app_id` is duplicated, any
  filename does not match its `"app_id"` field, or the directory is empty.

### 13.2 Per-tenant JSON (`/etc/eventpump/tenants/<app_id>.json`)

One file per app. `chmod 640 root:eventpump` — the file holds real secrets
(destination credentials, the tenant api key). Content:

```jsonc
{
  // Must match the filename (zainmart.json ⇒ "app_id":"zainmart").
  "app_id": "zainmart",

  // Client-side per-tenant API key (§9.1). Sent by the mobile / web SDK
  // on /v1/*. Ships in app bundles — treat as a build-time secret.
  "tenant_api_key": "zainmart-client-key",
  // Server-side per-tenant secret (§9.3). Sent by backend producers on
  // /internal/v1/*. Never shipped to any client bundle. Must differ from
  // `tenant_api_key`; the loader refuses to boot otherwise.
  "internal_token": "zainmart-internal-secret",

  // Web SDK boundary — per-tenant so each app owns its own subdomain scope.
  "cookie_domain": ".zainmart.com",
  "cors_origins": ["https://www.zainmart.com", "https://m.zainmart.com"],

  // Optional per-tenant override; falls back to EP_RATE_LIMIT.
  "rate_limit": { "permits": 600, "window_seconds": 60 },

  // Same shape as pre-1.2 tracking-plan.json — moves inline into the tenant file.
  "attributes":   { /* SPEC §6.1 user-attribute allowlist */ },
  "events":       { /* SPEC §8 event definitions + origin + destinations */ },
  "destinations": { /* SPEC §6.2 per-destination rename maps */ },

  // What was previously the EP_<X>_* env vars, moved in, per-tenant.
  "destination_config": {
    "ga4": {
      "enabled": true,
      "endpoint": "https://www.google-analytics.com",
      "measurement_id": "G-ZM1",
      "api_secret": "zainmart-ga4-secret",
      "firebase_app_id": null,
      "attributes_enabled": true
    },
    "amplitude": {
      "enabled": true,
      "endpoint": "https://api2.amplitude.com/2/httpapi",
      "api_key":  "zainmart-amp-key",
      "attributes_enabled": true
    },
    "moengage": {
      "enabled": true,
      "endpoint": "https://api-01.moengage.com",
      "app_id":   "ZM-MOE-APP",
      "api_key":  "zainmart-moe-key",
      "attributes_enabled": true
    },
    "adjust": {
      "enabled": true,
      "endpoint": "https://s2s.adjust.com/event",
      "app_token": "zainmart-adjust-app",
      "s2s_token": "zainmart-adjust-s2s-secret",
      "environment": "production",
      "max_identity_age_days": 30,
      "attributes_enabled": true
    },
    "meta": {
      "enabled": false,
      "endpoint": "https://graph.facebook.com",
      "graph_version": "v25.0",
      "pixel_id": "",
      "access_token": "",
      "test_event_code": null,
      "consent_gating": false,
      "action_source": "website",
      "attributes_enabled": false
    }
  }
}
```

**Attribute-flag defaults:** `moengage.attributes_enabled` = **true** by
design (MoEngage is the raw-PII destination). All other destinations'
`attributes_enabled` = **false** by default — opt in per tenant.

`meta_name` on an event (v1.0) is **retired** — superseded by
`destinations.meta.events.<x>.name` (§6.2 R4). A tenant file still carrying
the key is rejected at boot with the migration in the error.

### 13.3 Tenant lifecycle (ops)

- **Add a tenant:** write `/etc/eventpump/tenants/<app_id>.json`, `systemctl
  restart eventpump-api eventpump-worker`. No new database, no migration.
- **Remove a tenant:** delete the file, restart. Historical DB rows for that
  tenant survive and continue to age out per §11 partition retention. To
  hard-delete, `DELETE FROM ... WHERE app_id = '<removed>'` per table.
- **Rotate credentials:** edit the file, restart. Old HTTP clients drain in
  the graceful SIGTERM window; new ones use the new credentials.
- **Change config without restart:** not supported in v1.2 (restart-to-apply,
  see §13.4). Cheap, and prevents whole classes of half-reloaded state bugs.

### 13.4 Loader semantics

- Boot: scan `EP_TENANTS_DIR/*.json`, parse each (JSONC comments allowed),
  validate.
- Duplicate `"app_id"` across files ⇒ fail loud (`duplicate_app_id`).
- `"app_id"` field mismatched to filename ⇒ fail loud (`app_id_filename_mismatch`).
- Zero tenants ⇒ fail loud (`no_tenants_loaded`). A running Event Pump with
  no tenants would silently 401 every request; that's a config bug, not a
  runtime state.
- Any tenant fails validation (§6.1, §6.2, missing required
  `destination_config` for a listed destination, etc.) ⇒ fail loud, no
  tenants loaded. All-or-nothing keeps other tenants from silently going
  missing.
- **An enabled destination missing its credentials ⇒ fail loud** — `tenant
  '<app>': ga4 enabled but api_secret is empty`. The credential is knowable
  at boot, and left to delivery time a typo'd api key or a forgotten
  `measurement_id` starts a pipeline that fails every send: the tenant's
  outbox fills with `failed` rows behind circuit-breaker backoff, and the
  first sign of it is hours of undelivered events. Required per destination
  is what its sender actually reads — GA4 `api_secret` plus one of
  `measurement_id` / `firebase_app_id`, Amplitude `api_key`, MoEngage
  `moengage_app_id` + `api_key`, Adjust `app_token`, Meta `pixel_id` +
  `access_token`. A destination with `"enabled": false` is not checked, so a
  tenant file can carry a scaffolded block with empty credentials and switch
  it on later. Erasure adds nothing: its pipelines are registered only
  alongside their vendor, and Amplitude's deletion-only `secret_key` stays a
  delivery-time `skipped: no_secret_key` (§9.7.2) rather than locking out
  every tenant that has never obtained one.
- Restart-to-apply: no SIGHUP hot reload in v1.2.

### Observability

Prometheus `/metrics` on both processes (AOT-safe exposition — see PLAN.md).
**Every metric gains an `app_id` label** so per-tenant dashboards are
possible against a single Event Pump deployment:

```
events_ingested_total{origin,endpoint,app_id}
deliveries_total{destination,status,app_id}
outbox_pending{destination,app_id}
circuit_state{destination,app_id}
delivery_latency_seconds{destination,app_id}
```

Cardinality risk is minor at expected single-digit tenant counts.

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
