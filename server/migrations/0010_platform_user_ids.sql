-- Platform-specific user identifiers (SPEC follow-up 2026-07-28).
--
-- The app's canonical `user_id` (a business identifier stored on
-- events_outbox + identity_registry) is what most destinations receive
-- as their own user id today. Some destinations were integrated
-- historically with a different identifier — e.g. MoEngage was seeded
-- with `M-<id>` when the customer profile was first created, GA4 was
-- given `G-<id>` at a later date. Rather than force the app to unify
-- them retroactively, the SDK's `identify()` can supply per-destination
-- user identifiers as additional handles. Each destination's sender
-- uses its own handle when set, falling back to the generic `user_id`.
--
-- Adjust identifies by ADID / hashed PII and has no native `user_id`
-- concept, so it does not get a column here.

ALTER TABLE identity_registry
    ADD COLUMN moengage_customer_id text,
    ADD COLUMN ga4_user_id          text,
    ADD COLUMN amplitude_user_id    text,
    ADD COLUMN meta_external_id     text;
