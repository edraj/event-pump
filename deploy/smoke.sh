#!/usr/bin/env bash
# Event Pump end-to-end smoke: PostgreSQL -> AOT binaries -> web SDK IIFE in a
# headless page + a flutter-format batch + the SQL producer path -> mock
# destination servers -> assert delivery rows and identity enrichment.
#
# PostgreSQL source (pick one):
#   EP_SMOKE_USE_LOCAL_PG=1 with standard PGHOST/PGPORT/PGUSER/PGPASSWORD env
#   otherwise: docker/podman compose up (deploy/docker-compose.yml)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
DB="ep_smoke_$RANDOM"
MOCK_PORT=9700
API_PORT=9701
INTERNAL_PORT=9702
METRICS_PORT=9703
PIDS=()
COMPOSE_STARTED=0

log() { printf '\n== %s\n' "$*"; }

cleanup() {
  for pid in "${PIDS[@]:-}"; do kill "$pid" 2>/dev/null || true; done
  sleep 1
  psql_admin -c "DROP DATABASE IF EXISTS $DB WITH (FORCE)" >/dev/null 2>&1 || true
  if [[ "$COMPOSE_STARTED" == 1 ]]; then
    (cd "$ROOT/deploy" && (docker compose down || podman compose down)) >/dev/null 2>&1 || true
  fi
  rm -rf "$WORK"
}
trap cleanup EXIT

psql_admin() { PGPASSWORD="${PGPASSWORD}" psql -h "${PGHOST}" -p "${PGPORT}" -U "${PGUSER}" -d postgres -qtA "$@"; }
psql_db() { PGPASSWORD="${PGPASSWORD}" psql -h "${PGHOST}" -p "${PGPORT}" -U "${PGUSER}" -d "$DB" -qtA "$@"; }

# ---------------------------------------------------------------- postgres
if [[ "${EP_SMOKE_USE_LOCAL_PG:-0}" == 1 ]]; then
  : "${PGHOST:=localhost}" "${PGPORT:=5432}" "${PGUSER:?set PGUSER}" "${PGPASSWORD:?set PGPASSWORD}"
  log "using local PostgreSQL at $PGHOST:$PGPORT"
else
  log "starting PostgreSQL via compose"
  (cd "$ROOT/deploy" && (docker compose up -d --wait || podman compose up -d))
  COMPOSE_STARTED=1
  PGHOST=127.0.0.1 PGPORT=54329 PGUSER=eventpump PGPASSWORD=eventpump
  for _ in $(seq 1 30); do psql_admin -c "SELECT 1" >/dev/null 2>&1 && break; sleep 1; done
fi

psql_admin -c "CREATE DATABASE $DB" >/dev/null
CONN="Host=$PGHOST;Port=$PGPORT;Username=$PGUSER;Password=$PGPASSWORD;Database=$DB"

# ------------------------------------------------------------------ builds
log "publishing AOT binary"
dotnet publish "$ROOT/server/src/EventPump" -c Release -o "$WORK/bin" -v q --nologo >/dev/null
log "building web SDK (with gzip size report)"
(cd "$ROOT/sdks/web" && node scripts/build.mjs)

# ---------------------------------------------------------------- tracking plan
cat > "$WORK/plan.json" <<'PLAN'
{
  "events": {
    "product_viewed": { "origin": "client", "destinations": ["ga4", "amplitude"] },
    "screen_view": { "origin": "client", "destinations": ["amplitude"] },
    "order_placed": {
      "origin": "server",
      "destinations": ["ga4", "amplitude", "moengage", "adjust"],
      "adjust_token": "smoketok"
    },
    "first_visit": { "origin": "server", "destinations": ["moengage"] }
  }
}
PLAN

log "running migrations"
EP_DB_CONNSTRING="$CONN" \
EP_MIGRATIONS_DIR="$ROOT/server/migrations" \
EP_PRODUCER_CONTRACT="$ROOT/server/sql/producer_contract.sql" \
  "$WORK/bin/eventpump" migrate

# ------------------------------------------------------------- mock + services
log "starting mock destination servers"
MOCK_PORT=$MOCK_PORT node "$ROOT/deploy/mock-destinations.mjs" & PIDS+=($!)

