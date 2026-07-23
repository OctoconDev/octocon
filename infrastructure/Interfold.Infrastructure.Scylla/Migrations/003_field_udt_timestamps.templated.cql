-- =====================
-- Schema Template: Replace {{KEYSPACE}} with regional keyspace (nam, eur, ocn, eas, sam, sas, gdpr)
-- Applied by ScyllaMigrationService at startup, after 002_create_interfold_schema.
-- Adds the per-field created/updated timestamps onto the `field` UDT so the Simply Plural
-- importer can preserve a real created date decoded from the SP MongoDB ObjectId, and so
-- public Update/Relocate paths can bump a per-field `updated_at`. Both columns are nullable
-- to honour pre-migration rows (they read back as null and surface as null on the read model).
-- =====================

ALTER TYPE {{KEYSPACE}}.field ADD inserted_at timestamp;
ALTER TYPE {{KEYSPACE}}.field ADD updated_at timestamp;
