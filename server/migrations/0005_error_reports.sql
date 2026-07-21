-- Client error/crash reports (thin first-party error visibility; SPEC follow-up
-- 2026-07-14). Deliberately NOT events: reports arrive on a dedicated endpoint
-- with its own rate bucket, bypass the SDK queue (they must work when the SDK
-- itself is broken), and dedupe by stack hash per app per day — not event_id.
-- Small table, plain deletes on the retention timer; no partitioning needed.
CREATE TABLE error_reports (
    day         date        NOT NULL DEFAULT current_date,
    app_id      text        NOT NULL,
    stack_hash  text        NOT NULL,
    kind        text,
    message     text,
    stack       text,
    first_seen  timestamptz NOT NULL DEFAULT now(),
    last_seen   timestamptz NOT NULL DEFAULT now(),
    occurrences int         NOT NULL DEFAULT 1,
    sample      jsonb       NOT NULL DEFAULT '{}',
    PRIMARY KEY (day, app_id, stack_hash)
);