COMMON_ENV=(
  "EP_DB_CONNSTRING=$CONN"
  "EP_TRACKING_PLAN=$WORK/plan.json"
  "EP_CLIENT_TOKENS=smokeapp:smoke-token"
  "EP_INTERNAL_TOKEN=smoke-internal"
  "EP_CORS_ORIGINS=http://127.0.0.1:9704"
  "EP_LISTEN=http://127.0.0.1:$API_PORT"
  "EP_INTERNAL_LISTEN=http://127.0.0.1:$INTERNAL_PORT"
  "EP_METRICS_LISTEN=http://127.0.0.1:$METRICS_PORT"
  "EP_WORKER_POLL_MS=200"
  "EP_GA4_ENABLED=true" "EP_GA4_ENDPOINT=http://127.0.0.1:$MOCK_PORT"
  "EP_GA4_API_SECRET=smoke-secret" "EP_GA4_MEASUREMENT_ID=G-SMOKE"
  "EP_AMPLITUDE_ENABLED=true" "EP_AMPLITUDE_ENDPOINT=http://127.0.0.1:$MOCK_PORT/2/httpapi"
  "EP_AMPLITUDE_API_KEY=smoke-amp-key"
  "EP_MOENGAGE_ENABLED=true" "EP_MOENGAGE_ENDPOINT=http://127.0.0.1:$MOCK_PORT"
  "EP_MOENGAGE_APP_ID=SMOKE-APP" "EP_MOENGAGE_API_KEY=smoke-moe-key"
  "EP_ADJUST_ENABLED=true" "EP_ADJUST_ENDPOINT=http://127.0.0.1:$MOCK_PORT/event"
  "EP_ADJUST_APP_TOKEN=smoke-adjust-app"
)

log "starting eventpump api + worker"
env "${COMMON_ENV[@]}" "$WORK/bin/eventpump" api & PIDS+=($!)
env "${COMMON_ENV[@]}" "$WORK/bin/eventpump" worker & PIDS+=($!)

for _ in $(seq 1 50); do
  curl -fsS "http://127.0.0.1:$API_PORT/healthz" >/dev/null 2>&1 &&
  curl -fsS "http://127.0.0.1:$METRICS_PORT/healthz" >/dev/null 2>&1 && break
  sleep 0.2
done
curl -fsS "http://127.0.0.1:$API_PORT/healthz" >/dev/null

# ------------------------------------------------- producer path c: web SDK
log "driving the web SDK IIFE in a headless page"
WEB_HEADERS=$(cd "$ROOT/sdks/web" && EP_ENDPOINT="http://127.0.0.1:$API_PORT" EP_TOKEN=smoke-token node scripts/smoke-web.mjs | tail -1)
echo "web session: $WEB_HEADERS"
WEB_SESSION=$(echo "$WEB_HEADERS" | python3 -c "import json,sys; print(json.load(sys.stdin)['X-Session-Key'])")
WEB_ANON=$(echo "$WEB_HEADERS" | python3 -c "import json,sys; print(json.load(sys.stdin)['X-Anonymous-Id'])")

# -------------------------------------- producer path c: flutter-format batch
log "posting a flutter-format batch"
FL_SESSION="018f4d5e-7b20-7abc-8def-0123456789ab"
FL_ANON="$(python3 -c 'import uuid; print(uuid.uuid4())')"
FL_EVENT="$(python3 -c 'import uuid; print(uuid.uuid4())')"
NOW="$(date -u +%Y-%m-%dT%H:%M:%S.000Z)"
curl -fsS -X POST "http://127.0.0.1:$API_PORT/v1/identity" \
  -H "Authorization: Bearer smoke-token" -H "Content-Type: application/json" \
  -H "X-Real-IP: 203.0.113.77" \
  -d "{\"session_key\":\"$FL_SESSION\",\"anonymous_id\":\"$FL_ANON\",\"session_number\":1,\"user_id\":\"smoke-user\",
       \"handles\":{\"amplitude_device_id\":\"$FL_ANON\",\"ga4_client_id\":\"$FL_ANON\",\"adjust_adid\":\"adid-smoke\"},
       \"context\":{\"os\":\"Android\",\"os_version\":\"15\",\"model\":\"Pixel 9\",\"category\":\"mobile\",\"language\":\"ar\",\"app_version\":\"9.9.9\"}}"
curl -fsS -X POST "http://127.0.0.1:$API_PORT/v1/events" \
  -H "Authorization: Bearer smoke-token" -H "Content-Type: application/json" \
  -d "{\"events\":[{\"event_id\":\"$FL_EVENT\",\"event_name\":\"screen_view\",\"occurred_at\":\"$NOW\",
       \"anonymous_id\":\"$FL_ANON\",\"session_key\":\"$FL_SESSION\",
       \"context\":{\"screen\":{\"name\":\"CheckoutScreen\"},\"engagement_time_msec\":900,\"session_number\":1,\"sdk\":{\"name\":\"event-pump-flutter\",\"version\":\"0.1.0\"}}}]}" >/dev/null
echo "flutter batch accepted"

# ------------------------------------------- producer path a: SQL contract
log "emitting order_placed via the SQL producer contract (flutter session)"
psql_db -c "SELECT emit_event('order_placed',
    p_properties   => '{\"revenue\": 9.99, \"currency\": \"IQD\", \"order_id\": \"o-smoke\"}',
    p_user_id      => 'smoke-user',
    p_anonymous_id => '$FL_ANON',
    p_session_key  => '$FL_SESSION')" >/dev/null

