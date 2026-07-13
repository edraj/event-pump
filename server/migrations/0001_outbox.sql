-- Canonical event outbox (SPEC §1, §11). Partitioned daily by received_at;
-- partitions are pre-created by `eventpump migrate` and the worker maintenance
-- timer (ep_ensure_partitions in 0002), and dropped per the retention policy.
CREATE TABLE events_outbox (
    id            bigint      GENERATED ALWAYS AS IDENTITY,
    event_id      uuid        NOT NULL,
    event_name    text        NOT NULL,
    origin        text        NOT NULL CHECK (origin IN ('client', 'server')),
    occurred_at   timestamptz NOT NULL,
    received_at   timestamptz NOT NULL DEFAULT now(),
    user_id       text,
    anonymous_id  uuid,
    session_key   uuid,
    properties    jsonb       NOT NULL DEFAULT '{}',
    context       jsonb       NOT NULL DEFAULT '{}',
    -- Unique indexes on a partitioned table must include the partition key;
    -- (received_at, id) is also exactly how delivery rows reference us.
    PRIMARY KEY (received_at, id)
) PARTITION BY RANGE (received_at);

-- Global dedupe guarantee for event_id (SPEC §11): a partitioned table cannot
-- carry a unique index on event_id alone, so the guarantee lives in this small
-- side table. Rows are pruned by the worker retention timer after
-- EP_RETENTION_DAYS (default 30) — far beyond the 24 h SDK retry window plus
-- the 7 d occurred_at acceptance window.
CREATE TABLE events_dedupe (
    event_id    uuid        PRIMARY KEY,
    received_at timestamptz NOT NULL DEFAULT now()
);
