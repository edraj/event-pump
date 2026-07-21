-- Person-scoped user attribute store (SPEC §6.1). Keyed by user_id, not
-- session_key — attributes describe the person and persist across sessions,
-- devices, and logouts. Upserted partially by POST /v1/identity when the
-- request body carries an `attributes` block and a user_id is in scope;
-- deleted per-user via DELETE /internal/v1/user_attributes/{user_id} (§9.6).
--
-- `attributes` holds raw canonical values (email lowercased, phone in E.164,
-- gender in canonical enum). Per-destination hashing (GA4 user_data,
-- Adjust s2s_email) happens at send time in each sender.
--
-- `hash` = sha256(canonical_json(attributes)); when it differs from
-- `moengage_synced_hash`, the API handler enqueues a `moengage_customer`
-- delivery. The MoEngage sender writes back the hash of the payload it
-- actually sent (captured at fetch time) — never the row's current hash —
-- so a concurrent setUserAttributes landing mid-flight is still detected
-- as an out-of-sync state (SPEC §6.1).
--
-- Retention: TBD (deferred). Rows persist until DSR deletion (§9.6).
CREATE TABLE user_attributes (
    user_id              text        PRIMARY KEY,
    attributes           jsonb       NOT NULL DEFAULT '{}',
    hash                 text,
    moengage_synced_hash text,
    moengage_synced_at   timestamptz,
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now()
);