# ------------------------------------------- producer path b: internal HTTP
log "emitting order_placed via /internal/v1/events (web session)"
curl -fsS -X POST "http://127.0.0.1:$INTERNAL_PORT/internal/v1/events" \
  -H "Authorization: Bearer smoke-internal" -H "Content-Type: application/json" \
  -d "{\"events\":[{\"event_id\":\"$(python3 -c 'import uuid; print(uuid.uuid4())')\",\"event_name\":\"order_placed\",\"occurred_at\":\"$NOW\",
       \"anonymous_id\":\"$WEB_ANON\",\"session_key\":\"$WEB_SESSION\",\"user_id\":\"smoke-user\",
       \"properties\":{\"revenue\":5.5,\"currency\":\"IQD\",\"order_id\":\"o-web\"}}]}" >/dev/null

# ------------------------------------------------------------- assertions
log "waiting for deliveries to drain"
for _ in $(seq 1 100); do
  REMAINING=$(psql_db -c "SELECT count(*) FROM events_delivery WHERE status IN ('pending','failed')")
  [[ "$REMAINING" == 0 ]] && break
  sleep 0.3
done

assert_eq() { # actual expected label
  if [[ "$1" != "$2" ]]; then echo "FAIL: $3 (got '$1', want '$2')"; exit 1; fi
  echo "ok: $3 = $1"
}

assert_eq "$(psql_db -c "SELECT count(*) FROM events_delivery WHERE status IN ('pending','failed')")" 0 "no deliveries left pending"
assert_eq "$(psql_db -c "SELECT count(*) FROM events_delivery WHERE status = 'dead'")" 0 "no dead deliveries"
# web product_viewed -> ga4+amplitude (2); flutter screen_view -> amplitude (1);
# 2x order_placed -> ga4+amplitude+moengage+adjust (8) MINUS the web-session
# adjust delivery, which is skipped by design (web never sets adjust_adid);
# 2x first_visit (web anon + flutter anon) -> moengage (2)
assert_eq "$(psql_db -c "SELECT count(*) FROM events_delivery WHERE status = 'delivered'")" 12 "delivered rows"
assert_eq "$(psql_db -c "SELECT status || ':' || last_error FROM events_delivery WHERE status = 'skipped'")" \
  "skipped:no_adjust_adid" "web order_placed adjust delivery skipped by design (SPEC §6)"
assert_eq "$(psql_db -c "SELECT count(*) FROM events_outbox WHERE event_name = 'first_visit'")" 2 "first_visit once per anonymous_id"
assert_eq "$(psql_db -c "SELECT count(*) FROM identity_registry")" 2 "identity registry rows"
assert_eq "$(psql_db -c "SELECT client_ip FROM identity_registry WHERE session_key = '$FL_SESSION'")" "203.0.113.77" "client ip captured from X-Real-IP"

# Mock payload assertions: identity enrichment end-to-end (SPEC §12).
MOCK_PORT=$MOCK_PORT python3 - <<'PY'
import json, os, sys, urllib.request

port = os.environ["MOCK_PORT"]
reqs = json.load(urllib.request.urlopen(f"http://127.0.0.1:{port}/_requests"))

def check(label, ok):
    print(("ok: " if ok else "FAIL: ") + label)
    if not ok:
        sys.exit(1)

check("GA4 payload enriched with identify()'d ga4_client_id (web session)",
      any('/mp/collect' in r['url'] and '555.666' in r['body'] and 'o-web' in r['body'] for r in reqs))
check("GA4 payload enriched with flutter device context + ip_override",
      any('/mp/collect' in r['url'] and 'Pixel 9' in r['body'] and 'o-smoke' in r['body']
          and '203.0.113.77' in r['body'] for r in reqs))
check("Amplitude received screen_view with insert_id dedupe key",
      any('/2/httpapi' in r['url'] and '"insert_id"' in r['body'] and 'screen_view' in r['body'] for r in reqs))
check("MoEngage received first_visit",
      any('/v1/event/SMOKE-APP' in r['url'] and 'first_visit' in r['body'] for r in reqs))
check("Adjust received adid + revenue + event token",
      any(r['url'].startswith('/event') and 'adid=adid-smoke' in r['body'] and 'revenue=9.99' in r['body']
          and 'event_token=smoketok' in r['body'] for r in reqs))
check("first_visit never forwarded to GA4 (SPEC §8)",
      not any('/mp/collect' in r['url'] and 'first_visit' in r['body'] for r in reqs))
PY

echo
echo "SMOKE PASSED"
