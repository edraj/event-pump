#!/usr/bin/env python3
"""Ingest load generator for Event Pump.

Every event carries a fresh uuid4 event_id — the server dedupes on
(app_id, event_id), so a replayed id would collapse the whole run into one
stored row and the numbers would be a lie.

Two paths, matching the two producer paths in SPEC §8:

  --path internal   POST :8091/internal/v1/events   origin=server, no rate limiter
  --path client     POST :8090/v1/events            origin=client, rate limited

Client-origin events require an anonymous_id, and every sender needs a handle
off the identity registry (ga4_client_id, amplitude_device_id, adjust_adid,
moengage_customer_id) or the delivery is skipped rather than sent.  So both
paths pre-register a pool of identities and draw from it, which is also what
makes the delivery side of the run realistic.

    python3 tools/loadtest/load.py --path internal --duration 30 --concurrency 32
"""
import argparse
import asyncio
import json
import random
import statistics
import time
import uuid

import aiohttp

CLIENT_KEY = "loadtest_client_2f1c9a4e5b8d47a3"
INTERNAL_KEY = "loadtest_server_7d3e6b1f0c92485a"

# (path suffix, credential, event name); the host comes from --public/--internal.
PATHS = {
    "internal": ("/internal/v1/events", INTERNAL_KEY, "lt_order_completed"),
    "client":   ("/v1/events",          CLIENT_KEY,   "lt_screen_view"),
}


def hex32():
    return uuid.uuid4().hex


async def register_identities(session, public, count):
    """Seed the identity registry; returns [(session_key, anonymous_id), ...]."""
    pool = []

    async def one():
        session_key, anonymous_id = str(uuid.uuid4()), str(uuid.uuid4())
        body = {
            "session_key": session_key,
            "anonymous_id": anonymous_id,
            "session_number": 1,
            "user_id": f"lt-user-{uuid.uuid4().hex[:12]}",
            "handles": {
                "ga4_client_id": f"{random.randint(10**9, 10**10)}.{int(time.time())}",
                "amplitude_device_id": str(uuid.uuid4()),
                "adjust_adid": hex32(),
                "moengage_customer_id": f"lt-{uuid.uuid4().hex[:12]}",
            },
            "context": {"os": "android", "os_version": "14", "app_version": "1.0.0"},
        }
        async with session.post(
            f"{public}/v1/identity",
            json=body,
            headers={"Authorization": f"Bearer {CLIENT_KEY}",
                     "X-Real-IP": f"10.{random.randint(0,255)}.{random.randint(0,255)}.{random.randint(1,254)}"},
        ) as response:
            if response.status not in (200, 204):
                raise SystemExit(
                    f"identity registration failed: {response.status} {await response.text()}")
        pool.append((session_key, anonymous_id))

    await asyncio.gather(*(one() for _ in range(count)))
    return pool


def make_batch(event_name, path, pool, batch_size, counter):
    now = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    events = []
    for _ in range(batch_size):
        session_key, anonymous_id = random.choice(pool)
        seq = next(counter)
        event = {
            "event_id": str(uuid.uuid4()),
            "event_name": event_name,
            "occurred_at": now,
            "session_key": session_key,
            "anonymous_id": anonymous_id,
        }
        if path == "internal":
            event["properties"] = {
                "order_id": f"LT-{seq}", "value": round(random.uniform(1, 500), 2),
                "currency": "IQD", "seq": seq,
            }
        else:
            event["properties"] = {"screen_name": random.choice(
                ["home", "search", "cart", "profile"]), "seq": seq}
            event["context"] = {"screen": "home", "session_number": 1}
        events.append(event)
    return {"events": events}


async def worker(session, url, token, event_name, path, pool, batch_size,
                 deadline, latencies, statuses, accepted, counter):
    headers = {"Authorization": f"Bearer {token}", "Content-Type": "application/json"}
    while time.monotonic() < deadline:
        payload = json.dumps(make_batch(event_name, path, pool, batch_size, counter))
        started = time.perf_counter()
        try:
            async with session.post(url, data=payload, headers=headers) as response:
                body = await response.read()
                statuses[response.status] += 1
                if response.status == 200:
                    accepted[0] += json.loads(body).get("accepted", 0)
        except Exception as exc:                      # noqa: BLE001 — any failure is a data point
            statuses[type(exc).__name__] += 1
        latencies.append((time.perf_counter() - started) * 1000)


def percentile(values, p):
    if not values:
        return float("nan")
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, int(len(ordered) * p / 100))]


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--path", choices=PATHS, default="internal")
    ap.add_argument("--duration", type=float, default=30.0)
    ap.add_argument("--concurrency", type=int, default=32)
    ap.add_argument("--batch", type=int, default=50, help="events per request (max 100)")
    ap.add_argument("--identities", type=int, default=200)
    ap.add_argument("--public", default="http://127.0.0.1:8090", help="EP_LISTEN")
    ap.add_argument("--internal", default="http://127.0.0.1:8091", help="EP_INTERNAL_LISTEN")
    args = ap.parse_args()

    if args.batch > 100:
        raise SystemExit("batch must be <= 100 (EventValidation.MaxBatchSize)")

    suffix, token, event_name = PATHS[args.path]
    url = (args.internal if args.path == "internal" else args.public) + suffix
    latencies, statuses, accepted = [], {}, [0]
    counter = iter(range(1, 1 << 62))

    connector = aiohttp.TCPConnector(limit=args.concurrency * 2, force_close=False)
    timeout = aiohttp.ClientTimeout(total=30)
    async with aiohttp.ClientSession(connector=connector, timeout=timeout) as session:
        print(f"registering {args.identities} identities…", flush=True)
        pool = await register_identities(session, args.public, args.identities)

        print(f"path={args.path} url={url}", flush=True)
        print(f"concurrency={args.concurrency} batch={args.batch} "
              f"duration={args.duration}s", flush=True)

        class Counter(dict):
            def __missing__(self, key):
                return 0
        statuses = Counter()

        started = time.monotonic()
        deadline = started + args.duration
        await asyncio.gather(*(
            worker(session, url, token, event_name, args.path, pool, args.batch,
                   deadline, latencies, statuses, accepted, counter)
            for _ in range(args.concurrency)))
        elapsed = time.monotonic() - started

    requests = len(latencies)
    # A run can finish with nothing measured -- --duration 0, or every worker
    # erroring out. The status codes collected are exactly what explains why, so
    # the summary must survive to print them rather than dying in the stats.
    rate = (lambda n: f"{n/elapsed:,.0f}/s") if elapsed > 0 else (lambda n: "n/a")
    print("\n" + "=" * 58)
    print(f"elapsed            {elapsed:8.2f} s")
    print(f"requests           {requests:8}   ({rate(requests)})")
    print(f"events accepted    {accepted[0]:8}   ({rate(accepted[0])})")
    if latencies:
        print(f"latency  mean      {statistics.fmean(latencies):8.1f} ms")
        print(f"         p50       {percentile(latencies, 50):8.1f} ms")
        print(f"         p95       {percentile(latencies, 95):8.1f} ms")
        print(f"         p99       {percentile(latencies, 99):8.1f} ms")
        print(f"         max       {max(latencies):8.1f} ms")
    else:
        print("latency            no requests completed")
    print(f"status codes       {dict(statuses)}")
    print("=" * 58)


if __name__ == "__main__":
    asyncio.run(main())
