-- ============================================================================
-- Event Pump — SQL producer contract (SPEC §10, v1.2 multi-tenant)
--
-- Platform services sharing this database emit server-fact events by calling
-- emit_event(...) INSIDE their business transaction: the event becomes durable
-- iff the transaction commits. This is the PRIMARY server-fact path — do NOT
-- call POST /internal/v1/events from services that can reach this database.
--
--   PERFORM emit_event(
--       'zainmart',                        -- REQUIRED: tenant this event belongs to
--       'order_placed',
--       p_properties   => jsonb_build_object('order_id', v_order_id,
--                                            'revenue', v_total,
--                                            'currency', 'IQD'),
--       p_user_id      => v_customer_id,
--       p_anonymous_id => v_anonymous_id,  -- from X-Anonymous-Id when present
--       p_session_key  => v_session_key);  -- from X-Session-Key when present
--
-- Returns the event_id, or NULL when p_event_id was already ingested (no-op).
-- Raises when p_event_name is not a registered origin='server' name IN THAT
-- TENANT: an unknown name is a deploy-time bug and must fail loudly, in the
-- producer's own transaction. Raises when the tenant does not exist in the
-- event_registry — silent-drop of an unknown tenant would let a code bug in
-- one service leak events across tenant boundaries with no visible signal.
--
-- Tier-2 (X-Event header pattern, SPEC §8): platform endpoints that receive
-- X-Event / X-Session-Key / X-Anonymous-Id headers pass them straight through
-- along with their own tenant id (which they know statically — a Zainmart
-- service always emits for 'zainmart').
--
-- ACCESS CONTROL: emit_event is the SOLE doorway into the outbox for
-- producer roles. It is SECURITY DEFINER (executes with the privileges of
-- the eventpump role that owns it, search_path pinned), and its default
-- PUBLIC EXECUTE grant is revoked below. Platform service roles need
-- EXECUTE on the function and NOTHING else — no table privileges:
--
--   GRANT EXECUTE ON FUNCTION
--       emit_event(text, text, jsonb, text, uuid, uuid, jsonb, timestamptz, uuid)
--       TO platform_service_role;
--
-- (Run once per producing role, as the eventpump owner or a DBA. Grants
-- survive re-applies of this file; the REVOKE below only strips PUBLIC.)
--
-- This file is idempotent (CREATE OR REPLACE) and re-applied on every
-- `eventpump migrate`, after the numbered migrations.
-- ============================================================================

-- Drop the pre-v1.2 signature first: CREATE OR REPLACE cannot change the
-- parameter list. Anyone still linking against the old single-arg contract
-- will now fail loudly at emit-time — cross-tenant leakage risk is higher
-- than the ergonomic cost of naming the tenant.
DROP FUNCTION IF EXISTS emit_event(text, jsonb, text, uuid, uuid, jsonb, timestamptz, uuid);

CREATE OR REPLACE FUNCTION emit_event(
    p_app_id       text,
    p_event_name   text,
    p_properties   jsonb       DEFAULT '{}',
    p_user_id      text        DEFAULT NULL,
    p_anonymous_id uuid        DEFAULT NULL,
    p_session_key  uuid        DEFAULT NULL,
    p_context      jsonb       DEFAULT '{}',
    p_occurred_at  timestamptz DEFAULT now(),
    p_event_id     uuid        DEFAULT gen_random_uuid()
) RETURNS uuid
LANGUAGE plpgsql
SECURITY DEFINER
-- pinned: a SECURITY DEFINER function with a mutable search_path lets the
-- caller shadow our tables from their own schema and escalate
SET search_path = public, pg_temp
AS $$
DECLARE
    v_destinations text[];
    v_reserved     boolean;
    v_now          timestamptz := now();
    v_ref          bigint;
BEGIN
    IF p_app_id IS NULL OR p_app_id = '' THEN
        RAISE EXCEPTION 'event_pump: app_id is required'
            USING HINT = 'pass the tenant id as the first argument to emit_event()';
    END IF;

    SELECT destinations, reserved INTO v_destinations, v_reserved
    FROM event_registry
    WHERE app_id = p_app_id AND event_name = p_event_name AND origin = 'server';
    IF NOT FOUND THEN
        RAISE EXCEPTION 'event_pump: unknown server event_name "%" for app_id "%"',
            p_event_name, p_app_id
            USING HINT = 'register the event with origin=server in that tenant''s tracking plan';
    END IF;
    -- SPEC §6.1: reserved names (e.g. ep_attributes_synced) are auto-registered
    -- for internal server-generated events and must not be emitted by producers.
    IF v_reserved THEN
        RAISE EXCEPTION 'event_pump: reserved event_name "%"', p_event_name
            USING HINT = 'reserved names are enqueued internally, not by producers';
    END IF;

    -- Per-tenant idempotence on event_id (SPEC §1, §11): duplicate => no-op.
    -- Composite PK (app_id, event_id) since migration 0009 — the explicit
    -- conflict target documents intent and would fail loud if the PK ever
    -- changes shape again.
    INSERT INTO events_dedupe (event_id, app_id, received_at)
    VALUES (p_event_id, p_app_id, v_now)
    ON CONFLICT (app_id, event_id) DO NOTHING;
    IF NOT FOUND THEN
        RETURN NULL;
    END IF;

    INSERT INTO events_outbox
        (app_id, event_id, event_name, origin, occurred_at, received_at,
         user_id, anonymous_id, session_key, properties, context)
    VALUES
        (p_app_id, p_event_id, p_event_name, 'server', p_occurred_at, v_now,
         p_user_id, p_anonymous_id, p_session_key,
         coalesce(p_properties, '{}'), coalesce(p_context, '{}'))
    RETURNING id INTO v_ref;

    -- Fan out delivery rows for routed destinations only (SPEC §8).
    INSERT INTO events_delivery (event_ref, received_at, app_id, destination)
    SELECT v_ref, v_now, p_app_id, d FROM unnest(v_destinations) AS d;

    -- Server-authoritative first visit (SPEC §8): once ever per (app_id,
    -- anonymous_id) by construction of the ON CONFLICT DO NOTHING insert.
    IF p_anonymous_id IS NOT NULL AND p_event_name <> 'first_visit' THEN
        INSERT INTO first_seen (app_id, anonymous_id, first_seen_at)
        VALUES (p_app_id, p_anonymous_id, v_now)
        ON CONFLICT DO NOTHING;
        IF FOUND THEN
            PERFORM emit_event(
                p_app_id,
                'first_visit',
                p_user_id      => p_user_id,
                p_anonymous_id => p_anonymous_id,
                p_session_key  => p_session_key,
                p_occurred_at  => v_now);
        END IF;
    END IF;

    RETURN p_event_id;
END;
$$;

-- Functions default to PUBLIC EXECUTE; this doorway is opt-in per role.
REVOKE ALL ON FUNCTION
    emit_event(text, text, jsonb, text, uuid, uuid, jsonb, timestamptz, uuid)
    FROM PUBLIC;
