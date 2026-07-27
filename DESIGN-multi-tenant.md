# Design — Multi-tenant Event Pump (v1.2)

**Status:** DRAFT — pending review and approval before implementation.
**Motivation:** Today the whole system is single-tenant (implicitly "zainmart").
Configuration, tracking plan, and destination credentials all live in one set
of env vars and one JSON file. Two more apps are known to be coming and more
after that. This design turns Event Pump into a proper multi-tenant service
without duplicating deployments.

---

## 1. Decisions locked

| # | Decision | Choice |
|---|---|---|
| D1 | Data model | **Shared PostgreSQL** — every domain table gains an `app_id` column. Existing zainmart data backfilled `app_id='zainmart'` at migration time. |
| D2 | Config layout | **One JSON file per tenant** at `/etc/eventpump/tenants/<app_id>.json` (JSONC allowed). Secrets in the same file, `chmod 640 root:eventpump`. **Restart-to-apply** (no hot reload). **Fail-loud** at boot if any tenant file is malformed. |
| D3 | `app_id` per request | **From the bearer token.** `EP_CLIENT_TOKENS` extended; symmetric `EP_INTERNAL_TOKENS` added for backend producers. |
| D4 | SDKs | **Unchanged** — each app already configures its own `endpoint` + `appToken`. |
| D5 | Backward compat | **Hard cutover** — one release, one migration + config-directory setup in a maintenance window. |

---

## 2. Configuration surface — what's global vs per-tenant

### Global env vars (`/etc/eventpump/eventpump.env`)

Everything that's process-level, not tenant-level:

| Variable | Purpose |
|---|---|
| `EP_DB_CONNSTRING` | one Postgres, shared across tenants |
| `EP_LISTEN` / `EP_INTERNAL_LISTEN` / `EP_METRICS_LISTEN` | bind addresses |
| `EP_TENANTS_DIR` | new — directory of per-tenant JSON files |
| `EP_RATE_LIMIT` | fallback rate limit; per-tenant override in tenant file |
| `EP_RETENTION_DAYS` / `EP_RETENTION_DEAD_DAYS` | one retention policy for all tenants |
| `EP_IP_MODE` | one IP handling policy |
| `EP_WORKER_*` | worker tuning (poll, batch, concurrency, backoff, breaker, lease, timeout) |

### Per-tenant JSON file (`/etc/eventpump/tenants/<app_id>.json`)

Everything that varies per app: tracking plan (already tenant-shaped) + auth + destination credentials.

```jsonc
{
  "app_id": "zainmart",

  // Bearer tokens
  "client_tokens": ["zainmart-web-tok", "zainmart-mobile-tok"],
  "internal_token": "zainmart-internal-secret",

  // Web SDK boundary
  "cookie_domain": ".zainmart.com",
  "cors_origins": ["https://www.zainmart.com", "https://m.zainmart.com"],

  // Optional per-tenant rate limit override (falls back to EP_RATE_LIMIT)
  "rate_limit": { "permits": 600, "window_seconds": 60 },

  // From today's tracking-plan.json — unchanged shape
  "attributes": { /* SPEC §6.1 allowlist */ },
  "events":     { /* SPEC §13 event definitions */ },
  "destinations": { /* SPEC §6.2 per-destination rename maps */ },

  // What was previously EP_<X>_* env vars, moved in
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

### Loader semantics

- At boot, `EP_TENANTS_DIR` is scanned for `*.json` files.
- Each file is parsed (JSONC comment-aware). Each `app_id` inside the file must match the filename (`zainmart.json` → `"app_id":"zainmart"`) — otherwise fail loud.
- No tenants ⇒ fail loud (a running Event Pump with zero tenants is a config bug, not a runtime state).
- Duplicate `app_id` across files ⇒ fail loud.
- Same tracking-plan validation applies per tenant (SPEC §6.2 R6, R7).

---

## 3. Database schema — the migration

Every domain table gains `app_id`. Existing data backfilled `zainmart`.

### New migration `0009_multi_tenant.sql`

```sql
-- Add app_id everywhere, non-null with a transitional default.
ALTER TABLE events_outbox     ADD COLUMN app_id text NOT NULL DEFAULT 'zainmart';
ALTER TABLE events_delivery   ADD COLUMN app_id text NOT NULL DEFAULT 'zainmart';
ALTER TABLE identity_registry ADD COLUMN app_id text NOT NULL DEFAULT 'zainmart';
ALTER TABLE user_attributes   ADD COLUMN app_id text NOT NULL DEFAULT 'zainmart';
ALTER TABLE first_seen        ADD COLUMN app_id text NOT NULL DEFAULT 'zainmart';
ALTER TABLE event_registry    ADD COLUMN app_id text NOT NULL DEFAULT 'zainmart';
ALTER TABLE events_dedupe     ADD COLUMN app_id text NOT NULL DEFAULT 'zainmart';

