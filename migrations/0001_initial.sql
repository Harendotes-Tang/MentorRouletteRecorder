-- MentorRecorder initial schema, version 1.
--
-- Normative reference: docs/data-model.md. The two supporting tables not exposed as
-- business entities (ipc_idempotency and parser_errors), plus run_events.event_key,
-- implement the documented idempotency and bounded-diagnostics requirements.
--
-- Conventions enforced by CHECK constraints rather than by application code:
--   * every timestamp is UTC ISO-8601 with milliseconds and a literal Z;
--   * every boolean is an INTEGER restricted to 0 or 1;
--   * every duration is a non-negative integer number of milliseconds;
--   * business rows are only ever soft deleted, and run_revisions is append-only
--     (two triggers below make UPDATE and DELETE on it fail loudly).
--
-- This migration creates structure only. It writes no user data; the single
-- achievement_settings row is seeded by the Collector at startup so that its
-- timestamp comes from the injected clock.

CREATE TABLE schema_migrations (
    version        INTEGER NOT NULL PRIMARY KEY,
    name           TEXT    NOT NULL,
    checksum       TEXT    NOT NULL,
    applied_at_utc TEXT    NOT NULL
        CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', applied_at_utc) = applied_at_utc)
);

CREATE TABLE capture_sessions (
    capture_session_id  TEXT    NOT NULL PRIMARY KEY,
    started_at_utc      TEXT    NOT NULL
                                CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', started_at_utc) = started_at_utc),
    ended_at_utc        TEXT    NULL
                                CHECK (ended_at_utc IS NULL OR strftime('%Y-%m-%dT%H:%M:%fZ', ended_at_utc) = ended_at_utc),
    collector_version   TEXT    NOT NULL,
    region              TEXT    NOT NULL DEFAULT 'UNKNOWN'
                                CHECK (region IN ('CN', 'GLOBAL', 'UNKNOWN')),
    game_build          TEXT    NULL,
    protocol_profile_id TEXT    NULL,
    profile_status      TEXT    NOT NULL DEFAULT 'NONE'
                                CHECK (profile_status IN ('NONE', 'UNVERIFIED', 'VERIFIED', 'UNSUPPORTED_BUILD')),
    adapter_id          TEXT    NULL,
    packets_observed    INTEGER NOT NULL DEFAULT 0 CHECK (packets_observed >= 0),
    packets_dropped     INTEGER NOT NULL DEFAULT 0 CHECK (packets_dropped >= 0),
    end_reason          TEXT    NULL
                                CHECK (end_reason IS NULL OR end_reason IN ('USER_STOP', 'PROCESS_EXIT', 'ERROR', 'UNKNOWN'))
);

CREATE INDEX ix_sessions_open ON capture_sessions(ended_at_utc);

