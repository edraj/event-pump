-- Person-scoped identity lookup for server-origin events (SPEC §12).
--
-- The worker resolves identity by joining identity_registry on session_key.
-- Backend producers (POST /internal/v1/events, emit_event) know the person's
-- user_id but have no session_key to send — they are not inside a client
-- session — so the join misses and every identity-gated destination skips
-- the event: `no_amplitude_device_id`, `no_ga4_identity`, `no_adjust_adid`.
--
-- The link those events need is already recorded. setUser(id) reruns S3 with
-- the SAME session_key (SPEC §3), so the row created for the anonymous part
-- of the session is updated in place and ends up holding user_id next to
-- amplitude_device_id / ga4_client_id / adjust_adid. Resolving by user_id is
-- therefore a lookup of data we already have, not a new handle to harvest.
--
-- A person has one row per session, so the fallback takes the most recently
-- updated one -- hence updated_at in the index, which serves the ORDER BY
-- without a sort. updated_at is bumped on every upsert
-- (EventStore.UpsertIdentityAsync), so it tracks last activity rather than
-- session start, and it travels with the row so a sender can judge staleness
-- for itself (AdjustSender is the only one that does).
--
-- Partial index: rows for never-logged-in sessions carry user_id NULL and can
-- never satisfy this lookup, so they are kept out of the index entirely.
CREATE INDEX identity_registry_person_idx
    ON identity_registry (app_id, user_id, updated_at DESC)
    WHERE user_id IS NOT NULL;

-- Lock note: MigrationRunner wraps each file in a transaction, so this cannot
-- be CREATE INDEX CONCURRENTLY. The build takes a SHARE lock on
-- identity_registry and blocks /v1/identity writes while it runs — fine at one
-- row per session for this deployment's volume, but run `eventpump migrate`
-- off-peak if identity_registry has grown to millions of rows.
