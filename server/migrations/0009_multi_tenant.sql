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
-- Composite: dedupe is a per-tenant "have we ever seen this event_id?" gate,
-- not a global one. UUIDv4 collisions across tenants are astronomically
-- unlikely in practice, but the composite PK makes cross-tenant suppression
-- structurally impossible rather than probabilistically-so — and matches
-- the signed-off design in DESIGN-multi-tenant.md §3 + SPEC §11.
ALTER TABLE events_dedupe ADD COLUMN app_id text NOT NULL DEFAULT 'zainmart';
ALTER TABLE events_dedupe ALTER COLUMN app_id DROP DEFAULT;
ALTER TABLE events_dedupe DROP CONSTRAINT events_dedupe_pkey;
ALTER TABLE events_dedupe ADD PRIMARY KEY (app_id, event_id);

------------------------------------------------------------------ identity_registry
-- Composite: session_key is a UUID and honest SDKs never collide, but a
-- misconfigured / malicious tenant B can post an identity carrying a
-- session_key that already belongs to tenant A. Without app_id in the PK,
-- the ON CONFLICT upsert (EventStore.UpsertIdentityAsync) overwrites A's
-- row with B's data — the row keeps app_id='A' but every identity handle
-- becomes B's, so B's later events pick up A's ga4_client_id / adjust_adid
-- / client_ip on the worker's session_key join and ship them to B's
-- destination accounts. Composite key blocks that cross-tenant overwrite.
ALTER TABLE identity_registry ADD COLUMN app_id text NOT NULL DEFAULT 'zainmart';
ALTER TABLE identity_registry ALTER COLUMN app_id DROP DEFAULT;
ALTER TABLE identity_registry DROP CONSTRAINT identity_registry_pkey;
ALTER TABLE identity_registry ADD PRIMARY KEY (app_id, session_key);
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
