-- Amplitude session_id handle (Amplitude TSD §9 — hybrid tracking).
--
-- The Amplitude SDK generates its own session_id (epoch milliseconds of
-- session start). For client + server events to join one Amplitude session
-- in a live-app window, the mobile SDK must forward it to EP and the server
-- sender must use it in preference to the UUIDv7-derived fallback that the
-- `session_id` field in the Amplitude payload is currently derived from.
--
-- Stored as text to match the other `amplitude_*` handles on this table and
-- to match the shape /v1/identity accepts (`handles.amplitude_session_id` is
-- a string, converted to long in the sender).

ALTER TABLE identity_registry
    ADD COLUMN amplitude_session_id text;