-- Composite primary keys (session_key + app_id, user_id + app_id, etc.)
-- because tenants have their own id namespaces.
ALTER TABLE identity_registry DROP CONSTRAINT identity_registry_pkey;
ALTER TABLE identity_registry ADD  CONSTRAINT identity_registry_pkey PRIMARY KEY (app_id, session_key);

ALTER TABLE user_attributes   DROP CONSTRAINT user_attributes_pkey;
ALTER TABLE user_attributes   ADD  CONSTRAINT user_attributes_pkey   PRIMARY KEY (app_id, user_id);

ALTER TABLE first_seen        DROP CONSTRAINT first_seen_pkey;
ALTER TABLE first_seen        ADD  CONSTRAINT first_seen_pkey        PRIMARY KEY (app_id, anonymous_id);

ALTER TABLE event_registry    DROP CONSTRAINT event_registry_pkey;
ALTER TABLE event_registry    ADD  CONSTRAINT event_registry_pkey    PRIMARY KEY (app_id, event_name);

ALTER TABLE events_dedupe     DROP CONSTRAINT events_dedupe_pkey;
ALTER TABLE events_dedupe     ADD  CONSTRAINT events_dedupe_pkey     PRIMARY KEY (app_id, event_id);

-- Delivery uniqueness within a partition day
ALTER TABLE events_delivery   DROP CONSTRAINT IF EXISTS events_delivery_pkey;
ALTER TABLE events_delivery   ADD  CONSTRAINT events_delivery_pkey
  PRIMARY KEY (app_id, received_at, event_ref, destination);

-- Indexes: worker claim, per-tenant list queries
CREATE INDEX events_delivery_claim_idx
  ON events_delivery (app_id, destination, next_attempt_at)
  WHERE status IN ('pending','failed');

CREATE INDEX identity_registry_anon_idx ON identity_registry (app_id, anonymous_id);

-- Drop the transitional default now that all existing rows carry a value.
ALTER TABLE events_outbox     ALTER COLUMN app_id DROP DEFAULT;
ALTER TABLE events_delivery   ALTER COLUMN app_id DROP DEFAULT;
ALTER TABLE identity_registry ALTER COLUMN app_id DROP DEFAULT;
ALTER TABLE user_attributes   ALTER COLUMN app_id DROP DEFAULT;
ALTER TABLE first_seen        ALTER COLUMN app_id DROP DEFAULT;
ALTER TABLE event_registry    ALTER COLUMN app_id DROP DEFAULT;
ALTER TABLE events_dedupe     ALTER COLUMN app_id DROP DEFAULT;
```

**Notes:**

- `zainmart` is the historical hard-coded default. Operators with a different first app can adjust the migration by editing the DEFAULT before running.
- Composite PKs mean `session_key`, `user_id`, `event_id` are scoped per-tenant; `session_key='X'` for zainmart and `session_key='X'` for appb are distinct rows.
- Retention (SPEC §11 partition drop) is unchanged — daily partitions on `received_at` don't need to be per-tenant. Retention is a process-level decision.

---

## 4. Runtime — how `app_id` flows

### API request lifecycle

1. `POST /v1/events` arrives with `Authorization: Bearer <tok>`.
2. Middleware resolves `<tok> → app_id` via the concatenated map of all tenants' `client_tokens`. If not found, `401 unauthorized`.
3. The resolved `app_id` is attached to `HttpContext.Items["app_id"]` (or equivalent).
4. Every subsequent operation — tracking-plan lookup, event validation, DB write — takes `app_id` as an explicit parameter.
5. Response cookies use the tenant's `cookie_domain`; CORS allows the tenant's `cors_origins`.

### Internal listener

- Same, keyed off `EP_INTERNAL_TOKENS` (the new symmetric map for backend producers). One token per tenant.
- DSR endpoint becomes `DELETE /internal/v1/user_attributes/{app_id}/{user_id}` — `user_id` alone is no longer unique.

### SQL producer contract

`emit_event()` gets a new required parameter `p_app_id`:

```sql
PERFORM emit_event(
  p_app_id       => 'zainmart',
  p_event_name   => 'order_placed',
  p_properties   => jsonb_build_object(...),
  ...);
