-- Session-scoped identity & context registry (SPEC §11). Upserted partially by
-- POST /v1/identity; joined by the worker via events.session_key to enrich
-- destination payloads (SPEC §12).
CREATE TABLE identity_registry (
    session_key              uuid        PRIMARY KEY,
    anonymous_id             uuid        NOT NULL,
    user_id                  text,
    session_number           int,
    ga4_client_id            text,
    ga4_session_id           text,
    firebase_app_instance_id text,
    amplitude_device_id      text,
    adjust_adid              text,
    adjust_platform_ad_id    text,
    fbp                      text,
    fbc                      text,
    click_ids                jsonb       NOT NULL DEFAULT '{}',
    context                  jsonb       NOT NULL DEFAULT '{}',
    client_ip                text,
    client_location          jsonb,
    created_at               timestamptz NOT NULL DEFAULT now(),
    updated_at               timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX identity_registry_anonymous_id_idx ON identity_registry (anonymous_id);

-- Server-authoritative first-visit determination (SPEC §8): the successful
-- ON CONFLICT DO NOTHING insert is the once-ever gate for `first_visit`.
CREATE TABLE first_seen (
    anonymous_id  uuid        PRIMARY KEY,
    first_seen_at timestamptz NOT NULL DEFAULT now()
);
