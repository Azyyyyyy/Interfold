-- =====================
-- Schema Template: Replace {{KEYSPACE}} with regional keyspace (nam, eur, ocn, eas, sam, sas, gdpr)
-- Applied by ScyllaMigrationService at startup, phase 2 of 3 of the users.primary_front
-- int -> smallint narrowing. Runs AFTER the int -> primary_front_new backfill and BEFORE
-- the primary_front_new -> primary_front backfill (see 006 header for the full sequence).
--
-- Drops the legacy int column and re-adds primary_front as smallint. The two ALTERs run
-- in the order shown; the second must come after the first because Scylla would otherwise
-- reject "column primary_front already exists" (there's no ALTER COLUMN TYPE).
--
-- Idempotence: crash-between-ALTERs is handled by ExecuteStatements catching both
-- "column does not exist" (for the DROP re-run) and "column already exists" (for the
-- ADD re-run) — same defensive pair used by any ALTER-side migration.
-- =====================

ALTER TABLE {{KEYSPACE}}.users DROP primary_front;
ALTER TABLE {{KEYSPACE}}.users ADD primary_front smallint;