CREATE TABLE mentor_runs (
    run_id               TEXT    NOT NULL PRIMARY KEY,
    revision             INTEGER NOT NULL CHECK (revision >= 1),
    capture_session_id   TEXT    NULL REFERENCES capture_sessions(capture_session_id) ON DELETE RESTRICT,
    region               TEXT    NOT NULL DEFAULT 'UNKNOWN'
                                 CHECK (region IN ('CN', 'GLOBAL', 'UNKNOWN')),
    game_build           TEXT    NULL,
    protocol_profile_id  TEXT    NULL,
    mentor_roulette_id   INTEGER NULL,
    content_id           INTEGER NULL,
    territory_id         INTEGER NULL,
    duty_name            TEXT    NULL,
    duty_category        TEXT    NULL,
    job_id               INTEGER NULL,
    job_name             TEXT    NULL,
    role                 TEXT    NOT NULL DEFAULT 'UNKNOWN'
                                 CHECK (role IN ('TANK', 'HEALER', 'DPS', 'UNKNOWN')),
    matched_at_utc       TEXT    NULL
                                 CHECK (matched_at_utc IS NULL OR strftime('%Y-%m-%dT%H:%M:%fZ', matched_at_utc) = matched_at_utc),
    entered_at_utc       TEXT    NULL
                                 CHECK (entered_at_utc IS NULL OR strftime('%Y-%m-%dT%H:%M:%fZ', entered_at_utc) = entered_at_utc),
    ended_at_utc         TEXT    NULL
                                 CHECK (ended_at_utc IS NULL OR strftime('%Y-%m-%dT%H:%M:%fZ', ended_at_utc) = ended_at_utc),
    duration_ms          INTEGER NULL CHECK (duration_ms IS NULL OR duration_ms >= 0),
    result               TEXT    NOT NULL
                                 CHECK (result IN ('COMPLETED', 'LEFT_OR_ABANDONED', 'CANCELLED_BEFORE_ENTRY',
                                                   'DISCONNECTED', 'INTERRUPTED', 'UNKNOWN')),
    detection_confidence TEXT    NOT NULL DEFAULT 'NONE'
                                 CHECK (detection_confidence IN ('HIGH', 'MEDIUM', 'LOW', 'NONE')),
    source               TEXT    NOT NULL
                                 CHECK (source IN ('AUTO_NETWORK', 'MANUAL', 'IMPORT')),
    contributes_to_goal  INTEGER NOT NULL DEFAULT 1 CHECK (contributes_to_goal IN (0, 1)),
    manually_created     INTEGER NOT NULL DEFAULT 0 CHECK (manually_created IN (0, 1)),
    manually_corrected   INTEGER NOT NULL DEFAULT 0 CHECK (manually_corrected IN (0, 1)),
    soft_deleted         INTEGER NOT NULL DEFAULT 0 CHECK (soft_deleted IN (0, 1)),
    created_at_utc       TEXT    NOT NULL
                                 CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at_utc) = created_at_utc),
    updated_at_utc       TEXT    NOT NULL
                                 CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', updated_at_utc) = updated_at_utc),

    CHECK (matched_at_utc IS NULL OR entered_at_utc IS NULL OR matched_at_utc <= entered_at_utc),
    CHECK (entered_at_utc IS NULL OR ended_at_utc  IS NULL OR entered_at_utc <= ended_at_utc)
);

CREATE INDEX ix_runs_entered ON mentor_runs(entered_at_utc);
CREATE INDEX ix_runs_result  ON mentor_runs(result);
CREATE INDEX ix_runs_content ON mentor_runs(content_id);
CREATE INDEX ix_runs_job     ON mentor_runs(job_id);
CREATE INDEX ix_runs_live    ON mentor_runs(soft_deleted, entered_at_utc);
CREATE INDEX ix_runs_session ON mentor_runs(capture_session_id);

CREATE TABLE run_events (
    event_id             TEXT    NOT NULL PRIMARY KEY,
    run_id               TEXT    NOT NULL REFERENCES mentor_runs(run_id) ON DELETE RESTRICT,
    sequence             INTEGER NOT NULL CHECK (sequence >= 0),
    occurred_at_utc      TEXT    NOT NULL
                                 CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', occurred_at_utc) = occurred_at_utc),
    monotonic_offset_ms  INTEGER NOT NULL CHECK (monotonic_offset_ms >= 0),
    event_type           TEXT    NOT NULL,
    from_state           TEXT    NULL,
    to_state             TEXT    NULL,
    confidence           TEXT    NOT NULL DEFAULT 'NONE'
                                 CHECK (confidence IN ('HIGH', 'MEDIUM', 'LOW', 'NONE')),
    event_key            TEXT    NULL,
    detail_json          TEXT    NULL,

    UNIQUE (run_id, sequence)
);

CREATE INDEX ix_events_run ON run_events(run_id, sequence);

-- Deduplication across process restarts and repeated replays. Partial index so that
-- manually created events, which have no observation identity, may all carry NULL.
CREATE UNIQUE INDEX ux_events_key ON run_events(event_key) WHERE event_key IS NOT NULL;

CREATE TABLE run_revisions (
    revision_id    TEXT    NOT NULL PRIMARY KEY,
    run_id         TEXT    NOT NULL REFERENCES mentor_runs(run_id) ON DELETE RESTRICT,
    revision       INTEGER NOT NULL CHECK (revision >= 1),
    changed_at_utc TEXT    NOT NULL
                           CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', changed_at_utc) = changed_at_utc),
    change_kind    TEXT    NOT NULL
                           CHECK (change_kind IN ('CREATE_AUTO', 'CREATE_MANUAL', 'CORRECT',
                                                  'SOFT_DELETE', 'RESTORE', 'IMPORT')),
    actor          TEXT    NOT NULL CHECK (actor IN ('USER', 'SYSTEM')),
    reason         TEXT    NULL,
    request_id     TEXT    NULL,
    changes_json   TEXT    NOT NULL,

    UNIQUE (run_id, revision),
    UNIQUE (request_id)
);

