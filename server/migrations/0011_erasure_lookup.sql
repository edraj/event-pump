-- DSR erasure fan-out (SPEC §9.7).
--
-- Erasure resolves the handles a person is known by downstream before it
-- queues anything, and that lookup is by (app_id, user_id) — a pair no
-- existing index serves. identity_registry is keyed on session_key, and its
-- only secondary indexes are on anonymous_id (0003) and app_id (0009), so
-- without this every erasure scans the tenant's whole session history.
--
-- Partial on user_id IS NOT NULL: anonymous sessions can never match a DSR
-- lookup, and they are the bulk of the table.
CREATE INDEX identity_registry_app_user_idx
    ON identity_registry (app_id, user_id)
    WHERE user_id IS NOT NULL;
