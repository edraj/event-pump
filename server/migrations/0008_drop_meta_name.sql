-- SPEC §6.2 R4: `meta_name` is superseded by the per-destination rename map
-- (destinations.meta.events.<x>.name in the tracking plan). The senders now
-- consult TrackingPlan directly in-memory, so event_registry no longer needs
-- to project this column.
ALTER TABLE event_registry DROP COLUMN IF EXISTS meta_name;
