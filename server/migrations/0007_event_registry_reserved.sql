-- Reserved event names (SPEC §6.1, §10, §11) — server-generated internal events
-- that producers must not emit. emit_event() and /internal/v1/events reject
-- calls with `reserved = true` names (error: `reserved_event_name`).
--
-- The first reserved name is `ep_attributes_synced`, auto-injected by the
-- tracking-plan loader and routed to the `moengage_customer` destination for
-- the MoEngage type:"customer" sync path.
ALTER TABLE event_registry
    ADD COLUMN reserved boolean NOT NULL DEFAULT false;
