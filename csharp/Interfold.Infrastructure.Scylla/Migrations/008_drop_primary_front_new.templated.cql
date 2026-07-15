-- =====================
-- Schema Template: Replace {{KEYSPACE}} with regional keyspace (nam, eur, ocn, eas, sam, sas, gdpr)
-- Applied by ScyllaMigrationService at startup, phase 3 of 3 of the users.primary_front
-- int -> smallint narrowing. Runs AFTER the primary_front_new -> primary_front backfill
-- (see 006 header for the full sequence).
--
-- Drops the temp smallint column now that its contents have been copied back into the
-- re-added primary_front. Post-drop the users table matches the shape the repositories
-- have been binding against since this PR started (a single primary_front smallint
-- column). Repositories only accept traffic after StartingAsync completes, so no
-- intermediate state is ever visible to reads/writes outside of the migration boot.
-- =====================

ALTER TABLE {{KEYSPACE}}.users DROP primary_front_new;
