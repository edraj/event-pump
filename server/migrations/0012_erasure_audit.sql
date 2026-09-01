-- DSR erasure audit trail (SPEC §9.7).
--
-- Deliberately NOT partitioned and NOT registered with PartitionMaintenance:
-- events_outbox/events_delivery are dropped at EP_RETENTION_DAYS (30) or
-- EP_RETENTION_DEAD_DAYS (90), and a complaint about an ignored erasure can
-- arrive long after that. This table is the only durable proof that a request
-- was received and what each destination did with it.
--
-- Rows are written for every erasure request, including ones that queue
-- nothing: a tenant with all destinations off is still a request we must be
-- able to show we handled.
CREATE TABLE erasure_audit (
    id                   bigserial   PRIMARY KEY,
    app_id               text        NOT NULL,
    user_id              text        NOT NULL,
    variant              text        NOT NULL,
    event_id             uuid,
    handles              jsonb       NOT NULL DEFAULT '{}',
    destinations         text[]      NOT NULL DEFAULT '{}',
    cancelled_deliveries int         NOT NULL DEFAULT 0,
    outcomes             jsonb       NOT NULL DEFAULT '{}',
    requested_at         timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX erasure_audit_app_user_idx
    ON erasure_audit (app_id, user_id, requested_at DESC);

-- The worker writes outcomes back by (app_id, event_id).
CREATE INDEX erasure_audit_event_idx
    ON erasure_audit (app_id, event_id)
    WHERE event_id IS NOT NULL;
