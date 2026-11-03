-- Until schema 12 the product served CN only. 1.8.0 and earlier stored every hand-entered run and
-- every import without a region column as UNKNOWN; those completions counted towards the single
-- achievement, and an import answered 「已包含」 was deducted from its baseline. 0012 moved that
-- baseline to CN, so the runs it was kept for move with it (audit 2026-11-03 H1-1). A run a capture
-- wrote has a capture session and keeps the region its session recorded. A run created after the
-- database reached schema 12 was stored as UNKNOWN on purpose (未知区服 chosen in a schema-12 build)
-- and keeps it (re-review 2026-11-03 RV2-4); MigrationRunner records version 12 before this script
-- runs, also when both run in one upgrade. Only the region column changes: no revision, no update
-- time, no other row.
UPDATE mentor_runs SET region = 'CN'
WHERE region = 'UNKNOWN' AND capture_session_id IS NULL
  AND created_at_utc < (SELECT applied_at_utc FROM schema_migrations WHERE version = 12);
