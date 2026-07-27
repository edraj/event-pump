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

Per tenant (`app_id`, tokens, plan, rate limits, destination endpoints and
credentials, cookie domain, CORS origins, per-destination attribute gates)
all live in the tenant's JSON file.

## Onboarding a new tenant

1. Copy `zainmart.example.jsonc` to `<newtenant>.jsonc`.
2. Set `app_id`, `client_tokens`, `internal_token`, `cookie_domain`,
   `cors_origins`, and destination credentials.
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
pre-v1.2 env vars (`EP_CLIENT_TOKENS`, `EP_INTERNAL_TOKEN`, `EP_TRACKING_PLAN`,
`EP_COOKIE_DOMAIN`, `EP_CORS_ORIGINS`, `EP_*_ENABLED`, and the destination
endpoint/credential vars). The synthesised `app_id` is taken from
`EP_CLIENT_TOKENS` (all tokens must map to the same id) or defaults to
`zainmart`. This means an existing single-tenant deployment can upgrade to
v1.2 without touching env or writing a tenant file; the migration is
required, everything else keeps working.

## Adding a second tenant

Once you have a real second tenant, drop the legacy env vars and move to
`EP_TENANTS_DIR`:

1. Create `EP_TENANTS_DIR=/etc/eventpump/tenants`.
2. Move the zainmart values from env into `zainmart.jsonc`.
3. Add the new `<other>.jsonc`.
4. Remove the pre-v1.2 env vars from the systemd unit / RPM install (they
   are only consulted when `EP_TENANTS_DIR` is unset).
5. Restart `eventpump api` and `eventpump worker`.

The DSR URL is per-tenant: `DELETE /internal/v1/user_attributes/{app_id}/{user_id}`
with the tenant's `internal_token` as the bearer. Using tenant A's token to
DSR-delete tenant B's user returns 401.