CREATE INDEX ix_revisions_run ON run_revisions(run_id, revision);

CREATE TRIGGER trg_run_revisions_validate_insert
BEFORE INSERT ON run_revisions
BEGIN
    SELECT CASE
        WHEN NEW.revision != COALESCE(
            (SELECT MAX(revision) + 1 FROM run_revisions WHERE run_id = NEW.run_id), 1)
            THEN RAISE(ABORT, 'run_revisions must be contiguous and start at 1')
        WHEN NEW.revision = 1 AND NEW.change_kind NOT IN ('CREATE_AUTO', 'CREATE_MANUAL', 'IMPORT')
            THEN RAISE(ABORT, 'revision 1 must be a create revision')
        WHEN NEW.revision > 1 AND NEW.change_kind IN ('CREATE_AUTO', 'CREATE_MANUAL', 'IMPORT')
            THEN RAISE(ABORT, 'create revision must be revision 1')
        WHEN NEW.change_kind IN ('CREATE_MANUAL', 'CORRECT', 'SOFT_DELETE', 'RESTORE')
             AND (NEW.reason IS NULL OR length(trim(NEW.reason)) NOT BETWEEN 1 AND 500)
            THEN RAISE(ABORT, 'this revision requires a reason of 1 to 500 characters')
        WHEN (SELECT revision FROM mentor_runs WHERE run_id = NEW.run_id) != NEW.revision
            THEN RAISE(ABORT, 'mentor_runs revision must equal appended revision')
    END;
END;

-- The audit chain is append-only. These triggers are the enforcement, not a convention:
-- there is no code path anywhere that updates or deletes a revision, and if one were ever
-- introduced it would fail here instead of quietly rewriting history.
CREATE TRIGGER trg_run_revisions_no_update
BEFORE UPDATE ON run_revisions
BEGIN
    SELECT RAISE(ABORT, 'run_revisions is append-only: UPDATE is forbidden');
END;

CREATE TRIGGER trg_run_revisions_no_delete
BEFORE DELETE ON run_revisions
BEGIN
    SELECT RAISE(ABORT, 'run_revisions is append-only: DELETE is forbidden');
END;

CREATE TABLE achievement_settings (
    id                       INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
    goal_count               INTEGER NOT NULL DEFAULT 2000 CHECK (goal_count >= 1),
    baseline_completed_count INTEGER NOT NULL DEFAULT 0 CHECK (baseline_completed_count >= 0),
    baseline_effective_at    TEXT    NOT NULL
                                    CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', baseline_effective_at) = baseline_effective_at),
    updated_at_utc           TEXT    NOT NULL
                                    CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', updated_at_utc) = updated_at_utc)
);

CREATE TABLE application_settings (
    key            TEXT NOT NULL PRIMARY KEY,
    value_json     TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
        CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', updated_at_utc) = updated_at_utc)
);

-- Idempotency of mutating IPC requests. The stored response is replayed verbatim when the
-- same request_id arrives again, so a client that resends after a dropped connection can
-- never produce a second write (docs/manual-correction.md section 4).
CREATE TABLE ipc_idempotency (
    request_id     TEXT NOT NULL PRIMARY KEY,
    message_type   TEXT NOT NULL,
    response_json  TEXT NOT NULL,
    created_at_utc TEXT NOT NULL
        CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at_utc) = created_at_utc)
);

CREATE INDEX ix_idempotency_created ON ipc_idempotency(created_at_utc);

-- Bounded diagnostics table. Rows carry a short kind and a non-sensitive detail string;
-- packet payloads, character names and addresses are never written here.
CREATE TABLE parser_errors (
    error_id           TEXT NOT NULL PRIMARY KEY,
    occurred_at_utc    TEXT NOT NULL
                               CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', occurred_at_utc) = occurred_at_utc),
    capture_session_id TEXT NULL,
    kind               TEXT NOT NULL,
    detail             TEXT NULL
);

CREATE INDEX ix_parser_errors_time ON parser_errors(occurred_at_utc);
