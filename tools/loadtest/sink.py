#!/usr/bin/env python3
"""Local destination sink for load tests.

Stands in for GA4 / Amplitude / MoEngage / Adjust so a run never leaves the
box.  Raw asyncio rather than a framework: the sink must never be the
bottleneck the test ends up measuring.

Route prefixes match what each sender builds:
    /ga4/mp/collect?...        Ga4Sender        -> 204
    /amplitude                 AmplitudeSender  -> 200
    /moengage/v1/event/<id>    MoEngageSender   -> 200
    /adjust                    AdjustSender     -> 200

Adjust deliberately gets 200, never 202: AdjustSender maps 202 to a dead
delivery ("accepted transport, discarded data").

    python3 tools/loadtest/sink.py [--port 8099] [--latency-ms 0]
"""
import argparse
import asyncio
import collections
import signal
import time

COUNTS = collections.Counter()

RESPONSES = {
    "ga4": b"HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n",
    "amplitude": b'HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n'
                 b'Content-Length: 15\r\n\r\n{"code":200}   ',
    "moengage": b'HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n'
                b'Content-Length: 15\r\n\r\n{"status":"ok"}',
    "adjust": b'HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n'
              b'Content-Length: 15\r\n\r\n{"status":"OK"}',
}
NOT_FOUND = b"HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n"


async def handle(reader, writer, latency):
    try:
        while True:
            head = await reader.readuntil(b"\r\n\r\n")
            if not head:
                break
            line, _, rest = head.partition(b"\r\n")
            path = line.split(b" ")[1].decode("latin-1", "replace") if b" " in line else "/"

            length = 0
            for header in rest.split(b"\r\n"):
                if header[:15].lower() == b"content-length:":
                    length = int(header.split(b":")[1].strip())
                    break
            if length:
                await reader.readexactly(length)

            key = path.lstrip("/").split("/")[0].split("?")[0]
            COUNTS[key] += 1
            if latency:
                await asyncio.sleep(latency)
            writer.write(RESPONSES.get(key, NOT_FOUND))
            await writer.drain()
    except (asyncio.IncompleteReadError, ConnectionResetError, BrokenPipeError):
        pass
    finally:
        writer.close()


async def report():
    started, last = time.monotonic(), 0
    while True:
        await asyncio.sleep(5)
        total = sum(COUNTS.values())
        rate = (total - last) / 5
        last = total
        breakdown = " ".join(f"{k}={v}" for k, v in sorted(COUNTS.items()))
        print(f"[{time.monotonic()-started:6.1f}s] {total:>8} received  "
              f"{rate:>8.0f}/s   {breakdown}", flush=True)


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8099)
    ap.add_argument("--latency-ms", type=float, default=0.0,
                    help="simulate a slow destination")
    args = ap.parse_args()

    latency = args.latency_ms / 1000.0
    server = await asyncio.start_server(
        lambda r, w: handle(r, w, latency), "127.0.0.1", args.port, backlog=2048)
    print(f"sink listening on 127.0.0.1:{args.port} "
          f"(latency {args.latency_ms}ms) — ctrl-c to stop", flush=True)

    stop = asyncio.Event()
    loop = asyncio.get_running_loop()
    for sig in (signal.SIGINT, signal.SIGTERM):
        loop.add_signal_handler(sig, stop.set)

    reporter = asyncio.create_task(report())
    async with server:
        await stop.wait()
    reporter.cancel()
    print("\nfinal:", dict(COUNTS), flush=True)


if __name__ == "__main__":
    asyncio.run(main())
