# Event Pump — implementation plan

**Status: APPROVED (2026-07-12) alongside SPEC.md v1.0.**

---

## 1. Repository layout

```
event-pump/
├── SPEC.md                          # the contract (approved before any code)
├── PLAN.md                          # this file
├── README.md                        # architecture diagram, producer paths, event tiers,
│                                    # context table, retry/partition strategy,
│                                    # how-to-add-a-destination, Clarity exclusion note
│
├── server/
│   ├── EventPump.slnx
│   ├── src/EventPump/               # ONE project → ONE AOT binary `eventpump`
│   │   ├── EventPump.csproj         # net10.0, PublishAot, InvariantGlobalization
│   │   ├── Program.cs               # subcommand dispatch: `eventpump api|worker|migrate`
│   │   ├── Model/                   # event/identity/delivery records
│   │   │   └── JsonContexts.cs      # source-generated System.Text.Json contexts (ALL serialization)
│   │   ├── Config/                  # env parsing + tracking-plan loader + registry sync
│   │   ├── Data/                    # NpgsqlDataSource, hand-written mappers, migration runner
│   │   ├── Api/                     # minimal-API endpoints, bearer auth, rate limit,
│   │   │                            # validation, ep_aid cookie, CORS, internal listener
│   │   ├── Worker/                  # claim loop, per-destination Channels, backoff+jitter,
│   │   │                            # circuit breaker, partition maintenance timer, SIGTERM drain
│   │   ├── Senders/                 # ISender + Ga4 / Amplitude / MoEngage / Adjust /
│   │   │                            # PixelPlatformSender (abstract) / MetaCapi (reference, off)
│   │   └── Observability/           # hand-rolled Prometheus registry + JSON log helpers
│   ├── migrations/                  # plain .sql, ordered; applied by `eventpump migrate`
│   │   ├── 0001_outbox.sql          # events_outbox (daily range partitions) + events_dedupe
│   │   ├── 0002_delivery.sql        # events_delivery + claim indexes (documented in-file)
│   │   ├── 0003_identity.sql        # identity_registry + first_seen
│   │   └── 0004_event_registry.sql  # allowlist/routing table (synced from tracking plan)
│   ├── sql/producer_contract.sql    # emit_event() — idempotent CREATE OR REPLACE, applied
│   │                                # after numbered migrations; ALSO the doc platform teams read
│   └── tests/EventPump.Tests/       # xunit: Testcontainers-PG integration + sender/API tests
│
├── sdks/web/
│   ├── package.json                 # zero runtime deps; dev-deps below
│   ├── src/                         # index (public API), session, identity, context, queue,
│   │                                # transport, engagement, clickids, cookies(read-only), uuid
│   ├── src/iife.ts                  # IIFE entry: drains window.ep.q stub in order
│   ├── sveltekit/                   # optional helper: page() wired to afterNavigate
│   ├── tests/                       # vitest + jsdom
│   ├── e2e/                         # Playwright smoke: IIFE + mock server
│   └── scripts/                     # esbuild build (ESM + IIFE), gzip size report
│
├── sdks/flutter/                    # package `event_pump`
│   ├── pubspec.yaml
│   ├── lib/event_pump.dart          # public exports
│   ├── lib/src/                     # client, session, identity, context, queue(JSONL),
│   │                                # transport, engagement, dio_interceptor, http_helper,
│   │                                # route_observer (opt-in)
│   ├── test/                        # fake_async suites
│   └── example/                     # init + track + interceptor + late adjust_adid identify()
│
└── deploy/
    ├── systemd/eventpump-api.service      # Restart=always, NoNewPrivileges,
    ├── systemd/eventpump-worker.service   # ProtectSystem=strict, EnvironmentFile
    ├── .env.example
    ├── tracking-plan.example.json
    ├── docker-compose.yml                 # Postgres 18 for local/smoke only
    ├── mock-destinations.mjs              # single-file Node mock for GA4/Amplitude/… endpoints
    └── smoke.sh                           # end-to-end: PG → migrate → AOT binaries → IIFE page
                                           # + flutter-format batch → assert delivery rows + identity
```

