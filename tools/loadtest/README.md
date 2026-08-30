# Load testing Event Pump

Three pieces, all local:

| File | Role |
|---|---|
| `../../deploy/tenants/loadtest.jsonc` | Isolated tenant. Every destination points at the sink, so a run never reaches GA4 / Amplitude / MoEngage / Adjust. |
| `sink.py` | Stand-in for the four destinations, on `:8099`. Raw asyncio so it never becomes the bottleneck. |
| `load.py` | Ingest generator. Fresh `uuid4` per event — a reused `event_id` would be deduped and the numbers would be a lie. |

Isolation rests on `DeliveryWorker`'s claim query filtering `app_id = $4`: a
worker without `loadtest.jsonc` cannot claim these deliveries, so the systemd
instance ignores the run entirely.

## Running

```bash
python3 tools/loadtest/sink.py                    # terminal 1
./run-local.sh api                                # terminal 2 (picks up the tenant)
./run-local.sh worker                             # terminal 3, only for delivery tests

python3 tools/loadtest/load.py --path internal --duration 30 --concurrency 32
python3 tools/loadtest/load.py --path client   --duration 30 --concurrency 32
```

`--path internal` hits `/internal/v1/events` (origin `server`, no rate limiter
on the route). `--path client` hits `/v1/events` (origin `client`); the tenant
sets absurdly high rate limits so the test measures the pipeline, not the
limiter. Other flags: `--batch` (events per request, max 100), `--identities`
(registry pool size), `--public` / `--internal` (host overrides).

## Cleaning up

A run leaves millions of rows. Delete them before restarting a worker that
knows this tenant, or it will spend hours draining them:

```sql
DELETE FROM events_delivery    WHERE app_id = 'loadtest';
DELETE FROM events_outbox      WHERE app_id = 'loadtest';
DELETE FROM events_dedupe      WHERE app_id = 'loadtest';
DELETE FROM identity_registry  WHERE app_id = 'loadtest';
```
