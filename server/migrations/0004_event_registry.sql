-- Tracking-plan projection (SPEC §13): synced from the EP_TRACKING_PLAN file at
-- api/worker boot, so emit_event() enforces the same per-origin allowlist and
-- routing map as the HTTP endpoints. The file is the source of truth; this
-- table is a cache the SQL path can read.
CREATE TABLE event_registry (
    event_name   text        PRIMARY KEY,
    origin       text        NOT NULL CHECK (origin IN ('client', 'server')),
    destinations text[]      NOT NULL DEFAULT '{}',
    meta_name    text,
    adjust_token text,
    updated_at   timestamptz NOT NULL DEFAULT now()
);

-- first_visit must always exist so emit_event's internal emission can never
-- fail a producer's business transaction; its routing comes from the tracking
-- plan (default: moengage only — never GA4/Amplitude, SPEC §8).
INSERT INTO event_registry (event_name, origin, destinations)
VALUES ('first_visit', 'server', '{}')
ON CONFLICT (event_name) DO NOTHING;
