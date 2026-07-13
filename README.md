# Event Pump

[![CI](https://github.com/edraj/event-pump/actions/workflows/ci.yml/badge.svg)](https://github.com/edraj/event-pump/actions/workflows/ci.yml)

First-party event pipeline: web/mobile clients and backend services emit events
into a PostgreSQL outbox (inside the platform's business database), and a
worker delivers them to downstream destinations over their server-to-server
APIs. The full contract lives in [SPEC.md](SPEC.md); the implementation plan in
[PLAN.md](PLAN.md).

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

One .NET 10 **Native AOT** binary (`eventpump`), two subcommands (`api`,
`worker`) running as separate systemd services, plus `migrate`. No EF, no
reflection serializers, zero AOT/trim warnings. Server runtime dependency
count: **one** (Npgsql).

## The three producer paths (SPEC §8)

| Path | Who | How | When to use |
|---|---|---|---|
| **a. SQL contract** | Platform services sharing the database | `SELECT emit_event(...)` inside the business transaction ([server/sql/producer_contract.sql](server/sql/producer_contract.sql)) | **The PRIMARY server-fact path.** The event is durable iff the transaction commits — order_placed can never exist without its order. |
| **b. Internal HTTP** | Backend producers *outside* this database | `POST /internal/v1/events` with the internal bearer token, on the firewalled internal listener | Only for services that cannot reach the platform database. Services that share the database use the SQL contract, **not** this endpoint. |
| **c. Client SDKs** | Browsers / apps | `POST /v1/events` batches with the per-app token | Client-fact events (views, taps). `origin='client'` allowlist. |

Each event name belongs to exactly **one** path via the per-origin allowlists
in the tracking plan.

## Event tiers

| Tier | What | Mechanism |
|---|---|---|
| 1 | Server facts (order_placed, refund_issued) | `emit_event()` in the business transaction |
| 2 | Client-initiated facts the platform already sees | **X-Event header pattern**: the SDK stamps `X-Event` / `X-Session-Key` / `X-Anonymous-Id` on a request it already makes (`eventHeaders()` / dio interceptor); the platform handler forwards them to `emit_event()`. No extra round trip. |
| 3 | Pure client behavior (product_viewed, screen_view) | SDK `track()` → `/v1/events` |

## Context enrichment (SPEC §5)

Full context is collected once per session and registered via `/v1/identity`;
per-event context carries only page/screen, `engagement_time_msec`,
`session_number`, and `sdk`. Fields exist because downstream senders consume
them:

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

The server additionally records the client IP from `X-Real-IP` (nginx must set
it) into the identity registry and event context — consumed by GA4
`ip_override`, Adjust, and CAPI `user_data`.

## Failure & retry semantics (SPEC §11)

- **SDK → API**: at-least-once. Batches retry on 5s/30s/2m backoff, give up
  after 24 h; queues persist across restarts (web localStorage cap 200;
  flutter JSONL file cap 500). The server dedupes on `event_id`, so resending
  is always safe (`sendBeacon` batches are deliberately never acked locally).
- **Worker → destinations**: lease-based claims (`FOR UPDATE SKIP LOCKED`,
  N instances safe; a crashed worker's claims self-release). Exponential
  backoff with jitter: 30 s base, ×2, 1 h cap, 10 attempts ⇒ `dead`.
  Per-destination circuit breaker (5 consecutive failures ⇒ 2 min pause) and
  independent pipelines — one slow destination never blocks the others.
- **`skipped` is not an error**: it means required identity was absent by
  design (e.g. web events have no `adjust_adid` — the server's
  no-adid ⇒ skipped rule is intended; identity is never fabricated).
- Delivery state transitions are structured-logged as `event_id` +
  `event_name` + `destination` + `status` only — **never payloads**.

## Partitions & retention (SPEC §11)

`events_outbox` and `events_delivery` are partitioned per day on
`received_at`. The worker's maintenance timer pre-creates partitions 3 days
ahead and drops day pairs older than `EP_RETENTION_DAYS` (30) — unless the
day's delivery partition contains `dead` rows, in which case the pair is held
until `EP_RETENTION_DEAD_DAYS` (90) to preserve failure evidence, then dropped
regardless. Global `event_id` dedupe lives in the non-partitioned
`events_dedupe` table (a partitioned table cannot carry that unique index),
pruned on the same timer.

## How to add a destination

1. Implement `IDestinationSender` in `server/src/EventPump/Senders/` (or
   subclass `PixelPlatformSender` for Meta/Snap/TikTok-style CAPI: SHA-256
   normalization, consent gating, and user_data assembly are already there).
   Consult the destination's **current** API docs first.
2. Classify every response into `Delivered` / `Retry` / `Skip(reason)` /
   `Dead` — never throw for expected failures.
3. Register it in `SenderFactory.Create` behind an `EP_<NAME>_ENABLED` flag
   and add its config to `EpConfig`.
4. Route events to it in the tracking plan: `"destinations": [..., "name"]`.
5. Add payload-correctness + error-mapping tests against the stub
   `HttpMessageHandler` (see `SenderTests.cs`) and a mock endpoint to
   `deploy/mock-destinations.mjs`.

Adding a **click-id platform** for CAPI is config only: append the URL param
name to the web SDK's `clickIdParams` — no SDK release.

> **Microsoft Clarity is deliberately OUT OF SCOPE.** Clarity has no
> server-side ingestion API (device SDK only) — do not add a sender for it.

## Running

```bash
# migrations (plain .sql + tiny runner; re-runnable)
EP_DB_CONNSTRING=... eventpump migrate

# services (see deploy/.env.example for full config)
eventpump api
eventpump worker
```

### RPM (EL9+ and Fedora)

```bash
./deploy/rpm/build-rpm.sh        # -> build/rpm/RPMS/x86_64/eventpump-*.rpm
sudo dnf install build/rpm/RPMS/x86_64/eventpump-*.rpm

sudo vi /etc/eventpump/eventpump.env         # config, %config(noreplace)
sudo vi /etc/eventpump/tracking-plan.json
eventpump migrate                            # finds /usr/share/eventpump/migrations
sudo systemctl enable --now eventpump-api eventpump-worker
```

The package installs the self-contained Native AOT binary to
`/usr/bin/eventpump` (no .NET runtime dependency), hardened systemd units, a
`sysusers.d` service account, config under `/etc/eventpump` (0640,
root:eventpump, preserved on upgrade), and migrations + the SQL producer
contract under `/usr/share/eventpump`. ILCompiler links against glibc 2.34
(= RHEL 9), so one package built on Fedora installs on EL9+ — RPM's automatic
ELF dependency generator enforces the floor at `dnf` time. The build vendors
the NuGet cache into the SRPM, so `%build` is fully offline (mock-friendly);
build requires `dotnet-sdk-10.0`, `clang`, `zlib-devel`.

Manual deploy instead: `deploy/systemd/*.service`, `deploy/.env.example`,
`deploy/tracking-plan.example.json`.

**Deployment requirements (SPEC §9.5):** the API must be served from a
subdomain of the site's registrable domain (e.g. `collect.example.com`) with
`EP_COOKIE_DOMAIN=.example.com`, so the server-set `ep_aid` cookie
(SameSite=Lax, ~13 months) flows on SDK requests; nginx must pass `X-Real-IP`.

## Tests & smoke

```bash
# server (Testcontainers PG by default; or point at a local PG 18)
cd server && EP_TEST_CONNSTRING="Host=...;Username=...;Password=..." dotnet test

# web SDK
cd sdks/web && npm test && npm run build && npx playwright test

# flutter SDK
cd sdks/flutter && flutter test

# end-to-end (compose PG, or EP_SMOKE_USE_LOCAL_PG=1 with PGUSER/PGPASSWORD)
./deploy/smoke.sh
```

The smoke exercises all three producer paths — a real headless browser running
the IIFE build, a flutter-format batch, `emit_event()` over psql, and
`/internal/v1/events` — against mock destination servers, then asserts
delivery rows and identity enrichment end-to-end.

## SDKs

- **Web** ([sdks/web](sdks/web)): zero runtime dependencies, ESM + IIFE
  builds, ~3.7 KB gzipped. The copy-paste snippet lives in
  [sdks/web/README.md](sdks/web/README.md).
- **Flutter** ([sdks/flutter](sdks/flutter)): package `event_pump`, dio
  transport, `EventPumpDioInterceptor` for the X-Event pattern, JSONL offline
  queue.

## License

[AGPL-3.0-only](LICENSE) — matching the rest of the edraj organization.

Ground rules that bind the whole repo: the word "analytics" never appears in
package names, binaries, globals, cookies, or endpoints; no third-party
tracking libraries in the SDKs; no PII collected automatically (`user_id` via
`setUser()` only); no fingerprinting and no hardware ids — `anonymous_id` is a
server-set first-party cookie on web and a persisted random UUID on mobile.
