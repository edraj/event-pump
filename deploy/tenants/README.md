# Multi-tenant configuration

Every tenant lives in its own `*.json` or `*.jsonc` file under the directory
pointed to by the `EP_TENANTS_DIR` env var. `zainmart.example.jsonc` in this
directory is a full reference — copy it, fill in the `REPLACE_ME_*` fields,
name the file whatever you like (the `app_id` inside the file is what the
registry keys on).

## What's global vs per-tenant

Global (`EP_*` env vars, one value for the process):

- `EP_DB_CONNSTRING` — the shared PostgreSQL cluster
- `EP_LISTEN`, `EP_INTERNAL_LISTEN`, `EP_METRICS_LISTEN` — listeners
- `EP_TENANTS_DIR` — where to find tenant files
- `EP_RETENTION_DAYS`, `EP_RETENTION_DEAD_DAYS` — partition retention
- `EP_WORKER_*` — poll interval, batch size, backoff, breaker knobs
- `EP_IP_MODE`, `EP_DOCS` — SDK ingestion behaviour

Per tenant (`app_id`, `tenant_api_key`, `internal_token`, plan, rate limits,
destination endpoints and credentials, cookie domain, CORS origins,
per-destination attribute gates) all live in the tenant's JSON file.

## Auth model (SPEC v1.2)

**Two per-tenant secrets** — a client-side key and a server-side secret.
Ship one, keep the other private.

- `tenant_api_key` — client key. Sent as `Authorization: Bearer <key>` by
  the mobile SDK and web SDK on `/v1/events`, `/v1/identity`, `/v1/errors`.
  Ships inside app bundles, so treat it as a build-time secret: a leak
  only lets an attacker fake this tenant's client traffic — it cannot
  reach the internal listener. Rotate on suspicion of leak.
- `internal_token` — server secret. Sent as `Authorization: Bearer <key>`
  by backend producers on `/internal/v1/events` and by DSR admin operations
  on `DELETE /internal/v1/user_attributes/...`. Never exposed to any client
  bundle; lives in backend secret stores only. The pump refuses to boot if
  a tenant's `tenant_api_key` and `internal_token` are the same value.

## Onboarding a new tenant

1. Copy `zainmart.example.jsonc` to `<newtenant>.jsonc`.
2. Set `app_id`, `tenant_api_key`, `internal_token`, `cookie_domain`,
   `cors_origins`, and destination credentials. The two keys MUST be
   different.
3. Fill `events` / `attributes` / `destinations` per SPEC §6 and §8.
4. Restart both `eventpump api` and `eventpump worker` — the registry is
   built once at boot.

On restart the worker starts an independent claim loop per
`(app_id, destination)` pair, and `RegistrySync` seeds the tenant's
`event_registry` rows (plus a safety `first_visit` entry). Migration 0009
back-filled every pre-v1.2 row with `app_id = 'zainmart'`; the DEFAULT was
dropped so future rows must specify their tenant explicitly.

## Back-compat: single-tenant zainmart

If `EP_TENANTS_DIR` is unset the process synthesises one tenant from the
env vars `EP_TENANT_API_KEY` (client key sent by SDKs), `EP_INTERNAL_TOKEN`
(server secret for `/internal/v1/*`), `EP_LEGACY_APP_ID` (defaults to
`zainmart`), `EP_TRACKING_PLAN`, `EP_COOKIE_DOMAIN`, `EP_CORS_ORIGINS`,
`EP_*_ENABLED`, and the destination endpoint/credential vars. Existing
single-tenant deployments upgrade to v1.2 by keeping their pre-existing
`EP_INTERNAL_TOKEN` and adding `EP_TENANT_API_KEY` for the SDK side.

## Adding a second tenant

Once you have a real second tenant, drop the legacy env vars and move to
`EP_TENANTS_DIR`:

1. Create `EP_TENANTS_DIR=/etc/eventpump/tenants` (or wherever ops
   chooses).
2. Move the zainmart values from env into `zainmart.jsonc`.
3. Add the new `<other>.jsonc`.
4. Remove the pre-v1.2 env vars from the systemd unit / RPM install (they
   are only consulted when `EP_TENANTS_DIR` is unset).
5. Restart `eventpump api` and `eventpump worker`.

The DSR URL is per-tenant: `DELETE /internal/v1/user_attributes/{app_id}/{user_id}`
with the tenant's `internal_token` as the bearer. The URL's `{app_id}` must
match the token's tenant — using tenant A's internal token on a tenant B
path returns 401.