```

The `event_registry` lookup keys on `(app_id, event_name)`. Platform services that share this DB must know which tenant they're emitting for — usually one platform service belongs to exactly one tenant.

### Worker

- Claim loop: `SELECT ... WHERE app_id = $1 AND destination = $2 AND status IN ('pending','failed')`.
- One goroutine per `(app_id, destination)` pair — a slow zainmart Adjust does not block appb Adjust.
- Circuit breaker per `(app_id, destination)` for the same reason.
- Sender lookup: `senders[app_id][destination]` — each tenant has its own set of sender instances with its own HTTP client + credentials.

### Sender construction

Where today `SenderFactory.Create(config, plan, dataSource, loggerFactory)` returns one list, it becomes:

```csharp
var byTenant = SenderFactory.CreateAll(tenants, dataSource, loggerFactory);
// byTenant["zainmart"]["ga4"] -> Ga4Sender with zainmart's credentials
```

Each sender still implements `IDestinationSender` unchanged; only the resolution path changes.

---

## 5. Metrics

Every counter/gauge gains an `app_id` label:

```
events_ingested_total{origin, endpoint, app_id}
deliveries_total{destination, status, app_id}
outbox_pending{destination, app_id}
circuit_state{destination, app_id}
delivery_latency_seconds{destination, app_id}
```

Cardinality risk is minor — expect single-digit tenants for the foreseeable future.

---

## 6. Reserved events (`ep_attributes_synced`, `first_visit`)

These are per-tenant events. Each tenant's plan gets its own `ep_attributes_synced` auto-injected by `TrackingPlan.Parse` (already does this). `first_visit` is emitted per-tenant when a new `(app_id, anonymous_id)` appears.

---

## 7. SPEC.md changes (draft — will be finalized before implementation)

- **Banner** → v1.2 with a note.
- **§0 Overview** — mention "multi-tenant by client token" once.
- **§8 Producer paths** — `emit_event(p_app_id, ...)` — new required parameter.
- **§9.1 / §9.2 / §9.3** — mention "server resolves `app_id` from bearer token."
- **§9.6 DSR** — path becomes `/internal/v1/user_attributes/{app_id}/{user_id}`.
- **§10 SQL producer contract** — `emit_event` signature updated.
- **§11** — every table entry gains `app_id`; primary keys documented as composite.
- **§13** — most env vars move into the per-tenant JSON; new `EP_TENANTS_DIR`. Split the config table into "global env" and "per-tenant JSON."
- **§13 observability** — metrics label list adds `app_id`.

---

## 8. Rollout plan (hard cutover per D5)

Ops runbook for the maintenance window:

1. **Stop** `eventpump-api` and `eventpump-worker`.
2. **Build** and install the v1.2 binary.
3. **Create** `/etc/eventpump/tenants/zainmart.json` by moving env-var content into JSON (a helper command `eventpump migrate-from-env > /etc/eventpump/tenants/zainmart.json` writes it for you).
4. **Update** `/etc/eventpump/eventpump.env` — remove the moved-out variables, add `EP_TENANTS_DIR=/etc/eventpump/tenants`.
5. **Run** `eventpump migrate` — applies migration 0009, backfills `app_id='zainmart'`.
6. **Start** `eventpump-api`; smoke a request; **start** `eventpump-worker`.
7. **Verify** metrics show `app_id="zainmart"` labels.

Onboarding a second tenant post-cutover:
1. Drop a new `/etc/eventpump/tenants/appb.json` (copy zainmart's, edit).
2. `sudo systemctl restart eventpump-api eventpump-worker`.
3. Point appb's SDK at the shared endpoint with appb's own token.

---

## 9. Open items — please confirm or override

1. **Per-tenant rate limits** — the design allows an optional `rate_limit` override in the tenant file with `EP_RATE_LIMIT` as fallback. Confirm this is what you want, or drop rate-limit-per-tenant entirely.
2. **Concurrency per tenant** — worker runs one pipeline per `(app_id, destination)`. That means N tenants × M destinations goroutines. For 3 tenants × 5 destinations = 15 pipelines. Fine now; may want per-tenant concurrency caps if a huge tenant is added.
3. **DSR endpoint URL shape** — I proposed `/internal/v1/user_attributes/{app_id}/{user_id}`. Alternative: `/internal/v1/tenants/{app_id}/user_attributes/{user_id}` (verbose but consistent if we add more per-tenant admin endpoints). Which?
4. **`migrate-from-env` helper command** — a one-shot conversion tool for the cutover. Worth building, or do you prefer a hand-written first tenant file?

---

## 10. Files touched (Step 2 preview — no code yet)

**Server:**

- `server/migrations/0009_multi_tenant.sql` (new)
- `server/sql/producer_contract.sql` — `emit_event` gains `p_app_id`
- `server/src/EventPump/Config/EpConfig.cs` — global-only settings; per-tenant moved out
- `server/src/EventPump/Config/TenantConfig.cs` (new) — record for one tenant's config
- `server/src/EventPump/Config/TenantRegistry.cs` (new) — loader for `EP_TENANTS_DIR`
- `server/src/EventPump/Config/TrackingPlan.cs` — the plan is now inline inside a tenant, not a top-level file
- `server/src/EventPump/Api/ApiApp.cs` — token-to-app resolution middleware, per-tenant cookie/CORS
- `server/src/EventPump/Api/EventValidation.cs` — takes `app_id` for per-tenant allowlist
- `server/src/EventPump/Api/IdentityValidation.cs` — takes `app_id` for per-tenant attribute allowlist
- `server/src/EventPump/Data/EventStore.cs` — `app_id` parameter on every insert/upsert/select
- `server/src/EventPump/Data/RegistrySync.cs` — syncs per-tenant `event_registry` rows
- `server/src/EventPump/Worker/DeliveryWorker.cs` — claim keyed by `(app_id, destination)`, per-pair circuit breakers
- `server/src/EventPump/Worker/IDestinationSender.cs` — `DeliveryItem` gains `AppId`
- `server/src/EventPump/Senders/*` — each sender constructor takes tenant-scoped credentials
- `server/src/EventPump/Senders/SenderFactory.cs` — builds `Dictionary<app_id, IReadOnlyDict<destination, IDestinationSender>>`
- `server/src/EventPump/Program.cs` — loads tenants dir, wires per-tenant senders
- New CLI subcommand `eventpump migrate-from-env` (optional per open item 4)

**Deploy:**

- `deploy/tenants/zainmart.example.json` (new) — reference file for the current setup
- `deploy/.env.example` — pared-down global-only settings
- `deploy/systemd/eventpump-api.service` — no change (still one service)
- `deploy/rpm/eventpump.spec` — install the `tenants/` directory

**Tests:**

- `server/tests/EventPump.Tests/TenantRegistryTests.cs` (new) — loader, malformed rejection, duplicate app_id rejection
- `server/tests/EventPump.Tests/MultiTenantIsolationTests.cs` (new) — tenant A's events never reach tenant B's senders
- Every existing test updated to pass `app_id` where it matters

**SDKs:** none. Per D4.

---

## 11. Milestone breakdown

| M# | Milestone | Est |
|---|---|---|
| M9 | SPEC.md v1.2 amendment + design sign-off | 1 day |
| M10 | Migration 0009 + per-tenant `event_registry` sync | 1 day |
| M11 | `TenantConfig` / `TenantRegistry` loader + tests | 1 day |
| M12 | API layer: token→app_id middleware, per-tenant cookie/CORS, validators | 2 days |
| M13 | EventStore: `app_id` threaded through every method | 1 day |
| M14 | SQL producer contract: `emit_event(p_app_id, ...)` + registry sync | 1 day |
| M15 | Sender layer: per-tenant construction, DeliveryItem carries app_id | 2 days |
| M16 | Worker: per-`(app_id, destination)` claim + circuit breaker + metrics labels | 2 days |
| M17 | DSR endpoint tenancy, `/metrics` labels, ops runbook | 1 day |
| M18 | Migration test + smoke updates + rollout runbook | 2 days |

**~14 working days of focused work.** Not aggressive, but realistic given the cross-cutting nature.

---

## 12. What I need from you before Step 2

- **Answers to §9 open items** (per-tenant rate limits, DSR URL shape, migrate-from-env helper).
- **Green light on the SPEC diff sketch in §7** — I'll write the full diff as part of M9.
- Anything I've missed that you want folded in before I start.
