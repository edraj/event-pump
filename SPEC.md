# Event Pump — SPEC

**Status: APPROVED v1.0 (2026-07-12). Behavior changes require spec re-approval.**

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
eventHeaders(event_name?: string): Record<string, string>   // §8 X-Event pattern
flush(): Promise<void>
```

Flutter (`event_pump`):

```dart
EventPump.init(EventPumpConfig(endpoint, appToken, ...));
EventPump.instance.track(name, {properties});
EventPump.instance.screen(name, {properties});
EventPump.instance.setUser(userId);  EventPump.instance.clearUser();
EventPump.instance.identify({handles});          // e.g. late adjust_adid
EventPump.instance.eventHeaders([eventName]);    // Map<String, String>
EventPump.instance.flush();
// EventPumpDioInterceptor (dio), plain-http helper, optional EventPumpRouteObserver (OFF by default)
```

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
  "first_seen_at"?, "handles"?: {…§6…}, "context"?: {…§5 full…}}`.
- **Partial upsert**: only the fields present are written. `handles.click_ids`
  merges per click-id name (latest `captured_at` wins). `context` merges at the
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
   unknown event name is a deploy-time bug, not a runtime condition).
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
  `no_adjust_adid`, `no_event_token`, `destination_disabled`, `consent_absent`.

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
| MoEngage Data API | `user_id` → their customer id | `skipped: no_user_id` | auth per current docs; receives `first_visit` |
| Adjust S2S | `adjust_adid` (or platform ad id) + config event-token map | `skipped: no_adjust_adid` / `no_event_token` | revenue+currency on purchases; includes IP (AEM requirement); follows their idempotency guidance |
| Meta CAPI (reference subclass) | `fbp`/`fbc`/hashed user_data | `skipped` | built on `PixelPlatformSender`; **disabled by default** |

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

Tracking-plan JSON (synced into the `event_registry` table at api/worker boot so
`emit_event` shares the same allowlist + routing):

```jsonc
{
  "events": {
    "product_viewed": { "origin": "client", "destinations": ["ga4", "amplitude"] },
    "order_placed":   { "origin": "server", "destinations": ["ga4", "amplitude", "adjust", "moengage"],
                        "meta_name": "Purchase", "adjust_token": "abc123" },
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
(PII):** `event_id` + `event_name` + `destination` + `status` only. No secrets in
code, logs, or tests.

---

## 14. Ground rules (restated, binding)

- The word "analytics" must not appear in package names, binaries, globals,
  cookies, or endpoints (docs/comments may use it descriptively).
- No third-party tracking/measurement libraries in the SDKs. No PII collected
  automatically; `user_id` via `setUser()` only.
- .NET 10 Native AOT compliance is non-negotiable: source-generated
  `System.Text.Json` contexts for ALL serialization, zero AOT/trim warnings on
  publish. A non-AOT-safe library ⇒ pick another or hand-roll, and say so.
- Web SDK: zero runtime dependencies, ≤ 8 KB gzipped (number reported).
- Any deviation from this spec: stop and ask.