One solution, one published binary (`AssemblyName=eventpump`), two systemd services
running `eventpump api` / `eventpump worker`, independently restartable, shared
model/config code. A third subcommand `migrate` runs the .sql runner (also invoked
by smoke).

---

## 2. Dependencies

### 2.1 Server — NuGet (runtime): **one package**

| Package | Why | AOT status |
|---|---|---|
| `Npgsql` (latest stable for .NET 10) | Mandated PostgreSQL driver. Used via `NpgsqlDataSource` + hand-written mappers; `jsonb` passed as strings (no dynamic JSON mapping). | Officially supports NativeAOT/trimming when dynamic features are avoided — which we avoid. |

Everything else is the framework: ASP.NET Core minimal APIs (framework reference),
`Microsoft.Extensions.Hosting/Logging` (inbox, JSON console formatter),
`System.Threading.Channels` (inbox), `System.Threading.RateLimiting` +
`Microsoft.AspNetCore.RateLimiting` (inbox), `Guid.CreateVersion7()` (inbox, .NET 9+),
SHA-256 via `System.Security.Cryptography` (inbox).

**Deliberately hand-rolled instead of packaged** (each is small, and the packages
are the AOT risk):

- **Prometheus exposition** (~200 lines, counters/gauges/histograms, text format
  v0.0.4). Verified before this plan: `prometheus-net` does not advertise
  trim/AOT annotations, and the official
  [OpenTelemetry Prometheus AspNetCore exporter](https://www.nuget.org/packages/OpenTelemetry.Exporter.Prometheus.AspNetCore)
  is **still beta** (`1.16.0-beta.1`, June 2026) with no AOT guarantee. Five metric
  families don't justify gambling the zero-warning requirement. ← this is the
  "non-AOT-safe library ⇒ hand-roll, and tell me" case.
- **Retry backoff + circuit breaker** (~100 lines). Polly v8 is likely fine, but
  our semantics (per-destination breaker wired to `circuit_state` metric, lease
  release) are custom anyway; a dependency saves nothing.
- **Migration runner** (~80 lines: `schema_migrations` table, apply in order in a
  transaction). Mandated: "plain .sql migrations + tiny runner; no EF".
- **Env/config binding** (no `Microsoft.Extensions.Configuration` binder —
  reflection-based binding is an AOT trap; we read env vars explicitly).

### 2.2 Server — NuGet (test-only; AOT-irrelevant, tests run on CoreCLR)

| Package | Why |
|---|---|
| `xunit` + `xunit.runner.visualstudio` + `Microsoft.NET.Test.Sdk` | Standard test stack. |
| `Testcontainers.PostgreSql` | Mandated: real PG for outbox fan-out, SKIP LOCKED concurrency, partition job, dedupe, first_seen once-ever. When `EP_TEST_CONNSTRING` is set (a server whose role has CREATEDB), the fixture uses that server with throwaway databases instead — needed on this dev box, where the container registry is unreachable. |
| `Microsoft.AspNetCore.Mvc.Testing` | In-memory host for API tests (validation, allowlists, auth, batch caps, ep_aid set-when-absent). |

No Moq/NSubstitute (reflection/emit based): senders take `HttpMessageHandler`, so a
hand-written stub handler covers all sender tests (also mandated: "AOT-safe stub").

### 2.3 Web SDK — npm

**Runtime: none** (mandated; UUIDv4/v7 hand-rolled on `crypto.getRandomValues`).

Dev-only:

| Package | Why |
|---|---|
| `typescript` | Language + `.d.ts` emission. |
| `esbuild` | Builds both targets (ESM + minified IIFE `ep.js`) from one small script; fastest path to a reported gzip number. No plugin ecosystem needed → no rollup/vite complexity. |
| `vitest` + `jsdom` | Mandated test stack (S0–S4 ordering, rotation fake timers, engagement stopwatch, click-id harvesting, `_ga`/`_fbp` parsing, stub draining, queue persistence). |
| `@playwright/test` | Mandated smoke: IIFE in a real page vs mock server (identity-before-events, sendBeacon-on-hidden). |

### 2.4 Flutter SDK — pub.dev

| Package | Why |
|---|---|
| `dio` | Transport (user decision 2026-07-12 — matches the app). Also powers the mandated `EventPumpDioInterceptor`. |
| `shared_preferences` | anonymous_id, first_seen_at, session_number, session_key, last_active_at (SPEC §2/§3). |
| `path_provider` | App-support dir for the JSONL queue file (SPEC §7). |
| `package_info_plus` | app_version / build for context (SPEC §5). |
| `device_info_plus` | os, os_version, model, category for context (SPEC §5). |
| `connectivity_plus` | connection type context + flush-on-regained-connectivity (SPEC §5/§7). |
| `uuid` | UUIDv4 + UUIDv7 (v7 supported since uuid 4.x). |

Dev-only: `flutter_test` (SDK), `fake_async` (mandated: session/flush/stopwatch
clocks), `flutter_lints`. No mocking package — hand-written fakes (dio has a
pluggable `HttpClientAdapter`).

---

## 3. Implementation order (after approval)

Each phase ends at its verification gate; nothing ships unverified.

1. **Schema + migrations + producer contract** — migrations, `emit_event`,
   partition helpers. *Gate:* Testcontainers tests: fan-out, dedupe, first_seen
   once-ever, SKIP LOCKED with two claimers, partition create/drop.
2. **`eventpump api`** — endpoints, auth, validation, cookie, CORS, rate limit,
   registry sync. *Gate:* API test suite green.
3. **`eventpump worker`** — claim loop, channels, backoff, breaker, partition
   timer, SIGTERM drain. *Gate:* integration tests green; `dotnet publish
   /p:PublishAot=true` **zero warnings** (checked here, not at the end).
4. **Destination senders** — web-search each API's current docs first (GA4 MP,
   Amplitude V2, MoEngage, Adjust S2S, Meta CAPI params); implement against mock
   HTTP. *Gate:* payload-correctness + error-mapping unit tests green.
