#!/usr/bin/env bash
#
# Event Pump health monitor: units, /healthz, delivery metrics. Mails on a
# change of state — once when something breaks, once when it recovers.
#
# Three failures, and the third is the reason this exists:
#   1. process or host dead   — unit check + /healthz
#   2. up but blind           — /healthz answers 503 (db_unreachable)
#   3. up but not delivering  — outbox_pending / circuit_state
# Nothing ages out a waiting delivery, so a stuck worker builds a backlog in
# silence while "is it up" reports healthy throughout.
#
# Install and settings: see monitor.conf.example.
#
#   (no flag)     run on a schedule; mails only when the state CHANGES
#   --report      run once and always mail — for a daily check, where a
#                 missing mail is itself the signal
#   --show        print the checks, mail nothing
#   --test-mail   run the checks and mail them, to prove the transport works
#
# Exit: 0 all clear, 1 problems found, 2 could not run the checks.

set -uo pipefail

CONF=${EP_MONITOR_CONF:-/etc/eventpump/monitor.conf}
# shellcheck source=/dev/null
[ -r "$CONF" ] && . "$CONF"

# ---------------------------------------------------------------- settings

API_HEALTH=${API_HEALTH:-http://127.0.0.1:8080/healthz}
INTERNAL_HEALTH=${INTERNAL_HEALTH:-http://127.0.0.1:8081/healthz}
WORKER_HEALTH=${WORKER_HEALTH:-http://127.0.0.1:9090/healthz}
WORKER_METRICS=${WORKER_METRICS:-http://127.0.0.1:9090/metrics}

# `-` not `:-`, so UNITS="" genuinely means "watch no units" rather than
# silently falling back to the default.
UNITS=${UNITS-"eventpump-api eventpump-worker"}

# Deliveries waiting across all pipelines before this counts as a problem.
PENDING_WARN=${PENDING_WARN:-5000}

# A breaker trip is normal and clears itself; only a sustained one matters.
BREAKER_WARN_MIN=${BREAKER_WARN_MIN:-10}

CURL_TIMEOUT=${CURL_TIMEOUT:-5}

STATE_DIR=${STATE_DIR:-/var/lib/eventpump-monitor}
HOSTNAME_LABEL=${HOSTNAME_LABEL:-$(hostname -f 2>/dev/null || hostname)}

MAIL_TO=${MAIL_TO:-}
MAIL_FROM=${MAIL_FROM:-}
SMTP_URL=${SMTP_URL:-smtp://smtp.office365.com:587}
SMTP_USER=${SMTP_USER:-$MAIL_FROM}
SMTP_PASS=${SMTP_PASS:-}

# Dead man's switch (healthchecks.io or similar). See the ping block at the end.
HEARTBEAT_URL=${HEARTBEAT_URL:-}

mkdir -p "$STATE_DIR" 2>/dev/null || {
  echo "cannot create $STATE_DIR" >&2
  exit 2
}

# KEYS decides whether to mail and carries no changing numbers; MSGS is what a
# human reads. Keyed on the message instead, a drifting backlog figure would
# mail every single minute.
PROBLEM_KEYS=()
PROBLEM_MSGS=()
DETAILS=()

note() { DETAILS+=("$1"); }
problem() {
  PROBLEM_KEYS+=("$1")
  PROBLEM_MSGS+=("$2")
  DETAILS+=("FAIL  $2")
}

# ------------------------------------------------------------------ checks

# `is-active` alone is not enough: under Restart=always a unit that aborts on
# startup still reads `active` for the instant the process is alive, so a
# crash loop answers "active" to roughly every other check. The restart
# counter only climbs while a unit is failing, so comparing it with the
# previous run catches what is-active misses. A counter that went DOWN is a
# manual restart (systemd resets it), not a loop.
check_units() {
  local seen="$STATE_DIR/restarts"
  : >"$seen.new"
  for unit in $UNITS; do
    local state restarts previous
    state=$(systemctl is-active "$unit" 2>/dev/null)
    restarts=$(systemctl show -p NRestarts --value "$unit" 2>/dev/null)
    restarts=${restarts:-0}
    previous=$(awk -F'\t' -v k="$unit" '$1 == k { print $2 }' "$seen" 2>/dev/null)
    printf '%s\t%s\n' "$unit" "$restarts" >>"$seen.new"

    if [ "$state" != "active" ]; then
      problem "unit:$unit" "unit $unit is ${state:-missing}"
    elif [ -n "$previous" ] && [ "$restarts" -gt "$previous" ]; then
      problem "crashloop:$unit" \
        "unit $unit restarted $((restarts - previous)) time(s) since the last check — crash looping"
    else
      note "ok    unit $unit active (restarts $restarts)"
    fi
  done
  mv "$seen.new" "$seen"
}

# /healthz is 200 when the process is up AND can reach PostgreSQL, 503
# {"status":"db_unreachable"} when it cannot, and silent when the process is
# gone — three different answers this has to keep apart.
check_health() {
  local label=$1 url=$2 response http payload
  # No -f: a 503 here is an answer worth reading, not a curl failure.
  # %{http_code} lands on its own line, and is "000" if nothing replied.
  response=$(curl -sS --max-time "$CURL_TIMEOUT" -w '\n%{http_code}' "$url" 2>/dev/null)
  http=${response##*$'\n'}
  payload=${response%$'\n'*}

  case "$http" in
  200)
    note "ok    $label healthy"
    ;;
  "" | 000)
    problem "health:$label" "$label not answering ($url)"
    ;;
  *)
    problem "health:$label" "$label returned HTTP $http ${payload:-}"
    ;;
  esac
}

check_metrics() {
  local metrics
  metrics=$(curl -fsS --max-time "$CURL_TIMEOUT" "$WORKER_METRICS" 2>/dev/null)
  if [ -z "$metrics" ]; then
    problem "metrics" "worker metrics unavailable ($WORKER_METRICS)"
    return
  fi

  # "No data" is not "zero". The worker publishes outbox_pending only for
  # destinations with a registered sender, so rows queued for one without —
  # a disabled vendor, a destination misspelled in a tracking plan — produce
  # no series at all, and summing nothing would read as a clean zero.
  local series pending
  series=$(printf '%s\n' "$metrics" | grep -c '^outbox_pending{') || true
  series=${series:-0}
  if [ "$series" -eq 0 ]; then
    note "warn  delivery backlog not published (no registered senders) — queued rows are invisible here"
  else
    pending=$(printf '%s\n' "$metrics" |
      awk '/^outbox_pending\{/ { total += $NF } END { printf "%d", total + 0 }')
    if [ "$pending" -ge "$PENDING_WARN" ]; then
      problem "backlog" "delivery backlog is $pending (threshold $PENDING_WARN)"
    else
      note "ok    delivery backlog $pending across $series pipeline(s)"
    fi
  fi

  # Remembered with the time it was first seen open, so a brief vendor hiccup
  # never mails anybody.
  local open_now
  open_now=$(printf '%s\n' "$metrics" |
    awk '/^circuit_state\{/ && $NF == 1 {
           match($0, /\{[^}]*\}/)
           print substr($0, RSTART + 1, RLENGTH - 2)
         }')

  local seen_file="$STATE_DIR/breakers"
  local now cutoff
  now=$(date +%s)
  cutoff=$((BREAKER_WARN_MIN * 60))
  : >"$seen_file.new"

  if [ -n "$open_now" ]; then
    while IFS= read -r labels; do
      [ -z "$labels" ] && continue
      local first
      first=$(awk -F'\t' -v k="$labels" '$1 == k { print $2 }' "$seen_file" 2>/dev/null)
      [ -z "$first" ] && first=$now
      printf '%s\t%s\n' "$labels" "$first" >>"$seen_file.new"
      local open_for=$(((now - first) / 60))
      if [ $((now - first)) -ge "$cutoff" ]; then
        problem "circuit:$labels" "circuit open ${open_for}m: $labels"
      else
        note "warn  circuit open ${open_for}m (under ${BREAKER_WARN_MIN}m): $labels"
      fi
    done <<<"$open_now"
  fi
  mv "$seen_file.new" "$seen_file"

  [ -z "$open_now" ] && note "ok    no circuit breakers open"
}

# ------------------------------------------------------------------- mail

# python3, not curl: Fedora and RHEL build curl WITHOUT the smtp protocol, so
# `curl --url smtp://…` fails with `Protocol "smtp" not supported`.
#
# Credentials travel in the environment, never argv — /proc/<pid>/cmdline is
# world-readable.
#
# An empty SMTP_PASS skips the login step. That is the normal case on a
# corporate network: an internal relay takes unauthenticated submission on
# port 25, and on a Microsoft 365 tenant it is usually the only route open,
# since SMTP AUTH is off by default and app passwords are commonly blocked.
#
# Returns 0 sent, 1 no channel configured, 2 configured but the send failed.
# The caller needs 1 and 2 apart: see the state write in main.
send_mail() {
  local subject=$1 body=$2
  if [ -z "$MAIL_TO" ] || [ -z "$MAIL_FROM" ]; then
    echo "mail not configured (MAIL_TO / MAIL_FROM); would have sent:" >&2
    echo "$subject" >&2
    return 1
  fi
  if ! command -v python3 >/dev/null; then
    echo "python3 not found — cannot send mail" >&2
    return 2
  fi

  EP_SUBJECT="$subject" EP_BODY="$body" \
  EP_TO="$MAIL_TO" EP_FROM="$MAIL_FROM" \
  EP_URL="$SMTP_URL" EP_USER="$SMTP_USER" EP_PASS="$SMTP_PASS" \
    python3 - <<'PY'
import os, smtplib, ssl, sys
from email.message import EmailMessage
from urllib.parse import urlparse

url = urlparse(os.environ["EP_URL"])
# smtps:// (implicit TLS, usually 465) vs smtp:// (STARTTLS, usually 587).
implicit = url.scheme == "smtps"
host = url.hostname or "smtp.office365.com"
port = url.port or (465 if implicit else 587)

msg = EmailMessage()
msg["From"] = os.environ["EP_FROM"]
msg["To"] = os.environ["EP_TO"]
msg["Subject"] = os.environ["EP_SUBJECT"]
msg.set_content(os.environ["EP_BODY"])

try:
    context = ssl.create_default_context()
    if implicit:
        server = smtplib.SMTP_SSL(host, port, timeout=30, context=context)
    else:
        server = smtplib.SMTP(host, port, timeout=30)
    with server:
        server.ehlo()
        # A relay on port 25 usually offers no STARTTLS, and demanding it
        # would fail against the one route a locked-down tenant leaves open.
        if not implicit and server.has_extn("starttls"):
            server.starttls(context=context)
            server.ehlo()
        password = os.environ.get("EP_PASS", "")
        if password:
            server.login(os.environ["EP_USER"], password)
        server.send_message(msg)
except Exception as exc:
    print(f"smtp send failed: {type(exc).__name__}: {exc}", file=sys.stderr)
    sys.exit(2)
PY
}

report() {
  printf 'host: %s\n' "$HOSTNAME_LABEL"
  printf 'time: %s (%s UTC)\n\n' "$(date -Is)" "$(date -u +%H:%M:%S)"
  printf '%s\n' "${DETAILS[@]}"
}

# -------------------------------------------------------------------- main

case "${1:-}" in
--test-mail)
  # Checks first, so the test mail carries a real report and shows what a live
  # alert will look like — not just that the transport works.
  check_units
  check_health "api (public)" "$API_HEALTH"
  check_health "api (internal)" "$INTERNAL_HEALTH"
  check_health "worker" "$WORKER_HEALTH"
  check_metrics
  if send_mail "[eventpump] test from $HOSTNAME_LABEL" "$(report)"; then
    echo "test mail sent to $MAIL_TO"
    exit 0
  fi
  echo "test mail FAILED — see the error above" >&2
  exit 2
  ;;
--show)
  check_units
  check_health "api (public)" "$API_HEALTH"
  check_health "api (internal)" "$INTERNAL_HEALTH"
  check_health "worker" "$WORKER_HEALTH"
  check_metrics
  report
  exit $(( ${#PROBLEM_KEYS[@]} > 0 ? 1 : 0 ))
  ;;
--report)
  # For a scheduled check that runs once a day rather than once a minute.
  # Mails EVERY time, state unchanged or not, because at that cadence silence
  # is ambiguous — nothing arriving could mean "all well" or "the job never
  # ran", and those need telling apart. A mail that fails to arrive is then
  # itself the signal. The edge-triggered default is right the other way
  # round: minute by minute, a daily digest would be 1440 mails.
  check_units
  check_health "api (public)" "$API_HEALTH"
  check_health "api (internal)" "$INTERNAL_HEALTH"
  check_health "worker" "$WORKER_HEALTH"
  check_metrics
  if [ ${#PROBLEM_KEYS[@]} -gt 0 ]; then
    subject="[eventpump] $HOSTNAME_LABEL: ${#PROBLEM_KEYS[@]} problem(s) — ${PROBLEM_MSGS[0]}"
  else
    subject="[eventpump] $HOSTNAME_LABEL: all clear"
  fi
  send_mail "$subject" "$(report)" || report
  exit $(( ${#PROBLEM_KEYS[@]} > 0 ? 1 : 0 ))
  ;;
esac

check_units
check_health "api (public)" "$API_HEALTH"
check_health "api (internal)" "$INTERNAL_HEALTH"
check_health "worker" "$WORKER_HEALTH"
check_metrics

STATE_FILE="$STATE_DIR/last_state"
PREVIOUS=$(cat "$STATE_FILE" 2>/dev/null || echo ok)

if [ ${#PROBLEM_KEYS[@]} -gt 0 ]; then
  CURRENT="fail"
  # From the KEYS, so a moving number does not re-alert — but a NEW kind of
  # failure appearing while an old one is unresolved still does.
  SIGNATURE="fail:$(printf '%s|' "${PROBLEM_KEYS[@]}")"
else
  CURRENT="ok"
  SIGNATURE="ok"
fi

if [ "$SIGNATURE" != "$PREVIOUS" ]; then
  if [ "$CURRENT" = "fail" ]; then
    send_mail "[eventpump] PROBLEM on $HOSTNAME_LABEL: ${PROBLEM_MSGS[0]}" "$(report)"
  else
    send_mail "[eventpump] recovered on $HOSTNAME_LABEL" "$(report)"
  fi
  # Record the state only when the alert reached something, or when there is
  # no channel to reach. A failed send (2) leaves it untouched so the next run
  # retries: otherwise one unreachable mail server buries the alert for good,
  # and the monitor goes quiet exactly when it is needed. Seen for real — a
  # Spamhaus rejection swallowed a live "api is down" alert.
  case $? in
  0 | 1) printf '%s' "$SIGNATURE" >"$STATE_FILE" ;;
  *) echo "alert not delivered — leaving state unchanged so the next run retries" >&2 ;;
  esac
fi

# Dead man's switch, covering the one failure no local check can report:
#   pass -> ping the URL   fail -> ping <url>/fail   host dead -> silence
# Outbound HTTPS only, so it works from an IP that cannot send mail.
if [ -n "$HEARTBEAT_URL" ]; then
  if [ ${#PROBLEM_KEYS[@]} -eq 0 ]; then
    curl -fsS --max-time "$CURL_TIMEOUT" -o /dev/null "$HEARTBEAT_URL" 2>/dev/null
  else
    curl -fsS --max-time "$CURL_TIMEOUT" -o /dev/null \
      --data-binary "$(report)" "${HEARTBEAT_URL%/}/fail" 2>/dev/null
  fi
fi

exit $(( ${#PROBLEM_KEYS[@]} > 0 ? 1 : 0 ))
