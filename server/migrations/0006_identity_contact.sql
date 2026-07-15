ALTER TABLE identity_registry ADD COLUMN IF NOT EXISTS email  text;
ALTER TABLE identity_registry ADD COLUMN IF NOT EXISTS msisdn text;