5. **Web SDK** — core, IIFE stub, sveltekit helper. *Gate:* vitest green,
   Playwright smoke green, gzip size reported ≤ 8 KB.
6. **Flutter SDK** — core, interceptor, example. *Gate:* `flutter test` green.
7. **/deploy + README + smoke** — units, env example, mock destinations, smoke.
   *Gate:* `smoke.sh` passes end-to-end.
8. **Final evidence report** — dotnet test, AOT publish output, npm test + bundle
   size, flutter test, smoke — all shown, per the deliverables list.

---

## 4. Resolved decisions (2026-07-12)

1. **Flutter HTTP client: `dio`** — matches the app; interceptor ships in the same
   package; `dio` is a regular dependency.
2. **Flutter session resume: persist `session_key`** in `shared_preferences`
   alongside `last_active_at`; cold start within 30 min resumes the session.
3. **IP → geo:** v1 ships `EP_IP_MODE=raw` fully implemented; `geo` config surface
   exists behind a pluggable resolver interface; mmdb reader deferred.
4. **Tracking plan as a JSON file** referenced by `EP_TRACKING_PLAN`, synced to the
   `event_registry` table at boot — accepted as satisfying "env-var driven".
5. **Partial batch acceptance** — accepted (valid events stored, invalid reported
   per index).
6. **Flutter device id: SPEC §2 stands** — random UUIDv4 in `shared_preferences`,
   no hardware ids. `platform_device_id_plus` was evaluated and rejected: returns
   ANDROID_ID (forbidden by spec; Google Play policy risk since `anonymous_id`
   feeds Adjust/Meta/GA4), IDFV on iOS dies with the last vendor app anyway, raw
   User-Agent on web, and the package is stale (15 months, unverified uploader).
