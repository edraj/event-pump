-- Multi-tenant storage (SPEC v1.2, §11). Every domain table gains an app_id
-- column. Two PKs go composite because their identifier spaces are per-tenant:
--
--   event_registry (app_id, event_name)   -- routing differs per tenant
--   user_attributes (app_id, user_id)     -- user ids are tenant-scoped
--
-- Other tables keep their existing PK because the key is a globally-unique
-- UUID or a per-partition sequence id: adding app_id changes nothing about
-- uniqueness, only about which tenant a row belongs to.
--
-- Migration path: DEFAULT 'zainmart' back-fills every existing row into the
-- pre-existing single-tenant deployment, then the DEFAULT is dropped so all
-- future inserts must specify app_id explicitly (see emit_event() and
-- EventStore.InsertBatchAsync). Producing an event without an app_id becomes
-- a NOT NULL violation at the storage layer — no silent leak into 'zainmart'.
--
-- The worker claim index changes to (destination, app_id, next_attempt_at):
-- each tenant × destination pipeline claims its own rows and only its own.

------------------------------------------------------------------ events_outbox
ALTER TABLE events_outbox ADD COLUMN app_id text NOT NULL DEFAULT 'zainmart';
ALTER TABLE events_outbox ALTER COLUMN app_id DROP DEFAULT;

------------------------------------------------------------------ events_delivery
-- Delivery rows carry app_id explicitly so the worker claim query can filter
-- on (destination, app_id) without joining events_outbox on every poll.
ALTER TABLE events_delivery ADD COLUMN app_id text NOT NULL DEFAULT 'zainmart';
ALTER TABLE events_delivery ALTER COLUMN app_id DROP DEFAULT;

DROP INDEX IF EXISTS events_delivery_claim_idx;
CREATE INDEX events_delivery_claim_idx
    ON events_delivery (destination, app_id, next_attempt_at)
    WHERE status IN ('pending', 'failed');

------------------------------------------------------------------ events_dedupe
-- event_id is a UUID and dedupe is a global "have we ever seen this?" gate,
-- so the PK stays on event_id alone. app_id is recorded for observability
-- (which tenant minted the id) but is not part of the uniqueness key.
ALTER TABLE events_dedupe ADD COLUMN app_id text NOT NULL DEFAULT 'zainmart';
ALTER TABLE events_dedupe ALTER COLUMN app_id DROP DEFAULT;

------------------------------------------------------------------ identity_registry
-- session_key is a UUID — cross-tenant collision is impossible in practice,
-- so the PK stays on session_key alone. Filtered lookups still narrow by
-- app_id to prevent one tenant's SDK from reading another tenant's identity
-- state (that is enforced at the API layer via the auth middleware).
ALTER TABLE identity_registry ADD COLUMN app_id text NOT NULL DEFAULT 'zainmart';
ALTER TABLE identity_registry ALTER COLUMN app_id DROP DEFAULT;
CREATE INDEX identity_registry_app_idx ON identity_registry (app_id);

------------------------------------------------------------------ first_seen
-- Composite: two tenants that legitimately share the same anonymous_id
-- (extremely unlikely with UUIDv4, but the SDK never coordinates across
-- tenants so it is not impossible) each get their own first_visit.
ALTER TABLE first_seen ADD COLUMN app_id text NOT NULL DEFAULT 'zainmart';
ALTER TABLE first_seen ALTER COLUMN app_id DROP DEFAULT;
ALTER TABLE first_seen DROP CONSTRAINT first_seen_pkey;
ALTER TABLE first_seen ADD PRIMARY KEY (app_id, anonymous_id);

------------------------------------------------------------------ event_registry
-- Composite: each tenant's tracking plan is independent — same event name
-- can route differently (or not at all) per tenant.
ALTER TABLE event_registry ADD COLUMN app_id text NOT NULL DEFAULT 'zainmart';
ALTER TABLE event_registry ALTER COLUMN app_id DROP DEFAULT;
ALTER TABLE event_registry DROP CONSTRAINT event_registry_pkey;
ALTER TABLE event_registry ADD PRIMARY KEY (app_id, event_name);

------------------------------------------------------------------ user_attributes
-- Composite: user_id spaces are tenant-scoped by definition — every tenant
-- has its own account system. DSR deletion, MoEngage sync-hash, and hash
-- write-back all key on (app_id, user_id).
ALTER TABLE user_attributes ADD COLUMN app_id text NOT NULL DEFAULT 'zainmart';
ALTER TABLE user_attributes ALTER COLUMN app_id DROP DEFAULT;
ALTER TABLE user_attributes DROP CONSTRAINT user_attributes_pkey;
ALTER TABLE user_attributes ADD PRIMARY KEY (app_id, user_id);

-- error_reports already carries app_id in its PK from 0005 — no change here.
