-- Per-destination delivery state (SPEC §11), fanned out at insert per the
-- routing map. Partitioned identically to events_outbox. The composite PK
-- provides UNIQUE (event_ref, destination): event_ref embeds its day, so
-- within-partition uniqueness is global uniqueness.
--
-- No FK to events_outbox by design: outbox + delivery rows are written in the
-- same transaction, and day partitions are dropped as a pair by the retention
-- job — the FK would only add insert cost and complicate partition drops.
CREATE TABLE events_delivery (
    event_ref       bigint      NOT NULL,
    received_at     timestamptz NOT NULL,
    destination     text        NOT NULL,
    status          text        NOT NULL DEFAULT 'pending'
                    CHECK (status IN ('pending', 'delivered', 'failed', 'dead', 'skipped')),
    attempts        int         NOT NULL DEFAULT 0,
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    last_error      text,
    delivered_at    timestamptz,
    PRIMARY KEY (received_at, event_ref, destination)
) PARTITION BY RANGE (received_at);

-- Serves the worker claim query (SPEC §11) and nothing else:
--   WHERE destination = $1 AND status IN ('pending','failed')
--     AND next_attempt_at <= now()
--   ORDER BY next_attempt_at LIMIT n FOR UPDATE SKIP LOCKED
-- Partial: delivered/dead/skipped rows (the vast majority at steady state)
-- never enter the index.
CREATE INDEX events_delivery_claim_idx
    ON events_delivery (destination, next_attempt_at)
    WHERE status IN ('pending', 'failed');

-- Pre-creates one day's partition pair (idempotent). Called by `eventpump
-- migrate` (yesterday..+3 days) and by the worker maintenance timer.
CREATE FUNCTION ep_ensure_partitions(p_day date) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE
    v_suffix text := to_char(p_day, 'YYYYMMDD');
BEGIN
    EXECUTE format(
        'CREATE TABLE IF NOT EXISTS events_outbox_%s PARTITION OF events_outbox FOR VALUES FROM (%L) TO (%L)',
        v_suffix, p_day, p_day + 1);
    EXECUTE format(
        'CREATE TABLE IF NOT EXISTS events_delivery_%s PARTITION OF events_delivery FOR VALUES FROM (%L) TO (%L)',
        v_suffix, p_day, p_day + 1);
END;
$$;
